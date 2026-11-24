// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CommandLineParserTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.IO.Compression;
using System.Text;
using ReplayRenderer.Service.Configuration;
using ReplayRenderer.Service.Models;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class CommandLineParserTests : IDisposable
{
    private const string SampleReplayInfoXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<replayinfo version=""4.10.1.0"" gamemode=""Mid Wars"" matchid=""2060568725"" matchlength=""1411000"">
  <player name=""ClickBotAlpha"" accountid=""1077"" team=""1"" teamindex=""0"" clientnum=""0"" heroname=""Hero_BabaYaga"" heroicon=""/heroes/babayaga/icon.tga""/>
  <player name=""guccidonut"" accountid=""58"" team=""2"" teamindex=""0"" clientnum=""1"" heroname=""Hero_Bubbles"" heroicon=""/heroes/bubbles/icon.tga""/>
  <player name=""ValkyrieAce"" accountid=""999"" team=""1"" teamindex=""1"" clientnum=""4"" heroname=""Hero_Valkyrie"" heroicon=""/heroes/valkyrie/icon.tga""/>
</replayinfo>";

    private readonly string _mockGameDir;
    private readonly string _mockLayerDir;
    private readonly string _mockReplayPath;
    private readonly ITestOutputHelper _output;
    private readonly string _tempDirectory;

    public CommandLineParserTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "CmdParserTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _mockReplayPath = Path.Combine(_tempDirectory, "test_match.honreplay");
        CreateMockReplayFile(_mockReplayPath, SampleReplayInfoXml);

        _mockGameDir = Path.Combine(_tempDirectory, "games", "client");
        Directory.CreateDirectory(_mockGameDir);
        File.WriteAllText(Path.Combine(_mockGameDir, "hon_x64.exe"), "MOCK_EXE");

        _mockLayerDir = Path.Combine(_tempDirectory, "native", "vulkan-layer", "build", "Release");
        Directory.CreateDirectory(_mockLayerDir);
        File.WriteAllText(Path.Combine(_mockLayerDir, "VkLayer_hon_replay_capture.dll"), "MOCK_DLL");
        File.WriteAllText(Path.Combine(_mockLayerDir, "VkLayer_hon_replay_capture.json"), "{}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
        }
        catch
        {
            // Ignore cleanup exceptions
        }
    }

    private static void CreateMockReplayFile(string path, string xmlContent)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using ZipArchive archive = new(fs, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("replayinfo");
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(xmlContent);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Parse_DefaultPlayer_DefaultsToSlotZero()
    {
        _output.WriteLine("[Test] Testing default player slot resolution (defaults to 0)...");

        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "out.mp4"),
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(0, config.PovClientIndex);
        _output.WriteLine($"[Test] Resolved PovClientIndex: {config.PovClientIndex}");
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("7", 7)]
    public void Parse_PlayerWithIntegerSlot_SetsPovClientIndexCorrectly(string slotStr, int expectedSlot)
    {
        _output.WriteLine($"[Test] Testing --player with integer slot '{slotStr}'...");

        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "out.mp4"),
            "--player", slotStr,
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(expectedSlot, config.PovClientIndex);
    }

    [Theory]
    // Player names
    [InlineData("ClickBotAlpha", 0)]
    [InlineData("clickbotalpha", 0)]
    [InlineData("guccidonut", 1)]
    [InlineData("GucciDonut", 1)]
    [InlineData("ValkyrieAce", 4)]
    [InlineData("valkyrieace", 4)]
    // Hero names (prefixed and clean)
    [InlineData("Hero_BabaYaga", 0)]
    [InlineData("BabaYaga", 0)]
    [InlineData("babayaga", 0)]
    [InlineData("Hero_Bubbles", 1)]
    [InlineData("Bubbles", 1)]
    [InlineData("Hero_Valkyrie", 4)]
    [InlineData("Valkyrie", 4)]
    // Team slot aliases
    [InlineData("L1", 0)]
    [InlineData("L2", 4)] // ValkyrieAce is team 1 index 1 (slot 4) in sample XML
    [InlineData("H1", 1)] // guccidonut is team 2 index 0 (slot 1) in sample XML
    [InlineData("legion:0", 0)]
    public void Parse_PlayerWithNameOrHeroOrTeamSlot_ResolvesClientNumFromReplayMetadata(string playerIdentifier,
        int expectedSlot)
    {
        _output.WriteLine($"[Test] Testing --player with identifier '{playerIdentifier}' resolved from replay file...");

        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "out.mp4"),
            "-p", playerIdentifier,
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(expectedSlot, config.PovClientIndex);
    }

    [Theory]
    [InlineData("1920x1080", 1920u, 1080u)]
    [InlineData("2560x1440", 2560u, 1440u)]
    [InlineData("3840x2160", 3840u, 2160u)]
    [InlineData("1280x720", 1280u, 720u)]
    [InlineData("1920X1080", 1920u, 1080u)]
    public void Parse_ResolutionFlag_SetsWidthAndHeightCorrectly(string resolutionStr, uint expectedW, uint expectedH)
    {
        _output.WriteLine($"[Test] Testing --resolution flag: '{resolutionStr}'...");

        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "out.mp4"),
            "--resolution", resolutionStr,
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(expectedW, config.RenderWidth);
        Assert.Equal(expectedH, config.RenderHeight);
    }

    [Fact]
    public void Parse_StartAndDurationFlags_SetsStartSecondsAndMaxDuration()
    {
        _output.WriteLine("[Test] Testing --start and --duration flags...");

        string[] args =
        [
            "-r", _mockReplayPath,
            "-o", Path.Combine(_tempDirectory, "out.mp4"),
            "--start", "45",
            "-d", "30",
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(45, config.StartSeconds);
        Assert.NotNull(config.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), config.MaxDuration.Value);
    }

    [Fact]
    public void Parse_AutoDetectPaths_DiscoversGameDirAndLayerDirWhenOmitted()
    {
        _output.WriteLine("[Test] Testing auto-discovery of game-dir and layer-dir...");

        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "out.mp4")
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(Path.GetFullPath(_mockGameDir), Path.GetFullPath(config.GameDirectory));
        Assert.Equal(Path.GetFullPath(_mockLayerDir), Path.GetFullPath(config.VulkanLayerPath));
    }

    [Fact]
    public void Parse_AllShortFlags_ParsesCorrectly()
    {
        _output.WriteLine("[Test] Testing CLI short flag aliases (-r, -o, -p, -s, -d, -w, -h, -c, -g, -l)...");

        string[] args =
        [
            "-r", _mockReplayPath,
            "-o", Path.Combine(_tempDirectory, "short_out.mp4"),
            "-p", "1",
            "-s", "10",
            "-d", "20",
            "-w", "1280",
            "-h", "720",
            "-c", "hevc_nvenc",
            "-g", _mockGameDir,
            "-l", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.Equal(1, config.PovClientIndex);
        Assert.Equal(10, config.StartSeconds);
        Assert.Equal(TimeSpan.FromSeconds(20), config.MaxDuration);
        Assert.Equal(1280u, config.RenderWidth);
        Assert.Equal(720u, config.RenderHeight);
        Assert.Equal(VideoCodec.HevcNvenc, config.Codec);
    }

    [Theory]
    [InlineData("1920x1080", true, 1920u, 1080u)]
    [InlineData("2560X1440", true, 2560u, 1440u)]
    [InlineData("1280,720", true, 1280u, 720u)]
    [InlineData("3840*2160", true, 3840u, 2160u)]
    [InlineData("720p", true, 1280u, 720u)]
    [InlineData("1080p", true, 1920u, 1080u)]
    [InlineData("1440p", true, 2560u, 1440u)]
    [InlineData("2K", true, 2560u, 1440u)]
    [InlineData("4K", true, 3840u, 2160u)]
    [InlineData("4k", true, 3840u, 2160u)]
    [InlineData("2160p", true, 3840u, 2160u)]
    [InlineData("invalid", false, 0u, 0u)]
    [InlineData("1920", false, 0u, 0u)]
    [InlineData("0x1080", false, 0u, 0u)]
    [InlineData("", false, 0u, 0u)]
    public void TryParseResolution_VariousInputs_ValidatesCorrectly(string input, bool expectedSuccess, uint expectedW,
        uint expectedH)
    {
        bool success = CommandLineParser.TryParseResolution(input, out uint w, out uint h);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(expectedW, w);
            Assert.Equal(expectedH, h);
        }
    }

    [Fact]
    public void Parse_Defaults_EnforceHeadlessZeroExposureAndFfprobeVerification()
    {
        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "default_safe.mp4"),
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.True(config.Headless);
        Assert.False(config.AllowForegroundActivation);
        Assert.True(config.VerifyOutputWithFfprobe);
        Assert.Equal("ffprobe", config.FfprobeExecutablePath);
    }

    [Fact]
    public void Parse_ExplicitFfprobeAndZeroExposureOverrideFlags_AreHonored()
    {
        string[] args =
        [
            "--replay", _mockReplayPath,
            "--output", Path.Combine(_tempDirectory, "custom_flags.mp4"),
            "--game-dir", _mockGameDir,
            "--layer-dir", _mockLayerDir,
            "--ffprobe", @"C:\ProgramData\chocolatey\bin\ffprobe.exe",
            "--headless", "false",
            "--allow-foreground-activation", "true",
            "--skip-ffprobe"
        ];

        RenderJobConfiguration? config = CommandLineParser.Parse(args, _tempDirectory);

        Assert.NotNull(config);
        Assert.False(config.Headless);
        Assert.True(config.AllowForegroundActivation);
        Assert.False(config.VerifyOutputWithFfprobe);
        Assert.Equal(@"C:\ProgramData\chocolatey\bin\ffprobe.exe", config.FfprobeExecutablePath);
    }
}
