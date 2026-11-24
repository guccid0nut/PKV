// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data;
using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Store;
using KONGOR.MasterServer.Controllers.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     MediatR command handler for <see cref="RedeemStoreCodeCommand"/>.
///     Executes transactional validation, inventory grants, coin rewards, and audit trail logging.
///     Distributed locking on the coupon code is handled automatically via <see cref="NEXUS.Contracts.CQRS.ILockableRequest"/>
///     and <see cref="ASPIRE.Common.Behaviors.DistributedLockBehavior{TRequest, TResponse}"/>.
/// </summary>
public sealed class RedeemStoreCodeHandler(
    MerrickContext db,
    ILogger<RedeemStoreCodeHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<RedeemStoreCodeCommand, RedeemStoreCodeResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RedeemStoreCodeResultDto> Handle(
        RedeemStoreCodeCommand request,
        CancellationToken cancellationToken)
    {
        string normalisedCode = request.Code.Trim().ToUpperInvariant();

        logger.LogInformation(
            "[RedeemStoreCodeHandler] Attempting redemption of code '{Code}' for Account ID {AccountId}",
            normalisedCode, request.AccountId);

        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            IDbContextTransaction? transaction = null;
            try
            {
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "[RedeemStoreCodeHandler] Explicit transaction not supported or needed, proceeding with context tracking.");
            }

            try
            {
                Account? account = await db.Accounts
                    .Include(a => a.User).ThenInclude(u => u!.InventoryItems)
                    .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

                if (account?.User is null)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    logger.LogWarning("[RedeemStoreCodeHandler] Account or User not found for Account #{AccountId}", request.AccountId);
                    return new RedeemStoreCodeResultDto(
                        false,
                        (int)StoreErrorCode.STORE_PURCHASE_ERROR,
                        null,
                        StoreController.CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ERROR, request.HostTime));
                }

                RedeemableCode? code = await db.RedeemableCodes
                    .SingleOrDefaultAsync(c => c.Code.Equals(normalisedCode), cancellationToken);

                if (code is null)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    logger.LogWarning("[RedeemStoreCodeHandler] Code '{Code}' not found in database.", normalisedCode);
                    return new RedeemStoreCodeResultDto(
                        false,
                        (int)StoreErrorCode.STORE_PURCHASE_CODE_INVALID_ERROR,
                        null,
                        StoreController.CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_CODE_INVALID_ERROR, request.HostTime));
                }

                if (code.TimestampRedeemed is not null || code.RedeemedByAccountID is not null)
                {
                    if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    logger.LogWarning("[RedeemStoreCodeHandler] Code '{Code}' has already been redeemed.", normalisedCode);
                    return new RedeemStoreCodeResultDto(
                        false,
                        (int)StoreErrorCode.STORE_PURCHASE_CODE_USED_ERROR,
                        null,
                        StoreController.CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_CODE_USED_ERROR, request.HostTime));
                }

                User user = account.User;
                StoreItem? grantedProduct = null;

                if (code.ProductID is { } productID)
                {
                    grantedProduct = StoreController.StoreItems.GetByID(productID);

                    if (grantedProduct is null)
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        db.ChangeTracker.Clear();
                        logger.LogWarning(@"[RedeemStoreCodeHandler] Code ""{Code}"" references unknown product ID ""{ProductID}""",
                            code.Code, productID);

                        return new RedeemStoreCodeResultDto(
                            false,
                            (int)StoreErrorCode.STORE_PURCHASE_ITEM_MISSING_ERROR,
                            null,
                            StoreController.CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_ITEM_MISSING_ERROR, request.HostTime));
                    }

                    if (!db.UserInventory.Any(i => i.UserID == user.ID && i.ItemCode == grantedProduct.PrefixedCode))
                    {
                        UserInventory newInv = new()
                        {
                            UserID = user.ID,
                            AccountID = account.ID,
                            ItemCode = grantedProduct.PrefixedCode,
                            StoreItemId = null,
                            Quantity = 1,
                            IsPermanent = true,
                            TimestampAcquired = _timeProvider.GetUtcNow(),
                            Source = "RedeemCode"
                        };
                        db.UserInventory.Add(newInv);
                        user.InventoryItems ??= [];
                        user.InventoryItems.Add(newInv);
                    }
                }

                user.GoldCoins += code.GoldCoinsReward;
                user.SilverCoins += code.SilverCoinsReward;
                user.PlinkoTickets += code.PlinkoTicketsReward;

                code.RedeemedByAccountID = account.ID;
                code.TimestampRedeemed = _timeProvider.GetUtcNow();

                db.EconomyTransactions.Add(new EconomyTransaction
                {
                    UserID = user.ID,
                    AccountID = account.ID,
                    GoldCoinsDelta = code.GoldCoinsReward,
                    SilverCoinsDelta = code.SilverCoinsReward,
                    PlinkoTicketsDelta = code.PlinkoTicketsReward,
                    GoldCoinsBalanceAfter = user.GoldCoins,
                    SilverCoinsBalanceAfter = user.SilverCoins,
                    PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                    TransactionType = "CodeRedemption",
                    ReferenceId = code.Code,
                    Description = $"Redeemed in-game promo code {code.Code}",
                    Timestamp = _timeProvider.GetUtcNow()
                });

                await db.SaveChangesAsync(cancellationToken);

                if (transaction != null) await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    @"[RedeemStoreCodeHandler] Account #{AccountID} successfully redeemed code ""{Code}"" (Gold={Gold}, Silver={Silver}, Tickets={Tickets}, ProductId={ProductID})",
                    account.ID, code.Code, code.GoldCoinsReward, code.SilverCoinsReward, code.PlinkoTicketsReward, code.ProductID);

                string redeemedField = StoreController.FormatRedeemedField(code.GoldCoinsReward, code.SilverCoinsReward, grantedProduct);

                Dictionary<string, object> response = new()
                {
                    ["responseCode"] = (int)StoreResponseCode.NO_RESPONSE,
                    ["popupCode"] = (int)StorePopupCode.POP_UP_COIN_REDEEM_SUCCESS,
                    ["errorCode"] = 0,
                    ["redeemed"] = redeemedField,
                    ["totalPoints"] = user.GoldCoins,
                    ["totalMMPoints"] = user.SilverCoins,
                    ["requestHostTime"] = request.HostTime ?? string.Empty
                };

                return new RedeemStoreCodeResultDto(true, 0, redeemedField, response);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                logger.LogWarning(ex,
                    "[RedeemStoreCodeHandler] Concurrency conflict redeeming code {Code} for Account {AccountId}",
                    normalisedCode, request.AccountId);
                return new RedeemStoreCodeResultDto(
                    false,
                    (int)StoreErrorCode.STORE_PURCHASE_CODE_USED_ERROR,
                    null,
                    StoreController.CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_CODE_USED_ERROR, request.HostTime));
            }
            catch (Exception ex)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                logger.LogError(ex,
                    "[RedeemStoreCodeHandler] Unexpected error redeeming code {Code} for Account {AccountId}",
                    normalisedCode, request.AccountId);
                return new RedeemStoreCodeResultDto(
                    false,
                    (int)StoreErrorCode.STORE_PURCHASE_CODE_INVALID_ERROR,
                    null,
                    StoreController.CreateRedeemErrorResponse((int)StoreErrorCode.STORE_PURCHASE_CODE_INVALID_ERROR, request.HostTime));
            }
        });
    }
}
