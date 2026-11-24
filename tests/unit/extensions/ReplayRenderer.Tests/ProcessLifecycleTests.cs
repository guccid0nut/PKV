// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ProcessLifecycleTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.ProcessLifecycle;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class ProcessLifecycleTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITestOutputHelper _output;
    private readonly string _tempDirectory;

    public ProcessLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = NullLoggerFactory.Instance;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NexusReplay_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _output.WriteLine($"[Setup] Test temporary directory created at: {_tempDirectory}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
                _output.WriteLine($"[Teardown] Test temporary directory deleted: {_tempDirectory}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[Teardown Warning] Failed to clean up temp directory: {ex.Message}");
        }
    }

    [Fact]
    public void WindowsJobObjectSandbox_CreationAndNullProcessAssignment_HandlesGracefully()
    {
        _output.WriteLine("[Test] Starting WindowsJobObjectSandbox creation and null process assignment test...");
        ILogger<WindowsJobObjectSandbox> logger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        using WindowsJobObjectSandbox sandbox = new(logger);
        _output.WriteLine("[Test] Sandbox instantiated successfully.");

        Process? nullProcess = null;
        bool nullResult = sandbox.AssignProcess(nullProcess);
        _output.WriteLine($"[Test] AssignProcess(null) returned: {nullResult}");
        Assert.False(nullResult);

        nint zeroHandle = nint.Zero;
        bool zeroResult = sandbox.AssignProcess(zeroHandle);
        _output.WriteLine($"[Test] AssignProcess(nint.Zero) returned: {zeroResult}");
        Assert.False(zeroResult);
    }

    [Fact]
    public void WindowsJobObjectSandbox_AssignCurrentProcessHandle_SucceedsOnWindows()
    {
        _output.WriteLine("[Test] Testing AssignProcess with child process handle...");
        ILogger<WindowsJobObjectSandbox> logger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        using WindowsJobObjectSandbox sandbox = new(logger);

        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep",
            Arguments = OperatingSystem.IsWindows() ? "/c ping 127.0.0.1 -n 3 > nul" : "3",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        Process? childProcess = Process.Start(startInfo);
        Assert.NotNull(childProcess);

        try
        {
            bool assignResult = sandbox.AssignProcess(childProcess);
            _output.WriteLine($"[Test] AssignProcess(childProcess: {childProcess.Id}) returned: {assignResult}");

            if (OperatingSystem.IsWindows())
                Assert.True(assignResult);
            else
                Assert.True(assignResult); // non-Windows returns true as bypass
        }
        finally
        {
            try
            {
                if (!childProcess.HasExited)
                {
                    childProcess.Kill();
                    childProcess.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[Test Note] Process cleanup note: {ex.Message}");
            }
            finally
            {
                childProcess.Dispose();
            }
        }
    }

    [Fact]
    public void WindowsJobObjectSandbox_DoubleDispose_DoesNotThrow()
    {
        _output.WriteLine("[Test] Testing multiple Dispose calls on WindowsJobObjectSandbox...");
        ILogger<WindowsJobObjectSandbox> logger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        WindowsJobObjectSandbox sandbox = new(logger);
        sandbox.Dispose();
        _output.WriteLine("[Test] First Dispose succeeded.");

        sandbox.Dispose();
        _output.WriteLine("[Test] Second Dispose succeeded without exception.");
    }

    [Fact]
    public void RenderJobConfiguration_Validate_ValidConfig_DoesNotThrow()
    {
        _output.WriteLine("[Test] Testing RenderJobConfiguration.Validate on valid parameters...");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "C:\\Replays\\match_01.honreplay",
            OutputFilePath = "C:\\Videos\\match_01.mp4",
            GameDirectory = "C:\\Games\\Heroes of Newerth",
            VulkanLayerPath = "C:\\Layers",
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60,
            AudioSampleRate = 48000
        };

        config.Validate();
        _output.WriteLine("[Test] Config validation passed successfully.");

        Assert.Equal(1920u * 1080u * 4u, config.GetCalculatedVideoFrameBytes());
        Assert.Equal(3200, config.GetCalculatedAudioBytesPerFrame());
    }

    [Theory]
    [InlineData("", "C:\\Videos\\out.mp4", "C:\\Game", 1920, 1080, 60, 48000)]
    [InlineData("C:\\match.honreplay", "", "C:\\Game", 1920, 1080, 60, 48000)]
    [InlineData("C:\\match.honreplay", "C:\\Videos\\out.mp4", "", 1920, 1080, 60, 48000)]
    [InlineData("C:\\match.honreplay", "C:\\Videos\\out.mp4", "C:\\Game", 0, 1080, 60, 48000)]
    [InlineData("C:\\match.honreplay", "C:\\Videos\\out.mp4", "C:\\Game", 1920, 0, 60, 48000)]
    [InlineData("C:\\match.honreplay", "C:\\Videos\\out.mp4", "C:\\Game", 1920, 1080, 0, 48000)]
    [InlineData("C:\\match.honreplay", "C:\\Videos\\out.mp4", "C:\\Game", 1920, 1080, 60, 22050)]
    [InlineData("C:\\match.honreplay", "C:\\Videos\\out.mp4", "C:\\Game", 1920, 1080, 60, 44101)]
    public void RenderJobConfiguration_Validate_InvalidConfig_ThrowsArgumentException(
        string replayPath,
        string outputPath,
        string gameDir,
        uint width,
        uint height,
        int fps,
        int audioRate)
    {
        _output.WriteLine(
            $"[Test] Testing invalid configuration: Replay='{replayPath}', Out='{outputPath}', Game='{gameDir}', Dim={width}x{height}, FPS={fps}, AudioRate={audioRate}");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = replayPath,
            OutputFilePath = outputPath,
            GameDirectory = gameDir,
            VulkanLayerPath = "C:\\Layers",
            RenderWidth = width,
            RenderHeight = height,
            TargetFramerate = fps,
            AudioSampleRate = audioRate
        };

        Assert.Throws<ArgumentException>(config.Validate);
        _output.WriteLine("[Test] Expected ArgumentException thrown as expected.");
    }

    [Fact]
    public void ReplayProcessLauncher_LaunchMissingExecutable_ThrowsFileNotFoundException()
    {
        _output.WriteLine("[Test] Testing ReplayProcessLauncher with non-existent hon_x64.exe...");
        ILogger<ReplayProcessLauncher> launcherLogger = _loggerFactory.CreateLogger<ReplayProcessLauncher>();
        ILogger<WindowsJobObjectSandbox> sandboxLogger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        using WindowsJobObjectSandbox sandbox = new(sandboxLogger);
        ReplayProcessLauncher launcher = new(launcherLogger, sandbox);

        string emptyDir = Path.Combine(_tempDirectory, "EmptyGameDir");
        Directory.CreateDirectory(emptyDir);

        string fakeReplay = Path.Combine(_tempDirectory, "match.honreplay");
        File.WriteAllText(fakeReplay, "MOCK_REPLAY_DATA");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = fakeReplay,
            OutputFilePath = Path.Combine(_tempDirectory, "out.mp4"),
            GameDirectory = emptyDir,
            VulkanLayerPath = _tempDirectory,
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60
        };

        Assert.Throws<FileNotFoundException>(() => launcher.Launch(config, "hon_frame_pipe_test"));
        _output.WriteLine("[Test] FileNotFoundException correctly thrown for missing executable.");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void ReplayProcessLauncher_ScriptGenerationAndEnvironmentInjection_ValidatesCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        _output.WriteLine("[Test] Testing autoexec.cfg generation and script verification...");

        string mockGameDir = Path.Combine(_tempDirectory, "MockGame");
        Directory.CreateDirectory(mockGameDir);

        string mockExePath = Path.Combine(mockGameDir, "hon_x64.exe");
        File.WriteAllText(mockExePath, "MOCK_HON_BINARY");

        string mockReplaySource = Path.Combine(_tempDirectory, "source_match_999.honreplay");
        File.WriteAllText(mockReplaySource, "BINARY_REPLAY_STREAM_999");

        string mockDxvkConf = Path.Combine(mockGameDir, "dxvk.conf");
        File.WriteAllText(mockDxvkConf, "d3d9.maxFrameLatency = 1\ndxvk.hud = 0\n");

        ILogger<ReplayProcessLauncher> launcherLogger = _loggerFactory.CreateLogger<ReplayProcessLauncher>();
        ILogger<WindowsJobObjectSandbox> sandboxLogger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        using WindowsJobObjectSandbox sandbox = new(sandboxLogger);
        ReplayProcessLauncher launcher = new(launcherLogger, sandbox);

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = mockReplaySource,
            OutputFilePath = Path.Combine(_tempDirectory, "output.mp4"),
            GameDirectory = mockGameDir,
            VulkanLayerPath = Path.Combine(_tempDirectory, "VulkanLayer"),
            RenderWidth = 3840,
            RenderHeight = 2160,
            TargetFramerate = 60,
            PovClientIndex = 3,
            Timescale = 1.0f,
            EnableCleanCinematicUi = true
        };

        try
        {
            launcher.Launch(config, "hon_frame_pipe_4k");
        }
        catch (Exception ex)
        {
            _output.WriteLine(
                $"[Test Note] Process launch exception (expected since hon_x64.exe is a stub text file): {ex.Message}");
        }

        // Verify isolated userdataKONGOR replay copy (never polluting git-tracked game/replays)
        string expectedVfsReplay = Path.Combine(mockGameDir, "userdataKONGOR", "replays", "source_match_999.honreplay");
        _output.WriteLine($"[Test] Checking isolated staged replay file at: {expectedVfsReplay}");
        Assert.True(File.Exists(expectedVfsReplay));
        string vfsContent = File.ReadAllText(expectedVfsReplay);
        Assert.Equal("BINARY_REPLAY_STREAM_999", vfsContent);

        // Verify generated isolated autoexec.cfg in userdataKONGOR/configuration/autoexec.cfg
        string autoexecPath = Path.Combine(mockGameDir, "userdataKONGOR", "configuration", "autoexec.cfg");
        _output.WriteLine($"[Test] Checking autoexec.cfg at: {autoexecPath}");
        Assert.True(File.Exists(autoexecPath));

        string autoexecContent = File.ReadAllText(autoexecPath);
        _output.WriteLine("[Test] autoexec.cfg content:\n" + autoexecContent);

        Assert.Contains("Set \"sys_fixedFrameTime\" \"0\"", autoexecContent);
        Assert.Contains("Set \"host_framerate\" \"60\"", autoexecContent);
        Assert.Contains("Set \"sys_sleepTime\" \"0\"", autoexecContent);
        Assert.Contains("Set \"vid_maxFPS\" \"60\"", autoexecContent);
        Assert.Contains("Set \"vid_shadowmapSize\" \"2048\"", autoexecContent);
        Assert.Contains("Set \"vid_antialiasing\" \"4\"", autoexecContent);
        Assert.Contains("Set \"cg_draw2d\" \"false\"", autoexecContent);
        Assert.Contains("Set \"ui_render\" \"true\"", autoexecContent);
        Assert.Contains("Set \"replay_target\" \"replays/source_match_999.honreplay\"", autoexecContent);
        Assert.Contains("Set \"replay_client\" \"3\"", autoexecContent);
        Assert.Contains("Set \"replay_auto_quit\" \"false\"", autoexecContent);
    }

    [Fact]
    public void BuildModStackAndEngineArguments_EnforcesFn014MountPrecedenceAndIsolatesEnvironment()
    {
        string parentRoot = Path.Combine(_tempDirectory, "Fn014Layout");
        string gameDir = Path.Combine(parentRoot, "client");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "ClientPackageFiles"));
        Directory.CreateDirectory(Path.Combine(parentRoot, "shared"));

        List<string> mounts = ReplayProcessLauncher.BuildModStack(gameDir);
        Assert.Equal("base", mounts[0]);
        Assert.Equal("game", mounts[1]);
        Assert.Equal("ClientPackageFiles", mounts[2]);
        Assert.Equal("../shared", mounts[3]);
        Assert.Equal("KONGOR/configuration", mounts[4]);
        Assert.Equal("KONGOR/updates", mounts[5]);
        Assert.Equal("KONGOR/extensions", mounts[6]);
        Assert.Equal("override", mounts[^1]);

        RenderJobConfiguration defaultPipeConfig = new()
        {
            ReplayFilePath = Path.Combine(_tempDirectory, "match.honreplay"),
            OutputFilePath = Path.Combine(_tempDirectory, "out.mp4"),
            GameDirectory = gameDir,
            VulkanLayerPath = _tempDirectory,
            IpcPipeName = null
        };

        List<string> args = ReplayProcessLauncher.BuildEngineArguments(defaultPipeConfig, gameDir);
        Assert.Contains("-mod base;game;ClientPackageFiles;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions;override", args);
        Assert.DoesNotContain(args, a => a.Contains("willowmaker-ipc", StringComparison.OrdinalIgnoreCase));

        string? originalFilter = Environment.GetEnvironmentVariable("DXVK_FILTER_DEVICE_NAME");
        string? originalCapture = Environment.GetEnvironmentVariable("HON_CAPTURE_ENABLE");
        try
        {
            Environment.SetEnvironmentVariable("DXVK_FILTER_DEVICE_NAME", null);
            Environment.SetEnvironmentVariable("HON_CAPTURE_ENABLE", null);

            string stubExe = Path.Combine(gameDir, "hon_x64.exe");
            File.WriteAllText(stubExe, "NOT_A_VALID_PE");
            File.WriteAllText(defaultPipeConfig.ReplayFilePath, "REPLAY");

            using WindowsJobObjectSandbox sandbox = new(_loggerFactory.CreateLogger<WindowsJobObjectSandbox>());
            ReplayProcessLauncher launcher = new(_loggerFactory.CreateLogger<ReplayProcessLauncher>(), sandbox);
            try
            {
                launcher.Launch(defaultPipeConfig, "hon_frame_pipe_isolated");
            }
            catch (Exception)
            {
                // Expected on non-PE stub; verifies Launch pre-start setup did not pollute host Environment.
            }

            Assert.Null(Environment.GetEnvironmentVariable("DXVK_FILTER_DEVICE_NAME"));
            Assert.Null(Environment.GetEnvironmentVariable("HON_CAPTURE_ENABLE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DXVK_FILTER_DEVICE_NAME", originalFilter);
            Environment.SetEnvironmentVariable("HON_CAPTURE_ENABLE", originalCapture);
        }
    }
}
