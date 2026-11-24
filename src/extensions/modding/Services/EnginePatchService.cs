// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Structured binary machine-code patch definition for known crash hotspots and safety guards.
/// </summary>
public sealed record BinaryPatchDefinition(
    string Id,
    string Name,
    string TargetBinaryName,
    string Description,
    int KnownOffset,
    byte[] OriginalBytes,
    byte[] PatchedBytes,
    byte[]? SearchPattern = null,
    bool IsMandatory = true
);

/// <summary>
///     Engine binary patch inspection item.
/// </summary>
public sealed record EnginePatchStatusItem(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool HasBak,
    bool HasBakOld,
    bool IsFpsUnclamped,
    bool IsUserDirRedirected,
    bool IsVidDriverPatched,
    int SuppressedLegacyDomainsCount,
    IReadOnlyList<string> AppliedCrashFixes,
    IReadOnlyList<string> PendingCrashFixes,
    IReadOnlyList<string> ImportedDllNames
);

/// <summary>
///     Comprehensive engine binary status report.
/// </summary>
public sealed record EnginePatchStatusReport(
    string GameDirectory,
    bool IsHealthy,
    IReadOnlyList<EnginePatchStatusItem> Binaries,
    string Timestamp,
    IReadOnlyList<Kongor.Core.Services.Native.NativeBinaryTarget>? NativeLibraries = null,
    IReadOnlyList<Kongor.Core.Services.Native.SecurityBinaryTarget>? SecurityTargets = null
);

/// <summary>
///     Provides high-performance binary tuning and safety patching for the K2 Game Engine and Client Game binaries.
///     Lifts the legacy 250 FPS timing clamp to 1000 FPS for ultra-high refresh rate displays and RTX GPUs,
///     and suppresses dead legacy backend heartbeats to eliminate 6000ms synchronous frame hitches.
/// </summary>
public static class EnginePatchService
{
    // Instruction: jl 0x8b7e40 (7C 96) in CHost::Execute
    private const int ThrottleJumpOffset = 0x8B72A8;

    // User Directory Redirection: CSystem::Init (SHGetFolderPathW -> local userdata/)
    private const int UserDirPatternOffset = 0x9FE37A;

    // 250.0f in IEEE-754 32-bit float (0x437A0000)
    private static readonly byte[] Fps250Pattern = [0x00, 0x00, 0x7A, 0x43];

    // 1000.0f in IEEE-754 32-bit float (0x447A0000)
    private static readonly byte[] Fps1000Pattern = [0x00, 0x00, 0x7A, 0x44];
    private static readonly byte[] ThrottleJumpOriginal = [0x7C, 0x96];
    private static readonly byte[] ThrottleJumpPatched = [0x90, 0x90]; // NOP NOP

    // Video Driver ChangeDisplaySettingsW failure bypass: vid_d3d9_x64.dll
    public const int VidDriverOffset = 0x0005B0D0;
    private static readonly byte[] VidDriverOriginal = [0x85, 0xC0, 0x74, 0x6D];
    private static readonly byte[] VidDriverPatched = [0x85, 0xC0, 0xEB, 0x6D];

