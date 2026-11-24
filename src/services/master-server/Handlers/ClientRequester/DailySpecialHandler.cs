// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Services.Store;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class DailySpecialHandler(
    IStoreRepository storeRepository,
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<DailySpecialHandler> logger) : IClientRequestHandler
{
    private IStoreRepository StoreRepository { get; } = storeRepository;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(Request);
        if (string.IsNullOrEmpty(cookie))
        {
            LogMissingCookie();
            return new UnauthorizedResult();
        }

        cookie = cookie.Replace("-", string.Empty);

        string? sessionAccountName = context.Items["SessionAccountName"] as string;

        if (sessionAccountName is null)
        {
            (bool accountSessionCookieIsValid, string? cacheAccountName) =
                await DistributedCache.ValidateAccountSessionCookie(cookie);
            if (accountSessionCookieIsValid) sessionAccountName = cacheAccountName;
        }

        if (sessionAccountName is null)
            return new UnauthorizedObjectResult($@"No Session Found For Cookie ""{cookie}""");

        LogFetchingSpecials(sessionAccountName);

        Account? account = await StoreRepository.GetAccountWithUserForStoreAsync(sessionAccountName, cancellationToken);
        if (account == null)
        {
            account = await AccountRepository.GetByNicknameAsync(sessionAccountName, cancellationToken);
        }

        if (account == null)
        {
            LogAccountNotFound(sessionAccountName);
            return new UnauthorizedResult();
        }

        IReadOnlyList<string> ownedItems = await StoreRepository.GetOwnedItemCodesAsync(sessionAccountName, cancellationToken);
        HashSet<string> ownedSet = new(ownedItems, StringComparer.OrdinalIgnoreCase);

        StoreItemsConfiguration storeItems = JSONConfiguration.StoreItemsConfiguration;
        DailySpecialsConfiguration dailySpecials = JSONConfiguration.DailySpecialsConfiguration;

        List<Dictionary<string, object>> dailySpecialItems = [];

        for (int index = 0; index < dailySpecials.Specials.Count; index++)
        {
            DailySpecialEntry special = dailySpecials.Specials[index];
            StoreItem? storeItem = storeItems.GetByID(special.StoreItemID);

            if (storeItem is null)
            {
                LogStoreItemNotFound(special.StoreItemID);
                continue;
            }

            string heroName = string.Empty;

            if (storeItem.StoreItemType is StoreItemType.AlternativeAvatar)
            {
                int underscoreIndex = storeItem.Code.IndexOf('_');
                int dotIndex = storeItem.Code.IndexOf('.');

                if (underscoreIndex >= 0 && dotIndex > underscoreIndex)
                    heroName = storeItem.Code[(underscoreIndex + 1)..dotIndex];
            }

            bool isSandbox = UpgradesResolutionService.UnlockAllItems;
            int goldCost = isSandbox ? 0 : storeItem.GoldCost;
            int silverCost = isSandbox ? 0 : storeItem.SilverCost;
            int discountGold = isSandbox ? 0 : special.DiscountPercentageGold;
            int discountSilver = isSandbox ? 0 : special.DiscountPercentageSilver;
            int discountedGoldCost = isSandbox ? 0 : storeItem.GoldCost * (100 - special.DiscountPercentageGold) / 100;
            int discountedSilverCost = isSandbox ? 0 : storeItem.SilverCost * (100 - special.DiscountPercentageSilver) / 100;

            bool isOwned = isSandbox || ownedSet.Contains(storeItem.PrefixedCode);

            dailySpecialItems.Add(new Dictionary<string, object>
            {
                ["panel_index"] = index + 1,
                ["product_id"] = storeItem.ID.ToString(),
                ["hero_name"] = heroName,
                ["item_name"] = storeItem.Code,
                ["item_cname"] = storeItem.Name,
                ["item_type"] = MapStoreItemTypeToCategoryName(storeItem.StoreItemType),
                ["purchasable"] = storeItem.Purchasable,
                ["item_code"] = storeItem.PrefixedCode,
                ["local_content"] = storeItem.Resource,
                ["plinko_only"] = 0,
                ["gold_coins"] = goldCost,
                ["silver_coins"] = silverCost,
                ["discount_off"] = discountGold,
                ["discount_silver"] = discountSilver,
                ["current_gold_coins"] = discountedGoldCost,
                ["current_silver_coins"] = discountedSilverCost,
                ["tag_title"] = special.TagTitle,
                ["tag_date_limit"] = special.TagDateLimit,
                ["item_page"] = 0,
                ["is_owned"] = isOwned
            });
        }

        Dictionary<string, object> response = new() { ["list"] = dailySpecialItems, ["vested_threshold"] = 5 };

        string jsonResponse = JsonSerializer.Serialize(response);
        return new ContentResult
        {
            Content = jsonResponse,
            ContentType = "application/json; charset=utf-8",
            StatusCode = 200
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[DailySpecial] Missing cookie.")]
    private partial void LogMissingCookie();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[DailySpecial] Fetching specials for '{SessionAccountName}'...")]
    private partial void LogFetchingSpecials(string sessionAccountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[DailySpecial] Account '{SessionAccountName}' not found.")]
    private partial void LogAccountNotFound(string sessionAccountName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[DailySpecial] Store item ID {StoreItemID} not found in configuration.")]
    private partial void LogStoreItemNotFound(int storeItemID);

    private static string MapStoreItemTypeToCategoryName(StoreItemType type)
    {
        return type switch
        {
            StoreItemType.AlternativeAvatar => "Alt Avatar",
            StoreItemType.AnnouncerVoice => "Alt Announcement",
            StoreItemType.Courier => "Couriers",
            StoreItemType.Hero => "Hero",
            StoreItemType.Ward => "Ward",
            StoreItemType.Taunt => "Taunt",
            StoreItemType.Miscellaneous => "Misc",
            StoreItemType.EarlyAccessProduct => "EAP",
            StoreItemType.ChatNameColour => "Name Color",
            StoreItemType.ChatSymbol => "Symbol",
            StoreItemType.AccountIcon => "Account Icon",
            StoreItemType.Enhancement => "Enhancement",
            _ => string.Empty
        };
    }
}
