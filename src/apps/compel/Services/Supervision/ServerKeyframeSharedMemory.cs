// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ServerKeyframeSharedMemory.cs
// System: Heroes of Newerth - Dedicated Game Server Cluster (COMPEL)
// Subsystem: Shared Memory State Keyframe Capture & Zero-Downtime Crash Recovery
// Milestone 4.5: UDP Session Proxy & Keyframe Crash Recovery
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Memory-mapped binary header stamped into physical RAM for zero-copy state keyframes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ServerKeyframeHeader
{
    public const uint ExpectedMagic = 0x4B474B46; // "KGKF" (Kongor KeyFrame)
    public const uint CurrentVersion = 1;
    public const int HeaderSizeBytes = 40;

    public uint Magic;
    public uint Version;
    public uint Sequence;
    public uint ServerTick;
    public ulong MatchId;
    public long TimestampUtcTicks;
    public uint PayloadSizeBytes;
    public uint Crc32;
}

/// <summary>
///     Point-in-time coordinates and vitals for a single player/hero unit within a keyframe payload.
/// </summary>
public sealed class KeyframePlayerCoordinate
{
    public int Slot { get; set; }
    public int Team { get; set; }
    public string Hero { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Health { get; set; } = 1000f;
    public float MaxHealth { get; set; } = 1000f;
    public float Mana { get; set; } = 500f;
    public float MaxMana { get; set; } = 500f;
    public int Level { get; set; } = 1;
    public float Heading { get; set; }
    public bool IsAlive { get; set; } = true;
}

/// <summary>
///     Structured keyframe payload parsed from the raw JSON payload bytes.
/// </summary>
public sealed class ServerKeyframePayload
{
    public uint GamePhase { get; set; }
    public uint ServerTick { get; set; }
    public ulong MatchId { get; set; }
    public ulong UptimeMs { get; set; }
    public uint ConnectedClients { get; set; }
    public int Port { get; set; }
    public List<KeyframePlayerCoordinate>? Players { get; set; }
    public List<KeyframePlayerCoordinate>? Entities { get; set; }
}

/// <summary>
///     Represents a compact point-in-time snapshot of the simulation state (hero positions, HP, cooldowns, game clock)
///     captured periodically by the dedicated server worker into shared memory.
/// </summary>
public sealed class KongorServerKeyframe
{
    public uint Sequence { get; init; }
    public uint ServerTick { get; init; }
    public ulong MatchId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public byte[] StatePayload { get; init; } = [];
    public uint Crc32 { get; init; }
    public List<KeyframePlayerCoordinate>? Players { get; init; }
    public List<KeyframePlayerCoordinate>? Entities { get; init; }

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

    public static bool TryParsePayloadJson(
        byte[] payloadBytes,
        out ServerKeyframePayload? payload,
        ILogger? logger = null)
    {
        payload = null;
        if (payloadBytes == null || payloadBytes.Length == 0) return false;

        try
        {
            string json = System.Text.Encoding.UTF8.GetString(payloadBytes);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;

            uint gamePhase = root.TryGetProperty("gamePhase", out System.Text.Json.JsonElement gpElem) && gpElem.TryGetUInt32(out uint gp) ? gp : 0;
            uint serverTick = root.TryGetProperty("serverTick", out System.Text.Json.JsonElement stElem) && stElem.TryGetUInt32(out uint st) ? st : 0;
            ulong matchId = root.TryGetProperty("matchId", out System.Text.Json.JsonElement miElem) && miElem.TryGetUInt64(out ulong mi) ? mi : 0;
            ulong uptimeMs = root.TryGetProperty("uptimeMs", out System.Text.Json.JsonElement utElem) && utElem.TryGetUInt64(out ulong ut) ? ut : 0;
            uint clients = root.TryGetProperty("connectedClients", out System.Text.Json.JsonElement ccElem) && ccElem.TryGetUInt32(out uint cc) ? cc : 0;
            int port = root.TryGetProperty("port", out System.Text.Json.JsonElement ptElem) && ptElem.TryGetInt32(out int pt) ? pt : 0;

            List<KeyframePlayerCoordinate>? players = null;
            if (root.TryGetProperty("players", out System.Text.Json.JsonElement plElem) && plElem.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                players = ParsePlayerCoordinates(plElem);
            }

            List<KeyframePlayerCoordinate>? entities = null;
            if (root.TryGetProperty("entities", out System.Text.Json.JsonElement entElem) && entElem.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                entities = ParsePlayerCoordinates(entElem);
            }

            payload = new ServerKeyframePayload
            {
                GamePhase = gamePhase,
                ServerTick = serverTick,
                MatchId = matchId,
                UptimeMs = uptimeMs,
                ConnectedClients = clients,
                Port = port,
                Players = players,
                Entities = entities
            };
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[Keyframe] Failed to parse JSON state payload");
            return false;
        }
    }

