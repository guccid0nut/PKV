// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Store;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// REST API controller managing the in-game cosmetic store, item purchasing, equipment loadouts, Goblin coin balances, Plinko drops, and promotional code redemptions.
/// </summary>
[ApiController]
[Route("api/store")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
[RequireInternalServiceKey]
public class StoreApiController(
    IInventoryRepository inventoryRepo,
    IAccountRepository accountRepo,
    IConfiguration? configuration = null,
    ILogger<StoreApiController>? logger = null,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly ILogger<StoreApiController> _logger = logger ?? NullLogger<StoreApiController>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private bool IsUnlockAllItemsEnabled =>
        NEXUS.Contracts.Store.StoreEntitlementPolicy.IsFreeUnlockAll(configuration, defaultFreeAll: true);

    private StoreItemDto ProjectCatalogItem(StoreItem item)
    {
        StoreItemDto dto = item.ToDto();
        if (!IsUnlockAllItemsEnabled ||
            NEXUS.Contracts.Store.StoreEntitlementPolicy.IsRestrictedEntitlement(dto.Code, null, dto.Name))
        {
            return dto;
        }

        dto.GoldCost = 0;
        dto.SilverCost = 0;
        dto.Purchasable = true;
        dto.IsEnabled = true;
        return dto;
    }

    /// <summary>
    /// Retrieves store catalog items, supporting category filtering, keyword searching, and pagination.
    /// </summary>
    /// <param name="category">Optional cosmetic category filter (e.g., "avatars", "taunts", "announcers").</param>
    /// <param name="search">Optional search query matching item name, code, or associated hero.</param>
    /// <param name="page">Optional page number for paginated view (1-based).</param>
    /// <param name="pageSize">Optional page size for paginated view.</param>
    /// <param name="ownershipFilter">Ownership filter ("all", "owned", "unowned").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Store catalog items or paged view model.</returns>
    /// <response code="200">Store catalog items or paged view model retrieved successfully.</response>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(StaffStoreViewModelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(List<StoreItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalogEntities(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? ownershipFilter = "all",
        CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreApiController] GetCatalogEntities category='{Category}' search='{Search}' page={Page}", category, search, page);
        IReadOnlyList<StoreItem> items = await inventoryRepo.GetStoreCatalogAsync(ct);

        if (page.HasValue || pageSize.HasValue || !string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(search))
        {
            bool unlockAll = IsUnlockAllItemsEnabled;
            IEnumerable<StoreItemDto> query = items.Select(ProjectCatalogItem);

            if (!string.IsNullOrWhiteSpace(category) && !string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(i => string.Equals(i.TypeName, category, StringComparison.OrdinalIgnoreCase) ||
                                         (string.Equals(category, "avatars", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(i.HeroName)));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim();
                query = query.Where(i => (i.Name != null && i.Name.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                                         (i.Code != null && i.Code.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                                         (!string.IsNullOrEmpty(i.HeroName) && i.HeroName.Contains(s, StringComparison.OrdinalIgnoreCase)));
            }

            List<StoreItemDto> filtered = query.ToList();
            int totalCount = filtered.Count;
            int currentPage = Math.Max(1, page ?? 1);
            int size = Math.Clamp(pageSize ?? 12, 1, 100);
            int totalPages = (int)Math.Ceiling(totalCount / (double)size);

            List<StaffStoreItemDto> pageItems = filtered
                .Skip((currentPage - 1) * size)
                .Take(size)
                .Select(i => new StaffStoreItemDto
                {
                    Id = i.ID,
                    Code = i.Code,
                    Name = i.Name,
                    TypeName = i.TypeName,
                    Type = i.Type,
                    GoldCost = i.GoldCost,
                    SilverCost = i.SilverCost,
                    IsEnabled = i.IsEnabled,
                    Purchasable = i.Purchasable,
                    Resource = i.Resource,
                    HeroName = i.HeroName,
                    AltAvatarSuffix = i.AltAvatarSuffix,
                    IsOwnedByUser = unlockAll,
                    IsEquippedByUser = false
                })
                .ToList();

            return Ok(new StaffStoreViewModelDto(
                pageItems,
                totalCount,
                currentPage,
                Math.Max(1, totalPages),
                size,
                category,
                search,
                ownershipFilter
            ));
        }

        return Ok(items.Select(ProjectCatalogItem).ToList());
    }

    /// <summary>
    /// Non-action helper overload to retrieve all catalog items without query filters.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Action result containing the full catalog list.</returns>
    [NonAction]
    public Task<IActionResult> GetCatalogEntities(CancellationToken ct) =>
        GetCatalogEntities(null, null, null, null, "all", ct);

    /// <summary>
    /// Retrieves a single store item by its unique catalog item ID.
    /// </summary>
    /// <param name="id">The unique integer identifier of the store item.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The store item DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Store item retrieved successfully.</response>
    /// <response code="404">Store item with specified ID was not found.</response>
    [HttpGet("items/{id:int}")]
    [ProducesResponseType(typeof(StoreItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItemById(int id, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreApiController] GetItemById #{ItemId}", id);
        IReadOnlyList<StoreItem> items = await inventoryRepo.GetStoreCatalogAsync(ct);
        StoreItem? item = items.FirstOrDefault(i => i.Id == id);
        if (item == null) return NotFound("Item not found");
        return Ok(ProjectCatalogItem(item));
    }

    /// <summary>
    /// Retrieves all store cosmetic items and alt avatars associated with a specific hero.
    /// </summary>
    /// <param name="heroName">Hero identifier or clean hero display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of store items linked to the specified hero.</returns>
    /// <response code="200">List of hero store items retrieved successfully.</response>
    [HttpGet("heroes/{heroName}/items")]
    [ProducesResponseType(typeof(List<StoreItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetItemsByHero(string heroName, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreApiController] GetItemsByHero '{HeroName}'", heroName);
        IReadOnlyList<StoreItem> items = await inventoryRepo.GetStoreCatalogAsync(ct);
        string cleanHero = heroName.Trim();
        string cleanHeroWithPrefix = cleanHero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? cleanHero
            : $"Hero_{cleanHero}";
        string cleanHeroWithoutPrefix = cleanHero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? cleanHero[5..]
            : cleanHero;

        List<StoreItemDto> result = items
            .Select(ProjectCatalogItem)
            .Where(i => !string.IsNullOrEmpty(i.HeroName) &&
                        (string.Equals(i.HeroName, cleanHeroWithoutPrefix, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(i.HeroName, cleanHeroWithPrefix, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(i.HeroName, cleanHero, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Ok(result);
    }

    /// <summary>
    /// Searches the store catalog for items matching a text query across item name, product code, or hero name.
    /// </summary>
    /// <param name="q">Search query parameter 'q'.</param>
    /// <param name="query">Alternative search query parameter 'query'.</param>
    /// <param name="search">Alternative search query parameter 'search'.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching store item DTOs.</returns>
    /// <response code="200">Matching store items retrieved successfully.</response>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<StoreItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchStore(
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery(Name = "query")] string? query = null,
        [FromQuery(Name = "search")] string? search = null,
        CancellationToken ct = default)
    {
        string? effectiveQuery = !string.IsNullOrWhiteSpace(q) ? q : !string.IsNullOrWhiteSpace(query) ? query : search;
        _logger.LogDebug("[StoreApiController] SearchStore '{Query}'", effectiveQuery);
        IReadOnlyList<StoreItem> items = await inventoryRepo.GetStoreCatalogAsync(ct);
        if (string.IsNullOrWhiteSpace(effectiveQuery)) return Ok(items.Select(ProjectCatalogItem).ToList());
        string term = effectiveQuery.Trim();
        List<StoreItemDto> result = items
            .Select(ProjectCatalogItem)
            .Where(i => (i.Name != null && i.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                        (i.Code != null && i.Code.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(i.HeroName) && i.HeroName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Ok(result);
    }

    /// <summary>
    /// Updates pricing and availability flags for a store catalog item.
    /// </summary>
    /// <param name="id">The unique integer identifier of the store item.</param>
    /// <param name="request">Payload containing updated pricing and availability settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the item was updated; otherwise, 404 NotFound.</returns>
    /// <response code="200">Store item updated successfully.</response>
    /// <response code="404">Store item was not found.</response>
    [HttpPut("items/{id:int}")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStoreItem(int id, [FromBody] UpdateStoreItemRequestDto request, CancellationToken ct = default)
    {
        _logger.LogInformation("[StoreApiController] UpdateStoreItem #{ItemId}", id);
        IReadOnlyList<StoreItem> items = await inventoryRepo.GetStoreCatalogAsync(ct);
        StoreItem? item = items.FirstOrDefault(i => i.Id == id);
        if (item == null) return NotFound("Item not found");
        item.PriceInMMP = request.GoldCost;
        item.Price = request.SilverCost;
        item.Enabled = request.IsEnabled;
        item.Purchasable = request.Purchasable;
        return Ok(true);
    }

    /// <summary>
    /// Retrieves current currency balances (Gold, Silver, Plinko tickets) and unlocked/equipped items for a user.
    /// </summary>
    /// <param name="userId">Unique identifier of the user account.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>User balances and cosmetic loadout DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">User balances retrieved successfully.</response>
    /// <response code="404">User was not found.</response>
    [HttpGet("balances/{userId:int}")]
    [ProducesResponseType(typeof(UserBalancesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBalances(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreApiController] GetBalances for User #{UserId}", userId);
        User? user = await accountRepo.GetUserByIdAsync(userId, ct);
        if (user == null) return NotFound("User not found");

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        UserBalancesDto dto = new(
            user.ID,
            user.GoldCoins,
            user.SilverCoins,
            user.PlinkoTickets,
            user.InventoryItems?.Select(i => i.ItemCode).Distinct().ToList() ?? [],
            mainAccount?.SelectedStoreItemCodes ?? [.. StoreDefaults.DefaultSelectedItems]
        );
        return Ok(dto);
    }

    /// <summary>
    /// Executes a store purchase for an item using Gold or Silver coins.
    /// </summary>
    /// <param name="req">Purchase request specifying user, item ID, and chosen currency.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Purchase transaction result DTO.</returns>
    /// <response code="200">Item purchased successfully.</response>
    /// <response code="400">Insufficient funds, already owned, or purchase validation failed.</response>
    /// <response code="404">User or item not found.</response>
    [HttpPost("purchase")]
    [ProducesResponseType(typeof(PurchaseItemResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PurchaseItemResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PurchaseItemResultDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Purchase([FromBody] StorePurchaseRequestDto req, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[StoreApiController] Purchase request for User #{UserId}, Item #{ItemId}, Currency '{Currency}'",
            req.UserId, req.ItemId, req.Currency);
        string? rawIdempotencyKey = !string.IsNullOrWhiteSpace(req.IdempotencyKey)
            ? req.IdempotencyKey
            : HttpContext?.Request?.Headers["Idempotency-Key"].FirstOrDefault()
              ?? HttpContext?.Request?.Headers["X-Idempotency-Key"].FirstOrDefault();

        PurchaseItemResultDto result = string.IsNullOrWhiteSpace(rawIdempotencyKey)
            ? await inventoryRepo.PurchaseStoreItemAsync(req.UserId, req.ItemId, req.Currency, ct)
            : await inventoryRepo.PurchaseStoreItemAsync(req.UserId, req.ItemId, req.Currency, rawIdempotencyKey, ct);
        if (!result.Success)
        {
            if (result.Message == "User not found") return NotFound(result);
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Equips a cosmetic item (e.g. alt avatar, announcer, taunt) onto an account loadout.
    /// </summary>
    /// <param name="req">Equip request containing account name, item ID, and cosmetic code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Equip result DTO.</returns>
    /// <response code="200">Cosmetic item equipped successfully.</response>
    /// <response code="404">Account or item not found, or item is not owned.</response>
    [HttpPost("equip")]
    [ProducesResponseType(typeof(EquipItemResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(EquipItemResultDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Equip([FromBody] StoreEquipRequestDto req, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[StoreApiController] Equip request for Account '{AccountName}', Item #{ItemId}, Code '{Code}'",
            req.AccountName, req.ItemId, req.Code);
        EquipItemResultDto result = await inventoryRepo.EquipStoreItemAsync(req.AccountName, req.ItemId, req.Code, ct);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves Plinko board availability, ticket counts, and daily free drop cooldown for a user.
    /// </summary>
    /// <param name="userId">Unique identifier of the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Plinko status and cooldown response DTO.</returns>
    /// <response code="200">Plinko status retrieved successfully.</response>
    [HttpGet("plinko/status/{userId:int}")]
    [ProducesResponseType(typeof(PlinkoStatusResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlinkoStatus(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[StoreApiController] GetPlinkoStatus for User #{UserId}", userId);
        PlinkoStatusResponseDto result = await inventoryRepo.GetPlinkoStatusAsync(userId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Drops a Plinko chip to win random cosmetic prizes, silver coins, or bonus tickets.
    /// </summary>
    /// <param name="req">Drop request containing user ID, currency type, and daily drop flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result DTO containing prize outcome, physics trajectory, and updated balances.</returns>
    /// <response code="200">Plinko drop completed successfully.</response>
    /// <response code="400">Invalid currency or daily drop unavailable.</response>
    /// <response code="404">User not found.</response>
    [HttpPost("plinko/drop")]
    [ProducesResponseType(typeof(PlinkoDropResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlinkoDropResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PlinkoDropResultDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PlinkoDrop([FromBody] PlinkoDropApiRequestDto req, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[StoreApiController] PlinkoDrop for User #{UserId}, Currency '{Currency}', UseDaily {UseDaily}",
            req.UserId, req.Currency, req.UseDaily);
        PlinkoDropResultDto result =
            await inventoryRepo.PlayPlinkoDropAsync(req.UserId, req.Currency, req.UseDaily, ct);
        if (!result.Success)
        {
            if (result.Message == "User not found") return NotFound(result);
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Redeems a promotional redemption code for Goblin coins, alt avatars, or exclusive items.
    /// </summary>
    /// <param name="req">Redemption request containing user ID and promo code string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Redemption outcome DTO detailing rewards granted or error reason.</returns>
    /// <response code="200">Promo code redeemed successfully.</response>
    /// <response code="400">Promo code is expired, invalid, or already claimed.</response>
    /// <response code="404">User not found.</response>
    [HttpPost("redeem")]
    [ProducesResponseType(typeof(RedeemCodeResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RedeemCodeResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RedeemCodeResultDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RedeemCode([FromBody] RedeemCodeApiRequestDto req, CancellationToken ct = default)
    {
        _logger.LogInformation("[StoreApiController] RedeemCode for User #{UserId}", req.UserId);
        RedeemCodeResultDto result = await inventoryRepo.RedeemPromoCodeAsync(req.UserId, req.Code, ct);
        if (!result.Success)
        {
            if (result.Message == "User not found") return NotFound(result);
            return BadRequest(result);
        }

        return Ok(result);
    }
}

/// <summary>
/// Request DTO for purchasing an item from the cosmetic store.
/// </summary>
/// <param name="UserId">User ID making the purchase.</param>
/// <param name="ItemId">Catalog item ID being purchased.</param>
/// <param name="Currency">Payment currency ("Gold" or "Silver").</param>
public record StorePurchaseRequestDto(int UserId, int ItemId, string Currency, string? IdempotencyKey = null);

/// <summary>
/// Request DTO for equipping a cosmetic item to an account.
/// </summary>
/// <param name="AccountName">Account name to equip item on.</param>
/// <param name="ItemId">Item ID to equip.</param>
/// <param name="Code">Optional item identifier code.</param>
public record StoreEquipRequestDto(string AccountName, int ItemId, string? Code);

/// <summary>
/// Request DTO for initiating a Plinko drop.
/// </summary>
/// <param name="UserId">User ID playing Plinko.</param>
/// <param name="Currency">Currency used if not using daily drop.</param>
/// <param name="UseDaily">Flag indicating whether to consume the daily free drop.</param>
public record PlinkoDropApiRequestDto(int UserId, string? Currency, bool UseDaily);

/// <summary>
/// Request DTO for redeeming a promotional gift code.
/// </summary>
/// <param name="UserId">User ID claiming the code.</param>
/// <param name="Code">Promo code string.</param>
public record RedeemCodeApiRequestDto(int UserId, string Code);
