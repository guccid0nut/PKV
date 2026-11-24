// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;

namespace MERRICK.DatabaseContext.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IStoreRepository"/> using <see cref="MerrickContext"/>.
/// </summary>
public class StoreRepository(
    MerrickContext db,
    ILogger<StoreRepository>? logger = null,
    INexusHybridCache? hybridCache = null) : IStoreRepository, IDisposable
{
    private readonly MerrickContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<StoreRepository> _logger = logger ?? NullLogger<StoreRepository>.Instance;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    private static TimeSpan GetL2ExpirationWithJitter(int baseMinutes = 10, int jitterSeconds = 15)
    {
        int offset = Random.Shared.Next(-jitterSeconds, jitterSeconds + 1);
        return TimeSpan.FromMinutes(baseMinutes) + TimeSpan.FromSeconds(offset);
    }

    public async Task<StoreItem?> GetProductByIdAsync(int productId, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Fetching product by ID #{ProductId}", productId);
        if (hybridCache != null)
        {
            return await hybridCache.GetOrCreateAsync(
                $"store:product:id:{productId}",
                async token =>
                {
                    await _dbLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        return await _db.StoreItems
                            .AsNoTracking()
                            .FirstOrDefaultAsync(s => s.Id == productId, token);
                    }
                    finally
                    {
                        _dbLock.Release();
                    }
                },
                ["store:catalog"],
                TimeSpan.FromMinutes(1),
                GetL2ExpirationWithJitter(10),
                ct);
        }

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.StoreItems
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == productId, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<StoreItem?> GetProductByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("[StoreRepository] GetProductByCodeAsync called with null or empty code");
            return null;
        }

        string trimmed = code.Trim();
        string lower = trimmed.ToLowerInvariant();
        _logger.LogDebug("[StoreRepository] Fetching product by Code/Name '{Code}'", trimmed);
        if (hybridCache != null)
        {
            return await hybridCache.GetOrCreateAsync(
                $"store:product:code:{lower}",
                async token =>
                {
                    await _dbLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        return await _db.StoreItems
                            .AsNoTracking()
                            .FirstOrDefaultAsync(s => s.Code.ToLower() == lower || s.Name.ToLower() == lower, token);
                    }
                    finally
                    {
                        _dbLock.Release();
                    }
                },
                ["store:catalog"],
                TimeSpan.FromMinutes(1),
                GetL2ExpirationWithJitter(10),
                ct);
        }

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.StoreItems
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Code.ToLower() == lower || s.Name.ToLower() == lower, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<StoreItem>> GetStoreCatalogAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Fetching full store catalog");
        if (hybridCache != null)
        {
            return await hybridCache.GetOrCreateAsync(
                "store:catalog:all",
                async token =>
                {
                    await _dbLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        List<StoreItem> items = await _db.StoreItems
                            .AsNoTracking()
                            .ToListAsync(token);
                        _logger.LogDebug("[StoreRepository] Retrieved {Count} catalog items from database (cached)", items.Count);
                        return (IReadOnlyList<StoreItem>)items;
                    }
                    finally
                    {
                        _dbLock.Release();
                    }
                },
                ["store:catalog"],
                TimeSpan.FromMinutes(1),
                GetL2ExpirationWithJitter(10),
                ct) ?? [];
        }

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<StoreItem> dbItems = await _db.StoreItems
                .AsNoTracking()
                .ToListAsync(ct);

            _logger.LogDebug("[StoreRepository] Retrieved {Count} catalog items from database", dbItems.Count);
            return dbItems;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<StoreItem>> GetEnabledProductsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Fetching enabled store items");
        if (hybridCache != null)
        {
            return await hybridCache.GetOrCreateAsync(
                "store:catalog:enabled",
                async token =>
                {
                    await _dbLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        List<StoreItem> items = await _db.StoreItems
                            .AsNoTracking()
                            .Where(s => s.Enabled)
                            .ToListAsync(token);
                        _logger.LogDebug("[StoreRepository] Retrieved {Count} enabled items from database (cached)", items.Count);
                        return (IReadOnlyList<StoreItem>)items;
                    }
                    finally
                    {
                        _dbLock.Release();
                    }
                },
                ["store:catalog"],
                TimeSpan.FromMinutes(1),
                GetL2ExpirationWithJitter(10),
                ct) ?? [];
        }

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<StoreItem> dbItems = await _db.StoreItems
                .AsNoTracking()
                .Where(s => s.Enabled)
                .ToListAsync(ct);

            _logger.LogDebug("[StoreRepository] Retrieved {Count} enabled items", dbItems.Count);
            return dbItems;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<StoreItem>> GetPurchasableProductsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Fetching purchasable store items");
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<StoreItem> items = await _db.StoreItems
                .AsNoTracking()
                .Where(s => s.Enabled && s.Purchasable)
                .ToListAsync(ct);

            _logger.LogDebug("[StoreRepository] Retrieved {Count} purchasable items", items.Count);
            return items;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<StoreItem>> GetProductsByTypeAsync(int upgradeType, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Fetching store items with UpgradeType {UpgradeType}", upgradeType);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<StoreItem> items = await _db.StoreItems
                .AsNoTracking()
                .Where(s => s.UpgradeType == upgradeType && s.Enabled)
                .ToListAsync(ct);

            return items;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetOwnedItemCodesAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            _logger.LogWarning("[StoreRepository] GetOwnedItemCodesAsync called with null or empty account name");
            return [];
        }

        string trimmed = accountName.Trim();
        _logger.LogDebug("[StoreRepository] Fetching owned item codes for Account '{AccountName}'", trimmed);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Account? account;
            if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            {
                string lower = trimmed.ToLowerInvariant();
                account = await _db.Accounts
                    .AsNoTracking()
                    .Include(a => a.User)
                    .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
            }
            else
            {
                account = await _db.Accounts
                    .AsNoTracking()
                    .Include(a => a.User)
                    .FirstOrDefaultAsync(a => a.Name == trimmed, ct);
            }

            if (account?.User is null)
            {
                _logger.LogWarning("[StoreRepository] Account '{AccountName}' not found or has no linked User", trimmed);
                return [];
            }

            List<string> dbCodes = await _db.UserInventory
                .AsNoTracking()
                .Where(i => i.UserID == account.User.ID || i.AccountID == account.ID)
                .Select(i => i.ItemCode)
                .Distinct()
                .ToListAsync(ct);

            _logger.LogDebug("[StoreRepository] Resolved {Count} distinct owned item codes for Account '{AccountName}'", dbCodes.Count, trimmed);
            return dbCodes;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetOwnedItemCodesByAccountIdAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Fetching owned item codes for Account #{AccountId}", accountId);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Account? account = await _db.Accounts
                .AsNoTracking()
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.ID == accountId, ct);

            if (account?.User is null)
            {
                _logger.LogWarning("[StoreRepository] Account #{AccountId} not found or has no linked User", accountId);
                return [];
            }

            List<string> dbCodes = await _db.UserInventory
                .AsNoTracking()
                .Where(i => i.UserID == account.User.ID || i.AccountID == accountId)
                .Select(i => i.ItemCode)
                .Distinct()
                .ToListAsync(ct);

            _logger.LogDebug("[StoreRepository] Resolved {Count} distinct owned item codes for Account #{AccountId}", dbCodes.Count, accountId);
            return dbCodes;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Account?> GetAccountWithUserForStoreAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            _logger.LogWarning("[StoreRepository] GetAccountWithUserForStoreAsync called with null or empty account name");
            return null;
        }

        string trimmed = accountName.Trim();
        _logger.LogDebug("[StoreRepository] Loading Account with User for Account '{AccountName}'", trimmed);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            {
                string lower = trimmed.ToLowerInvariant();
                return await _db.Accounts
                    .AsNoTracking()
                    .Include(a => a.User).ThenInclude(u => u.InventoryItems)
                    .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
            }

            return await _db.Accounts
                .AsNoTracking()
                .Include(a => a.User).ThenInclude(u => u.InventoryItems)
                .FirstOrDefaultAsync(a => a.Name == trimmed, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Account?> GetAccountWithUserForStoreByIdAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreRepository] Loading Account with User for Account #{AccountId}", accountId);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Accounts
                .AsNoTracking()
                .Include(a => a.User).ThenInclude(u => u.InventoryItems)
                .FirstOrDefaultAsync(a => a.ID == accountId, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> HasItemAsync(int accountId, string itemCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return false;

        string trimmed = itemCode.Trim();
        string lower = trimmed.ToLower();
        _logger.LogDebug("[StoreRepository] Checking item ownership: Account #{AccountId}, ItemCode '{ItemCode}'", accountId, trimmed);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Account? account = await _db.Accounts
                .AsNoTracking()
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.ID == accountId, ct);

            if (account?.User is null) return false;

            return await _db.UserInventory
                .AsNoTracking()
                .AnyAsync(i => (i.UserID == account.User.ID || i.AccountID == accountId) &&
                               (i.ItemCode.ToLower() == lower || i.ItemCode.ToLower().EndsWith("." + lower)), ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<ItemRecipeNodeDTO?> ResolveRecipeTreeAsync(string itemCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return null;

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            return await ResolveInternalAsync(itemCode.Trim(), visited, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task<ItemRecipeNodeDTO?> ResolveInternalAsync(string itemCode, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(itemCode))
        {
            return null;
        }

        HeroItem? item = await _db.HeroItems.AsNoTracking()
            .Include(i => i.Components)
            .FirstOrDefaultAsync(i => i.Code.ToLower() == itemCode.ToLower(), ct);

        if (item is null) return null;

        ItemRecipeNodeDTO node = new()
        {
            ItemCode = item.Code,
            Name = item.Name,
            Cost = item.Cost,
            RecipeCost = item.RecipeCost,
            Components = []
        };

        if (item.Components is { Count: > 0 })
        {
            foreach (HeroItemComponent comp in item.Components)
            {
                ItemRecipeNodeDTO? subRecipe = await ResolveInternalAsync(comp.ComponentItemCode, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase), ct);
                node.Components.Add(new ItemRecipeComponentDTO
                {
                    ComponentItemCode = comp.ComponentItemCode,
                    Quantity = comp.Quantity,
                    SubRecipe = subRecipe
                });
            }
        }

        return node;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dbLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
