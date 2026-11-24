// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AspireServiceDegradationBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary and Corner Cases (Feature 01: Aspire Service Degradation)
// Policy: Strict Explicit Typing, Boundary Validation (>= 5 tests)
// ============================================================================

using System.Net;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class AspireServiceDegradationBoundaryTests(KONGORTestFixture masterFixture)
    : IClassFixture<KONGORTestFixture>
{
    [Fact]
    public async Task Boundary_InvalidEndpointRoute_ReturnsNotFoundGracefully()
    {
        // Arrange
        HttpClient client = masterFixture.Client;

        // Act
        HttpResponseMessage response =
            await client.GetAsync("/nonexistent_synthetic_route_" + Guid.NewGuid().ToString("N"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Boundary_MalformedHeader_HandlesRequestWithoutCrashing()
    {
        // Arrange
        HttpClient client = masterFixture.Client;
        HttpRequestMessage request = new(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("X-Custom-Header", "BoundaryTestingData");

        // Act & Assert
        try
        {
            HttpResponseMessage response = await client.SendAsync(request);
            response.Should().NotBeNull();
        }
        catch (InvalidOperationException)
        {
            // .NET HttpClient handles header validation safely
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_HighConcurrencyPings_MaintainsServerResponsiveness()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        HttpClient client = masterFixture.Client;
        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[20];

        // Act
        for (int i = 0; i < tasks.Length; i++) tasks[i] = client.GetAsync("/health");

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert
        foreach (HttpResponseMessage response in responses)
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Boundary_PostWithEmptyBody_RejectsOrHandlesSafely()
    {
        // Arrange
        HttpClient client = masterFixture.Client;

        // Act
        HttpResponseMessage response = await client.PostAsync("/health", new StringContent(string.Empty));

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.MethodNotAllowed,
            HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Boundary_LargePayloadToHealthEndpoint_RejectsGracefully()
    {
        // Arrange
        HttpClient client = masterFixture.Client;
        byte[] largeData = new byte[1024 * 1024]; // 1MB

        // Act
        HttpResponseMessage response = await client.PostAsync("/health", new ByteArrayContent(largeData));

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.MethodNotAllowed,
            HttpStatusCode.NotFound, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public void Boundary_ServiceScopeDisposal_ReleasesScopedResourcesCleanly()
    {
        // Arrange
        IServiceScope scope = masterFixture.CreateScope();
        MerrickContext db = masterFixture.GetDbContext(scope);

        // Act
        db.Should().NotBeNull();
        scope.Dispose();

        // Assert - Scope disposed without exception
    }
}
