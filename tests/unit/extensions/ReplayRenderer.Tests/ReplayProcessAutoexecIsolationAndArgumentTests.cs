// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayProcessAutoexecIsolationAndArgumentTests.cs
// System: ReplayRenderer.Tests - Wave 4A Tracked Config & Pipe ACL Hardening
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Ipc;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.ProcessLifecycle;
using Xunit;

namespace ReplayRenderer.Tests;

public sealed class ReplayProcessAutoexecIsolationAndArgumentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _uniqueReplayName;

    public ReplayProcessAutoexecIsolationAndArgumentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorWave4AReplay_" + Guid.NewGuid().ToString("N"));
        _uniqueReplayName = $"wave4a_probe_{Guid.NewGuid():N}.honreplay";
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore temp cleanup contention
            }
        }

        // Clean up any pre-fix scatter copies in user Documents if created during Red-phase repro
        CleanupScatteredUserDocsProbe(_uniqueReplayName);
    }

    [Fact]
    public async Task ReplayProcessLauncher_NeverMutatesOrDeletesGitTrackedGameAutoexecOrScattersReplays()
    {
        string mockGameDir = Path.Combine(_tempDir, "client");
        Directory.CreateDirectory(Path.Combine(mockGameDir, "game"));
        Directory.CreateDirectory(Path.Combine(mockGameDir, "base"));
        Directory.CreateDirectory(Path.Combine(mockGameDir, "override"));
        Directory.CreateDirectory(Path.Combine(mockGameDir, "KONGOR", "configuration"));

        // Stub hon_x64.exe (non-PE text file fails Process.Start cleanly after config prep)
        await File.WriteAllTextAsync(Path.Combine(mockGameDir, "hon_x64.exe"), "STUB_BINARY");

        string trackedGameAutoexec = Path.Combine(mockGameDir, "game", "autoexec.cfg");
        string trackedKongorAutoexec = Path.Combine(mockGameDir, "KONGOR", "configuration", "autoexec.cfg");
        string trackedFullscreenCfg = Path.Combine(mockGameDir, "KONGOR", "configuration", "init.cfg");

        await File.WriteAllTextAsync(trackedGameAutoexec, "// GIT_TRACKED_GAME_AUTOEXEC_SENTINEL");
        await File.WriteAllTextAsync(trackedKongorAutoexec, "// GIT_TRACKED_KONGOR_AUTOEXEC_SENTINEL");
        await File.WriteAllTextAsync(trackedFullscreenCfg, "SetSave \"vid_fullscreen\" \"true\"");

        string sourceReplay = Path.Combine(_tempDir, _uniqueReplayName);
        await File.WriteAllTextAsync(sourceReplay, "MOCK_REPLAY_PAYLOAD");

        using WindowsJobObjectSandbox sandbox = new WindowsJobObjectSandbox(NullLogger<WindowsJobObjectSandbox>.Instance);
        await using ReplayProcessLauncher launcher = new ReplayProcessLauncher(
            NullLogger<ReplayProcessLauncher>.Instance,
            sandbox);

        RenderJobConfiguration config = new RenderJobConfiguration
        {
            ReplayFilePath = sourceReplay,
            OutputFilePath = Path.Combine(_tempDir, "out.mp4"),
            GameDirectory = mockGameDir,
            VulkanLayerPath = Path.Combine(_tempDir, "layer"),
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60
        };

        try
        {
            launcher.Launch(config, $"wave4a-frame-{Guid.NewGuid():N}");
        }
        catch
        {
            // Expected when stub hon_x64.exe fails Process.Start
        }

        await launcher.DisposeAsync();

        // Git-tracked files under game/, KONGOR/configuration/, base/, override/ MUST remain untouched!
        Assert.True(File.Exists(trackedGameAutoexec));
        Assert.Equal("// GIT_TRACKED_GAME_AUTOEXEC_SENTINEL", await File.ReadAllTextAsync(trackedGameAutoexec));
        Assert.Equal("// GIT_TRACKED_KONGOR_AUTOEXEC_SENTINEL", await File.ReadAllTextAsync(trackedKongorAutoexec));
        Assert.Equal("SetSave \"vid_fullscreen\" \"true\"", await File.ReadAllTextAsync(trackedFullscreenCfg));
        Assert.False(File.Exists(Path.Combine(mockGameDir, "override", "autoexec.cfg")));
        Assert.False(File.Exists(Path.Combine(mockGameDir, "base", "autoexec.cfg")));

        // No scattered .honreplay copies in git-tracked directories or left behind after DisposeAsync
        Assert.False(File.Exists(Path.Combine(mockGameDir, _uniqueReplayName)));
        Assert.False(File.Exists(Path.Combine(mockGameDir, "game", _uniqueReplayName)));
        Assert.False(File.Exists(Path.Combine(mockGameDir, "override", _uniqueReplayName)));
        Assert.False(File.Exists(Path.Combine(mockGameDir, "userdataKONGOR", "replays", _uniqueReplayName)));
    }

    [Fact]
    public async Task NamedPipeFrameServer_OnWindows_DoesNotGrantWorldSidEveryoneAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $"test-wave4a-frame-{Guid.NewGuid():N}";
        await using NamedPipeFrameServer server = new NamedPipeFrameServer(
            NullLogger<NamedPipeFrameServer>.Instance,
            pipeName,
            64,
            64);

        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.StartAsync(null, cts.Token);

        await using NamedPipeClientStream aclProbe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.None,
            HandleInheritability.None);
        await aclProbe.ConnectAsync(2000, cts.Token);

        PipeSecurity security = aclProbe.GetAccessControl();
        SecurityIdentifier worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (PipeAccessRule rule in rules)
        {
            Assert.NotEqual(worldSid, rule.IdentityReference);
        }
    }

    private static void CleanupScatteredUserDocsProbe(string fileName)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents")
        ];

        string[] suffixes =
        [
            Path.Combine("Heroes of Newerth x64", "game", "replays", fileName),
            Path.Combine("Heroes of Newerth x64", "replays", fileName),
            Path.Combine("Heroes of Newerth x64 - PKOF", "game", "replays", fileName),
            Path.Combine("Heroes of Newerth x64 - PKOF", "replays", fileName)
        ];

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (string suffix in suffixes)
            {
                string candidate = Path.Combine(root, suffix);
                try
                {
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }
}
