// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class OverlayConflictDetectorTests
{
    [Theory]
    [InlineData("Elgato Virtual Display", "Generic PnP Monitor", @"\\?\DISPLAY#ELG0001#4&1234", 1920, 1080, false, true)]
    [InlineData("NVIDIA GeForce RTX 4090", "Stream Deck Prompter", @"USB\VID_0FD9&PID_0080", 1024, 600, false, true)]
    [InlineData("USB Display Adapter", "Generic Display", null, 800, 480, false, true)]
    [InlineData("DisplayLink USB Device", "ASUS MB16AC", null, 1920, 1080, false, true)]
    [InlineData("USB Display Adapter", "Small Recovery Panel", null, 800, 600, true, false)]
    [InlineData("NVIDIA GeForce RTX 4090", "LG UltraGear", @"\\?\DISPLAY#GSM5BBF#5&abcd", 2560, 1440, false, false)]
    [InlineData("AMD Radeon RX 7900 XTX", "ROG Swift PG32UCDM", @"\\?\DISPLAY#AUS3201#5&ef01", 3840, 2160, true, false)]
    public void IsAuxiliaryOrStreamDeckDisplay_ClassifiesMiniUsbAndStreamDeckDisplaysWithoutDroppingGamingOrPrimaryMonitors(
        string? adapterName,
        string? friendlyName,
        string? hardwareId,
        int nativeWidth,
        int nativeHeight,
        bool isPrimary,
        bool expectedIgnored)
    {
        // protects: StreamDeck / USB mini-LCD displays must never pollute WILLOWMAKER's gaming monitor selector while keeping primary and real secondary monitors intact
        // fails_when: Auxiliary 800x480 / Elgato / DisplayLink displays are exposed as gaming monitors or the OS primary display is mistakenly filtered out
        // seam: HardwareDetectionService.IsAuxiliaryOrStreamDeckDisplay classification boundary
        bool actual = HardwareDetectionService.IsAuxiliaryOrStreamDeckDisplay(
            adapterName,
            friendlyName,
            hardwareId,
            nativeWidth,
            nativeHeight,
            isPrimary);

        Assert.Equal(expectedIgnored, actual);
    }

    [Fact]
    public void EvaluateProcessNames_CompanionDiscordOverlay_EnablesSafeSwapchainPacingWithoutFalseWarningBanner()
    {
        // protects: DXVK swapchain stability for Discord users (Discord auth is standard in Project Kongor) without spamming false yellow conflict warnings
        // fails_when: Discord fails to enable Safe Swapchain Pacing OR triggers HasActiveConflicts warning banner
        // seam: OverlayConflictDetectorService.EvaluateProcessNames
        OverlayConflictDetectorService detector = new OverlayConflictDetectorService();

        OverlayConflictReport report = detector.EvaluateProcessNames(
            new[] { "explorer", "Discord", "hon_x64" },
            hasVirtualUsbDisplays: false);

        Assert.False(report.HasActiveConflicts);
        Assert.True(report.ShouldApplySafeSwapchainPacing);
        Assert.Empty(report.ConflictingProcesses);
        Assert.NotNull(report.CompanionProcesses);
        Assert.Contains("Discord", report.CompanionProcesses!);
        Assert.Equal(string.Empty, report.WarningBannerText);
    }

    [Fact]
    public void EvaluateProcessNames_HighRiskScreenRecorder_RaisesConflictBannerAndSafeSwapchainPacing()
    {
        // protects: Detection of intrusive Vulkan/D3D9 swapchain recorders (Mirillis Action!, RTSS, Overwolf) that cause VK_ERROR_DEVICE_LOST
        // fails_when: High-risk screen recorders fail to set HasActiveConflicts or ShouldApplySafeSwapchainPacing
        // seam: OverlayConflictDetectorService.EvaluateProcessNames
        OverlayConflictDetectorService detector = new OverlayConflictDetectorService();

        OverlayConflictReport report = detector.EvaluateProcessNames(
            new[] { "Action_x64", "RTSSHooks64", "Discord" },
            hasVirtualUsbDisplays: true);

        Assert.True(report.HasActiveConflicts);
        Assert.True(report.ShouldApplySafeSwapchainPacing);
        Assert.Contains("Action_x64", report.ConflictingProcesses);
        Assert.Contains("RTSSHooks64", report.ConflictingProcesses);
        Assert.Contains("Action_x64", report.WarningBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportBundleAsync_ForcedNonZeroExitTelemetryWithoutMinidump_ProducesCallerSlugInsteadOfCrash0x0()
    {
        // protects: BUG-501-06 forced ExitProcess(0xFFFFFFFF) diagnostics so bundles identify the primary engine/render caller instead of generic crash_0x0
        // fails_when: ExportBundleAsync ignores [EXIT_TELEMETRY] in logs/crash/engine_crashes.log and emits Kongor_Diag_*_crash_0x0.zip
        // seam: Real DiagnosticBundleService.ExportBundleAsync + filesystem logs/crash/engine_crashes.log + zip CRASH_SUMMARY.md
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ForcedExitDiag_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
            string engineCrashLog = Path.Combine(crashDir, "engine_crashes.log");
            File.WriteAllText(
                engineCrashLog,
                "[EXIT_TELEMETRY] Non-zero ExitProcess(0xFFFFFFFF) PrimaryCaller=k2_x64.dll+0x12AB34 Stack=[ucrtbase.dll+0x4120 <- k2_x64.dll+0x12AB34 <- hon_x64.exe+0x1450] PID=29412\n");
            File.SetLastWriteTimeUtc(engineCrashLog, DateTime.UtcNow);

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                minDumpWriteTimeUtc: launchStartUtc,
                processExitCode: 0xFFFFFFFFu);

            Assert.True(result.Success);
            Assert.True(File.Exists(result.ZipPath));
            string fileName = Path.GetFileName(result.ZipPath);
            Assert.Contains("forced_exit_k2_x64.dll_0x12AB34", fileName, StringComparison.Ordinal);
            Assert.DoesNotContain("crash_0x0", fileName, StringComparison.OrdinalIgnoreCase);

            using ZipArchive archive = ZipFile.OpenRead(result.ZipPath);
            ZipArchiveEntry? summaryEntry = archive.GetEntry("CRASH_SUMMARY.md");
            Assert.NotNull(summaryEntry);
            using StreamReader reader = new StreamReader(summaryEntry!.Open());
            string summaryMarkdown = await reader.ReadToEndAsync();
            Assert.Contains("k2_x64.dll", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0x12AB34", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0xFFFFFFFF", summaryMarkdown, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
