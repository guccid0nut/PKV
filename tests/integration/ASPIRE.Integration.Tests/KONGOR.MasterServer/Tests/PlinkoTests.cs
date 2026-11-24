// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Extensions.Cache;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class PlinkoTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    [Fact]
    public async Task Index_InvalidCookie_ReturnsStatusZero()

    {
        HttpClient client = fixture.Client;

        Dictionary<string, string> payload = new() { ["cookie"] = "invalid_cookie" };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/casino/", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        IDictionary deserialized = (IDictionary)PhpSerialization.Deserialize(body)!;


        Assert.Equal(0, Convert.ToInt32(deserialized["status_code"]));
    }


    [Fact]
    public async Task Index_ValidCookie_ReturnsBalancesAndCosts()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = "plinko_index@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            GoldCoins = 100,

            SilverCoins = 500,

            PlinkoTickets = 250,

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = "PlinkoIndexUser", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await redis.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new() { ["cookie"] = cookie };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/casino/", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        IDictionary deserialized = (IDictionary)PhpSerialization.Deserialize(body)!;


        Assert.Equal(1, Convert.ToInt32(deserialized["status_code"]));

        Assert.Equal(100, Convert.ToInt32(deserialized["user_gold"]));

        Assert.Equal(500, Convert.ToInt32(deserialized["silver"]));

        Assert.Equal(250, Convert.ToInt32(deserialized["user_tickets"]));

        Assert.Equal(JSONConfiguration.PlinkoConfiguration.GoldCost, Convert.ToInt32(deserialized["gold_cost"]));

        Assert.Equal(JSONConfiguration.PlinkoConfiguration.TicketCost, Convert.ToInt32(deserialized["ticket_cost"]));
    }


    [Fact]
    public async Task Drop_PayingWithGold_DeductsGoldAndReturnsOutcome()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = "plinko_drop_gold@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            GoldCoins = 1000,

            PlinkoTickets = 0,

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = "PlinkoDropGoldUser", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await redis.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new() { ["cookie"] = cookie, ["currency"] = "gold" };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/casino/drop/", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        IDictionary deserialized = (IDictionary)PhpSerialization.Deserialize(body)!;


        Assert.Equal(1, Convert.ToInt32(deserialized["status_code"]));


        // Retrieve user from DB again to verify balances

        dbContext.ChangeTracker.Clear();

        Account updatedAccount = await dbContext.Accounts
            .Include(a => a.User)
            .SingleAsync(a => a.ID == account.ID);


        int expectedGold = 1000 - JSONConfiguration.PlinkoConfiguration.GoldCost;

        Assert.Equal(expectedGold, updatedAccount.User.GoldCoins);


        // Plinko tickets should increase by the ticket_amount returned in response

        int ticketReward = Convert.ToInt32(deserialized["ticket_amount"]);

        Assert.Equal(ticketReward, updatedAccount.User.PlinkoTickets);
    }


    [Fact]
    public async Task Plinko_ViewChest_ReturnsAvailableProducts()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = "plinko_chest@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = "PlinkoChestUser", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await redis.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new() { ["cookie"] = cookie, ["tier_id"] = "1", ["target_index"] = "1" };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/casino/viewchest/", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        IDictionary deserialized = (IDictionary)PhpSerialization.Deserialize(body)!;


        Assert.Equal(1, Convert.ToInt32(deserialized["tier_id"]));

        Assert.True(Convert.ToInt32(deserialized["items_amount"]) > 0);

        Assert.NotEmpty(deserialized["product_ids"]?.ToString() ?? string.Empty);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task TicketExchange_List_ReturnsItemsAndBalance()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = "ticket_list@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PlinkoTickets = 1500,

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = "TicketListUser", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await redis.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new() { ["cookie"] = cookie };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/ticketexchange/", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        IDictionary deserialized = (IDictionary)PhpSerialization.Deserialize(body)!;


        Assert.Equal(51, Convert.ToInt32(deserialized["status_code"])); // StatusSuccess = 51

        Assert.Equal(1500, Convert.ToInt32(deserialized["user_tickets"]));

        Assert.NotNull(deserialized["items"]);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task TicketExchange_Purchase_SuccessAndDeductsTickets()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = "ticket_purchaser@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PlinkoTickets = 2000,

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = "TicketPurchaserUser", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await redis.SetAccountNameForSessionCookie(cookie, account.Name);


        // Purchase product 1792 (Super-Taunt) which costs 1300 tickets

        Dictionary<string, string> payload = new() { ["cookie"] = cookie, ["id"] = "1792" };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("master/ticketexchange/purchase/", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        IDictionary deserialized = (IDictionary)PhpSerialization.Deserialize(body)!;


        Assert.Equal(51, Convert.ToInt32(deserialized["status_code"])); // StatusSuccess = 51

        Assert.Equal(700, Convert.ToInt32(deserialized["tickets_remaining"])); // 2000 - 1300


        dbContext.ChangeTracker.Clear();

        Account updatedAccount = await dbContext.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u!.InventoryItems)
            .SingleAsync(a => a.ID == account.ID);

        Assert.Equal(700, updatedAccount.User.PlinkoTickets);

        Assert.Contains(updatedAccount.User.InventoryItems.Select(i => i.ItemCode),
            item => item.EndsWith("Super-Taunt", StringComparison.OrdinalIgnoreCase));
    }
}
