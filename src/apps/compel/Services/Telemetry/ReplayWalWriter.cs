// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayWalWriter.cs
// System: COMPEL Telemetry - Strategic Recommendation SR-04
// Subsystem: Background Replay Write-Ahead Logging (WAL) Chunked Writer
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Telemetry;

/// <summary>
/// High-throughput, chunked binary Write-Ahead Log (WAL) writer for in-flight dedicated server match replays.
/// Uses a dedicated single-consumer Channel loop (WAL cord pattern) to guarantee strict chronological FIFO ordering,
/// zero per-line heap allocations, IEEE 802.3 CRC32 checksumming, and durable match-end disk flushing.
/// </summary>
public sealed class ReplayWalWriter : IAsyncDisposable
{
    private enum WalOpKind : byte
    {
        AppendLine,
        AppendBytes,
        Flush,
        Complete
    }

    private readonly struct WalWorkItem
    {
        public WalOpKind Kind { get; init; }
        public string? Line { get; init; }
        public ReadOnlyMemory<byte> Bytes { get; init; }
        public TaskCompletionSource<bool>? Completion { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    private static readonly uint[] CrcTable = InitializeCrcTable();

    private readonly long _matchId;
    private readonly int _port;
    private readonly string _walFilePath;
    private readonly int _chunkSize;
    private readonly ILogger? _logger;
    private readonly Func<Stream>? _streamFactory;
    private readonly Channel<WalWorkItem> _channel;
    private readonly Task _consumerTask;
    private readonly byte[] _buffer;
    private readonly byte[] _headerBuffer = new byte[ReplayWalChunkHeader.HeaderSizeBytes];
    private readonly Encoder _utf8Encoder = Encoding.UTF8.GetEncoder();
    private int _bufferOffset;
    private uint _sequence;
    private long _totalFlushedBytes;
    private uint _lastCrc32;
    private Stream? _stream;
    private volatile bool _isAcceptingWrites = true;
    private volatile bool _isCompleted;
    private volatile bool _isDisposed;
    private ReplayWalBeacon _currentBeacon;

    public long MatchId => _matchId;
    public int Port => _port;
    public string WalFilePath => _walFilePath;
    public ReplayWalBeacon CurrentBeacon => _currentBeacon;

    public ReplayWalWriter(
        long matchId,
        int port,
        string walDirectory,
        int chunkSize = 64 * 1024,
        ILogger? logger = null,
        Func<Stream>? streamFactory = null)
    {
        _matchId = matchId;
        _port = port;
        _chunkSize = Math.Max(64, chunkSize);
        _logger = logger;
        _streamFactory = streamFactory;
        _buffer = new byte[_chunkSize];

        Directory.CreateDirectory(walDirectory);
        _walFilePath = Path.Combine(walDirectory, $"match_{matchId}_port{port}.wal");

        _currentBeacon = new ReplayWalBeacon(
            MatchId: matchId,
            Port: port,
            Sequence: 0,
            FlushedBytes: 0,
            LastCrc32: 0,
            Status: "INITIALIZING");

        _channel = Channel.CreateUnbounded<WalWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _consumerTask = Task.Run(ProcessChannelLoopAsync);
    }

    private void EnsureStreamOpen()
    {
        if (_stream == null)
        {
            _stream = _streamFactory != null
                ? _streamFactory()
                : new FileStream(
                    _walFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            _currentBeacon = _currentBeacon with { Status = "RECORDING" };
        }
    }

    /// <summary>
    /// Synchronously enqueues a line into the single-reader WAL channel in strict FIFO order with zero byte[] allocation.
    /// </summary>
    public void TryEnqueueLine(string line)
    {
        if (!_isAcceptingWrites || _isCompleted || _isDisposed || string.IsNullOrWhiteSpace(line)) return;

        _channel.Writer.TryWrite(new WalWorkItem
        {
            Kind = WalOpKind.AppendLine,
            Line = line
        });
    }

    /// <summary>
    /// Appends a single UTF-8 encoded text line (e.g. from combat logs) into the WAL buffer.
    /// Flushes a chunk to disk whenever the buffer reaches the configured chunk size (default: 64 KB).
    /// </summary>
    public Task AppendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (!_isAcceptingWrites || _isCompleted || _isDisposed || string.IsNullOrWhiteSpace(line))
            return Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new WalWorkItem
        {
            Kind = WalOpKind.AppendLine,
            Line = line,
            Completion = tcs,
            CancellationToken = cancellationToken
        }))
        {
            return Task.CompletedTask;
        }

        return tcs.Task;
    }

