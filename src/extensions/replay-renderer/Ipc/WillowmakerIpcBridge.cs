// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WillowmakerIpcBridge.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Ipc;

namespace ReplayRenderer.Service.Ipc;

/// <summary>
///     Lightweight Win32 Named Pipe IPC server emulating the WILLOWMAKER launcher IPC protocol
///     on \\.\pipe\willowmaker-ipc so hon_x64.exe boots cleanly without waiting or exiting.
/// </summary>
public sealed class WillowmakerIpcBridge(
    ILogger<WillowmakerIpcBridge> logger,
    string replayFileName,
    string? pipeName = null,
    int startOffsetSeconds = 0) : IAsyncDisposable
{
    public const ushort WillowmakerMagic = WillowmakerIpcMessage.HeaderMagic; // 0x574D ('WM')
    public const ushort OpCodePing = (ushort)IpcOpCode.Ping;
    public const ushort OpCodePong = (ushort)IpcOpCode.Pong;
    public const ushort OpCodeExecuteCommand = (ushort)IpcOpCode.ExecuteCommand;
    private readonly ILogger<WillowmakerIpcBridge> _logger = logger;
    private readonly string _pipeName = string.IsNullOrWhiteSpace(pipeName) || string.Equals(pipeName, "willowmaker-ipc", StringComparison.OrdinalIgnoreCase)
        ? $"hon_willowmaker_pipe_{Guid.NewGuid():N}"
        : pipeName;
    private readonly string _replayFileName = replayFileName;
    private readonly int _startOffsetSeconds = startOffsetSeconds;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private NamedPipeServerStream? _pipeServer;
    private Task? _serverTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _logger.LogInformation("[WillowmakerIpc] Disposing WILLOWMAKER IPC bridge server...");

        if (_cts != null)
        {
            _cts.Cancel();
            if (_serverTask != null)
                try
                {
                    await _serverTask;
                }
                catch
                {
                    // Suppress cancellation exception
                }

            _cts.Dispose();
            _cts = null;
        }

        if (_pipeServer != null)
        {
            await _pipeServer.DisposeAsync();
            _pipeServer = null;
        }
    }

    /// <summary>
    ///     Creates a binary packet conforming to the WILLOWMAKER IPC protocol:
    ///     [Magic (2 bytes)][OpCode (2 bytes)][PayloadLength (4 bytes)][Payload UTF-8].
    /// </summary>
    public static byte[] CreatePacket(ushort opCode, string payload)
    {
        WillowmakerIpcMessage msg = WillowmakerIpcMessage.CreateString((IpcOpCode)opCode, payload);
        return WillowmakerIpcMessage.Serialize(msg);
    }

    /// <summary>
    ///     Creates an ExecuteCommand (OpCode 3) packet.
    /// </summary>
    public static byte[] CreateCommandPacket(string command)
    {
        WillowmakerIpcMessage msg = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, command);
        return WillowmakerIpcMessage.Serialize(msg);
    }

    /// <summary>
    ///     Sends a raw console command to the engine over the IPC pipe.
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_pipeServer == null || !_pipeServer.IsConnected)
        {
            _logger.LogWarning("[WillowmakerIpc] Cannot send command '{Cmd}' — pipe is not connected.", command);
            return;
        }

        byte[] frame = CreateCommandPacket(command);
        await _pipeServer.WriteAsync(frame, cancellationToken);
        await _pipeServer.FlushAsync(cancellationToken);
        _logger.LogInformation("[WillowmakerIpc] Sent command to engine: '{Cmd}'", command);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[WillowmakerIpc] Starting WILLOWMAKER IPC emulation pipe server at \\\\.\\pipe\\{PipeName}...", _pipeName);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                PipeSecurity pipeSecurity = new();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));

                _pipeServer = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    65536,
                    65536,
                    pipeSecurity);
            }
            else
            {
                _pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    65536,
                    65536);
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[WillowmakerIpc] Optional WILLOWMAKER IPC bridge pipe \\\\.\\pipe\\{PipeName} could not be created: {Message}. Continuing without bridge.",
                _pipeName, ex.Message);
        }

        return Task.CompletedTask;
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[WillowmakerIpc] Awaiting hon_x64 engine IPC connection...");
            await _pipeServer!.WaitForConnectionAsync(cancellationToken);
            _logger.LogInformation("[WillowmakerIpc] hon_x64 engine connected to WILLOWMAKER IPC pipe successfully!");



            byte[] buffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested && _pipeServer.IsConnected)
            {
                int bytesRead = await _pipeServer.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0) break;

                if (WillowmakerIpcMessage.TryDeserialize(buffer.AsSpan(0, bytesRead), out WillowmakerIpcMessage? msg,
                        out int _) && msg != null)
                {
                    _logger.LogInformation(
                        "[WillowmakerIpc] Received IPC OpCode: {OpCode} (Magic: 0x{Magic:X4}, Bytes: {Bytes}) Payload: '{Payload}'",
                        (ushort)msg.OpCode, WillowmakerIpcMessage.HeaderMagic, bytesRead, msg.PayloadString);

                    // Respond to Ping (1) with Pong (2)
                    if (msg.OpCode == IpcOpCode.Ping)
                    {
                        byte[] pongFrame = CreatePacket(OpCodePong, "PONG");
                        await _pipeServer.WriteAsync(pongFrame, cancellationToken);
                        await _pipeServer.FlushAsync(cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[WillowmakerIpc] IPC server loop canceled cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WillowmakerIpc] Exception in WILLOWMAKER IPC loop: {Message}", ex.Message);
        }
    }
}
