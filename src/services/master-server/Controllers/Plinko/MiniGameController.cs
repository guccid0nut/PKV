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
///     Handles the three Plinko mini-game endpoints: the panel index (<c>/master/casino/</c>), a single drop (
///     <c>/master/casino/drop/</c>), and the paginated chest-browsing view (<c>/master/casino/viewchest/</c>).
/// </summary>
[ApiController]
[Route("master/casino")]
[Consumes("application/x-www-form-urlencoded")]
public class MiniGameController(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<MiniGameController> logger,
    IEconomyService? economyService = null,
    IDistributedLockService? distributedLockService = null) : ControllerBase
{
    private const int ViewChestPageSize = 56;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IEconomyService? EconomyService { get; } = economyService;
    private IDistributedLockService? DistributedLockService { get; } = distributedLockService;
    private ILogger Logger { get; } = logger;

    private static PlinkoConfiguration PlinkoConfiguration => JSONConfiguration.PlinkoConfiguration;
    private static PlinkoTierProductsConfiguration TierProducts => JSONConfiguration.PlinkoTierProductsConfiguration;
    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    /// <summary>
    ///     Returns the Plinko panel initial state: costs, the player's balances, the tier partition layout, and the
    ///     per-partition product counts used by the UI.
    /// </summary>
    [HttpPost("", Name = "Plinko Index Requester")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        Account? account = await LoadAccountFromCookie(cancellationToken);

        if (account is null) return new PhpResult(new PlinkoIndexResponseDTO { StatusCode = 0 });

        User user = account.User;

        // Per-Partition Enabled Product Counts, In The Visual Order Declared By The Partition Layout
        string amountOfProducts = string.Join(",", PlinkoConfiguration.TierPartitionOrder
            .Select(tierID => TierProducts.CountEnabledProducts(tierID, StoreItems).ToString()));

        PlinkoIndexResponseDTO response = new()
        {
            StatusCode = 1,
            Tiers = PlinkoConfiguration.TierPartitionOrder,
            TicketCost = PlinkoConfiguration.TicketCost,
            GoldCost = PlinkoConfiguration.GoldCost,
            UserGold = user.GoldCoins,
            Silver = user.SilverCoins,
            UserTickets = user.PlinkoTickets,
            AmountOfProducts = amountOfProducts,
            LastUpdateTime = PlinkoConfiguration.LastUpdateTimes
        };

        return new PhpResult(response);
    }

    /// <summary>
    ///     Executes a single Plinko drop, consuming either gold or tickets, rolling a weighted tier, and granting either a
    ///     cosmetic product from that tier's pool or a consolation ticket payout.
    /// </summary>
    [HttpPost("drop/", Name = "Plinko Drop Requester")]
    public async Task<IActionResult> Drop(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Account? account = await LoadAccountFromCookie(cancellationToken);

            if (account is null) return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 0 });

            string currency = Request.Form["currency"].ToString();

            if (currency is not "gold" and not "tickets")
                return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 4 });

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
                Logger.LogWarning("[Plinko] Timed out acquiring economy lock for User #{UserId} (Account #{AccountId}).",
                    user.ID, account.ID);
                return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 0 });
            }

            bool payingWithGold = currency.Equals("gold");

            int goldCost = payingWithGold ? PlinkoConfiguration.GoldCost : 0;
            int ticketCost = payingWithGold ? 0 : PlinkoConfiguration.TicketCost;
            string? rawClientKey = ExtractClientIdempotencyKey(Request);
            string requestIdempotencyKey = Services.Economy.EconomyService.NormalizeScopedIdempotencyKey("plinko-drop", account.ID, rawClientKey);
            int rolledTierId = GetRandomPlinkoTier();
            PlinkoDropOutcome? committedOutcomeSnapshot = null;

            EconomyService? concreteEconomy = EconomyService as EconomyService;
            bool isRelationalTx = concreteEconomy is { HasActiveRelationalDatabase: true };

            IActionResult BuildReplayedPlinkoDropResponse(User currentUser, EconomyTransaction committedTx)
            {
                int resolvedTier = rolledTierId;
                PlinkoDropOutcome? outcomeToReturn = committedOutcomeSnapshot;
                if (outcomeToReturn is null && TryReconstructOutcomeFromReference(committedTx.ReferenceId, out int parsedTier, out PlinkoDropOutcome? parsedOutcome))
                {
                    resolvedTier = parsedTier;
                    outcomeToReturn = parsedOutcome;
                }

                outcomeToReturn ??= new PlinkoDropOutcome
                {
                    ProductID = -1,
                    ProductName = "Ticket",
                    ProductPath = "Ticket",
                    ProductType = "Ticket",
                    TicketReward = 0,
                    ProductsExhausted = true
                };

                return new PhpResult(new PlinkoDropResponseDTO
                {
                    StatusCode = 1,
                    RandomTier = resolvedTier,
                    ProductID = outcomeToReturn.ProductID,
                    ProductName = outcomeToReturn.ProductName,
                    ProductPath = outcomeToReturn.ProductPath,
                    ProductType = outcomeToReturn.ProductType,
                    TicketAmount = outcomeToReturn.TicketReward,
                    UserTickets = currentUser.PlinkoTickets,
                    UserGold = currentUser.GoldCoins,
                    ProductsExhausted = outcomeToReturn.ProductsExhausted ? 1 : 0
                });
            }

            async Task<IActionResult> ExecuteDropAttemptAsync(int attempt, CancellationToken ct)
            {
                Account? attemptAccount = attempt == 0 ? account : await LoadAccountFromCookie(ct);
                if (attemptAccount?.User is null)
                    return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 0 });

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
                            "[Plinko] Idempotent PlinkoDrop replay detected for Account #{AccountId} (Attempt=#{Attempt}, IdempotencyKey={IdempotencyKey}); returning committed outcome without duplicate charge or grant.",
                            attemptAccount.ID, attempt + 1, requestIdempotencyKey);

                        return BuildReplayedPlinkoDropResponse(attemptUser, alreadyCommitted);
                    }
                }

                if (payingWithGold && attemptUser.GoldCoins < PlinkoConfiguration.GoldCost)
                    return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 3 });

                if (!payingWithGold && attemptUser.PlinkoTickets < PlinkoConfiguration.TicketCost)
                    return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 2 });

                int originalGold = attemptUser.GoldCoins;
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
                                goldCost,
                                0,
                                ticketCost,
                                "PlinkoDrop",
                                idempotencyKey: requestIdempotencyKey,
                                referenceId: $"tier:{rolledTierId}",
                                description: $"Plinko drop paying with {(payingWithGold ? "gold" : "tickets")}",
                                ct);

                        if (!deductResult.Success)
                            return new PhpResult(new PlinkoDropResponseDTO { StatusCode = payingWithGold ? 3 : 2 });

                        if (replayedTx is not null)
                        {
                            Logger.LogWarning(
                                "[Plinko] Concurrent idempotency replay detected during DeductCurrencies for Account #{AccountId} (IdempotencyKey={IdempotencyKey}); skipping duplicate reward grant.",
                                attemptAccount.ID, requestIdempotencyKey);
                            return BuildReplayedPlinkoDropResponse(attemptUser, replayedTx);
                        }

                        economyDeducted = true;
                        attemptUser.GoldCoins = deductResult.GoldBalanceAfter;
                        attemptUser.PlinkoTickets = deductResult.PlinkoTicketsBalanceAfter;
                    }
                    else if (EconomyService is not null)
                    {
                        CurrencyRewardResult deductResult = await EconomyService.DeductCurrenciesAsync(
                            attemptUser.ID,
                            attemptAccount.ID,
                            goldCost,
                            0,
                            ticketCost,
                            "PlinkoDrop",
                            referenceId: requestIdempotencyKey,
                            description: $"Plinko drop paying with {(payingWithGold ? "gold" : "tickets")}",
                            ct);

                        if (!deductResult.Success)
                            return new PhpResult(new PlinkoDropResponseDTO { StatusCode = payingWithGold ? 3 : 2 });

                        economyDeducted = true;
                        attemptUser.GoldCoins = deductResult.GoldBalanceAfter;
                        attemptUser.PlinkoTickets = deductResult.PlinkoTicketsBalanceAfter;
                    }
                    else if (payingWithGold)
                    {
                        attemptUser.GoldCoins -= PlinkoConfiguration.GoldCost;
                    }
                    else
                    {
                        attemptUser.PlinkoTickets -= PlinkoConfiguration.TicketCost;
                    }

                    PlinkoDropOutcome outcome = rolledTierId switch
                    {
                        1 or 2 or 3 or 4 => ResolveChestDrop(attemptUser, rolledTierId),
                        5 or 6 => ResolveTicketTierDrop(rolledTierId),
                        _ => throw new InvalidOperationException($@"Rolled Tier ID ""{rolledTierId}"" Is Outside The Supported Range 1-6")
                    };

                    committedOutcomeSnapshot = outcome;
                    attemptUser.PlinkoTickets += outcome.TicketReward;

                    await AccountRepository.UpdateUserAsync(attemptUser, ct);

                    if (concreteEconomy is not null)
                    {
                        await concreteEconomy.SetTransactionReferenceByIdempotencyKeyAsync(
                            attemptUser.ID,
                            requestIdempotencyKey,
                            FormatPlinkoOutcomeReference(rolledTierId, outcome),
                            ct);
                    }

                    if (Logger.IsEnabled(LogLevel.Information))
                        Logger.LogInformation(
                            @"Plinko Drop For Account ""{AccountID}"" Rolled Tier ""{TierID}"" (Currency ""{Currency}"", Product ""{ProductID}"", Ticket Delta ""{TicketDelta}"", IdempotencyKey ""{IdempotencyKey}"")",
                            attemptAccount.ID, rolledTierId, currency, outcome.ProductID, outcome.TicketReward, requestIdempotencyKey);

                    PlinkoDropResponseDTO response = new()
                    {
                        StatusCode = 1,
                        RandomTier = rolledTierId,
                        ProductID = outcome.ProductID,
                        ProductName = outcome.ProductName,
                        ProductPath = outcome.ProductPath,
                        ProductType = outcome.ProductType,
                        TicketAmount = outcome.TicketReward,
                        UserTickets = attemptUser.PlinkoTickets,
                        UserGold = attemptUser.GoldCoins,
                        ProductsExhausted = outcome.ProductsExhausted ? 1 : 0
                    };

                    return new PhpResult(response);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    attemptUser.GoldCoins = originalGold;
                    attemptUser.PlinkoTickets = originalTickets;

                    if (!isRelationalTx && economyDeducted && EconomyService is not null)
                    {
                        concreteEconomy?.ClearTrackedChanges();
                        Logger.LogWarning(ex,
                            "[Plinko] Persistence failed after currency deduction for User #{UserId} (Account #{AccountId}); issuing compensating refund (+{Gold}G, +{Tickets}T).",
                            attemptUser.ID, attemptAccount.ID, goldCost, ticketCost);

                        if (concreteEconomy is not null)
                        {
                            string refundKey = requestIdempotencyKey.Length <= 120
                                ? $"{requestIdempotencyKey}:refund"
                                : $"{requestIdempotencyKey[..120]}:refund";
                            await concreteEconomy.RecordTransactionWithIdempotencyAsync(
                                attemptUser.ID,
                                attemptAccount.ID,
                                goldCost,
                                0,
                                ticketCost,
                                "PlinkoDropRefund",
                                idempotencyKey: refundKey,
                                referenceId: $"tier:{rolledTierId}",
                                description: "Automatic rollback compensation after failed Plinko reward persistence",
                                CancellationToken.None);
                        }
                        else
                        {
                            await EconomyService.RecordTransactionAsync(
                                attemptUser.ID,
                                attemptAccount.ID,
                                goldCost,
                                0,
                                ticketCost,
                                "PlinkoDropRefund",
                                referenceId: requestIdempotencyKey,
                                description: "Automatic rollback compensation after failed Plinko reward persistence",
                                CancellationToken.None);
                        }
                    }

                    throw;
                }
            }

            return concreteEconomy is not null
                ? await concreteEconomy.ExecuteInRepeatableReadTransactionAsync(ExecuteDropAttemptAsync, cancellationToken)
                : await ExecuteDropAttemptAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error executing Plinko drop");
            return new PhpResult(new PlinkoDropResponseDTO { StatusCode = 0 });
        }
    }

    /// <summary>
    ///     Returns a paginated slice of the products available in a chest tier.
    ///     Used by the client's "browse chest contents" UI. Tiers 5 and 6 are ticket-only and are rejected.
    /// </summary>
    [HttpPost("viewchest/", Name = "Plinko View Chest Requester")]
    public async Task<IActionResult> ViewChest(CancellationToken cancellationToken = default)
    {
        Account? account = await LoadAccountFromCookie(cancellationToken);

        if (account is null || int.TryParse(Request.Form["tier_id"], out int tierID).Equals(false) ||
            tierID is < 1 or > 4)
            return new PhpResult(new PlinkoViewChestResponseDTO { StatusCode = 0 });

        int targetIndex = 1;

        if (int.TryParse(Request.Form["target_index"], out int parsedTargetIndex))
            targetIndex = Math.Max(parsedTargetIndex, 1);

        IReadOnlyList<int> productIDs = TierProducts.GetProductIDs(tierID);

        List<StoreItem> page =
        [
            .. productIDs
                .Skip(targetIndex - 1)
                .Take(ViewChestPageSize)
                .Select(StoreItems.GetByID)
                .Where(storeItem => storeItem is { Purchasable: true, IsEnabled: true })
                .Select(storeItem => storeItem!)
        ];

        PlinkoViewChestResponseDTO response = new()
        {
            StatusCode = 1,
            TierID = tierID,
            TargetIndex = targetIndex,
            FirstItemIndex = targetIndex,
            ItemsAmount = page.Count,
            ProductNames = string.Join(",", page.Select(storeItem => storeItem.Code)),
            ProductTypes = string.Join(",", page.Select(storeItem => storeItem.TypeCode)),
            ProductPaths = string.Join(",", page.Select(storeItem => storeItem.Resource)),
            ProductIDs = string.Join(",", page.Select(storeItem => storeItem.ID.ToString()))
        };

        return new PhpResult(response);
    }

    /// <summary>
    ///     Rolls a weighted tier using the injected random source, matching the legacy probability distribution.
    /// </summary>
    private int GetRandomPlinkoTier()
    {
        double roll = Random.Shared.NextDouble() * 100.0;

        double cumulative = PlinkoConfiguration.DropProbabilities.Tier1;
        if (roll < cumulative) return 1;

        cumulative += PlinkoConfiguration.DropProbabilities.Tier2;
        if (roll < cumulative) return 2;

        cumulative += PlinkoConfiguration.DropProbabilities.Tier3;
        if (roll < cumulative) return 3;

        cumulative += PlinkoConfiguration.DropProbabilities.Tier4;
        if (roll < cumulative) return 4;

        cumulative += PlinkoConfiguration.DropProbabilities.Tier5;
        return roll < cumulative ? 5 : 6;
    }

    /// <summary>
    ///     Resolves a chest-tier drop: filters the tier's products against what the user already owns and what is disabled,
    ///     then either grants a uniformly random eligible product or, if none remain, falls back to the configured exhaustion
    ///     ticket reward.
    /// </summary>
    private PlinkoDropOutcome ResolveChestDrop(User user, int tierID)
    {
        HashSet<string> ownedPrefixedCodes = user.InventoryItems != null
            ? new HashSet<string>(user.InventoryItems.Select(i => i.ItemCode), StringComparer.OrdinalIgnoreCase)
            : [];

        List<StoreItem> eligible = [];

        foreach (int productID in TierProducts.GetProductIDs(tierID))
        {
            StoreItem? storeItem = StoreItems.GetByID(productID);

            if (storeItem is null)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                    Logger.LogWarning(@"Plinko Tier ""{TierID}"" References Unknown Product ID ""{ProductID}""", tierID,
                        productID);

                continue;
            }

            if (storeItem.IsEnabled.Equals(false) || storeItem.Purchasable.Equals(false)) continue;

            if (ownedPrefixedCodes.Contains(storeItem.PrefixedCode)) continue;

            eligible.Add(storeItem);
        }

        if (eligible.Count.Equals(0))
            return new PlinkoDropOutcome
            {
                ProductID = -1,
                ProductName = "Ticket",
                ProductPath = "Ticket",
                ProductType = "Ticket",
                TicketReward = PlinkoConfiguration.GetExhaustionTicketReward(tierID),
                ProductsExhausted = true
            };

        StoreItem winner = eligible[Random.Shared.Next(eligible.Count)];

        user.InventoryItems ??= [];
        user.InventoryItems.Add(new UserInventory
        {
            UserID = user.ID,
            ItemCode = winner.PrefixedCode,
            StoreItemId = null,
            Quantity = 1,
            IsPermanent = true,
            TimestampAcquired = TimeProvider.System.GetUtcNow(),
            Source = "PlinkoChestDrop"
        });

        return new PlinkoDropOutcome
        {
            ProductID = winner.ID,
            ProductName = winner.Code,
            ProductPath = winner.Resource,
            ProductType = StoreItem.GetTypeName(winner.StoreItemType),
            TicketReward = 0,
            ProductsExhausted = false
        };
    }

    /// <summary>
    ///     Resolves a ticket-tier drop: always grants the configured consolation tickets and never mutates the user's
    ///     inventory.
    /// </summary>
    private static PlinkoDropOutcome ResolveTicketTierDrop(int tierID)
    {
        return new PlinkoDropOutcome
        {
            ProductID = -1,
            ProductName = "Ticket",
            ProductPath = "Ticket",
            ProductType = "Ticket",
            TicketReward = PlinkoConfiguration.GetConsolationTicketReward(tierID),
            ProductsExhausted = true
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
                Logger.LogWarning(@"Plinko Request With Invalid Cookie ""{CookiePrefix}"" From ""{IPAddress}""",
                    SecurityMasking.MaskToken(cookie),
                    Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN");

            return null;
        }

        return await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);
    }

    internal static string? ExtractClientIdempotencyKey(HttpRequest? request)
    {
        if (request is null) return null;

        if (request.HasFormContentType)
        {
            string formReqId = request.Form["request_id"].ToString();
            if (!string.IsNullOrWhiteSpace(formReqId)) return formReqId;

            string formIdem = request.Form["idempotency_key"].ToString();
            if (!string.IsNullOrWhiteSpace(formIdem)) return formIdem;
        }

        string? headerKey = request.Headers["Idempotency-Key"].FirstOrDefault()
                            ?? request.Headers["X-Idempotency-Key"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(headerKey) ? null : headerKey;
    }

    private static string FormatPlinkoOutcomeReference(int tierId, PlinkoDropOutcome outcome) =>
        $"tier:{tierId}|pid:{outcome.ProductID}|tix:{outcome.TicketReward}|exh:{(outcome.ProductsExhausted ? 1 : 0)}";

    private bool TryReconstructOutcomeFromReference(string? referenceId, out int tierId, out PlinkoDropOutcome? outcome)
    {
        tierId = 1;
        outcome = null;
        if (string.IsNullOrWhiteSpace(referenceId) || !referenceId.StartsWith("tier:", StringComparison.Ordinal))
        {
            return false;
        }

        int productId = -1;
        int ticketReward = 0;
        bool productsExhausted = false;
        bool hasPid = false;

        foreach (string part in referenceId.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int sep = part.IndexOf(':');
            if (sep <= 0) continue;
            ReadOnlySpan<char> k = part.AsSpan(0, sep);
            ReadOnlySpan<char> v = part.AsSpan(sep + 1);
            if (k.SequenceEqual("tier") && int.TryParse(v, out int parsedTier))
            {
                tierId = parsedTier;
            }
            else if (k.SequenceEqual("pid") && int.TryParse(v, out int parsedPid))
            {
                productId = parsedPid;
                hasPid = true;
            }
            else if (k.SequenceEqual("tix") && int.TryParse(v, out int parsedTix))
            {
                ticketReward = parsedTix;
            }
            else if (k.SequenceEqual("exh") && int.TryParse(v, out int parsedExh))
            {
                productsExhausted = parsedExh != 0;
            }
        }

        if (!hasPid)
        {
            return false;
        }

        if (productId <= 0)
        {
            outcome = new PlinkoDropOutcome
            {
                ProductID = -1,
                ProductName = "Ticket",
                ProductPath = "Ticket",
                ProductType = "Ticket",
                TicketReward = ticketReward,
                ProductsExhausted = productsExhausted
            };
            return true;
        }

        StoreItem? winner = StoreItems.GetByID(productId);
        outcome = new PlinkoDropOutcome
        {
            ProductID = productId,
            ProductName = winner?.Code ?? string.Empty,
            ProductPath = winner?.Resource ?? string.Empty,
            ProductType = winner is not null ? StoreItem.GetTypeName(winner.StoreItemType) : string.Empty,
            TicketReward = ticketReward,
            ProductsExhausted = productsExhausted
        };
        return true;
    }
}

/// <summary>
///     The internal outcome of resolving a Plinko drop.
/// </summary>
internal sealed record PlinkoDropOutcome
{
    public required int ProductID { get; init; }
    public required string ProductName { get; init; }
    public required string ProductPath { get; init; }
    public required string ProductType { get; init; }
    public required int TicketReward { get; init; }
    public required bool ProductsExhausted { get; init; }
}
