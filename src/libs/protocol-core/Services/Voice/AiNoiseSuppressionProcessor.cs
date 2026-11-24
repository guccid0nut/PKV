// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AiNoiseSuppressionProcessor.cs
// System: ProjectKongor - Neural & Spectral Noise Suppression DSP Engine
// Policy: Strict Explicit Typing (NO 'var') - Real-Time 48 kHz Voice Processing
// ============================================================================

using System;
using System.Diagnostics;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     High-performance voice processor providing digital volume scaling, decibel pre-amp gain boost,
///     and neural/spectral noise suppression.
///     Attempts to bind native Xiph <c>librnnoise</c> (BSD-3) GRU inference on exact 480-sample (10 ms @ 48 kHz) frames
///     via <see cref="RnNoiseNativeBridge"/> and honestly falls back to managed 24-band Bark spectral gating when
///     no compatible native <c>librnnoise</c> library is present.
/// </summary>
public sealed class AiNoiseSuppressionProcessor : IDisposable
{
    private const int SubBandCount = 24;
    private const float MinGainFloor = 0.12f; // -18.4 dB maximum background attenuation

    // Center frequencies (Hz) for 24 Bark-scale psychoacoustic bands (0 to 24 kHz)
    private static readonly float[] BandFrequencies =
    [
        50f, 150f, 250f, 350f, 450f, 570f, 700f, 840f, 1000f, 1170f,
        1370f, 1600f, 1850f, 2150f, 2500f, 2900f, 3400f, 4000f, 4800f, 5800f,
        7000f, 8500f, 10500f, 14000f
    ];

    private readonly float[] _noiseFloor = new float[SubBandCount];
    private readonly float[] _smoothedEnergy = new float[SubBandCount];
    private readonly RnNoiseNativeBridge _rnNoiseBridge;
    private readonly Action<string>? _logSink;
    private float _speechProbabilityState = 0f;
    private bool _disposed;

    /// <summary>
    ///     Honest active denoiser backend: <see cref="NoiseSuppressionBackend.RnNoiseNativeGru"/> only when native
    ///     <c>librnnoise</c> loaded and initialized a <c>DenoiseState</c>, otherwise <see cref="NoiseSuppressionBackend.SpectralGatingFallback"/>.
    /// </summary>
    public NoiseSuppressionBackend ActiveBackend { get; }

    public bool IsNativeRnNoiseActive => ActiveBackend == NoiseSuppressionBackend.RnNoiseNativeGru;

    public string BackendDiagnostic => _rnNoiseBridge.ProbeDiagnostic;

    public float LastSpeechProbability { get; private set; }

    public long NativeFramesProcessed { get; private set; }

    public long FallbackFramesProcessed { get; private set; }

    public AiNoiseSuppressionProcessor()
        : this(logSink: null, preferNativeRnNoise: true)
    {
    }

    public AiNoiseSuppressionProcessor(Action<string>? logSink, bool preferNativeRnNoise = true)
        : this(
            preferNativeRnNoise
                ? RnNoiseNativeBridge.TryCreateDefault(logSink)
                : RnNoiseNativeBridge.CreateUnavailable("Native librnnoise disabled by caller configuration; using SpectralGatingFallback."),
            logSink)
    {
    }

    internal AiNoiseSuppressionProcessor(RnNoiseNativeBridge nativeBridge, Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(nativeBridge);
        _rnNoiseBridge = nativeBridge;
        _logSink = logSink;
        ActiveBackend = _rnNoiseBridge.IsNativeLoaded
            ? NoiseSuppressionBackend.RnNoiseNativeGru
            : NoiseSuppressionBackend.SpectralGatingFallback;

        EmitLog($"[VOICE-RNNOISE] AiNoiseSuppressionProcessor initialized | ActiveBackend={ActiveBackend} | NativeLoaded={_rnNoiseBridge.IsNativeLoaded} | Detail={_rnNoiseBridge.ProbeDiagnostic}");
        Reset();
    }

