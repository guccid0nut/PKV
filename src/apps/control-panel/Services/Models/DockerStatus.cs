// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.ControlPanel.GUI.Services.Models;

public sealed class DockerStatus
{
    public bool IsInstalled { get; set; }
    public bool IsRunning { get; set; }
    public string Version { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = "Unknown";
}
