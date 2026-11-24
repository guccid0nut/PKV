// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Production service implementing IMatchmakingSimulateService:
/// 3-tier port discovery, 10-client concurrent headless TCP socket harness,
/// binary wire protocol framing, SHA1 authentication, and discrete latency benchmarking.
/// </summary>
public sealed class MatchmakingSimulateService : IMatchmakingSimulateService
{
    public const string S2AuthenticationSalt = "8roespiemlasToUmiuglEhOaMiaSWlesplUcOAniupr2esPOeBRiudOEphiutOuJ";
    public const string ClientVersion = "5.0.0.0";
    public const int DefaultChatServerPort = 11031;

    // Protocol Opcodes
    public const ushort NET_CHAT_CL_CONNECT = 0x0C00;
    public const ushort NET_CHAT_CL_ACCEPT = 0x1C00;
    public const ushort NET_CHAT_CL_REJECT = 0x1C01;
    public const ushort CHAT_CMD_OPTIONS = 0x00C0;
    public const ushort CHAT_CMD_INITIAL_STATUS = 0x000B;
    public const ushort CHAT_CMD_UPDATE_STATUS = 0x00C1;
    public const ushort NET_CHAT_CL_TMM_GROUP_CREATE = 0x0C0A;
    public const ushort NET_CHAT_CL_TMM_GROUP_JOIN_QUEUE = 0x0D01;
    public const ushort NET_CHAT_CL_TMM_GROUP_UPDATE = 0x0D03;
    public const ushort NET_CHAT_CL_TMM_GROUP_PLAYER_LOADING_STATUS = 0x0D04;
    public const ushort NET_CHAT_CL_TMM_GROUP_PLAYER_READY_STATUS = 0x0D05;
    public const ushort NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE = 0x0D06;
    public const ushort NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE = 0x0D09;
    public const ushort CHAT_CMD_AUTO_MATCH_CONNECT = 0x0062;

    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly Func<string, string?>? _customEnvLookup;

    public MatchmakingSimulateService(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        Func<string, string?>? customEnvLookup = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _customEnvLookup = customEnvLookup;
    }

    private void Log(string message) => _logger?.Invoke($"[MatchmakingSimulateService] {message}");

    /// <inheritdoc />
    public (int Port, string DiscoveryTier) ResolveChatServerPort(int? explicitPort, Func<string, string?>? envLookup = null)
    {
        // Tier 1: Explicit parameter
        if (explicitPort.HasValue && explicitPort.Value > 0 && explicitPort.Value <= 65535)
        {
            Log($"Resolved port {explicitPort.Value} via Tier 1 (explicit)");
            return (explicitPort.Value, "explicit");
        }

        // Tier 2: Aspire configuration & environment variables
        Func<string, string?> lookup = envLookup ?? _customEnvLookup ?? Environment.GetEnvironmentVariable;
        string[] envKeys =
        [
            "services__chat-server__tcp__0",
            "services__chat-server__client__0",
            "services__chatserver__tcp__0",
            "CHAT_SERVER_PORT_CLIENT",
            "CHAT_SERVER_PORT_CLIENT_LOCAL",
            "ChatServer:ClientPort"
        ];

        foreach (string key in envKeys)
        {
            string? val = lookup(key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                if (int.TryParse(val, out int p) && p > 0 && p <= 65535)
                {
                    Log($"Resolved port {p} from key '{key}' via Tier 2 (aspire)");
                    return (p, "aspire");
                }

                if (Uri.TryCreate(val, UriKind.Absolute, out Uri? uri) && uri.Port > 0 && uri.Port <= 65535)
                {
                    Log($"Resolved port {uri.Port} from URI '{val}' via Tier 2 (aspire)");
                    return (uri.Port, "aspire");
                }
            }
        }

        // Tier 3: Default fallback
        Log($"Resolved fallback port {DefaultChatServerPort} via Tier 3 (default)");
        return (DefaultChatServerPort, "default");
    }