    public const int VidDriverSetModeBypassOffset = 0x0005BED0;
    public static readonly byte[] VidDriverSetModeOriginal =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10
    ];
    public static readonly byte[] VidDriverSetModePatched =
    [
        0xC3, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
    ];

    private static readonly byte[] UserDirOriginalPattern =
    [
        0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
        0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
        0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
        0x3C, 0x40, 0x00, 0x75, 0xF5
    ];

    private static readonly byte[] UserDirPatchedPattern =
    [
        0x48, 0xB8, 0x75, 0x00, 0x73, 0x00, 0x65, 0x00, 0x72, 0x00, // mov rax, L"user" (10 bytes)
        0x48, 0x89, 0x45, 0x90, // mov [rbp - 70h], rax (4 bytes)
        0x48, 0xB8, 0x64, 0x00, 0x61, 0x00, 0x74, 0x00, 0x61, 0x00, // mov rax, L"data" (10 bytes)
        0x48, 0x89, 0x45, 0x98, // mov [rbp - 68h], rax (4 bytes)
        0x66, 0xC7, 0x45, 0xA0, 0x00, 0x00, // mov word ptr [rbp - 60h], 0 (6 bytes)
        0x49, 0xC7, 0xC0, 0x08, 0x00, 0x00, 0x00, // mov r8, 8 (length) (7 bytes)
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 // 12 NOPs
    ];

    private static readonly AobPattern UserDirOriginalPatternAob = AobPattern.FromBytes(UserDirOriginalPattern);
    private static readonly AobPattern UserDirPatchedPatternAob = AobPattern.FromBytes(UserDirPatchedPattern);
    private static readonly AobPattern VidDriverOriginalAob = AobPattern.FromBytes(VidDriverOriginal);
    private static readonly AobPattern VidDriverPatchedAob = AobPattern.FromBytes(VidDriverPatched);
    private static readonly AobPattern VidDriverSetModeOriginalAob = AobPattern.FromBytes(VidDriverSetModeOriginal);
    private static readonly AobPattern VidDriverSetModePatchedAob = AobPattern.FromBytes(VidDriverSetModePatched);
    private static readonly AobPattern Fps250PatternAob = AobPattern.FromBytes(Fps250Pattern);
    private static readonly AobPattern Fps1000PatternAob = AobPattern.FromBytes(Fps1000Pattern);
    private static readonly AobPattern ThrottleJumpOriginalAob = AobPattern.FromBytes(ThrottleJumpOriginal);
    private static readonly AobPattern ThrottleJumpPatchedAob = AobPattern.FromBytes(ThrottleJumpPatched);

    private static readonly (string RelativePath, int KnownOffset)[] TargetBinaries =
    [
        // Windows binaries (root and windows-x64/)
        ("hon_x64.exe", -1),
        (Path.Combine("windows-x64", "hon_x64.exe"), -1),
        ("k2_x64.dll", 0xFB416C),
        (Path.Combine("windows-x64", "k2_x64.dll"), 0xFB416C),
        (Path.Combine("game", "cgame_x64.dll"), 0x26BD74),
        (Path.Combine("windows-x64", "game", "cgame_x64.dll"), 0x26BD74),
        (Path.Combine("editor", "cgame_x64.dll"), 0x097E59),
        (Path.Combine("windows-x64", "editor", "cgame_x64.dll"), 0x097E59),
        ("vid_d3d9_x64.dll", -1),
        (Path.Combine("windows-x64", "vid_d3d9_x64.dll"), -1),
        (Path.Combine("game", "game_x64.dll"), -1),
        (Path.Combine("windows-x64", "game", "game_x64.dll"), -1),
        ("game_shared_x64.dll", -1),
        (Path.Combine("windows-x64", "game_shared_x64.dll"), -1),
        (Path.Combine("game", "game_shared_x64.dll"), -1),
        (Path.Combine("windows-x64", "game", "game_shared_x64.dll"), -1),
        (Path.Combine("editor", "game_shared_x64.dll"), -1),
        (Path.Combine("windows-x64", "editor", "game_shared_x64.dll"), -1),

        // Linux client and server binaries (root and linux-x64/)
        ("hon-x86_64", -1),
        (Path.Combine("linux-x64", "hon-x86_64"), -1),
        ("hon-x86_64-server", -1),
        (Path.Combine("linux-x64", "hon-x86_64-server"), -1),
        ("libk2-x86_64.so", 0x01533D10),
        (Path.Combine("linux-x64", "libk2-x86_64.so"), 0x01533D10),
        ("libk2-x86_64-server.so", -1),
        (Path.Combine("linux-x64", "libk2-x86_64-server.so"), -1),
        (Path.Combine("game", "cgame-x86_64.so"), -1),
        (Path.Combine("linux-x64", "game", "cgame-x86_64.so"), -1),
        (Path.Combine("game", "game-x86_64-server.so"), -1),
        (Path.Combine("linux-x64", "game", "game-x86_64-server.so"), -1),
        (Path.Combine("game", "libgame_shared-x86_64-server.so"), -1),
        (Path.Combine("linux-x64", "game", "libgame_shared-x86_64-server.so"), -1),
        (Path.Combine("game", "libgame_shared-x86_64.so"), -1),
        (Path.Combine("linux-x64", "game", "libgame_shared-x86_64.so"), -1),

        // macOS binaries (root and macos-x64/)
        ("hon", -1),
        (Path.Combine("macos-x64", "hon"), -1),
        ("libk2.dylib", 0x01614B8C),
        (Path.Combine("macos-x64", "libk2.dylib"), 0x01614B8C),
        (Path.Combine("game", "cgame.dylib"), -1),
        (Path.Combine("macos-x64", "game", "cgame.dylib"), -1)
    ];

    private static readonly string[] LegacyDomainsToNullify =
    [
        "client.sea.heroesofnewerth.com",
        "masterserver.naeu.heroesofnewerth.com",
        "logcollect.naeu.heroesofnewerth.com",
        "messaging-naeu.heroesofnewerth.com",
        "masterserver.hon.s2games.com",
        "masterserver.cis.heroesofnewerth.com",
        "masterserver.savage2.s2games.com",
        "fun.rct.sea.heroesofnewerth.com",
        "fun.naeu.heroesofnewerth.com",
        "fun.sbt.naeu.heroesofnewerth.com",
        "crashdump.naeu.heroesofnewerth.com",
        "api.heroesofnewerth.com",
        "patch.heroesofnewerth.com",
        "dl.heroesofnewerth.com"
    ];




    private static readonly byte[] UnicodeHeroesOfNewerth = Encoding.Unicode.GetBytes("heroesofnewerth");
    private static readonly byte[] UnicodeS2Games = Encoding.Unicode.GetBytes("s2games");

    private static readonly (byte[] OrigAscii, byte[] OldPatchAscii, byte[] NewPatchAscii, byte[] OrigWide, byte[] OldPatchWide, byte[] NewPatchWide, byte[] OrigUtf32, byte[] OldPatchUtf32, byte[] NewPatchUtf32)[] PrecompiledPatterns;

    private static readonly byte[] UnicodeLocalhost = Encoding.Unicode.GetBytes("127.0.0.1");
    private static readonly byte[] Utf32HeroesOfNewerth = Encoding.UTF32.GetBytes("heroesofnewerth");
    private static readonly byte[] Utf32S2Games = Encoding.UTF32.GetBytes("s2games");
    private static readonly byte[] Utf32Localhost = Encoding.UTF32.GetBytes("127.0.0.1");

    static EnginePatchService()
    {
        List<(byte[], byte[], byte[], byte[], byte[], byte[], byte[], byte[], byte[])> list = [];
        string replacement = "0.0.0.0";
        string oldPatch = "127.0.0.1";
        foreach (string domain in LegacyDomainsToNullify)
        {
            byte[] origAscii = Encoding.ASCII.GetBytes(domain);
            byte[] oldPatchAscii = CreatePaddedPattern(oldPatch, origAscii.Length, Encoding.ASCII);
            byte[] newPatchAscii = CreatePaddedPattern(replacement, origAscii.Length, Encoding.ASCII);

            byte[] origWide = Encoding.Unicode.GetBytes(domain);
            byte[] oldPatchWide = CreatePaddedPattern(oldPatch, origWide.Length / 2, Encoding.Unicode);
            byte[] newPatchWide = CreatePaddedPattern(replacement, origWide.Length / 2, Encoding.Unicode);

            byte[] origUtf32 = Encoding.UTF32.GetBytes(domain);
            byte[] oldPatchUtf32 = CreatePaddedPattern(oldPatch, origUtf32.Length / 4, Encoding.UTF32);
            byte[] newPatchUtf32 = CreatePaddedPattern(replacement, origUtf32.Length / 4, Encoding.UTF32);

            list.Add((origAscii, oldPatchAscii, newPatchAscii, origWide, oldPatchWide, newPatchWide, origUtf32, oldPatchUtf32, newPatchUtf32));
        }

        PrecompiledPatterns = [.. list];
    }

    /// <summary>
    ///     Safely validates and patches all engine and client binaries to uncap framerates beyond 250 FPS,
    ///     redirect user directory initialization to local userdata/, and eliminate dead legacy URL frame delays.
    ///     Creates a verified .bak backup prior to any modification.
    /// </summary>
    // satisfies: OBL-01
    private static bool ShouldPatchTargetOnCurrentHost(string relativePath, string gameDirectory, bool? patchMacOsDylibsOverride = null)
    {
        string normalized = (relativePath ?? string.Empty).Replace('\\', '/');
        string normalizedDir = (gameDirectory ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        bool isMacOsTarget = normalized.StartsWith("macos-x64/", StringComparison.OrdinalIgnoreCase) ||
                             normalized.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(normalized, "hon", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(normalized, "HoN64", StringComparison.OrdinalIgnoreCase);

        if (isMacOsTarget)
        {
            return patchMacOsDylibsOverride ?? string.Equals(Environment.GetEnvironmentVariable("KONGOR_MACOS_PATCH_DYLIBS"), "1", StringComparison.Ordinal);
        }

        bool isLinuxTarget = normalized.StartsWith("linux-x64/", StringComparison.OrdinalIgnoreCase) ||
                             normalized.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                             normalized.Contains("hon-x86_64", StringComparison.OrdinalIgnoreCase);
        if (isLinuxTarget)
        {
            return OperatingSystem.IsLinux() ||
                   !normalized.StartsWith("linux-x64/", StringComparison.OrdinalIgnoreCase) ||
                   normalizedDir.EndsWith("/linux-x64", StringComparison.OrdinalIgnoreCase);
        }

        return !normalizedDir.EndsWith("/macos-x64", StringComparison.OrdinalIgnoreCase) &&
               !normalizedDir.EndsWith("/linux-x64", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAdHocCodesignMacOsBinary(string fullPath, Action<string>? logAction)
    {
        if (!OperatingSystem.IsMacOS() || !fullPath.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            System.Diagnostics.ProcessStartInfo psi = new("/usr/bin/codesign")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--force");
            psi.ArgumentList.Add("--sign");
            psi.ArgumentList.Add("-");
            psi.ArgumentList.Add(fullPath);

            using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(10000);
                if (proc.ExitCode == 0)
                {
                    logAction?.Invoke($"[ENGINE_PATCH] Ad-hoc re-signed modified Mach-O binary: '{fullPath}'.");
                }
                else
                {
                    string stderr = proc.StandardError.ReadToEnd();
                    logAction?.Invoke($"[ENGINE_PATCH] ERROR: codesign failed (exit {proc.ExitCode}) on '{fullPath}': {stderr}. Fix: Run /usr/bin/codesign --force --sign - \"{fullPath}\"");
                }
            }
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[ENGINE_PATCH] ERROR: Unable to invoke /usr/bin/codesign for '{fullPath}': {ex.Message}. Fix: Run /usr/bin/codesign --force --sign - \"{fullPath}\"");
        }
    }

    public static bool EnsureEnginePatched(string gameDirectory, Action<string>? logAction = null, bool? patchMacOsDylibsOverride = null)
    {
        bool allSucceeded = true;

        foreach ((string relativePath, int knownOffset) in TargetBinaries)
        {
            if (!ShouldPatchTargetOnCurrentHost(relativePath, gameDirectory, patchMacOsDylibsOverride)) continue;
            string fullPath = Path.Combine(gameDirectory, relativePath);
            string backupPath = $"{fullPath}.bak";

            if (File.Exists(fullPath) is false) continue;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(fullPath);
                bool modified = false;

                // Step 1: Patch float constants (250.0f -> 1000.0f)
                var fpsCheck = AobScanner.VerifyFastPathOrScan(fileBytes, knownOffset, Fps1000PatternAob, Fps1000Pattern);
                if (!fpsCheck.Found)
                {
                    var fpsScan = AobScanner.VerifyFastPathOrScan(fileBytes, knownOffset, Fps250PatternAob, Fps250Pattern);
                    if (fpsScan.Found && fpsScan.Offset + 4 <= fileBytes.Length)
                    {
                        fileBytes[fpsScan.Offset] = Fps1000Pattern[0];
                        fileBytes[fpsScan.Offset + 1] = Fps1000Pattern[1];
                        fileBytes[fpsScan.Offset + 2] = Fps1000Pattern[2];
                        fileBytes[fpsScan.Offset + 3] = Fps1000Pattern[3];
                        modified = true;
                        logAction?.Invoke(
                            $"[ENGINE_PATCH] Patched {relativePath} float constant at 0x{fpsScan.Offset:X8}: 250 FPS -> 1000 FPS{(fpsScan.WasFastPath ? " (FastPath)" : " (AOB Scanned)")}.");
                        if (knownOffset < 0)
                        {
                            logAction?.Invoke(
                                $"[ENGINE_PATCH] WARN: {relativePath} patched via unanchored scan at 0x{fpsScan.Offset:X8}; no verified offset on record.");
                        }
                    }
                }

                // Step 2: In k2_x64.dll, patch the frame throttle jump (7C 96 -> 90 90)
                if (relativePath.EndsWith("k2_x64.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var throttleCheck = AobScanner.VerifyFastPathOrScan(fileBytes, ThrottleJumpOffset, ThrottleJumpPatchedAob, ThrottleJumpPatched);
                    if (!throttleCheck.Found)
                    {
                        var throttleScan = AobScanner.VerifyFastPathOrScan(fileBytes, ThrottleJumpOffset, ThrottleJumpOriginalAob, ThrottleJumpOriginal);
                        if (throttleScan.Found && throttleScan.Offset + ThrottleJumpPatched.Length <= fileBytes.Length)
                        {
                            fileBytes[throttleScan.Offset] = ThrottleJumpPatched[0];
                            fileBytes[throttleScan.Offset + 1] = ThrottleJumpPatched[1];
                            modified = true;
                            logAction?.Invoke(
                                $"[ENGINE_PATCH] NOPed out host frame throttle jump in {relativePath} at 0x{throttleScan.Offset:X8}{(throttleScan.WasFastPath ? " (FastPath)" : " (AOB Scanned)")}.");
                        }
                    }

                    // Step 3: In k2_x64.dll, redirect user directory initialization (SHGetFolderPathW -> local userdata/)
                    var userDirCheck = AobScanner.VerifyFastPathOrScan(fileBytes, UserDirPatternOffset, UserDirPatchedPatternAob, UserDirPatchedPattern);
                    if (!userDirCheck.Found)
                    {
                        var userDirScan = AobScanner.VerifyFastPathOrScan(fileBytes, UserDirPatternOffset, UserDirOriginalPatternAob, UserDirOriginalPattern);
                        if (userDirScan.Found && userDirScan.Offset + UserDirPatchedPattern.Length <= fileBytes.Length)
                        {
                            Buffer.BlockCopy(UserDirPatchedPattern, 0, fileBytes, userDirScan.Offset, UserDirPatchedPattern.Length);
                            modified = true;
                            logAction?.Invoke(
                                $"[ENGINE_PATCH] Redirected user directory initialization in {relativePath} at 0x{userDirScan.Offset:X8} to local userdata/{(userDirScan.WasFastPath ? " (FastPath)" : " (AOB Scanned)")}.");
                        }
                    }
                }

                // Step 4: Suppress dead legacy heartbeat domains to 0.0.0.0
                int suppressedCount = SuppressLegacyDomains(fileBytes);
                if (suppressedCount > 0)
                {
                    modified = true;
                    logAction?.Invoke(
                        $"[ENGINE_PATCH] Redirected {suppressedCount} dead legacy heartbeat URLs in {relativePath} to 0.0.0.0 (Suppressed 6000ms stalls).");
                }

                // Step 5: In vid_d3d9_x64.dll, patch ChangeDisplaySettingsW failure check (74 6D -> EB 6D) and D3D_SetMode failure (ret 0xC3)
                if (relativePath.EndsWith("vid_d3d9_x64.dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (PatchVidDriver(fileBytes))
                    {
                        modified = true;
                        logAction?.Invoke(
                            $"[ENGINE_PATCH] Patched {relativePath}: bypassed ChangeDisplaySettingsW and D3D_SetMode failure.");
                    }
                }

                // Step 6: In k2_x64.dll and client binaries, apply registered machine-code crash fixes
                foreach (BinaryPatchDefinition fix in RegisteredCrashFixes)
                {
                    if (relativePath.EndsWith(fix.TargetBinaryName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ApplyCrashFix(fileBytes, fix, logAction))
                        {
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    // Ensure two-tier backup exists before saving modifications (.bak and .bak.old)
                    string bakOldPath = $"{fullPath}.bak.old";
                    if (File.Exists(backupPath))
                    {
                        if (!File.Exists(bakOldPath))
                        {
                            File.Copy(backupPath, bakOldPath, true);
                            logAction?.Invoke($"[ENGINE_PATCH] Preserved historical baseline: '{bakOldPath}'.");
                        }
                    }
                    else
                    {
                        File.Copy(fullPath, backupPath, false);
                        logAction?.Invoke($"[ENGINE_PATCH] Created verified backup: '{backupPath}'.");
                    }

                    File.WriteAllBytes(fullPath, fileBytes);
                    TryAdHocCodesignMacOsBinary(fullPath, logAction);
                    logAction?.Invoke($"[ENGINE_PATCH] Saved optimized binary: {relativePath}.");
                }
                else
                {
                    logAction?.Invoke($"[ENGINE_PATCH] {relativePath} is fully up to date.");
                }
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[ENGINE_PATCH] Failed to patch {relativePath}: {ex.Message}");
                allSucceeded = false;
            }
        }

        ValidateNativeDependencies(gameDirectory, logAction);
        return allSucceeded;
    }

    /// <summary>
    ///     Inspects and logs native binary security status including zlibwapi_x64.dll.
    /// </summary>
    public static void ValidateNativeDependencies(string gameDirectory, Action<string>? logAction = null)
    {
        try
        {
            var status = Kongor.Core.Services.Native.NativeBinaryManager.GetStatus(gameDirectory);
            foreach (var item in status)
            {
                if (item.Exists)
                {
                    logAction?.Invoke($"[NATIVE_SECURITY] {item.RelativePath}: v{item.FileVersion} ({item.SecurityStatus})");
                }
            }
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[NATIVE_SECURITY] Unable to inspect native dependencies: {ex.Message}");
        }
    }

    private static readonly byte[] HotreloadPayloadPatchedBytes =
        CreatePaddedPattern("UnloadInterface main;LoadInterface /ui/fe2/main_v2.interface;ShowInterface main\0", 80, Encoding.Unicode);

    private static readonly byte[] HotreloadPayloadPreviousPatchedBytes =
        CreatePaddedPattern("ReloadInterface main;ShowInterface none;ShowInterface main\0", 59, Encoding.Unicode);

    private static readonly byte[] HotreloadPayloadOriginalBytes = new byte[HotreloadPayloadPatchedBytes.Length];

    /// <summary>
    ///     Registered machine-code crash mitigation patches for documented engine crash hotspots.
    /// </summary>
    public static readonly IReadOnlyList<BinaryPatchDefinition> RegisteredCrashFixes =
    [
        new(
            "CRASH_SNAPSHOT_DELTA_NULL",
            "Entity Snapshot Delta Null-Pointer Guard",
            "k2_x64.dll",
            "Guards CClientSnapshot::ProcessEntityDelta against null entity pointer dereference on player disconnect/despawn during tick interpolation.",
            0x7C1E40,
            [0x48, 0x8B, 0x81, 0x88, 0x00, 0x00, 0x00],
            [0x48, 0x85, 0xC9, 0x74, 0x12, 0x90, 0x90]
        ),
        new(
            "CRASH_AUDIO_SAMPLE_STOP_NULL",
            "Audio Sample Cleanup Null Guard",
            "k2_x64.dll",
            "Guards CSoundManager::StopSample against null sample/channel structure dereference on map transition or shutdown.",
            0x5A4C20,
            [0x48, 0x8B, 0x4B, 0x10, 0x48, 0x85, 0xC9],
            [0x48, 0x85, 0xDB, 0x74, 0x08, 0x90, 0x90]
        ),
        new(
            "CRASH_AUTO_CONCEDE_PRACTICE_BOTS",
            "Bot Match / Practice Mode Auto-Concede Safety Guard",
            "game_x64.dll",
            "Ensures m_bPracticeMode is unconditionally set when starting practice matches with bots, preventing instant team concession on tick 0.",
            0x800A2,
            [
                0x85, 0xDB, 0x75, 0x20, 0x85, 0xC0, 0x75, 0x1C, 0x66, 0xC7, 0x86, 0xA8, 0x05, 0x00, 0x00, 0x00, 0x01
            ],
            [
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x66, 0xC7, 0x86, 0xA8, 0x05, 0x00, 0x00, 0x00, 0x01
            ]
        ),
        new(
            "CRASH_AUTO_CONCEDE_LOADING_GUARD",
            "Hero Select Auto-Concede Loading Latency Guard",
            "game_x64.dll",
            "Guards CGameServer::Frame from auto-conceding during hero selection while clients are loading world assets.",
            0x825E1,
            [0x85, 0xC0, 0x74, 0x2B, 0x41, 0xBD, 0x01, 0x00, 0x00, 0x00],
            [0x85, 0xC0, 0x90, 0x90, 0x41, 0xBD, 0x01, 0x00, 0x00, 0x00]
        ),
        new(
            "CRASH_BOT_HERO_SELECT_BYPASS",
            "Bot Match Hero Selection Bypass Removal Guard",
            "game_x64.dll",
            "Prevents CGameServer::AddBot from conditionally skipping CPlayer::SelectHero, ensuring designated bot heroes are locked in before the pick timer expires.",
            0x69644,
            [
                0x74, 0x2F, 0x48, 0x8B, 0x45, 0x7F, 0x0F, 0xB7, 0x50, 0x08, 0x48, 0x8B, 0xCF
            ],
            [
                0x90, 0x90, 0x48, 0x8B, 0x45, 0x7F, 0x0F, 0xB7, 0x50, 0x08, 0x48, 0x8B, 0xCF
            ]
        ),
        new(
            "CRASH_MATCHMAKING_DISCONNECT_GUARD",
            "Matchmaking Disconnect Abort Safety Guard",
            "game_x64.dll",
            "Guards CGameServer::Frame from prematurely aborting bot and local matches with disconnect_game_over due to legacy TMM human player count checks.",
            0x82C81,
            [
                0x80, 0x3D, 0x61, 0x8E, 0x08, 0x00, 0x00, 0x0F, 0x84, 0x62, 0x01, 0x00, 0x00
            ],
            [
                0x80, 0x3D, 0x61, 0x8E, 0x08, 0x00, 0x00, 0xE9, 0xA1, 0x01, 0x00, 0x00, 0x90
            ],
            [
                0x80, 0x3D, 0x61, 0x8E, 0x08, 0x00, 0x00, 0xE9, 0x63, 0x01, 0x00, 0x00, 0x90
            ]
        ),
        new(
            "CRASH_DISCONNECT_GAME_OVER_CALL_GUARD",
            "CGameServer::Frame disconnect_game_over EndGame Call Bypass",
            "game_x64.dll",
            "Replaces JE +0x32 with JMP +0x32 at 0x82DFA, ensuring CGameServer::Frame unconditionally jumps over EndGame(0x17) disconnect_game_over.",
            0x82DFA,
            [ 0x74, 0x32 ],
            [ 0xEB, 0x32 ]
        ),
        new(
            "CRASH_UNASSIGNED_TEAM_DISCONNECT_GUARD",
            "StartMatch Unassigned Team Disconnect Guard",
            "game_x64.dll",
            "Guards CGameServer::StartMatch from disconnecting unassigned players with disconnect_unassigned, automatically promoting unassigned players to Team 1 (Legion).",
            0x7E08C,
            [
                0x83, 0xF9, 0xFC, 0x74, 0x05, 0x83, 0xF9, 0xFF, 0x75, 0x0E, 0xBA, 0x36, 0x00, 0x00, 0x00, 0x48,
                0x8B, 0xC8, 0xFF, 0x15, 0x7C, 0xB8, 0x04, 0x00
            ],
            [
                0x83, 0xF9, 0x01, 0x7D, 0x13, 0xC7, 0x87, 0xAC, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xEB,
                0x07, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
            ]
        ),
        new(
            "CRASH_MATCHMAKING_DISCONNECT_GUARD_SERVER",
            "Server Matchmaking Disconnect Abort Safety Guard",
            "game_x64.dll",
            "Guards dedicated CGameServer::Frame from prematurely aborting bot and local matches with disconnect_game_over.",
            0x92ADE,
            [
                0x80, 0x3D, 0x04, 0x20, 0x09, 0x00, 0x00, 0x0F, 0x84, 0xC1, 0x00, 0x00, 0x00
            ],
            [
                0x80, 0x3D, 0x04, 0x20, 0x09, 0x00, 0x00, 0xE9, 0xC2, 0x00, 0x00, 0x00, 0x90
            ]
        ),
        new(
            "CRASH_UI_RESOURCE_HANDLE_CHANGED_GUARD",
            "IResourceLibrary::Reload Handle Mismatch Exception Bypass",
            "k2_x64.dll",
            "Replaces CMP EAX, R15D / JNZ throw with MOV R15D, EAX / NOPs in IResourceLibrary::Reload, allowing interface reloading when child interfaces alter slot allocation.",
            0xC110BF,
            [0x41, 0x3B, 0xC7, 0x0F, 0x85, 0x63, 0x02, 0x00, 0x00],
            [0x44, 0x8B, 0xF8, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
        ),
        new(
            "CRASH_UI_FRAME_HOTRELOAD_PAYLOAD",
            "CUIManager::Frame Atomic Hotreload Command String Payload",
            "k2_x64.dll",
            "Stores the UTF-16LE 'UnloadInterface main;LoadInterface /ui/fe2/main_v2.interface;ShowInterface main\\0' command string payload in .text slack space for CUIManager::Frame hotreload.",
            0xE098CC,
            HotreloadPayloadOriginalBytes,
            HotreloadPayloadPatchedBytes,
            HotreloadPayloadPreviousPatchedBytes
        ),
        new(
            "CRASH_UI_FRAME_HOTRELOAD_HOOK",
            "CUIManager::Frame Atomic Hotreload Hook",
            "k2_x64.dll",
            "Redirects CUIManager::Frame deferred reload to safe chained UnloadInterface main;LoadInterface /ui/fe2/main_v2.interface;ShowInterface main payload, setting R8D to 0x4F (79 chars) to reactivate the interface cleanly without black screens.",
            0x00A4A31E,
            [0x66, 0x44, 0x89, 0x75, 0x90, 0x4C, 0x8B, 0xC2, 0x48, 0x8D, 0x15, 0xB3, 0x58, 0x50, 0x00],
            [0x41, 0xB8, 0x4F, 0x00, 0x00, 0x00, 0x48, 0x8D, 0x15, 0xA1, 0xF5, 0x3B, 0x00, 0x90, 0x90],
            [0x41, 0xB8, 0x3A, 0x00, 0x00, 0x00, 0x48, 0x8D, 0x15, 0xA1, 0xF5, 0x3B, 0x00, 0x90, 0x90]
        ),
        new(
            "CRASH_UI_RELOAD_INTERFACES_GUARD",
            "CCmd::ReloadInterfaces Plural Logout & Crash Guard",
            "k2_x64.dll",
            "Guards CCmd::ReloadInterfaces from calling ReloadAll, setting ui_reloadInterfaces = true to route safely into CUIManager::Frame.",
            0x009956E0,
            [
                0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x0D, 0x2D, 0x2D, 0x8E, 0x00, 0xBA, 0x0E, 0x00, 0x00, 0x00,
                0xE8, 0xAB, 0xEE, 0xFF, 0xFF, 0x48, 0x8B, 0xC8, 0x33, 0xD2, 0xE8, 0x71, 0x01, 0x28, 0x00,
                0xB0, 0x01, 0x48, 0x83, 0xC4, 0x28, 0xC3
            ],
            [
                0xC6, 0x05, 0xF2, 0x13, 0x94, 0x00, 0x01, 0xB0, 0x01, 0xC3, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90
            ]
        ),
        new(
            "CRASH_UI_HOVER_WIDGET_NULL_GUARD",
            "CInterface::GetHoverWidget Null-Pointer Safety Guard",
            "k2_x64.dll",
            "Guards CInterface::GetHoverWidget against null interface pointers during interface unloading/reloading, returning NULL instead of dereferencing [NULL + 0x2ed] in CUIManager::Frame.",
            0x009010A0,
            [
                0x80, 0xB9, 0xED, 0x02, 0x00, 0x00, 0x00, 0x74, 0x27, 0x0F, 0x1F, 0x80, 0x00, 0x00, 0x00, 0x00,
                0x48, 0x8B, 0x81, 0x38, 0x02, 0x00, 0x00, 0x48, 0x85, 0xC0, 0x74, 0x14, 0x80, 0xB8, 0xED, 0x02,
                0x00, 0x00, 0x00, 0x48, 0x8B, 0xC8, 0x75, 0xE8, 0x48, 0x8B, 0x80, 0x90, 0x02, 0x00, 0x00, 0xC3,
                0x48, 0x8B, 0x81, 0x90, 0x02, 0x00, 0x00, 0xC3, 0xCC, 0xCC, 0xCC
            ],
            [
                0x48, 0x85, 0xC9, 0x74, 0x33, 0x80, 0xB9, 0xED, 0x02, 0x00, 0x00, 0x00, 0x74, 0x22, 0x66, 0x90,
                0x48, 0x8B, 0x81, 0x38, 0x02, 0x00, 0x00, 0x48, 0x85, 0xC0, 0x74, 0x14, 0x80, 0xB8, 0xED, 0x02,
                0x00, 0x00, 0x00, 0x48, 0x8B, 0xC8, 0x75, 0xE8, 0x48, 0x8B, 0x80, 0x90, 0x02, 0x00, 0x00, 0xC3,
                0x48, 0x8B, 0x81, 0x90, 0x02, 0x00, 0x00, 0xC3, 0x31, 0xC0, 0xC3
            ]
        ),
        new(
            "CRASH_SERVER_BROWSER_WATERMARK_DISCRIMINATOR",
            "Server Browser Ping Socket Watermark Bypass Guard",
            "k2_x64.dll",
            "Forces R9D (net_enableWatermark) to 0 for Socket Type 2 (Ping Socket) at offset 0x95D689 while preserving watermark stripping for Socket Type 0 (Game Socket) at 0x95D4E9, allowing unwatermarked PONG packets to populate the server browser without breaking dedicated game server connections.",
            0x0095D689,
            [0x44, 0x0F, 0xB6, 0x0D, 0x38, 0xA5, 0x95, 0x00],
            [0x45, 0x31, 0xC9, 0x90, 0x90, 0x90, 0x90, 0x90]
        ),
        new(
            "CRASH_NET_WATERMARK_DEFAULT_FALSE",
            "CCvar net_enableWatermark Native Default False Guard",
            "k2_x64.dll",
            "Changes default value of net_enableWatermark from true (1) to false (0) at file offset 0x000F9622 (RVA 0x000FA222) in CCvar constructor.",
            0x000F9622,
            [0x41, 0xB0, 0x01, 0x48, 0x8D, 0x54, 0x24, 0x30, 0x48, 0x8D, 0x0D, 0xCF, 0xE3, 0x1B, 0x01],
            [0x41, 0xB0, 0x00, 0x48, 0x8D, 0x54, 0x24, 0x30, 0x48, 0x8D, 0x0D, 0xCF, 0xE3, 0x1B, 0x01]
        ),
        new(
            "FEATURE_CLIENT_MAP_SETTINGS_EXECUTION",
            "Client Map & Game Settings Configuration Execution Enabler",
            "k2_x64.dll",
            "Enables execution of /game_settings.cfg and /map_settings.cfg in CWorld::StartLoad for clients (m_uiLoadFlags = 2, WORLD_LOAD_CLIENT) on dedicated server connect by changing 'test al, 1; je' to 'test al, 3; je' at file offset 0x00B022E4.",
            0x00B022E4,
            [0xA8, 0x01, 0x0F, 0x84, 0xE6, 0x00, 0x00, 0x00],
            [0xA8, 0x03, 0x0F, 0x84, 0xE6, 0x00, 0x00, 0x00]
        ),
        new(
            "CRASH_FAKEPLAYER_TERMINATION_GUARD_WIN",
            "FakePlayer Connectionless Disconnect Watchdog Termination Guard (Windows)",
            "game_shared_x64.dll",
            "Sets bit 7 (0x80) on connectionless players in CPlayer::InitializeConnectionless, changing mov eax, 0x202 to mov eax, 0x282 so the server disconnect watchdog never terminates FakePlayers.",
            -1,
            [0xB8, 0x02, 0x02, 0x00, 0x00, 0xC6, 0x86, 0x6B, 0x06, 0x00, 0x00, 0x01, 0x66, 0x09, 0x86, 0xDE, 0x03, 0x00, 0x00],
            [0xB8, 0x82, 0x02, 0x00, 0x00, 0xC6, 0x86, 0x6B, 0x06, 0x00, 0x00, 0x01, 0x66, 0x09, 0x86, 0xDE, 0x03, 0x00, 0x00]
        ),
        new(
            "CRASH_FAKEPLAYER_TERMINATION_GUARD_LINUX_SERVER",
            "FakePlayer Connectionless Disconnect Watchdog Termination Guard (Linux Server)",
            "libgame_shared-x86_64-server.so",
            "Sets bit 7 (0x80) on connectionless players in CPlayer::InitializeConnectionless, changing or word ptr [rbx + 0x3ce], 0x202 to 0x282 so the server disconnect watchdog never terminates FakePlayers.",
            0xC951B5,
            [0x66, 0x81, 0x8B, 0xCE, 0x03, 0x00, 0x00, 0x02, 0x02],
            [0x66, 0x81, 0x8B, 0xCE, 0x03, 0x00, 0x00, 0x82, 0x02]
        ),
        new(
            "CRASH_FAKEPLAYER_TERMINATION_GUARD_LINUX_CLIENT",
            "FakePlayer Connectionless Disconnect Watchdog Termination Guard (Linux Client)",
            "libgame_shared-x86_64.so",
            "Sets bit 7 (0x80) on connectionless players in CPlayer::InitializeConnectionless, changing or word ptr [rbx + 0x3ce], 0x202 to 0x282 so the server disconnect watchdog never terminates FakePlayers.",
            0xC9517E,
            [0x66, 0x81, 0x8B, 0xCE, 0x03, 0x00, 0x00, 0x02, 0x02],
            [0x66, 0x81, 0x8B, 0xCE, 0x03, 0x00, 0x00, 0x82, 0x02]
        )
    ];

    /// <summary>
    ///     Checks whether a machine-code crash fix has already been applied to the binary buffer.
    /// </summary>
    public static bool IsCrashFixApplied(byte[] buffer, BinaryPatchDefinition fix)
    {
        if (fix.KnownOffset >= 0 &&
            buffer.Length >= fix.KnownOffset + fix.PatchedBytes.Length &&
            MatchesPattern(buffer, fix.KnownOffset, fix.PatchedBytes))
        {
            return true;
        }

        return FindPatternOffset(buffer, fix.PatchedBytes) != -1;
    }

    /// <summary>
    ///     Applies a machine-code crash fix to the specified binary buffer.
    /// </summary>
    public static bool ApplyCrashFix(byte[] buffer, BinaryPatchDefinition fix, Action<string>? logAction = null)
    {
        if (IsCrashFixApplied(buffer, fix)) return false;

        AobPattern targetPattern = (fix.SearchPattern != null && fix.SearchPattern.Length > 0)
            ? AobPattern.FromBytes(fix.SearchPattern)
            : AobPattern.FromBytes(fix.OriginalBytes);

        byte[] expectedBytes = (fix.SearchPattern != null && fix.SearchPattern.Length > 0)
            ? fix.SearchPattern
            : fix.OriginalBytes;

        var scanResult = AobScanner.VerifyFastPathOrScan(buffer, fix.KnownOffset, targetPattern, expectedBytes);
        int offset = scanResult.Found ? scanResult.Offset : -1;

        if (offset == -1 && fix.SearchPattern != null && fix.SearchPattern.Length > 0)
        {
            var origPattern = AobPattern.FromBytes(fix.OriginalBytes);
            var origResult = AobScanner.VerifyFastPathOrScan(buffer, fix.KnownOffset, origPattern, fix.OriginalBytes);
            if (origResult.Found)
            {
                offset = origResult.Offset;
                scanResult = origResult;
            }
        }

        if (offset != -1 && offset + fix.PatchedBytes.Length <= buffer.Length)
        {
            Buffer.BlockCopy(fix.PatchedBytes, 0, buffer, offset, fix.PatchedBytes.Length);
            logAction?.Invoke($"[ENGINE_PATCH] Applied {fix.Name} ({fix.Id}) at offset 0x{offset:X8}{(scanResult.WasFastPath ? " (FastPath)" : " (AOB Scanned)")}.");
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Reverts a machine-code crash fix in the specified binary buffer back to original machine code.
    /// </summary>
    public static bool RevertCrashFix(byte[] buffer, BinaryPatchDefinition fix, Action<string>? logAction = null)
    {
        if (!IsCrashFixApplied(buffer, fix)) return false;

        int offset = -1;
        if (fix.KnownOffset >= 0 &&
            buffer.Length >= fix.KnownOffset + fix.PatchedBytes.Length &&
            MatchesPattern(buffer, fix.KnownOffset, fix.PatchedBytes))
        {
            offset = fix.KnownOffset;
        }
        else
        {
            offset = FindPatternOffset(buffer, fix.PatchedBytes);
        }

        if (offset != -1 && offset + fix.OriginalBytes.Length <= buffer.Length)
        {
            Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, offset, fix.OriginalBytes.Length);
            logAction?.Invoke($"[ENGINE_PATCH] Reverted {fix.Name} ({fix.Id}) at offset 0x{offset:X8}.");
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Generates a comprehensive diagnostic report of all engine binaries and their patch states.
    /// </summary>
    public static EnginePatchStatusReport GetEnginePatchStatus(string gameDirectory)
    {
        List<EnginePatchStatusItem> items = [];
        bool allHealthy = true;

        // Distinct list of relative targets
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string rel, _) in TargetBinaries)
        {
            if (!seen.Add(rel)) continue;

            string full = Path.Combine(gameDirectory, rel);
            bool exists = File.Exists(full);
            long size = 0;
            string sha = string.Empty;
            bool hasBak = File.Exists(full + ".bak");
            bool hasBakOld = File.Exists(full + ".bak.old");
            bool isFps = false;
            bool isUserDir = false;
            bool isVid = false;
            int suppressedDomains = 0;
            List<string> appliedFixes = [];
            List<string> pendingFixes = [];
            List<string> importedDlls = [];

            if (exists)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(full);
                    size = bytes.Length;
                    sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

                    isFps = IsK2Patched(bytes);
                    isUserDir = IsUserDirectoryPatched(bytes);
                    isVid = IsVidDriverPatched(bytes);

                    foreach (BinaryPatchDefinition fix in RegisteredCrashFixes)
                    {
                        if (rel.EndsWith(fix.TargetBinaryName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsCrashFixApplied(bytes, fix))
                            {
                                appliedFixes.Add(fix.Id);
                            }
                            else
                            {
                                pendingFixes.Add(fix.Id);
                            }
                        }
                    }

                    // Extract IAT imported modules
                    PeImportReport report = PeImportAddressTableService.InspectClientBinaryImports(gameDirectory, rel);
                    if (report.IsValidPe)
                    {
                        foreach (PeImportedDll mod in report.ImportedModules)
                        {
                            importedDlls.Add(mod.DllName);
                        }
                    }
                }
                catch
                {
                    allHealthy = false;
                }
            }

            items.Add(new EnginePatchStatusItem(
                rel,
                full,
                exists,
                size,
                sha,
                hasBak,
                hasBakOld,
                isFps,
                isUserDir,
                isVid,
                suppressedDomains,
                appliedFixes,
                pendingFixes,
                importedDlls
            ));
        }

        var nativeTargets = Kongor.Core.Services.Native.NativeBinaryManager.GetStatus(gameDirectory);
        foreach (var nat in nativeTargets)
        {
            if (nat.Exists && (nat.SecurityStatus.Contains("VULNERABLE", StringComparison.OrdinalIgnoreCase) ||
                               nat.SecurityStatus.Contains("FAILED", StringComparison.OrdinalIgnoreCase)))
            {
                allHealthy = false;
            }
        }

        var securityTargets = Kongor.Core.Services.Native.NativeSecurityUpgradeService.GetSecurityStatus(gameDirectory).Targets;
        foreach (var sec in securityTargets)
        {
            if (sec.Exists && (sec.CveRemediationStatus.Contains("VULNERABLE", StringComparison.OrdinalIgnoreCase) || !sec.IsParityValid))
            {
                allHealthy = false;
            }
        }

        return new EnginePatchStatusReport(gameDirectory, allHealthy, items, TimeProvider.System.GetUtcNow().ToString("o"), nativeTargets, securityTargets);
    }

    /// <summary>
    ///     Applies Phase 4 modern security and cryptography uplift (libcurl 8.8+ Schannel TLS 1.3, OpenSSL 1.1.1w / proxy forwarders).
    /// </summary>
    public static Kongor.Core.Services.Native.SecurityUpgradeResult ApplyPhase4SecurityUplift(
        string gameDirectory,
        bool redirectIat = false,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        return Kongor.Core.Services.Native.NativeSecurityUpgradeService.UpgradeSecurityBinaries(
            gameDirectory,
            skipProcessCheck: skipProcessCheck,
            redirectIat: redirectIat,
            logAction: logAction);
    }

    /// <summary>
    ///     Restores native binaries from backup files (.bak or .bak.old).
    /// </summary>
    public static RollbackResult RollbackEnginePatches(
        string gameDirectory,
        string tier = "previous",
        Action<string>? logAction = null)
    {
        bool isOld = string.Equals(tier, "old", StringComparison.OrdinalIgnoreCase);
        List<string> restored = [];

        foreach ((string rel, _) in TargetBinaries)
        {
            string full = Path.Combine(gameDirectory, rel);
            string backupTarget = isOld ? $"{full}.bak.old" : $"{full}.bak";

            if (File.Exists(backupTarget))
            {
                try
                {
                    File.Copy(backupTarget, full, true);
                    restored.Add(rel);
                    logAction?.Invoke($"[ENGINE_PATCH] Restored '{rel}' from '{Path.GetFileName(backupTarget)}'.");
                }
                catch (Exception ex)
                {
                    logAction?.Invoke($"[ENGINE_PATCH] Failed to restore '{rel}': {ex.Message}");
                }
            }
        }

        bool success = restored.Count > 0;
        string message = success
            ? $"Successfully rolled back {restored.Count} binaries to {tier} tier."
            : $"No backup files found for {tier} tier rollback.";

        return new RollbackResult(success, message, restored, tier);
    }

    /// <summary>
    ///     Searches and redirects user directory initialization in k2_x64.dll buffer from Documents to local userdata/.
    /// </summary>
    public static bool RedirectUserDirectory(byte[] buffer)
    {
        int offset = -1;
        if (buffer.Length >= UserDirPatternOffset + UserDirOriginalPattern.Length &&
            MatchesPattern(buffer, UserDirPatternOffset, UserDirOriginalPattern))
            offset = UserDirPatternOffset;
        else
            offset = FindPatternOffset(buffer, UserDirOriginalPattern);

        if (offset != -1)
        {
            Buffer.BlockCopy(UserDirPatchedPattern, 0, buffer, offset, UserDirPatchedPattern.Length);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Checks whether user directory redirection is already applied to the binary buffer.
    /// </summary>
    public static bool IsUserDirectoryPatched(byte[] buffer)
    {
        if (buffer.Length >= UserDirPatternOffset + UserDirPatchedPattern.Length &&
            MatchesPattern(buffer, UserDirPatternOffset, UserDirPatchedPattern))
            return true;

        return FindPatternOffset(buffer, UserDirPatchedPattern) != -1;
    }

    /// <summary>
    ///     Checks whether vid_d3d9_x64.dll has been patched to bypass ChangeDisplaySettingsW failure and D3D_SetMode failure.
    /// </summary>
    public static bool IsVidDriverPatched(byte[] buffer)
    {
        bool hasSetMode = buffer.Length >= VidDriverSetModeBypassOffset + VidDriverSetModePatched.Length &&
                          MatchesPattern(buffer, VidDriverSetModeBypassOffset, VidDriverSetModePatched);

        if (hasSetMode)
            return true;

        if (buffer.Length < VidDriverSetModeBypassOffset + VidDriverSetModePatched.Length)
        {
            return buffer.Length >= VidDriverOffset + VidDriverPatched.Length &&
                   MatchesPattern(buffer, VidDriverOffset, VidDriverPatched);
        }

        return false;
    }

    /// <summary>
    ///     Patches vid_d3d9_x64.dll to bypass ChangeDisplaySettingsW failure (74 6D -> EB 6D) and
    ///     D3D_SetMode failure in FUN_18005cad0 (ret 0xC3).
    ///     Strictly targets the verified offsets to prevent destructive global scans on common function prologues.
    /// </summary>
    public static bool PatchVidDriver(byte[] buffer)
    {
        bool modified = false;

        // 1. Patch FUN_18005cad0 entry to RET (0xC3) to bypass ChangeDisplaySettingsExW fatal D3D_SetMode crash
        var setModeCheck = AobScanner.VerifyFastPathOrScan(buffer, VidDriverSetModeBypassOffset, VidDriverSetModePatchedAob, VidDriverSetModePatched);
        if (!setModeCheck.Found)
        {
            var setModeScan = AobScanner.VerifyFastPathOrScan(buffer, VidDriverSetModeBypassOffset, VidDriverSetModeOriginalAob, VidDriverSetModeOriginal);
            if (setModeScan.Found && setModeScan.Offset + VidDriverSetModePatched.Length <= buffer.Length)
            {
                Buffer.BlockCopy(VidDriverSetModePatched, 0, buffer, setModeScan.Offset, VidDriverSetModePatched.Length);
                modified = true;
            }
        }

        // 2. Patch legacy ChangeDisplaySettingsW failure check (74 6D -> EB 6D)
        var vidCheck = AobScanner.VerifyFastPathOrScan(buffer, VidDriverOffset, VidDriverPatchedAob, VidDriverPatched);
        if (!vidCheck.Found)
        {
            var vidScan = AobScanner.VerifyFastPathOrScan(buffer, VidDriverOffset, VidDriverOriginalAob, VidDriverOriginal);
            if (vidScan.Found && vidScan.Offset + VidDriverPatched.Length <= buffer.Length)
            {
                Buffer.BlockCopy(VidDriverPatched, 0, buffer, vidScan.Offset, VidDriverPatched.Length);
                modified = true;
            }
        }

        return modified;
    }

    /// <summary>
    ///     Checks whether k2_x64.dll has been patched for 1000 FPS and frame unthrottling.
    /// </summary>
    public static bool IsK2Patched(byte[] buffer)
    {
        bool throttlePatched = buffer.Length > ThrottleJumpOffset + 1 &&
                               buffer[ThrottleJumpOffset] == ThrottleJumpPatched[0] &&
                               buffer[ThrottleJumpOffset + 1] == ThrottleJumpPatched[1];
        bool fpsPatched = (buffer.Length > 0xFB416C + 3 &&
                          buffer[0xFB416C] == Fps1000Pattern[0] &&
                          buffer[0xFB416C + 1] == Fps1000Pattern[1] &&
                          buffer[0xFB416C + 2] == Fps1000Pattern[2] &&
                          buffer[0xFB416C + 3] == Fps1000Pattern[3]) ||
                          (buffer.Length > 0x00F6913C + 3 &&
                          buffer[0x00F6913C] == Fps1000Pattern[0] &&
                          buffer[0x00F6913C + 1] == Fps1000Pattern[1] &&
                          buffer[0x00F6913C + 2] == Fps1000Pattern[2] &&
                          buffer[0x00F6913C + 3] == Fps1000Pattern[3]);
        return throttlePatched || fpsPatched;
    }

    /// <summary>
    ///     Searches and replaces occurrences of legacy dead domains with 0.0.0.0, null-padded to length.
    /// </summary>
    public static int SuppressLegacyDomains(byte[] buffer)
    {
        if (buffer.Length < 10) return 0;

        ReadOnlySpan<byte> span = buffer;
        bool hasAscii = span.IndexOf("heroesofnewerth"u8) >= 0 || span.IndexOf("s2games"u8) >= 0;
        bool hasWide = span.IndexOf(UnicodeHeroesOfNewerth) >= 0 || span.IndexOf(UnicodeS2Games) >= 0;
        bool hasOldPatchAscii = span.IndexOf("127.0.0.1"u8) >= 0;
        bool hasOldPatchWide = span.IndexOf(UnicodeLocalhost) >= 0;
        bool hasUtf32 = span.IndexOf(Utf32HeroesOfNewerth) >= 0 || span.IndexOf(Utf32S2Games) >= 0;
        bool hasOldPatchUtf32 = span.IndexOf(Utf32Localhost) >= 0;

        if (!hasAscii && !hasWide && !hasUtf32 && !hasOldPatchAscii && !hasOldPatchWide && !hasOldPatchUtf32) return 0;

        int totalReplacements = 0;

        foreach ((byte[] origAscii, byte[] oldPatchAscii, byte[] newPatchAscii, byte[] origWide, byte[] oldPatchWide,
                     byte[] newPatchWide, byte[] origUtf32, byte[] oldPatchUtf32, byte[] newPatchUtf32) in PrecompiledPatterns)
        {
            if (hasAscii) totalReplacements += ReplaceBytePattern(buffer, origAscii, newPatchAscii);
            if (hasOldPatchAscii) totalReplacements += ReplaceBytePattern(buffer, oldPatchAscii, newPatchAscii);
            if (hasWide) totalReplacements += ReplaceBytePattern(buffer, origWide, newPatchWide);
            if (hasOldPatchWide) totalReplacements += ReplaceBytePattern(buffer, oldPatchWide, newPatchWide);
            if (hasUtf32) totalReplacements += ReplaceBytePattern(buffer, origUtf32, newPatchUtf32);
            if (hasOldPatchUtf32) totalReplacements += ReplaceBytePattern(buffer, oldPatchUtf32, newPatchUtf32);
        }

        return totalReplacements;
    }

    private static byte[] CreatePaddedPattern(string text, int targetCharLength, Encoding encoding)
    {
        string padded = text.PadRight(targetCharLength, '\0');
        return encoding.GetBytes(padded);
    }

    private static int ReplaceBytePattern(byte[] buffer, byte[] pattern, byte[] replacement)
    {
        if (pattern.Length != replacement.Length || buffer.Length < pattern.Length) return 0;

        int count = 0;
        ReadOnlySpan<byte> patternSpan = pattern;
        Span<byte> bufferSpan = buffer;
        int index = 0;

        while (index <= buffer.Length - pattern.Length)
        {
            int found = bufferSpan.Slice(index).IndexOf(patternSpan);
            if (found < 0) break;

            int matchIndex = index + found;
            replacement.CopyTo(bufferSpan.Slice(matchIndex, replacement.Length));
            count++;
            index = matchIndex + pattern.Length;
        }

        return count;
    }

    private static bool MatchesPattern(byte[] buffer, int offset, byte[] pattern)
    {
        if (offset < 0 || offset + pattern.Length > buffer.Length) return false;

        return buffer.AsSpan(offset, pattern.Length).SequenceEqual(pattern);
    }

    private static int FindPatternOffset(byte[] buffer, byte[] pattern)
    {
        if (pattern == null || pattern.Length == 0 || buffer.Length < pattern.Length) return -1;

        return AobScanner.FindPattern(buffer, AobPattern.FromBytes(pattern));
    }

    private static int FindPatternOffset(byte[] buffer, AobPattern pattern)
    {
        if (pattern == null || buffer.Length < pattern.Length) return -1;

        return AobScanner.FindPattern(buffer, pattern);
    }
}
