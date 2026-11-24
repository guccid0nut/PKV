// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRenderJob.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using NEXUS.Contracts.Replay;

namespace ReplayRenderer.Service.Models;

/// <summary>
///     Represents a replay rendering job queued for background processing.
/// </summary>
public sealed class ReplayRenderJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public int MatchId { get; set; }
    public string ReplayFilePath { get; set; } = string.Empty;
    public int TargetAccountId { get; set; }
    public int TargetPlayerIndex { get; set; } = -1;
    public string? TargetPlayerName { get; set; }
    public string CameraMode { get; set; } = "PlayerPOV";
    public string Resolution { get; set; } = "1080p";
    public int Framerate { get; set; } = 60;
    public bool ShowHud { get; set; } = true;
    public int StartMs { get; set; }
    public int DurationMs { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public string? CallbackUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public long RenderDurationMs { get; set; }
}

/// <summary>
///     Ingestion request payload accepted by POST /render (aliased to centralized contract).
/// </summary>
public sealed class ReplayRenderJobRequest
{
    public int MatchId { get; set; }
    public string ReplayFilePath { get; set; } = string.Empty;
    public int TargetAccountId { get; set; }
    public int TargetPlayerIndex { get; set; } = -1;
    public string? TargetPlayerName { get; set; }
    public string CameraMode { get; set; } = "PlayerPOV";
    public string Resolution { get; set; } = "1080p";
    public int Framerate { get; set; } = 60;
    public bool ShowHud { get; set; } = true;
    public int StartMs { get; set; }
    public int DurationMs { get; set; }
    public string? OutputPath { get; set; }
    public string? CallbackUrl { get; set; }

    public static implicit operator ReplayRenderJobRequestDto(ReplayRenderJobRequest req)
    {
        return new ReplayRenderJobRequestDto
        {
            MatchId = req.MatchId,
            ReplayFilePath = req.ReplayFilePath,
            TargetAccountId = req.TargetAccountId,
            TargetPlayerIndex = req.TargetPlayerIndex,
            TargetPlayerName = req.TargetPlayerName,
            CameraMode = req.CameraMode,
            Resolution = req.Resolution,
            Framerate = req.Framerate,
            ShowHud = req.ShowHud,
            StartMs = req.StartMs,
            DurationMs = req.DurationMs,
            OutputPath = req.OutputPath,
            CallbackUrl = req.CallbackUrl
        };
    }

    public static implicit operator ReplayRenderJobRequest(ReplayRenderJobRequestDto dto)
    {
        return new ReplayRenderJobRequest
        {
            MatchId = dto.MatchId,
            ReplayFilePath = dto.ReplayFilePath,
            TargetAccountId = dto.TargetAccountId,
            TargetPlayerIndex = dto.TargetPlayerIndex,
            TargetPlayerName = dto.TargetPlayerName,
            CameraMode = dto.CameraMode,
            Resolution = dto.Resolution,
            Framerate = dto.Framerate,
            ShowHud = dto.ShowHud,
            StartMs = dto.StartMs,
            DurationMs = dto.DurationMs,
            OutputPath = dto.OutputPath,
            CallbackUrl = dto.CallbackUrl
        };
    }
}
