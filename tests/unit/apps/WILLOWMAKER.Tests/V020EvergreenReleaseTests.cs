// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Core.Utilities;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Services.P2P;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class V020EvergreenReleaseTests
{
    [Fact]
    public async Task DiscordPresence_ReadNewLogLinesAsync_StreamsIncrementalUtf16LeChunksWithoutRereading()
    {
        // protects:Incremental UTF-16LE console.log tailing in DiscordPresenceService without re-reading from byte 0 or splitting UTF-16 code units.
        // fails_when: Seek offset ignores UTF-16LE BOM, misaligns odd byte offsets, or drops newly appended lines.
        // seam: Real filesystem FileStream + DetectLogStreamEncoding + ReadNewLogLinesAsync.
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_drp_utf16_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "console.log");

        try
        {
            Dictionary<string, Encoding> encodings = new(StringComparer.OrdinalIgnoreCase);
            byte[] bom = Encoding.Unicode.GetPreamble();
            byte[] chunk1 = Encoding.Unicode.GetBytes("Login success: #77 [PK]AlphaHost\r\n");
            await using (FileStream fs1 = new(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                await fs1.WriteAsync(bom);
                await fs1.WriteAsync(chunk1);
            }

            long len1 = new FileInfo(logPath).Length;
            List<string> firstBatch = await DiscordPresenceService.ReadNewLogLinesAsync(logPath, 0, encodings);
            firstBatch.Should().ContainSingle().Which.Should().Be("Login success: #77 [PK]AlphaHost");

            byte[] chunk2 = Encoding.Unicode.GetBytes("[NEXUS_TELEMETRY] {\"player\":\"AlphaHost\",\"hero\":\"Hero_DoctorRepulsor\",\"status\":\"InGame\"}\r\n");
            await using (FileStream fs2 = new(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                await fs2.WriteAsync(chunk2);
            }

            // Also verify odd-offset resilience (len1 + 1 snaps cleanly back to UTF-16 code-unit boundary)
            List<string> secondBatch = await DiscordPresenceService.ReadNewLogLinesAsync(logPath, len1 + 1, encodings);
            secondBatch.Should().ContainSingle().Which.Should().Contain("Hero_DoctorRepulsor");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DiscordPresence_AccountIsolationAndActiveMatchProtection_PreventsForeignStompAndHeroWipe()
    {
        // protects: 3-tier DRP account isolation, active-match hero preservation, solo Party==null, and [NEXUS_EVENT] filtering.
        // fails_when: Foreign [NEXUS_TELEMETRY] / PresenceDto / teammate disconnect / [NEXUS_EVENT] overwrites local hero or account name.
        // seam: Real DiscordPresenceService state machine (ProcessConsoleLogLine, ProcessPresenceUpdate, ProcessFastBusState).
        using DiscordPresenceService drp = new();

        P2pConnectionService.IsDisallowedDefaultNickname("guccidonut").Should().BeTrue();
        P2pConnectionService.IsDisallowedDefaultNickname("UnnamedNewbie").Should().BeTrue();
        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            P2pConnectionService.IsDisallowedDefaultNickname(Environment.UserName).Should().BeTrue();
        }
        DiscordPresenceService.IsDisallowedPreloadedConfigName("Kongor").Should().BeTrue();
        DiscordPresenceService.IsDisallowedPreloadedConfigName("KongorSrv").Should().BeTrue();
        DiscordPresenceService.IsDisallowedPreloadedConfigName("UnnamedNewbie").Should().BeTrue();
        DiscordPresenceService.IsPlaceholderPlayerName("UnnamedNewbie").Should().BeTrue();
        DiscordPresenceService.StripClanTag("[PK]CommunityPlayer").Should().Be("CommunityPlayer");

        drp.ProcessConsoleLogLine("Login success: #42 [PK]CommunityPlayer");
        drp.AccountId.Should().Be(42);
        drp.AccountName.Should().Be("[PK]CommunityPlayer");
        drp.IsLocalPlayerName("CommunityPlayer").Should().BeTrue();
        drp.IsLocalPlayerName("[OTHER]Stranger").Should().BeFalse();

        // Enter Mid Wars match as Doctor Repulsor via structured JSON telemetry
        drp.ProcessConsoleLogLine("[NEXUS_TELEMETRY] {\"player\":\"CommunityPlayer\",\"hero\":\"Hero_DoctorRepulsor\",\"map\":\"midwars\",\"status\":\"InGame\",\"level\":11,\"k\":6,\"d\":1,\"a\":9}");
        drp.SelectedHeroAssetKey.Should().Be("hero_doctor_repulsor");
        drp.SelectedHeroDisplayName.Should().Be("Doctor Repulsor");
        drp.IsInActiveMatchOrLobby.Should().BeTrue();
        drp.CurrentPresence.Should().NotBeNull();
        drp.CurrentPresence!.Party.Should().BeNull("solo players (_partySize == 1) must omit Discord Party metadata");

        // 1. [NEXUS_EVENT] non-DRP UI telemetry must be completely ignored
        drp.ProcessConsoleLogLine("[NEXUS_EVENT] {\"event\":\"store_preview\",\"hero\":\"Hero_Chipper\",\"status\":\"Lobby\"}");
        drp.SelectedHeroAssetKey.Should().Be("hero_doctor_repulsor");

        // 2. Foreign player [NEXUS_TELEMETRY] must be ignored
        drp.ProcessConsoleLogLine("[NEXUS_TELEMETRY] player=ForeignUser;hero=Hero_Devourer;status=Lobby");
        drp.SelectedHeroAssetKey.Should().Be("hero_doctor_repulsor");
        drp.AccountName.Should().Be("[PK]CommunityPlayer");

        // 3. Foreign SignalR PresenceDto must be ignored
        drp.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 999,
            State = "Online in Menus",
            Details = "In Main Menu",
            SmallImageKey = "hero_pyromancer",
            SmallImageText = "ForeignUser"
        });
        drp.SelectedHeroAssetKey.Should().Be("hero_doctor_repulsor");
        drp.AccountId.Should().Be(42);

        // 4. Mid-match teammate nickname/pipe/disconnect chatter must NOT wipe active match hero
        drp.ProcessConsoleLogLine("nickname, STRING: ForeignTeammate");
        drp.ProcessConsoleLogLine("^g999|ForeignTeammate|5");
        drp.ProcessConsoleLogLine("ForeignTeammate has disconnected.");
        drp.SelectedHeroAssetKey.Should().Be("hero_doctor_repulsor");
        drp.AccountName.Should().Be("[PK]CommunityPlayer");

        // 5. Party > 1 populates Party metadata cleanly
        drp.UpdateParty(3, 5, "party_42");
        drp.CurrentPresence.Party.Should().NotBeNull();
        drp.CurrentPresence.Party!.Size.Should().Be(3);
        drp.CurrentPresence.Party.Max.Should().Be(5);

        // 6. FastBus phase 5 -> 1 returns cleanly to Main Menu
        drp.ProcessFastBusState(5, 1001, "midwars", "CommunityPlayer");
        drp.ProcessFastBusState(1, 0, string.Empty, "CommunityPlayer");
        drp.SelectedHeroAssetKey.Should().BeNull();
        drp.CurrentPresence.Details.Should().Be("In Main Menu");
    }

    [Fact]
    public void DiagnosticRedactor_ScrubText_EnforcesWordBoundariesOnShortUsernames()
    {
        // protects: Crash report readability when tester OS username is a short prefix like 'manu', 'max', or 'win'.
        // fails_when: Substring scrubbing mangles 'ManualReport', 'manufacturer', 'Windowed', or 'Maximum' into '[REDACTED_USER]alReport'.
        // seam: Real DiagnosticRedactor.ScrubText regex pipeline.
        string sample = """
                        ReportType: ManualReport
                        DisplayMode: Windowed (Maximum Fidelity, manufacturer=NVIDIA)
                        User manu launched /home/manu/.config/ProjectKongor on host win-box with Bearer secret_jwt_token_123456
                        Peer connected from 198.51.100.42 while master 127.0.0.1 stayed local.
                        """;

        (string scrubbed, RedactionAuditCounts counts) = DiagnosticRedactor.ScrubText(
            sample,
            allowedMasterServerHost: "127.0.0.1",
            userNameOverride: "manu",
            machineNameOverride: "win");

        scrubbed.Should().Contain("ManualReport");
        scrubbed.Should().Contain("Windowed");
        scrubbed.Should().Contain("Maximum");
        scrubbed.Should().Contain("manufacturer=NVIDIA");
        scrubbed.Should().Contain("User [REDACTED_USER] launched [USER_DIR]/.config/ProjectKongor");
        scrubbed.Should().Contain("host [REDACTED_HOST]-box");
        scrubbed.Should().Contain("Bearer [REDACTED_TOKEN]");
        scrubbed.Should().Contain("[REDACTED_IP]");
        scrubbed.Should().Contain("127.0.0.1");
        counts.SecretsRedacted.Should().BeGreaterThanOrEqualTo(1);
        counts.UserIdentifiersRedacted.Should().BeGreaterThanOrEqualTo(2);
        counts.PeerIpsRedacted.Should().Be(1);
    }

    [Fact]
    public async Task DiagnosticBundleService_PosixExit0x7FAndOptimusAdapterRejection_ProducesActionableSyntheticTriage()
    {
        // protects: Zero-MDMP crash diagnosis on Linux (ld.so exit 0x7F) and Windows Hybrid Optimus / pre-Vulkan-1.3 laptops.
        // fails_when: ExportBundleAsync emits generic 'UNKNOWN' instead of missing library or discrete GPU remediation.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + ZIP CRASH_SUMMARY.md inspection + Logger.FlushPendingEntriesAsync.
        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_diag_v020_" + Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outDir);

        try
        {
            static async Task<string> ReadSharedLogAsync(string path)
            {
                await using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader sr = new(fs, Encoding.UTF8);
                return await sr.ReadToEndAsync();
            }

            string launcherLogPath = Path.Combine(tempRoot, "WILLOWMAKER.log");
            await using (Logger liveLogger = new(launcherLogPath))
            {
                liveLogger.Log("LAUNCH", "Testing non-destructive FlushPendingEntriesAsync before diagnostic zip.");
                await liveLogger.FlushPendingEntriesAsync();
                File.Exists(launcherLogPath).Should().BeTrue();
                (await ReadSharedLogAsync(launcherLogPath)).Should().Contain("FlushPendingEntriesAsync");

                // Logger must remain open and writable after FlushPendingEntriesAsync
                liveLogger.Log("LAUNCH", "Post-flush log line still active.");
                await liveLogger.FlushPendingEntriesAsync();
                (await ReadSharedLogAsync(launcherLogPath)).Should().Contain("Post-flush log line still active.");
            }

            DiagnosticBundleResult posixResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: tempRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                processExitCode: 0x7FU,
                capturedFatalStderr: "./hon-x86_64: error while loading shared libraries: libxml2.so.2: cannot open shared object file: No such file or directory",
                activeLauncherLogPath: launcherLogPath);

            posixResult.Success.Should().BeTrue();
            posixResult.ZipPath.Should().Contain("libxml2");
            using (ZipArchive zip = ZipFile.OpenRead(posixResult.ZipPath))
            {
                ZipArchiveEntry? summary = zip.GetEntry("CRASH_SUMMARY.md");
                summary.Should().NotBeNull();
                using StreamReader reader = new(summary!.Open());
                string md = await reader.ReadToEndAsync();
                md.Should().Contain("libxml2.so.2");
                md.Should().Contain("0x0000007F");
            }

            // Now write a simulated DXVK "No adapters found" log with a hidden discrete RTX GPU
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "hon_x64_d3d9.log"),
                "info:  Intel(R) UHD Graphics 620:\nwarn:  DXVK: No adapters found. Please check your device filter settings and Vulkan setup.");

            DiagnosticBundleResult optimusResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: tempRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                knownDiscreteGpusOverride: ["NVIDIA GeForce RTX 4070 Laptop GPU"]);

            optimusResult.Success.Should().BeTrue();
            optimusResult.ZipPath.Should().Contain("dxvk_no_vulkan13_adapter");
            using (ZipArchive zip2 = ZipFile.OpenRead(optimusResult.ZipPath))
            {
                using StreamReader reader2 = new(zip2.GetEntry("CRASH_SUMMARY.md")!.Open());
                string md2 = await reader2.ReadToEndAsync();
                md2.Should().Contain("NVIDIA GeForce RTX 4070 Laptop GPU");
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ModManager_AlphanumericOfficialResourcesArchives_AreStrictlyProtectedFromCleanup()
    {
        // protects: Official avatar/skin archives (resourcesa0.s2z..resourcesz9.s2z) from accidental deletion during ModManager sync.
        // fails_when: ProtectedResourcesArchiveRegex only matches numeric resources0..99.s2z and misses alphanumeric official packs.
        // seam: Real ModManagerViewModel.IsProtectedSystemOrExportArchive.
        ModManagerViewModel.IsProtectedSystemOrExportArchive("resources0.s2z").Should().BeTrue();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("resources94.s2z").Should().BeTrue();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("resourcesa0.s2z").Should().BeTrue();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("resourcesd4.s2z").Should().BeTrue();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("resourcesz9.s2z").Should().BeTrue();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("textures.s2z").Should().BeTrue();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("midwars.s2z").Should().BeTrue();

        ModManagerViewModel.IsProtectedSystemOrExportArchive("resources_custom_mod.s2z").Should().BeFalse();
        ModManagerViewModel.IsProtectedSystemOrExportArchive("mod_custom_ui.s2z").Should().BeFalse();
    }

    [Fact]
    public void EvergreenDefaults_OfflineAutoDisplayAudioAndRuntimePrerequisites_HonorSixMonthContract()
    {
        // protects: 6-month standalone offline-first defaults, sanitized nickname resolution, display CVars, audio mixRate, and Vulkan 1.3 fallback.
        // fails_when: Default master server reverts to remote host, dev nicknames leak, Windowed sets fullscreen=true, or Vulkan 1.2 skips D3D9 fallback.
        // seam: Real LauncherSettingsModel, MainViewModel, P2pConnectionService, LaunchConfigWriterService, PlaytestPolicyService, AudioModernizationService.
        LauncherSettingsModel defaults = LauncherSettingsModel.CreateFirstBootDefaults(144, isWindowsOverride: true);
        defaults.MasterServerAddress.Should().Be(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer);
        defaults.EnableNvidiaReflex.Should().BeFalse();
        defaults.EnableSpatialAudio.Should().BeFalse();
        defaults.AllowThirdPartyVulkanOverlays.Should().BeFalse();

        MainViewModel.IsLocalOfflineAutoSelection(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer).Should().BeTrue();
        MainViewModel.IsOfflineModeSelection(LauncherSettingsModel.OfflineModeSelection).Should().BeTrue();
        MainViewModel.IsOfflineModeSelection("localhost:55555").Should().BeFalse();

        string tempClient = Path.Combine(Path.GetTempPath(), "kongor_nick_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempClient, "userdata"));
        try
        {
            File.WriteAllText(
                Path.Combine(tempClient, "userdata", "startup.cfg"),
                "SetSave \"login_name\" \"guccidonut\"\r\n");
            P2pConnectionService.ResolveDefaultNickname(tempClient).Should().Be("Player");

            File.WriteAllText(
                Path.Combine(tempClient, "userdata", "startup.cfg"),
                "SetSave \"p2p_nickname\" \"EvergreenHost\"\r\nSetSave \"login_name\" \"guccidonut\"\r\n");
            P2pConnectionService.ResolveDefaultNickname(tempClient).Should().Be("EvergreenHost");
        }
        finally
        {
            Directory.Delete(tempClient, recursive: true);
        }

        LaunchConfigWriterService.ResolveDisplayModeCvars("Borderless Windowed", "vid_vulkan")
            .Should().Be(("true", "true", "false"));
        LaunchConfigWriterService.ResolveDisplayModeCvars("Windowed", "vid_vulkan")
            .Should().Be(("false", "false", "false"));
        LaunchConfigWriterService.ResolveDisplayModeCvars("Exclusive Fullscreen", "vid_vulkan")
            .Should().Be(("true", "false", "false"));
        LaunchConfigWriterService.ResolveDisplayModeCvars("Exclusive Fullscreen", "vid_d3d9")
            .Should().Be(("true", "false", "true"));

        AudioModernizationService.ResolveAudioCvars(enableSpatialAudio: false, endpointSampleRateOverride: 44100)
            .Should().Be(("44100", "100"));
        AudioModernizationService.ResolveAudioCvars(enableSpatialAudio: true, endpointSampleRateOverride: 48000)
            .Should().Be(("48000", "256"));

        RuntimePrerequisiteResult linuxPrereq = PlaytestPolicyService.CheckRuntimePrerequisites(
            clientRoot: Path.GetTempPath(),
            lddOutputOverride: "\tlibxml2.so.2 => not found\n\tlibGL.so.1 => /lib/x86_64-linux-gnu/libGL.so.1\n",
            isLinuxOverride: true,
            isWindowsOverride: false);
        linuxPrereq.MissingLinuxLibraries.Should().ContainSingle().Which.Should().Be("libxml2.so.2");

        RuntimePrerequisiteResult winFallback = PlaytestPolicyService.CheckRuntimePrerequisites(
            clientRoot: Path.GetTempPath(),
            vulkanAdaptersOverride: [("Intel(R) HD Graphics 520", PlaytestPolicyService.MakeVulkanApiVersion(1, 2, 0))],
            windowsDisplayAdaptersOverride: ["Intel(R) HD Graphics 520", "NVIDIA GeForce GTX 1060"],
            isLinuxOverride: false,
            isWindowsOverride: true);
        winFallback.RequiresNativeD3D9Fallback.Should().BeTrue();
        winFallback.Warnings.Should().ContainSingle(w => w.Contains("NVIDIA GeForce GTX 1060"));
    }
}