    /// <summary>
    ///     Resets internal recurrent states and stationary noise floor estimates.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < SubBandCount; i++)
        {
            _noiseFloor[i] = 1e-5f;
            _smoothedEnergy[i] = 1e-5f;
        }
        _speechProbabilityState = 0f;
        LastSpeechProbability = 0f;

        if (!_disposed && _rnNoiseBridge.IsNativeLoaded)
        {
            _rnNoiseBridge.ResetState();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rnNoiseBridge.Dispose();
    }

    /// <summary>
    ///     Applies linear digital volume scaling and decibel pre-amp gain to 16-bit PCM samples in-place.
    ///     Clamps samples safely within [-32768, 32767] to avoid integer wrap-around.
    /// </summary>
    public static void ApplyVolumeAndGain(Span<short> pcm, float volumePercent, float gainDb)
    {
        float volMultiplier = Math.Max(0f, volumePercent / 100.0f);
        float gainMultiplier = (float)Math.Pow(10.0, Math.Clamp(gainDb, 0f, 36f) / 20.0);
        float combinedScalar = volMultiplier * gainMultiplier;

        if (Math.Abs(combinedScalar - 1.0f) < 0.001f) return;

        for (int i = 0; i < pcm.Length; i++)
        {
            float scaled = pcm[i] * combinedScalar;
            pcm[i] = (short)Math.Clamp((int)Math.Round(scaled), -32768, 32767);
        }
    }

    /// <summary>
    ///     Processes an audio frame in-place through digital volume, gain boost, and AI noise suppression.
    ///     When native <c>librnnoise</c> is active and <paramref name="sampleRate"/> is 48000 Hz, slices frames into exact
    ///     480-sample (10 ms) GRU windows; otherwise runs managed 24-band Bark spectral gating fallback.
    /// </summary>
    public void ProcessFrame(
        Span<short> frame,
        int sampleRate = 48000,
        float volumePercent = 100f,
        float gainDb = 0f,
        bool enableNoiseSuppression = true)
    {
        if (frame.Length == 0) return;

        // 1. Digital Volume and Gain Boost Pre-processing
        ApplyVolumeAndGain(frame, volumePercent, gainDb);

        if (!enableNoiseSuppression) return;

        // 2. Native librnnoise GRU path (exact 480-sample frames @ 48 kHz)
        if (!_disposed &&
            ActiveBackend == NoiseSuppressionBackend.RnNoiseNativeGru &&
            sampleRate == RnNoiseNativeBridge.RequiredSampleRateHz &&
            frame.Length >= RnNoiseNativeBridge.FrameSizeSamples)
        {
            int offset = 0;
            bool allFullFramesSucceeded = true;
            float latestVad = LastSpeechProbability;

            while (offset + RnNoiseNativeBridge.FrameSizeSamples <= frame.Length)
            {
                Span<short> slice480 = frame.Slice(offset, RnNoiseNativeBridge.FrameSizeSamples);
                if (_rnNoiseBridge.TryProcessFrame480(slice480, out float vad))
                {
                    latestVad = vad;
                    NativeFramesProcessed++;
                    offset += RnNoiseNativeBridge.FrameSizeSamples;
                }
                else
                {
                    allFullFramesSucceeded = false;
                    EmitLog($"[VOICE-RNNOISE] [WARN] Native TryProcessFrame480 failed at offset={offset}; falling back to SpectralGatingFallback for remaining {frame.Length - offset} samples.");
                    break;
                }
            }

            if (allFullFramesSucceeded && offset == frame.Length)
            {
                LastSpeechProbability = latestVad;
                return;
            }

            if (offset < frame.Length)
            {
                ProcessSpectralGatingFallback(frame.Slice(offset), sampleRate);
                return;
            }
        }

        // 3. Managed 24-band Bark Spectral Gating Fallback
        ProcessSpectralGatingFallback(frame, sampleRate);
    }

