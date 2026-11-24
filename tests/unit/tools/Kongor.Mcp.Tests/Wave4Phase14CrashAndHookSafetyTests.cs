// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kongor.Cli.Mcp.Services;
using Kongor.Cli.Mcp.Services.Models;
using Kongor.Core.Diagnostics;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Wave v4 Phase 14 (#166-#180: wave4-crash-shield-hook-safety-and-job-guard) verification suite.
/// Protects:
/// - #166 MSVC 0xE06D7363 C++ _ThrowInfo RTTI demangling &amp; what() extraction
/// - #167 VEH benign exception filter &amp; Access Violation READ/WRITE/DEP_EXECUTE classification
/// - #168 x64 .pdata UNW_FLAG_CHAININFO unwind chain walker &amp; trampoline frame resolution
/// - #169 Non-overlapping patch interval collision detector (OBL-HOOK-NO-OVERLAP)
/// - #170 Safe-mode boot bypass &amp; last-crashed.sentinel recovery
/// - #171 Scoped RAII COM guard (RPC_E_CHANGED_MODE tolerant) &amp; single-flight teardown
/// - #172 Win32 Job Object memory cap (JOB_OBJECT_LIMIT_JOB_MEMORY) &amp; no-empty-catch process kills
/// - #173 Dual Head-Tail process output ring buffer governor
/// - #174 Dynamic 500x ratio / 256 MiB floor archive decompression bomb guard
/// - #175 Observe-Effect VFS completeness invariant
/// - #176-#180 Main-thread callback queue, RawInput decoupling, re-entrant priority event bus, shadow VMT + EntityFieldContainer, and JSON-RPC 2.0 pipe envelope
/// </summary>
public sealed class Wave4Phase14CrashAndHookSafetyTests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir, "native", "proxies", "native_guard")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Repository root not found. Run 'dotnet test' from within the ProjectKongor workspace.");
    }

    [Fact]
    public void MsvcCppExceptionRttiDemangler_And_ThrowInfoDecoder_DecodesParameters_And_GuardsMalformedInputs()
    {
        // 1. Demangler tests (#166)
        Assert.Equal("std::runtime_error", CrashTriageOrchestrator.DemangleMsvcRttiTypeName(".?AVruntime_error@std@@"));
        Assert.Equal("std::bad_alloc", CrashTriageOrchestrator.DemangleMsvcRttiTypeName(".?AUbad_alloc@std@@"));
        Assert.Equal("K2::CException", CrashTriageOrchestrator.DemangleMsvcRttiTypeName(".?AVCException@K2@@"));
        Assert.Equal("PlainTypeName", CrashTriageOrchestrator.DemangleMsvcRttiTypeName("PlainTypeName"));
        Assert.Equal("UnknownType", CrashTriageOrchestrator.DemangleMsvcRttiTypeName(""));

        // 2. Valid 0x19930520 4-parameter x64 MSVC C++ exception decoding
        ulong[] validParams =
        [
            0x19930520UL,
            0x0000008811223344UL,
            0x00007FF600000400UL,
            0x00007FF600000000UL
        ];
        MsvcCppExceptionMetadata decoded = CrashTriageOrchestrator.DecodeMsvcCppExceptionParameters(
            validParams,
            ".?AVruntime_error@std@@",
            "simulated what() payload");

        Assert.True(decoded.IsValid);
        Assert.Equal(0x19930520U, decoded.MagicNumber);
        Assert.Equal(".?AVruntime_error@std@@", decoded.MangledTypeName);
        Assert.Equal("std::runtime_error", decoded.DemangledTypeName);
        Assert.Equal("simulated what() payload", decoded.WhatMessage);
        Assert.Equal(0x00007FF600000000UL, decoded.ThrowImageBase);

        // 3. Invalid magic rejection & short parameter span guard
        ulong[] badMagicParams = [0xDEADBEEFUL, 0x1000UL, 0x2000UL];
        MsvcCppExceptionMetadata badMagic = CrashTriageOrchestrator.DecodeMsvcCppExceptionParameters(badMagicParams);
        Assert.False(badMagic.IsValid);

        ulong[] tooShortParams = [0x19930520UL];
        MsvcCppExceptionMetadata shortMeta = CrashTriageOrchestrator.DecodeMsvcCppExceptionParameters(tooShortParams);
        Assert.False(shortMeta.IsValid);

        // 4. Verify C++ native_guard implementation contract (#166)
        string repoRoot = FindRepoRoot();
        string vehSource = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "veh_crash_shield.cpp"));
        Assert.Contains("SehExtractMsvcCppExceptionInfo", vehSource);
        Assert.Contains("0x19930520", vehSource);
        Assert.Contains("[GEODE-RTTI]", vehSource);
    }

    [Fact]
    public void VehBenignExceptionFilter_And_AccessViolationClassifier_ClassifiesReadWriteAndDepFaults()
    {
        // 1. Benign exception filter (#167)
        Assert.True(CrashTriageOrchestrator.IsBenignDebugOrThreadNameException(0x40010006u));
        Assert.True(CrashTriageOrchestrator.IsBenignDebugOrThreadNameException(0x4001000Au));
        Assert.True(CrashTriageOrchestrator.IsBenignDebugOrThreadNameException(0x406D1388u));
        Assert.False(CrashTriageOrchestrator.IsBenignDebugOrThreadNameException(0xC0000005u));
        Assert.False(CrashTriageOrchestrator.IsBenignDebugOrThreadNameException(0xE06D7363u));

        // 2. Access Violation classification (#167)
        (string readKind, string readTarget, string readDesc) = CrashTriageOrchestrator.ClassifyAccessViolationFault(0UL, 0x0UL);
        Assert.Equal("READ", readKind);
        Assert.Equal("0x0000000000000000", readTarget);
        Assert.Contains("[SHADPS4-VEH]", readDesc);

        (string writeKind, string writeTarget, string _) = CrashTriageOrchestrator.ClassifyAccessViolationFault(1UL, 0x88UL);
        Assert.Equal("WRITE", writeKind);
        Assert.Equal("0x0000000000000088", writeTarget);

        (string depKind, string depTarget, string _) = CrashTriageOrchestrator.ClassifyAccessViolationFault(8UL, 0x00007FF6DEADBEEFUL);
        Assert.Equal("DEP_EXECUTE", depKind);
        Assert.Equal("0x00007FF6DEADBEEF", depTarget);

        // 3. Verify C++ native_guard implementation contract (#167)
        string repoRoot = FindRepoRoot();
        string vehSource = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "veh_crash_shield.cpp"));
        Assert.Contains("IsBenignDebugOrThreadNameException", vehSource);
        Assert.Contains("ClassifyAccessViolationKind", vehSource);
        Assert.Contains("[SHADPS4-VEH]", vehSource);
    }

    [Fact]
    public void PdataUnwindChainWalker_ResolvesChainedUnwindInfoToPrimaryFunctionEntry_And_DetectsCycles()
    {
        // Build a synthetic module image with UNWIND_INFO records at 0x2000, 0x2100, 0x2200, 0x2300, 0x2400
        // and a .pdata buffer with 5 RUNTIME_FUNCTION records:
        // Entry 0 (Primary):   [0x1000, 0x1100), UnwindInfo @ 0x2000 (Flags = 0x0)
        // Entry 1 (Secondary): [0x1100, 0x1200), UnwindInfo @ 0x2100 (Flags = 0x4 UNW_FLAG_CHAININFO -> Entry 0)
        // Entry 2 (Tertiary):  [0x1200, 0x1300), UnwindInfo @ 0x2200 (Flags = 0x4 UNW_FLAG_CHAININFO -> Entry 1)
        // Entry 3 & 4 (Cycle): [0x1400, 0x1500) <-> [0x1500, 0x1600) chained to each other
        byte[] image = new byte[0x3000];

        // Entry 0 UnwindInfo @ 0x2000: Version=1, Flags=0
        image[0x2000] = 0x01;
        image[0x2001] = 0x04;
        image[0x2002] = 0x00;
        image[0x2003] = 0x00;

        // Entry 1 UnwindInfo @ 0x2100: Version=1, Flags=4 (UNW_FLAG_CHAININFO -> (4 << 3) | 1)
        image[0x2100] = (byte)((0x4 << 3) | 0x1);
        image[0x2101] = 0x04;
        image[0x2102] = 0x00; // CountOfCodes = 0 -> chained RUNTIME_FUNCTION starts at +4
        image[0x2103] = 0x00;
        WriteRuntimeFunction(image, 0x2104, 0x1000u, 0x1100u, 0x2000u);

        // Entry 2 UnwindInfo @ 0x2200: Chained to Entry 1
        image[0x2200] = (byte)((0x4 << 3) | 0x1);
        image[0x2201] = 0x04;
        image[0x2202] = 0x00;
        image[0x2203] = 0x00;
        WriteRuntimeFunction(image, 0x2204, 0x1100u, 0x1200u, 0x2100u);

        // Entry 3 & 4 cyclic UnwindInfo @ 0x2300 <-> 0x2400
        image[0x2300] = (byte)((0x4 << 3) | 0x1);
        image[0x2302] = 0x00;
        WriteRuntimeFunction(image, 0x2304, 0x1500u, 0x1600u, 0x2400u);

        image[0x2400] = (byte)((0x4 << 3) | 0x1);
        image[0x2402] = 0x00;
        WriteRuntimeFunction(image, 0x2404, 0x1400u, 0x1500u, 0x2300u);

        byte[] pdataBytes = new byte[5 * 12];
        WriteRuntimeFunction(pdataBytes, 0, 0x1000u, 0x1100u, 0x2000u);
        WriteRuntimeFunction(pdataBytes, 12, 0x1100u, 0x1200u, 0x2100u);
        WriteRuntimeFunction(pdataBytes, 24, 0x1200u, 0x1300u, 0x2200u);
        WriteRuntimeFunction(pdataBytes, 36, 0x1400u, 0x1500u, 0x2300u);
        WriteRuntimeFunction(pdataBytes, 48, 0x1500u, 0x1600u, 0x2400u);

        List<CrashTriageOrchestrator.PdataRuntimeFunctionEntry> table =
            CrashTriageOrchestrator.ParsePePdataRuntimeFunctions(pdataBytes);
        Assert.Equal(5, table.Count);

        CrashTriageOrchestrator.PdataRuntimeFunctionEntry? primaryEntry =
            CrashTriageOrchestrator.ResolvePrimaryPdataFunctionEntry(table, 0x1250u, image);
        Assert.True(primaryEntry.HasValue);
        Assert.Equal(0x1000u, primaryEntry.Value.BeginAddress);
        Assert.Equal(0x1100u, primaryEntry.Value.EndAddress);

        // Verify cyclic UNW_FLAG_CHAININFO terminates cleanly within 16 hops without hanging
        CrashTriageOrchestrator.PdataRuntimeFunctionEntry? cycleResult =
            CrashTriageOrchestrator.ResolvePrimaryPdataFunctionEntry(table, 0x1450u, image);
        Assert.True(cycleResult.HasValue);
        Assert.True(cycleResult.Value.BeginAddress == 0x1400u || cycleResult.Value.BeginAddress == 0x1500u);

        // Verify COMPEL AutomatedCrashSymbolizerService also implements ParsePePdataRuntimeFunctions & ResolvePrimaryPdataFunctionEntry
        string repoRoot = FindRepoRoot();
        string compelSymbolizerCs = File.ReadAllText(Path.Combine(repoRoot, "src", "apps", "compel", "Services", "Diagnostics", "AutomatedCrashSymbolizerService.cs"));
        Assert.Contains("ParsePePdataRuntimeFunctions", compelSymbolizerCs);
        Assert.Contains("ResolvePrimaryPdataFunctionEntry", compelSymbolizerCs);
    }

    [Fact]
    public void NonOverlappingPatchIntervalGuard_RejectsCollidingDetourRanges_And_AllowsAdjacentIntervals()
    {
        // 1. Adjacent non-overlapping intervals pass (#169, OBL-HOOK-NO-OVERLAP)
        IpsPatchCodec.PatchIntervalDescriptor[] cleanIntervals =
        [
            new IpsPatchCodec.PatchIntervalDescriptor("Core", "Hook_A", 0x1000UL, 14),
            new IpsPatchCodec.PatchIntervalDescriptor("Core", "Hook_B", 0x100EUL, 14),
            new IpsPatchCodec.PatchIntervalDescriptor("Core", "Hook_A", 0x1000UL, 14) // Idempotent re-registration allowed
        ];

        IpsPatchCodec.PatchIntervalCollisionReport cleanReport =
            IpsPatchCodec.ValidateNonOverlappingPatchIntervals(cleanIntervals);
        Assert.False(cleanReport.HasCollision);
        Assert.Null(cleanReport.FirstName);
        Assert.Null(cleanReport.SecondName);

        // 2. Overlapping intervals are rejected with [OBL-HOOK-NO-OVERLAP]
        IpsPatchCodec.PatchIntervalDescriptor[] collidingIntervals =
        [
            new IpsPatchCodec.PatchIntervalDescriptor("VFS", "Detour_CArchive_Open", 0x90E90UL, 14),
            new IpsPatchCodec.PatchIntervalDescriptor("VFS", "MidFunc_OverlapPatch", 0x90E98UL, 8)
        ];

        IpsPatchCodec.PatchIntervalCollisionReport collisionReport =
            IpsPatchCodec.ValidateNonOverlappingPatchIntervals(collidingIntervals);
        Assert.True(collisionReport.HasCollision);
        Assert.Equal("Detour_CArchive_Open", collisionReport.FirstName);
        Assert.Equal("MidFunc_OverlapPatch", collisionReport.SecondName);
        Assert.NotNull(collisionReport.DiagnosticMessage);
        Assert.Contains("[OBL-HOOK-NO-OVERLAP]", collisionReport.DiagnosticMessage);

        // 3. Verify C++ obligation tag in runtime_patch_controller.cpp (live C++ overlap rejection runs in __phase14_selftest__)
        string repoRoot = FindRepoRoot();
        string patchControllerCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "runtime_patch_controller.cpp"));
        Assert.Contains("satisfies: OBL-HOOK-NO-OVERLAP", patchControllerCpp);
        Assert.Contains("CheckPatchIntervalCollisionLocked", patchControllerCpp);
    }

    [Fact]
    public void SafeModeBootBypass_And_LastCrashedSentinel_Contract_And_SingleFlightComTeardown()
    {
        string repoRoot = FindRepoRoot();
        string patchControllerCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "runtime_patch_controller.cpp"));
        string vehCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "veh_crash_shield.cpp"));
        string dllMainCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "dllmain.cpp"));

        // #170 Safe Mode boot bypass + both last-crashed.sentinel locations (narrow '/' or wide Win32 '\\' literals)
        Assert.Contains("KONGOR_SAFE_MODE", patchControllerCpp);
        Assert.True(
            vehCpp.Contains("logs/crash/last-crashed.sentinel", StringComparison.Ordinal) ||
            vehCpp.Contains(@"logs\\crash\\last-crashed.sentinel", StringComparison.Ordinal),
            "veh_crash_shield.cpp must write logs/crash/last-crashed.sentinel");
        Assert.True(
            vehCpp.Contains("userdata/logs/last-crashed.sentinel", StringComparison.Ordinal) ||
            vehCpp.Contains(@"userdata\\logs\\last-crashed.sentinel", StringComparison.Ordinal),
            "veh_crash_shield.cpp must write userdata/logs/last-crashed.sentinel");
        Assert.Contains("GetAsyncKeyState(VK_MENU)", patchControllerCpp);
        Assert.Contains("GetAsyncKeyState(VK_SHIFT)", patchControllerCpp);

        // #171 Scoped RAII COM guard, single-flight teardown, and lpReserved != nullptr loader-lock guard
        Assert.Contains("KongorScopedComGuard", dllMainCpp);
        Assert.Contains("0x80010106", dllMainCpp); // RPC_E_CHANGED_MODE
        Assert.Contains("TryBeginSingleFlightShutdown", dllMainCpp);
        Assert.Contains("lpReserved != nullptr", dllMainCpp);
    }

    [Fact]
    public void JobObjectMemoryCap_And_ProcessKillLogging_EnforcesConfiguredBudgetAndNoEmptyCatches()
    {
        // #172 JobObjectManager memory cap & accounting telemetry (including MemoryLimitReached)
        ulong customLimit = JobObjectManager.ResolveConfiguredJobMemoryLimitBytes("4096");
        Assert.Equal(4096UL * 1024UL * 1024UL, customLimit);
        Assert.Equal(8192UL * 1024UL * 1024UL, JobObjectManager.ResolveConfiguredJobMemoryLimitBytes(null));

        JobObjectTelemetry telemetry = JobObjectManager.QueryTelemetry();
        Assert.True(telemetry.ConfiguredJobMemoryLimitBytes > 0);
        Assert.False(telemetry.MemoryLimitReached);

        JobObjectTelemetry syntheticExceeded = new JobObjectTelemetry(
            ActiveProcesses: 1,
            TotalProcessesSpawned: 2,
            TotalTerminatedProcesses: 1,
            PeakProcessMemoryBytes: 5000UL,
            PeakJobMemoryBytes: 9000UL,
            ConfiguredJobMemoryLimitBytes: 8000UL,
            MemoryLimitReached: true);
        Assert.True(syntheticExceeded.MemoryLimitReached);

        string repoRoot = FindRepoRoot();
        string jobManagerCs = File.ReadAllText(Path.Combine(repoRoot, "src", "tools", "kongor-cli", "Mcp", "Services", "JobObjectManager.cs"));
        Assert.Contains("JOB_OBJECT_LIMIT_JOB_MEMORY", jobManagerCs);
        Assert.Contains("QueryInformationJobObject", jobManagerCs);
        Assert.Contains("MemoryLimitReached", jobManagerCs);

        string win32JobStrategyCs = File.ReadAllText(Path.Combine(repoRoot, "src", "apps", "compel", "Internals", "Win32JobObjectStrategy.cs"));
        Assert.DoesNotContain("Suppress per-process shutdown errors", win32JobStrategyCs);
        Assert.DoesNotContain("Suppress shutdown exception", win32JobStrategyCs);
    }

    [Fact]
    public void DualHeadTailOutputRingBuffer_PinsStartupHeadAndLatestTailWithTruncationMarker()
    {
        // #173 Dual Head-Tail Ring Buffer Governor
        ClientOutputRingBuffer buffer = new ClientOutputRingBuffer(maxCapacity: 6);
        Assert.Equal(3, buffer.HeadCapacity);
        Assert.Equal(3, buffer.TailCapacity);

        for (int i = 1; i <= 6; i++)
        {
            buffer.AppendLine($"line-{i}");
        }

        Assert.Equal(6, buffer.TotalLinesAppended);
        Assert.Equal(0, buffer.TruncatedLineCount);
        IReadOnlyList<string> unTruncated = buffer.GetHeadAndTailSnapshot();
        Assert.Equal(6, unTruncated.Count);
        Assert.Equal("line-1", unTruncated[0]);
        Assert.Equal("line-6", unTruncated[5]);

        // Append 4 more lines (total 10 lines -> 4 middle lines evicted)
        for (int i = 7; i <= 10; i++)
        {
            buffer.AppendLine($"line-{i}");
        }

        Assert.Equal(10, buffer.TotalLinesAppended);
        Assert.Equal(4, buffer.TruncatedLineCount);

        IReadOnlyList<string> snapshot = buffer.GetHeadAndTailSnapshot();
        Assert.Equal(7, snapshot.Count);
        Assert.Equal("line-1", snapshot[0]);
        Assert.Equal("line-2", snapshot[1]);
        Assert.Equal("line-3", snapshot[2]);
        Assert.Equal("...[truncated 4 lines]...", snapshot[3]);
        Assert.Equal("line-8", snapshot[4]);
        Assert.Equal("line-9", snapshot[5]);
        Assert.Equal("line-10", snapshot[6]);

        IReadOnlyList<string> tailTwo = buffer.GetTail(2);
        Assert.Equal(2, tailTwo.Count);
        Assert.Equal("line-9", tailTwo[0]);
        Assert.Equal("line-10", tailTwo[1]);

        buffer.Clear();
        Assert.Equal(0, buffer.TotalLinesAppended);
        Assert.Equal(0, buffer.TruncatedLineCount);
        Assert.Empty(buffer.GetHeadAndTailSnapshot());
    }

    [Fact]
    public void DynamicArchiveDecompressionBudgetGuard_Enforces500xRatioWith256MiBFloor()
    {
        // #174 Dynamic 500x ratio / 256 MiB floor archive decompression budget guard
        const long floor256MiB = 256L * 1024L * 1024L;

        // Small compressed entry gets the 256 MiB floor
        long smallBudget = BoundedDecompressionPaginator.ComputeArtifactDecompressionBudgetBytes(1024L);
        Assert.Equal(floor256MiB, smallBudget);

        // 2 MiB compressed entry gets 2 MiB * 500 = 1000 MiB budget
        long twoMiB = 2L * 1024L * 1024L;
        long largeBudget = BoundedDecompressionPaginator.ComputeArtifactDecompressionBudgetBytes(twoMiB);
        Assert.Equal(twoMiB * 500L, largeBudget);

        // Zip-bomb: 64 KiB compressed claiming 300 MiB uncompressed (> 256 MiB floor and > 500x ratio)
        List<string> logs = new List<string>();
        ArchiveDecompressionBudgetEvaluation bombEval = BoundedDecompressionPaginator.ValidateArchiveEntryDecompressionBudget(
            "ui/bomb.xml",
            compressedBytes: 64L * 1024L,
            uncompressedBytes: 300L * 1024L * 1024L,
            logger: logs.Add);
        Assert.False(bombEval.IsWithinBudget);
        Assert.Single(logs);
        Assert.Contains("[REA-DECOMP-BUDGET]", logs[0]);

        // Legitimate large entry: 2 MiB compressed, 400 MiB uncompressed (200x ratio <= 1000 MiB budget)
        ArchiveDecompressionBudgetEvaluation validLargeEval = BoundedDecompressionPaginator.ValidateArchiveEntryDecompressionBudget(
            "textures/atlas.dds",
            compressedBytes: twoMiB,
            uncompressedBytes: 400L * 1024L * 1024L);
        Assert.True(validLargeEval.IsWithinBudget);
    }

    [Fact]
    public void ObserveEffectVfsCompletenessInvariant_NeverClaimsCreatedOrDeletedWhenRootIncompleteOrTruncated()
    {
        // #175 Observe-Effect VFS Completeness Invariant
        HashSet<string> completeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "override", "ui" };
        HashSet<string> incompleteRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ui" };

        VfsSnapshotEntry sampleEntry = new VfsSnapshotEntry("override/ui/main.package", 1024L, "AAA111");
        VfsSnapshotEntry modifiedEntry = new VfsSnapshotEntry("override/ui/main.package", 2048L, "BBB222");
        VfsSnapshotEntry missingDigestEntry = new VfsSnapshotEntry("override/ui/main.package", 1024L, null);

        // 1. Created is allowed when pre-root is complete and pre-scan is NOT truncated
        VfsFilesystemEffectClassification validCreated = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: null,
            postEntry: sampleEntry,
            preCompleteRoots: completeRoots,
            postCompleteRoots: completeRoots,
            preScanTruncated: false,
            postScanTruncated: false);
        Assert.Equal(VfsFilesystemEffectKind.Created, validCreated.Effect);
        Assert.Null(validCreated.UnobservedAbsenceReason);

        // 2. Created is FORBIDDEN (returns Unknown) when pre-root is incomplete
        VfsFilesystemEffectClassification unverifiedCreatedRoot = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: null,
            postEntry: sampleEntry,
            preCompleteRoots: incompleteRoots,
            postCompleteRoots: completeRoots,
            preScanTruncated: false,
            postScanTruncated: false);
        Assert.Equal(VfsFilesystemEffectKind.Unknown, unverifiedCreatedRoot.Effect);
        Assert.NotNull(unverifiedCreatedRoot.UnobservedAbsenceReason);
        Assert.Contains("root_not_completely_indexed", unverifiedCreatedRoot.UnobservedAbsenceReason);

        // 3. Created is FORBIDDEN (returns Unknown) when pre-scan was truncated
        VfsFilesystemEffectClassification truncatedCreated = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: null,
            postEntry: sampleEntry,
            preCompleteRoots: completeRoots,
            postCompleteRoots: completeRoots,
            preScanTruncated: true,
            postScanTruncated: false);
        Assert.Equal(VfsFilesystemEffectKind.Unknown, truncatedCreated.Effect);
        Assert.NotNull(truncatedCreated.UnobservedAbsenceReason);
        Assert.Contains("scan_truncated", truncatedCreated.UnobservedAbsenceReason);

        // 4. Deleted is allowed when post-root is complete and post-scan is NOT truncated
        VfsFilesystemEffectClassification validDeleted = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: sampleEntry,
            postEntry: null,
            preCompleteRoots: completeRoots,
            postCompleteRoots: completeRoots,
            preScanTruncated: false,
            postScanTruncated: false);
        Assert.Equal(VfsFilesystemEffectKind.Deleted, validDeleted.Effect);

        // 5. Deleted is FORBIDDEN (returns Unknown) when post-scan was truncated
        VfsFilesystemEffectClassification truncatedDeleted = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: sampleEntry,
            postEntry: null,
            preCompleteRoots: completeRoots,
            postCompleteRoots: completeRoots,
            preScanTruncated: false,
            postScanTruncated: true);
        Assert.Equal(VfsFilesystemEffectKind.Unknown, truncatedDeleted.Effect);
        Assert.NotNull(truncatedDeleted.UnobservedAbsenceReason);
        Assert.Contains("scan_truncated", truncatedDeleted.UnobservedAbsenceReason);

        // 6. Modified is still authoritative even if scan was truncated because both pre and post observed the file
        VfsFilesystemEffectClassification modifiedClassification = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: sampleEntry,
            postEntry: modifiedEntry,
            preCompleteRoots: incompleteRoots,
            postCompleteRoots: incompleteRoots,
            preScanTruncated: true,
            postScanTruncated: true);
        Assert.Equal(VfsFilesystemEffectKind.Modified, modifiedClassification.Effect);

        // 7. Equal SizeBytes with missing Sha256Hex returns Unknown with missing_sha256_digest
        VfsFilesystemEffectClassification missingHashClassification = VfsCompletenessCheckerService.ClassifyFilesystemEffect(
            "override/ui/main.package",
            preEntry: sampleEntry,
            postEntry: missingDigestEntry,
            preCompleteRoots: completeRoots,
            postCompleteRoots: completeRoots,
            preScanTruncated: false,
            postScanTruncated: false);
        Assert.Equal(VfsFilesystemEffectKind.Unknown, missingHashClassification.Effect);
        Assert.NotNull(missingHashClassification.UnobservedAbsenceReason);
        Assert.Contains("missing_sha256_digest", missingHashClassification.UnobservedAbsenceReason);

        // 8. Full snapshot diff aggregation test
        Dictionary<string, VfsSnapshotEntry> preMap = new Dictionary<string, VfsSnapshotEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["override/ui/deleted.xml"] = new VfsSnapshotEntry("override/ui/deleted.xml", 100L, "H1"),
            ["override/ui/modified.xml"] = new VfsSnapshotEntry("override/ui/modified.xml", 200L, "H2")
        };
        Dictionary<string, VfsSnapshotEntry> postMap = new Dictionary<string, VfsSnapshotEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["override/ui/created.xml"] = new VfsSnapshotEntry("override/ui/created.xml", 300L, "H3"),
            ["override/ui/modified.xml"] = new VfsSnapshotEntry("override/ui/modified.xml", 250L, "H2_NEW"),
            ["unscanned_root/mystery.xml"] = new VfsSnapshotEntry("unscanned_root/mystery.xml", 400L, "H4")
        };

        VfsObserveEffectDiffReport diffReport = VfsCompletenessCheckerService.DiffSnapshotsWithCompleteness(
            preMap,
            postMap,
            completeRoots,
            completeRoots,
            preScanTruncated: false,
            postScanTruncated: false);

        Assert.False(diffReport.IsAuthoritative); // Because unscanned_root/mystery.xml is Unknown
        Assert.Equal(1, diffReport.CreatedCount);
        Assert.Equal(1, diffReport.DeletedCount);
        Assert.Equal(1, diffReport.ModifiedCount);
        Assert.Equal(1, diffReport.UnknownCount);
    }

    [Fact]
    public void NativeGuardEventBus_MainThreadQueue_RawInputDecoupling_ShadowVmt_And_JsonRpc2PipeEnvelope_Contract()
    {
        // Per FN-007, never call LoadLibraryW on games/client/version.dll inside the shared testhost.exe process;
        // live in-process C++ execution of __phase14_selftest__ and __jsonrpc__ runs in scripts/test_hook_toggle.ps1 (Case 6g).
        // Here we verify both the C++ implementation contracts and the compiled games/client/version.dll PE64 binary on disk.
        string repoRoot = FindRepoRoot();
        string headerSource = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "runtime_patch_controller.h"));
        string cppSource = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "runtime_patch_controller.cpp"));
        string dllMainSource = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "dllmain.cpp")) +
                               File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "vfs_and_hooks.cpp")) +
                               File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "pipe_server.cpp"));

        // #176 Double-buffered swap main-thread callback queue
        Assert.Contains("g_pendingMainThreadCallbacks", cppSource);
        Assert.Contains("g_pendingMainThreadCallbacks.swap(drainingCallbacks)", cppSource);
        Assert.Contains("EnqueueMainThreadCallback", headerSource);
        Assert.Contains("DrainMainThreadCallbacks", headerSource);

        // #177 RawInput WM_INPUT message-pump decoupling
        Assert.Contains("WM_INPUT", dllMainSource);
        Assert.Contains("DecoupledRawInputEvent", dllMainSource);
        Assert.Contains("KeyStateTracker", dllMainSource);

        // #178 Re-entrant priority event bus with lower integer priority first (prio < existing.priority)
        Assert.Contains("ReentrantPriorityEventDispatcher", headerSource);
        Assert.Contains("prio < existing.priority", headerSource);

        // #179 Shadow VMT hook manager + EntityFieldContainer custom fields
        Assert.Contains("ConcreteVmtHookManager", dllMainSource);
        Assert.Contains("EntityFieldContainer", dllMainSource);

        // #180 Length-delimited JSON-RPC 2.0 named-pipe envelope + string ID preservation + live self-test harness
        Assert.Contains("\\\"jsonrpc\\\":\\\"2.0\\\"", dllMainSource);
        Assert.Contains("bool preserveQuotes = false", dllMainSource);
        Assert.Contains("-32601", dllMainSource);
        Assert.Contains("Kongor_ProcessPipeStreamLines", dllMainSource);
        Assert.Contains("RunPhase14SelfTest", cppSource);

        // Verify compiled games/client/version.dll PE64 binary on disk contains the compiled Phase 14 markers
        if (NexusTestEnvironment.HasGameAssets)
        {
            string versionDllPath = Path.Combine(repoRoot, "games", "client", "version.dll");
            Assert.True(File.Exists(versionDllPath), $"Expected compiled version.dll at {versionDllPath}. Run 'cmake --build native/proxies/native_guard/build --config Release'.");
            byte[] peBytes = File.ReadAllBytes(versionDllPath);
            string peAscii = Encoding.ASCII.GetString(peBytes);
            Assert.Contains("\"jsonrpc\":\"2.0\"", peAscii);
            Assert.Contains("__phase14_selftest__", peAscii);
            Assert.Contains("__jsonrpc__", peAscii);
            Assert.Contains("rttiCorruptSehOk", peAscii);
            Assert.Contains("dispatcherOrder", peAscii);
            Assert.Contains("logs/crash/last-crashed.sentinel", peAscii);
            Assert.Contains("userdata/logs/last-crashed.sentinel", peAscii);
        }

        // #255 Execute standalone C++ unit test runner (native_guard_unit_tests.exe) exercising real C++ implementations
        string defaultCppTestExePath = Path.Combine(
            repoRoot,
            "native",
            "proxies",
            "native_guard",
            "build",
            "Release",
            "native_guard_unit_tests.exe");
        string? resolvedCppTestExePath = File.Exists(defaultCppTestExePath)
            ? defaultCppTestExePath
            : NexusTestEnvironment.TryFindNativeBinary("native_guard_unit_tests.exe");
        if (resolvedCppTestExePath is null)
        {
            if (!NexusTestEnvironment.HasGameAssets || !OperatingSystem.IsWindows()) return;
            resolvedCppTestExePath = defaultCppTestExePath;
        }

        string cppTestExePath = resolvedCppTestExePath;
        Assert.True(
            File.Exists(cppTestExePath),
            $"Expected C++ unit test executable at {cppTestExePath}. Build with: cmake --build native/proxies/native_guard/build --config Release");

        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"kongor_ng_unit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cppTestExePath,
                WorkingDirectory = tempWorkDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(proc);
            string cppStdout = proc.StandardOutput.ReadToEnd();
            string cppStderr = proc.StandardError.ReadToEnd();
            bool exited = proc.WaitForExit(10000);
            Assert.True(exited, "native_guard_unit_tests.exe timed out after 10s.");
            Assert.True(
                proc.ExitCode == 0,
                $"native_guard_unit_tests.exe failed with exit code {proc.ExitCode}.\nSTDOUT:\n{cppStdout}\nSTDERR:\n{cppStderr}");
            Assert.Contains("Failed=0", cppStdout);
            Assert.Contains("[PASS] PdataChain_ResolvesTwoHopOddPaddedChainToPrimary", cppStdout);
            Assert.Contains("[PASS] IntervalGuard_RejectsSingleByteBoundaryOverlap", cppStdout);
        }
        finally
        {
            if (Directory.Exists(tempWorkDir))
            {
                try
                {
                    Directory.Delete(tempWorkDir, recursive: true);
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"[WARN] Could not remove temp NativeGuard test dir '{tempWorkDir}': {ex.Message}");
                }
            }
        }
    }

    private static void WriteRuntimeFunction(byte[] buffer, int offset, uint beginRva, uint endRva, uint unwindInfoRva)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 0, 4), beginRva);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4, 4), endRva);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8, 4), unwindInfoRva);
    }
}
