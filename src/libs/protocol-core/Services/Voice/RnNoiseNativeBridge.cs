// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RnNoiseNativeBridge.cs
// System: ProjectKongor - Native Xiph librnnoise (BSD-3) P/Invoke Bridge
// Policy: Strict Explicit Typing (NO 'var') - NativeAOT-Safe Unmanaged Function Pointers
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     Honest runtime classification of which noise-suppression backend is executing frames.
///     Never reports <see cref="RnNoiseNativeGru"/> unless native <c>librnnoise</c> loaded and created a valid state.
/// </summary>
public enum NoiseSuppressionBackend
{
    SpectralGatingFallback = 0,
    RnNoiseNativeGru = 1
}

internal interface IRnNoiseNativeApi
{
    string LibraryName { get; }
    int ExpectedFrameSize { get; }
    IntPtr CreateState();
    void DestroyState(IntPtr state);
    float ProcessFrame(IntPtr state, Span<float> output, ReadOnlySpan<float> input);
}

/// <summary>
///     NativeAOT-safe P/Invoke bridge to Xiph.Org <c>librnnoise</c> (BSD-3-Clause):
///     probes <c>rnnoise.dll</c> / <c>librnnoise.so</c> / <c>librnnoise.dylib</c> via <see cref="NativeLibrary.TryLoad(string, out nint)"/>
///     and invokes <c>rnnoise_create</c>, <c>rnnoise_process_frame</c>, and <c>rnnoise_destroy</c> on exact
///     480-sample (10 ms @ 48 kHz) float32 frames in 16-bit PCM magnitude scale [-32768.0, 32767.0].
/// </summary>
public sealed class RnNoiseNativeBridge : IDisposable
{
    public const int FrameSizeSamples = 480;
    public const int RequiredSampleRateHz = 48000;

