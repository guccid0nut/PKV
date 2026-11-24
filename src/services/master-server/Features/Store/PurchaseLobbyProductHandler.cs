// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data;
using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Store;
using KONGOR.MasterServer.Controllers.Store;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     MediatR command handler for match lobby purchases (<see cref="PurchaseLobbyProductCommand"/>).
///     Handles default hero avatars, already-owned avatars, atomic purchasing via execution strategy,
///     and domain event publishing with automatic distributed locking via <see cref="NEXUS.Contracts.CQRS.ILockableRequest"/>.
/// </summary>
public sealed class PurchaseLobbyProductHandler(
    MerrickContext db,
    ILogger<PurchaseLobbyProductHandler> logger,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<PurchaseLobbyProductCommand, PurchaseLobbyProductResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PurchaseLobbyProductResultDto> Handle(
        PurchaseLobbyProductCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [PurchaseLobbyProductHandler] Match lobby purchase request: AccountId={AccountId}, HeroName={HeroName}, AvatarCode={AvatarCode}, Currency={Currency}",
            _timeProvider.GetUtcNow(),
            request.AccountId,
            request.HeroName,
            request.AvatarCode,
            request.Currency);

        string cleanHero = HeroDefinitionService.CleanHeroIdentifier(request.HeroName);
        string itemCode = request.HeroName + "." + request.AvatarCode;
        string cleanItemCode = cleanHero + "." + request.AvatarCode;

        StoreItem? storeItem = StoreController.StoreItems.GetByCode(itemCode)
                               ?? StoreController.StoreItems.GetByPrefixedCode(itemCode)
                               ?? StoreController.StoreItems.GetByName(itemCode)
                               ?? StoreController.StoreItems.GetByCode(cleanItemCode)
                               ?? StoreController.StoreItems.GetByPrefixedCode(cleanItemCode)
                               ?? StoreController.StoreItems.GetByName(cleanItemCode);

        if (storeItem is null && (request.AvatarCode.Contains('.') || request.AvatarCode.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)))
        {
            storeItem = StoreController.StoreItems.GetByCode(request.AvatarCode)
                        ?? StoreController.StoreItems.GetByPrefixedCode(request.AvatarCode)
                        ?? StoreController.StoreItems.GetByName(request.AvatarCode);
        }

        bool isDefaultAvatar = request.AvatarCode.Equals("default", StringComparison.OrdinalIgnoreCase)
                               || request.AvatarCode.Equals("standard", StringComparison.OrdinalIgnoreCase)
                               || request.AvatarCode.Equals("base", StringComparison.OrdinalIgnoreCase)
                               || request.AvatarCode.Equals("hero", StringComparison.OrdinalIgnoreCase);

        Account? account = await db.Accounts
            .Include(a => a.User).ThenInclude(u => u!.InventoryItems)
            .Include(a => a.User).ThenInclude(u => u!.EconomyTransactions)
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

        if (account?.User is null)
        {
            logger.LogError(
                "[{Timestamp}] [PurchaseLobbyProductHandler] Account or User not found for Account ID {AccountId}",
                _timeProvider.GetUtcNow(),
                request.AccountId);
            var notFoundResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_ACCOUNT_INFORMATION_ERROR);
            notFoundResponse["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
            return new PurchaseLobbyProductResultDto(
                false,
                (int)StoreErrorCode.STORE_ACCOUNT_INFORMATION_ERROR,
                (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                notFoundResponse);
        }

        bool isAlreadyOwned = (UpgradesResolutionService.UnlockAllItems && storeItem is not null) || (storeItem is not null &&
            (account.User.InventoryItems?.Any(i =>
                string.Equals(i.ItemCode, storeItem.PrefixedCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.ItemCode, storeItem.Code, StringComparison.OrdinalIgnoreCase)) ?? false));

        if (isDefaultAvatar || isAlreadyOwned)
        {
            logger.LogInformation(
                "[{Timestamp}] [PurchaseLobbyProductHandler] Avatar already owned or default for AccountId={AccountId}: isDefault={IsDefault}, isOwned={IsOwned}",
                _timeProvider.GetUtcNow(),
                account.ID,
                isDefaultAvatar,
                isAlreadyOwned);

            Dictionary<string, object> alreadyOwnedResponse = new()
            {
                ["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST,
                ["popupCode"] = -1,
                ["errorCode"] = 0,
                ["totalPoints"] = account.User.GoldCoins,
                ["totalMMPoints"] = account.User.SilverCoins
            };

            return new PurchaseLobbyProductResultDto(true, 0, -1, alreadyOwnedResponse);
        }

        if (storeItem is null)
        {
            logger.LogWarning(
                "[{Timestamp}] [PurchaseLobbyProductHandler] Store item not found for ItemCode={ItemCode}",
                _timeProvider.GetUtcNow(),
                itemCode);
            var notFoundItemResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ITEM_MISSING_ERROR);
            notFoundItemResponse["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
            return new PurchaseLobbyProductResultDto(
                false,
                (int)StoreErrorCode.STORE_PURCHASE_ITEM_MISSING_ERROR,
                (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                notFoundItemResponse);
        }

        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            const int maxRetries = 3;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    db.ChangeTracker.Clear();
                    account = await db.Accounts
                        .Include(a => a.User).ThenInclude(u => u!.InventoryItems)
                        .Include(a => a.User).ThenInclude(u => u!.EconomyTransactions)
                        .Include(a => a.SelectedStoreItems)
                        .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

                    if (account?.User is null) break;
                }

                IDbContextTransaction? transaction = null;
                try
                {
                    transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[PurchaseLobbyProductHandler] Explicit transaction not supported or required by provider");
                }

                try
                {
                    (Dictionary<string, object> response, bool success) = StoreController.ExecutePurchase(
                        account, storeItem, request.Currency);

                    if (!success)
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        db.ChangeTracker.Clear();

                        response["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
                        int errorCode = response.TryGetValue("errorCode", out object? err) && err is int ec ? ec : (int)StoreErrorCode.STORE_PURCHASE_ERROR;
                        int popupCode = response.TryGetValue("popupCode", out object? pop) && pop is int pc ? pc : (int)StorePopupCode.POP_UP_ERROR_MESSAGE;

                        logger.LogWarning(
                            "[{Timestamp}] [PurchaseLobbyProductHandler] Lobby purchase failed for AccountId {AccountId}, ErrorCode {ErrorCode}",
                            _timeProvider.GetUtcNow(),
                            request.AccountId,
                            errorCode);

                        return new PurchaseLobbyProductResultDto(false, errorCode, popupCode, response);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    if (transaction != null) await transaction.CommitAsync(cancellationToken);

                    if (eventBus != null)
                    {
                        int cost = request.Currency == "1" ? storeItem.SilverCost : storeItem.GoldCost;
                        int currencyType = request.Currency == "1" ? 1 : 0;
                        HeroAvatarPurchasedEvent domainEvent = new(
                            Guid.NewGuid(),
                            _timeProvider.GetUtcNow(),
                            account.ID,
                            account.User.ID,
                            storeItem.ID,
                            storeItem.Code ?? storeItem.PrefixedCode ?? storeItem.Name,
                            currencyType,
                            cost,
                            _timeProvider.GetUtcNow()
                        );
                        await eventBus.PublishAsync(domainEvent, cancellationToken);
                    }

                    response["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
                    response["totalPoints"] = account.User.GoldCoins;
                    response["totalMMPoints"] = account.User.SilverCoins;

                    logger.LogInformation(
                        "[{Timestamp}] [PurchaseLobbyProductHandler] Lobby purchase succeeded for AccountId {AccountId}, Item {ItemCode}. Balance: Gold={Gold}, Silver={Silver}",
                        _timeProvider.GetUtcNow(),
                        account.ID,
                        storeItem.Code,
                        account.User.GoldCoins,
                        account.User.SilverCoins);

                    return new PurchaseLobbyProductResultDto(
                        true,
                        0,
                        (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS,
                        response);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    if (attempt == maxRetries - 1)
                    {
                        logger.LogError(ex, "[PurchaseLobbyProductHandler] Concurrency failure after max retries for AccountId {AccountId}", request.AccountId);
                        var busyResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR);
                        busyResponse["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
                        return new PurchaseLobbyProductResultDto(
                            false,
                            (int)StoreErrorCode.STORE_PURCHASE_ERROR,
                            (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                            busyResponse);
                    }
                }
                catch (Exception ex)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    logger.LogError(ex, "[PurchaseLobbyProductHandler] Unexpected error during purchase for AccountId {AccountId}", request.AccountId);
                    throw;
                }
            }

            var fallbackResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR);
            fallbackResponse["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST;
            return new PurchaseLobbyProductResultDto(
                false,
                (int)StoreErrorCode.STORE_PURCHASE_ERROR,
                (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                fallbackResponse);
        });
    }
}
