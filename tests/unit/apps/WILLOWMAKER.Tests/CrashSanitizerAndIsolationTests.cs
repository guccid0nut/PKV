// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kongor.Core.Diagnostics;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class CrashSanitizerAndIsolationTests
{
    private static string CreateTempRoot(string suffix)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"KongorH1b_{suffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryCleanupTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TEST_CLEANUP] Ignored cleanup warning for '{root}': {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a valid minimal Windows MDMP buffer where SystemInfoStream.CSDVersionRva (0x11A)
    /// has origByteLen == 0 and immediately abuts ThreadListStream (0x11E).
    /// Before the Issue 7A +2B fix, clearing CSDVersionRva overwrote ThreadListStream.NumberOfThreads to 0.
    /// </summary>
    private static byte[] BuildSyntheticMdmpWithEmptyCsdAbuttingThreadList(uint threadId = 4242)
    {
        const uint sysInfoRva = 0x5C;
        const uint csdStringRva = 0x11A;
        const uint threadListRva = 0x11E;
        const uint threadListSize = 4 + 48; // NumberOfThreads (4) + 1 MINIDUMP_THREAD (48) = 0x34 -> ends at 0x152
        const uint moduleNameRva = 0x94;    // 0x94..0xCD (< 0x11A)
        const uint moduleListRva = 0x152;
        const uint moduleListSize = 4 + 108; // NumberOfModules (4) + 1 MINIDUMP_MODULE (108) = 0x70 -> ends at 0x1C2
        const uint exceptionStreamRva = 0x1C2;
        const uint exceptionStreamSize = 168; // -> ends at 0x26A
        const uint stackDataRva = 0x280;
        const uint stackDataSize = 64;
        const uint memoryListRva = 0x2C0;
        const uint memoryListSize = 4 + 16;   // NumberOfMemoryRanges (4) + 1 MINIDUMP_MEMORY_DESCRIPTOR (16) = 20

        byte[] buffer = new byte[memoryListRva + memoryListSize];
        Span<byte> s = buffer;

        // MINIDUMP_HEADER (0x00..0x1F)
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x00..], 0x504D444Du); // "MDMP"
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x04..], 0x0000A793u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x08..], 5u);          // NumberOfStreams
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x0C..], 0x20u);       // StreamDirectoryRva
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], 1700000000u); // TimeDateStamp

        // Stream Directory Entries (0x20..0x5B)
        // [0] SystemInfoStream (7)
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], 7u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], 56u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x28..], sysInfoRva);
        // [1] ThreadListStream (3) at 0x11E immediately after 0x11A..0x11D empty CSD string header
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x2C..], 3u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x30..], threadListSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x34..], threadListRva);
        // [2] ModuleListStream (4)
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x38..], 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x3C..], moduleListSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x40..], moduleListRva);
        // [3] ExceptionStream (6)
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x44..], 6u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x48..], exceptionStreamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x4C..], exceptionStreamRva);
        // [4] MemoryListStream (5)
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x50..], 5u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x54..], memoryListSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x58..], memoryListRva);

        // SystemInfoStream (0x5C..0x93)
        BinaryPrimitives.WriteUInt16LittleEndian(s[(int)sysInfoRva..], 9);            // AMD64
        s[(int)sysInfoRva + 6] = 8;                                                   // NumberOfProcessors
        s[(int)sysInfoRva + 7] = 1;                                                   // Workstation
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)sysInfoRva + 8)..], 10u);    // MajorVersion
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)sysInfoRva + 12)..], 0u);    // MinorVersion
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)sysInfoRva + 16)..], 22631u);// BuildNumber
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)sysInfoRva + 20)..], 2u);    // PlatformId
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)sysInfoRva + 24)..], csdStringRva);// CSDVersionRva = 0x11A

        // Module name MINIDUMP_STRING at 0x94 ("C:\Users\Alice\hon_x64.exe" -> 26 chars = 52 bytes)
        const string rawModulePath = @"C:\Users\Alice\hon_x64.exe";
        byte[] modNameBytes = Encoding.Unicode.GetBytes(rawModulePath);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(int)moduleNameRva..], (uint)modNameBytes.Length);
        modNameBytes.CopyTo(s[((int)moduleNameRva + 4)..]);

        // Empty CSDVersion MINIDUMP_STRING at 0x11A: Length = 0 (4 bytes: 0x11A..0x11D)
        BinaryPrimitives.WriteUInt32LittleEndian(s[(int)csdStringRva..], 0u);

        // ThreadListStream at 0x11E: NumberOfThreads = 1 immediately abutting CSDVersionRva
        BinaryPrimitives.WriteUInt32LittleEndian(s[(int)threadListRva..], 1u);
        int t0 = (int)threadListRva + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(s[t0..], threadId);       // ThreadId
        BinaryPrimitives.WriteUInt64LittleEndian(s[(t0 + 24)..], 0x0000001000000000UL); // Stack.StartOfMemoryRange
        BinaryPrimitives.WriteUInt32LittleEndian(s[(t0 + 32)..], stackDataSize);        // Stack.DataSize
        BinaryPrimitives.WriteUInt32LittleEndian(s[(t0 + 36)..], stackDataRva);         // Stack.Rva

        // ModuleListStream at 0x152
        BinaryPrimitives.WriteUInt32LittleEndian(s[(int)moduleListRva..], 1u);
        int m0 = (int)moduleListRva + 4;
        BinaryPrimitives.WriteUInt64LittleEndian(s[m0..], 0x0000000140000000UL); // BaseOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(s[(m0 + 8)..], 0x00100000u);    // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(s[(m0 + 20)..], moduleNameRva); // ModuleNameRva

        // ExceptionStream at 0x1C2
        BinaryPrimitives.WriteUInt32LittleEndian(s[(int)exceptionStreamRva..], threadId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)exceptionStreamRva + 8)..], 0xC0000005u);
        BinaryPrimitives.WriteUInt64LittleEndian(s[((int)exceptionStreamRva + 24)..], 0x0000000140001234UL);

        // MemoryListStream at 0x2C0
        BinaryPrimitives.WriteUInt32LittleEndian(s[(int)memoryListRva..], 1u);
        BinaryPrimitives.WriteUInt64LittleEndian(s[((int)memoryListRva + 4)..], 0x0000001000000000UL);
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)memoryListRva + 12)..], stackDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[((int)memoryListRva + 16)..], stackDataRva);

        return buffer;
    }

    [Fact]
    public void ConfigWriter_StripsClanTagBeforeWritingLoginNameCvar()
    {
        string tempRoot = CreateTempRoot("ConfigStripClan");
        try
        {
            var settings = new LauncherSettingsModel
            {
                LastPlayerNickname = "[GUCI]guccidonut"
            };
            LauncherSettingsService.Save(settings, tempRoot);

            var vm = new MainViewModel();
            try
            {
                vm.WriteCustomConfiguration(tempRoot);
            }
            finally
            {
                vm.DisposeApplicationBackgroundServices();
            }

            string[] cfgFiles = Directory.GetFiles(tempRoot, "*.cfg", SearchOption.AllDirectories);
            Assert.NotEmpty(cfgFiles);

            bool foundSanitizedLoginName = false;
            foreach (string cfg in cfgFiles)
            {
                string content = File.ReadAllText(cfg);
                Assert.DoesNotContain("[GUCI]", content, StringComparison.OrdinalIgnoreCase);
                if (content.Contains("SetSave \"login_name\" \"guccidonut\"", StringComparison.Ordinal))
                {
                    foundSanitizedLoginName = true;
                }
            }

            Assert.True(foundSanitizedLoginName, "Expected WriteCustomConfiguration to write SetSave \"login_name\" \"guccidonut\" with clan tag stripped.");
        }
        finally
        {
            TryCleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void MinidumpPrivacySanitizer_EmptyCsdVersionRva_DoesNotCorruptAdjacentThreadListCount()
    {
        byte[] buffer = BuildSyntheticMdmpWithEmptyCsdAbuttingThreadList(threadId: 4242);
        string tempRoot = CreateTempRoot("SanitizerCsd");
        try
        {
            uint preThreadCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x11E, 4));
            uint preModuleCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x152, 4));
            Assert.Equal(1u, preThreadCount);
            Assert.Equal(1u, preModuleCount);

            MinidumpSanitizeResult result = MinidumpPrivacySanitizer.SanitizeInPlace(
                buffer,
                new PrivacyScrubContext(clientRoot: tempRoot, outputDir: tempRoot, faultingModuleName: "hon_x64.exe"),
                extraSensitiveValues: new[] { "Alice" });

            Assert.True(result.AllowedForExport, $"Expected sanitization to succeed, got {result.StatusCode}: {result.Reason}");
            uint postThreadCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x11E, 4));
            uint postModuleCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x152, 4));
            Assert.Equal(1u, postThreadCount);
            Assert.Equal(1u, postModuleCount);

            // Verify WinDbg/MinidumpReader can still inspect fault module from sanitized buffer and Alice PII was wiped
            string sanitizedDmp = Path.Combine(tempRoot, "sanitized.dmp");
            File.WriteAllBytes(sanitizedDmp, buffer);
            MinidumpTriageResult triage = MinidumpReader.Inspect(sanitizedDmp);
            Assert.True(triage.IsValidDump, $"Sanitized MDMP failed MinidumpReader.Inspect: {triage.Status}");
            Assert.Contains("hon_x64.exe", triage.FaultingModule, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0x1234", triage.FaultingRva, StringComparison.OrdinalIgnoreCase);
            string sanitizedUtf16Text = Encoding.Unicode.GetString(buffer);
            Assert.DoesNotContain("Alice", sanitizedUtf16Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryCleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ColdBootSentinel_IsolatesStaleDumpAndCrossPidTelemetry_ClassifiesUngracefulNoDumpAndMatchesSamePid()
    {
        string tempRoot = CreateTempRoot("ColdBootIsolation");
        string outDir = Path.Combine(tempRoot, "exports");
        Directory.CreateDirectory(outDir);

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // 1. Place a 4-day-old stale .dmp file that must NEVER be attached to today's orphan crash
            string staleDumpDir = Path.Combine(tempRoot, "userdata", "crash_dumps");
            Directory.CreateDirectory(staleDumpDir);
            string staleDumpPath = Path.Combine(staleDumpDir, "stale_4days.dmp");
            File.WriteAllBytes(staleDumpPath, BuildSyntheticMdmpWithEmptyCsdAbuttingThreadList(threadId: 1111));
            File.SetLastWriteTimeUtc(staleDumpPath, now.AddDays(-4).UtcDateTime);

            // 2. Place today's unacked orphan PID 4242 sentinel
            string crashDir = Path.Combine(tempRoot, "logs", "crash");
            Directory.CreateDirectory(crashDir);
            string activeSentinel = Path.Combine(crashDir, "session-active.4242.sentinel");
            File.WriteAllText(activeSentinel, $"PID=4242\nStartedUtc={now:O}\n");
            File.SetLastWriteTimeUtc(activeSentinel, now.UtcDateTime);

            // 3. Put a legacy untimestamped telemetry line AND another PID's (9999) telemetry line in engine_crashes.log
            string runtimeLog = Path.Combine(crashDir, "engine_crashes.log");
            File.WriteAllLines(runtimeLog, new[]
            {
                "[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) PrimaryCaller=legacy_untimestamped.dll+0x1234",
                $"[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC={now:O} PrimaryCaller=other_pid_fault.dll+0x9999 PID=9999"
            });

            var vm = new MainViewModel();
            try
            {
                vm.CheckColdBootCrashSentinel(tempRoot);
                Assert.True(vm.HasCrashReportBanner);
                Assert.Null(vm.LastActiveCrashDumpPath);

                DiagnosticBundleResult? unattributed = await vm.ExportDiagnosticBundleForRootAsync(
                    clientRootOverride: tempRoot,
                    outputDirectoryOverride: outDir,
                    revealInFileManager: false);

                Assert.NotNull(unattributed);
                string unattributedZipName = Path.GetFileName(unattributed!.ZipPath);
                Assert.Contains("ungraceful_exit_no_dump", unattributedZipName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("legacy_untimestamped", unattributedZipName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("other_pid_fault", unattributedZipName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("hon_x64.exe", unattributedZipName, StringComparison.OrdinalIgnoreCase);

                using (ZipArchive zip = ZipFile.OpenRead(unattributed.ZipPath))
                {
                    Assert.DoesNotContain(zip.Entries, e => e.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                    ZipArchiveEntry? summaryEntry = zip.GetEntry("CRASH_SUMMARY.md");
                    Assert.NotNull(summaryEntry);
                    using var reader = new StreamReader(summaryEntry!.Open());
                    string summary = await reader.ReadToEndAsync();
                    Assert.Contains("UNGRACEFUL_EXIT_NO_DUMP", summary, StringComparison.Ordinal);
                    Assert.DoesNotContain("other_pid_fault.dll", summary, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("legacy_untimestamped.dll", summary, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                vm.DisposeApplicationBackgroundServices();
            }

            // 4. Now reset acknowledgment & append matching PID=4242 telemetry with valid UTC timestamp -> must match PID 4242!
            var settings = LauncherSettingsService.Load(tempRoot);
            settings.LastAcknowledgedCrashUtc = string.Empty;
            LauncherSettingsService.Save(settings, tempRoot);

            File.AppendAllLines(runtimeLog, new[]
            {
                $"[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC={now.AddSeconds(1):O} PrimaryCaller=cgame_x64.dll+0x4567 PID=4242"
            });

            var vmMatched = new MainViewModel();
            try
            {
                vmMatched.CheckColdBootCrashSentinel(tempRoot);
                Assert.True(vmMatched.HasCrashReportBanner);

                DiagnosticBundleResult? matched = await vmMatched.ExportDiagnosticBundleForRootAsync(
                    clientRootOverride: tempRoot,
                    outputDirectoryOverride: outDir,
                    revealInFileManager: false);

                Assert.NotNull(matched);
                string matchedZipName = Path.GetFileName(matched!.ZipPath);
                Assert.Contains("forced_exit_cgame_x64.dll_0x4567", matchedZipName, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                vmMatched.DisposeApplicationBackgroundServices();
            }
        }
        finally
        {
            TryCleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ForcedExitTelemetry_UnwrapsCSystemExitAndErrorWrappers_AndExtractsConsoleFatalLine()
    {
        string tempRoot = CreateTempRoot("WrapperUnwrap");
        string outDir = Path.Combine(tempRoot, "exports");
        Directory.CreateDirectory(outDir);

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string crashDir = Path.Combine(tempRoot, "logs", "crash");
            Directory.CreateDirectory(crashDir);
            string userKongorDir = Path.Combine(tempRoot, "userdata", "Heroes of Newerth x64", "game");
            Directory.CreateDirectory(userKongorDir);

            const string telemetryLine =
                "[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC=2026-11-18T12:00:00Z PrimaryCaller=k2_x64.dll+0x9FE30B Stack=[version.dll+0x1234 <- k2_x64.dll+0x9FE30B <- k2_x64.dll+0xA0542D <- vid_d3d9_x64.dll+0x5D7B7 <- hon_x64.exe+0x1450] PID=4242";

            Assert.True(DiagnosticBundleService.TryParseExitTelemetryLine(
                telemetryLine,
                out string parsedExitCode,
                out string parsedModule,
                out string parsedRva,
                out _,
                out DateTimeOffset? parsedUtc,
                out int? parsedPid));
            Assert.Equal("0x00000001", parsedExitCode);
            Assert.Equal("vid_d3d9_x64.dll", parsedModule);
            Assert.Equal("0x5D7B7", parsedRva);
            Assert.Equal(4242, parsedPid);
            Assert.NotNull(parsedUtc);

            // Standalone CSystem::Error range must also unwrap to real caller
            const string errorWrapperLine =
                "[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC=2026-11-18T12:00:01Z PrimaryCaller=k2_x64.dll+0xA0542D Stack=[k2_x64.dll+0xA0542D <- vid_d3d9_x64.dll+0x5D7B7] PID=4242";
            Assert.True(DiagnosticBundleService.TryParseExitTelemetryLine(
                errorWrapperLine,
                out _,
                out string errUnwrappedModule,
                out string errUnwrappedRva,
                out _,
                out _,
                out _));
            Assert.Equal("vid_d3d9_x64.dll", errUnwrappedModule);
            Assert.Equal("0x5D7B7", errUnwrappedRva);

            File.WriteAllLines(Path.Combine(crashDir, "engine_crashes.log"), new[]
            {
                $"[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC={now:O} PrimaryCaller=k2_x64.dll+0x9FE30B Stack=[version.dll+0x1234 <- k2_x64.dll+0x9FE30B <- k2_x64.dll+0xA0542D <- vid_d3d9_x64.dll+0x5D7B7 <- hon_x64.exe+0x1450] PID=4242"
            });

            string consoleLogPath = Path.Combine(userKongorDir, "console.log");
            File.WriteAllLines(consoleLogPath, new[]
            {
                "Info: Initializing renderer...",
                "Error: D3D_Start: CreateWindow() failed"
            });

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: tempRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                minDumpWriteTimeUtc: now.AddSeconds(-5),
                processExitCode: 1u,
                expectedProcessId: 4242);

            string zipName = Path.GetFileName(result.ZipPath);
            Assert.Contains("forced_exit_vid_d3d9_x64.dll_0x5D7B7", zipName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("k2_x64.dll_0x9FE30B", zipName, StringComparison.OrdinalIgnoreCase);

            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            ZipArchiveEntry? summaryEntry = zip.GetEntry("CRASH_SUMMARY.md");
            Assert.NotNull(summaryEntry);
            using var reader = new StreamReader(summaryEntry!.Open());
            string summary = await reader.ReadToEndAsync();
            Assert.Contains("vid_d3d9_x64.dll+0x5D7B7", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Error: D3D_Start: CreateWindow() failed", summary, StringComparison.Ordinal);
            Assert.Contains("CreateWindow()", summary, StringComparison.Ordinal);
        }
        finally
        {
            TryCleanupTempRoot(tempRoot);
        }
    }
}
