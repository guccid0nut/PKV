// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data;
using System.Security.Cryptography;
using ASPIRE.Common.Serialization.PHP;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using Npgsql;

namespace MERRICK.DatabaseContext.Repositories;

public class InventoryRepository(MerrickContext db, ILogger<InventoryRepository>? logger = null, IConfiguration? configuration = null) : IInventoryRepository
{
    private readonly ILogger<InventoryRepository> _logger = logger ?? NullLogger<InventoryRepository>.Instance;
    private readonly IConfiguration? _configuration = configuration;

    public static readonly IReadOnlyList<string> AllPlinkoBorders =
    [
        "border_flaming_kongor",
        "border_frostbite",
        "border_electric_diamond",
        "border_prismatic_void",
        "border_hellbourne_pyre"
    ];

    public static readonly IReadOnlyList<string> AllPlinkoTitles =
    [
        "title_legion_veteran",
        "title_hellbourne_slayer",
        "title_tavern_regular",
        "title_kongors_bane",
        "title_immortal_legend"
    ];

    private bool IsUnlockAllItemsEnabled =>
        _configuration is not null && NEXUS.Contracts.Store.StoreEntitlementPolicy.IsFreeUnlockAll(_configuration);

    public async Task<IReadOnlyList<OwnedStoreItemDTO>> GetOwnedItemsAsync(int accountId,
        CancellationToken ct = default)
    {

        _logger.LogDebug("[InventoryRepository] Getting owned items for Account #{AccountId}", accountId);
        Account? account = await db.Accounts
            .AsNoTracking()
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is null) return [];

        List<UserInventory> dbInventory = await db.UserInventory
            .AsNoTracking()
            .Where(i => i.UserID == account.User.ID || i.AccountID == accountId)
            .ToListAsync(ct);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<OwnedStoreItemDTO> result = [];

        foreach (UserInventory inv in dbInventory)
            if (seen.Add(inv.ItemCode))
                result.Add(new OwnedStoreItemDTO
                {
                    Data = inv.ItemCode,
                    Permanent = inv.IsPermanent ? "1" : "0",
                    Used = 0,
                    ExpirationDate = inv.TimestampExpires?.ToUnixTimeSeconds().ToString() ?? "0"
                });

