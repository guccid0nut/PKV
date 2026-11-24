// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MachineCodeCrashPatchTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 3 Machine-Code Crash Mitigation & Rollback
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class MachineCodeCrashPatchTests
{
    private const int MockK2Size = 0x1300000;

    private static byte[] CreateMockK2BufferWithCrashHotspots()
    {
        byte[] buffer = new byte[MockK2Size];

        // 1. Float constant at 0xFB416C: 250.0f (0x437A0000)
        buffer[0xFB416C] = 0x00;
        buffer[0xFB416D] = 0x00;
        buffer[0xFB416E] = 0x7A;
        buffer[0xFB416F] = 0x43;

        // 2. Throttle jump at 0x8B72A8: 7C 96
        buffer[0x8B72A8] = 0x7C;
        buffer[0x8B72A9] = 0x96;

        // 3. User directory pattern at 0x9FE37A
        byte[] userDirPattern =
        [
            0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
            0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
            0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
            0x3C, 0x40, 0x00, 0x75, 0xF5
        ];
        Buffer.BlockCopy(userDirPattern, 0, buffer, 0x9FE37A, userDirPattern.Length);

        // 4. CRASH_SNAPSHOT_DELTA_NULL at 0x7C1E40
        BinaryPatchDefinition snapFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_SNAPSHOT_DELTA_NULL");
        Buffer.BlockCopy(snapFix.OriginalBytes, 0, buffer, snapFix.KnownOffset, snapFix.OriginalBytes.Length);

        // 5. CRASH_AUDIO_SAMPLE_STOP_NULL at 0x5A4C20
        BinaryPatchDefinition audioFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_AUDIO_SAMPLE_STOP_NULL");
        Buffer.BlockCopy(audioFix.OriginalBytes, 0, buffer, audioFix.KnownOffset, audioFix.OriginalBytes.Length);

        // 6. CRASH_UI_RESOURCE_HANDLE_CHANGED_GUARD at 0xC110BF
        BinaryPatchDefinition handleFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_RESOURCE_HANDLE_CHANGED_GUARD");
        Buffer.BlockCopy(handleFix.OriginalBytes, 0, buffer, handleFix.KnownOffset, handleFix.OriginalBytes.Length);

        // 7. CRASH_UI_FRAME_HOTRELOAD_PAYLOAD at 0xE098CC
        BinaryPatchDefinition payloadFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_PAYLOAD");
        Buffer.BlockCopy(payloadFix.OriginalBytes, 0, buffer, payloadFix.KnownOffset, payloadFix.OriginalBytes.Length);

        // 8. CRASH_UI_FRAME_HOTRELOAD_HOOK at 0x00A4A31E
        BinaryPatchDefinition hookFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_HOOK");
        Buffer.BlockCopy(hookFix.OriginalBytes, 0, buffer, hookFix.KnownOffset, hookFix.OriginalBytes.Length);

        // 9. CRASH_UI_RELOAD_INTERFACES_GUARD at 0x009956E0
        BinaryPatchDefinition guardFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_RELOAD_INTERFACES_GUARD");
        Buffer.BlockCopy(guardFix.OriginalBytes, 0, buffer, guardFix.KnownOffset, guardFix.OriginalBytes.Length);

        // 10. CRASH_UI_HOVER_WIDGET_NULL_GUARD at 0x009010A0
        BinaryPatchDefinition hoverFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_HOVER_WIDGET_NULL_GUARD");
        Buffer.BlockCopy(hoverFix.OriginalBytes, 0, buffer, hoverFix.KnownOffset, hoverFix.OriginalBytes.Length);

        // 11. CRASH_SERVER_BROWSER_WATERMARK_DISCRIMINATOR at 0x0095D689
        BinaryPatchDefinition watermarkFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_SERVER_BROWSER_WATERMARK_DISCRIMINATOR");
        Buffer.BlockCopy(watermarkFix.OriginalBytes, 0, buffer, watermarkFix.KnownOffset, watermarkFix.OriginalBytes.Length);

        // 12. CRASH_NET_WATERMARK_DEFAULT_FALSE at 0x000F9622
        BinaryPatchDefinition watermarkDefaultFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_NET_WATERMARK_DEFAULT_FALSE");
        Buffer.BlockCopy(watermarkDefaultFix.OriginalBytes, 0, buffer, watermarkDefaultFix.KnownOffset, watermarkDefaultFix.OriginalBytes.Length);

        // 13. FEATURE_CLIENT_MAP_SETTINGS_EXECUTION at 0x00B022E4
        BinaryPatchDefinition mapSettingsFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "FEATURE_CLIENT_MAP_SETTINGS_EXECUTION");
        Buffer.BlockCopy(mapSettingsFix.OriginalBytes, 0, buffer, mapSettingsFix.KnownOffset, mapSettingsFix.OriginalBytes.Length);

        return buffer;
    }

    [Fact]
    public void MachineCodeCrashPatch_RegisteredCrashFixes_AreValidAndConfigured()
    {
        Assert.NotEmpty(EnginePatchService.RegisteredCrashFixes);
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_SNAPSHOT_DELTA_NULL");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_AUDIO_SAMPLE_STOP_NULL");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_AUTO_CONCEDE_PRACTICE_BOTS");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_AUTO_CONCEDE_LOADING_GUARD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_BOT_HERO_SELECT_BYPASS");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_MATCHMAKING_DISCONNECT_GUARD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_MATCHMAKING_DISCONNECT_GUARD_SERVER");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_UNASSIGNED_TEAM_DISCONNECT_GUARD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_UI_RESOURCE_HANDLE_CHANGED_GUARD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_PAYLOAD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_HOOK");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_UI_RELOAD_INTERFACES_GUARD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_UI_HOVER_WIDGET_NULL_GUARD");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_SERVER_BROWSER_WATERMARK_DISCRIMINATOR");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "CRASH_NET_WATERMARK_DEFAULT_FALSE");
        Assert.Contains(EnginePatchService.RegisteredCrashFixes, f => f.Id == "FEATURE_CLIENT_MAP_SETTINGS_EXECUTION");

        foreach (BinaryPatchDefinition fix in EnginePatchService.RegisteredCrashFixes)
        {
            Assert.False(string.IsNullOrWhiteSpace(fix.Id));
            Assert.False(string.IsNullOrWhiteSpace(fix.Name));
            Assert.False(string.IsNullOrWhiteSpace(fix.TargetBinaryName));
            Assert.True(fix.KnownOffset > 0 || fix.KnownOffset == -1, $"Fix '{fix.Id}' must have positive offset or -1 for pattern scanning");
            Assert.NotEmpty(fix.OriginalBytes);
            Assert.NotEmpty(fix.PatchedBytes);
            Assert.Equal(fix.OriginalBytes.Length, fix.PatchedBytes.Length);
        }
    }

    [Fact]
    public void ApplyCrashFix_SnapshotDeltaNull_AppliesPatchAtKnownOffset()
    {
        byte[] buffer = new byte[0x800000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_SNAPSHOT_DELTA_NULL");

        // Set original bytes at known offset
        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, fix.KnownOffset, fix.OriginalBytes.Length);

        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, fix));

        // Act
        bool applied = EnginePatchService.ApplyCrashFix(buffer, fix);

        // Assert
        Assert.True(applied);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, fix));

        byte[] actualBytes = new byte[fix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, fix.KnownOffset, actualBytes, 0, actualBytes.Length);
        Assert.Equal(fix.PatchedBytes, actualBytes);
    }

    [Fact]
    public void ApplyCrashFix_AudioSampleStopNull_AppliesPatchAtKnownOffset()
    {
        byte[] buffer = new byte[0x600000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_AUDIO_SAMPLE_STOP_NULL");

        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, fix.KnownOffset, fix.OriginalBytes.Length);
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, fix));

        // Act
        bool applied = EnginePatchService.ApplyCrashFix(buffer, fix);

        // Assert
        Assert.True(applied);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, fix));

        byte[] actualBytes = new byte[fix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, fix.KnownOffset, actualBytes, 0, actualBytes.Length);
        Assert.Equal(fix.PatchedBytes, actualBytes);
    }

    [Fact]
    public void ApplyCrashFix_MatchmakingDisconnectGuard_AppliesPatchAtKnownOffset()
    {
        byte[] buffer = new byte[0x100000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_MATCHMAKING_DISCONNECT_GUARD");

        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, fix.KnownOffset, fix.OriginalBytes.Length);
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, fix));

        // Act
        bool applied = EnginePatchService.ApplyCrashFix(buffer, fix);

        // Assert
        Assert.True(applied);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, fix));

        byte[] actualBytes = new byte[fix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, fix.KnownOffset, actualBytes, 0, actualBytes.Length);
        Assert.Equal(fix.PatchedBytes, actualBytes);
    }

    [Fact]
    public void ApplyCrashFix_MatchmakingDisconnectGuardServer_AppliesPatchAtKnownOffset()
    {
        byte[] buffer = new byte[0x100000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_MATCHMAKING_DISCONNECT_GUARD_SERVER");

        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, fix.KnownOffset, fix.OriginalBytes.Length);
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, fix));

        // Act
        bool applied = EnginePatchService.ApplyCrashFix(buffer, fix);

        // Assert
        Assert.True(applied);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, fix));

        byte[] actualBytes = new byte[fix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, fix.KnownOffset, actualBytes, 0, actualBytes.Length);
        Assert.Equal(fix.PatchedBytes, actualBytes);
    }

    [Fact]
    public void ApplyCrashFix_ResourceHandleChangedGuard_AppliesPatchAtKnownOffset()
    {
        byte[] buffer = new byte[0xD00000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_RESOURCE_HANDLE_CHANGED_GUARD");

        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, fix.KnownOffset, fix.OriginalBytes.Length);
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, fix));

        // Act
        bool applied = EnginePatchService.ApplyCrashFix(buffer, fix);

        // Assert
        Assert.True(applied);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, fix));

        byte[] actualBytes = new byte[fix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, fix.KnownOffset, actualBytes, 0, actualBytes.Length);
        Assert.Equal(fix.PatchedBytes, actualBytes);
    }

    [Fact]
    public void MachineCodeCrashPatch_RevertCrashFix_RestoresOriginalBytesAccurately()
    {
        byte[] buffer = new byte[0x800000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_SNAPSHOT_DELTA_NULL");

        // Seed with patched bytes
        Buffer.BlockCopy(fix.PatchedBytes, 0, buffer, fix.KnownOffset, fix.PatchedBytes.Length);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, fix));

        // Act
        bool reverted = EnginePatchService.RevertCrashFix(buffer, fix);

        // Assert
        Assert.True(reverted);
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, fix));

        byte[] actualBytes = new byte[fix.OriginalBytes.Length];
        Buffer.BlockCopy(buffer, fix.KnownOffset, actualBytes, 0, actualBytes.Length);
        Assert.Equal(fix.OriginalBytes, actualBytes);
    }

    [Fact]
    public void ApplyCrashFix_Idempotency_ReturnsFalseWhenAlreadyApplied()
    {
        byte[] buffer = new byte[0x800000];
        BinaryPatchDefinition fix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_SNAPSHOT_DELTA_NULL");

        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, fix.KnownOffset, fix.OriginalBytes.Length);

        bool first = EnginePatchService.ApplyCrashFix(buffer, fix);
        bool second = EnginePatchService.ApplyCrashFix(buffer, fix);

        Assert.True(first);
        Assert.False(second, "Second application should be a no-op and return false.");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void MachineCodeCrashPatch_EnsureEnginePatched_AppliesCrashMitigationsAndCreatesTwoTierBackups()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_CrashGuards_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] mockK2Bytes = CreateMockK2BufferWithCrashHotspots();
            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, mockK2Bytes);

            // Act - First run
            bool success1 = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(success1);
            Assert.True(File.Exists(k2Path + ".bak"), ".bak backup must be created");
            Assert.False(File.Exists(k2Path + ".bak.old"), ".bak.old should not exist yet");

            byte[] patchedBytes = File.ReadAllBytes(k2Path);
            BinaryPatchDefinition snapFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_SNAPSHOT_DELTA_NULL");
            BinaryPatchDefinition audioFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_AUDIO_SAMPLE_STOP_NULL");

            Assert.True(EnginePatchService.IsCrashFixApplied(patchedBytes, snapFix));
            Assert.True(EnginePatchService.IsCrashFixApplied(patchedBytes, audioFix));

            // Act - Second run (simulate unpatched modification that needs patching to trigger rotation)
            // Revert float constant back to 250.0f so EnsureEnginePatched needs to patch it
            patchedBytes[0xFB416C] = 0x00;
            patchedBytes[0xFB416D] = 0x00;
            patchedBytes[0xFB416E] = 0x7A;
            patchedBytes[0xFB416F] = 0x43;
            File.WriteAllBytes(k2Path, patchedBytes);

            // Re-apply EnsureEnginePatched
            bool success2 = EnginePatchService.EnsureEnginePatched(tempDir);
            Assert.True(success2);

            // Verify .bak and .bak.old exist
            Assert.True(File.Exists(k2Path + ".bak"));
            // .bak.old was rotated from previous .bak
            Assert.True(File.Exists(k2Path + ".bak.old"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MachineCodeCrashPatch_RollbackEnginePatches_RestoresFromTierPreviousAndOld()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_Rollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            string bakPath = k2Path + ".bak";
            string bakOldPath = k2Path + ".bak.old";

            byte[] liveBytes = new byte[100];
            Array.Fill(liveBytes, (byte)0x11);

            byte[] bakBytes = new byte[100];
            Array.Fill(bakBytes, (byte)0x22);

            byte[] bakOldBytes = new byte[100];
            Array.Fill(bakOldBytes, (byte)0x33);

            File.WriteAllBytes(k2Path, liveBytes);
            File.WriteAllBytes(bakPath, bakBytes);
            File.WriteAllBytes(bakOldPath, bakOldBytes);

            // 1. Rollback to 'previous' (.bak)
            RollbackResult prevRes = EnginePatchService.RollbackEnginePatches(tempDir, "previous");
            Assert.True(prevRes.Success);
            Assert.Equal("previous", prevRes.TargetTier);
            Assert.Contains("k2_x64.dll", prevRes.RestoredPaths);

            byte[] afterPrev = File.ReadAllBytes(k2Path);
            Assert.Equal(bakBytes, afterPrev);

            // 2. Rollback to 'old' (.bak.old)
            RollbackResult oldRes = EnginePatchService.RollbackEnginePatches(tempDir, "old");
            Assert.True(oldRes.Success);
            Assert.Equal("old", oldRes.TargetTier);
            Assert.Contains("k2_x64.dll", oldRes.RestoredPaths);

            byte[] afterOld = File.ReadAllBytes(k2Path);
            Assert.Equal(bakOldBytes, afterOld);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MachineCodeCrashPatch_GetEnginePatchStatus_AggregatesDetailedStatusAcrossBinaries()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_Status_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] mockK2Bytes = CreateMockK2BufferWithCrashHotspots();
            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, mockK2Bytes);

            // Initial status: not patched
            EnginePatchStatusReport initialReport = EnginePatchService.GetEnginePatchStatus(tempDir);
            Assert.Contains(initialReport.Binaries, b => b.RelativePath == "k2_x64.dll" && b.Exists);

            EnginePatchStatusItem k2Item = initialReport.Binaries.First(b => b.RelativePath == "k2_x64.dll");
            Assert.Equal("k2_x64.dll", k2Item.RelativePath);
            Assert.True(k2Item.Exists);
            Assert.False(k2Item.IsFpsUnclamped);
            Assert.False(k2Item.IsUserDirRedirected);
            Assert.Empty(k2Item.AppliedCrashFixes);
            Assert.NotEmpty(k2Item.PendingCrashFixes);
            Assert.False(k2Item.HasBak);
            Assert.False(k2Item.HasBakOld);

            // Apply patch
            EnginePatchService.EnsureEnginePatched(tempDir);

            // Status after patch: all applied
            EnginePatchStatusReport patchedReport = EnginePatchService.GetEnginePatchStatus(tempDir);
            EnginePatchStatusItem patchedItem = patchedReport.Binaries.First(b => b.RelativePath == "k2_x64.dll");

            Assert.True(patchedItem.IsFpsUnclamped);
            Assert.True(patchedItem.IsUserDirRedirected);
            Assert.Equal(10, patchedItem.AppliedCrashFixes.Count);
            Assert.Contains("CRASH_SNAPSHOT_DELTA_NULL", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_AUDIO_SAMPLE_STOP_NULL", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_UI_RESOURCE_HANDLE_CHANGED_GUARD", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_UI_FRAME_HOTRELOAD_PAYLOAD", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_UI_FRAME_HOTRELOAD_HOOK", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_UI_RELOAD_INTERFACES_GUARD", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_UI_HOVER_WIDGET_NULL_GUARD", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_SERVER_BROWSER_WATERMARK_DISCRIMINATOR", patchedItem.AppliedCrashFixes);
            Assert.Contains("CRASH_NET_WATERMARK_DEFAULT_FALSE", patchedItem.AppliedCrashFixes);
            Assert.Contains("FEATURE_CLIENT_MAP_SETTINGS_EXECUTION", patchedItem.AppliedCrashFixes);
            Assert.Empty(patchedItem.PendingCrashFixes);
            Assert.True(patchedItem.HasBak);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyCrashFix_HotreloadPatches_AppliesAllThreeAtKnownOffsets()
    {
        byte[] buffer = new byte[MockK2Size];
        BinaryPatchDefinition payloadFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_PAYLOAD");
        BinaryPatchDefinition hookFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_HOOK");
        BinaryPatchDefinition guardFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_RELOAD_INTERFACES_GUARD");

        Buffer.BlockCopy(payloadFix.OriginalBytes, 0, buffer, payloadFix.KnownOffset, payloadFix.OriginalBytes.Length);
        Buffer.BlockCopy(hookFix.OriginalBytes, 0, buffer, hookFix.KnownOffset, hookFix.OriginalBytes.Length);
        Buffer.BlockCopy(guardFix.OriginalBytes, 0, buffer, guardFix.KnownOffset, guardFix.OriginalBytes.Length);

        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, payloadFix));
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, hookFix));
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, guardFix));

        Assert.True(EnginePatchService.ApplyCrashFix(buffer, payloadFix));
        Assert.True(EnginePatchService.ApplyCrashFix(buffer, hookFix));
        Assert.True(EnginePatchService.ApplyCrashFix(buffer, guardFix));

        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, payloadFix));
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, hookFix));
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, guardFix));
    }

    [Fact]
    public void ApplyCrashFix_HotreloadPatches_UpgradesFromPreviousReloadInterfacePatch()
    {
        byte[] buffer = new byte[MockK2Size];
        BinaryPatchDefinition payloadFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_PAYLOAD");
        BinaryPatchDefinition hookFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_HOOK");

        Assert.NotNull(payloadFix.SearchPattern);
        Assert.NotNull(hookFix.SearchPattern);

        // Prepopulate with previous ReloadInterface main;ShowInterface none;ShowInterface main patch
        Buffer.BlockCopy(payloadFix.SearchPattern, 0, buffer, payloadFix.KnownOffset, payloadFix.SearchPattern.Length);
        Buffer.BlockCopy(hookFix.SearchPattern, 0, buffer, hookFix.KnownOffset, hookFix.SearchPattern.Length);

        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, payloadFix));
        Assert.False(EnginePatchService.IsCrashFixApplied(buffer, hookFix));

        // Act - Apply upgrade
        Assert.True(EnginePatchService.ApplyCrashFix(buffer, payloadFix));
        Assert.True(EnginePatchService.ApplyCrashFix(buffer, hookFix));

        // Assert - Successfully upgraded to UnloadInterface main;LoadInterface /ui/fe2/main_v2.interface;ShowInterface main
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, payloadFix));
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, hookFix));

        byte[] actualPayload = new byte[payloadFix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, payloadFix.KnownOffset, actualPayload, 0, actualPayload.Length);
        Assert.Equal(payloadFix.PatchedBytes, actualPayload);

        byte[] actualHook = new byte[hookFix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, hookFix.KnownOffset, actualHook, 0, actualHook.Length);
        Assert.Equal(hookFix.PatchedBytes, actualHook);
    }

    [Fact]
    public void ApplyCrashFix_WithSearchPattern_FallsBackToOriginalBytesWhenUnpatched()
    {
        byte[] buffer = new byte[MockK2Size];
        BinaryPatchDefinition hookFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_UI_FRAME_HOTRELOAD_HOOK");

        Assert.NotNull(hookFix.SearchPattern);

        // Place original bytes at a shifted offset (not KnownOffset) where SearchPattern is absent
        int shiftedOffset = 0x200000;
        Buffer.BlockCopy(hookFix.OriginalBytes, 0, buffer, shiftedOffset, hookFix.OriginalBytes.Length);

        // Act
        bool applied = EnginePatchService.ApplyCrashFix(buffer, hookFix);

        // Assert
        Assert.True(applied);
        Assert.True(EnginePatchService.IsCrashFixApplied(buffer, hookFix));

        byte[] actual = new byte[hookFix.PatchedBytes.Length];
        Buffer.BlockCopy(buffer, shiftedOffset, actual, 0, actual.Length);
        Assert.Equal(hookFix.PatchedBytes, actual);
    }
}
