// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: BinauralSpatialAudioSynthesizer.cs
// System: WILLOWMAKER Launcher - Spatial Audio & HRTF Modernization Engine
// Policy: Strict Explicit Typing (NO 'var') - Pure In-Memory Zero-Dependency Audio
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Configurable acoustic parameters for binaural spatial synthesis and auditioning.
/// </summary>
public record SpatialAudioOptions(
    string HrtfProfile = "Built-In 48000hz",
    int Channels = 128,
    bool OcclusionDamping = true);

/// <summary>
///     Self-contained binaural HRTF acoustic synthesizer that generates in-memory 16-bit
///     PCM WAV streams with Interaural Time Difference (ITD), Interaural Level Difference (ILD),
///     contralateral head-shadowing lowpass filters, Biquad ear-profile equalization, and
///     multi-channel polyphonic room reflections. Enables zero-dependency headphone auditioning.
/// </summary>
public static class BinauralSpatialAudioSynthesizer
{
    private const int SampleRate = 44100;
    private const short Channels = 2;
    private const short BitsPerSample = 16;
    private const int MaxDelaySamples = 29; // ~0.65ms max ITD across average human head (diameter ~17.5cm)

    private const int SND_ASYNC = 0x0001;
    private const int SND_NODEFAULT = 0x0002;
    private const int SND_MEMORY = 0x0004;
    private const int SND_LOOP = 0x0008;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundA", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundNative(byte[]? pszSound, nint hmod, int fdwSound);

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

    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint WHDR_DONE = 0x00000001;
    private const int MMSYSERR_NOERROR = 0;

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out nint hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, nint dwCallback, nint dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(nint hWaveOut, nint lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(nint hWaveOut, nint lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(nint hWaveOut, nint lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(nint hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(nint hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    /// <summary>
    ///     Checks if at least one audio output device is present on the host system to prevent crashes in headless/CI environments.
    /// </summary>
    public static bool HasAudioOutputDevice()
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
    ///     Optional integration point to route listener spatial coordinates to the underlying
    ///     NEXUS.SpatialAudio OpenAL engine when initialized.
    /// </summary>
    public static void SyncToHardwareEngine(NEXUS.SpatialAudio.SpatialAudioEngine? audioEngine, float azimuthDegrees, float distance)
    {
        if (audioEngine == null || !audioEngine.IsInitialized) return;
        float azimuthRad = azimuthDegrees * (MathF.PI / 180f);
        float x = MathF.Sin(azimuthRad) * distance;
        float z = -MathF.Cos(azimuthRad) * distance;
        audioEngine.SetListenerPose(
            new System.Numerics.Vector3(x, 0f, z),
            new System.Numerics.Vector3(0f, 0f, -1f),
            new System.Numerics.Vector3(0f, 1f, 0f));
    }

    // Cached mono audio samples for instant real-time spatialization during live radar dragging
    private static float[]? _cachedCanYouFeelMyHeartMono;
    private static readonly Lock _cacheLock = new();

    // Continuous Real-Time Audio Streaming State
    private static readonly Lock _streamLock = new();
    private static volatile bool _isStreaming = false;
    private static CancellationTokenSource? _streamingCts;
    private static Thread? _streamingThread;
    private static float[]? _activeStreamMono;
    private static string _activeStreamTrackKey = string.Empty;
    private static volatile float _dynamicAzimuth = 0f;
    private static volatile float _dynamicDistance = 1f;
    private static SpatialAudioOptions _dynamicOptions = new();

    /// <summary>
    ///     True if the real-time continuous audio streaming thread is currently active.
    /// </summary>
    public static bool IsContinuousStreamingActive => _isStreaming;

    /// <summary>
    ///     Stops any currently playing in-memory audio or continuous stream immediately.
    /// </summary>
    public static void StopPlayback()
    {
        StopContinuousStream();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                PlaySoundNative(null, nint.Zero, 0);
            }
            catch
            {
                // Non-critical
            }
        }
    }

    /// <summary>
    ///     Stops active continuous streaming.
    /// </summary>
    public static void StopContinuousStream()
    {
        lock (_streamLock)
        {
            if (!_isStreaming) return;
            _isStreaming = false;
            try
            {
                _streamingCts?.Cancel();
                _streamingThread?.Join(300);
            }
            catch
            {
                // Non-critical
            }
            finally
            {
                _streamingCts = null;
                _streamingThread = null;
            }
        }
    }

    /// <summary>
    ///     Updates the listener azimuth angle, distance, and acoustic options for the active
    ///     continuous audio stream smoothly in real time without restarting from the beginning.
    /// </summary>
    public static void UpdateContinuousPosition(float azimuthDegrees, float distance, SpatialAudioOptions? options = null)
    {
        _dynamicAzimuth = azimuthDegrees;
        _dynamicDistance = distance;
        if (options != null) _dynamicOptions = options;
    }

    /// <summary>
    ///     Begins continuous real-time binaural spatial audio streaming. If the same track is already streaming,
    ///     updates coordinates and acoustics without restarting the playback timeline.
    /// </summary>
    public static void StartContinuousStream(string trackKey, float azimuthDegrees, float distance, SpatialAudioOptions options)
    {
        if (!OperatingSystem.IsWindows() || !HasAudioOutputDevice()) return;

        lock (_streamLock)
        {
            _dynamicAzimuth = azimuthDegrees;
            _dynamicDistance = distance;
            _dynamicOptions = options;

            // If the stream is already actively playing the same track, do not restart!
            if (_isStreaming && string.Equals(_activeStreamTrackKey, trackKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Stop any existing stream or one-shot
            StopContinuousStream();
            if (OperatingSystem.IsWindows())
            {
                try { PlaySoundNative(null, nint.Zero, 0); } catch { }
            }

            // Load mono source for the requested track
            float[]? monoSource;
            if (trackKey.Contains("Heart", StringComparison.OrdinalIgnoreCase) ||
                trackKey.Contains("Song", StringComparison.OrdinalIgnoreCase))
            {
                monoSource = GetOrLoadCanYouFeelMyHeartSamples();
            }
            else if (trackKey.Contains("Spell", StringComparison.OrdinalIgnoreCase) ||
                     trackKey.Contains("Pyromancer", StringComparison.OrdinalIgnoreCase) ||
                     trackKey.Contains("Cast", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("pyromancer_dragon_stun.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("Attack", StringComparison.OrdinalIgnoreCase) ||
                     trackKey.Contains("Hammer", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("hammerstorm_cleave.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("Kongor", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("kongor_slain.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("Denied", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("denied.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("Smackdown", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("smackdown.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("First Blood", StringComparison.OrdinalIgnoreCase) ||
                     trackKey.Contains("Blood", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("first_blood.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("Double Kill", StringComparison.OrdinalIgnoreCase))
            {
                string? p = ResolveAudioFilePath("double_kill.wav");
                monoSource = p != null ? LoadPcmWavSamples(p) : null;
            }
            else if (trackKey.Contains("Chime", StringComparison.OrdinalIgnoreCase) ||
                     trackKey.Contains("Directional", StringComparison.OrdinalIgnoreCase))
            {
                monoSource = GenerateChimeMonoSamples();
            }
            else if (trackKey.Contains("Orbit", StringComparison.OrdinalIgnoreCase) ||
                     trackKey.Contains("Sweep", StringComparison.OrdinalIgnoreCase))
            {
                monoSource = GenerateOrbitMonoSamples();
            }
            else
            {
                monoSource = GetOrLoadCanYouFeelMyHeartSamples();
            }

            if (monoSource == null || monoSource.Length == 0) return;

            _activeStreamMono = monoSource;
            _activeStreamTrackKey = trackKey;
            _isStreaming = true;
            _streamingCts = new CancellationTokenSource();

            _streamingThread = new Thread(StreamingLoop)
            {
                IsBackground = true,
                Name = "WillowmakerContinuousSpatialStream"
            };
            _streamingThread.Start();
        }
    }

    private static void StreamingLoop()
    {
        if (!OperatingSystem.IsWindows() || !HasAudioOutputDevice())
        {
            _isStreaming = false;
            return;
        }

        WAVEFORMATEX wfx = new()
        {
            wFormatTag = 1,
            nChannels = (ushort)Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = (uint)(SampleRate * Channels * (BitsPerSample / 8)),
            nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
            wBitsPerSample = (ushort)BitsPerSample,
            cbSize = 0
        };

        int openResult = waveOutOpen(out nint hWaveOut, WAVE_MAPPER, ref wfx, nint.Zero, nint.Zero, 0);
        if (openResult != MMSYSERR_NOERROR || hWaveOut == nint.Zero)
        {
            _isStreaming = false;
            return;
        }

        const int framesPerBuffer = 2205; // 50ms at 44.1kHz
        const int bytesPerBuffer = framesPerBuffer * 4; // 16-bit stereo = 4 bytes per frame
        const int numBuffers = 4;
        int waveHdrSize = Marshal.SizeOf<WAVEHDR>();

        nint[] dataBuffers = new nint[numBuffers];
        nint[] hdrBuffers = new nint[numBuffers];
        bool[] isQueued = new bool[numBuffers];

        for (int i = 0; i < numBuffers; i++)
        {
            dataBuffers[i] = Marshal.AllocHGlobal(bytesPerBuffer);
            hdrBuffers[i] = Marshal.AllocHGlobal(waveHdrSize);
            byte[] zeroBytes = new byte[bytesPerBuffer];
            Marshal.Copy(zeroBytes, 0, dataBuffers[i], bytesPerBuffer);

            WAVEHDR hdr = new()
            {
                lpData = dataBuffers[i],
                dwBufferLength = (uint)bytesPerBuffer,
                dwFlags = 0
            };
            Marshal.StructureToPtr(hdr, hdrBuffers[i], false);
        }

        int cursor = 0;
        float[] monoSlice = new float[framesPerBuffer];
        float[] leftSlice = new float[framesPerBuffer];
        float[] rightSlice = new float[framesPerBuffer];
        byte[] pcmBytes = new byte[bytesPerBuffer];

        float filterL = 0f;
        float filterR = 0f;
        float occlL = 0f;
        float occlR = 0f;
        BiquadFilter biquadL = default;
        BiquadFilter biquadR = default;
        string lastProfile = string.Empty;

        try
        {
            while (_isStreaming && _streamingCts is { IsCancellationRequested: false })
            {
                float[]? monoSource = _activeStreamMono;
                if (monoSource == null || monoSource.Length == 0) break;

                bool wroteAny = false;

                for (int b = 0; b < numBuffers; b++)
                {
                    WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrBuffers[b]);

                    if (!isQueued[b] || (hdr.dwFlags & WHDR_DONE) != 0)
                    {
                        if (isQueued[b])
                        {
                            waveOutUnprepareHeader(hWaveOut, hdrBuffers[b], (uint)waveHdrSize);
                            isQueued[b] = false;
                        }

                        float azDeg = _dynamicAzimuth;
                        float dist = _dynamicDistance;
                        if (_activeStreamTrackKey.Contains("Orbit", StringComparison.OrdinalIgnoreCase) ||
                            _activeStreamTrackKey.Contains("Sweep", StringComparison.OrdinalIgnoreCase))
                        {
                            _dynamicAzimuth = (_dynamicAzimuth + 9.0f) % 360f;
                            azDeg = _dynamicAzimuth;
                        }
                        SpatialAudioOptions opts = _dynamicOptions ?? new SpatialAudioOptions();

                        float clampedDistance = Math.Clamp(dist, 0.0f, 2.0f);
                        float distanceGain = clampedDistance > 0.05f ? 1.0f / MathF.Sqrt(Math.Max(0.3f, clampedDistance)) : 1.2f;

                        int srcLen = monoSource.Length;
                        for (int i = 0; i < framesPerBuffer; i++)
                        {
                            monoSlice[i] = monoSource[(cursor + i) % srcLen] * distanceGain;
                        }
                        cursor = (cursor + framesPerBuffer) % srcLen;

                        RenderStreamingBlock(
                            monoSlice,
                            azDeg,
                            clampedDistance,
                            opts,
                            leftSlice,
                            rightSlice,
                            ref filterL,
                            ref filterR,
                            ref occlL,
                            ref occlR,
                            ref biquadL,
                            ref biquadR,
                            ref lastProfile);

                        for (int i = 0; i < framesPerBuffer; i++)
                        {
                            short sampleL = (short)Math.Clamp((int)(leftSlice[i] * 32767f), short.MinValue, short.MaxValue);
                            short sampleR = (short)Math.Clamp((int)(rightSlice[i] * 32767f), short.MinValue, short.MaxValue);

                            int byteIdx = i * 4;
                            pcmBytes[byteIdx] = (byte)(sampleL & 0xFF);
                            pcmBytes[byteIdx + 1] = (byte)((sampleL >> 8) & 0xFF);
                            pcmBytes[byteIdx + 2] = (byte)(sampleR & 0xFF);
                            pcmBytes[byteIdx + 3] = (byte)((sampleR >> 8) & 0xFF);
                        }

                        Marshal.Copy(pcmBytes, 0, dataBuffers[b], bytesPerBuffer);

                        hdr.lpData = dataBuffers[b];
                        hdr.dwBufferLength = (uint)bytesPerBuffer;
                        hdr.dwFlags = 0;
                        Marshal.StructureToPtr(hdr, hdrBuffers[b], false);

                        waveOutPrepareHeader(hWaveOut, hdrBuffers[b], (uint)waveHdrSize);
                        waveOutWrite(hWaveOut, hdrBuffers[b], (uint)waveHdrSize);
                        isQueued[b] = true;
                        wroteAny = true;
                    }
                }

                if (!wroteAny)
                {
                    _streamingCts?.Token.WaitHandle.WaitOne(12);
                }
                else
                {
                    _streamingCts?.Token.WaitHandle.WaitOne(4);
                }
            }
        }
        catch
        {
            // Non-critical
        }
        finally
        {
            waveOutReset(hWaveOut);

            for (int i = 0; i < numBuffers; i++)
            {
                if (isQueued[i])
                {
                    try { waveOutUnprepareHeader(hWaveOut, hdrBuffers[i], (uint)waveHdrSize); } catch { }
                }
                Marshal.FreeHGlobal(dataBuffers[i]);
                Marshal.FreeHGlobal(hdrBuffers[i]);
            }

            waveOutClose(hWaveOut);
            _isStreaming = false;
        }
    }

    private static void RenderStreamingBlock(
        float[] monoSlice,
        float azDeg,
        float distance,
        SpatialAudioOptions options,
        float[] leftOut,
        float[] rightOut,
        ref float filterL,
        ref float filterR,
        ref float occlL,
        ref float occlR,
        ref BiquadFilter biquadL,
        ref BiquadFilter biquadR,
        ref string lastProfile)
    {
        int length = monoSlice.Length;
        float azimuthRad = azDeg * (MathF.PI / 180f);

        bool isCenter = distance < 0.06f;
        float sinAz = isCenter ? 0f : MathF.Sin(azimuthRad);
        float cosAz = isCenter ? 1f : MathF.Cos(azimuthRad);

        bool isCompetitive = options.HrtfProfile.Contains("Competitive", StringComparison.OrdinalIgnoreCase);
        bool isCinematic = options.HrtfProfile.Contains("Cinematic", StringComparison.OrdinalIgnoreCase);

        // ILD Calculation
        float ildSin = isCompetitive ? Math.Clamp(sinAz * 1.30f, -1.0f, 1.0f) : sinAz;
        float effectiveIld = ildSin * Math.Clamp(distance, 0f, 1f);
        float gainL = MathF.Sqrt(Math.Max(0.04f, (1.0f - effectiveIld) * 0.5f));
        float gainR = MathF.Sqrt(Math.Max(0.04f, (1.0f + effectiveIld) * 0.5f));

        if (cosAz < 0 && distance > 0.1f)
        {
            gainL *= 0.80f;
            gainR *= 0.80f;
        }

        // ITD Woodworth model
        int maxDelay = isCinematic ? 36 : (isCompetitive ? 27 : MaxDelaySamples);
        int delaySamples = isCenter ? 0 : (int)(MathF.Abs(sinAz * Math.Clamp(distance, 0f, 1f)) * maxDelay);

        float alpha = isCompetitive ? 0.55f : 0.65f;
        bool applyOcclusion = options.OcclusionDamping && !isCenter && (cosAz < -0.1f || MathF.Abs(sinAz) > 0.5f);
        float occlAlpha = 0.50f;

        // Maintain Biquad filters if profile changed
        if (options.HrtfProfile != lastProfile)
        {
            biquadL = isCompetitive
                ? BiquadFilter.CreatePeakingEq(SampleRate, 4500f, 1.4f, 5.5f)
                : isCinematic
                    ? BiquadFilter.CreateLowShelf(SampleRate, 220f, 4.0f)
                    : default;
            biquadR = isCompetitive
                ? BiquadFilter.CreatePeakingEq(SampleRate, 4500f, 1.4f, 5.5f)
                : isCinematic
                    ? BiquadFilter.CreateLowShelf(SampleRate, 220f, 4.0f)
                    : default;
            lastProfile = options.HrtfProfile;
        }

        for (int i = 0; i < length; i++)
        {
            float source = monoSlice[i];
            float curL;
            float curR;

            if (sinAz >= 0) // Sound on Right (Left ear delayed and filtered)
            {
                curR = source * gainR;

                int delayedIdx = i - delaySamples;
                float leftRaw = delayedIdx >= 0 ? monoSlice[delayedIdx] * gainL : source * gainL * 0.5f;
                filterL = (1.0f - alpha) * leftRaw + alpha * filterL;
                curL = filterL;
            }
            else // Sound on Left (Right ear delayed and filtered)
            {
                curL = source * gainL;

                int delayedIdx = i - delaySamples;
                float rightRaw = delayedIdx >= 0 ? monoSlice[delayedIdx] * gainR : source * gainR * 0.5f;
                filterR = (1.0f - alpha) * rightRaw + alpha * filterR;
                curR = filterR;
            }

            if (applyOcclusion)
            {
                occlL = (1.0f - occlAlpha) * curL + occlAlpha * occlL;
                occlR = (1.0f - occlAlpha) * curR + occlAlpha * occlR;
                curL = occlL * 0.85f;
                curR = occlR * 0.85f;
            }

            if (isCompetitive || isCinematic)
            {
                curL = biquadL.Process(curL);
                curR = biquadR.Process(curR);
            }

            leftOut[i] = curL;
            rightOut[i] = curR;
        }

        ApplyChannelPolyphonyReflections(leftOut, rightOut, options.Channels);
    }

    /// <summary>
    ///     Plays raw WAV bytes asynchronously (optionally looping).
    /// </summary>
    public static void PlayWavBytes(byte[] wavBytes, bool loop = false)
    {
        if (!OperatingSystem.IsWindows() || !HasAudioOutputDevice()) return;

        try
        {
            int flags = SND_ASYNC | SND_MEMORY | SND_NODEFAULT;
            if (loop) flags |= SND_LOOP;
            PlaySoundNative(wavBytes, nint.Zero, flags);
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    ///     Resolves audio file path across working directory, application base directory, and project assets.
    /// </summary>
    public static string? ResolveAudioFilePath(string fileName)
    {
        string current = Directory.GetCurrentDirectory();
        string appBase = AppDomain.CurrentDomain.BaseDirectory;

        string[] searchPaths =
        [
            Path.Combine(current, "assets", "audio", fileName),
            Path.Combine(current, "src", "apps", "willowmaker", "Assets", "audio", fileName),
            Path.Combine(current, "games", "client", "assets", "audio", fileName),
            Path.Combine(appBase, "Assets", "audio", fileName),
            Path.Combine(appBase, "assets", "audio", fileName),
            Path.Combine(appBase, "..", "..", "..", "Assets", "audio", fileName),
            Path.Combine(appBase, "..", "..", "..", "assets", "audio", fileName),
            Path.Combine(appBase, "..", "..", "..", "..", "..", "assets", "audio", fileName)
        ];

        foreach (string p in searchPaths)
        {
            try
            {
                string fullPath = Path.GetFullPath(p);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch
            {
                // Ignore path resolution exceptions
            }
        }

        return null;
    }

    /// <summary>
    ///     Synthesizes and plays a localized 3D binaural chime at a specific horizontal azimuth angle.
    /// </summary>
    public static void PlayDirectionalChime(float azimuthDegrees, float distance = 1.0f, SpatialAudioOptions? options = null)
    {
        Task.Run(() =>
        {
            byte[] wavBytes = GenerateDirectionalWav(azimuthDegrees, distance, options);
            PlayWavBytes(wavBytes, loop: false);
        });
    }

    /// <summary>
    ///     Synthesizes and plays a continuous 360-degree orbital audio sweep that smoothly
    ///     circles around the listener's head (Front -> Right -> Behind -> Left -> Front).
    /// </summary>
    public static void Play3DOrbitSweep(SpatialAudioOptions? options = null)
    {
        Task.Run(() =>
        {
            byte[] wavBytes = GenerateOrbitWav(options);
            PlayWavBytes(wavBytes, loop: false);
        });
    }

    /// <summary>
    ///     Generates a 3D binaural reference chime WAV at the specified azimuth angle and distance.
    /// </summary>
    public static byte[] GenerateDirectionalWav(float azimuthDegrees, float distance = 1.0f, SpatialAudioOptions? options = null)
    {
        float azimuthRad = azimuthDegrees * (MathF.PI / 180f);
        float clampedDistance = Math.Clamp(distance, 0.3f, 2.0f);
        float distanceGain = 1.0f / MathF.Sqrt(clampedDistance);

        int durationMs = 380;
        int totalSamples = SampleRate * durationMs / 1000;

        float[] monoSource = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;
            float attack = Math.Min(1.0f, t / 0.005f); // 5ms attack
            float decay = MathF.Exp(-9.0f * t);        // Exponential decay

            // Pleasing harmonic chime: Fundamental (659.25 Hz, E5) + Harmonic (1318.5 Hz, E6)
            float wave1 = MathF.Sin(2.0f * MathF.PI * 659.25f * t);
            float wave2 = 0.45f * MathF.Sin(2.0f * MathF.PI * 1318.5f * t);
            float wave3 = 0.2f * MathF.Sin(2.0f * MathF.PI * 1975.5f * t); // B6 fifth

            monoSource[i] = (wave1 + wave2 + wave3) * attack * decay * distanceGain * 0.75f;
        }

        return RenderBinauralStereoWav(monoSource, azimuthRad, options);
    }

    /// <summary>
    ///     Loads or synthesizes in-game spell casting audio: Pyromancer Dragon Stun
    ///     (rising arcane flame charge + explosive fire burst + resonant low-end body).
    /// </summary>
    public static byte[] GenerateSpellCastWav(float azimuthDegrees, float distance = 1.0f, SpatialAudioOptions? options = null)
    {
        float azimuthRad = azimuthDegrees * (MathF.PI / 180f);
        float clampedDistance = Math.Clamp(distance, 0.3f, 2.0f);
        float distanceGain = 1.0f / MathF.Sqrt(clampedDistance);

        string? audioPath = ResolveAudioFilePath("pyromancer_dragon_stun.wav");
        if (audioPath != null)
        {
            float[]? fileSamples = LoadPcmWavSamples(audioPath, maxSeconds: 5f);
            if (fileSamples != null && fileSamples.Length > 0)
            {
                if (Math.Abs(distanceGain - 1.0f) > 0.01f)
                {
                    for (int i = 0; i < fileSamples.Length; i++) fileSamples[i] *= distanceGain;
                }
                return RenderBinauralStereoWav(fileSamples, azimuthRad, options);
            }
        }

        int durationMs = 650;
        int totalSamples = SampleRate * durationMs / 1000;
        float[] monoSource = new float[totalSamples];
        Random rng = new(42);

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;

            // 1. Initial arcane charge whoosh (0 to 220ms): frequency sweep 260Hz -> 820Hz
            float chargeEnvelope = t < 0.22f ? MathF.Sin(t / 0.22f * (MathF.PI / 2f)) : 0f;
            float chargeFreq = 260f + (t / 0.22f) * 560f;
            float chargeWave = MathF.Sin(2f * MathF.PI * chargeFreq * t) * chargeEnvelope * 0.45f;

            // 2. Fire burst explosion (at 180ms): filtered burst noise
            float burstTime = Math.Max(0f, t - 0.18f);
            float burstEnv = MathF.Exp(-16f * burstTime);
            float noise = ((float)rng.NextDouble() * 2f - 1f) * burstEnv * 0.65f;

            // 3. Resonant sub-bass thud (at 190ms): 75Hz decay
            float thudTime = Math.Max(0f, t - 0.19f);
            float thudEnv = MathF.Exp(-7f * thudTime);
            float thud = MathF.Sin(2f * MathF.PI * 75f * thudTime) * thudEnv * 0.85f;

            monoSource[i] = (chargeWave + noise + thud) * distanceGain * 0.70f;
        }

        return RenderBinauralStereoWav(monoSource, azimuthRad, options);
    }

    /// <summary>
    ///     Loads or synthesizes in-game melee attack & impact audio: Hammerstorm Cleave
    ///     (sharp high-frequency steel weapon slash + heavy metallic crash + crushing thump).
    /// </summary>
    public static byte[] GenerateMeleeAttackWav(float azimuthDegrees, float distance = 1.0f, SpatialAudioOptions? options = null)
    {
        float azimuthRad = azimuthDegrees * (MathF.PI / 180f);
        float clampedDistance = Math.Clamp(distance, 0.3f, 2.0f);
        float distanceGain = 1.0f / MathF.Sqrt(clampedDistance);

        string? audioPath = ResolveAudioFilePath("hammerstorm_cleave.wav");
        if (audioPath != null)
        {
            float[]? fileSamples = LoadPcmWavSamples(audioPath, maxSeconds: 5f);
            if (fileSamples != null && fileSamples.Length > 0)
            {
                if (Math.Abs(distanceGain - 1.0f) > 0.01f)
                {
                    for (int i = 0; i < fileSamples.Length; i++) fileSamples[i] *= distanceGain;
                }
                return RenderBinauralStereoWav(fileSamples, azimuthRad, options);
            }
        }

        int durationMs = 520;
        int totalSamples = SampleRate * durationMs / 1000;
        float[] monoSource = new float[totalSamples];
        Random rng = new(99);

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;

            // 1. Blade swoosh (0 to 140ms): descending whoosh noise
            float swooshEnv = t < 0.14f ? MathF.Sin(t / 0.14f * MathF.PI) : 0f;
            float swooshNoise = ((float)rng.NextDouble() * 2f - 1f) * swooshEnv * 0.4f;

            // 2. Heavy steel impact crash (at 110ms): metallic resonance at 1.8kHz, 3.2kHz
            float impactTime = Math.Max(0f, t - 0.11f);
            float impactEnv = MathF.Exp(-22f * impactTime);
            float metallicClang = (MathF.Sin(2f * MathF.PI * 1820f * impactTime) +
                                   0.5f * MathF.Sin(2f * MathF.PI * 3240f * impactTime) +
                                   0.3f * MathF.Sin(2f * MathF.PI * 4680f * impactTime)) * impactEnv * 0.55f;

            // 3. Heavy crushing body thud (at 115ms): 62Hz punch
            float thudTime = Math.Max(0f, t - 0.115f);
            float thudEnv = MathF.Exp(-8.5f * thudTime);
            float thud = MathF.Sin(2f * MathF.PI * 62f * thudTime) * thudEnv * 0.9f;

            monoSource[i] = (swooshNoise + metallicClang + thud) * distanceGain * 0.72f;
        }

        return RenderBinauralStereoWav(monoSource, azimuthRad, options);
    }

    /// <summary>
    ///     Loads or synthesizes announcer voice lines (Bloodlust, Double Kill, Kongor Slain),
    ///     spatializing them at the target azimuth and distance.
    /// </summary>
    public static byte[] GenerateAnnouncerWav(string soundKey, float azimuthDegrees, float distance = 1.0f, SpatialAudioOptions? options = null)
    {
        float azimuthRad = azimuthDegrees * (MathF.PI / 180f);
        float clampedDistance = Math.Clamp(distance, 0.3f, 2.0f);
        float distanceGain = 1.0f / MathF.Sqrt(clampedDistance);

        string candidateFileName = soundKey.ToLowerInvariant() switch
        {
            "bloodlust" or "first_blood" => "first_blood.wav",
            "double_kill" => "double_kill.wav",
            "smackdown" => "smackdown.wav",
            "denied" => "denied.wav",
            "kongor_slain" => "kongor_slain.wav",
            _ => "first_blood.wav"
        };

        string? audioPath = ResolveAudioFilePath(candidateFileName);
        if (audioPath != null)
        {
            float[]? fileSamples = LoadPcmWavSamples(audioPath, maxSeconds: 8f);
            if (fileSamples != null && fileSamples.Length > 0)
            {
                if (Math.Abs(distanceGain - 1.0f) > 0.01f)
                {
                    for (int i = 0; i < fileSamples.Length; i++) fileSamples[i] *= distanceGain;
                }
                return RenderBinauralStereoWav(fileSamples, azimuthRad, options);
            }
        }

        // Procedural resonant voice formant fallback
        int durationMs = 700;
        int totalSamples = SampleRate * durationMs / 1000;
        float[] monoSource = new float[totalSamples];

        float pitch = soundKey.Contains("double", StringComparison.OrdinalIgnoreCase) ? 140f : 110f;
        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;
            float env = MathF.Sin(Math.Min(1f, t / 0.65f) * MathF.PI);
            float f0 = MathF.Sin(2f * MathF.PI * pitch * t);
            float f1 = 0.6f * MathF.Sin(2f * MathF.PI * (pitch * 3f) * t);
            float f2 = 0.4f * MathF.Sin(2f * MathF.PI * (pitch * 5f) * t);
            monoSource[i] = (f0 + f1 + f2) * env * distanceGain * 0.65f;
        }

        return RenderBinauralStereoWav(monoSource, azimuthRad, options);
    }

    /// <summary>
    ///     Generates the looping "Can You Feel My Heart" audio track spatialized to the
    ///     specified azimuth angle and distance. Loads from master audio or falls back to melodic synth hook.
    /// </summary>
    public static byte[] GenerateCanYouFeelMyHeartWav(float azimuthDegrees, float distance = 1.0f, SpatialAudioOptions? options = null)
    {
        float azimuthRad = azimuthDegrees * (MathF.PI / 180f);
        float clampedDistance = Math.Clamp(distance, 0.3f, 2.0f);
        float distanceGain = 1.0f / MathF.Sqrt(clampedDistance);

        float[] monoSamples = GetOrLoadCanYouFeelMyHeartSamples();
        float[] scaledSamples = new float[monoSamples.Length];
        for (int i = 0; i < monoSamples.Length; i++)
        {
            scaledSamples[i] = monoSamples[i] * distanceGain;
        }

        return RenderBinauralStereoWav(scaledSamples, azimuthRad, options);
    }

    private static float[] GenerateChimeMonoSamples()
    {
        int durationMs = 1200;
        int totalSamples = SampleRate * durationMs / 1000;
        float[] monoSource = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;
            if (t < 0.5f)
            {
                float attack = Math.Min(1.0f, t / 0.005f);
                float decay = MathF.Exp(-9.0f * t);
                float wave1 = MathF.Sin(2.0f * MathF.PI * 659.25f * t);
                float wave2 = 0.45f * MathF.Sin(2.0f * MathF.PI * 1318.5f * t);
                float wave3 = 0.2f * MathF.Sin(2.0f * MathF.PI * 1975.5f * t);
                monoSource[i] = (wave1 + wave2 + wave3) * attack * decay * 0.75f;
            }
        }
        return monoSource;
    }

    private static float[] GenerateOrbitMonoSamples()
    {
        float durationSec = 2.0f;
        int totalSamples = (int)(SampleRate * durationSec);
        float[] mono = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;
            float pulseTime = (t * 4.0f) % 1.0f;
            float attack = Math.Min(1.0f, pulseTime / 0.008f);
            float decay = MathF.Exp(-7.5f * pulseTime);

            float chime1 = MathF.Sin(2.0f * MathF.PI * 784f * t); // G5
            float chime2 = 0.5f * MathF.Sin(2.0f * MathF.PI * 1568f * t); // G6
            float chime3 = 0.25f * MathF.Sin(2.0f * MathF.PI * 1975.5f * t); // B6

            float carrierFreq = 587.33f + (MathF.Sin((t / durationSec) * 2f * MathF.PI) * 40.0f);
            float wave = (MathF.Sin(2.0f * MathF.PI * carrierFreq * t) +
                          0.35f * MathF.Sin(2.0f * MathF.PI * carrierFreq * 2.0f * t)) * attack * decay * 0.65f;

            mono[i] = ((chime1 + chime2 + chime3) * 0.35f + wave) * attack * decay * 0.75f;
        }
        return mono;
    }

    private static float[] GetOrLoadCanYouFeelMyHeartSamples()
    {
        lock (_cacheLock)
        {
            if (_cachedCanYouFeelMyHeartMono != null && _cachedCanYouFeelMyHeartMono.Length > 0)
            {
                return _cachedCanYouFeelMyHeartMono;
            }

            string[] candidateFiles = ["can_you_feel_my_heart_cover.wav", "can_you_feel_my_heart.wav"];
            foreach (string fileName in candidateFiles)
            {
                string? resolved = ResolveAudioFilePath(fileName);
                if (resolved != null)
                {
                    // Load full song track (up to 10 minutes)
                    float[]? loaded = LoadPcmWavSamples(resolved, maxSeconds: 600.0f);
                    if (loaded != null && loaded.Length > 0)
                    {
                        _cachedCanYouFeelMyHeartMono = loaded;
                        return loaded;
                    }
                }
            }

            // High-fidelity synthesized melodic hook fallback ("Can You Feel My Heart" Bring Me The Horizon riff)
            float bpm = 128f;
            float beatSec = 60f / bpm;
            float totalDuration = beatSec * 16f; // 4 bars = 7.5 seconds
            int totalSamples = (int)(SampleRate * totalDuration);
            float[] synthHook = new float[totalSamples];

            // Melodic progression: E minor / C / G / D riff (E4, G4, B4, C5, B4, G4)
            float[] notePitches = [329.63f, 392.00f, 493.88f, 523.25f, 493.88f, 392.00f, 329.63f, 293.66f];

            for (int i = 0; i < totalSamples; i++)
            {
                float t = (float)i / SampleRate;
                int noteIndex = (int)(t / (beatSec * 0.5f)) % notePitches.Length;
                float freq = notePitches[noteIndex];

                float noteTime = t % (beatSec * 0.5f);
                float attack = Math.Min(1f, noteTime / 0.015f);
                float decay = MathF.Exp(-3.8f * noteTime);

                // Heavy synth saw + pulse wave characteristic of the track
                float saw = 2f * ((t * freq) - MathF.Floor(0.5f + (t * freq)));
                float sub = MathF.Sin(2f * MathF.PI * (freq * 0.5f) * t) * 0.6f;
                float oct = MathF.Sin(2f * MathF.PI * (freq * 2f) * t) * 0.35f;

                synthHook[i] = (saw * 0.45f + sub + oct) * attack * decay * 0.42f;
            }

            _cachedCanYouFeelMyHeartMono = synthHook;
            return synthHook;
        }
    }

    public static float[]? LoadPcmWavSamples(string filePath, float maxSeconds = 16f)
    {
        try
        {
            using FileStream fs = File.OpenRead(filePath);
            using BinaryReader reader = new(fs);

            // Read RIFF
            string riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (riff != "RIFF") return null;
            reader.ReadInt32(); // ChunkSize
            string wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (wave != "WAVE") return null;

            short channels = 1;
            int fileSampleRate = 44100;
            short bits = 16;
            byte[]? dataBytes = null;

            while (fs.Position < fs.Length)
            {
                byte[] chunkIdBytes = reader.ReadBytes(4);
                if (chunkIdBytes.Length < 4) break;
                string chunkId = Encoding.ASCII.GetString(chunkIdBytes);
                int chunkSize = reader.ReadInt32();

                if (chunkId == "fmt ")
                {
                    reader.ReadInt16(); // audio format
                    channels = reader.ReadInt16();
                    fileSampleRate = reader.ReadInt32();
                    reader.ReadInt32(); // byteRate
                    reader.ReadInt16(); // blockAlign
                    bits = reader.ReadInt16();
                    int extra = chunkSize - 16;
                    if (extra > 0) fs.Seek(extra, SeekOrigin.Current);
                }
                else if (chunkId == "data")
                {
                    int maxBytesToRead = (int)(fileSampleRate * channels * (bits / 8) * maxSeconds);
                    int readLen = Math.Min(chunkSize, maxBytesToRead);
                    dataBytes = reader.ReadBytes(readLen);
                    break;
                }
                else
                {
                    fs.Seek(chunkSize, SeekOrigin.Current);
                }
            }

            if (dataBytes == null || dataBytes.Length == 0 || bits != 16) return null;

            int bytesPerFrame = channels * 2;
            int numFrames = dataBytes.Length / bytesPerFrame;
            float[] monoOut = new float[numFrames];

            for (int i = 0; i < numFrames; i++)
            {
                int offset = i * bytesPerFrame;
                short sampleLeft = BitConverter.ToInt16(dataBytes, offset);
                float sampleNorm;
                if (channels >= 2)
                {
                    short sampleRight = BitConverter.ToInt16(dataBytes, offset + 2);
                    sampleNorm = (sampleLeft + sampleRight) / (2f * 32768f);
                }
                else
                {
                    sampleNorm = sampleLeft / 32768f;
                }
                monoOut[i] = sampleNorm;
            }

            // High-precision linear resampling to 44100 Hz if file sample rate differs
            if (fileSampleRate != SampleRate && fileSampleRate > 0 && monoOut.Length > 1)
            {
                int targetLength = (int)((long)monoOut.Length * SampleRate / fileSampleRate);
                if (targetLength > 1)
                {
                    float[] resampled = new float[targetLength];
                    double ratio = (double)(monoOut.Length - 1) / (targetLength - 1);
                    for (int i = 0; i < targetLength; i++)
                    {
                        double srcIdx = i * ratio;
                        int i0 = (int)srcIdx;
                        int i1 = Math.Min(i0 + 1, monoOut.Length - 1);
                        float frac = (float)(srcIdx - i0);
                        resampled[i] = monoOut[i0] * (1.0f - frac) + monoOut[i1] * frac;
                    }
                    return resampled;
                }
            }

            return monoOut;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] GenerateOrbitWav(SpatialAudioOptions? options = null)
    {
        float durationSec = 1.4f;
        int totalSamples = (int)(SampleRate * durationSec);

        float[] leftSamples = new float[totalSamples];
        float[] rightSamples = new float[totalSamples];

        float filterLeft = 0f;
        float filterRight = 0f;
        float alpha = 0.65f; // ~1.8 kHz lowpass for shadowed ear

        options ??= new SpatialAudioOptions();
        bool isCompetitive = options.HrtfProfile.Contains("Competitive", StringComparison.OrdinalIgnoreCase);
        bool isCinematic = options.HrtfProfile.Contains("Cinematic", StringComparison.OrdinalIgnoreCase);

        BiquadFilter biquadL = isCompetitive
            ? BiquadFilter.CreatePeakingEq(SampleRate, 4500f, 1.4f, 5.5f)
            : isCinematic
                ? BiquadFilter.CreateLowShelf(SampleRate, 220f, 4.0f)
                : default;
        BiquadFilter biquadR = isCompetitive
            ? BiquadFilter.CreatePeakingEq(SampleRate, 4500f, 1.4f, 5.5f)
            : isCinematic
                ? BiquadFilter.CreateLowShelf(SampleRate, 220f, 4.0f)
                : default;

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / SampleRate;
            // Full 360-degree rotation: 0 -> 2pi
            float azimuthRad = (t / durationSec) * 2.0f * MathF.PI;

            // Rhythmic chime pulses: 4 pulses during rotation (Front, Right, Behind, Left)
            float pulseTime = (t * 4.0f) % 1.0f;
            float attack = Math.Min(1.0f, pulseTime / 0.008f);
            float decay = MathF.Exp(-7.5f * pulseTime);

            float carrierFreq = 587.33f + (MathF.Sin(azimuthRad) * 40.0f); // Slight doppler shift
            float wave = (MathF.Sin(2.0f * MathF.PI * carrierFreq * t) +
                          0.4f * MathF.Sin(2.0f * MathF.PI * carrierFreq * 2.0f * t)) * attack * decay * 0.75f;

            // ILD Calculation
            float sinAz = MathF.Sin(azimuthRad);
            float gainL = MathF.Sqrt(Math.Max(0.05f, (1.0f - sinAz) * 0.5f));
            float gainR = MathF.Sqrt(Math.Max(0.05f, (1.0f + sinAz) * 0.5f));

            // Rear pinna attenuation
            if (MathF.Cos(azimuthRad) < 0)
            {
                gainL *= 0.82f;
                gainR *= 0.82f;
            }

            // Head shadow filter
            float rawL = wave * gainL;
            float rawR = wave * gainR;

            if (sinAz > 0) // Sound is on the right; Left ear is shadowed
            {
                filterLeft = (1.0f - alpha) * rawL + alpha * filterLeft;
                leftSamples[i] = filterLeft;
                rightSamples[i] = rawR;
            }
            else // Sound is on the left; Right ear is shadowed
            {
                filterRight = (1.0f - alpha) * rawR + alpha * filterRight;
                leftSamples[i] = rawL;
                rightSamples[i] = filterRight;
            }

            if (isCompetitive || isCinematic)
            {
                leftSamples[i] = biquadL.Process(leftSamples[i]);
                rightSamples[i] = biquadR.Process(rightSamples[i]);
            }
        }

        // Apply channel polyphony reflections
        ApplyChannelPolyphonyReflections(leftSamples, rightSamples, options.Channels);

        return EncodeStereoWav(leftSamples, rightSamples);
    }

    public static byte[] RenderBinauralStereoWav(
        float[] monoSource,
        float azimuthRad,
        SpatialAudioOptions? options = null)
    {
        options ??= new SpatialAudioOptions();
        int length = monoSource.Length;
        float[] leftSamples = new float[length];
        float[] rightSamples = new float[length];

        float sinAz = MathF.Sin(azimuthRad);
        float cosAz = MathF.Cos(azimuthRad);

        bool isCompetitive = options.HrtfProfile.Contains("Competitive", StringComparison.OrdinalIgnoreCase);
        bool isCinematic = options.HrtfProfile.Contains("Cinematic", StringComparison.OrdinalIgnoreCase);

        // ILD Calculation: Competitive Pinpoint sharpens angular separation
        float ildSin = isCompetitive ? Math.Clamp(sinAz * 1.30f, -1.0f, 1.0f) : sinAz;
        float gainL = MathF.Sqrt(Math.Max(0.04f, (1.0f - ildSin) * 0.5f));
        float gainR = MathF.Sqrt(Math.Max(0.04f, (1.0f + ildSin) * 0.5f));

        // Pinna rear attenuation if sound source is behind
        if (cosAz < 0)
        {
            gainL *= 0.80f;
            gainR *= 0.80f;
        }

        // ITD Woodworth model: delay in samples. Cinematic widens head soundstage
        int maxDelay = isCinematic ? 36 : (isCompetitive ? 27 : MaxDelaySamples);
        int delaySamples = (int)(MathF.Abs(sinAz) * maxDelay);

        float filterL = 0f;
        float filterR = 0f;
        float alpha = isCompetitive ? 0.55f : 0.65f; // Head shadow filter coefficient

        // Optional geometry occlusion damping (trees / cliffs): lowpass damping
        float occlFilterL = 0f;
        float occlFilterR = 0f;
        bool applyOcclusion = options.OcclusionDamping && (cosAz < -0.1f || MathF.Abs(sinAz) > 0.5f);
        float occlAlpha = 0.50f; // ~2.2kHz gentle acoustic diffraction lowpass

        BiquadFilter biquadL = isCompetitive
            ? BiquadFilter.CreatePeakingEq(SampleRate, 4500f, 1.4f, 5.5f)
            : isCinematic
                ? BiquadFilter.CreateLowShelf(SampleRate, 220f, 4.0f)
                : default;
        BiquadFilter biquadR = isCompetitive
            ? BiquadFilter.CreatePeakingEq(SampleRate, 4500f, 1.4f, 5.5f)
            : isCinematic
                ? BiquadFilter.CreateLowShelf(SampleRate, 220f, 4.0f)
                : default;

        for (int i = 0; i < length; i++)
        {
            float source = monoSource[i];

            float curL;
            float curR;

            if (sinAz >= 0) // Sound on Right (Left ear delayed and filtered)
            {
                curR = source * gainR;

                int delayedIdx = i - delaySamples;
                float leftRaw = delayedIdx >= 0 ? monoSource[delayedIdx] * gainL : 0f;
                filterL = (1.0f - alpha) * leftRaw + alpha * filterL;
                curL = filterL;
            }
            else // Sound on Left (Right ear delayed and filtered)
            {
                curL = source * gainL;

                int delayedIdx = i - delaySamples;
                float rightRaw = delayedIdx >= 0 ? monoSource[delayedIdx] * gainR : 0f;
                filterR = (1.0f - alpha) * rightRaw + alpha * filterR;
                curR = filterR;
            }

            // Geometry occlusion damping
            if (applyOcclusion)
            {
                occlFilterL = (1.0f - occlAlpha) * curL + occlAlpha * occlFilterL;
                occlFilterR = (1.0f - occlAlpha) * curR + occlAlpha * occlFilterR;
                curL = occlFilterL * 0.85f;
                curR = occlFilterR * 0.85f;
            }

            // Profile-specific Biquad Filter (Competitive Pinpoint presence or Cinematic low-shelf)
            if (isCompetitive || isCinematic)
            {
                curL = biquadL.Process(curL);
                curR = biquadR.Process(curR);
            }

            leftSamples[i] = curL;
            rightSamples[i] = curR;
        }

        // Apply channel polyphony reflections & acoustics
        ApplyChannelPolyphonyReflections(leftSamples, rightSamples, options.Channels);

        return EncodeStereoWav(leftSamples, rightSamples);
    }

    private static void ApplyChannelPolyphonyReflections(float[] left, float[] right, int channels)
    {
        int length = Math.Min(left.Length, right.Length);

        if (channels <= 64)
        {
            // 64 Channels: Esports fast gated transient response (zero room tails, snappy direct path)
            return;
        }

        if (channels >= 256)
        {
            // 256 Channels: Ultra polyphony with 4-tap early spatial room reflections & cross-feed
            int[] delayTaps = [794, 1588, 2866, 4322]; // ~18ms, ~36ms, ~65ms, ~98ms at 44.1kHz
            float[] gains = [0.18f, 0.12f, 0.08f, 0.05f];

            for (int t = 0; t < delayTaps.Length; t++)
            {
                int delay = delayTaps[t];
                float gain = gains[t];
                bool crossPan = (t % 2) == 1;

                for (int i = delay; i < length; i++)
                {
                    if (crossPan)
                    {
                        left[i] += right[i - delay] * gain;
                        right[i] += left[i - delay] * gain;
                    }
                    else
                    {
                        left[i] += left[i - delay] * gain;
                        right[i] += right[i - delay] * gain;
                    }
                }
            }
        }
        else
        {
            // 128 Channels: Balanced studio acoustic presence (single gentle room reflection tap at ~22ms)
            int delay = 970; // ~22ms
            float gain = 0.11f;
            for (int i = delay; i < length; i++)
            {
                left[i] += left[i - delay] * gain;
                right[i] += right[i - delay] * gain;
            }
        }
    }

    private struct BiquadFilter
    {
        public float B0, B1, B2, A1, A2;
        public float X1, X2, Y1, Y2;

        public static BiquadFilter CreatePeakingEq(float sampleRate, float f0, float q, float gainDb)
        {
            float a = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * f0 / sampleRate;
            float alpha = MathF.Sin(w0) / (2f * q);
            float cosw0 = MathF.Cos(w0);

            float b0 = 1f + alpha * a;
            float b1 = -2f * cosw0;
            float b2 = 1f - alpha * a;
            float a0 = 1f + alpha / a;
            float a1 = -2f * cosw0;
            float a2 = 1f - alpha / a;

            return new BiquadFilter
            {
                B0 = b0 / a0,
                B1 = b1 / a0,
                B2 = b2 / a0,
                A1 = a1 / a0,
                A2 = a2 / a0
            };
        }

        public static BiquadFilter CreateLowShelf(float sampleRate, float f0, float gainDb)
        {
            float a = MathF.Pow(10f, gainDb / 40f);
            float w0 = 2f * MathF.PI * f0 / sampleRate;
            float cosw0 = MathF.Cos(w0);
            float sinw0 = MathF.Sin(w0);
            float sqrtA = MathF.Sqrt(a);
            float alpha = sinw0 / 2f * MathF.Sqrt(2f);

            float b0 = a * ((a + 1f) - (a - 1f) * cosw0 + 2f * sqrtA * alpha);
            float b1 = 2f * a * ((a - 1f) - (a + 1f) * cosw0);
            float b2 = a * ((a + 1f) - (a - 1f) * cosw0 - 2f * sqrtA * alpha);
            float a0 = (a + 1f) + (a - 1f) * cosw0 + 2f * sqrtA * alpha;
            float a1 = -2f * ((a - 1f) + (a + 1f) * cosw0);
            float a2 = (a + 1f) + (a - 1f) * cosw0 - 2f * sqrtA * alpha;

            return new BiquadFilter
            {
                B0 = b0 / a0,
                B1 = b1 / a0,
                B2 = b2 / a0,
                A1 = a1 / a0,
                A2 = a2 / a0
            };
        }

        public float Process(float inSample)
        {
            float outSample = B0 * inSample + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2;
            X2 = X1;
            X1 = inSample;
            Y2 = Y1;
            Y1 = outSample;
            return outSample;
        }
    }

    private static byte[] EncodeStereoWav(float[] left, float[] right)
    {
        int numSamples = Math.Min(left.Length, right.Length);
        int subChunk2Size = numSamples * Channels * (BitsPerSample / 8);
        int chunkSize = 36 + subChunk2Size;

        using MemoryStream stream = new(44 + subChunk2Size);
        using BinaryWriter writer = new(stream, Encoding.ASCII, true);

        // RIFF Header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(chunkSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // "fmt " Subchunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size for PCM
        writer.Write((short)1); // AudioFormat (1 = PCM)
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * (BitsPerSample / 8)); // ByteRate
        writer.Write((short)(Channels * (BitsPerSample / 8)));     // BlockAlign
        writer.Write(BitsPerSample);

        // "data" Subchunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(subChunk2Size);

        for (int i = 0; i < numSamples; i++)
        {
            short sampleL = (short)Math.Clamp((int)(left[i] * 32767f), short.MinValue, short.MaxValue);
            short sampleR = (short)Math.Clamp((int)(right[i] * 32767f), short.MinValue, short.MaxValue);

            writer.Write(sampleL);
            writer.Write(sampleR);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
