// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Fleet;

/// <summary>
///     User request payload for submitting a new game server host application.
/// </summary>
public sealed class ApplyHostRequestDto
{
    public string Location { get; set; } = "USE";
    public int Instances { get; set; } = 2;
    public string ServerNamePrefix { get; set; } = "Community Server";
    public string? ApplicationNotes { get; set; }
}
