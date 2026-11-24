// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Net;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Extensions.Cache;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StatsRequesterControllerSubmitTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private static void PopulateFullMatchForm(Dictionary<string, string> payload, int matchId, string serverId,
        string nickname)

    {
        payload["match_stats[match_id]"] = matchId.ToString(CultureInfo.InvariantCulture);

        payload["match_stats[server_id]"] = serverId;

        payload["match_stats[map]"] = "caldavar";

        payload["match_stats[map_version]"] = "1.0";

        payload["match_stats[version]"] = "4.10.1";

        payload["match_stats[time_played]"] = "1500";

        payload["match_stats[file_size]"] = "1024";

        payload["match_stats[file_name]"] = $"M{matchId}.honreplay";

        payload["match_stats[c_state]"] = "0";

        payload["match_stats[avgpsr]"] = "1500";

        payload["match_stats[avgpsr_team1]"] = "1500";

        payload["match_stats[avgpsr_team2]"] = "1500";

        payload["match_stats[gamemode]"] = "normal";

        payload["match_stats[teamscoregoal]"] = "0";

        payload["match_stats[playerscoregoal]"] = "0";

        payload["match_stats[numrounds]"] = "1";

        payload["match_stats[release_stage]"] = "Live";

        payload["match_stats[awd_mann]"] = "0";

        payload["match_stats[awd_mqk]"] = "0";

        payload["match_stats[awd_lgks]"] = "0";

        payload["match_stats[awd_msd]"] = "0";

        payload["match_stats[awd_mkill]"] = "0";

        payload["match_stats[awd_masst]"] = "0";

        payload["match_stats[awd_ledth]"] = "0";

        payload["match_stats[awd_mbdmg]"] = "0";

        payload["match_stats[awd_mwk]"] = "0";

        payload["match_stats[awd_mhdd]"] = "0";

        payload["match_stats[awd_hcs]"] = "0";

        payload["match_stats[winner]"] = "1";


        payload["team_stats[1][score]"] = "30";

        payload["team_stats[2][score]"] = "20";


        payload["player_stats[0][Hero_Pyromancer][hero_id]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][nickname]"] = nickname;

        payload["player_stats[0][Hero_Pyromancer][team]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][position]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][group_num]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][benefit]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][wins]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][losses]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][discos]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][concedes]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][kicked]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][social_bonus]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][used_token]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][concedevotes]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][herokills]"] = "12";

        payload["player_stats[0][Hero_Pyromancer][deaths]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][heroassists]"] = "7";

        payload["player_stats[0][Hero_Pyromancer][gold]"] = "14000";

        payload["player_stats[0][Hero_Pyromancer][exp]"] = "16000";

        payload["player_stats[0][Hero_Pyromancer][herodmg]"] = "12000";

        payload["player_stats[0][Hero_Pyromancer][herokillsgold]"] = "2400";

        payload["player_stats[0][Hero_Pyromancer][heroexp]"] = "9000";

        payload["player_stats[0][Hero_Pyromancer][buybacks]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][goldlost2death]"] = "100";

        payload["player_stats[0][Hero_Pyromancer][secs_dead]"] = "30";

        payload["player_stats[0][Hero_Pyromancer][teamcreepkills]"] = "25";

        payload["player_stats[0][Hero_Pyromancer][teamcreepdmg]"] = "2500";

        payload["player_stats[0][Hero_Pyromancer][teamcreepgold]"] = "600";

        payload["player_stats[0][Hero_Pyromancer][teamcreepexp]"] = "500";

        payload["player_stats[0][Hero_Pyromancer][neutralcreepkills]"] = "12";

        payload["player_stats[0][Hero_Pyromancer][neutralcreepdmg]"] = "1200";

        payload["player_stats[0][Hero_Pyromancer][neutralcreepgold]"] = "300";

        payload["player_stats[0][Hero_Pyromancer][neutralcreepexp]"] = "250";

        payload["player_stats[0][Hero_Pyromancer][bdmg]"] = "2000";

        payload["player_stats[0][Hero_Pyromancer][razed]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][bdmgexp]"] = "400";

        payload["player_stats[0][Hero_Pyromancer][bgold]"] = "500";

        payload["player_stats[0][Hero_Pyromancer][denies]"] = "5";

        payload["player_stats[0][Hero_Pyromancer][exp_denied]"] = "100";

        payload["player_stats[0][Hero_Pyromancer][gold_spent]"] = "13000";

        payload["player_stats[0][Hero_Pyromancer][actions]"] = "1800";

        payload["player_stats[0][Hero_Pyromancer][secs]"] = "1500";

        payload["player_stats[0][Hero_Pyromancer][level]"] = "16";

        payload["player_stats[0][Hero_Pyromancer][consumables]"] = "3";

        payload["player_stats[0][Hero_Pyromancer][wards]"] = "2";

        payload["player_stats[0][Hero_Pyromancer][bloodlust]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][doublekill]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][triplekill]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][quadkill]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][annihilation]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks3]"] = "1";

        payload["player_stats[0][Hero_Pyromancer][ks4]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks5]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks6]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks7]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks8]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks9]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks10]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][ks15]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][smackdown]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][humiliation]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][nemesis]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][retribution]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][score]"] = "600";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat0]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat1]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat2]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat3]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat4]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat5]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat6]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat7]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat8]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][gameplaystat9]"] = "0";

        payload["player_stats[0][Hero_Pyromancer][time_earning_exp]"] = "1200";
    }


    [Fact]
    public async Task HandleStatsSubmission_WithMissingSession_ReturnsBadRequest()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        Dictionary<string, string> payload = new()

        {
            { "f", "submit_stats" },

            { "match_id", "12345" }
        };


        HttpResponseMessage response =
            await client.PostAsync("stats_requester.php", new FormUrlEncodedContent(payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    [Fact]
    public async Task HandleStatsSubmission_WithInvalidMatchId_ReturnsBadRequest()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        Dictionary<string, string> payload = new()

        {
            { "f", "submit_stats" },

            { "session", "some_session_token" },

            { "match_stats[match_id]", "invalid_id_not_a_number" }
        };


        HttpResponseMessage response =
            await client.PostAsync("stats_requester.php", new FormUrlEncodedContent(payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    [Fact]
    public async Task HandleStatsSubmission_WithValidSessionAndMatch_ReturnsSerializedOk()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        using IServiceScope scope = factory.Services.CreateScope();

        MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        const int matchId = 200001;

        string sessionCookie = $"sess_{Guid.NewGuid():N}";


        MatchServer matchServer = new()

        {
            ID = 5,

            Name = "US_East_Dedicated_5",

            HostAccountName = "ServerHostAdmin",

            HostAccountID = 1,

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "Local",

            Description = "Dedicated Test Server",

            Cookie = sessionCookie
        };

        await redis.SetMatchServer("ServerHostAdmin", matchServer);


        Role? existingRole = await db.Roles.FindAsync(1);

        Role role = existingRole ?? new Role { ID = 1, Name = "User" };

        if (existingRole == null) await db.Roles.AddAsync(role);


        User u1 = new()
        {
            ID = 9101, EmailAddress = "player9101@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };

        Account a1 = new() { ID = 9101, Name = "DedicatedHero9101", IsMain = true, User = u1 };

        u1.Accounts = [a1];


        await db.Users.AddAsync(u1);

        await db.Accounts.AddAsync(a1);

        await db.SaveChangesAsync();


        Dictionary<string, string> payload = new()

        {
            { "f", "submit_stats" },

            { "session", sessionCookie }
        };

        PopulateFullMatchForm(payload, matchId, "5", "DedicatedHero9101");


        HttpResponseMessage response =
            await client.PostAsync("stats_requester.php", new FormUrlEncodedContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        string responseContent = await response.Content.ReadAsStringAsync();

        Assert.NotEmpty(responseContent);

        Assert.StartsWith("a:", responseContent);

        Assert.Contains("match_info", responseContent);

        Assert.Contains("match_summ", responseContent);
    }


    [Fact]
    public async Task HandleStatsSubmission_WithOrphanedSession_RecoversAndSucceeds()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        const int matchId = 200002;

        string orphanedSession = $"orphan_{Guid.NewGuid():N}";


        Dictionary<string, string> payload = new()

        {
            { "f", "submit_stats" },

            { "session", orphanedSession }
        };

        PopulateFullMatchForm(payload, matchId, "99", "OrphanedPlayer");


        HttpResponseMessage response =
            await client.PostAsync("stats_requester.php", new FormUrlEncodedContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        string responseContent = await response.Content.ReadAsStringAsync();

        Assert.NotEmpty(responseContent);

        Assert.StartsWith("a:", responseContent);
    }


    [Fact]
    public async Task HandleStatsResubmission_WithMissingLogin_ReturnsBadRequest()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        Dictionary<string, string> payload = new()

        {
            { "f", "resubmit_stats" },

            { "match_id", "200003" }
        };


        HttpResponseMessage response =
            await client.PostAsync("stats_requester.php", new FormUrlEncodedContent(payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
