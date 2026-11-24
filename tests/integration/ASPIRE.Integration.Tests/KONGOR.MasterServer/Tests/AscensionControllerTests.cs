// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class AscensionControllerTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private readonly HttpClient _client = fixture.Client;


    [Fact]
    public async Task CheckMatch_ReturnsSuccess_WhenMatchIdProvided()

    {
        HttpResponseMessage response = await _client.GetAsync("/?r=api/match/checkmatch&match_id=123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        string content = await response.Content.ReadAsStringAsync();

        Assert.Contains("error_code", content);

        Assert.Contains("100", content);

        Assert.Contains("is_season_match", content);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task CheckMatch_ReturnsBadRequest_WhenMatchIdMissing()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        HttpResponseMessage response = await _client.GetAsync("/?r=api/match/checkmatch");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
