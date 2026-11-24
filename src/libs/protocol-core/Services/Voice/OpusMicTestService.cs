// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpusMicTestService.cs
// System: ProjectKongor - Modern Voice Loopback & Microphone Diagnostic Service
// Policy: Strict Explicit Typing (NO 'var') - Native Win32 Low-Latency Audio & Opus Loopback
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     Encapsulates the acoustic diagnostics and audio loopback payload from an Opus mic test run.
/// </summary>
public sealed class OpusMicTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string InputDeviceName { get; init; } = string.Empty;
    public string OutputDeviceName { get; init; } = string.Empty;
    public int SamplesRecorded { get; init; }
    public int OpusBytesEncoded { get; init; }
    public double CompressionRatio { get; init; }
    public float PeakAmplitude { get; init; }
    public float NormalizedPeak { get; init; }
    public float RmsEnergy { get; init; }
    public float InputVolumePercent { get; init; } = 100f;
    public float AppliedGainDb { get; init; } = 0f;
    public bool AiNoiseSuppressionActive { get; init; } = false;
    public float NoiseReductionDb { get; init; } = 0f;
    public byte[] DecodedWavBytes { get; init; } = Array.Empty<byte>();
    public string SavedWavPath { get; init; } = string.Empty;
}

/// <summary>
///     Modern Windows microphone recording, real-time Opus encoding/decoding,
///     acoustic energy diagnostics, and low-latency headphone loopback auditioning.
///     Completely replaces the legacy 2003 DirectSound 8 capture engine.
/// </summary>
public static class OpusMicTestService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public nint lpNext;
        public nint reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WAVEINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint CALLBACK_NULL = 0;
    private const int SND_ASYNC = 0x0001;
    private const int SND_NODEFAULT = 0x0002;
    private const int SND_MEMORY = 0x0004;

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(nint uDeviceID, out WAVEOUTCAPS pwoc, uint cbwoc);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveInGetDevCaps(nint uDeviceID, out WAVEINCAPS pwic, uint cbwic);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out nint phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, nint dwCallback, nint dwCallbackInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(nint hwi, nint pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(nint hwi, nint pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(nint hwi, nint pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(nint hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(nint hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(nint hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(nint hwi);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundA", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundNative(byte[]? pszSound, nint hmod, int fdwSound);

    /// <summary>
    ///     Checks if a working audio input capture device (microphone) is detected on Windows.
    /// </summary>
    public static bool HasMicrophone()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return waveInGetNumDevs() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Checks if an audio output device (headphones/speakers) is detected on Windows.
    /// </summary>
    public static bool HasPlaybackDevice()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return waveOutGetNumDevs() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Retrieves the friendly display name of the primary Windows audio input device.
    /// </summary>
    public static string GetDefaultInputDeviceName()
    {
        if (!OperatingSystem.IsWindows() || waveInGetNumDevs() == 0) return "Default Microphone";
        try
        {
            if (waveInGetDevCaps(nint.Zero, out WAVEINCAPS caps, (uint)Marshal.SizeOf<WAVEINCAPS>()) == 0)
            {
                return caps.szPname;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpusMicTestService] waveInGetDevCaps failed: {ex.Message}");
        }
        return "Default Microphone";
    }

    /// <summary>
    ///     Retrieves the friendly display name of the primary Windows audio output device.
    /// </summary>
    public static string GetDefaultOutputDeviceName()
    {
        if (!OperatingSystem.IsWindows() || waveOutGetNumDevs() == 0) return "Default Audio Output";
        try
        {
            if (waveOutGetDevCaps(nint.Zero, out WAVEOUTCAPS caps, (uint)Marshal.SizeOf<WAVEOUTCAPS>()) == 0)
            {
                return caps.szPname;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpusMicTestService] waveOutGetDevCaps failed: {ex.Message}");
        }
        return "Default Audio Output";
    }

    /// <summary>
    ///     Stops any active audio playback immediately.
    /// </summary>
    public static void StopPlayback()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            PlaySoundNative(null, nint.Zero, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpusMicTestService] StopPlayback failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Plays raw WAV bytes directly through Windows default audio output.
    ///     When wait is true (default), this call blocks until playback completes.
    /// </summary>
    public static void PlayWavBytes(byte[] wavBytes, bool wait = true)
    {
        if (!OperatingSystem.IsWindows() || !HasPlaybackDevice() || wavBytes.Length == 0) return;
        try
        {
            int flags = SND_MEMORY | SND_NODEFAULT;
            if (!wait) flags |= SND_ASYNC;
            PlaySoundNative(wavBytes, nint.Zero, flags);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpusMicTestService] PlayWavBytes failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Plays raw WAV bytes asynchronously while tracking progress, preventing the host process
    ///     from terminating prematurely before the sound buffer finishes playing.
    /// </summary>
    public static async Task PlayWavBytesAsync(
        byte[] wavBytes,
        Action<int>? onProgressPercent = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !HasPlaybackDevice() || wavBytes.Length <= 44) return;

        uint sampleRate = BitConverter.ToUInt32(wavBytes, 24);
        ushort channels = BitConverter.ToUInt16(wavBytes, 22);
        uint dataSize = BitConverter.ToUInt32(wavBytes, 40);
        int totalSamples = (int)(dataSize / (channels * sizeof(short)));
        int durationMs = (int)((totalSamples / (double)sampleRate) * 1000.0);

        Task playTask = Task.Run(() =>
        {
            PlaySoundNative(wavBytes, nint.Zero, SND_MEMORY | SND_NODEFAULT);
        }, CancellationToken.None);

        int elapsed = 0;
        const int stepMs = 50;
        while (elapsed < durationMs && !playTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(stepMs, cancellationToken);
            elapsed += stepMs;
            int pct = Math.Min(100, elapsed * 100 / Math.Max(1, durationMs));
            onProgressPercent?.Invoke(pct);
        }

        await playTask;
        onProgressPercent?.Invoke(100);
    }

    /// <summary>
    ///     Applies software loudness normalization to PCM samples to bring voice volume
    ///     up to a comfortable headphone level without clipping.
    /// </summary>
    public static float NormalizeAudio(short[] pcm, float targetPeak = 0.75f)
    {
        if (pcm.Length == 0) return 0f;

        float currentPeak = 0f;
        for (int i = 0; i < pcm.Length; i++)
        {
            float norm = Math.Abs(pcm[i] / 32768.0f);
            if (norm > currentPeak) currentPeak = norm;
        }

        if (currentPeak < 0.005f) return currentPeak; // Pure silence / background noise

        float gain = Math.Min(targetPeak / currentPeak, 8.0f);
        float newPeak = 0f;
        for (int i = 0; i < pcm.Length; i++)
        {
            int scaled = (int)(pcm[i] * gain);
            pcm[i] = (short)Math.Clamp(scaled, -32768, 32767);
            float norm = Math.Abs(pcm[i] / 32768.0f);
            if (norm > newPeak) newPeak = norm;
        }

        return newPeak;
    }

    /// <summary>
    ///     Records audio from the microphone for the specified duration, encodes through Opus,
    ///     decodes back to PCM, applies software gain normalization, and returns loopback diagnostics.
    /// </summary>
    public static async Task<OpusMicTestResult> RecordAndAuditionAsync(
        int durationSeconds = 3,
        int sampleRate = 48000,
        float inputVolumePercent = 100f,
        float gainDb = 0f,
        bool enableAiNoiseSuppression = true,
        bool playBackImmediately = false,
        string? saveWavPath = null,
        Action<int>? onProgressPercent = null,
        Action<float>? onLevelUpdate = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new OpusMicTestResult
            {
                Success = false,
                Message = "Microphone capture is only supported on Windows."
            };
        }

        if (!HasMicrophone())
        {
            return new OpusMicTestResult
            {
                Success = false,
                Message = "No microphone hardware detected on this system."
            };
        }

        string inputDev = GetDefaultInputDeviceName();
        string outputDev = GetDefaultOutputDeviceName();

        int clampedDuration = Math.Clamp(durationSeconds, 1, 10);
        int totalSamples = sampleRate * clampedDuration;
        int totalBytes = totalSamples * sizeof(short);

        WAVEFORMATEX wfx = new WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = 1,  // Mono
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * sizeof(short)),
            nBlockAlign = (ushort)sizeof(short),
            wBitsPerSample = 16,
            cbSize = 0
        };

        int openResult = waveInOpen(out nint hWaveIn, WAVE_MAPPER, ref wfx, nint.Zero, nint.Zero, CALLBACK_NULL);
        if (openResult != 0)
        {
            return new OpusMicTestResult
            {
                Success = false,
                Message = $"Failed to open Windows audio recording device '{inputDev}' (MMSYSERR: {openResult}).",
                InputDeviceName = inputDev,
                OutputDeviceName = outputDev
            };
        }

        nint pBuffer = Marshal.AllocHGlobal(totalBytes);
        nint pHdr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());

        try
        {
            WAVEHDR hdr = new WAVEHDR
            {
                lpData = pBuffer,
                dwBufferLength = (uint)totalBytes,
                dwBytesRecorded = 0,
                dwFlags = 0
            };
            Marshal.StructureToPtr(hdr, pHdr, false);

            waveInPrepareHeader(hWaveIn, pHdr, (uint)Marshal.SizeOf<WAVEHDR>());
            waveInAddBuffer(hWaveIn, pHdr, (uint)Marshal.SizeOf<WAVEHDR>());
            waveInStart(hWaveIn);

            int elapsedMs = 0;
            int totalMs = clampedDuration * 1000;
            const int pollIntervalMs = 50;

            while (elapsedMs < totalMs && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(pollIntervalMs, cancellationToken);
                elapsedMs += pollIntervalMs;
                int percent = Math.Min(100, elapsedMs * 100 / totalMs);
                onProgressPercent?.Invoke(percent);

                // Sample recent audio level if available
                WAVEHDR currentHdr = Marshal.PtrToStructure<WAVEHDR>(pHdr);
                uint bytesRecorded = currentHdr.dwBytesRecorded;
                if (bytesRecorded >= sizeof(short) * 960)
                {
                    int sampleIndex = (int)(bytesRecorded / sizeof(short)) - 960;
                    float recentLevel = ComputePeakAmplitude(pBuffer, sampleIndex, 960);
                    onLevelUpdate?.Invoke(recentLevel);
                }
            }

            waveInStop(hWaveIn);
            waveInReset(hWaveIn);
            waveInUnprepareHeader(hWaveIn, pHdr, (uint)Marshal.SizeOf<WAVEHDR>());
            waveInClose(hWaveIn);
            hWaveIn = nint.Zero;

            WAVEHDR finalHdr = Marshal.PtrToStructure<WAVEHDR>(pHdr);
            int recordedBytes = (int)finalHdr.dwBytesRecorded;
            int recordedSamples = recordedBytes / sizeof(short);

            if (recordedSamples == 0)
            {
                return new OpusMicTestResult
                {
                    Success = false,
                    Message = "Zero audio samples recorded from microphone.",
                    InputDeviceName = inputDev,
                    OutputDeviceName = outputDev
                };
            }

            // Copy recorded PCM samples into managed array
            short[] rawPcm = new short[recordedSamples];
            Marshal.Copy(pBuffer, rawPcm, 0, recordedSamples);

            // Clone raw PCM prior to digital processing to measure noise reduction delta
            short[] originalPcm = (short[])rawPcm.Clone();

            // Apply digital volume, gain boost, and AI neural noise suppression in-place
            AiNoiseSuppressionProcessor.ProcessBuffer(
                rawPcm.AsSpan(),
                sampleRate: sampleRate,
                volumePercent: inputVolumePercent,
                gainDb: gainDb,
                enableNoiseSuppression: enableAiNoiseSuppression);

            float noiseReductionDb = enableAiNoiseSuppression
                ? AiNoiseSuppressionProcessor.CalculateNoiseReductionDb(originalPcm, rawPcm)
                : 0f;

            // Compute peak amplitude and RMS energy of processed signal
            float peak = 0f;
            double sumSquares = 0;
            for (int i = 0; i < recordedSamples; i++)
            {
                float normalized = Math.Abs(rawPcm[i] / 32768.0f);
                if (normalized > peak) peak = normalized;
                sumSquares += (double)rawPcm[i] * rawPcm[i];
            }
            float rms = (float)Math.Sqrt(sumSquares / recordedSamples) / 32768.0f;

            // Process through Opus Voice Codec (20ms frames = 960 samples at 48 kHz)
            using OpusVoiceCodec encoderCodec = new OpusVoiceCodec(new OpusVoiceOptions
            {
                SampleRate = sampleRate,
                Channels = 1,
                FrameSizeMs = 20,
                Bitrate = 28000,
                Complexity = 5,
                UseInbandFec = true,
                UseDtx = false // keep continuous for loopback test
            });

            using OpusVoiceCodec decoderCodec = new OpusVoiceCodec(new OpusVoiceOptions
            {
                SampleRate = sampleRate,
                Channels = 1,
                FrameSizeMs = 20,
                Bitrate = 28000
            });

            int frameSize = encoderCodec.FrameSizeSamples; // 960
            int frameCount = recordedSamples / frameSize;
            short[] decodedPcm = new short[frameCount * frameSize];
            byte[] opusTemp = new byte[1276];
            int totalOpusBytes = 0;

            for (int f = 0; f < frameCount; f++)
            {
                ReadOnlySpan<short> inputFrame = rawPcm.AsSpan(f * frameSize, frameSize);
                int encodedBytes = encoderCodec.Encode(inputFrame, opusTemp);
                totalOpusBytes += encodedBytes;

                Span<short> outputFrame = decodedPcm.AsSpan(f * frameSize, frameSize);
                decoderCodec.Decode(opusTemp.AsSpan(0, encodedBytes), outputFrame);
            }

            // Apply software loudness normalization so voice is clear and audible in headphones
            float normalizedPeak = NormalizeAudio(decodedPcm, targetPeak: 0.75f);

            // Generate clean RIFF WAV byte stream from decoded PCM
            byte[] wavBytes = CreateWavFile(decodedPcm, sampleRate, 1);

            string savedPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(saveWavPath))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(saveWavPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(saveWavPath, wavBytes);
                    savedPath = saveWavPath;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OpusMicTestService] Failed to save WAV file: {ex.Message}");
                }
            }

            if (playBackImmediately)
            {
                PlayWavBytes(wavBytes, wait: true);
            }

            double originalBytes = recordedSamples * sizeof(short);
            double compressionRatio = originalBytes / Math.Max(1, totalOpusBytes);

            string noiseSummary = enableAiNoiseSuppression && noiseReductionDb > 0.1f
                ? $", Krisp AI Noise Reduction: -{noiseReductionDb:F1} dB"
                : string.Empty;

            return new OpusMicTestResult
            {
                Success = true,
                Message = $"Opus loopback test complete: {recordedSamples:N0} samples encoded to {totalOpusBytes:N0} bytes ({compressionRatio:F1}:1 compression). Peak: {peak * 100:F1}%, Normalized: {normalizedPeak * 100:F0}%, RMS: {rms * 100:F1}%{noiseSummary}.",
                InputDeviceName = inputDev,
                OutputDeviceName = outputDev,
                SamplesRecorded = recordedSamples,
                OpusBytesEncoded = totalOpusBytes,
                CompressionRatio = compressionRatio,
                PeakAmplitude = peak,
                NormalizedPeak = normalizedPeak,
                RmsEnergy = rms,
                InputVolumePercent = inputVolumePercent,
                AppliedGainDb = gainDb,
                AiNoiseSuppressionActive = enableAiNoiseSuppression,
                NoiseReductionDb = noiseReductionDb,
                DecodedWavBytes = wavBytes,
                SavedWavPath = savedPath
            };
        }
        finally
        {
            if (hWaveIn != nint.Zero)
            {
                try
                {
                    waveInStop(hWaveIn);
                    waveInReset(hWaveIn);
                    waveInClose(hWaveIn);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OpusMicTestService] waveIn cleanup failed: {ex.Message}");
                }
            }

            if (pBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(pBuffer);
            }

            if (pHdr != nint.Zero)
            {
                Marshal.FreeHGlobal(pHdr);
            }
        }
    }

    private static float ComputePeakAmplitude(nint pBuffer, int startSample, int count)
    {
        if (startSample < 0 || count <= 0) return 0f;
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            short sample = Marshal.ReadInt16(pBuffer, (startSample + i) * sizeof(short));
            float normalized = Math.Abs(sample / 32768.0f);
            if (normalized > peak) peak = normalized;
        }
        return peak;
    }

    /// <summary>
    ///     Creates a valid 44-byte RIFF WAVE file header followed by 16-bit PCM samples.
    /// </summary>
    public static byte[] CreateWavFile(ReadOnlySpan<short> pcmSamples, int sampleRate, int channels)
    {
        int subChunk2Size = pcmSamples.Length * sizeof(short);
        int chunkSize = 36 + subChunk2Size;
        int byteRate = sampleRate * channels * sizeof(short);
        int blockAlign = channels * sizeof(short);

        byte[] wav = new byte[44 + subChunk2Size];
        using MemoryStream ms = new MemoryStream(wav);
        using BinaryWriter writer = new BinaryWriter(ms);

        // RIFF chunk descriptor
        writer.Write((byte)'R'); writer.Write((byte)'I'); writer.Write((byte)'F'); writer.Write((byte)'F');
        writer.Write(chunkSize);
        writer.Write((byte)'W'); writer.Write((byte)'A'); writer.Write((byte)'V'); writer.Write((byte)'E');

        // "fmt " sub-chunk
        writer.Write((byte)'f'); writer.Write((byte)'m'); writer.Write((byte)'t'); writer.Write((byte)' ');
        writer.Write(16); // Subchunk1Size (16 for PCM)
        writer.Write((ushort)1); // AudioFormat (1 = PCM)
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)16); // BitsPerSample

        // "data" sub-chunk
        writer.Write((byte)'d'); writer.Write((byte)'a'); writer.Write((byte)'t'); writer.Write((byte)'a');
        writer.Write(subChunk2Size);

        // Raw PCM samples
        for (int i = 0; i < pcmSamples.Length; i++)
        {
            writer.Write(pcmSamples[i]);
        }

        return wav;
    }
}