    /// <summary>
    /// Enqueues raw bytes into the active page buffer and triggers disk writes on chunk boundaries.
    /// </summary>
    public Task AppendBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!_isAcceptingWrites || _isCompleted || _isDisposed || data.IsEmpty)
            return Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        // Copy caller memory only when raw Span/Memory is passed asynchronously across channel boundary
        byte[] copy = data.ToArray();
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new WalWorkItem
        {
            Kind = WalOpKind.AppendBytes,
            Bytes = copy,
            Completion = tcs,
            CancellationToken = cancellationToken
        }))
        {
            return Task.CompletedTask;
        }

        return tcs.Task;
    }

    /// <summary>
    /// Flushes any pending buffered bytes to the OS file stream after draining all preceding queued writes.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _isCompleted) return Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new WalWorkItem
        {
            Kind = WalOpKind.Flush,
            Completion = tcs,
            CancellationToken = cancellationToken
        }))
        {
            return Task.CompletedTask;
        }

        return tcs.Task;
    }

    /// <summary>
    /// Drains all queued items, finalizes the WAL journal, forces physical disk flush (Flush(true)), and closes the stream.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_isCompleted) return;
        _isAcceptingWrites = false;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_channel.Writer.TryWrite(new WalWorkItem
        {
            Kind = WalOpKind.Complete,
            Completion = tcs,
            CancellationToken = cancellationToken
        }))
        {
            _channel.Writer.TryComplete();
            await tcs.Task.ConfigureAwait(false);
        }

        await _consumerTask.ConfigureAwait(false);
    }

    private async Task ProcessChannelLoopAsync()
    {
        ChannelReader<WalWorkItem> reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out WalWorkItem item))
                {
                    try
                    {
                        if (item.CancellationToken.IsCancellationRequested)
                        {
                            item.Completion?.TrySetCanceled(item.CancellationToken);
                            continue;
                        }

                        switch (item.Kind)
                        {
                            case WalOpKind.AppendLine:
                                if (!string.IsNullOrEmpty(item.Line) && !_isCompleted)
                                {
                                    await WriteLineInternalAsync(item.Line, item.CancellationToken).ConfigureAwait(false);
                                }
                                item.Completion?.TrySetResult(true);
                                break;

                            case WalOpKind.AppendBytes:
                                if (!item.Bytes.IsEmpty && !_isCompleted)
                                {
                                    await WriteBytesInternalAsync(item.Bytes, item.CancellationToken).ConfigureAwait(false);
                                }
                                item.Completion?.TrySetResult(true);
                                break;

                            case WalOpKind.Flush:
                                if (_bufferOffset > 0 && _stream != null)
                                {
                                    await FlushChunkInternalAsync(item.CancellationToken).ConfigureAwait(false);
                                }
                                if (_stream != null)
                                {
                                    await _stream.FlushAsync(item.CancellationToken).ConfigureAwait(false);
                                }
                                item.Completion?.TrySetResult(true);
                                break;

                            case WalOpKind.Complete:
                                await FinalizeStreamInternalAsync(item.CancellationToken).ConfigureAwait(false);
                                item.Completion?.TrySetResult(true);
                                return;
                        }
                    }
                    catch (OperationCanceledException oce)
                    {
                        item.Completion?.TrySetCanceled(oce.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "[ReplayWalWriter] WAL operation '{Kind}' faulted for Match #{MatchId} on Port {Port}.",
                            item.Kind, _matchId, _port);
                        item.Completion?.TrySetException(ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "[ReplayWalWriter] Fatal unhandled exception in WAL consumer loop for Match #{MatchId} on Port {Port}.",
                _matchId, _port);
        }
    }

    private int EncodeLineSlice(string line, int charOffset, int spaceLeft, out int bytesUsed, out bool completed)
    {
        _utf8Encoder.Convert(
            line.AsSpan(charOffset),
            _buffer.AsSpan(_bufferOffset, spaceLeft),
            flush: true,
            out int charsUsed,
            out bytesUsed,
            out completed);
        return charsUsed;
    }

    private async Task WriteLineInternalAsync(string line, CancellationToken cancellationToken)
    {
        EnsureStreamOpen();

        _utf8Encoder.Reset();

        int charOffset = 0;
        while (charOffset < line.Length)
        {
            int spaceLeft = _chunkSize - _bufferOffset;
            if (spaceLeft <= 0)
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
                spaceLeft = _chunkSize;
            }

            int charsUsed = EncodeLineSlice(line, charOffset, spaceLeft, out int bytesUsed, out bool completed);

            _bufferOffset += bytesUsed;
            charOffset += charsUsed;

            if (_bufferOffset >= _chunkSize || (!completed && charsUsed == 0))
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Append trailing newline '\n' unless caller already supplied one at the end
        if (line[^1] != '\n')
        {
            if (_bufferOffset >= _chunkSize)
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
            }
            _buffer[_bufferOffset++] = (byte)'\n';
            if (_bufferOffset >= _chunkSize)
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteBytesInternalAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        EnsureStreamOpen();

        int remainingOffset = 0;
        int remainingLength = data.Length;
        while (remainingLength > 0)
        {
            int spaceLeft = _chunkSize - _bufferOffset;
            if (spaceLeft <= 0)
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
                spaceLeft = _chunkSize;
            }

            int toCopy = Math.Min(remainingLength, spaceLeft);
            data.Span.Slice(remainingOffset, toCopy).CopyTo(_buffer.AsSpan(_bufferOffset, toCopy));
            _bufferOffset += toCopy;
            remainingOffset += toCopy;
            remainingLength -= toCopy;

            if (_bufferOffset >= _chunkSize)
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushChunkInternalAsync(CancellationToken cancellationToken)
    {
        if (_bufferOffset == 0 || _stream == null) return;

        _sequence++;
        uint payloadSize = (uint)_bufferOffset;
        uint crc = ComputeCrc32(_buffer.AsSpan(0, _bufferOffset));
        _lastCrc32 = crc;

        ReplayWalChunkHeader header = new(
            Magic: ReplayWalChunkHeader.ExpectedMagic,
            SequenceNumber: _sequence,
            PayloadSizeBytes: payloadSize,
            Crc32Checksum: crc);

        header.WriteTo(_headerBuffer);

        // Sequential write: Header + Payload using reusable pre-allocated buffers (0 heap allocations)
        await _stream.WriteAsync(_headerBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(_buffer.AsMemory(0, _bufferOffset), cancellationToken).ConfigureAwait(false);

        _totalFlushedBytes += ReplayWalChunkHeader.HeaderSizeBytes + _bufferOffset;
        _bufferOffset = 0;

        _currentBeacon = new ReplayWalBeacon(
            MatchId: _matchId,
            Port: _port,
            Sequence: _sequence,
            FlushedBytes: _totalFlushedBytes,
            LastCrc32: _lastCrc32,
            Status: "RECORDING");
    }

    private async Task FinalizeStreamInternalAsync(CancellationToken cancellationToken)
    {
        if (_isCompleted) return;

        if (_stream != null)
        {
            if (_bufferOffset > 0)
            {
                await FlushChunkInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_stream is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            _isCompleted = true;
            _currentBeacon = _currentBeacon with { Status = "COMPLETED" };

            _logger?.LogInformation(
                "[ReplayWalWriter] Finalized WAL for Match #{MatchId} on Port {Port}. Total Chunks: {Seq}, Total Bytes: {Bytes:N0}.",
                _matchId, _port, _sequence, _totalFlushedBytes);
        }
        else
        {
            _isCompleted = true;
            _currentBeacon = _currentBeacon with { Status = "COMPLETED" };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isAcceptingWrites = false;

        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "[ReplayWalWriter] Exception during DisposeAsync WAL shutdown for Match #{MatchId} on Port {Port}. Run dotnet test --filter ReplayWalTests to diagnose.",
                _matchId, _port);
        }

        _channel.Writer.TryComplete();

        if (_stream != null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "[ReplayWalWriter] Stream disposal warning for Match #{MatchId} on Port {Port}.",
                    _matchId, _port);
            }
            _stream = null;
        }
    }

    /// <summary>
    /// Pure, zero-allocation IEEE 802.3 CRC32 calculation table lookup.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte tableIndex = (byte)((crc ^ bytes[i]) & 0xFF);
            crc = (crc >> 8) ^ CrcTable[tableIndex];
        }
        return ~crc;
    }

    private static uint[] InitializeCrcTable()
    {
        const uint polynomial = 0xEDB88320;
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
            {
                if ((entry & 1) == 1)
                    entry = (entry >> 1) ^ polynomial;
                else
                    entry >>= 1;
            }
            table[i] = entry;
        }
        return table;
    }
}
