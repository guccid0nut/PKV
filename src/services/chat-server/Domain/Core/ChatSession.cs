// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable LogMessageIsConditional

using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Threading.Channels;
using ASPIRE.Common.Telemetry;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

[SuppressMessage("ReSharper", "LogMessageIsConditional")]
public partial class ChatSession(TCPServer server, IServiceProvider serviceProvider)
    : TCPSession(server, serviceProvider?.GetService<ILogger<ChatSession>>())
{
    // Primary Constructor logic moved to explicit constructor to support base class call

    private readonly IChatContext _chatContext = serviceProvider.GetRequiredService<IChatContext>();

    private readonly IDiscordPresencePublisherService _discordPublisher =
        serviceProvider.GetRequiredService<IDiscordPresencePublisherService>();

    private readonly IQueueArcadeService? _queueArcadeService = serviceProvider.GetService<IQueueArcadeService>();
    private readonly IMatchmakingService
        _matchmakingService = serviceProvider.GetRequiredService<IMatchmakingService>();

    private readonly IDistributedChatStateService?
        _distributedChatState = serviceProvider?.GetService<IDistributedChatStateService>();

    public IDistributedChatStateService? DistributedChatState => _distributedChatState;

    private readonly ITelemetryPublisherService?
        _telemetryPublisher = serviceProvider?.GetService<ITelemetryPublisherService>();

    private readonly TimeProvider _timeProvider =
        serviceProvider?.GetService<TimeProvider>() ?? TimeProvider.System;

    private readonly HeartbeatRollupTracker _heartbeatTracker =
        serviceProvider?.GetService<HeartbeatRollupTracker>() ?? HeartbeatRollupTracker.Shared;

    internal bool? VerboseHeartbeatsOverride { get; set; }

    public const int DefaultBroadcastSoftWatermarkBytes = 512 * 1024;
    internal int? BroadcastSoftWatermarkBytesOverride { get; set; }
    private long _outboundBackpressureDrops;
    public long OutboundBackpressureDrops => Interlocked.Read(ref _outboundBackpressureDrops);

    private static readonly TimeSpan HeartbeatSummaryInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultPreAuthTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(300);

    internal TimeSpan? PreAuthTimeoutOverride { get; set; }
    internal TimeSpan? IdleTimeoutOverride { get; set; }
    internal string? IdleTimeoutModeOverride { get; set; }

    private readonly Pipe _ingressPipe = new(new PipeOptions(
        pauseWriterThreshold: 65536,
        resumeWriterThreshold: 32768,
        useSynchronizationContext: false));

    private Task? _packetProcessingTask;

    /// <summary>
    ///     Gets the current hosting environment information for the web application.
    /// </summary>
    /// <remarks>
    ///     Use this property to access environment-specific settings, such as the application name, the content root path, or
    ///     the environment name.
    /// </remarks>
    // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
    public IWebHostEnvironment HostEnvironment { get; init; } =
        serviceProvider?.GetService<IWebHostEnvironment>()!;

    /// <summary>
    ///     Gets set after a successful handshake (Client or Server).
    ///     Contains the account information of the entity connected to this chat session.
    ///     This property is NULL before authentication, but is guaranteed non-NULL after Accept is called.
    /// </summary>
    public Account Account { get; set; } = null!;

    private volatile bool _isAuthenticated;
    private ChatPeerRole _authenticatedRole;

    /// <summary>
    ///     Indicates whether this session has completed cryptographic/session-cookie handshake authentication.
    /// </summary>
    public bool IsAuthenticated => _isAuthenticated || (object?)Account is not null;

    /// <summary>
    ///     Virtual connection liveness seam allowing deterministic in-memory simulation without socket binds.
    /// </summary>
    public virtual bool IsSessionConnected => IsConnected;

    /// <summary>
    ///     Resolves the strict listener/peer role bound to this session (Client 11031, MatchServer 11032, ServerManager 11033).
    /// </summary>
    public ChatPeerRole PeerRole
    {
        get
        {
            if (Server is ClientChatServer) return ChatPeerRole.Client;
            if (Server is MatchServerChatServer) return ChatPeerRole.MatchServer;
            if (Server is MatchServerManagerChatServer) return ChatPeerRole.ServerManager;
            if (_authenticatedRole != ChatPeerRole.None) return _authenticatedRole;
            if (ServerMetadata is not null) return ChatPeerRole.MatchServer;
            if (ManagerMetadata is not null) return ChatPeerRole.ServerManager;
            if (ClientMetadata is not null || (object?)Account is not null) return ChatPeerRole.Client;
            return ChatPeerRole.None;
        }
    }

    public void MarkAuthenticated(ChatPeerRole role)
    {
        _authenticatedRole = role;
        _isAuthenticated = true;
    }

    /// <summary>
    ///     Token-bucket rate limiter safeguarding this session against client packet flooding.
    /// </summary>
    public SessionTokenBucketRateLimiter RateLimiter { get; } = new(50.0, 100.0);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat Session ID {SessionID} Was Created")]
    private partial void LogSessionCreated(Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chat Session ID {SessionID} Caught A Socket Error With Code {SocketErrorCode}")]
    private partial void LogSocketError(Guid sessionId, SocketError socketErrorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat Session ID {SessionID} Has Terminated")]
    private partial void LogSessionTerminated(Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "Broadcasting Status Update {Status} for Account {AccountID} ({AccountName}) to {ObserverCount} Observers")]
    private partial void LogBroadcastStatusUpdate(ChatProtocol.ChatClientStatus status, int accountID,
        string accountName, int observerCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[RAW] Received {Length} bytes: {BytesHex}")]
    private partial void LogRawReceived(int length, string bytesHex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in OnReceived")]
    private partial void LogReceiveError(Exception ex);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "Missing Type Mapping For Command: \"0x{Command:X4}\"\nMessage UTF8 Bytes: {Bytes}\nMessage UTF8 Text: {Text}")]
    private partial void LogMissingCommandType(ushort command, string bytes, string text);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[IN] Processing Command: \"0x{Command:X4}\" From Session {SessionID}")]
    private partial void LogProcessingCommand(ushort command, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[IN] Processing Command: \"0x{Command:X4}\" From Session {SessionID}")]
    private partial void LogProcessingHeartbeatCommand(ushort command, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] Dispatching Command Type: {TypeName}")]
    private partial void LogDispatchingCommand(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] Synchronous Instance Type: {TypeName}")]
    private partial void LogSyncInstanceType(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] Invoking SYNC Process Method on {TypeName}")]
    private partial void LogInvokeSync(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] Async Instance Type: {TypeName}")]
    private partial void LogAsyncInstanceType(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] Invoking ASYNC Process Method on {TypeName}")]
    private partial void LogInvokeAsync(string typeName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[CRITICAL] Process Method NOT FOUND on interface {InterfaceName}")]
    private partial void LogProcessMethodNotFound(string interfaceName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[CRITICAL] IAsynchronousCommandProcessor Interface NOT FOUND on {TypeName}")]
    private partial void LogAsyncInterfaceNotFound(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DEBUG] Command Processing Completed")]
    private partial void LogProcessingCompleted();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[DEBUG] Packet 0x{Command:X4} Payload ({Size} bytes): {Payload}")]
    private partial void LogPacketPayload(ushort command, int size, string payload);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[HeartbeatSummary] 60s: pings={Pings} gsStatus={GsStatus} smStatus={SmStatus}")]
    private partial void LogHeartbeatSummary(long pings, long gsStatus, long smStatus);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[BUG] Could Not Create Command Type Instance For Command: \"0x{Command:X4}\"")]
    private partial void LogCreateInstanceError(ushort command);

    [LoggerMessage(Level = LogLevel.Error, Message = "[BUG] Error Processing Command: \"0x{Command:X4}\"")]
    private partial void LogProcessingError(Exception ex, ushort command);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Command Lookup Failed for 0x{Command:X4}. Available Commands:\n{AvailableCommands}")]
    private partial void LogCommandLookupFailed(ushort command, string availableCommands);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[Security] Session {SessionId} declared frame length {FrameLength} exceeding max limit {MaxLimit}; disconnecting")]
    private partial void LogFramingViolation(Guid sessionId, int frameLength, int maxLimit);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[BUG] Could Not Add Command-To-Type Mapping For Command \"0x{Command:X4}\" And Type \"{TypeName}\"")]
    private partial void LogMappingError(ushort command, string typeName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[BUG] Command Type \"{TypeName}\" Does Not Implement A Supported Processor Interface")]
    private partial void LogInterfaceError(string typeName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[OUT] Sending Command: \"0x{Command:X4}\" ({Size} bytes) To Session {SessionID}")]
    private partial void LogSendingCommand(ushort command, int size, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[OUT] Sending Command: \"0x{Command:X4}\" ({Size} bytes) To Session {SessionID}")]
    private partial void LogSendingHeartbeatCommand(ushort command, int size, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Packet Of {PacketSize} Bytes Exceeds Maximum Allowed Size Of {MaximumPacketSize} Bytes")]
    private partial void LogPacketSizeError(int packetSize, int maximumPacketSize);

    protected override bool EnableCustomSocketIngress => true;

    internal static bool IsDroppableBroadcastPacket(ushort command)
    {
        return IsRoutineHeartbeatPacket(command) ||
               command is
                   ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG or
                   ChatProtocol.Command.CHAT_CMD_JOINED_CHANNEL or
                   ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL or
                   ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS or
                   ChatProtocol.Command.CHAT_CMD_DYNAMIC_PRODUCT_LIST;
    }

    internal static bool IsRoutineHeartbeatPacket(ushort command)
    {
        return command is
            ChatProtocol.Bidirectional.NET_CHAT_PING or
            ChatProtocol.Bidirectional.NET_CHAT_PONG or
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_STATUS or
            ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_STATUS;
    }

    internal static bool IsCredentialBearingCommand(ushort command)
    {
        return command is
            ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT or
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_CONNECT or
            ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_CONNECT or
            ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND or
            ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_REMOTE_COMMAND or
            ChatProtocol.Command.CHAT_CMD_CHANNEL_SET_PASSWORD or
            ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL_PASSWORD;
    }

    internal static string FormatPacketPayloadForLog(ushort command, ReadOnlySpan<byte> payload)
    {
        return IsCredentialBearingCommand(command)
            ? "[REDACTED-AUTH-PAYLOAD]"
            : Convert.ToHexString(payload);
    }

    internal static string FormatRawReceivedForLog(ReadOnlySpan<byte> incoming)
    {
        if (incoming.Length >= 4)
        {
            ushort firstOpcode = (ushort)(incoming[2] | (incoming[3] << 8));
            if (IsCredentialBearingCommand(firstOpcode))
            {
                return "[REDACTED-AUTH-PAYLOAD]";
            }
        }

        return Convert.ToHexString(incoming);
    }

    internal static bool IsVerboseHeartbeatsEnabled(string? raw = null)
    {
        raw ??= Environment.GetEnvironmentVariable("KONGOR_VERBOSE_HEARTBEATS");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldLogVerboseHeartbeats() => VerboseHeartbeatsOverride ?? IsVerboseHeartbeatsEnabled();

    private void RecordHeartbeatAndMaybeEmitSummary(ushort command)
    {
        if (_heartbeatTracker.TryRecordAndCheckSummary(
                command,
                _timeProvider,
                HeartbeatSummaryInterval,
                out long pings,
                out long gsStatus,
                out long smStatus) &&
            Logger.IsEnabled(LogLevel.Information))
        {
            LogHeartbeatSummary(pings, gsStatus, smStatus);
        }
    }

    internal sealed class HeartbeatRollupTracker
    {
        public static readonly HeartbeatRollupTracker Shared = new();

        private long _pingCount;
        private long _gsStatusCount;
        private long _smStatusCount;
        private long _lastSummaryTimestamp;

        public bool TryRecordAndCheckSummary(
            ushort command,
            TimeProvider timeProvider,
            TimeSpan interval,
            out long pings,
            out long gsStatus,
            out long smStatus)
        {
            pings = 0;
            gsStatus = 0;
            smStatus = 0;

            switch (command)
            {
                case ChatProtocol.Bidirectional.NET_CHAT_PING:
                    Interlocked.Increment(ref _pingCount);
                    break;
                case ChatProtocol.GameServerToChatServer.NET_CHAT_GS_STATUS:
                    Interlocked.Increment(ref _gsStatusCount);
                    break;
                case ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_STATUS:
                    Interlocked.Increment(ref _smStatusCount);
                    break;
                default:
                    return false;
            }

            long now = timeProvider.GetTimestamp();
            long last = Interlocked.Read(ref _lastSummaryTimestamp);
            if (last == 0)
            {
                Interlocked.CompareExchange(ref _lastSummaryTimestamp, now, 0);
                return false;
            }

            if (timeProvider.GetElapsedTime(last, now) < interval)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastSummaryTimestamp, now, last) != last)
            {
                return false;
            }

            pings = Interlocked.Exchange(ref _pingCount, 0);
            gsStatus = Interlocked.Exchange(ref _gsStatusCount, 0);
            smStatus = Interlocked.Exchange(ref _smStatusCount, 0);
            return pings + gsStatus + smStatus > 0;
        }
    }

    protected override void OnConnected()
    {
        base.OnConnected();

        if (Socket is not null)
        {
            AttachPipeHandler(new PipeSocketHandler(Socket));
        }

        if (Logger.IsEnabled(LogLevel.Information)) LogSessionCreated(ID);
    }

    protected override void OnError(SocketError socketError)
    {
        if (Logger.IsEnabled(LogLevel.Information)) LogSocketError(ID, socketError);
    }

    protected override void OnDisconnected()
    {
        if (_pipeHandler is not null)
        {
            _pipeHandler.Dispose();
            _pipeHandler = null;
        }

        try
        {
            _ingressPipe.Writer.Complete();
        }
        catch
        {
            // Ignore if already completed
        }

        Cleanup();

        if (Logger.IsEnabled(LogLevel.Information)) LogSessionTerminated(ID);
    }

    private PipeSocketHandler? _pipeHandler;

    /// <summary>
    ///     Attaches a <see cref="PipeSocketHandler" /> for direct, zero-copy pipeline streaming over the active socket.
    /// </summary>
    public void AttachPipeHandler(PipeSocketHandler handler)
    {
        _pipeHandler = handler;
        handler.Start();
        _packetProcessingTask = ProcessPipeAsync(handler.Input, SessionCts.Token);
    }

    /// <summary>
    ///     Processes incoming socket data asynchronously directly from a <see cref="PipeReader" /> with zero-copy packet
    ///     framing and amortized service scope reuse across batch frames.
    /// </summary>
    public async Task ProcessPipeAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        IConfiguration? cfg = serviceProvider.GetService<IConfiguration>();
        TimeSpan preAuthTimeout = PreAuthTimeoutOverride
                                  ?? (int.TryParse(cfg?["Chat:PreAuthTimeoutSeconds"], out int paSec) && paSec > 0
                                      ? TimeSpan.FromSeconds(paSec)
                                      : DefaultPreAuthTimeout);
        TimeSpan idleTimeout = IdleTimeoutOverride
                               ?? (int.TryParse(cfg?["Chat:IdleTimeoutSeconds"], out int idleSec) && idleSec > 0
                                   ? TimeSpan.FromSeconds(idleSec)
                                   : DefaultIdleTimeout);
        string idleMode = IdleTimeoutModeOverride ?? cfg?["Chat:IdleTimeoutMode"] ?? "Shadow";
        long connectTimestamp = _timeProvider.GetTimestamp();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result;
                if (!IsAuthenticated)
                {
                    TimeSpan elapsedSinceConnect = _timeProvider.GetElapsedTime(connectTimestamp);
                    TimeSpan remainingPreAuth = preAuthTimeout - elapsedSinceConnect;
                    if (remainingPreAuth <= TimeSpan.Zero)
                    {
                        Logger.LogWarning(
                            "[SECURITY-ALERT] [ChatSession] Disconnecting unauthenticated slowloris session {SessionId} after {ElapsedMs:F0}ms without handshake.",
                            ID, elapsedSinceConnect.TotalMilliseconds);
                        Disconnect();
                        break;
                    }

                    using CancellationTokenSource preAuthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    preAuthCts.CancelAfter(remainingPreAuth);
                    try
                    {
                        result = await reader.ReadAsync(preAuthCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Logger.LogWarning(
                            "[SECURITY-ALERT] [ChatSession] Disconnecting unauthenticated slowloris session {SessionId} after pre-auth deadline ({TimeoutMs:F0}ms).",
                            ID, preAuthTimeout.TotalMilliseconds);
                        Disconnect();
                        break;
                    }
                }
                else if (!string.Equals(idleMode, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    using CancellationTokenSource idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idleCts.CancelAfter(idleTimeout);
                    try
                    {
                        result = await reader.ReadAsync(idleCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Logger.LogWarning(
                            "[SECURITY-ALERT] [ChatSession] Authenticated session {SessionId} (Account='{AccountName}') exceeded idle timeout ({IdleSeconds:F0}s, Mode={IdleMode}).",
                            ID, Account?.Name ?? "unknown", idleTimeout.TotalSeconds, idleMode);
                        if (string.Equals(idleMode, "Enforce", StringComparison.OrdinalIgnoreCase))
                        {
                            Disconnect();
                            break;
                        }

                        continue;
                    }
                }
                else
                {
                    result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }

                ReadOnlySequence<byte> buffer = result.Buffer;

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                try
                {
                    if (TryReadPacket(ref buffer, out ReadOnlySequence<byte> firstPacket))
                    {
                        using IServiceScope batchScope = serviceProvider.CreateScope();
                        ReadOnlySequence<byte> packetPayload = firstPacket;
                        do
                        {
                            if (!RateLimiter.TryConsume(1))
                            {
                                if (Logger.IsEnabled(LogLevel.Warning))
                                {
                                    Logger.LogWarning("[ChatSession] Rate limit exceeded for session {SessionId}. Consecutive violations: {Violations}",
                                        ID, RateLimiter.ConsecutiveViolations);
                                }

                                if (RateLimiter.ConsecutiveViolations > 50)
                                {
                                    if (Logger.IsEnabled(LogLevel.Error))
                                    {
                                        Logger.LogError("[ChatSession] Disconnecting session {SessionId} due to persistent packet flood (>50 violations)", ID);
                                    }
                                    Disconnect();
                                    break;
                                }

                                continue;
                            }

                            _telemetryPublisher?.RecordChatCommandAsync();
                            await DispatchPacketPayloadAsync(batchScope.ServiceProvider, packetPayload).ConfigureAwait(false);
                        } while (TryReadPacket(ref buffer, out packetPayload));
                    }

                    consumed = buffer.Start;
                    examined = buffer.End;
                }
                finally
                {
                    reader.AdvanceTo(consumed, examined);
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    Disconnect();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect / cancellation
        }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Error)) LogProcessingError(ex, 0);
        }
        finally
        {
            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore completion failures during shutdown
            }

            if (IsConnected)
            {
                Disconnect();
            }
        }
    }

    private async Task DispatchPacketPayloadAsync(IServiceProvider sp, ReadOnlySequence<byte> packetPayload)
    {
        int length = (int)packetPayload.Length;
        if (length < 2) return;

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            packetPayload.CopyTo(rented);
            await ProcessDataSegmentAsync(sp, rented, length).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task DispatchPacketPayloadAsync(ReadOnlySequence<byte> packetPayload)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        await DispatchPacketPayloadAsync(scope.ServiceProvider, packetPayload).ConfigureAwait(false);
    }

    /// <summary>
    ///     Maximum packet frame payload length (8 KB) permitted by the chat gateway protocol firewall.
    /// </summary>
    public const int MaxFrameLength = 8192;

    internal static bool TryReadPacketFrame(
        ref ReadOnlySequence<byte> buffer,
        out ReadOnlySequence<byte> packetPayload,
        out bool framingViolation,
        out ushort declaredLength)
    {
        framingViolation = false;
        declaredLength = 0;

        if (buffer.Length < 2)
        {
            packetPayload = default;
            return false;
        }

        ushort payloadLength;
        if (buffer.FirstSpan.Length >= 2)
        {
            payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.FirstSpan);
        }
        else
        {
            Span<byte> header = stackalloc byte[2];
            buffer.Slice(0, 2).CopyTo(header);
            payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(header);
        }

        declaredLength = payloadLength;

        if (payloadLength > MaxFrameLength)
        {
            framingViolation = true;
            packetPayload = default;
            return false;
        }

        if (buffer.Length < 2 + payloadLength)
        {
            packetPayload = default;
            return false;
        }

        packetPayload = buffer.Slice(2, payloadLength);
        buffer = buffer.Slice(2 + payloadLength);
        return true;
    }

    private bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packetPayload)
    {
        if (!TryReadPacketFrame(ref buffer, out packetPayload, out bool framingViolation, out ushort declaredLength))
        {
            if (framingViolation)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                {
                    LogFramingViolation(ID, declaredLength, MaxFrameLength);
                }
                Disconnect();
            }
            return false;
        }

        return true;
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            ReadOnlySpan<byte> incoming = buffer.AsSpan((int)offset, (int)size);
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                string hex = FormatRawReceivedForLog(incoming);
                LogRawReceived(incoming.Length, hex);
            }

            EnsurePacketProcessingLoopRunning();

            Span<byte> span = _ingressPipe.Writer.GetSpan(incoming.Length);
            incoming.CopyTo(span);
            _ingressPipe.Writer.Advance(incoming.Length);

            ValueTask<FlushResult> flushTask = _ingressPipe.Writer.FlushAsync();
            if (!flushTask.IsCompletedSuccessfully)
            {
                _ = CompleteFlushAsync(flushTask);
            }
        }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Error)) LogReceiveError(ex);
        }
    }

    private async Task CompleteFlushAsync(ValueTask<FlushResult> flushTask)
    {
        try
        {
            await flushTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Error)) LogReceiveError(ex);
        }
    }

    private void EnsurePacketProcessingLoopRunning()
    {
        if (_packetProcessingTask is null)
            lock (_ingressPipe)
            {
                _packetProcessingTask ??= ProcessPipeAsync(_ingressPipe.Reader, SessionCts.Token);
            }
    }

    internal async Task ProcessDataSegmentAsync(IServiceProvider sp, byte[] segmentArray, int segmentLength)
    {
        ReadOnlySpan<byte> segment = segmentArray.AsSpan(0, segmentLength);
        if (segment.Length < 2) return;

        ushort command = (ushort)(segment[0] | (segment[1] << 8));
        if (!ChatCommandRegistry.TryGetDescriptor(command, out ChatCommandDescriptor? descriptor))
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                string hexSegment = string.Join(':', [.. segment]);
                string textSegment = Encoding.UTF8.GetString(segment);
                LogMissingCommandType(command, hexSegment, textSegment);
            }

            return;
        }

        using Activity? activity = NexusTracing.StartCommandActivity(descriptor.Name, command, Account?.ID ?? 0);
        NexusMetrics.CommandsProcessedTotal.Add(1);

        bool isHeartbeat = IsRoutineHeartbeatPacket(command);
        bool verboseHeartbeats = isHeartbeat && ShouldLogVerboseHeartbeats();

        if (isHeartbeat)
        {
            RecordHeartbeatAndMaybeEmitSummary(command);
        }

        if (isHeartbeat && !verboseHeartbeats)
        {
            if (Logger.IsEnabled(LogLevel.Debug)) LogProcessingHeartbeatCommand(command, ID);
        }
        else
        {
            if (Logger.IsEnabled(LogLevel.Information)) LogProcessingCommand(command, ID);
        }

        try
        {
            ChatBuffer buffer = new(segmentArray, segmentLength);
            if (Logger.IsEnabled(LogLevel.Debug)) LogDispatchingCommand(descriptor.Name);

            await ChatPacketDispatcher.DispatchAsync(sp, this, command, buffer)
                .ConfigureAwait(false);

            if (Logger.IsEnabled(LogLevel.Debug)) LogProcessingCompleted();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                string payloadText = FormatPacketPayloadForLog(command, buffer.Data.AsSpan(0, (int)buffer.Size));
                LogPacketPayload(
                    command,
                    (int)buffer.Size,
                    payloadText);
            }
        }
        catch (Exception exception)
        {
            if (Logger.IsEnabled(LogLevel.Error)) LogProcessingError(exception, command);
        }
    }

    private async Task ProcessDataSegmentAsync(byte[] segmentArray, int segmentLength)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        await ProcessDataSegmentAsync(scope.ServiceProvider, segmentArray, segmentLength).ConfigureAwait(false);
    }

    private async Task ProcessDataSegmentAsync(ReadOnlyMemory<byte> segmentMemory)
    {
        int length = segmentMemory.Length;
        if (length < 2) return;

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            segmentMemory.Span.CopyTo(rented);
            await ProcessDataSegmentAsync(rented, length).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Send buffer data with non-destructive size prefixing, which does not mutate the original buffer.
    ///     Enforces 16KB (16384 bytes) maximum packet size, as to not exceed the chat protocol's limitations.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public bool SendAsync(ChatBuffer buffer)
    {
        return Send(buffer);
    }

    /// <summary>
    ///     Send buffer data with non-destructive size prefixing, which does not mutate the original buffer.
    ///     Enforces 16KB (16384 bytes) maximum packet size, as to not exceed the chat protocol's limitations.
    /// </summary>
    public bool Send(ChatBuffer buffer)
    {
        if (buffer.Size > ChatProtocol.MAX_PACKET_SIZE)
        {
            if (Logger.IsEnabled(LogLevel.Error))
                LogPacketSizeError((int)buffer.Size, (int)ChatProtocol.MAX_PACKET_SIZE);

            return false;
        }

        short messageLength = Convert.ToInt16(buffer.Size);
        int totalPacketSize = 2 + messageLength;

        ushort outCommand = messageLength >= 2
            ? (ushort)(buffer.Data[0] | (buffer.Data[1] << 8))
            : (ushort)0;

        // Soft outbound backpressure guard (H2-3): shed non-critical broadcast/chat/heartbeat frames
        // when queued user-space outbound bytes (BytesPending + BytesSending) exceed the soft watermark
        // so control frames retain send-buffer headroom up to OptionSendBufferLimit.
        int softWatermark = BroadcastSoftWatermarkBytesOverride ?? DefaultBroadcastSoftWatermarkBytes;
        long queuedOutboundBytes = BytesPending + BytesSending;
        if (softWatermark > 0 &&
            messageLength >= 2 &&
            IsDroppableBroadcastPacket(outCommand) &&
            queuedOutboundBytes + totalPacketSize > softWatermark)
        {
            long dropped = Interlocked.Increment(ref _outboundBackpressureDrops);
            if (dropped == 1 || dropped % 100 == 0)
            {
                Logger.LogWarning(
                    "[Backpressure] Shedding outbound broadcast packet 0x{Command:X4} ({Size}B) on session {SessionID}: QueuedOutbound={Queued}B (Pending={Pending}B, Sending={Sending}B) > softWatermark={Watermark}B (totalDropped={Dropped})",
                    outCommand,
                    totalPacketSize,
                    ID,
                    queuedOutboundBytes,
                    BytesPending,
                    BytesSending,
                    softWatermark,
                    dropped);
            }
            return false;
        }

        if (messageLength >= 2)
        {
            bool isHeartbeat = IsRoutineHeartbeatPacket(outCommand);
            if (isHeartbeat && !ShouldLogVerboseHeartbeats())
            {
                if (Logger.IsEnabled(LogLevel.Debug)) LogSendingHeartbeatCommand(outCommand, messageLength, ID);
            }
            else
            {
                if (Logger.IsEnabled(LogLevel.Information)) LogSendingCommand(outCommand, messageLength, ID);
            }
        }

        if (totalPacketSize <= 512)
        {
            Span<byte> packetSpan = stackalloc byte[totalPacketSize];
            BinaryPrimitives.WriteInt16LittleEndian(packetSpan, messageLength);
            buffer.Data.AsSpan(0, messageLength).CopyTo(packetSpan[2..]);

            return SendAsync(packetSpan);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalPacketSize);
        try
        {
            Span<byte> packetSpan = rented.AsSpan(0, totalPacketSize);
            BinaryPrimitives.WriteInt16LittleEndian(packetSpan, messageLength);
            buffer.Data.AsSpan(0, messageLength).CopyTo(packetSpan[2..]);

            return SendAsync(packetSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
