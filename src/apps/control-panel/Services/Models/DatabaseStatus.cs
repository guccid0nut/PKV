// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.ControlPanel.GUI.Services.Models;

public sealed class DatabaseStatus
{
    public string DatabaseType { get; init; } = "SQL Server";
    public int Port { get; init; } = 1433;
    public bool IsConnected { get; init; }
    public long LatencyMs { get; init; }
    public string StatusMessage { get; init; } = "Checking...";
    public string StatusColor => IsConnected ? "#10B981" : "#EF4444"; // Emerald or Red
}
