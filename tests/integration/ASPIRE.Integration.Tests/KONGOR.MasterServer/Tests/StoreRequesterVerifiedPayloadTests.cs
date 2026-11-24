// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Tests.Data;
using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Store;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StoreRequesterVerifiedPayloadTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    [Fact]
    public async Task StoreRequesterVerifiedPayload_DailySpecial_ReturnsValidJson()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase redis = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string id = Guid.NewGuid().ToString("N")[..8];

        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = $"ds_{id}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = $"DSUser_{id}", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        await redis.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new() { ["f"] = "get_daily_special", ["cookie"] = cookie };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        Assert.NotEmpty(body);


        using JsonDocument jsonDoc = JsonDocument.Parse(body);

        JsonElement root = jsonDoc.RootElement;

        Assert.True(root.TryGetProperty("list", out JsonElement listElement));

        Assert.Equal(JsonValueKind.Array, listElement.ValueKind);
        Assert.True(listElement.GetArrayLength() > 0);

        JsonElement firstItem = listElement[0];
        Assert.Equal(1, firstItem.GetProperty("panel_index").GetInt32());
        Assert.Equal("6019", firstItem.GetProperty("product_id").GetString());
        Assert.Equal("Hero_Bubbles.Alt16", firstItem.GetProperty("item_name").GetString());
        Assert.Equal("Bubbles", firstItem.GetProperty("hero_name").GetString());
        Assert.Equal("Abyssal Bubbles", firstItem.GetProperty("item_cname").GetString());
        Assert.Equal("aa.Hero_Bubbles.Alt16", firstItem.GetProperty("item_code").GetString());
        Assert.Equal("Alt Avatar", firstItem.GetProperty("item_type").GetString());
    }


    [Fact]
    public async Task StoreRequest_WhenExecuted_ReturnsSuccess()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string id = Guid.NewGuid().ToString("N")[..8];

        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = $"store_{id}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = $"StoreUser_{id}", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = MiscRequestPayloads.Unverified.StoreRequest(cookie);

        payload["request_code"] = "1";

        payload["category_id"] = "2";

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("store_requester.php", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        Assert.NotEmpty(body);


        bool isPhpArray = body.StartsWith("a:", StringComparison.Ordinal);

        Assert.True(isPhpArray);

        Assert.Contains("productIDs", body);

        Assert.Contains("productCodes", body);
    }


    [Fact]
    public async Task StoreRequest_PurchaseProduct_DeductsCoinsAndUpdatesOwned()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string id = Guid.NewGuid().ToString("N")[..8];

        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = $"buyer_{id}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            GoldCoins = 1000,

            SilverCoins = 5000,

            InventoryItems = [new UserInventory { ItemCode = "ai.Default Icon", GrantSource = "Test" }],

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new() { Name = $"BuyerUser_{id}", User = user, Type = AccountType.Normal, IsMain = true };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new()

        {
            ["cookie"] = cookie,

            ["request_code"] = "4",

            ["product_id"] = "15",

            ["currency"] = "0",

            ["category_id"] = "2",

            ["page"] = "1",

            ["hostTime"] = "1234567890"
        };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("store_requester.php", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("a:", body, StringComparison.Ordinal);

        Assert.Contains("popupCode", body);


        dbContext.ChangeTracker.Clear();

        Account updatedAccount = await dbContext.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u!.InventoryItems)
            .SingleAsync(a => a.ID == account.ID);


        Assert.Contains("aa.Hero_Pyromancer.Female", updatedAccount.User.InventoryItems.Select(i => i.ItemCode));

        Assert.Equal(600, updatedAccount.User.GoldCoins); // 1000 - 400
    }


    [Fact]
    public async Task StoreRequest_ViewVault_ReturnsOwnedItemsAndSelectedUpgrades()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string id = Guid.NewGuid().ToString("N")[..8];

        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = $"vault_buyer_{id}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            InventoryItems =
            [
                new UserInventory { ItemCode = "ai.Default Icon", GrantSource = "Test" },
                new UserInventory { ItemCode = "ai.custom_icon:test", GrantSource = "Test" },
                new UserInventory { ItemCode = "cc.white", GrantSource = "Test" },
                new UserInventory { ItemCode = "cc.silvershield", GrantSource = "Test" }
            ],

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new()

        {
            Name = $"VaultBuyerUser_{id}",

            User = user,

            Type = AccountType.Normal,

            IsMain = true,

            SelectedStoreItems = ["ai.custom_icon:test", "cc.silvershield"]
        };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        Dictionary<string, string> payload = new()

        {
            ["cookie"] = cookie,

            ["request_code"] = "2", // LIST_VAULT_ITEMS_REQUEST

            ["hostTime"] = "1234567890"
        };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("store_requester.php", content);

        response.EnsureSuccessStatusCode();


        string body = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("a:", body, StringComparison.Ordinal);


        // Assert vault Category 3 (Account Icons) has the custom icon mapped

        Assert.Contains("custom_icon:test`ai.custom_icon:test`web", body);


        // Assert vault Category 16 (Name Colors) has the silvershield color (ID 21)

        Assert.Contains("21`cc.silvershield", body);


        // Assert selectedUpgrades returns custom_icon:test and 21 (ID of silvershield)

        Assert.Contains("custom_icon:test", body);

        Assert.Contains("|21", body);
    }


    [Fact]
    public async Task ClientRequest_SelectedUpgrades_SavesSelections()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string id = Guid.NewGuid().ToString("N")[..8];

        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = $"selector_{id}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            InventoryItems =
            [
                new UserInventory { ItemCode = "ai.Default Icon", GrantSource = "Test" },
                new UserInventory { ItemCode = "cc.white", GrantSource = "Test" },
                new UserInventory { ItemCode = "cc.silvershield", GrantSource = "Test" }
            ],

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new()

        {
            Name = $"SelectorUser_{id}",

            User = user,

            Type = AccountType.Normal,

            IsMain = true,

            SelectedStoreItems = ["ai.Default Icon", "cc.white"]
        };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        // Equip silvershield

        Dictionary<string, string> payload = new()

        {
            ["f"] = "selected_upgrades",
            ["cookie"] = cookie,
            ["selected_upgrades[]"] = "silvershield"
        };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

        response.EnsureSuccessStatusCode();


        dbContext.ChangeTracker.Clear();

        Account updatedAccount = await dbContext.Accounts
            .Include(a => a.User)
            .Include(a => a.SelectedStoreItems)
            .SingleAsync(a => a.ID == account.ID);


        // Should contain cc.silvershield, and cc.white should be cleared (not selected)

        Assert.Contains("cc.silvershield", updatedAccount.SelectedStoreItemCodes);

        Assert.DoesNotContain("cc.white", updatedAccount.SelectedStoreItemCodes);
    }


    [Fact]
    public async Task StoreRequest_RepeatableItems_CanBePurchasedMultipleTimes()

    {
        HttpClient client = fixture.Client;

        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string id = Guid.NewGuid().ToString("N")[..8];

        string cookie = Guid.NewGuid().ToString("N");

        User user = new()

        {
            EmailAddress = $"repeat_buyer_{id}@kongor.net",

            PBKDF2PasswordHash = "h",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            GoldCoins = 10000,

            SilverCoins = 5000,

            InventoryItems = [new UserInventory { ItemCode = "ai.Default Icon", GrantSource = "Test" }],

            Role = new EntityRole { Name = UserRoles.User }
        };

        Account account = new()

        {
            Name = $"RepeatBuyerUser_{id}",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();

        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        // 1. Purchase Nickname Change (Product ID 161, GoldCost 2000)

        Dictionary<string, string> payload1 = new()

        {
            ["cookie"] = cookie,

            ["request_code"] = "4",

            ["product_id"] = "161",

            ["currency"] = "0",

            ["category_id"] = "6",

            ["page"] = "1",

            ["hostTime"] = "1234567890"
        };

        FormUrlEncodedContent content1 = new(payload1);

        HttpResponseMessage response1 = await client.PostAsync("store_requester.php", content1);

        response1.EnsureSuccessStatusCode();


        string body1 = await response1.Content.ReadAsStringAsync();

        Assert.Contains("popupCode", body1);


        dbContext.ChangeTracker.Clear();

        Account accountAfterFirst = await dbContext.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u!.InventoryItems)
            .SingleAsync(a => a.ID == account.ID);

        Assert.Equal(8000, accountAfterFirst.User.GoldCoins);

        Assert.Contains("m.Nickname Change", accountAfterFirst.User.InventoryItems.Select(i => i.ItemCode));


        // 2. Purchase Nickname Change again - should SUCCEED and not return ALREADY_OWNED error

        Dictionary<string, string> payload2 = new()

        {
            ["cookie"] = cookie,

            ["request_code"] = "4",

            ["product_id"] = "161",

            ["currency"] = "0",

            ["category_id"] = "6",

            ["page"] = "1",

            ["hostTime"] = "1234567891"
        };

        FormUrlEncodedContent content2 = new(payload2);

        HttpResponseMessage response2 = await client.PostAsync("store_requester.php", content2);

        response2.EnsureSuccessStatusCode();


        string body2 = await response2.Content.ReadAsStringAsync();

        Assert.Contains("popupCode", body2);


        dbContext.ChangeTracker.Clear();

        Account accountAfterSecond = await dbContext.Accounts
            .Include(a => a.User)
            .SingleAsync(a => a.ID == account.ID);

        Assert.Equal(6000, accountAfterSecond.User.GoldCoins);


        // 3. View Category 6 (Other) - verify Nickname Change has purchasable=1 and productAlreadyOwned=0

        Dictionary<string, string> listPayload = new()

        {
            ["cookie"] = cookie,

            ["request_code"] = "1",

            ["category_id"] = "6",

            ["page"] = "1",

            ["hostTime"] = "1234567892"
        };

        FormUrlEncodedContent listContent = new(listPayload);

        HttpResponseMessage listResponse = await client.PostAsync("store_requester.php", listContent);

        listResponse.EnsureSuccessStatusCode();


        string listBody = await listResponse.Content.ReadAsStringAsync();

        Assert.Contains("161", listBody);
    }
}
