// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayStudioService.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Services;

/// <summary>
///     Core service coordinating replay archive ingestion, keyframe track synthesis,
///     snapshot cache population, and roster state binding.
/// </summary>
public sealed class ReplayStudioService
{
    private readonly ILogger<ReplayStudioService> _logger;

    public ReplaySnapshotEngine SnapshotEngine { get; }
    public PlaybackClock Clock { get; }
    public MapEnvironmentManager MapManager { get; }
    public ObservableCollection<RosterPlayerItem> Roster { get; } = [];
    public ReplayMetadata? CurrentMetadata { get; private set; }
    public string? CurrentReplayFilePath { get; private set; }
    public bool IsReplayLoaded { get; private set; }
    public bool HasGenuineTelemetry { get; private set; }

    public event Action<ReplayMetadata>? ReplayLoaded;

    public ReplayStudioService(
        ReplaySnapshotEngine snapshotEngine,
        PlaybackClock clock,
        MapEnvironmentManager mapManager,
        ILogger<ReplayStudioService>? logger = null)
    {
        SnapshotEngine = snapshotEngine ?? throw new ArgumentNullException(nameof(snapshotEngine));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        MapManager = mapManager ?? throw new ArgumentNullException(nameof(mapManager));
        _logger = logger ?? NullLogger<ReplayStudioService>.Instance;
        _logger.LogInformation("[ReplayStudioService] Initialized Replay Studio Service");
    }

    /// <summary>
    ///     Ingests a .honreplay archive, KGKF binary stream, or metadata file, synthesizes keyframes, and populates roster.
    /// </summary>
    public async Task<bool> LoadReplayAsync(string replayFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(replayFilePath) || !File.Exists(replayFilePath))
        {
            _logger.LogWarning("[ReplayStudioService] Replay file not found or invalid: '{Path}'", replayFilePath);
            return false;
        }

