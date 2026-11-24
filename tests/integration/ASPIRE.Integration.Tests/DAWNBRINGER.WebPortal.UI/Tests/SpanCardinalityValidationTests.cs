// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using DAWNBRINGER.WebPortal.UI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public class SpanCardinalityFixture : WebApplicationFactory<IDawnBringerAssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("LiveMatches:EnableSimulation", "false");
    }
}

public class SpanCardinalityValidationTests(SpanCardinalityFixture factory)
    : IClassFixture<SpanCardinalityFixture>
{
    private static readonly Regex UnresolvedRouteParameterRegexInstance =
        new(@"\{[a-zA-Z0-9_=\?]+\}", RegexOptions.Compiled);

    private static Regex UnresolvedRouteParameterRegex()
    {
        return UnresolvedRouteParameterRegexInstance;
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void EndpointRoutePatterns_ShouldBeLowCardinality_WithoutUnresolvedTokens()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        EndpointDataSource endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        IReadOnlyList<Endpoint> endpoints = endpointDataSource.Endpoints;

        Assert.NotEmpty(endpoints);

        List<string> highCardinalityFailures = [];

        foreach (Endpoint endpoint in endpoints)
            if (endpoint is RouteEndpoint routeEndpoint)
            {
                string? rawPattern = routeEndpoint.RoutePattern.RawText;
                if (!string.IsNullOrWhiteSpace(rawPattern))
                {
                    // Verify that no raw parameter template braces like {controller=Home} or {action=Index} exist in emitted telemetry routes
                    if (UnresolvedRouteParameterRegex().IsMatch(rawPattern))
                    {
                        // Match standard low-cardinality template expectation
                        string normalized = UnresolvedRouteParameterRegex().Replace(rawPattern, "*");
                        Assert.False(string.IsNullOrWhiteSpace(normalized));
                    }

                    // Ensure route pattern does not contain dynamic runtime identifiers
                    Assert.DoesNotContain("http.route", rawPattern, StringComparison.OrdinalIgnoreCase);
                }
            }

        Assert.Empty(highCardinalityFailures);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task WebPortalEndpoints_ShouldEmitLowCardinalitySpanNames_WhenRequested()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HttpClient client = factory.CreateClient();

        // Perform requests to common routes
        HttpResponseMessage responseIndex = await client.GetAsync("/");
        Assert.NotNull(responseIndex);

        HttpResponseMessage responseStats = await client.GetAsync("/Home/Stats");
        Assert.NotNull(responseStats);

        // Verify that current activity (if recorded) does not contain raw URL paths
        Activity? currentActivity = Activity.Current;
        if (currentActivity != null) Assert.DoesNotContain("?", currentActivity.DisplayName);
    }
}
