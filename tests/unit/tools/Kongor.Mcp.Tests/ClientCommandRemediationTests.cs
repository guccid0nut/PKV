// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ClientCommandRemediationTests
{
    private sealed class FakeTimeProvider(DateTimeOffset fixedTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedTime;
    }

    private readonly FakeTimeProvider _timeProvider;
    private readonly string _testTempDir;

    public ClientCommandRemediationTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.Zero));
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_remediation_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
    }

    [Fact]
    public void AnalyzeCommandError_WindowsNilReference_ReturnsImprovedCommandAndRemediationHint()
    {
        // User request verbatim scenario: Windows.main:GetWidget('practice_button'):DoEvent('onclick')
        string cmd = "Windows.main:GetWidget('practice_button'):DoEvent('onclick')";
        string err = "attempt to index global 'Windows' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("NilGlobalReference", rem.ErrorCategory);
        Assert.Contains("UIManager", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.NotNull(rem.ImprovedCommand);
        Assert.Contains("UIManager.GetInterface('main')", rem.ImprovedCommand);
        Assert.DoesNotContain("Windows.main", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_DoEventStringArgument_RemediatesToZeroArgDoEvent()
    {
        string cmd = "widget:DoEvent('onclick')";
        string err = "bad argument #1 to 'DoEvent' (number expected, got string)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("InvalidEventSignature", rem.ErrorCategory);
        Assert.Contains("widget:DoEvent()", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("widget:DoEvent()", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_PracticeButtonWidget_RemediatesToSysbarQuickStart()
    {
        string cmd = "GetWidget('practice_button'):DoEvent()";
        string err = "widget 'practice_button' not found";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("WidgetNotFound", rem.ErrorCategory);
        Assert.Contains("sysbar_quickStart", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("UIManager.GetInterface('main'):GetWidget('sysbar_quickStart'):DoEvent()", rem.ImprovedCommand);
        var globalNilRem = ClientCommandRemediationService.AnalyzeCommandError("GetWidget('practice_button'):DoEvent()", "attempt to call global 'GetWidget' (a nil value)", "lua");
        Assert.Equal("UIManager.GetInterface('main'):GetWidget('sysbar_quickStart'):DoEvent()", globalNilRem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_StartGameMissingMapOption_RemediatesGrammar()
    {
        string cmd = "StartGame practice caldavar";
        string err = "Unknown map 'practice'";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "stdin");

        Assert.Equal("MalformedCommandArguments", rem.ErrorCategory);
        Assert.Contains("session/game name", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("StartGame practice PracticeGame map:caldavar allheroes:true", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_RawLuaWithoutScriptPrefix_RemediatesWithScriptPrefix()
    {
        string cmd = "UIManager.GetInterface('main'):GetWidget('name')";
        string err = "Unknown command: UIManager.GetInterface";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "in_game_console");

        Assert.Equal("MissingScriptPrefix", rem.ErrorCategory);
        Assert.Contains("prefixed with 'Script '", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("Script UIManager.GetInterface('main'):GetWidget('name')", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_ClientOffline_ReturnsClientOfflineWithoutRetry()
    {
        string cmd = "echo test";
        string err = "Game client is not currently running";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "none");

        Assert.Equal("ClientOffline", rem.ErrorCategory);
        Assert.Contains("hon_x64.exe", rem.RemediationHint);
        Assert.False(rem.CanAutoRetry);
        Assert.Null(rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_ReadOnlyCvar_SuggestsDeveloperMode()
    {
        string cmd = "Set sv_cheats 1";
        string err = "Set: Cvar 'sv_cheats' is read only";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "stdin");

        Assert.Equal("ReadOnlyCvar", rem.ErrorCategory);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("Set dev true; Set sv_cheats 1", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_StoreContainerMismatch_RemediatesToStoreContainer2()
    {
        string cmd = "UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', 'store_container', nil, false)";
        string err = "";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("StoreContainerMismatch", rem.ErrorCategory);
        Assert.Contains("store_container2", rem.RemediationHint);
        Assert.Contains("cg_store2_", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.NotNull(rem.ImprovedCommand);
        Assert.Contains("store_container2", rem.ImprovedCommand);
        Assert.DoesNotContain("'store_container'", rem.ImprovedCommand);
    }

    [Fact]
    public async Task ErrorCollector_WithInvalidInput_RecordsErrorsAndEnforcesCap()
    {
        var service = new ClientCommandRemediationService(_testTempDir, _timeProvider);

        for (int i = 0; i < 60; i++)
        {
            service.RecordAndRemediate($"cmd_{i}", $"error_{i}", "stdin");
        }

        var recent = service.GetRecentErrors(10);
        Assert.Equal(10, recent.Count);
        Assert.Equal("cmd_59", recent[0].Command); // Most recent first

        var all = service.GetRecentErrors(100);
        Assert.Equal(50, all.Count); // Capped at 50

        await service.FlushAsync();

        // Verify disk file exists and has records
        string ndjsonPath = Path.Combine(_testTempDir, "client_command_errors.ndjson");
        Assert.True(File.Exists(ndjsonPath));
        string[] lines = File.ReadAllLines(ndjsonPath);
        Assert.Equal(60, lines.Length);
    }

    [Fact]
    public async Task GameClientManageHandler_ActionErrors_ReturnsRecentErrors()
    {
        var remediationService = new ClientCommandRemediationService(_testTempDir, _timeProvider);
        remediationService.RecordAndRemediate("bad_cmd", "Unknown command", "stdin");

        var handler = new GameClientManageHandler(
            ipcBridge: null,
            uiControlService: null,
            remediationService: remediationService,
            timeProvider: _timeProvider);

        using var doc = JsonDocument.Parse("{\"action\": \"errors\", \"limit\": 5}");
        var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.False(res.IsError);
        string jsonText = ((TextContentItem)res.Content[0]).Text;
        Assert.Contains("errors", jsonText);
        Assert.Contains("bad_cmd", jsonText);
        Assert.Contains("UnknownCommand", jsonText);
    }

    [Fact]
    public async Task GameCommandHandler_ActionErrors_ReturnsRecentErrors()
    {
        var remediationService = new ClientCommandRemediationService(_testTempDir, _timeProvider);
        remediationService.RecordAndRemediate("bad_cmd_2", "syntax error", "in_game_console");

        var handler = new GameCommandHandler(
            ipcBridge: null,
            serverManageHandler: null,
            clientManageHandler: null,
            uiControlService: null,
            remediationService: remediationService,
            timeProvider: _timeProvider);

        using var doc = JsonDocument.Parse("{\"action\": \"errors\"}");
        var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.False(res.IsError);
        string jsonText = ((TextContentItem)res.Content[0]).Text;
        Assert.Contains("errors", jsonText);
        Assert.Contains("bad_cmd_2", jsonText);
        Assert.Contains("LuaSyntaxError", jsonText);
    }

    [Fact]
    public async Task GameLuaHandler_WhenInvoked_SelfHealsWindowsAndPracticeButtonAutomatically()
    {
        var remediationService = new ClientCommandRemediationService(_testTempDir, _timeProvider);

        // When user sends "Windows.main:GetWidget('practice_button'):DoEvent('onclick')"
        var handler = new GameLuaHandler(
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: null,
            remediationService: remediationService,
            timeProvider: _timeProvider);

        using var doc = JsonDocument.Parse("{\"code\": \"Windows.main:GetWidget('practice_button'):DoEvent('onclick')\"}");
        var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        // Even though hon_x64 is not running, the command was auto-remediated in memory
        string jsonText = ((TextContentItem)res.Content[0]).Text;
        Assert.Contains("UIManager.GetInterface('main')", jsonText);
        Assert.Contains("sysbar_quickStart", jsonText);
    }

    [Fact]
    public async Task MarkSelfHealed_WhenInvoked_UpdatesInMemoryRecordAndDiskPersistence()
    {
        var service = new ClientCommandRemediationService(_testTempDir, _timeProvider);
        var record = service.RecordAndRemediate("echo test", "error response", "stdin");
        Assert.False(record.SelfHealed);

        service.MarkSelfHealed(record.Id);

        var recent = service.GetRecentErrors(5);
        var updated = Assert.Single(recent, r => r.Id == record.Id);
        Assert.True(updated.SelfHealed);

        await service.FlushAsync();

        string ndjsonPath = Path.Combine(_testTempDir, "client_command_errors.ndjson");
        Assert.True(File.Exists(ndjsonPath));
        string[] lines = File.ReadAllLines(ndjsonPath);
        Assert.True(lines.Length >= 2); // Initial insert + updated write
        var lastRecord = JsonSerializer.Deserialize<ClientCommandErrorRecord>(lines[^1]);
        Assert.NotNull(lastRecord);
        Assert.Equal(record.Id, lastRecord.Id);
        Assert.True(lastRecord.SelfHealed);
    }

    [Fact]
    public void AnalyzeCommandError_StartGamePracticeLoginTimeout_RemediatesWithOfflineCvarGuards()
    {
        string cmd = "StartGame practice PracticeGame map:caldavar allheroes:true";
        string err = "Connection to masterserver timed out";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "console");

        Assert.Equal("PracticeLoginHang", rem.ErrorCategory);
        Assert.Contains("svr_login false", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.StartsWith("Set svr_login false; Set svr_broadcastLocal false; Set ui_quickStart true;", rem.ImprovedCommand);
        Assert.Contains("StartGame practice PracticeGame map:caldavar allheroes:true", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_CreateGameMissingMapOption_RemediatesGrammar()
    {
        string cmd = "CreateGame caldavar";
        string err = "Failed to load map";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "stdin", "server:11255");

        Assert.Equal("MalformedCommandArguments", rem.ErrorCategory);
        Assert.Contains("CreateGame syntax requires map options", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("CreateGame caldavar map:caldavar allheroes:true", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_StartGameOnDedicatedServer_RemediatesToCreateGame()
    {
        string cmd = "StartGame practice GameName map:caldavar allheroes:true";
        string err = "rejected_not_host";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "stdin", "server:11255");

        Assert.Equal("DedicatedServerCreateGameRequired", rem.ErrorCategory);
        Assert.Contains("Dedicated servers (CHostServer) load matches via 'CreateGame", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("CreateGame caldavar map:caldavar allheroes:true", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_InterfaceGlobalNil_RemediatesToCmd()
    {
        string cmd = "interface:UICmd('ClearReconnectInfo()')";
        string err = "attempt to index global 'interface' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("InvalidGlobalReference", rem.ErrorCategory);
        Assert.Contains("interface", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("Cmd('ClearReconnectInfo()')", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_LuaTriggerGlobalNil_RemediatesToGetTrigger()
    {
        string cmd = "local t = LuaTrigger.GetTrigger('ReconnectAddress')";
        string err = "attempt to index global 'LuaTrigger' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("InvalidTriggerApi", rem.ErrorCategory);
        Assert.Contains("GetTrigger", rem.RemediationHint);
        Assert.True(rem.CanAutoRetry);
        Assert.Equal("local t = GetTrigger('ReconnectAddress')", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_IoOpenSandboxRestriction_ReturnsLuaSandboxRestrictionCategory()
    {
        string cmd = "local f = io.open('test.txt', 'w')";
        string err = "attempt to index global 'io' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("LuaSandboxRestriction", rem.ErrorCategory);
        Assert.Contains("sandboxed Lua environment", rem.RemediationHint);
        Assert.False(rem.CanAutoRetry);
    }

    [Fact]
    public void AnalyzeCommandError_DebugGetUpvalueSandboxRestriction_ReturnsLuaSandboxRestrictionCategory()
    {
        string cmd = "local name, val = debug.getupvalue(f, 1)";
        string err = "attempt to index global 'debug' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("LuaSandboxRestriction", rem.ErrorCategory);
        Assert.Contains("debug", rem.RemediationHint);
        Assert.False(rem.CanAutoRetry);
    }

    [Fact]
    public async Task GameCommandHandler_ActionStatus_WithoutCommand_RoutesToClientManage()
    {
        var clientHandler = new GameClientManageHandler(
            ipcBridge: null,
            uiControlService: null,
            remediationService: null,
            timeProvider: _timeProvider);

        var handler = new GameCommandHandler(
            ipcBridge: null,
            serverManageHandler: null,
            clientManageHandler: clientHandler,
            uiControlService: null,
            remediationService: null,
            timeProvider: _timeProvider);

        using var doc = JsonDocument.Parse("{\"action\": \"status\"}");
        var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.False(res.IsError);
        string jsonText = ((TextContentItem)res.Content[0]).Text;
        Assert.Contains("status", jsonText);
    }

    [Fact]
    public async Task GameClientManageHandler_ExecuteClientCommandAsync_ScriptWithLuaTrigger_AutoRemediates()
    {
        var mockIpc = new Moq.Mock<IGameIpcBridge>();
        mockIpc.Setup(b => b.IsConnected).Returns(true);
        mockIpc.Setup(b => b.ExecuteCommandAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        var clientHandler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: null,
            remediationService: null,
            timeProvider: _timeProvider);

        var res = await clientHandler.ExecuteClientCommandAsync(
            "Script local trigger = LuaTrigger.GetTrigger('GamePhase')",
            CancellationToken.None);

        Assert.False(res.IsError);
        // Verify IPC received the remediated GetTrigger call rather than LuaTrigger.GetTrigger
        mockIpc.Verify(b => b.ExecuteCommandAsync(
            Moq.It.Is<string>(s => s.Contains("GetTrigger('GamePhase')") && !s.Contains("LuaTrigger")),
            Moq.It.IsAny<int>(),
            Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
    }

    [Fact]
    public async Task GameClientManageHandler_ExecuteClientCommandAsync_ScriptWithIo_BlocksSandboxViolation()
    {
        var mockIpc = new Moq.Mock<IGameIpcBridge>();
        mockIpc.Setup(b => b.IsConnected).Returns(true);

        var clientHandler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: null,
            remediationService: null,
            timeProvider: _timeProvider);

        var res = await clientHandler.ExecuteClientCommandAsync(
            "Script local f = io.open('test.txt', 'w')",
            CancellationToken.None);

        Assert.True(res.IsError);
        string jsonText = ((TextContentItem)res.Content[0]).Text;
        Assert.Contains("LuaSandboxRestriction", jsonText);
        // Verify mock IPC was NEVER called with the violating script
        mockIpc.Verify(b => b.ExecuteCommandAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.Never);
    }

    [Fact]
    public void AnalyzeCommandError_ColonMethodCheck_RemediatesToDotAccess()
    {
        string cmd = "if w:GetNumChildren and w:GetNumChildren() > 0 then Echo('has children') end";
        string err = "syntax error near 'and'";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("LuaMethodCheckSyntaxError", rem.ErrorCategory);
        Assert.True(rem.CanAutoRetry);
        Assert.NotNull(rem.ImprovedCommand);
        Assert.Equal("if w.GetNumChildren and w:GetNumChildren() > 0 then Echo('has children') end", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_GetRootWidget_RemediatesToGetWidget()
    {
        string cmd = "local root = interface:GetRootWidget():GetWidget('container')";
        string err = "attempt to call method 'GetRootWidget' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("NonExistentWidgetMethod", rem.ErrorCategory);
        Assert.True(rem.CanAutoRetry);
        Assert.NotNull(rem.ImprovedCommand);
        Assert.Equal("local root = interface:GetWidget('container')", rem.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeCommandError_NilWidgetDereference_ReturnsDiagnostic()
    {
        string cmd = "w:GetNumChildren()";
        string err = "attempt to call method 'GetNumChildren' (a nil value)";

        var rem = ClientCommandRemediationService.AnalyzeCommandError(cmd, err, "lua");

        Assert.Equal("NilWidgetDereference", rem.ErrorCategory);
        Assert.False(rem.CanAutoRetry);
        Assert.Contains("GetWidget", rem.RemediationHint);
    }
}


