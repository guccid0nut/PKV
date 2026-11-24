// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Telemetry;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Economy;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NEXUS.Contracts.Economy;
using NEXUS.Contracts.Services;
using Npgsql;

namespace KONGOR.MasterServer.Services.Economy;

/// <summary>
///     Domain service implementation for currency reward calculations, wallet balance adjustments,
///     match rewards, and audit ledger persistence across ProjectKongor ecosystem.
/// </summary>
public class EconomyService : IEconomyService
{
    private readonly MerrickContext? _db;
    private readonly ILogger<EconomyService> _logger;
    private readonly EconomyConfiguration _economyConfiguration;
    private readonly TimeProvider _timeProvider;

    internal bool HasActiveRelationalDatabase =>
        _db is not null && _db.Database.IsRelational();

    internal static string NormalizeScopedIdempotencyKey(string scopePrefix, int accountOrUserId, string? rawClientKey)
    {
        if (!string.IsNullOrWhiteSpace(rawClientKey))
        {
            string trimmed = rawClientKey.Trim();
            Span<char> buf = stackalloc char[Math.Min(trimmed.Length, 80)];
            int len = 0;
            foreach (char ch in trimmed)
            {
                if (ch is >= '!' and <= '~')
                {
                    buf[len++] = ch;
                    if (len == buf.Length) break;
                }
            }

            if (len > 0)
            {
                return $"{scopePrefix}:{accountOrUserId}:{new string(buf[..len])}";
            }
        }

        return $"{scopePrefix}:{accountOrUserId}:{Guid.NewGuid():N}";
    }

    internal static bool IsUniqueIdempotencyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pgEx &&
        pgEx.SqlState == PostgresErrorCodes.UniqueViolation &&
        (string.Equals(pgEx.ConstraintName, "IX_EconomyTransactions_UserID_IdempotencyKey", StringComparison.OrdinalIgnoreCase) ||
         (pgEx.MessageText?.Contains("IX_EconomyTransactions_UserID_IdempotencyKey", StringComparison.OrdinalIgnoreCase) ?? false));

    internal async Task<EconomyTransaction?> TryGetByIdempotencyKeyAsync(
        int userId,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (_db is null || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        return await _db.EconomyTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.UserID == userId &&
                     t.IdempotencyKey == idempotencyKey,
                ct);
    }

    internal async Task SetTransactionReferenceByIdempotencyKeyAsync(
        int userId,
        string idempotencyKey,
        string referenceId,
        CancellationToken ct = default)
    {
        if (_db is null || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return;
        }

        EconomyTransaction? trackedOrDbTx = _db.EconomyTransactions.Local
                .FirstOrDefault(t => t.UserID == userId && t.IdempotencyKey == idempotencyKey)
            ?? await _db.EconomyTransactions
                .FirstOrDefaultAsync(t => t.UserID == userId && t.IdempotencyKey == idempotencyKey, ct);

        if (trackedOrDbTx is not null)
        {
            trackedOrDbTx.ReferenceId = referenceId;
            await _db.SaveChangesAsync(ct);
        }
    }

    internal async Task<T> ExecuteInRepeatableReadTransactionAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        if (_db is null || !_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            return await operation(0, ct);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        int attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            int currentAttempt = attempt++;
            if (currentAttempt > 0)
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(
                    "[EconomyService] Retrying repeatable-read transaction (attempt #{Attempt}) after transient database failure.",
                    currentAttempt + 1);
            }

