// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.Data;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StoreInventoryCanaryPilotTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>
{
    [Fact]
    public async Task CanaryPilotUser_ResolvesOnlyFromNormalizedUserInventory_AndMatchesProtocol()
    {
        HttpClient client = fixture.Client;
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();

        string cookie = Guid.NewGuid().ToString("N");

        // Clean up any previous test run
        Account? existing = await dbContext.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Name == InventoryCanaryGate.CanaryPilotAccountName);

        if (existing != null)
        {
            List<UserInventory> oldInv = await dbContext.UserInventory
                .Where(i => i.AccountID == existing.ID || (existing.User != null && i.UserID == existing.User.ID))
                .ToListAsync();
            dbContext.UserInventory.RemoveRange(oldInv);
            dbContext.Accounts.Remove(existing);
            if (existing.User != null) dbContext.Users.Remove(existing.User);
            await dbContext.SaveChangesAsync();
        }

        // 1. Create Test User (proving all data comes from UserInventory)
        User canaryUser = new()
        {
            EmailAddress = "canarypilot@project.kongor",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            GoldCoins = 1000,
            SilverCoins = 5000,
            Role = new EntityRole { Name = UserRoles.User }
        };

        Account canaryAccount = new()
        {
            Name = InventoryCanaryGate.CanaryPilotAccountName,
            User = canaryUser,
            Type = AccountType.Normal,
            IsMain = true
        };

        await dbContext.Users.AddAsync(canaryUser);
        await dbContext.Accounts.AddAsync(canaryAccount);
        await dbContext.SaveChangesAsync();

        // 2. Insert the 2 items into core.UserInventory
        UserInventory pilotAvatar = new()
        {
            UserID = canaryUser.ID,
            AccountID = canaryAccount.ID,
            ItemCode = "aa.Hero_Pyromancer.Female",
            Quantity = 1,
            IsPermanent = true,
            Source = "Store"
        };

        UserInventory pilotTaunt = new()
        {
            UserID = canaryUser.ID,
            AccountID = canaryAccount.ID,
            ItemCode = "t.Dumpster_Taunt",
            Quantity = 1,
            IsPermanent = true,
            Source = "Store"
        };

        await dbContext.UserInventory.AddRangeAsync(pilotAvatar, pilotTaunt);
        await dbContext.SaveChangesAsync();

        await cache.SetAccountNameForSessionCookie(cookie, canaryAccount.Name);

        // 3. Test StoreController.ViewVault (request_code = 2)
        Dictionary<string, string> vaultPayload = new()
        {
            ["cookie"] = cookie,
            ["request_code"] = "2", // LIST_VAULT_ITEMS_REQUEST
            ["hostTime"] = "1234567890"
        };

        FormUrlEncodedContent vaultContent = new(vaultPayload);
        HttpResponseMessage vaultResponse = await client.PostAsync("store_requester.php", vaultContent);
        vaultResponse.EnsureSuccessStatusCode();

        string vaultBody = await vaultResponse.Content.ReadAsStringAsync();

        // Must start with PHP serialized array
        Assert.StartsWith("a:", vaultBody, StringComparison.Ordinal);

        // Vault contains the taunt category (vaultCategory27) with Dumpster Taunt from UserInventory
        Assert.Contains("vaultCategory27", vaultBody);
        Assert.Contains("t.Dumpster_Taunt", vaultBody);

        // 4. Test UpgradesHandler (f = get_upgrades)
        Dictionary<string, string> upgradesPayload = new()
        {
            ["cookie"] = cookie,
            ["f"] = "get_upgrades"
        };

        FormUrlEncodedContent upgradesContent = new(upgradesPayload);
        HttpResponseMessage upgradesResponse = await client.PostAsync("client_requester.php", upgradesContent);
        upgradesResponse.EnsureSuccessStatusCode();

        string upgradesBody = await upgradesResponse.Content.ReadAsStringAsync();
        Assert.StartsWith("a:", upgradesBody, StringComparison.Ordinal);
        Assert.Contains("Hero_Pyromancer.Female", upgradesBody);
        Assert.Contains("t.Dumpster_Taunt", upgradesBody);
    }
}
