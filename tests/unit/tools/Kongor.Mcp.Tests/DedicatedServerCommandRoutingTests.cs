// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class DedicatedServerCommandRoutingTests
{
    private static JsonElement ParseJson(string json) => JsonElement.Parse(json);

    [Fact]
    public async Task HandleServerCommandAsync_DefaultPort_EnforcesPort11235()
    {
        // When port is omitted and no active worker registered, the Dedicated Game Server Command Routing Rule enforces port 11235
        var tempRegistryPath = Path.Combine(Path.GetTempPath(), $"test_reg_{Guid.NewGuid():N}.json");
        var portRegistry = new ServerPortRegistry(tempRegistryPath);
        GameServerManageHandler serverManageHandler = new(portRegistry: portRegistry);
        GameCommandHandler commandHandler = new(serverManageHandler: serverManageHandler, portRegistry: portRegistry);

        JsonElement args = ParseJson("{\"command\": \"AddFakePlayer 1\"}");
        ToolCallResult result = await commandHandler.HandleServerCommandAsync(args);

        // Result should indicate either success (if server running) or error for port 11235, never generic or unnamed
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"port\": 11235");
        text.Should().Contain("AddFakePlayer 1");

        try { if (File.Exists(tempRegistryPath)) File.Delete(tempRegistryPath); } catch { }
    }

    [Fact]
    public async Task HandleServerCommandAsync_ExplicitPort11235_RoutesTo11235()
    {
        GameServerManageHandler serverManageHandler = new();
        GameCommandHandler commandHandler = new(serverManageHandler: serverManageHandler);

        JsonElement args = ParseJson("{\"command\": \"AddFakePlayer 2\", \"port\": 11235}");
        ToolCallResult result = await commandHandler.HandleServerCommandAsync(args);

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"port\": 11235");
        text.Should().Contain("AddFakePlayer 2");
    }

    [Fact]
    public async Task HandleServerCommandAsync_MissingCommand_ReturnsError()
    {
        GameCommandHandler commandHandler = new();
        JsonElement args = ParseJson("{\"port\": 11235}");

        ToolCallResult result = await commandHandler.HandleServerCommandAsync(args);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Missing required parameter 'command'");
    }

    [Fact]
    public async Task HandleServerCommandAsync_InvalidNonExistentPort_ReturnsError()
    {
        GameServerManageHandler serverManageHandler = new();
        GameCommandHandler commandHandler = new(serverManageHandler: serverManageHandler);

        JsonElement args = ParseJson("{\"command\": \"AddFakePlayer 1\", \"port\": 99999}");
        ToolCallResult result = await commandHandler.HandleServerCommandAsync(args);

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"port\": 99999");
        text.Should().Contain("No running dedicated game server worker found on port 99999");
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_DualChannel_RedirectedStdinDispatchesSuccessfully()
    {
        GameServerManageHandler handler = new();

        // Inject simulated standard input stream for port 11235 via reflection
        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        stdinField.Should().NotBeNull();
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(handler)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11235] = writer;

        ToolCallResult result = await handler.ExecuteCommandOnPortAsync(11235, "AddFakePlayer 1");

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"port\": 11235");
        jsonText.Should().Contain("\"command\": \"AddFakePlayer 1\"");
        jsonText.Should().Contain("\"dispatchedViaStdin\": true");

        // Verify content was actually written into the stream
        ms.Position = 0;
        using StreamReader reader = new(ms, Encoding.UTF8);
        string writtenContent = await reader.ReadToEndAsync();
        writtenContent.Should().Contain("AddFakePlayer 1");
    }

    [Fact]
    public async Task GameServerManageHandler_CommandAction_RoutesToExecuteCommandOnPort()
    {
        GameServerManageHandler handler = new();

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(handler)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11235] = writer;

        JsonElement args = ParseJson("{\"action\": \"command\", \"port\": 11235, \"command\": \"AddFakePlayer 2\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"dispatchedViaStdin\": true");
        jsonText.Should().Contain("\"port\": 11235");
    }

    [Fact]
    public void BuildDedicatedServerArguments_WhenInvoked_IncludesPortVoicePortAndNamedPipe()
    {
        IReadOnlyList<string> args = GameServerManageHandler.BuildDedicatedServerArguments(
            gameDir: "C:/Games/HoN",
            port: 11236,
            masterServer: "127.0.0.1",
            serverName: "Test Server",
            map: "caldavar");

        args.Should().Contain("-port 11236");
        args.Should().Contain("-voiceport 11436");
        args.Should().Contain("+Set svr_port 11236");
        args.Should().Contain("+Set svr_voicePort 11436");
        args.Should().Contain("-pipe hon-server-11236");
        args.Should().Contain("+CreateGame caldavar map:caldavar allheroes:true");
    }

    [Theory]
    [InlineData("-dedicated -port 11236", 11236)]
    [InlineData("+Set svr_port 11237", 11237)]
    [InlineData("-startServerPort 11238", 11238)]
    [InlineData("-port=11239", 11239)]
    [InlineData("svr_port=\"11240\"", 11240)]
    [InlineData("", null)]
    [InlineData("-dedicated -otherOption", null)]
    public void ExtractPortFromCommandLine_WhenInvoked_ParsesVariousFormats(string cmdLine, int? expectedPort)
    {
        int? result = GameServerManageHandler.ExtractPortFromCommandLine(cmdLine);
        result.Should().Be(expectedPort);
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_ChannelStdin_DispatchesOnlyViaStdin()
    {
        GameServerManageHandler handler = new();
        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(handler)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11235] = writer;

        ToolCallResult result = await handler.ExecuteCommandOnPortAsync(11235, "echo StdinOnly", autoLaunch: false, channel: "stdin");

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"dispatchedViaStdin\": true");
        jsonText.Should().Contain("\"channel\": \"stdin\"");

        ms.Position = 0;
        using StreamReader reader = new(ms, Encoding.UTF8);
        string written = await reader.ReadToEndAsync();
        written.Should().Contain("echo StdinOnly");
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_ChannelPipe_DispatchesViaNamedPipeStream()
    {
        int testPort = 11289;
        string pipeName = $"hon-server-{testPort}";
        await using NamedPipeServerStream serverPipe = new(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var readTask = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync();
            using StreamReader pipeReader = new(serverPipe, Encoding.UTF8, leaveOpen: true);
            return await pipeReader.ReadLineAsync();
        });

        GameServerManageHandler handler = new();
        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(handler)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[testPort] = writer;

        ToolCallResult result = await handler.ExecuteCommandOnPortAsync(testPort, "echo PipeOnly", autoLaunch: false, channel: "pipe");

        string? received = await readTask;
        received.Should().Be("echo PipeOnly");

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"dispatchedViaPipe\": true");
        jsonText.Should().Contain("\"dispatchedViaStdin\": false");
        jsonText.Should().Contain("\"channel\": \"pipe\"");
        ms.Length.Should().Be(0);
    }

    [Fact]
    public async Task HandleServerCommandAsync_WhenInvoked_ForwardChannelArgument()
    {
        GameServerManageHandler serverManageHandler = new();
        GameCommandHandler commandHandler = new(serverManageHandler: serverManageHandler);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManageHandler)!;

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        workerStdin[11235] = writer;

        JsonElement args = ParseJson("{\"command\": \"AddFakePlayer 1\", \"port\": 11235, \"channel\": \"stdin\"}");
        ToolCallResult result = await commandHandler.HandleServerCommandAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"dispatchedViaStdin\": true");
        text.Should().Contain("\"channel\": \"stdin\"");
    }
}
