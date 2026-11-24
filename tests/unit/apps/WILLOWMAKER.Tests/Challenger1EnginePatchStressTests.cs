// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Tests.Infrastructure;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class Challenger1EnginePatchStressTests
{
    private static readonly byte[] Fps250 = [0x00, 0x00, 0x7A, 0x43];
    private static readonly byte[] Fps1000 = [0x00, 0x00, 0x7A, 0x44];
    private static readonly byte[] ThrottleJumpOrig = [0x7C, 0x96];
    private static readonly byte[] ThrottleJumpNop = [0x90, 0x90];

    private static readonly byte[] UserDirOriginal =
    [
        0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
        0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
        0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
        0x3C, 0x40, 0x00, 0x75, 0xF5
    ];

    private static readonly byte[] UserDirPatched =
    [
        0x48, 0xB8, 0x75, 0x00, 0x73, 0x00, 0x65, 0x00, 0x72, 0x00, // mov rax, L"user" (10 bytes)
        0x48, 0x89, 0x45, 0x90, // mov [rbp - 70h], rax (4 bytes)
        0x48, 0xB8, 0x64, 0x00, 0x61, 0x00, 0x74, 0x00, 0x61, 0x00, // mov rax, L"data" (10 bytes)
        0x48, 0x89, 0x45, 0x98, // mov [rbp - 68h], rax (4 bytes)
        0x66, 0xC7, 0x45, 0xA0, 0x00, 0x00, // mov word ptr [rbp - 60h], 0 (6 bytes)
        0x49, 0xC7, 0xC0, 0x08, 0x00, 0x00, 0x00, // mov r8, 8 (length) (7 bytes)
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 // 12 NOPs
    ];

    private static readonly string[] LegacyDomains =
    [
        "client.sea.heroesofnewerth.com",
        "masterserver.naeu.heroesofnewerth.com",
        "logcollect.naeu.heroesofnewerth.com",
        "messaging-naeu.heroesofnewerth.com",
        "masterserver.hon.s2games.com",
        "masterserver.cis.heroesofnewerth.com",
        "masterserver.savage2.s2games.com"
    ];

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_Windows_AllBinaries_FullPatch_Backup_AndFastPathIdempotence()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Stress_Win_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            (string RelPath, int Offset)[] winTargets =
            [
                ("k2_x64.dll", 0xFB416C),
                (Path.Combine("windows-x64", "k2_x64.dll"), 0xFB416C),
                (Path.Combine("game", "cgame_x64.dll"), 0x26BD74),
                (Path.Combine("windows-x64", "game", "cgame_x64.dll"), 0x26BD74),
                (Path.Combine("editor", "cgame_x64.dll"), 0x097E59),
                (Path.Combine("windows-x64", "editor", "cgame_x64.dll"), 0x097E59)
            ];

            foreach ((string relPath, int offset) in winTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                int size = offset + 0x1000;
                if (relPath.Contains("k2_x64.dll", StringComparison.OrdinalIgnoreCase)) size = Math.Max(size, 0xFB5000);

                byte[] buffer = new byte[size];

                // Seed float pattern at known offset
                Buffer.BlockCopy(Fps250, 0, buffer, offset, 4);

                if (relPath.EndsWith("k2_x64.dll", StringComparison.OrdinalIgnoreCase))
                {
                    // Throttle jump at 0x8B72A8
                    Buffer.BlockCopy(ThrottleJumpOrig, 0, buffer, 0x8B72A8, 2);
                    // User directory at 0x9FE37A
                    Buffer.BlockCopy(UserDirOriginal, 0, buffer, 0x9FE37A, UserDirOriginal.Length);
                }

                // Inject dead domains
                byte[] asciiDom = Encoding.ASCII.GetBytes("client.sea.heroesofnewerth.com");
                Buffer.BlockCopy(asciiDom, 0, buffer, 0x100, asciiDom.Length);

                byte[] wideDom = Encoding.Unicode.GetBytes("masterserver.naeu.heroesofnewerth.com");
                Buffer.BlockCopy(wideDom, 0, buffer, 0x300, wideDom.Length);

                File.WriteAllBytes(fullPath, buffer);
            }

            // --- Pass 1: Initial Patch ---
            List<string> pass1Logs = [];
            bool pass1Result = EnginePatchService.EnsureEnginePatched(tempDir, pass1Logs.Add);

            pass1Result.Should().BeTrue();
            pass1Logs.Should().Contain(log => log.Contains("250 FPS -> 1000 FPS"));

            // Verify modifications and backups for every binary
            foreach ((string relPath, int offset) in winTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                string backupPath = $"{fullPath}.bak";

                File.Exists(backupPath).Should().BeTrue($"Backup should exist for {relPath}");

                byte[] bakBytes = File.ReadAllBytes(backupPath);
                bakBytes[offset..(offset + 4)].Should().Equal(Fps250, "Backup must preserve original 250.0f");

                byte[] patchedBytes = File.ReadAllBytes(fullPath);
                patchedBytes[offset..(offset + 4)].Should().Equal(Fps1000, "Patched binary must have 1000.0f");

                if (relPath.EndsWith("k2_x64.dll", StringComparison.OrdinalIgnoreCase))
                {
                    bakBytes[0x8B72A8..(0x8B72A8 + 2)].Should()
                        .Equal(ThrottleJumpOrig, "Backup must preserve throttle jump");
                    patchedBytes[0x8B72A8..(0x8B72A8 + 2)].Should()
                        .Equal(ThrottleJumpNop, "Patched k2 must have NOPed throttle jump");

                    patchedBytes[0x9FE37A..(0x9FE37A + UserDirPatched.Length)].Should()
                        .Equal(UserDirPatched, "Patched k2 must have userdata redirection");
                    EnginePatchService.IsUserDirectoryPatched(patchedBytes).Should().BeTrue();
                }

                // Verify dead domains nullified
                string asciiString = Encoding.ASCII.GetString(patchedBytes);
                asciiString.Should().NotContain("client.sea.heroesofnewerth.com");
                asciiString.Should().Contain("0.0.0.0");

                string wideString = Encoding.Unicode.GetString(patchedBytes);
                wideString.Should().NotContain("masterserver.naeu.heroesofnewerth.com");
                wideString.Should().Contain("0.0.0.0");
            }

            // Snapshot patched files and backup timestamps
            Dictionary<string, (DateTime bakModified, byte[] bytes)> snapshots = [];
            foreach ((string relPath, _) in winTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                snapshots[relPath] = (File.GetLastWriteTimeUtc($"{fullPath}.bak"), File.ReadAllBytes(fullPath));
            }

            // --- Pass 2: Fast-Path & Idempotence Verification ---
            List<string> pass2Logs = [];
            bool pass2Result = EnginePatchService.EnsureEnginePatched(tempDir, pass2Logs.Add);

            pass2Result.Should().BeTrue();
            pass2Logs.Should().OnlyContain(log => log.Contains("is fully up to date."));

            // Verify files and backups were not touched on Pass 2
            foreach ((string relPath, _) in winTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                File.GetLastWriteTimeUtc($"{fullPath}.bak").Should().Be(snapshots[relPath].bakModified,
                    "Backup must not be overwritten on second pass");
                File.ReadAllBytes(fullPath).Should()
                    .Equal(snapshots[relPath].bytes, "Patched bytes must remain identical");
            }

            // --- Pass 3: Repeated Run ---
            bool pass3Result = EnginePatchService.EnsureEnginePatched(tempDir);
            pass3Result.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_Linux_KnownAndDynamicOffsets_FullPatchAndFastPath()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Stress_Linux_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            (string RelPath, int Offset, bool IsDynamic)[] linuxTargets =
            [
                ("libk2-x86_64.so", 0x01533D10, false),
                (Path.Combine("linux-x64", "libk2-x86_64.so"), 0x01533D10, false),
                ("libk2-x86_64-server.so", 0x35000, true),
                (Path.Combine("linux-x64", "libk2-x86_64-server.so"), 0x42000, true),
                (Path.Combine("game", "cgame-x86_64.so"), 0x12000, true),
                (Path.Combine("linux-x64", "game", "cgame-x86_64.so"), 0x18000, true),
                (Path.Combine("game", "game-x86_64-server.so"), 0x50000, true),
                (Path.Combine("linux-x64", "game", "game-x86_64-server.so"), 0x55000, true)
            ];

            foreach ((string relPath, int offset, _) in linuxTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                byte[] buffer = new byte[offset + 0x200];
                Buffer.BlockCopy(Fps250, 0, buffer, offset, 4);

                // Add legacy domain in ASCII
                byte[] dom = Encoding.ASCII.GetBytes("masterserver.hon.s2games.com");
                Buffer.BlockCopy(dom, 0, buffer, 0x50, dom.Length);

                File.WriteAllBytes(fullPath, buffer);
            }

            // Act - Pass 1
            List<string> logs = [];
            bool pass1Result = EnginePatchService.EnsureEnginePatched(tempDir, logs.Add);

            pass1Result.Should().BeTrue();

            foreach ((string relPath, int offset, _) in linuxTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                File.Exists($"{fullPath}.bak").Should().BeTrue();

                byte[] patched = File.ReadAllBytes(fullPath);
                patched[offset..(offset + 4)].Should().Equal(Fps1000,
                    $"Target {relPath} should have 1000.0f at offset 0x{offset:X}");

                string content = Encoding.ASCII.GetString(patched);
                content.Should().NotContain("masterserver.hon.s2games.com");
                content.Should().Contain("0.0.0.0");
            }

            // Act - Pass 2 (Idempotency)
            List<string> pass2Logs = [];
            bool pass2Result = EnginePatchService.EnsureEnginePatched(tempDir, pass2Logs.Add);

            pass2Result.Should().BeTrue();
            pass2Logs.Should().OnlyContain(l => l.Contains("is fully up to date."));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_MacOS_KnownAndDynamicOffsets_FullPatchAndFastPath()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Stress_Mac_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            (string RelPath, int Offset, bool IsDynamic)[] macTargets =
            [
                ("libk2.dylib", 0x01614B8C, false),
                (Path.Combine("macos-x64", "libk2.dylib"), 0x01614B8C, false),
                (Path.Combine("game", "cgame.dylib"), 0x24000, true),
                (Path.Combine("macos-x64", "game", "cgame.dylib"), 0x28000, true)
            ];

            foreach ((string relPath, int offset, _) in macTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                byte[] buffer = new byte[offset + 0x200];
                Buffer.BlockCopy(Fps250, 0, buffer, offset, 4);

                byte[] dom = Encoding.Unicode.GetBytes("masterserver.savage2.s2games.com");
                Buffer.BlockCopy(dom, 0, buffer, 0x60, dom.Length);

                File.WriteAllBytes(fullPath, buffer);
            }

            // Act - Pass 1
            List<string> logs = [];
            bool pass1Result = EnginePatchService.EnsureEnginePatched(tempDir, logs.Add);

            pass1Result.Should().BeTrue();

            foreach ((string relPath, int offset, _) in macTargets)
            {
                string fullPath = Path.Combine(tempDir, relPath);
                File.Exists($"{fullPath}.bak").Should().BeTrue();

                byte[] patched = File.ReadAllBytes(fullPath);
                patched[offset..(offset + 4)].Should().Equal(Fps1000,
                    $"Target {relPath} should have 1000.0f at offset 0x{offset:X}");

                string content = Encoding.Unicode.GetString(patched);
                content.Should().NotContain("masterserver.savage2.s2games.com");
                content.Should().Contain("0.0.0.0");
            }

            // Act - Pass 2 (Idempotency)
            List<string> pass2Logs = [];
            bool pass2Result = EnginePatchService.EnsureEnginePatched(tempDir, pass2Logs.Add);

            pass2Result.Should().BeTrue();
            pass2Logs.Should().OnlyContain(l => l.Contains("is fully up to date."));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_DeadLegacyDomains_All7Domains_BothEncodings_AndOld127001Upgrade()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        int bufferSize = 0x10000;
        byte[] buffer = new byte[bufferSize];
        int writePos = 0x100;

        foreach (string domain in LegacyDomains)
        {
            // 1. Pristine ASCII
            byte[] asciiBytes = Encoding.ASCII.GetBytes(domain);
            Buffer.BlockCopy(asciiBytes, 0, buffer, writePos, asciiBytes.Length);
            writePos += asciiBytes.Length + 16;

            // 2. Old 127.0.0.1 ASCII patch
            string oldAscii = "127.0.0.1".PadRight(domain.Length, '\0');
            byte[] oldAsciiBytes = Encoding.ASCII.GetBytes(oldAscii);
            Buffer.BlockCopy(oldAsciiBytes, 0, buffer, writePos, oldAsciiBytes.Length);
            writePos += oldAsciiBytes.Length + 16;

            // 3. Pristine UTF-16LE
            byte[] wideBytes = Encoding.Unicode.GetBytes(domain);
            Buffer.BlockCopy(wideBytes, 0, buffer, writePos, wideBytes.Length);
            writePos += wideBytes.Length + 16;

            // 4. Old 127.0.0.1 UTF-16LE patch
            string oldWide = "127.0.0.1".PadRight(domain.Length, '\0');
            byte[] oldWideBytes = Encoding.Unicode.GetBytes(oldWide);
            Buffer.BlockCopy(oldWideBytes, 0, buffer, writePos, oldWideBytes.Length);
            writePos += oldWideBytes.Length + 16;
        }

        // Act - Pass 1
        int replacementsPass1 = EnginePatchService.SuppressLegacyDomains(buffer);
        replacementsPass1.Should().Be(LegacyDomains.Length * 4,
            "All 7 domains in 4 variations (28 occurrences) must be replaced");

        // Verify no traces of domain or 127.0.0.1 remain
        string resultAscii = Encoding.ASCII.GetString(buffer);
        string resultWide = Encoding.Unicode.GetString(buffer);

        foreach (string domain in LegacyDomains)
        {
            resultAscii.Should().NotContain(domain);
            resultWide.Should().NotContain(domain);
        }

        resultAscii.Should().NotContain("127.0.0.1");
        resultWide.Should().NotContain("127.0.0.1");

        // Act - Pass 2 (Idempotency)
        int replacementsPass2 = EnginePatchService.SuppressLegacyDomains(buffer);
        replacementsPass2.Should().Be(0, "Already nullified domains should require 0 replacements");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_UserDirectoryRedirection_RelocatedPattern_AndBoundarySafety()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 1. Relocated pattern at non-standard offset
        int customOffset = 0x34567;
        byte[] buffer = new byte[customOffset + UserDirOriginal.Length + 100];
        Buffer.BlockCopy(UserDirOriginal, 0, buffer, customOffset, UserDirOriginal.Length);

        EnginePatchService.IsUserDirectoryPatched(buffer).Should().BeFalse();

        bool redirected = EnginePatchService.RedirectUserDirectory(buffer);
        redirected.Should().BeTrue();

        EnginePatchService.IsUserDirectoryPatched(buffer).Should().BeTrue();
        buffer[customOffset..(customOffset + UserDirPatched.Length)].Should().Equal(UserDirPatched);

        // Second call should return false (already patched, original pattern no longer present)
        bool redirectedAgain = EnginePatchService.RedirectUserDirectory(buffer);
        redirectedAgain.Should().BeFalse();

        // 2. Boundary safety: buffers near pattern length
        for (int len = 0; len <= UserDirOriginal.Length; len++)
        {
            byte[] smallBuf = new byte[len];
            EnginePatchService.RedirectUserDirectory(smallBuf).Should().BeFalse();
            EnginePatchService.IsUserDirectoryPatched(smallBuf).Should().BeFalse();
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_MalformedAndEdgeCaseBinaries_NoExceptionsThrown()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Stress_Edge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create target files with unusual sizes
            // Empty file
            File.WriteAllBytes(Path.Combine(tempDir, "k2_x64.dll"), []);
            // 3-byte file (less than float size)
            Directory.CreateDirectory(Path.Combine(tempDir, "game"));
            File.WriteAllBytes(Path.Combine(tempDir, "game", "cgame_x64.dll"), [0x00, 0x00, 0x7A]);
            // 4-byte file with non-matching bytes
            Directory.CreateDirectory(Path.Combine(tempDir, "editor"));
            File.WriteAllBytes(Path.Combine(tempDir, "editor", "cgame_x64.dll"), [0x11, 0x22, 0x33, 0x44]);

            // Act
            List<string> logs = [];
            bool result = EnginePatchService.EnsureEnginePatched(tempDir, logs.Add);

            // Should complete cleanly without throwing unhandled exceptions
            result.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_AlreadyPatchedBinary_FastPathBypassesFullScan()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Stress_FastPath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Large 32MB file with 1000.0f at known offset, and 250.0f placed at an unrelated later offset
            int knownOffset = 0xFB416C;
            int lateOffset = 0x1E00000;
            int fileSize = 0x2000000; // 32MB

            byte[] buffer = new byte[fileSize];
            // Place 1000.0f at known offset
            Buffer.BlockCopy(Fps1000, 0, buffer, knownOffset, 4);
            // Place 250.0f at late offset
            Buffer.BlockCopy(Fps250, 0, buffer, lateOffset, 4);

            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, buffer);

            // Act
            List<string> logs = [];
            bool result = EnginePatchService.EnsureEnginePatched(tempDir, logs.Add);

            result.Should().BeTrue();

            byte[] inspected = File.ReadAllBytes(k2Path);
            // Because known offset was already 1000.0f, fast path took effect and didn't touch lateOffset
            inspected[knownOffset..(knownOffset + 4)].Should().Equal(Fps1000);
            inspected[lateOffset..(lateOffset + 4)].Should().Equal(Fps250);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
