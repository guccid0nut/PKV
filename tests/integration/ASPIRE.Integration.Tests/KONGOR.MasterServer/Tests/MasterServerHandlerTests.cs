// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using KONGOR.MasterServer.Extensions.Cache;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MasterServerHandlerTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie, Account UserAccount, Account
            TargetAccount, Clan TestClan, WebApplicationFactory<KONGORAssemblyMarker> Factory)> SetupClanTestAsync()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();

        IServiceScope scope = factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string leaderName = $"Leader_{Guid.NewGuid().ToString("N")[..6]}";

        string targetName = $"Target_{Guid.NewGuid().ToString("N")[..6]}";


        Role userRole = await dbContext.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };

        if (userRole.ID == 0 || dbContext.Entry(userRole).State == EntityState.Detached)
            await dbContext.Roles.AddAsync(userRole);


        User uLeader = new()

        {
            EmailAddress = $"{leaderName}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = userRole
        };


        Clan clan = new()

        {
            Name = $"Clan_{Guid.NewGuid().ToString("N")[..6]}",
            Tag = "TST",
            TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
        };

        await dbContext.Clans.AddAsync(clan);

        await dbContext.SaveChangesAsync();


        Account leaderAccount = new()

        {
            Name = leaderName,

            User = uLeader,

            Type = AccountType.Normal,

            IsMain = true,

            Clan = clan,

            ClanTier = ClanTier.Leader
        };


        User uTarget = new()

        {
            EmailAddress = $"{targetName}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = userRole
        };


        Account targetAccount = new()

        {
            Name = targetName,

            User = uTarget,

            Type = AccountType.Normal,

            IsMain = true,

            Clan = clan,

            ClanTier = ClanTier.Member
        };


        await dbContext.Users.AddAsync(uLeader);

        await dbContext.Users.AddAsync(uTarget);

        await dbContext.Accounts.AddAsync(leaderAccount);

        await dbContext.Accounts.AddAsync(targetAccount);

        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();


        await cache.SetAccountNameForSessionCookie(cookie, leaderAccount.Name);


        return (client, dbContext, cache, cookie, leaderAccount, targetAccount, clan, factory);
    }


    [Fact]
    public async Task ClanHandler_SetRank_MissingParameters_ReturnsBadRequest()

    {
        (HttpClient client, _, _, string cookie, _, _, _, _) = await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string>

            {
                { "f", "set_rank" }, { "cookie", cookie }
            });

            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);


            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task ClanHandler_SetRank_PromoteToOfficer_Success()

    {
        (HttpClient client, MerrickContext dbContext, _, string cookie, _, Account targetAccount, Clan clan, _) =
            await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string>

            {
                { "f", "set_rank" },

                { "cookie", cookie },

                { "target_id", targetAccount.ID.ToString() },

                { "clan_id", clan.ID.ToString() },

                { "rank", "Officer" }
            });


            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);


            Account updatedTarget = await dbContext.Accounts.SingleAsync(a => a.ID == targetAccount.ID);

            Assert.Equal(ClanTier.Officer, updatedTarget.ClanTier);
        }
    }


    [Fact]
    public async Task ClanHandler_SetRank_KickMember_Success()

    {
        (HttpClient client, MerrickContext dbContext, _, string cookie, _, Account targetAccount, Clan clan, _) =
            await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string>

            {
                { "f", "set_rank" },

                { "cookie", cookie },

                { "target_id", targetAccount.ID.ToString() },

                { "clan_id", clan.ID.ToString() },

                { "rank", "Remove" }
            });


            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);


            Account updatedTarget = await dbContext.Accounts.Include(account => account.Clan)
                .SingleAsync(a => a.ID == targetAccount.ID);

            Assert.Null(updatedTarget.Clan);

            Assert.Equal(ClanTier.None, updatedTarget.ClanTier);
        }
    }


    [Fact]
    public async Task ClaimMasteryRewardHandler_AccountNotFound_ReturnsEmptyPhpDict()
    {
        (HttpClient client, _, IDatabase cache, _, _, _, _, _) = await SetupClanTestAsync();
        string missingAccountNickname = $"NonExistentAccount_{Guid.NewGuid():N}"[..24];
        string missingAccountCookie = Guid.NewGuid().ToString("N");
        await cache.SetAccountNameForSessionCookie(missingAccountCookie, missingAccountNickname);

        FormUrlEncodedContent formContent = new(new Dictionary<string, string>
        {
            { "f", "claim_mastery_reward" },
            { "cookie", missingAccountCookie },
            { "nickname", missingAccountNickname },
            { "level", "5" }
        });

        HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();

        Assert.Contains("a:0:{}", content);
    }

    [Fact]
    public async Task ClaimMasteryRewardHandler_SessionAccountMismatch_ReturnsUnauthorized()
    {
        (HttpClient client, _, _, string cookie, _, _, _, _) = await SetupClanTestAsync();

        FormUrlEncodedContent formContent = new(new Dictionary<string, string>
        {
            { "f", "claim_mastery_reward" },
            { "cookie", cookie },
            { "nickname", "NonExistentAccount_9999" },
            { "level", "5" }
        });

        HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task FriendsHandler_RemoveFriend_MissingParams_ReturnsBadRequest()

    {
        (HttpClient client, _, _, string cookie, _, _, _, _) = await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string>

            {
                { "f", "remove_friend" }, { "cookie", cookie }
            });


            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task ClaimMasteryRewardHandler_ValidAccount_GrantsReward()

    {
        (HttpClient client, _, _, string cookie, Account account, _, _, _) = await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string>

            {
                { "f", "claim_mastery_reward" },

                { "cookie", cookie },

                { "nickname", account.Name },

                { "level", "1" }
            });


            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);


            string content = await response.Content.ReadAsStringAsync();

            Assert.Contains("vested_threshold", content);
        }
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task UpgradesHandler_MissingCookie_ReturnsUnauthorized()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (HttpClient client, _, _, _, _, _, _, _) = await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string> { { "f", "get_upgrades" } });


            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task UpgradesHandler_ValidCookie_ReturnsUpgradesData()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (HttpClient client, _, _, string cookie, _, _, _, _) = await SetupClanTestAsync();

        {
            FormUrlEncodedContent formContent = new(new Dictionary<string, string>

            {
                { "f", "get_upgrades" }, { "cookie", cookie }
            });


            HttpResponseMessage response = await client.PostAsync("client_requester.php", formContent);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);


            string content = await response.Content.ReadAsStringAsync();

            Assert.Contains("my_upgrades", content);

            Assert.Contains("field_stats", content);
        }
    }
}
