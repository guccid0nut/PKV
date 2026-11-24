// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpusVoiceCodec.cs
// System: ProjectKongor - Opus Voice Subsystem
// Policy: Strict Explicit Typing (NO 'var') - Pure Managed Concentus Audio Codec
// ============================================================================

using System;
using Concentus.Enums;
using Concentus.Structs;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     Encapsulates configuration options for the Opus voice encoder.
/// </summary>
public sealed class OpusVoiceOptions
{
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 1;
    public const int DefaultFrameSizeMs = 20;
    public const int DefaultBitrate = 28000; // 28 kbps optimal for voice clarity and low packet footprint

    public int SampleRate { get; init; } = DefaultSampleRate;
    public int Channels { get; init; } = DefaultChannels;
    public int FrameSizeMs { get; init; } = DefaultFrameSizeMs;
    public int Bitrate { get; init; } = DefaultBitrate;
    public int Complexity { get; init; } = 5; // 0 (fastest) to 10 (highest quality), 5 is sweet spot
    public bool UseInbandFec { get; init; } = true;
    public bool UseDtx { get; init; } = true; // Discontinuous Transmission for silence suppression

    /// <summary>Number of PCM samples per frame for a single channel (e.g. 960 samples for 20ms at 48 kHz).</summary>
    public int FrameSizeSamples => SampleRate * FrameSizeMs / 1000;
}

/// <summary>
///     Unified interface for managed Opus audio encoding, decoding, and packet loss concealment (PLC).
/// </summary>
public interface IOpusVoiceCodec : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    int FrameSizeSamples { get; }

    /// <summary>
    ///     Encodes a 16-bit linear PCM audio frame into an Opus compressed datagram.
    /// </summary>
    int Encode(ReadOnlySpan<short> pcmInput, Span<byte> opusOutput);

    /// <summary>
    ///     Decodes an Opus compressed datagram into 16-bit linear PCM samples.
    ///     Pass an empty or null payload to trigger Packet Loss Concealment (PLC).
    /// </summary>
    int Decode(ReadOnlySpan<byte> opusInput, Span<short> pcmOutput, bool decodeFec = false);

    /// <summary>
    ///     Resets internal encoder and decoder states.
    /// </summary>
    void Reset();
}

/// <summary>
///     High-performance managed Opus voice codec powered by Concentus.
/// </summary>
public sealed class OpusVoiceCodec : IOpusVoiceCodec
{
    private readonly OpusEncoder _encoder;
    private readonly OpusDecoder _decoder;
    private readonly OpusVoiceOptions _options;
    private readonly short[] _pcmEncodeScratch;
    private readonly short[] _pcmDecodeScratch;
    private readonly byte[] _opusScratch;
    private bool _isDisposed;

    public int SampleRate => _options.SampleRate;
    public int Channels => _options.Channels;
    public int FrameSizeSamples => _options.FrameSizeSamples;

    public OpusVoiceCodec(OpusVoiceOptions? options = null)
    {
        _options = options ?? new OpusVoiceOptions();

        _encoder = new OpusEncoder(
            _options.SampleRate,
            _options.Channels,
            OpusApplication.OPUS_APPLICATION_VOIP);

        _encoder.Bitrate = _options.Bitrate;
        _encoder.Complexity = _options.Complexity;
        _encoder.UseInbandFEC = _options.UseInbandFec;
        _encoder.UseDTX = _options.UseDtx;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;

        _decoder = new OpusDecoder(
            _options.SampleRate,
            _options.Channels);

        _pcmEncodeScratch = new short[_options.FrameSizeSamples * _options.Channels];
        _pcmDecodeScratch = new short[_options.FrameSizeSamples * _options.Channels];
        _opusScratch = new byte[1276];
    }

    public int Encode(ReadOnlySpan<short> pcmInput, Span<byte> opusOutput)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int frameSamples = _options.FrameSizeSamples * _options.Channels;
        if (pcmInput.Length < frameSamples)
        {
            throw new ArgumentException(
                $"PCM input has {pcmInput.Length} samples, but expected {frameSamples} for a {_options.FrameSizeMs}ms frame.",
                nameof(pcmInput));
        }

        lock (_encoder)
        {
            pcmInput[..frameSamples].CopyTo(_pcmEncodeScratch.AsSpan());
            int bytesEncoded = _encoder.Encode(
                _pcmEncodeScratch,
                0,
                _options.FrameSizeSamples,
                _opusScratch,
                0,
                _opusScratch.Length);

            if (bytesEncoded < 0)
            {
                throw new InvalidOperationException($"Opus encoding error code: {bytesEncoded}");
            }

            if (opusOutput.Length < bytesEncoded)
            {
                throw new ArgumentException(
                    $"Output span of length {opusOutput.Length} is too small for encoded payload of {bytesEncoded} bytes.",
                    nameof(opusOutput));
            }

            _opusScratch.AsSpan(0, bytesEncoded).CopyTo(opusOutput);
            return bytesEncoded;
        }
    }

    public int Decode(ReadOnlySpan<byte> opusInput, Span<short> pcmOutput, bool decodeFec = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int frameSamples = _options.FrameSizeSamples * _options.Channels;
        if (pcmOutput.Length < frameSamples)
        {
            throw new ArgumentException(
                $"PCM output span has {pcmOutput.Length} samples, but requires at least {frameSamples}.",
                nameof(pcmOutput));
        }

        lock (_decoder)
        {
            int samplesDecoded;
            if (opusInput.IsEmpty)
            {
                // Packet Loss Concealment (PLC) reconstruction
                samplesDecoded = _decoder.Decode(
                    null,
                    0,
                    0,
                    _pcmDecodeScratch,
                    0,
                    _options.FrameSizeSamples,
                    decodeFec);
            }
            else
            {
                opusInput.CopyTo(_opusScratch.AsSpan());
                samplesDecoded = _decoder.Decode(
                    _opusScratch,
                    0,
                    opusInput.Length,
                    _pcmDecodeScratch,
                    0,
                    _options.FrameSizeSamples,
                    decodeFec);
            }

            if (samplesDecoded < 0)
            {
                throw new InvalidOperationException($"Opus decoding error code: {samplesDecoded}");
            }

            _pcmDecodeScratch.AsSpan(0, samplesDecoded * _options.Channels).CopyTo(pcmOutput);
            return samplesDecoded * _options.Channels;
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_encoder)
        {
            _encoder.ResetState();
        }

        lock (_decoder)
        {
            _decoder.ResetState();
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
    }
}
