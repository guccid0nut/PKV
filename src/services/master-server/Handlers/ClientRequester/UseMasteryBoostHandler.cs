// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Helpers.Stats;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class UseMasteryBoostHandler(
    IAccountRepository accountRepository,
    IMatchHistoryRepository matchHistoryRepository,
    ISessionRepository sessionRepository,
    ILogger<UseMasteryBoostHandler>? logger = null,
    MerrickContext? db = null,
    INexusHybridCache? hybridCache = null) : IClientRequestHandler
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private ISessionRepository SessionRepository { get; } = sessionRepository;
    private ILogger<UseMasteryBoostHandler> Logger { get; } = logger ?? NullLogger<UseMasteryBoostHandler>.Instance;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(request);

        string matchIdStr = request.GetFormOrQueryValue("match_id") ?? string.Empty;
        if (string.IsNullOrEmpty(matchIdStr))
            matchIdStr = request.GetFormOrQueryValue("matchid") ?? string.Empty;

        string isSuperStr = request.GetFormOrQueryValue("is_super") ?? string.Empty;

        bool isSuper = string.Equals(isSuperStr, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(isSuperStr, "1", StringComparison.Ordinal);

        string? sessionAccountName = context.Items["SessionAccountName"] as string;

        if (string.IsNullOrWhiteSpace(sessionAccountName) && !string.IsNullOrWhiteSpace(cookie))
        {
            string cleanCookie = cookie.Replace("-", string.Empty);
            sessionAccountName =
                await SessionRepository.GetAccountNameForSessionCookieAsync(cleanCookie, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(sessionAccountName))
        {
            Logger.LogWarning("[MasteryBoost] Rejected unauthenticated use_mastery_boost request (matchId='{MatchIdStr}')", matchIdStr);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "UNAUTHORIZED" },
                contentType: "text/plain; charset=utf-8");
        }

        string nickname = sessionAccountName.Trim();

        if (!int.TryParse(matchIdStr, out int matchId) || matchId <= 0)
        {
            Logger.LogWarning("[MasteryBoost] Rejected invalid match_id '{MatchIdStr}' for account '{AccountName}'", matchIdStr, nickname);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "INVALID_MATCH" },
                contentType: "text/plain; charset=utf-8");
        }

        MerrickContext? effectiveDb = db
                                      ?? context.RequestServices?.GetService<MerrickContext>()
                                      ?? (AccountRepository as AccountRepository)?.DbContext;

        INexusHybridCache? effectiveCache = hybridCache ?? context.RequestServices?.GetService<INexusHybridCache>();

        if (effectiveDb is null)
        {
            return await HandleLegacyRepositoryPathAsync(nickname, matchId, isSuper, effectiveCache, cancellationToken);
        }

        IExecutionStrategy strategy = effectiveDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            const int maxContentionAttempts = 5;
            for (int attempt = 0; attempt < maxContentionAttempts; attempt++)
            {
                IActionResult? attemptResult = await ExecuteRedeemAttemptAsync(
                    effectiveDb, effectiveCache, nickname, matchId, isSuper, attempt, maxContentionAttempts,
                    cancellationToken);
                if (attemptResult != null) return attemptResult;
            }

            Logger.LogError(
                "[MasteryBoost] Exhausted {MaxAttempts} contention retries redeeming boost for account '{AccountName}' Match #{MatchId}",
                maxContentionAttempts, nickname, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "RETRY_CONFLICT" },
                contentType: "text/plain; charset=utf-8");
        });
    }

    private async Task<IActionResult?> ExecuteRedeemAttemptAsync(
        MerrickContext effectiveDb,
        INexusHybridCache? effectiveCache,
        string nickname,
        int matchId,
        bool isSuper,
        int attempt,
        int maxContentionAttempts,
        CancellationToken cancellationToken)
    {
        string matchClaimCode = $"mb.claimed_match:{matchId}";
        string legacyMatchClaimCode = $"mboost_claimed:{matchId}";

        await using IDbContextTransaction? tx =
            effectiveDb.Database.IsRelational() && effectiveDb.Database.CurrentTransaction is null
                ? await effectiveDb.Database.BeginTransactionAsync(cancellationToken)
                : null;

        effectiveDb.ChangeTracker.Clear();

        Account? account = await LoadTrackedAccountAsync(effectiveDb, nickname, cancellationToken);
        if (account is null || account.User is null)
        {
            Logger.LogWarning("[MasteryBoost] Account not found for authenticated nickname '{AccountName}' (matchId={MatchId})", nickname, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "UNAUTHORIZED" },
                contentType: "text/plain; charset=utf-8");
        }

        User user = account.User;
        user.InventoryItems ??= [];

        if (MasteryConsumables.HasClaimedMatchBoost(user, matchId))
        {
            Logger.LogWarning("[MasteryBoost] Blocked duplicate mastery boost redemption for account '{AccountName}' on Match #{MatchId}", account.Name, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "ALREADY_CLAIMED" },
                contentType: "text/plain; charset=utf-8");
        }

        if (MasteryConsumables.GetTotalMasteryBoostsOwned(user, isSuper) <= 0)
        {
            Logger.LogWarning(
                "[MasteryBoost] Rejected mastery boost redemption for account '{AccountName}' on Match #{MatchId}: no owned boost (isSuper={IsSuper})",
                account.Name, matchId, isSuper);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "NO_BOOST_OWNED" },
                contentType: "text/plain; charset=utf-8");
        }

        PlayerStatistics? playerMatchStats =
            await effectiveDb.PlayerStatistics
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.MatchID == matchId && p.AccountID == account.ID, cancellationToken)
            ?? await MatchHistoryRepository.GetPlayerStatisticsAsync(matchId, account.ID, cancellationToken);

        if (playerMatchStats is null || string.IsNullOrEmpty(playerMatchStats.HeroIdentifier))
        {
            Logger.LogWarning("[MasteryBoost] Rejected mastery boost redemption for account '{AccountName}' on Match #{MatchId}: player match stats not found", account.Name, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "MATCH_NOT_FOUND" },
                contentType: "text/plain; charset=utf-8");
        }

        if (!MasteryConsumables.TryConsumeOneBoostWithTarget(user, isSuper,
                out List<MasteryConsumables.BoostRowMutation> boostMutations))
        {
            Logger.LogWarning(
                "[MasteryBoost] Failed to consume mastery boost for account '{AccountName}' on Match #{MatchId} (isSuper={IsSuper})",
                account.Name, matchId, isSuper);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "NO_BOOST_OWNED" },
                contentType: "text/plain; charset=utf-8");
        }

        user.InventoryItems.Add(new UserInventory
        {
            UserID = user.ID,
            AccountID = account.ID,
            ItemCode = matchClaimCode,
            Quantity = 1,
            IsPermanent = true,
            TimestampAcquired = TimeProvider.System.GetUtcNow(),
            Source = "MasteryBoostRedeem"
        });

        try
        {
            bool casConflict = false;
            if (effectiveDb.Database.IsRelational())
            {
                foreach (MasteryConsumables.BoostRowMutation mutation in boostMutations)
                {
                    if (mutation.Row.ID > 0)
                    {
                        string expectedOrigCode = mutation.OriginalItemCode;
                        int expectedOrigQty = mutation.OriginalQuantity;
                        int targetRowId = mutation.Row.ID;

                        if (mutation.IsRemoved)
                        {
                            int casDeleted = await effectiveDb.UserInventories
                                .Where(i => i.ID == targetRowId &&
                                            i.ItemCode == expectedOrigCode &&
                                            i.Quantity == expectedOrigQty)
                                .ExecuteDeleteAsync(cancellationToken);

                            if (casDeleted == 0)
                            {
                                casConflict = true;
                                break;
                            }

                            effectiveDb.Entry(mutation.Row).State = EntityState.Detached;
                        }
                        else
                        {
                            string newCode = mutation.Row.ItemCode;
                            int newQty = mutation.Row.Quantity;
                            int casUpdated = await effectiveDb.UserInventories
                                .Where(i => i.ID == targetRowId &&
                                            i.ItemCode == expectedOrigCode &&
                                            i.Quantity == expectedOrigQty)
                                .ExecuteUpdateAsync(setters => setters
                                        .SetProperty(i => i.ItemCode, newCode)
                                        .SetProperty(i => i.Quantity, newQty),
                                    cancellationToken);

                            if (casUpdated == 0)
                            {
                                casConflict = true;
                                break;
                            }

                            effectiveDb.Entry(mutation.Row).State = EntityState.Unchanged;
                        }
                    }
                    else if (mutation.IsRemoved)
                    {
                        effectiveDb.UserInventories.Remove(mutation.Row);
                    }
                }
            }
            else
            {
                foreach (MasteryConsumables.BoostRowMutation mutation in boostMutations)
                {
                    if (mutation.IsRemoved)
                    {
                        effectiveDb.UserInventories.Remove(mutation.Row);
                    }
                }
            }

            if (casConflict)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                effectiveDb.ChangeTracker.Clear();
                Logger.LogWarning(
                    "[MasteryBoost] CAS contention consuming boost for account '{AccountName}' Match #{MatchId} (attempt {Attempt}/{Max}); retrying",
                    account.Name, matchId, attempt + 1, maxContentionAttempts);
                return null;
            }

            await effectiveDb.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when ((ex is DbUpdateException dbEx && IsInventoryUniqueViolation(dbEx)) ||
                                   ex is DbUpdateConcurrencyException)
        {
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            effectiveDb.ChangeTracker.Clear();

            bool matchAlreadyClaimed = await effectiveDb.UserInventories
                .AsNoTracking()
                .AnyAsync(i => i.UserID == user.ID &&
                               (i.ItemCode == matchClaimCode || i.ItemCode == legacyMatchClaimCode),
                    cancellationToken);

            if (!matchAlreadyClaimed && attempt + 1 < maxContentionAttempts)
            {
                Logger.LogWarning(
                    ex,
                    "[MasteryBoost] Boost inventory contention for account '{AccountName}' Match #{MatchId} (attempt {Attempt}/{Max}); refreshing tracked rows",
                    account.Name, matchId, attempt + 1, maxContentionAttempts);
                return null;
            }

            Logger.LogWarning(
                ex,
                "[MasteryBoost] Concurrent duplicate boost redemption blocked by UQ_UserInventory_UserID_ItemCode for account '{AccountName}' on Match #{MatchId}",
                account.Name, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "ALREADY_CLAIMED" },
                contentType: "text/plain; charset=utf-8");
        }

        int currentExp = account.Mastery?.GetHeroExperienceByHeroIdentifier(playerMatchStats.HeroIdentifier) ?? 0;
        int boostExp = isSuper ? 1000 : 240;
        int updatedExp = currentExp + boostExp;

        Mastery masteryTarget = account.Mastery ?? new Mastery { Account = account, AccountID = account.ID };
        masteryTarget.SetHeroExperienceByHeroIdentifier(playerMatchStats.HeroIdentifier, updatedExp);

        await MatchHistoryRepository.SaveMasteryAsync(masteryTarget, cancellationToken);

        int updatedTotalLevel = masteryTarget.TotalMasteryLevel();
        user.TotalLevel = updatedTotalLevel;
        if (effectiveDb.Entry(user).State == EntityState.Detached)
        {
            effectiveDb.Attach(user);
        }
        effectiveDb.Entry(user).Property(u => u.TotalLevel).IsModified = true;
        await effectiveDb.SaveChangesAsync(cancellationToken);

        if (tx != null) await tx.CommitAsync(cancellationToken);

        if (effectiveCache != null)
        {
            await effectiveCache.RemoveByTagAsync($"account:{account.ID}", cancellationToken);
            await effectiveCache.RemoveByTagAsync(
                $"account:nick:{account.Name.Trim().ToLowerInvariant()}", cancellationToken);
            await effectiveCache.RemoveAsync($"match:stats:{matchId}:{account.ID}", cancellationToken);
            Logger.LogInformation(
                "[MasteryBoost] Invalidated hybrid cache tags and match stats key for account {AccountId} ('{AccountName}') Match #{MatchId}",
                account.ID, account.Name, matchId);
        }

        Logger.LogInformation(
            "[MasteryBoost] Atomically redeemed boost (isSuper={IsSuper}) for account '{AccountName}' Match #{MatchId}: granted +{BoostExp} XP on '{HeroIdentifier}' (newTotalLevel={TotalLevel})",
            isSuper, account.Name, matchId, boostExp, playerMatchStats.HeroIdentifier, updatedTotalLevel);

        Dictionary<string, object> response = new() { ["0"] = 0, ["success"] = "1", ["status"] = "OK" };
        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleLegacyRepositoryPathAsync(
        string nickname,
        int matchId,
        bool isSuper,
        INexusHybridCache? effectiveCache,
        CancellationToken cancellationToken)
    {
        Account? account = await AccountRepository.GetByNicknameAsync(nickname, cancellationToken);
        if (account is null)
        {
            Logger.LogWarning("[MasteryBoost] Account not found for authenticated nickname '{AccountName}' (matchId={MatchId})", nickname, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "UNAUTHORIZED" },
                contentType: "text/plain; charset=utf-8");
        }

        account.User.InventoryItems ??= [];
        if (MasteryConsumables.HasClaimedMatchBoost(account.User, matchId))
        {
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "ALREADY_CLAIMED" },
                contentType: "text/plain; charset=utf-8");
        }

        if (MasteryConsumables.GetTotalMasteryBoostsOwned(account.User, isSuper) <= 0)
        {
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "NO_BOOST_OWNED" },
                contentType: "text/plain; charset=utf-8");
        }

        PlayerStatistics? playerMatchStats =
            await MatchHistoryRepository.GetPlayerStatisticsAsync(matchId, account.ID, cancellationToken);
        if (playerMatchStats is null || string.IsNullOrEmpty(playerMatchStats.HeroIdentifier))
        {
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "MATCH_NOT_FOUND" },
                contentType: "text/plain; charset=utf-8");
        }

        if (!MasteryConsumables.TryConsumeOneBoost(account.User, isSuper, out _))
        {
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "NO_BOOST_OWNED" },
                contentType: "text/plain; charset=utf-8");
        }

        account.User.InventoryItems.Add(new UserInventory
        {
            UserID = account.User.ID,
            AccountID = account.ID,
            ItemCode = $"mb.claimed_match:{matchId}",
            Quantity = 1,
            IsPermanent = true,
            TimestampAcquired = TimeProvider.System.GetUtcNow(),
            Source = "MasteryBoostRedeem"
        });

        try
        {
            await AccountRepository.UpdateAsync(account, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            Logger.LogWarning(ex, "[MasteryBoost] Concurrent duplicate boost redemption blocked for account '{AccountName}' on Match #{MatchId}", account.Name, matchId);
            return new PhpResult(
                new Dictionary<string, object> { ["0"] = 1, ["success"] = "0", ["status"] = "ALREADY_CLAIMED" },
                contentType: "text/plain; charset=utf-8");
        }

        account.Mastery ??= new Mastery { Account = account, AccountID = account.ID };
        int currentExp = account.Mastery.GetHeroExperienceByHeroIdentifier(playerMatchStats.HeroIdentifier);
        int boostExp = isSuper ? 1000 : 240;
        account.Mastery.SetHeroExperienceByHeroIdentifier(playerMatchStats.HeroIdentifier, currentExp + boostExp);
        account.User.TotalLevel = account.Mastery.TotalMasteryLevel();

        await MatchHistoryRepository.SaveMasteryAsync(account.Mastery, cancellationToken);

        if (effectiveCache != null)
        {
            await effectiveCache.RemoveByTagAsync($"account:{account.ID}", cancellationToken);
            await effectiveCache.RemoveByTagAsync(
                $"account:nick:{account.Name.Trim().ToLowerInvariant()}", cancellationToken);
            await effectiveCache.RemoveAsync($"match:stats:{matchId}:{account.ID}", cancellationToken);
        }

        return new PhpResult(new Dictionary<string, object> { ["0"] = 0, ["success"] = "1", ["status"] = "OK" },
            contentType: "text/plain; charset=utf-8");
    }

    private static async Task<Account?> LoadTrackedAccountAsync(
        MerrickContext effectiveDb,
        string nickname,
        CancellationToken cancellationToken)
    {
        if (effectiveDb.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = nickname.ToLowerInvariant();
            return await effectiveDb.Accounts
                .AsSplitQuery()
                .Include(a => a.User).ThenInclude(u => u.InventoryItems)
                .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
                .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, cancellationToken);
        }

        return await effectiveDb.Accounts
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.InventoryItems)
            .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
            .FirstOrDefaultAsync(a => a.Name == nickname, cancellationToken);
    }

    private static bool IsInventoryUniqueViolation(DbUpdateException ex)
    {
        string msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("UQ_UserInventory_UserID_ItemCode", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }
}
