// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers;

[ApiController]
public class ReportController(
    IAccountRepository accountRepository,
    IMatchHistoryRepository matchHistoryRepository,
    ISessionContextResolver sessionResolver,
    ILogger<ReportController> logger) : ControllerBase
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;

    private const int MaxReportsPerRequest = 10;

    private sealed record RawReportItem(
        string Offender,
        string MatchIdStr,
        string TypeStr,
        string BodyStr,
        string TimeStr);

    [HttpPost("master/report/report")]
    [HttpPost("rap/report.php")]
    [HttpPost("rap/report2.php")]
    public async Task<IActionResult> SubmitReport(CancellationToken cancellationToken = default)
    {
        IFormCollection form = Request.Form;

        // 1. Require Valid Authenticated Reporter Session (never accept unauthenticated account_id spoofing)
        Account? reporter = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken);
        if (reporter is null)
        {
            logger.LogWarning(
                "[SECURITY-ALERT] [Report] Rejected unauthenticated player report submission from {RemoteIp}.",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOWN");
            return Unauthorized("Valid session required to submit player reports.");
        }

        string reporterName = reporter.Name;
        int reporterId = reporter.ID;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[Report] Incoming authenticated RAP submission from '{Reporter}' (ID: {ReporterId}, FieldCount: {FieldCount})",
                reporterName, reporterId, form.Count);
        }

        // 2. Extract structured player_report[i][...] entries OR flat array parameters, bounded by MaxReportsPerRequest
        List<RawReportItem> rawEntries = ExtractRawReportItems(form);

        // 3. Batch-resolve all target accounts in O(1) queries (at most 1 ID batch + 1 Nickname batch)
        HashSet<int> distinctTargetIds = [];
        HashSet<string> distinctTargetNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (RawReportItem item in rawEntries)
        {
            if (int.TryParse(item.Offender, out int parsedTargetId) && parsedTargetId > 0)
            {
                distinctTargetIds.Add(parsedTargetId);
            }
            else if (!string.IsNullOrWhiteSpace(item.Offender))
            {
                distinctTargetNames.Add(item.Offender);
            }
        }

        Dictionary<int, Account> targetsById = [];
        if (distinctTargetIds.Count > 0)
        {
            IReadOnlyList<Account> resolvedById = await AccountRepository.GetAccountsByIdsAsync(distinctTargetIds, cancellationToken) ?? [];
            foreach (Account acc in resolvedById)
            {
                targetsById[acc.ID] = acc;
            }
        }

        Dictionary<string, Account> targetsByName = new(StringComparer.OrdinalIgnoreCase);
        if (distinctTargetNames.Count > 0)
        {
            IReadOnlyList<Account> resolvedByName = await AccountRepository.GetAccountsByNamesAsync(distinctTargetNames, cancellationToken) ?? [];
            foreach (Account acc in resolvedByName)
            {
                targetsByName[acc.Name] = acc;
            }
        }

        List<PlayerReport> reportsToPersist = new(rawEntries.Count);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        foreach (RawReportItem item in rawEntries)
        {
            Account? targetAccount = null;
            int? targetId = null;

            if (int.TryParse(item.Offender, out int parsedTargetId) && parsedTargetId > 0)
            {
                targetsById.TryGetValue(parsedTargetId, out targetAccount);
                targetId = parsedTargetId;
            }
            else if (!string.IsNullOrWhiteSpace(item.Offender))
            {
                targetsByName.TryGetValue(item.Offender, out targetAccount);
            }

            string targetName = targetAccount?.Name ?? (!string.IsNullOrWhiteSpace(item.Offender) ? item.Offender : "UNKNOWN");
            targetId ??= targetAccount?.ID;

            int? parsedMatchId = int.TryParse(item.MatchIdStr, out int mid) ? mid : null;
            string finalReason = FormatReportReason(item.TypeStr);

            string? finalDescription = !string.IsNullOrWhiteSpace(item.BodyStr) ? item.BodyStr : null;
            if (!string.IsNullOrWhiteSpace(item.TimeStr) && int.TryParse(item.TimeStr, out int timeMinutes) && timeMinutes > 0)
                finalDescription = string.IsNullOrWhiteSpace(finalDescription)
                    ? $"(Match Time: {timeMinutes}m)"
                    : $"{finalDescription} (Match Time: {timeMinutes}m)";

            reportsToPersist.Add(new PlayerReport
            {
                ReporterAccountID = reporterId,
                ReporterAccountName = reporterName,
                ReportedAccountID = targetId,
                ReportedAccountName = targetName,
                MatchID = parsedMatchId,
                Reason = finalReason,
                Description = finalDescription,
                Status = "PENDING",
                TimestampCreated = now
            });
        }

        await AccountRepository.SavePlayerReportsAsync(reportsToPersist, cancellationToken);

        int lastCreatedReportId = reportsToPersist.Count > 0 ? reportsToPersist[^1].ID : 0;

        if (logger.IsEnabled(LogLevel.Information))
        {
            foreach (PlayerReport report in reportsToPersist)
            {
                logger.LogInformation(
                    "[Report] Successfully filed Player Report #{ReportId} from '{Reporter}' (ID: {ReporterId}) against '{Target}' (ID: {TargetId}) for Match ID '{MatchId}', Reason: '{Reason}', Comment: '{Comment}'",
                    report.ID, reporterName, reporterId, report.ReportedAccountName, report.ReportedAccountID,
                    report.MatchID, report.Reason, report.Description);
            }
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = "true",
            ["report_success"] = "1",
            ["report_enabled"] = "1",
            ["status"] = "OK",
            ["report_id"] = lastCreatedReportId.ToString(),
            ["message"] = "Report successfully received.",
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private static List<RawReportItem> ExtractRawReportItems(IFormCollection form)
    {
        StringValues globalMatchIds = GetFormValues(form, "match_id[]", "match_id", "matchId", "matchId[]", "match");
        string defaultMatchId = globalMatchIds.Count > 0 ? globalMatchIds[0]?.Trim() ?? "" : "";

        List<RawReportItem> indexedItems = [];
        for (int i = 0; i < MaxReportsPerRequest; i++)
        {
            string prefix = $"player_report[{i}]";
            StringValues indexedOffender = GetFormValues(
                form,
                $"{prefix}[reported_id]",
                $"{prefix}[reported_account_id]",
                $"{prefix}[reported_nickname]",
                $"{prefix}[offender_name]",
                $"{prefix}[nickname]");

            if (indexedOffender.Count == 0)
            {
                if (i == 0) break;
                continue;
            }

            StringValues indexedReason = GetFormValues(form, $"{prefix}[reason]", $"{prefix}[type]", $"{prefix}[reason_id]");
            StringValues indexedComment = GetFormValues(form, $"{prefix}[comment]", $"{prefix}[body]", $"{prefix}[description]");
            StringValues indexedMatch = GetFormValues(form, $"{prefix}[match_id]");
            StringValues indexedTime = GetFormValues(form, $"{prefix}[time]", $"{prefix}[timestamp]");

            indexedItems.Add(new RawReportItem(
                indexedOffender[0]?.Trim() ?? "",
                indexedMatch.Count > 0 ? indexedMatch[0]?.Trim() ?? "" : defaultMatchId,
                indexedReason.Count > 0 ? indexedReason[0]?.Trim() ?? "" : "",
                indexedComment.Count > 0 ? indexedComment[0]?.Trim() ?? "" : "",
                indexedTime.Count > 0 ? indexedTime[0]?.Trim() ?? "" : ""));
        }

        if (indexedItems.Count > 0)
        {
            return indexedItems;
        }

        StringValues offenderList = GetFormValues(form, "offenderName[]", "offenderName", "offender_name[]",
            "offender_name", "reportee_name[]", "reportee_name", "reported_nickname", "reported_account_id", "target",
            "target_name", "reported_player", "reportedPlayer", "reportedPlayer[]");
        StringValues typeList = GetFormValues(form, "type[]", "type", "reason_type[]", "reason_type", "reason",
            "reason_id", "reason[]", "reason_id[]", "offense_type", "offense_type[]", "report_type", "report_type[]");
        StringValues bodyList = GetFormValues(form, "body[]", "body", "report_description[]", "report_description",
            "comment", "comment[]", "comments", "reason_description", "description", "description[]", "text");
        StringValues timeList = GetFormValues(form, "time[]", "time", "timestamp", "timestamp[]");

        int entryCount = Math.Min(MaxReportsPerRequest, Math.Max(1, offenderList.Count));
        List<RawReportItem> flatItems = new(entryCount);

        for (int i = 0; i < entryCount; i++)
        {
            string offender = i < offenderList.Count ? offenderList[i]?.Trim() ?? "" : "";
            string matchIdStr = i < globalMatchIds.Count ? globalMatchIds[i]?.Trim() ?? "" : defaultMatchId;
            string typeStr = i < typeList.Count ? typeList[i]?.Trim() ?? "" :
                typeList.Count > 0 ? typeList[0]?.Trim() ?? "" : "";
            string bodyStr = i < bodyList.Count ? bodyList[i]?.Trim() ?? "" :
                bodyList.Count > 0 ? bodyList[0]?.Trim() ?? "" : "";
            string timeStr = i < timeList.Count ? timeList[i]?.Trim() ?? "" :
                timeList.Count > 0 ? timeList[0]?.Trim() ?? "" : "";

            flatItems.Add(new RawReportItem(offender, matchIdStr, typeStr, bodyStr, timeStr));
        }

        return flatItems;
    }

    [HttpPost("rap/match_list2.php")]
    public async Task<IActionResult> GetRecentMatchesForReport(CancellationToken cancellationToken = default)
    {
        IFormCollection form = Request.Form;

        Account? reporter = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken);
        int accountId = reporter?.ID ?? 0;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Report] Fetching recent matches for RAP dialogue (Account: {AccountName}, ID: {AccountId})",
                reporter?.Name ?? "UNKNOWN", accountId);

        IReadOnlyList<PlayerStatistics> recentPlayerStats = await MatchHistoryRepository.GetRecentPlayerStatisticsByAccountIdAsync(
            accountId, 10, cancellationToken);

        Dictionary<object, object> matchesDict = [];
        int index = 0;
        foreach (PlayerStatistics ps in recentPlayerStats)
            matchesDict[index++] = new Dictionary<string, object>
            {
                ["match_id"] = ps.MatchID.ToString(),
                ["hero"] = ps.HeroIdentifier,
                ["date"] = TimeProvider.System.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss")
            };

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["matches"] = matchesDict,
            ["count"] = matchesDict.Count,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("rap/player_list.php")]
    public async Task<IActionResult> GetPlayersFromMatch(CancellationToken cancellationToken = default)
    {
        IFormCollection form = Request.Form;
        string matchIdStr = form["match_id"].ToString();

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Report] Fetching players list for Match ID: {MatchId}", matchIdStr);

        Dictionary<object, object> playersDict = [];

        if (int.TryParse(matchIdStr, out int matchId))
        {
            IReadOnlyList<PlayerStatistics> players = await MatchHistoryRepository.GetPlayerStatisticsForMatchAsync(
                matchId, cancellationToken);

            int index = 0;
            foreach (PlayerStatistics p in players)
                playersDict[index++] = new Dictionary<string, object>
                {
                    ["account_id"] = p.AccountID.ToString(),
                    ["nickname"] = p.AccountName,
                    ["team"] = p.Team.ToString(),
                    ["hero"] = p.HeroIdentifier
                };
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["match_id"] = matchIdStr,
            ["players"] = playersDict,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private static StringValues GetFormValues(IFormCollection form, params string[] keys)
    {
        foreach (string key in keys)
            if (form.TryGetValue(key, out StringValues val) && val.Count > 0)
                return val;
        return StringValues.Empty;
    }

    private static string FormatReportReason(string reasonStr)
    {
        return reasonStr.Trim().ToLowerInvariant() switch
        {
            "1" or "verbal" or "verbal abuse" or "harassment" => "Verbal Abuse",
            "2" or "griefing" or "feeding" => "Intentional Griefing / Feeding",
            "3" or "cheating" or "hacking" => "Cheating / Third-Party Exploits",
            "4" or "afk" or "leaving" => "AFK / Leaving / Non-Participation",
            "5" or "statpadding" => "Stat Manipulation / Win-Trading",
            "6" => "AFK / Leaving / Non-Participation",
            "badnickname" or "bad_nickname" => "Inappropriate Nickname",
            _ => !string.IsNullOrWhiteSpace(reasonStr) ? reasonStr : "General Misconduct"
        };
    }
}
