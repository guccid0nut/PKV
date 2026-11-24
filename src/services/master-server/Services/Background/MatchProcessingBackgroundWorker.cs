// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Hosting;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Services.Matches;

namespace KONGOR.MasterServer.Services.Background;

public sealed class MatchProcessingBackgroundWorker(
    IMatchProcessingChannel channel,
    IServiceScopeFactory scopeFactory,
    ILogger<MatchProcessingBackgroundWorker> logger,
    IMatchDeadLetterQueue? deadLetterQueue = null) : ResilientBackgroundService(logger, nameof(MatchProcessingBackgroundWorker))
{
    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[MatchProcessingBackgroundWorker] Background match processing worker started with {PartitionCount} dedicated partition workers.",
                channel.PartitionCount);

        Task[] workerTasks = new Task[channel.PartitionCount];
        for (int i = 0; i < channel.PartitionCount; i++)
        {
            int partitionIndex = i;
            workerTasks[i] = ProcessPartitionAsync(partitionIndex, stoppingToken);
        }

        await Task.WhenAll(workerTasks).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[MatchProcessingBackgroundWorker] Background match processing worker stopped.");
    }

    private async Task ProcessPartitionAsync(int partitionIndex, CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("[MatchProcessingBackgroundWorker] Partition worker #{PartitionIndex} started.", partitionIndex);

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await foreach (MatchSubmissionPayload payload in channel.ReadAllAsync(partitionIndex, stoppingToken)
                                   .ConfigureAwait(false))
                    await ProcessMatchPayloadAsync(payload, partitionIndex, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex,
                        "[MatchProcessingBackgroundWorker] Error during match background processing loop in Partition #{PartitionIndex}.",
                        partitionIndex);

                // Brief backoff before resuming read on this partition
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("[MatchProcessingBackgroundWorker] Partition worker #{PartitionIndex} stopped.", partitionIndex);
    }

    public Task ProcessMatchPayloadAsync(MatchSubmissionPayload payload, CancellationToken cancellationToken)
    {
        return ProcessMatchPayloadAsync(payload, -1, cancellationToken);
    }

    public async Task ProcessMatchPayloadAsync(MatchSubmissionPayload payload, int partitionIndex,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IMatchProcessingService matchProcessingService =
            scope.ServiceProvider.GetRequiredService<IMatchProcessingService>();

        try
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(
                    "[MatchProcessingBackgroundWorker] Processing Match ID: {MatchId} on Partition #{PartitionIndex} (Account: {AccountId})",
                    payload.MatchID, partitionIndex, payload.AccountId);

            await matchProcessingService.ProcessMatchSubmissionAsync(payload, cancellationToken);
            await channel.AcknowledgeAsync(payload, cancellationToken).ConfigureAwait(false);
            await UpdateIdempotencyGuardAsync(scope, payload.MatchID, succeeded: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex,
                    "[MatchProcessingBackgroundWorker] Failed to process background match payload for Match ID: {MatchId} on Partition #{PartitionIndex}",
                    payload.MatchID, partitionIndex);

            // Release the submission guard so a retry from the game server is accepted (DB indexes prevent duplicates).
            await UpdateIdempotencyGuardAsync(scope, payload.MatchID, succeeded: false).ConfigureAwait(false);

            if (deadLetterQueue != null)
            {
                try
                {
                    DeadLetterMatchJob job = new()
                    {
                        MatchId = payload.MatchID,
                        PartitionIndex = partitionIndex,
                        Payload = payload,
                        FailureReason = ex.Message,
                        ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                        StackTrace = ex.StackTrace,
                        FailedAt = TimeProvider.System.GetUtcNow()
                    };
                    await deadLetterQueue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception dlqEx)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError(dlqEx,
                            "[MatchProcessingBackgroundWorker] Failed to route failed Match ID: {MatchId} to Dead-Letter Queue",
                            payload.MatchID);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Transitions the Redis submission guard (see <see cref="MatchSubmissionIdempotency" />).
    ///     Never throws: guard bookkeeping must not fail an already-committed match or mask the original failure.
    /// </summary>
    private async Task UpdateIdempotencyGuardAsync(IServiceScope scope, int matchId, bool succeeded)
    {
        try
        {
            StackExchange.Redis.IDatabase? distributedCache = scope.ServiceProvider.GetService<StackExchange.Redis.IDatabase>();
            if (distributedCache is null)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "[MatchProcessingBackgroundWorker] No IDatabase registered; skipping idempotency guard update for Match ID: {MatchId}",
                        matchId);
                return;
            }

            if (succeeded)
            {
                await MatchSubmissionIdempotency.MarkCompletedAsync(distributedCache, matchId).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "[MatchProcessingBackgroundWorker] Idempotency guard for Match ID: {MatchId} marked {State} (TTL: {Ttl}).",
                        matchId, MatchSubmissionIdempotency.StateCompleted, MatchSubmissionIdempotency.CompletedTtl);
            }
            else
            {
                await MatchSubmissionIdempotency.ReleaseAsync(distributedCache, matchId).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "[MatchProcessingBackgroundWorker] Idempotency guard for Match ID: {MatchId} released after processing failure; a resubmission from the match server will be accepted.",
                        matchId);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(ex,
                    "[MatchProcessingBackgroundWorker] Failed to update idempotency guard for Match ID: {MatchId} (Succeeded: {Succeeded}).",
                    matchId, succeeded);
        }
    }
}
