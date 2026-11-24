// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ASPIRE.Common.Utilities;
using Kongor.Core.Diagnostics;
using Kongor.Core.Services.Native;
using Kongor.Modding.Core.Packaging;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Wave 5 Phase 21 (#260-#263) deterministic unit and negative-control test suite:
/// - #260: AMD64 0xFFFFFFFFFFFFFFFF #GPF Non-Canonical Register Detector &amp; Poison/Bit-Flip Classifier
/// - #261: Bounded 40-Qword Stack-Scan Preceding-CALL Opcode Validator (ret_addr - 1) &amp; C++ unit runner
/// - #262: Headless minidump-stackwalk JSON Trust-Grade Parser &amp; Breakpad .sym Resolver
/// - #263: Single-Owner Tracy Guard &amp; 50-Model K2 .model Orphan-Vertex + 32-Entry FIFO ACMR Empirical Gate
/// </summary>
public sealed class Wave5LeanForensicsAndAuditTests
{
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    [Fact]
    public void Item260_IsAmd64NonCanonicalAddress_ClassifiesCanonicalAndNonCanonicalBoundaries()
    {
        // Canonical low half (user space): bits 63..47 == 0
        Assert.False(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0x0000000000000000UL));
        Assert.False(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0x00007FF728AB1000UL));
        Assert.False(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0x00007FFFFFFFFFFFUL));

        // Canonical high half (kernel space): bits 63..47 == all 1s (0xFFFF800000000000..0xFFFFFFFFFFFFFFFF)
        Assert.False(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0xFFFF800000000000UL));
        Assert.False(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0xFFFFFFFFFFFFFFFFUL));

        // Non-canonical gap: bits 63..47 neither all 0s nor all 1s
        Assert.True(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0x0000800000000000UL));
        Assert.True(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0x0001000000001000UL));
        Assert.True(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0xDDDDDDDDDDDDDDDDUL));
        Assert.True(CrashTriageOrchestrator.IsAmd64NonCanonicalAddress(0xFEFEFEFEFEFEFEFEUL));
    }

    [Fact]
    public void Item260_AnalyzeAmd64GpfAndPoison_RecoversNonCanonicalRegisterAndRejectsMultiBitFlips()
    {
        Dictionary<string, ulong> gpfRegisters = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            ["RAX"] = 0x00007FF728AB1000UL,
            ["RCX"] = 0xDDDDDDDDDDDDDDDDUL,
            ["RSP"] = 0x000000E3599FC520UL,
            ["RIP"] = 0x00007FF728AB1234UL
        };

        CrashTriageOrchestrator.Amd64GpfAndPoisonAnalysis gpfResult = CrashTriageOrchestrator.AnalyzeAmd64GpfAndPoison(
            faultTargetAddress: 0xFFFFFFFFFFFFFFFFUL,
            gprs: gpfRegisters);

        Assert.True(gpfResult.IsGeneralProtectionFaultSentinel);
        Assert.False(gpfResult.IsUnalignedSseCandidate);
        Assert.Equal("RCX", gpfResult.NonCanonicalRegisterName);
        Assert.Equal(0xDDDDDDDDDDDDDDDDUL, gpfResult.NonCanonicalRegisterValue);
        Assert.Equal("CRT_FREED_HEAP_0xDD", gpfResult.PoisonPatternTag);

        // Negative control: all registers canonical, but RSP is misaligned by 8 bytes -> SSE unaligned fault
        Dictionary<string, ulong> sseRegisters = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            ["RAX"] = 0x00007FF728AB1000UL,
            ["RCX"] = 0x000002255B119200UL,
            ["RSP"] = 0x000000E3599FC528UL,
            ["RIP"] = 0x00007FF728AB1234UL
        };

        CrashTriageOrchestrator.Amd64GpfAndPoisonAnalysis sseResult = CrashTriageOrchestrator.AnalyzeAmd64GpfAndPoison(
            faultTargetAddress: 0xFFFFFFFFFFFFFFFFUL,
            gprs: sseRegisters);

        Assert.True(sseResult.IsGeneralProtectionFaultSentinel);
        Assert.Null(sseResult.NonCanonicalRegisterName);
        Assert.True(sseResult.IsUnalignedSseCandidate);

        // Single-bit flip in high canonical bits (bit 48 flipped: 0x0001000000001000) -> [heuristic-hint]
        bool classifiedSingleBit = CrashTriageOrchestrator.TryClassifyPoisonOrSingleBitFlip(
            0x0001000000001000UL,
            0UL,
            out string? bitFlipTag,
            out bool isBitFlip,
            out int bitIndex);
        Assert.True(classifiedSingleBit);
        Assert.True(isBitFlip);
        Assert.Equal(48, bitIndex);
        Assert.NotNull(bitFlipTag);
        Assert.Contains("[heuristic-hint]", bitFlipTag!, StringComparison.Ordinal);

        // Multi-bit corruption (bits 48 and 49 flipped: 0x0003000000001000) -> must NOT report single-bit flip
        bool classifiedMultiBit = CrashTriageOrchestrator.TryClassifyPoisonOrSingleBitFlip(
            0x0003000000001000UL,
            0UL,
            out string? _,
            out bool isMultiFlip,
            out int _);
        Assert.False(classifiedMultiBit);
        Assert.False(isMultiFlip);
    }

    [Fact]
    public void Item261_BoundedStackScanForCallSites_ValidatesCallOpcodesAndEnforces40QwordLimit()
    {
        // Construct a synthetic 0x1100-byte module image where .text is at RVA 0x1000..0x1040
        byte[] moduleImage = new byte[0x1100];
        // Offset 0x1005..0x100A: 5-byte direct CALL rel32 (E8 26 00 00 00) -> return address at RVA 0x100A
        moduleImage[0x1005] = 0xE8;
        moduleImage[0x1006] = 0x26;
        moduleImage[0x1007] = 0x00;
        moduleImage[0x1008] = 0x00;
        moduleImage[0x1009] = 0x00;

        // Offset 0x1010..0x1016: 6-byte RIP-relative indirect CALL (FF 15 10 00 00 00) -> return address at RVA 0x1016
        moduleImage[0x1010] = 0xFF;
        moduleImage[0x1011] = 0x15;
        moduleImage[0x1012] = 0x10;

        // Offset 0x1020..0x1022: 2-byte register indirect CALL RAX (FF D0) -> return address at RVA 0x1022
        moduleImage[0x1020] = 0xFF;
        moduleImage[0x1021] = 0xD0;

        // Negative control at 0x1028..0x102D: 5-byte JMP rel32 (E9 10 00 00 00) -> NOT a CALL!
        moduleImage[0x1028] = 0xE9;
        moduleImage[0x1029] = 0x10;

        // Negative control at 0x1030..0x1032: 2-byte JMP RAX (FF E0) -> NOT a CALL!
        moduleImage[0x1030] = 0xFF;
        moduleImage[0x1031] = 0xE0;

        ulong moduleBase = 0x00007FF700000000UL;
        uint textStartRva = 0x1000U;
        uint textEndRva = 0x1040U;

        // Build a 45-qword stack where slot 0 is E8 call, slot 1 is JMP (rejected), slot 2 is FF 15 call,
        // slot 3 is FF E0 jmp (rejected), slot 39 is FF D0 call (last valid slot in 40-qword bound),
        // and slot 42 is a valid E8 call beyond the 40-qword bound (must be ignored!).
        ulong[] stackQwords = new ulong[45];
        stackQwords[0] = moduleBase + 0x100AUL; // Valid E8 rel32
        stackQwords[1] = moduleBase + 0x102DUL; // Invalid E9 JMP
        stackQwords[2] = moduleBase + 0x1016UL; // Valid FF 15
        stackQwords[3] = moduleBase + 0x1032UL; // Invalid FF E0 JMP
        stackQwords[39] = moduleBase + 0x1022UL; // Valid FF D0 at slot 39 (40th qword)
        stackQwords[42] = moduleBase + 0x100AUL; // Beyond 40-qword ceiling -> must NOT be scanned

        List<CrashTriageOrchestrator.ValidatedStackCallSite> recovered =
            CrashTriageOrchestrator.ScanBoundedStackForCallSites(
                stackQwords: stackQwords,
                moduleBaseVa: moduleBase,
                textStartRva: textStartRva,
                textEndRva: textEndRva,
                moduleImageBytes: moduleImage);

        Assert.Equal(3, recovered.Count);
        Assert.Equal(0, recovered[0].StackQwordIndex);
        Assert.Equal("CALL_REL32_E8", recovered[0].CallOpcodeKind);
        Assert.Equal(0x1009U, recovered[0].CallSiteRva); // ret_addr - 1 invariant!

        Assert.Equal(2, recovered[1].StackQwordIndex);
        Assert.Equal("CALL_RIP_IND_FF15", recovered[1].CallOpcodeKind);
        Assert.Equal(0x1015U, recovered[1].CallSiteRva);

        Assert.Equal(39, recovered[2].StackQwordIndex);
        Assert.Equal("CALL_REG_IND_FFD0", recovered[2].CallOpcodeKind);
        Assert.Equal(0x1021U, recovered[2].CallSiteRva);
    }

    [Fact]
    public void Item261_NativeGuardCppUnitTestsExecutable_PassesAll50Assertions()
    {
        string repoRoot = FindRepoRoot();
        string defaultExePath = Path.Combine(repoRoot, "native", "proxies", "native_guard", "build", "Release", "native_guard_unit_tests.exe");
        string? resolvedExePath = File.Exists(defaultExePath)
            ? defaultExePath
            : NexusTestEnvironment.TryFindNativeBinary("native_guard_unit_tests.exe");
        if (resolvedExePath is null)
        {
            if (!NexusTestEnvironment.HasGameAssets || !OperatingSystem.IsWindows()) return;
            resolvedExePath = defaultExePath;
        }

        string exePath = resolvedExePath;
        Assert.True(File.Exists(exePath), $"Expected NativeGuard C++ unit test binary at {exePath}. Build native_guard_unit_tests first.");

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process? proc = Process.Start(psi);
        Assert.NotNull(proc);
        string stdout = proc!.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        bool exited = proc.WaitForExit(10000);
        Assert.True(exited, "native_guard_unit_tests.exe timed out. Rebuild with: cmake --build native/proxies/native_guard/build --config Release --target native_guard_unit_tests");
        Assert.True(proc.ExitCode == 0, $"native_guard_unit_tests.exe exited with {proc.ExitCode}. Run native/proxies/native_guard/build/Release/native_guard_unit_tests.exe directly.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");


        Assert.Contains("Failed=0", stdout, StringComparison.Ordinal);
        Match passSummaryMatch = Regex.Match(stdout, @"\[PASS\] All (\d+)/\1 NativeGuard C\+\+ unit assertions passed\.");
        Assert.True(passSummaryMatch.Success, $"Expected NativeGuard pass summary banner in stdout, got:\n{stdout}");
        Assert.True(int.Parse(passSummaryMatch.Groups[1].Value) >= 120, $"Expected at least 120 passing NativeGuard C++ assertions, got {passSummaryMatch.Groups[1].Value}.");
        Assert.Contains("[PASS] Hde64Validator_Refuses_CArchiveIsOpen_JeAtOffset8_WhenTrampolineRequested", stdout, StringComparison.Ordinal);
        Assert.Contains("[PASS] InstallManagedInlineHook64_AllowsFullReplacementWhenTrampolineNull", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Item262_MinidumpStackwalkJsonAndBreakpadSym_ParsesTrustGradesAndResolvesRvas()
    {
        string sampleJson = """
        {
          "status": "OK",
          "crash_info": {
            "type": "EXCEPTION_ACCESS_VIOLATION_READ",
            "address": "0xffffffffffffffff",
            "crashing_thread": 0
          },
          "crashing_thread": {
            "frames": [
              { "frame": 0, "module": "k2_x64.dll", "function": "CModelPanel::Frame", "module_offset": "0x951020", "trust": "context" },
              { "frame": 1, "module": "k2_x64.dll", "function": "CWidget::Frame", "module_offset": "0x842110", "trust": "cfi" },
              { "frame": 2, "module": "cgame_x64.dll", "function": "CInterface::Update", "module_offset": "0x1e5de0", "trust": "frame_pointer" },
              { "frame": 3, "module": "hon_x64.exe", "function": "MainLoop", "module_offset": "0x1420", "trust": "scan" }
            ]
          }
        }
        """;

        CrashTriageOrchestrator.MinidumpStackwalkResult parsed = CrashTriageOrchestrator.ParseMinidumpStackwalkJson(sampleJson);
        Assert.True(parsed.Success);
        Assert.False(parsed.UsedCdbFallback);
        Assert.Equal(4, parsed.Frames.Count);
        Assert.Equal(CrashTriageOrchestrator.StackFrameTrustGrade.Context, parsed.Frames[0].TrustGrade);
        Assert.Equal(CrashTriageOrchestrator.StackFrameTrustGrade.Cfi, parsed.Frames[1].TrustGrade);
        Assert.Equal(CrashTriageOrchestrator.StackFrameTrustGrade.FramePointer, parsed.Frames[2].TrustGrade);
        Assert.Equal(CrashTriageOrchestrator.StackFrameTrustGrade.Scan, parsed.Frames[3].TrustGrade);

        // Negative control: corrupt JSON triggers clean WinDbg CDB fallback without throwing
        CrashTriageOrchestrator.MinidumpStackwalkResult fallback = CrashTriageOrchestrator.ParseMinidumpStackwalkJson("{not_valid_json");
        Assert.False(fallback.Success);
        Assert.True(fallback.UsedCdbFallback);

        // Breakpad .sym text parsing and RVA lookup
        string symText = string.Join("\n", new string[]
        {
            "MODULE windows x86_64 11223344556677889900AABBCCDDEEFF1 k2_x64.pdb",
            "FUNC 951000 80 0 CModelPanel::Frame(unsigned int)",
            "PUBLIC 952000 0 CModelPanel_StaticEntry"
        });

        CrashTriageOrchestrator.BreakpadSymModuleRecord symMod = CrashTriageOrchestrator.ParseBreakpadSymText(symText);
        Assert.Equal("windows", symMod.OperatingSystem);
        Assert.Equal("x86_64", symMod.CpuArchitecture);
        Assert.Single(symMod.Functions);

        string? resolved = CrashTriageOrchestrator.ResolveSymbolFromBreakpadSym(symMod, 0x951028U);
        Assert.NotNull(resolved);
        Assert.Contains("CModelPanel::Frame(unsigned int)", resolved!, StringComparison.Ordinal);
    }

    [Fact]
    public void Item263_K2ModelOrphanVertexAndAcmrAuditor_DetectsOrphansAndAudits50RealArchiveModels()
    {
        // 1. Synthetic mesh with 10 declared vertices, 2 triangles referencing only vertices 0..4 (vertices 5..9 are orphans)
        int[] indices = [0, 1, 2, 2, 3, 4];

        K2ModelMeshIntegrityAuditor.MeshVertexCacheAndOrphanMetrics syntheticResult =
            K2ModelMeshIntegrityAuditor.ComputeMeshOrphanVerticesAndAcmr(10, indices);
        Assert.Equal(10, syntheticResult.VertexCount);
        Assert.Equal(5, syntheticResult.ReferencedVertexCount);
        Assert.Equal(5, syntheticResult.OrphanedVertexCount);
        Assert.Equal(0.5f, syntheticResult.OrphanedVertexRatio, 3);
        Assert.Equal(2.5f, syntheticResult.Acmr, 3); // 5 cache misses across 2 triangles = 2.5 ACMR

        // 2. Empirical 50-model scan on games/client/game/resources0.s2z
        string repoRoot = FindRepoRoot();
        string resources0Path = Path.Combine(repoRoot, "games", "client", "game", "resources0.s2z");
        if (File.Exists(resources0Path))
        {
            K2ModelMeshIntegrityAuditor.ArchiveModelEmpiricalAuditSummary summary =
                K2ModelMeshIntegrityAuditor.ScanArchiveModelsEmpiricalSample(
                    resources0Path,
                    maxModels: 50,
                    distinctHeroFoldersOnly: true);

            Assert.Equal(50, summary.ModelsScanned);
            Assert.Equal(50, summary.DistinctHeroFoldersScanned);
            Assert.True(summary.TotalMeshesScanned >= 50);
            Assert.True(summary.SkippedMalformedEntries >= 0);
            Assert.True(summary.AverageAcmr >= 0.5f && summary.AverageAcmr <= 3.0f);

            string evidencePath = Path.Combine(repoRoot, "artifacts", "telemetry", "wave5_k2_model_50_empirical_audit.json");
            string rawArchiveToken = JsonEncodedText.Encode(resources0Path).ToString();
            string json = JsonSerializer.Serialize(summary, s_indentedJsonOptions)
                .Replace(rawArchiveToken, "games/client/game/resources0.s2z", StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(evidencePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
                File.WriteAllText(evidencePath, json);
            }
            Assert.DoesNotContain(rawArchiveToken, json, StringComparison.OrdinalIgnoreCase);
        }

        // 3. (#265) Verify no-op KONGOR_ENABLE_TRACY / KONGOR_TRACY_ZONE_SCOPED scaffolding and THIRD_PARTY_NOTICES Tracy row are purged
        string cmakePath = Path.Combine(repoRoot, "native", "proxies", "native_guard", "CMakeLists.txt");
        if (File.Exists(cmakePath))
        {
            string cmakeText = File.ReadAllText(cmakePath);
            Assert.DoesNotContain("KONGOR_ENABLE_TRACY", cmakeText, StringComparison.Ordinal);
        }

        string baseCmdHeaderPath = Path.Combine(repoRoot, "native", "proxies", "native_guard", "kongor_base_cmd.h");
        if (File.Exists(baseCmdHeaderPath))
        {
            string headerText = File.ReadAllText(baseCmdHeaderPath);
            Assert.DoesNotContain("KONGOR_TRACY_ZONE_SCOPED", headerText, StringComparison.Ordinal);
        }

        string noticesPath = Path.Combine(repoRoot, "THIRD_PARTY_NOTICES.md");
        if (File.Exists(noticesPath))
        {
            string noticesText = File.ReadAllText(noticesPath);
            Assert.DoesNotContain("wolfpld/tracy", noticesText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Item266_FileLockDetector_DetectsCurrentProcessLock_And_S2zArchiveWriter_SurfacesLockingPid()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_lock_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string lockedS2zPath = Path.Combine(tempDir, "locked_resources999.s2z");
        try
        {
            await File.WriteAllBytesAsync(lockedS2zPath, new byte[] { 0x50, 0x4B, 0x05, 0x06 });

            using FileStream exclusiveLock = new FileStream(
                lockedS2zPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            List<FileLockingProcess> lockers = FileLockDetector.GetLockingProcesses(lockedS2zPath);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains(lockers, p => p.ProcessID == Environment.ProcessId);
            }

            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ui/test.xml"] = "<interface/>"u8.ToArray()
            };

            IOException ex = await Assert.ThrowsAsync<IOException>(() =>
                S2zArchiveWriter.WriteArchiveAsync(lockedS2zPath, files, CompressionLevel.Fastest));

            Assert.Contains("locking process(es) detected", ex.Message, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains($"PID {Environment.ProcessId}", ex.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Wave5LeanForensicsAndAuditTests] Temp cleanup warning: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void Item267_And_Item259_LiveCanaryReceipt_VerifiesRealTelemetryNormalModeAndLiveSentinelDrill()
    {
        string repoRoot = FindRepoRoot();
        if (!NexusTestEnvironment.HasGameAssets) return;
        string scriptPath = Path.Combine(repoRoot, "scripts", "qa", "Invoke-KongorLiveCanary.ps1");
        Assert.True(File.Exists(scriptPath));
        string scriptText = File.ReadAllText(scriptPath);
        Assert.DoesNotContain("6.85 + (($sec % 5)", scriptText, StringComparison.Ordinal);
        Assert.Contains("Local\\KongorClientTelemetry_", scriptText, StringComparison.Ordinal);

        if (!NexusTestEnvironment.HasGameAssets) return;

        string receiptPath = Path.Combine(repoRoot, "artifacts", "telemetry", "wave4_live_canary_receipt.json");
        Assert.True(File.Exists(receiptPath));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(receiptPath));
        JsonElement root = doc.RootElement;

        Assert.StartsWith("Local\\KongorClientTelemetry_", root.GetProperty("telemetry_source").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("distinct_host_frames").GetInt32() >= 50);
        Assert.True(root.GetProperty("host_frames_delta").GetUInt64() >= 1000UL);
        Assert.True(root.GetProperty("normal_mode_verified").GetBoolean());
        Assert.Contains("\"safeMode\":false", root.GetProperty("last_pipe_response").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("live_sentinel_drill_verified").GetBoolean());
        Assert.Contains("\"safeMode\":true", root.GetProperty("live_sentinel_trip_response").GetString(), StringComparison.Ordinal);
        Assert.Contains("LAST_CRASHED_SENTINEL", root.GetProperty("live_sentinel_trip_response").GetString(), StringComparison.Ordinal);
        Assert.Contains("\"safeMode\":false", root.GetProperty("live_sentinel_recover_response").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("native_guard_cpp_tests").GetInt32() >= 66);
        Assert.True(root.GetProperty("crash_sentinel_verified").GetBoolean());
    }

    [Fact]
    public void Item269_ThresholdGatedMeasurements_Verifies50DistinctHeroFoldersAndLiveReceipts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = FindRepoRoot();
        string modelAuditPath = Path.Combine(repoRoot, "artifacts", "telemetry", "wave5_k2_model_50_empirical_audit.json");
        Assert.True(File.Exists(modelAuditPath));
        using (JsonDocument modelDoc = JsonDocument.Parse(File.ReadAllText(modelAuditPath)))
        {
            JsonElement modelRoot = modelDoc.RootElement;
            Assert.Equal(50, modelRoot.GetProperty("ModelsScanned").GetInt32());
            Assert.Equal(50, modelRoot.GetProperty("DistinctHeroFoldersScanned").GetInt32());

            HashSet<string> heroFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement sample in modelRoot.GetProperty("SampledModels").EnumerateArray())
            {
                string entryPath = sample.GetProperty("EntryPath").GetString() ?? string.Empty;
                Assert.Contains("heroes/", entryPath, StringComparison.OrdinalIgnoreCase);
                int idx = entryPath.IndexOf("heroes/", StringComparison.OrdinalIgnoreCase);
                int start = idx + "heroes/".Length;
                int slash = entryPath.IndexOf('/', start);
                Assert.True(slash > start);
                heroFolders.Add(entryPath.Substring(start, slash - start));
            }
            Assert.Equal(50, heroFolders.Count);
        }

        string threshPath = Path.Combine(repoRoot, "artifacts", "telemetry", "wave5_threshold_gated_measurements.json");
        Assert.True(File.Exists(threshPath));
        using (JsonDocument threshDoc = JsonDocument.Parse(File.ReadAllText(threshPath)))
        {
            JsonElement threshRoot = threshDoc.RootElement;
            Assert.Equal("kongor.wave5.threshold_gated_measurements.v1", threshRoot.GetProperty("schema").GetString());
            Assert.StartsWith("Local\\KongorClientTelemetry_", threshRoot.GetProperty("telemetry_source").GetString(), StringComparison.Ordinal);

            JsonElement luaAudit = threshRoot.GetProperty("lua_reload_10x_audit");
            Assert.Equal(10, luaAudit.GetProperty("iterations").GetInt32());
            Assert.Equal(11, luaAudit.GetProperty("lua_memory_samples_kb").GetArrayLength());

            JsonElement qosAb = threshRoot.GetProperty("window_focus_qos_ab");
            Assert.Equal(10, qosAb.GetProperty("focused_samples").GetInt32());
            Assert.Equal(10, qosAb.GetProperty("minimized_samples").GetInt32());
            Assert.True(qosAb.GetProperty("focused_host_frames_delta").GetUInt64() > 0UL);
            Assert.True(qosAb.GetProperty("minimized_host_frames_delta").GetUInt64() > 0UL);
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AGENTS.md")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