    private static List<KeyframePlayerCoordinate> ParsePlayerCoordinates(System.Text.Json.JsonElement arrayElem)
    {
        List<KeyframePlayerCoordinate> list = [];
        foreach (System.Text.Json.JsonElement item in arrayElem.EnumerateArray())
        {
            int slot = item.TryGetProperty("slot", out System.Text.Json.JsonElement sElem) && sElem.TryGetInt32(out int s)
                ? s
                : (item.TryGetProperty("clientIndex", out System.Text.Json.JsonElement ciElem) && ciElem.TryGetInt32(out int ci) ? ci : 0);
            int team = item.TryGetProperty("team", out System.Text.Json.JsonElement tElem) && tElem.TryGetInt32(out int t) ? t : (slot < 5 ? 1 : 2);
            string hero = item.TryGetProperty("hero", out System.Text.Json.JsonElement hElem)
                ? (hElem.GetString() ?? string.Empty)
                : (item.TryGetProperty("heroName", out System.Text.Json.JsonElement hnElem) ? (hnElem.GetString() ?? string.Empty) : string.Empty);
            string name = item.TryGetProperty("name", out System.Text.Json.JsonElement nElem)
                ? (nElem.GetString() ?? string.Empty)
                : (item.TryGetProperty("playerName", out System.Text.Json.JsonElement pnElem) ? (pnElem.GetString() ?? string.Empty) : string.Empty);

            float x = item.TryGetProperty("x", out System.Text.Json.JsonElement xElem) && xElem.TryGetSingle(out float fx) ? fx : 0f;
            float y = item.TryGetProperty("y", out System.Text.Json.JsonElement yElem) && yElem.TryGetSingle(out float fy) ? fy : 0f;
            float z = item.TryGetProperty("z", out System.Text.Json.JsonElement zElem) && zElem.TryGetSingle(out float fz) ? fz : 0f;

            float health = item.TryGetProperty("health", out System.Text.Json.JsonElement hpElem) && hpElem.TryGetSingle(out float fhp)
                ? fhp
                : (item.TryGetProperty("hp", out System.Text.Json.JsonElement hp2Elem) && hp2Elem.TryGetSingle(out float fhp2) ? fhp2 : 1000f);
            float maxHealth = item.TryGetProperty("maxHealth", out System.Text.Json.JsonElement mhpElem) && mhpElem.TryGetSingle(out float fmhp)
                ? fmhp
                : (item.TryGetProperty("maxHp", out System.Text.Json.JsonElement mhp2Elem) && mhp2Elem.TryGetSingle(out float fmhp2) ? fmhp2 : 1000f);
            float mana = item.TryGetProperty("mana", out System.Text.Json.JsonElement mpElem) && mpElem.TryGetSingle(out float fmp)
                ? fmp
                : (item.TryGetProperty("mp", out System.Text.Json.JsonElement mp2Elem) && mp2Elem.TryGetSingle(out float fmp2) ? fmp2 : 500f);
            float maxMana = item.TryGetProperty("maxMana", out System.Text.Json.JsonElement mmpElem) && mmpElem.TryGetSingle(out float fmmp)
                ? fmmp
                : (item.TryGetProperty("maxMp", out System.Text.Json.JsonElement mmp2Elem) && mmp2Elem.TryGetSingle(out float fmmp2) ? fmmp2 : 500f);

            int level = item.TryGetProperty("level", out System.Text.Json.JsonElement lvlElem) && lvlElem.TryGetInt32(out int lvl) ? lvl : 1;
            float heading = item.TryGetProperty("heading", out System.Text.Json.JsonElement hdElem) && hdElem.TryGetSingle(out float fhd)
                ? fhd
                : (item.TryGetProperty("headingAngle", out System.Text.Json.JsonElement hdaElem) && hdaElem.TryGetSingle(out float fhda) ? fhda : 0f);
            bool isAlive = !item.TryGetProperty("isAlive", out System.Text.Json.JsonElement alElem) || alElem.GetBoolean();

            list.Add(new KeyframePlayerCoordinate
            {
                Slot = slot,
                Team = team,
                Hero = hero,
                PlayerName = name,
                X = x,
                Y = y,
                Z = z,
                Health = health,
                MaxHealth = maxHealth,
                Mana = mana,
                MaxMana = maxMana,
                Level = level,
                Heading = heading,
                IsAlive = isAlive
            });
        }
        return list;
    }
}

/// <summary>
///     Provides high-performance shared-memory read and write operations for dedicated server state keyframes.
///     Backed by physical RAM via Win32 Shared Memory / POSIX MemoryMappedFile (Local\KongorServerKeyframe_{port}).
/// </summary>
public static class ServerKeyframeSharedMemory
{
    public const int DefaultCapacityBytes = 65536; // 64 KB capacity for compact keyframes

