// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
///     Response to a latency probe ping against the COMPEL game server control plane.
/// </summary>
public sealed class CompelPingResponseDto
{
    /// <summary>
    ///     Application name (e.g. "COMPEL").
    /// </summary>
    [JsonPropertyName("application")]
    public string Application { get; set; } = string.Empty;

    /// <summary>
    ///     Control plane application version.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    ///     Unix timestamp in milliseconds at server evaluation time.
    /// </summary>
    [JsonPropertyName("serverTimeUnixMilliseconds")]
    public long ServerTimeUnixMilliseconds { get; set; }

    /// <summary>
    ///     Backward-compatible timestamp alias for GUI clients.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp
    {
        get => ServerTimeUnixMilliseconds;
        set => ServerTimeUnixMilliseconds = value;
    }
}
