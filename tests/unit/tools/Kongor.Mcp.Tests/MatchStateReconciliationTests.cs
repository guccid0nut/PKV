// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class MatchStateReconciliationTests
{
    private readonly StateReconciliationService _reconciliationService = new();

    #region StateReconciliationService Direct Engine Tests

    [Fact]
    public void ReconcileMatchState_WhenSynchronized_ReturnsIsSynchronizedTrueAndZeroSeverity()
    {
        var clientState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "gamePhase": "In-Game Combat (Phase 3)",
            "matchTimeSeconds": 124.5,
            "hero": {
                "name": "Hero_Pyromancer",
                "level": 11,
                "gold": 2450,
                "health": 920.0,
                "mana": 680.0,
                "position": { "x": 7400.0, "y": 6900.0, "z": 128.0 }
            }
        }
        """);

        var serverState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "gamePhase": "In-Game Combat",
            "matchTimeSeconds": 124.5,
            "hero": {
                "name": "Hero_Pyromancer",
                "level": 11,
                "gold": 2450,
                "health": 920.0,
                "mana": 680.0,
                "position": { "x": 7400.0, "y": 6900.0, "z": 128.0 }
            }
        }
        """);

        var report = _reconciliationService.ReconcileMatchState(clientState, serverState);

        report.IsSynchronized.Should().BeTrue();
        report.DesyncSeverity.Should().Be("none");
        report.OutOfToleranceCount.Should().Be(0);
        report.PositionDrift.Should().Be(0.0);
        report.ClockDriftSeconds.Should().Be(0.0);
        report.RemediationAction.Should().Be("none");
    }

    [Fact]
    public void ReconcileMatchState_WhenPositionDriftExceedsTolerance_ReportsDesyncDeltaWithRemediation()
    {
        var clientState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "matchTimeSeconds": 124.5,
            "hero": {
                "name": "Hero_Pyromancer",
                "level": 11,
                "gold": 2450,
                "health": 920.0,
                "mana": 680.0,
                "position": { "x": 7400.0, "y": 6900.0, "z": 128.0 }
            }
        }
        """);

        var serverState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "matchTimeSeconds": 124.5,
            "hero": {
                "name": "Hero_Pyromancer",
                "level": 11,
                "gold": 2450,
                "health": 920.0,
                "mana": 680.0,
                "position": { "x": 7550.0, "y": 6900.0, "z": 128.0 }
            }
        }
        """);

        var report = _reconciliationService.ReconcileMatchState(clientState, serverState, new ReconciliationOptions { ToleranceEpsilon = 50.0 });

        report.IsSynchronized.Should().BeFalse();
        report.PositionDrift.Should().Be(150.0);
        report.OutOfToleranceCount.Should().Be(1);
        report.RemediationAction.Should().Be("kongor_game_action action:teleport");
        report.RemediationHint.Should().ContainEquivalentOf("teleport");

        var delta = report.Deltas.First(d => d.Property == "hero.position");
        delta.InTolerance.Should().BeFalse();
        delta.DriftMagnitude.Should().Be(150.0);
        delta.Severity.Should().Be("medium");
    }

    [Fact]
    public void ReconcileMatchState_WhenGoldDiverges_FlagsEconomyDesyncWithPredictionHint()
    {
        var clientState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "matchTimeSeconds": 100.0,
            "hero": {
                "name": "Hero_Pyromancer",
                "gold": 2500,
                "health": 900.0,
                "mana": 500.0
            }
        }
        """);

        var serverState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "matchTimeSeconds": 100.0,
            "hero": {
                "name": "Hero_Pyromancer",
                "gold": 1800,
                "health": 900.0,
                "mana": 500.0
            }
        }
        """);

        var report = _reconciliationService.ReconcileMatchState(clientState, serverState, new ReconciliationOptions { GoldTolerance = 0 });

        report.IsSynchronized.Should().BeFalse();
        var delta = report.Deltas.First(d => d.Property == "hero.gold");
        delta.InTolerance.Should().BeFalse();
        delta.DriftMagnitude.Should().Be(700);
        delta.Severity.Should().Be("high");
        delta.RemediationHint.Should().ContainEquivalentOf("gold");
    }

    [Fact]
    public void ReconcileMatchState_WhenHealthOrManaDiverges_FlagsVitalityDesync()
    {
        var clientState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "matchTimeSeconds": 100.0,
            "hero": {
                "name": "Hero_Pyromancer",
                "health": 920.0,
                "mana": 680.0
            }
        }
        """);

        var serverState = JsonElement.Parse("""
        {
            "matchId": 12345,
            "matchTimeSeconds": 100.0,
            "hero": {
                "name": "Hero_Pyromancer",
                "health": 750.0,
                "mana": 600.0
            }
        }
        """);

        var report = _reconciliationService.ReconcileMatchState(clientState, serverState, new ReconciliationOptions { HealthTolerance = 2.0, ManaTolerance = 2.0 });

        report.IsSynchronized.Should().BeFalse();
        report.Deltas.Should().Contain(d => d.Property == "hero.health" && !d.InTolerance);
        report.Deltas.Should().Contain(d => d.Property == "hero.mana" && !d.InTolerance);
    }

    [Fact]
    public void ReconcileMatchState_WhenClockDrifts_FlagsClockDesync()
    {
        var clientState = JsonElement.Parse("""
        {
            "matchTimeSeconds": 105.5
        }
        """);

        var serverState = JsonElement.Parse("""
        {
            "matchTimeSeconds": 100.0
        }
        """);

        var report = _reconciliationService.ReconcileMatchState(clientState, serverState, new ReconciliationOptions { MaxClockDriftSeconds = 1.0 });

        report.IsSynchronized.Should().BeFalse();
        report.ClockDriftSeconds.Should().Be(5.5);
        report.Deltas.Should().Contain(d => d.Property == "match.matchTimeSeconds" && !d.InTolerance);
    }

    [Fact]
    public void ReconcileSnapshots_WhenInvoked_ComparesCheckpointsDirectly()
    {
        var snap1 = new GameSnapshotDto
        {
            Hero = "Hero_Pyromancer",
            Level = 16,
            Gold = 5000,
            Health = 1200.0,
            Mana = 800.0,
            X = 0,
            Y = 0,
            Z = 0,
            MatchTimeSeconds = 600.0
        };

        var snap2 = new GameSnapshotDto
        {
            Hero = "Hero_Pyromancer",
            Level = 16,
            Gold = 4500,
            Health = 1200.0,
            Mana = 800.0,
            X = 100.0,
            Y = 0,
            Z = 0,
            MatchTimeSeconds = 600.0
        };

        var report = _reconciliationService.ReconcileSnapshots(snap1, snap2, new ReconciliationOptions { ToleranceEpsilon = 50.0 });

        report.IsSynchronized.Should().BeFalse();
        report.PositionDrift.Should().Be(100.0);
        report.Deltas.Should().Contain(d => d.Property == "hero.position");
        report.Deltas.Should().Contain(d => d.Property == "hero.gold");
    }

    #endregion

    #region MatchStateHandler Integration Tests

    [Fact]
    public async Task MatchStateHandler_WhenTargetIsBothAndServerNull_PreservesBackwardCompatibilityClientTarget()
    {
        // Existing test compatibility test: McpHighValueToolsTests line 28 asserts root.GetProperty("target").GetString().Should().Be("client")
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { Connected = true, GamePhase = 3, CurrentMap = "caldavar" });
        MatchStateHandler handler = new(mockIpc.Object, null);

        var args = JsonElement.Parse("""
        {
            "target": "both",
            "includePlayers": true
        }
        """);

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("client");
    }

    [Fact]
    public async Task MatchStateHandler_WhenTargetIsReconciled_ReturnsReconciliationReport()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { Connected = true, GamePhase = 3, CurrentMap = "caldavar" });
        GameServerManageHandler serverHandler = new(logger: _ => { });
        MatchStateHandler handler = new(mockIpc.Object, serverHandler);

        var args = JsonElement.Parse("""
        {
            "target": "reconciled",
            "port": 11235,
            "toleranceEpsilon": 50.0
        }
        """);

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("reconciled");
        root.TryGetProperty("isSynchronized", out _).Should().BeTrue();
        root.TryGetProperty("desyncSeverity", out _).Should().BeTrue();
        root.TryGetProperty("clockDriftSeconds", out _).Should().BeTrue();
        root.TryGetProperty("positionDrift", out _).Should().BeTrue();
        root.TryGetProperty("deltas", out _).Should().BeTrue();
        root.TryGetProperty("clientState", out _).Should().BeTrue();
        root.TryGetProperty("serverState", out _).Should().BeTrue();
    }

    #endregion

    #region GameSnapshotHandler Integration Tests

    [Fact]
    public async Task GameSnapshotHandler_WorldState_WhenTargetIsReconciled_CrossVerifiesWorldEntities()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            gameLuaHandler: null,
            serverManageHandler: null,
            portRegistry: null);

        var args = JsonElement.Parse("""
        {
            "action": "world_state",
            "target": "reconciled",
            "toleranceEpsilon": 50.0,
            "allowSimulation": true
        }
        """);

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("world_state");
        root.GetProperty("target").GetString().Should().Be("reconciled");
        root.GetProperty("isSynchronized").GetBoolean().Should().BeTrue();
        root.GetProperty("desyncSeverity").GetString().Should().Be("none");
    }

    [Fact]
    public async Task GameSnapshotHandler_ActionReconcile_ReturnsDetailedReport()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            gameLuaHandler: null,
            serverManageHandler: null,
            portRegistry: null);

        var args = JsonElement.Parse("""
        {
            "action": "reconcile",
            "toleranceEpsilon": 50.0,
            "allowSimulation": true
        }
        """);

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("isSynchronized").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GameSnapshotHandler_ApplyFixture_WhenTargetIsBoth_ExecutesSuccessfully()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            gameLuaHandler: null,
            serverManageHandler: null,
            portRegistry: null);

        var args = JsonElement.Parse("""
        {
            "action": "apply_fixture",
            "target": "both",
            "fixture": {
                "hero": "Hero_Pyromancer",
                "level": 16,
                "gold": 5000
            }
        }
        """);

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("both");
    }

    #endregion
}