    private static readonly Lazy<NativeProbeResult> SharedNativeProbe = new(
        ProbeDefaultNativeLibrary,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IRnNoiseNativeApi? _api;
    private readonly Lock _stateLock = new();
    private IntPtr _stateHandle;
    private bool _disposed;

    public bool IsNativeLoaded { get; }
    public string? LoadedLibraryPath { get; }
    public string ProbeDiagnostic { get; }
    public float LastVoiceActivityProbability { get; private set; }
    public long ProcessedNativeFrames { get; private set; }

    private RnNoiseNativeBridge(
        IRnNoiseNativeApi? api,
        IntPtr stateHandle,
        string? loadedLibraryPath,
        string probeDiagnostic)
    {
        _api = api;
        _stateHandle = stateHandle;
        IsNativeLoaded = api is not null && stateHandle != IntPtr.Zero;
        LoadedLibraryPath = IsNativeLoaded ? loadedLibraryPath : null;
        ProbeDiagnostic = probeDiagnostic;
    }

    /// <summary>
    ///     Probes the platform native loader for <c>librnnoise</c> and initializes a dedicated <c>DenoiseState</c>.
    ///     Returns a safe non-throwing bridge with <see cref="IsNativeLoaded"/> = <see langword="false"/> if absent.
    /// </summary>
    public static RnNoiseNativeBridge TryCreateDefault(Action<string>? logSink = null)
    {
        NativeProbeResult probe = SharedNativeProbe.Value;
        if (probe.Api is null)
        {
            Log(logSink, $"[VOICE-RNNOISE] Native librnnoise unavailable ({probe.Diagnostic}); using SpectralGatingFallback.");
            return new RnNoiseNativeBridge(null, IntPtr.Zero, null, probe.Diagnostic);
        }

        return CreateWithApi(probe.Api, logSink);
    }

    internal static RnNoiseNativeBridge CreateWithApi(IRnNoiseNativeApi api, Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        if (api.ExpectedFrameSize != FrameSizeSamples)
        {
            string mismatchDiag = $"Native library '{api.LibraryName}' reported unsupported frame size {api.ExpectedFrameSize} (expected {FrameSizeSamples}); falling back to SpectralGatingFallback.";
            Log(logSink, $"[VOICE-RNNOISE] [WARN] {mismatchDiag}");
            return new RnNoiseNativeBridge(null, IntPtr.Zero, null, mismatchDiag);
        }

        IntPtr state = IntPtr.Zero;
        try
        {
            state = api.CreateState();
        }
        catch (Exception ex)
        {
            string createErr = $"rnnoise_create threw {ex.GetType().Name}: {ex.Message}; falling back to SpectralGatingFallback.";
            Log(logSink, $"[VOICE-RNNOISE] [WARN] {createErr}");
            return new RnNoiseNativeBridge(null, IntPtr.Zero, null, createErr);
        }

        if (state == IntPtr.Zero)
        {
            string nullDiag = $"rnnoise_create returned NULL from '{api.LibraryName}'; falling back to SpectralGatingFallback.";
            Log(logSink, $"[VOICE-RNNOISE] [WARN] {nullDiag}");
            return new RnNoiseNativeBridge(null, IntPtr.Zero, null, nullDiag);
        }

        string okDiag = $"Loaded native librnnoise GRU backend from '{api.LibraryName}' (frame={FrameSizeSamples} samples @ {RequiredSampleRateHz} Hz).";
        Log(logSink, $"[VOICE-RNNOISE] [OK] {okDiag}");
        return new RnNoiseNativeBridge(api, state, api.LibraryName, okDiag);
    }

    internal static RnNoiseNativeBridge CreateUnavailable(string diagnostic)
    {
        return new RnNoiseNativeBridge(null, IntPtr.Zero, null, diagnostic);
    }

    /// <summary>
    ///     Processes an exact 480-sample (10 ms @ 48 kHz) mono 16-bit PCM frame in-place through native <c>rnnoise_process_frame</c>.
    /// </summary>
    public bool TryProcessFrame480(Span<short> pcm480, out float vadProbability)
    {
        vadProbability = 0f;

        if (!IsNativeLoaded || pcm480.Length != FrameSizeSamples)
        {
            return false;
        }

        Span<float> inputFloats = stackalloc float[FrameSizeSamples];
        Span<float> outputFloats = stackalloc float[FrameSizeSamples];

        // RNNoise operates on float32 samples scaled to 16-bit signed PCM magnitude [-32768.0, 32767.0].
        for (int i = 0; i < FrameSizeSamples; i++)
        {
            inputFloats[i] = pcm480[i];
        }

        float rawVad;
        lock (_stateLock)
        {
            if (_disposed || _api is null || _stateHandle == IntPtr.Zero)
            {
                return false;
            }

            rawVad = _api.ProcessFrame(_stateHandle, outputFloats, inputFloats);
        }

        for (int i = 0; i < FrameSizeSamples; i++)
        {
            float sample = outputFloats[i];
            if (!float.IsFinite(sample))
            {
                sample = 0f;
            }

            pcm480[i] = (short)Math.Clamp((int)MathF.Round(sample), -32768, 32767);
        }

        vadProbability = float.IsFinite(rawVad) ? Math.Clamp(rawVad, 0f, 1f) : 0f;
        LastVoiceActivityProbability = vadProbability;
        ProcessedNativeFrames++;
        return true;
    }

    /// <summary>
    ///     Resets the native RNNoise GRU recurrent state by recreating the underlying <c>DenoiseState</c>.
    /// </summary>
    public void ResetState()
    {
        if (!IsNativeLoaded)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposed || _api is null || _stateHandle == IntPtr.Zero)
            {
                return;
            }

            IntPtr oldState = _stateHandle;
            IntPtr freshState = _api.CreateState();
            if (freshState != IntPtr.Zero)
            {
                _stateHandle = freshState;
                _api.DestroyState(oldState);
            }

            LastVoiceActivityProbability = 0f;
        }
    }

    public void Dispose()
    {
        ReleaseNativeState();
        GC.SuppressFinalize(this);
    }

    ~RnNoiseNativeBridge()
    {
        ReleaseNativeState();
    }

    private void ReleaseNativeState()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IntPtr handle = _stateHandle;
            _stateHandle = IntPtr.Zero;

            if (handle != IntPtr.Zero && _api is not null)
            {
                try
                {
                    _api.DestroyState(handle);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[VOICE-RNNOISE] rnnoise_destroy cleanup notice: {ex.Message}");
                }
            }
        }
    }

    private static NativeProbeResult ProbeDefaultNativeLibrary()
    {
        List<string> candidates = BuildCandidateLibraryPaths();
        List<string> attempted = new List<string>(candidates.Count);

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            bool isRooted = Path.IsPathRooted(candidate);
            if (isRooted && !File.Exists(candidate))
            {
                continue;
            }

            attempted.Add(candidate);
            IntPtr libHandle;
            bool loaded = isRooted
                ? NativeLibrary.TryLoad(candidate, out libHandle)
                : NativeLibrary.TryLoad(
                    candidate,
                    typeof(RnNoiseNativeBridge).Assembly,
                    DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories,
                    out libHandle);

            if (!loaded || libHandle == IntPtr.Zero)
            {
                continue;
            }

            if (NativeLibraryRnNoiseApi.TryBind(libHandle, candidate, out NativeLibraryRnNoiseApi? boundApi, out string bindError))
            {
                return new NativeProbeResult(boundApi, $"Bound native librnnoise exports from '{candidate}'.");
            }

            Trace.WriteLine($"[VOICE-RNNOISE] Candidate '{candidate}' loaded but failed ABI export check: {bindError}");
            NativeLibrary.Free(libHandle);
        }

        string summary = attempted.Count == 0
            ? "No librnnoise candidate libraries present on search path."
            : $"Probed [{string.Join(", ", attempted)}]; no compatible librnnoise binary found.";
        return new NativeProbeResult(null, summary);
    }

    internal static List<string> BuildCandidateLibraryPaths()
    {
        List<string> candidates = new List<string>(10);

        string? envOverride = Environment.GetEnvironmentVariable("KONGOR_RNNOISE_LIB_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            candidates.Add(envOverride.Trim());
        }

        string baseDir = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(baseDir, "rnnoise.dll"));
            candidates.Add(Path.Combine(baseDir, "native", "rnnoise.dll"));
            candidates.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native", "rnnoise.dll"));
            candidates.Add("rnnoise.dll");
            candidates.Add("librnnoise.dll");
            candidates.Add("librnnoise-0.dll");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine(baseDir, "librnnoise.0.dylib"));
            candidates.Add(Path.Combine(baseDir, "librnnoise.dylib"));
            candidates.Add(Path.Combine(baseDir, "runtimes", "osx-arm64", "native", "librnnoise.dylib"));
            candidates.Add(Path.Combine(baseDir, "runtimes", "osx-x64", "native", "librnnoise.dylib"));
            candidates.Add("librnnoise.0.dylib");
            candidates.Add("librnnoise.dylib");
        }
        else
        {
            candidates.Add(Path.Combine(baseDir, "librnnoise.so.0"));
            candidates.Add(Path.Combine(baseDir, "librnnoise.so"));
            candidates.Add(Path.Combine(baseDir, "runtimes", "linux-x64", "native", "librnnoise.so"));
            candidates.Add("librnnoise.so.0");
            candidates.Add("librnnoise.so");
        }

        return candidates;
    }

    private static void Log(Action<string>? logSink, string message)
    {
        Trace.WriteLine(message);
        logSink?.Invoke(message);
    }

    private sealed record NativeProbeResult(IRnNoiseNativeApi? Api, string Diagnostic);

    private sealed unsafe class NativeLibraryRnNoiseApi : IRnNoiseNativeApi
    {
        private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _create;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float> _processFrame;

        public string LibraryName { get; }
        public int ExpectedFrameSize { get; }

        private NativeLibraryRnNoiseApi(
            string libraryName,
            int expectedFrameSize,
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr> create,
            delegate* unmanaged[Cdecl]<IntPtr, void> destroy,
            delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float> processFrame)
        {
            LibraryName = libraryName;
            ExpectedFrameSize = expectedFrameSize;
            _create = create;
            _destroy = destroy;
            _processFrame = processFrame;
        }

        public static bool TryBind(
            IntPtr libraryHandle,
            string libraryName,
            out NativeLibraryRnNoiseApi? api,
            out string error)
        {
            api = null;
            error = string.Empty;

            if (!NativeLibrary.TryGetExport(libraryHandle, "rnnoise_create", out IntPtr createPtr) ||
                !NativeLibrary.TryGetExport(libraryHandle, "rnnoise_destroy", out IntPtr destroyPtr) ||
                !NativeLibrary.TryGetExport(libraryHandle, "rnnoise_process_frame", out IntPtr processPtr))
            {
                error = "Missing one or more required C exports (rnnoise_create, rnnoise_destroy, rnnoise_process_frame).";
                return false;
            }

            int frameSize = FrameSizeSamples;
            if (NativeLibrary.TryGetExport(libraryHandle, "rnnoise_get_frame_size", out IntPtr frameSizePtr))
            {
                delegate* unmanaged[Cdecl]<int> getFrameSize = (delegate* unmanaged[Cdecl]<int>)frameSizePtr;
                frameSize = getFrameSize();
                if (frameSize != FrameSizeSamples)
                {
                    error = $"rnnoise_get_frame_size() returned {frameSize}, expected {FrameSizeSamples}.";
                    return false;
                }
            }

            api = new NativeLibraryRnNoiseApi(
                libraryName,
                frameSize,
                (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)createPtr,
                (delegate* unmanaged[Cdecl]<IntPtr, void>)destroyPtr,
                (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float>)processPtr);
            return true;
        }

        public IntPtr CreateState() => _create(IntPtr.Zero);

        public void DestroyState(IntPtr state)
        {
            if (state != IntPtr.Zero)
            {
                _destroy(state);
            }
        }

        public float ProcessFrame(IntPtr state, Span<float> output, ReadOnlySpan<float> input)
        {
            fixed (float* outPtr = output)
            fixed (float* inPtr = input)
            {
                return _processFrame(state, outPtr, inPtr);
            }
        }
    }
}
