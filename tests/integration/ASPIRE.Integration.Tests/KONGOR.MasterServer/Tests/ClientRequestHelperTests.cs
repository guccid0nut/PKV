// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Requester;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class ClientRequestHelperTests

{
    [Fact]
    public Task ClientRequestHelper_CreateShowSimpleStatsResponse_ReturnsSortedStats()

    {
        try

        {
            // Arrange

            Account account = new()

            {
                ID = 123,

                Name = "TestPlayer",

                Type = AccountType.Normal,

                IsMain = true,

                User = new User

                {
                    EmailAddress = "test@example.com",

                    Role = new Role { ID = 1, Name = "User" },

                    PBKDF2PasswordHash = "hash",

                    SRPPasswordHash = "hash",

                    SRPPasswordSalt = "salt",

                    TotalLevel = 10,

                    TotalExperience = 1000,

                    InventoryItems = []
                }
            };


            // Create player stats aggregated DTO

            PlayerStatisticsAggregatedDTO stats = new()

            {
                Smackdowns = 1,

                Annihilations = 5,

                Assists = 100,

                Kills = 50,

                RankedWins = 1,

                RankedMatches = 1,

                RankedSeconds = 1000,

                CasualSeconds = 1000,


                // Input unordered to verify sorting

                TopHeroes =

                [
                    new FavHeroDTO { HeroId = 3, SecondsPlayed = 200 }, // Least (3rd)

                    new FavHeroDTO { HeroId = 1, SecondsPlayed = 1000 }, // Most (1st)

                    new FavHeroDTO { HeroId = 2, SecondsPlayed = 500 } // Middle (2nd)
                ]
            };


            IHeroDefinitionService heroDefinitions = new TestHeroDefinitionService();


            // Act

            ShowSimpleStatsResponse response =
                ClientRequestHelper.CreateShowSimpleStatsResponse(account, stats, 12, heroDefinitions);


            // Assert

            // 1. Verify Awards are sorted by Count Descending

            // Expected: Assists (100) > Kills (50) > Annihilations (5) > Smackdowns (1)

            Assert.Equal(4, response.Top4AwardNames.Count);


            Assert.Equal("awd_masst", response.Top4AwardNames[0]); // 100

            Assert.Equal("awd_mkill", response.Top4AwardNames[1]); // 50

            Assert.Equal("awd_mann", response.Top4AwardNames[2]); // 5

            Assert.Equal("awd_msd", response.Top4AwardNames[3]); // 1


            Assert.Equal("100", response.Top4AwardCounts[0]);

            Assert.Equal("50", response.Top4AwardCounts[1]);

            Assert.Equal("5", response.Top4AwardCounts[2]);

            Assert.Equal("1", response.Top4AwardCounts[3]);


            // 2. Verify Heroes are sorted by Time Descending

            // Expected: Hero 1 (1000s) > Hero 2 (500s) > Hero 3 (200s)

            Assert.Equal("jereziah", response.FavHero1); // Hero 1

            Assert.Equal(50, response.FavHero1Time); // 1000/2000 * 100


            Assert.Equal("legionnaire", response.FavHero2); // Hero 2

            Assert.Equal(25, response.FavHero2Time); // 500/2000 * 100


            Assert.Equal("magmar", response.FavHero3); // Hero 3

            Assert.Equal(10, response.FavHero3Time); // 200/2000 * 100

            return Task.CompletedTask;
        }

        catch (Exception exception)

        {
            return Task.FromException(exception);
        }
    }


    private sealed class TestHeroDefinitionService : IHeroDefinitionService

    {
        public string GetHeroIdentifier(uint heroId)

        {
            return heroId switch

            {
                1 => "Hero_Jereziah",

                2 => "Hero_Legionnaire",

                3 => "Hero_Magmar",

                4 => "Hero_PuppetMaster",

                5 => "Hero_Devourer",

                _ => $"Hero_{heroId}"
            };
        }


        public uint GetBaseHeroId(uint productId)

        {
            return productId;
        }


        public uint GetBaseHeroId(string identifier)

        {
            return 0;
        }


        public bool IsHero(uint heroId)

        {
            return true;
        }


        public IEnumerable<uint> GetAllHeroIds()

        {
            return [];
        }
    }
}
