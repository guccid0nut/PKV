// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SpatialAudioEngine.cs
// System: NEXUS Spatial Audio Modernization Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NEXUS.SpatialAudio;

/// <summary>
///     Represents the operational status of OpenAL Soft's binaural HRTF pipeline.
/// </summary>
public enum HrtfRuntimeStatus
{
    Disabled = 0,
    ActiveBinaural = 1,
    HeadphonesDetected = 2,
    Required = 3,
    Denied = 4,
    UnsupportedFormat = 5,
    NotAvailable = 6
}

/// <summary>
///     Result of ray-marched K2 cliff and terrain obstacle occlusion between listener and source.
/// </summary>
public readonly record struct SpatialOcclusionResult(
    float GainMultiplier,
    float LowPassGainHf,
    int OccludedSteps);

/// <summary>
///     Managed audio orchestrator that interfaces with OpenAL Soft, queries HRTF status,
///     manages 3D listener orientation, and handles spatial voice allocation.
/// </summary>
public sealed class SpatialAudioEngine : IDisposable
{
    private readonly ILogger<SpatialAudioEngine> _logger;
    private readonly Lock _lock = new();
    private nint _device;
    private nint _context;
    private bool _isInitialized;

    public SpatialAudioEngine(ILogger<SpatialAudioEngine> logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _isInitialized;
    public HrtfRuntimeStatus CurrentHrtfStatus { get; private set; } = HrtfRuntimeStatus.NotAvailable;
    public int SelectedHrtfProfileIndex { get; private set; } = -1;

    /// <summary>
    ///     Initializes the OpenAL Soft device with explicit HRTF attributes and optional HRTF profile ID.
    /// </summary>
    public unsafe bool Initialize(bool enableHrtf = true, int frequency = 48000, int hrtfProfileIndex = -1)
    {
        lock (_lock)
        {
            if (_isInitialized) return true;

            SelectedHrtfProfileIndex = hrtfProfileIndex;

            try
            {
                _device = OpenAlNative.alcOpenDevice(null);
                if (_device == nint.Zero)
                {
                    _logger.LogWarning("[SpatialAudioEngine] OpenAL Soft device could not be opened (library missing or no audio device)");
                    return false;
                }

                // Attributes: Frequency, HRTF enabled, and optional ALC_HRTF_ID_SOFT profile index
                int[] attrs = enableHrtf && hrtfProfileIndex >= 0
                    ? [
                        OpenAlNative.ALC_FREQUENCY, frequency,
                        OpenAlNative.ALC_HRTF_SOFT, OpenAlNative.AL_TRUE,
                        OpenAlNative.ALC_HRTF_ID_SOFT, hrtfProfileIndex,
                        0 // Null terminator
                    ]
                    : [
                        OpenAlNative.ALC_FREQUENCY, frequency,
                        OpenAlNative.ALC_HRTF_SOFT, enableHrtf ? OpenAlNative.AL_TRUE : OpenAlNative.AL_FALSE,
                        0 // Null terminator
                    ];

                fixed (int* pAttrs = attrs)
                {
                    _context = OpenAlNative.alcCreateContext(_device, pAttrs);
                }

                if (_context == nint.Zero)
                {
                    _logger.LogError("[SpatialAudioEngine] Failed to create OpenAL context");
                    OpenAlNative.alcCloseDevice(_device);
                    _device = nint.Zero;
                    return false;
                }

                OpenAlNative.alcMakeContextCurrent(_context);
                OpenAlNative.alDistanceModel(OpenAlNative.AL_INVERSE_DISTANCE_CLAMPED);

                // Query HRTF Status
                QueryHrtfStatus();

                _isInitialized = true;
                _logger.LogInformation(
                    "[SpatialAudioEngine] Initialized OpenAL Soft HRTF Spatializer successfully. Status: {Status}, HrtfProfileIndex: {ProfileIndex}",
                    CurrentHrtfStatus,
                    SelectedHrtfProfileIndex);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SpatialAudioEngine] Exception during OpenAL Soft initialization");
                return false;
            }
        }
    }

    /// <summary>
    ///     Tears down any active OpenAL context/device and re-initializes with the specified HRTF profile index.
    /// </summary>
    public bool ReinitializeWithHrtfProfile(int hrtfProfileIndex, int frequency = 48000)
    {
        lock (_lock)
        {
            _logger.LogInformation(
                "[SpatialAudioEngine] Reinitializing OpenAL Soft context with HRTF profile index {ProfileIndex} at {Frequency} Hz",
                hrtfProfileIndex,
                frequency);

            Dispose();
            SelectedHrtfProfileIndex = hrtfProfileIndex;
            return Initialize(true, frequency, hrtfProfileIndex);
        }
    }

