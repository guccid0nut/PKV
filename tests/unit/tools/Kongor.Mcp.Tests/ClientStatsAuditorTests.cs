// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ClientStatsAuditorTests : IDisposable
{
    private readonly string _testTempDir;
    private readonly string _testIssuesPath;

    public ClientStatsAuditorTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_stats_audit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
        _testIssuesPath = Path.Combine(_testTempDir, "ISSUES.md");

        // Seed basic issues file header
        File.WriteAllText(_testIssuesPath, """
            # ProjectKongor Living Issue Backlog
            - **Total Tracked**: 142
            - **Patched / Resolved**: 142
            - **Active / Investigating**: 0

            ## Active Issues

            ## Resolved Archive
            """);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Audit_FullParity_ReportsZeroDiscrepancies()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var report = await auditor.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = "MCPPlayer",
            Synthetic = true,
            RecordBacklog = true
        });

        Assert.True(report.Success);
        Assert.Empty(report.Discrepancies);
        Assert.Empty(report.LoggedIssueIds);
        Assert.Equal("Synthetic", report.ExecutionMode);
    }

    [Fact]
    public async Task Audit_MismatchedKda_DetectsDiscrepancyAndRecordsIssue()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customGt = new AccountFixtureDto
        {
            Username = "TestPlayer",
            Wins = 10,
            Losses = 5,
            MatchHistory =
            [
                new MatchHistoryEntryFixtureDto
                {
                    MatchId = "2001",
                    Winner = 1,
                    PlayerTeam = 1,
                    Kills = 14,
                    Deaths = 2,
                    Assists = 9,
                    DurationSeconds = 1800,
                    HeroName = "Hero_Pyromancer",
                    HeroIcon = "/heroes/pyromancer/icon.tga"
                }
            ]
        };

        var customClientState = new RenderedClientStatsDto
        {
            Wins = 10,
            Losses = 5,
            WinningPercentage = "66%",
            TotalGames = 15,
            MatchRows =
            [
                new RenderedMatchRowDto
                {
                    MatchId = "2001",
                    HeroName = "Hero_Pyromancer",
                    HeroIconTexture = "/heroes/pyromancer/icon.tga",
                    ResultTexture = "match_result_victory.png",
                    KdaText = "^g0^*/^r0^*/^y0", // Discrepancy!
                    DurationText = "30:00"
                }
            ]
        };

        var report = await auditor.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = "TestPlayer",
            Synthetic = true,
            RecordBacklog = true,
            CustomGroundTruth = customGt,
            CustomRenderedState = customClientState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.KDA_MISMATCH);
        Assert.Single(report.LoggedIssueIds);

        // Verify issue was written to test ISSUES.md
        string fileContent = await File.ReadAllTextAsync(_testIssuesPath);
        Assert.Contains("[ISSUE-143]", fileContent);
        Assert.Contains("KDA_MISMATCH", fileContent);
    }

    [Fact]
    public async Task Audit_DivisionByZeroWinRate_DetectsNaNAnomaly()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customGt = new AccountFixtureDto
        {
            Username = "NewPlayer",
            Wins = 0,
            Losses = 0,
            MatchHistory = []
        };

        var customClientState = new RenderedClientStatsDto
        {
            Wins = 0,
            Losses = 0,
            WinningPercentage = "NaN%", // Division by zero bug!
            TotalGames = 0,
            MatchRows = []
        };

        var report = await auditor.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = "NewPlayer",
            Synthetic = true,
            RecordBacklog = true,
            CustomGroundTruth = customGt,
            CustomRenderedState = customClientState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.DIV_ZERO_NAN);
        Assert.Single(report.LoggedIssueIds);

        string fileContent = await File.ReadAllTextAsync(_testIssuesPath);
        Assert.Contains("DIV_ZERO_NAN", fileContent);
        Assert.Contains("Wrap winning percentage calculation", fileContent);
    }

    [Fact]
    public async Task Audit_MissingHeroIcon_DetectsVfsMissingTexture()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customGt = new AccountFixtureDto
        {
            Username = "IconTestPlayer",
            Wins = 1,
            Losses = 0,
            MatchHistory =
            [
                new MatchHistoryEntryFixtureDto
                {
                    MatchId = "3001",
                    Winner = 1,
                    PlayerTeam = 1,
                    Kills = 5,
                    Deaths = 1,
                    Assists = 3,
                    DurationSeconds = 1200,
                    HeroName = "Hero_Aluna",
                    HeroIcon = "/heroes/aluna/icon.tga"
                }
            ]
        };

        var customClientState = new RenderedClientStatsDto
        {
            Wins = 1,
            Losses = 0,
            WinningPercentage = "100%",
            TotalGames = 1,
            MatchRows =
            [
                new RenderedMatchRowDto
                {
                    MatchId = "3001",
                    HeroName = "Hero_Aluna",
                    HeroIconTexture = "/ui/elements/unknown.dds", // Missing texture fallback!
                    ResultTexture = "match_result_victory.png",
                    KdaText = "^g5^*/^r1^*/^y3",
                    DurationText = "20:00"
                }
            ]
        };

        var report = await auditor.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = "IconTestPlayer",
            Synthetic = true,
            RecordBacklog = true,
            CustomGroundTruth = customGt,
            CustomRenderedState = customClientState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.VFS_TEXTURE_MISSING);
    }

    [Fact]
    public async Task Audit_RepeatedExecution_DoesNotDuplicateBacklogIssues()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customGt = new AccountFixtureDto
        {
            Username = "DedupPlayer",
            Wins = 5,
            Losses = 2,
            MatchHistory = []
        };

        var customClientState = new RenderedClientStatsDto
        {
            Wins = 3, // Mismatch!
            Losses = 2,
            WinningPercentage = "71%",
            TotalGames = 5,
            MatchRows = []
        };

        var request = new StatsAuditRequest
        {
            Nickname = "DedupPlayer",
            Synthetic = true,
            RecordBacklog = true,
            CustomGroundTruth = customGt,
            CustomRenderedState = customClientState
        };

        // First run: records issue
        var report1 = await auditor.RunFullAuditAsync(request);
        Assert.Single(report1.LoggedIssueIds);

        // Second run with identical issue: suppressed by idempotency
        var report2 = await auditor.RunFullAuditAsync(request);
        Assert.Empty(report2.LoggedIssueIds);

        // Verify only 1 issue was added to the file
        string text = await File.ReadAllTextAsync(_testIssuesPath);
        int issueMatches = System.Text.RegularExpressions.Regex.Count(text, "GAMES_COUNT_MISMATCH on 'DedupPlayer'");
        Assert.Equal(1, issueMatches);
    }

    [Fact]
    public async Task AuditCommand_SpectreConsole_ExecutesAndReturnsExpectedExitCode()
    {
        var command = new ClientStatsAuditCommand();
        var settingsPass = new AuditCommandSettings
        {
            Nickname = "MCPPlayer",
            Synthetic = true,
            DryRun = true
        };

        int exitCodePass = await command.ExecuteAsync(null!, settingsPass);
        Assert.Equal(0, exitCodePass);
    }

    [Fact]
    public async Task Audit_MasteryCorrupt_DetectsHoodedSilhouettes()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customState = new RenderedClientStatsDto
        {
            Wins = 5,
            Losses = 2,
            ConRewardTexture = "/ui/fe2/newui/res/playerstats/chest0.png",
            Mastery = new RenderedMasteryDto
            {
                TotalHeroes = 145,
                HoodedSilhouettesCount = 3, // Corrupt silhouettes!
                HasOverallRewardClaimButton = true
            }
        };

        var report = await auditor.AuditMasteryAsync(new StatsAuditRequest
        {
            Nickname = "MasteryPlayer",
            Synthetic = true,
            CustomRenderedState = customState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.MASTERY_DATA_CORRUPT);
    }

    [Fact]
    public async Task Audit_RewardButtonMissing_DetectsMissingClaimButton()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customState = new RenderedClientStatsDto
        {
            Wins = 5,
            Losses = 2,
            ConRewardTexture = "/ui/fe2/newui/res/playerstats/chest0.png",
            Mastery = new RenderedMasteryDto
            {
                TotalHeroes = 145,
                HoodedSilhouettesCount = 0,
                HasOverallRewardClaimButton = false // Missing button!
            }
        };

        var report = await auditor.AuditMasteryAsync(new StatsAuditRequest
        {
            Nickname = "ButtonPlayer",
            Synthetic = true,
            CustomRenderedState = customState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.REWARD_BUTTON_MISSING);
    }

    [Fact]
    public async Task Audit_MatchDetailsMissingData_DetectsEmptyPedestalAndDashedXpBar()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customState = new RenderedClientStatsDto
        {
            Wins = 5,
            Losses = 2,
            ConRewardTexture = "/ui/fe2/newui/res/playerstats/chest0.png",
            MatchDetails = new RenderedMatchDetailsDto
            {
                MatchId = "5555",
                MatchOwner = "MatchOwnerPlayer",
                HeroModelPath = "", // Missing 3D pedestal model!
                MasteryXpBarText = "EXP -/-", // Dashed placeholder!
                NextLevelAwardTexture = "$invis" // Blank next level award!
            }
        };

        var report = await auditor.AuditMatchDetailsAsync(new StatsAuditRequest
        {
            Nickname = "MatchOwnerPlayer",
            Synthetic = true,
            CustomRenderedState = customState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.MATCH_DETAILS_MISSING_DATA);
    }

    [Fact]
    public async Task Audit_UiGhosting_DetectsMultipleActiveContainers()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customState = new RenderedClientStatsDto
        {
            Wins = 5,
            Losses = 2,
            ConRewardTexture = "/ui/fe2/newui/res/playerstats/chest0.png",
            VisibleContainers = ["playerstats_overview", "playerstats_mastery"] // Ghosting bleed-through!
        };

        var report = await auditor.AuditUiGhostingAsync(new StatsAuditRequest
        {
            Nickname = "GhostPlayer",
            Synthetic = true,
            CustomRenderedState = customState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.UI_LAYER_GHOSTING);
    }

    [Fact]
    public async Task Audit_ConRewardBlank_FlagsInvisFallback()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customState = new RenderedClientStatsDto
        {
            Wins = 5,
            Losses = 2,
            ConRewardTexture = "$invis" // Blank fallback!
        };

        var report = await auditor.AuditPlayerStatsAsync(new StatsAuditRequest
        {
            Nickname = "ConPlayer",
            Synthetic = true,
            CustomRenderedState = customState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.CON_REWARD_BLANK);
    }

    [Fact]
    public async Task Audit_DevTeapotDetected_FlagsPlaceholder()
    {
        var issueTracker = new IssueTrackerService(_testIssuesPath);
        var auditor = new ClientStatsAuditorService(issueTracker);

        var customState = new RenderedClientStatsDto
        {
            Wins = 5,
            Losses = 2,
            ConRewardTexture = "/ui/fe2/newui/res/playerstats/chest0.png",
            TeapotOccurrences = ["^m[DEV:WINS]^0"] // Utah Teapot indicator!
        };

        var report = await auditor.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = "TeapotPlayer",
            Synthetic = true,
            CustomRenderedState = customState
        });

        Assert.False(report.Success);
        Assert.Contains(report.Discrepancies, d => d.RuleCode == DiscrepancyRuleCode.DEV_TEAPOT_DETECTED);
    }
}