    /// <inheritdoc />
    public async Task<MatchmakingSimulateResult> SimulateMatchmakingAsync(
        MatchmakingSimulateOptions options,
        CancellationToken cancellationToken = default)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        int clientCount = Math.Clamp(options.ClientCount <= 0 ? 10 : options.ClientCount, 1, 10);
        string host = string.IsNullOrWhiteSpace(options.ServerHost) ? "127.0.0.1" : options.ServerHost.Trim();
        (int port, string discoveryTier) = ResolveChatServerPort(options.ServerPort);
        int timeoutSec = options.TimeoutSeconds <= 0 ? 15 : options.TimeoutSeconds;

        Log($"Starting simulation: {clientCount} clients -> {host}:{port} (Tier: {discoveryTier}), timeout: {timeoutSec}s");

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        CancellationToken ct = timeoutCts.Token;

        ConcurrentBag<TcpClient> allSockets = [];
        ConcurrentBag<VirtualClientStatus> clientStatuses = [];
        MatchAssignmentInfo? matchAssignment = null;
        object matchLock = new();

        try
        {
            Task[] tasks = new Task[clientCount];
            for (int i = 0; i < clientCount; i++)
            {
                int clientIndex = i;
                tasks[i] = Task.Run(async () =>
                {
                    VirtualClientStatus status = await RunVirtualClientAsync(
                        clientIndex,
                        host,
                        port,
                        options,
                        allSockets,
                        assigned =>
                        {
                            lock (matchLock)
                            {
                                matchAssignment ??= assigned;
                            }
                        },
                        ct).ConfigureAwait(false);

                    clientStatuses.Add(status);
                }, ct);
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log("Simulation reached configured timeout threshold.");
            }
        }
        finally
        {
            // Clean teardown: Hard-reset all sockets to avoid TIME_WAIT socket leaks
            int closedCount = 0;
            foreach (TcpClient client in allSockets)
            {
                try
                {
                    client.Close();
                    client.Dispose();
                    closedCount++;
                }
                catch
                {
                    // Suppress cleanup errors
                }
            }
            Log($"Socket cleanup complete: {closedCount} sockets disposed.");
        }

        totalSw.Stop();

        // Sort clients by index
        List<VirtualClientStatus> orderedClients = clientStatuses.OrderBy(c => c.ClientIndex).ToList();
        int successfulConnections = orderedClients.Count(c => c.Connected);
        int handshakeCount = orderedClients.Count(c => c.HandshakeComplete);
        int queueJoinedCount = orderedClients.Count(c => c.QueueJoined);

        IEnumerable<double> connectLatencies = orderedClients.Where(c => c.Connected).Select(c => c.ConnectLatencyMs);
        IEnumerable<double> handshakeLatencies = orderedClients.Where(c => c.HandshakeComplete).Select(c => c.HandshakeLatencyMs);
        IEnumerable<double> queueJoinLatencies = orderedClients.Where(c => c.QueueJoined).Select(c => c.QueueJoinLatencyMs);