        _logger.LogDebug("[InventoryRepository] Resolved {Count} owned items for Account #{AccountId}", result.Count,
            accountId);
        return result;
    }

    public async Task<bool> HasItemAsync(int accountId, string itemCode, CancellationToken ct = default)
    {
        _logger.LogDebug("[InventoryRepository] Checking item ownership: Account #{AccountId}, Item '{ItemCode}'",
            accountId, itemCode);
        Account? account = await db.Accounts
            .AsNoTracking()
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is null) return false;

        return await db.UserInventory
            .AsNoTracking()
            .AnyAsync(i => (i.UserID == account.User.ID || i.AccountID == accountId) &&
                           (i.ItemCode == itemCode || i.ItemCode.EndsWith("." + itemCode)), ct);
    }

    public async Task GrantItemAsync(int accountId, string itemCode, int quantity = 1, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[InventoryRepository] Granting item '{ItemCode}' (qty: {Quantity}) to Account #{AccountId}", itemCode,
            quantity, accountId);
        Account? account = await db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is null)
        {
            _logger.LogWarning(
                "[InventoryRepository] Cannot grant item '{ItemCode}': Account #{AccountId} or associated User not found",
                itemCode, accountId);
            return;
        }

        StoreItem? storeItem = await db.StoreItems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == itemCode || s.Name == itemCode, ct);

        string trimmedCode = itemCode.Trim();
        UserInventory? existing = await db.UserInventory
            .FirstOrDefaultAsync(i => i.UserID == account.User.ID &&
                                      (i.ItemCode == trimmedCode || i.ItemCode.ToLower() == trimmedCode.ToLower()), ct);

        if (existing is not null)
        {
            existing.Quantity += quantity;
            existing.AccountID ??= account.ID;
            if (storeItem != null && existing.StoreItemId == null) existing.StoreItemId = storeItem.Id;
        }
        else
        {
            UserInventory newInv = new()
            {
                UserID = account.User.ID,
                AccountID = account.ID,
                ItemCode = trimmedCode,
                Quantity = quantity,
                StoreItemId = storeItem?.Id,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "Store"
            };
            await db.UserInventory.AddAsync(newInv, ct);
            if (account.User.InventoryItems != null && !account.User.InventoryItems.Any(i => ReferenceEquals(i, newInv) || string.Equals(i.ItemCode, trimmedCode, StringComparison.OrdinalIgnoreCase)))
            {
                account.User.InventoryItems.Add(newInv);
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[InventoryRepository] Successfully granted '{ItemCode}' to User #{UserId} / Account #{AccountId}",
            itemCode, account.User.ID, account.ID);
    }

    public async Task<bool> RevokeItemAsync(int accountId, string itemCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return false;
        string trimmed = itemCode.Trim();
        string lower = trimmed.ToLowerInvariant();

        _logger.LogInformation(
            "[InventoryRepository] Revoking item '{ItemCode}' from Account #{AccountId}", trimmed, accountId);

        Account? account = await db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is null)
        {
            _logger.LogWarning(
                "[InventoryRepository] Cannot revoke item '{ItemCode}': Account #{AccountId} or associated User not found",
                trimmed, accountId);
            return false;
        }

        UserInventory? existing = await db.UserInventory
            .FirstOrDefaultAsync(i => (i.UserID == account.User.ID || i.AccountID == account.ID) &&
                                      (i.ItemCode == trimmed || i.ItemCode.ToLower() == lower), ct);

        bool revoked = false;
        if (existing is not null)
        {
            if (existing.Quantity > 1)
            {
                existing.Quantity -= 1;
            }
            else
            {
                db.UserInventory.Remove(existing);
                account.User.InventoryItems?.Remove(existing);
            }
            revoked = true;
        }

        if (revoked)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "[InventoryRepository] Successfully revoked '{ItemCode}' from User #{UserId} / Account #{AccountId}",
                trimmed, account.User.ID, accountId);
        }

        return revoked;
    }

    public async Task<bool> DeductCurrenciesAsync(int accountId, int goldDelta, int silverDelta, int plinkoDelta,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[InventoryRepository] Deducting currencies from Account #{AccountId}: Gold -{Gold}, Silver -{Silver}, Plinko -{Plinko}",
            accountId, goldDelta, silverDelta, plinkoDelta);

        Account? account = await db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is null)
        {
            _logger.LogWarning("[InventoryRepository] Cannot deduct currency: Account #{AccountId} not found",
                accountId);
            return false;
        }

        if (db.Database.IsRelational())
        {
            int rowsUpdated = await db.Users
                .Where(u => u.ID == account.User.ID &&
                            u.GoldCoins >= goldDelta &&
                            u.SilverCoins >= silverDelta &&
                            u.PlinkoTickets >= plinkoDelta)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.GoldCoins, u => u.GoldCoins - goldDelta)
                    .SetProperty(u => u.SilverCoins, u => u.SilverCoins - silverDelta)
                    .SetProperty(u => u.PlinkoTickets, u => u.PlinkoTickets - plinkoDelta), ct);

            if (rowsUpdated == 0)
            {
                await db.Entry(account.User).ReloadAsync(ct);
                _logger.LogWarning(
                    "[InventoryRepository] Insufficient funds for Account #{AccountId}. User has Gold:{Gold}, Silver:{Silver}, Plinko:{Plinko}",
                    accountId, account.User.GoldCoins, account.User.SilverCoins, account.User.PlinkoTickets);
                return false;
            }

            await db.Entry(account.User).ReloadAsync(ct);
        }
        else
        {
            if (account.User.GoldCoins < goldDelta ||
                account.User.SilverCoins < silverDelta ||
                account.User.PlinkoTickets < plinkoDelta)
            {
                _logger.LogWarning(
                    "[InventoryRepository] Insufficient funds for Account #{AccountId}. User has Gold:{Gold}, Silver:{Silver}, Plinko:{Plinko}",
                    accountId, account.User.GoldCoins, account.User.SilverCoins, account.User.PlinkoTickets);
                return false;
            }

            account.User.GoldCoins -= goldDelta;
            account.User.SilverCoins -= silverDelta;
            account.User.PlinkoTickets -= plinkoDelta;
        }

        db.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = account.User.ID,
            AccountID = account.ID,
            GoldCoinsDelta = -goldDelta,
            SilverCoinsDelta = -silverDelta,
            PlinkoTicketsDelta = -plinkoDelta,
            GoldCoinsBalanceAfter = account.User.GoldCoins,
            SilverCoinsBalanceAfter = account.User.SilverCoins,
            PlinkoTicketsBalanceAfter = account.User.PlinkoTickets,
            TransactionType = "InventoryDeduct",
            Description = $"Deducted gold:{goldDelta}, silver:{silverDelta}, plinko:{plinkoDelta}",
            Timestamp = TimeProvider.System.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[InventoryRepository] Currencies deducted successfully for Account #{AccountId}. New Balances: Gold:{Gold}, Silver:{Silver}, Plinko:{Plinko}",
            accountId, account.User.GoldCoins, account.User.SilverCoins, account.User.PlinkoTickets);
        return true;
    }

    public async Task AddCurrenciesAsync(int accountId, int goldDelta, int silverDelta, int plinkoDelta,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[InventoryRepository] Adding currencies to Account #{AccountId}: Gold +{Gold}, Silver +{Silver}, Plinko +{Plinko}",
            accountId, goldDelta, silverDelta, plinkoDelta);

        Account? account = await db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is not null)
        {
            if (db.Database.IsRelational())
            {
                await db.Users
                    .Where(u => u.ID == account.User.ID)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(u => u.GoldCoins, u => u.GoldCoins + goldDelta)
                        .SetProperty(u => u.SilverCoins, u => u.SilverCoins + silverDelta)
                        .SetProperty(u => u.PlinkoTickets, u => u.PlinkoTickets + plinkoDelta), ct);

                await db.Entry(account.User).ReloadAsync(ct);
            }
            else
            {
                account.User.GoldCoins += goldDelta;
                account.User.SilverCoins += silverDelta;
                account.User.PlinkoTickets += plinkoDelta;
            }

            db.EconomyTransactions.Add(new EconomyTransaction
            {
                UserID = account.User.ID,
                AccountID = account.ID,
                GoldCoinsDelta = goldDelta,
                SilverCoinsDelta = silverDelta,
                PlinkoTicketsDelta = plinkoDelta,
                GoldCoinsBalanceAfter = account.User.GoldCoins,
                SilverCoinsBalanceAfter = account.User.SilverCoins,
                PlinkoTicketsBalanceAfter = account.User.PlinkoTickets,
                TransactionType = "InventoryAdd",
                Description = $"Added gold:{goldDelta}, silver:{silverDelta}, plinko:{plinkoDelta}",
                Timestamp = TimeProvider.System.GetUtcNow()
            });

            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "[InventoryRepository] Currencies added successfully for Account #{AccountId}. New Balances: Gold:{Gold}, Silver:{Silver}, Plinko:{Plinko}",
                accountId, account.User.GoldCoins, account.User.SilverCoins, account.User.PlinkoTickets);
        }
        else
        {
            _logger.LogWarning("[InventoryRepository] Cannot add currency: Account #{AccountId} not found", accountId);
        }
    }

    public async Task<IReadOnlyList<string>> GetOwnedItemCodesAsync(int accountId, CancellationToken ct = default)
    {
        Account? account = await db.Accounts
            .AsNoTracking()
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account?.User is null) return [];

        return await db.UserInventory
            .AsNoTracking()
            .Where(i => i.UserID == account.User.ID || i.AccountID == accountId)
            .Select(i => i.ItemCode)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetSelectedStoreItemsAsync(int accountId, CancellationToken ct = default)
    {
        List<string> items = await db.AccountSelectedStoreItems
            .AsNoTracking()
            .Where(s => s.AccountID == accountId)
            .Select(s => s.ItemCode)
            .ToListAsync(ct);

        return items;
    }

    public async Task SetSelectedStoreItemsAsync(int accountId, IEnumerable<string> items,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[InventoryRepository] Setting selected store items for Account #{AccountId}", accountId);
        Account? account = await db.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account is not null)
        {
            account.SetSelectedStoreItems(items);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<StoreItem?> GetStoreItemByCodeAsync(string itemCode, CancellationToken ct = default)
    {
        _logger.LogDebug("[InventoryRepository] Fetching StoreItem by Code/Name '{ItemCode}'", itemCode);
        return await db.StoreItems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == itemCode || s.Name == itemCode, ct);
    }

    public async Task<IReadOnlyList<StoreItem>> GetStoreCatalogAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[InventoryRepository] Fetching full Store catalog");
        return await db.StoreItems
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserWebReward>> GetUserWebRewardsAsync(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[InventoryRepository] Fetching web rewards for User #{UserId}", userId);
        return await db.UserWebRewards.AsNoTracking().Where(r => r.UserID == userId).ToListAsync(ct);
    }

    public async Task<bool> EquipWebRewardAsync(int userId, string rewardType, string rewardCode,
        CancellationToken ct = default)
    {
        string normalizedType = (rewardType ?? string.Empty).Trim();
        string normalizedCode = (rewardCode ?? string.Empty).Trim();
        _logger.LogInformation(
            "[InventoryRepository] Equipping web reward for User #{UserId}: Type '{RewardType}', Code '{RewardCode}' (UnlockAllItems={UnlockAll})",
            userId, normalizedType, normalizedCode, IsUnlockAllItemsEnabled);

        List<UserWebReward> rewards = await db.UserWebRewards
            .Where(r => r.UserID == userId && r.RewardType == normalizedType)
            .ToListAsync(ct);

        bool matchedExisting = false;
        foreach (UserWebReward r in rewards)
        {
            bool match = !string.IsNullOrEmpty(normalizedCode) &&
                         string.Equals(r.RewardCode, normalizedCode, StringComparison.OrdinalIgnoreCase);
            r.IsEquipped = match;
            if (match) matchedExisting = true;
        }

        if (!matchedExisting && !string.IsNullOrEmpty(normalizedCode))
        {
            _logger.LogInformation(
                "[InventoryRepository] Upserting unlocked web reward row for User #{UserId}: Type '{RewardType}', Code '{RewardCode}'",
                userId, normalizedType, normalizedCode);
            db.UserWebRewards.Add(new UserWebReward
            {
                UserID = userId,
                RewardType = normalizedType,
                RewardCode = normalizedCode,
                Name = normalizedCode,
                IsEquipped = true,
                Source = IsUnlockAllItemsEnabled ? "GlobalUnlock" : "WebVault",
                TimestampAcquired = TimeProvider.System.GetUtcNow()
            });
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string NormalizeStoreIdempotencyKey(int userId, int itemId, string? rawIdempotencyKey)
    {
        if (!string.IsNullOrWhiteSpace(rawIdempotencyKey))
        {
            string trimmed = rawIdempotencyKey.Trim();
            Span<char> buf = stackalloc char[Math.Min(trimmed.Length, 80)];
            int len = 0;
            foreach (char ch in trimmed)
            {
                if (ch is >= '!' and <= '~')
                {
                    buf[len++] = ch;
                    if (len == buf.Length) break;
                }
            }

            if (len > 0)
            {
                return $"web-store:{userId}:{itemId}:{new string(buf[..len])}";
            }
        }

        return $"web-store:{userId}:{itemId}:{Guid.NewGuid():N}";
    }

    private static bool IsUniqueIdempotencyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pgEx &&
        pgEx.SqlState == PostgresErrorCodes.UniqueViolation &&
        (string.Equals(pgEx.ConstraintName, "IX_EconomyTransactions_UserID_IdempotencyKey", StringComparison.OrdinalIgnoreCase) ||
         (pgEx.MessageText?.Contains("IX_EconomyTransactions_UserID_IdempotencyKey", StringComparison.OrdinalIgnoreCase) ?? false));

    public Task<PurchaseItemResultDto> PurchaseStoreItemAsync(int userId, int itemId, string currency,
        CancellationToken ct = default) =>
        PurchaseStoreItemAsync(userId, itemId, currency, idempotencyKey: null, ct);

    public async Task<PurchaseItemResultDto> PurchaseStoreItemAsync(int userId, int itemId, string currency, string? idempotencyKey,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[InventoryRepository] Processing purchase for User #{UserId}, Item #{ItemId}, Currency '{Currency}'",
            userId, itemId, currency);

        string effectiveIdempotencyKey = NormalizeStoreIdempotencyKey(userId, itemId, idempotencyKey);
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
        {
            return await ExecutePurchaseStoreItemCoreAsync(userId, itemId, currency, effectiveIdempotencyKey, 0, startExplicitTx: false, ct);
        }

        int attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            int currentAttempt = attempt++;
            if (currentAttempt > 0)
            {
                db.ChangeTracker.Clear();
                _logger.LogWarning(
                    "[InventoryRepository] Retrying PurchaseStoreItemAsync (attempt #{Attempt}) for User #{UserId}, Item #{ItemId} (IdempotencyKey={IdempotencyKey}).",
                    currentAttempt + 1, userId, itemId, effectiveIdempotencyKey);
            }

            return await ExecutePurchaseStoreItemCoreAsync(userId, itemId, currency, effectiveIdempotencyKey, currentAttempt, startExplicitTx: true, ct);
        });
    }

    private async Task<PurchaseItemResultDto> ExecutePurchaseStoreItemCoreAsync(
        int userId,
        int itemId,
        string currency,
        string effectiveIdempotencyKey,
        int attempt,
        bool startExplicitTx,
        CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational())
        {
            try
            {
                if (startExplicitTx)
                {
                    transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                }
            }
            catch (Exception ex)
            {
                if (ex is not NotSupportedException)
                {
                    _logger.LogError(ex, "[InventoryRepository] Failed to begin RepeatableRead transaction for User #{UserId}, Item #{ItemId}.", userId, itemId);
                    throw;
                }
                _logger.LogDebug(ex, "[InventoryRepository] Provider does not support explicit transactions; proceeding with context tracking.");
            }
        }

        try
        {
            User? user = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == userId, ct);
            if (user == null)
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                return new PurchaseItemResultDto(false, "User not found", 0, 0, itemId, null, null);
            }

            StoreItem? item = await db.StoreItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
            if (item == null || string.IsNullOrWhiteSpace(item.Code))
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                _logger.LogWarning("[SECURITY-ALERT] Rejected purchase of nonexistent StoreItem #{ItemId} by User #{UserId}.", itemId, userId);
                return new PurchaseItemResultDto(false, "Item not found", user.GoldCoins, user.SilverCoins, itemId, null, null);
            }

            if (!item.Enabled || !item.Purchasable)
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                _logger.LogWarning("[SECURITY-ALERT] Rejected purchase of disabled/unpurchasable StoreItem #{ItemId} ('{Code}') by User #{UserId}.", itemId, item.Code, userId);
                return new PurchaseItemResultDto(false, "Item is not available for purchase", user.GoldCoins, user.SilverCoins, itemId, item.Code, item.Name);
            }

            EconomyTransaction? existingTx = await db.EconomyTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.UserID == userId &&
                         t.IdempotencyKey == effectiveIdempotencyKey,
                    ct);
            if (existingTx is not null)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(ct);
                    await transaction.DisposeAsync();
                    transaction = null;
                }
                _logger.LogWarning(
                    "[InventoryRepository] Idempotent purchase replay detected for User #{UserId}, Item #{ItemId} (IdempotencyKey={IdempotencyKey}); returning committed balances.",
                    userId, itemId, effectiveIdempotencyKey);
                return new PurchaseItemResultDto(
                    true,
                    $"Successfully unlocked '{item.Name}'!",
                    existingTx.GoldCoinsBalanceAfter,
                    existingTx.SilverCoinsBalanceAfter,
                    itemId,
                    item.Code,
                    item.Name);
            }


            string itemName = item.Name;
            string itemCode = item.Code;

            bool unlockAllItems = IsUnlockAllItemsEnabled;
            int goldCost = unlockAllItems ? 0 : item.Price;
            int silverCost = unlockAllItems ? 0 : item.PriceInMMP;

            string currCheck = (currency ?? "").Trim().ToLowerInvariant();
            if (unlockAllItems && string.IsNullOrEmpty(currCheck))
            {
                currCheck = "gold";
            }

            if (!unlockAllItems &&
                ((currCheck == "gold" && (goldCost < 0 || (goldCost == 0 && silverCost > 0))) ||
                 (currCheck == "silver" && (silverCost < 0 || (silverCost == 0 && goldCost > 0)))))
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                return new PurchaseItemResultDto(false, "Item cannot be purchased with the selected currency.", user.GoldCoins, user.SilverCoins, itemId, item.Code, item.Name);
            }

            string curr = currCheck;
            int goldDelta = 0;
            int silverDelta = 0;
            if (unlockAllItems)
            {
                if (curr != "gold" && curr != "silver")
                {
                    if (transaction != null) await transaction.RollbackAsync(ct);
                    return new PurchaseItemResultDto(false, "Invalid currency", user.GoldCoins, user.SilverCoins, itemId,
                        itemCode, itemName);
                }

                _logger.LogInformation(
                    "[InventoryRepository] Global Store Unlock active (Store:UnlockAllItems=true): unlocking Item #{ItemId} ('{ItemCode}') for User #{UserId} at 0 cost.",
                    itemId, itemCode, user.ID);
            }
            else if (curr == "gold")
            {
                if (db.Database.IsRelational())
                {
                    int rows = await db.Users
                        .Where(u => u.ID == user.ID && u.GoldCoins >= goldCost)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.GoldCoins, u => u.GoldCoins - goldCost), ct);
                    if (rows == 0)
                    {
                        if (transaction != null) await transaction.RollbackAsync(ct);
                        await db.Entry(user).ReloadAsync(ct);
                        return new PurchaseItemResultDto(false,
                            $"Insufficient Gold Coins! You have {user.GoldCoins}, need {goldCost}.", user.GoldCoins,
                            user.SilverCoins, itemId, itemCode, itemName);
                    }
                    await db.Entry(user).ReloadAsync(ct);
                }
                else
                {
                    if (user.GoldCoins < goldCost)
                    {
                        if (transaction != null) await transaction.RollbackAsync(ct);
                        return new PurchaseItemResultDto(false,
                            $"Insufficient Gold Coins! You have {user.GoldCoins}, need {goldCost}.", user.GoldCoins,
                            user.SilverCoins, itemId, itemCode, itemName);
                    }
                    user.GoldCoins -= goldCost;
                }
                goldDelta = -goldCost;
            }
            else if (curr == "silver")
            {
                if (db.Database.IsRelational())
                {
                    int rows = await db.Users
                        .Where(u => u.ID == user.ID && u.SilverCoins >= silverCost)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.SilverCoins, u => u.SilverCoins - silverCost), ct);
                    if (rows == 0)
                    {
                        if (transaction != null) await transaction.RollbackAsync(ct);
                        await db.Entry(user).ReloadAsync(ct);
                        return new PurchaseItemResultDto(false,
                            $"Insufficient Silver Coins! You have {user.SilverCoins}, need {silverCost}.", user.GoldCoins,
                            user.SilverCoins, itemId, itemCode, itemName);
                    }
                    await db.Entry(user).ReloadAsync(ct);
                }
                else
                {
                    if (user.SilverCoins < silverCost)
                    {
                        if (transaction != null) await transaction.RollbackAsync(ct);
                        return new PurchaseItemResultDto(false,
                            $"Insufficient Silver Coins! You have {user.SilverCoins}, need {silverCost}.", user.GoldCoins,
                            user.SilverCoins, itemId, itemCode, itemName);
                    }
                    user.SilverCoins -= silverCost;
                }
                silverDelta = -silverCost;
            }
            else
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                return new PurchaseItemResultDto(false, "Invalid currency", user.GoldCoins, user.SilverCoins, itemId,
                    itemCode, itemName);
            }

            Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();

            // 1. Synchronize UserInventory
            UserInventory? existingInv =
                await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == itemCode, ct);
            if (existingInv != null)
            {
                existingInv.Quantity += 1;
                existingInv.AccountID ??= mainAccount?.ID;
                if (item != null && existingInv.StoreItemId == null) existingInv.StoreItemId = item.Id;
            }
            else
            {
                db.UserInventory.Add(new UserInventory
                {
                    UserID = user.ID,
                    AccountID = mainAccount?.ID,
                    ItemCode = itemCode,
                    StoreItemId = item?.Id,
                    Quantity = 1,
                    IsPermanent = true,
                    TimestampAcquired = TimeProvider.System.GetUtcNow(),
                    Source = "WebStore"
                });
            }


            // 3. Log EconomyTransaction
            db.EconomyTransactions.Add(new EconomyTransaction
            {
                UserID = user.ID,
                AccountID = mainAccount?.ID,
                GoldCoinsDelta = goldDelta,
                SilverCoinsDelta = silverDelta,
                PlinkoTicketsDelta = 0,
                GoldCoinsBalanceAfter = user.GoldCoins,
                SilverCoinsBalanceAfter = user.SilverCoins,
                PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                TransactionType = "StorePurchase",
                ReferenceId = itemCode,
                IdempotencyKey = effectiveIdempotencyKey,
                Description = $"Purchased '{itemName}' with {curr}",
                Timestamp = TimeProvider.System.GetUtcNow()
            });

            await db.SaveChangesAsync(ct);
            if (transaction != null)
            {
                await transaction.CommitAsync(ct);
            }

            _logger.LogInformation(
                "[InventoryRepository] Purchase succeeded for User #{UserId}, Item #{ItemId} ('{ItemName}')", userId,
                itemId, itemName);
            return new PurchaseItemResultDto(true, $"Successfully unlocked '{itemName}'!", user.GoldCoins, user.SilverCoins,
                itemId, itemCode, itemName);
        }
        catch (DbUpdateException dbEx) when (IsUniqueIdempotencyViolation(dbEx))
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
                transaction = null;
            }
            db.ChangeTracker.Clear();

            EconomyTransaction? racedTx = await db.EconomyTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.UserID == userId && t.IdempotencyKey == effectiveIdempotencyKey,
                    ct);
            if (racedTx is not null)
            {
                StoreItem? racedItem = await db.StoreItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
                string resolvedName = racedItem?.Name ?? $"Item #{itemId}";
                string? resolvedCode = racedItem?.Code;
                _logger.LogWarning(
                    "[InventoryRepository] Concurrent idempotency race (23505) resolved for User #{UserId}, Item #{ItemId} (IdempotencyKey={IdempotencyKey}); returning committed balances.",
                    userId, itemId, effectiveIdempotencyKey);
                return new PurchaseItemResultDto(
                    true,
                    $"Successfully unlocked '{resolvedName}'!",
                    racedTx.GoldCoinsBalanceAfter,
                    racedTx.SilverCoinsBalanceAfter,
                    itemId,
                    resolvedCode,
                    resolvedName);
            }

            throw;
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<EquipItemResultDto> EquipStoreItemAsync(string accountName, int itemId, string? code,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[InventoryRepository] Equipping store item for Account '{AccountName}', Item #{ItemId}, Code '{Code}'",
            accountName, itemId, code);
        Account? account = await db.Accounts
            .Include(a => a.Clan)
            .Include(a => a.User)
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.Name == accountName, ct);
        if (account == null) return new EquipItemResultDto(false, "Account not found", null, []);

        string equipCode = code?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(equipCode) && itemId > 0)
        {
            StoreItem? storeItem = await db.StoreItems.FirstOrDefaultAsync(s => s.Id == itemId, ct);
            equipCode = storeItem is not null && !string.IsNullOrWhiteSpace(storeItem.Code)
                ? storeItem.Code.Trim()
                : $"item_{itemId}";
        }
        else if (string.IsNullOrWhiteSpace(equipCode))
        {
            equipCode = $"item_{itemId}";
        }

        bool unlockAllItems = NEXUS.Contracts.Store.StoreEntitlementPolicy.IsFreeUnlockAll(_configuration, defaultFreeAll: true);
        bool isRestricted = NEXUS.Contracts.Store.StoreEntitlementPolicy.IsRestrictedEntitlement(equipCode, code);
        bool isAuthorizedForRestricted = !isRestricted ||
                                         NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                                             account.Type, account.Clan?.Tag, equipCode, code);

        if ((!unlockAllItems || !isAuthorizedForRestricted) &&
            !await IsStoreItemOwnedOrDefaultAsync(account, itemId, equipCode, code, ct))
        {
            List<string> currentSelected = [.. account.SelectedStoreItems.Select(s => s.ItemCode)];
            _logger.LogWarning(
                "[SECURITY-ALERT] Blocked unauthorized/unowned StoreItem equip attempt by Account '{AccountName}' (#{AccountId}, Type={AccountType}) for Item #{ItemId} / Code '{Code}' (Restricted={IsRestricted}).",
                account.Name, account.ID, account.Type, itemId, equipCode, isRestricted);
            return new EquipItemResultDto(false, "You do not own or are not authorized to equip this item.", null, currentSelected);
        }

        void ReplaceCategory(string prefix, string newCode)
        {
            newCode = newCode.Trim();
            bool alreadyEquipped = false;
            List<AccountSelectedStoreItem> toRemove = [];

            foreach (AccountSelectedStoreItem item in account.SelectedStoreItems)
            {
                if (item.ItemCode.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!alreadyEquipped && string.Equals(item.ItemCode.Trim(), newCode, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyEquipped = true;
                    }
                    else
                    {
                        toRemove.Add(item);
                    }
                }
            }

            foreach (AccountSelectedStoreItem item in toRemove)
            {
                account.SelectedStoreItems.Remove(item);
            }

            if (!alreadyEquipped)
            {
                account.SelectedStoreItems.Add(new AccountSelectedStoreItem
                {
                    AccountID = account.ID,
                    ItemCode = newCode
                });
            }
        }

        // 1. Account Icon (ai.)
        if (equipCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
            equipCode.StartsWith("icon.", StringComparison.OrdinalIgnoreCase) ||
            equipCode.Contains("account_icon", StringComparison.OrdinalIgnoreCase))
        {
            if (equipCode.StartsWith("icon.", StringComparison.OrdinalIgnoreCase))
            {
                equipCode = $"ai.{equipCode["icon.".Length..]}";
            }
            else if (!equipCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase))
            {
                equipCode = $"ai.{equipCode}";
            }
            ReplaceCategory("ai.", equipCode);
        }
        // 2. Chat Color (cc.) - Checked before Courier (c.)
        else if (equipCode.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("color", StringComparison.OrdinalIgnoreCase))
        {
            equipCode = equipCode.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ? equipCode : $"cc.{equipCode}";
            ReplaceCategory("cc.", equipCode);
        }
        // 3. Chat Shield / Symbol (cs.) - Checked before Courier (c.)
        else if (equipCode.StartsWith("cs.", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("shield", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("symbol", StringComparison.OrdinalIgnoreCase))
        {
            equipCode = equipCode.StartsWith("cs.", StringComparison.OrdinalIgnoreCase) ? equipCode : $"cs.{equipCode}";
            ReplaceCategory("cs.", equipCode);
        }
        // 4. Courier (c.) - Evaluated AFTER cc. and cs.
        else if (equipCode.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("courier", StringComparison.OrdinalIgnoreCase))
        {
            equipCode = equipCode.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ? equipCode : $"c.{equipCode}";
            ReplaceCategory("c.", equipCode);
        }
        // 5. Taunt (t.)
        else if (equipCode.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("taunt", StringComparison.OrdinalIgnoreCase))
        {
            equipCode = equipCode.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ? equipCode : $"t.{equipCode}";
            ReplaceCategory("t.", equipCode);
        }
        // 6. Avatar / Announcer Voice (av.)
        else if (equipCode.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("announcer", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("avatar", StringComparison.OrdinalIgnoreCase))
        {
            equipCode = equipCode.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ? equipCode : $"av.{equipCode}";
            ReplaceCategory("av.", equipCode);
        }
        // 7. Ward (w.)
        else if (equipCode.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                 equipCode.Contains("ward", StringComparison.OrdinalIgnoreCase))
        {
            equipCode = equipCode.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ? equipCode : $"w.{equipCode}";
            ReplaceCategory("w.", equipCode);
        }
        // 8. Arbitrary item fallback
        else
        {
            equipCode = equipCode.Trim();
            if (!account.SelectedStoreItems.Any(x => string.Equals(x.ItemCode.Trim(), equipCode, StringComparison.OrdinalIgnoreCase)))
            {
                account.SelectedStoreItems.Add(new AccountSelectedStoreItem
                {
                    AccountID = account.ID,
                    ItemCode = equipCode
                });
            }
        }

        await db.SaveChangesAsync(ct);
        List<string> selected = account.SelectedStoreItems.Select(s => s.ItemCode).ToList();
        return new EquipItemResultDto(true, $"Equipped '{equipCode}' successfully!", equipCode, selected);
    }

    private async Task<bool> IsStoreItemOwnedOrDefaultAsync(
        Account account,
        int itemId,
        string equipCode,
        string? rawCode,
        CancellationToken ct)
    {
        HashSet<string> candidateCodes = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(equipCode)) candidateCodes.Add(equipCode.Trim());
        if (!string.IsNullOrWhiteSpace(rawCode)) candidateCodes.Add(rawCode.Trim());

        if (equipCode.StartsWith("icon.", StringComparison.OrdinalIgnoreCase))
            candidateCodes.Add($"ai.{equipCode["icon.".Length..]}");
        else if (equipCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase))
            candidateCodes.Add($"icon.{equipCode["ai.".Length..]}");

        if (candidateCodes.Any(c =>
                c.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("ai.Default Icon", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("ai.custom_icon:1", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("cc.white", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("t.Standard", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (itemId > 0)
        {
            StoreItem? storeItem = await db.StoreItems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == itemId, ct);
            if (storeItem != null && !string.IsNullOrWhiteSpace(storeItem.Code))
            {
                candidateCodes.Add(storeItem.Code.Trim());
            }
        }

        List<string> codesList = [.. candidateCodes];
        return await db.UserInventory
            .AsNoTracking()
            .AnyAsync(inv =>
                (inv.UserID == account.UserID || inv.AccountID == account.ID) &&
                inv.Quantity > 0 &&
                codesList.Contains(inv.ItemCode), ct);
    }

    public async Task<PlinkoStatusResponseDto> GetPlinkoStatusAsync(int userId, CancellationToken ct = default)
    {
        bool unlockAllItems = IsUnlockAllItemsEnabled;
        _logger.LogDebug(
            "[InventoryRepository] Getting Plinko status for User #{UserId} (UnlockAllItems={UnlockAll})",
            userId, unlockAllItems);
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null)
        {
            return new PlinkoStatusResponseDto(
                false,
                false,
                0,
                0,
                0,
                0,
                unlockAllItems ? 0 : 100,
                unlockAllItems ? 0 : 50,
                [],
                [],
                null,
                null);
        }

        TimeSpan dailyCooldown = TimeSpan.FromHours(24);
        DateTimeOffset? lastClaim = user.LastPlinkoDailyClaimUtc;
        bool canDaily = true;
        double remainingSeconds = 0;

        if (!unlockAllItems && lastClaim.HasValue)
        {
            TimeSpan elapsed = TimeProvider.System.GetUtcNow() - lastClaim.Value;
            if (elapsed < dailyCooldown)
            {
                canDaily = false;
                remainingSeconds = Math.Max(0, (dailyCooldown - elapsed).TotalSeconds);
            }
        }

        List<UserWebReward> webRewards =
            await db.UserWebRewards.AsNoTracking().Where(r => r.UserID == user.ID).ToListAsync(ct);

        IEnumerable<string> borderSource = webRewards
            .Where(r => r.RewardType.Equals("Border", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RewardCode);
        if (unlockAllItems)
        {
            borderSource = AllPlinkoBorders.Concat(borderSource);
        }

        List<string> unlockedBorders = [.. borderSource.Distinct(StringComparer.OrdinalIgnoreCase)];

        IEnumerable<string> titleSource = webRewards
            .Where(r => r.RewardType.Equals("Title", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RewardCode);
        if (unlockAllItems)
        {
            titleSource = AllPlinkoTitles.Concat(titleSource);
        }

        List<string> unlockedTitles = [.. titleSource.Distinct(StringComparer.OrdinalIgnoreCase)];

        string? equippedBorder = webRewards
            .FirstOrDefault(r => r.RewardType.Equals("Border", StringComparison.OrdinalIgnoreCase) && r.IsEquipped)
            ?.RewardCode;
        string? equippedTitle = webRewards
            .FirstOrDefault(r => r.RewardType.Equals("Title", StringComparison.OrdinalIgnoreCase) && r.IsEquipped)
            ?.RewardCode;

        return new PlinkoStatusResponseDto(
            true,
            canDaily,
            Math.Ceiling(remainingSeconds),
            user.PlinkoTickets,
            user.GoldCoins,
            user.SilverCoins,
            unlockAllItems ? 0 : 100,
            unlockAllItems ? 0 : 50,
            unlockedBorders,
            unlockedTitles,
            equippedBorder,
            equippedTitle
        );
    }

    public async Task<PlinkoDropResultDto> PlayPlinkoDropAsync(int userId, string? currency, bool useDaily,
        CancellationToken ct = default)
    {
        bool unlockAllItems = IsUnlockAllItemsEnabled;
        _logger.LogInformation(
            "[InventoryRepository] Playing Plinko drop for User #{UserId}, Currency '{Currency}', UseDaily {UseDaily} (UnlockAllItems={UnlockAll})",
            userId, currency, useDaily, unlockAllItems);
        User? user = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null)
            return new PlinkoDropResultDto(false, "User not found", 0, null, null, null, 0, 0, 0, 0, false);

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        TimeSpan dailyCooldown = TimeSpan.FromHours(24);

        string curr = currency?.ToLowerInvariant() ?? (useDaily ? "daily" : "tickets");
        int feeGoldDelta = 0;
        int feePlinkoDelta = 0;

        if (unlockAllItems)
        {
            if (curr == "daily" || useDaily)
            {
                user.LastPlinkoDailyClaimUtc = TimeProvider.System.GetUtcNow();
            }

            _logger.LogInformation(
                "[InventoryRepository] Global Store Unlock active: waiving Plinko entry fee/cooldown for User #{UserId} (mode='{Mode}')",
                userId, curr);
        }
        else if (curr == "daily" || useDaily)
        {
            if (user.LastPlinkoDailyClaimUtc.HasValue &&
                TimeProvider.System.GetUtcNow() - user.LastPlinkoDailyClaimUtc.Value < dailyCooldown)
                return new PlinkoDropResultDto(false, "Daily free drop is on cooldown", 0, null, null, null, 0,
                    user.PlinkoTickets, user.GoldCoins, user.SilverCoins, false);
            user.LastPlinkoDailyClaimUtc = TimeProvider.System.GetUtcNow();
        }
        else if (curr == "gold")
        {
            if (db.Database.IsRelational())
            {
                int rows = await db.Users
                    .Where(u => u.ID == user.ID && u.GoldCoins >= 50)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.GoldCoins, u => u.GoldCoins - 50), ct);
                if (rows == 0)
                {
                    await db.Entry(user).ReloadAsync(ct);
                    return new PlinkoDropResultDto(false, "Insufficient Gold coins", 0, null, null, null, 0,
                        user.PlinkoTickets, user.GoldCoins, user.SilverCoins, false);
                }
                await db.Entry(user).ReloadAsync(ct);
            }
            else
            {
                if (user.GoldCoins < 50)
                    return new PlinkoDropResultDto(false, "Insufficient Gold coins", 0, null, null, null, 0,
                        user.PlinkoTickets, user.GoldCoins, user.SilverCoins, false);
                user.GoldCoins -= 50;
            }
            feeGoldDelta = -50;
        }
        else
        {
            if (db.Database.IsRelational())
            {
                int rows = await db.Users
                    .Where(u => u.ID == user.ID && u.PlinkoTickets >= 100)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.PlinkoTickets, u => u.PlinkoTickets - 100), ct);
                if (rows == 0)
                {
                    await db.Entry(user).ReloadAsync(ct);
                    return new PlinkoDropResultDto(false, "Insufficient Plinko tickets", 0, null, null, null, 0,
                        user.PlinkoTickets, user.GoldCoins, user.SilverCoins, false);
                }
                await db.Entry(user).ReloadAsync(ct);
            }
            else
            {
                if (user.PlinkoTickets < 100)
                    return new PlinkoDropResultDto(false, "Insufficient Plinko tickets", 0, null, null, null, 0,
                        user.PlinkoTickets, user.GoldCoins, user.SilverCoins, false);
                user.PlinkoTickets -= 100;
            }
            feePlinkoDelta = -100;
        }

        if (feeGoldDelta != 0 || feePlinkoDelta != 0)
            db.EconomyTransactions.Add(new EconomyTransaction
            {
                UserID = user.ID,
                AccountID = mainAccount?.ID,
                GoldCoinsDelta = feeGoldDelta,
                SilverCoinsDelta = 0,
                PlinkoTicketsDelta = feePlinkoDelta,
                GoldCoinsBalanceAfter = user.GoldCoins,
                SilverCoinsBalanceAfter = user.SilverCoins,
                PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                TransactionType = "PlinkoFee",
                Description = $"Plinko entry fee ({curr})",
                Timestamp = TimeProvider.System.GetUtcNow()
            });

        int slot = RandomNumberGenerator.GetInt32(0, 9);
        string rewardType;
        string? rewardCode = null;
        string rewardName;
        int rewardAmount = 0;
        bool isJackpot = false;

        int winGoldDelta = 0;
        int winSilverDelta = 0;
        int winPlinkoDelta = 0;

        switch (slot)
        {
            case 0:
            case 8:
                rewardType = "Gold";
                rewardAmount = 150;
                rewardName = "150 Gold Coins";
                user.GoldCoins += rewardAmount;
                winGoldDelta = rewardAmount;
                isJackpot = true;
                break;
            case 1:
            case 7:
                rewardType = "Silver";
                rewardAmount = 500;
                rewardName = "500 Silver Coins";
                user.SilverCoins += rewardAmount;
                winSilverDelta = rewardAmount;
                break;
            case 2:
            case 6:
                rewardType = "Tickets";
                rewardAmount = 250;
                rewardName = "250 Plinko Tickets";
                user.PlinkoTickets += rewardAmount;
                winPlinkoDelta = rewardAmount;
                break;
            case 3:
            case 5:
                rewardType = "Silver";
                rewardAmount = 200;
                rewardName = "200 Silver Coins";
                user.SilverCoins += rewardAmount;
                winSilverDelta = rewardAmount;
                break;
            default:
                rewardType = "Gold";
                rewardAmount = 50;
                rewardName = "50 Gold Coins";
                user.GoldCoins += rewardAmount;
                winGoldDelta = rewardAmount;
                break;
        }

        db.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = mainAccount?.ID,
            GoldCoinsDelta = winGoldDelta,
            SilverCoinsDelta = winSilverDelta,
            PlinkoTicketsDelta = winPlinkoDelta,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "PlinkoReward",
            ReferenceId = $"Slot_{slot + 1}",
            Description = $"Plinko Slot #{slot + 1} win ({rewardName})",
            Timestamp = TimeProvider.System.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);

        return new PlinkoDropResultDto(
            true,
            $"You landed on Slot #{slot + 1} and won {rewardName}!",
            slot,
            rewardType,
            rewardCode,
            rewardName,
            rewardAmount,
            user.PlinkoTickets,
            user.GoldCoins,
            user.SilverCoins,
            isJackpot
        );
    }

    public async Task<RedeemCodeResultDto> RedeemPromoCodeAsync(int userId, string code, CancellationToken ct = default)
    {
        _logger.LogInformation("[InventoryRepository] Redeeming promo code '{Code}' for User #{UserId}", code, userId);
        User? user = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return new RedeemCodeResultDto(false, "User not found", 0, 0, []);

        string normalizedCode = (code ?? "").Trim().ToUpperInvariant();
        RedeemableCode? redeemCode = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == normalizedCode, ct);
        if (redeemCode == null || redeemCode.TimestampRedeemed != null)
            return new RedeemCodeResultDto(false, "Invalid or already redeemed code", 0, 0, []);

        user.GoldCoins += redeemCode.GoldCoinsReward;
        user.SilverCoins += redeemCode.SilverCoinsReward;
        user.PlinkoTickets += redeemCode.PlinkoTicketsReward;

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        redeemCode.TimestampRedeemed = TimeProvider.System.GetUtcNow();
        redeemCode.RedeemedByAccountID = mainAccount?.ID;

        List<string> grantedProducts = [];
        if (redeemCode.ProductID.HasValue)
        {
            StoreItem? storeItem = await db.StoreItems.FirstOrDefaultAsync(s => s.Id == redeemCode.ProductID.Value, ct);
            string productCode = storeItem?.Code ?? redeemCode.ProductID.Value.ToString();
            grantedProducts.Add(productCode);

            // Synchronize UserInventory
            UserInventory? existingInv =
                await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == productCode, ct);
            if (existingInv != null)
            {
                existingInv.Quantity += 1;
                existingInv.AccountID ??= mainAccount?.ID;
                if (storeItem != null && existingInv.StoreItemId == null) existingInv.StoreItemId = storeItem.Id;
            }
            else
            {
                db.UserInventory.Add(new UserInventory
                {
                    UserID = user.ID,
                    AccountID = mainAccount?.ID,
                    ItemCode = productCode,
                    StoreItemId = storeItem?.Id,
                    Quantity = 1,
                    IsPermanent = true,
                    TimestampAcquired = TimeProvider.System.GetUtcNow(),
                    Source = "PromoCode"
                });
            }
        }

        db.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = mainAccount?.ID,
            GoldCoinsDelta = redeemCode.GoldCoinsReward,
            SilverCoinsDelta = redeemCode.SilverCoinsReward,
            PlinkoTicketsDelta = redeemCode.PlinkoTicketsReward,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "CodeRedemption",
            ReferenceId = redeemCode.Code,
            Description = $"Redeemed promo code {redeemCode.Code}",
            Timestamp = TimeProvider.System.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[InventoryRepository] Successfully redeemed promo code '{Code}' for User #{UserId}",
            code, userId);
        return new RedeemCodeResultDto(true, "Code redeemed successfully!", redeemCode.GoldCoinsReward,
            redeemCode.SilverCoinsReward, grantedProducts);
    }

    public async Task<bool> ValidatePromoCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        string trimmedCode = code.Trim();
        string upperCode = trimmedCode.ToUpperInvariant();
        return await db.RedeemableCodes
            .AsNoTracking()
            .AnyAsync(rc => (rc.Code == trimmedCode || rc.Code == upperCode) && rc.TimestampRedeemed == null, ct);
    }
}
