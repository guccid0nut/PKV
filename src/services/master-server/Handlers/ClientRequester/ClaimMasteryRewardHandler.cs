// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Globalization;
using ASPIRE.Common.Caching;
using KONGOR.MasterServer.Helpers.Stats;
using KONGOR.MasterServer.Logging;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class ClaimMasteryRewardHandler(
    IAccountRepository accountRepository,
    ILogger<ClaimMasteryRewardHandler> logger,
    MerrickContext? db = null,
    INexusHybridCache? hybridCache = null)
    : IClientRequestHandler
{
    public string FunctionName => "claim_mastery_reward";

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(request);
        string? requestedNickname = GetRequestValue(request, "nickname");
        string? levelStr = GetRequestValue(request, "level")
                           ?? GetRequestValue(request, "level_id")
                           ?? GetRequestValue(request, "reward_level");

        logger.LogProcessingRequest(FunctionName, SecurityMasking.MaskToken(cookie), true);

        string? sessionNickname = context.Items["SessionAccountName"] as string;
        if (string.IsNullOrWhiteSpace(sessionNickname) && !string.IsNullOrWhiteSpace(cookie))
        {
            ISessionRepository? sessionRepo = context.RequestServices?.GetService<ISessionRepository>();
            if (sessionRepo != null)
            {
                sessionNickname = await sessionRepo.GetAccountNameForSessionCookieAsync(cookie.Replace("-", string.Empty), cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(sessionNickname))
            {
                StackExchange.Redis.IDatabase? cache = context.RequestServices?.GetService<IConnectionMultiplexer>()?.GetDatabase();
                if (cache != null) sessionNickname = await cache.GetAccountNameForSessionCookie(cookie);
            }
        }

        if (string.IsNullOrWhiteSpace(sessionNickname))
        {
            logger.LogWarning("[Mastery Event] Rejected unauthenticated claim_mastery_reward request (requestedNickname='{RequestedNickname}')", requestedNickname ?? string.Empty);
            return new UnauthorizedObjectResult("Invalid or missing session");
        }

        if (!string.IsNullOrWhiteSpace(requestedNickname) &&
            !string.Equals(requestedNickname.Trim(), sessionNickname.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "[Mastery Event] Cross-account IDOR attempt blocked: session '{SessionAccount}' attempted to claim mastery reward for '{RequestedNickname}'",
                sessionNickname, requestedNickname);
            return new UnauthorizedObjectResult("Session account mismatch");
        }

        string nickname = sessionNickname.Trim();
        MerrickContext? effectiveDb = db
                                      ?? context.RequestServices?.GetService<MerrickContext>()
                                      ?? (accountRepository as AccountRepository)?.DbContext;

        INexusHybridCache? effectiveCache = hybridCache ?? context.RequestServices?.GetService<INexusHybridCache>();

        if (effectiveDb is null)
        {
            return await HandleLegacyRepositoryPathAsync(nickname, levelStr, effectiveCache, cancellationToken);
        }

        IExecutionStrategy strategy = effectiveDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            const int maxContentionAttempts = 5;
            for (int attempt = 0; attempt < maxContentionAttempts; attempt++)
            {
                IActionResult? attemptResult = await ExecuteClaimAttemptAsync(
                    effectiveDb, effectiveCache, nickname, levelStr, attempt, maxContentionAttempts,
                    cancellationToken);
                if (attemptResult != null) return attemptResult;
            }

            logger.LogError(
                "[Mastery Event] Exhausted {MaxAttempts} contention retries claiming mastery Level {Level} for '{Nickname}'",
                maxContentionAttempts, levelStr ?? string.Empty, nickname);
            return BuildInvalidLevelResult("Concurrent inventory update contention; please retry");
        });
    }

    private async Task<IActionResult?> ExecuteClaimAttemptAsync(
        MerrickContext effectiveDb,
        INexusHybridCache? effectiveCache,
        string nickname,
        string? levelStr,
        int attempt,
        int maxContentionAttempts,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction? tx =
            effectiveDb.Database.IsRelational() && effectiveDb.Database.CurrentTransaction is null
                ? await effectiveDb.Database.BeginTransactionAsync(cancellationToken)
                : null;

        effectiveDb.ChangeTracker.Clear();

        Account? account = await LoadTrackedAccountAsync(effectiveDb, nickname, cancellationToken);
        if (account?.User == null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Account not found for nickname: {Nickname}", nickname);

            return new PhpResult(new Dictionary<object, object>(), contentType: "text/html; charset=utf-8");
        }

        if (!int.TryParse(levelStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) ||
            level < 0)
        {
            logger.LogWarning("[Mastery Event] Invalid mastery reward level '{LevelStr}' for '{Nickname}'",
                levelStr ?? string.Empty, nickname);
            return BuildInvalidLevelResult();
        }

        int earnedTotalLevel = Math.Max(
            account.Mastery?.TotalMasteryLevel() ?? 0,
            account.User?.TotalLevel ?? 0);

        MasteryRewardsConfiguration config = JSONConfiguration.MasteryRewardsConfiguration;
        List<string> ownedCodes = account.User?.InventoryItems?.Select(i => i.ItemCode).ToList() ?? [];

        MasteryClaimResult claimResult =
            MasteryRewardCalculator.EvaluateClaim(config, level, ownedCodes, earnedTotalLevel);

        if (claimResult.Status == MasteryClaimStatus.InsufficientLevel)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(
                    "[Mastery Event] Rejected mastery reward claim for {Nickname}: requested level {RequestedLevel}, required {RequiredLevel}, earned total level {EarnedTotalLevel}",
                    nickname, level, claimResult.Level, earnedTotalLevel);

            return BuildInsufficientLevelResult();
        }

        if (claimResult.Status == MasteryClaimStatus.AlreadyClaimed)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(
                    "[Mastery Event] Nickname {Nickname} already claimed reward for Mastery Level {Level}",
                    nickname, level);

            return BuildAlreadyClaimedResult();
        }

        if (claimResult.Status != MasteryClaimStatus.Success)
        {
            return BuildInvalidLevelResult("Mastery reward unavailable for requested level");
        }

        User user = account.User!;
        int gold = user.GoldCoins;
        int silver = user.SilverCoins;
        int plinko = user.PlinkoTickets;

        MasteryRewardCalculator.ApplyClaim(claimResult, ownedCodes, ref gold, ref silver, ref plinko);

        user.GoldCoins = gold;
        user.SilverCoins = silver;
        user.PlinkoTickets = plinko;
        user.InventoryItems ??= [];

        if (!string.IsNullOrEmpty(claimResult.ClaimKey) &&
            !user.InventoryItems.Any(i =>
                string.Equals(i.ItemCode, claimResult.ClaimKey, StringComparison.OrdinalIgnoreCase)))
        {
            user.InventoryItems.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = claimResult.ClaimKey,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "MasteryClaim"
            });
        }

        string? grantedProductCode = null;
        int grantedQuantity = 0;
        bool isCountedBoostReward = false;
        MasteryConsumables.BoostRowMutation? updatedExistingRow = null;
        if (!string.IsNullOrEmpty(claimResult.ProductCode))
        {
            (grantedProductCode, grantedQuantity, updatedExistingRow) =
                MasteryConsumables.GrantRewardProductWithTarget(
                    user,
                    account.ID,
                    claimResult.ProductCode,
                    claimResult.ProductQuantity,
                    TimeProvider.System.GetUtcNow(),
                    "MasteryClaim");
            isCountedBoostReward =
                string.Equals(grantedProductCode, MasteryConsumables.MasteryBoostCode,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(grantedProductCode, MasteryConsumables.SuperMasteryBoostCode,
                    StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            if (updatedExistingRow is { Row.ID: > 0 } && effectiveDb.Database.IsRelational())
            {
                string newCode = updatedExistingRow.Row.ItemCode;
                int newQty = updatedExistingRow.Row.Quantity;
                string expectedOrigCode = updatedExistingRow.OriginalItemCode;
                int expectedOrigQty = updatedExistingRow.OriginalQuantity;
                int targetRowId = updatedExistingRow.Row.ID;

                int casAffected = await effectiveDb.UserInventories
                    .Where(i => i.ID == targetRowId &&
                                i.ItemCode == expectedOrigCode &&
                                i.Quantity == expectedOrigQty)
                    .ExecuteUpdateAsync(setters => setters
                            .SetProperty(i => i.ItemCode, newCode)
                            .SetProperty(i => i.Quantity, newQty),
                        cancellationToken);

                if (casAffected == 0)
                {
                    if (tx != null) await tx.RollbackAsync(cancellationToken);
                    effectiveDb.ChangeTracker.Clear();
                    logger.LogWarning(
                        "[Mastery Event] CAS contention on counted boost row ID {RowId} (expected '{ExpectedCode}' qty={ExpectedQty}) for {Nickname} Level {Level} (attempt {Attempt}/{Max}); retrying",
                        targetRowId, expectedOrigCode, expectedOrigQty, nickname, level, attempt + 1,
                        maxContentionAttempts);
                    return null;
                }

                effectiveDb.Entry(updatedExistingRow.Row).State = EntityState.Unchanged;
            }

            await effectiveDb.SaveChangesAsync(cancellationToken);
            if (tx != null) await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when ((ex is DbUpdateException dbEx && IsInventoryUniqueViolation(dbEx)) ||
                                   ex is DbUpdateConcurrencyException)
        {
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            effectiveDb.ChangeTracker.Clear();

            bool claimKeyExists = !string.IsNullOrEmpty(claimResult.ClaimKey) &&
                await effectiveDb.UserInventories
                    .AsNoTracking()
                    .AnyAsync(i => i.UserID == user.ID && i.ItemCode == claimResult.ClaimKey,
                        cancellationToken);

            if (!claimKeyExists && (isCountedBoostReward || ex is DbUpdateConcurrencyException) &&
                attempt + 1 < maxContentionAttempts)
            {
                logger.LogWarning(
                    ex,
                    "[Mastery Event] Inventory contention for {Nickname} Level {Level} (attempt {Attempt}/{Max}); refreshing tracked rows",
                    nickname, level, attempt + 1, maxContentionAttempts);
                return null;
            }

            logger.LogWarning(
                ex,
                "[Mastery Event] Concurrent duplicate mastery reward claim blocked by UQ_UserInventory_UserID_ItemCode for {Nickname} Level {Level}",
                nickname, level);
            return BuildAlreadyClaimedResult();
        }

        if (effectiveCache != null)
        {
            await effectiveCache.RemoveByTagAsync($"account:{account.ID}", cancellationToken);
            await effectiveCache.RemoveByTagAsync(
                $"account:nick:{account.Name.Trim().ToLowerInvariant()}", cancellationToken);
            logger.LogInformation(
                "[Mastery Event] Invalidated hybrid cache tags for account {AccountId} ('{Nickname}') after claiming Level {Level}",
                account.ID, account.Name, level);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            if (!string.IsNullOrEmpty(grantedProductCode))
                logger.LogInformation(
                    "[Mastery Event] Granted {Quantity}x {ProductCode} to {Nickname} for Mastery Level {Level}",
                    grantedQuantity, grantedProductCode, nickname, level);

            if (claimResult.GoldCoins > 0 || claimResult.SilverCoins > 0 || claimResult.PlinkoTickets > 0)
                logger.LogInformation(
                    "[Mastery Event] Granted economy currency to {Nickname} for Mastery Level {Level} (Silver: {Silver}, Plinko: {Plinko}, Gold: {Gold})",
                    nickname, level, claimResult.SilverCoins, claimResult.PlinkoTickets,
                    claimResult.GoldCoins);
        }

        return BuildSuccessResult();
    }

    private async Task<IActionResult> HandleLegacyRepositoryPathAsync(
        string nickname,
        string? levelStr,
        INexusHybridCache? effectiveCache,
        CancellationToken cancellationToken)
    {
        Account? account = await accountRepository.GetByNicknameAsync(nickname, cancellationToken);
        if (account == null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Account not found for nickname: {Nickname}", nickname);

            return new PhpResult(new Dictionary<object, object>(), contentType: "text/html; charset=utf-8");
        }

        if (!int.TryParse(levelStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) || level < 0)
        {
            logger.LogWarning("[Mastery Event] Invalid mastery reward level '{LevelStr}' for '{Nickname}'",
                levelStr ?? string.Empty, nickname);
            return BuildInvalidLevelResult();
        }

        int earnedTotalLevel = Math.Max(account.Mastery?.TotalMasteryLevel() ?? 0, account.User?.TotalLevel ?? 0);
        MasteryRewardsConfiguration config = JSONConfiguration.MasteryRewardsConfiguration;
        List<string> ownedCodes = account.User?.InventoryItems?.Select(i => i.ItemCode).ToList() ?? [];

        MasteryClaimResult claimResult =
            MasteryRewardCalculator.EvaluateClaim(config, level, ownedCodes, earnedTotalLevel);

        if (claimResult.Status == MasteryClaimStatus.InsufficientLevel)
            return BuildInsufficientLevelResult();

        if (claimResult.Status == MasteryClaimStatus.AlreadyClaimed)
            return BuildAlreadyClaimedResult();

        if (claimResult.Status != MasteryClaimStatus.Success)
            return BuildInvalidLevelResult("Mastery reward unavailable for requested level");

        User user = account.User!;
        int gold = user.GoldCoins;
        int silver = user.SilverCoins;
        int plinko = user.PlinkoTickets;

        MasteryRewardCalculator.ApplyClaim(claimResult, ownedCodes, ref gold, ref silver, ref plinko);

        user.GoldCoins = gold;
        user.SilverCoins = silver;
        user.PlinkoTickets = plinko;
        user.InventoryItems ??= [];

        if (!string.IsNullOrEmpty(claimResult.ClaimKey) &&
            !user.InventoryItems.Any(i => string.Equals(i.ItemCode, claimResult.ClaimKey, StringComparison.OrdinalIgnoreCase)))
        {
            user.InventoryItems.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = claimResult.ClaimKey,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "MasteryClaim"
            });
        }

        if (!string.IsNullOrEmpty(claimResult.ProductCode))
        {
            MasteryConsumables.GrantRewardProduct(
                user,
                account.ID,
                claimResult.ProductCode,
                claimResult.ProductQuantity,
                TimeProvider.System.GetUtcNow(),
                "MasteryClaim");
        }

        try
        {
            await accountRepository.UpdateAsync(account, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(
                ex,
                "[Mastery Event] Concurrent duplicate mastery reward claim blocked for {Nickname} Level {Level}",
                nickname, level);
            return BuildAlreadyClaimedResult();
        }

        if (effectiveCache != null)
        {
            await effectiveCache.RemoveByTagAsync($"account:{account.ID}", cancellationToken);
            await effectiveCache.RemoveByTagAsync(
                $"account:nick:{account.Name.Trim().ToLowerInvariant()}", cancellationToken);
        }

        return BuildSuccessResult();
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

    private static PhpResult BuildSuccessResult() => new(
        new Dictionary<string, object>
        {
            ["reward_status"] = "OK",
            ["error_code"] = 0,
            ["error_msg"] = string.Empty,
            ["0"] = true,
            ["reward"] = true,
            ["vested_threshold"] = "5"
        },
        contentType: "text/plain; charset=utf-8");

    private static PhpResult BuildAlreadyClaimedResult() => new(
        new Dictionary<string, object>
        {
            ["reward_status"] = "ALREADY_CLAIMED",
            ["error_code"] = 1,
            ["error_msg"] = "Reward already claimed",
            ["0"] = false,
            ["reward"] = false,
            ["vested_threshold"] = "5"
        },
        contentType: "text/plain; charset=utf-8");

    private static PhpResult BuildInsufficientLevelResult() => new(
        new Dictionary<string, object>
        {
            ["reward_status"] = "INSUFFICIENT_LEVEL",
            ["error_code"] = 2,
            ["error_msg"] = "Insufficient mastery level",
            ["0"] = false,
            ["reward"] = false,
            ["vested_threshold"] = "5"
        },
        contentType: "text/plain; charset=utf-8");

    private static PhpResult BuildInvalidLevelResult(string errorMsg = "Invalid mastery reward level") => new(
        new Dictionary<string, object>
        {
            ["reward_status"] = "INVALID_LEVEL",
            ["error_code"] = 2,
            ["error_msg"] = errorMsg,
            ["0"] = false,
            ["reward"] = false,
            ["vested_threshold"] = "5"
        },
        contentType: "text/plain; charset=utf-8");

    private static string? GetRequestValue(HttpRequest request, string key)
    {
        if (request.QueryString.HasValue &&
            SpanFormUrlEncodedParser.TryExtractParameter(request.QueryString.Value.AsSpan(), key,
                out ReadOnlySpan<char> querySpan))
            return querySpan.ToString();

        if (request.HasFormContentType && request.Form.ContainsKey(key)) return request.Form[key].FirstOrDefault();

        return request.Query.ContainsKey(key) ? request.Query[key].FirstOrDefault() : null;
    }
}
