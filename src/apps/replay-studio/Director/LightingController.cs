// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LightingController.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayStudio.GUI.Director;

public enum LightingPreset
{
    MiddaySun = 0,
    SunsetGoldenHour = 1,
    NightMoonlight = 2,
    HellbourneInferno = 3
}

/// <summary>
///     128-byte unmanaged uniform block passed via Vulkan push constants to CVulkanDriver shaders.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 128)]
public struct LightingPushConstants
{
    // [0..63] 64 bytes: Model-View-Projection Matrix
    public Matrix4x4 ModelViewProjection;

    // [64..75] 12 bytes: Normalized Sun Direction
    public Vector3 SunDirection;

    // [76..79] 4 bytes: Ambient Irradiance Factor (default 0.35)
    public float AmbientFactor;

    // [80..95] 16 bytes: Team Tint Color (RGB + Intensity/Alpha)
    public Vector4 TeamTint;

    // [96..99] 4 bytes: Specular Highlight Factor (default 0.8)
    public float SpecularFactor;

    // [100..103] 4 bytes: Bloom HDR Threshold (default 1.2)
    public float BloomThreshold;

    // [104..107] 4 bytes: Team Aura Glow Intensity (default 0.5)
    public float TeamAuraGlow;

    // [108..111] 4 bytes: Elapsed Simulation Time in Seconds
    public float TimeSeconds;

    // [112..127] 16 bytes: Terrain & Environmental Parameters (WaterLevel, GridScale, FogDensity, SpecularPower)
    public Vector4 EnvironmentalParams;
}

/// <summary>
///     Controller managing dynamic sun angle, specular/ambient lighting, bloom, team aura glow,
///     and 128-byte Vulkan push constants generation.
/// </summary>
public sealed class LightingController
{
    private readonly ILogger<LightingController> _logger;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.4f, 0.7f, 1.0f));
    public float AmbientFactor { get; set; } = 0.35f;
    public float SpecularFactor { get; set; } = 0.8f;
    public float BloomThreshold { get; set; } = 1.2f;
    public float TeamAuraIntensity { get; set; } = 0.5f;

    public Vector4 LegionTint { get; set; } = new(0.3f, 0.7f, 1.0f, 1.0f);
    public Vector4 HellbourneTint { get; set; } = new(1.0f, 0.4f, 0.2f, 1.0f);

    public LightingController(ILogger<LightingController>? logger = null)
    {
        _logger = logger ?? NullLogger<LightingController>.Instance;
        _logger.LogInformation("[LightingController] Initialized with default daylight configuration");
    }

    /// <summary>
    ///     Sets the sun direction from spherical azimuth and elevation angles in degrees.
    /// </summary>
    public void SetSunAngles(float azimuthDeg, float elevationDeg)
    {
        float azRad = azimuthDeg * (MathF.PI / 180.0f);
        float elRad = Math.Clamp(elevationDeg, 5.0f, 85.0f) * (MathF.PI / 180.0f);

        SunDirection = Vector3.Normalize(new Vector3(
            MathF.Cos(elRad) * MathF.Sin(azRad),
            MathF.Cos(elRad) * MathF.Cos(azRad),
            MathF.Sin(elRad)));

        _logger.LogDebug("[LightingController] Sun direction updated to {SunDir} (Az: {Az}, El: {El})", SunDirection, azimuthDeg, elevationDeg);
    }

    /// <summary>
    ///     Constructs a 128-byte LightingPushConstants struct for Vulkan pipeline dispatch.
    /// </summary>
    public LightingPushConstants BuildPushConstants(Matrix4x4 mvp, float timeSeconds, int team, Vector4 envParams = default)
    {
        Vector4 tint = team switch
        {
            1 => LegionTint,
            2 => HellbourneTint,
            _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
        };

        return new LightingPushConstants
        {
            ModelViewProjection = mvp,
            SunDirection = SunDirection,
            AmbientFactor = AmbientFactor,
            TeamTint = tint,
            SpecularFactor = SpecularFactor,
            BloomThreshold = BloomThreshold,
            TeamAuraGlow = TeamAuraIntensity,
            TimeSeconds = timeSeconds,
            EnvironmentalParams = envParams
        };
    }

    /// <summary>
    ///     Applies a preset lighting theme.
    /// </summary>
    public void LoadPreset(LightingPreset preset)
    {
        switch (preset)
        {
            case LightingPreset.MiddaySun:
                SetSunAngles(45.0f, 65.0f);
                AmbientFactor = 0.40f;
                SpecularFactor = 0.85f;
                BloomThreshold = 1.20f;
                TeamAuraIntensity = 0.50f;
                break;

            case LightingPreset.SunsetGoldenHour:
                SetSunAngles(240.0f, 18.0f);
                AmbientFactor = 0.28f;
                SpecularFactor = 1.10f;
                BloomThreshold = 0.95f;
                TeamAuraIntensity = 0.70f;
                break;

            case LightingPreset.NightMoonlight:
                SetSunAngles(315.0f, 45.0f);
                AmbientFactor = 0.15f;
                SpecularFactor = 0.40f;
                BloomThreshold = 1.40f;
                TeamAuraIntensity = 0.85f;
                break;

            case LightingPreset.HellbourneInferno:
                SetSunAngles(180.0f, 35.0f);
                AmbientFactor = 0.30f;
                SpecularFactor = 1.30f;
                BloomThreshold = 0.80f;
                TeamAuraIntensity = 1.00f;
                break;
        }

        _logger.LogInformation("[LightingController] Applied lighting preset '{Preset}'", preset);
    }
}
