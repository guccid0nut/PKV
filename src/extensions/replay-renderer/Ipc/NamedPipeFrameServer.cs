// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NamedPipeFrameServer.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.Ipc;

/// <summary>
///     High-throughput Named Pipe frame server.
///     Ingests uncompressed BGRA8 video frames directly from the Vulkan capture layer
///     over a dedicated 64MB Win32 Named Pipe buffer without disk I/O.
/// </summary>
public sealed class NamedPipeFrameServer(
    ILogger<NamedPipeFrameServer> logger,
    string pipeName,
    uint width,
    uint height) : IAsyncDisposable
{
    private readonly ILogger<NamedPipeFrameServer> _logger = logger;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private NamedPipeServerStream? _pipeServer;
    private Task? _readTask;
    private long _totalBytesReceived;
    private long _totalFramesReceived;

    public string PipeName { get; } = pipeName;

    public string PipePath => $@"\\.\pipe\{PipeName}";
    public uint Width { get; } = width;

    public uint Height { get; } = height;

    public uint FrameSizeBytes { get; } = width * height * 4;

    public long TotalFramesReceived => Interlocked.Read(ref _totalFramesReceived);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    public bool IsConnected { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _logger.LogInformation("[NamedPipeServer] Disposing Named Pipe Server at {PipePath}...", PipePath);

        if (_cts != null)
        {
            _cts.Cancel();
            if (_readTask != null)
                try
                {
                    await _readTask;
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
    ///     Starts the asynchronous Named Pipe server and begins accepting frame streaming connections.
    /// </summary>
    public Task StartAsync(Func<ReadOnlyMemory<byte>, ValueTask>? onFrameReceived, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[NamedPipeServer] Initializing owner-restricted Named Pipe server at {PipePath} (Expected Frame Size: {Size:N0} bytes, {W}x{H} BGRA8)...",
            PipePath, FrameSizeBytes, Width, Height);

        if (OperatingSystem.IsWindows())
        {
            PipeSecurity pipeSecurity = CreateOwnerOnlyPipeSecurity();

            _pipeServer = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                64 * 1024 * 1024,
                64 * 1024 * 1024,
                pipeSecurity);
        }
        else
        {
            _pipeServer = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                64 * 1024 * 1024,
                64 * 1024 * 1024);

            TryRestrictUnixPipeSocketPermissions(PipeName, _logger);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (onFrameReceived != null)
            _readTask = Task.Run(() => ReadFrameLoopAsync(onFrameReceived, _cts.Token), CancellationToken.None);
        else
            _readTask = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("[NamedPipeServer] Awaiting connection on {PipePath}...", PipePath);
                    await _pipeServer!.WaitForConnectionAsync(_cts.Token);
                    IsConnected = true;
                    _logger.LogInformation("[NamedPipeServer] Client connected successfully to {PipePath}.", PipePath);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[NamedPipeServer] Pipe connection wait canceled.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[NamedPipeServer] Error during pipe connection: {Message}", ex.Message);
                }
            }, CancellationToken.None);

        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static PipeSecurity CreateOwnerOnlyPipeSecurity()
    {
        PipeSecurity pipeSecurity = new();
        using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? ownerSid = currentIdentity.User;
        if (ownerSid != null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                ownerSid,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return pipeSecurity;
    }

    [UnsupportedOSPlatform("windows")]
    private static void TryRestrictUnixPipeSocketPermissions(string pipeName, ILogger logger)
    {
        try
        {
            string tempDir = Path.GetTempPath();
            string[] candidates =
            [
                Path.Combine(tempDir, $"CoreFxPipe_{pipeName}"),
                pipeName
            ];

            foreach (string socketPath in candidates)
            {
                if (File.Exists(socketPath))
                {
                    File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    logger.LogDebug("[NamedPipeServer] Enforced 0600 permissions on Unix pipe socket: {SocketPath}", socketPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[NamedPipeServer] Could not enforce 0600 Unix socket mode for pipe '{PipeName}'.", pipeName);
        }
    }

    /// <summary>
    ///     Writes a single uncompressed BGRA8 video frame to the connected pipe client (FFmpeg).
    /// </summary>
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frameData, CancellationToken cancellationToken)
    {
        if (_pipeServer == null || !_pipeServer.IsConnected) return;

        await _pipeServer.WriteAsync(frameData, cancellationToken);

        long frameIndex = Interlocked.Increment(ref _totalFramesReceived);
        Interlocked.Add(ref _totalBytesReceived, frameData.Length);

        if (frameIndex % 300 == 0 || frameIndex == 1)
            _logger.LogInformation(
                "[NamedPipeServer] Frame #{Frame}: Streamed {Size:N0} bytes to FFmpeg encoder ({TotalMB:F2} MB total).",
                frameIndex, frameData.Length, Interlocked.Read(ref _totalBytesReceived) / (1024.0 * 1024.0));
    }

    /// <summary>
    ///     Flushes the pipe and blocks until FFmpeg has read all buffered frames, up to the specified timeout.
    /// </summary>
    public async Task WaitForDrainAsync(TimeSpan timeout)
    {
        if (_pipeServer != null && _pipeServer.IsConnected)
        {
            try
            {
                _logger.LogInformation("[NamedPipeServer] Awaiting pipe drain by FFmpeg encoder...");
                if (OperatingSystem.IsWindows())
                {
                    using CancellationTokenSource cts = new(timeout);
                    await Task.Run(() =>
                    {
                        if (OperatingSystem.IsWindows()) _pipeServer.WaitForPipeDrain();
                    }).WaitAsync(cts.Token);
                }
                _logger.LogInformation("[NamedPipeServer] Pipe drained successfully by FFmpeg.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[NamedPipeServer] Pipe drain timed out after {Timeout}s.", timeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[NamedPipeServer] Pipe drain ended: {Message}", ex.Message);
            }
        }
    }

    private async Task ReadFrameLoopAsync(Func<ReadOnlyMemory<byte>, ValueTask> onFrameReceived,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "[NamedPipeServer] Awaiting connection from Vulkan Interception Layer on {PipePath}...", PipePath);
            await _pipeServer!.WaitForConnectionAsync(cancellationToken);
            IsConnected = true;
            _logger.LogInformation("[NamedPipeServer] Vulkan Interception Layer connected successfully to {PipePath}.",
                PipePath);

            byte[] frameBuffer = new byte[FrameSizeBytes];
            int currentFrameBytesRead = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRemaining = (int)FrameSizeBytes - currentFrameBytesRead;
                int read = await _pipeServer.ReadAsync(frameBuffer.AsMemory(currentFrameBytesRead, bytesRemaining),
                    cancellationToken);

                if (read == 0)
                {
                    _logger.LogInformation("[NamedPipeServer] Pipe client disconnected (EOF). Ending read loop.");
                    break;
                }

                currentFrameBytesRead += read;
                Interlocked.Add(ref _totalBytesReceived, read);

                if (currentFrameBytesRead == (int)FrameSizeBytes)
                {
                    long frameIndex = Interlocked.Increment(ref _totalFramesReceived);
                    if (frameIndex % 300 == 0 || frameIndex == 1)
                        _logger.LogInformation(
                            "[NamedPipeServer] Frame #{Frame}: Received {Size:N0} bytes ({TotalMB:F2} MB total transferred).",
                            frameIndex, FrameSizeBytes, Interlocked.Read(ref _totalBytesReceived) / (1024.0 * 1024.0));

                    await onFrameReceived(frameBuffer.AsMemory(0, (int)FrameSizeBytes));
                    currentFrameBytesRead = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[NamedPipeServer] Frame read loop canceled cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NamedPipeServer] Exception in frame read loop: {Message}", ex.Message);
        }
        finally
        {
            IsConnected = false;
        }
    }
}