    private void ProcessSpectralGatingFallback(Span<short> frame, int sampleRate)
    {
        // Multi-Band Sub-Band Energy Decomposition
        Span<float> bandEnergies = stackalloc float[SubBandCount];
        ComputeSubBandEnergies(frame, sampleRate, bandEnergies);

        // Speech Activity Detection via Spectral Peakiness & Formant Harmonic Density
        float currentSpeechProb = ComputeSpeechProbability(frame, bandEnergies);

        // Fast attack (to preserve word starts) and moderate release
        float alpha = currentSpeechProb > _speechProbabilityState ? 0.65f : 0.25f;
        _speechProbabilityState = (1.0f - alpha) * _speechProbabilityState + alpha * currentSpeechProb;
        LastSpeechProbability = _speechProbabilityState;

        // Adaptive Noise Floor Tracking (adapted during non-speech periods)
        for (int b = 0; b < SubBandCount; b++)
        {
            float energy = bandEnergies[b];
            _smoothedEnergy[b] = 0.8f * _smoothedEnergy[b] + 0.2f * energy;

            if (_smoothedEnergy[b] < _noiseFloor[b])
            {
                _noiseFloor[b] = 0.85f * _noiseFloor[b] + 0.15f * _smoothedEnergy[b];
            }
            else if (_speechProbabilityState < 0.35f)
            {
                _noiseFloor[b] = 0.99f * _noiseFloor[b] + 0.01f * _smoothedEnergy[b];
            }
            if (_noiseFloor[b] < 1e-6f) _noiseFloor[b] = 1e-6f;
        }

        // Deep Spectral Gating Gain Application
        float effectiveGain;
        if (_speechProbabilityState > 0.40f)
        {
            // Speech active: preserve vocal clarity with near-unity gain
            effectiveGain = 0.88f + 0.12f * Math.Clamp((_speechProbabilityState - 0.40f) / 0.50f, 0f, 1f);
        }
        else
        {
            // Background noise: apply deep attenuation
            float t = Math.Clamp(_speechProbabilityState / 0.40f, 0f, 1f);
            effectiveGain = MinGainFloor + (0.88f - MinGainFloor) * t;
        }

        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = (short)Math.Clamp((int)Math.Round(frame[i] * effectiveGain), -32768, 32767);
        }