        MatchmakingLatencySummary latencySummary = new()
        {
            ConnectMs = CalculateStats(connectLatencies),
            HandshakeMs = CalculateStats(handshakeLatencies),
            QueueJoinMs = CalculateStats(queueJoinLatencies),
            MatchFormationMs = matchAssignment != null
                ? new LatencyStats
                {
                    MinMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
                    MaxMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
                    AvgMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2)
                }
                : null
        };

        bool overallSuccess = handshakeCount > 0;
        string? errorMessage = overallSuccess
            ? null
            : (orderedClients.FirstOrDefault(c => c.Error != null)?.Error ?? "No virtual clients completed handshake");

        string? remediationCat = null;
        string? remediationHint = null;

        if (!overallSuccess)
        {
            if (successfulConnections == 0)
            {
                remediationCat = "ChatServerOffline";
                remediationHint = $"ChatServer is not listening on {host}:{port}. Verify ChatServer is running via 'kongor_server_manage action:status' or start it with 'kongor_server_manage action:start target:chat-server'.";
            }
            else
            {
                remediationCat = "SimulationTimeout";
                remediationHint = $"The simulation timed out after {timeoutSec}s waiting for handshake responses. Inspect ChatServer logs or increase 'timeoutSeconds'.";
            }
        }

        return new MatchmakingSimulateResult
        {
            Status = overallSuccess ? "success" : "error",
            ResolvedPort = port,
            DiscoveryTier = discoveryTier,
            ClientCount = clientCount,
            SuccessfulConnections = successfulConnections,
            HandshakeCount = handshakeCount,
            QueueJoinedCount = queueJoinedCount,
            TotalElapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
            Latency = latencySummary,
            MatchAssigned = matchAssignment != null,
            MatchAssignment = matchAssignment,
            Clients = orderedClients,
            SocketsCleaned = allSockets.Count,
            ErrorMessage = errorMessage,
            RemediationCategory = remediationCat,
            RemediationHint = remediationHint,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private async Task<VirtualClientStatus> RunVirtualClientAsync(
        int clientIndex,
        string host,
        int port,
        MatchmakingSimulateOptions options,
        ConcurrentBag<TcpClient> allSockets,
        Action<MatchAssignmentInfo> onMatchAssigned,
        CancellationToken ct)
    {
        int accountId = 90000 + clientIndex;
        string accountName = $"VirtualClient_{clientIndex}";
        string sessionCookie = $"cookie_{accountId}";
        double connectMs = 0;
        double handshakeMs = 0;
        double queueJoinMs = 0;
        bool connected = false;
        bool handshakeComplete = false;
        bool queueJoined = false;
        bool matchAssigned = false;
        string? error = null;

        TcpClient client = new()
        {
            NoDelay = true,
            LingerState = new LingerOption(true, 0)
        };
        allSockets.Add(client);

        try
        {
            // 1. Connect
            Stopwatch sw = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            sw.Stop();
            connectMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            connected = true;

            NetworkStream stream = client.GetStream();

            // 2. Handshake
            string authHash = ComputeChatAuthHash(accountId, host, sessionCookie);
            byte[] connectPayload = BuildConnectPayload(accountId, sessionCookie, host, authHash, ClientVersion);
            byte[] connectPacket = EncodePacket(NET_CHAT_CL_CONNECT, connectPayload);

            sw.Restart();
            await stream.WriteAsync(connectPacket, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // Read response packet
            while (!handshakeComplete && !ct.IsCancellationRequested)
            {
                (ushort opcode, byte[] payload) = await ReadPacketAsync(stream, ct).ConfigureAwait(false);

                if (opcode == NET_CHAT_CL_ACCEPT)
                {
                    sw.Stop();
                    handshakeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                    handshakeComplete = true;
                }
                else if (opcode == NET_CHAT_CL_REJECT)
                {
                    byte reason = payload.Length > 0 ? payload[0] : (byte)0;
                    error = $"Connection rejected by ChatServer (0x1C01), reason: 0x{reason:X2}";
                    return BuildStatus();
                }
            }

            // 3. Matchmaking Group Creation & Queue Placement
            byte[] groupCreatePayload = BuildGroupCreatePayload(options.MapName, options.GameMode, options.Region, ClientVersion);
            byte[] groupCreatePacket = EncodePacket(NET_CHAT_CL_TMM_GROUP_CREATE, groupCreatePayload);

            sw.Restart();
            await stream.WriteAsync(groupCreatePacket, ct).ConfigureAwait(false);

            // Set loading status (100%) and ready status (1)
            byte[] loadingPacket = EncodePacket(NET_CHAT_CL_TMM_GROUP_PLAYER_LOADING_STATUS, [100]);
            byte[] readyPacket = EncodePacket(NET_CHAT_CL_TMM_GROUP_PLAYER_READY_STATUS, [0x01, 0x01]);

            await stream.WriteAsync(loadingPacket, ct).ConfigureAwait(false);
            await stream.WriteAsync(readyPacket, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // 4. Read Loop: Monitor Queue Updates and Match Formation
            while (!ct.IsCancellationRequested)
            {
                CancellationToken readCt = ct;
                using CancellationTokenSource? quickCts = (!options.SimulateMatchServer && queueJoined)
                    ? new CancellationTokenSource(TimeSpan.FromMilliseconds(150))
                    : null;
                using CancellationTokenSource? linkedCts = quickCts != null
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct, quickCts.Token)
                    : null;

                if (linkedCts != null) readCt = linkedCts.Token;

                (ushort opcode, byte[] payload) packet;
                try
                {
                    packet = await ReadPacketAsync(stream, readCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (quickCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
                {
                    // Queue joined and no match found packet arrived within 150ms -> benchmark complete
                    break;
                }

                (ushort opcode, byte[] payload) = packet;

                if (opcode == NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE ||
                    opcode == NET_CHAT_CL_TMM_GROUP_JOIN_QUEUE ||
                    opcode == NET_CHAT_CL_TMM_GROUP_UPDATE)
                {
                    if (!queueJoined)
                    {
                        sw.Stop();
                        queueJoinMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                        queueJoined = true;
                    }
                }
                else if (opcode == NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE) // 0x0D09
                {
                    matchAssigned = true;
                    if (!queueJoined)
                    {
                        sw.Stop();
                        queueJoinMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                        queueJoined = true;
                    }

                    if (payload.Length >= 2)
                    {
                        try
                        {
                            using MemoryStream pms = new(payload);
                            using BinaryReader preader = new(pms, Encoding.UTF8);
                            string map = ReadNullTerminatedString(preader);
                            byte teamSize = preader.BaseStream.Position < preader.BaseStream.Length ? preader.ReadByte() : (byte)(options.ClientCount / 2);
                            onMatchAssigned(new MatchAssignmentInfo
                            {
                                MatchId = 10001,
                                ServerAddress = host,
                                ServerPort = 11235,
                                Map = string.IsNullOrEmpty(map) ? options.MapName : map,
                                TeamSize = teamSize > 0 ? teamSize : 5
                            });
                        }
                        catch
                        {
                            onMatchAssigned(new MatchAssignmentInfo
                            {
                                MatchId = 10001,
                                ServerAddress = host,
                                ServerPort = 11235,
                                Map = options.MapName,
                                TeamSize = options.ClientCount / 2 > 0 ? options.ClientCount / 2 : 5
                            });
                        }
                    }
                    break;
                }
                else if (opcode == CHAT_CMD_AUTO_MATCH_CONNECT) // 0x0062
                {
                    matchAssigned = true;
                    if (!queueJoined)
                    {
                        sw.Stop();
                        queueJoinMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                        queueJoined = true;
                    }

                    if (payload.Length >= 8)
                    {
                        try
                        {
                            using MemoryStream ms = new(payload);
                            using BinaryReader reader = new(ms, Encoding.UTF8);
                            byte matchType = reader.ReadByte();
                            int matchId = reader.ReadInt32();
                            string sAddress = ReadNullTerminatedString(reader);
                            short sPort = reader.ReadInt16();

                            onMatchAssigned(new MatchAssignmentInfo
                            {
                                MatchId = matchId,
                                ServerAddress = string.IsNullOrEmpty(sAddress) ? host : sAddress,
                                ServerPort = sPort,
                                Map = options.MapName,
                                TeamSize = options.ClientCount / 2 > 0 ? options.ClientCount / 2 : 5
                            });
                        }
                        catch
                        {
                            // fallback
                        }
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected upon timeout completion
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return BuildStatus();

        VirtualClientStatus BuildStatus() => new()
        {
            ClientIndex = clientIndex,
            AccountId = accountId,
            AccountName = accountName,
            Connected = connected,
            ConnectLatencyMs = connectMs,
            HandshakeComplete = handshakeComplete,
            HandshakeLatencyMs = handshakeMs,
            QueueJoined = queueJoined,
            QueueJoinLatencyMs = queueJoinMs,
            MatchAssigned = matchAssigned,
            Error = error
        };
    }

    #region Protocol Codec Helpers

    public static string ComputeChatAuthHash(int accountId, string ip, string cookie, string? salt = null)
    {
        salt ??= S2AuthenticationSalt;
        string raw = $"{accountId}{ip}{cookie}{salt}";
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    public static byte[] EncodePacket(ushort opcode, byte[]? payload = null)
    {
        payload ??= [];
        ushort totalPayloadLength = (ushort)(2 + payload.Length);
        byte[] packet = new byte[2 + totalPayloadLength];

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), totalPayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), opcode);
        if (payload.Length > 0)
        {
            payload.CopyTo(packet.AsSpan(4));
        }

        return packet;
    }

    public static async Task<(ushort Opcode, byte[] Payload)> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] lengthHeader = new byte[2];
        await stream.ReadExactlyAsync(lengthHeader.AsMemory(0, 2), ct).ConfigureAwait(false);
        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(lengthHeader);

        if (payloadLength < 2)
        {
            throw new InvalidOperationException($"Malformed packet with invalid payload length {payloadLength}.");
        }

        byte[] payloadBuffer = new byte[payloadLength];
        await stream.ReadExactlyAsync(payloadBuffer.AsMemory(0, payloadLength), ct).ConfigureAwait(false);

        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payloadBuffer.AsSpan(0, 2));
        byte[] payload = payloadBuffer.Length > 2 ? payloadBuffer.AsSpan(2).ToArray() : [];

        return (opcode, payload);
    }

    public static byte[] BuildConnectPayload(
        int accountId,
        string cookie,
        string ip,
        string authHash,
        string clientVersion = ClientVersion)
    {
        using MemoryStream ms = new();
        using BinaryWriter writer = new(ms, Encoding.UTF8);

        writer.Write(accountId);
        WriteNullTermString(writer, cookie);
        WriteNullTermString(writer, ip);
        WriteNullTermString(writer, authHash);
        writer.Write(1); // Protocol Version
        writer.Write((byte)1); // OS ID (Windows)
        writer.Write((byte)1); // OS Major
        writer.Write((byte)0); // OS Minor
        writer.Write((byte)0); // OS Patch
        WriteNullTermString(writer, "build");
        WriteNullTermString(writer, "arch");

        string[] versionParts = clientVersion.Split('.');
        writer.Write(byte.Parse(versionParts[0]));
        writer.Write(byte.Parse(versionParts[1]));
        writer.Write(byte.Parse(versionParts[2]));
        writer.Write(byte.Parse(versionParts[3]));

        writer.Write((byte)1); // Status = Connected
        writer.Write((byte)0); // Mode = Available
        WriteNullTermString(writer, "USE");
        WriteNullTermString(writer, "en");

        return ms.ToArray();
    }

    public static byte[] BuildGroupCreatePayload(
        string mapName = "caldavar",
        string modes = "ap",
        string regions = "USE",
        string clientVersion = ClientVersion)
    {
        using MemoryStream ms = new();
        using BinaryWriter writer = new(ms, Encoding.UTF8);

        WriteNullTermString(writer, clientVersion);
        writer.Write((byte)2); // TMMType.TMM_TYPE_PVP
        writer.Write((byte)1); // TMMGameType.TMM_GAME_TYPE_NORMAL
        WriteNullTermString(writer, mapName);
        WriteNullTermString(writer, modes);
        WriteNullTermString(writer, regions);
        writer.Write((byte)1); // Ranked = true
        writer.Write((byte)0); // MatchFidelity
        writer.Write((byte)0); // BotDifficulty
        writer.Write((byte)0); // RandomizeBots

        return ms.ToArray();
    }

    public static string ReadNullTerminatedString(BinaryReader reader)
    {
        List<byte> bytes = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void WriteNullTermString(BinaryWriter writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Write((byte)0);
    }

    private static LatencyStats CalculateStats(IEnumerable<double> samples)
    {
        List<double> list = samples.ToList();
        if (list.Count == 0) return new LatencyStats();

        return new LatencyStats
        {
            MinMs = Math.Round(list.Min(), 2),
            MaxMs = Math.Round(list.Max(), 2),
            AvgMs = Math.Round(list.Average(), 2)
        };
    }

    #endregion
}

/// <summary>
/// Wave 4 (#227): TigerBeetle zero-float rational probability (`src/stdx/prng.zig`)
/// eliminating IEEE-754 floating-point non-determinism across platforms.
/// </summary>
public readonly record struct Ratio
{
    public uint Numerator { get; }
    public uint Denominator { get; }

    public Ratio(uint numerator, uint denominator)
    {
        if (denominator == 0U)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "[Ratio] Denominator must be strictly positive.");
        }
        if (numerator > denominator)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), numerator, $"[Ratio] Numerator ({numerator}) cannot exceed denominator ({denominator}).");
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public static Ratio Zero(uint denominator = 100U) => new(0U, denominator);
    public static Ratio One(uint denominator = 100U) => new(denominator, denominator);
}

/// <summary>
/// Wave 4 (#227): TigerBeetle deterministic PRNG (`src/stdx/prng.zig`) featuring Lemire's divisionless
/// `IntInclusive(ulong max)` (`UInt128` multiply-high rejection sampling), zero-float `Chance(Ratio)`,
/// `RandomSubset` bitmasks, and `GenerateSwarmWeights` for skewed matchmaking fault testing.
/// </summary>
public sealed class TigerBeetlePrng
{
    private ulong _s0;
    private ulong _s1;

    public long RejectionSampleRetries { get; private set; }

    public TigerBeetlePrng(ulong seed = 0x71BE_5EED_CAFE_BEEFUL)
    {
        // SplitMix64 seeding
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        _s0 = z ^ (z >> 31);

        z = seed + 0x3C6EF372FE94F82BUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        _s1 = z ^ (z >> 31);

        if (_s0 == 0UL && _s1 == 0UL)
        {
            _s0 = 1UL;
        }
    }

    public ulong NextUInt64()
    {
        ulong s0 = _s0;
        ulong s1 = _s1;
        ulong result = unchecked(s0 + s1);

        s1 ^= s0;
        _s0 = System.Numerics.BitOperations.RotateLeft(s0, 24) ^ s1 ^ (s1 << 16);
        _s1 = System.Numerics.BitOperations.RotateLeft(s1, 37);
        return result;
    }

    /// <summary>
    /// Lemire's divisionless unbiased bounded integer generation in `[0, max]` using `UInt128` multiply-high.
    /// </summary>
    public ulong IntInclusive(ulong max)
    {
        if (max == ulong.MaxValue)
        {
            return NextUInt64();
        }

        ulong range = max + 1UL;
        ulong x = NextUInt64();
        UInt128 product = (UInt128)x * (UInt128)range;
        ulong low = (ulong)product;

        if (low < range)
        {
            ulong threshold = unchecked(0UL - range) % range;
            while (low < threshold)
            {
                RejectionSampleRetries++;
                x = NextUInt64();
                product = (UInt128)x * (UInt128)range;
                low = (ulong)product;
            }
        }

        return (ulong)(product >> 64);
    }

    /// <summary>
    /// Evaluates a zero-float rational probability `numerator / denominator`.
    /// </summary>
    public bool Chance(Ratio ratio)
    {
        if (ratio.Numerator == 0U)
        {
            return false;
        }
        if (ratio.Numerator >= ratio.Denominator)
        {
            return true;
        }

        return IntInclusive((ulong)(ratio.Denominator - 1U)) < (ulong)ratio.Numerator;
    }

    /// <summary>
    /// Selects a non-empty random subset bitmask of `[0, count)` with `k` active bits (`1 <= k <= count`).
    /// </summary>
    public ulong RandomSubset(int count)
    {
        if (count <= 0 || count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "[TigerBeetlePrng] Subset count must be in [1, 64].");
        }

        int targetActive = (int)IntInclusive((ulong)(count - 1)) + 1;
        ulong mask = 0UL;
        int active = 0;

        for (int i = 0; i < count; i++)
        {
            int remainingSlots = count - i;
            int remainingNeeded = targetActive - active;
            if (remainingNeeded <= 0)
            {
                break;
            }

            if ((int)IntInclusive((ulong)(remainingSlots - 1)) < remainingNeeded)
            {
                mask |= 1UL << i;
                active++;
            }
        }

        return mask;
    }

    /// <summary>
    /// Generates heterogeneous integer swarm weights for `count` fault/queue categories:
    /// selects a random subset of active categories (`0` weight for disabled categories) and assigns
    /// skewed integer weights `[1, 100]` to active categories.
    /// </summary>
    public ulong[] GenerateSwarmWeights(int count)
    {
        ulong subsetMask = RandomSubset(count);
        ulong[] weights = new ulong[count];

        for (int i = 0; i < count; i++)
        {
            if ((subsetMask & (1UL << i)) != 0UL)
            {
                weights[i] = IntInclusive(99UL) + 1UL;
            }
        }

        return weights;
    }

    public int SampleWeightedIndex(ReadOnlySpan<ulong> weights)
    {
        ulong total = 0UL;
        for (int i = 0; i < weights.Length; i++)
        {
            total += weights[i];
        }

        if (total == 0UL)
        {
            throw new ArgumentException("[TigerBeetlePrng] Total weight must be greater than zero.", nameof(weights));
        }

        ulong ticket = IntInclusive(total - 1UL);
        ulong cumulative = 0UL;
        for (int i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (ticket < cumulative)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }
}

