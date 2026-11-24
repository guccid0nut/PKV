// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

/// <summary>
/// Value-Weighted Regression Suite for Silent ExitCode 0 Crash Detection, Cross-Platform Config Isolation,
/// Menu Asset-Prewarm Discord RPC Suppression, and Journal-Verified 0-Mod Fast-Path Sync.
/// </summary>
public sealed class SilentExitAndCrossPlatformHardeningTests
{
    [Fact]
    public async Task HandleGameProcessTerminationAsync_SilentExitZeroWithActivePidSentinel_GeneratesCrashReportAndSnapshotsConsoleLog()
    {
        // protects: Guaranteed crash reporting when hon_x64.exe terminates with ExitCode 0 (e.g. CSystem::Exit(0) or TerminateProcess(0)) mid-session without user quit
        // fails_when: HandleGameProcessTerminationAsync assumes ExitCode == 0 is always a clean user quit and closes WILLOWMAKER without saving diagnostics
        // seam: Real MainViewModel.HandleGameProcessTerminationAsync + per-PID sentinel files + DiagnosticBundleService zip export
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_SilentExitPid_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string overrideDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(overrideDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow.AddSeconds(-20);
            const int pid = 26844;
            string sessionSentinel = Path.Combine(crashDir, $"session-active.{pid}.sentinel");
            File.WriteAllText(sessionSentinel, $"PID={pid}\nStartUtc={launchStartUtc:O}\n");
            File.SetLastWriteTimeUtc(sessionSentinel, DateTime.UtcNow);

            string consoleLogPath = Path.Combine(overrideDir, "console.log");
            File.WriteAllText(consoleLogPath,
                "************************************************************\n" +
                "K2 - S2 Games - Heroes of Newerth\n" +
                "[MAIN_LUA] LoggedIn() executing -> Transition to lobby!\n" +
                "CHostClient::Connect(empath_maze)\n");
            File.SetLastWriteTimeUtc(consoleLogPath, DateTime.UtcNow);

            int? exitOverrideCode = null;
            var vm = new MainViewModel
            {
                LaunchIsInProgress = true,
                ExitProcessOverride = code => exitOverrideCode = code
            };

            await vm.HandleGameProcessTerminationAsync(
                exitCode: 0u,
                launchStartUtc: launchStartUtc,
                clientRoot: clientRoot,
                outputDirectoryOverride: outDir,
                gameProcessId: pid);

            vm.DisposeApplicationBackgroundServices();

            Assert.Null(exitOverrideCode);
            Assert.False(vm.LaunchIsInProgress);
            Assert.True(vm.HasCrashReportBanner);
            Assert.Contains("0x00000000", vm.CrashReportTargetText, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(vm.LastAutoExportedCrashZipPath));
            Assert.True(File.Exists(vm.LastAutoExportedCrashZipPath));
            Assert.Contains(Directory.GetFiles(crashDir, "crash_console_*_pid26844.log"), File.Exists);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleGameProcessTerminationAsync_ConcurrentTwoClientPids_IsolatesPerPidSentinels()
    {
        // protects: 2-client local multiplayer testing so Client A's active sentinel never triggers a false crash report when Client B exits cleanly
        // fails_when: Sentinels are global rather than keyed by process ID
        // seam: Real MainViewModel.HandleGameProcessTerminationAsync with two concurrent PID sentinels in logs/crash
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_TwoClients_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow.AddSeconds(-10);
            const int pidStillRunningOrCrashed = 41001;
            const int pidCleanQuit = 41002;

            File.WriteAllText(Path.Combine(crashDir, $"session-active.{pidStillRunningOrCrashed}.sentinel"), "PID=41001\n");
            File.WriteAllText(Path.Combine(crashDir, $"session-active.{pidCleanQuit}.sentinel"), "PID=41002\n");
            File.WriteAllText(Path.Combine(crashDir, $"clean-exit.{pidCleanQuit}.sentinel"), "PID=41002\nReason=WM_CLOSE\n");

            int? exitCodeCaptured = null;
            var vm = new MainViewModel
            {
                ExitProcessOverride = code => exitCodeCaptured = code
            };

            await vm.HandleGameProcessTerminationAsync(
                exitCode: 0u,
                launchStartUtc: launchStartUtc,
                clientRoot: clientRoot,
                gameProcessId: pidCleanQuit);

            vm.DisposeApplicationBackgroundServices();

            Assert.Equal(0, exitCodeCaptured);
            Assert.False(vm.HasCrashReportBanner);
            // Client A's active sentinel must remain untouched while Client B's sentinels were cleaned up
            Assert.True(File.Exists(Path.Combine(crashDir, $"session-active.{pidStillRunningOrCrashed}.sentinel")));
            Assert.False(File.Exists(Path.Combine(crashDir, $"session-active.{pidCleanQuit}.sentinel")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("[KONGOR-CLEAN-QUIT] User initiated Quit via UI", false)]
    [InlineData("CHost::Shutdown() complete.", false)]
    [InlineData("K2Updater: Applying update and restarting client...", false)]
    [InlineData("CHostClient::Connect -> abrupt end with zero shutdown marker", true)]
    public async Task HandleGameProcessTerminationAsync_CrossPlatformPosixConsoleLog_ClassifiesQuitVsSilentCrash(
        string finalLogLine,
        bool expectedCrash)
    {
        // protects: Linux and macOS crash reporting where NativeGuard version.dll is not loaded and silent ExitCode 0 aborts only leave console.log
        // fails_when: POSIX console.log paths (.Heroes of Newerth) are ignored or legitimate quit/updater markers trigger false crash reports
        // seam: Real MainViewModel.HandleGameProcessTerminationAsync + POSIX userdata/.Heroes of Newerth/override/console.log
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_PosixExit_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string linuxLogDir = Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "override");
        Directory.CreateDirectory(linuxLogDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow.AddSeconds(-15);
            string linuxConsoleLog = Path.Combine(linuxLogDir, "console.log");
            File.WriteAllText(linuxConsoleLog,
                "************************************************************\n" +
                "K2 - S2 Games - Heroes of Newerth Linux x86_64\n" +
                "vid_gl2 initialized successfully.\n" +
                "[MAIN_LUA] LoggedIn() executing -> Transition to lobby!\n" +
                finalLogLine + "\n");
            File.SetLastWriteTimeUtc(linuxConsoleLog, DateTime.UtcNow);

            int? exitCodeCaptured = null;
            var vm = new MainViewModel
            {
                LaunchIsInProgress = true,
                ExitProcessOverride = code => exitCodeCaptured = code
            };

            await vm.HandleGameProcessTerminationAsync(
                exitCode: 0u,
                launchStartUtc: launchStartUtc,
                clientRoot: clientRoot,
                outputDirectoryOverride: outDir);

            vm.DisposeApplicationBackgroundServices();

            Assert.Equal(expectedCrash, vm.HasCrashReportBanner);
            if (expectedCrash)
            {
                Assert.Null(exitCodeCaptured);
                Assert.True(File.Exists(vm.LastAutoExportedCrashZipPath));
            }
            else
            {
                Assert.Equal(0, exitCodeCaptured);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleGameProcessTerminationAsync_IgnoresStaleConsoleLogFromPreviousSession()
    {
        // protects: Clean early exit handling so an old unrotated console.log from yesterday never triggers a false silent-crash report
        // fails_when: InspectSessionConsoleLogShutdownState fails to check File.GetLastWriteTimeUtc >= launchStartUtc
        // seam: Real MainViewModel.HandleGameProcessTerminationAsync with pre-launch timestamped console.log
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_StaleLog_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string overrideDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(overrideDir);

        try
        {
            string staleLog = Path.Combine(overrideDir, "console.log");
            File.WriteAllText(staleLog,
                "K2 - S2 Games - Heroes of Newerth\n" +
                "[MAIN_LUA] Old crashed session from 1 hour ago without quit marker\n" +
                new string('x', 512));
            File.SetLastWriteTimeUtc(staleLog, DateTime.UtcNow.AddMinutes(-30));

            int? exitCodeCaptured = null;
            var vm = new MainViewModel
            {
                ExitProcessOverride = code => exitCodeCaptured = code
            };

            await vm.HandleGameProcessTerminationAsync(
                exitCode: 0u,
                launchStartUtc: DateTimeOffset.UtcNow,
                clientRoot: clientRoot);

            vm.DisposeApplicationBackgroundServices();

            Assert.Equal(0, exitCodeCaptured);
            Assert.False(vm.HasCrashReportBanner);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DiscordPresenceService_SuppressesWorldLoadDuringMenuPrewarm_ThenAllowsRealMatchLoad()
    {
        // protects: Discord Rich Presence accuracy so clicking PLAY NOW ("WARMING GAMEPLAY ASSETS...") does not broadcast "In Match (Forests of Caldavar)"
        // fails_when: ProcessConsoleLogLine treats PreloadWorld('caldavar') CWorld::StartLoad lines as an active match
        // seam: Real DiscordPresenceService.ProcessConsoleLogLine state machine
        using var rpc = new DiscordPresenceService();
        rpc.ProcessConsoleLogLine("Login success: #59 guccidonut");
        Assert.Equal(string.Empty, rpc.CurrentGameMode);

        rpc.ProcessConsoleLogLine("[KONGOR-BOOT-PRELOAD] START map=caldavar trigger=select_a_mode");
        rpc.ProcessConsoleLogLine("Start loading world caldavar");
        rpc.ProcessConsoleLogLine("CWorld::StartLoad(caldavar)");
        Assert.Equal(string.Empty, rpc.CurrentGameMode);

        rpc.ProcessConsoleLogLine("[KONGOR-BOOT-PRELOAD] COMPLETE elapsedMs=4120");
        rpc.ProcessConsoleLogLine("Joining game 'Mid Wars Public'...");
        rpc.ProcessConsoleLogLine("Start loading world midwars");
        Assert.Contains("Mid Wars", rpc.CurrentGameMode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchConfigWriterService_SynchronizeStartup_IsolatesPlatformTargetsAndForcesGl2OnPosix()
    {
        // protects: Cross-platform startup.cfg integrity so Windows Vulkan launches never stamp vid_vulkan into Linux/macOS startup.cfg files
        // fails_when: Directory.Exists(linux-x64) triggers POSIX config writes on Windows or stamps vid_vulkan into Linux/macOS startup.cfg
        // seam: Real LaunchConfigWriterService.SynchronizeStartupAndAccountProfiles filesystem output
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_XplatCfg_{Guid.NewGuid():N}");
        string winRoot = Path.Combine(tempRoot, "client");
        string nestedLinuxDir = Path.Combine(winRoot, "linux-x64");
        Directory.CreateDirectory(nestedLinuxDir);

        try
        {
            LaunchConfigWriterService.SynchronizeStartupAndAccountProfiles(
                rootDir: winRoot,
                activeDriver: "vid_vulkan",
                fullscreenSetting: "false",
                borderlessSetting: "true",
                resParam: "1920,1080",
                refreshRateNumber: "144",
                shadowMapSize: "2048",
                shadowDrawDistance: "3000",
                foliageDensity: "1.0",
                cullGroundSprites: "false",
                textureCompressionSetting: "true",
                ambientOcclusionSetting: "true");

            if (OperatingSystem.IsWindows())
            {
                string winStartup = Path.Combine(winRoot, "userdata", "Heroes of Newerth x64", "game", "startup.cfg");
                Assert.True(File.Exists(winStartup));
                Assert.Contains("\"vid_vulkan\"", File.ReadAllText(winStartup), StringComparison.Ordinal);

                string nestedLinuxStartup = Path.Combine(nestedLinuxDir, "userdata", ".Heroes of Newerth", "game", "startup.cfg");
                Assert.False(File.Exists(nestedLinuxStartup));
            }

            // Explicitly targeting linux-x64 must clamp vid_driver to vid_gl2 even if caller passed vid_vulkan
            LaunchConfigWriterService.SynchronizeStartupAndAccountProfiles(
                rootDir: nestedLinuxDir,
                activeDriver: "vid_vulkan",
                fullscreenSetting: "false",
                borderlessSetting: "true",
                resParam: "1920,1080",
                refreshRateNumber: "144",
                shadowMapSize: "2048",
                shadowDrawDistance: "3000",
                foliageDensity: "1.0",
                cullGroundSprites: "false",
                textureCompressionSetting: "true",
                ambientOcclusionSetting: "true");

            string explicitLinuxStartup = Path.Combine(nestedLinuxDir, "userdata", ".Heroes of Newerth", "game", "startup.cfg");
            Assert.True(File.Exists(explicitLinuxStartup));
            string linuxText = File.ReadAllText(explicitLinuxStartup);
            Assert.Contains("SetSave \"vid_driver\" \"vid_gl2\"", linuxText, StringComparison.Ordinal);
            Assert.DoesNotContain("vid_vulkan", linuxText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ModManagerViewModel_ZeroModsFastPath_SkipsWhenClean_AndRollsBackWhenDirty()
    {
        // protects: Cold-launch responsiveness (< 50ms when 0 mods active) WITHOUT skipping rollback when dirty mod artifacts exist
        // fails_when: Fast path triggers while resources999.s2z or rollback_journal.json is present, or clean 0-mod state runs full archive scan
        // seam: Real ModManagerViewModel.EnsureSynchronizedForLaunchAsync + filesystem dirty-baseline markers
        string tempClient = Path.Combine(Path.GetTempPath(), $"Kongor_ModFastPath_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(tempClient, "game");
        Directory.CreateDirectory(gameDir);

        try
        {
            ConcurrentQueue<string> logs = new();
            var vm = new ModManagerViewModel((cat, msg) => logs.Enqueue(msg), autoLoad: false)
            {
                GameClientDirectoryOverride = tempClient
            };

            // 1. Clean baseline -> fast path must engage
            await vm.EnsureSynchronizedForLaunchAsync();
            Assert.Contains(logs, m => m.Contains("[MOD APPLY FAST-PATH]", StringComparison.Ordinal));

            // 2. Dirty baseline (leftover resources999.s2z) -> fast path MUST NOT skip rollback!
            while (logs.TryDequeue(out _)) { }
            string dirtyRes999 = Path.Combine(gameDir, "resources999.s2z");
            File.WriteAllBytes(dirtyRes999, [0x50, 0x4B, 0x05, 0x06]);

            await vm.EnsureSynchronizedForLaunchAsync();
            Assert.DoesNotContain(logs, m => m.Contains("[MOD APPLY FAST-PATH]", StringComparison.Ordinal));
            Assert.False(File.Exists(dirtyRes999));
        }
        finally
        {
            if (Directory.Exists(tempClient)) Directory.Delete(tempClient, recursive: true);
        }
    }

    [Fact]
    public void CheckColdBootCrashSentinel_DetectsPerPidCrashAndOrphanedSessionSentinels()
    {
        // protects: Cold-boot crash recovery banner when WILLOWMAKER is re-launched after an unsupervised CSystem::Error/Exit or silent dead-PID abort
        // fails_when: CheckColdBootCrashSentinel only checks legacy last-crashed.sentinel and ignores last-crashed.<pid>.sentinel or dead-PID session-active.<pid>.sentinel
        // seam: Real MainViewModel.CheckColdBootCrashSentinel + filesystem sentinel inspection
        string tempClient = Path.Combine(Path.GetTempPath(), $"Kongor_ColdBootPid_{Guid.NewGuid():N}");
        string crashDir = Path.Combine(tempClient, "logs", "crash");
        Directory.CreateDirectory(crashDir);

        try
        {
            const int nonExistentDeadPid = 999991;
            string orphanActive = Path.Combine(crashDir, $"session-active.{nonExistentDeadPid}.sentinel");
            File.WriteAllText(orphanActive, $"PID={nonExistentDeadPid}\n");
            File.SetLastWriteTimeUtc(orphanActive, DateTime.UtcNow);

            var vm = new MainViewModel();
            vm.CheckColdBootCrashSentinel(tempClient);
            vm.DisposeApplicationBackgroundServices();

            Assert.True(vm.HasCrashReportBanner);
            Assert.Contains(nonExistentDeadPid.ToString(), vm.CrashReportBannerText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempClient)) Directory.Delete(tempClient, recursive: true);
        }
    }

    [Fact]
    public async Task HandleGameProcessTerminationAsync_NonZeroExitWithoutSentinel_IgnoresStaleVehMinidumpPairAndSelectsFreshMinidump()
    {
        // protects: Crash attribution accuracy so a Force-Close / TerminateProcess(1) exit minutes after a recovered [SHADPS4-VEH] mid-session exception is never misattributed to the stale minidump_*.dmp / .rich.dmp pair
        // fails_when: HandleGameProcessTerminationAsync only filters stale minidump_*.dmp on ExitCode == 0 or fails to advance threshold past companion .rich.dmp before the 2.5s settle poll and ExportBundleAsync
        // seam: Real MainViewModel.HandleGameProcessTerminationAsync + DiagnosticBundleService.LocateLatestCrashDumpPath + zip export
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_StaleVehExit1_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow.AddSeconds(-120);

            // Case 1: Stale (-60s / -59s) recovered mid-session VEH minidump pair with ExitCode == 1 and no sentinel
            string staleTier1 = Path.Combine(crashDir, "minidump_20261108_120000.dmp");
            string staleRich = Path.Combine(crashDir, "minidump_20261108_120000.rich.dmp");
            File.WriteAllBytes(staleTier1, new byte[512]);
            File.WriteAllBytes(staleRich, new byte[1024]);
            File.SetLastWriteTimeUtc(staleTier1, DateTime.UtcNow.AddSeconds(-60));
            File.SetLastWriteTimeUtc(staleRich, DateTime.UtcNow.AddSeconds(-59));

            var staleVm = new MainViewModel
            {
                LaunchIsInProgress = true,
                CrashDumpSettlePollIntervalOverride = TimeSpan.FromMilliseconds(5)
            };

            await staleVm.HandleGameProcessTerminationAsync(
                exitCode: 1u,
                launchStartUtc: launchStartUtc,
                clientRoot: clientRoot,
                outputDirectoryOverride: outDir,
                gameProcessId: 48201);
            staleVm.DisposeApplicationBackgroundServices();

            Assert.True(staleVm.HasCrashReportBanner);
            Assert.Null(staleVm.LastActiveCrashDumpPath);
            Assert.Equal("Process Exit 0x00000001", staleVm.CrashReportTargetText);

            // Case 2: Fresh (-2s) fatal VEH minidump written right at process exit IS selected
            string freshTier1 = Path.Combine(crashDir, "minidump_20261108_120158.dmp");
            File.WriteAllBytes(freshTier1, new byte[512]);
            File.SetLastWriteTimeUtc(freshTier1, DateTime.UtcNow.AddSeconds(-2));

            var freshVm = new MainViewModel
            {
                LaunchIsInProgress = true
            };

            await freshVm.HandleGameProcessTerminationAsync(
                exitCode: 1u,
                launchStartUtc: launchStartUtc,
                clientRoot: clientRoot,
                outputDirectoryOverride: outDir,
                gameProcessId: 48202);
            freshVm.DisposeApplicationBackgroundServices();

            Assert.True(freshVm.HasCrashReportBanner);
            Assert.Equal(freshTier1, freshVm.LastActiveCrashDumpPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveCanonicalDiagnosticsSettingsDir_RoutesClientRootToUserdataKongorAndPreservesBareTempDir()
    {
        // protects: Single canonical launcher_settings.json path so diagnostics crash acknowledgments never split-brain into games/client/launcher_settings.json
        // fails_when: ResolveCanonicalDiagnosticsSettingsDir writes to clientRoot directly when userdataKONGOR or hon_x64.exe exists, or alters bare unit-test temp directories
        // seam: Real MainViewModel.ResolveCanonicalDiagnosticsSettingsDir filesystem probe
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_DiagSettingsDir_{Guid.NewGuid():N}");
        string bareDir = Path.Combine(tempRoot, "bare");
        string clientWithUserdata = Path.Combine(tempRoot, "client_ud");
        string clientWithExe = Path.Combine(tempRoot, "client_exe");
        Directory.CreateDirectory(bareDir);
        Directory.CreateDirectory(Path.Combine(clientWithUserdata, "userdataKONGOR"));
        Directory.CreateDirectory(clientWithExe);
        File.WriteAllText(Path.Combine(clientWithExe, "hon_x64.exe"), "stub");

        try
        {
            Assert.Equal(bareDir, MainViewModel.ResolveCanonicalDiagnosticsSettingsDir(bareDir));
            Assert.Equal(
                Path.Combine(clientWithUserdata, "userdataKONGOR"),
                MainViewModel.ResolveCanonicalDiagnosticsSettingsDir(clientWithUserdata));
            Assert.Equal(
                Path.Combine(clientWithExe, "userdataKONGOR"),
                MainViewModel.ResolveCanonicalDiagnosticsSettingsDir(clientWithExe));

            // Also verify lossless migration of a newer legacy clientRoot LastAcknowledgedCrashUtc into userdataKONGOR
            string crashDir = Path.Combine(clientWithUserdata, "logs", "crash");
            Directory.CreateDirectory(crashDir);
            string oldCrashSentinel = Path.Combine(crashDir, "last-crashed.32.sentinel");
            File.WriteAllText(oldCrashSentinel, "PID=32\nReason=LegacyCrash\n");
            File.SetLastWriteTimeUtc(oldCrashSentinel, DateTime.UtcNow.AddDays(-7));

            string userdataDir = Path.Combine(clientWithUserdata, "userdataKONGOR");
            var canonicalSettings = LauncherSettingsService.Load(userdataDir);
            canonicalSettings.LastAcknowledgedCrashUtc = DateTimeOffset.UtcNow.AddDays(-18).ToString("O");
            LauncherSettingsService.Save(canonicalSettings, userdataDir);

            DateTimeOffset newerLegacyAck = DateTimeOffset.UtcNow.AddMinutes(-5);
            var legacySettings = LauncherSettingsService.Load(clientWithUserdata);
            legacySettings.LastAcknowledgedCrashUtc = newerLegacyAck.ToString("O");
            LauncherSettingsService.Save(legacySettings, clientWithUserdata);

            var vm = new MainViewModel();
            vm.CheckColdBootCrashSentinel(clientWithUserdata);
            vm.DisposeApplicationBackgroundServices();

            Assert.False(vm.HasCrashReportBanner);
            var migratedSettings = LauncherSettingsService.Load(userdataDir);
            Assert.True(DateTimeOffset.TryParse(migratedSettings.LastAcknowledgedCrashUtc, out DateTimeOffset migratedAck));
            Assert.True(migratedAck >= newerLegacyAck.AddSeconds(-1));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CheckColdBootCrashSentinel_PrunesOrphanedQuitSentinelWithConfirmedCleanExit_AndStillDetectsRecycledPidHang()
    {
        // protects: False-positive 0xCFFFFFFF SHUTDOWN_HANG_TIMEOUT suppression when player_sessions.jsonl confirms ExitCode 0 clean exit for that PID, without masking a genuine newer hang on a recycled Windows PID
        // fails_when: CheckColdBootCrashSentinel fires a crash banner for a PID already logged as clean exitCode=0/crashed=false, or suppresses a newer sentinel using a multi-day-old recycled PID record
        // seam: Real MainViewModel.CheckColdBootCrashSentinel + logs/player_sessions.jsonl cross-check
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_QuitOrphan_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string sessionsPath = Path.Combine(clientRoot, "logs", "player_sessions.jsonl");
        Directory.CreateDirectory(crashDir);

        try
        {
            // Case 1: Orphaned quit-initiated.999991.sentinel whose PID exited cleanly 1 second later in player_sessions.jsonl
            // Use a non-existent high PID (like recycledPid=999992 below) so live Windows worker processes never collide with IsProcessCurrentlyRunning().
            const int cleanPid = 999991;
            DateTimeOffset quitTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-30);
            DateTimeOffset cleanExitUtc = quitTimeUtc.AddSeconds(1);
            string cleanQuitSentinel = Path.Combine(crashDir, $"quit-initiated.{cleanPid}.sentinel");
            File.WriteAllText(cleanQuitSentinel, $"PID={cleanPid}\nReason=CSystem::Exit\n");
            File.SetLastWriteTimeUtc(cleanQuitSentinel, quitTimeUtc.UtcDateTime);

            File.WriteAllText(
                sessionsPath,
                $"{{\"timestampUtc\":\"{cleanExitUtc:O}\",\"sessionId\":\"clean1\",\"processId\":{cleanPid},\"durationMs\":716406,\"exitCode\":0,\"crashed\":false,\"crashReason\":null}}\n");

            var cleanVm = new MainViewModel();
            cleanVm.CheckColdBootCrashSentinel(clientRoot);
            cleanVm.DisposeApplicationBackgroundServices();

            Assert.False(cleanVm.HasCrashReportBanner);
            Assert.False(File.Exists(cleanQuitSentinel));

            // Case 2: Recycled Windows PID — old clean exit from 2 days ago MUST NOT suppress a brand-new shutdown hang sentinel
            const int recycledPid = 999992;
            DateTimeOffset oldCleanUtc = DateTimeOffset.UtcNow.AddDays(-2);
            string freshHangSentinel = Path.Combine(crashDir, $"quit-initiated.{recycledPid}.sentinel");
            File.WriteAllText(freshHangSentinel, $"PID={recycledPid}\nReason=CSystem::Exit\n");
            File.SetLastWriteTimeUtc(freshHangSentinel, DateTime.UtcNow);

            File.AppendAllText(
                sessionsPath,
                $"{{\"timestampUtc\":\"{oldCleanUtc:O}\",\"sessionId\":\"oldclean\",\"processId\":{recycledPid},\"durationMs\":50000,\"exitCode\":0,\"crashed\":false,\"crashReason\":null}}\n");

            var hangVm = new MainViewModel();
            hangVm.CheckColdBootCrashSentinel(clientRoot);
            hangVm.DisposeApplicationBackgroundServices();

            Assert.True(hangVm.HasCrashReportBanner);
            Assert.Contains(recycledPid.ToString(), hangVm.CrashReportBannerText, StringComparison.Ordinal);
            Assert.True(File.Exists(freshHangSentinel));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CleanupSessionActiveSentinels_PreservesCleanExitWhenQuitSentinelLocked_AndCleansMirrorDirWhenUnlocked()
    {
        // protects: Atomic sentinel cleanup ordering so a locked quit-initiated.<pid>.sentinel never causes clean-exit.<pid>.sentinel to be deleted first (which would manufacture a false-positive SHUTDOWN_HANG_TIMEOUT)
        // fails_when: CleanupSessionActiveSentinels deletes clean-exit.<pid>.sentinel while quit-initiated.<pid>.sentinel fails to delete, or misses userdata/logs/crash mirror sentinels
        // seam: Real MainViewModel.CleanupSessionActiveSentinels with OS file lock + dual crash directories
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_SentinelOrder_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string primaryCrashDir = Path.Combine(clientRoot, "logs", "crash");
        string mirrorCrashDir = Path.Combine(clientRoot, "userdata", "logs", "crash");
        Directory.CreateDirectory(primaryCrashDir);
        Directory.CreateDirectory(mirrorCrashDir);

        try
        {
            const int pid = 44512;
            string activePrimary = Path.Combine(primaryCrashDir, $"session-active.{pid}.sentinel");
            string quitPrimary = Path.Combine(primaryCrashDir, $"quit-initiated.{pid}.sentinel");
            string cleanPrimary = Path.Combine(primaryCrashDir, $"clean-exit.{pid}.sentinel");
            string quitMirror = Path.Combine(mirrorCrashDir, $"quit-initiated.{pid}.sentinel");
            string cleanMirror = Path.Combine(mirrorCrashDir, $"clean-exit.{pid}.sentinel");

            File.WriteAllText(activePrimary, $"PID={pid}\n");
            File.WriteAllText(quitPrimary, $"PID={pid}\n");
            File.WriteAllText(cleanPrimary, $"PID={pid}\n");
            File.WriteAllText(quitMirror, $"PID={pid}\n");
            File.WriteAllText(cleanMirror, $"PID={pid}\n");

            if (OperatingSystem.IsWindows())
            {
                using (var exclusiveLock = new FileStream(quitPrimary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    MainViewModel.CleanupSessionActiveSentinels(primaryCrashDir, pid, _ => { }, clientRoot);

                    // Primary clean-exit MUST be preserved while quit-initiated is locked!
                    Assert.False(File.Exists(activePrimary));
                    Assert.True(File.Exists(quitPrimary));
                    Assert.True(File.Exists(cleanPrimary));

                    // Mirror crash dir was unlocked so it cleaned cleanly
                    Assert.False(File.Exists(quitMirror));
                    Assert.False(File.Exists(cleanMirror));
                }
            }

            // Once unlocked, all remaining primary sentinels must be cleanly removed
            MainViewModel.CleanupSessionActiveSentinels(primaryCrashDir, pid, _ => { }, clientRoot);
            Assert.False(File.Exists(activePrimary));
            Assert.False(File.Exists(quitPrimary));
            Assert.False(File.Exists(cleanPrimary));
            Assert.False(File.Exists(quitMirror));
            Assert.False(File.Exists(cleanMirror));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
