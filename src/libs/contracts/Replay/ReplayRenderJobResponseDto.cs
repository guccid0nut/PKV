// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Response payload returned by the Replay Video Renderer service upon job submission or status query.
/// </summary>
public sealed class ReplayRenderJobResponseDto
{
    /// <summary>
    ///     Unique identifier assigned to the rendering job.
    /// </summary>
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    ///     Current job execution status ("Pending", "Rendering", "Completed", "Failed", "Cancelled").
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Pending";

    /// <summary>
    ///     Descriptive message or error detail regarding job processing.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    ///     Estimated time remaining in seconds until render completion.
    /// </summary>
    [JsonPropertyName("estimatedDurationSeconds")]
    public double EstimatedDurationSeconds { get; set; }
}
