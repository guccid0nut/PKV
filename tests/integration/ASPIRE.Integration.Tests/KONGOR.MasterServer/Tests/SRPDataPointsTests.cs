// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using MERRICK.DatabaseContext.Entities.Statistics;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class SRPDataPointsTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task AuthenticateWithSRP_ReturnsCorrectDataPoints_WhenStatisticsExist()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;


        SRPAuthenticationService srpAuthenticationService = new(webApplicationFactory);

        string email = $"datapoints_{Guid.NewGuid():N}@kongor.com";

        string accountName = $"DPP_{Guid.NewGuid():N}"[..12];

        const string password = "Password123!";


        // 1. Create Account

        (Account account, _) =
            await srpAuthenticationService.CreateAccountWithSRPCredentials(email, accountName, password);


        // 2. Seed AccountStatistics

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        Account dbAccount = await databaseContext.Accounts.Include(a => a.User).FirstAsync(a => a.ID == account.ID);


        List<AccountStatistics> stats =

        [
            new()

            {
                AccountID = dbAccount.ID,

                Account = dbAccount,

                Type = AccountStatisticsType.Public,

                MatchesPlayed = 10,

                MatchesWon = 5,

                MatchesLost = 5,

                MatchesDisconnected = 1,

                SkillRating = 1600.5,

                PerformanceScore = 0.0,

                PlacementMatchesData = ""
            },

            new()

            {
                AccountID = dbAccount.ID,

                Account = dbAccount,

                Type = AccountStatisticsType.Matchmaking,

                MatchesPlayed = 20,

                MatchesWon = 12,

                MatchesLost = 8,

                MatchesDisconnected = 0,

                SkillRating = 1750.0,

                PerformanceScore = 0.0,

                PlacementMatchesData = ""
            },

            new()

            {
                AccountID = dbAccount.ID,

                Account = dbAccount,

                Type = AccountStatisticsType.MidWars,

                MatchesPlayed = 5,

                MatchesWon = 3,

                MatchesLost = 2,

                MatchesDisconnected = 2,

                SkillRating = 1400.0,

                PerformanceScore = 0.0,

                PlacementMatchesData = ""
            }
        ];


        await databaseContext.AccountStatistics.AddRangeAsync(stats);


        // Update Account AscensionLevel

        dbAccount.AscensionLevel = 5;

        // Update User Experience

        dbAccount.User.TotalExperience = 1000;


        await databaseContext.SaveChangesAsync();


        // 3. Perform Authentication

        using HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");


        // Pre-Auth

        SrpParameters parameters = SrpParameters.Create<SHA256>(SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
            SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);

        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpClient client = new(parameters);

        SrpEphemeral clientEphemeral = client.GenerateEphemeral();


        Dictionary<string, string> preAuthFormData = new()

        {
            { "login", accountName }, { "A", clientEphemeral.Public }, { "SysInfo", "test|sys|info|hash|mac" }
        };

        HttpResponseMessage preAuthResponse = await httpClient.PostAsync("client_requester.php?f=pre_auth",
            new FormUrlEncodedContent(preAuthFormData));

        string preAuthBody = await preAuthResponse.Content.ReadAsStringAsync();

        IDictionary<object, object>? preAuthData =
            PhpSerialization.Deserialize(preAuthBody) as IDictionary<object, object>;


        Assert.NotNull(preAuthData);


        string sessionSalt = preAuthData["salt"] as string ?? throw new InvalidOperationException("Salt is null");

        string passwordSalt = preAuthData["salt2"] as string ?? throw new InvalidOperationException("Salt2 is null");

        string serverPublicEphemeral = preAuthData["B"] as string ?? throw new InvalidOperationException("B is null");


        string passwordHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(password, passwordSalt);

        string privateClientKey = client.DerivePrivateKey(sessionSalt, accountName, passwordHash);

        SrpSession clientSession = client.DeriveSession(clientEphemeral.Secret, serverPublicEphemeral, sessionSalt,
            accountName, privateClientKey);


        // Auth

        Dictionary<string, string> authFormData = new()

        {
            { "login", accountName },

            { "proof", clientSession.Proof },

            { "OSType", "windows" },

            { "MajorVersion", "4" },

            { "MinorVersion", "10" },

            { "MicroVersion", "1" },

            { "SysInfo", "testhash|testhash|testhash|testhash|testhash" }
        };


        HttpResponseMessage authResponse =
            await httpClient.PostAsync("client_requester.php?f=srpAuth", new FormUrlEncodedContent(authFormData));

        string authResponseBody = await authResponse.Content.ReadAsStringAsync();


        Assert.True(authResponse.IsSuccessStatusCode);


        IDictionary<object, object>? stageTwoData =
            PhpSerialization.Deserialize(authResponseBody) as IDictionary<object, object>;


        // 4. Verify DataPoints

        Assert.NotNull(stageTwoData);

        Assert.True(stageTwoData.ContainsKey("infos"));


        object infos = stageTwoData["infos"];

        IDictionary<object, object>? dataPoint = null;


        if (infos is IList<object> list)
            dataPoint = list[0] as IDictionary<object, object>;

        else if (infos is IDictionary<object, object> dict)
            // PHP arrays can be deserialized as Dict<int, object> or Dict<string, object>
            dataPoint = dict.Values.Cast<IDictionary<object, object>>().First();


        Assert.NotNull(dataPoint);


        // Verify Mapped Fields

        Assert.Equal("5", dataPoint["level"]); // AscensionLevel

        Assert.Equal("1000", dataPoint["level_exp"]); // User.TotalExperience


        // Disconnects (Total = 1 + 0 + 2 = 3)

        Assert.Equal("3", dataPoint["discos"]);


        // MatchesPlayed (Total = 10 + 20 + 5 = 35)

        Assert.Equal("35", dataPoint["games_played"]);


        // Public Stats

        Assert.Equal("1600.500", dataPoint["acc_pub_skill"]);

        Assert.Equal("10", dataPoint["acc_games_played"]);

        Assert.Equal("5", dataPoint["acc_wins"]);

        Assert.Equal("5", dataPoint["acc_losses"]);

        Assert.Equal("1", dataPoint["acc_discos"]);


        // Matchmaking Stats

        Assert.Equal("1750.000", dataPoint["rnk_amm_team_rating"]);

        Assert.Equal("20", dataPoint["rnk_games_played"]);

        Assert.Equal("12", dataPoint["rnk_wins"]);

        Assert.Equal("8", dataPoint["rnk_losses"]);

        Assert.Equal("0", dataPoint["rnk_discos"]);


        // MidWars Stats

        Assert.Equal("1400.000", dataPoint["mid_amm_team_rating"]);

        Assert.Equal("5", dataPoint["mid_games_played"]);

        Assert.Equal("2", dataPoint["mid_discos"]);


        // Unset stats should be defaults

        Assert.Equal("1500.000", dataPoint["rift_amm_team_rating"]);

        Assert.Equal("0", dataPoint["rift_games_played"]);
    }
}
