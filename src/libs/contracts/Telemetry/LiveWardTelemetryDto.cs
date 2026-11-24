// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Active ward placement telemetry during a live match.
/// </summary>
public sealed class LiveWardTelemetryDto
{
    public string WardType { get; set; } = "Sight";
    public int Team { get; set; } = 1;
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float VisionRadius { get; set; } = 0.085f;
    public string PlacerHeroName { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; } = 360;

    public LiveWardTelemetryDto Clone()
    {
        return new LiveWardTelemetryDto
        {
            WardType = WardType,
            Team = Team,
            PositionX = PositionX,
            PositionY = PositionY,
            VisionRadius = VisionRadius,
            PlacerHeroName = PlacerHeroName,
            ExpiresInSeconds = ExpiresInSeconds
        };
    }

    public static implicit operator LiveWardTelemetryDto(LiveWardState s)
    {
        return new LiveWardTelemetryDto
        {
            WardType = s.WardType,
            Team = s.Team,
            PositionX = s.PositionX,
            PositionY = s.PositionY,
            VisionRadius = s.VisionRadius,
            PlacerHeroName = s.PlacerHeroName,
            ExpiresInSeconds = s.RemainingSeconds
        };
    }

    public static implicit operator LiveWardState(LiveWardTelemetryDto d)
    {
        return new LiveWardState
        {
            WardType = d.WardType,
            Team = d.Team,
            PositionX = d.PositionX,
            PositionY = d.PositionY,
            VisionRadius = d.VisionRadius,
            PlacerHeroName = d.PlacerHeroName,
            DurationSeconds = d.ExpiresInSeconds,
            PlacedAtSecond = 0,
            CurrentElapsedSeconds = 0
        };
    }
}
