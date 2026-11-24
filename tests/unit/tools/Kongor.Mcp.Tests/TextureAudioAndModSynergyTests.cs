// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Comprehensive fast automated unit test suite (< 2.0s) validating:
/// - Texture, Audio & Mod Pipeline Synergy (Roadmap Item #7).
/// - kongor_texture_preview: VFS layer resolution, channel isolation, live ReloadTextures IPC dispatch.
/// - kongor_texture_diff: Perceptual SSIM/MSE diffing and blank white placeholder anomaly detection.
/// - kongor_texture_in_game_inspect: Camera presets (front/side/back/top/portrait) and in-engine viewport capture.
/// - kongor_audio_preview: Canonical WAV PCM header extraction and multi-target PlaySound command routing (client, server, both).
/// - kongor_mod_build: s2z archive packaging, live client hot-reloading (ReloadTextures & ReloadInterfaces), and games/shared sync.
/// - kongor_material_inspect: VFS layer sampler resolution and dedicated server shared compatibility auditing.
/// - McpToolRegistry registration and schema validation for all six tools.
/// </summary>
public sealed class TextureAudioAndModSynergyTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly List<string> _tempDirs = [];

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

    private string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"kongor_tam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class AudioSynergyTestContext : IDisposable
    {
        public AudioPreviewHandler AudioHandler { get; init; } = null!;
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

    private AudioSynergyTestContext CreateAudioContext(int port = 11235, bool registerWorker = true)
    {
        string tempRegPath = Path.Combine(Path.GetTempPath(), $"reg_audio_synergy_{Guid.NewGuid():N}.json");
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

        List<string> clientCmds = [];
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => clientCmds.Add(cmd))
            .ReturnsAsync("OK");

        var audioHandler = new AudioPreviewHandler(
            gameIpcBridge: mockIpc.Object,
            serverManageHandler: serverManage,
            portRegistry: reg);

        return new AudioSynergyTestContext
        {
            AudioHandler = audioHandler,
            ServerManage = serverManage,
            PortRegistry = reg,
            MockClientIpc = mockIpc,
            ServerStdinStream = ms,
            ServerStdinWriter = writer,
            ClientCommands = clientCmds,
            Port = port
        };
    }

    public void Dispose()
    {
        foreach (string f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        foreach (string d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
    }

    [Fact]
    public async Task TexturePreview_ResolvesVfsLayers_IdentifiesWinningLayer()
    {
        string rootDir = CreateTempDirectory();
        string overrideDir = Path.Combine(rootDir, "games", "client", "override", "textures");
        string gameDir = Path.Combine(rootDir, "games", "client", "game", "textures");
        Directory.CreateDirectory(overrideDir);
        Directory.CreateDirectory(gameDir);

        // Put texture in both override and game to test layer priority (override > game)
        string overrideFile = Path.Combine(overrideDir, "test.tga");
        string gameFile = Path.Combine(gameDir, "test.tga");

        byte[] fakeTga = new byte[18 + 64];
        fakeTga[2] = 2;
        fakeTga[12] = 4; // width
        fakeTga[14] = 4; // height
        fakeTga[16] = 32;

        await File.WriteAllBytesAsync(overrideFile, fakeTga);
        await File.WriteAllBytesAsync(gameFile, fakeTga);

        TexturePreviewHandler handler = new(timeProvider: null, gameDir: rootDir);

        using var args = JsonDocument.Parse("{\"path\":\"textures/test.tga\"}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("status").GetString().Should().Be("success");
        json.GetProperty("vfsLayer").GetString().Should().Be("override");
        json.GetProperty("resolvedSource").GetString().Should().Contain("override");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task TexturePreview_ReloadTextures_DispatchesIpcCommandToClient()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        List<string> dispatchedCommands = [];
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => dispatchedCommands.Add(cmd))
            .ReturnsAsync("OK");

        TexturePreviewHandler handler = new(gameIpcBridge: mockIpc.Object, timeProvider: null);

        using var args = JsonDocument.Parse("{\"path\":\"heroes/pyromancer/color.tga\",\"reload_textures\":true}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("reloadedLive").GetBoolean().Should().BeTrue();
        dispatchedCommands.Should().Contain("ReloadTextures");
    }

    [Fact]
    public async Task TextureDiff_ComputesSsimAndMse_WithinTolerance()
    {
        string dir = CreateTempDirectory();
        string baseImgPath = Path.Combine(dir, "baseline.png");
        string candImgPath = Path.Combine(dir, "candidate.png");

        using (Image<Rgba32> img = new(32, 32))
        {
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    img[x, y] = new Rgba32((byte)(x * 8), (byte)(y * 8), 128, 255);
                }
            }
            await img.SaveAsPngAsync(baseImgPath);
            await img.SaveAsPngAsync(candImgPath);
        }

        TextureDiffHandler handler = new();

        using var args = JsonDocument.Parse($"{{\"baseline\":\"{baseImgPath.Replace('\\', '/')}\",\"candidate\":\"{candImgPath.Replace('\\', '/')}\",\"tolerance\":0.05}}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("ssim").GetDouble().Should().BeApproximately(1.0, 0.001);
        json.GetProperty("mse").GetDouble().Should().BeApproximately(0.0, 0.001);
        json.GetProperty("anomalyDetected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task TextureDiff_WhenInvoked_DetectsBlankWhitePlaceholderAnomaly()
    {
        string dir = CreateTempDirectory();
        string baseImgPath = Path.Combine(dir, "baseline.png");
        string whiteImgPath = Path.Combine(dir, "candidate_white.png");

        using (Image<Rgba32> baseImg = new(32, 32))
        {
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    baseImg[x, y] = new Rgba32(30, 60, 90, 255);
                }
            }
            await baseImg.SaveAsPngAsync(baseImgPath);
        }

        using (Image<Rgba32> whiteImg = new(32, 32))
        {
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    whiteImg[x, y] = new Rgba32(255, 255, 255, 255); // 100% pure white
                }
            }
            await whiteImg.SaveAsPngAsync(whiteImgPath);
        }

        TextureDiffHandler handler = new();

        using var args = JsonDocument.Parse($"{{\"baseline\":\"{baseImgPath.Replace('\\', '/')}\",\"candidate\":\"{whiteImgPath.Replace('\\', '/')}\",\"tolerance\":0.05}}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("anomalyDetected").GetBoolean().Should().BeTrue();
        json.GetProperty("anomalyType").GetString().Should().Be("BlankWhitePlaceholder");
    }

    [Fact]
    public async Task TextureInGameInspect_WhenInvoked_AppliesCameraPresetAndReloadsTextures()
    {
        List<string> dispatchedCommands = [];
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => dispatchedCommands.Add(cmd))
            .ReturnsAsync("OK");

        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(x => x.CaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth x64",
                Width = 1920,
                Height = 1080
            });

        TextureInGameInspectHandler handler = new(windowCapturer: mockCapturer.Object, gameIpcBridge: mockIpc.Object);

        using var args = JsonDocument.Parse("{\"hero\":\"Hero_Pyromancer\",\"camera_preset\":\"side\",\"reload_textures\":true}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("status").GetString().Should().Be("success");
        json.GetProperty("cameraPreset").GetString().Should().Be("side");
        json.GetProperty("reloadedLive").GetBoolean().Should().BeTrue();

        dispatchedCommands.Should().Contain("SetCamTarget Hero_Pyromancer");
        dispatchedCommands.Should().Contain("CameraSetDistance 500");
        dispatchedCommands.Should().Contain("ReloadTextures");
    }

    [Fact]
    public async Task AudioPreview_InspectsWavHeader_ExtractsPcmAttributes()
    {
        string dir = CreateTempDirectory();
        string wavFile = Path.Combine(dir, "test_pcm.wav");

        // Construct 44-byte standard PCM WAV header
        byte[] wavBytes = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wavBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wavBytes.AsSpan(4, 4), 36); // Size - 8
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wavBytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wavBytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(wavBytes.AsSpan(16, 4), 16); // Subchunk1Size
        BinaryPrimitives.WriteUInt16LittleEndian(wavBytes.AsSpan(20, 2), 1);  // AudioFormat: PCM (1)
        BinaryPrimitives.WriteUInt16LittleEndian(wavBytes.AsSpan(22, 2), 2);  // Channels: Stereo (2)
        BinaryPrimitives.WriteUInt32LittleEndian(wavBytes.AsSpan(24, 4), 44100); // SampleRate
        BinaryPrimitives.WriteUInt32LittleEndian(wavBytes.AsSpan(28, 4), 176400); // ByteRate
        BinaryPrimitives.WriteUInt16LittleEndian(wavBytes.AsSpan(32, 2), 4);  // BlockAlign
        BinaryPrimitives.WriteUInt16LittleEndian(wavBytes.AsSpan(34, 2), 16); // BitsPerSample
        Encoding.ASCII.GetBytes("data").CopyTo(wavBytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wavBytes.AsSpan(40, 4), 0);  // Subchunk2Size

        await File.WriteAllBytesAsync(wavFile, wavBytes);

        AudioPreviewHandler handler = new();

        using var args = JsonDocument.Parse($"{{\"soundPath\":\"{wavFile.Replace('\\', '/')}\",\"action\":\"info\"}}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("status").GetString().Should().Be("success");
        var info = json.GetProperty("audioInfo");
        info.GetProperty("channels").GetInt32().Should().Be(2);
        info.GetProperty("channelFormat").GetString().Should().Be("Stereo");
        info.GetProperty("sampleRateHz").GetInt32().Should().Be(44100);
        info.GetProperty("bitsPerSample").GetInt32().Should().Be(16);
        info.GetProperty("isValid").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AudioPreview_PlayAction_DispatchesPlaySoundToClientIpc()
    {
        using var ctx = CreateAudioContext();

        using var args = JsonDocument.Parse("{\"action\":\"play\",\"target\":\"client\",\"soundPath\":\"/shared/sounds/ui/button.wav\"}");
        var res = await ctx.AudioHandler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();
        json.GetProperty("serverDispatchedLive").GetBoolean().Should().BeFalse();
        ctx.ClientCommands.Should().Contain("PlaySound /shared/sounds/ui/button.wav");
    }

    [Fact]
    public async Task AudioPreview_PlayAction_TargetServer_PipesToDedicatedServerStdin()
    {
        using var ctx = CreateAudioContext(port: 11235);

        using var args = JsonDocument.Parse("{\"action\":\"play\",\"target\":\"server\",\"port\":11235,\"soundPath\":\"/shared/sounds/ui/button.wav\"}");
        var res = await ctx.AudioHandler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();
        json.GetProperty("clientDispatchedLive").GetBoolean().Should().BeFalse();
        json.GetProperty("resolvedPort").GetInt32().Should().Be(11235);

        string stdinContent = await ctx.ReadServerStdinContentAsync();
        stdinContent.Should().Contain("PlaySound /shared/sounds/ui/button.wav");
    }

    [Fact]
    public async Task AudioPreview_PlayAction_TargetBoth_DispatchesToBothEndpoints()
    {
        using var ctx = CreateAudioContext(port: 11235);

        using var args = JsonDocument.Parse("{\"action\":\"play\",\"target\":\"both\",\"port\":11235,\"soundPath\":\"/shared/sounds/ui/ping.wav\"}");
        var res = await ctx.AudioHandler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();
        json.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();

        ctx.ClientCommands.Should().Contain("PlaySound /shared/sounds/ui/ping.wav");
        string stdinContent = await ctx.ReadServerStdinContentAsync();
        stdinContent.Should().Contain("PlaySound /shared/sounds/ui/ping.wav");
    }

    [Fact]
    public async Task ModBuild_BuildS2z_WithHotReload_DispatchesReloadTexturesAndInterfaces()
    {
        string rootDir = CreateTempDirectory();
        string modSrcDir = Path.Combine(rootDir, "mods", "my_mod");
        Directory.CreateDirectory(modSrcDir);
        await File.WriteAllTextAsync(Path.Combine(modSrcDir, "mod.manifest"), "<mod name='MyMod'/>");

        string outS2z = Path.Combine(rootDir, "games", "client", "override", "my_mod.s2z");

        List<string> dispatchedCommands = [];
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => dispatchedCommands.Add(cmd))
            .ReturnsAsync("OK");

        ModBuildHandler handler = new(gameIpcBridge: mockIpc.Object, timeProvider: null, gameDir: rootDir);

        using var args = JsonDocument.Parse($"{{\"action\":\"build_s2z\",\"sourceDirectory\":\"{modSrcDir.Replace('\\', '/')}\",\"outputFile\":\"{outS2z.Replace('\\', '/')}\",\"hotReload\":true,\"syncShared\":true}}");
        var res = await handler.HandleAsync(args.RootElement);

        res.IsError.Should().BeFalse();
        var json = ParseResult(res);
        json.GetProperty("status").GetString().Should().Be("success");
        json.GetProperty("reloadedLive").GetBoolean().Should().BeTrue();
        json.GetProperty("sharedSynced").GetBoolean().Should().BeTrue();

        dispatchedCommands.Should().Contain("ReloadTextures");
        dispatchedCommands.Should().Contain("ReloadInterfaces");

        // Verify file copied to games/shared
        string sharedFile = Path.Combine(rootDir, "games", "shared", "my_mod.s2z");
        File.Exists(sharedFile).Should().BeTrue();
    }

    [Fact]
    public async Task MaterialInspect_AuditsClientAndSharedLayers_ReportsServerCompatibility()
    {
        string rootDir = CreateTempDirectory();
        string sharedDir = Path.Combine(rootDir, "games", "shared", "heroes", "pyro");
        string overrideDir = Path.Combine(rootDir, "games", "client", "override", "heroes", "pyro");
        Directory.CreateDirectory(sharedDir);
        Directory.CreateDirectory(overrideDir);

        // Texture in shared
        await File.WriteAllTextAsync(Path.Combine(sharedDir, "shared_tex.tga"), "dummy");
        // Texture only in override
        await File.WriteAllTextAsync(Path.Combine(overrideDir, "client_only_tex.tga"), "dummy");

        // 1. Material referencing shared texture -> ServerCompatible: true
        string matSharedXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <material shader="models_hero" phase="color">
                <sampler name="diffuse" texture="heroes/pyro/shared_tex.tga" />
                <sampler name="normalmap" texture="$flat" />
            </material>
            """;
        string matSharedPath = Path.Combine(sharedDir, "shared.material");
        await File.WriteAllTextAsync(matSharedPath, matSharedXml);

        // 2. Material referencing client-only texture -> ServerCompatible: false
        string matClientXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <material shader="models_hero" phase="color">
                <sampler name="diffuse" texture="heroes/pyro/client_only_tex.tga" />
            </material>
            """;
        string matClientPath = Path.Combine(overrideDir, "client.material");
        await File.WriteAllTextAsync(matClientPath, matClientXml);

        MaterialInspectHandler handler = new(timeProvider: null, gameDir: rootDir);

        // Inspect shared material
        using var argsShared = JsonDocument.Parse($"{{\"path\":\"{matSharedPath.Replace('\\', '/')}\"}}");
        var resShared = await handler.HandleAsync(argsShared.RootElement);
        resShared.IsError.Should().BeFalse();
        var jsonShared = ParseResult(resShared);
        jsonShared.GetProperty("serverCompatible").GetBoolean().Should().BeTrue();
        jsonShared.TryGetProperty("serverWarning", out _).Should().BeFalse();

        // Inspect client-only material
        using var argsClient = JsonDocument.Parse($"{{\"path\":\"{matClientPath.Replace('\\', '/')}\"}}");
        var resClient = await handler.HandleAsync(argsClient.RootElement);
        resClient.IsError.Should().BeFalse();
        var jsonClient = ParseResult(resClient);
        jsonClient.GetProperty("serverCompatible").GetBoolean().Should().BeFalse();
        jsonClient.GetProperty("serverWarning").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersAllSixTextureAudioAndModTools()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        string[] expectedTools =
        [
            "kongor_texture_preview",
            "kongor_texture_diff",
            "kongor_texture_in_game_inspect",
            "kongor_audio_preview",
            "kongor_mod_build",
            "kongor_material_inspect"
        ];

        foreach (string tool in expectedTools)
        {
            registry.GetTool(tool).Should().NotBeNull($"Tool '{tool}' must be registered");
        }

        registry.TexturePreviewHandler.Should().NotBeNull();
        registry.TextureDiffHandler.Should().NotBeNull();
        registry.TextureInGameInspectHandler.Should().NotBeNull();
        registry.AudioPreviewHandler.Should().NotBeNull();
        registry.ModBuildHandler.Should().NotBeNull();
        registry.MaterialInspectHandler.Should().NotBeNull();

        // Check schema extensions
        var prevSchema = registry.GetTool("kongor_texture_preview")!.InputSchema.Properties;
        prevSchema.Should().ContainKey("reload_textures");

        var diffSchema = registry.GetTool("kongor_texture_diff")!.InputSchema.Properties;
        diffSchema.Should().ContainKey("tolerance");

        var inGameSchema = registry.GetTool("kongor_texture_in_game_inspect")!.InputSchema.Properties;
        inGameSchema.Should().ContainKey("camera_preset");

        var audioSchema = registry.GetTool("kongor_audio_preview")!.InputSchema.Properties;
        audioSchema.Should().ContainKey("target");
        audioSchema.Should().ContainKey("port");

        var modSchema = registry.GetTool("kongor_mod_build")!.InputSchema.Properties;
        modSchema.Should().ContainKey("hotReload");
        modSchema.Should().ContainKey("syncShared");
    }
}
