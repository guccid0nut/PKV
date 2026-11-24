// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

#pragma warning disable CA2254
#pragma warning disable CA1848
#pragma warning disable CA1304
#pragma warning disable CA1305
#pragma warning disable CA1311
#pragma warning disable CS9113

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Models;
using MERRICK.DatabaseContext.Constants;
using Microsoft.AspNetCore.Mvc;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[Route("store")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class StoreController(
    IStoreApiClient storeApiClient,
    IUserApiClient userApiClient,
    IStaffStoreService storeService,
    ILogger<StoreController>? logger = null) : Controller
{
    private readonly ILogger<StoreController>? _logger = logger;

    [HttpGet("/armory")]
    public IActionResult ArmoryRedirect()
    {
        return Redirect(Request.QueryString.HasValue ? $"/store{Request.QueryString.Value}" : "/store");
    }

    [HttpGet("/plinko")]
    public IActionResult PlinkoRedirect()
    {
        return Redirect("/store?category=plinko");
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? category = null,
        string? search = null,
        string? ownership = "all",
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        UserDto? user = null;
        AccountDto? mainAccount = null;
        IReadOnlyCollection<string>? ownedItems = null;
        IReadOnlyCollection<string>? equippedItems = null;

        if (User?.Identity?.IsAuthenticated == true)
        {
            user = await ResolveCurrentUserAsync(cancellationToken);
            if (user != null)
            {
                ownedItems = user.OwnedStoreItems;
                mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
                if (mainAccount != null)
                {
                    equippedItems = mainAccount.SelectedStoreItems;
                }
            }
        }

        bool isAuthenticatedUser = user != null;
        bool unlockAll = storeService.IsUnlockAllItemsEnabled;
        StaffStoreViewModel model = storeService.GetCatalog(category, search, page, pageSize: 12,
            userOwnedCodes: ownedItems, ownershipFilter: ownership, userEquippedCodes: equippedItems,
            isAuthenticated: isAuthenticatedUser);

        if (user != null)
        {
            model.IsAuthenticated = true;
            model.IsUnlockAllItemsEnabled = unlockAll;
            model.UserGold = user.GoldCoins;
            model.UserSilver = user.SilverCoins;
            model.IsStaff = user.IsStaff ||
                            (User != null && (User.HasClaim("is_staff", "true") ||
                                              User.IsInRole("Staff") ||
                                              User.IsInRole(UserRoles.Administrator) ||
                                              User.IsInRole("ADMINISTRATOR")));

            if (!unlockAll)
            {
                model.UserOwnedItemCodes = StaffStoreService.BuildNormalizedOwnedLookup(user.OwnedStoreItems);
            }

            if (mainAccount != null)
            {
                model.UserEquippedItemCodes =
                    new HashSet<string>(mainAccount.SelectedStoreItems, StringComparer.OrdinalIgnoreCase);
            }
        }

        return View(model);
    }

    [HttpGet("item/{id:int}")]
    [HttpGet("product/{id:int}")]
    public async Task<IActionResult> Item(int id, CancellationToken cancellationToken = default)
    {
        StaffStoreItemDto? item = storeService.GetItemById(id);
        if (item == null)
        {
            return NotFound();
        }

        bool unlockAll = storeService.IsUnlockAllItemsEnabled;
        StoreItemDetailViewModel model = new()
        {
            Item = item,
            IsUnlockAllItemsEnabled = unlockAll,
            RelatedItems = !string.IsNullOrEmpty(item.HeroName)
                ? [.. storeService.GetItemsByHero(item.HeroName).Where(x => x.Id != id)]
                : []
        };

        if (User?.Identity?.IsAuthenticated == true)
        {
            UserDto? user = await ResolveCurrentUserAsync(cancellationToken);
            if (user != null)
            {
                model.IsAuthenticated = true;
                model.UserGold = user.GoldCoins;
                model.UserSilver = user.SilverCoins;
                model.IsStaff = user.IsStaff ||
                                (User != null && (User.HasClaim("is_staff", "true") ||
                                                  User.IsInRole("Staff") ||
                                                  User.IsInRole(UserRoles.Administrator) ||
                                                  User.IsInRole("ADMINISTRATOR")));

                HashSet<string> normalizedOwned = StaffStoreService.BuildNormalizedOwnedLookup(user.OwnedStoreItems);
                model.IsOwnedByUser = unlockAll || StaffStoreService.IsItemOwned(item, normalizedOwned);
                item.IsOwnedByUser = model.IsOwnedByUser;

                AccountDto? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
                HashSet<string> equippedSet =
                    new(mainAccount?.SelectedStoreItems ?? [], StringComparer.OrdinalIgnoreCase);
                item.IsEquippedByUser = StaffStoreService.IsItemEquipped(item, equippedSet);

                foreach (StaffStoreItemDto rel in model.RelatedItems)
                {
                    rel.IsOwnedByUser = unlockAll || StaffStoreService.IsItemOwned(rel, normalizedOwned);
                    rel.IsEquippedByUser = StaffStoreService.IsItemEquipped(rel, equippedSet);
                }
            }
        }

        return View("Item", model);
    }

    [HttpPost("purchase")]
    public async Task<IActionResult> Purchase([FromBody] PurchaseItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { error = "You must be logged in to purchase items." });
        }

        UserDto? user = await ResolveCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return Unauthorized(new { error = "User account not found." });
        }

        PurchaseItemResultDto result =
            await storeApiClient.PurchaseItemAsync(user.ID, request.ItemId, request.Currency, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Message ?? "Purchase failed" });
        }

        return Ok(new
        {
            success = true,
            message = result.Message,
            newGold = result.NewGold,
            newSilver = result.NewSilver,
            itemId = result.ItemId,
            code = result.Code,
            name = result.Name
        });
    }

    [HttpPost("equip")]
    public Task<IActionResult>
        Equip([FromBody] EquipItemRequest request, CancellationToken cancellationToken = default) =>
        EquipItem(request, cancellationToken);

    public async Task<IActionResult> EquipItem(EquipItemRequest request, CancellationToken cancellationToken = default)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { error = "You must be logged in to equip items." });
        }

        UserDto? user = await ResolveCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return Unauthorized(new { error = "User account not found." });
        }

        AccountDto? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        if (mainAccount == null)
        {
            return BadRequest(new { error = "No associated game account found to equip items to." });
        }

        EquipItemResultDto result =
            await storeApiClient.EquipItemAsync(mainAccount.Name, request.ItemId, request.Code, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Message ?? "Failed to equip item" });
        }

        return Json(new
        {
            success = true,
            message = result.Message,
            equippedCode = result.EquippedCode,
            selectedItems = result.SelectedItems
        });
    }

    private async Task<UserDto?> ResolveCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? discordId = User.FindFirst("discord_id")?.Value
                            ?? User.FindFirst("urn:discord:id")?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        string? email = User.FindFirst(ClaimTypes.Email)?.Value;
        string? name = User.FindFirst(ClaimTypes.Name)?.Value;

        if (!string.IsNullOrEmpty(discordId))
        {
            UserDto? user = await userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            if (user != null) return user;
        }

        if (!string.IsNullOrEmpty(email))
        {
            UserDto? user = await userApiClient.GetUserByEmailAsync(email, cancellationToken);
            if (user != null) return user;
        }

        if (!string.IsNullOrEmpty(name))
        {
            UserDto? user = await userApiClient.GetUserByAccountNameAsync(name, cancellationToken);
            if (user != null) return user;
        }

        return null;
    }

    public record PurchaseItemRequest(int ItemId, string Currency);

    public record EquipItemRequest(int ItemId, string? Code);
}
