// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayStudioCliOptions.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ReplayStudio.GUI.Models;

/// <summary>
///     CLI arguments and runtime configuration passed to the Replay Studio application.
/// </summary>
public sealed class ReplayStudioCliOptions
{
    public string? ReplayPath { get; set; }
    public int InitialTimestampSec { get; set; }
    public CameraMode InitialCameraMode { get; set; } = CameraMode.PlayerPov;
    public int? TargetHeroSlot { get; set; }
    public bool AutoPlay { get; set; }
    public float PlaybackSpeed { get; set; } = 1.0f;

    /// <summary>
    ///     Parses command-line arguments into structured ReplayStudioCliOptions.
    /// </summary>
    public static ReplayStudioCliOptions Parse(string[]? args)
    {
        ReplayStudioCliOptions options = new();
        if (args == null || args.Length == 0) return options;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--replay", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.ReplayPath = args[++i];
            }
            else if (string.Equals(arg, "--timestamp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int ts)) options.InitialTimestampSec = Math.Max(0, ts);
            }
            else if (string.Equals(arg, "--camera", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string cam = args[++i];
                if (Enum.TryParse(cam, true, out CameraMode mode))
                {
                    options.InitialCameraMode = mode;
                }
                else if (string.Equals(cam, "free", StringComparison.OrdinalIgnoreCase))
                {
                    options.InitialCameraMode = CameraMode.FreeSpectator;
                }
                else if (string.Equals(cam, "rts", StringComparison.OrdinalIgnoreCase))
                {
                    options.InitialCameraMode = CameraMode.RtsIsometric;
                }
                else if (string.Equals(cam, "firstperson", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(cam, "1st", StringComparison.OrdinalIgnoreCase))
                {
                    options.InitialCameraMode = CameraMode.FirstPerson;
                }
                else if (string.Equals(cam, "spline", StringComparison.OrdinalIgnoreCase))
                {
                    options.InitialCameraMode = CameraMode.CinematicSpline;
                }
                else if (string.Equals(cam, "chase", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(cam, "pov", StringComparison.OrdinalIgnoreCase))
                {
                    options.InitialCameraMode = CameraMode.PlayerPov;
                }
            }
            else if (string.Equals(arg, "--hero", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string heroStr = args[++i];
                if (int.TryParse(heroStr, out int slot)) options.TargetHeroSlot = slot;
            }
            else if (string.Equals(arg, "--autoplay", StringComparison.OrdinalIgnoreCase))
            {
                options.AutoPlay = true;
            }
            else if (string.Equals(arg, "--speed", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], out float spd)) options.PlaybackSpeed = Math.Clamp(spd, 0.25f, 8.0f);
            }
        }

        return options;
    }
}
