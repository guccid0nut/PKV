// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Services.Background;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Domain.Identifiers;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.StatsRequesterController;

public partial class StatsRequesterController
{
    private async Task<IActionResult> HandleStatsSubmission(StatsForSubmissionRequestForm form,
        CancellationToken cancellationToken = default)
    {
        if (form.Session is null) return BadRequest(@"Missing Value For Form Parameter ""session""");

        if (!int.TryParse(form.MatchStats?["match_id"], out int matchID)) return BadRequest("Invalid Match ID");

        string remoteIp = Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1";

        SubmitMatchStatsCommand command = new(
            new MatchId(matchID),
            form.Session,
            form,
            remoteIp);

        SubmitMatchStatsResponse result = await SubmitMatchStatsHandler.HandleAsync(command, cancellationToken);
        if (!result.Success)
        {
            return StatusCode(result.StatusCode, result.ErrorMessage);
        }

        return Ok(PhpSerialization.Serialize(result.ResponseData));
    }

    private async Task<IActionResult> HandleStatsResubmission(StatsForSubmissionRequestForm form,
        CancellationToken cancellationToken = default)
    {
        if (form.HostAccountName is null) return BadRequest(@"Missing Value For Form Parameter ""login""");

        // The Host Account Name Is Expected To End With A Colon (":") Character
        // Which The Match Server Launcher Uses To Separate The Host Account Name From The Match Server Instance ID
        form.HostAccountName = form.HostAccountName.TrimEnd(':');

        Account? hostAccount = await AccountRepository.GetByNicknameAsync(form.HostAccountName, cancellationToken);

        if (hostAccount is null)
            return NotFound(@"Unable To Retrieve Account For Host Account Name");

        string remoteIp = Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1";

        if (hostAccount.Type != AccountType.ServerHost || hostAccount.IsBanned)
        {
            Logger.LogWarning(
                "[SECURITY-ALERT] Rejected f=resubmit_stats from unauthorized account '{AccountName}' (ID={AccountId}, Type={AccountType}, Banned={IsBanned}, RemoteIp={RemoteIp}).",
                hostAccount.Name, hostAccount.ID, hostAccount.Type, hostAccount.IsBanned, remoteIp);
            return Unauthorized("Host account is not authorized as a ServerHost");
        }

        if (form.HostAccountPasswordHash is null) return BadRequest(@"Missing Value For Form Parameter ""pass""");

        string computedSRPPasswordHash =
            SRPAuthenticationHandlers.ComputeSRPPasswordHash(form.HostAccountPasswordHash,
                hostAccount.User.SRPPasswordSalt);
        string expectedSRPPasswordHash = hostAccount.User.SRPPasswordHash;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedSRPPasswordHash),
                Encoding.UTF8.GetBytes(expectedSRPPasswordHash)))
            return Unauthorized("Invalid Host Account Password");

        if (form.StatsResubmissionKey is null)
            return BadRequest(@"Missing Value For Form Parameter ""resubmission_key""");

        if (!int.TryParse(form.MatchStats["match_id"], out int matchID) || matchID <= 0)
            return BadRequest("Invalid Match ID");

        string computedStatsResubmissionKey = Convert
            .ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(matchID + MatchStatsSubmissionSalt))).ToLower();
        string expectedStatsResubmissionKey = form.StatsResubmissionKey;

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedStatsResubmissionKey),
                Encoding.UTF8.GetBytes(expectedStatsResubmissionKey)))
            return Unauthorized("Invalid Match Statistics Resubmission Key");

        if (form.ServerID is null) return BadRequest(@"Missing Value For Form Parameter ""server_id""");

        MatchServer? matchServer =
            (await DistributedCache.GetMatchServersByAccountName(hostAccount.Name)).FirstOrDefault(server =>
                server.ID == form.ServerID);

        if (matchServer is null && Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                @"Match Server ID {FormServerId} Hosted By ""{HostAccountName}"" Is No Longer Online While Match Statistics For Match ID {MatchId} Are Being Resubmitted",
                form.ServerID, hostAccount.Name, matchID);
        }

        Dictionary<string, object> response = new()
        {
            { "match_id", matchID },
            { "match_info", "OK" },
            { "match_summ", "OK" },
            { "match_stats", "OK" },
            { "match_history", "OK" }
        };

        bool isFirstSubmission = await MatchSubmissionIdempotency.TryAcquireAsync(DistributedCache, matchID);
        if (!isFirstSubmission)
        {
            Logger.LogWarning(
                "[StatsSubmission] Duplicate resubmit_stats received for Match ID {MatchId} from ServerHost '{HostAccountName}'. Returning idempotent OK.",
                matchID, hostAccount.Name);
            return Ok(PhpSerialization.Serialize(response));
        }

        MatchSubmissionPayload payload = new()
        {
            MatchID = matchID,
            Form = form,
            MatchServer = matchServer,
            RemoteIp = remoteIp,
            AccountId = hostAccount.ID,
            IsUnrankedListenServer = false
        };

        try
        {
            bool enqueued = await MatchProcessingChannel.EnqueueAsync(payload, cancellationToken);
            if (!enqueued)
            {
                await MatchSubmissionIdempotency.ReleaseAsync(DistributedCache, matchID);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    $"Match {matchID} could not be queued for processing; retry later.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[StatsSubmission] Failed to enqueue resubmit_stats for Match ID {MatchId}; releasing idempotency guard.", matchID);
            await MatchSubmissionIdempotency.ReleaseAsync(DistributedCache, matchID);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                $"Match {matchID} could not be queued for processing; retry later.");
        }

        return Ok(PhpSerialization.Serialize(response));
    }
}