    public static string GetMapName(int port)
    {
        return OperatingSystem.IsWindows()
            ? $@"Local\KongorServerKeyframe_{port}"
            : $"KongorServerKeyframe_{port}";
    }

    public static string GetFallbackFilePath(int port)
    {
        string tempDir = Path.GetTempPath();
        return Path.Combine(tempDir, $"KongorServerKeyframe_{port}.bin");
    }

    /// <summary>
    ///     Atomically writes a state keyframe to shared memory for the specified dedicated server worker port.
    /// </summary>
    public static bool WriteKeyframe(
        int port,
        KongorServerKeyframe keyframe,
        int capacityBytes = DefaultCapacityBytes,
        ILogger? logger = null)
    {
        string mapName = GetMapName(port);
        MemoryMappedFile? mmf = null;
        FileStream? fallbackStream = null;

        try
        {
            string fallbackPath = GetFallbackFilePath(port);
            fallbackStream = new FileStream(fallbackPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fallbackStream.Length < capacityBytes)
            {
                fallbackStream.SetLength(capacityBytes);
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    mmf = MemoryMappedFile.CreateFromFile(fallbackStream, mapName, capacityBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                }
                catch (Exception)
                {
                    mmf = MemoryMappedFile.CreateFromFile(fallbackStream, null, capacityBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                }
            }
            else
            {
                mmf = MemoryMappedFile.CreateFromFile(fallbackStream, null, capacityBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            }

            using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, capacityBytes, MemoryMappedFileAccess.ReadWrite))
            {
                byte[] payload = keyframe.StatePayload ?? [];
                uint crc = KongorServerKeyframe.ComputeCrc32(payload);

                ServerKeyframeHeader header = new()
                {
                    Magic = ServerKeyframeHeader.ExpectedMagic,
                    Version = ServerKeyframeHeader.CurrentVersion,
                    Sequence = keyframe.Sequence,
                    ServerTick = keyframe.ServerTick,
                    MatchId = keyframe.MatchId,
                    TimestampUtcTicks = keyframe.Timestamp.UtcTicks,
                    PayloadSizeBytes = (uint)payload.Length,
                    Crc32 = crc
                };

                // Invalidate magic first during write to ensure partial writes are never observed
                accessor.Write(0, 0u);

                // Write payload starting at offset ServerKeyframeHeader.HeaderSizeBytes
                if (payload.Length > 0)
                {
                    accessor.WriteArray(ServerKeyframeHeader.HeaderSizeBytes, payload, 0, payload.Length);
                }

                // Write header fields
                accessor.Write(4, header.Version);
                accessor.Write(8, header.Sequence);
                accessor.Write(12, header.ServerTick);
                accessor.Write(16, header.MatchId);
                accessor.Write(24, header.TimestampUtcTicks);
                accessor.Write(32, header.PayloadSizeBytes);
                accessor.Write(36, header.Crc32);

                // Memory barrier before committing magic
                Thread.MemoryBarrier();

                // Commit magic
                accessor.Write(0, header.Magic);
            }

            logger?.LogDebug(
                "[Keyframe] Persisted keyframe for Port {Port} at Tick {Tick} (MatchId: {MatchId}, Seq: {Seq}, Payload: {Bytes} bytes)",
                port, keyframe.ServerTick, keyframe.MatchId, keyframe.Sequence, keyframe.StatePayload?.Length ?? 0);

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Keyframe] Failed to write keyframe to shared memory for Port {Port}", port);
            return false;
        }
        finally
        {
            mmf?.Dispose();
            fallbackStream?.Dispose();
        }
    }

