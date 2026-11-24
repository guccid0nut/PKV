// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AudioSoftLimiter.cs
// System: ProjectKongor - High-Definition Opus Voice Pipeline
// Policy: Strict Explicit Typing (NO 'var') - High-Performance Audio Processing
// ============================================================================

using System;
using System.Collections.Generic;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     High-performance audio mixer and soft-knee peak limiter.
///     Summates multiple concurrent peer voice streams with configurable gain headroom (+0 dB to +12 dB)
///     and applies a hyperbolic tangent (tanh) soft-saturation curve to eliminate harsh digital clipping.
/// </summary>
public static class AudioSoftLimiter
{
    private const float MaxPcm16 = 32767.0f;
    private const float MinPcm16 = -32768.0f;
    private const float LinearKneeThreshold = 0.70f; // Linear response up to 70% full scale (-3.1 dBFS)

    /// <summary>
    ///     Converts a decibel gain value into a linear amplitude multiplier.
    /// </summary>
    public static float DbToLinear(float gainDb)
    {
        if (gainDb <= 0.0f) return 1.0f;
        return MathF.Pow(10.0f, gainDb / 20.0f);
    }

    /// <summary>
    ///     Applies soft-knee limiting to a single floating-point sample, guaranteeing the result
    ///     remains strictly clamped within the 16-bit signed integer PCM range [-32768, 32767].
    /// </summary>
    public static short LimitSample(float sample)
    {
        float normalized = sample / MaxPcm16;

        // Linear passthrough for signals within comfortable headroom
        if (MathF.Abs(normalized) <= LinearKneeThreshold)
        {
            float linearResult = sample;
            if (linearResult > MaxPcm16) return (short)MaxPcm16;
            if (linearResult < MinPcm16) return (short)MinPcm16;
            return (short)MathF.Round(linearResult);
        }

        // Smooth non-linear compression beyond knee threshold via tanh
        float sign = normalized < 0.0f ? -1.0f : 1.0f;
        float absNorm = MathF.Abs(normalized);
        float excess = absNorm - LinearKneeThreshold;
        float compressedExcess = (1.0f - LinearKneeThreshold) * MathF.Tanh(excess / (1.0f - LinearKneeThreshold));
        float limitedNorm = sign * (LinearKneeThreshold + compressedExcess);

        float limitedSample = limitedNorm * MaxPcm16;
        if (limitedSample > MaxPcm16) return (short)MaxPcm16;
        if (limitedSample < MinPcm16) return (short)MinPcm16;
        return (short)MathF.Round(limitedSample);
    }

    /// <summary>
    ///     Applies gain and soft-knee peak limiting in-place across a PCM sample buffer.
    /// </summary>
    public static void ProcessFrameInPlace(Span<short> buffer, float gainDb = 0.0f)
    {
        if (buffer.IsEmpty) return;

        float linearGain = DbToLinear(gainDb);

        for (int i = 0; i < buffer.Length; i++)
        {
            float boosted = buffer[i] * linearGain;
            buffer[i] = LimitSample(boosted);
        }
    }

    /// <summary>
    ///     Mixes multiple peer PCM streams into a single output buffer, applying summation gain
    ///     and soft-knee limiting without allocating heap memory.
    /// </summary>
    public static void MixAndLimit(
        IReadOnlyList<ReadOnlyMemory<short>> peerStreams,
        Span<short> outputBuffer,
        float gainDb = 0.0f)
    {
        if (outputBuffer.IsEmpty) return;

        outputBuffer.Clear();

        if (peerStreams.Count == 0) return;

        float linearGain = DbToLinear(gainDb);

        if (peerStreams.Count == 1)
        {
            ReadOnlySpan<short> singleStream = peerStreams[0].Span;
            int lengthToProcess = Math.Min(singleStream.Length, outputBuffer.Length);

            for (int i = 0; i < lengthToProcess; i++)
            {
                float boosted = singleStream[i] * linearGain;
                outputBuffer[i] = LimitSample(boosted);
            }
            return;
        }

        // Multi-stream summation with soft-knee limiter
        for (int i = 0; i < outputBuffer.Length; i++)
        {
            float sum = 0.0f;
            for (int p = 0; p < peerStreams.Count; p++)
            {
                ReadOnlySpan<short> peer = peerStreams[p].Span;
                if (i < peer.Length)
                {
                    sum += peer[i];
                }
            }

            float boostedSum = sum * linearGain;
            outputBuffer[i] = LimitSample(boostedSum);
        }
    }
}
