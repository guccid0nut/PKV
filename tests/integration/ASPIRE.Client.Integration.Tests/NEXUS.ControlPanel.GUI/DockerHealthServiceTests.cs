// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using Xunit;

namespace ASPIRE.Client.Integration.Tests.NEXUS.ControlPanel.GUI;

public sealed class DockerHealthServiceTests
{
    [Fact]
    public void ResolveDockerCliPath_ReturnsValidString()
    {
        string cliPath = DockerHealthService.ResolveDockerCliPath();
        Assert.False(string.IsNullOrWhiteSpace(cliPath));
    }

    [Fact]
    public void IsDockerDaemonRunningFast_ExecutesInstantlyWithoutThrowing()
    {
        Stopwatch sw = Stopwatch.StartNew();
        bool isRunning = DockerHealthService.IsDockerDaemonRunningFast();
        sw.Stop();

        // Must execute in < 250ms (never block or hang on child processes)
        Assert.True(sw.ElapsedMilliseconds < 250, $"IsDockerDaemonRunningFast took {sw.ElapsedMilliseconds}ms, expected < 250ms");
    }

    [Fact]
    public async Task CheckDockerStatusAsync_ReturnsStatusFast()
    {
        DockerHealthService service = new();
        Stopwatch sw = Stopwatch.StartNew();
        DockerStatus status = await service.CheckDockerStatusAsync();
        sw.Stop();

        Assert.NotNull(status);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"CheckDockerStatusAsync took {sw.ElapsedMilliseconds}ms, expected < 1500ms");

        // When daemon is running on the host, verify status is positive
        if (DockerHealthService.IsDockerDaemonRunningFast())
        {
            Assert.True(status.IsRunning);
            Assert.True(status.IsInstalled);
            Assert.Contains("running and ready", status.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(status.Version));
        }
    }

    [Fact]
    public async Task CheckDockerStatusAsync_CachesResultForSubsequentCalls()
    {
        DockerHealthService service = new();
        DockerStatus status1 = await service.CheckDockerStatusAsync();

        Stopwatch sw = Stopwatch.StartNew();
        DockerStatus status2 = await service.CheckDockerStatusAsync();
        sw.Stop();

        Assert.Same(status1, status2);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Cached call took {sw.ElapsedMilliseconds}ms, expected < 50ms");
    }
}
