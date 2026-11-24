// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.IO.Compression;
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

/// <summary>
/// Comprehensive fast automated unit test suite (< 2.0s) validating:
/// - Replay & Spectator Verification synergy (Roadmap Item #6).
/// - kongor_replay_inspect: archive parsing, replays discovery, and active dedicated server port resolution.
/// - kongor_replay_playback: live match spectating, demo recording on dedicated server stdin, camera hero tracking, fog of war, and multi-target dispatch (client, server, both).
/// - kongor_replay_diff: live client/server desync audit and tick delta detection under port routing rules.
/// - McpToolRegistry registration and schema validation for all three replay synergy tools.
/// </summary>
public sealed class ReplayAndSpectatorSynergyTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.IsError.Should().BeFalse(result.FirstText);
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem tc ? tc.Text : result.FirstText;
        return ParseJson(text);
    }

    private sealed class ReplaySynergyTestContext : IDisposable
    {
        public ReplayInspectHandler InspectHandler { get; init; } = null!;
        public ReplayPlaybackHandler PlaybackHandler { get; init; } = null!;
        public ReplayDiffHandler DiffHandler { get; init; } = null!;
        public GameServerManageHandler ServerManage { get; init; } = null!;
        public ServerPortRegistry PortRegistry { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public MemoryStream ServerStdinStream { get; init; } = null!;
        public StreamWriter ServerStdinWriter { get; init; } = null!;
        public List<string> ClientCommands { get; init; } = [];
        public int Port { get; init; }

        public void Dispose()
        {
            ServerStdinWriter.Dispose();
            ServerStdinStream.Dispose();
        }

        public async Task<string> ReadServerStdinContentAsync()
        {
            await ServerStdinWriter.FlushAsync().ConfigureAwait(false);
            ServerStdinStream.Position = 0;
            using StreamReader reader = new(ServerStdinStream, Encoding.UTF8, leaveOpen: true);
            string content = await reader.ReadToEndAsync().ConfigureAwait(false);
            ServerStdinStream.Position = ServerStdinStream.Length;
            return content;
        }
    }

    private ReplaySynergyTestContext CreateContext(int port = 11235, bool registerWorker = true)
    {
        string tempRegPath = Path.Combine(Path.GetTempPath(), $"reg_replay_synergy_{Guid.NewGuid():N}.json");
        _tempFiles.Add(tempRegPath);
        var reg = new ServerPortRegistry(tempRegPath);

        var serverManage = new GameServerManageHandler(portRegistry: reg);

        MemoryStream ms = new();
        StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;
        workerStdin[port] = writer;

        if (registerWorker)
        {
            reg.RegisterWorker(Environment.ProcessId, port, $"Project KONGOR Test Worker {port}", "caldavar", matchId: 99887L);

            FieldInfo? workersField = typeof(GameServerManageHandler).GetField("_trackedWorkers", BindingFlags.NonPublic | BindingFlags.Instance);
            var trackedWorkers = (ConcurrentDictionary<int, GameServerWorkerInfo>)workersField!.GetValue(serverManage)!;
            trackedWorkers[Environment.ProcessId] = new GameServerWorkerInfo
            {
                ProcessId = Environment.ProcessId,
                Port = port,
                ServerName = $"Project KONGOR Test Worker {port}",
                Map = "caldavar",
                Responding = true
            };
        }

        List<string> clientCommands = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCommands.Add(cmd);
                return Task.FromResult("[TAO_RESPONSE]{\"status\":\"ok\"}[/TAO_RESPONSE]");
            });

        var inspectHandler = new ReplayInspectHandler(
            serverManageHandler: serverManage,
            portRegistry: reg,
            gameIpcBridge: mockIpc.Object);

        var playbackHandler = new ReplayPlaybackHandler(
            gameIpcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg,
            clientManageHandler: null);

        var diffHandler = new ReplayDiffHandler(
            portRegistry: reg,
            serverManageHandler: serverManage,
            gameIpcBridge: mockIpc.Object);

        return new ReplaySynergyTestContext
        {
            InspectHandler = inspectHandler,
            PlaybackHandler = playbackHandler,
            DiffHandler = diffHandler,
            ServerManage = serverManage,
            PortRegistry = reg,
            MockClientIpc = mockIpc,
            ServerStdinStream = ms,
            ServerStdinWriter = writer,
            ClientCommands = clientCommands,
            Port = port
        };
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(file)) Directory.Delete(file, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task ReplayInspect_ResolvesPortAndActiveWorker_WhenPortOmitted()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("{\"allowSimulation\": true}");
        var result = await ctx.InspectHandler.HandleAsync(argsDoc.RootElement);

        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11235);
        root.GetProperty("mapName").GetString().Should().Be("caldavar");
        root.GetProperty("matchId").GetString().Should().Be("99887");
        root.GetProperty("players").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReplayInspect_ListAction_EnumeratesAvailableReplays()
    {
        using var ctx = CreateContext();

        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_replays_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempFiles.Add(tempDir);

        string sampleReplay = Path.Combine(tempDir, "M99887_caldavar.honreplay");
        await File.WriteAllTextAsync(sampleReplay, "dummy replay content");

        using var argsDoc = JsonDocument.Parse($$"""
        {
            "action": "list",
            "path": {{JsonSerializer.Serialize(tempDir)}}
        }
        """);

        var result = await ctx.InspectHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("list");
        root.GetProperty("totalFound").GetInt32().Should().BeGreaterOrEqualTo(1);

        var replays = root.GetProperty("replays");
        replays.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        replays[0].GetProperty("fileName").GetString().Should().Be("M99887_caldavar.honreplay");
    }

    [Fact]
    public async Task ReplayInspect_ParsesArchive_WhenValidZipReplayProvided()
    {
        using var ctx = CreateContext();

        string tempZip = Path.Combine(Path.GetTempPath(), $"match_parse_test_{Guid.NewGuid():N}.honreplay");
        _tempFiles.Add(tempZip);

        string summaryXml = """
        <match id="771122" gamemode="AP" map="caldavar" winner="Legion">
            <player name="PyroGod" hero="Hero_Pyromancer" kills="15" deaths="1" assists="9" />
            <player name="DevoHook" hero="Hero_Devourer" kills="2" deaths="11" assists="4" />
        </match>
        """;

        using (FileStream zipStream = new(tempZip, FileMode.Create))
        using (ZipArchive zipArchive = new(zipStream, ZipArchiveMode.Create))
        {
            var entry = zipArchive.CreateEntry("summary.xml");
            using StreamWriter entryWriter = new(entry.Open());
            await entryWriter.WriteAsync(summaryXml);
        }

        using var argsDoc = JsonDocument.Parse($$"""
        {
            "action": "inspect",
            "path": {{JsonSerializer.Serialize(tempZip)}}
        }
        """);

        var result = await ctx.InspectHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("matchId").GetString().Should().Be("771122");
        root.GetProperty("winner").GetString().Should().Be("Legion");
        root.GetProperty("playerCount").GetInt32().Should().Be(2);

        var players = root.GetProperty("players");
        players[0].GetProperty("name").GetString().Should().Be("PyroGod");
        players[0].GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        players[0].GetProperty("kills").GetInt32().Should().Be(15);
    }

    [Fact]
    public async Task ReplayPlayback_Spectate_ResolvesPortAndDispatchesConnectToClient()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "spectate"
        }
        """);

        var result = await ctx.PlaybackHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("spectate");
        root.GetProperty("command").GetString().Should().Be("Connect 127.0.0.1:11235 spectator:true");
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();

        ctx.ClientCommands.Should().Contain("Connect 127.0.0.1:11235 spectator:true");
    }

    [Fact]
    public async Task ReplayPlayback_RecordDemo_TargetServer_PipesToDedicatedServerStdin()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "record_demo",
            "demoName": "scrimmage_replay_01",
            "target": "server",
            "port": 11235
        }
        """);

        var result = await ctx.PlaybackHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("command").GetString().Should().Be("RecordDemo scrimmage_replay_01");
        root.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("RecordDemo scrimmage_replay_01");
    }

    [Fact]
    public async Task ReplayPlayback_RecordDemo_TargetBoth_DispatchesToBothServerAndClient()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "record_demo",
            "demoName": "dual_capture_demo",
            "target": "both"
        }
        """);

        var result = await ctx.PlaybackHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();
        root.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("RecordDemo dual_capture_demo");

        ctx.ClientCommands.Should().Contain("RecordDemo dual_capture_demo");
    }

    [Fact]
    public async Task ReplayPlayback_StopDemo_PipesStopToTarget()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "stop_demo",
            "target": "both"
        }
        """);

        var result = await ctx.PlaybackHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("command").GetString().Should().Be("StopDemo");
        root.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();
        root.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("StopDemo");

        ctx.ClientCommands.Should().Contain("StopDemo");
    }

    [Fact]
    public async Task ReplayPlayback_CameraTracking_BindsCameraTargetToClient()
    {
        using var ctx = CreateContext();

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "camera",
            "hero": "Hero_Pyromancer"
        }
        """);

        var result = await ctx.PlaybackHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("command").GetString().Should().Be("SetCamTarget Hero_Pyromancer");
        root.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();

        ctx.ClientCommands.Should().Contain("SetCamTarget Hero_Pyromancer");
    }

    [Fact]
    public async Task ReplayPlayback_SpeedAndSeek_DispatchesPlaybackControls()
    {
        using var ctx = CreateContext();

        // Speed test
        using (var speedDoc = JsonDocument.Parse("""
        {
            "action": "speed",
            "speed": 2.5
        }
        """))
        {
            var speedResult = await ctx.PlaybackHandler.HandleAsync(speedDoc.RootElement);
            var root = ParseResult(speedResult);
            root.GetProperty("command").GetString().Should().Be("ReplaySetPlaybackSpeed 1");
            ctx.ClientCommands.Should().Contain("ReplaySetPlaybackSpeed 1");
        }

        // Seek test (time in seconds: 20 seconds = 400 ticks at 20 ticks/sec)
        using (var seekDoc = JsonDocument.Parse("""
        {
            "action": "seek",
            "time": 20.0
        }
        """))
        {
            var seekResult = await ctx.PlaybackHandler.HandleAsync(seekDoc.RootElement);
            var root = ParseResult(seekResult);
            root.GetProperty("command").GetString().Should().Be("ReplaySetFrame 400");
            ctx.ClientCommands.Should().Contain("ReplaySetFrame 400");
        }
    }

    [Fact]
    public async Task ReplayPlayback_FogToggle_DispatchesFogCvar()
    {
        using var ctx = CreateContext();

        // Fog disabled
        using (var fogOffDoc = JsonDocument.Parse("""
        {
            "action": "fog",
            "fogOfWar": false
        }
        """))
        {
            var res = await ctx.PlaybackHandler.HandleAsync(fogOffDoc.RootElement);
            var root = ParseResult(res);
            root.GetProperty("command").GetString().Should().Be("FogOfWar 0");
            ctx.ClientCommands.Should().Contain("FogOfWar 0");
        }

        // Fog enabled
        using (var fogOnDoc = JsonDocument.Parse("""
        {
            "action": "fog",
            "fogOfWar": true
        }
        """))
        {
            var res = await ctx.PlaybackHandler.HandleAsync(fogOnDoc.RootElement);
            var root = ParseResult(res);
            root.GetProperty("command").GetString().Should().Be("FogOfWar 1");
            ctx.ClientCommands.Should().Contain("FogOfWar 1");
        }
    }

    [Fact]
    public async Task ReplayDiff_LiveCompare_AuditsDesyncBetweenClientAndServerPort()
    {
        using var ctx = CreateContext(port: 11235);

        // Scenario 1: synchronized (< 5 tick difference)
        using (var syncDoc = JsonDocument.Parse("""
        {
            "action": "live_compare",
            "port": 11235,
            "clientTick": 1000,
            "serverTick": 1002,
            "allowSimulation": true
        }
        """))
        {
            var syncResult = await ctx.DiffHandler.HandleAsync(syncDoc.RootElement);
            var root = ParseResult(syncResult);

            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("resolvedPort").GetInt32().Should().Be(11235);
            root.GetProperty("tickDelta").GetInt64().Should().Be(2);
            root.GetProperty("desyncDetected").GetBoolean().Should().BeFalse();
            root.GetProperty("syncStatus").GetString().Should().Be("synchronized");
        }

        // Scenario 2: divergent desync (> 5 tick difference)
        using (var desyncDoc = JsonDocument.Parse("""
        {
            "action": "live_compare",
            "port": 11235,
            "clientTick": 1000,
            "serverTick": 1025,
            "allowSimulation": true
        }
        """))
        {
            var desyncResult = await ctx.DiffHandler.HandleAsync(desyncDoc.RootElement);
            var root = ParseResult(desyncResult);

            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("tickDelta").GetInt64().Should().Be(25);
            root.GetProperty("desyncDetected").GetBoolean().Should().BeTrue();
            root.GetProperty("syncStatus").GetString().Should().Be("divergent");
        }
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersReplayToolsWithSynergyParameters()
    {
        string tempRegPath = Path.Combine(Path.GetTempPath(), $"reg_registry_test_{Guid.NewGuid():N}.json");
        _tempFiles.Add(tempRegPath);

        var registry = new McpToolRegistry(registerDefaults: true);

        // 1. Inspect definition check
        var inspectDef = registry.GetTool("kongor_replay_inspect");
        inspectDef.Should().NotBeNull();
        inspectDef!.InputSchema.Properties.Should().ContainKey("port");
        inspectDef.InputSchema.Properties.Should().ContainKey("matchId");
        inspectDef.InputSchema.Properties["action"].Enum.Should().Contain("list");

        // 2. Playback definition check
        var playbackDef = registry.GetTool("kongor_replay_playback");
        playbackDef.Should().NotBeNull();
        playbackDef!.InputSchema.Properties.Should().ContainKey("port");
        playbackDef.InputSchema.Properties.Should().ContainKey("target");
        playbackDef.InputSchema.Properties.Should().ContainKey("demoName");
        playbackDef.InputSchema.Properties["action"].Enum.Should().Contain("spectate");
        playbackDef.InputSchema.Properties["action"].Enum.Should().Contain("record_demo");
        playbackDef.InputSchema.Properties["action"].Enum.Should().Contain("stop_demo");

        // 3. Diff definition check
        var diffDef = registry.GetTool("kongor_replay_diff");
        diffDef.Should().NotBeNull();
        diffDef!.InputSchema.Properties.Should().ContainKey("port");
        diffDef.InputSchema.Properties["action"].Enum.Should().Contain("live_compare");
    }
}
