// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Store;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Mastery;
using MERRICK.DatabaseContext.Entities.Statistics;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class ProfileMasteryReplayAwardTests

{
    [Fact]
    public void ProfileMasteryReplayAward_StoreItemsConfiguration_ExcludesProduct6000()

    {
        StoreItemsConfiguration config = JSONConfiguration.StoreItemsConfiguration;

        Assert.NotNull(config);

        StoreItem? item6000 = config.StoreItems.FirstOrDefault(x => x.ID == 6000);

        Assert.Null(item6000);
    }


    [Fact]
    public void ProfileMasteryReplayAward_MasteryRewardService_GeneratesCommaSeparatedMasteryInfoString()

    {
        MasteryRewardService service = new();

        Mock<IHeroDefinitionService> mockHeroService = new();


        string masteryString = service.GenerateMasteryInfoString(null, mockHeroService.Object);


        Assert.NotNull(masteryString);

        Assert.NotEmpty(masteryString);

        string[] tokens = masteryString.Split(',');

        Assert.True(tokens.Length >= 139 * 2, $"Expected at least {139 * 2} tokens, got {tokens.Length}");

        Assert.Contains("Hero_Accursed", tokens);
    }


    [Fact]
    public void ProfileMasteryReplayAward_MasteryRewardService_GeneratesCommaSeparatedMasteryRewardsString()

    {
        MasteryRewardService service = new();


        string rewardsString = service.GenerateMasteryRewardsString();


        Assert.NotNull(rewardsString);

        Assert.NotEmpty(rewardsString);

        string[] tokens = rewardsString.Split(',');

        Assert.True(tokens.Length >= 9, $"Expected multiple reward tokens, got {tokens.Length}");

        Assert.Equal(0,
            tokens.Length % 9); // Must be exact multiple of 9 tokens per tier (matching cgame_x64.dll loop step)


        // Verify there are no empty tokens (which break K2 C++ Tokenize and shift fields)

        foreach (string token in tokens)
            Assert.False(string.IsNullOrEmpty(token),
                "Found empty token in mastery rewards string which corrupts K2 C++ tokenization.");
    }


    [Fact]
    public void ProfileMasteryReplayAward_MasteryRewardService_SerializesCurrencyTiersWithZeroForMissingFields()

    {
        MasteryRewardService service = new();


        string rewardsString = service.GenerateMasteryRewardsString();

        string[] tokens = rewardsString.Split(',');


        // Each tier: [0]=level, [1]=alreadygot, [2]=product_id, [3]=product_name, [4]=icon, [5]=quantity, [6]=gold, [7]=silver, [8]=plinko

        for (int i = 0; i < tokens.Length; i += 9)

        {
            string levelStr = tokens[i];

            string alreadyGotStr = tokens[i + 1];

            string productIdStr = tokens[i + 2];

            string productName = tokens[i + 3];

            string productIcon = tokens[i + 4];

            string quantityStr = tokens[i + 5];

            string goldStr = tokens[i + 6];

            string silverStr = tokens[i + 7];

            string plinkoStr = tokens[i + 8];


            Assert.True(int.TryParse(levelStr, out _), $"Level '{levelStr}' at index {i} must be integer");

            Assert.True(alreadyGotStr is "true" or "false", $"AlreadyGot '{alreadyGotStr}' must be boolean");

            Assert.NotEmpty(productName);

            Assert.NotEmpty(productIcon);

            Assert.True(int.TryParse(quantityStr, out _), $"Quantity '{quantityStr}' must be integer");

            Assert.True(int.TryParse(goldStr, out _), $"Gold '{goldStr}' must be integer");

            Assert.True(int.TryParse(silverStr, out _), $"Silver '{silverStr}' must be integer");

            Assert.True(int.TryParse(plinkoStr, out _), $"Plinko '{plinkoStr}' must be integer");
        }
    }


    [Fact]
    public void ProfileMasteryReplayAward_MatchSummary_ResolvesFallbackServerNameAndReplayUrl()

    {
        Environment.SetEnvironmentVariable("INFRASTRUCTURE_GATEWAY", "localhost");


        MatchStatistics matchStats = new()

        {
            MatchID = 12345,

            TimestampRecorded = TimeProvider.System.GetUtcNow().UtcDateTime,

            ServerID = 7,

            Map = "caldavar",

            MapVersion = "1.0",

            Version = "4.10.1",

            GameMode = "bot_ranked",

            TimePlayed = 1800,

            HostAccountName = "NexusHost",

            FileSize = 1048576,

            ReplayUrl = "http://localhost/replays/M12345.honreplay",

            FileName = "M12345.honreplay",

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "0",

            BannedHeroes = null,

            AwardMostAnnihilations = 0,

            AwardMostQuadKills = 0,

            AwardLargestKillStreak = 0,

            AwardMostSmackdowns = 0,

            AwardMostKills = 0,

            AwardMostAssists = 0,

            AwardLeastDeaths = 0,

            AwardMostBuildingDamage = 0,

            AwardMostWardsKilled = 0,

            AwardMostHeroDamageDealt = 0,

            AwardHighestCreepScore = 0
        };


        MatchSummary summary = new(matchStats, [], null);


        Assert.Equal("NexusHost", summary.ServerName);

        Assert.Equal(1048576, summary.FileSize);

        Assert.Equal(1048576, summary.Size);

        Assert.Equal("/replays", summary.Directory);

        Assert.Equal("http://localhost/replays/M12345.honreplay", summary.URL);

        Assert.Equal("http://localhost/replays/M12345.honreplay", summary.S3URL);
    }
}
