// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http.Headers;
using KONGOR.MasterServer.Services.Storage;
using MERRICK.DatabaseContext.Entities.Statistics;
using NEXUS.Contracts.Security;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

internal sealed class FakeReplayStorageProvider : IReplayStorageProvider
{
    public Task<string> UploadReplayAsync(string matchId, Stream fileStream, string fileName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"https://mockcloud.com/replays/{fileName}");
    }
}

public sealed class ReplayStorageTestFixture : IDisposable
{
    public const string TestInternalServiceKey = "test-replay-storage-internal-key-2026";

    public ReplayStorageTestFixture()
    {
        Factory = KONGORServiceProvider.CreateOrchestratedInstance("ReplayStorageShared")
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("InternalServiceKey", TestInternalServiceKey);
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IReplayStorageProvider, FakeReplayStorageProvider>();
                });
            });
    }

    public WebApplicationFactory<KONGORAssemblyMarker> Factory { get; }

    public void Dispose()
    {
        try
        {
            Factory.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

public sealed class ReplayStorageTests(ReplayStorageTestFixture fixture) : IClassFixture<ReplayStorageTestFixture>
{
    private (HttpClient Client, MerrickContext DbContext) Setup(bool authenticated = true)
    {
        HttpClient client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false // Crucial for asserting 302 Found redirect
        });

        if (authenticated)
        {
            client.DefaultRequestHeaders.Add(
                SecurityConstants.InternalServiceKeyHeader,
                ReplayStorageTestFixture.TestInternalServiceKey);
        }

        IServiceScope scope = fixture.Factory.Services.CreateScope();
        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        return (client, dbContext);
    }

    [Fact]
    public async Task UploadReplay_DefaultInternalServiceKeyInProduction_Returns401Unauthorized()
    {
        using var prodFactory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("KONGOR_ALLOW_HTTP", "true");
            builder.UseSetting("InternalServiceKey", SecurityConstants.DefaultInternalServiceKey);
        });
        using HttpClient prodDefaultKeyClient = prodFactory.CreateClient();
        prodDefaultKeyClient.DefaultRequestHeaders.Add(
            SecurityConstants.InternalServiceKeyHeader,
            SecurityConstants.DefaultInternalServiceKey);

        using MultipartFormDataContent form = [];
        ByteArrayContent fileContent = new("RAW_REPLAY_BINARY_DATA"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(fileContent, "file", "M99999.honreplay");
        form.Add(new StringContent("99999"), "match_id");

        HttpResponseMessage response = await prodDefaultKeyClient.PostAsync("/replays/upload", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadReplay_Unauthenticated_Returns401Unauthorized()
    {
        (HttpClient unauthenticatedClient, _) = Setup(authenticated: false);

        using MultipartFormDataContent form = [];
        ByteArrayContent fileContent = new("RAW_REPLAY_BINARY_DATA"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(fileContent, "file", "M99999.honreplay");
        form.Add(new StringContent("99999"), "match_id");

        HttpResponseMessage response = await unauthenticatedClient.PostAsync("/replays/upload", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadReplay_MultipartForm_UpdatesDatabaseAndReturns200()
    {
        (HttpClient client, MerrickContext dbContext) = Setup();

        int matchId = Random.Shared.Next(100000, 999999);

        // Seed a MatchStatistics entity
        MatchStatistics match = new()
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "ServerHost",
            Map = "caldavar",
            MapVersion = "4.10.1",
            Version = "4.10.1",
            GameMode = "normal",
            TimePlayed = 1800,
            FileSize = 0,
            FileName = "",
            ConnectionState = 0,
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            ScoreTeam1 = 0,
            ScoreTeam2 = 0,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "Live",
            BannedHeroes = null,
            AwardMostAnnihilations = null,
            AwardMostQuadKills = null,
            AwardLargestKillStreak = null,
            AwardMostSmackdowns = null,
            AwardMostKills = null,
            AwardMostAssists = null,
            AwardLeastDeaths = null,
            AwardMostBuildingDamage = null,
            AwardMostWardsKilled = null,
            AwardMostHeroDamageDealt = null,
            AwardHighestCreepScore = null
        };
        dbContext.MatchStatistics.Add(match);
        await dbContext.SaveChangesAsync();

        using MultipartFormDataContent form = [];
        byte[] fakeReplayBytes = "RAW_REPLAY_BINARY_DATA"u8.ToArray();
        ByteArrayContent fileContent = new(fakeReplayBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(fileContent, "file", $"M{matchId}.honreplay");
        form.Add(new StringContent(matchId.ToString()), "match_id");

        HttpResponseMessage response = await client.PostAsync("/replays/upload", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Refresh match from DB using a fresh scope to avoid change tracker stale cache
        using IServiceScope queryScope = fixture.Factory.Services.CreateScope();
        MerrickContext queryContext = queryScope.ServiceProvider.GetRequiredService<MerrickContext>();
        MatchStatistics? updatedMatch =
            await queryContext.MatchStatistics.AsNoTracking().FirstOrDefaultAsync(m => m.MatchID == matchId);
        Assert.NotNull(updatedMatch);
        Assert.Equal(fakeReplayBytes.Length, updatedMatch.FileSize);
        Assert.Equal($"https://mockcloud.com/replays/M{matchId}.honreplay", updatedMatch.ReplayUrl);
    }

    [Fact]
    public async Task UploadReplay_MissingMatchId_ReturnsBadRequest()
    {
        (HttpClient client, _) = Setup();

        using MultipartFormDataContent form = [];
        ByteArrayContent fileContent = new("RAW_REPLAY_BINARY_DATA"u8.ToArray());
        form.Add(fileContent, "file", "M12345.honreplay");

        HttpResponseMessage response = await client.PostAsync("/replays/upload", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadReplay_MissingFile_ReturnsBadRequest()
    {
        (HttpClient client, _) = Setup();

        using MultipartFormDataContent form = new()
        {
            { new StringContent("12345"), "match_id" }
        };

        HttpResponseMessage response = await client.PostAsync("/replays/upload", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
