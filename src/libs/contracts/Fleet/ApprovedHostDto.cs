// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Fleet;

/// <summary>
///     Representation of an approved community game server host.
/// </summary>
public sealed class ApprovedHostDto
{
    public int ApplicationId { get; set; }
    public int UserId { get; set; }
    public int? HostAccountId { get; set; }
    public string? HostAccountName { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string ServerLocation { get; set; } = string.Empty;
    public string? HostIpAddress { get; set; }
    public int Port { get; set; }
}
