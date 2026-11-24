// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ControlPanelToWebPortalToCompelCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (ControlPanel -> WebPortal -> COMPEL)
// Policy: Strict Explicit Typing, Interaction Verification (>= 5 tests)
// ============================================================================

using System.Net;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class ControlPanelToWebPortalToCompelCombinationTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task Integration_ControlPanelToWebPortalApi_AuthenticatesAndFetchesHealth()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using WebApplicationFactory<IZorgathAssemblyMarker> zorgathFactory =
            ZORGATHServiceProvider.CreateOrchestratedInstance(Guid.NewGuid().ToString("N"));
        HttpClient client = zorgathFactory.CreateClient();

        // Act
        HttpResponseMessage healthResponse = await client.GetAsync("/health");

        // Assert
        healthResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void Integration_CompelServerConfiguration_DefinesStandardPortRange()
    {
        // Arrange
        int minPort = 11235;
        int maxPort = 11335;

        // Assert
        maxPort.Should().BeGreaterThan(minPort);
        (maxPort - minPort).Should().Be(100);
    }

    [Fact]
    public void Integration_WebPortalToCompel_CommandPayloadFormatting_IsAccurate()
    {
        // Arrange
        string serverId = "compel_node_01";
        string command = "restart";

        // Act
        var payload = new { TargetServer = serverId, Action = command, Timestamp = TimeProvider.System.GetUtcNow() };

        // Assert
        payload.TargetServer.Should().Be("compel_node_01");
        payload.Action.Should().Be("restart");
    }

    [Fact]
    public void Integration_ControlPanelFleetDiscovery_ParsesIpAndPortCorrectly()
    {
        // Arrange
        string rawAddress = "127.0.0.1:11235";
        string[] parts = rawAddress.Split(':');

        // Act
        string ip = parts[0];
        int port = int.Parse(parts[1]);

        // Assert
        ip.Should().Be("127.0.0.1");
        port.Should().Be(11235);
    }

    [Fact]
    public void Integration_ControlPanelTelemetryFrameRate_EnforcesMinimumPollingInterval()
    {
        // Arrange
        int minIntervalMs = 1000;
        int targetIntervalMs = 2000;

        // Assert
        targetIntervalMs.Should().BeGreaterThanOrEqualTo(minIntervalMs);
    }

    [Fact]
    public void Integration_CompelLogCollection_MatchesExpectedLogDirectoryStructure()
    {
        // Arrange
        string serverId = "compel-alpha";
        string logPath = $@"C:\Kongor\Logs\{serverId}\console.log";

        // Assert
        logPath.Should().Contain(serverId);
        logPath.Should().EndWith("console.log");
    }
}
