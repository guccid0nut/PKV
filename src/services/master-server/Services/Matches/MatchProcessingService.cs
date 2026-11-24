// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Economy;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Economy;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Quests;
using KONGOR.MasterServer.Services.Rating;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Stats;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Economy;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Services;
using StackExchange.Redis;
using IDatabase = StackExchange.Redis.IDatabase;

namespace KONGOR.MasterServer.Services.Matches;

public sealed class MatchProcessingService : IMatchProcessingService
{
    private readonly IMatchIngestionHandler _ingestionHandler;
    private readonly IHeroMasteryProgressionService _masteryService;
    private readonly IOpenSkillRatingCalculator _ratingCalculator;
    private readonly IReplayPersistenceWorker _persistenceWorker;
    private readonly ILogger<MatchProcessingService> _logger;
    private readonly MerrickContext? _merrickContext;
    private readonly IWarEffortProgressionService? _warEffortProgressionService;

    /// <summary>
    /// Primary DI constructor resolving decomposed single-responsibility match services.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public MatchProcessingService(
        IMatchIngestionHandler ingestionHandler,
        IHeroMasteryProgressionService masteryService,
        IOpenSkillRatingCalculator ratingCalculator,
        IReplayPersistenceWorker persistenceWorker,
        ILogger<MatchProcessingService> logger,
        MerrickContext? merrickContext = null,
        IWarEffortProgressionService? warEffortProgressionService = null)
    {
        _ingestionHandler = ingestionHandler;
        _masteryService = masteryService;
        _ratingCalculator = ratingCalculator;
        _persistenceWorker = persistenceWorker;
        _logger = logger;
        _merrickContext = merrickContext;
        _warEffortProgressionService = warEffortProgressionService;
    }

    /// <summary>
    /// Backwards-compatible constructor for integration and unit tests.
    /// </summary>
    public MatchProcessingService(
        MerrickContext merrickContext,
        IPlayerStatisticsService statsService,
        IHeroDefinitionService heroDefinitionService,
        IDatabase distributedCache,
        ILogger<MatchProcessingService> logger,
        IReplayAutoUploadService? replayAutoUploadService = null,
        IEconomyService? economyService = null,
        IMasteryRewardService? masteryRewardService = null,
        IEventBus? eventBus = null,
        IOpenSkillRatingService? openSkillRatingService = null)
    {
        _logger = logger;
        _merrickContext = merrickContext;
        IEconomyService economy = economyService ?? new EconomyService(merrickContext);
        IMasteryRewardService masteryReward = masteryRewardService ?? MasteryRewardService.Instance;
        IOpenSkillRatingService openSkill = openSkillRatingService ?? new OpenSkillRatingService();

        _ingestionHandler = new MatchIngestionHandler(merrickContext, heroDefinitionService, NullLogger<MatchIngestionHandler>.Instance);
        _masteryService = new HeroMasteryProgressionService(merrickContext, economy, masteryReward, NullLogger<HeroMasteryProgressionService>.Instance);
        _ratingCalculator = new OpenSkillRatingCalculator(merrickContext, openSkill, NullLogger<OpenSkillRatingCalculator>.Instance);
        _persistenceWorker = new ReplayPersistenceWorker(merrickContext, statsService, distributedCache, NullLogger<ReplayPersistenceWorker>.Instance, replayAutoUploadService, eventBus);
    }

    public async Task ProcessMatchSubmissionAsync(MatchSubmissionPayload payload,
        CancellationToken cancellationToken = default)
    {
        int matchID = payload.MatchID;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "[MatchProcessingService] Starting processing for Match ID: {MatchId} (Queue delay: {DelayMs:F1}ms)",
                matchID, (TimeProvider.System.GetUtcNow() - payload.EnqueuedAt).TotalMilliseconds);

        IngestedMatchContext? context = null;

        try
        {
            if (_merrickContext is not null)
            {
                IExecutionStrategy strategy = _merrickContext.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    IDbContextTransaction? transaction = null;
                    if (_merrickContext.Database.IsRelational())
                    {
                        transaction = await _merrickContext.Database.BeginTransactionAsync(
                            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        // Stage 1: Payload validation, account resolution, and entity initialization
                        context = await _ingestionHandler.IngestMatchSubmissionAsync(payload, cancellationToken).ConfigureAwait(false);

                        // Stage 2: Economy currency rewards, Hero Mastery, and War Effort progression
                        await _masteryService.ProcessProgressionAndRewardsAsync(context, cancellationToken).ConfigureAwait(false);
                        if (_warEffortProgressionService is not null)
                        {
                            await _warEffortProgressionService.ProcessMatchProgressionAsync(context, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("[MatchProcessingService] Skipping War Effort Stage-2 progression for Match ID {MatchId}: IWarEffortProgressionService was not injected.", matchID);
                        }

                        // Stage 3: Bayesian OpenSkill rating computation and statistics mutation
                        await _ratingCalculator.CalculateAndUpdateRatingsAsync(context, cancellationToken).ConfigureAwait(false);

                        // Stage 4A: Database persistence save
                        await _persistenceWorker.PersistAsync(context, cancellationToken).ConfigureAwait(false);

                        if (transaction != null)
                        {
                            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        if (transaction != null)
                        {
                            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        }
                        _merrickContext.ChangeTracker.Clear();
                        throw;
                    }
                    finally
                    {
                        if (transaction != null)
                        {
                            await transaction.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }).ConfigureAwait(false);
            }
            else
            {
                // Fallback for mocked scenarios without DbContext
                context = await _ingestionHandler.IngestMatchSubmissionAsync(payload, cancellationToken).ConfigureAwait(false);
                await _masteryService.ProcessProgressionAndRewardsAsync(context, cancellationToken).ConfigureAwait(false);
                if (_warEffortProgressionService is not null)
                {
                    await _warEffortProgressionService.ProcessMatchProgressionAsync(context, cancellationToken).ConfigureAwait(false);
                }
                await _ratingCalculator.CalculateAndUpdateRatingsAsync(context, cancellationToken).ConfigureAwait(false);
                await _persistenceWorker.PersistAsync(context, cancellationToken).ConfigureAwait(false);
            }

            // Stage 4B: Post-Commit Side Effects (Only executed after successful DB commit)
            if (context != null)
            {
                try
                {
                    await _persistenceWorker.FinalizeSideEffectsAsync(context, payload.MatchServer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception sideEffectEx)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(sideEffectEx,
                            "[MatchProcessingService] Non-fatal error during post-commit side-effect finalization for Match ID: {MatchId}",
                            matchID);
                    }
                }
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[MatchProcessingService] Successfully persisted, committed, and finalized Match ID: {MatchId}",
                    matchID);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex,
                    "[MatchProcessingService] Failed to process match submission for Match ID: {MatchId}", matchID);

            throw;
        }
    }

    /// <summary>
    /// Delegates currency reward calculation to <see cref="IEconomyService"/>.
    /// </summary>
    public static (int GoldCoins, int SilverCoins, int PlinkoTickets) CalculateCurrencyRewards(
        MatchRewards matchRewards,
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds)
    {
        EconomyConfiguration config = new()
        {
            MatchRewards = matchRewards,
            EventRewards = null!,
            SignupRewards = null!
        };
        EconomyService economy = new(db: null, logger: null, economyConfiguration: config, timeProvider: null);
        CurrencyDeltaDTO delta = economy.CalculateCurrencyRewards(groupSize, isWin, isMvp, durationSeconds);
        return (delta.GoldDelta, delta.SilverDelta, delta.PlinkoDelta);
    }
}
