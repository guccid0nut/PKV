// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Pipelines;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

/// <summary>
///     Provides high-concurrency zero-copy socket buffer management over a <see cref="Socket" /> using
///     <see cref="PipeReader" /> and <see cref="PipeWriter" />.
/// </summary>
public sealed class PipeSocketHandler(
    Socket socket,
    PipeOptions? inputOptions = null,
    PipeOptions? outputOptions = null) : IAsyncDisposable, IDisposable
{
    private static readonly PipeOptions DefaultPipeOptions = new(
        pool: MemoryPool<byte>.Shared,
        readerScheduler: PipeScheduler.ThreadPool,
        writerScheduler: PipeScheduler.ThreadPool,
        pauseWriterThreshold: 65536,
        resumeWriterThreshold: 32768,
        useSynchronizationContext: false);

    private readonly Socket _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    private readonly CancellationTokenSource _cts = new();
    private readonly Pipe _inputPipe = new(inputOptions ?? DefaultPipeOptions);
    private readonly Pipe _outputPipe = new(outputOptions ?? DefaultPipeOptions);
    private Task? _fillInputTask;
    private Task? _flushOutputTask;
    private bool _isDisposed;

    public PipeReader Input => _inputPipe.Reader;
    public PipeWriter Output => _outputPipe.Writer;
    public Socket Socket => _socket;

    public void Start()
    {
        _fillInputTask = FillInputPipeAsync(_socket, _inputPipe.Writer, _cts.Token);
        _flushOutputTask = FlushOutputPipeAsync(_socket, _outputPipe.Reader, _cts.Token);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken = default)
    {
        using CancellationTokenRegistration _ = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _cts.Cancel())
            : default;

        FlushResult result = await _outputPipe.Writer.WriteAsync(memory, _cts.Token).ConfigureAwait(false);
        if (result.IsCompleted || result.IsCanceled)
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        FlushResult result = await _outputPipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted || result.IsCanceled)
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task FillInputPipeAsync(Socket socket, PipeWriter writer, CancellationToken cancellationToken)
    {
        const int minimumBufferSize = 1024;

        while (!cancellationToken.IsCancellationRequested)
        {
            Memory<byte> memory = writer.GetMemory(minimumBufferSize);
            try
            {
                int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                writer.Advance(bytesRead);
            }
            catch (Exception)
            {
                break;
            }

            FlushResult result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCompleted || result.IsCanceled)
            {
                break;
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task FlushOutputPipeAsync(Socket socket, PipeReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.IsEmpty && result.IsCompleted)
            {
                break;
            }

            if (!buffer.IsEmpty)
            {
                bool socketFaulted = false;
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    ReadOnlyMemory<byte> remaining = segment;
                    while (!remaining.IsEmpty)
                    {
                        int sent;
                        try
                        {
                            sent = await socket.SendAsync(remaining, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            socketFaulted = true;
                            break;
                        }

                        if (sent == 0)
                        {
                            socketFaulted = true;
                            break;
                        }

                        remaining = remaining[sent..];
                    }

                    if (socketFaulted)
                    {
                        break;
                    }
                }

                if (socketFaulted)
                {
                    break;
                }
            }

            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted || result.IsCanceled)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation issues during teardown
        }

        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch
        {
            // Ignore socket shutdown failures during teardown
        }

        try
        {
            _socket.Dispose();
        }
        catch
        {
            // Ignore socket disposal failures during teardown
        }

        if (_fillInputTask is not null)
        {
            try
            {
                await _fillInputTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                // Ignored during teardown
            }
        }

        if (_flushOutputTask is not null)
        {
            try
            {
                await _flushOutputTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                // Ignored during teardown
            }
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore cancellation issues during teardown
        }

        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch
        {
            // Ignore socket shutdown failures during teardown
        }

        try
        {
            _socket.Dispose();
        }
        catch
        {
            // Ignore socket disposal failures during teardown
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
