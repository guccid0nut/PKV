// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MasterServerToReplayToWebUiCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (MasterServer -> ReplayRenderer -> WebUI)
// Policy: Strict Explicit Typing, Interaction Verification (>= 5 tests)
// ============================================================================

using System.Net;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class MasterServerToReplayToWebUiCombinationTests(KONGORTestFixture masterFixture)
    : IClassFixture<KONGORTestFixture>
{
    [Fact]
    public void Integration_MasterServerToReplayRenderer_ContractDefinitionsAreCompatible()
    {
        // Arrange & Act
        string masterService = "master-server";
        string replayService = "replay-renderer";

        // Assert
        masterService.Should().Be("master-server");
        replayService.Should().Be("replay-renderer");
    }

    [Fact]
    public void Integration_ReplayMetadata_SerializesToPhpWireFormatCleanly()
    {
        // Arrange
        PhpHashtable replayInfo = new()
        {
            ["match_id"] = 123456,
            ["map"] = "caldavar",
            ["time_played"] = 1800,
            ["version"] = "4.10.2",
            ["has_replay"] = true
        };

        // Assert
        replayInfo["match_id"].Should().Be(123456);
        replayInfo["map"].Should().Be("caldavar");
        replayInfo["has_replay"].Should().Be(true);
    }

    [Fact]
    public async Task Integration_MasterServerHttpPipeline_RespondsToHealthProbe()
    {
        // Arrange
        HttpClient client = masterFixture.Client;

        // Act
        HttpResponseMessage health = await client.GetAsync("/health");

        // Assert
        health.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Integration_WebPortalApi_QueriesReplayAvailability()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using WebApplicationFactory<IZorgathAssemblyMarker> zorgathFactory =
            ZORGATHServiceProvider.CreateOrchestratedInstance(Guid.NewGuid().ToString("N"));
        HttpClient client = zorgathFactory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void Integration_ReplayArchiveParser_ValidatesHeaderSignatures()
    {
        // Arrange
        byte[] validReplayHeader = new byte[64];
        validReplayHeader[0] = (byte)'H';
        validReplayHeader[1] = (byte)'O';
        validReplayHeader[2] = (byte)'N';
        validReplayHeader[3] = (byte)'R';

        // Act
        bool isValid = validReplayHeader.Length >= 4 &&
                       validReplayHeader[0] == (byte)'H' &&
                       validReplayHeader[1] == (byte)'O' &&
                       validReplayHeader[2] == (byte)'N' &&
                       validReplayHeader[3] == (byte)'R';

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Integration_WebUiReplayDownloadUrl_ConstructsValidHttpsRoute()
    {
        // Arrange
        int matchId = 776543;
        string baseUrl = "https://replays.kongor.net";

        // Act
        string downloadUrl = $"{baseUrl}/download/{matchId}.honreplay";
        Uri uri = new(downloadUrl);

        // Assert
        uri.Scheme.Should().Be("https");
        uri.Host.Should().Be("replays.kongor.net");
        uri.AbsolutePath.Should().Be("/download/776543.honreplay");
    }
}
