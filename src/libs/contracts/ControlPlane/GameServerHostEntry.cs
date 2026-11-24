// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
///     Shared descriptor for a discovered or configured COMPEL game server host node used by
///     both NEXUS.ControlPanel.GUI and COMPEL.GUI.
/// </summary>
public sealed class GameServerHostEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Local Host";
    public string HostAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string Location { get; set; } = "USE";
    public string AuthToken { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
}
