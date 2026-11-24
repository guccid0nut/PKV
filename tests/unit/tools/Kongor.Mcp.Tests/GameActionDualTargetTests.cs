// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class GameActionDualTargetTests
{
    private static JsonElement ParseJson(string json) => JsonElement.Parse(json);

    [Fact]
    public async Task GameActionHandler_DefaultClientTarget_RoutesToIpcBridge()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IPC_CLIENT_OK");

        GameActionHandler handler = new(mockIpc.Object);

        JsonElement args = ParseJson(@"{
            ""action"": ""spawn_unit"",
            ""unit"": ""Hero_Pyromancer"",
            ""team"": 1,
            ""x"": 150.5,
            ""y"": 250.5
        }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("destination").GetString().Should().Be("client");
        root.GetProperty("action").GetString().Should().Be("spawn_unit");
        root.GetProperty("command").GetString().Should().Be("SpawnUnit Hero_Pyromancer 1 150.5 250.5");
        root.GetProperty("engineResponse").GetString().Should().Be("IPC_CLIENT_OK");

        mockIpc.Verify(x => x.ExecuteCommandAsync("SpawnUnit Hero_Pyromancer 1 150.5 250.5", 3000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameActionHandler_ServerTargetWithExplicitPort_RoutesToDedicatedServerWorker()
    {
        var tempRegistryPath = Path.Combine(Path.GetTempPath(), $"reg_action_test_{Guid.NewGuid():N}.json");
        var portRegistry = new ServerPortRegistry(tempRegistryPath);
        GameServerManageHandler serverManage = new(portRegistry: portRegistry);

        // Inject simulated stdin stream for server worker on port 11236
        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        stdinField.Should().NotBeNull();
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11236] = writer;

        GameActionHandler actionHandler = new(
            serverManageHandler: serverManage,
            portRegistry: portRegistry);

        JsonElement args = ParseJson(@"{
            ""action"": ""spawn_unit"",
            ""unit"": ""Hero_Devourer"",
            ""team"": 2,
            ""target"": ""server:11236""
        }");

        ToolCallResult result = await actionHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("destination").GetString().Should().Be("server");
        root.GetProperty("target").GetString().Should().Be("server:11236");
        root.GetProperty("port").GetInt32().Should().Be(11236);
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11236);
        root.GetProperty("dispatchedViaStdin").GetBoolean().Should().BeTrue();
        root.GetProperty("command").GetString().Should().Be("SpawnUnit Hero_Devourer 2 0.0 0.0");

        // Verify stream write
        ms.Position = 0;
        using StreamReader reader = new(ms, Encoding.UTF8);
        string written = await reader.ReadToEndAsync();
        written.Should().Contain("SpawnUnit Hero_Devourer 2 0.0 0.0");

        try { if (File.Exists(tempRegistryPath)) File.Delete(tempRegistryPath); } catch { }
    }

    [Fact]
    public async Task GameActionHandler_ServerTargetGeneric_ResolvesPortViaRegistry()
    {
        var tempRegistryPath = Path.Combine(Path.GetTempPath(), $"reg_action_test_{Guid.NewGuid():N}.json");
        var portRegistry = new ServerPortRegistry(tempRegistryPath);
        // Register active worker on port 11238
        portRegistry.RegisterWorker(processId: 65432, port: 11238, map: "caldavar", matchId: 8821);

        GameServerManageHandler serverManage = new(portRegistry: portRegistry);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11238] = writer;

        GameActionHandler actionHandler = new(
            serverManageHandler: serverManage,
            portRegistry: portRegistry);

        JsonElement args = ParseJson(@"{
            ""action"": ""give_gold"",
            ""amount"": 4500,
            ""target"": ""server""
        }");

        ToolCallResult result = await actionHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("destination").GetString().Should().Be("server");
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11238);
        root.GetProperty("resolutionReason").GetString().Should().Be("most_recent_active");
        root.GetProperty("dispatchedViaStdin").GetBoolean().Should().BeTrue();
        root.GetProperty("command").GetString().Should().Be("GiveGold 4500");

        ms.Position = 0;
        using StreamReader reader = new(ms, Encoding.UTF8);
        string written = await reader.ReadToEndAsync();
        written.Should().Contain("GiveGold 4500");

        try { if (File.Exists(tempRegistryPath)) File.Delete(tempRegistryPath); } catch { }
    }

    [Fact]
    public async Task GameActionHandler_KillUnit_DisambiguatesEntityFromDestination()
    {
        var tempRegistryPath = Path.Combine(Path.GetTempPath(), $"reg_action_test_{Guid.NewGuid():N}.json");
        var portRegistry = new ServerPortRegistry(tempRegistryPath);
        GameServerManageHandler serverManage = new(portRegistry: portRegistry);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11235] = writer;

        GameActionHandler actionHandler = new(
            serverManageHandler: serverManage,
            portRegistry: portRegistry);

        // Case 1: Target is server, entity is specified explicitly
        JsonElement serverArgs = ParseJson(@"{
            ""action"": ""kill_unit"",
            ""target"": ""server:11235"",
            ""entity"": ""Creep_LegionSiege""
        }");

        ToolCallResult serverResult = await actionHandler.HandleAsync(serverArgs);
        serverResult.IsError.Should().BeFalse();
        using (JsonDocument doc = JsonDocument.Parse(((TextContentItem)serverResult.Content[0]).Text))
        {
            doc.RootElement.GetProperty("destination").GetString().Should().Be("server");
            doc.RootElement.GetProperty("command").GetString().Should().Be("KillUnit Creep_LegionSiege");
        }

        // Case 2: Target is entity name (legacy backward compatibility) without destination keyword
        Mock<IGameIpcBridge> mockClientIpc = new();
        mockClientIpc.Setup(x => x.IsConnected).Returns(true);
        mockClientIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        GameActionHandler clientHandler = new(mockClientIpc.Object, serverManage, portRegistry);

        JsonElement clientArgs = ParseJson(@"{
            ""action"": ""kill_unit"",
            ""target"": ""Creep_HellbourneMelee""
        }");

        ToolCallResult clientResult = await clientHandler.HandleAsync(clientArgs);
        clientResult.IsError.Should().BeFalse();
        using (JsonDocument doc = JsonDocument.Parse(((TextContentItem)clientResult.Content[0]).Text))
        {
            doc.RootElement.GetProperty("destination").GetString().Should().Be("client");
            doc.RootElement.GetProperty("command").GetString().Should().Be("KillUnit Creep_HellbourneMelee");
        }

        try { if (File.Exists(tempRegistryPath)) File.Delete(tempRegistryPath); } catch { }
    }

    [Fact]
    public async Task GameActionHandler_ServerTarget_AutoRemediatesItemPrefix()
    {
        var tempRegistryPath = Path.Combine(Path.GetTempPath(), $"reg_action_test_{Guid.NewGuid():N}.json");
        var portRegistry = new ServerPortRegistry(tempRegistryPath);
        GameServerManageHandler serverManage = new(portRegistry: portRegistry);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11235] = writer;

        GameActionHandler actionHandler = new(
            serverManageHandler: serverManage,
            portRegistry: portRegistry);

        JsonElement args = ParseJson(@"{
            ""action"": ""buy_item"",
            ""item"": ""PostHaste"",
            ""destination"": ""server"",
            ""port"": 11235
        }");

        ToolCallResult result = await actionHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("selfHealed").GetBoolean().Should().BeTrue();
        root.GetProperty("remediationHint").GetString().Should().Contain("Item_PostHaste");
        root.GetProperty("command").GetString().Should().Be("PurchaseItem Item_PostHaste");

        ms.Position = 0;
        using StreamReader reader = new(ms, Encoding.UTF8);
        string written = await reader.ReadToEndAsync();
        written.Should().Contain("PurchaseItem Item_PostHaste");

        try { if (File.Exists(tempRegistryPath)) File.Delete(tempRegistryPath); } catch { }
    }

    [Fact]
    public async Task GameActionHandler_OfflineServer_ReturnsStructuredErrorWithRemediation()
    {
        var tempRegistryPath = Path.Combine(Path.GetTempPath(), $"reg_action_test_{Guid.NewGuid():N}.json");
        var portRegistry = new ServerPortRegistry(tempRegistryPath);
        GameServerManageHandler serverManage = new(portRegistry: portRegistry);

        GameActionHandler actionHandler = new(
            serverManageHandler: serverManage,
            portRegistry: portRegistry);

        JsonElement args = ParseJson(@"{
            ""action"": ""spawn_unit"",
            ""unit"": ""Hero_Pyromancer"",
            ""target"": ""server:99999""
        }");

        ToolCallResult result = await actionHandler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("target_offline");
        root.GetProperty("destination").GetString().Should().Be("server");
        root.GetProperty("port").GetInt32().Should().Be(99999);
        root.TryGetProperty("remediation", out JsonElement remEl).Should().BeTrue();

        try { if (File.Exists(tempRegistryPath)) File.Delete(tempRegistryPath); } catch { }
    }

    [Fact]
    public async Task McpToolRegistry_GameActionHandler_WiredWithServerManageHandlerAndPortRegistry()
    {
        McpToolRegistry registry = new();

        registry.GameActionHandler.Should().NotBeNull();
        registry.GameServerManageHandler.Should().NotBeNull();

        ToolDefinition def = McpToolRegistry.CreateGameActionDefinition();
        def.InputSchema.Properties.Should().ContainKey("destination");
        def.InputSchema.Properties.Should().ContainKey("channel");
        def.InputSchema.Properties.Should().ContainKey("entity");
        def.InputSchema.Properties.Should().ContainKey("item");
        def.InputSchema.Properties.Should().ContainKey("target");
        def.InputSchema.Properties.Should().ContainKey("port");
    }
}
