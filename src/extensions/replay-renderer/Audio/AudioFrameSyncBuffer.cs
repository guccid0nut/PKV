// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AudioFrameSyncBuffer.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.Audio;

/// <summary>
///     Bit-perfect audio synchronization ring buffer.
///     Slices incoming arbitrary-sized WASAPI loopback PCM chunks into exact per-frame blocks:
///     (SampleRate / TargetFps) * Channels * (BitsPerSample / 8).
///     For 48kHz Stereo 16-bit PCM at 60 FPS: 800 samples/chan * 2 chan * 2 bytes = exactly 3,200 bytes/frame.
///     Injects synthetic zero-volume PCM silence on underrun to guarantee 100% video/audio lockstep.
/// </summary>
public sealed class AudioFrameSyncBuffer : IDisposable
{
    private readonly MemoryStream _accumulatedBuffer = new();
    private readonly ILogger<AudioFrameSyncBuffer> _logger;
    private readonly byte[] _silenceBuffer;

    private readonly Lock _syncLock = new();
    private long _totalBytesDequeued;
    private long _totalBytesEnqueued;
    private long _totalFramesProcessed;
    private long _underrunFramesCount;

    public AudioFrameSyncBuffer(
        ILogger<AudioFrameSyncBuffer> logger,
        int sampleRate = 48000,
        int channels = 2,
        int bitsPerSample = 16,
        int targetFps = 60)
    {
        _logger = logger;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        TargetFps = targetFps;

        int bytesPerSample = bitsPerSample / 8;
        int samplesPerFramePerChannel = sampleRate / targetFps; // 48000 / 60 = 800
        BytesPerFrame = samplesPerFramePerChannel * channels * bytesPerSample; // 800 * 2 * 2 = 3200 bytes

        _silenceBuffer = new byte[BytesPerFrame];
        Array.Clear(_silenceBuffer, 0, _silenceBuffer.Length);

        _logger.LogInformation(
            "[AudioSyncBuffer] Configured {Rate}Hz {Channels}ch {Bits}bit ({Bytes} bytes/frame, {Samples} samples/frame/chan) @ {Fps} FPS.",
            sampleRate, channels, bitsPerSample, BytesPerFrame, samplesPerFramePerChannel, targetFps);
    }

    public int BytesPerFrame { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample { get; }

    public int TargetFps { get; }

    public long TotalFramesProcessed => Interlocked.Read(ref _totalFramesProcessed);
    public long UnderrunFramesCount => Interlocked.Read(ref _underrunFramesCount);
    public long TotalBytesEnqueued => Interlocked.Read(ref _totalBytesEnqueued);
    public long TotalBytesDequeued => Interlocked.Read(ref _totalBytesDequeued);

    /// <summary>
    ///     Enqueues an arbitrary-sized block of raw PCM audio bytes into the accumulator stream.
    /// </summary>
    public bool TryEnqueueAudio(ReadOnlySpan<byte> pcmData)
    {
        if (pcmData.IsEmpty) return false;

        lock (_syncLock)
        {
            // If the buffer grows larger than 5 seconds of audio (5 * 60 * 3200 = 960,000 bytes), drop excess to prevent memory bloat
            if (_accumulatedBuffer.Length > BytesPerFrame * TargetFps * 5)
            {
                _logger.LogDebug(
                    "[AudioSyncBuffer] Audio buffer overflow ({Len} bytes). Truncating oldest data to maintain real-time sync.",
                    _accumulatedBuffer.Length);
                _accumulatedBuffer.SetLength(0);
            }

            _accumulatedBuffer.Position = _accumulatedBuffer.Length;
            _accumulatedBuffer.Write(pcmData);
            Interlocked.Add(ref _totalBytesEnqueued, pcmData.Length);
        }

        return true;
    }

    /// <summary>
    ///     Dequeues exactly one video frame's worth of PCM audio bytes (e.g. 3,200 bytes).
    ///     If fewer bytes are available in the accumulator, returns a zero-filled silence buffer and logs an underrun.
    /// </summary>
    public byte[] GetNextFrameAudioBlock()
    {
        Interlocked.Increment(ref _totalFramesProcessed);

        lock (_syncLock)
        {
            if (_accumulatedBuffer.Length >= BytesPerFrame)
            {
                byte[] frameBlock = new byte[BytesPerFrame];
                _accumulatedBuffer.Position = 0;
                int bytesRead = _accumulatedBuffer.Read(frameBlock, 0, BytesPerFrame);

                if (bytesRead == BytesPerFrame)
                {
                    // Shift remaining unread bytes to the front of the MemoryStream
                    int remainingBytes = (int)(_accumulatedBuffer.Length - BytesPerFrame);
                    if (remainingBytes > 0)
                    {
                        byte[] remainingBuffer = new byte[remainingBytes];
                        _accumulatedBuffer.Read(remainingBuffer, 0, remainingBytes);
                        _accumulatedBuffer.SetLength(0);
                        _accumulatedBuffer.Write(remainingBuffer, 0, remainingBytes);
                    }
                    else
                    {
                        _accumulatedBuffer.SetLength(0);
                    }

                    Interlocked.Add(ref _totalBytesDequeued, BytesPerFrame);
                    return frameBlock;
                }
            }
        }

        // Buffer underrun occurred: inject synthetic zero-volume PCM silence
        Interlocked.Increment(ref _underrunFramesCount);
        if (_underrunFramesCount % 60 == 1)
            _logger.LogDebug(
                "[AudioSyncBuffer] Audio underrun (Total Underruns: {Count}). Injected 0-PCM silence frame.",
                _underrunFramesCount);

        return _silenceBuffer;
    }

    /// <summary>
    ///     Resets the internal accumulator and stats.
    /// </summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            _accumulatedBuffer.SetLength(0);
        }

        Interlocked.Exchange(ref _totalFramesProcessed, 0);
        Interlocked.Exchange(ref _underrunFramesCount, 0);
        Interlocked.Exchange(ref _totalBytesEnqueued, 0);
        Interlocked.Exchange(ref _totalBytesDequeued, 0);
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _accumulatedBuffer.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
