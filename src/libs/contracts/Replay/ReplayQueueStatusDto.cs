// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Detailed state of a replay rendering job.
/// </summary>
public sealed class ReplayRenderJobItemDto
{
    [JsonPropertyName("jobId")] public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("matchId")] public int MatchId { get; set; }

    [JsonPropertyName("replayFilePath")] public string ReplayFilePath { get; set; } = string.Empty;

    [JsonPropertyName("mapName")] public string MapName { get; set; } = "Forests of Caldavar";

    [JsonPropertyName("targetAccountId")] public int TargetAccountId { get; set; }

    [JsonPropertyName("targetPlayerIndex")] public int TargetPlayerIndex { get; set; } = -1;

    [JsonPropertyName("targetPlayerName")] public string TargetPlayerName { get; set; } = "Free Cam";

    [JsonPropertyName("targetHeroName")] public string TargetHeroName { get; set; } = string.Empty;

    [JsonPropertyName("cameraMode")] public string CameraMode { get; set; } = "PlayerPOV";

    [JsonPropertyName("resolution")] public string Resolution { get; set; } = "1080p";

    [JsonPropertyName("framerate")] public int Framerate { get; set; } = 60;

    [JsonPropertyName("showHud")] public bool ShowHud { get; set; } = true;

    [JsonPropertyName("startMs")] public int StartMs { get; set; }

    [JsonPropertyName("durationMs")] public int DurationMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Queued"; // Queued, Processing, Completed, Failed, Cancelled

    [JsonPropertyName("progressPercent")] public double ProgressPercent { get; set; }

    [JsonPropertyName("currentFrame")] public long CurrentFrame { get; set; }

    [JsonPropertyName("totalFrames")] public long TotalFrames { get; set; }

    [JsonPropertyName("fps")] public double Fps { get; set; }

    [JsonPropertyName("elapsedSeconds")] public double ElapsedSeconds { get; set; }

    [JsonPropertyName("estimatedRemainingSeconds")]
    public double EstimatedRemainingSeconds { get; set; }

    [JsonPropertyName("outputPath")] public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("videoUrl")] public string? VideoUrl { get; set; }

    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = TimeProvider.System.GetUtcNow();

    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
///     Status of the complete Replay Rendering queue & active worker.
/// </summary>
public sealed class ReplayQueueStatusDto
{
    [JsonPropertyName("isRendererOnline")] public bool IsRendererOnline { get; set; }

    [JsonPropertyName("activeJob")] public ReplayRenderJobItemDto? ActiveJob { get; set; }

    [JsonPropertyName("queuedJobs")] public List<ReplayRenderJobItemDto> QueuedJobs { get; set; } = [];

    [JsonPropertyName("completedJobs")] public List<ReplayRenderJobItemDto> CompletedJobs { get; set; } = [];

    [JsonPropertyName("totalQueuedCount")] public int TotalQueuedCount => QueuedJobs.Count;

    [JsonPropertyName("totalCompletedCount")]
    public int TotalCompletedCount => CompletedJobs.Count;
}