            await using var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            try
            {
                T result = await operation(currentAttempt, ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateException dbEx) when (IsUniqueIdempotencyViolation(dbEx))
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                _logger.LogWarning(
                    "[EconomyService] Concurrent idempotency unique-constraint race (23505) rolled back cleanly; replaying read Attempt #{ReplayAttempt} to return winning transaction.",
                    currentAttempt + 1);

                await using var replayTx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                T replayResult = await operation(currentAttempt + 1, ct);
                await replayTx.CommitAsync(ct);
                return replayResult;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    internal void ClearTrackedChanges() => _db?.ChangeTracker.Clear();

    public EconomyService(
        MerrickContext? db = null,
        ILogger<EconomyService>? logger = null,
        EconomyConfiguration? economyConfiguration = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<EconomyService>.Instance;
        _economyConfiguration = economyConfiguration ?? JSONConfiguration.EconomyConfiguration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }



    /// <inheritdoc />
    public CurrencyDeltaDTO CalculateCurrencyRewards(
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds)
    {
        MatchRewards matchRewards = _economyConfiguration.MatchRewards;

        (int gold, int silver, int plinko) = groupSize switch
        {
            <= 1 => isWin
                ? (matchRewards.Solo.Win.GoldCoins, matchRewards.Solo.Win.SilverCoins,
                    matchRewards.Solo.Win.PlinkoTickets)
                : (matchRewards.Solo.Loss.GoldCoins, matchRewards.Solo.Loss.SilverCoins,
                    matchRewards.Solo.Loss.PlinkoTickets),
            2 => isWin
                ? (matchRewards.TwoPersonGroup.Win.GoldCoins, matchRewards.TwoPersonGroup.Win.SilverCoins,
                    matchRewards.TwoPersonGroup.Win.PlinkoTickets)
                : (matchRewards.TwoPersonGroup.Loss.GoldCoins, matchRewards.TwoPersonGroup.Loss.SilverCoins,
                    matchRewards.TwoPersonGroup.Loss.PlinkoTickets),
            3 => isWin
                ? (matchRewards.ThreePersonGroup.Win.GoldCoins, matchRewards.ThreePersonGroup.Win.SilverCoins,
                    matchRewards.ThreePersonGroup.Win.PlinkoTickets)
                : (matchRewards.ThreePersonGroup.Loss.GoldCoins, matchRewards.ThreePersonGroup.Loss.SilverCoins,
                    matchRewards.ThreePersonGroup.Loss.PlinkoTickets),
            4 => isWin
                ? (matchRewards.FourPersonGroup.Win.GoldCoins, matchRewards.FourPersonGroup.Win.SilverCoins,
                    matchRewards.FourPersonGroup.Win.PlinkoTickets)
                : (matchRewards.FourPersonGroup.Loss.GoldCoins, matchRewards.FourPersonGroup.Loss.SilverCoins,
                    matchRewards.FourPersonGroup.Loss.PlinkoTickets),
            _ => isWin
                ? (matchRewards.FivePersonGroup.Win.GoldCoins, matchRewards.FivePersonGroup.Win.SilverCoins,
                    matchRewards.FivePersonGroup.Win.PlinkoTickets)
                : (matchRewards.FivePersonGroup.Loss.GoldCoins, matchRewards.FivePersonGroup.Loss.SilverCoins,
                    matchRewards.FivePersonGroup.Loss.PlinkoTickets)
        };

        if (durationSeconds is > 0 and < 900)
        {
            double durationScale = Math.Clamp(durationSeconds / 900.0, 0.25, 1.0);
            gold = (int)Math.Round(gold * durationScale);
            silver = (int)Math.Round(silver * durationScale);
            plinko = (int)Math.Round(plinko * durationScale);
        }

        if (isMvp)
        {
            gold = (int)Math.Round(gold * 1.25);
            silver = (int)Math.Round(silver * 1.25);
            plinko += 2;
        }

        int finalGold = Math.Max(1, gold);
        int finalSilver = Math.Max(1, silver);
        int finalPlinko = Math.Max(1, plinko);

        _logger.LogDebug(
            "[EconomyService] CalculateCurrencyRewards: GroupSize={GroupSize}, Win={Win}, MVP={Mvp}, Duration={Duration}s => Gold={Gold}, Silver={Silver}, Plinko={Plinko}",
            groupSize, isWin, isMvp, durationSeconds, finalGold, finalSilver, finalPlinko);

        return new CurrencyDeltaDTO(finalGold, finalSilver, finalPlinko);
    }

    /// <inheritdoc />
    public CurrencyRewardCalculation CalculateMatchRewards(
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds)
    {
        CurrencyDeltaDTO delta = CalculateCurrencyRewards(groupSize, isWin, isMvp, durationSeconds);
        return new CurrencyRewardCalculation(delta.GoldDelta, delta.SilverDelta, delta.PlinkoDelta);
    }

    /// <inheritdoc />
    public async Task<MatchRewardResultDTO> AwardMatchRewardsAsync(
        MatchRewardRequestDTO request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CurrencyDeltaDTO delta = CalculateCurrencyRewards(
            request.GroupSize,
            request.IsWin,
            request.IsMvp,
            request.DurationSeconds);

        // Check if user is already tracked in active context or load from database
        User? user = _db?.ChangeTracker.Entries<User>()
            .Select(e => e.Entity)
            .FirstOrDefault(u => u.ID == request.UserId);

        if (user is null && _db is not null)
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.ID == request.UserId, ct);
        }

