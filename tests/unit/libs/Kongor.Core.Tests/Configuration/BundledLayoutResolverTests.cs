// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using Kongor.Core.Configuration;
using Xunit;

namespace Kongor.Core.Tests.Configuration;

public sealed class BundledLayoutResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public BundledLayoutResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "KongorBundledLayoutTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        BundledLayoutResolver.ResetLoggedRootsForTesting();
    }

    public void Dispose()
    {
        BundledLayoutResolver.ResetLoggedRootsForTesting();
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors during test teardown
            }
        }
    }

    private void CreateValidBundleLayout(string root)
    {
        File.WriteAllText(Path.Combine(root, "playtest_policy.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "games", "client"));
    }

    [Fact]
    public void TryResolve_AtBundleRoot_ShouldResolveSuccessfullyAndPopulateAllProperties()
    {
        // Arrange
        CreateValidBundleLayout(_tempRoot);

        // Act
        bool resolved = BundledLayoutResolver.TryResolve(
            out BundledLayout? layout,
            startDirectory: _tempRoot,
            ridOverride: "win-x64",
            isWindowsOverride: true);

        // Assert
        resolved.Should().BeTrue();
        layout.Should().NotBeNull();
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempRoot));

        layout!.Root.Should().Be(normalizedRoot);
        layout.Rid.Should().Be("win-x64");
        layout.IsWindows.Should().BeTrue();
        layout.ExeSuffix.Should().Be(".exe");

        layout.UserData.Should().Be(Path.Combine(normalizedRoot, "userdata"));
        layout.SecretsDir.Should().Be(Path.Combine(normalizedRoot, "userdata", "secrets"));
        layout.SecretsJson.Should().Be(Path.Combine(normalizedRoot, "userdata", "secrets", "nexus_secrets.json"));
        layout.DockerEnv.Should().Be(Path.Combine(normalizedRoot, "userdata", "secrets", ".env.docker"));
        layout.InitialCredentials.Should().Be(Path.Combine(normalizedRoot, "userdata", "initial_admin_credentials.txt"));
        layout.InviteCodes.Should().Be(Path.Combine(normalizedRoot, "userdata", "invite_codes.txt"));
        layout.DataProtectionKeys.Should().Be(Path.Combine(normalizedRoot, "userdata", "dataprotection-keys"));
        layout.Logs.Should().Be(Path.Combine(normalizedRoot, "userdata", "logs"));
        layout.DockerCompose.Should().Be(Path.Combine(normalizedRoot, "infra", "docker", "docker-compose.yml"));
        layout.ClientDir.Should().Be(Path.Combine(normalizedRoot, "games", "client"));
        layout.DedicatedServerDir.Should().Be(Path.Combine(normalizedRoot, "games", "server"));

        layout.ControlPanelExe.Should().Be(Path.Combine(normalizedRoot, "control-panel-win-x64", "NEXUS.ControlPanel.GUI.exe"));
        layout.CompelExe.Should().Be(Path.Combine(normalizedRoot, "compel-win-x64", "COMPEL.exe"));
        layout.CompelGuiExe.Should().Be(Path.Combine(normalizedRoot, "compel-win-x64", "COMPEL.GUI.exe"));
        layout.KongorCliExe.Should().Be(Path.Combine(normalizedRoot, "tools-win-x64", "kongor.exe"));
    }

    [Fact]
    public void TryResolve_FromSubdirectories_ShouldResolveUpToThreeLevels_AndFailAtFourLevels()
    {
        // Arrange: bundle root at _tempRoot
        CreateValidBundleLayout(_tempRoot);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempRoot));

        // Level 1: control-panel-win-x64 (1 parent up)
        string level1Dir = Path.Combine(_tempRoot, "control-panel-win-x64");
        Directory.CreateDirectory(level1Dir);

        // Level 2: services-win-x64/master-server (2 parents up)
        string level2Dir = Path.Combine(_tempRoot, "services-win-x64", "master-server");
        Directory.CreateDirectory(level2Dir);

        // Level 3: a/b/c (3 parents up)
        string level3Dir = Path.Combine(_tempRoot, "a", "b", "c");
        Directory.CreateDirectory(level3Dir);

        // Level 4: a/b/c/d (4 parents deep -> exceeds 0..3 parent search)
        string level4Dir = Path.Combine(_tempRoot, "a", "b", "c", "d");
        Directory.CreateDirectory(level4Dir);

        // Act & Assert Level 1
        bool resolvedL1 = BundledLayoutResolver.TryResolve(out BundledLayout? layoutL1, startDirectory: level1Dir);
        resolvedL1.Should().BeTrue();
        layoutL1!.Root.Should().Be(normalizedRoot);

        // Act & Assert Level 2
        bool resolvedL2 = BundledLayoutResolver.TryResolve(out BundledLayout? layoutL2, startDirectory: level2Dir);
        resolvedL2.Should().BeTrue();
        layoutL2!.Root.Should().Be(normalizedRoot);

        // Act & Assert Level 3
        bool resolvedL3 = BundledLayoutResolver.TryResolve(out BundledLayout? layoutL3, startDirectory: level3Dir);
        resolvedL3.Should().BeTrue();
        layoutL3!.Root.Should().Be(normalizedRoot);

        // Act & Assert Level 2 and Level 3 with trailing directory separator (matching AppContext.BaseDirectory)
        bool resolvedL2Slash = BundledLayoutResolver.TryResolve(
            out BundledLayout? layoutL2Slash,
            startDirectory: level2Dir + Path.DirectorySeparatorChar);
        resolvedL2Slash.Should().BeTrue();
        layoutL2Slash!.Root.Should().Be(normalizedRoot);

        bool resolvedL3Slash = BundledLayoutResolver.TryResolve(
            out BundledLayout? layoutL3Slash,
            startDirectory: level3Dir + Path.DirectorySeparatorChar);
        resolvedL3Slash.Should().BeTrue();
        layoutL3Slash!.Root.Should().Be(normalizedRoot);

        // Act & Assert Level 4
        bool resolvedL4 = BundledLayoutResolver.TryResolve(out BundledLayout? layoutL4, startDirectory: level4Dir);
        resolvedL4.Should().BeFalse();
        layoutL4.Should().BeNull();
    }

    [Fact]
    public void TryResolve_InNonBundleDirectories_ShouldReturnFalse()
    {
        // Case 1: Dev repo layout (games/client exists, but playtest_policy.json is absent)
        string devRepoDir = Path.Combine(_tempRoot, "dev-repo");
        Directory.CreateDirectory(Path.Combine(devRepoDir, "games", "client"));

        bool resolvedDev = BundledLayoutResolver.TryResolve(out BundledLayout? layoutDev, startDirectory: devRepoDir);
        resolvedDev.Should().BeFalse();
        layoutDev.Should().BeNull();

        // Case 2: playtest_policy.json exists, but games/client is absent
        string policyOnlyDir = Path.Combine(_tempRoot, "policy-only");
        Directory.CreateDirectory(policyOnlyDir);
        File.WriteAllText(Path.Combine(policyOnlyDir, "playtest_policy.json"), "{}");

        bool resolvedPolicy = BundledLayoutResolver.TryResolve(out BundledLayout? layoutPolicy, startDirectory: policyOnlyDir);
        resolvedPolicy.Should().BeFalse();
        layoutPolicy.Should().BeNull();

        // Case 3: Empty directory
        string emptyDir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(emptyDir);

        bool resolvedEmpty = BundledLayoutResolver.TryResolve(out BundledLayout? layoutEmpty, startDirectory: emptyDir);
        resolvedEmpty.Should().BeFalse();
        layoutEmpty.Should().BeNull();

        // Case 4: Non-existent directory
        string nonExistent = Path.Combine(_tempRoot, "does-not-exist");
        bool resolvedNonExistent = BundledLayoutResolver.TryResolve(out BundledLayout? layoutNonExistent, startDirectory: nonExistent);
        resolvedNonExistent.Should().BeFalse();
        layoutNonExistent.Should().BeNull();
    }

    [Fact]
    public void LauncherExe_WhenNeitherFileExists_ReturnsPlatformAppropriateDefault()
    {
        // When neither exists on disk:
        // Windows -> returns <Root>/WILLOWMAKER.exe
        // Non-Windows -> returns <Root>/launcher-<Rid>/WILLOWMAKER
        BundledLayout winLayout = new(_tempRoot, "win-x64", isWindows: true);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempRoot));
        winLayout.LauncherExe.Should().Be(Path.Combine(normalizedRoot, "WILLOWMAKER.exe"));

        BundledLayout linuxLayout = new(_tempRoot, "linux-x64", isWindows: false);
        linuxLayout.LauncherExe.Should().Be(Path.Combine(normalizedRoot, "launcher-linux-x64", "WILLOWMAKER"));
    }

    [Fact]
    public void LauncherExe_WhenOnlyLauncherRidExists_ResolvesLauncherRidBinary()
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempRoot));

        // Windows: create launcher-win-x64/WILLOWMAKER.exe
        string winLauncherDir = Path.Combine(_tempRoot, "launcher-win-x64");
        Directory.CreateDirectory(winLauncherDir);
        string winLauncherFile = Path.Combine(winLauncherDir, "WILLOWMAKER.exe");
        File.WriteAllText(winLauncherFile, "fake binary");

        BundledLayout winLayout = new(_tempRoot, "win-x64", isWindows: true);
        winLayout.LauncherExe.Should().Be(Path.Combine(normalizedRoot, "launcher-win-x64", "WILLOWMAKER.exe"));

        // Linux: create launcher-linux-x64/WILLOWMAKER
        string linuxLauncherDir = Path.Combine(_tempRoot, "launcher-linux-x64");
        Directory.CreateDirectory(linuxLauncherDir);
        string linuxLauncherFile = Path.Combine(linuxLauncherDir, "WILLOWMAKER");
        File.WriteAllText(linuxLauncherFile, "fake binary");

        BundledLayout linuxLayout = new(_tempRoot, "linux-x64", isWindows: false);
        linuxLayout.LauncherExe.Should().Be(Path.Combine(normalizedRoot, "launcher-linux-x64", "WILLOWMAKER"));
    }

    [Fact]
    public void LauncherExe_WhenBothRootAndLauncherRidExist_RootBinaryTakesPrecedence()
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempRoot));

        // Windows: both root WILLOWMAKER.exe and launcher-win-x64/WILLOWMAKER.exe exist
        string rootWinFile = Path.Combine(_tempRoot, "WILLOWMAKER.exe");
        File.WriteAllText(rootWinFile, "root binary");

        string winLauncherDir = Path.Combine(_tempRoot, "launcher-win-x64");
        Directory.CreateDirectory(winLauncherDir);
        string ridWinFile = Path.Combine(winLauncherDir, "WILLOWMAKER.exe");
        File.WriteAllText(ridWinFile, "rid binary");

        BundledLayout winLayout = new(_tempRoot, "win-x64", isWindows: true);
        winLayout.LauncherExe.Should().Be(Path.Combine(normalizedRoot, "WILLOWMAKER.exe"));

        // Non-Windows: both root WILLOWMAKER and launcher-linux-x64/WILLOWMAKER exist
        string rootLinuxFile = Path.Combine(_tempRoot, "WILLOWMAKER");
        File.WriteAllText(rootLinuxFile, "root linux binary");

        string linuxLauncherDir = Path.Combine(_tempRoot, "launcher-linux-x64");
        Directory.CreateDirectory(linuxLauncherDir);
        string ridLinuxFile = Path.Combine(linuxLauncherDir, "WILLOWMAKER");
        File.WriteAllText(ridLinuxFile, "rid linux binary");

        BundledLayout linuxLayout = new(_tempRoot, "linux-x64", isWindows: false);
        linuxLayout.LauncherExe.Should().Be(Path.Combine(normalizedRoot, "WILLOWMAKER"));
    }

    [Fact]
    public void DetectCurrentRid_ShouldMapPlatformsAndArchitecturesAccurately()
    {
        BundledLayout.DetectCurrentRid(OSPlatform.Windows, Architecture.X64).Should().Be("win-x64");
        BundledLayout.DetectCurrentRid(OSPlatform.Windows, Architecture.Arm64).Should().Be("win-arm64");

        BundledLayout.DetectCurrentRid(OSPlatform.Linux, Architecture.X64).Should().Be("linux-x64");
        BundledLayout.DetectCurrentRid(OSPlatform.Linux, Architecture.Arm64).Should().Be("linux-arm64");

        BundledLayout.DetectCurrentRid(OSPlatform.OSX, Architecture.Arm64).Should().Be("osx-arm64");
        BundledLayout.DetectCurrentRid(OSPlatform.OSX, Architecture.X64).Should().Be("osx-x64");

        // Runtime detection with no overrides should return a non-empty string matching current platform
        string currentRid = BundledLayout.DetectCurrentRid();
        currentRid.Should().NotBeNullOrWhiteSpace();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            currentRid.Should().StartWith("win-");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            currentRid.Should().StartWith("linux-");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            currentRid.Should().StartWith("osx-");
        }
    }

    [Fact]
    public void ServiceExe_ShouldResolveCanonicalServicesAndCustomBinaries()
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempRoot));
        BundledLayout winLayout = new(_tempRoot, "win-x64", isWindows: true);

        // Canonical 4 services on Windows
        winLayout.ServiceDir("master-server").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "master-server"));
        winLayout.ServiceExe("master-server").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "master-server", "KONGOR.MasterServer.exe"));

        winLayout.ServiceDir("chat-server").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "chat-server"));
        winLayout.ServiceExe("chat-server").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "chat-server", "TRANSMUTANSTEIN.ChatServer.exe"));

        winLayout.ServiceDir("web-portal-api").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "web-portal-api"));
        winLayout.ServiceExe("web-portal-api").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "web-portal-api", "ZORGATH.WebPortal.API.exe"));

        winLayout.ServiceDir("web-portal-ui").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "web-portal-ui"));
        winLayout.ServiceExe("web-portal-ui").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "web-portal-ui", "DAWNBRINGER.WebPortal.UI.exe"));

        // Fallback service name
        winLayout.ServiceExe("custom-worker").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "custom-worker", "custom-worker.exe"));

        // Explicit binaryBaseName override
        winLayout.ServiceExe("custom-worker", "CustomWorkerService").Should().Be(Path.Combine(normalizedRoot, "services-win-x64", "custom-worker", "CustomWorkerService.exe"));

        // Canonical 4 services on Linux (no .exe suffix)
        BundledLayout linuxLayout = new(_tempRoot, "linux-x64", isWindows: false);
        linuxLayout.ServiceExe("master-server").Should().Be(Path.Combine(normalizedRoot, "services-linux-x64", "master-server", "KONGOR.MasterServer"));
        linuxLayout.ServiceExe("chat-server").Should().Be(Path.Combine(normalizedRoot, "services-linux-x64", "chat-server", "TRANSMUTANSTEIN.ChatServer"));
        linuxLayout.ServiceExe("web-portal-api").Should().Be(Path.Combine(normalizedRoot, "services-linux-x64", "web-portal-api", "ZORGATH.WebPortal.API"));
        linuxLayout.ServiceExe("web-portal-ui").Should().Be(Path.Combine(normalizedRoot, "services-linux-x64", "web-portal-ui", "DAWNBRINGER.WebPortal.UI"));
    }

    [Fact]
    public void TryResolve_LogsOncePerUniqueResolvedRoot()
    {
        CreateValidBundleLayout(_tempRoot);

        System.Collections.Generic.List<string> capturedLines = [];
        BundledLayoutResolver.ResetLoggedRootsForTesting();

        // First resolve should log once via diagnostic sink without mutating global Console streams
        bool first = BundledLayoutResolver.TryResolve(
            out _,
            startDirectory: _tempRoot,
            ridOverride: "win-x64",
            isWindowsOverride: true,
            diagnosticLogger: capturedLines.Add);
        first.Should().BeTrue();

        string firstOutput = string.Join(Environment.NewLine, capturedLines);
        firstOutput.Should().Contain("[BundledLayout] root=");
        firstOutput.Should().Contain("rid=win-x64");

        capturedLines.Clear();

        // Second resolve of same root should NOT log
        bool second = BundledLayoutResolver.TryResolve(
            out _,
            startDirectory: _tempRoot,
            ridOverride: "win-x64",
            isWindowsOverride: true,
            diagnosticLogger: capturedLines.Add);
        second.Should().BeTrue();

        capturedLines.Should().BeEmpty();
    }
}