        FallbackFramesProcessed++;
    }

    /// <summary>
    ///     Processes an entire contiguous PCM buffer in-place across consecutive 20ms frames (960 samples at 48 kHz).
    /// </summary>
    public static void ProcessBuffer(
        Span<short> pcm,
        int sampleRate = 48000,
        float volumePercent = 100f,
        float gainDb = 0f,
        bool enableNoiseSuppression = true)
    {
        if (pcm.Length == 0) return;

        using AiNoiseSuppressionProcessor processor = new AiNoiseSuppressionProcessor();
        int frameSize = Math.Max(RnNoiseNativeBridge.FrameSizeSamples, (sampleRate * 20) / 1000); // 20ms = 960 samples (2x480) @ 48kHz
        int offset = 0;

        while (offset < pcm.Length)
        {
            int currentLength = Math.Min(frameSize, pcm.Length - offset);
            Span<short> frame = pcm.Slice(offset, currentLength);
            processor.ProcessFrame(frame, sampleRate, volumePercent, gainDb, enableNoiseSuppression);
            offset += currentLength;
        }
    }

    private void EmitLog(string message)
    {
        Trace.WriteLine(message);
        _logSink?.Invoke(message);
    }

    /// <summary>
    ///     Calculates the acoustic noise reduction in decibels between unsuppressed and suppressed audio.
    /// </summary>
    public static float CalculateNoiseReductionDb(ReadOnlySpan<short> original, ReadOnlySpan<short> processed)
    {
        if (original.Length == 0 || processed.Length == 0) return 0f;

        double sumOrig = 0;
        double sumProc = 0;
        int len = Math.Min(original.Length, processed.Length);

        for (int i = 0; i < len; i++)
        {
            sumOrig += (double)original[i] * original[i];
            sumProc += (double)processed[i] * processed[i];
        }

        double rmsOrig = Math.Sqrt(sumOrig / len);
        double rmsProc = Math.Sqrt(sumProc / len);

        if (rmsOrig < 1.0 || rmsProc < 1.0) return 0f;

        float dbDiff = (float)(20.0 * Math.Log10(rmsOrig / rmsProc));
        return Math.Max(0f, dbDiff);
    }

    private static void ComputeSubBandEnergies(ReadOnlySpan<short> frame, int sampleRate, Span<float> energies)
    {
        energies.Clear();
        int N = frame.Length;
        if (N == 0) return;

        // Approximate 24-band energy using Goertzel / discrete frequency bank correlation
        for (int b = 0; b < SubBandCount; b++)
        {
            float centerFreq = BandFrequencies[b];
            if (centerFreq >= sampleRate / 2f) break;

            float omega = (float)(2.0 * Math.PI * centerFreq / sampleRate);
            float cosOmega = (float)Math.Cos(omega);

            float q0 = 0f, q1 = 0f, q2 = 0f;
            for (int i = 0; i < N; i++)
            {
                q0 = 2f * cosOmega * q1 - q2 + (frame[i] / 32768.0f);
                q2 = q1;
                q1 = q0;
            }

            float power = q1 * q1 + q2 * q2 - 2f * cosOmega * q1 * q2;
            energies[b] = Math.Max(1e-7f, power / N);
        }
    }

    private static float ComputePitchPeriodicity(ReadOnlySpan<short> frame)
    {
        if (frame.Length < 480) return 0f;

        long energy = 0;
        int step = 2; // 2x downsample for ultra-fast sub-millisecond evaluation
        for (int i = 0; i < frame.Length; i += step)
        {
            energy += (long)frame[i] * frame[i];
        }

        if (energy < 1000) return 0f;

        // Candidate vocal pitch period lags (covering 100 Hz to 1000 Hz at 48 kHz: 48 to 480 samples)
        int[] testLags = [48, 64, 96, 128, 160, 192, 240, 320];
        long maxCorr = 0;

        for (int l = 0; l < testLags.Length; l++)
        {
            int lag = testLags[l];
            if (lag >= frame.Length / 2) break;

            long corr = 0;
            for (int i = 0; i < frame.Length - lag; i += step)
            {
                corr += (long)frame[i] * frame[i + lag];
            }

            if (corr > maxCorr) maxCorr = corr;
        }

        float periodicity = (float)maxCorr / energy;
        return Math.Clamp(periodicity, 0f, 1f);
    }

    private static float ComputeSpeechProbability(ReadOnlySpan<short> frame, ReadOnlySpan<float> bandEnergies)
    {
        float periodicity = ComputePitchPeriodicity(frame);

        float maxEnergy = 0f;
        float sumEnergy = 0f;
        for (int b = 0; b < SubBandCount; b++)
        {
            float e = bandEnergies[b];
            if (e > maxEnergy) maxEnergy = e;
            sumEnergy += e;
        }

        float meanEnergy = sumEnergy / SubBandCount;
        float peakiness = maxEnergy / (meanEnergy + 1e-6f);

        // Vocal periodic pitch correlation is strong (> 0.40) for human speech & vocal formants
        // Random stationary noise (fans, mic hiss) has near-zero pitch correlation (< 0.15)
        float pitchScore = Math.Clamp((periodicity - 0.22f) / 0.30f, 0f, 1f);
        float peakScore = Math.Clamp((peakiness - 5.0f) / 5.0f, 0f, 1f);

        float speechScore = pitchScore > 0.25f ? Math.Max(pitchScore, peakScore) : peakScore * 0.2f;
        return speechScore;
    }
}
