// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AudioStreamSyncBuffer.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Export;

/// <summary>
///     High-fidelity, bit-perfect 48kHz 16-bit stereo PCM audio synchronization buffer.
///     Computes exact per-frame audio byte quotas:
///     $\frac{\text{SampleRate}}{\text{FPS}} \times \text{Channels} \times 2 = 3,200$ bytes per frame at 48kHz/60fps.
///     Supports live PCM ingestion, synthetic procedural combat sound synthesis, and WAV container formatting.
/// </summary>
public sealed class AudioStreamSyncBuffer : IDisposable
{
    private readonly MemoryStream _accumulatedBuffer = new();
    private readonly ILogger<AudioStreamSyncBuffer> _logger;
    private readonly byte[] _silenceBuffer;
    private readonly Lock _syncLock = new();

    private long _totalBytesDequeued;
    private long _totalBytesEnqueued;
    private long _totalFramesProcessed;
    private long _underrunFramesCount;
    private bool _isDisposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public int TargetFps { get; }
    public int BytesPerFrame { get; }
    public int SamplesPerFramePerChannel { get; }

    public long TotalFramesProcessed => Interlocked.Read(ref _totalFramesProcessed);
    public long UnderrunFramesCount => Interlocked.Read(ref _underrunFramesCount);
    public long TotalBytesEnqueued => Interlocked.Read(ref _totalBytesEnqueued);
    public long TotalBytesDequeued => Interlocked.Read(ref _totalBytesDequeued);

