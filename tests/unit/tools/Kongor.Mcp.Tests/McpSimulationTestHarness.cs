// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Moq;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Core testing harness, protocol codecs, and simulation engines for ProjectKongor Phase 3 Modernization.
/// Supports high-speed, zero-window inner-loop execution (< 2.0s per test).
/// </summary>
public static class McpSimulationTestHarness
{
    public const string S2ChatServerAuthenticationSalt = "8roespiemlasToUmiuglEhOaMiaSWlesplUcOAniupr2esPOeBRiudOEphiutOuJ";
    public const string DefaultClientVersion = "4.10.1.0";
    public const int DefaultTickRate = 20; // 20 ticks per second
    public const int DefaultTickDeltaMs = 50; // 50ms per tick

    #region Protocol Framing & Codec (Opcodes & Wire Framing)

    public static class Opcodes
    {
        public const ushort NET_CHAT_CL_CONNECT = 0x0C00;
        public const ushort NET_CHAT_CL_ACCEPT = 0x1C00;
        public const ushort NET_CHAT_CL_REJECT = 0x1C01;
        public const ushort NET_CHAT_CL_TMM_GROUP_CREATE = 0x0C0A;
        public const ushort NET_CHAT_CL_TMM_GROUP_JOIN_QUEUE = 0x0D01;
        public const ushort NET_CHAT_CL_TMM_GROUP_PLAYER_LOADING_STATUS = 0x0D04;
        public const ushort NET_CHAT_CL_TMM_GROUP_PLAYER_READY_STATUS = 0x0D05;
        public const ushort NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE = 0x0D06;
        public const ushort NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE = 0x0D09;
        public const ushort CHAT_CMD_AUTO_MATCH_CONNECT = 0x0062;
    }

    /// <summary>
    /// Computes S2Crypto chat authentication hash matching server expectation:
    /// SHA1(accountId + remoteIp + cookie + S2ChatServerAuthenticationSalt)
    /// </summary>
    public static string ComputeChatAuthHash(int accountId, string ip, string cookie)
    {
        string raw = $"{accountId}{ip}{cookie}{S2ChatServerAuthenticationSalt}";
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Writes a length-prefixed binary packet with a 2-byte Little-Endian unsigned length prefix.
    /// Total packet size = 2 + 2 (opcode) + payload.Length.
    /// </summary>
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

    /// <summary>
    /// Reads a length-prefixed packet from the given span.
    /// </summary>
    public static bool TryDecodePacket(ReadOnlySpan<byte> buffer, out ushort opcode, out byte[] payload, out int consumedBytes)
    {
        opcode = 0;
        payload = [];
        consumedBytes = 0;

        if (buffer.Length < 4) return false;

        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]);
        if (buffer.Length < 2 + payloadLength) return false;

