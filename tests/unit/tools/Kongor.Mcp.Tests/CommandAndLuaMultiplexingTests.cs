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

public sealed class CommandAndLuaMultiplexingTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private static JsonElement ParseJson(string json) => JsonElement.Parse(json);

    private (GameServerManageHandler serverManage, MemoryStream stream, ServerPortRegistry registry) CreateIsolatedServerManageWithStdin(int port = 11235)
    {
        string tempRegistryPath = Path.Combine(Path.GetTempPath(), $"reg_mux_{Guid.NewGuid():N}.json");
        _tempFiles.Add(tempRegistryPath);
        var reg = new ServerPortRegistry(tempRegistryPath);

        var serverManage = new GameServerManageHandler(portRegistry: reg);
        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        stdinField.Should().NotBeNull();
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;

        MemoryStream ms = new();
        StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[port] = writer;

        return (serverManage, ms, reg);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    [Fact]
    public async Task GameCommandHandler_TargetBoth_DispatchesToBothClientAndServer()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("CLIENT_ECHO_OK");

        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var commandHandler = new GameCommandHandler(
            ipcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""command"": ""Echo TestMultiplex"",
            ""target"": ""both""
        }");

        ToolCallResult result = await commandHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("command").GetString().Should().Be("Echo TestMultiplex");
        root.GetProperty("multiplexed").GetBoolean().Should().BeTrue();
        root.GetProperty("port").GetInt32().Should().Be(11235);

        // Verify client payload
        JsonElement client = root.GetProperty("client");
        client.GetProperty("status").GetString().Should().Be("success");
        string clientOutput = client.TryGetProperty("response", out var respEl) ? respEl.GetString()! : client.GetProperty("engineResponse").GetString()!;
        clientOutput.Should().Be("CLIENT_ECHO_OK");

        // Verify server payload
        JsonElement server = root.GetProperty("server");
        server.GetProperty("status").GetString().Should().Be("success");
        server.GetProperty("port").GetInt32().Should().Be(11235);
        server.GetProperty("dispatchedViaStdin").GetBoolean().Should().BeTrue();

        // Verify streams & mocks
        mockIpc.Verify(x => x.ExecuteCommandAsync("Echo TestMultiplex", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8);
        string serverWritten = await reader.ReadToEndAsync();
        serverWritten.Should().Contain("Echo TestMultiplex");
    }

    [Fact]
    public async Task GameCommandHandler_TargetAll_DispatchesToBothClientAndServer()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK_ALL");

        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var commandHandler = new GameCommandHandler(
            ipcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""command"": ""Echo TargetAll"",
            ""target"": ""all""
        }");

        ToolCallResult result = await commandHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("multiplexed").GetBoolean().Should().BeTrue();

        mockIpc.Verify(x => x.ExecuteCommandAsync("Echo TargetAll", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8);
        string serverWritten = await reader.ReadToEndAsync();
        serverWritten.Should().Contain("Echo TargetAll");
    }

    [Fact]
    public async Task GameCommandHandler_TargetBoth_TimescaleSimulationAction_PropagatesToBoth()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("TIMESCALE_OK");

        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var commandHandler = new GameCommandHandler(
            ipcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""action"": ""set_timescale"",
            ""scale"": 2.5,
            ""target"": ""both""
        }");

        ToolCallResult result = await commandHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("set_timescale");
        string resolvedCmd = root.GetProperty("command").GetString()!;
        resolvedCmd.Should().Contain("Set host_timeScale 2.5000");

        mockIpc.Verify(x => x.ExecuteCommandAsync(resolvedCmd, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8);
        string serverWritten = await reader.ReadToEndAsync();
        serverWritten.Should().Contain("Set host_timeScale 2.5000");
    }

    [Fact]
    public async Task GameCommandHandler_TargetServer_ExplicitChannelStdin_RoutesToWorkerStdin()
    {
        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var commandHandler = new GameCommandHandler(
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""command"": ""AddFakePlayer 1"",
            ""target"": ""server:11235"",
            ""channel"": ""stdin""
        }");

        ToolCallResult result = await commandHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("channel").GetString().Should().Be("stdin");
        root.GetProperty("dispatchedViaStdin").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GameCommandHandler_TargetServer_PortRegistryResolution_ResolvesActiveWorkerPort()
    {
        var (serverManage, stream, portRegistry) = CreateIsolatedServerManageWithStdin(11245);
        portRegistry.RegisterWorker(processId: 9999, port: 11245, map: "caldavar", matchId: 99);

        var commandHandler = new GameCommandHandler(serverManageHandler: serverManage, portRegistry: portRegistry);

        JsonElement args = ParseJson(@"{
            ""command"": ""Echo ActiveMatchWorker"",
            ""target"": ""server"",
            ""map"": ""caldavar""
        }");

        ToolCallResult result = await commandHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("port").GetInt32().Should().Be(11245);
        root.GetProperty("resolutionReason").GetString().Should().Contain("caldavar");

        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8);
        string serverWritten = await reader.ReadToEndAsync();
        serverWritten.Should().Contain("Echo ActiveMatchWorker");
    }

    [Fact]
    public async Task GameCommandHandler_TargetBoth_PartialFailure_ReportsBothResultsCleanly()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(false); // Client offline

        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var commandHandler = new GameCommandHandler(
            ipcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""command"": ""Echo ClientOffline"",
            ""target"": ""both"",
            ""autoLaunch"": false
        }");

        ToolCallResult result = await commandHandler.HandleAsync(args);

        // One succeeded (server) and one failed (client) -> partial_success, IsError = false
        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("partial_success");
        root.GetProperty("multiplexed").GetBoolean().Should().BeTrue();

        JsonElement client = root.GetProperty("client");
        client.GetProperty("status").GetString().Should().BeOneOf("error", "target_offline");

        JsonElement server = root.GetProperty("server");
        server.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task GameLuaHandler_TargetBoth_DispatchesRawLuaToBothClientAndServer()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("CLIENT_LUA_OK");

        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var luaHandler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""script"": ""return 42"",
            ""target"": ""both""
        }");

        ToolCallResult result = await luaHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("multiplexed").GetBoolean().Should().BeTrue();

        // Server gets Script return 42
        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8);
        string serverWritten = await reader.ReadToEndAsync();
        serverWritten.Should().Contain("return 42");

        // Client gets Script return 42
        mockIpc.Verify(x => x.ExecuteCommandAsync(It.Is<string>(s => s.Contains("return 42")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameLuaHandler_TargetBoth_UIOnlyAction_SkipsServerGracefully()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("LOGIN_HIDDEN");

        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var luaHandler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""action"": ""hide_login"",
            ""target"": ""both""
        }");

        ToolCallResult result = await luaHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("both");

        // Client executed normally
        JsonElement client = root.GetProperty("client");
        client.GetProperty("status").GetString().Should().Be("success");

        // Server skipped gracefully
        JsonElement server = root.GetProperty("server");
        server.GetProperty("status").GetString().Should().Be("skipped");
        server.GetProperty("message").GetString().Should().Contain("client GUI operation and was skipped on dedicated server worker");

        // Server stream received nothing
        stream.Length.Should().Be(0);
    }

    [Fact]
    public async Task GameLuaHandler_TargetServer_DispatchesLuaToDedicatedServerStdin()
    {
        var (serverManage, stream, reg) = CreateIsolatedServerManageWithStdin(11235);
        var luaHandler = new GameLuaHandler(
            serverManageHandler: serverManage,
            portRegistry: reg);

        JsonElement args = ParseJson(@"{
            ""script"": ""print('dedicated server lua')"",
            ""target"": ""server:11235"",
            ""channel"": ""stdin""
        }");

        ToolCallResult result = await luaHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("channel").GetString().Should().Be("stdin");
        root.GetProperty("dispatchedViaStdin").GetBoolean().Should().BeTrue();

        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8);
        string serverWritten = await reader.ReadToEndAsync();
        serverWritten.Should().Contain("print('dedicated server lua')");
    }

    [Fact]
    public async Task GameLuaHandler_TargetServer_PortResolution_ResolvesActiveWorker()
    {
        var (serverManage, stream, portRegistry) = CreateIsolatedServerManageWithStdin(11260);
        portRegistry.RegisterWorker(processId: 8888, port: 11260, map: "midwars", matchId: 777);

        var luaHandler = new GameLuaHandler(serverManageHandler: serverManage, portRegistry: portRegistry);

        JsonElement args = ParseJson(@"{
            ""script"": ""print('match worker test')"",
            ""target"": ""server"",
            ""matchId"": 777
        }");

        ToolCallResult result = await luaHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("port").GetInt32().Should().Be(11260);
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11260);
        root.GetProperty("resolutionReason").GetString().Should().Contain("777");
    }

    [Fact]
    public async Task GameLuaHandler_SelfHealing_RewritesWindowsToUIManager()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(x => x.IsConnected).Returns(true);
        string capturedCommand = string.Empty;
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => capturedCommand = cmd)
            .ReturnsAsync("OK");

        var luaHandler = new GameLuaHandler(gameIpcBridge: mockIpc.Object);

        JsonElement args = ParseJson(@"{
            ""script"": ""Windows.main:GetWidget('button'):DoEvent()"",
            ""target"": ""client""
        }");

        ToolCallResult result = await luaHandler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("selfHealed").GetBoolean().Should().BeTrue();
        root.GetProperty("remediationCategory").GetString().Should().Be("NilGlobalReference");

        capturedCommand.Should().Contain("UIManager.GetInterface('main'):GetWidget('button'):DoEvent()");
    }

    [Fact]
    public void McpToolRegistry_GameCommandAndLuaDefinitions_ExposeMultiplexingSchema()
    {
        var registry = new McpToolRegistry(registerDefaults: true);

        // Verify kongor_game_command definition
        ToolDefinition? cmdTool = registry.GetTool("kongor_game_command");
        cmdTool.Should().NotBeNull();
        cmdTool!.InputSchema.Properties.Should().ContainKey("channel");
        cmdTool.InputSchema.Properties.Should().ContainKey("target");
        cmdTool.Description.Should().Contain("both");
        cmdTool.InputSchema.Properties["target"].Description.Should().Contain("both");

        // Verify kongor_game_lua definition
        ToolDefinition? luaTool = registry.GetTool("kongor_game_lua");
        luaTool.Should().NotBeNull();
        luaTool!.InputSchema.Properties.Should().ContainKey("channel");
        luaTool.InputSchema.Properties.Should().ContainKey("target");
        luaTool.InputSchema.Properties["target"].Enum.Should().Contain(new[] { "client", "server", "both" });
        luaTool.Description.Should().Contain("both");
    }
}