    public AudioStreamSyncBuffer(
        int sampleRate = 48000,
        int channels = 2,
        int bitsPerSample = 16,
        int targetFps = 60,
        ILogger<AudioStreamSyncBuffer>? logger = null)
    {
        _logger = logger ?? NullLogger<AudioStreamSyncBuffer>.Instance;

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "SampleRate must be positive.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be positive.");
        }

        if (bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
        {
            throw new ArgumentException("BitsPerSample must be 16, 24, or 32.", nameof(bitsPerSample));
        }

        if (targetFps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps), targetFps, "TargetFps must be positive.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        TargetFps = targetFps;

        int bytesPerSample = bitsPerSample / 8;
        SamplesPerFramePerChannel = sampleRate / targetFps; // 48000 / 60 = 800
        BytesPerFrame = SamplesPerFramePerChannel * channels * bytesPerSample; // 800 * 2 * 2 = 3200 bytes

        _silenceBuffer = new byte[BytesPerFrame];
        Array.Clear(_silenceBuffer, 0, _silenceBuffer.Length);

        _logger.LogInformation(
            "[AudioStreamSyncBuffer] Configured {Rate}Hz {Channels}ch {Bits}bit ({Bytes} bytes/frame, {Samples} samples/chan) @ {Fps} FPS",
            sampleRate, channels, bitsPerSample, BytesPerFrame, SamplesPerFramePerChannel, targetFps);
    }

    /// <summary>
    ///     Calculates the exact audio byte quota for arbitrary sample rates and frame rates.
    /// </summary>
    public static int CalculateBytesPerFrame(int sampleRate, int targetFps, int channels = 2, int bitsPerSample = 16)
    {
        if (targetFps <= 0) return 0;
        int bytesPerSample = bitsPerSample / 8;
        int samplesPerFramePerChannel = sampleRate / targetFps;
        return samplesPerFramePerChannel * channels * bytesPerSample;
    }

    /// <summary>
    ///     Enqueues an arbitrary-sized block of raw PCM audio bytes into the internal accumulator stream.
    /// </summary>
    public bool TryEnqueueAudio(ReadOnlySpan<byte> pcmData)
    {
        if (pcmData.IsEmpty) return false;
        ThrowIfDisposed();

        lock (_syncLock)
        {
            // Limit internal buffer to 5 seconds of audio to prevent unbounded memory growth
            long maxBufferBytes = (long)BytesPerFrame * TargetFps * 5;
            if (_accumulatedBuffer.Length > maxBufferBytes)
            {
                _logger.LogWarning(
                    "[AudioStreamSyncBuffer] Audio buffer overflow ({Len} bytes). Truncating to maintain real-time synchronization.",
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
    ///     If fewer bytes are available in the accumulator, returns a zero-filled silence buffer and records an underrun.
    /// </summary>
    public byte[] GetNextFrameAudioBlock()
    {
        ThrowIfDisposed();
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
        if (UnderrunFramesCount % 60 == 1)
        {
            _logger.LogDebug(
                "[AudioStreamSyncBuffer] Audio underrun (Total Underruns: {Count}). Injected 0-PCM silence frame.",
                UnderrunFramesCount);
        }

        return _silenceBuffer;
    }

    /// <summary>
    ///     Generates a full continuous 48kHz 16-bit stereo PCM audio track across $[t_{in}, t_{out}]$,
    ///     synthesizing ambient game background audio and procedural combat event sound effects (First Blood, Kills, Ults).
    /// </summary>
    public byte[] SynthesizeAudioTrack(
        int inMarkerMs,
        int outMarkerMs,
        int targetFps = 60,
        IReadOnlyList<CombatEventMarker>? combatEvents = null)
    {
        int durationMs = Math.Max(0, outMarkerMs - inMarkerMs);
        if (durationMs <= 0)
        {
            return Array.Empty<byte>();
        }

        int totalFrames = (int)Math.Floor((double)durationMs * targetFps / 1000.0);
        int totalSamplesPerChannel = totalFrames * (SampleRate / targetFps);
        int totalSamples = totalSamplesPerChannel * Channels;
        int totalBytes = totalSamples * (BitsPerSample / 8);

        short[] pcmSamples = new short[totalSamples];

        _logger.LogInformation(
            "[AudioStreamSyncBuffer] Synthesizing {DurationMs}ms audio track ({Frames} frames, {Bytes} bytes, {Events} combat events)...",
            durationMs, totalFrames, totalBytes, combatEvents?.Count ?? 0);

        // 1. Generate ambient game background soundscape (low atmospheric hum + river flow oscillation)
        double sampleRateDouble = SampleRate;
        for (int i = 0; i < totalSamplesPerChannel; i++)
        {
            double t = (double)i / sampleRateDouble;

            // 55 Hz sub-drone + 110 Hz harmonic + 0.5 Hz atmospheric swell
            double swell = 0.7 + 0.3 * Math.Sin(2.0 * Math.PI * 0.5 * t);
            double ambientHum = Math.Sin(2.0 * Math.PI * 55.0 * t) * 600.0 * swell;
            double riverWind = Math.Sin(2.0 * Math.PI * 110.0 * t) * 300.0 * swell;

            short leftSample = (short)Math.Clamp((int)(ambientHum + riverWind), short.MinValue, short.MaxValue);
            short rightSample = (short)Math.Clamp((int)(ambientHum - riverWind), short.MinValue, short.MaxValue);

            pcmSamples[i * 2] = leftSample;
            pcmSamples[i * 2 + 1] = rightSample;
        }

        // 2. Synthesize discrete combat event sound effects at exact timeline timestamps
        if (combatEvents != null)
        {
            foreach (CombatEventMarker evt in combatEvents)
            {
                if (evt.TimestampMs < inMarkerMs || evt.TimestampMs > outMarkerMs)
                {
                    continue; // Event outside trim range
                }

                int eventOffsetMs = evt.TimestampMs - inMarkerMs;
                int startSampleIndex = (int)((double)eventOffsetMs * SampleRate / 1000.0);
                SynthesizeCombatEventSfx(pcmSamples, totalSamplesPerChannel, startSampleIndex, evt);
            }
        }

        // 3. Convert 16-bit short samples to little-endian byte array
        byte[] pcmBytes = new byte[totalBytes];
        Buffer.BlockCopy(pcmSamples, 0, pcmBytes, 0, totalBytes);

        _logger.LogInformation(
            "[AudioStreamSyncBuffer] Synthesized complete audio track of {Bytes} bytes ({Frames} frames @ {Fps} FPS)",
            pcmBytes.Length, totalFrames, targetFps);

        return pcmBytes;
    }

    private void SynthesizeCombatEventSfx(
        short[] pcmSamples,
        int totalSamplesPerChannel,
        int startSampleIndex,
        CombatEventMarker evt)
    {
        double sampleRateDouble = SampleRate;
        int sfxDurationSamples = (int)(SampleRate * 1.5); // 1.5 seconds SFX envelope

        float intensity = Math.Clamp(evt.IntensityScore, 0.2f, 1.0f);

        for (int offset = 0; offset < sfxDurationSamples; offset++)
        {
            int channelIdx = startSampleIndex + offset;
            if (channelIdx >= totalSamplesPerChannel) break;

            double t = (double)offset / sampleRateDouble;
            double decay = Math.Exp(-t * 3.5); // Exponential decay

            double wave = 0.0;

            switch (evt.EventType)
            {
                case CombatEventType.FirstBlood:
                case CombatEventType.HeroKill:
                    // Fanfare brass stinger: 440 Hz (A4) -> 880 Hz (A5) pitch sweep + punchy transient
                    double pitch = 440.0 + (880.0 - 440.0) * Math.Exp(-t * 6.0);
                    wave = Math.Sin(2.0 * Math.PI * pitch * t) * 8000.0 * intensity * decay;
                    // Add percussive sub-bass impact punch
                    wave += Math.Sin(2.0 * Math.PI * 65.0 * t) * 9000.0 * Math.Exp(-t * 10.0) * intensity;
                    break;

                case CombatEventType.TowerKill:
                case CombatEventType.BarracksKill:
                    // Low resonant structural collapse rumble (50 Hz + 75 Hz)
                    wave = (Math.Sin(2.0 * Math.PI * 50.0 * t) + 0.6 * Math.Sin(2.0 * Math.PI * 75.0 * t)) * 10000.0 * intensity * Math.Exp(-t * 2.0);
                    break;

                case CombatEventType.BossKill:
                case CombatEventType.TeamFight:
                    // Resonant combat stinger (300 Hz to 600 Hz modulation)
                    double modFreq = 300.0 + 300.0 * Math.Sin(2.0 * Math.PI * 4.0 * t);
                    wave = Math.Sin(2.0 * Math.PI * modFreq * t) * 6000.0 * intensity * decay;
                    break;

                default:
                    // General combat hit impact (120 Hz tone with rapid exponential drop)
                    wave = Math.Sin(2.0 * Math.PI * 120.0 * t) * 5000.0 * intensity * Math.Exp(-t * 8.0);
                    break;
            }

            int existingLeft = pcmSamples[channelIdx * 2];
            int existingRight = pcmSamples[channelIdx * 2 + 1];

            int mixedLeft = (int)Math.Clamp(existingLeft + wave, short.MinValue, short.MaxValue);
            int mixedRight = (int)Math.Clamp(existingRight + wave, short.MinValue, short.MaxValue);

            pcmSamples[channelIdx * 2] = (short)mixedLeft;
            pcmSamples[channelIdx * 2 + 1] = (short)mixedRight;
        }
    }

    /// <summary>
    ///     Writes a standard 44-byte RIFF/WAVE header to the stream for 16-bit PCM stereo audio.
    /// </summary>
    public static void WriteWavHeader(
        Stream destinationStream,
        int pcmDataLength,
        int sampleRate = 48000,
        int channels = 2,
        int bitsPerSample = 16)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);

        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int chunkSize = 36 + pcmDataLength;

        using BinaryWriter writer = new(destinationStream, Encoding.UTF8, leaveOpen: true);

        // RIFF Chunk Descriptor
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(chunkSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt Sub-chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size for PCM = 16
        writer.Write((short)1); // AudioFormat: 1 = PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);

        // data Sub-chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmDataLength);
    }

    /// <summary>
    ///     Wraps raw PCM audio bytes into a fully formed in-memory WAV stream with standard RIFF headers.
    /// </summary>
    public static MemoryStream CreateWavStream(
        byte[] pcmData,
        int sampleRate = 48000,
        int channels = 2,
        int bitsPerSample = 16)
    {
        ArgumentNullException.ThrowIfNull(pcmData);

        MemoryStream wavStream = new(44 + pcmData.Length);
        WriteWavHeader(wavStream, pcmData.Length, sampleRate, channels, bitsPerSample);
        wavStream.Write(pcmData, 0, pcmData.Length);
        wavStream.Position = 0;
        return wavStream;
    }

    /// <summary>
    ///     Saves raw PCM audio bytes as a valid .wav audio file on disk.
    /// </summary>
    public static void SaveToWavFile(
        string filePath,
        byte[] pcmData,
        int sampleRate = 48000,
        int channels = 2,
        int bitsPerSample = 16)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(pcmData);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using FileStream fs = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteWavHeader(fs, pcmData.Length, sampleRate, channels, bitsPerSample);
        fs.Write(pcmData, 0, pcmData.Length);
    }

    /// <summary>
    ///     Resets internal accumulator state and counters.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        lock (_syncLock)
        {
            _accumulatedBuffer.SetLength(0);
        }

        Interlocked.Exchange(ref _totalFramesProcessed, 0);
        Interlocked.Exchange(ref _underrunFramesCount, 0);
        Interlocked.Exchange(ref _totalBytesEnqueued, 0);
        Interlocked.Exchange(ref _totalBytesDequeued, 0);
        _logger.LogInformation("[AudioStreamSyncBuffer] Reset audio buffer state");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, typeof(AudioStreamSyncBuffer));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_syncLock)
        {
            _accumulatedBuffer.Dispose();
        }

        _logger.LogInformation("[AudioStreamSyncBuffer] Disposed audio synchronization buffer");
        GC.SuppressFinalize(this);
    }
}