        if (user is null)
        {
            _logger.LogWarning("[EconomyService] AwardMatchRewards failed: User #{UserId} not found", request.UserId);
            return new MatchRewardResultDTO(
                request.UserId,
                request.AccountId,
                request.AccountName,
                request.MatchId,
                delta,
                new CurrencyBalancesDTO(request.UserId, 0, 0, 0),
                false,
                $"User #{request.UserId} not found");
        }

        user.GoldCoins += delta.GoldDelta;
        user.SilverCoins += delta.SilverDelta;
        user.PlinkoTickets += delta.PlinkoDelta;

        if (_db is not null && (delta.GoldDelta > 0 || delta.SilverDelta > 0 || delta.PlinkoDelta > 0))
        {
            EconomyTransaction transaction = new()
            {
                UserID = user.ID,
                AccountID = request.AccountId > 0 ? request.AccountId : null,
                GoldCoinsDelta = delta.GoldDelta,
                SilverCoinsDelta = delta.SilverDelta,
                PlinkoTicketsDelta = delta.PlinkoDelta,
                GoldCoinsBalanceAfter = user.GoldCoins,
                SilverCoinsBalanceAfter = user.SilverCoins,
                PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                TransactionType = "MatchReward",
                ReferenceId = request.MatchId.ToString(CultureInfo.InvariantCulture),
                Description = $"Match #{request.MatchId} completion reward",
                Timestamp = _timeProvider.GetUtcNow()
            };

            await _db.EconomyTransactions.AddAsync(transaction, ct);
        }

        NexusMetrics.RecordMatchRewardAwarded(
            request.GroupSize,
            request.IsWin,
            request.IsMvp,
            delta.GoldDelta,
            delta.SilverDelta,
            delta.PlinkoDelta);

        _logger.LogInformation(
            "[EconomyService] Awarded match rewards to User #{UserId} (Account '{AccountName}' #{AccountId}) for Match #{MatchId}: Gold +{Gold}, Silver +{Silver}, Plinko +{Plinko}. New balances: {GoldBal}G / {SilverBal}S / {PlinkoBal}P",
            request.UserId, request.AccountName, request.AccountId, request.MatchId,
            delta.GoldDelta, delta.SilverDelta, delta.PlinkoDelta,
            user.GoldCoins, user.SilverCoins, user.PlinkoTickets);

        CurrencyBalancesDTO balancesAfter = new(user.ID, user.GoldCoins, user.SilverCoins, user.PlinkoTickets);
        return new MatchRewardResultDTO(
            request.UserId,
            request.AccountId,
            request.AccountName,
            request.MatchId,
            delta,
            balancesAfter,
            true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchRewardResultDTO>> AwardMatchRewardsBatchAsync(
        int matchId,
        IEnumerable<MatchRewardRequestDTO> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        List<MatchRewardResultDTO> results = [];
        foreach (MatchRewardRequestDTO req in requests)
        {
            MatchRewardResultDTO result = await AwardMatchRewardsAsync(req, ct);
            results.Add(result);
        }

        _logger.LogInformation(
            "[EconomyService] AwardMatchRewardsBatchAsync completed for Match #{MatchId} ({Count} player rewards processed)",
            matchId, results.Count);

        return results;
    }

    /// <inheritdoc />
    public async Task<CurrencyBalancesDTO?> GetUserBalancesAsync(
        int userId,
        CancellationToken ct = default)
    {
        if (_db is null) return null;

        User? user = _db.ChangeTracker.Entries<User>()
            .Select(e => e.Entity)
            .FirstOrDefault(u => u.ID == userId);

        if (user is null)
        {
            user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == userId, ct);
        }

        if (user is null)
        {
            _logger.LogDebug("[EconomyService] GetUserBalancesAsync: User #{UserId} not found", userId);
            return null;
        }

        return new CurrencyBalancesDTO(user.ID, user.GoldCoins, user.SilverCoins, user.PlinkoTickets);
    }

