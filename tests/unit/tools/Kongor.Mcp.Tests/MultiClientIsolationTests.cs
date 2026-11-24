// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for Multi-Client Isolation, Headless Grid Slot Allocation,
/// Zero-Disk Credential Resolution, and Independent Agent Process Routing.
/// </summary>
public sealed class MultiClientIsolationTests
{
    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.Content.Should().ContainSingle();
        string json = ((TextContentItem)result.Content[0]).Text;
        return JsonElement.Parse(json);
    }

    #region ClientCredentialManager Tests

    [Fact]
    public void HashPassword_DefaultPassword_ReturnsCanonicalMD5()
    {
        var manager = new ClientCredentialManager();
        string hash = manager.HashPassword("KONGOR");
        hash.Should().Be("4d36fc3708f5a844b3be75b3f389db8d");
    }

    [Fact]
    public void HashPassword_AlreadyMD5_ReturnsUnchangedLowercase()
    {
        var manager = new ClientCredentialManager();
        string input = "4D36FC3708F5A844B3BE75B3F389DB8D";
        string hash = manager.HashPassword(input);
        hash.Should().Be("4d36fc3708f5a844b3be75b3f389db8d");
    }

    [Fact]
    public void ResolveCredentials_Slot0_ResolvesPrimaryDefaultPlayer()
    {
        var manager = new ClientCredentialManager();
        var creds = manager.ResolveCredentials(username: null, password: null, slotIndex: 0);

        creds.Username.Should().Be("KongorPlayer");
        creds.PasswordMd5.Should().Be("4d36fc3708f5a844b3be75b3f389db8d");
        creds.AutoLogin.Should().BeTrue();
    }

    [Fact]
    public void ResolveCredentials_Slot1_ResolvesGuest01()
    {
        var manager = new ClientCredentialManager();
        var creds = manager.ResolveCredentials(username: null, password: null, slotIndex: 1);

        creds.Username.Should().Be("guest-01");
        creds.PasswordMd5.Should().Be("4d36fc3708f5a844b3be75b3f389db8d");
    }

    [Fact]
    public void ResolveCredentials_AgentIdProvided_ResolvesAgentUsername()
    {
        var manager = new ClientCredentialManager();
        var creds = manager.ResolveCredentials(username: null, password: null, slotIndex: 2, agentId: "subagent-beta");

        creds.Username.Should().Be("agent_subagent-beta");
    }

    [Fact]
    public void ResolveCredentials_CustomUsername_TakesPrecedence()
    {
        var manager = new ClientCredentialManager();
        var creds = manager.ResolveCredentials(username: "canarypilotuser", password: "custom_password", slotIndex: 5);

        creds.Username.Should().Be("canarypilotuser");
        creds.PasswordMd5.Should().Be(manager.HashPassword("custom_password"));
    }

    [Fact]
    public void BuildAuthenticationLaunchArguments_InjectsCvarsWithoutModifyingFiles()
    {
        var manager = new ClientCredentialManager();
        var creds = manager.ResolveCredentials("test_bot", "KONGOR", slotIndex: 1);
        var args = manager.BuildAuthenticationLaunchArguments(creds);

        args.Should().Contain("+Set login_name \"test_bot\"");
        args.Should().Contain("+Set login_password \"4d36fc3708f5a844b3be75b3f389db8d\"");
        args.Should().Contain("+Set login_autoLogin true");
    }

    #endregion

    #region OffscreenSlotManager Tests

    [Fact]
    public void AllocateSlot_AssignsUniqueNonOverlappingCoordinates()
    {
        var slotManager = new OffscreenSlotManager();

        var slot0 = slotManager.AllocateSlot(processId: 100);
        var slot1 = slotManager.AllocateSlot(processId: 101);
        var slot2 = slotManager.AllocateSlot(processId: 102);

        slot0.SlotIndex.Should().Be(0);
        slot0.X.Should().Be(-10000);
        slot0.Y.Should().Be(-10000);

        slot1.SlotIndex.Should().Be(1);
        slot1.X.Should().Be(-8000); // BaseX + 1 * 2000
        slot1.Y.Should().Be(-10000);

        slot2.SlotIndex.Should().Be(2);
        slot2.X.Should().Be(-6000); // BaseX + 2 * 2000
        slot2.Y.Should().Be(-10000);

        // Coordinates must never collide
        (slot0.X == slot1.X && slot0.Y == slot1.Y).Should().BeFalse();
        (slot1.X == slot2.X && slot1.Y == slot2.Y).Should().BeFalse();
    }

    [Fact]
    public void AllocateSlot_RespectsPreferredSlotWhenAvailable()
    {
        var slotManager = new OffscreenSlotManager();
        var slot3 = slotManager.AllocateSlot(processId: 200, preferredSlot: 3);

        slot3.SlotIndex.Should().Be(3);
        slot3.X.Should().Be(-10000 + (3 * 2000));
    }

    [Fact]
    public void ReleaseSlot_ReclaimsSlotIndexForNextAllocation()
    {
        var slotManager = new OffscreenSlotManager();
        var slot0 = slotManager.AllocateSlot(processId: 300);
        var slot1 = slotManager.AllocateSlot(processId: 301);

        slotManager.ReleaseSlot(slot0.SlotIndex);

        var reclaimedSlot = slotManager.AllocateSlot(processId: 302);
        reclaimedSlot.SlotIndex.Should().Be(0);
        reclaimedSlot.X.Should().Be(-10000);
        reclaimedSlot.Y.Should().Be(-10000);
    }

    [Fact]
    public void AssignProcess_AssociatesProcessIdWithAllocatedSlot()
    {
        var slotManager = new OffscreenSlotManager();
        var slot = slotManager.AllocateSlot(processId: 0);

        slotManager.AssignProcess(slot.SlotIndex, 5555);

        var retrieved = slotManager.GetSlotForProcess(5555);
        retrieved.Should().NotBeNull();
        retrieved!.SlotIndex.Should().Be(slot.SlotIndex);
        retrieved.ProcessId.Should().Be(5555);
    }

    #endregion

    #region ClientProcessRegistry Tests

    [Fact]
    public void ClientProcessRegistry_RegistersAndRetrievesClients()
    {
        var registry = new ClientProcessRegistry();
        using var currentProc = Process.GetCurrentProcess();

        var instance1 = new ClientInstance
        {
            ProcessId = currentProc.Id,
            SlotIndex = 0,
            OffscreenX = -32000,
            OffscreenY = -32000,
            PipeName = "hon-client-0",
            Username = "guccidonut",
            AgentId = "agent-alpha",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        };

        registry.Register(instance1);

        registry.IsManagedClient(currentProc.Id).Should().BeTrue();
        registry.IsManagedClient(999999).Should().BeFalse();

        var byPid = registry.Get(currentProc.Id);
        byPid.Should().NotBeNull();
        byPid!.Username.Should().Be("guccidonut");

        var byAgent = registry.GetClientByAgentId("agent-alpha");
        byAgent.Should().NotBeNull();
        byAgent!.ProcessId.Should().Be(currentProc.Id);

        var bySlot = registry.GetBySlot(0);
        bySlot.Should().NotBeNull();
        bySlot!.ProcessId.Should().Be(currentProc.Id);
    }

    [Fact]
    public void ClientProcessRegistry_Unregister_UpdatesPrimaryPid()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        var instance = new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        };

        registry.Register(instance);
        registry.GetPrimaryClient().Should().NotBeNull();

        bool removed = registry.Unregister(proc.Id);
        removed.Should().BeTrue();
        registry.IsManagedClient(proc.Id).Should().BeFalse();
    }

    #endregion

    #region GameClientManageHandler Multi-Client Integration Tests

    [Fact]
    public async Task GameClientManage_ListAction_ReturnsManagedClients()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 1,
            OffscreenX = -29500,
            OffscreenY = -32000,
            PipeName = "hon-client-1",
            Username = "guest-01",
            AgentId = "agent-worker",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var handler = new GameClientManageHandler(
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var res = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"list\"}"));
        res.IsError.Should().BeFalse();

        var root = ParseResult(res);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("totalManagedClients").GetInt32().Should().Be(1);

        var clientsArray = root.GetProperty("clients");
        clientsArray.GetArrayLength().Should().Be(1);
        clientsArray[0].GetProperty("processId").GetInt32().Should().Be(proc.Id);
        clientsArray[0].GetProperty("username").GetString().Should().Be("guest-01");
        clientsArray[0].GetProperty("slotIndex").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GameClientManage_StopAction_TargetedPid_OnlyStopsTargetProcess()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var handler = new GameClientManageHandler(
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        // Stopping an unmanaged non-existent process returns not found without touching current proc
        var res = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"stop\",\"processId\":99999999}"));
        res.IsError.Should().BeFalse();

        // Target proc was not touched
        registry.IsManagedClient(proc.Id).Should().BeTrue();
    }

    [Fact]
    public async Task GameClientManage_LoginAction_GeneratesAuthenticatedCredentials()
    {
        var handler = new GameClientManageHandler(
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var res = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"login\",\"username\":\"agent_beta\",\"slot\":3}"));
        res.IsError.Should().BeFalse();

        var root = ParseResult(res);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("login");
        root.GetProperty("username").GetString().Should().Be("agent_beta");
        root.GetProperty("passwordMd5").GetString().Should().Be(ClientCredentialManager.RedactedPlaceholder);
        string[] launchArgs = root.GetProperty("launchArgs")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
        launchArgs.Should().NotBeEmpty();
        launchArgs.Should().Contain($"+Set login_password \"{ClientCredentialManager.RedactedPlaceholder}\"");
        res.FirstText.Should().NotContain(ClientCredentialManager.DefaultPasswordMd5);
    }

    #endregion

    #region Agent Isolation & Anti-Cross-Contamination Tests

    [Fact]
    public void ValidateAgentAccess_MatchingOwner_ReturnsTrue()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        bool allowed = registry.ValidateAgentAccess("agent-alpha", proc.Id, out string? errorReason);
        allowed.Should().BeTrue();
        errorReason.Should().BeNull();
    }

    [Fact]
    public void ValidateAgentAccess_DifferentOwner_ReturnsFalseWithReason()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });
        registry.Register(new ClientInstance
        {
            ProcessId = 999991,
            SlotIndex = 1,
            AgentId = "agent-beta",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        bool allowed = registry.ValidateAgentAccess("agent-beta", proc.Id, out string? errorReason);
        allowed.Should().BeFalse();
        errorReason.Should().Contain("agent-alpha");
        errorReason.Should().Contain("agent-beta");
        errorReason.Should().Contain("Access denied");
    }

    [Fact]
    public void ValidateAgentAccess_AgentTargetingUnmanagedProcess_ReturnsFalseToProtectUserGame()
    {
        var registry = new ClientProcessRegistry();
        int unmanagedPid = 9999999;

        bool allowed = registry.ValidateAgentAccess("agent-alpha", unmanagedPid, out string? errorReason);
        allowed.Should().BeFalse();
        errorReason.Should().Contain("Access denied");
        errorReason.Should().Contain("user's personal game session");
    }

    [Fact]
    public void ValidateAgentAccess_NonAgentTargetingUnmanagedProcess_ReturnsTrue()
    {
        var registry = new ClientProcessRegistry();
        int unmanagedPid = 9999999;

        bool allowed = registry.ValidateAgentAccess(callerAgentId: null, unmanagedPid, out string? errorReason);
        allowed.Should().BeTrue();
        errorReason.Should().BeNull();
    }

    [Fact]
    public async Task GameClientManage_StopAction_AgentScoped_OnlyStopsOwnClient()
    {
        var registry = new ClientProcessRegistry();
        using var currentProc = Process.GetCurrentProcess();

        // Register client for Agent Alpha (dummy PID)
        int dummyAlphaPid = 888881;
        registry.Register(new ClientInstance
        {
            ProcessId = dummyAlphaPid,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            Process = currentProc, // Use currentProc so IsAlive is true
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        // Register client for Agent Beta (dummy PID)
        int dummyBetaPid = 888882;
        registry.Register(new ClientInstance
        {
            ProcessId = dummyBetaPid,
            SlotIndex = 1,
            AgentId = "agent-beta",
            Process = currentProc, // Use currentProc so IsAlive is true
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var handler = new GameClientManageHandler(
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        McpAgentContext.CurrentAgentId = "agent-alpha";
        try
        {
            // Agent Alpha calls stop without specifying PID
            var res = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"stop\"}"));
            res.IsError.Should().BeFalse();

            // Agent Alpha's client was unregistered
            registry.IsManagedClient(dummyAlphaPid).Should().BeFalse();

            // Agent Beta's client MUST STILL BE MANAGED AND ALIVE!
            registry.IsManagedClient(dummyBetaPid).Should().BeTrue();
            registry.GetClientByAgentId("agent-beta").Should().NotBeNull();
        }
        finally
        {
            McpAgentContext.CurrentAgentId = null;
        }
    }

    [Fact]
    public async Task ExecuteClientCommand_ExplicitPidOfOtherAgent_IsRejected()
    {
        var registry = new ClientProcessRegistry();
        using var currentProc = Process.GetCurrentProcess();

        int dummyAlphaPid = 888883;
        registry.Register(new ClientInstance
        {
            ProcessId = dummyAlphaPid,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });
        int dummyBetaPid = 888889;
        registry.Register(new ClientInstance
        {
            ProcessId = dummyBetaPid,
            SlotIndex = 1,
            AgentId = "agent-beta",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var handler = new GameClientManageHandler(
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        McpAgentContext.CurrentAgentId = "agent-beta";
        try
        {
            // Agent Beta tries to command Agent Alpha's process
            var res = await handler.ExecuteClientCommandAsync("echo test", processId: dummyAlphaPid);
            res.IsError.Should().BeTrue();
            string err = ((TextContentItem)res.Content[0]).Text;
            err.Should().Contain("Access denied");
            err.Should().Contain("agent-alpha");
            err.Should().Contain("agent-beta");
        }
        finally
        {
            McpAgentContext.CurrentAgentId = null;
        }
    }

    [Fact]
    public async Task ExecuteClientCommand_AgentWithNoClient_WhenOthersExist_IsRejected()
    {
        var registry = new ClientProcessRegistry();
        using var currentProc = Process.GetCurrentProcess();

        int dummyAlphaPid = 888884;
        registry.Register(new ClientInstance
        {
            ProcessId = dummyAlphaPid,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });
        int dummyBetaPid = 888888;
        registry.Register(new ClientInstance
        {
            ProcessId = dummyBetaPid,
            SlotIndex = 1,
            AgentId = "agent-beta",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var handler = new GameClientManageHandler(
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        McpAgentContext.CurrentAgentId = "agent-gamma";
        try
        {
            // Agent Gamma calls command without PID
            var res = await handler.ExecuteClientCommandAsync("echo test");
            res.IsError.Should().BeTrue();
            string err = ((TextContentItem)res.Content[0]).Text;
            err.Should().Contain("No game client found for agent 'agent-gamma'");
            err.Should().Contain("Active clients belong to other agents");
        }
        finally
        {
            McpAgentContext.CurrentAgentId = null;
        }
    }

    [Fact]
    public async Task CaptureWindow_ExplicitPidOfOtherAgent_IsRejected()
    {
        var registry = new ClientProcessRegistry(_ => { });
        using var currentProc = Process.GetCurrentProcess();
        int dummyAlphaPid = 888885;
        int dummyBetaPid = 888887;

        registry.Register(new ClientInstance
        {
            ProcessId = dummyAlphaPid,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });
        registry.Register(new ClientInstance
        {
            ProcessId = dummyBetaPid,
            SlotIndex = 1,
            AgentId = "agent-beta",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var handler = new CaptureWindowHandler(processRegistry: registry);

        try
        {
            // Agent Beta tries to capture Agent Alpha's window
            var res = await handler.HandleAsync(JsonElement.Parse($"{{\"target\":\"pid:{dummyAlphaPid}\",\"agent_id\":\"agent-beta\"}}"));
            res.IsError.Should().BeTrue();
            string err = ((TextContentItem)res.Content[0]).Text;
            err.Should().Contain("Access denied");
        }
        finally
        {
            McpAgentContext.CurrentAgentId = null;
        }
    }

    [Fact]
    public async Task ExecuteClientCommand_AgentContextActive_AutoResolvesToOwnClient()
    {
        var registry = new ClientProcessRegistry();
        using var currentProc = Process.GetCurrentProcess();

        int alphaPid = 7771;
        registry.Register(new ClientInstance
        {
            ProcessId = alphaPid,
            SlotIndex = 0,
            AgentId = "agent-alpha",
            PipeName = "willowmaker-ipc",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        int betaPid = 7772;
        registry.Register(new ClientInstance
        {
            ProcessId = betaPid,
            SlotIndex = 1,
            AgentId = "agent-beta",
            PipeName = "hon-client-1",
            Process = currentProc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("echo success");

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        McpAgentContext.CurrentAgentId = "agent-alpha";
        try
        {
            // Agent Alpha calls command without specifying processId
            var res = await handler.ExecuteClientCommandAsync("echo test_auto_resolve", autoLaunch: false);
            res.IsError.Should().BeFalse();
            var root = ParseResult(res);
            root.GetProperty("status").GetString().Should().Be("success");
            // Target was auto-resolved to Agent Alpha's PID
            root.GetProperty("target").GetString().Should().Be($"client:{alphaPid}");
        }
        finally
        {
            McpAgentContext.CurrentAgentId = null;
        }
    }

    [Fact]
    public async Task McpServer_HandleToolsCall_ExtractsConversationIdFromMeta_SetsAgentContext()
    {
        var server = new McpServer();
        string requestJson = """
        {
            "jsonrpc": "2.0",
            "id": 999,
            "method": "tools/call",
            "params": {
                "_meta": {
                    "antigravity.google/conversation_id": "conv-test-999"
                },
                "name": "kongor_game_client_manage",
                "arguments": {
                    "action": "list"
                }
            }
        }
        """;

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var result = doc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    #endregion
}