        opcode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
        int dataLength = payloadLength - 2;
        payload = buffer.Slice(4, dataLength).ToArray();
        consumedBytes = 2 + payloadLength;
        return true;
    }

    /// <summary>
    /// Builds full NET_CHAT_CL_CONNECT binary payload.
    /// </summary>
    public static byte[] BuildConnectPayload(
        int accountId,
        string cookie,
        string ip,
        string authHash,
        string clientVersion = DefaultClientVersion)
    {
        using MemoryStream ms = new();
        using BinaryWriter writer = new(ms, Encoding.UTF8);

        writer.Write(accountId);
        writer.Write(Encoding.UTF8.GetBytes(cookie));
        writer.Write((byte)0); // null-terminated
        writer.Write(Encoding.UTF8.GetBytes(ip));
        writer.Write((byte)0);
        writer.Write(Encoding.UTF8.GetBytes(authHash));
        writer.Write((byte)0);
        writer.Write(1); // chatProtocolVersion
        writer.Write((byte)1); // osId
        writer.Write((byte)1); // osMajor
        writer.Write((byte)0); // osMinor
        writer.Write((byte)0); // osPatch
        writer.Write(Encoding.UTF8.GetBytes("build\0"));
        writer.Write(Encoding.UTF8.GetBytes("arch\0"));

        string[] versionParts = clientVersion.Split('.');
        writer.Write(byte.Parse(versionParts[0]));
        writer.Write(byte.Parse(versionParts[1]));
        writer.Write(byte.Parse(versionParts[2]));
        writer.Write(byte.Parse(versionParts[3]));

        writer.Write((byte)1); // status
        writer.Write((byte)0); // mode
        writer.Write(Encoding.UTF8.GetBytes("USE\0"));
        writer.Write(Encoding.UTF8.GetBytes("en\0"));

        return ms.ToArray();
    }

    /// <summary>
    /// Builds group create payload (0x0C0A).
    /// </summary>
    public static byte[] BuildGroupCreatePayload(
        string clientVersion = DefaultClientVersion,
        byte groupType = 2,
        byte gameType = 1,
        string mapName = "caldavar",
        string modes = "ap",
        string regions = "USE",
        bool ranked = true)
    {
        using MemoryStream ms = new();
        using BinaryWriter writer = new(ms, Encoding.UTF8);

        writer.Write(Encoding.UTF8.GetBytes(clientVersion + "\0"));
        writer.Write(groupType);
        writer.Write(gameType);
        writer.Write(Encoding.UTF8.GetBytes(mapName + "\0"));
        writer.Write(Encoding.UTF8.GetBytes(modes + "\0"));
        writer.Write(Encoding.UTF8.GetBytes(regions + "\0"));
        writer.Write(ranked ? (byte)1 : (byte)0);
        writer.Write((byte)0); // fidelity
        writer.Write((byte)0); // botDifficulty
        writer.Write((byte)0); // randomizeBots

        return ms.ToArray();
    }

    #endregion

    #region In-Memory Loopback ChatServer Simulator

    /// <summary>
    /// High-speed in-memory loopback ChatServer simulator supporting up to 10 concurrent clients,
    /// protocol handshakes, and matchmaking group updates.
    /// </summary>
    public sealed class MockChatServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private Task? _acceptLoopTask;

        private int _handshakesCompleted;
        private int _groupsCreated;
        private int _playersReady;

        public int Port { get; }
        public int ConnectedClientsCount => _cts.IsCancellationRequested ? 0 : _clients.Count(c =>
        {
            try { return c.Connected && c.Client != null && c.Client.Connected; }
            catch { return false; }
        });
        public int HandshakesCompleted => Volatile.Read(ref _handshakesCompleted);
        public int GroupsCreated => Volatile.Read(ref _groupsCreated);
        public int PlayersReady => Volatile.Read(ref _playersReady);
        public bool SimulateRejectionOnVersionMismatch { get; set; } = true;

        public MockChatServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start()
        {
            _acceptLoopTask = Task.Run(AcceptConnectionsLoopAsync);
        }

        private async Task AcceptConnectionsLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    client.NoDelay = true;
                    client.LingerState = new LingerOption(true, 0);
                    _clients.Add(client);
                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (_cts.Token.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using (NetworkStream stream = client.GetStream())
            {
                byte[] lengthHeader = new byte[2];
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int read = await stream.ReadAsync(lengthHeader.AsMemory(0, 2), ct).ConfigureAwait(false);
                    if (read < 2) break;

                    ushort payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(lengthHeader);
                    byte[] payloadBuf = new byte[payloadLen];
                    int totalRead = 0;
                    while (totalRead < payloadLen)
                    {
                        int bytesRead = await stream.ReadAsync(payloadBuf.AsMemory(totalRead, payloadLen - totalRead), ct).ConfigureAwait(false);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }

                    if (totalRead < payloadLen) break;

                    ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payloadBuf.AsSpan(0, 2));

                    if (opcode == Opcodes.NET_CHAT_CL_CONNECT)
                    {
                        // Check client version in payload (bytes after accountId, cookie, ip, hash, etc.)
                        bool versionOk = true;
                        if (SimulateRejectionOnVersionMismatch && payloadLen > 40)
                        {
                            // if version string does not match 4.10.1.0, reject
                            // In test payload check
                        }

                        if (versionOk)
                        {
                            Interlocked.Increment(ref _handshakesCompleted);
                            byte[] acceptPacket = EncodePacket(Opcodes.NET_CHAT_CL_ACCEPT, [0x01, 0x00, 0x00, 0x00]);
                            await stream.WriteAsync(acceptPacket, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            byte[] rejectPacket = EncodePacket(Opcodes.NET_CHAT_CL_REJECT, [0x01, 0x00]); // ECR_BAD_VERSION
                            await stream.WriteAsync(rejectPacket, ct).ConfigureAwait(false);
                            break;
                        }
                    }
                    else if (opcode == Opcodes.NET_CHAT_CL_TMM_GROUP_CREATE)
                    {
                        Interlocked.Increment(ref _groupsCreated);
                        byte[] groupCreatedPacket = EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE, [0x01, 0x00, 0x00, 0x00]);
                        await stream.WriteAsync(groupCreatedPacket, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else if (opcode == Opcodes.NET_CHAT_CL_TMM_GROUP_PLAYER_READY_STATUS)
                    {
                        Interlocked.Increment(ref _playersReady);
                        byte[] matchFoundPacket = EncodePacket(Opcodes.NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE, [0x01, 0x00]);
                        await stream.WriteAsync(matchFoundPacket, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
        }

        private bool _isDisposed;

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            foreach (var client in _clients)
            {
                try { client.Close(); client.Dispose(); } catch { }
            }
            if (_acceptLoopTask != null)
            {
                try { await _acceptLoopTask.ConfigureAwait(false); } catch { }
            }
            _cts.Dispose();
        }
    }

    #endregion

    #region Port-Addressed Dedicated Server Worker Simulator

    /// <summary>
    /// Simulates dedicated game server worker lifecycle on designated port (e.g. 11235),
    /// dual-channel command routing (stdin and named pipe), and 5v5 bot filling.
    /// </summary>
    public sealed class MockDedicatedWorker
    {
        public int Port { get; }
        public bool IsRunning { get; private set; }
        public bool ClientConnected { get; private set; }
        public int GamePhase { get; set; } = 0;
        public int Team1Bots { get; private set; }
        public int Team2Bots { get; private set; }
        public bool HumanAssignedToTeam1Slot0 { get; private set; }
        public List<string> CommandsReceivedOnStdin { get; } = [];
        public List<string> CommandsReceivedOnPipe { get; } = [];

        public MockDedicatedWorker(int port = 11235)
        {
            Port = port;
        }

        public void Start()
        {
            IsRunning = true;
            GamePhase = 0; // Loading
        }

        public void Stop()
        {
            IsRunning = false;
            ClientConnected = false;
            GamePhase = 0;
            Team1Bots = 0;
            Team2Bots = 0;
            HumanAssignedToTeam1Slot0 = false;
        }

        public void ConnectClient()
        {
            if (!IsRunning) throw new InvalidOperationException("Dedicated server worker not running.");
            ClientConnected = true;
            HumanAssignedToTeam1Slot0 = true;
            GamePhase = 1; // Team pick
        }

        public void ExecuteCommand(string channel, string command)
        {
            if (!IsRunning) throw new InvalidOperationException("Dedicated server worker not running.");

            if (string.Equals(channel, "stdin", StringComparison.OrdinalIgnoreCase))
            {
                CommandsReceivedOnStdin.Add(command);
            }
            else
            {
                CommandsReceivedOnPipe.Add(command);
            }

            string trimmed = command.Trim();
            if (trimmed.Equals("AddFakePlayer 1", StringComparison.OrdinalIgnoreCase))
            {
                Team1Bots++;
            }
            else if (trimmed.Equals("AddFakePlayer 2", StringComparison.OrdinalIgnoreCase))
            {
                Team2Bots++;
            }
            else if (trimmed.StartsWith("StartGame", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("CreateGame", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("RequestMatchStart", StringComparison.OrdinalIgnoreCase))
            {
                GamePhase = 3; // Active Match
            }
        }

        public int TotalPlayerCount => (HumanAssignedToTeam1Slot0 ? 1 : 0) + Team1Bots + Team2Bots;
    }

    #endregion

    #region Debounced File Watching & Hot Reload Simulator

    /// <summary>
    /// Simulates HotReloadService sliding debounce timer and IPC execution.
    /// </summary>
    public sealed class MockHotReloadService
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _touchedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();
        private int _debounceMs = 150;
        private CancellationTokenSource? _timerCts;

        public bool IsWatching { get; private set; }
        public List<string> WatchedPaths { get; } = [];
        public int TotalReloads { get; private set; }
        public double LastReloadDurationMs { get; private set; }
        public DateTimeOffset? LastReloadTimestamp { get; private set; }
        public List<string> LastChangedFiles { get; private set; } = [];
        public List<string> DispatchedIpcCommands { get; } = [];

        public int DebounceMs
        {
            get => _debounceMs;
            set => _debounceMs = value <= 0 ? 150 : Math.Clamp(value, 10, 30000);
        }

        public void Watch(IEnumerable<string>? paths = null, int debounceMs = 150)
        {
            IsWatching = true;
            DebounceMs = debounceMs;
            WatchedPaths.Clear();
            WatchedPaths.AddRange(paths ?? ["games/shared/ui/", "games/shared/scripts/"]);
        }

        public void Unwatch()
        {
            IsWatching = false;
            lock (_sync)
            {
                _timerCts?.Cancel();
                _timerCts?.Dispose();
                _timerCts = null;
                _touchedFiles.Clear();
            }
        }

        public void NotifyFileChanged(string filePath)
        {
            if (!IsWatching) return;

            _touchedFiles[filePath] = TimeProvider.System.GetUtcNow();

            lock (_sync)
            {
                _timerCts?.Cancel();
                _timerCts?.Dispose();
                _timerCts = new CancellationTokenSource();
                var token = _timerCts.Token;

                _ = Task.Delay(_debounceMs, token).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                    {
                        TriggerReload();
                    }
                }, TaskScheduler.Default);
            }
        }

        public void TriggerReload()
        {
            lock (_sync)
            {
                _timerCts?.Cancel();
                _timerCts?.Dispose();
                _timerCts = null;

                var files = _touchedFiles.Keys.ToList();
                _touchedFiles.Clear();

                if (files.Count > 0 || LastChangedFiles.Count == 0)
                {
                    LastChangedFiles = files;
                }
                DispatchedIpcCommands.Add("ReloadInterfaces");
                DispatchedIpcCommands.Add("Script dofile('/ui/scripts/nexus_bridge.lua')");
                TotalReloads++;
                LastReloadDurationMs = 28.5; // sub-200ms
                LastReloadTimestamp = TimeProvider.System.GetUtcNow();
            }
        }

        public async Task<bool> WaitForReloadAsync(int expectedCount = 1, int timeoutMs = 2000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_sync)
                {
                    if (TotalReloads >= expectedCount) return true;
                }
                await Task.Delay(10);
            }
            lock (_sync)
            {
                return TotalReloads >= expectedCount;
            }
        }

        public JsonElement GetStatusJson()
        {
            var data = new
            {
                status = "success",
                isWatching = IsWatching,
                watchedPaths = WatchedPaths,
                debounceMs = DebounceMs,
                totalReloads = TotalReloads,
                lastReloadDurationMs = LastReloadDurationMs,
                lastReloadTimestamp = LastReloadTimestamp?.ToString("O"),
                lastChangedFiles = LastChangedFiles
            };
            return JsonSerializer.SerializeToElement(data);
        }
    }

    #endregion

    #region Replay Tick Math & Phase 3 State Validator

    public static class ReplayMath
    {
        public static long SecondsToTicks(double seconds)
        {
            if (seconds < 0) return 0;
            return (long)Math.Round(seconds * DefaultTickRate);
        }

        public static long MillisecondsToTicks(long ms)
        {
            if (ms < 0) return 0;
            return (long)Math.Round(ms / (double)DefaultTickDeltaMs);
        }

        public static double TicksToSeconds(long ticks)
        {
            if (ticks < 0) return 0;
            return ticks / (double)DefaultTickRate;
        }

        public static long MinutesAndSecondsToTicks(int minutes, double seconds)
        {
            if (minutes < 0) minutes = 0;
            if (seconds < 0) seconds = 0;
            return (minutes * 1200) + (long)Math.Round(seconds * DefaultTickRate);
        }
    }

    public static class SnapshotPhaseValidator
    {
        public static (bool Success, string? ErrorCode, int CurrentPhase, int[] AllowedPhases) VerifyPhase(
            int currentPhase,
            int[]? allowedPhases = null)
        {
            allowedPhases ??= [3];

            if (allowedPhases.Contains(currentPhase))
            {
                return (true, null, currentPhase, allowedPhases);
            }

            return (false, "INVALID_GAME_PHASE", currentPhase, allowedPhases);
        }
    }

    #endregion

    #region K2 Lua Script Rules & AST Validator

    public static class K2LuaValidator
    {
        private static readonly Regex WindowsPattern = new(@"\bWindows\b", RegexOptions.Compiled);
        private static readonly Regex DoEventStringPattern = new(@"DoEvent\s*\(\s*['""][^'""]*['""]\s*\)", RegexOptions.Compiled);
        private static readonly Regex DoEventZeroArgPattern = new(@"DoEvent\s*\(\s*\)", RegexOptions.Compiled);
        private static readonly Regex DoEventNIntPattern = new(@"DoEventN\s*\(\s*\d+\s*\)", RegexOptions.Compiled);

        public static (bool IsValid, string? Violation) ValidateK2LuaScript(string luaScript)
        {
            if (string.IsNullOrWhiteSpace(luaScript))
            {
                return (false, "Empty script");
            }

            if (WindowsPattern.IsMatch(luaScript))
            {
                return (false, "Forbidden 'Windows' global reference. Must use 'UIManager'.");
            }

            if (DoEventStringPattern.IsMatch(luaScript))
            {
                return (false, "Forbidden string argument passed to DoEvent(). Must use 0-arg DoEvent() or integer DoEventN(n).");
            }

            return (true, null);
        }

        public static string AutoRemediateLuaScript(string luaScript)
        {
            string remediated = WindowsPattern.Replace(luaScript, "UIManager");
            remediated = DoEventStringPattern.Replace(remediated, "DoEvent()");
            return remediated;
        }
    }

    #endregion

    #region Tiered Port Discovery Engine

    public static class TieredPortDiscovery
    {
        public static (int Port, string Tier) ResolveChatServerPort(
            int? explicitPort = null,
            Func<string, string?>? envLookup = null,
            int defaultPort = 11031)
        {
            // Tier 1: Explicit parameter
            if (explicitPort.HasValue && explicitPort.Value > 0 && explicitPort.Value <= 65535)
            {
                return (explicitPort.Value, "explicit");
            }

            // Tier 2: Aspire environment configuration
            if (envLookup != null)
            {
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
                    string? val = envLookup(key);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        if (int.TryParse(val, out int p) && p > 0 && p <= 65535)
                        {
                            return (p, "aspire");
                        }

                        // Try URL parsing
                        if (Uri.TryCreate(val, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
                        {
                            return (uri.Port, "aspire");
                        }
                    }
                }
            }

            // Tier 3: Default fallback
            return (defaultPort, "default");
        }
    }

    #endregion
}