    /// <inheritdoc />
    public Task<CurrencyOperationResultDTO> AdjustBalanceAsync(
        int userId,
        int? accountId,
        CurrencyDeltaDTO delta,
        string transactionType,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default) =>
        AdjustBalanceWithIdempotencyAsync(
            userId,
            accountId,
            delta,
            transactionType,
            idempotencyKey: null,
            referenceId: referenceId,
            description: description,
            ct: ct);

    public async Task<CurrencyOperationResultDTO> AdjustBalanceWithIdempotencyAsync(
        int userId,
        int? accountId,
        CurrencyDeltaDTO delta,
        string transactionType,
        string? idempotencyKey,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (_db is null)
        {
            return new CurrencyOperationResultDTO(
                userId,
                accountId,
                delta,
                new CurrencyBalancesDTO(userId, 0, 0, 0),
                false,
                "Database context unavailable");
        }

        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    EconomyTransaction? existingTx = await TryGetByIdempotencyKeyAsync(userId, idempotencyKey, ct);
                    if (existingTx is not null)
                    {
                        _logger.LogWarning(
                            "[EconomyService] Idempotent replay detected for ({Type}, IdempotencyKey={IdempotencyKey}) on User #{UserId}; returning committed balances without double-adjusting.",
                            transactionType, idempotencyKey, userId);

                        CurrencyBalancesDTO existingBalances = new(
                            userId,
                            existingTx.GoldCoinsBalanceAfter,
                            existingTx.SilverCoinsBalanceAfter,
                            existingTx.PlinkoTicketsBalanceAfter);
                        return new CurrencyOperationResultDTO(
                            userId,
                            accountId,
                            new CurrencyDeltaDTO(existingTx.GoldCoinsDelta, existingTx.SilverCoinsDelta, existingTx.PlinkoTicketsDelta),
                            existingBalances,
                            true);
                    }
                }

                User? user = await _db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
                if (user == null)
                {
                    _logger.LogWarning("[EconomyService] AdjustBalanceAsync failed: User #{UserId} not found", userId);
                    return new CurrencyOperationResultDTO(
                        userId,
                        accountId,
                        delta,
                        new CurrencyBalancesDTO(userId, 0, 0, 0),
                        false,
                        $"User #{userId} not found");
                }

                user.GoldCoins += delta.GoldDelta;
                user.SilverCoins += delta.SilverDelta;
                user.PlinkoTickets += delta.PlinkoDelta;

                EconomyTransaction transaction = new()
                {
                    UserID = user.ID,
                    AccountID = accountId,
                    GoldCoinsDelta = delta.GoldDelta,
                    SilverCoinsDelta = delta.SilverDelta,
                    PlinkoTicketsDelta = delta.PlinkoDelta,
                    GoldCoinsBalanceAfter = user.GoldCoins,
                    SilverCoinsBalanceAfter = user.SilverCoins,
                    PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                    TransactionType = transactionType,
                    ReferenceId = referenceId,
                    IdempotencyKey = idempotencyKey,
                    Description = description,
                    Timestamp = _timeProvider.GetUtcNow()
                };

                await _db.EconomyTransactions.AddAsync(transaction, ct);
                await _db.SaveChangesAsync(ct);

                NexusMetrics.RecordEconomyTransaction(
                    transactionType,
                    delta.GoldDelta,
                    delta.SilverDelta,
                    delta.PlinkoDelta);

