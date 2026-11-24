// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Ingestion request payload accepted by the Replay Video Renderer service (POST /render).
/// </summary>
public sealed class ReplayRenderJobRequestDto
{
    /// <summary>
    ///     Unique match identifier whose replay should be rendered.
    /// </summary>
    [JsonPropertyName("matchId")]
    public int MatchId { get; set; }

    /// <summary>
    ///     File path to the .honreplay archive on disk.
    /// </summary>
    [JsonPropertyName("replayFilePath")]
    public string ReplayFilePath { get; set; } = string.Empty;

    /// <summary>
    ///     Target Account ID to lock the camera POV on (0 for FreeCam/ActionCam).
    /// </summary>
    [JsonPropertyName("targetAccountId")]
    public int TargetAccountId { get; set; }

    /// <summary>
    ///     Target Player Index (0-9) to lock camera perspective to.
    /// </summary>
    [JsonPropertyName("targetPlayerIndex")]
    public int TargetPlayerIndex { get; set; } = -1;

    /// <summary>
    ///     Target Player Name for display and metadata.
    /// </summary>
    [JsonPropertyName("targetPlayerName")]
    public string? TargetPlayerName { get; set; }

    /// <summary>
    ///     Camera Mode: "PlayerPOV", "ActionCam", "FreeCam", "Overview".
    /// </summary>
    [JsonPropertyName("cameraMode")]
    public string CameraMode { get; set; } = "PlayerPOV";

    /// <summary>
    ///     Video Resolution preset: "1080p", "1440p", "4k".
    /// </summary>
    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "1080p";

    /// <summary>
    ///     Target video framerate: 60 or 120 fps.
    /// </summary>
    [JsonPropertyName("framerate")]
    public int Framerate { get; set; } = 60;

    /// <summary>
    ///     Whether in-game HUD is shown in the rendered video.
    /// </summary>
    [JsonPropertyName("showHud")]
    public bool ShowHud { get; set; } = true;

    /// <summary>
    ///     Playback start offset in milliseconds (0 = start of replay).
    /// </summary>
    [JsonPropertyName("startMs")]
    public int StartMs { get; set; }

    /// <summary>
    ///     Target clip duration in milliseconds (0 = full remaining match length).
    /// </summary>
    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; }

    /// <summary>
    ///     Optional target destination path for the rendered MP4 video.
    /// </summary>
    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; set; }

    /// <summary>
    ///     Optional webhook callback URL invoked when rendering completes or fails.
    /// </summary>
    [JsonPropertyName("callbackUrl")]
    public string? CallbackUrl { get; set; }
}
