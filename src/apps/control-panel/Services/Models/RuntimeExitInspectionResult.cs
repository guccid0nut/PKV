// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.ControlPanel.GUI.Services.Models;

public sealed record RuntimeExitInspectionResult(
    bool HasRunningDockerServices,
    int RunningDockerContainerCount,
    IReadOnlyList<string> RunningDockerContainerNames,
    bool IsDockerStackActive,
    bool HasRunningLocalHostServices,
    string SummaryMessage)
{
    public static RuntimeExitInspectionResult Idle { get; } = new(
        HasRunningDockerServices: false,
        RunningDockerContainerCount: 0,
        RunningDockerContainerNames: [],
        IsDockerStackActive: false,
        HasRunningLocalHostServices: false,
        SummaryMessage: "All background services are stopped.");

    public bool HasAnyActiveServices => HasRunningDockerServices || HasRunningLocalHostServices;
}