    /// <summary>
    ///     Attempts to read and validate the latest state keyframe from shared memory for the specified port.
    ///     Returns false if no keyframe exists, magic is invalid, or CRC check fails.
    /// </summary>
    public static bool TryReadKeyframe(
        int port,
        out KongorServerKeyframe? keyframe,
        int capacityBytes = DefaultCapacityBytes,
        ILogger? logger = null)
    {
        keyframe = null;
        string mapName = GetMapName(port);
        MemoryMappedFile? mmf = null;
        FileStream? fallbackStream = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                }
                catch (Exception)
                {
                    // Fall back to disk-backed MMF below
                }
            }

            if (mmf == null)
            {
                string fallbackPath = GetFallbackFilePath(port);
                if (File.Exists(fallbackPath))
                {
                    fallbackStream = new FileStream(fallbackPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    mmf = MemoryMappedFile.CreateFromFile(fallbackStream, null, capacityBytes, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                }
                else
                {
                    return false;
                }
            }

            using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, capacityBytes, MemoryMappedFileAccess.Read))
            {
                uint magic = accessor.ReadUInt32(0);
                if (magic != ServerKeyframeHeader.ExpectedMagic)
                {
                    return false;
                }

                uint version = accessor.ReadUInt32(4);
                if (version != ServerKeyframeHeader.CurrentVersion)
                {
                    return false;
                }

                uint sequence = accessor.ReadUInt32(8);
                uint serverTick = accessor.ReadUInt32(12);
                ulong matchId = accessor.ReadUInt64(16);
                long timestampTicks = accessor.ReadInt64(24);
                uint payloadSize = accessor.ReadUInt32(32);
                uint expectedCrc = accessor.ReadUInt32(36);

                if (payloadSize > 65536u || capacityBytes < 40 || payloadSize > (uint)(capacityBytes - 40))
                {
                    return false;
                }

                byte[] payload = new byte[payloadSize];
                if (payloadSize > 0)
                {
                    accessor.ReadArray(ServerKeyframeHeader.HeaderSizeBytes, payload, 0, (int)payloadSize);
                }

                uint computedCrc = KongorServerKeyframe.ComputeCrc32(payload);
                if (computedCrc != expectedCrc)
                {
                    logger?.LogWarning(
                        "[Keyframe] CRC corruption detected for Port {Port} at Tick {Tick} (Expected 0x{Exp:X8}, Computed 0x{Act:X8})",
                        port, serverTick, expectedCrc, computedCrc);
                    return false;
                }

                List<KeyframePlayerCoordinate>? players = null;
                List<KeyframePlayerCoordinate>? entities = null;
                if (KongorServerKeyframe.TryParsePayloadJson(payload, out ServerKeyframePayload? parsedPayload, logger))
                {
                    players = parsedPayload?.Players;
                    entities = parsedPayload?.Entities;
                }

                keyframe = new KongorServerKeyframe
                {
                    Sequence = sequence,
                    ServerTick = serverTick,
                    MatchId = matchId,
                    Timestamp = new DateTimeOffset(timestampTicks, TimeSpan.Zero),
                    StatePayload = payload,
                    Crc32 = computedCrc,
                    Players = players,
                    Entities = entities
                };

                return true;
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[Keyframe] Could not read keyframe from shared memory for Port {Port}", port);
            return false;
        }
        finally
        {
            mmf?.Dispose();
            fallbackStream?.Dispose();
        }
    }

    /// <summary>
    ///     Creates a standalone binary buffer matching the KGKF header + CRC32 payload layout.
    /// </summary>
    public static byte[] CreateKgkfBytes(
        uint sequence,
        uint serverTick,
        ulong matchId,
        DateTimeOffset timestamp,
        string payloadJson)
    {
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadJson ?? string.Empty);
        uint crc = KongorServerKeyframe.ComputeCrc32(payloadBytes);

        byte[] output = new byte[ServerKeyframeHeader.HeaderSizeBytes + payloadBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), ServerKeyframeHeader.ExpectedMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), ServerKeyframeHeader.CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), serverTick);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(16, 8), matchId);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(24, 8), timestamp.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(32, 4), (uint)payloadBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(36, 4), crc);

        if (payloadBytes.Length > 0)
        {
            payloadBytes.CopyTo(output, ServerKeyframeHeader.HeaderSizeBytes);
        }

        return output;
    }

    /// <summary>
    ///     Cleans up shared memory and any fallback files for the specified dedicated server worker port.
    /// </summary>
    public static void ClearKeyframe(int port)
    {
        try
        {
            string fallbackPath = GetFallbackFilePath(port);
            if (File.Exists(fallbackPath))
            {
                File.Delete(fallbackPath);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of temporary fallback shared-memory artifact
        }
    }
}
