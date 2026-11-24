// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Background;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Matches.SubmitMatchStats;

public interface ISubmitMatchStatsHandler
{
    Task<SubmitMatchStatsResponse> HandleAsync(SubmitMatchStatsCommand command, CancellationToken ct = default);
}

public sealed class SubmitMatchStatsHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    IMatchProcessingChannel matchProcessingChannel,
    ILogger<SubmitMatchStatsHandler> logger,
    IMatchDeadLetterQueue? deadLetterQueue = null) : ISubmitMatchStatsHandler
{
    /// <summary>Dead-letter failure category for submissions whose session maps to no server and no account.</summary>
    public const string QuarantineReasonOrphanedSession = "Quarantined:OrphanedSession";

    /// <summary>Dead-letter failure category for submissions from a server that did not host the match.</summary>
    public const string QuarantineReasonServerMismatch = "Quarantined:ServerMismatch";

    /// <summary>Dead-letter failure category for P2P Listen Server submissions where the session owner is not a participant in PlayerStats.</summary>
    public const string QuarantineReasonListenServerNonParticipant = "Quarantined:ListenServerNonParticipant";

    /// <summary>Dead-letter failure category for dedicated server submissions whose backing host account is not AccountType.ServerHost.</summary>
    public const string QuarantineReasonUnauthorizedHostType = "Quarantined:UnauthorizedHostType";

    private readonly SubmitMatchStatsValidator _validator = new();

    public async Task<SubmitMatchStatsResponse> HandleAsync(SubmitMatchStatsCommand command, CancellationToken ct = default)
    {
        (bool isValid, string? errorMessage) = _validator.Validate(command);
        if (!isValid)
        {
            return SubmitMatchStatsResponse.BadRequest(errorMessage ?? "Invalid match submission");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[StatsSubmission] Received Stats Submission For Session: {SessionPrefix}",
                SecurityMasking.MaskToken(command.Session));
        }

        int matchId = command.MatchId.Value;
        MatchServer? matchServer = await distributedCache.GetMatchServerBySessionCookie(command.Session!);
        bool resolvedFromRegisteredServer = matchServer != null;
        bool isUnrankedListenServer = false;

        if (matchServer != null && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[StatsSubmission] Session {SessionPrefix} Found In Redis. Server ID: {ServerId}",
                SecurityMasking.MaskToken(command.Session),
                matchServer.ID);
        }

        if (matchServer == null && !string.IsNullOrEmpty(command.Session))
        {
            string? sessionAccountName = await distributedCache.GetAccountNameForSessionCookie(command.Session);
            if (sessionAccountName != null)
            {
                Account? account = await accountRepository.GetByNicknameAsync(sessionAccountName, ct);

                if (account != null && !account.IsBanned && account.Type != AccountType.Disabled)
                {
                    MatchStartData? matchStartData = await distributedCache.GetMatchStartData(matchId);
                    int serverId = 0;
                    if (matchStartData != null)
                        serverId = matchStartData.ServerID;
                    else if (command.Form.MatchStats.TryGetValue("server_id", out string? serverIdStr) &&
                             int.TryParse(serverIdStr, out int parsedServerId))
                        serverId = parsedServerId;

                    MatchServer listenServerContext = new()
                    {
                        ID = serverId,
                        HostAccountName = account.Name,
                        HostAccountID = account.ID,
                        Name = "Listen Server (Unranked)",
                        Instance = 1,
                        IPAddress = command.RemoteIp,
                        Port = 0,
                        Location = "Local",
                        Description = "Listen Server (Unranked Participant Context)"
                    };

                    if (!IsSubmittingAccountMatchParticipant(account, command.Form))
                    {
                        logger.LogWarning(
                            "[SECURITY-ALERT] [StatsSubmission] Blocked Listen Server stats submission for Match ID {MatchId} from non-participant account '{AccountName}' (ID: {AccountId}, Remote IP: {RemoteIp}). Quarantining to dead-letter queue.",
                            matchId, account.Name, account.ID, command.RemoteIp);

                        await QuarantineAsync(
                            command,
                            matchId,
                            listenServerContext,
                            QuarantineReasonListenServerNonParticipant,
                            $"Submitting player session '{account.Name}' (ID={account.ID}) is not a participant in Match {matchId} PlayerStats.",
                            ct);

                        return SubmitMatchStatsResponse.Succeeded(matchId);
                    }

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "[StatsSubmission] Session {SessionPrefix} Resolved To Verified Match Participant {AccountName} (ID: {AccountId}). Treating As Unranked Listen Server Submission For Match ID {MatchId} (Server ID: {ServerId}, MatchStartData: {HasStartData}, 0 MMR / 0 Coins).",
                            SecurityMasking.MaskToken(command.Session), account.Name, account.ID, matchId, serverId,
                            matchStartData != null);
                    }

                    matchServer = listenServerContext;
                    isUnrankedListenServer = true;
                }
            }
        }

        if (matchServer == null)
        {
            // SECURITY: The session maps to neither a registered match server nor an authenticated account.
            // Previously this path fabricated an "Orphaned Server" context and fed the stats into the rating/economy
            // pipeline, which let any caller forge match results. We still answer with the legacy success payload
            // (so the submitting client is not logged out), but the stats are QUARANTINED in the dead-letter queue
            // (in-memory + forensic JSON dump under logs/dead_letter_matches/) instead of being processed.
            // An operator can inspect and replay a legitimate submission via IMatchDeadLetterQueue.ReplayJobAsync.
            int claimedServerId = 0;
            if (command.Form.MatchStats.TryGetValue("server_id", out string? serverIdStr) &&
                int.TryParse(serverIdStr, out int parsedServerId))
                claimedServerId = parsedServerId;

            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[StatsSubmission] SECURITY: Session {SessionPrefix} Not Found During Stats Submission For Match ID {MatchId} (Claimed Server ID: {ClaimedServerId}, Remote IP: {RemoteIp}). No registered server or authenticated account. Quarantining submission to dead-letter queue (NOT processing ratings/rewards).",
                    SecurityMasking.MaskToken(command.Session), matchId, claimedServerId, command.RemoteIp);
            }

            MatchServer orphanedContext = new()
            {
                ID = claimedServerId,
                HostAccountName = "Unknown",
                HostAccountID = -1,
                Name = "Orphaned Server (Quarantined)",
                Instance = 1,
                IPAddress = command.RemoteIp,
                Port = 0,
                Location = "Local",
                Description = "Orphaned Server (Quarantined, Not Processed)"
            };

            await QuarantineAsync(command, matchId, orphanedContext, QuarantineReasonOrphanedSession,
                $"Session not mapped to any registered match server or authenticated account (claimed server_id={claimedServerId}).",
                ct);

            return SubmitMatchStatsResponse.Succeeded(matchId);
        }

        if (resolvedFromRegisteredServer)
        {
            if (IsQueueArcadeMatchServer(matchServer!))
            {
                logger.LogInformation(
                    "[StatsSubmission] Ignoring Queue Arcade warm match stats submission for Match ID {MatchId} from Server ID {ServerId} ({HostAccountName} / '{ServerName}'). Zero MMR, coin, or leaver side effects.",
                    matchId, matchServer!.ID, matchServer.HostAccountName, matchServer.Name);
                return SubmitMatchStatsResponse.Succeeded(matchId);
            }

            if (matchServer.HostAccountID > 0)
            {
                Account? hostAccount = await accountRepository.GetByIdAsync(matchServer.HostAccountID, ct);
                if (hostAccount is not null && hostAccount.Type != AccountType.ServerHost)
                {
                    logger.LogWarning(
                        "[SECURITY-ALERT] [StatsSubmission] Registered server session (Server ID: {ServerId}) maps to non-ServerHost account '{AccountName}' (ID: {AccountId}, Type: {AccountType}, Remote IP: {RemoteIp}) for Match ID {MatchId}. Quarantining submission.",
                        matchServer.ID, hostAccount.Name, hostAccount.ID, hostAccount.Type, command.RemoteIp, matchId);

                    await QuarantineAsync(
                        command,
                        matchId,
                        matchServer,
                        QuarantineReasonUnauthorizedHostType,
                        $"Registered server {matchServer.ID} host account '{hostAccount.Name}' (ID={hostAccount.ID}) has Type={hostAccount.Type} instead of ServerHost.",
                        ct);

                    return SubmitMatchStatsResponse.Succeeded(matchId);
                }
            }

            // SECURITY: A registered server may only submit stats for matches it actually started.
            MatchStartData? startData = await distributedCache.GetMatchStartData(matchId);
            if (startData is { ServerID: > 0 } && startData.ServerID != matchServer.ID)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "[SECURITY-ALERT] [StatsSubmission] SECURITY: Server ID {SubmittingServerId} (Host: {HostAccountName}, Remote IP: {RemoteIp}) Submitted Stats For Match ID {MatchId}, But That Match Was Started On Server ID {OwningServerId}. Quarantining submission to dead-letter queue.",
                        matchServer.ID, matchServer.HostAccountName, command.RemoteIp, matchId, startData.ServerID);
                }

                await QuarantineAsync(command, matchId, matchServer, QuarantineReasonServerMismatch,
                    $"Submitting server {matchServer.ID} does not own match {matchId} (owner server {startData.ServerID}).",
                    ct);

                return SubmitMatchStatsResponse.Succeeded(matchId);
            }

            if (startData == null && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "[StatsSubmission] No MatchStartData cached for Match ID {MatchId}; skipping server ownership check for Server ID {ServerId}.",
                    matchId, matchServer.ID);
            }
        }

        // Cheap Redis pre-filter against duplicate submissions. The database (unique MatchID / (MatchID, AccountID)
        // indexes + per-player skip in MatchIngestionHandler) remains the authoritative duplicate guard.
        bool isFirstSubmission = await MatchSubmissionIdempotency.TryAcquireAsync(distributedCache, matchId);

        if (!isFirstSubmission)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[StatsSubmission] Duplicate submission received for Match ID {MatchId} (already queued or completed). Skipping duplicate queueing to prevent duplicate MMR and stats processing.",
                    matchId);
            }

            return SubmitMatchStatsResponse.Succeeded(matchId);
        }

        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId,
            Form = command.Form,
            MatchServer = matchServer,
            RemoteIp = command.RemoteIp,
            AccountId = matchServer.HostAccountID,
            IsUnrankedListenServer = isUnrankedListenServer
        };

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[StatsSubmission] Enqueueing Match ID {MatchId} to background processing queue before returning OK to match server (Server ID: {ServerId}, Host Account ID: {HostAccountId}).",
                matchId, matchServer.ID, matchServer.HostAccountID);
        }

        bool enqueued;
        try
        {
            enqueued = await matchProcessingChannel.EnqueueAsync(payload, ct);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex,
                    "[StatsSubmission] Failed to enqueue Match ID {MatchId}. Releasing idempotency guard and returning 503 so the match server retries.",
                    matchId);
            }

            await SafeReleaseGuardAsync(matchId);
            return SubmitMatchStatsResponse.ServiceUnavailable($"Match {matchId} could not be queued for processing; retry later.");
        }

        if (!enqueued)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(
                    "[StatsSubmission] Processing queue rejected Match ID {MatchId} (channel closed or full). Releasing idempotency guard and returning 503 so the match server retries.",
                    matchId);
            }

            await SafeReleaseGuardAsync(matchId);
            return SubmitMatchStatsResponse.ServiceUnavailable($"Match {matchId} could not be queued for processing; retry later.");
        }

        return SubmitMatchStatsResponse.Succeeded(matchId);
    }

    private async Task QuarantineAsync(SubmitMatchStatsCommand command, int matchId, MatchServer context,
        string reasonCategory, string detail, CancellationToken ct)
    {
        if (deadLetterQueue is null)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(
                    "[StatsSubmission] No dead-letter queue registered; quarantined submission for Match ID {MatchId} ({Reason}: {Detail}) is DROPPED.",
                    matchId, reasonCategory, detail);
            }

            return;
        }

        try
        {
            await deadLetterQueue.EnqueueAsync(new DeadLetterMatchJob
            {
                MatchId = matchId,
                PartitionIndex = -1,
                Payload = new MatchSubmissionPayload
                {
                    MatchID = matchId,
                    Form = command.Form,
                    MatchServer = context,
                    RemoteIp = command.RemoteIp,
                    AccountId = context.HostAccountID
                },
                FailureReason = reasonCategory,
                ExceptionType = "SecurityQuarantine",
                StackTrace = detail,
                FailedAt = TimeProvider.System.GetUtcNow()
            }, ct);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex,
                    "[StatsSubmission] Failed to quarantine Match ID {MatchId} into dead-letter queue ({Reason}).",
                    matchId, reasonCategory);
            }
        }
    }

    private static bool IsSubmittingAccountMatchParticipant(Account account, StatsForSubmissionRequestForm form)
    {
        if (form.PlayerStats is null || form.PlayerStats.Count == 0)
        {
            return false;
        }

        string accountIdStr = account.ID.ToString();

        foreach ((int playerIndex, Dictionary<string, Dictionary<string, string>> heroMap) in form.PlayerStats)
        {
            if (heroMap is null) continue;

            foreach (Dictionary<string, string> statsDict in heroMap.Values)
            {
                if (statsDict is null) continue;

                if (statsDict.TryGetValue("nickname", out string? rawNickname) && !string.IsNullOrWhiteSpace(rawNickname))
                {
                    string cleanNickname = AccountExtensions.SeparateClanTagFromAccountName(rawNickname).AccountName;
                    if (string.Equals(cleanNickname, account.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    continue;
                }

                if ((statsDict.TryGetValue("aid", out string? aid) && aid == accountIdStr) ||
                    (statsDict.TryGetValue("account_id", out string? accId) && accId == accountIdStr) ||
                    playerIndex == account.ID)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsQueueArcadeMatchServer(MatchServer server) =>
        server.IsQueueArcadeServer();

    private async Task SafeReleaseGuardAsync(int matchId)
    {
        try
        {
            await MatchSubmissionIdempotency.ReleaseAsync(distributedCache, matchId);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(ex,
                    "[StatsSubmission] Failed to release idempotency guard for Match ID {MatchId}; retries will be ignored until it expires ({Ttl}).",
                    matchId, MatchSubmissionIdempotency.QueuedTtl);
            }
        }
    }
}