                _logger.LogInformation(
                    "[EconomyService] AdjustBalanceAsync ({Type}) for User #{UserId}: Gold {Gold:+0;-#;0}, Silver {Silver:+0;-#;0}, Plinko {Plinko:+0;-#;0}. New balances: {GoldBal}G / {SilverBal}S / {PlinkoBal}P",
                    transactionType, userId, delta.GoldDelta, delta.SilverDelta, delta.PlinkoDelta,
                    user.GoldCoins, user.SilverCoins, user.PlinkoTickets);

                CurrencyBalancesDTO balancesAfter = new(user.ID, user.GoldCoins, user.SilverCoins, user.PlinkoTickets);
                return new CurrencyOperationResultDTO(
                    userId,
                    accountId,
                    delta,
                    balancesAfter,
                    true);
            }
            catch (DbUpdateException dbEx) when (!string.IsNullOrWhiteSpace(idempotencyKey) && IsUniqueIdempotencyViolation(dbEx))
            {
                if (_db.Database.CurrentTransaction is not null)
                {
                    throw;
                }

                _db.ChangeTracker.Clear();
                EconomyTransaction? racedTx = await TryGetByIdempotencyKeyAsync(userId, idempotencyKey, ct);
                if (racedTx is not null)
                {
                    _logger.LogWarning(
                        "[EconomyService] Concurrent idempotency race (23505) resolved for ({Type}, IdempotencyKey={IdempotencyKey}) on User #{UserId}; returning committed balances.",
                        transactionType, idempotencyKey, userId);

                    return new CurrencyOperationResultDTO(
                        userId,
                        accountId,
                        new CurrencyDeltaDTO(racedTx.GoldCoinsDelta, racedTx.SilverCoinsDelta, racedTx.PlinkoTicketsDelta),
                        new CurrencyBalancesDTO(userId, racedTx.GoldCoinsBalanceAfter, racedTx.SilverCoinsBalanceAfter, racedTx.PlinkoTicketsBalanceAfter),
                        true);
                }

                throw;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxRetries - 1)
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex,
                    "[EconomyService] Concurrency collision adjusting balance for User #{UserId}. Attempt {Attempt} of {MaxRetries}",
                    userId, attempt + 1, maxRetries);
                await Task.Delay(25 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[EconomyService] Failed to adjust balance ({Type}) for User #{UserId}",
                    transactionType, userId);
                return new CurrencyOperationResultDTO(
                    userId,
                    accountId,
                    delta,
                    new CurrencyBalancesDTO(userId, 0, 0, 0),
                    false,
                    ex.Message);
            }
        }

        return new CurrencyOperationResultDTO(
            userId,
            accountId,
            delta,
            new CurrencyBalancesDTO(userId, 0, 0, 0),
            false,
            "Exhausted retries due to concurrency collisions");
    }

    /// <inheritdoc />
    public async Task<CurrencyRewardResult> AwardMatchRewardsAsync(
        int userId,
        int accountId,
        int matchId,
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds,
        CancellationToken ct = default)
    {
        MatchRewardRequestDTO request = new(
            userId,
            accountId,
            $"Account_{accountId}",
            matchId,
            groupSize,
            isWin,
            isMvp,
            durationSeconds);

        MatchRewardResultDTO result = await AwardMatchRewardsAsync(request, ct);
        if (!result.Success)
        {
            return CurrencyRewardResult.Failed(result.ErrorMessage ?? "Failed to award match rewards");
        }

        if (_db is not null)
        {
            await _db.SaveChangesAsync(ct);
        }

        return CurrencyRewardResult.Succeeded(
            result.CurrenciesEarned.GoldDelta,
            result.CurrenciesEarned.SilverDelta,
            result.CurrenciesEarned.PlinkoDelta,
            result.BalancesAfter.GoldCoins,
            result.BalancesAfter.SilverCoins,
            result.BalancesAfter.PlinkoTickets);
    }

    /// <inheritdoc />
    public Task<CurrencyRewardResult> RecordTransactionAsync(
        int userId,
        int? accountId,
        int goldDelta,
        int silverDelta,
        int plinkoDelta,
        string transactionType,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default) =>
        RecordTransactionWithIdempotencyAsync(
            userId,
            accountId,
            goldDelta,
            silverDelta,
            plinkoDelta,
            transactionType,
            idempotencyKey: null,
            referenceId: referenceId,
            description: description,
            ct: ct);

    public async Task<CurrencyRewardResult> RecordTransactionWithIdempotencyAsync(
        int userId,
        int? accountId,
        int goldDelta,
        int silverDelta,
        int plinkoDelta,
        string transactionType,
        string? idempotencyKey,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default)
    {
        CurrencyDeltaDTO delta = new(goldDelta, silverDelta, plinkoDelta);
        CurrencyOperationResultDTO opResult = await AdjustBalanceWithIdempotencyAsync(
            userId,
            accountId,
            delta,
            transactionType,
            idempotencyKey,
            referenceId,
            description,
            ct);

        if (!opResult.Success)
        {
            return CurrencyRewardResult.Failed(opResult.ErrorMessage ?? "Failed to record transaction");
        }

        return CurrencyRewardResult.Succeeded(
            goldDelta,
            silverDelta,
            plinkoDelta,
            opResult.BalancesAfter.GoldCoins,
            opResult.BalancesAfter.SilverCoins,
            opResult.BalancesAfter.PlinkoTickets);
    }

    /// <inheritdoc />
    public async Task<CurrencyRewardResult> DeductCurrenciesAsync(
        int userId,
        int? accountId,
        int goldCost,
        int silverCost,
        int plinkoCost,
        string transactionType,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default)
    {
        (CurrencyRewardResult result, _) = await DeductCurrenciesWithIdempotencyAsync(
            userId,
            accountId,
            goldCost,
            silverCost,
            plinkoCost,
            transactionType,
            idempotencyKey: null,
            referenceId: referenceId,
            description: description,
            ct: ct);
        return result;
    }

    public async Task<(CurrencyRewardResult Result, EconomyTransaction? ReplayedTransaction)> DeductCurrenciesWithIdempotencyAsync(
        int userId,
        int? accountId,
        int goldCost,
        int silverCost,
        int plinkoCost,
        string transactionType,
        string? idempotencyKey,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default)
    {
        if (_db is null) return (CurrencyRewardResult.Failed("Database context unavailable"), null);

        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    EconomyTransaction? existingTx = await TryGetByIdempotencyKeyAsync(userId, idempotencyKey, ct);
                    if (existingTx is not null)
                    {
                        _logger.LogWarning(
                            "[EconomyService] Idempotent replay detected for ({Type}, IdempotencyKey={IdempotencyKey}) on User #{UserId}; returning committed balances without double-charging.",
                            transactionType, idempotencyKey, userId);

                        return (CurrencyRewardResult.Succeeded(
                            existingTx.GoldCoinsDelta,
                            existingTx.SilverCoinsDelta,
                            existingTx.PlinkoTicketsDelta,
                            existingTx.GoldCoinsBalanceAfter,
                            existingTx.SilverCoinsBalanceAfter,
                            existingTx.PlinkoTicketsBalanceAfter), existingTx);
                    }
                }

                User? user = await _db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
                if (user == null)
                {
                    _logger.LogWarning("[EconomyService] DeductCurrencies failed: User #{UserId} not found", userId);
                    return (CurrencyRewardResult.Failed($"User #{userId} not found"), null);
                }

                if (goldCost > 0 && user.GoldCoins < goldCost)
                {
                    _logger.LogWarning("[EconomyService] Insufficient gold for User #{UserId}: has {Has}, needs {Needs}",
                        userId, user.GoldCoins, goldCost);
                    return (CurrencyRewardResult.Failed("Insufficient Gold Coins"), null);
                }

                if (silverCost > 0 && user.SilverCoins < silverCost)
                {
                    _logger.LogWarning("[EconomyService] Insufficient silver for User #{UserId}: has {Has}, needs {Needs}",
                        userId, user.SilverCoins, silverCost);
                    return (CurrencyRewardResult.Failed("Insufficient Silver Coins"), null);
                }

                if (plinkoCost > 0 && user.PlinkoTickets < plinkoCost)
                {
                    _logger.LogWarning("[EconomyService] Insufficient plinko tickets for User #{UserId}: has {Has}, needs {Needs}",
                        userId, user.PlinkoTickets, plinkoCost);
                    return (CurrencyRewardResult.Failed("Insufficient Plinko Tickets"), null);
                }

                user.GoldCoins -= goldCost;
                user.SilverCoins -= silverCost;
                user.PlinkoTickets -= plinkoCost;

                EconomyTransaction transaction = new()
                {
                    UserID = user.ID,
                    AccountID = accountId,
                    GoldCoinsDelta = -goldCost,
                    SilverCoinsDelta = -silverCost,
                    PlinkoTicketsDelta = -plinkoCost,
                    GoldCoinsBalanceAfter = user.GoldCoins,
                    SilverCoinsBalanceAfter = user.SilverCoins,
                    PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                    TransactionType = transactionType,
                    ReferenceId = referenceId,
                    IdempotencyKey = idempotencyKey,
                    Description = description,
                    Timestamp = _timeProvider.GetUtcNow()
                };

                await _db.EconomyTransactions.AddAsync(transaction, ct);
                await _db.SaveChangesAsync(ct);

                NexusMetrics.RecordEconomyTransaction(
                    transactionType,
                    -goldCost,
                    -silverCost,
                    -plinkoCost);

                _logger.LogInformation(
                    "[EconomyService] Deducted currencies ({Type}) for User #{UserId}: Gold -{Gold}, Silver -{Silver}, Plinko -{Plinko}. Remaining: {GoldBal}G / {SilverBal}S / {PlinkoBal}P",
                    transactionType, userId, goldCost, silverCost, plinkoCost,
                    user.GoldCoins, user.SilverCoins, user.PlinkoTickets);

                return (CurrencyRewardResult.Succeeded(
                    -goldCost,
                    -silverCost,
                    -plinkoCost,
                    user.GoldCoins,
                    user.SilverCoins,
                    user.PlinkoTickets), null);
            }
            catch (DbUpdateException dbEx) when (!string.IsNullOrWhiteSpace(idempotencyKey) && IsUniqueIdempotencyViolation(dbEx))
            {
                if (_db.Database.CurrentTransaction is not null)
                {
                    throw;
                }

                _db.ChangeTracker.Clear();
                EconomyTransaction? racedTx = await TryGetByIdempotencyKeyAsync(userId, idempotencyKey, ct);
                if (racedTx is not null)
                {
                    _logger.LogWarning(
                        "[EconomyService] Concurrent idempotency race (23505) resolved for ({Type}, IdempotencyKey={IdempotencyKey}) on User #{UserId}; returning committed balances.",
                        transactionType, idempotencyKey, userId);

                    return (CurrencyRewardResult.Succeeded(
                        racedTx.GoldCoinsDelta,
                        racedTx.SilverCoinsDelta,
                        racedTx.PlinkoTicketsDelta,
                        racedTx.GoldCoinsBalanceAfter,
                        racedTx.SilverCoinsBalanceAfter,
                        racedTx.PlinkoTicketsBalanceAfter), racedTx);
                }

                throw;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxRetries - 1)
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex,
                    "[EconomyService] Concurrency collision deducting currencies for User #{UserId}. Attempt {Attempt} of {MaxRetries}",
                    userId, attempt + 1, maxRetries);
                await Task.Delay(25 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                if (_db.Database.CurrentTransaction is not null && ex is DbUpdateException)
                {
                    throw;
                }

                _logger.LogError(ex,
                    "[EconomyService] Failed to deduct currencies ({Type}) for User #{UserId}",
                    transactionType, userId);
                return (CurrencyRewardResult.Failed(ex.Message), null);
            }
        }

        return (CurrencyRewardResult.Failed("Exhausted retries due to concurrency collisions"), null);
    }
}
