// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpHighValueToolsTests
{
    [Fact]
    public async Task MatchStateHandler_QueriesLiveMatchState_ReturnsExpectedStructure()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                Connected = true,
                GamePhase = 3,
                GamePhaseName = "In-Game Combat (Phase 2)",
                CurrentMap = "caldavar"
            });
        MatchStateHandler handler = new(mockIpc.Object, null);
        JsonElement args = JsonElement.Parse(@"{ ""target"": ""both"", ""includePlayers"": true }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("client");
        root.GetProperty("gamePhase").GetString().Should().Contain("In-Game");
        root.GetProperty("hero").GetProperty("name").GetString().Should().Be("Hero_Pyromancer");
    }

    [Theory]
    [InlineData("spawn_unit", "Hero_Pyromancer")]
    [InlineData("give_gold", null)]
    [InlineData("level_up", null)]
    [InlineData("teleport", null)]
    [InlineData("cast_ability", null)]
    [InlineData("order", null)]
    [InlineData("execute", null)]
    public async Task GameActionHandler_ExecutesActions_ReturnsSuccess(string action, string? unit)
    {
        Mock<IGameIpcBridge> mockIpc = new();
        GameActionHandler handler = new(mockIpc.Object);

        string argsJson = unit != null
            ? $@"{{ ""action"": ""{action}"", ""unit"": ""{unit}"", ""team"": 1, ""x"": 100, ""y"": 200 }}"
            : $@"{{ ""action"": ""{action}"", ""amount"": 1000, ""slot"": 1, ""command"": ""echo test"" }}";

        JsonElement args = JsonElement.Parse(argsJson);
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("action").GetString().Should().Be(action);
    }

    [Fact]
    public async Task ReplayInspectHandler_ParsesHonreplayZip_ReturnsMetadata()
    {
        // Create temporary dummy .honreplay zip in temp directory
        string tempDir = Path.Combine(Path.GetTempPath(), $"replay_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string replayPath = Path.Combine(tempDir, "test_match.honreplay");

        try
        {
            using (ZipArchive archive = ZipFile.Open(replayPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry summaryEntry = archive.CreateEntry("summary.xml");
                using StreamWriter writer = new(summaryEntry.Open());
                writer.WriteLine("<match id='98765' map='caldavar' duration='1840' winner='1' version='5.0.0'>");
                writer.WriteLine("  <player id='1' name='DevPlayer' hero='Hero_Pyromancer' kills='12' deaths='2' assists='8' gold='14200' />");
                writer.WriteLine("</match>");
            }

            ReplayInspectHandler handler = new();
            JsonElement args = JsonElement.Parse($@"{{ ""path"": ""{replayPath.Replace('\\', '/')}"", ""summaryOnly"": false }}");

            ToolCallResult result = await handler.HandleAsync(args);

            result.IsError.Should().BeFalse();
            string json = (result.Content[0] as TextContentItem)!.Text;
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("matchId").GetString().Should().Be("98765");
            root.GetProperty("mapName").GetString().Should().Be("caldavar");
            root.GetProperty("winner").GetString().Should().Be("1");
            root.GetProperty("players").GetArrayLength().Should().Be(1);
            root.GetProperty("players")[0].GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ReplayInspectHandler_FileNotFound_ReturnsError()
    {
        ReplayInspectHandler handler = new();
        JsonElement args = JsonElement.Parse(@"{ ""path"": ""non_existent_replay_12345.honreplay"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("Replay file not found");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("stop")]
    [InlineData("seek")]
    [InlineData("speed")]
    public async Task ReplayPlaybackHandler_Commands_DispatchesCorrectly(string action)
    {
        Mock<IGameIpcBridge> mockIpc = new();
        ReplayPlaybackHandler handler = new(mockIpc.Object);

        JsonElement args = JsonElement.Parse($@"{{ ""action"": ""{action}"", ""replayFile"": ""match.honreplay"", ""time"": 120.0, ""speed"": 2.0 }}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("action").GetString().Should().Be(action);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("query")]
    [InlineData("status")]
    public async Task DbFixtureHandler_Operations_WithoutConnectionString_ReturnsActionableError(string action)
    {
        DbFixtureHandler handler = new();
        JsonElement args = JsonElement.Parse($@"{{ ""action"": ""{action}"", ""fixture"": ""accounts"", ""count"": 5 }}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = (result.Content[0] as TextContentItem)!.Text;
        error.Should().Contain("connection string");
    }

    [Fact]
    public void DbFixtureHandler_GetDefaultRoster_ContainsCanonicalGuestAndRoles()
    {
        DbFixtureHandler.ResetToDefaults();
        var roster = DbFixtureHandler.GetDefaultRoster();
        roster.Should().NotBeEmpty();

        var guest = DbFixtureHandler.FindAccount("GUEST-11");
        guest.Should().NotBeNull();
        guest!.Username.Should().Be("GUEST-11");
        guest.Password.Should().Be("KONGOR");
        guest.Role.Should().Be("Guest");
        guest.TypeId.Should().Be(100);

        var host = DbFixtureHandler.FindAccount("MCPHost");
        host.Should().NotBeNull();
        host!.Username.Should().Be("MCPHost");
        host.AccountType.Should().Be("ServerHost");
        host.TypeId.Should().Be(2);

        var staff = DbFixtureHandler.FindAccount("MCPStaff");
        staff.Should().NotBeNull();
        staff!.Username.Should().Be("MCPStaff");
        staff.AccountType.Should().Be("Staff");
        staff.TypeId.Should().Be(5);
    }

    [Fact]
    public async Task MatchStateHandler_WithBoundAccount_ReturnsAccountDetails()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { Connected = true, GamePhase = 3, CurrentMap = "caldavar" });
        MatchStateHandler handler = new(mockIpc.Object, null);
        JsonElement args = JsonElement.Parse(@"{ ""account"": ""GUEST-22"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse((result.Content[0] as TextContentItem)!.Text);
        var player = doc.RootElement.GetProperty("player");
        player.GetProperty("username").GetString().Should().Be("GUEST-22");
        player.GetProperty("mmr").GetInt32().Should().Be(1650);
        player.GetProperty("level").GetInt32().Should().Be(22);
    }

    [Fact]
    public async Task GameActionHandler_GiveGold_MutatesBoundAccount()
    {
        GameActionHandler handler = new();
        DbFixtureHandler.ResetToDefaults();

        JsonElement args = JsonElement.Parse(@"{ ""action"": ""give_gold"", ""account"": ""GUEST-11"", ""amount"": 5000 }");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse((result.Content[0] as TextContentItem)!.Text);
        var acc = doc.RootElement.GetProperty("account");
        acc.GetProperty("username").GetString().Should().Be("GUEST-11");
        acc.GetProperty("goldCoins").GetInt32().Should().Be(16000); // 11,000 + 5,000
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task VfsResolveHandler_ResolvesVirtualPath_ReturnsMountInfo()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        VfsResolveHandler handler = new();
        JsonElement args = JsonElement.Parse(@"{ ""virtualPath"": ""/ui/main.interface"", ""checkMounts"": true }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("virtualPath").GetString().Should().Contain("ui/main.interface");
        root.GetProperty("mountHierarchy").GetArrayLength().Should().BeGreaterThan(0);
    }

    // protects: K2 VFS mount precedence (override -> KONGOR/* -> ../shared -> ClientPackageFiles -> game/resources99..0.s2z -> game/textures.s2z -> base) and Step 12 shadow-collision safety gate
    // fails_when: ClientPackageFiles is omitted (MCP-112), resources*.s2z are evaluated in ascending instead of descending numeric order, or intermediate layer shadow collisions are missed
    // seam: Real ZipArchive (.s2z) Central Directory binary headers and physical directory tree in VfsResolveHandler
    [Fact]
    public async Task VfsResolveHandler_ResolvesClientPackageFilesAndDescendingResourcesArchives_AndAuditsShadows()
    {
        string tempClientDir = Path.Combine(Path.GetTempPath(), $"kongor_vfs_test_{Guid.NewGuid():N}");
        string overrideDir = Path.Combine(tempClientDir, "override");
        string cpfDir = Path.Combine(tempClientDir, "ClientPackageFiles");
        string gameDir = Path.Combine(tempClientDir, "game");

        Directory.CreateDirectory(Path.Combine(overrideDir, "ui", "scripts"));
        Directory.CreateDirectory(Path.Combine(overrideDir, "heroes", "frieren"));
        Directory.CreateDirectory(Path.Combine(overrideDir, "heroes", "pyromancer"));
        Directory.CreateDirectory(Path.Combine(cpfDir, "ui", "scripts"));
        Directory.CreateDirectory(gameDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(overrideDir, "ui", "scripts", "store2.lua"), "-- override store2");
            await File.WriteAllTextAsync(Path.Combine(cpfDir, "ui", "scripts", "store2.lua"), "-- cpf store2");
            await File.WriteAllTextAsync(Path.Combine(cpfDir, "ui", "scripts", "only_in_cpf.lua"), "-- cpf only");
            await File.WriteAllTextAsync(Path.Combine(overrideDir, "heroes", "frieren", "model.mdf"), "<model />");
            await File.WriteAllTextAsync(Path.Combine(overrideDir, "heroes", "pyromancer", "hero.entity"), "<hero version='override' />");

            static void WriteS2z(string s2zPath, params (string EntryPath, string Content)[] entries)
            {
                using var fs = new FileStream(s2zPath, FileMode.Create, FileAccess.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var (entryPath, content) in entries)
                {
                    var entry = zip.CreateEntry(entryPath);
                    using var sw = new StreamWriter(entry.Open());
                    sw.Write(content);
                }
            }

            WriteS2z(
                Path.Combine(gameDir, "resources0.s2z"),
                ("ui/scripts/store2.lua", "-- res0 store2"),
                ("heroes/pyromancer/hero.entity", "<hero version='res0' />"));

            WriteS2z(
                Path.Combine(gameDir, "resources99.s2z"),
                ("heroes/pyromancer/hero.entity", "<hero version='res99' />"));

            WriteS2z(
                Path.Combine(gameDir, "textures.s2z"),
                ("00000000/heroes/pyromancer/icon.dds", "DDS_MIP0_DATA"));

            var handler = new VfsResolveHandler(clientDirOverride: tempClientDir);

            // 1. Resolve file existing only in ClientPackageFiles (MCP-112 regression check)
            var cpfResult = await handler.HandleAsync(JsonElement.Parse("""{"virtualPath": "/ui/scripts/only_in_cpf.lua"}"""));
            cpfResult.IsError.Should().BeFalse();
            using var cpfDoc = JsonDocument.Parse(((TextContentItem)cpfResult.Content[0]).Text);
            cpfDoc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
            cpfDoc.RootElement.GetProperty("winningFile").GetProperty("layer").GetString().Should().Be("ClientPackageFiles");

            // 2. Resolve file shadowed across override -> game/resources99.s2z -> game/resources0.s2z
            var heroResult = await handler.HandleAsync(JsonElement.Parse("""{"virtualPath": "heroes/pyromancer/hero.entity"}"""));
            heroResult.IsError.Should().BeFalse();
            using var heroDoc = JsonDocument.Parse(((TextContentItem)heroResult.Content[0]).Text);
            heroDoc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
            heroDoc.RootElement.GetProperty("winningFile").GetProperty("layer").GetString().Should().Be("override");
            var shadowed = heroDoc.RootElement.GetProperty("shadowedCandidates");
            shadowed.GetArrayLength().Should().Be(2);
            shadowed[0].GetProperty("layer").GetString().Should().Be("game/resources99.s2z");
            shadowed[1].GetProperty("layer").GetString().Should().Be("game/resources0.s2z");

            // 3. Resolve .tga texture mapped to 00000000/*.dds inside game/textures.s2z
            var texResult = await handler.HandleAsync(JsonElement.Parse("""{"virtualPath": "/heroes/pyromancer/icon.tga"}"""));
            texResult.IsError.Should().BeFalse();
            using var texDoc = JsonDocument.Parse(((TextContentItem)texResult.Content[0]).Text);
            texDoc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
            texDoc.RootElement.GetProperty("winningFile").GetProperty("layer").GetString().Should().Be("game/textures.s2z");

            // 4. Audit shadows across override/
            var auditResult = await handler.HandleAsync(JsonElement.Parse("""{"action": "audit_shadows"}"""));
            auditResult.IsError.Should().BeFalse();
            using var auditDoc = JsonDocument.Parse(((TextContentItem)auditResult.Content[0]).Text);
            var summary = auditDoc.RootElement.GetProperty("summary");
            summary.GetProperty("safeNewNetAssetsCount").GetInt32().Should().Be(1);
            summary.GetProperty("safeArchiveUpdateCount").GetInt32().Should().Be(1);
            summary.GetProperty("intermediateLayerCollisionRiskCount").GetInt32().Should().Be(1);

            var risks = auditDoc.RootElement.GetProperty("collisionRisks");
            risks[0].GetProperty("virtualPath").GetString().Should().Be("ui/scripts/store2.lua");
            risks[0].GetProperty("classification").GetString().Should().Be("CollisionRisk_IntermediateLayerShadowsGameS2z");

            var updates = auditDoc.RootElement.GetProperty("archiveUpdatesSample");
            updates[0].GetProperty("virtualPath").GetString().Should().Be("heroes/pyromancer/hero.entity");
            updates[0].GetProperty("highestShadowedArchive").GetString().Should().Be("game/resources99.s2z");
        }
        finally
        {
            if (Directory.Exists(tempClientDir))
            {
                Directory.Delete(tempClientDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ModBuildHandler_BuildsModArchive_ReturnsManifestChecksums()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"mod_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "mod.xml"), "<mod name='TestMod' version='1.0' />");

        string outArchive = Path.Combine(tempDir, "test.s2z");

        try
        {
            ModBuildHandler handler = new();
            JsonElement args = JsonElement.Parse($@"{{ ""action"": ""build"", ""sourceDirectory"": ""{srcDir.Replace('\\', '/')}"", ""outputFile"": ""{outArchive.Replace('\\', '/')}"", ""modType"": ""s2z"" }}");

            ToolCallResult result = await handler.HandleAsync(args);

            result.IsError.Should().BeFalse();
            string json = (result.Content[0] as TextContentItem)!.Text;
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("modType").GetString().Should().Be("s2z");
            File.Exists(outArchive).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData("inspect_audio")]
    [InlineData("verify_soundbank")]
    [InlineData("test_event")]
    public async Task AudioPreviewHandler_Actions_ReturnsValidTelemetry(string action)
    {
        AudioPreviewHandler handler = new();
        JsonElement args = JsonElement.Parse($@"{{ ""action"": ""{action}"", ""sound_path"": ""shared/sounds/ui/button_click.wav"", ""event_name"": ""ui/button_click"" }}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
    }

    [Theory]
    [InlineData("hon_x64.exe -dedicated -port 11235", 11235)]
    [InlineData("hon_x64.exe -dedicated +Set svr_port 11236", 11236)]
    [InlineData("hon_x64 -dedicated -execute \"Set svr_port 11335\" -pipe hon-server-native-11335", 11335)]
    [InlineData("hon_x64.exe -server -pipe hon-server-11239", 11239)]
    [InlineData("hon_x64.exe -dedicated port:11435", 11435)]
    public void GameServerManageHandler_ExtractPortFromCommandLine_SupportsAllDedicatedFormats(string cmdLine, int expectedPort)
    {
        int? resolved = GameServerManageHandler.ExtractPortFromCommandLine(cmdLine);
        resolved.Should().Be(expectedPort);
    }

    [Fact]
    public async Task GameServerManageHandler_ExecuteCommandOnPort_RoutesToNativePipe_WithFramedProtocol()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int testPort = 19876;
        string pipeName = $"hon-server-native-{testPort}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task<string> serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            byte[] header = new byte[12];
            int read = 0;
            while (read < 12)
            {
                int n = await server.ReadAsync(header.AsMemory(read, 12 - read), cts.Token);
                if (n <= 0) break;
                read += n;
            }

            ushort cmdType = BitConverter.ToUInt16(header, 2);
            int payloadLen = BitConverter.ToInt32(header, 4);
            uint reqId = BitConverter.ToUInt32(header, 8);
            byte[] payload = new byte[payloadLen];
            int pRead = 0;
            while (pRead < payloadLen)
            {
                int n = await server.ReadAsync(payload.AsMemory(pRead, payloadLen - pRead), cts.Token);
                if (n <= 0) break;
                pRead += n;
            }

            string receivedCmd = Encoding.UTF8.GetString(payload);
            byte[] replyPayload = Encoding.UTF8.GetBytes($"OK:{receivedCmd}");
            byte[] replyFrame = new byte[12 + replyPayload.Length];
            replyFrame[0] = 0x4D;
            replyFrame[1] = 0x57;
            BitConverter.TryWriteBytes(replyFrame.AsSpan(2, 2), (ushort)0x0004);
            BitConverter.TryWriteBytes(replyFrame.AsSpan(4, 4), replyPayload.Length);
            BitConverter.TryWriteBytes(replyFrame.AsSpan(8, 4), reqId);
            Buffer.BlockCopy(replyPayload, 0, replyFrame, 12, replyPayload.Length);

            await server.WriteAsync(replyFrame, cts.Token);
            await server.FlushAsync(cts.Token);
            return $"{cmdType}:{receivedCmd}";
        }, cts.Token);

        using var handler = new GameServerManageHandler();
        ToolCallResult result = await handler.ExecuteCommandOnPortAsync(testPort, "ServerStatus", cancellationToken: cts.Token);

        string received = await serverTask;
        received.Should().Be("3:ServerStatus");

        result.IsError.Should().BeFalse();
        string json = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("channel").GetString().Should().Be("native-pipe");
        doc.RootElement.GetProperty("ipcResponse").GetString().Should().Be("OK:ServerStatus");
    }

    [Fact]
    public async Task GameServerManageHandler_ExecuteCommandOnPort_RoutesToCompelDockerControlPlaneHttp()
    {
        string? capturedCommand = null;
        string? capturedUrl = null;

        var fakeHttpHandler = new DelegatingStubHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsoluteUri == "http://127.0.0.1:11334/api/servers")
            {
                string serversJson = """
                [
                  {
                    "instanceIndex": 1,
                    "port": 11335,
                    "voicePort": 11435,
                    "publicGamePort": 21335,
                    "processId": 42,
                    "isRunning": true,
                    "workingSetBytes": 536870912,
                    "workingSetMb": 512.0,
                    "uptimeSeconds": 120.5,
                    "crashCount": 0,
                    "currentState": "Ready",
                    "currentTickrateHz": 60.0,
                    "tickVarianceMs": 0.2
                  }
                ]
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(serversJson, Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsoluteUri == "http://127.0.0.1:11334/api/instances/11335/command")
            {
                capturedUrl = req.RequestUri.AbsoluteUri;
                using var reader = new StreamReader(req.Content!.ReadAsStream(), Encoding.UTF8);
                capturedCommand = reader.ReadToEnd();
                string respJson = """{"port":11335,"command":"ServerStatus","success":true,"channel":"stdin","message":"Dispatched via COMPEL worker stdin"}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(respJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(fakeHttpHandler);
        using var handler = new GameServerManageHandler(httpClient: httpClient);

        var discovered = handler.DiscoverRunningDedicatedServers();
        discovered.Should().ContainSingle(w => w.Port == 11335 && w.ProcessId == 42);

        ToolCallResult cmdResult = await handler.ExecuteCommandOnPortAsync(11335, "ServerStatus");
        cmdResult.IsError.Should().BeFalse();

        capturedUrl.Should().Be("http://127.0.0.1:11334/api/instances/11335/command");
        capturedCommand.Should().Contain("ServerStatus");

        string json = (cmdResult.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("channel").GetString().Should().Be("compel-docker-http:stdin");
        doc.RootElement.GetProperty("dispatchedViaCompel").GetBoolean().Should().BeTrue();
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
