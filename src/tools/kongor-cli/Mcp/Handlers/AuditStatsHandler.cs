// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using NEXUS.Contracts.CQRS.PlayerStats;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_audit_stats' (Phase 5 Subsystem A).
/// Autonomous telemetry and UI verification sweep comparing client-rendered stats/match history
/// against master-server/DB ground truth, detecting anomalies and logging structured issues.
/// </summary>
public sealed class AuditStatsHandler
{
    private readonly IClientStatsAuditorService _auditorService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly IMediator? _mediator;

    public AuditStatsHandler(
        IClientStatsAuditorService? auditorService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IMediator? mediator = null)
    {
        _auditorService = auditorService ?? new ClientStatsAuditorService();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [AuditStats] {msg}"));
        _mediator = mediator;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string nickname = "MCPPlayer";
        if (arguments.TryGetProperty("nickname", out JsonElement nickEl) && !string.IsNullOrWhiteSpace(nickEl.GetString()))
        {
            nickname = nickEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("player", out nickEl) && !string.IsNullOrWhiteSpace(nickEl.GetString()))
        {
            nickname = nickEl.GetString()!.Trim();
        }

        string tab = "all";
        if (arguments.TryGetProperty("tab", out JsonElement tabEl) && !string.IsNullOrWhiteSpace(tabEl.GetString()))
        {
            tab = tabEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool synthetic = GameServerManageHandler.IsTestExecution;
        if (arguments.TryGetProperty("synthetic", out JsonElement synEl) &&
            (synEl.ValueKind == JsonValueKind.True || synEl.ValueKind == JsonValueKind.False))
        {
            synthetic = synEl.GetBoolean();
        }

        bool recordBacklog = true;
        if (arguments.TryGetProperty("recordIssue", out JsonElement recEl) || arguments.TryGetProperty("recordBacklog", out recEl))
        {
            recordBacklog = recEl.GetBoolean();
        }

        await (McpProgressContext.Current?.ReportProgressAsync(0.2, 1.0, $"Starting stats audit for '{nickname}' ({tab})...", cancellationToken) ?? Task.CompletedTask);

        AccountFixtureDto? customGroundTruth = null;
        if (_mediator is not null)
        {
            try
            {
                var profile = await _mediator.Send(new GetPlayerProfileQuery(nickname), cancellationToken);
                if (profile != null)
                {
                    customGroundTruth = new AccountFixtureDto
                    {
                        Username = profile.Account.Name,
                        AccountId = profile.Account.ID,
                        Email = profile.User.EmailAddress,
                        Mmr = profile.RankedStats?.SkillRating != null ? (int)Math.Round(profile.RankedStats.SkillRating) : 1500,
                        Wins = profile.RankedStats?.MatchesWon ?? 0,
                        Losses = profile.RankedStats?.MatchesLost ?? 0,
                        Disconnects = profile.RankedStats?.Disconnects ?? 0,
                        Kills = profile.AggregatedStats?.HeroKills ?? 0,
                        Deaths = profile.AggregatedStats?.Deaths ?? 0,
                        Assists = profile.AggregatedStats?.HeroAssists ?? 0,
                        GoldCoins = profile.User.GoldCoins,
                        SilverCoins = profile.User.SilverCoins
                    };
                }
            }
            catch (Exception ex)
            {
                _logger($"Failed to resolve ground truth via MediatR for '{nickname}': {ex.Message}");
            }
        }

        var report = await _auditorService.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = nickname,
            Tab = tab,
            Synthetic = synthetic,
            RecordBacklog = recordBacklog,
            CustomGroundTruth = customGroundTruth
        }, cancellationToken).ConfigureAwait(false);

        if (report is null)
        {
            return ToolCallResult.Error($"Audit service returned null report for '{nickname}'");
        }

        await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Audit complete. Found {report.Discrepancies.Count} discrepancies.", cancellationToken) ?? Task.CompletedTask);

        string fidelity = string.Equals(report.ExecutionMode, "LiveIPC", StringComparison.OrdinalIgnoreCase)
            ? ExecutionFidelity.LiveVerified.ToString()
            : ExecutionFidelity.SimulatedTestDouble.ToString();

        return ToolCallResult.Json(new
        {
            status = report.Success ? "passed" : "discrepancies_detected",
            executionFidelity = fidelity,
            nickname = report.Nickname,
            executionMode = report.ExecutionMode,
            parityMatched = report.Success,
            discrepanciesCount = report.Discrepancies.Count,
            discrepancies = report.Discrepancies,
            issuesLogged = report.LoggedIssueIds,
            elapsedMs = report.ElapsedMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }
}
