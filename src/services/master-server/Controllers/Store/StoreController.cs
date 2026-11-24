// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable LoopCanBeConvertedToQuery
// ReSharper disable PartOfLoopCanBeConvertedToQuery
// ReSharper disable LogMessageIsConditional

using System.Data;
using KONGOR.MasterServer.Helpers.Stats;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;
using NEXUS.Contracts.Constants;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Store;
using ASPIRE.Common.Services.Distributed;
using KONGOR.MasterServer.Features.Store;
using IDatabase = StackExchange.Redis.IDatabase;

namespace KONGOR.MasterServer.Controllers.Store;

[ApiController]
[Route("store_requester.php")]
[Consumes("application/x-www-form-urlencoded")]
public class StoreController(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<StoreController> logger,
    IMediator mediator) : ControllerBase
{
    private const int ItemsPerPage = 12;
    private const int TauntStoreItemID = 91;
    private const string TauntPrefixedCode = StoreDefaults.DefaultTaunt;
    private const int SuperTauntStoreItemID = 1792;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;
    private readonly IMediator _mediator = mediator;

    internal static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    [HttpPost(Name = "Store Requester")]
    public async Task<IActionResult> StoreRequester(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Request.HasFormContentType) return BadRequest(@"Missing Value For Form Parameter ""cookie""");

        string? cookie = Request.Form["cookie"];

        if (cookie is null) return BadRequest(@"Missing Value For Form Parameter ""cookie""");

        string? requestCodeString = Request.Form["request_code"];

        if (requestCodeString is null) return BadRequest(@"Missing Value For Form Parameter ""request_code""");

        if (Enum.TryParse(requestCodeString, out StoreRequestCode requestCode).Equals(false))
            return BadRequest(@"Invalid Value For Form Parameter ""request_code""");

        (bool isValid, string? accountName) = await DistributedCache.ValidateAccountSessionCookie(cookie);

        bool isReadOnlyCatalogPreview = requestCode is StoreRequestCode.LIST_STORE_ITEMS_REQUEST
            or StoreRequestCode.GET_QUANTITY_FOR_PRODUCT_REQUEST
            or StoreRequestCode.BUNDLE_CONTENT_REQUEST;

        if (isValid.Equals(false) || string.IsNullOrEmpty(accountName))
        {
            if (isReadOnlyCatalogPreview)
            {
                accountName = null;
                if (Logger.IsEnabled(LogLevel.Information))
                    Logger.LogInformation(@"Unauthenticated Read-Only Store Preview ({RequestCode}) With Cookie ""{CookiePrefix}""",
                        requestCode, SecurityMasking.MaskToken(cookie));
            }
            else
            {
                string remotePeer = Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN";
                Logger.LogWarning(@"[SECURITY-ALERT] Rejected Unauthenticated Store Request ({RequestCode}) With Invalid Cookie ""{CookiePrefix}"" From ""{IPAddress}""",
                    requestCode, SecurityMasking.MaskToken(cookie), remotePeer);
                return new PhpResult(CreateErrorResponse((int)StoreErrorCode.STORE_SESSION_ERROR));
            }
        }

        Account? account = null;
        try
        {
            if (!string.IsNullOrEmpty(accountName))
            {
                account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);
            }


        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(ex, "Store: Failed to fetch account '{AccountName}' from database. Falling back to guest account.", accountName);
        }

        if (account is null)
        {
            if (!isReadOnlyCatalogPreview)
            {
                Logger.LogWarning("[SECURITY-ALERT] Store Request ({RequestCode}) failed account lookup for '{AccountName}'; rejecting without Account #1 fallback.", requestCode, accountName ?? "NULL");
                return new PhpResult(CreateErrorResponse((int)StoreErrorCode.STORE_SESSION_ERROR));
            }

            account = new Account
            {
                ID = 0,
                Name = "GuestPreview",
                User = new User
                {
                    EmailAddress = "preview@players.kongor.local",
                    SRPPasswordSalt = string.Empty,
                    SRPPasswordHash = string.Empty,
                    PBKDF2PasswordHash = string.Empty,
                    InventoryItems = [],
                    SilverCoins = 0,
                    GoldCoins = 0
                }
            };
        }

        string? accountIDString = Request.Form["account_id"];

        if (accountIDString is not null && int.TryParse(accountIDString, out int accountID) && account.ID != accountID)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    @"Store Request Account ID Mismatch For ""{AccountName}"" (Expected {RealID}, Got {ClaimedID})",
                    accountName, account.ID, accountID);

            if (account.ID == 0 && isReadOnlyCatalogPreview)


            {
                account.ID = accountID;
            }
            else
            {
                Logger.LogWarning("[SECURITY-ALERT] Blocked Store Account ID Mismatch on {RequestCode} for '{AccountName}' (Authenticated ID={RealID}, Claimed ID={ClaimedID}).", requestCode, account.Name, account.ID, accountID);
                return new PhpResult(CreateErrorResponse((int)StoreErrorCode.STORE_ACCOUNT_INFORMATION_ERROR));
            }
        }

        switch (requestCode)
        {
            case StoreRequestCode.LIST_STORE_ITEMS_REQUEST:
                return await HandleViewStore(account, cancellationToken);
            case StoreRequestCode.LIST_VAULT_ITEMS_REQUEST:
                return await HandleViewVault(account, cancellationToken);
            case StoreRequestCode.ATTEMPT_PURCHASE_PRODUCT_REQUEST:
                return await HandlePurchaseProduct(account, cancellationToken);
            case StoreRequestCode.LIST_PRODUCT_IDS_SELECTED_REQUEST:
                return await HandleListSelectedUpgrades(account, cancellationToken);
            case StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST:
                return await HandlePurchaseProductInMatchLobby(account, cancellationToken);
            case StoreRequestCode.REDEEM_CODE_REQUEST:
                return await HandleRedeemCode(account, cancellationToken);
            default:
                return await HandleViewStore(account, cancellationToken);
        }
    }

    private async Task<IActionResult> HandleViewStore(Account account, CancellationToken cancellationToken)
    {
        string? categoryIDString = Request.Form["category_id"];
        int categoryID = 0;
        if (categoryIDString is not null && !int.TryParse(categoryIDString, out categoryID))
            categoryID = categoryIDString.ToLowerInvariant() switch
            {
                "gamevanity" or "taunt" or "taunts" => (int)StoreCategory.Taunt,
                "accountvanity" or "accounticons" or "account_icons" => (int)StoreCategory.AccountIcons,
                "namecolors" or "name_colors" => (int)StoreCategory.NameColors,
                "symbols" or "chatsymbols" => (int)StoreCategory.ChatSymbols,
                "announcers" => (int)StoreCategory.Announcers,
                "couriers" => (int)StoreCategory.Couriers,
                "wards" => (int)StoreCategory.Wards,
                "tauntbadges" => (int)StoreCategory.TauntBadges,
                "tpeffects" => (int)StoreCategory.TeleportationEffects,
                "selectioncircles" => (int)StoreCategory.SelectionCircles,
                "creeps" => (int)StoreCategory.Creeps,
                "enhancements" => (int)StoreCategory.Enhancements,
                _ => (int)StoreCategory.AccountIcons
            };

        if (categoryID <= 0) categoryID = (int)StoreCategory.AccountIcons;

        string? pageString = Request.Form["page"];
        int currentPage = 1;
        if (pageString is not null && int.TryParse(pageString, out int parsedPage)) currentPage = parsedPage;
        if (currentPage <= 0) currentPage = 1;

        string hostTime = Request.Form["hostTime"].FirstOrDefault() ?? "0";

        StoreCategoryResultDto result = await _mediator.Send(
            new GetStoreCategoryQuery(account.ID, categoryID, currentPage, hostTime),
            cancellationToken);

        return new PhpResult(result.RawResponse);
    }

    private async Task<IActionResult> HandlePurchaseProduct(Account account, CancellationToken cancellationToken)
    {
        string? productIDString = Request.Form["product_id"];
        if (productIDString is null) return BadRequest(@"Missing Value For Form Parameter ""product_id""");
        if (!int.TryParse(productIDString, out int productID)) return BadRequest(@"Invalid Value For Form Parameter ""product_id""");

        string? currency = Request.Form["currency"];
        if (currency is null) return BadRequest(@"Missing Value For Form Parameter ""currency""");

        string? categoryIDString = Request.Form["category_id"];
        if (categoryIDString is null) return BadRequest(@"Missing Value For Form Parameter ""category_id""");

        bool isMasteryStatsScreen = categoryIDString.StartsWith("MASTERY", StringComparison.Ordinal) && productID == MasteryBoost.Regular.ProductCode;

        int categoryID = 0;
        if (!int.TryParse(categoryIDString, out categoryID))
        {
            if (isMasteryStatsScreen)
            {
                categoryID = (int)StoreCategory.Enhancements;
            }
            else
            {
                return BadRequest(@"Invalid Value For Form Parameter ""category_id""");
            }
        }

        if (categoryID <= 0) categoryID = (int)StoreCategory.Enhancements;

        string? pageString = Request.Form["page"];
        int currentPage = 1;
        if (pageString is not null && int.TryParse(pageString, out int parsedPage))
        {
            currentPage = parsedPage;
        }
        else if (!isMasteryStatsScreen)
        {
            if (pageString is null) return BadRequest(@"Missing Value For Form Parameter ""page""");
            return BadRequest(@"Invalid Value For Form Parameter ""page""");
        }

        string hostTime = Request.Form["hostTime"].FirstOrDefault() ?? "0";

        if (!int.TryParse(Request.Form["discount"], out int discountCode)) discountCode = 0;

        try
        {
            PurchaseStoreProductResultDto result = await _mediator.Send(
                new PurchaseStoreProductCommand(account.ID, productID, currency, categoryID, currentPage, hostTime, discountCode, UserId: account.UserID),
                cancellationToken);

            if (isMasteryStatsScreen && result.Success)
            {
                Dictionary<string, object> masteryResponse = new(result.RawResponse)
                {
                    ["product_id"] = MasteryBoost.Regular.ProductCode,
                    ["_confirm_submitted"] = true
                };
                return new PhpResult(masteryResponse);
            }

            return new PhpResult(result.RawResponse);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("[StoreController] Lock acquisition timed out for product purchase for Account {AccountId}", account.ID);
            Dictionary<string, object> errResponse = CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR);
            return new PhpResult(errResponse);
        }
    }

    private async Task<IActionResult> HandleViewVault(Account account, CancellationToken cancellationToken)
    {
        string hostTime = Request.Form["hostTime"].FirstOrDefault() ?? "0";
        VaultItemsResultDto result = await _mediator.Send(
            new GetVaultItemsQuery(account.ID, hostTime),
            cancellationToken);

        return new PhpResult(result.RawResponse);
    }

    private async Task<IActionResult> HandleListSelectedUpgrades(Account account, CancellationToken cancellationToken)
    {
        string? hostTime = Request.Form["hostTime"];
        if (hostTime is null) return BadRequest(@"Missing Value For Form Parameter ""hostTime""");

        SelectedUpgradesResultDto result = await _mediator.Send(
            new GetSelectedUpgradesQuery(account.ID, hostTime),
            cancellationToken);

        return new PhpResult(result.RawResponse);
    }

    private async Task<IActionResult> HandlePurchaseProductInMatchLobby(Account account, CancellationToken cancellationToken)
    {
        string? heroName = Request.Form["hero_name"];
        if (heroName is null) return BadRequest(@"Missing Value For Form Parameter ""hero_name""");

        string? avatarCode = Request.Form["avatar_code"];
        if (avatarCode is null) return BadRequest(@"Missing Value For Form Parameter ""avatar_code""");

        string? currency = Request.Form["currency"];
        if (currency is null) return BadRequest(@"Missing Value For Form Parameter ""currency""");

        try
        {
            PurchaseLobbyProductResultDto result = await _mediator.Send(
                new PurchaseLobbyProductCommand(account.ID, heroName, avatarCode, currency),
                cancellationToken);

            return new PhpResult(result.RawResponse);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("[StoreController] Lock acquisition timed out for lobby purchase for Account {AccountId}", account.ID);
            Dictionary<string, object> errResponse = CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR);
            errResponse["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
            return new PhpResult(errResponse);
        }
    }

    private async Task<IActionResult> HandleRedeemCode(Account account, CancellationToken cancellationToken)
    {
        string? codeInput = Request.Form["code"];
        string? hostTime = Request.Form["hostTime"];

        if (string.IsNullOrWhiteSpace(codeInput))
            return new PhpResult(CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_CODE_INVALID_ERROR,
                hostTime));

        try
        {
            RedeemStoreCodeResultDto result = await _mediator.Send(
                new RedeemStoreCodeCommand(account.ID, codeInput, hostTime),
                cancellationToken);

            return new PhpResult(result.RawResponse);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("[StoreController] Lock acquisition timed out for code redemption for Account {AccountId}", account.ID);
            return new PhpResult(CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR, hostTime));
        }
    }



    /// <summary>
    ///     Formats the <c>redeemed</c> response field as the comma-separated three-part string the client UI expects.
    ///     Empty segments are emitted for absent rewards so the client's <c>explode(',', redeemed)</c> always produces three
    ///     table entries.
    /// </summary>
    internal static string FormatRedeemedField(int goldReward, int silverReward, StoreItem? product)
    {
        string goldSegment = goldReward > 0 ? goldReward.ToString() : string.Empty;
        string silverSegment = silverReward > 0 ? silverReward.ToString() : string.Empty;
        string productSegment = product is null ? string.Empty : $"{product.ID}~{product.Resource}";

        return $"{goldSegment},{silverSegment},{productSegment}";
    }

    /// <summary>
    ///     Builds the failure response for a redemption attempt with every field the client form declaration expects, so the
    ///     client UI can reliably render the localised error message.
    /// </summary>
    internal static Dictionary<string, object> CreateRedeemErrorResponse(int errorCode, string? hostTime)
    {
        return new Dictionary<string, object>
        {
            ["responseCode"] = (int)StoreResponseCode.NO_RESPONSE,
            ["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
            ["errorCode"] = errorCode,
            ["redeemed"] = string.Empty,
            ["requestHostTime"] = hostTime ?? string.Empty
        };
    }

    /// <summary>
    ///     Executes a purchase transaction, deducting the appropriate currency and adding the item to the user's owned items.
    /// </summary>
    internal static (Dictionary<string, object> Response, bool Success) ExecutePurchase(Account account,
        StoreItem? storeItem, string currency, int discountCode = 0)
    {
        Dictionary<string, object> response = [];
        User user = account.User;

        if (storeItem is null)
        {
            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_ITEM_MISSING_ERROR;

            return (response, false);
        }

        bool isSandbox = UpgradesResolutionService.UnlockAllItems;

        if (!NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                account.Type, account.Clan?.Tag, storeItem.Code, storeItem.PrefixedCode, storeItem.Name))
        {
            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_ITEM_ERROR;
            return (response, false);
        }

        // Mastery Boost Products Are Consumables: Purchasing One Adds A Counted Consumable Entry Rather Than An Owned Vanity Item, So They Remain Repurchasable
        if (storeItem.ID is MasteryBoost.Regular.ProductCode or MasteryBoost.Super.ProductCode
            or MasteryBoost.Bundle.ProductCode)
            return ExecuteMasteryBoostPurchase(account, storeItem, isSandbox);

        if (!isSandbox && (storeItem.Purchasable.Equals(false) || storeItem.IsEnabled.Equals(false)))
        {
            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_ITEM_ERROR;

            return (response, false);
        }

        bool isRepeatable = IsRepeatableItem(storeItem);
        bool isAlreadyOwned = !isRepeatable && (user.InventoryItems?.Any(i =>
            string.Equals(i.ItemCode, storeItem.PrefixedCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.ItemCode, storeItem.Code, StringComparison.OrdinalIgnoreCase)) ?? false);

        if (!isAlreadyOwned && !isRepeatable && (storeItem.IsBundle || storeItem.Type == 20))
        {
            BundleCatalogService catalogService = new(NullLogger<BundleCatalogService>.Instance);
            BundleContentResponse bundleContent = catalogService.GetBundleContent(storeItem.ID, account);
            if (bundleContent.Info.IsOwned) isAlreadyOwned = true;
        }

        if (isAlreadyOwned)
        {
            if (isSandbox)
            {
                response["popupCode"] = (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS;
                response["errorCode"] = 0;
                return (response, true);
            }

            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_ALREADY_OWNED;

            return (response, false);
        }

        // Must Own The Base Taunt Before Purchasing Other Taunts Or The Super Taunt
        if (!isSandbox && RequiresBaseTaunt(storeItem) && !(user.InventoryItems?.Any(i =>
            string.Equals(i.ItemCode, TauntPrefixedCode, StringComparison.OrdinalIgnoreCase)) ?? false))
        {
            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_ITEM_ERROR;

            return (response, false);
        }

        // Apply An Owned Mastery Coupon When One Is Specified And Is Valid For The Product Being Purchased
        MasteryCoupon? appliedCoupon = MasteryCouponHelper.ResolveApplicableCoupon(user, discountCode, storeItem);

        // Mastery Coupons Apply A 25% Discount; Math.Floor Matches The Values Displayed By The Client
        int goldCost = isSandbox ? 0 : (appliedCoupon is null ? storeItem.GoldCost : (int)Math.Floor(storeItem.GoldCost * 0.75));
        int silverCost = isSandbox ? 0 : (appliedCoupon is null ? storeItem.SilverCost : (int)Math.Floor(storeItem.SilverCost * 0.75));

        if (currency is "0")
        {
            if (user.GoldCoins < goldCost)
            {
                response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
                response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_POINT_ERROR;

                return (response, false);
            }

            user.GoldCoins -= goldCost;
        }

        else if (currency is "1")
        {
            if (user.SilverCoins < silverCost)
            {
                response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
                response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_POINT_ERROR;

                return (response, false);
            }

            user.SilverCoins -= silverCost;
        }

        else
        {
            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_INVALID_REQUEST_ERROR;

            return (response, false);
        }

        user.InventoryItems ??= [];
        user.InventoryItems.Add(new UserInventory
        {
            UserID = user.ID,
            AccountID = account.ID,
            ItemCode = storeItem.PrefixedCode,
            StoreItemId = null,
            Quantity = 1,
            IsPermanent = true,
            TimestampAcquired = TimeProvider.System.GetUtcNow(),
            Source = "InGameStore"
        });

        user.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = account.ID,
            GoldCoinsDelta = currency is "0" ? -goldCost : 0,
            SilverCoinsDelta = currency is "1" ? -silverCost : 0,
            PlinkoTicketsDelta = 0,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "StorePurchase",
            ReferenceId = storeItem.PrefixedCode,
            Description =
                $"Purchased '{storeItem.Name}' for {(currency is "0" ? goldCost : silverCost)} {(currency is "0" ? "Gold" : "Silver")}",
            Timestamp = TimeProvider.System.GetUtcNow()
        });

        // Consume The Mastery Coupon Once The Discounted Purchase Has Succeeded
        if (appliedCoupon is not null) MasteryConsumables.RemoveMasteryCoupon(user, appliedCoupon.Hero);

        // If The Purchased Item Is A Featured Bundle, Also Grant The Individual Products It Contains
        GrantFeaturedBundleContents(account, storeItem);

        // Grant Generic / Shop Bundle and Grab Bag Contents
        GrantGenericBundleContents(account, storeItem, response);

        // Mark Any Featured Bundles As Owned If All Their Individual Products Are Now Owned
        MarkCompletedFeaturedBundlesAsOwned(account);

        response["popupCode"] = (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS;
        response["errorCode"] = 0;

        return (response, true);
    }

    /// <summary>
    ///     Purchases a mastery boost consumable, deducting its gold cost and adding the corresponding counted consumable entry
    ///     to the user's owned items.
    /// </summary>
    private static (Dictionary<string, object> Response, bool Success) ExecuteMasteryBoostPurchase(Account account,
        StoreItem storeItem, bool isSandbox = false)
    {
        Dictionary<string, object> response = [];
        User user = account.User;

        int boostCost = isSandbox ? 0 : storeItem.GoldCost;

        if (user.GoldCoins < boostCost)
        {
            response["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE;
            response["errorCode"] = (int)StoreErrorCode.STORE_PURCHASE_POINT_ERROR;

            return (response, false);
        }

        user.GoldCoins -= boostCost;

        user.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = account.ID,
            GoldCoinsDelta = -boostCost,
            SilverCoinsDelta = 0,
            PlinkoTicketsDelta = 0,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "MasteryBoostPurchase",
            ReferenceId = storeItem.PrefixedCode,
            Description = $"Purchased mastery boost '{storeItem.Name}' for {boostCost} Gold",
            Timestamp = TimeProvider.System.GetUtcNow()
        });

        if (storeItem.ID == MasteryBoost.Super.ProductCode)
            MasteryConsumables.AddSuperMasteryBoost(user);

        else if (storeItem.ID == MasteryBoost.Bundle.ProductCode)
            MasteryConsumables.AddMasteryBoost(user, 10);

        else
            MasteryConsumables.AddMasteryBoost(user);

        response["popupCode"] = (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS;
        response["errorCode"] = 0;

        return (response, true);
    }



    /// <summary>
    ///     Grants the individual products contained within a featured bundle to the user.
    ///     If the purchased item is not a featured bundle, this method does nothing.
    /// </summary>
    private static void GrantFeaturedBundleContents(Account account, StoreItem purchasedItem)
    {
        FeaturedItemsConfiguration featuredConfiguration = JSONConfiguration.FeaturedItemsConfiguration;

        FeaturedBundle? bundle = featuredConfiguration.Bundles
            .SingleOrDefault(featuredBundle => featuredBundle.StoreItemID == purchasedItem.ID);

        if (bundle is null) return;

        User user = account.User;
        user.InventoryItems ??= [];

        IEnumerable<StoreItem> itemsToGrant = bundle.IncludedProductIndices
            .Where(index => index >= 0 && index < featuredConfiguration.FeaturedItemIDs.Count)
            .Select(index => StoreItems.GetByID(featuredConfiguration.FeaturedItemIDs[index]))
            .Where(item => item is not null && !user.InventoryItems.Any(i => string.Equals(i.ItemCode, item.PrefixedCode, StringComparison.OrdinalIgnoreCase)))!;

        foreach (StoreItem includedItem in itemsToGrant)
        {
            user.InventoryItems.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = includedItem.PrefixedCode,
                StoreItemId = null,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "FeaturedBundle"
            });
        }
    }

    /// <summary>
    ///     Grants the products contained within a generic store bundle or grab bag.
    ///     For grab bags, selects an unowned item from the pool and grants it to the user.
    /// </summary>
    private static void GrantGenericBundleContents(Account account, StoreItem purchasedItem,
        Dictionary<string, object> response)
    {
        User user = account.User;
        if (purchasedItem.Type != 20 && !purchasedItem.IsBundle) return;

        string bundleName = purchasedItem.Name;
        bool isGrabBag = bundleName.Contains("Grab Bag", StringComparison.OrdinalIgnoreCase) ||
                         bundleName.Contains("Grabbag", StringComparison.OrdinalIgnoreCase) ||
                         bundleName.Contains("Bag", StringComparison.OrdinalIgnoreCase) ||
                         bundleName.Contains("Box", StringComparison.OrdinalIgnoreCase);

        BundleCatalogService catalogService = new(NullLogger<BundleCatalogService>.Instance);
        BundleContentResponse bundleContent = catalogService.GetBundleContent(purchasedItem.ID, account);

        user.InventoryItems ??= [];

        if (isGrabBag)
        {
            List<BundleItemDTO> eligibleItems = [.. bundleContent.List.Where(x => !x.IsOwned)];
            if (eligibleItems.Count == 0) eligibleItems = bundleContent.List;

            if (eligibleItems.Count > 0)
            {
                int randomIndex = Random.Shared.Next(eligibleItems.Count);
                BundleItemDTO wonItem = eligibleItems[randomIndex];
                StoreItem? wonStoreItem = StoreItems.GetByID(wonItem.ProductID);
                string codeToGrant = wonStoreItem?.PrefixedCode ?? wonItem.ItemCode;
                if (!user.InventoryItems.Any(i => string.Equals(i.ItemCode, codeToGrant, StringComparison.OrdinalIgnoreCase)))
                {
                    user.InventoryItems.Add(new UserInventory
                    {
                        UserID = user.ID,
                        AccountID = account.ID,
                        ItemCode = codeToGrant,
                        StoreItemId = null,
                        Quantity = 1,
                        IsPermanent = true,
                        TimestampAcquired = TimeProvider.System.GetUtcNow(),
                        Source = "GrabBag"
                    });
                }

                string theme = bundleContent.Info.UiTheme;
                if (string.IsNullOrEmpty(theme)) theme = "lucky_bundle";

                string localPath = wonStoreItem?.Resource ?? wonItem.LocalContent;
                string productName = wonStoreItem?.Name ?? wonItem.ItemCode;

                response["grabBag"] = "1";
                response["grabBagTheme"] = theme;
                response["grabBagIDs"] = wonItem.ProductID.ToString();
                response["grabBagTypes"] = "1";
                response["grabBagLocalPaths"] = localPath;
                response["grabBagProductNames"] = productName;
                response["awarded_product_id"] = wonItem.ProductID;
                response["awarded_item_code"] = wonItem.ItemCode;
            }
        }
        else
        {
            for (int i = 0; i < bundleContent.List.Count; i++)
            {
                BundleItemDTO itemDto = bundleContent.List[i];
                StoreItem? item = StoreItems.GetByID(itemDto.ProductID);
                string codeToGrant = item?.PrefixedCode ?? itemDto.ItemCode;
                if (!user.InventoryItems.Any(i => string.Equals(i.ItemCode, codeToGrant, StringComparison.OrdinalIgnoreCase)))
                {
                    user.InventoryItems.Add(new UserInventory
                    {
                        UserID = user.ID,
                        AccountID = account.ID,
                        ItemCode = codeToGrant,
                        StoreItemId = null,
                        Quantity = 1,
                        IsPermanent = true,
                        TimestampAcquired = TimeProvider.System.GetUtcNow(),
                        Source = "Bundle"
                    });
                }
            }
        }
    }

    /// <summary>
    ///     Marks any featured bundles as owned when all of their included products are already owned.
    ///     This prevents the user from purchasing a bundle whose individual products were acquired separately.
    /// </summary>
    private static void MarkCompletedFeaturedBundlesAsOwned(Account account)
    {
        FeaturedItemsConfiguration featuredConfiguration = JSONConfiguration.FeaturedItemsConfiguration;
        User user = account.User;
        user.InventoryItems ??= [];

        IEnumerable<StoreItem> completedBundles = featuredConfiguration.Bundles
            .Select(bundle => (Bundle: bundle, Item: StoreItems.GetByID(bundle.StoreItemID)))
            .Where(t => t.Item is not null && !user.InventoryItems.Any(i => string.Equals(i.ItemCode, t.Item.PrefixedCode, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t.Bundle.IncludedProductIndices.All(productIndex =>
            {
                if (productIndex < 0 || productIndex >= featuredConfiguration.FeaturedItemIDs.Count) return false;

                int itemID = featuredConfiguration.FeaturedItemIDs[productIndex];
                StoreItem? includedItem = StoreItems.GetByID(itemID);
                return includedItem is not null && user.InventoryItems.Any(i => string.Equals(i.ItemCode, includedItem.PrefixedCode, StringComparison.OrdinalIgnoreCase));
            }))
            .Select(t => t.Item)!;

        foreach (StoreItem bundleItem in completedBundles)
        {
            user.InventoryItems.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = bundleItem.PrefixedCode,
                StoreItemId = null,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "CompletedBundle"
            });
        }
    }

    /// <summary>
    ///     Populates the response dictionary with the item listing for a store category page.
    ///     For the featured heroes category (68), a specialised response format with bundle data is returned.
    /// </summary>
    internal static void PopulateItemListing(User user, Account account, Dictionary<string, object> response,
        int categoryID, int currentPage)
    {
        // The Featured Heroes Category Uses A Specialised Response Format With Bundle Data
        if (categoryID == (int)StoreCategory.FeaturedHeroAvatars)
        {
            PopulateFeaturedItemListing(user, account, response);
            return;
        }

        StoreItemType itemType = MapStoreCategoryToItemType(categoryID);
        bool isSandbox = UpgradesResolutionService.UnlockAllItems;

        // Include Taunt Availability Status For Taunt And Miscellaneous Categories
        if (ShouldSendTauntStatus(itemType))
        {
            StoreItem? baseTaunt = StoreItems.GetByID(TauntStoreItemID);

            if (baseTaunt is not null)
            {
                response["tauntUnlocked"] = (isSandbox || (user.InventoryItems?.Any(i => string.Equals(i.ItemCode, baseTaunt.PrefixedCode, StringComparison.OrdinalIgnoreCase)) ?? false)) ? 1 : 0;
                response["tauntUnlockCost"] = isSandbox ? 0 : baseTaunt.GoldCost;
                response["tauntUnlockCostMMP"] = isSandbox ? 0 : baseTaunt.SilverCost;
            }
        }

        // Retrieve All Enabled Items For This Store Item Type
        IEnumerable<StoreItem> itemsForSale = StoreItems.GetEnabledItemsByType(itemType);

        // Chat Symbols Are Split Into Flags And Hero Symbols Based On The Category ID
        if (categoryID == (int)StoreCategory.FlagSymbols)
            itemsForSale = itemsForSale.Where(storeItem => storeItem.Name.Contains("Flag"));

        else if (categoryID == (int)StoreCategory.HeroSymbols)
            itemsForSale = itemsForSale.Where(storeItem => storeItem.Name.Contains("Flag").Equals(false));

        List<StoreItem> allItems = [.. itemsForSale];

        IEnumerable<StoreItem> displayItems = allItems;

        if (IsPaginated(categoryID)) displayItems = allItems.Skip((currentPage - 1) * ItemsPerPage).Take(ItemsPerPage);

        List<string> productPrices = [];
        List<string> productIDs = [];
        List<string> productNames = [];
        List<string> productAlreadyOwned = [];
        List<string> purchasable = [];
        List<string> productPremium = [];
        List<string> premiumMMPCost = [];
        List<string> productCodes = [];
        List<string> productLocalContent = [];
        List<string> productIsBundle = [];
        List<string> productQuantity = [];
        List<string> productWebContent = [];
        List<string> productDescription = [];
        List<string> productsRequired = [];
        List<string> productEligibility = [];
        List<string> vaultCategory74 = [];
        int eligibilityIndex = 0;

        BundleCatalogService catalogService = new(NullLogger<BundleCatalogService>.Instance);

        HashSet<string> ownedCodes = new(StringComparer.OrdinalIgnoreCase);
        if (user.InventoryItems != null)
        {
            foreach (var invItem in user.InventoryItems)
            {
                if (!string.IsNullOrEmpty(invItem.ItemCode))
                {
                    ownedCodes.Add(invItem.ItemCode);
                }
            }
        }

        foreach (StoreItem item in displayItems)
        {
            bool isItemAuthorized = NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                account.Type, account.Clan?.Tag, item.Code, item.PrefixedCode, item.Name);
            bool effectiveFreeUnlock = isSandbox && isItemAuthorized;
            int displayPrice = effectiveFreeUnlock ? 0 : item.GoldCost;

            // 9001-9010 Is A Reserved Price Range For Non-Purchasable Display Items
            if (displayPrice is >= 9001 and <= 9010 and not 9006) displayPrice = 9011;

            bool isRepeatable = IsRepeatableItem(item);
            bool itemOwned = !isRepeatable && (effectiveFreeUnlock ||
                (!string.IsNullOrEmpty(item.PrefixedCode) && ownedCodes.Contains(item.PrefixedCode)) ||
                (!string.IsNullOrEmpty(item.Code) && ownedCodes.Contains(item.Code)));

            if (!itemOwned && !isRepeatable && (item.IsBundle || item.Type == 20))
            {
                BundleContentResponse bundleContent = catalogService.GetBundleContent(item.ID, account);
                if (bundleContent.Info.IsOwned) itemOwned = true;
            }

            bool isPurchasable = isItemAuthorized && (effectiveFreeUnlock ? true : (item.Purchasable && (isRepeatable || !itemOwned)));

            productPrices.Add(displayPrice.ToString());
            productIDs.Add(item.ID.ToString());
            productNames.Add(item.Name ?? string.Empty);
            productCodes.Add(!string.IsNullOrEmpty(item.Code) ? item.Code : (!string.IsNullOrEmpty(item.PrefixedCode) ? item.PrefixedCode : item.ID.ToString()));
            productAlreadyOwned.Add(itemOwned ? "1" : "0");
            purchasable.Add(isPurchasable ? "1" : "0");
            productPremium.Add(item.IsPremium ? "1" : "0");
            premiumMMPCost.Add(effectiveFreeUnlock ? "0" : item.SilverCost.ToString());
            string localContentPath = item.Resource ?? "";
            if (!string.IsNullOrEmpty(localContentPath) && !localContentPath.StartsWith('/'))
            {
                localContentPath = "/" + localContentPath;
            }
            productLocalContent.Add(localContentPath);
            productIsBundle.Add((item.IsBundle || item.Type == 20) ? "1" : "0");
            productQuantity.Add("-1");
            productWebContent.Add("-1");
            productDescription.Add(string.Empty);

            string itemName = item.Name ?? string.Empty;
            bool isGrabBag = itemName.Contains("Grab Bag", StringComparison.OrdinalIgnoreCase) ||
                             itemName.Contains("Grabbag", StringComparison.OrdinalIgnoreCase) ||
                             itemName.Contains("Bag", StringComparison.OrdinalIgnoreCase) ||
                             itemName.Contains("Box", StringComparison.OrdinalIgnoreCase);

            string grabBagEntry = isGrabBag ? "1~0" : "0~0";
            vaultCategory74.Add(grabBagEntry);

            string requiredString = item.Required.Length > 0
                ? string.Join(";", item.Required)
                : "";

            productsRequired.Add(requiredString);

            if (item.Required.Length > 0)
            {
                bool isEligible = isSandbox || item.Required.All(requiredID =>
                    StoreItems.GetByID(requiredID) is { } requiredItem
                    && !string.IsNullOrEmpty(requiredItem.PrefixedCode)
                    && ownedCodes.Contains(requiredItem.PrefixedCode));

                productEligibility.Add(string.Join("~", item.ID, eligibilityIndex, isEligible ? 1 : 0, isSandbox ? 0 : item.GoldCost, 0,
                    requiredString));
            }

            eligibilityIndex++;
        }

        response["productPrices"] = string.Join("|", productPrices);
        response["productIDs"] = string.Join("|", productIDs);
        response["productNames"] = string.Join("|", productNames);
        response["productAlreadyOwned"] = string.Join("|", productAlreadyOwned);
        response["purchasable"] = string.Join("|", purchasable);
        response["productPremium"] = string.Join("|", productPremium);
        response["premium_mmp_cost"] = string.Join("|", premiumMMPCost);
        response["productCodes"] = string.Join("|", productCodes);
        response["productLocalContent"] = string.Join("|", productLocalContent);
        response["productIsBundle"] = string.Join("|", productIsBundle);
        response["productQuantity"] = string.Join("|", productQuantity);
        response["productWebContent"] = string.Join("|", productWebContent);
        response["productDescription"] = string.Join("|", productDescription);
        response["productsRequired"] = string.Join("|", productsRequired);
        response["productEligibility"] = string.Join("|", productEligibility);
        response["vaultCategory74"] = string.Join("|", vaultCategory74);

        int count = productIDs.Count;
        string chargesEntry = string.Join("~", Enumerable.Repeat("-1", 6));
        response["productCharges"] = string.Join("|", Enumerable.Repeat(chargesEntry, count));
        string durationsEntry = string.Join("~", Enumerable.Repeat("-1", 7));
        response["productDurations"] = string.Join("|", Enumerable.Repeat(durationsEntry, count));
        response["chargesRemaining"] = string.Join("|", Enumerable.Repeat("-1", count));
        response["productTimes"] = string.Join("|", Enumerable.Repeat("0,0", count));

        // Always Compute Total Pages Based On All Items, Even For Non-Paginated Categories (The Client Uses This For Client-Side Pagination)
        int totalPages = allItems.Count > 0
            ? (allItems.Count + ItemsPerPage - 1) / ItemsPerPage
            : 1;

        response["totalPages"] = totalPages;
        response["totalPoints"] = user.GoldCoins;
        response["totalMMPoints"] = user.SilverCoins;
        response["categoryID"] = categoryID;
        response["currentPage"] = currentPage;
        response["customAccountIcon"] = 0;
        response["customAccountIconCost"] = isSandbox ? 0 : 350;
        response["customAccountIconCostMMP"] = isSandbox ? 0 : 1000;
        response["accountIconsUnlocked"] = 1;
        response["timestamp"] = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        response["vaultHighlight"] = "NULL";

        StoreVaultHelpers.PopulateSelectedUpgrades(account, response);
    }

    /// <summary>
    ///     Populates the response dictionary with the featured heroes listing.
    ///     This format includes individual product data and associated bundle information, matching the response format
    ///     expected by the client's featured tab (category 68).
    /// </summary>
    private static void PopulateFeaturedItemListing(User user, Account account, Dictionary<string, object> response)
    {
        bool isSandbox = UpgradesResolutionService.UnlockAllItems;
        FeaturedItemsConfiguration featuredConfiguration = JSONConfiguration.FeaturedItemsConfiguration;

        List<string> productNames = [];
        List<string> productIDs = [];
        List<string> productLocalContent = [];
        List<string> productAlreadyOwned = [];
        List<string> productEligibility = [];

        foreach (StoreItem item in featuredConfiguration.FeaturedItemIDs.Select(StoreItems.GetByID)
                     .OfType<StoreItem>())
        {
            productNames.Add(item.Code);
            productIDs.Add(item.ID.ToString());
            productLocalContent.Add(item.Resource);
            productAlreadyOwned.Add((isSandbox || (user.InventoryItems?.Any(i => string.Equals(i.ItemCode, item.PrefixedCode, StringComparison.OrdinalIgnoreCase)) ?? false)) ? "1" : "0");

            if (item.Required.Length > 0)
            {
                bool isEligible = isSandbox || item.Required.All(requiredID =>
                    StoreItems.GetByID(requiredID) is { } requiredItem
                    && (user.InventoryItems?.Any(i => string.Equals(i.ItemCode, requiredItem.PrefixedCode, StringComparison.OrdinalIgnoreCase)) ?? false));

                string requiredString = string.Join(";", item.Required);

                productEligibility.Add(string.Join("~", item.ID, item.Required.Length, isEligible ? 1 : 0,
                    isSandbox ? 0 : item.GoldCost, 0, requiredString));
            }
        }

        List<string> bundleIDs = [];
        List<string> bundleAlreadyOwned = [];
        List<string> bundleIncludedProducts = [];
        List<string> bundleNames = [];
        List<string> bundleLocalPaths = [];
        List<string> bundleCosts = [];

        foreach (FeaturedBundle bundle in featuredConfiguration.Bundles)
        {
            StoreItem? bundleItem = StoreItems.GetByID(bundle.StoreItemID);

            if (bundleItem is null) continue;

            bundleIDs.Add(bundleItem.ID.ToString());
            bundleAlreadyOwned.Add((isSandbox || (user.InventoryItems?.Any(i => string.Equals(i.ItemCode, bundleItem.PrefixedCode, StringComparison.OrdinalIgnoreCase)) ?? false)) ? "1" : "0");

            HashSet<int> includedIndices = [.. bundle.IncludedProductIndices];

            // The Client Interprets Each Tilde-Separated Value As A Boolean Flag Per Product Position
            // So We Convert The Zero-Based Indices Into A Positional Mask (e.g. Indices [0, 1] With 2 Products → "1~1")
            string inclusionMask = string.Join("~", Enumerable.Range(0, featuredConfiguration.FeaturedItemIDs.Count)
                .Select(index => includedIndices.Contains(index) ? "1" : "0"));

            bundleIncludedProducts.Add(inclusionMask);
            bundleNames.Add(bundleItem.Name);
            bundleLocalPaths.Add(bundleItem.Resource);
            bundleCosts.Add(isSandbox ? "0" : bundleItem.GoldCost.ToString());
        }

        response["productPrices"] = string.Empty;
        response["productNames"] = string.Join("|", productNames);
        response["productIDs"] = string.Join("|", productIDs);
        response["productAlreadyOwned"] = string.Join("|", productAlreadyOwned);
        response["productLocalContent"] = string.Join("|", productLocalContent);
        response["productEligibility"] = string.Join("|", productEligibility);

        int placeholderCount = Math.Max(productNames.Count, 3);
        string placeholderPipe = string.Join("|", Enumerable.Repeat("-1", placeholderCount));

        response["productIsBundle"] = string.Join("|", Enumerable.Repeat("1", placeholderCount));
        response["productQuantity"] = placeholderPipe;
        response["productWebContent"] = placeholderPipe;
        response["productDescription"] = placeholderPipe;

        string specialBundleEntry = string.Join("~", Enumerable.Repeat("-1", 5));
        response["specialBundles"] = string.Join("|", Enumerable.Repeat(specialBundleEntry, placeholderCount));

        string chargesEntry = string.Join("~", Enumerable.Repeat("-1", 6));
        response["productCharges"] = string.Join("|", Enumerable.Repeat(chargesEntry, placeholderCount));

        string durationsEntry = string.Join("~", Enumerable.Repeat("-1", 7));
        response["productDurations"] = string.Join("|", Enumerable.Repeat(durationsEntry, placeholderCount));

        response["productTimes"] = string.Join("|", Enumerable.Repeat("0,0", placeholderCount));
        response["purchasable"] = string.Join("|", Enumerable.Repeat("1", placeholderCount));
        response["productPremium"] = string.Join("|", Enumerable.Repeat("0", placeholderCount));
        response["premium_mmp_cost"] = string.Join("|", Enumerable.Repeat(isSandbox ? "0" : "9002", placeholderCount));

        const string statsEntry = "-1~~0~0";
        response["productStats"] = string.Join("|", Enumerable.Repeat(statsEntry, placeholderCount));
        response["productEnhancements"] = string.Join("|", Enumerable.Repeat(string.Empty, placeholderCount));
        response["productEnhancementIDs"] = string.Join("|", Enumerable.Repeat(string.Empty, placeholderCount));

        response["bundleIDs"] = string.Join("|", bundleIDs);
        response["bundleAlreadyOwned"] = string.Join("|", bundleAlreadyOwned);
        response["bundleIncludedProducts"] = string.Join("|", bundleIncludedProducts);
        response["bundleNames"] = string.Join("|", bundleNames);
        response["bundleLocalPaths"] = string.Join("|", bundleLocalPaths);
        response["bundleCosts"] = string.Join("|", bundleCosts);

        response["chargesRemaining"] = string.Empty;
        response["durationsRemaining"] = string.Empty;

        response["totalPages"] = 1;
        response["totalPoints"] = user.GoldCoins;
        response["totalMMPoints"] = user.SilverCoins;
        response["categoryID"] = (int)StoreCategory.FeaturedHeroAvatars;
        response["currentPage"] = 1;
        response["customAccountIcon"] = 0;
        response["customAccountIconCost"] = isSandbox ? 0 : 350;
        response["customAccountIconCostMMP"] = isSandbox ? 0 : 1000;
        response["accountIconsUnlocked"] = 1;
        response["timestamp"] = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        response["vaultHighlight"] = "NULL";

        StoreVaultHelpers.PopulateSelectedUpgrades(account, response);
    }

    /// <summary>
    ///     Maps a store category ID to the corresponding <see cref="StoreItemType" />.
    /// </summary>
    internal static StoreItemType MapStoreCategoryToItemType(int categoryID)
    {
        return categoryID switch
        {
            (int)StoreCategory.HeroAvatars => StoreItemType.AlternativeAvatar,
            (int)StoreCategory.FeaturedHeroAvatars => StoreItemType.AlternativeAvatar,
            (int)StoreCategory.AccountIcons => StoreItemType.AccountIcon,
            (int)StoreCategory.ChatSymbols => StoreItemType.ChatSymbol,
            (int)StoreCategory.FlagSymbols => StoreItemType.ChatSymbol,
            (int)StoreCategory.HeroSymbols => StoreItemType.ChatSymbol,
            (int)StoreCategory.MiscellaneousSymbols => StoreItemType.ChatSymbol,
            (int)StoreCategory.Announcers => StoreItemType.AnnouncerVoice,
            (int)StoreCategory.Miscellaneous => StoreItemType.Miscellaneous,
            (int)StoreCategory.Bundles => StoreItemType.Bundle,
            (int)StoreCategory.NameColors => StoreItemType.ChatNameColour,
            (int)StoreCategory.Taunt => StoreItemType.Taunt,
            (int)StoreCategory.Couriers => StoreItemType.Courier,
            (int)StoreCategory.EarlyAccessHeroes => StoreItemType.EarlyAccessProduct,
            (int)StoreCategory.Heroes => StoreItemType.Hero,
            (int)StoreCategory.Wards => StoreItemType.Ward,
            (int)StoreCategory.Enhancements => StoreItemType.Enhancement,
            (int)StoreCategory.Creeps => StoreItemType.Creep,
            (int)StoreCategory.TauntBadges => StoreItemType.TauntBadge,
            (int)StoreCategory.TeleportationEffects => StoreItemType.TeleportEffect,
            (int)StoreCategory.SelectionCircles => StoreItemType.SelectionCircle,
            _ => StoreItemType.EarlyAccessProduct
        };
    }

    /// <summary>
    ///     Determines whether the specified store category uses pagination.
    /// </summary>
    private static bool IsPaginated(int categoryID)
    {
        return categoryID switch
        {
            (int)StoreCategory.Announcers => true,
            (int)StoreCategory.Taunt => true,
            (int)StoreCategory.NameColors => true,
            (int)StoreCategory.Enhancements => true,
            (int)StoreCategory.Couriers => true,
            (int)StoreCategory.TeleportationEffects => true,
            (int)StoreCategory.Wards => true,
            (int)StoreCategory.Miscellaneous => true,
            (int)StoreCategory.Creeps => true,
            (int)StoreCategory.Bundles => true,
            _ => false
        };
    }

    /// <summary>
    ///     Determines whether the taunt availability status should be included in the response for the given item type.
    /// </summary>
    private static bool ShouldSendTauntStatus(StoreItemType itemType)
    {
        return itemType is StoreItemType.Taunt or StoreItemType.Miscellaneous;
    }

    /// <summary>
    ///     Determines whether purchasing the given item requires the base taunt to be owned first.
    /// </summary>
    private static bool RequiresBaseTaunt(StoreItem storeItem)
    {
        if (storeItem.StoreItemType == StoreItemType.Taunt) return storeItem.ID != TauntStoreItemID;

        return storeItem.ID == SuperTauntStoreItemID;
    }

    /// <summary>
    ///     Creates an error response dictionary with the specified error code.
    /// </summary>
    internal static Dictionary<string, object> CreateErrorResponse(int errorCode)
    {
        return new Dictionary<string, object>
        {
            ["popupCode"] = (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
            ["errorCode"] = errorCode
        };
    }

    /// <summary>
    ///     Determines whether the given item is a repeatable or consumable service (e.g. Nickname Change,
    ///     Sub-Account, Stat Reset, Mastery Boosts) that can be purchased multiple times.
    /// </summary>
    private static bool IsRepeatableItem(StoreItem item)
    {
        return item.ID is 161 or 162 or 163 or 492 or 655
               || item.StoreItemType is StoreItemType.Mastery or StoreItemType.Coupon
               || item.ID is MasteryBoost.Regular.ProductCode or MasteryBoost.Super.ProductCode
                   or MasteryBoost.Bundle.ProductCode
               || item.Name.Contains("Mastery Boost", StringComparison.OrdinalIgnoreCase)
               || item.Name.Contains("Nickname Change", StringComparison.OrdinalIgnoreCase)
               || item.Name.Contains("Sub-Account", StringComparison.OrdinalIgnoreCase)
               || item.Name.Contains("Stat Reset", StringComparison.OrdinalIgnoreCase);
    }
}
