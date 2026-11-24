// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.Data;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class QuestVerifiedPayloadTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    [Fact]
    public async Task QuestVerifiedPayload_GetPlayerQuests_ReturnsSuccess()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        string cookie = Guid.NewGuid().ToString("N");

        string uniqueName = $"QuestUser_{Guid.NewGuid():N}"[..15];

        User user = new()

        {
            EmailAddress = $"{uniqueName}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = uniqueName, User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        Dictionary<string, string> payload = MiscRequestPayloads.Unverified.GetPlayerQuests(cookie);

        payload.Add("account_ids[0]", account.ID.ToString());

        payload.Add("account_ids[1]", "-1");


        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/questserver/getplayerquests/", content);

        response.EnsureSuccessStatusCode();


        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("quest_queue", responseBody);
        Assert.Contains("player_quest_queue", responseBody);
    }


    [Fact]
    public async Task QuestVerifiedPayload_GetCurrentQuests_ReturnsSuccess()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        string cookie = Guid.NewGuid().ToString("N");

        string uniqueName = $"QuestUser2_{Guid.NewGuid():N}"[..15];

        User user = new()

        {
            EmailAddress = $"{uniqueName}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = uniqueName, User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        Dictionary<string, string> payload = MiscRequestPayloads.Unverified.GetCurrentQuests(cookie);

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/questserver/getcurrentquests/", content);

        response.EnsureSuccessStatusCode();


        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("quests", responseBody);
    }
}