    /// <summary>
    ///     Computes direct-path gain and high-frequency low-pass attenuation across an N-step ray march
    ///     between K2 listener and source coordinates.
    ///     Note: Default cliffBlockThresholdUnits = 128f corresponds to 1 standard K2 cliff elevation tier.
    /// </summary>
    public static SpatialOcclusionResult ComputeCliffAndObstacleOcclusion(
        Vector3 listenerK2,
        Vector3 sourceK2,
        Func<float, float, float>? terrainHeightSampler = null,
        float cliffBlockThresholdUnits = 128f,
        int raySteps = 8)
    {
        int steps = Math.Clamp(raySteps, 2, 64);
        float distance = Vector3.Distance(listenerK2, sourceK2);
        if (distance < 1e-3f)
        {
            return new SpatialOcclusionResult(1.0f, 1.0f, 0);
        }

        int occludedSteps = 0;

        // Check direct elevation differential (e.g. high-ground cliff blind spot)
        float elevationDelta = MathF.Abs(sourceK2.Z - listenerK2.Z);
        if (terrainHeightSampler == null)
        {
            if (elevationDelta >= cliffBlockThresholdUnits)
            {
                occludedSteps = 1 + (int)MathF.Min(3f, (elevationDelta - cliffBlockThresholdUnits) / cliffBlockThresholdUnits);
            }
        }
        else
        {
            for (int step = 1; step < steps; step++)
            {
                float t = (float)step / steps;
                Vector3 samplePoint = Vector3.Lerp(listenerK2, sourceK2, t);
                float terrainZ = terrainHeightSampler(samplePoint.X, samplePoint.Y);

                // Ray is occluded at this step if terrain rises above line-of-sight by threshold or blocks line-of-sight
                if (terrainZ > samplePoint.Z + 8.0f || (terrainZ - MathF.Min(listenerK2.Z, sourceK2.Z)) >= cliffBlockThresholdUnits && terrainZ > samplePoint.Z)
                {
                    occludedSteps++;
                }
            }
        }

        if (occludedSteps <= 0)
        {
            return new SpatialOcclusionResult(1.0f, 1.0f, 0);
        }

        float occlusionFraction = Math.Clamp((float)occludedSteps / (steps - 1), 0f, 1f);
        float gainMultiplier = Math.Clamp(1.0f - (0.55f * occlusionFraction) - (0.15f * MathF.Min(1f, occludedSteps)), 0.25f, 1.0f);
        float lowPassGainHf = Math.Clamp(1.0f - (0.65f * occlusionFraction) - (0.20f * MathF.Min(1f, occludedSteps)), 0.18f, 1.0f);

        return new SpatialOcclusionResult(gainMultiplier, lowPassGainHf, occludedSteps);
    }

    private unsafe void QueryHrtfStatus()
    {
        if (_device == nint.Zero) return;

        bool hasHrtfExt = OpenAlNative.alcIsExtensionPresent(_device, "ALC_SOFT_HRTF");
        if (!hasHrtfExt)
        {
            CurrentHrtfStatus = HrtfRuntimeStatus.NotAvailable;
            return;
        }

        int hrtfStatus = 0;
        OpenAlNative.alcGetIntegerv(_device, OpenAlNative.ALC_HRTF_STATUS_SOFT, 1, &hrtfStatus);

        CurrentHrtfStatus = hrtfStatus switch
        {
            OpenAlNative.ALC_HRTF_ENABLED_SOFT => HrtfRuntimeStatus.ActiveBinaural,
            OpenAlNative.ALC_HRTF_HEADPHONES_DETECTED_SOFT => HrtfRuntimeStatus.HeadphonesDetected,
            OpenAlNative.ALC_HRTF_REQUIRED_SOFT => HrtfRuntimeStatus.Required,
            OpenAlNative.ALC_HRTF_DENIED_SOFT => HrtfRuntimeStatus.Denied,
            OpenAlNative.ALC_HRTF_UNSUPPORTED_FORMAT_SOFT => HrtfRuntimeStatus.UnsupportedFormat,
            _ => HrtfRuntimeStatus.Disabled
        };
    }

    /// <summary>
    ///     Updates the 3D listener position and orientation in OpenAL Soft coordinate space.
    /// </summary>
    public unsafe void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up)
    {
        if (!_isInitialized) return;

        OpenAlNative.alListener3f(OpenAlNative.AL_POSITION, position.X, position.Y, position.Z);

        float[] orientation = [forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z];
        fixed (float* pOri = orientation)
        {
            OpenAlNative.alListenerfv(OpenAlNative.AL_ORIENTATION, pOri);
        }
    }

    /// <summary>
    ///     Converts K2 engine coordinates to OpenAL Soft HRTF coordinates and applies the listener pose.
    ///     K2 axes: +X is Right, +Y is Forward, +Z is Up.
    ///     OpenAL axes: +X is Right, +Y is Up, -Z is Forward.
    /// </summary>
    public void SetListenerPoseFromK2(
        float k2X, float k2Y, float k2Z,
        float k2FwdX, float k2FwdY, float k2FwdZ,
        float k2UpX, float k2UpY, float k2UpZ)
    {
        // Coordinate transformation:
        // OpenAL X = K2 X (Right)
        // OpenAL Y = K2 Z (Up)
        // OpenAL Z = -K2 Y (Forward)
        Vector3 alPos = new(k2X, k2Z, -k2Y);
        Vector3 alFwd = new(k2FwdX, k2FwdZ, -k2FwdY);
        Vector3 alUp = new(k2UpX, k2UpZ, -k2UpY);

        SetListenerPose(alPos, alFwd, alUp);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_isInitialized) return;

            if (_context != nint.Zero)
            {
                OpenAlNative.alcMakeContextCurrent(nint.Zero);
                OpenAlNative.alcDestroyContext(_context);
                _context = nint.Zero;
            }

            if (_device != nint.Zero)
            {
                OpenAlNative.alcCloseDevice(_device);
                _device = nint.Zero;
            }

            _isInitialized = false;
        }
    }
}
