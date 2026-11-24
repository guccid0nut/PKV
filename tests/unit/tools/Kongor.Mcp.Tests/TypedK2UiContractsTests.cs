// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TypedK2UiContractsTests.cs
// System: Kongor.Mcp.Tests - Strongly-Typed K2 UI Contract & RPC Bridge Tests
// ============================================================================

using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Moq;
using NEXUS.Contracts.K2;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TypedK2UiContractsTests
{
    [Fact]
    public void CWidgetRef_ToLuaExpression_GeneratesCanonicalUIManagerCall()
    {
        CWidgetRef widget = new("main", "sysbar_quickStart", (_, _, _) => Task.FromResult("OK"));

        string expr = widget.ToLuaExpression();
        expr.Should().Be("UIManager.GetInterface('main'):GetWidget('sysbar_quickStart')");
    }

    [Fact]
    public void CWidgetRef_ToLuaDoEvent_GeneratesStrictNoStringSignature()
    {
        CWidgetRef widget = new("main", "sysbar_quickStart", (_, _, _) => Task.FromResult("OK"));

        string script = widget.ToLuaDoEvent();
        script.Should().Contain("w:DoEvent()", "DoEvent must take zero string parameters.");
        script.Should().NotContain("DoEvent('onclick')");

        string indexedScript = widget.ToLuaDoEvent(2);
        indexedScript.Should().Contain("w:DoEventN(2)");
    }

    [Fact]
    public void CWidgetRef_ToLuaSetVisible_GeneratesSafeBooleanCall()
    {
        CWidgetRef widget = new("main", "prelogin_lower", (_, _, _) => Task.FromResult("OK"));

        string scriptTrue = widget.ToLuaSetVisible(true);
        scriptTrue.Should().Contain("w:SetVisible(true)");

        string scriptFalse = widget.ToLuaSetVisible(false);
        scriptFalse.Should().Contain("w:SetVisible(false)");
    }

    [Fact]
    public void CWidgetRef_ToLuaSetText_EscapesQuotesSafely()
    {
        CWidgetRef widget = new("main", "lbl_status", (_, _, _) => Task.FromResult("OK"));

        string script = widget.ToLuaSetText("Player's Match");
        script.Should().Contain(@"Player\'s Match");
    }

    [Fact]
    public async Task FluentBridge_Ui_DispatchesToExpectedLuaScript()
    {
        // Arrange
        string? capturedLua = null;
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((lua, _, _) => capturedLua = lua)
            .ReturnsAsync("SUCCESS");

        // Act - Click quick start on Main interface
        await mockBridge.Object.Ui().Main.SysbarQuickStart.DoEventAsync();

        // Assert
        capturedLua.Should().NotBeNull();
        capturedLua.Should().Contain("UIManager.GetInterface('main')");
        capturedLua.Should().Contain("sysbar_quickStart");
        capturedLua.Should().Contain("w:DoEvent()");
    }

    [Fact]
    public void CanonicalInterfaces_AllExposeExpectedStronglyTypedWidgets()
    {
        K2UiContractsRoot root = new((_, _, _) => Task.FromResult("OK"));

        // Main
        root.Main.InterfaceName.Should().Be("main");
        root.Main.SysbarQuickStart.WidgetName.Should().Be("sysbar_quickStart");
        root.Main.MainMenuPanelSwitcher.WidgetName.Should().Be("MainMenuPanelSwitcher");
        root.Main.PreloginLower.WidgetName.Should().Be("prelogin_lower");
        root.Main.StoreContainer.WidgetName.Should().Be("store_container2");

        // Game
        root.Game.InterfaceName.Should().Be("game");
        root.Game.MiniMap.WidgetName.Should().Be("minimap");
        root.Game.SkillPanel.WidgetName.Should().Be("ability_panel");
        root.Game.InventoryPanel.WidgetName.Should().Be("inventory_panel");

        // Replay Control
        root.ReplayControl.InterfaceName.Should().Be("game_replay_control");
        root.ReplayControl.PlayPauseButton.WidgetName.Should().Be("btn_play_pause");
        root.ReplayControl.Timeline.WidgetName.Should().Be("slider_timeline");

        // Spectator
        root.Spectator.InterfaceName.Should().Be("game_spectator");
        root.Spectator.GoldGraph.WidgetName.Should().Be("graph_gold");
        root.Spectator.XpGraph.WidgetName.Should().Be("graph_xp");

        // Profile & Stats (User Requested)
        root.ProfileStats.InterfaceName.Should().Be("player_stats_v2");
        root.ProfileStats.MatchHistoryList.WidgetName.Should().Be("match_history_list");
        root.ProfileStats.RankedRatingLabel.WidgetName.Should().Be("ranked_mmr_label");
        root.ProfileStats.HeroPerformanceGrid.WidgetName.Should().Be("hero_stats_grid");

        // Learnatorium (User Requested)
        root.Learnatorium.InterfaceName.Should().Be("learnatorium");
        root.Learnatorium.HeroRosterGrid.WidgetName.Should().Be("learnatorium_hero_grid");
        root.Learnatorium.HeroDetailsPanel.WidgetName.Should().Be("learnatorium_hero_details");
        root.Learnatorium.ItemsGuideGrid.WidgetName.Should().Be("learnatorium_items_grid");
        root.Learnatorium.BtnPracticeHero.WidgetName.Should().Be("btn_practice_hero");
    }
}
