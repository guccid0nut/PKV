// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
///     Outcome of a lifecycle action (synchronise, start, stop, or restart) executed against the COMPEL control plane.
/// </summary>
public sealed class CompelActionResponseDto
{
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;

    [JsonPropertyName("success")] public bool Success { get; set; }

    /// <summary>
    ///     Compatibility alias for 'Success'.
    /// </summary>
    [JsonPropertyName("accepted")]
    public bool Accepted
    {
        get => Success;
        set => Success = value;
    }

    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     Compatibility alias for 'Message'.
    /// </summary>
    [JsonPropertyName("detail")]
    public string Detail
    {
        get => Message;
        set => Message = value;
    }
}
