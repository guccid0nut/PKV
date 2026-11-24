// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Data;
using ASPIRE.Common.Services.Distributed;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Models.RequestResponse.Plinko;
using KONGOR.MasterServer.Services.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using IDatabase = StackExchange.Redis.IDatabase;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Economy;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Services;

namespace KONGOR.MasterServer.Controllers.Plinko;

/// <summary>
///     Handles the two Plinko ticket-exchange endpoints: the exchange catalogue listing (<c>/master/ticketexchange/</c>)
///     and the purchase of a specific item (<c>/master/ticketexchange/purchase/</c>).
/// </summary>
[ApiController]
[Route("master/ticketexchange")]
[Consumes("application/x-www-form-urlencoded")]
public class TicketExchangeController(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<TicketExchangeController> logger,
    IEconomyService? economyService = null,
    IDistributedLockService? distributedLockService = null) : ControllerBase
{
    private const int StatusBadCookie = 50;
    private const int StatusSuccess = 51;
    private const int StatusInsufficientTickets = 52;
    private const int StatusAlreadyOwned = 53;
    private const int StatusInvalidItem = 54;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IEconomyService? EconomyService { get; } = economyService;
    private IDistributedLockService? DistributedLockService { get; } = distributedLockService;
    private ILogger Logger { get; } = logger;

    private static TicketExchangeConfiguration ExchangeConfig => JSONConfiguration.TicketExchangeConfiguration;
    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    /// <summary>
    ///     Returns every entry in the ticket-exchange catalogue, keyed by zero-based index, along with the player's current
    ///     ticket balance.
    /// </summary>
    [HttpPost("", Name = "Plinko Ticket Exchange List Requester")]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default)
    {
        Account? account = await LoadAccountFromCookie(cancellationToken);

        if (account is null)
            return new PhpResult(new TicketExchangeListResponseDTO { StatusCode = StatusBadCookie, UserTickets = 0 });

        Dictionary<int, TicketExchangeItemDTO> items = [];

        for (int index = 0; index < ExchangeConfig.Items.Count; index++)
        {
            TicketExchangeEntry entry = ExchangeConfig.Items[index];

            items[index] = new TicketExchangeItemDTO
            {
                ID = entry.ID,
                Cost = entry.TicketCost,
                ProductID = entry.ProductID,
                Name = entry.Name,
                Type = entry.Type,
                LocalPath = entry.LocalPath
            };
        }

        TicketExchangeListResponseDTO response = new()
        {
            StatusCode = StatusSuccess,
            Items = items,
            UserTickets = account.User.PlinkoTickets
        };

        return new PhpResult(response);
    }

    /// <summary>
    ///     Purchases a ticket-exchange item, deducting the item's ticket cost and granting the store product to the player's
    ///     inventory.
    ///     Returns granular status codes so support staff can distinguish invalid cookie, insufficient tickets, already-owned,
    ///     and invalid-item failures.
    /// </summary>
    [HttpPost("purchase/", Name = "Plinko Ticket Exchange Purchase Requester")]
    public async Task<IActionResult> Purchase(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Account? account = await LoadAccountFromCookie(cancellationToken);

            if (account is null) return PurchaseFailureResponse(StatusBadCookie);

            if (int.TryParse(Request.Form["id"], out int productID).Equals(false))
                return PurchaseFailureResponse(StatusInvalidItem);

            TicketExchangeEntry? entry = ExchangeConfig.GetByProductID(productID);

            if (entry is null) return PurchaseFailureResponse(StatusInvalidItem);

            StoreItem? storeItem = StoreItems.GetByID(entry.ProductID);

            if (storeItem is null)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                    Logger.LogWarning(@"Ticket Exchange Entry ""{EntryID}"" References Unknown Product ID ""{ProductID}""",
                        entry.ID, entry.ProductID);

                return PurchaseFailureResponse(StatusInvalidItem);
            }

            User user = account.User;

            await using IDistributedLock? economyLock = DistributedLockService is not null
                ? await DistributedLockService.AcquireLockAsync(
                    $"lock:economy:user:{user.ID}",
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(5),
                    cancellationToken)
                : null;

            if (economyLock is { IsAcquired: false })
            {
                Logger.LogWarning("[TicketExchange] Timed out acquiring economy lock for User #{UserId} (Account #{AccountId}).",
                    user.ID, account.ID);
                return PurchaseFailureResponse(0);
            }

            EconomyService? concreteEconomy = EconomyService as EconomyService;
            bool isRelationalTx = concreteEconomy is { HasActiveRelationalDatabase: true };
            string? rawClientKey = MiniGameController.ExtractClientIdempotencyKey(Request);
            string requestIdempotencyKey = Services.Economy.EconomyService.NormalizeScopedIdempotencyKey($"ticket-exchange:{account.ID}", entry.ProductID, rawClientKey);

            async Task<IActionResult> ExecutePurchaseAttemptAsync(int attempt, CancellationToken ct)
            {
                Account? attemptAccount = attempt == 0 ? account : await LoadAccountFromCookie(ct);
                if (attemptAccount?.User is null)
                    return PurchaseFailureResponse(0);

                User attemptUser = attemptAccount.User;

                if (concreteEconomy is not null)
                {
                    EconomyTransaction? alreadyCommitted = await concreteEconomy.TryGetByIdempotencyKeyAsync(
                        attemptUser.ID,
                        requestIdempotencyKey,
                        ct);

                    if (alreadyCommitted is not null)
                    {
                        Logger.LogWarning(
                            "[TicketExchange] Idempotent TicketExchange replay detected for Account #{AccountId} (Attempt=#{Attempt}, IdempotencyKey={IdempotencyKey}); returning committed balance without duplicate charge or grant.",
                            attemptAccount.ID, attempt + 1, requestIdempotencyKey);
                        return new PhpResult(BuildPurchaseResponse(StatusSuccess, attemptUser.PlinkoTickets));
                    }
                }

                if (attemptUser.InventoryItems.Any(i => i.ItemCode == storeItem.PrefixedCode))
                    return PurchaseFailureResponse(StatusAlreadyOwned);

                if (attemptUser.PlinkoTickets < entry.TicketCost)
                    return PurchaseFailureResponse(StatusInsufficientTickets);

                int originalTickets = attemptUser.PlinkoTickets;
                bool economyDeducted = false;

                try
                {
                    if (concreteEconomy is not null)
                    {
                        (CurrencyRewardResult deductResult, EconomyTransaction? replayedTx) =
                            await concreteEconomy.DeductCurrenciesWithIdempotencyAsync(
                                attemptUser.ID,
                                attemptAccount.ID,
                                goldCost: 0,
                                silverCost: 0,
                                plinkoCost: entry.TicketCost,
                                transactionType: "TicketExchange",
                                idempotencyKey: requestIdempotencyKey,
                                referenceId: storeItem.PrefixedCode,
                                description: $"Ticket exchange purchase for {storeItem.PrefixedCode} (Product #{entry.ProductID})",
                                ct);

                        if (!deductResult.Success)
                            return PurchaseFailureResponse(StatusInsufficientTickets);

                        if (replayedTx is not null)
                        {
                            Logger.LogWarning(
                                "[TicketExchange] Concurrent idempotency replay detected during DeductCurrencies for Account #{AccountId} (IdempotencyKey={IdempotencyKey}); skipping duplicate inventory grant.",
                                attemptAccount.ID, requestIdempotencyKey);
                            return new PhpResult(BuildPurchaseResponse(StatusSuccess, attemptUser.PlinkoTickets));
                        }

                        economyDeducted = true;
                        attemptUser.PlinkoTickets = deductResult.PlinkoTicketsBalanceAfter;
                    }
                    else if (EconomyService is not null)
                    {
                        CurrencyRewardResult deductResult = await EconomyService.DeductCurrenciesAsync(
                            attemptUser.ID,
                            attemptAccount.ID,
                            goldCost: 0,
                            silverCost: 0,
                            plinkoCost: entry.TicketCost,
                            transactionType: "TicketExchange",
                            referenceId: requestIdempotencyKey,
                            description: $"Ticket exchange purchase for {storeItem.PrefixedCode} (Product #{entry.ProductID})",
                            ct);

                        if (!deductResult.Success)
                            return PurchaseFailureResponse(StatusInsufficientTickets);

                        economyDeducted = true;
                        attemptUser.PlinkoTickets = deductResult.PlinkoTicketsBalanceAfter;
                    }
                    else
                    {
                        attemptUser.PlinkoTickets -= entry.TicketCost;
                    }

                    attemptUser.InventoryItems.Add(new UserInventory
                    {
                        UserID = attemptUser.ID,
                        AccountID = attemptAccount.ID,
                        ItemCode = storeItem.PrefixedCode,
                        StoreItemId = null,
                        Quantity = 1,
                        IsPermanent = true,
                        TimestampAcquired = TimeProvider.System.GetUtcNow(),
                        Source = "TicketExchange"
                    });

                    await AccountRepository.UpdateUserAsync(attemptUser, ct);

                    if (Logger.IsEnabled(LogLevel.Information))
                        Logger.LogInformation(
                            @"Ticket Exchange Purchase For Account ""{AccountID}"" Redeemed Product ""{ProductID}"" For ""{TicketCost}"" Tickets (IdempotencyKey ""{IdempotencyKey}"")",
                            attemptAccount.ID, entry.ProductID, entry.TicketCost, requestIdempotencyKey);

                    TicketExchangePurchaseResponseDTO response = BuildPurchaseResponse(StatusSuccess, attemptUser.PlinkoTickets);

                    return new PhpResult(response);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    attemptUser.PlinkoTickets = originalTickets;

                    if (!isRelationalTx && economyDeducted && EconomyService is not null)
                    {
                        concreteEconomy?.ClearTrackedChanges();
                        Logger.LogWarning(ex,
                            "[TicketExchange] Persistence failed after ticket deduction for User #{UserId} (Account #{AccountId}); issuing compensating refund (+{Tickets}T).",
                            attemptUser.ID, attemptAccount.ID, entry.TicketCost);

                        if (concreteEconomy is not null)
                        {
                            string refundKey = requestIdempotencyKey.Length <= 120
                                ? $"{requestIdempotencyKey}:refund"
                                : $"{requestIdempotencyKey[..120]}:refund";
                            await concreteEconomy.RecordTransactionWithIdempotencyAsync(
                                attemptUser.ID,
                                attemptAccount.ID,
                                goldDelta: 0,
                                silverDelta: 0,
                                plinkoDelta: entry.TicketCost,
                                transactionType: "TicketExchangeRefund",
                                idempotencyKey: refundKey,
                                referenceId: storeItem.PrefixedCode,
                                description: "Automatic rollback compensation after failed Ticket Exchange persistence",
                                ct: CancellationToken.None);
                        }
                        else
                        {
                            await EconomyService.RecordTransactionAsync(
                                attemptUser.ID,
                                attemptAccount.ID,
                                goldDelta: 0,
                                silverDelta: 0,
                                plinkoDelta: entry.TicketCost,
                                transactionType: "TicketExchangeRefund",
                                referenceId: requestIdempotencyKey,
                                description: "Automatic rollback compensation after failed Ticket Exchange persistence",
                                ct: CancellationToken.None);
                        }
                    }

                    throw;
                }
            }

            return concreteEconomy is not null
                ? await concreteEconomy.ExecuteInRepeatableReadTransactionAsync(ExecutePurchaseAttemptAsync, cancellationToken)
                : await ExecutePurchaseAttemptAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error executing ticket exchange purchase");
            return PurchaseFailureResponse(0);
        }
    }

    /// <summary>
    ///     Builds a purchase failure response with all eight required client fields.
    /// </summary>
    private static PhpResult PurchaseFailureResponse(int statusCode)
    {
        TicketExchangePurchaseResponseDTO response = BuildPurchaseResponse(statusCode, 0);
        return new PhpResult(response);
    }

    /// <summary>
    ///     Builds the full eight-field purchase response matching <c>plinko.package</c>'s result-param declaration.
    /// </summary>
    private static TicketExchangePurchaseResponseDTO BuildPurchaseResponse(int statusCode, int ticketsRemaining)
    {
        return new TicketExchangePurchaseResponseDTO
        {
            StatusCode = statusCode,
            TicketsRemaining = ticketsRemaining,
            GrabBag = "0",
            GrabBagTheme = string.Empty,
            GrabBagIDs = string.Empty,
            GrabBagTypes = string.Empty,
            GrabBagLocalPaths = string.Empty,
            GrabBagProductNames = string.Empty
        };
    }

    /// <summary>
    ///     Resolves the current request's session cookie to an <see cref="Account" /> with its <see cref="User" /> eagerly
    ///     loaded, or returns <see langword="null" /> if the cookie is missing, invalid, or does not correspond to an existing
    ///     account.
    /// </summary>
    private async Task<Account?> LoadAccountFromCookie(CancellationToken cancellationToken = default)
    {
        string cookie = Request.HasFormContentType
            ? Request.Form["cookie"].ToString()
            : (Request.GetCookie() ?? string.Empty);

        if (string.IsNullOrEmpty(cookie)) return null;

        (bool isValid, string? accountName) = await DistributedCache.ValidateAccountSessionCookie(cookie);

        if (isValid.Equals(false) || accountName is null)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    @"Ticket Exchange Request With Invalid Cookie ""{CookiePrefix}"" From ""{IPAddress}""",
                    SecurityMasking.MaskToken(cookie),
                    Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN");

            return null;
        }

        return await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);
    }
}
