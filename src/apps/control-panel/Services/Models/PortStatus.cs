// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.ControlPanel.GUI.Services.Models;

public sealed class PortStatus
{
    public int Port { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string Protocol { get; init; } = "TCP";
    public bool IsAvailable { get; set; }
    public string StatusText => IsAvailable ? "Available" : "In Use / Busy";
}
