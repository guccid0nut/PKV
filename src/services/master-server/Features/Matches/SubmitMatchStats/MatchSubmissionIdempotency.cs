// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Matches.SubmitMatchStats;

/// <summary>
///     Redis-backed idempotency guard for match statistics submissions, plus in-process completion waiters
///     so immediate post-match reads (<c>GetMatchInfo</c> / <c>custom-stats</c>) wait for in-flight DB commits.
///     <para>
///         Lifecycle of the <c>LOCK:MATCH_SUBMISSION:{matchId}</c> key:
///         <list type="number">
///             <item><b>QUEUED</b> (short TTL) — set atomically (<c>SET NX</c>) by the HTTP handler right before enqueueing.</item>
///             <item><b>COMPLETED</b> (long TTL) — set by the background worker after the database transaction commits.</item>
///             <item><b>Deleted</b> — when enqueueing or background processing fails, so the game server's retry is accepted.</item>
///         </list>
///     </para>
///     <para>
///         This guard is only a cheap pre-filter. The authoritative duplicate protection is the database:
///         unique indexes on <c>MatchStatistics.MatchID</c> and <c>PlayerStatistics(MatchID, AccountID)</c>, plus the
///         per-player "already recorded" skip inside <c>MatchIngestionHandler</c>. Therefore releasing the guard on
///         failure can never cause double MMR / double rewards — it only guarantees a failed match is never silently lost.
///     </para>
/// </summary>
public static class MatchSubmissionIdempotency
{
    public const string KeyPrefix = "LOCK:MATCH_SUBMISSION:";
    public const string StateQueued = "QUEUED";
    public const string StateCompleted = "COMPLETED";

    private sealed record InFlightWaiter(TaskCompletionSource<bool> Tcs, long CreatedTimestamp);

    private static readonly ConcurrentDictionary<int, InFlightWaiter> InFlightWaiters = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     How long a QUEUED marker survives if the worker never reports back (e.g. process crash).
    ///     After expiry a retry is accepted again; database idempotency protects against duplicates.
    /// </summary>
    public static readonly TimeSpan QueuedTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     How long a COMPLETED marker short-circuits retries without touching the queue or database.
    /// </summary>
    public static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);

    public static RedisKey Key(int matchId) => $"{KeyPrefix}{matchId}";

    // NOTE: The explicit (expiry, keepTtl, when, flags) overload is used on purpose. With StackExchange.Redis 2.13 the
    // short (TimeSpan, When) form can bind to the new (Expiration, ValueCondition) overload via implicit conversions,
    // which makes the call shape ambiguous to readers and to mocks.

    /// <summary>
    ///     Atomically claims the submission slot for <paramref name="matchId" />. Returns <c>false</c> if a
    ///     submission for this match is already queued or completed.
    /// </summary>
    public static async Task<bool> TryAcquireAsync(IDatabase distributedCache, int matchId)
    {
        bool acquired = await distributedCache.StringSetAsync(Key(matchId), StateQueued, expiry: QueuedTtl, keepTtl: false, when: When.NotExists,
            flags: CommandFlags.None).ConfigureAwait(false);

        if (acquired)
        {
            RegisterInFlightWaiter(matchId);
        }

        return acquired;
    }

    /// <summary>
    ///     Releases the submission slot so that a retry from the game server will be accepted and re-queued.
    /// </summary>
    public static async Task<bool> ReleaseAsync(IDatabase distributedCache, int matchId)
    {
        bool deleted = await distributedCache.KeyDeleteAsync(Key(matchId)).ConfigureAwait(false);
        CompleteInFlightWaiter(matchId, succeeded: false);
        return deleted;
    }

    /// <summary>
    ///     Marks the match as durably processed (called after the database transaction commits).
    /// </summary>
    public static async Task<bool> MarkCompletedAsync(IDatabase distributedCache, int matchId)
    {
        bool stored = await distributedCache.StringSetAsync(Key(matchId), StateCompleted, expiry: CompletedTtl, keepTtl: false, when: When.Always,
            flags: CommandFlags.None).ConfigureAwait(false);
        CompleteInFlightWaiter(matchId, succeeded: true);
        return stored;
    }

    /// <summary>
    ///     If <paramref name="matchId" /> currently has an in-flight background ingestion job (tracked either
    ///     in-process or via Redis <c>LOCK:MATCH_SUBMISSION:{matchId} == QUEUED</c>), awaits its completion up to
    ///     <paramref name="timeout" />. Returns <c>true</c> if an in-flight submission was observed and awaited.
    /// </summary>
    public static async Task<bool> WaitForCompletionAsync(
        IDatabase? distributedCache,
        int matchId,
        TimeSpan timeout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (matchId <= 0 || timeout <= TimeSpan.Zero)
        {
            return false;
        }

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        long startTimestamp = clock.GetTimestamp();

        if (InFlightWaiters.TryGetValue(matchId, out InFlightWaiter? waiter))
        {
            using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delayTask = Task.Delay(timeout, clock, delayCts.Token);
            try
            {
                Task completed = await Task.WhenAny(waiter.Tcs.Task, delayTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, waiter.Tcs.Task))
                {
                    await delayCts.CancelAsync().ConfigureAwait(false);
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // Timed out waiting on this in-process waiter; evict it so stale entries never accumulate.
                InFlightWaiters.TryRemove(new KeyValuePair<int, InFlightWaiter>(matchId, waiter));
                return true;
            }
            finally
            {
                if (waiter.Tcs.Task.IsCompleted)
                {
                    InFlightWaiters.TryRemove(new KeyValuePair<int, InFlightWaiter>(matchId, waiter));
                }
            }
        }

        if (distributedCache is null)
        {
            return false;
        }

        // Multi-instance fallback: another master-server instance may be processing this match in Redis.
        bool waited = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            RedisValue state;
            try
            {
                state = await distributedCache.StringGetAsync(Key(matchId)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceWarning("[MatchSubmissionIdempotency] Redis StringGetAsync failed while checking MatchID {0}: {1}", matchId, ex.Message);
                return waited;
            }

            if (state != StateQueued)
            {
                return waited;
            }

            waited = true;
            TimeSpan elapsed = clock.GetElapsedTime(startTimestamp);
            if (elapsed >= timeout)
            {
                return true;
            }

            TimeSpan remaining = timeout - elapsed;
            TimeSpan delay = remaining < PollInterval ? remaining : PollInterval;
            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        }

        return waited;
    }

    internal static bool HasInFlightWaiter(int matchId) => InFlightWaiters.ContainsKey(matchId);

    internal static int ActiveInFlightWaiterCount => InFlightWaiters.Count;

    private static void RegisterInFlightWaiter(int matchId)
    {
        long now = Stopwatch.GetTimestamp();
        InFlightWaiter next = new(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), now);
        InFlightWaiters.AddOrUpdate(
            matchId,
            next,
            (_, existing) =>
            {
                existing.Tcs.TrySetResult(false);
                return next;
            });
    }

    private static void CompleteInFlightWaiter(int matchId, bool succeeded)
    {
        if (InFlightWaiters.TryRemove(matchId, out InFlightWaiter? waiter))
        {
            waiter.Tcs.TrySetResult(succeeded);
        }
    }
}
