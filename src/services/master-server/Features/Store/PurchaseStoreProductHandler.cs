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
using KONGOR.MasterServer.Services.Economy;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     MediatR command handler for <see cref="PurchaseStoreProductCommand"/>.
///     Executes transactional purchase validation, point deduction, inventory updates,
///     and publishes <see cref="HeroAvatarPurchasedEvent"/>.
///     Distributed locking is handled automatically via <see cref="NEXUS.Contracts.CQRS.ILockableRequest"/>
///     and the MediatR pipeline's <see cref="DistributedLockBehavior{TRequest, TResponse}"/>.
/// </summary>
public sealed class PurchaseStoreProductHandler(
    MerrickContext db,
    ILogger<PurchaseStoreProductHandler> logger,
    IEventBus? eventBus = null)
    : IRequestHandler<PurchaseStoreProductCommand, PurchaseStoreProductResultDto>
{
    public async Task<PurchaseStoreProductResultDto> Handle(
        PurchaseStoreProductCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[PurchaseStoreProductHandler] Processing purchase: AccountId={AccountId}, ProductId={ProductId}, Currency={Currency}, CategoryId={CategoryId}, Page={Page}",
            request.AccountId, request.ProductId, request.Currency, request.CategoryId, request.CurrentPage);

        StoreItem? storeItem = StoreController.StoreItems.GetByID(request.ProductId);
        string? clientStoreKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? request.IdempotencyKey
            : (!string.IsNullOrWhiteSpace(request.HostTime) && request.HostTime.Trim() != "0"
                ? $"ht-{request.HostTime.Trim()}"
                : null);
        string purchaseIdempotencyKey = EconomyService.NormalizeScopedIdempotencyKey(
            $"ingame-store:{request.ProductId}",
            request.AccountId,
            clientStoreKey);

        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            const int maxRetries = 3;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    db.ChangeTracker.Clear();
                }

                Account? account = await db.Accounts
                    .Include(a => a.Clan)
                    .Include(a => a.User).ThenInclude(u => u!.InventoryItems)
                    .Include(a => a.SelectedStoreItems)
                    .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

                if (account?.User is null)
                {
                    logger.LogError("[PurchaseStoreProductHandler] Account or User not found for Account ID {AccountId}", request.AccountId);
                    var notFoundResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_ACCOUNT_INFORMATION_ERROR);
                    return new PurchaseStoreProductResultDto(
                        false,
                        (int)StoreErrorCode.STORE_ACCOUNT_INFORMATION_ERROR,
                        (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                        notFoundResponse);
                }

                EconomyTransaction? existingTx = await db.EconomyTransactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.UserID == account.User.ID && t.IdempotencyKey == purchaseIdempotencyKey,
                        cancellationToken);
                if (existingTx is not null)
                {
                    logger.LogWarning(
                        "[PurchaseStoreProductHandler] Idempotent purchase replay detected for AccountId={AccountId}, ProductId={ProductId} (IdempotencyKey={IdempotencyKey}); returning committed listing.",
                        account.ID, request.ProductId, purchaseIdempotencyKey);
                    Dictionary<string, object> replayResponse = [];
                    StoreController.PopulateItemListing(account.User, account, replayResponse, request.CategoryId, request.CurrentPage);
                    replayResponse["responseCode"] = (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE;
                    replayResponse["requestHostTime"] = request.HostTime;
                    replayResponse["errorCode"] = 0;
                    replayResponse["popupCode"] = (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS;
                    return new PurchaseStoreProductResultDto(
                        true,
                        0,
                        (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS,
                        replayResponse);
                }

                IDbContextTransaction? transaction = null;
                try
                {
                    transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[PurchaseStoreProductHandler] Database provider does not require explicit transaction");
                }

                try
                {
                    (Dictionary<string, object> response, bool success) = StoreController.ExecutePurchase(
                        account, storeItem, request.Currency, request.DiscountCode);

                    if (!success)
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        db.ChangeTracker.Clear();

                        int errorCode = response.TryGetValue("errorCode", out object? err) && err is int ec ? ec : (int)StoreErrorCode.STORE_PURCHASE_ERROR;
                        int popupCode = response.TryGetValue("popupCode", out object? pop) && pop is int pc ? pc : (int)StorePopupCode.POP_UP_ERROR_MESSAGE;

                        logger.LogWarning("[PurchaseStoreProductHandler] Purchase failed for AccountId {AccountId}, ErrorCode {ErrorCode}",
                            request.AccountId, errorCode);

                        return new PurchaseStoreProductResultDto(false, errorCode, popupCode, response);
                    }

                    EconomyTransaction? pendingTx = account.User.EconomyTransactions
                        .LastOrDefault(t => t.ID == 0 && t.IdempotencyKey is null);
                    if (pendingTx is not null)
                    {
                        pendingTx.IdempotencyKey = purchaseIdempotencyKey;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    if (transaction != null) await transaction.CommitAsync(cancellationToken);

                    // Publish Domain Event
                    if (eventBus != null && storeItem != null)
                    {
                        int cost = request.Currency == "1" ? storeItem.SilverCost : storeItem.GoldCost;
                        int currencyType = request.Currency == "1" ? 1 : 0;
                        HeroAvatarPurchasedEvent domainEvent = new(
                            Guid.NewGuid(),
                            TimeProvider.System.GetUtcNow(),
                            account.ID,
                            account.User.ID,
                            storeItem.ID,
                            storeItem.Code ?? storeItem.PrefixedCode ?? storeItem.Name,
                            currencyType,
                            cost,
                            TimeProvider.System.GetUtcNow()
                        );
                        await eventBus.PublishAsync(domainEvent, cancellationToken);
                    }

                    StoreController.PopulateItemListing(account.User, account, response, request.CategoryId, request.CurrentPage);
                    response["responseCode"] = (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE;
                    response["requestHostTime"] = request.HostTime;
                    if (!response.ContainsKey("errorCode")) response["errorCode"] = 0;

                    logger.LogInformation(
                        "[PurchaseStoreProductHandler] Purchase successful for AccountId={AccountId}, ProductId={ProductId}, Remaining Gold={Gold}, Silver={Silver}",
                        account.ID, request.ProductId, account.User.GoldCoins, account.User.SilverCoins);

                    return new PurchaseStoreProductResultDto(
                        true,
                        0,
                        (int)StorePopupCode.POP_UP_PRODUCT_PURCHASE_SUCCESS,
                        response);
                }
                catch (DbUpdateException dbEx) when (EconomyService.IsUniqueIdempotencyViolation(dbEx) && attempt < maxRetries - 1)
                {
                    if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
                    db.ChangeTracker.Clear();
                    logger.LogWarning(
                        "[PurchaseStoreProductHandler] Concurrent idempotency race (23505) rolled back cleanly for AccountId={AccountId}, ProductId={ProductId}; replaying attempt #{NextAttempt}.",
                        request.AccountId, request.ProductId, attempt + 2);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    if (attempt == maxRetries - 1)
                    {
                        logger.LogError(ex, "[PurchaseStoreProductHandler] Concurrency failure after max retries for AccountId {AccountId}", request.AccountId);
                        var busyResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR);
                        return new PurchaseStoreProductResultDto(
                            false,
                            (int)StoreErrorCode.STORE_PURCHASE_ERROR,
                            (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                            busyResponse);
                    }
                }
                catch (Exception ex)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    logger.LogError(ex, "[PurchaseStoreProductHandler] Unexpected error during purchase for AccountId {AccountId}", request.AccountId);
                    throw;
                }
            }

            var fallbackResponse = StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR);
            return new PurchaseStoreProductResultDto(
                false,
                (int)StoreErrorCode.STORE_PURCHASE_ERROR,
                (int)StorePopupCode.POP_UP_ERROR_MESSAGE,
                fallbackResponse);
        });
    }
}