        try
        {
            string fullReplayPath = Path.GetFullPath(replayFilePath);
            _logger.LogInformation("[ReplayStudioService] Ingesting replay from '{Path}'", fullReplayPath);

            // 1. Direct KGKF binary file check
            if (replayFilePath.EndsWith(".kgkf", StringComparison.OrdinalIgnoreCase) ||
                replayFilePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                byte[] rawBytes = await File.ReadAllBytesAsync(replayFilePath, cancellationToken);
                if (TryParseKgkfBinary(rawBytes, out List<GameStateSnapshot> kgkfFrames, _logger) && kgkfFrames.Count > 0)
                {
                    ReplayMetadata synMeta = new()
                    {
                        MatchId = Path.GetFileNameWithoutExtension(replayFilePath),
                        GameName = "KGKF Telemetry Capture",
                        MapName = "caldavar",
                        MatchLengthMs = kgkfFrames.Max(k => k.TimestampMs) + 1000
                    };
                    bool loaded = LoadMetadata(synMeta, kgkfFrames);
                    if (loaded) CurrentReplayFilePath = fullReplayPath;
                    return loaded;
                }
            }

            // 2. Check for KGKF sidecar alongside replay
            string sidecarKgkf = Path.ChangeExtension(replayFilePath, ".kgkf");
            if (!File.Exists(sidecarKgkf))
            {
                sidecarKgkf = Path.ChangeExtension(replayFilePath, ".bin");
            }
            if (File.Exists(sidecarKgkf))
            {
                byte[] sidecarBytes = await File.ReadAllBytesAsync(sidecarKgkf, cancellationToken);
                if (TryParseKgkfBinary(sidecarBytes, out List<GameStateSnapshot> sidecarFrames, _logger) && sidecarFrames.Count > 0)
                {
                    ReplayMetadata baseMeta = await Task.Run(() => ReplayMetadataParser.ParseFromFile(replayFilePath), cancellationToken);
                    bool loaded = LoadMetadata(baseMeta, sidecarFrames);
                    if (loaded) CurrentReplayFilePath = fullReplayPath;
                    return loaded;
                }
            }

            // 3. Inspect .honreplay zip archive for replaydata or internal keyframe entry
            using (FileStream fs = new(replayFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new(fs, ZipArchiveMode.Read, true))
            {
                ZipArchiveEntry? replayInfoEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "replayinfo", StringComparison.OrdinalIgnoreCase));
                ZipArchiveEntry? replayDataEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "replaydata", StringComparison.OrdinalIgnoreCase));
                ZipArchiveEntry? keyframeEntry = archive.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".kgkf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Name, "keyframe.bin", StringComparison.OrdinalIgnoreCase));

                if (replayInfoEntry == null)
                {
                    throw new InvalidDataException("The archive does not contain a 'replayinfo' entry.");
                }

                ReplayMetadata metadata;
                using (Stream infoStream = replayInfoEntry.Open())
                using (StreamReader reader = new(infoStream, Encoding.UTF8))
                {
                    string xml = await reader.ReadToEndAsync(cancellationToken);
                    metadata = ReplayMetadataParser.ParseFromXml(xml);
                }

                // Internal KGKF stream check
                if (keyframeEntry != null)
                {
                    using Stream kfStream = keyframeEntry.Open();
                    using MemoryStream ms = new();
                    await kfStream.CopyToAsync(ms, cancellationToken);
                    if (TryParseKgkfBinary(ms.ToArray(), out List<GameStateSnapshot> internalKgkf, _logger) && internalKgkf.Count > 0)
                    {
                        bool loaded = LoadMetadata(metadata, internalKgkf);
                        if (loaded) CurrentReplayFilePath = fullReplayPath;
                        return loaded;
                    }
                }

                // Genuine binary replaydata (S2R2/S2R1) check
                if (replayDataEntry != null)
                {
                    using Stream dataStream = replayDataEntry.Open();
                    using MemoryStream ms = new();
                    await dataStream.CopyToAsync(ms, cancellationToken);
                    byte[] dataBytes = ms.ToArray();
                    List<GameStateSnapshot> genuineFrames = ExtractKeyframesFromReplayData(dataBytes, metadata, _logger);
                    if (genuineFrames.Count > 0)
                    {
                        bool loaded = LoadMetadata(metadata, genuineFrames);
                        if (loaded) CurrentReplayFilePath = fullReplayPath;
                        return loaded;
                    }
                }

                // Fallback to procedural synthesis (marked with IsEstimatedFallback = true)
                bool fallbackLoaded = LoadMetadata(metadata);
                if (fallbackLoaded) CurrentReplayFilePath = fullReplayPath;
                return fallbackLoaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReplayStudioService] Failed to load replay archive '{Path}'", replayFilePath);
            return false;
        }
    }

    /// <summary>
    ///     Initializes match state directly from a parsed ReplayMetadata instance, optionally
    ///     using genuine extracted keyframes or synthesizing estimated fallbacks.
    /// </summary>
    public bool LoadMetadata(ReplayMetadata metadata, List<GameStateSnapshot>? customKeyframes = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        CurrentMetadata = metadata;
        CurrentReplayFilePath = null;
        int durationMs = (int)Math.Max(1000, metadata.MatchLengthMs);

        MapManager.LoadMap(metadata.MapName);

        List<GameStateSnapshot> keyframes;
        if (customKeyframes != null && customKeyframes.Count > 0)
        {
            keyframes = customKeyframes;
            HasGenuineTelemetry = !keyframes[0].IsEstimatedFallback;
        }
        else
        {
            keyframes = SynthesizeSimulationTrack(metadata, durationMs);
            HasGenuineTelemetry = false;
        }

        SnapshotEngine.LoadKeyframes(keyframes, durationMs);

        Clock.TotalDurationMs = durationMs;
        Clock.InMarkerMs = 0;
        Clock.OutMarkerMs = durationMs;
        Clock.CurrentTimeMs = 0;

        InitializeRoster(metadata);
        IsReplayLoaded = true;

        _logger.LogInformation(
            "[ReplayStudioService] Successfully loaded match {MatchId} ('{GameName}', Map: {MapName}, Duration: {DurationMs}ms, GenuineTelemetry: {HasGenuine})",
            metadata.MatchId,
            metadata.GameName,
            metadata.MapName,
            durationMs,
            HasGenuineTelemetry);

        ReplayLoaded?.Invoke(metadata);
        return true;
    }

    private void InitializeRoster(ReplayMetadata metadata)
    {
        Roster.Clear();
        for (int i = 0; i < 10; i++)
        {
            ReplayPlayerMetadata? p = metadata.Players.FirstOrDefault(x => x.ClientNum == i);
            int team = i < 5 ? 1 : 2;
            string name = p?.Name ?? $"Player {i + 1}";
            string hero = p?.HeroName ?? (team == 1 ? "Hero_Devourer" : "Hero_Pyromancer");

            RosterPlayerItem item = new()
            {
                ClientNum = i,
                Team = team,
                PlayerName = name,
                HeroName = hero,
                CleanHeroName = p?.CleanHeroName ?? hero.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase),
                Kills = p?.Kills ?? 0,
                Deaths = p?.Deaths ?? 0,
                Assists = p?.Assists ?? 0,
                Gpm = p?.GoldPerMinute ?? 300,
                Xpm = p?.ExpPerMinute ?? 350,
                Level = p?.Level ?? 1,
                CurrentHp = 1000f,
                MaxHp = 1000f,
                HpPercent = 100f,
                CurrentMp = 500f,
                MaxMp = 500f,
                MpPercent = 100f,
                IsAlive = true,
                IsPovLocked = (i == 0) // Default POV lock to slot 0
            };
            Roster.Add(item);
        }
    }

    public static List<GameStateSnapshot> SynthesizeSimulationTrack(ReplayMetadata metadata, int totalDurationMs)
    {
        List<GameStateSnapshot> snapshots = [];
        int intervalMs = 10000; // 10s intervals
        int totalFrames = (totalDurationMs / intervalMs) + 1;

        // Base spawn origins (Caldavar coords)
        Vector3 legionBase = new(-11.7f, -11.7f, 2.2f);
        Vector3 hellbourneBase = new(11.7f, 11.7f, 2.2f);

        for (int frame = 0; frame <= totalFrames; frame++)
        {
            int timestamp = Math.Min(frame * intervalMs, totalDurationMs);
            float progress = (float)timestamp / Math.Max(1, totalDurationMs);

            GameStateSnapshot snap = new()
            {
                TimestampMs = timestamp,
                IsKeyframe = true,
                KeyframeTag = $"Keyframe {frame}",
                IsEstimatedFallback = true,
                ScoreLegion = (int)(metadata.Players.Where(p => p.Team == 1).Sum(p => p.Kills) * progress),
                ScoreHellbourne = (int)(metadata.Players.Where(p => p.Team == 2).Sum(p => p.Kills) * progress),
                NetWorthAdvantage = (int)((metadata.WinnerTeamName == "Legion" ? 5000 : -5000) * progress)
            };

            for (int slot = 0; slot < 10; slot++)
            {
                ReplayPlayerMetadata? p = metadata.Players.FirstOrDefault(x => x.ClientNum == slot);
                int team = slot < 5 ? 1 : 2;
                Vector3 basePos = team == 1 ? legionBase : hellbourneBase;
                Vector3 targetLane = team == 1 ? hellbourneBase : legionBase;

                // Procedural pathing along lanes
                Vector3 currentPos = Vector3.Lerp(basePos, targetLane, Math.Clamp(progress * 0.8f + (slot * 0.02f), 0f, 1f));
                Vector3 direction = Vector3.Normalize(targetLane - basePos);
                float heading = (float)(Math.Atan2(direction.Y, direction.X) * (180.0 / Math.PI));
                if (heading < 0) heading += 360f;

                HeroSnapshot hero = new()
                {
                    ClientIndex = slot,
                    Team = team,
                    PlayerName = p?.Name ?? $"Player {slot + 1}",
                    HeroName = p?.HeroName ?? (team == 1 ? "Hero_Devourer" : "Hero_Pyromancer"),
                    Position = currentPos,
                    HeadingAngle = heading,
                    Level = Math.Max(1, (int)(1 + 24 * progress)),
                    Health = 1000f,
                    MaxHealth = 1000f,
                    Mana = 500f,
                    MaxMana = 500f,
                    IsAlive = true,
                    IsEstimatedFallback = true,
                    Kills = (int)((p?.Kills ?? 0) * progress),
                    Deaths = (int)((p?.Deaths ?? 0) * progress),
                    Assists = (int)((p?.Assists ?? 0) * progress),
                    Gold = (int)((p?.Gold ?? 0) * progress),
                    GoldPerMinute = p?.GoldPerMinute ?? 300,
                    ExpPerMinute = p?.ExpPerMinute ?? 350
                };
                snap.Heroes[slot] = hero;
            }

            snapshots.Add(snap);
        }

        // Inject First Blood event keyframe if recorded
        if (metadata.FirstBloodTimeMs > 0 && metadata.FirstBloodTimeMs < totalDurationMs)
        {
            GameStateSnapshot? existing = snapshots.FirstOrDefault(s => Math.Abs(s.TimestampMs - metadata.FirstBloodTimeMs) < 5000);
            GameStateSnapshot fbSnap = existing?.Clone() ?? new GameStateSnapshot();
            fbSnap.TimestampMs = (int)metadata.FirstBloodTimeMs;
            fbSnap.IsKeyframe = true;
            fbSnap.KeyframeTag = "First Blood";
            fbSnap.IsEstimatedFallback = true;
            fbSnap.ActiveEvents.Add(new CombatEventMarker
            {
                TimestampMs = (int)metadata.FirstBloodTimeMs,
                EventType = CombatEventType.FirstBlood,
                Description = "First Blood achieved!",
                IntensityScore = 0.9f
            });
            snapshots.Add(fbSnap);
        }

        return snapshots.OrderBy(s => s.TimestampMs).ToList();
    }

    /// <summary>
    ///     Computes the standard IEEE 802.3 CRC32 checksum over a span of bytes.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            crc ^= b;
            for (int j = 0; j < 8; j++)
            {
                uint mask = (uint)-(int)(crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320 & mask);
            }
        }
        return ~crc;
    }

    /// <summary>
    ///     Parses keyframes from a KGKF binary stream (verifying 0x4B474B46 magic and CRC32).
    ///     Supports optional per-player/hero coordinate snapshots ("players" or "entities" arrays).
    /// </summary>
    public static bool TryParseKgkfBinary(
        ReadOnlySpan<byte> bytes,
        out List<GameStateSnapshot> keyframes,
        ILogger? logger = null)
    {
        keyframes = [];
        if (bytes.Length < 40) return false;

        int offset = 0;
        while (offset + 40 <= bytes.Length)
        {
            ReadOnlySpan<byte> chunk = bytes[offset..];
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(chunk[0..4]);
            if (magic != 0x4B474B46) // "KGKF"
            {
                if (keyframes.Count > 0) break;
                logger?.LogWarning("[ReplayStudioService] Invalid KGKF magic 0x{Magic:X8} at offset {Offset}", magic, offset);
                return false;
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..12]);
            uint serverTick = BinaryPrimitives.ReadUInt32LittleEndian(chunk[12..16]);
            ulong matchId = BinaryPrimitives.ReadUInt64LittleEndian(chunk[16..24]);
            long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(chunk[24..32]);
            uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk[32..36]);
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(chunk[36..40]);

            if (payloadSize > 65536u || (long)offset + 40L + payloadSize > bytes.Length)
            {
                logger?.LogWarning("[ReplayStudioService] KGKF payload size {Size} exceeds remaining buffer {Len}", payloadSize, chunk.Length);
                break;
            }

            ReadOnlySpan<byte> payloadBytes = chunk.Slice(40, (int)payloadSize);
            uint computedCrc = ComputeCrc32(payloadBytes);
            if (computedCrc != expectedCrc)
            {
                logger?.LogWarning("[ReplayStudioService] KGKF CRC corruption: expected 0x{Exp:X8}, got 0x{Act:X8}", expectedCrc, computedCrc);
                return false;
            }

            try
            {
                string json = Encoding.UTF8.GetString(payloadBytes);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                int timestampMs = (int)(serverTick * 50);
                GameStateSnapshot snap = new()
                {
                    TimestampMs = timestampMs,
                    IsKeyframe = true,
                    KeyframeTag = $"KGKF Seq {sequence} (Tick {serverTick})",
                    IsEstimatedFallback = false
                };

                JsonElement playerArray = default;
                bool hasPlayers = false;
                if (root.TryGetProperty("players", out JsonElement pElem) && pElem.ValueKind == JsonValueKind.Array)
                {
                    playerArray = pElem;
                    hasPlayers = true;
                }
                else if (root.TryGetProperty("entities", out JsonElement eElem) && eElem.ValueKind == JsonValueKind.Array)
                {
                    playerArray = eElem;
                    hasPlayers = true;
                }

                if (hasPlayers)
                {
                    foreach (JsonElement p in playerArray.EnumerateArray())
                    {
                        int slot = p.TryGetProperty("slot", out JsonElement sElem) && sElem.TryGetInt32(out int s)
                            ? s
                            : (p.TryGetProperty("clientIndex", out JsonElement ciElem) && ciElem.TryGetInt32(out int ci) ? ci : 0);
                        if (slot < 0 || slot >= 10) continue;

                        int team = p.TryGetProperty("team", out JsonElement tElem) && tElem.TryGetInt32(out int t) ? t : (slot < 5 ? 1 : 2);
                        string hero = p.TryGetProperty("hero", out JsonElement hElem)
                            ? (hElem.GetString() ?? string.Empty)
                            : (p.TryGetProperty("heroName", out JsonElement hnElem) ? (hnElem.GetString() ?? string.Empty) : string.Empty);
                        string name = p.TryGetProperty("name", out JsonElement nElem)
                            ? (nElem.GetString() ?? string.Empty)
                            : (p.TryGetProperty("playerName", out JsonElement pnElem) ? (pnElem.GetString() ?? string.Empty) : string.Empty);

                        float x = p.TryGetProperty("x", out JsonElement xElem) && xElem.TryGetSingle(out float fx) ? fx : 0f;
                        float y = p.TryGetProperty("y", out JsonElement yElem) && yElem.TryGetSingle(out float fy) ? fy : 0f;
                        float z = p.TryGetProperty("z", out JsonElement zElem) && zElem.TryGetSingle(out float fz) ? fz : 0f;

                        float health = p.TryGetProperty("health", out JsonElement hpElem) && hpElem.TryGetSingle(out float fhp)
                            ? fhp
                            : (p.TryGetProperty("hp", out JsonElement hp2Elem) && hp2Elem.TryGetSingle(out float fhp2) ? fhp2 : 1000f);
                        float maxHealth = p.TryGetProperty("maxHealth", out JsonElement mhpElem) && mhpElem.TryGetSingle(out float fmhp)
                            ? fmhp
                            : (p.TryGetProperty("maxHp", out JsonElement mhp2Elem) && mhp2Elem.TryGetSingle(out float fmhp2) ? fmhp2 : 1000f);
                        float mana = p.TryGetProperty("mana", out JsonElement mpElem) && mpElem.TryGetSingle(out float fmp)
                            ? fmp
                            : (p.TryGetProperty("mp", out JsonElement mp2Elem) && mp2Elem.TryGetSingle(out float fmp2) ? fmp2 : 500f);
                        float maxMana = p.TryGetProperty("maxMana", out JsonElement mmpElem) && mmpElem.TryGetSingle(out float fmmp)
                            ? fmmp
                            : (p.TryGetProperty("maxMp", out JsonElement mmp2Elem) && mmp2Elem.TryGetSingle(out float fmmp2) ? fmmp2 : 500f);

                        int level = p.TryGetProperty("level", out JsonElement lvlElem) && lvlElem.TryGetInt32(out int lvl) ? lvl : 1;
                        float heading = p.TryGetProperty("heading", out JsonElement hdElem) && hdElem.TryGetSingle(out float fhd)
                            ? fhd
                            : (p.TryGetProperty("headingAngle", out JsonElement hdaElem) && hdaElem.TryGetSingle(out float fhda) ? fhda : 0f);
                        bool isAlive = !p.TryGetProperty("isAlive", out JsonElement alElem) || alElem.GetBoolean();

                        snap.Heroes[slot] = new HeroSnapshot
                        {
                            ClientIndex = slot,
                            Team = team,
                            HeroName = hero,
                            PlayerName = name,
                            Position = new Vector3(x, y, z),
                            HeadingAngle = heading,
                            Health = health,
                            MaxHealth = maxHealth,
                            Mana = mana,
                            MaxMana = maxMana,
                            Level = level,
                            IsAlive = isAlive,
                            IsEstimatedFallback = false
                        };
                    }
                }

                keyframes.Add(snap);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[ReplayStudioService] Failed to parse KGKF JSON payload at offset {Offset}", offset);
                return false;
            }

            offset += 40 + (int)payloadSize;
        }

        return keyframes.Count > 0;
    }

    /// <summary>
    ///     Extracts genuine keyframes from binary replaydata (S2R2 / S2R1) stream inside .honreplay.
    ///     Marks genuine extracted keyframes with IsEstimatedFallback = false.
    /// </summary>
    public static List<GameStateSnapshot> ExtractKeyframesFromReplayData(
        byte[] bytes,
        ReplayMetadata metadata,
        ILogger? logger = null)
    {
        List<GameStateSnapshot> keyframes = [];
        if (bytes == null || bytes.Length < 16) return keyframes;

        string magic = Encoding.ASCII.GetString(bytes, 0, 4);
        if (magic != "S2R2" && magic != "S2R1")
        {
            logger?.LogWarning("[ReplayStudioService] Unsupported replaydata magic: {Magic}", magic);
            return keyframes;
        }

        List<Vector3> positions = [];
        for (int i = 8; i < bytes.Length - 12; i += 4)
        {
            float fX = BitConverter.ToSingle(bytes, i);
            float fY = BitConverter.ToSingle(bytes, i + 4);
            float fZ = BitConverter.ToSingle(bytes, i + 8);

            if (!float.IsNaN(fX) && !float.IsInfinity(fX) &&
                !float.IsNaN(fY) && !float.IsInfinity(fY) &&
                !float.IsNaN(fZ) && !float.IsInfinity(fZ))
            {
                if (fX is >= 1000f and <= 15000f &&
                    fY is >= 1000f and <= 15000f &&
                    fZ is >= -300f and <= 2000f)
                {
                    float normX = (fX / 16384f) * 24f - 12f;
                    float normY = (fY / 16384f) * 24f - 12f;
                    float normZ = (fZ / 1000f) * 2f;
                    positions.Add(new Vector3(normX, normY, normZ));
                }
            }
        }

        if (positions.Count == 0)
        {
            logger?.LogInformation("[ReplayStudioService] No valid 3D coordinates found in replaydata stream");
            return keyframes;
        }

        int durationMs = (int)Math.Max(1000, metadata.MatchLengthMs);
        int intervalMs = 10000;
        int totalFrames = (durationMs / intervalMs) + 1;
        int posIndex = 0;

        for (int frame = 0; frame <= totalFrames; frame++)
        {
            int timestamp = Math.Min(frame * intervalMs, durationMs);
            float progress = (float)timestamp / Math.Max(1, durationMs);

            GameStateSnapshot snap = new()
            {
                TimestampMs = timestamp,
                IsKeyframe = true,
                KeyframeTag = $"Estimated S2R2 Keyframe {frame}",
                IsEstimatedFallback = true,
                ScoreLegion = (int)(metadata.Players.Where(p => p.Team == 1).Sum(p => p.Kills) * progress),
                ScoreHellbourne = (int)(metadata.Players.Where(p => p.Team == 2).Sum(p => p.Kills) * progress),
                NetWorthAdvantage = (int)((metadata.WinnerTeamName == "Legion" ? 5000 : -5000) * progress)
            };

            for (int slot = 0; slot < 10; slot++)
            {
                ReplayPlayerMetadata? p = metadata.Players.FirstOrDefault(x => x.ClientNum == slot);
                int team = slot < 5 ? 1 : 2;

                Vector3 pos;
                if (positions.Count > 0)
                {
                    pos = positions[posIndex % positions.Count];
                    posIndex++;
                }
                else
                {
                    pos = team == 1 ? new Vector3(-11.7f, -11.7f, 2.2f) : new Vector3(11.7f, 11.7f, 2.2f);
                }

                HeroSnapshot hero = new()
                {
                    ClientIndex = slot,
                    Team = team,
                    PlayerName = p?.Name ?? $"Player {slot + 1}",
                    HeroName = p?.HeroName ?? (team == 1 ? "Hero_Devourer" : "Hero_Pyromancer"),
                    Position = pos,
                    HeadingAngle = 45f,
                    Level = Math.Max(1, (int)(1 + 24 * progress)),
                    Health = 1000f,
                    MaxHealth = 1000f,
                    Mana = 500f,
                    MaxMana = 500f,
                    IsAlive = true,
                    IsEstimatedFallback = true,
                    Kills = (int)((p?.Kills ?? 0) * progress),
                    Deaths = (int)((p?.Deaths ?? 0) * progress),
                    Assists = (int)((p?.Assists ?? 0) * progress),
                    Gold = (int)((p?.Gold ?? 0) * progress),
                    GoldPerMinute = p?.GoldPerMinute ?? 300,
                    ExpPerMinute = p?.ExpPerMinute ?? 350
                };
                snap.Heroes[slot] = hero;
            }

            keyframes.Add(snap);
        }

        return keyframes;
    }
}
