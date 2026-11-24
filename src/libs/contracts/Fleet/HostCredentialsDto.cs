// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Fleet;

/// <summary>
///     Provisioned credentials and configuration returned to an approved server host.
/// </summary>
public sealed class HostCredentialsDto
{
    public required string HostUsername { get; set; }
    public required string HostPassword { get; set; }
    public required string Status { get; set; }
    public string Location { get; set; } = "USE";
    public int Instances { get; set; } = 2;
    public string ServerNamePrefix { get; set; } = "Community Server";
}
