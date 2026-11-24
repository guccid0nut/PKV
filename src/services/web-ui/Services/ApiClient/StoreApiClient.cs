// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Models;
using StaffStoreItemDto = NEXUS.Contracts.Clients.StaffStoreItemDto;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public class StoreApiClient(
    HttpClient client,
    IStaffStoreService? localStoreService,
    ILogger<StoreApiClient> logger) : IStoreApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<StaffStoreViewModelDto> GetCatalogAsync(
        string? category,
        string? search,
        int page = 1,
        int pageSize = 12,
        IReadOnlyCollection<string>? userOwnedCodes = null,
        string? ownershipFilter = "all",
        IReadOnlyCollection<string>? userEquippedCodes = null,
        CancellationToken ct = default)
    {
        if (localStoreService != null)
        {
            StaffStoreViewModel vm = localStoreService.GetCatalog(category, search, page, pageSize, userOwnedCodes,
                ownershipFilter, userEquippedCodes);
            List<StaffStoreItemDto> converted =
            [
                .. vm.Items.Select(i => new StaffStoreItemDto
                {
                    Id = i.Id,
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
                    IsOwnedByUser = i.IsOwnedByUser,
                    IsEquippedByUser = i.IsEquippedByUser
                })
            ];

            return new StaffStoreViewModelDto(
                converted,
                vm.TotalCount,
                vm.Page,
                vm.TotalPages,
                vm.PageSize,
                vm.SelectedCategory,
                vm.SearchQuery,
                vm.OwnershipFilter
            );
        }

        try
        {
            string catParam = Uri.EscapeDataString(category ?? string.Empty);
            string searchParam = Uri.EscapeDataString(search ?? string.Empty);
            string filterParam = Uri.EscapeDataString(ownershipFilter ?? "all");
            string url =
                $"api/store/catalog?category={catParam}&search={searchParam}&page={page}&pageSize={pageSize}&ownershipFilter={filterParam}";
            StaffStoreViewModelDto? result =
                await client.GetFromJsonAsync<StaffStoreViewModelDto>(url, JsonOptions, ct);
            return result ?? new StaffStoreViewModelDto([], 0, 1, 1, pageSize, category, search, ownershipFilter);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error fetching store catalog");
            return new StaffStoreViewModelDto([], 0, 1, 1, pageSize, category, search, ownershipFilter);
        }
    }

    public async Task<StaffStoreItemDto?> GetItemByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<StaffStoreItemDto>($"api/store/items/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StoreApiClient] Error getting item #{ItemId}", id);
            return null;
        }
    }

    public async Task<IReadOnlyList<StaffStoreItemDto>> GetItemsByHeroAsync(string heroName,
        CancellationToken ct = default)
    {
        try
        {
            List<StaffStoreItemDto>? items =
                await client.GetFromJsonAsync<List<StaffStoreItemDto>>(
                    $"api/store/heroes/{Uri.EscapeDataString(heroName)}/items", JsonOptions, ct);
            return items ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StoreApiClient] Error getting items for hero '{HeroName}'", heroName);
            return [];
        }
    }

    public async Task<PurchaseItemResultDto> PurchaseItemAsync(int userId, int itemId, string currency,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/store/purchase",
                new { userId, itemId, currency }, JsonOptions, ct);
            if (resp.IsSuccessStatusCode)
            {
                PurchaseItemResultDto? res =
                    await resp.Content.ReadFromJsonAsync<PurchaseItemResultDto>(JsonOptions, ct);
                return res ?? new PurchaseItemResultDto(false, "Unknown response", 0, 0, itemId, null, null);
            }

            string err = await resp.Content.ReadAsStringAsync(ct);
            return new PurchaseItemResultDto(false, err, 0, 0, itemId, null, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error purchasing item #{ItemId}", itemId);
            return new PurchaseItemResultDto(false, ex.Message, 0, 0, itemId, null, null);
        }
    }

    public async Task<EquipItemResultDto> EquipItemAsync(string accountName, int itemId, string? code,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/store/equip",
                new { accountName, itemId, code }, JsonOptions, ct);
            if (resp.IsSuccessStatusCode)
            {
                EquipItemResultDto? res = await resp.Content.ReadFromJsonAsync<EquipItemResultDto>(JsonOptions, ct);
                return res ?? new EquipItemResultDto(false, "Unknown response", null, []);
            }

            string err = await resp.Content.ReadAsStringAsync(ct);
            return new EquipItemResultDto(false, err, null, []);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error equipping item #{ItemId}", itemId);
            return new EquipItemResultDto(false, ex.Message, null, []);
        }
    }

    public async Task<bool> UpdateStoreItemAsync(UpdateStoreItemRequestDto request, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PutAsJsonAsync($"api/store/items/{request.Id}", request, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error updating store item #{ItemId}", request.Id);
            return false;
        }
    }

    public async Task<IReadOnlyList<StaffStoreItemDto>> SearchStoreAsync(string query, CancellationToken ct = default)
    {
        try
        {
            List<StaffStoreItemDto>? items =
                await client.GetFromJsonAsync<List<StaffStoreItemDto>>(
                    $"api/store/search?q={Uri.EscapeDataString(query)}", JsonOptions, ct);
            return items ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error searching store for '{Query}'", query);
            return [];
        }
    }

    public async Task<PlinkoStatusResponseDto> GetPlinkoStatusAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            PlinkoStatusResponseDto? res =
                await client.GetFromJsonAsync<PlinkoStatusResponseDto>($"api/store/plinko/status/{userId}", JsonOptions,
                    ct);
            return res ?? new PlinkoStatusResponseDto(false, false, 0, 0, 0, 0, 0, 0, [], [], null, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error getting plinko status for user #{UserId}", userId);
            return new PlinkoStatusResponseDto(false, false, 0, 0, 0, 0, 0, 0, [], [], null, null);
        }
    }

    public async Task<PlinkoDropResultDto> PlinkoDropAsync(int userId, string currency, bool useDaily,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/store/plinko/drop",
                new { userId, currency, useDaily }, JsonOptions, ct);
            if (resp.IsSuccessStatusCode)
            {
                PlinkoDropResultDto? res = await resp.Content.ReadFromJsonAsync<PlinkoDropResultDto>(JsonOptions, ct);
                return res ??
                       new PlinkoDropResultDto(false, "Unknown response", 0, null, null, null, 0, 0, 0, 0, false);
            }

            string err = await resp.Content.ReadAsStringAsync(ct);
            return new PlinkoDropResultDto(false, err, 0, null, null, null, 0, 0, 0, 0, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error dropping plinko for user #{UserId}", userId);
            return new PlinkoDropResultDto(false, ex.Message, 0, null, null, null, 0, 0, 0, 0, false);
        }
    }

    public async Task<RedeemCodeResultDto> RedeemCodeAsync(int userId, string code, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsJsonAsync("api/store/redeem", new { userId, code }, JsonOptions, ct);
            if (resp.IsSuccessStatusCode)
            {
                RedeemCodeResultDto? res = await resp.Content.ReadFromJsonAsync<RedeemCodeResultDto>(JsonOptions, ct);
                return res ?? new RedeemCodeResultDto(false, "Unknown response", 0, 0, []);
            }

            string err = await resp.Content.ReadAsStringAsync(ct);
            return new RedeemCodeResultDto(false, err, 0, 0, []);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error redeeming code for user #{UserId}", userId);
            return new RedeemCodeResultDto(false, ex.Message, 0, 0, []);
        }
    }

    public async Task<UserBalancesDto?> GetUserBalancesAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<UserBalancesDto>($"api/store/balances/{userId}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error getting user balances for #{UserId}", userId);
            return null;
        }
    }

    public async Task<IReadOnlyList<StoreItemDto>> GetStoreCatalogEntitiesAsync(CancellationToken ct = default)
    {
        try
        {
            List<StoreItemDto>? items =
                await client.GetFromJsonAsync<List<StoreItemDto>>("api/store/catalog", JsonOptions, ct);
            return items ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StoreApiClient] Error getting store catalog entities");
            return [];
        }
    }
}
