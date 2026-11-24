// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Linq;
using ASPIRE.Common.MachineLearning;
using FluentAssertions;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Protocol;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Mcp.Tests.Rl;

public sealed class RlCommandTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_mcp_rl_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void McpRegistry_IncludesArcadeIdleModeActionAndModeParameter_OnKongorServerManage()
    {
        // protects: AI/MCP contract for kongor_server_manage so agents can toggle hibernate vs selfplay without invalid schema errors.
        // fails_when: McpToolRegistry omits 'arcade_idle_mode' from action enum or 'mode' parameter from Properties.
        // seam: McpToolRegistry.CreateServerManageDefinition schema inspection.
        ToolDefinition tool = McpToolRegistry.CreateServerManageDefinition();
        tool.Name.Should().Be("kongor_server_manage");
        tool.InputSchema.Properties.Should().ContainKey("action");
        tool.InputSchema.Properties["action"].Enum.Should().Contain("arcade_idle_mode");
        tool.InputSchema.Properties.Should().ContainKey("mode");
        tool.InputSchema.Properties["mode"].Enum.Should().Contain(["hibernate", "selfplay", "status"]);
    }

    [Fact]
    public async Task RlCommand_StatusAndRollback_ExecuteCleanlyAgainstTempRuntimeDirectory()
    {
        // protects: kongor rl CLI operator subcommands (status + rollback) against real RlArenaCoordinator runtime files.
        // fails_when: CLI throws on missing trajectories or fails to restore previous Champion backup generation.
        // seam: RlCommand.ExecuteAsync + RlArenaCoordinator.
        string runtimeDir = Path.Combine(_tempRoot, "rl_runtime");
        string trajDir = Path.Combine(_tempRoot, "rl_trajectories");
        Directory.CreateDirectory(trajDir);

        RlArenaCoordinator arena = new(runtimeDir, new RlArenaOptions(MinEvaluationMatches: 1, MinPromotionWinRate: 0.5));
        arena.EnsureInitialized();
        BotPolicyCheckpoint gen1 = BotPolicyNetwork.CreateSeedBaseline(42);
        gen1.Generation = 1;
        arena.InstallExternalCandidate(gen1);
        arena.RecordEpisodeAndMaybeEvaluate(new RlEpisodeRecord
        {
            MatchId = 9001,
            WinnerTeam = 1,
            CandidateTeam = 1,
            MeanCandidateReturn = 2.0,
            MeanChampionReturn = 0.5,
            Steps = []
        });
        arena.GetArenaState().ChampionGeneration.Should().Be(1);

        RlCommand cmd = new();
        int statusExit = await cmd.ExecuteAsync(null!, new RlCommandSettings
        {
            Action = "status",
            RuntimeDir = runtimeDir,
            TrajectoriesDir = trajDir
        });
        statusExit.Should().Be(0);

        int rollbackExit = await cmd.ExecuteAsync(null!, new RlCommandSettings
        {
            Action = "rollback",
            Value = "0",
            RuntimeDir = runtimeDir,
            TrajectoriesDir = trajDir
        });
        rollbackExit.Should().Be(0);
        arena.GetArenaState().ChampionGeneration.Should().Be(0);
    }
}
