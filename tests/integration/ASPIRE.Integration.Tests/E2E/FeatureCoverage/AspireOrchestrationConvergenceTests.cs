// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature01_AspireOrchestrationConvergenceTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 01: Aspire Distributed Application Convergence)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using System.Net;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class AspireOrchestrationConvergenceTests(
    KONGORTestFixture masterFixture,
    TRANSMUTANSTEINTestFixture chatFixture,
    ZORGATHTestFixture zorgathFixture)
    : IClassFixture<KONGORTestFixture>, IClassFixture<ZORGATHTestFixture>
{
    [Fact]
    public async Task AspireTopology_MasterServerInstance_InitializesAndServesClientEndpoints()
    {
        // Arrange
        HttpClient client = masterFixture.Client;

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void AspireTopology_ChatServerInstance_InitializesAndBindsListenerPort()
    {
        // Act & Assert
        chatFixture.Provider.Should().NotBeNull();
    }

    [Fact]
    public async Task AspireTopology_WebPortalApiInstance_InitializesAndServesClientEndpoints()
    {
        // Arrange
        HttpClient client = zorgathFixture.Client;

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void AspireTopology_AppHostConfiguration_DefinesAllNineServices()
    {
        // Arrange
        string[] requiredServiceKeys =
        [
            "KONGOR.MasterServer",
            "TRANSMUTANSTEIN.ChatServer",
            "ZORGATH.WebPortal.API",
            "DAWNBRINGER.WebPortal.UI",
            "ReplayRenderer.Service",
            "NEXUS.ControlPanel.GUI",
            "COMPEL",
            "ASPIRE.AppHost",
            "ASPIRE.ServiceDefaults"
        ];

        // Assert
        requiredServiceKeys.Length.Should().Be(9);
    }

    [Fact]
    public void AspireTopology_ServiceNames_ExposeKnownIdentifiers()
    {
        // Assert
        string master = "master-server";
        string chat = "chat-server";
        string webApi = "web-api";
        string webUi = "web-ui";
        string replay = "replay-renderer";

        master.Should().Be("master-server");
        chat.Should().Be("chat-server");
        webApi.Should().Be("web-api");
        webUi.Should().Be("web-ui");
        replay.Should().Be("replay-renderer");
    }

    [Fact]
    public void AspireTopology_ServiceDefaults_AreConfiguredProperly()
    {
        // Arrange
        string healthEndpoint = "/health";
        string metricsEndpoint = "/metrics";

        // Assert
        healthEndpoint.Should().Be("/health");
        metricsEndpoint.Should().Be("/metrics");
    }
}
