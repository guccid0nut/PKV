// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Reflection;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class MonkeyCheckersFeatureTests
{
    private static string FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ProjectKongor.sln")) || Directory.Exists(Path.Combine(dir, "src", "services", "web-ui")))
            {
                return dir;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void MonkeyCheckers_View_ExistsAndContainsEssentialElements()
    {
        string repoRoot = FindRepoRoot();
        string viewPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "ArMiniGames", "MonkeyCheckers.cshtml");

        Assert.True(File.Exists(viewPath), $"MonkeyCheckers.cshtml must exist at {viewPath}");

        string content = File.ReadAllText(viewPath);
        Assert.Contains("id=\"checkersVideo\"", content);
        Assert.Contains("id=\"checkersCanvasContainer\"", content);
        Assert.Contains("id=\"checkersCanvas\"", content);
        Assert.Contains("id=\"selectGameMode\"", content);
        Assert.Contains("id=\"btnCheckersToggleCamera\"", content);
        Assert.Contains("id=\"btnCheckersUndo\"", content);
        Assert.Contains("id=\"btnCheckersHint\"", content);
        Assert.Contains("id=\"btnCheckersToggleAudio\"", content);
        Assert.Contains("id=\"btnCheckersReset\"", content);
        Assert.Contains("id=\"checkersVictoryModal\"", content);
        Assert.Contains("id=\"checkersLobbyModal\"", content);
        Assert.Contains("id=\"checkersWaitingOverlay\"", content);
        Assert.Contains("id=\"checkersArReadyModal\"", content);
        Assert.Contains("id=\"checkersRoomFullModal\"", content);
        Assert.Contains("id=\"btnCheckersArYes\"", content);
        Assert.Contains("id=\"btnCheckersArNo\"", content);
        Assert.Contains("signalr.min.js", content);
        Assert.Contains("monkey_checkers.js", content);
    }

    [Fact]
    public void MonkeyCheckers_Script_ExistsAndContainsEssentialFeatures()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "monkey_checkers.js");

        Assert.True(File.Exists(scriptPath), $"monkey_checkers.js must exist at {scriptPath}");

        string content = File.ReadAllText(scriptPath);
        Assert.Contains("window.MonkeyKingCheckers", content);
        Assert.Contains("getLegalMovesForPiece", content);
        Assert.Contains("minimax", content);
        Assert.Contains("chooseBotMove", content);
        Assert.Contains("buildCaldavarBoard", content);
        Assert.Contains("ability_1", content);
        Assert.Contains("ability_2", content);
        Assert.Contains("ability_2_roll", content);
        Assert.Contains("create3DCrown", content);
        Assert.Contains("startCameraAR", content);
        Assert.Contains("playAudioFx", content);
        Assert.Contains("evaluateBoard", content);
        Assert.Contains("updateBoardTransparency", content);
        Assert.Contains("hasFirstBloodOccurred", content);
        Assert.Contains("playHeroVoice", content);
        Assert.Contains("voicePools", content);
        Assert.Contains("activeAudioSet", content);
        Assert.Contains("flipPivot", content);
        Assert.Contains("headingGroup", content);
        Assert.Contains("stopAllAudio", content);
        Assert.Contains("createFlyingNimbusCloud", content);
        Assert.Contains("connectAndJoinRoom", content);
        Assert.Contains("setupSignalRHandlers", content);
        Assert.Contains("OnRoomJoined", content);
        Assert.Contains("OnOpponentJoined", content);
        Assert.Contains("OnPlayerReadyStatus", content);
        Assert.Contains("OnMatchStarted", content);
        Assert.Contains("OnOpponentMove", content);
        Assert.Contains("SendMove", content);
        Assert.Contains("online_1v1", content);
    }

    [Fact]
    public void MonkeyCheckers_Layout_RegistersMenuItem()
    {
        string repoRoot = FindRepoRoot();
        string layoutPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Shared", "_Layout.cshtml");

        Assert.True(File.Exists(layoutPath), $"_Layout.cshtml must exist at {layoutPath}");

        string content = File.ReadAllText(layoutPath);
        Assert.Contains("/ar-mini-games/monkey-checkers", content);
        Assert.Contains("Monkey King", content);
        Assert.Contains("AR Mini Games", content);
    }

    [Fact]
    public void MonkeyCheckers_ToolsController_RedirectsPermanentlyToArMiniGames()
    {
        IStaffStoreService storeService = new StaffStoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<StaffStoreService>.Instance);

        ToolsController controller = new(storeService, NullLogger<ToolsController>.Instance);

        IActionResult result = controller.MonkeyCheckers();

        RedirectResult redirectResult = Assert.IsType<RedirectResult>(result);
        Assert.True(redirectResult.Permanent);
        Assert.Equal("/ar-mini-games/monkey-checkers", redirectResult.Url);
    }

    [Fact]
    public void MonkeyCheckers_ToolsController_WithRoomQueryParam_RedirectsWithQuery()
    {
        IStaffStoreService storeService = new StaffStoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<StaffStoreService>.Instance);

        ToolsController controller = new(storeService, NullLogger<ToolsController>.Instance);

        IActionResult result = controller.MonkeyCheckers("custom_room_99!");

        RedirectResult redirectResult = Assert.IsType<RedirectResult>(result);
        Assert.True(redirectResult.Permanent);
        Assert.Equal("/ar-mini-games/monkey-checkers?room=custom_room_99!", redirectResult.Url);
    }

    [Fact]
    public void MonkeyCheckers_ArMiniGamesController_ServesView()
    {
        ArMiniGamesController controller = new(NullLogger<ArMiniGamesController>.Instance);

        IActionResult result = controller.MonkeyCheckers();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.True(string.IsNullOrEmpty(viewResult.ViewName) || viewResult.ViewName == "MonkeyCheckers");
        Assert.NotNull(controller.ViewData["Title"]);
        Assert.Contains("Monkey King", controller.ViewData["Title"]?.ToString());
        Assert.NotNull(controller.ViewData["InitialRoom"]);
        Assert.Equal("MK42", controller.ViewData["InitialRoom"]?.ToString());
    }

    [Fact]
    public void MonkeyCheckers_ArMiniGamesController_WithRoomQueryParam_NormalizesInitialRoom()
    {
        ArMiniGamesController controller = new(NullLogger<ArMiniGamesController>.Instance);

        controller.MonkeyCheckers("custom_room_99!");

        Assert.Equal("CUSTOMRO", controller.ViewData["InitialRoom"]?.ToString());
    }

    [Fact]
    public void MonkeyCheckers_ToolsController_HasCorrectRouteAttributes()
    {
        MethodInfo? method = typeof(ToolsController).GetMethod("MonkeyCheckers");
        Assert.NotNull(method);

        var routeAttrs = method.GetCustomAttributes<HttpGetAttribute>().Select(a => a.Template).ToList();
        Assert.Contains("monkey-checkers", routeAttrs);
        Assert.Contains("checkers", routeAttrs);
    }
}
