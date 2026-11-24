// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DiagnosticBundleServiceTests.cs
// System: WILLOWMAKER - Diagnostics Unit Tests
// Subsystem: Privacy Redaction, Minidump Inspection, Retention & 1-Click Export
// ============================================================================

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Kongor.Core.Diagnostics;
using Microsoft.AspNetCore.SignalR.Client;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class DiagnosticBundleServiceTests
{
    // Helper: Synthesizes a valid x64 Win32 MDMP with Stream 7 (SystemInfo), Stream 4 (ModuleList),
    // optional Stream 6 (Exception + CONTEXT_AMD64 GPRs), and an optional trailing stack payload.
    private static byte[] BuildSyntheticMdmp(
        string moduleFullPath,
        ulong moduleBase,
        uint moduleSize,
        bool includeExceptionStream,
        uint exceptionCode = 0xC0000005u,
        ulong exceptionAddress = 0UL,
        ulong avAccessKind = 0UL,
        ulong avTargetAddr = 0x0000000000000018UL,
        ulong rcxValue = 0x00000000DEADBEEFUL,
        byte[]? extraStackBytes = null)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);

        int streamCount = includeExceptionStream ? 5 : 4;
        uint dirRva = 32u;           // 5 * 12 = 60 bytes -> ends 92
        uint sysInfoRva = 96u;       // 32 bytes -> ends 128
        uint modListRva = 160u;      // 4 + 108 = 112 bytes -> ends 272
        uint modNameRva = 300u;      // 4 + UTF-16LE bytes + 2 null bytes
        byte[] modNameUtf16 = Encoding.Unicode.GetBytes(moduleFullPath);
        uint threadListRva = 512u;   // 4 + 48 = 52 bytes -> ends 564
        uint memListRva = 576u;      // 4 + 16 = 20 bytes -> ends 596
        uint excStreamRva = 640u;    // 168 bytes -> ends 808
        uint ctxRva = 832u;          // 0x4D0 (1232) bytes -> ends 2064
        uint stackFrameRva = 2064u;  // 64 bytes -> ends 2128
        uint extraRva = 2144u;
        const ulong stackStartAddr = 0x0000008810FF00UL;
        const uint stackFrameSize = 64u;

        int totalSize = (int)extraRva + (extraStackBytes?.Length ?? 64);
        ms.SetLength(totalSize);

        // Header (32 bytes)
        bw.Seek(0, SeekOrigin.Begin);
        bw.Write(MinidumpReader.MdmpSignature);
        bw.Write(0x0000A793u);
        bw.Write((uint)streamCount);
        bw.Write(dirRva);
        bw.Write(0u);
        bw.Write(0u);
        bw.Write(0UL);

        // Directory entries at offset 32
        bw.Seek((int)dirRva, SeekOrigin.Begin);
        // Entry 0: Stream 7 (SystemInfo)
        bw.Write(7u);
        bw.Write(32u);
        bw.Write(sysInfoRva);
        // Entry 1: Stream 3 (ThreadList)
        bw.Write(3u);
        bw.Write(52u);
        bw.Write(threadListRva);
        // Entry 2: Stream 4 (ModuleList)
        bw.Write(4u);
        bw.Write(112u);
        bw.Write(modListRva);
        // Entry 3: Stream 5 (MemoryList)
        bw.Write(5u);
        bw.Write(20u);
        bw.Write(memListRva);
        if (includeExceptionStream)
        {
            // Entry 4: Stream 6 (ExceptionStream)
            bw.Write(6u);
            bw.Write(168u);
            bw.Write(excStreamRva);
        }

        // Stream 7: SystemInfo (AMD64 = 9)
        bw.Seek((int)sysInfoRva, SeekOrigin.Begin);
        bw.Write((ushort)9);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(16u);

        // Stream 3: ThreadList (1 thread)
        bw.Seek((int)threadListRva, SeekOrigin.Begin);
        bw.Write(1u);                  // NumberOfThreads
        bw.Write(4242u);               // ThreadId
        bw.Write(0u);                  // SuspendCount
        bw.Write(0u);                  // PriorityClass
        bw.Write(0u);                  // Priority
        bw.Write(0UL);                 // Teb
        bw.Write(stackStartAddr);      // Stack.StartOfMemoryRange
        bw.Write(stackFrameSize);      // Stack.Memory.DataSize
        bw.Write(stackFrameRva);       // Stack.Memory.Rva
        bw.Write(includeExceptionStream ? 0x4D0u : 0u); // ThreadContext.DataSize
        bw.Write(includeExceptionStream ? ctxRva : 0u); // ThreadContext.Rva

        // Stream 5: MemoryList (1 range matching crashing thread stack)
        bw.Seek((int)memListRva, SeekOrigin.Begin);
        bw.Write(1u);                  // NumberOfMemoryRanges
        bw.Write(stackStartAddr);      // StartOfMemoryRange
        bw.Write(stackFrameSize);      // Memory.DataSize
        bw.Write(stackFrameRva);       // Memory.Rva

        // Stream 4: ModuleList (1 module)
        bw.Seek((int)modListRva, SeekOrigin.Begin);
        bw.Write(1u); // NumberOfModules
        bw.Write(moduleBase);
        bw.Write(moduleSize);
        bw.Write(0u); // CheckSum
        bw.Write(0u); // TimeDateStamp
        bw.Write(modNameRva);
        bw.Write(new byte[84]); // VS_FIXEDFILEINFO + CvRecord + MiscRecord

        // MINIDUMP_STRING at modNameRva
        bw.Seek((int)modNameRva, SeekOrigin.Begin);
        bw.Write((uint)modNameUtf16.Length);
        bw.Write(modNameUtf16);
        bw.Write((ushort)0);

        if (includeExceptionStream)
        {
            bw.Seek((int)excStreamRva, SeekOrigin.Begin);
            bw.Write(4242u);         // ThreadId
            bw.Write(0u);            // __alignment
            bw.Write(exceptionCode); // ExceptionCode
            bw.Write(0u);            // ExceptionFlags
            bw.Write(0UL);           // ExceptionRecord
            bw.Write(exceptionAddress);
            bw.Write(2u);            // NumberParameters
            bw.Write(0u);            // __unusedAlignment
            bw.Write(avAccessKind);  // Param[0] (0 = READ)
            bw.Write(avTargetAddr);  // Param[1]

            // ThreadContext MINIDUMP_LOCATION_DESCRIPTOR at excStreamRva + 160
            bw.Seek((int)excStreamRva + 160, SeekOrigin.Begin);
            bw.Write(0x4D0u); // DataSize
            bw.Write(ctxRva); // Rva

            // CONTEXT_AMD64 GPRs at ctxRva + 0x78
            bw.Seek((int)ctxRva + 0x78, SeekOrigin.Begin);
            bw.Write(0UL);               // RAX
            bw.Write(rcxValue);          // RCX
            bw.Write(0x11223344UL);      // RDX
            bw.Write(0UL);               // RBX
            bw.Write(0x0000008810FF00UL);// RSP
            bw.Write(0UL);               // RBP
            bw.Write(0UL);               // RSI
            bw.Write(0UL);               // RDI
            for (int r = 0; r < 8; r++) bw.Write(0UL); // R8..R15
            bw.Write(exceptionAddress);  // RIP
        }

        if (extraStackBytes is { Length: > 0 })
        {
            bw.Seek((int)extraRva, SeekOrigin.Begin);
            bw.Write(extraStackBytes);
        }

        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void BinaryDmpRedactor_ScrubsUtf16AndAsciiUsernameMachineNameAndPasswordPreservingMdmpValidity()
    {
        // Protects: Same-length binary scrubbing of MDMP minidumps without corrupting RVA stream offsets.
        string userName = string.IsNullOrWhiteSpace(Environment.UserName) || Environment.UserName.Length < 3
            ? "TesterAlice"
            : Environment.UserName;
        string machineName = string.IsNullOrWhiteSpace(Environment.MachineName) || Environment.MachineName.Length < 3
            ? "ALICE-DESKTOP"
            : Environment.MachineName;
        const string secretPassword = "password123!";

        using MemoryStream stackPayload = new();
        stackPayload.Write(Encoding.ASCII.GetBytes($"login_password={secretPassword};host={machineName};"));
        stackPayload.Write(Encoding.Unicode.GetBytes($"wide_pw={secretPassword};wide_host={machineName};"));

        ulong modBase = 0x00007FF810000000UL;
        ulong faultAddr = modBase + 0x14B2A0UL;
        byte[] dmp = BuildSyntheticMdmp(
            moduleFullPath: $@"C:\Users\{userName}\Games\Kongor\cgame_x64.dll",
            moduleBase: modBase,
            moduleSize: 0x00600000u,
            includeExceptionStream: true,
            exceptionCode: 0xC0000005u,
            exceptionAddress: faultAddr,
            avAccessKind: 0UL,
            avTargetAddr: 0x0000000000000088UL,
            rcxValue: 0x00000000DEADBEEFUL,
            extraStackBytes: stackPayload.ToArray());

        int originalLength = dmp.Length;

        // Act
        int replacements = DiagnosticRedactor.ScrubBinaryMinidump(dmp, [userName, machineName, secretPassword]);

        // Assert 1: Byte length preserved identically & zero matches remain in ASCII or UTF-16LE
        Assert.Equal(originalLength, dmp.Length);
        Assert.True(replacements >= 5, $"Expected >= 5 binary scrub replacements, got {replacements}");
        Assert.DoesNotContain(secretPassword, Encoding.ASCII.GetString(dmp), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretPassword, Encoding.Unicode.GetString(dmp), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userName, Encoding.Unicode.GetString(dmp), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(machineName, Encoding.ASCII.GetString(dmp), StringComparison.OrdinalIgnoreCase);

        // Assert 2: MinidumpReader still resolves exact module+0xRVA, AV kind, and AMD64 GPRs post-scrub
        using MemoryStream verifyStream = new(dmp, writable: false);
        MinidumpTriageResult triage = MinidumpReader.Inspect(verifyStream);
        Assert.True(triage.IsValidDump);
        Assert.Equal("CRASH_TRIAGED", triage.Status);
        Assert.Equal("cgame_x64.dll", triage.FaultingModule);
        Assert.Equal("0x14B2A0", triage.FaultingRva);
        Assert.Equal("cgame_x64.dll+0x14B2A0", triage.TargetFormatted);
        Assert.Equal("0xC0000005", triage.ExceptionCodeHex);
        Assert.Equal("READ", triage.AccessViolationKind);
        Assert.Equal("0x00000000DEADBEEF", triage.Registers["RCX"]);
    }

    [Fact]
    public void TextRedactor_ScrubsSecretsHostnamesPeerIpsAndMacIpsUuids_WhileKeepingMasterServer()
    {
        // Protects: Text privacy firewall across K2 configs, macOS .ips JSON headers, hostnames, and P2P peer IPs.
        string user = Environment.UserName;
        string host = Environment.MachineName;
        string input = $$"""
            [12:00:00] SetSave "login_name" "ProGamer42"
            [12:00:01] SetSave "login_password" "password123!"
            [12:00:02] SetSave "login_token" "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.token"
            [12:00:03] Session cookie: session_id=9876543210abcdef; auth=yes
            [12:00:04] Bearer secret_jwt_token_123456
            [12:00:05] Path: C:\Users\{{user}}\Documents\Heroes of Newerth\console.log on machine {{host}}
            [12:00:06] {"crashReporterKey" : "A1B2C3D4-E5F6-7890-ABCD-1234567890EF", "anonId": "99887766-5544-3322-1100-AABBCCDDEEFF", "modelCode": "Mac14,5"}
            [12:00:07] Master server 192.0.2.1:55555 | Loopback 127.0.0.1:11235 | Peer 198.51.100.42:11235 | Peer2 203.0.113.19:11236
            """;

        var (redacted, counts) = DiagnosticRedactor.ScrubText(input, allowedMasterServerHost: "192.0.2.1");

        Assert.True(counts.SecretsRedacted >= 4);
        Assert.True(counts.UserIdentifiersRedacted >= 3);
        Assert.Equal(2, counts.PeerIpsRedacted);

        Assert.DoesNotContain("password123!", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_jwt_token_123456", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("9876543210abcdef", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A1B2C3D4-E5F6-7890-ABCD-1234567890EF", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("99887766-5544-3322-1100-AABBCCDDEEFF", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("198.51.100.42", redacted);
        Assert.DoesNotContain("203.0.113.19", redacted);
        if (host.Length >= 3)
        {
            Assert.DoesNotContain(host, redacted, StringComparison.OrdinalIgnoreCase);
        }

        // Allowed master server, loopback, and Mac hardware model remain intact for debugging
        Assert.Contains("192.0.2.1", redacted);
        Assert.Contains("127.0.0.1", redacted);
        Assert.Contains("Mac14,5", redacted);
    }

    [Fact]
    public void Utf16LeLogTailer_OddByteSeekPreservesCleanTextWithoutMojibake()
    {
        // Protects: Odd-byte seek alignment on locked UTF-16LE K2 console.log files without CJK mojibake or IOException.
        string tempLog = Path.Combine(Path.GetTempPath(), $"utf16_odd_seek_{Guid.NewGuid():N}.log");
        try
        {
            StringBuilder sb = new();
            for (int i = 1; i <= 120; i++)
            {
                sb.AppendLine($"[00:{i / 60:D2}:{i % 60:D2}] K2 Engine Tick #{i:D4}: VulkanSwapchain present nominal");
            }
            byte[] utf16WithBom = Encoding.Unicode.GetPreamble();
            byte[] body = Encoding.Unicode.GetBytes(sb.ToString());

            // Hold file open for writing (simulating live hon_x64.exe holding console.log)
            using FileStream liveWriter = new(tempLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            liveWriter.Write(utf16WithBom, 0, utf16WithBom.Length);
            liveWriter.Write(body, 0, body.Length);
            liveWriter.Flush();

            // Seek with an intentionally ODD byte count smaller than file length to trigger alignment logic
            string tail = DiagnosticBundleService.ReadBoundedLogText(
                tempLog,
                isUtf16Possible: true,
                maxTailLines: 5,
                maxTailBytes: 1023);

            Assert.Contains("K2 Engine Tick #0120: VulkanSwapchain present nominal", tail);
            Assert.Contains("K2 Engine Tick #0119", tail);
            Assert.False(tail.Contains('\0'), "Tail must not contain null-byte misalignment");
            Assert.False(tail.Contains('\uFFFD'), "Tail must not contain Unicode replacement characters");
        }
        finally
        {
            if (File.Exists(tempLog)) File.Delete(tempLog);
        }
    }

    [Fact]
    public async Task DiagnosticBundleService_PriorityEvictionGuaranteesUnder8MbAndLeavesRichDumpLocal()
    {
        // Protects: <= 8.0 MB Discord non-Nitro cap under incompressible payloads + strict local preservation of .rich.dmp.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_Evict_Test_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);

        try
        {
            // 1. Seed a 6 MB local .rich.dmp that MUST stay strictly local and untouched
            string richDumpPath = Path.Combine(crashDir, "minidump_20261025_120000.rich.dmp");
            byte[] richDummy = new byte[6 * 1024 * 1024];
            richDummy[0] = 0x4D;
            File.WriteAllBytes(richDumpPath, richDummy);

            // 2. Seed a 3.95 MB full-entropy incompressible valid MDMP minidump + multi-line high-entropy logs (> 9.5 MB compressed total!)
            byte[] randomTail = new byte[3_950_000];
            RandomNumberGenerator.Fill(randomTail);
            for (int r = 0; r < randomTail.Length; r++)
            {
                // Flip ':', '\', '/' to high-bit bytes so 253/256 symbols remain uniform (~7.98 bits/byte = 3.94 MB post-DEFLATE) with zero path markers
                if (randomTail[r] is (byte)':' or (byte)'\\' or (byte)'/')
                {
                    randomTail[r] |= 0x80;
                }
            }
            byte[] randomDmp = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: 0x00007FF810000000UL,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: 0x00007FF81014B2A0UL,
                extraStackBytes: randomTail);
            File.WriteAllBytes(Path.Combine(crashDir, "minidump_20261025_120000.dmp"), randomDmp);

            static void WriteHighEntropyLines(string path, int rawByteCount, string headerLine, int lineWidth = 2200)
            {
                byte[] buf = new byte[rawByteCount];
                RandomNumberGenerator.Fill(buf);
                string b64 = Convert.ToBase64String(buf);
                StringBuilder sb = new(b64.Length + 256);
                sb.AppendLine(headerLine);
                for (int pos = 0; pos < b64.Length; pos += lineWidth)
                {
                    sb.AppendLine(b64.Substring(pos, Math.Min(lineWidth, b64.Length - pos)));
                }
                File.WriteAllText(path, sb.ToString());
            }

            WriteHighEntropyLines(Path.Combine(clientRoot, "console.log"), 4_500_000, "K2 Startup Adapter: NVIDIA RTX 4090 Vulkan 1.3", lineWidth: 2200);
            WriteHighEntropyLines(Path.Combine(clientRoot, "WILLOWMAKER.log"), 1_000_000, "[2026-10-25T12:00:00.000Z] [BOOT] Launcher nominal", lineWidth: 1800);
            WriteHighEntropyLines(Path.Combine(crashDir, "engine_crashes.log"), 480_000, "[KONGOR NATIVE GUARD] Crash log stream", lineWidth: 1800);
            WriteHighEntropyLines(Path.Combine(clientRoot, "hon_x64_d3d9.log"), 240_000, "info: DXVK v3.0.2", lineWidth: 1800);
            WriteHighEntropyLines(Path.Combine(clientRoot, "client_lua_debug.log"), 240_000, "[LUA] UI frame budget nominal", lineWidth: 1800);

            // Act
            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "game;ClientPackageFiles;override",
                masterServerHost: "masterserver.newbs.io",
                outputDirectoryOverride: outDir,
                revealInFileManager: false);

            // Assert: Strictly under 8,000,000 bytes, notice recorded, and local .rich.dmp untouched!
            Assert.True(result.Success);
            Assert.True(File.Exists(result.ZipPath));
            Assert.True(
                result.ZipSizeBytes <= DiagnosticBundleService.HardZipSizeCapBytes,
                $"Expected zip <= {DiagnosticBundleService.HardZipSizeCapBytes} bytes, got {result.ZipSizeBytes}");
            Assert.True(File.Exists(richDumpPath), "Local .rich.dmp must remain untouched on disk");
            Assert.Equal(6 * 1024 * 1024, new FileInfo(richDumpPath).Length);

            using ZipArchive archive = ZipFile.OpenRead(result.ZipPath);
            Assert.Null(archive.GetEntry("minidump_20261025_120000.rich.dmp"));
            Assert.NotNull(archive.GetEntry("README_WHAT_IS_SHARED.txt"));
            Assert.NotNull(archive.GetEntry("CRASH_SUMMARY.md"));
            Assert.NotNull(archive.GetEntry("manifest.json"));

            using StreamReader summaryReader = new(archive.GetEntry("CRASH_SUMMARY.md")!.Open());
            string summaryMd = summaryReader.ReadToEnd();
            Assert.Contains("Size-Cap Priority Eviction Notice", summaryMd);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LocalCrashRetention_SkipsGitWorkspaceAndCapsStandaloneTesterFolderAt1Gb()
    {
        // Protects: Developer .git checkouts are never auto-pruned; standalone tester folders prune oldest dumps while protecting newest unacknowledged crash.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_Retention_{Guid.NewGuid():N}");
        string gitRepoClient = Path.Combine(tempRoot, "dev_repo", "games", "client");
        string standaloneClient = Path.Combine(tempRoot, "standalone_playtest");

        try
        {
            // 1. Developer .git checkout setup
            Directory.CreateDirectory(Path.Combine(tempRoot, "dev_repo", ".git"));
            string devCrashDir = Path.Combine(gitRepoClient, "logs", "crash");
            Directory.CreateDirectory(devCrashDir);
            File.WriteAllBytes(Path.Combine(devCrashDir, "minidump_old.dmp"), new byte[2000]);
            File.WriteAllBytes(Path.Combine(devCrashDir, "minidump_new.dmp"), new byte[2000]);

            int devDeleted = DiagnosticBundleService.EnforceLocalCrashRetention(gitRepoClient, lastAcknowledgedUtc: null, maxTotalBytes: 1000);
            Assert.Equal(0, devDeleted);
            Assert.Equal(2, Directory.GetFiles(devCrashDir, "*.dmp").Length);

            // 2. Standalone playtest folder setup (no .git ancestor)
            string testerCrashDir = Path.Combine(standaloneClient, "logs", "crash");
            Directory.CreateDirectory(testerCrashDir);

            DateTime baseUtc = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
            string old1 = Path.Combine(testerCrashDir, "minidump_01.rich.dmp");
            string old2 = Path.Combine(testerCrashDir, "minidump_02.rich.dmp");
            string newestTier1 = Path.Combine(testerCrashDir, "minidump_03.dmp");
            string newestRich = Path.Combine(testerCrashDir, "minidump_03.rich.dmp");

            File.WriteAllBytes(old1, new byte[1200]);
            File.SetLastWriteTimeUtc(old1, baseUtc);

            File.WriteAllBytes(old2, new byte[1200]);
            File.SetLastWriteTimeUtc(old2, baseUtc.AddMinutes(5));

            File.WriteAllBytes(newestTier1, new byte[600]);
            File.SetLastWriteTimeUtc(newestTier1, baseUtc.AddMinutes(10));

            File.WriteAllBytes(newestRich, new byte[1400]);
            File.SetLastWriteTimeUtc(newestRich, baseUtc.AddMinutes(10));

            // Total = 4400 bytes; cap = 2500 bytes; newest pair (03.dmp + 03.rich.dmp = 2000 bytes) is unacknowledged
            int testerDeleted = DiagnosticBundleService.EnforceLocalCrashRetention(standaloneClient, lastAcknowledgedUtc: null, maxTotalBytes: 2500);

            Assert.Equal(2, testerDeleted);
            Assert.False(File.Exists(old1));
            Assert.False(File.Exists(old2));
            Assert.True(File.Exists(newestTier1), "Newest unacknowledged tier-1 dump must be protected");
            Assert.True(File.Exists(newestRich), "Newest unacknowledged companion .rich.dmp must be protected");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MinidumpReader_HandlesCrashStreamAndLiveFreezeSnapshotCleanly()
    {
        // Protects: Exact Stream 4 + Stream 6 RVA triangulation vs Stream-6-less freeze snapshot without fake 0xC0000005.
        ulong baseAddr = 0x00007FF920000000UL;
        byte[] crashBytes = BuildSyntheticMdmp(
            moduleFullPath: @"D:\HoN\cgame_x64.dll",
            moduleBase: baseAddr,
            moduleSize: 0x00500000u,
            includeExceptionStream: true,
            exceptionCode: 0xC0000005u,
            exceptionAddress: baseAddr + 0x14B2A0UL);

        using (MemoryStream crashStream = new(crashBytes, writable: false))
        {
            MinidumpTriageResult crash = MinidumpReader.Inspect(crashStream);
            Assert.True(crash.IsValidDump);
            Assert.Equal("CRASH_TRIAGED", crash.Status);
            Assert.Equal("cgame_x64.dll+0x14B2A0", crash.TargetFormatted);
            Assert.Equal("0xC0000005", crash.ExceptionCodeHex);
            Assert.Equal("STATUS_ACCESS_VIOLATION", crash.ExceptionName);
        }

        byte[] freezeBytes = BuildSyntheticMdmp(
            moduleFullPath: @"D:\HoN\hon_x64.exe",
            moduleBase: 0x00007FF600000000UL,
            moduleSize: 0x00200000u,
            includeExceptionStream: false);

        using (MemoryStream freezeStream = new(freezeBytes, writable: false))
        {
            MinidumpTriageResult freeze = MinidumpReader.Inspect(freezeStream);
            Assert.True(freeze.IsValidDump);
            Assert.Equal("LIVE_SNAPSHOT_NO_EXCEPTION", freeze.Status);
            Assert.Equal("0x00000000", freeze.ExceptionCodeHex);
            Assert.DoesNotContain("0xC0000005", freeze.ExceptionCodeHex);
            Assert.Equal("hon_x64.exe", freeze.FaultingModule);
        }
    }

    [Fact]
    public void LocateLatestCrashDumpPath_SelectsNewerK2UserdataUuidDumpOverStaleNativeGuardDumpAndHonorsMinWriteTime()
    {
        // protects: Post-session crash supervision and cold-boot recovery banner accuracy across both NativeGuard (logs/crash) and K2 CCrashReporter (userdata/Heroes of Newerth x64/<mod>/<uuid>.dmp).
        // fails_when: WILLOWMAKER ignores userdata/<mod>/*.dmp or returns a stale dump older than launchStartUtc - 5s.
        // seam: Real filesystem DiagnosticBundleService.LocateLatestCrashDumpPath directory and timestamp filter.
        string tempClient = Path.Combine(Path.GetTempPath(), $"Kongor_DumpLocate_{Guid.NewGuid():N}");
        string nativeCrashDir = Path.Combine(tempClient, "logs", "crash");
        string k2OverrideDir = Path.Combine(tempClient, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(nativeCrashDir);
        Directory.CreateDirectory(k2OverrideDir);

        try
        {
            DateTime staleTimeUtc = new(2026, 10, 27, 2, 47, 51, DateTimeKind.Utc);
            DateTime sessionLaunchUtc = new(2026, 10, 27, 5, 49, 0, DateTimeKind.Utc);
            DateTime freshCrashUtc = new(2026, 10, 27, 5, 50, 29, DateTimeKind.Utc);

            string staleNativeDump = Path.Combine(nativeCrashDir, "minidump_20261027_024751.dmp");
            File.WriteAllBytes(staleNativeDump, [0x4D, 0x44, 0x4D, 0x50]);
            File.SetLastWriteTimeUtc(staleNativeDump, staleTimeUtc);

            string freshK2UuidDump = Path.Combine(k2OverrideDir, "1c3c37cd-fa79-430c-b5ce-e6a4b218e00c.dmp");
            File.WriteAllBytes(freshK2UuidDump, [0x4D, 0x44, 0x4D, 0x50]);
            File.SetLastWriteTimeUtc(freshK2UuidDump, freshCrashUtc);

            // 1. With launch threshold, selects the fresh K2 UUID dump in userdata/.../override/
            string? resolvedForCrashSession = DiagnosticBundleService.LocateLatestCrashDumpPath(
                tempClient,
                minWriteTimeUtc: new DateTimeOffset(sessionLaunchUtc.AddSeconds(-5), TimeSpan.Zero));
            Assert.Equal(Path.GetFullPath(freshK2UuidDump), Path.GetFullPath(resolvedForCrashSession!));

            // 2. A clean subsequent session launched AFTER freshCrashUtc must NOT pick up old dumps
            DateTime cleanSubsequentLaunchUtc = freshCrashUtc.AddMinutes(10);
            string? resolvedForCleanSession = DiagnosticBundleService.LocateLatestCrashDumpPath(
                tempClient,
                minWriteTimeUtc: new DateTimeOffset(cleanSubsequentLaunchUtc.AddSeconds(-5), TimeSpan.Zero));
            Assert.Null(resolvedForCleanSession);
        }
        finally
        {
            if (Directory.Exists(tempClient)) Directory.Delete(tempClient, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_WithNewerOversizeK2UuidDumpAndStaleTier1Dump_TriagesNewestDumpAndExcludesStaleMinidump()
    {
        // protects: Discord diagnostic zip triage fidelity so a newer >4 MB K2 CCrashReporter <uuid>.dmp is triaged accurately and never paired with an unrelated hours-old minidump_*.dmp.
        // fails_when: ExportBundleAsync reports the stale minidump's RVA or embeds a stale minidump_*.dmp written outside +-30s of the crash event.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + MinidumpReader + ZipArchive packaging.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_MixedDumps_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string nativeCrashDir = Path.Combine(clientRoot, "logs", "crash");
        string k2OverrideDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(nativeCrashDir);
        Directory.CreateDirectory(k2OverrideDir);

        try
        {
            DateTime staleUtc = new(2026, 10, 27, 2, 47, 51, DateTimeKind.Utc);
            DateTime freshUtc = new(2026, 10, 27, 5, 50, 29, DateTimeKind.Utc);

            // Stale <4 MB Tier-1 minidump (cgame_x64.dll+0x111111) from 3 hours earlier
            ulong staleBase = 0x00007FF810000000UL;
            byte[] staleBytes = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\cgame_x64.dll",
                moduleBase: staleBase,
                moduleSize: 0x00500000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: staleBase + 0x111111UL);
            string stalePath = Path.Combine(nativeCrashDir, "minidump_20261027_024751.dmp");
            File.WriteAllBytes(stalePath, staleBytes);
            File.SetLastWriteTimeUtc(stalePath, staleUtc);

            // Fresh >4 MB K2 CCrashReporter <uuid>.dmp (k2_x64.dll+0xC0C545)
            ulong freshBase = 0x00007FF930000000UL;
            byte[] freshBytes = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: freshBase,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: freshBase + 0xC0C545UL,
                extraStackBytes: new byte[4_300_000]);
            string freshUuidPath = Path.Combine(k2OverrideDir, "1c3c37cd-fa79-430c-b5ce-e6a4b218e00c.dmp");
            File.WriteAllBytes(freshUuidPath, freshBytes);
            File.SetLastWriteTimeUtc(freshUuidPath, freshUtc);

            DiagnosticBundleResult bundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "base;game;override",
                masterServerHost: "127.0.0.1:55555",
                cdnHost: "127.0.0.1:55555/cdn",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: freshUuidPath);

            Assert.True(bundle.Success);
            Assert.Contains("k2_x64.dll+0xC0C545", bundle.MarkdownSnippet);
            Assert.DoesNotContain("cgame_x64.dll+0x111111", bundle.MarkdownSnippet);

            using ZipArchive zip = ZipFile.OpenRead(bundle.ZipPath);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));

            using StreamReader summaryReader = new(zip.GetEntry("CRASH_SUMMARY.md")!.Open());
            string summaryMd = summaryReader.ReadToEnd();
            Assert.Contains("k2_x64.dll+0xC0C545", summaryMd);
            Assert.DoesNotContain("1c3c37cd-fa79-430c-b5ce-e6a4b218e00c", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("full-memory dump is preserved", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cgame_x64.dll+0x111111", summaryMd);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_ZeroPiiCanary_EnforcesAllowlistAcrossLogsConfigsZipHeadersAndMinidump()
    {
        // protects: End-to-end Zero-PII guarantee across exported Kongor_Diag_*.zip text logs, configs, zip headers, and binary MDMP streams.
        // fails_when: Any username, mismatched profile folder, custom drive path, 3rd-party personal app path, other player nickname, chat line, UUID, audio device name, timezone offset, or unallowlisted MDMP stream leaks into the zip, OR C++ Class::Method symbols are corrupted as IPv6.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + DiagnosticRedactor + MinidumpPrivacySanitizer + MinidumpReader + ZipArchive binary inspection.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ZeroPiiCanary_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "JaneDoe_Profile", "PycharmProjects", "ProjectKongor", "games", "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string gameCfgDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game");
        string kongorCfgDir = Path.Combine(clientRoot, "userdataKONGOR");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(gameCfgDir);
        Directory.CreateDirectory(kongorCfgDir);

        try
        {
            DateTimeOffset fixedUtc = new(2026, 11, 8, 20, 22, 12, TimeSpan.Zero);
            FixedUtcTimeProvider fakeTime = new(fixedUtc);

            // 1. Multi-stream synthetic MDMP with Stream 3, 4 (4 modules + RSDS PDB), 5 (2 thread stacks), 6, 7, 12 (HandleData), 13 (FunctionTable heap slack), 21 (SystemTimeInfo)
            ulong cgameBase = 0x00007FF840000000UL;
            ulong faultAddr = cgameBase + 0xB1D17UL;
            ulong returnAddrOnStack = cgameBase + 0x44220UL;
            byte[] canaryDmp = BuildMultiStreamCanaryMdmp(
                faultingModulePath: $@"{clientRoot}\cgame_x64.dll",
                pdbPath: @"D:\BuildAgent\JaneDoe\work\cgame_x64.pdb",
                thirdPartyModulePath: @"C:\Program Files\StreamRecorderApp\1.36.6\plugins\game_detour_64.dll",
                shortThirdPartyModulePath: "h3p.dll",
                cgameBase: cgameBase,
                faultAddr: faultAddr,
                returnAddrOnStack: returnAddrOnStack,
                crashingStackAsciiLeak: @"PATH=C:\Users\JaneDoe_Profile\.dotnet;D:\JaneSecretDrive\yCharm 2024.3\bin;", // kongor-security-ignore: synthetic PII redaction test fixture
                crashingStackUtf16Leak: "[ALL] OpponentBravo: secret phone 555-0199!",
                otherThreadStackLeak: "BackgroundWorkerSecretToken_998877!",
                handleStreamLeak: @"\Device\HarddiskVolume3\Users\JaneDoe_Profile\Desktop\tax_return.pdf",
                functionTableHeapSlackLeak: @"doe\.cache\lts\JaneSecretIde 2026.",
                timezoneStandardName: "Central Standard Time",
                timezoneDaylightName: "Central Daylight Time");

            string dmpPath = Path.Combine(crashDir, "minidump_20261108_202212.dmp");
            await File.WriteAllBytesAsync(dmpPath, canaryDmp);
            File.SetLastWriteTimeUtc(dmpPath, fixedUtc.UtcDateTime);

            // 2. Console log with local wall-clock [14:22:05], C++ :: symbols, multi-player names, chat, audio device, and custom drive paths
            string consoleText = $"""
                [14:22:00] Vid: CVid::StartDriver initialized on D:\JaneSecretDrive\Games\HoN\vid_d3d9_x64.dll
                [14:22:01] Res: CTexture::Load loaded [GAME_DIR] & C:\Users\JöhnDöe\CustomMods\hero.tex
                [14:22:02] Sound: Using driver 0: "Speakers (Jane's AirPods Pro)"
                [14:22:03] UI: IWidget::ExecuteLuaCallback('main_login') nominal
                [14:22:05] CGame: [NEXUS_TELEMETRY] hero=Bubbles;player=HeroTester;level=5;mode=normal;status=InGame
                [14:22:06] CGame: ^g92|HeroTester|4
                [14:22:07] CGame: ^g104|TeammateAlpha|2
                [14:22:08] CGame: [HOMEPAGE_LUA] AccountInfo watch fired -> accountId: 92, nickname: HeroTester
                [14:22:09] [ALL] OpponentBravo: meet me in Seattle!
                [14:22:10] [WHISPER] SecretFriend99: hey Jane!
                """;
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "console.log"), consoleText, Encoding.Unicode);

            // 3. WILLOWMAKER.log & willowmaker_crashes.log & hon_x64_d3d9.log (UUIDs, assembly versions, hardware UUIDs, Discord RPC & OAuth handles, bracket clocks)
            string wmLog = """
                [01:58:28] [ASSET_PREWARM] Hydrating local cache manifest
                [2026-11-08T14:22:01.123-06:00] [SETTINGS] Synchronized display settings in account profile b314b9c8-d31c-49ec-a8d1-3fb5335aeaea.cfg
                [2026-11-08T14:22:02.456-06:00] [AUDIO] Selected input Microphone (Jane's AirPods Pro)
                [2026-11-08T14:22:03.789-06:00] [NATIVE] Offline/P2P nickname 'HeroTester' synced (prev='OldNick77')
                [2026-11-08T14:22:04.100-06:00] [INFO] Identified active player: AltAccountQA
                [2026-11-08T14:22:04.200-06:00] [INFO] Identified active player: [CLAN9]RealHandle99
                [2026-11-08T14:22:04.300-06:00] [INFO] [Discord RPC] AltAccountQA (In Lobby) | In Main Menu
                [2026-11-08T14:22:04.400-06:00] [INFO] [Discord RPC] [CLAN9]RealHandle99 as Bubbles (Lvl 5) | Normal Mode
                [2026-11-08T14:22:04.500-06:00] [INFO] [DiscordRPC] Direct Telemetry Transition to Queue: AltAccountQA in Queue
                [2026-11-08T14:22:04.600-06:00] [INFO] [Discord RPC] Playing as Bubbles (Lvl 5) | Normal Mode
                [2026-11-08T14:22:04.700-06:00] [INFO] Successfully authenticated as 'RealDiscordUser' (Account: 'RealHandle99')
                [2026-11-08T14:22:04.800-06:00] [INFO] [DiscordRPC] Login detected: Account ID 884422, Name '[CLAN9]RealHandle99'
                """;
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "WILLOWMAKER.log"), wmLog);

            string wmCrashLog = """
                System.InvalidOperationException at Avalonia.Base, Version=11.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e
                Module Path: C:\Program Files\StreamRecorderApp\1.36.6\plugins\game_detour_64.dll
                """;
            await File.WriteAllTextAsync(Path.Combine(crashDir, "willowmaker_crashes.log"), wmCrashLog);

            string dxvkLog = """
                info:  Found device: NVIDIA GeForce RTX 4060 Ti (NVIDIA 596.49.0)
                info:    deviceUUID        : 11223344-5566-7788-99aa-bbccddeeff00
                info:    driverUUID        : aabbccdd-1122-3344-5566-77889900aabb
                """;
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "hon_x64_d3d9.log"), dxvkLog);

            // 4. Raw startup.cfg (with private channels, match ID, keybind chat macros, and string-injected vid_ CVar) + launcher_settings.json
            string startupCfg = """
                SetSave "vid_width" "1920"
                SetSave "vid_height" "1080"
                SetSave "vk_enable" "true"
                SetSave "d3d9_shaderModel" "high"
                SetSave "svr_requestSessionCookieTimeout" "0"
                SetSave "vid_customNote" "D:\JaneSecretDrive\personal_note"
                SetSave "_chat_saved_channels" "TERMINAL,KONGOR,JANE_PRIVATE_CLAN"
                SetSave "replay_view_stats_mid" "2060568725"
                SetSave "sound_output" "Speakers (Jane's AirPods Pro)"
                SetSave "login_name" "HeroTester"
                SetSave "login_password" "SuperSecretPw99!"
                BindImpulse "GAME" "T" "Say MyRealPhone5550199"
                """;
            await File.WriteAllTextAsync(Path.Combine(gameCfgDir, "startup.cfg"), startupCfg);

            string launcherSettingsJson = """
                {
                  "swapchainBackend": "DXVK (Vulkan WSI)",
                  "fpsCap": 240,
                  "allowThirdPartyVulkanOverlays": false,
                  "lastPlayerNickname": "HeroTester",
                  "customMasterServerAddress": "https://private.janedoe.example.com:55555",
                  "favoriteModIds": ["jane_private_mod"]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(kongorCfgDir, "launcher_settings.json"), launcherSettingsJson);

            // Act
            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "base;game;override;JaneDoePrivateMod",
                masterServerHost: "127.0.0.1:55555",
                cdnHost: "127.0.0.1:55555/cdn",
                timeProvider: fakeTime,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: dmpPath);

            Assert.True(result.Success);
            Assert.Contains("[CUSTOM_MOD]", result.MarkdownSnippet);
            Assert.DoesNotContain("JaneDoePrivateMod", result.MarkdownSnippet, StringComparison.OrdinalIgnoreCase);

            string[] forbiddenTokens =
            [
                "JaneDoe",
                "JaneDoePrivateMod",
                "JöhnDöe",
                "JaneSecretDrive",
                "PycharmProjects",
                "StreamRecorderApp",
                "game_detour_64",
                "BuildAgent",
                "HeroTester",
                "OldNick77",
                "AltAccountQA",
                "RealHandle99",
                "CLAN9",
                "RealDiscordUser",
                "884422",
                "[01:58:28]",
                "TeammateAlpha",
                "OpponentBravo",
                "SecretFriend99",
                "meet me in Seattle",
                "555-0199",
                "5550199",
                "JANE_PRIVATE_CLAN",
                "2060568725",
                "vid_customNote",
                "personal_note",
                "SuperSecretPw99",
                "b314b9c8-d31c-49ec-a8d1-3fb5335aeaea",
                "11223344-5566-7788-99aa-bbccddeeff00",
                "aabbccdd-1122-3344-5566-77889900aabb",
                "AirPods",
                "Central Standard Time",
                "Central Daylight Time",
                "tax_return",
                "BackgroundWorkerSecretToken",
                "yCharm 2024",
                "JaneSecretIde",
                @"doe\.cache",
                "h3p.dll",
                ".dotnet",
                "janedoe.example.com",
                "jane_private_mod",
                @"C:\",
                @"D:\",
                "C:/",
                "D:/",
                @"\Users\",
                "/Users/"
            ];

            foreach (string forbidden in forbiddenTokens)
            {
                Assert.False(
                    result.MarkdownSnippet.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Zero-PII Canary violation in Discord clipboard MarkdownSnippet: '{forbidden}' leaked. Run scripts/Invoke-KongorTestLock.ps1 -- dotnet test tests/unit/apps/WILLOWMAKER.Tests");
            }

            PrivacyScrubContext verifySnippetContext = new(clientRoot, outDir, faultingModuleName: "cgame_x64.dll", strictZeroPiiBundleMode: true);
            string? snippetCanaryViolation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
                "discord_clipboard_snippet.md",
                Encoding.UTF8.GetBytes(result.MarkdownSnippet),
                verifySnippetContext,
                isBinaryMinidump: false);
            Assert.Null(snippetCanaryViolation);

            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            Assert.NotEmpty(zip.Entries);

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                // Zip header timestamp must be normalized to UTC (20:22), never local wall-clock
                Assert.Equal(20, entry.LastWriteTime.Hour);
                Assert.Equal(22, entry.LastWriteTime.Minute);

                using MemoryStream entryMs = new();
                using (Stream s = entry.Open())
                {
                    await s.CopyToAsync(entryMs);
                }
                byte[] rawBytes = entryMs.ToArray();
                string utf8View = Encoding.UTF8.GetString(rawBytes);
                string utf16View = Encoding.Unicode.GetString(rawBytes);

                foreach (string forbidden in forbiddenTokens)
                {
                    Assert.False(
                        utf8View.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"Zero-PII Canary violation (UTF-8): '{forbidden}' leaked in zip entry '{entry.FullName}'. Run scripts/Invoke-KongorTestLock.ps1 -- dotnet test tests/unit/apps/WILLOWMAKER.Tests");
                    Assert.False(
                        utf16View.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"Zero-PII Canary violation (UTF-16LE): '{forbidden}' leaked in zip entry '{entry.FullName}'. Run scripts/Invoke-KongorTestLock.ps1 -- dotnet test tests/unit/apps/WILLOWMAKER.Tests");
                }
            }

            // Verify C++ Class::Method symbols & assembly versions were NOT corrupted
            string scrubbedConsole = ReadZipEntryText(zip, "logs/console.tail.log");
            Assert.Contains("CVid::StartDriver", scrubbedConsole);
            Assert.Contains("CTexture::Load", scrubbedConsole);
            Assert.Contains("IWidget::ExecuteLuaCallback", scrubbedConsole);
            Assert.DoesNotContain("[REDACTED_IPV6]", scrubbedConsole);
            Assert.Contains("[+00:00:00]", scrubbedConsole);
            Assert.DoesNotContain("[14:22:", scrubbedConsole);

            // Verify distinct salted pseudonyms within the same bundle for HeroTester vs TeammateAlpha
            Assert.Contains("[PLAYER_1]", scrubbedConsole);
            Assert.Contains("[PLAYER_2]", scrubbedConsole);

            string scrubbedWmLog = ReadZipEntryText(zip, "logs/WILLOWMAKER.tail.log");
            Assert.Contains("[+00:00:00] [ASSET_PREWARM]", scrubbedWmLog);
            Assert.Contains("Playing as Bubbles (Lvl 5)", scrubbedWmLog);

            string scrubbedWmCrash = ReadZipEntryText(zip, "logs/willowmaker_crashes.log");
            Assert.Contains("Version=11.0.0.0", scrubbedWmCrash);
            Assert.DoesNotContain("Version=[REDACTED_IP]", scrubbedWmCrash);

            // Verify allowlist config projection
            string scrubbedStartup = ReadZipEntryText(zip, "config/startup.cfg");
            Assert.Contains("vid_width", scrubbedStartup);
            Assert.Contains("1920", scrubbedStartup);
            Assert.Contains("vk_enable", scrubbedStartup);
            Assert.DoesNotContain("BindImpulse", scrubbedStartup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_chat_saved_channels", scrubbedStartup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("replay_view_stats_mid", scrubbedStartup, StringComparison.OrdinalIgnoreCase);

            string scrubbedLauncherJson = ReadZipEntryText(zip, "config/launcher_settings.json");
            Assert.Contains("DXVK (Vulkan WSI)", scrubbedLauncherJson);
            Assert.DoesNotContain("lastPlayerNickname", scrubbedLauncherJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customMasterServerAddress", scrubbedLauncherJson, StringComparison.OrdinalIgnoreCase);

            // Verify sanitized minidump remains structurally valid for MinidumpReader & WinDbg + preserves x64 return pointer on stack
            ZipArchiveEntry? packagedDmpEntry = Assert.Single(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
            using MemoryStream dmpVerifyMs = new();
            using (Stream ds = packagedDmpEntry.Open())
            {
                await ds.CopyToAsync(dmpVerifyMs);
            }
            byte[] sanitizedDmpBytes = dmpVerifyMs.ToArray();
            dmpVerifyMs.Position = 0;

            MinidumpTriageResult postSanitizeTriage = MinidumpReader.Inspect(dmpVerifyMs);
            Assert.True(postSanitizeTriage.IsValidDump);
            Assert.Equal("cgame_x64.dll+0xB1D17", postSanitizeTriage.TargetFormatted, ignoreCase: true);
            Assert.Contains(postSanitizeTriage.LoadedModules, m => string.Equals(m, "third_party_module_1.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(postSanitizeTriage.LoadedModules, m => string.Equals(m, "m.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(postSanitizeTriage.StreamDirectories!, d => d.StreamType is 12u or 13u or 21u);

            // Compacted MemoryListStream (Stream 5) directory DataSize MUST match 4 + (1 * 16) = 20 bytes so WinDbg dbgeng accepts the stack
            MinidumpDirectoryEntry memListDir = Assert.Single(postSanitizeTriage.StreamDirectories!, d => d.StreamType == 5u);
            Assert.Equal(20u, memListDir.DataSize);

            // Ensure the 64-bit return address on the crashing thread's stack survived stack text wiping
            byte[] expectedReturnPtrBytes = BitConverter.GetBytes(returnAddrOnStack);
            Assert.True(ContainsSubsequence(sanitizedDmpBytes, expectedReturnPtrBytes), "64-bit code return address on crashing stack must remain intact for WinDbg unwinding.");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_WithFullHeapMemory64Dump_FailsClosedAndOmitsBinaryMinidump()
    {
        // protects: Fail-closed privacy fallback when a minidump contains unallowlisted full-memory streams (Memory64ListStream = 9).
        // fails_when: ExportBundleAsync embeds a raw Memory64ListStream dump into the shareable .zip instead of omitting it and preserving text RVA triage.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + MinidumpPrivacySanitizer stream rejection + README_WHAT_IS_SHARED.txt audit notice.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_FailClosedDmp_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);

        try
        {
            ulong k2Base = 0x00007FF890000000UL;
            byte[] baseDmp = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0x22AA10UL);

            // Inject StreamType = 9 (Memory64ListStream) by overwriting directory entry 0's type in a copy? Better: append directory entry
            byte[] memory64Dmp = InjectDirectoryStreamType(baseDmp, streamType: 9u);
            string dmpPath = Path.Combine(crashDir, "minidump_20261108_210000.dmp");
            await File.WriteAllBytesAsync(dmpPath, memory64Dmp);

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: dmpPath);

            Assert.True(result.Success);
            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));

            string summaryMd = ReadZipEntryText(zip, "CRASH_SUMMARY.md");
            Assert.Contains("k2_x64.dll+0x22AA10", summaryMd, StringComparison.OrdinalIgnoreCase);

            string readme = ReadZipEntryText(zip, "README_WHAT_IS_SHARED.txt");
            Assert.Contains("omitted", readme, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string ReadZipEntryText(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry? entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        using StreamReader sr = new(entry!.Open(), Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] InjectDirectoryStreamType(byte[] validMdmp, uint streamType)
    {
        // Adds an extra MINIDUMP_DIRECTORY entry pointing to trailing 32 bytes at EOF
        uint oldCount = BitConverter.ToUInt32(validMdmp, 8);
        uint oldDirRva = BitConverter.ToUInt32(validMdmp, 12);
        uint newDirRva = (uint)validMdmp.Length;
        uint payloadRva = newDirRva + ((oldCount + 1) * 12u);
        byte[] expanded = new byte[payloadRva + 32];
        Buffer.BlockCopy(validMdmp, 0, expanded, 0, validMdmp.Length);
        Buffer.BlockCopy(validMdmp, (int)oldDirRva, expanded, (int)newDirRva, (int)(oldCount * 12u));

        int appendedEntryOffset = (int)(newDirRva + (oldCount * 12u));
        BitConverter.TryWriteBytes(expanded.AsSpan(8, 4), oldCount + 1);
        BitConverter.TryWriteBytes(expanded.AsSpan(12, 4), newDirRva);
        BitConverter.TryWriteBytes(expanded.AsSpan(appendedEntryOffset, 4), streamType);
        BitConverter.TryWriteBytes(expanded.AsSpan(appendedEntryOffset + 4, 4), 32u);
        BitConverter.TryWriteBytes(expanded.AsSpan(appendedEntryOffset + 8, 4), payloadRva);
        return expanded;
    }

    private static byte[] BuildMultiStreamCanaryMdmp(
        string faultingModulePath,
        string pdbPath,
        string thirdPartyModulePath,
        string shortThirdPartyModulePath,
        ulong cgameBase,
        ulong faultAddr,
        ulong returnAddrOnStack,
        string crashingStackAsciiLeak,
        string crashingStackUtf16Leak,
        string otherThreadStackLeak,
        string handleStreamLeak,
        string functionTableHeapSlackLeak,
        string timezoneStandardName,
        string timezoneDaylightName)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);

        const int streamCount = 8; // 7 (SysInfo), 4 (Modules), 3 (Threads), 5 (MemoryList), 6 (Exception), 12 (HandleData), 13 (FunctionTable), 21 (SystemTimeInfo)
        uint dirRva = 32u;
        uint sysInfoRva = 144u;       // 56 bytes -> ends 200
        uint modListRva = 208u;       // 4 + 4 * 108 = 436 bytes -> ends 644
        uint modStringsRva = 656u;    // 4 UTF-16LE MINIDUMP_STRINGs + RSDS CvRecord -> reserve up to 2048
        uint threadListRva = 2048u;   // 4 + 2 * 48 = 100 bytes -> ends 2148
        uint memListRva = 2176u;      // 4 + 2 * 16 = 36 bytes -> ends 2212
        uint excStreamRva = 2240u;    // 168 bytes -> ends 2408
        uint ctxRva = 2432u;          // 0x4D0 (1232) bytes -> ends 3664
        uint stack1Rva = 3712u;       // 512 bytes -> ends 4224
        uint stack2Rva = 4224u;       // 256 bytes -> ends 4480
        uint handleRva = 4480u;       // 256 bytes -> ends 4736
        uint sysTimeRva = 4736u;      // 256 bytes -> ends 4992
        uint funcTableRva = 4992u;    // 256 bytes -> ends 5248

        ms.SetLength(5376);

        // Header
        bw.Seek(0, SeekOrigin.Begin);
        bw.Write(MinidumpReader.MdmpSignature);
        bw.Write(0x0000A793u);
        bw.Write((uint)streamCount);
        bw.Write(dirRva);
        bw.Write(0u);
        bw.Write(0u);
        bw.Write(0UL);

        // Directory (8 * 12 = 96 bytes at offset 32..128)
        bw.Seek((int)dirRva, SeekOrigin.Begin);
        bw.Write(7u);  bw.Write(56u);  bw.Write(sysInfoRva);
        bw.Write(4u);  bw.Write(436u); bw.Write(modListRva);
        bw.Write(3u);  bw.Write(100u); bw.Write(threadListRva);
        bw.Write(5u);  bw.Write(36u);  bw.Write(memListRva);
        bw.Write(6u);  bw.Write(168u); bw.Write(excStreamRva);
        bw.Write(12u); bw.Write(256u); bw.Write(handleRva);
        bw.Write(13u); bw.Write(256u); bw.Write(funcTableRva);
        bw.Write(21u); bw.Write(256u); bw.Write(sysTimeRva);

        // Stream 7: SystemInfo (AMD64 = 9)
        bw.Seek((int)sysInfoRva, SeekOrigin.Begin);
        bw.Write((ushort)9);

        // Write 4 module strings + RSDS CodeView PDB record
        bw.Seek((int)modStringsRva, SeekOrigin.Begin);
        uint WriteMinidumpString(string s)
        {
            uint pos = (uint)ms.Position;
            byte[] utf16 = Encoding.Unicode.GetBytes(s);
            bw.Write((uint)utf16.Length);
            bw.Write(utf16);
            bw.Write((ushort)0);
            return pos;
        }

        uint mod0NameRva = WriteMinidumpString(faultingModulePath);
        uint mod1NameRva = WriteMinidumpString(@"C:\Windows\System32\kernel32.dll");
        uint mod2NameRva = WriteMinidumpString(thirdPartyModulePath);
        uint mod3NameRva = WriteMinidumpString(shortThirdPartyModulePath);

        uint cvRecordRva = (uint)ms.Position;
        byte[] pdbUtf8 = Encoding.UTF8.GetBytes(pdbPath + "\0");
        bw.Write(Encoding.ASCII.GetBytes("RSDS"));
        bw.Write(Guid.Parse("99887766-5544-3322-1100-aabbccddeeff").ToByteArray());
        bw.Write(1u);
        bw.Write(pdbUtf8);
        uint cvRecordSize = (uint)ms.Position - cvRecordRva;

        // Stream 4: ModuleList (4 modules * 108B)
        bw.Seek((int)modListRva, SeekOrigin.Begin);
        bw.Write(4u);
        void WriteModuleEntry(ulong baseAddr, uint size, uint nameRva, uint cvSize, uint cvRva)
        {
            bw.Write(baseAddr);
            bw.Write(size);
            bw.Write(0u); // CheckSum
            bw.Write(0u); // TimeDateStamp
            bw.Write(nameRva);
            bw.Write(new byte[52]); // VS_FIXEDFILEINFO
            bw.Write(cvSize);
            bw.Write(cvRva);
            bw.Write(0u); // MiscRecord.DataSize
            bw.Write(0u); // MiscRecord.Rva
            bw.Write(0UL); // Reserved0
            bw.Write(0UL); // Reserved1
        }
        WriteModuleEntry(cgameBase, 0x00600000u, mod0NameRva, cvRecordSize, cvRecordRva);
        WriteModuleEntry(0x00007FF900000000UL, 0x00100000u, mod1NameRva, 0u, 0u);
        WriteModuleEntry(0x00007FF700100000UL, 0x00080000u, mod2NameRva, 0u, 0u);
        WriteModuleEntry(0x00007FF700200000UL, 0x00040000u, mod3NameRva, 0u, 0u);

        // Stream 3: ThreadList (2 threads * 48B: ThreadId(4), SuspendCount(4), PriorityClass(4), Priority(4), Teb(8), StackStart(8), StackDataSize(4), StackRva(4), CtxDataSize(4), CtxRva(4))
        ulong stack1Start = 0x0000008810FF0000UL;
        ulong stack2Start = 0x0000008820FF0000UL;
        bw.Seek((int)threadListRva, SeekOrigin.Begin);
        bw.Write(2u);
        // Thread 0: crashing thread 4242
        bw.Write(4242u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0UL);
        bw.Write(stack1Start); bw.Write(512u); bw.Write(stack1Rva);
        bw.Write(0x4D0u); bw.Write(ctxRva);
        // Thread 1: background thread 7777
        bw.Write(7777u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0UL);
        bw.Write(stack2Start); bw.Write(256u); bw.Write(stack2Rva);
        bw.Write(0x4D0u); bw.Write(ctxRva);

        // Stream 5: MemoryList (2 descriptors * 16B: StartOfMemoryRange(8), DataSize(4), Rva(4))
        bw.Seek((int)memListRva, SeekOrigin.Begin);
        bw.Write(2u);
        bw.Write(stack1Start); bw.Write(512u); bw.Write(stack1Rva);
        bw.Write(stack2Start); bw.Write(256u); bw.Write(stack2Rva);

        // Populate Stack 1: 8-byte aligned return address + frame pointer + ASCII leak + UTF-16LE leak
        bw.Seek((int)stack1Rva, SeekOrigin.Begin);
        bw.Write(returnAddrOnStack);            // [0..7] valid module code pointer
        bw.Write(stack1Start + 0x80UL);         // [8..15] valid stack frame pointer
        bw.Write(Encoding.ASCII.GetBytes(crashingStackAsciiLeak));
        bw.Seek((int)stack1Rva + 160, SeekOrigin.Begin);
        bw.Write(Encoding.Unicode.GetBytes(crashingStackUtf16Leak));

        // Populate Stack 2: background worker thread stack
        bw.Seek((int)stack2Rva, SeekOrigin.Begin);
        bw.Write(Encoding.ASCII.GetBytes(otherThreadStackLeak));

        // Stream 6: ExceptionStream (Thread 4242)
        bw.Seek((int)excStreamRva, SeekOrigin.Begin);
        bw.Write(4242u);
        bw.Write(0u);
        bw.Write(0xC0000005u);
        bw.Write(0u);
        bw.Write(0UL);
        bw.Write(faultAddr);
        bw.Write(2u);
        bw.Write(0u);
        bw.Write(0UL); // READ
        bw.Write(0x0000000000000028UL);
        bw.Seek((int)excStreamRva + 160, SeekOrigin.Begin);
        bw.Write(0x4D0u);
        bw.Write(ctxRva);

        bw.Seek((int)ctxRva + 0x78, SeekOrigin.Begin);
        for (int r = 0; r < 16; r++) bw.Write(0UL);
        bw.Write(faultAddr); // RIP

        // Stream 12: HandleDataStream leak
        bw.Seek((int)handleRva, SeekOrigin.Begin);
        bw.Write(Encoding.Unicode.GetBytes(handleStreamLeak));

        // Stream 13: FunctionTableStream unzeroed heap-slack leak (Windows 11 dbgcore.dll descriptor slack)
        bw.Seek((int)funcTableRva, SeekOrigin.Begin);
        bw.Write(Encoding.Unicode.GetBytes(functionTableHeapSlackLeak));

        // Stream 21: SystemTimeInfoStream timezone leak
        bw.Seek((int)sysTimeRva, SeekOrigin.Begin);
        bw.Write(Encoding.Unicode.GetBytes(timezoneStandardName));
        bw.Seek((int)sysTimeRva + 96, SeekOrigin.Begin);
        bw.Write(Encoding.Unicode.GetBytes(timezoneDaylightName));

        bw.Flush();
        return ms.ToArray();
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task HandleGameProcessTerminationAsync_OnNativeCrash_ResetsLaunchInProgressAndAutoGeneratesDiagnosticZip()
    {
        // protects: Post-crash launcher recovery contract so hon_x64.exe crashes (e.g. 0xC0000005) immediately re-enable Play/Editor buttons, surface the crash banner, and automatically write Kongor_Diag_*.zip to disk.
        // fails_when: HandleGameProcessTerminationAsync leaves LaunchIsInProgress=true or fails to auto-export a scrubbed diagnostic .zip upon detecting a crash exit code and minidump.
        // seam: Real MainViewModel.HandleGameProcessTerminationAsync + MinidumpReader + DiagnosticBundleService.ExportBundleAsync + ZipArchive inspection.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_AutoCrashZip_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string k2OverrideDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(k2OverrideDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
            ulong k2Base = 0x00007FFEF0050000UL;
            byte[] dmpBytes = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01200000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0xC4BB44UL);
            string dmpPath = Path.Combine(k2OverrideDir, "944aeb99-c6d2-47bf-9b00-a5783e4dd6da.dmp");
            File.WriteAllBytes(dmpPath, dmpBytes);
            File.SetLastWriteTimeUtc(dmpPath, DateTime.UtcNow);

            var vm = new MainViewModel
            {
                LaunchIsInProgress = true
            };

            await vm.HandleGameProcessTerminationAsync(
                exitCode: 0xC0000005u,
                launchStartUtc: launchStartUtc,
                clientRoot: clientRoot,
                outputDirectoryOverride: outDir);

            Assert.False(vm.LaunchIsInProgress);
            Assert.True(vm.HasCrashReportBanner);
            Assert.Contains("k2_x64.dll+0xC4BB44", vm.CrashReportTargetText);
            Assert.False(string.IsNullOrWhiteSpace(vm.LastAutoExportedCrashZipPath));
            Assert.True(File.Exists(vm.LastAutoExportedCrashZipPath!));
            Assert.Contains(Path.GetFileName(vm.LastAutoExportedCrashZipPath!), vm.CrashReportBannerText);

            using ZipArchive zip = ZipFile.OpenRead(vm.LastAutoExportedCrashZipPath!);
            using StreamReader summaryReader = new(zip.GetEntry("CRASH_SUMMARY.md")!.Open());
            string summaryMd = summaryReader.ReadToEnd();
            Assert.Contains("k2_x64.dll+0xC4BB44", summaryMd);
            Assert.Contains("0xC0000005", summaryMd);

            // Metadata must be preserved in settings
            var persistedSettings = LauncherSettingsService.Load(clientRoot);
            Assert.Contains("k2_x64.dll", persistedSettings.LastCrashFaultSummary);
            Assert.True(persistedSettings.ConsecutiveCrashCount >= 1);

            // Next cold boot must NOT re-flag an already-supervised & auto-packaged crash
            var nextBootVm = new MainViewModel();
            nextBootVm.HasCrashReportBanner = false;
            nextBootVm.CheckColdBootCrashSentinel(clientRoot);
            Assert.False(nextBootVm.HasCrashReportBanner);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_WithRealMinidumpAndWin11SecondaryDxvkSkipLog_PreservesNativeModuleRvaAndFormatsSingleHexPrefix()
    {
        // protects: Real native minidump triage slug & summary accuracy on Windows 11 systems where DXVK logs benign 'Skipping: Device does not support Vulkan 1.3' on secondary Microsoft Direct3D12 / Basic Render Driver adapters before 'Creating device:' succeeds on the discrete GPU.
        // fails_when: ExportBundleAsync mislabels a k2_x64.dll crash as 'dxvk_no_vulkan13_adapter', injects a false Vulkan 1.3 driver remediation, or formats AV target addresses with a double '0x0x' prefix.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + MinidumpReader + ZipArchive CRASH_SUMMARY.md inspection.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_Win11DxvkSkip_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string k2OverrideDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(k2OverrideDir);

        try
        {
            const string win11HealthyDxvkLog = """
                info:  Game: hon_x64.exe
                info:  DXVK: v3.0.2
                info:  Found device: NVIDIA GeForce RTX 3070 (NVIDIA 596.49.0)
                info:  Found device: Microsoft Direct3D12 (NVIDIA GeForce RTX 3070) ( 26.2.99)
                info:    Skipping: Device does not support Vulkan 1.3
                info:  Found device: Microsoft Direct3D12 (Microsoft Basic Render Driver) ( 26.2.99)
                info:    Skipping: Device does not support Vulkan 1.3
                info:  Creating device:
                info:  NVIDIA GeForce RTX 3070:
                info:    Driver   : NVIDIA 596.49.0
                """;
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "hon_x64_d3d9.log"), win11HealthyDxvkLog);

            ulong k2Base = 0x00007FFE8E210000UL;
            byte[] dmpBytes = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01200000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0xC4BB44UL,
                avAccessKind: 0UL,
                avTargetAddr: 0x000000C8000000C8UL);
            string dmpPath = Path.Combine(k2OverrideDir, "944aeb99-c6d2-47bf-9b00-a5783e4dd6da.dmp");
            await File.WriteAllBytesAsync(dmpPath, dmpBytes);

            DiagnosticBundleResult crashBundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: dmpPath);

            Assert.True(crashBundle.Success);
            string zipFileName = Path.GetFileName(crashBundle.ZipPath);
            Assert.Contains("k2_x64.dll_0xC4BB44", zipFileName);
            Assert.DoesNotContain("dxvk_no_vulkan13_adapter", zipFileName, StringComparison.OrdinalIgnoreCase);

            using (ZipArchive zip = ZipFile.OpenRead(crashBundle.ZipPath))
            {
                using StreamReader summaryReader = new(zip.GetEntry("CRASH_SUMMARY.md")!.Open());
                string summaryMd = await summaryReader.ReadToEndAsync();
                Assert.Contains("k2_x64.dll+0xC4BB44", summaryMd);
                Assert.Contains("`READ` on `0x000000C8000000C8`", summaryMd);
                Assert.DoesNotContain("0x0x", summaryMd, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Device does not support Vulkan 1.3", summaryMd, StringComparison.OrdinalIgnoreCase);
            }

            // Even with no minidump, a DXVK log where 'Creating device:' succeeded must NOT falsely trigger dxvk_no_vulkan13_adapter
            File.Delete(dmpPath);
            DiagnosticBundleResult noDumpBundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                processExitCode: 0xC0000005u);

            Assert.DoesNotContain("dxvk_no_vulkan13_adapter", Path.GetFileName(noDumpBundle.ZipPath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DiscordPresenceService_DisposeUnderSingleThreadedSyncContext_DoesNotDeadlock()
    {
        // protects: Post-game UI thread responsiveness when WILLOWMAKER disposes DiscordPresenceService after WaitForExitAsync on AvaloniaSynchronizationContext.
        // fails_when: Synchronous Dispose() or DisposeAsync() captures SynchronizationContext.Current while tearing down SignalR HubConnection, deadlocking the UI thread before crash zip export can run.
        // seam: Real DiscordPresenceService + real Microsoft.AspNetCore.SignalR.Client.HubConnection + non-pumping single-threaded SynchronizationContext.
        Task runOnBlockedUiThread = Task.Run(() =>
        {
            var prevCtx = System.Threading.SynchronizationContext.Current;
            var blockedUiCtx = new NonPumpingUiSyncContext();
            System.Threading.SynchronizationContext.SetSynchronizationContext(blockedUiCtx);
            try
            {
                var service = new DiscordPresenceService();
                var hub = new HubConnectionBuilder()
                    .WithUrl("http://127.0.0.1:1/presence")
                    .Build();

                var hubField = typeof(DiscordPresenceService)
                    .GetField("_hubConnection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(hubField);
                hubField!.SetValue(service, hub);

                service.Dispose();
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        });

                await runOnBlockedUiThread.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExportBundleAsync_DefaultOutputDirectory_SavesInsideClientLogsCrashReportsAndRelativizesMillisecondClocks()
    {
        // protects: Project-first diagnostic zip output (<clientRoot>/logs/crash_reports, isolated from raw dumps in logs/crash) + [HH:MM:SS.fff] millisecond clock relativization.
        // fails_when: ExportBundleAsync writes to %LOCALAPPDATA%\Kongor\crash_reports when <clientRoot> is writable, or leaves raw [23:14:45.900] clocks in vid_vulkan_spy.log.
        // seam: Real DiagnosticBundleService.ExportBundleAsync (outputDirectoryOverride: null) + filesystem + ZipArchive inspection.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ProjectLocalOut_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        Directory.CreateDirectory(Path.Combine(clientRoot, "logs"));

        try
        {
            const string spyLog = """
                [23:14:45.900] [VID_VULKAN_SPY] BeginFrame #1
                [23:14:46.125] [VID_VULKAN_SPY] BeginFrame #2
                """;
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "logs", "vid_vulkan_spy.log"), spyLog);

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: false,
                outputDirectoryOverride: null,
                revealInFileManager: false);

            Assert.True(result.Success);
            string expectedDir = Path.GetFullPath(Path.Combine(clientRoot, "logs", "crash_reports"));
            string actualDir = Path.GetFullPath(Path.GetDirectoryName(result.ZipPath)!);
            Assert.Equal(expectedDir, actualDir, ignoreCase: OperatingSystem.IsWindows());
            Assert.True(File.Exists(result.ZipPath));
            Assert.Empty(Directory.GetFiles(expectedDir, ".kongor_write_probe_*"));

            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            string scrubbedSpy = ReadZipEntryText(zip, "logs/vid_vulkan_spy.log");
            Assert.Contains("[+00:00:00.900]", scrubbedSpy);
            Assert.Contains("[+00:00:01.125]", scrubbedSpy);
            Assert.DoesNotContain("[23:14:", scrubbedSpy);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetReportOutputDirectory_WhenClientRootUnwritableOrInsideMacAppBundle_FallsBackToOsReportDirectory()
    {
        // protects: Safe OS fallback (%LOCALAPPDATA% / ~/Library/Logs / $XDG_STATE_HOME) when <clientRoot> is read-only/blocked or inside a macOS .app / AppTranslocation mount.
        // fails_when: GetReportOutputDirectory throws IOException on unwritable game directories or attempts to create logs/crash_reports inside .app/Contents.
        // seam: Real DiagnosticBundleService.GetReportOutputDirectory + DeleteOnClose write probe + filesystem conflict.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ReadOnlyFallback_{Guid.NewGuid():N}");
        string blockedClientRoot = Path.Combine(tempRoot, "blocked_client");
        Directory.CreateDirectory(blockedClientRoot);

        try
        {
            string expectedOsFallback = Path.GetFullPath(DiagnosticBundleService.GetOsFallbackReportDirectory());

            // 1. Place a regular file at '<blockedClientRoot>/logs' so creating '<blockedClientRoot>/logs/crash_reports' fails writability probe
            File.WriteAllText(Path.Combine(blockedClientRoot, "logs"), "blocking file simulating unwritable directory");
            string resolvedBlocked = Path.GetFullPath(DiagnosticBundleService.GetReportOutputDirectory(blockedClientRoot));
            Assert.Equal(expectedOsFallback, resolvedBlocked, ignoreCase: OperatingSystem.IsWindows());

            // 2. Signed macOS .app bundle & Gatekeeper AppTranslocation paths must bypass project-local creation immediately
            string macAppClient = Path.Combine(tempRoot, "ProjectKongor.app", "Contents", "MacOS");
            string resolvedMacApp = Path.GetFullPath(DiagnosticBundleService.GetReportOutputDirectory(macAppClient));
            Assert.Equal(expectedOsFallback, resolvedMacApp, ignoreCase: OperatingSystem.IsWindows());
            Assert.False(Directory.Exists(macAppClient));

            string translocatedClient = Path.Combine(tempRoot, "AppTranslocation", "88AA-BBCC", "d", "Kongor");
            string resolvedTranslocated = Path.GetFullPath(DiagnosticBundleService.GetReportOutputDirectory(translocatedClient));
            Assert.Equal(expectedOsFallback, resolvedTranslocated, ignoreCase: OperatingSystem.IsWindows());
            Assert.False(Directory.Exists(translocatedClient));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EnforceExportedReportZipRetention_SkipsGitWorkspaceAndCapsStandaloneFolderAt25NewestZips()
    {
        // protects: 25-zip disk cap in standalone playtest logs/crash_reports while strictly preserving all zips inside .git developer checkouts.
        // fails_when: Standalone folders retain > 25 Kongor_Diag_*.zip files, delete newest zips instead of oldest, or prune inside .git workspaces.
        // seam: Real DiagnosticBundleService.EnforceExportedReportZipRetention + filesystem LastWriteTimeUtc sorting.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ZipRetention_{Guid.NewGuid():N}");
        string gitClient = Path.Combine(tempRoot, "git_repo", "games", "client");
        string gitReportsDir = Path.Combine(gitClient, "logs", "crash_reports");
        string standaloneClient = Path.Combine(tempRoot, "standalone_game");
        string standaloneReportsDir = Path.Combine(standaloneClient, "logs", "crash_reports");
        Directory.CreateDirectory(Path.Combine(tempRoot, "git_repo", ".git"));
        Directory.CreateDirectory(gitReportsDir);
        Directory.CreateDirectory(standaloneReportsDir);

        try
        {
            DateTime baseUtc = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 1; i <= 30; i++)
            {
                string name = $"Kongor_Diag_20261110_{i:D6}_manual_report.zip";
                string gitZip = Path.Combine(gitReportsDir, name);
                File.WriteAllBytes(gitZip, [0x50, 0x4B, 0x05, 0x06]);
                File.SetLastWriteTimeUtc(gitZip, baseUtc.AddMinutes(i));

                string standaloneZip = Path.Combine(standaloneReportsDir, name);
                File.WriteAllBytes(standaloneZip, [0x50, 0x4B, 0x05, 0x06]);
                File.SetLastWriteTimeUtc(standaloneZip, baseUtc.AddMinutes(i));
            }

            // 1. Developer .git checkout: 0 deleted, all 30 preserved
            int gitDeleted = DiagnosticBundleService.EnforceExportedReportZipRetention(gitReportsDir, gitClient);
            Assert.Equal(0, gitDeleted);
            Assert.Equal(30, Directory.GetFiles(gitReportsDir, "Kongor_Diag_*.zip").Length);

            // 2. Standalone install: oldest 5 pruned (#000001..#000005), newest 25 (#000006..#000030) retained
            int standaloneDeleted = DiagnosticBundleService.EnforceExportedReportZipRetention(standaloneReportsDir, standaloneClient);
            Assert.Equal(5, standaloneDeleted);
            Assert.Equal(25, Directory.GetFiles(standaloneReportsDir, "Kongor_Diag_*.zip").Length);
            Assert.False(File.Exists(Path.Combine(standaloneReportsDir, "Kongor_Diag_20261110_000005_manual_report.zip")));
            Assert.True(File.Exists(Path.Combine(standaloneReportsDir, "Kongor_Diag_20261110_000006_manual_report.zip")));
            Assert.True(File.Exists(Path.Combine(standaloneReportsDir, "Kongor_Diag_20261110_000030_manual_report.zip")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_ScrubsK2LoginSessionHashesSecondarySubaccountsAndAllChatMsgWhilePreservingProjectKongorName()
    {
        // protects: K2 live login lines (Cl: Login success: #<id> <handle> [<md5>:<sha1>], ^gAdd subaccount <handle> indexN, [ALL_CHAT_MSG])
        //           and reserved project keyword "Kongor" when a tester logs into a test handle named "[KNGR]Kongor".
        // fails_when: Session cookie hashes or secondary subaccounts survive in console.tail.log, [ALL_CHAT_MSG] lines survive,
        //             or "# Project Kongor" / "KONGOR/extensions" gets replaced with "[PLAYER_n]".
        // seam: Real DiagnosticBundleService.ExportBundleAsync + DiagnosticRedactor.HarvestKnownPlayerNamesFromConsoleLog + ScrubText.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_LiveLoginPolish_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "games", "client");
        string consoleDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(consoleDir);

        try
        {
            string consolePath = Path.Combine(consoleDir, "console.log");
            await File.WriteAllTextAsync(
                consolePath,
                """
                [00:35:50] ======== HELLO PROJECT KONGOR ============
                Resource: [00:35:50] /ui/fe2/avatars/AlphaTester.png - 65536 bytes (RGBA)
                [00:35:50] ^g[WEB_PANEL] RenderNativeLadder(season, page=1/10) populated ranks #1..#10 + self row (AlphaTester: rank=#149 tier=Bronze I score=1508 MMR)
                Cl: [00:36:09] Login success: #1 Kongor [01a1c416abdb7db9ab2f0db883d9cdd4:b2e5ba0fe9fa626bb5099bb308d55030ad81ead9]
                Cl: [00:36:09] ^gAdd subaccount Kongor index1
                Cl: [00:36:09] ^gAdd subaccount ONGOR index2
                Cl: [00:36:09] ^gAdd subaccount StaffAltOne index3
                Cl: [00:36:09] ^gAdd subaccount StaffAltTwo index4
                Cl: [00:36:09] ^gAdd subaccount SecondaryAltHandle index5
                CGame: [00:36:09] ^g[HOMEPAGE_LUA] AccountInfo watch fired -> accountId: 1, nickname: [KNGR]Kongor
                [00:36:09] ^r[ALL_CHAT_MSG] msgType=2, chanName=Clan KNGR Clan, text=^279* Now talking in channel ^090Clan KNGR Clan^279., isSelf=false
                """);

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: false,
                activeModString: "game;ClientPackageFiles;KONGOR/extensions;override",
                revealInFileManager: false);

            Assert.True(result.Success);
            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            string consoleTail = ReadZipEntryText(zip, "logs/console.tail.log");
            string summaryMd = ReadZipEntryText(zip, "CRASH_SUMMARY.md");
            string manifestJson = ReadZipEntryText(zip, "manifest.json");

            // 1. Login hash, secondary subaccounts, ladder self-row, avatar path, and [ALL_CHAT_MSG] must be scrubbed/dropped
            Assert.DoesNotContain("01a1c416abdb7db9ab2f0db883d9cdd4", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("b2e5ba0fe9fa626bb5099bb308d55030ad81ead9", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Login success: #[REDACTED_ID]", consoleTail, StringComparison.Ordinal);
            Assert.Contains("[REDACTED_TOKEN]", consoleTail, StringComparison.Ordinal);
            Assert.DoesNotContain("subaccount ONGOR", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("subaccount Kongor", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StaffAltOne", consoleTail, StringComparison.Ordinal);
            Assert.DoesNotContain("StaffAltTwo", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecondaryAltHandle", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AlphaTester", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[KNGR]Kongor", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[ALL_CHAT_MSG]", consoleTail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Clan KNGR", consoleTail, StringComparison.OrdinalIgnoreCase);

            // 2. Project KONGOR branding & KONGOR/extensions mod name must remain intact
            Assert.Contains("# Project KONGOR Diagnostic & Crash Triage Report", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"KONGOR/extensions\"", manifestJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_IgnoresHistoricalForcedExitTelemetryOnExitCodeZeroAndStaleTimestamps()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_diag_stale_exit_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        try
        {
            DateTimeOffset sessionStartUtc = new(2026, 11, 15, 13, 0, 0, TimeSpan.Zero);
            string consolePath = Path.Combine(clientRoot, "console.log");
            await File.WriteAllTextAsync(consolePath, "[13:00:01] Test session log\n");

            // Write an undated historical line AND an expired UTC-stamped line before sessionStartUtc
            string engineCrashesPath = Path.Combine(crashDir, "engine_crashes.log");
            await File.WriteAllTextAsync(
                engineCrashesPath,
                """
                [EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) PrimaryCaller=k2_x64.dll+0x9FE30B Stack=[k2_x64.dll+0x9FE30B] PID=32
                [EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC=2026-11-15T12:37:23Z PrimaryCaller=k2_x64.dll+0x9FE30B Stack=[k2_x64.dll+0x9FE30B] PID=999
                """);

            // Case 1: ExitCode == 0 (manual diagnostics export after clean session) must NEVER synthesize forced_exit triage
            DiagnosticBundleResult exitZeroResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "game;override",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                minDumpWriteTimeUtc: sessionStartUtc.AddSeconds(-5),
                processExitCode: 0U);

            Assert.True(exitZeroResult.Success);
            Assert.DoesNotContain("forced_exit_k2_x64.dll_0x9FE30B", exitZeroResult.ZipPath, StringComparison.OrdinalIgnoreCase);

            // Case 2: Non-zero exit where engine_crashes.log has ONLY an expired UTC-stamped entry must not blame stale session
            await File.WriteAllTextAsync(
                engineCrashesPath,
                "[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000001) UTC=2026-11-15T12:37:23Z PrimaryCaller=k2_x64.dll+0x9FE30B Stack=[k2_x64.dll+0x9FE30B] PID=999\n");

            DiagnosticBundleResult staleUtcResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "game;override",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                minDumpWriteTimeUtc: sessionStartUtc.AddSeconds(-5),
                processExitCode: 2U);

            Assert.True(staleUtcResult.Success);
            Assert.DoesNotContain("k2_x64.dll_0x9FE30B", staleUtcResult.ZipPath, StringComparison.OrdinalIgnoreCase);

            // Case 3: Fresh session non-zero exit with matching UTC >= minDumpWriteTimeUtc IS attributed accurately
            await File.AppendAllTextAsync(
                engineCrashesPath,
                "[EXIT_TELEMETRY] Non-zero ExitProcess(0x00000002) UTC=2026-11-15T13:01:00Z PrimaryCaller=cgame_x64.dll+0x1234 Stack=[cgame_x64.dll+0x1234] PID=4242\n");

            DiagnosticBundleResult freshExitResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "game;override",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                minDumpWriteTimeUtc: sessionStartUtc.AddSeconds(-5),
                processExitCode: 2U);

            Assert.True(freshExitResult.Success);
            Assert.Contains("forced_exit_cgame_x64.dll_0x1234", freshExitResult.ZipPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("k2_x64.dll_0x9FE30B", freshExitResult.ZipPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ManualReport_IgnoresStaleDump_PreservesRecentOnDiskDumpFilenameInDumpsFolder_AndNotesZeroByteHangDump()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_diag_manual_recency_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        try
        {
            ulong k2Base = 0x00007FF890000000UL;
            byte[] validDmp = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0x112233UL);

            // 1. Stale 6-day-old minidump + 0-byte hang dump matching PID 7788
            DateTime testNowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
            string staleDumpPath = Path.Combine(crashDir, "minidump_20261114_101058.dmp");
            await File.WriteAllBytesAsync(staleDumpPath, validDmp);
            File.SetLastWriteTimeUtc(staleDumpPath, testNowUtc.AddDays(-6));

            string emptyHangPath = Path.Combine(crashDir, "hang_20261119_220000_pid7788.dmp");
            await File.WriteAllBytesAsync(emptyHangPath, []);
            File.SetLastWriteTimeUtc(emptyHangPath, testNowUtc.AddMinutes(-2));

            DiagnosticBundleResult staleManual = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: false,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                expectedProcessId: 7788);

            Assert.True(staleManual.Success);
            using (ZipArchive zip1 = ZipFile.OpenRead(staleManual.ZipPath))
            {
                Assert.DoesNotContain(zip1.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                string summary1 = ReadZipEntryText(zip1, "CRASH_SUMMARY.md");
                Assert.Contains("hang_20261119_220000_pid7788.dmp", summary1, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("0-byte", summary1, StringComparison.OrdinalIgnoreCase);
            }

            // 2. Fresh (<= 15m) on-disk dump preserves original filename inside dumps/ without renaming to <now>
            string recentDumpName = "minidump_20261120_041500.dmp";
            string recentDumpPath = Path.Combine(crashDir, recentDumpName);
            await File.WriteAllBytesAsync(recentDumpPath, validDmp);
            File.SetLastWriteTimeUtc(recentDumpPath, testNowUtc.AddMinutes(-3));

            DiagnosticBundleResult recentManual = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: false,
                outputDirectoryOverride: outDir,
                revealInFileManager: false);

            Assert.True(recentManual.Success);
            using (ZipArchive zip2 = ZipFile.OpenRead(recentManual.ZipPath))
            {
                ZipArchiveEntry dmpEntry = Assert.Single(zip2.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                Assert.Equal($"dumps/{recentDumpName}", dmpEntry.FullName);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MinidumpPrivacySanitizer_RejectsMissingMemoryListOrTruncatedThreadListWithInvalidMdmpStreams()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_diag_invalid_mdmp_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        try
        {
            // Build an MDMP missing MemoryListStream (Stream 5) — only Stream 7 (SystemInfo) and Stream 4 (ModuleList)
            byte[] brokenMdmp = new byte[512];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(0, 4), MinidumpReader.MdmpSignature);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(4, 4), 0x0000A793u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(8, 4), 2u); // 2 streams (7 and 4)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(12, 4), 32u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(32, 4), 7u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(36, 4), 32u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(40, 4), 96u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(44, 4), 4u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(48, 4), 4u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenMdmp.AsSpan(52, 4), 160u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(brokenMdmp.AsSpan(96, 2), 9);

            PrivacyScrubContext ctx = new(clientRoot: clientRoot, outputDir: outDir);
            MinidumpSanitizeResult sanitizeResult = MinidumpPrivacySanitizer.SanitizeInPlace((byte[])brokenMdmp.Clone(), ctx);
            Assert.False(sanitizeResult.AllowedForExport);
            Assert.Equal("INVALID_MDMP_STREAMS", sanitizeResult.StatusCode);

            string brokenPath = Path.Combine(crashDir, "minidump_broken.dmp");
            await File.WriteAllBytesAsync(brokenPath, brokenMdmp);

            DiagnosticBundleResult bundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: brokenPath);

            Assert.True(bundle.Success);
            using ZipArchive zip = ZipFile.OpenRead(bundle.ZipPath);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
            string summary = ReadZipEntryText(zip, "CRASH_SUMMARY.md");
            Assert.Contains("INVALID_MDMP_STREAMS", summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ZeroPiiCanary_DoesNotSelfPoisonPlayerHandleOrUsersApiRoute_AndSurgicallyRedactsSingleOffendingLogLine()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_diag_canary_surgical_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(outDir);

        try
        {
            // 1. Default offline handle "Player" + loopback "/api/users/profile" must NOT trigger false-positive log drop!
            LauncherSettingsService.Save(new LauncherSettingsModel { LastPlayerNickname = "Player" }, clientRoot);
            string consolePath = Path.Combine(clientRoot, "console.log");
            await File.WriteAllTextAsync(
                consolePath,
                """
                [12:00:01] SetSave "login_name" "Player"
                [12:00:02] GET http://127.0.0.1:55555/api/users/me -> 200 OK
                [12:00:03] Vulkan device initialized cleanly
                """);

            DiagnosticBundleResult cleanResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: false,
                outputDirectoryOverride: outDir,
                revealInFileManager: false);

            using (ZipArchive zip1 = ZipFile.OpenRead(cleanResult.ZipPath))
            {
                Assert.NotNull(zip1.GetEntry("logs/console.tail.log"));
                string consoleScrubbed = ReadZipEntryText(zip1, "logs/console.tail.log");
                Assert.Contains("[PLAYER_1]", consoleScrubbed, StringComparison.Ordinal);
                Assert.Contains("/api/users/me", consoleScrubbed, StringComparison.Ordinal);
                Assert.Contains("Vulkan device initialized cleanly", consoleScrubbed, StringComparison.Ordinal);
            }

            // 2. UNC share containing \Users\ triggers FindZeroPiiCanaryViolation on ONLY line 2 -> surgical fallback preserves lines 1 & 3
            string wmLogPath = Path.Combine(clientRoot, "WILLOWMAKER.log");
            await File.WriteAllTextAsync(
                wmLogPath,
                "[12:00:01] Safe launcher line before\n[12:00:02] Uncaught UNC share \\\\nasbox\\Users\\SecretShare\\data\n[12:00:03] Safe launcher line after\n");

            DiagnosticBundleResult wmResult = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: false,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                activeLauncherLogPath: wmLogPath);

            using (ZipArchive zip2 = ZipFile.OpenRead(wmResult.ZipPath))
            {
                Assert.NotNull(zip2.GetEntry("logs/WILLOWMAKER.tail.log"));
                string wmScrubbed = ReadZipEntryText(zip2, "logs/WILLOWMAKER.tail.log");
                Assert.Contains("Safe launcher line before", wmScrubbed, StringComparison.Ordinal);
                Assert.Contains("[LINE_REDACTED_BY_ZERO_PII_CANARY: user_profile_path]", wmScrubbed, StringComparison.Ordinal);
                Assert.Contains("Safe launcher line after", wmScrubbed, StringComparison.Ordinal);
                Assert.DoesNotContain("SecretShare", wmScrubbed, StringComparison.OrdinalIgnoreCase);

                string summary2 = ReadZipEntryText(zip2, "CRASH_SUMMARY.md");
                Assert.Contains("logs/WILLOWMAKER.tail.log", summary2, StringComparison.Ordinal);

                string manifestJson2 = ReadZipEntryText(zip2, "manifest.json");
                DiagnosticManifestDto? manifest2 = System.Text.Json.JsonSerializer.Deserialize(
                    manifestJson2,
                    global::WILLOWMAKER.Core.Services.Serialization.WillowmakerJsonSerializerContext.Default.DiagnosticManifestDto);
                Assert.NotNull(manifest2);
                Assert.Equal(1, manifest2!.RedactionSummary["ZeroPiiCanaryLinesRedacted"]);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConsoleSpamCollapse_BuildId502_SilentExitZeroReason_AndCanonicalModAllowlist_WorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_diag_spam_mods_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(outDir);

        try
        {
            StringBuilder consoleBuilder = new();
            consoleBuilder.AppendLine("[12:00:00] Pre-storm boot diagnostic marker: Vulkan swapchain ready");
            for (int i = 0; i < 4000; i++)
            {
                consoleBuilder.AppendLine("Warn: [IResourceLibrary] Duplicate resource registration ignored");
            }
            consoleBuilder.AppendLine("[12:05:00] Final pre-exit marker before silent abort");
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "console.log"), consoleBuilder.ToString());

            string canonicalModArg = Kongor.Core.Vfs.ClientModMountSpec.GetCanonicalModString(includeOverride: true) + ";my_secret_mod";
            const string expectedSilentReason = "Client exited with code 0 after 42.3s without writing clean shutdown marker.";

            DiagnosticBundleResult bundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: canonicalModArg,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                processExitCode: 0U,
                silentExitZeroReason: expectedSilentReason);

            Assert.True(bundle.Success);
            using ZipArchive zip = ZipFile.OpenRead(bundle.ZipPath);

            string consoleTail = ReadZipEntryText(zip, "logs/console.tail.log");
            Assert.Contains("Pre-storm boot diagnostic marker: Vulkan swapchain ready", consoleTail, StringComparison.Ordinal);
            Assert.Contains("[suppressed 3996 identical lines]", consoleTail, StringComparison.Ordinal);
            Assert.Contains("Final pre-exit marker before silent abort", consoleTail, StringComparison.Ordinal);

            string summary = ReadZipEntryText(zip, "CRASH_SUMMARY.md");
            Assert.Contains("5.0.2", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("4.10.1-modernized", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedSilentReason, summary, StringComparison.Ordinal);

            string manifestJson = ReadZipEntryText(zip, "manifest.json");
            DiagnosticManifestDto? manifest = System.Text.Json.JsonSerializer.Deserialize(
                manifestJson,
                global::WILLOWMAKER.Core.Services.Serialization.WillowmakerJsonSerializerContext.Default.DiagnosticManifestDto);
            Assert.NotNull(manifest);
            Assert.Equal("5.0.2", manifest!.BuildId);
            Assert.Contains("../shared", manifest.ActiveMods);
            Assert.Contains("KONGOR/configuration", manifest.ActiveMods);
            Assert.Contains("KONGOR/updates", manifest.ActiveMods);
            Assert.Contains("KONGOR/extensions", manifest.ActiveMods);
            Assert.Equal(1, manifest.ActiveMods.Count(m => m.StartsWith("[CUSTOM_MOD", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TextRedactor_ScrubsHardwareMacAddresses_WithoutFalsePositiveOnUuids_AndZeroPiiCanaryCatchesUnredactedMacs()
    {
        // Protects: Hardware MAC redaction in logs without corrupting 8-4-4-4-12 UUIDs, and Zero-PII canary MAC enforcement.
        const string sampleUuid = "550e8400-e29b-41d4-a716-446655440000";
        string rawLog = $$"""
            [12:00:01] Network Adapter 0 MAC: c8-7f-54-ec-16-eb
            [12:00:02] Network Adapter 1 MAC=AA:BB:CC:DD:EE:FF
            [12:00:03] Secondary adapter MAC address 00-11-22-33-44-55 ready
            [12:00:04] Session correlation UUID={{sampleUuid}}
            [12:00:05] MAC: {{sampleUuid}}
            """;

        Assert.False(DiagnosticRedactor.ContainsRawHardwareMacAddress($"Session correlation UUID={sampleUuid}"));
        Assert.False(DiagnosticRedactor.ContainsRawHardwareMacAddress($"MAC: {sampleUuid}"));
        Assert.True(DiagnosticRedactor.ContainsRawHardwareMacAddress("MAC: c8-7f-54-ec-16-eb"));
        Assert.True(DiagnosticRedactor.ContainsRawHardwareMacAddress("MAC=AA:BB:CC:DD:EE:FF"));
        Assert.True(DiagnosticRedactor.ContainsRawHardwareMacAddress("Secondary adapter MAC address 00-11-22-33-44-55 ready"));

        (string scrubbed, RedactionAuditCounts counts) = DiagnosticRedactor.ScrubText(rawLog);

        Assert.True(counts.UserIdentifiersRedacted >= 5, $"Expected >= 5 user identifier redactions (3 MACs + 2 UUIDs), got {counts.UserIdentifiersRedacted}");
        Assert.Contains("Network Adapter 0 MAC: [REDACTED_MAC]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Network Adapter 1 MAC: [REDACTED_MAC]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Secondary adapter MAC address [REDACTED_MAC] ready", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Session correlation UUID=[REDACTED_UUID]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("MAC: [REDACTED_UUID]", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("c8-7f-54-ec-16-eb", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00-11-22-33-44-55", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sampleUuid, scrubbed, StringComparison.OrdinalIgnoreCase);

        // Explicit Windows CRLF (\r\n) regression check: standalone MAC on a CRLF line must also be scrubbed
        string crlfLog = "[12:00:03] Secondary adapter MAC address 00-11-22-33-44-55 ready\r\n[12:00:04] Next line\r\n";
        (string crlfScrubbed, RedactionAuditCounts crlfCounts) = DiagnosticRedactor.ScrubText(crlfLog);
        Assert.True(crlfCounts.UserIdentifiersRedacted >= 1, "CRLF MAC line must trigger at least 1 redaction.");
        Assert.Contains("Secondary adapter MAC address [REDACTED_MAC] ready", crlfScrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("00-11-22-33-44-55", crlfScrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.True(DiagnosticRedactor.ContainsRawHardwareMacAddress(crlfLog));

        PrivacyScrubContext ctx = new();
        string? rawMacViolation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
            "logs/console.tail.log",
            Encoding.UTF8.GetBytes("[12:00:01] Adapter MAC: c8-7f-54-ec-16-eb\n"),
            ctx,
            isBinaryMinidump: false);
        Assert.NotNull(rawMacViolation);
        Assert.Equal("hardware_mac_address", DiagnosticRedactor.ClassifyZeroPiiViolationKind(rawMacViolation));

        string? scrubbedViolation = DiagnosticRedactor.FindZeroPiiCanaryViolation(
            "logs/console.tail.log",
            Encoding.UTF8.GetBytes(scrubbed),
            ctx,
            isBinaryMinidump: false);
        Assert.Null(scrubbedViolation);
    }

    [Fact]
    public async Task BinaryMinidumpCanary_DetectsAsciiAndUtf16LeUsernameProfilePathAndMac_AndOmitsContaminatedTier1Dump()
    {
        // Protects: Fast binary Zero-PII canary scan on Tier-1 minimal .dmp bytes catching ASCII/UTF-16LE username, profile path, and MAC leaks.
        ulong k2Base = 0x00007FF890000000UL;
        byte[] cleanDmp = BuildSyntheticMdmp(
            moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
            moduleBase: k2Base,
            moduleSize: 0x01000000u,
            includeExceptionStream: true,
            exceptionCode: 0xC0000005u,
            exceptionAddress: k2Base + 0x112233UL);

        // 1. Direct unit checks on FindBinaryMinidumpZeroPiiViolation
        Assert.Null(DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            cleanDmp,
            userNameOverride: "TesterZoe",
            userProfilePathOverride: @"C:\Users\TesterZoe"));

        // Generic usernames ("user", "admin") must not false-positive
        byte[] genericWordPayload = Encoding.ASCII.GetBytes(" status=user role=admin ");
        Assert.Null(DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            genericWordPayload,
            userNameOverride: "user",
            userProfilePathOverride: @"C:\CustomProfile\ZoeProfile"));
        Assert.Null(DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            genericWordPayload,
            userNameOverride: "admin",
            userProfilePathOverride: @"C:\CustomProfile\ZoeProfile"));

        // ASCII & UTF-16LE username detection (including 3-char non-generic usernames and odd-byte UTF-16LE alignment)
        string? asciiUserViolation = DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            Encoding.ASCII.GetBytes("env_owner=Zoe;"),
            userNameOverride: "Zoe",
            userProfilePathOverride: @"C:\CustomProfile\NoMatch");
        Assert.NotNull(asciiUserViolation);
        Assert.Contains("ASCII", asciiUserViolation, StringComparison.Ordinal);
        Assert.Equal("os_user_identifier", DiagnosticRedactor.ClassifyZeroPiiViolationKind(asciiUserViolation));

        byte[] oddAlignedUtf16User = [0xAA, .. Encoding.Unicode.GetBytes("owner=TesterZoe;")];
        string? utf16UserViolation = DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            oddAlignedUtf16User,
            userNameOverride: "TesterZoe",
            userProfilePathOverride: @"C:\CustomProfile\NoMatch");
        Assert.NotNull(utf16UserViolation);
        Assert.Contains("UTF-16LE", utf16UserViolation, StringComparison.Ordinal);
        Assert.Equal("os_user_identifier", DiagnosticRedactor.ClassifyZeroPiiViolationKind(utf16UserViolation));

        // ASCII & UTF-16LE user profile root path detection
        string? asciiProfileViolation = DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            Encoding.ASCII.GetBytes(@"path=D:\Profiles\SecretOwner\AppData"),
            userNameOverride: "OtherUser",
            userProfilePathOverride: @"D:\Profiles\SecretOwner");
        Assert.NotNull(asciiProfileViolation);
        Assert.Contains("ASCII", asciiProfileViolation, StringComparison.Ordinal);
        Assert.Equal("user_profile_path", DiagnosticRedactor.ClassifyZeroPiiViolationKind(asciiProfileViolation));

        string? utf16ProfileViolation = DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            Encoding.Unicode.GetBytes(@"path=D:\Profiles\SecretOwner\AppData"),
            userNameOverride: "OtherUser",
            userProfilePathOverride: @"D:\Profiles\SecretOwner");
        Assert.NotNull(utf16ProfileViolation);
        Assert.Contains("UTF-16LE", utf16ProfileViolation, StringComparison.Ordinal);
        Assert.Equal("user_profile_path", DiagnosticRedactor.ClassifyZeroPiiViolationKind(utf16ProfileViolation));

        // ASCII & UTF-16LE raw MAC detection
        string? asciiMacViolation = DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            Encoding.ASCII.GetBytes("adapter MAC: c8-7f-54-ec-16-eb active"),
            userNameOverride: "TesterZoe",
            userProfilePathOverride: @"C:\Users\TesterZoe");
        Assert.NotNull(asciiMacViolation);
        Assert.Contains("ASCII", asciiMacViolation, StringComparison.Ordinal);
        Assert.Equal("hardware_mac_address", DiagnosticRedactor.ClassifyZeroPiiViolationKind(asciiMacViolation));

        string? utf16MacViolation = DiagnosticBundleService.FindBinaryMinidumpZeroPiiViolation(
            Encoding.Unicode.GetBytes("adapter MAC: C8:7F:54:EC:16:EB active"),
            userNameOverride: "TesterZoe",
            userProfilePathOverride: @"C:\Users\TesterZoe");
        Assert.NotNull(utf16MacViolation);
        Assert.Contains("UTF-16LE", utf16MacViolation, StringComparison.Ordinal);
        Assert.Equal("hardware_mac_address", DiagnosticRedactor.ClassifyZeroPiiViolationKind(utf16MacViolation));

        // 2. End-to-end ExportBundleAsync: clean Tier-1 .dmp passes; contaminated Tier-1 .dmp is omitted and recorded
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_diag_bin_canary_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        try
        {
            string cleanDumpPath = Path.Combine(crashDir, "minidump_clean_tier1.dmp");
            await File.WriteAllBytesAsync(cleanDumpPath, cleanDmp);

            DiagnosticBundleResult cleanBundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: cleanDumpPath);

            Assert.True(cleanBundle.Success);
            using (ZipArchive cleanZip = ZipFile.OpenRead(cleanBundle.ZipPath))
            {
                Assert.Contains(cleanZip.Entries, e => e.FullName.Equals("dumps/minidump_clean_tier1.dmp", StringComparison.OrdinalIgnoreCase));
            }

            // Contaminated Tier-1 .dmp carrying a raw UTF-16LE MAC in slack bytes
            byte[] contaminatedDmp = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0x112233UL,
                extraStackBytes: Encoding.Unicode.GetBytes("NIC MAC: c8-7f-54-ec-16-eb"));

            string dirtyDumpPath = Path.Combine(crashDir, "minidump_dirty_tier1.dmp");
            await File.WriteAllBytesAsync(dirtyDumpPath, contaminatedDmp);

            DiagnosticBundleResult dirtyBundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: dirtyDumpPath);

            Assert.True(dirtyBundle.Success);
            using (ZipArchive dirtyZip = ZipFile.OpenRead(dirtyBundle.ZipPath))
            {
                Assert.DoesNotContain(dirtyZip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));

                string summary = ReadZipEntryText(dirtyZip, "CRASH_SUMMARY.md");
                Assert.Contains("Zero-PII Canary", summary, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("minidump_dirty_tier1.dmp", summary, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("hardware MAC address", summary, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("c8-7f-54-ec-16-eb", summary, StringComparison.OrdinalIgnoreCase);

                string manifestJson = ReadZipEntryText(dirtyZip, "manifest.json");
                DiagnosticManifestDto? manifest = System.Text.Json.JsonSerializer.Deserialize(
                    manifestJson,
                    global::WILLOWMAKER.Core.Services.Serialization.WillowmakerJsonSerializerContext.Default.DiagnosticManifestDto);
                Assert.NotNull(manifest);
                Assert.Contains(manifest!.OmittedEntries, e =>
                    e.Contains("minidump_dirty_tier1.dmp", StringComparison.OrdinalIgnoreCase) &&
                    e.Contains("ZERO_PII_CANARY", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_WithSameEventK2Under4MbUuidDumpAndNativeGuardTier1Dump_PrefersNativeGuardTier1AndTreatsK2DumpAsFullDump()
    {
        // protects: Discord diagnostic bundle compactness and Zero-PII Tier-1 preference when a crash writes both NativeGuard's ~55 KB minidump_*.dmp and K2 CCrashReporter's ~3.74 MB <uuid>.dmp within +-30s.
        // fails_when: LocateLatestCrashDumpPath or ExportBundleAsync packages the 3.74 MB k2_crash_*.dmp instead of the companion Tier-1 minidump_*.dmp from the same crash event.
        // seam: Real DiagnosticBundleService.LocateLatestCrashDumpPath + ExportBundleAsync + ZipArchive entry verification.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_SameEventDualDump_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string nativeCrashDir = Path.Combine(clientRoot, "logs", "crash");
        string k2OverrideDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(nativeCrashDir);
        Directory.CreateDirectory(k2OverrideDir);

        try
        {
            DateTime crashUtc = new(2026, 11, 20, 20, 9, 4, DateTimeKind.Utc);
            ulong k2Base = 0x00007FF930000000UL;

            // 1. NativeGuard Tier-1 scrubbed minidump (~55 KB, written first at T+0.0s)
            byte[] tier1Bytes = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0xCA8B38UL);
            string tier1Path = Path.Combine(nativeCrashDir, "minidump_20261120_200904.dmp");
            await File.WriteAllBytesAsync(tier1Path, tier1Bytes);
            File.SetLastWriteTimeUtc(tier1Path, crashUtc);

            // 2. K2 CCrashReporter <uuid>.dmp (3.74 MB <= 4 MB, written 500ms later during EXCEPTION_CONTINUE_SEARCH)
            byte[] k2UuidBytes = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\k2_x64.dll",
                moduleBase: k2Base,
                moduleSize: 0x01000000u,
                includeExceptionStream: true,
                exceptionCode: 0xC0000005u,
                exceptionAddress: k2Base + 0xCA8B38UL,
                extraStackBytes: new byte[3_700_000]);
            string k2UuidPath = Path.Combine(k2OverrideDir, "f537c7ce-eaa3-44ea-a576-46a04c05f112.dmp");
            await File.WriteAllBytesAsync(k2UuidPath, k2UuidBytes);
            File.SetLastWriteTimeUtc(k2UuidPath, crashUtc.AddMilliseconds(500));

            string? located = DiagnosticBundleService.LocateLatestCrashDumpPath(clientRoot);
            Assert.Equal(Path.GetFullPath(tier1Path), Path.GetFullPath(located!));

            // Even if preferredDumpPath explicitly points at K2's 3.74 MB <uuid>.dmp, ExportBundleAsync must package the companion Tier-1 minidump_*.dmp
            DiagnosticBundleResult bundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "base;game;override",
                masterServerHost: "127.0.0.1:55555",
                cdnHost: "127.0.0.1:55555/cdn",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                preferredDumpPath: k2UuidPath);

            Assert.True(bundle.Success);
            using ZipArchive zip = ZipFile.OpenRead(bundle.ZipPath);
            ZipArchiveEntry dmpEntry = Assert.Single(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("dumps/minidump_20261120_200904.dmp", dmpEntry.FullName);
            Assert.True(dmpEntry.Length < 100_000, $"Expected compact Tier-1 minidump (<100 KB), got {dmpEntry.Length} bytes.");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateSilentExitZeroCrashAsync_QuitInitiatedWithoutCleanExit_NotMaskedByConsoleLogCleanQuit()
    {
        // protects: Windows per-PID sentinel precedence so a post-quit crash inside CVid::Shutdown / CHost::Shutdown
        //           that exits with code 0 is NEVER masked by early [KONGOR-CLEAN-QUIT] console.log markers.
        // fails_when: EvaluateSilentExitZeroCrashAsync returns IsSilentCrash=false when quit-initiated.<pid>.sentinel
        //             exists without clean-exit.<pid>.sentinel merely because console.log contains [KONGOR-CLEAN-QUIT].
        // seam: Real MainViewModel.EvaluateSilentExitZeroCrashAsync + filesystem sentinels + console.log.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ShutdownSentinelPrecedence_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string consoleDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(consoleDir);

        try
        {
            DateTimeOffset launchStartUtc = DateTimeOffset.UtcNow.AddSeconds(-30);
            int pid = 42424;

            // Early Lua UI quit wrote [KONGOR-CLEAN-QUIT] BEFORE CSystem::Exit started
            await File.WriteAllTextAsync(
                Path.Combine(consoleDir, "console.log"),
                "[16:23:49] [KONGOR-CLEAN-QUIT] User confirmed quit.\n");

            // NativeGuard wrote session-active + quit-initiated, then crashed before CHost::Shutdown completed (no clean-exit)
            await File.WriteAllTextAsync(Path.Combine(crashDir, $"session-active.{pid}.sentinel"), $"PID={pid}\n");
            await File.WriteAllTextAsync(Path.Combine(crashDir, $"quit-initiated.{pid}.sentinel"), $"PID={pid}\nReason=CSystem::Exit_UserQuit\n");

            (bool isSilentCrash, string? reason) = await global::WILLOWMAKER.Core.ViewModels.MainViewModel.EvaluateSilentExitZeroCrashAsync(
                clientRoot,
                crashDir,
                launchStartUtc,
                pid);

            Assert.True(isSilentCrash, "Incomplete shutdown (missing clean-exit.<pid>.sentinel) must be detected as a crash even with [KONGOR-CLEAN-QUIT] in console.log.");
            Assert.NotNull(reason);
            Assert.Contains($"quit-initiated.{pid}.sentinel", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_WithShutdownHangDumpAndSentinel_ProducesShutdownHangTriageZip()
    {
        // protects: Shutdown hang watchdog reports are packaged with deterministic shutdown_hang slug and
        //           SYNTHETIC_SHUTDOWN_HANG_TRIAGE summary instead of Unknown_0x0.
        // fails_when: ExportBundleAsync emits Unknown_0x0 or drops the hang_*_pid*.dmp snapshot.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + BuildSyntheticMdmp(no exception stream) + ZipArchive verification.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ShutdownHangTriage_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        try
        {
            int pid = 22840;
            byte[] liveHangMdmp = BuildSyntheticMdmp(
                moduleFullPath: @"C:\Games\HoN\VkLayer_hon_replay_capture.dll",
                moduleBase: 0x00007FF940000000UL,
                moduleSize: 0x00200000u,
                includeExceptionStream: false);
            string hangDumpPath = Path.Combine(crashDir, $"hang_20261120_222410_pid{pid}.dmp");
            await File.WriteAllBytesAsync(hangDumpPath, liveHangMdmp);

            await File.WriteAllTextAsync(
                Path.Combine(crashDir, $"last-crashed.{pid}.sentinel"),
                $"PID={pid}\nReason=ShutdownHangTimeout\nExceptionCode=0xCFFFFFFF\nLocation=ShutdownWatchdog\nHangElapsedSeconds=10.2\n");
            await File.WriteAllTextAsync(
                Path.Combine(crashDir, "native_guard_sentinel.log"),
                "[2026-11-20 16:23:49.000] [KONGOR NATIVE GUARD] [EXIT-HOOK] User CSystem::Exit(0) initiated\n");

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "base;game;override",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                processExitCode: 0xCFFFFFFFu,
                expectedProcessId: pid,
                preferredDumpPath: hangDumpPath);

            Assert.True(result.Success);
            Assert.Contains("_shutdown_hang.zip", Path.GetFileName(result.ZipPath), StringComparison.OrdinalIgnoreCase);

            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            string summaryMd = ReadZipEntryText(zip, "CRASH_SUMMARY.md");
            Assert.Contains("Shutdown Hang / Deadlock", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ShutdownHangTimeout", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(zip.Entries, e => e.FullName.Equals($"dumps/hang_20261120_222410_pid{pid}.dmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_WithFastFailExitCode0xC0000409NoDump_DecodesNtStatusAndIncludesSentinelTail()
    {
        // protects: Zero-UAC diagnostic triage for kernel-dispatched __fastfail (0xC0000409) without an SEH minidump.
        // fails_when: ExportBundleAsync reports a generic opaque exit code or omits the NativeGuard sentinel tail.
        // seam: Real DiagnosticBundleService.ExportBundleAsync + DescribeWindowsNtStatusExitCode + CRASH_SUMMARY.md verification.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_FastFailTriage_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempRoot, "client");
        string outDir = Path.Combine(tempRoot, "out");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(crashDir, "native_guard_sentinel.log"),
                "[2026-11-20 16:23:49.100] [KONGOR NATIVE GUARD] [EXIT-HOOK] Invoking Kongor_Overlay_PrepareProcessExit\n");

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                activeModString: "base;game;override",
                outputDirectoryOverride: outDir,
                revealInFileManager: false,
                processExitCode: 0xC0000409u,
                expectedProcessId: 31337);

            Assert.True(result.Success);
            Assert.Contains("_forced_exit_0xC0000409.zip", Path.GetFileName(result.ZipPath), StringComparison.OrdinalIgnoreCase);

            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            string summaryMd = ReadZipEntryText(zip, "CRASH_SUMMARY.md");
            Assert.Contains("STATUS_STACK_BUFFER_OVERRUN", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("__fastfail", summaryMd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Kongor_Overlay_PrepareProcessExit", summaryMd, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsLiveProcessCandidateMatch_ScopesLiveSnapshotToClientRootAndExpectedPid()
    {
        // protects: Isolation of manual diagnostic freeze snapshots so concurrent gameplay or multi-install sessions never leak into unrelated clientRoot bundles or unit tests.
        // fails_when: TryCaptureLiveProcessDump / IsLiveProcessCandidateMatch accepts a hon_x64 process outside clientRoot, matches a sibling client_backup prefix, ignores expectedProcessId, or accepts null MainModule paths without an on-disk binary + PID match.
        // seam: Real DiagnosticBundleService.IsLiveProcessCandidateMatch + ResolveLiveProcessScopePrefix against temporary filesystem layouts.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_LiveProcScope_{Guid.NewGuid():N}");
        string installAClient = Path.Combine(tempRoot, "InstallA", "games", "client");
        string installAWin64 = Path.Combine(installAClient, "windows-x64");
        string installAWinLauncher = Path.Combine(installAClient, "win-x64");
        string siblingLookalike = Path.Combine(tempRoot, "InstallA", "games", "client_backup");
        string isolatedTestClient = Path.Combine(tempRoot, "IsolatedTempTest", "client");

        Directory.CreateDirectory(installAWin64);
        Directory.CreateDirectory(installAWinLauncher);
        Directory.CreateDirectory(siblingLookalike);
        Directory.CreateDirectory(isolatedTestClient);

        try
        {
            string installARootExe = Path.Combine(installAClient, "hon_x64.exe");
            string installAWin64Exe = Path.Combine(installAWin64, "hon_x64.exe");
            string siblingExe = Path.Combine(siblingLookalike, "hon_x64.exe");
            File.WriteAllBytes(installARootExe, [0x4D, 0x5A]);
            File.WriteAllBytes(installAWin64Exe, [0x4D, 0x5A]);
            File.WriteAllBytes(siblingExe, [0x4D, 0x5A]);

            // 1. Unrelated live session is rejected for isolated clientRoot (both with and without expectedProcessId)
            Assert.False(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: installARootExe,
                clientRoot: isolatedTestClient,
                expectedProcessId: 7788,
                out string pidMismatchReason));
            Assert.Contains("7788", pidMismatchReason, StringComparison.OrdinalIgnoreCase);

            Assert.False(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: installARootExe,
                clientRoot: isolatedTestClient,
                expectedProcessId: null,
                out string outsideScopeReason));
            Assert.Contains("outside client scope", outsideScopeReason, StringComparison.OrdinalIgnoreCase);

            // 2. Sibling directory sharing string prefix ("client_backup" vs "client") is rejected
            Assert.False(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: siblingExe,
                clientRoot: installAClient,
                expectedProcessId: null,
                out _));

            // 3. Same clientRoot with mismatched expectedProcessId is rejected
            Assert.False(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: installARootExe,
                clientRoot: installAClient,
                expectedProcessId: 7788,
                out _));

            // 4. Matching clientRoot (including win-x64 / windows-x64 launcher subdir normalization) is accepted
            Assert.True(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: installAWin64Exe,
                clientRoot: installAClient,
                expectedProcessId: 20272,
                out string okReason1));
            Assert.Equal(string.Empty, okReason1);

            Assert.True(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: installARootExe,
                clientRoot: installAWinLauncher,
                expectedProcessId: null,
                out string okReason2));
            Assert.Equal(string.Empty, okReason2);

            // 5. Null MainModule path (e.g., cross-privilege Win32Exception) requires BOTH exact expectedProcessId AND on-disk binary
            Assert.False(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: null,
                clientRoot: installAClient,
                expectedProcessId: null,
                out _));

            Assert.False(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: null,
                clientRoot: isolatedTestClient,
                expectedProcessId: 20272,
                out _));

            Assert.True(DiagnosticBundleService.IsLiveProcessCandidateMatch(
                candidatePid: 20272,
                candidateExecutablePath: null,
                clientRoot: installAClient,
                expectedProcessId: 20272,
                out _));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class NonPumpingUiSyncContext : System.Threading.SynchronizationContext
    {
        public override void Post(System.Threading.SendOrPostCallback d, object? state)
        {
            // Intentionally never pumps while the UI thread is blocked synchronously inside Dispose(),
            // reproducing AvaloniaSynchronizationContext deadlock conditions if ConfigureAwait(false) is missing.
        }
    }
}
