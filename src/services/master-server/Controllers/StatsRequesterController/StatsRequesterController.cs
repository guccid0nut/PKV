// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Services.Background;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers.StatsRequesterController;

[ApiController]
[Route("stats_requester.php")]
[Consumes("application/x-www-form-urlencoded")]
public partial class StatsRequesterController(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    IMatchProcessingChannel matchProcessingChannel,
    ILogger<StatsRequesterController> logger,
    ISubmitMatchStatsHandler? submitMatchStatsHandler = null) : ControllerBase
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IMatchProcessingChannel MatchProcessingChannel { get; } = matchProcessingChannel;
    private ILogger Logger { get; } = logger;
    private ISubmitMatchStatsHandler SubmitMatchStatsHandler { get; } =
        submitMatchStatsHandler ?? new SubmitMatchStatsHandler(
            accountRepository,
            distributedCache,
            matchProcessingChannel,
            NullLogger<SubmitMatchStatsHandler>.Instance);


    /// <summary>
    ///     <para>
    ///         The stats resubmission key is the SHA1 hash of the match ID prepended to this salt.
    ///     </para>
    ///     <code>
    ///         StatsResubmissionKey = SHA1.HashData(Encoding.UTF8.GetBytes(matchID + MatchStatsSubmissionSalt));
    ///     </code>
    ///     <para>
    ///         This key can be found at offset 0x00F03A10 in k2_x64.dll of the WAS distribution.
    ///     </para>
    /// </summary>
    private static string MatchStatsSubmissionSalt =>
        "s8c7xaduxAbRanaspUf3kadRachecrac9efeyupr8suwrewecrUphayeweqUmana";

    // For Debugging Purposes, The "GiveGold" And "GiveExp" Commands (Case-Insensitive) Can Be Used From The Server Console To Complete Matches Quickly And Send Stats
    // e.g. #1: "givegold 0 65535" To Give 65535 Gold To Player Index 0 (The First Player), Or "givegold KONGOR 65535" To Give 65535 Gold To Player With Name "KONGOR"
    // e.g. #2: "giveexp KONGOR 65535" To Give 65535 Experience To Player With Name "KONGOR" (Unlike The "GiveGold" Command, The "GiveExp" Command Does Not Work With A Player Index)
    // NOTE #1: 1v1 Matches Are A Good Way To Test The Stats Submission System, As They Are The Quickest To Complete
    // NOTE #2: Another Quick Way To Test The Stats Submission System Is To Replay A Fiddler/Requestly/etc. Request Or Make A Postman/Insomnia/etc. Request With The Required Form Data

    [HttpPost(Name = "Stats Requester All-In-One")]
    public async Task<IActionResult> StatsRequester([FromForm] StatsForSubmissionRequestForm form,
        CancellationToken cancellationToken = default)
    {
        // 2026-01-07: Normalize to lowercase for case-insensitive handling (PHP parity).
        return Request.Form["f"].FirstOrDefault()?.ToLower() switch
        {
            "submit_stats" => await HandleStatsSubmission(form, cancellationToken),
            "resubmit_stats" => await HandleStatsResubmission(form, cancellationToken),

            _ => throw new NotImplementedException(
                $"Unsupported Stats Requester Controller Form Parameter: f={Request.Form["f"].FirstOrDefault()}")
        };
    }
}
