// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Webhook callback payload dispatched by the Replay Video Renderer upon completing or failing a render job.
/// </summary>
public sealed class ReplayRenderCallbackDto
{
    /// <summary>
    ///     Unique match identifier for the rendered match replay.
    /// </summary>
    [JsonPropertyName("matchId")]
    public int MatchId { get; set; }

    /// <summary>
    ///     Final job completion status ("Completed", "Failed", "Cancelled").
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    ///     Public URL or relative path where the rendered video file is stored.
    /// </summary>
    [JsonPropertyName("videoUrl")]
    public string? VideoUrl { get; set; }

    /// <summary>
    ///     Total duration of the rendering pipeline execution in milliseconds.
    /// </summary>
    [JsonPropertyName("renderDurationMs")]
    public long RenderDurationMs { get; set; }

    /// <summary>
    ///     Detailed error message if the rendering pipeline encountered a failure.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
