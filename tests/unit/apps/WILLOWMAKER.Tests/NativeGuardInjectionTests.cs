// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeGuardInjectionTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 5 Runtime Supervision - Native Process Injection & Trampoline Tests
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;
using NativeProcessInjectionService = WILLOWMAKER.Core.Services.NativeProcessInjectionService;

namespace WILLOWMAKER.Tests;

public sealed class NativeGuardInjectionTests
{
    [Fact]
    public void NativeGuardInjection_StagedNativeGuard_ExistsAndValidatesPeStructure()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string candidateDir = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64");
        if (!Directory.Exists(candidateDir) || !File.Exists(Path.Combine(candidateDir, NativeGuardBinaryBuilder.DllFileName)))
        {
            return;
        }

        string guardPath = NativeGuardBinaryBuilder.EnsureStaged(candidateDir, forceOverwrite: true);

        Assert.True(File.Exists(guardPath), $"Kongor.NativeGuard.dll must be staged at: {guardPath}");

        byte[] peBytes = File.ReadAllBytes(guardPath);
        Assert.True(peBytes.Length >= 0x400, "PE binary size must be at least headers size.");

        // 1. Validate DOS signature
        Assert.Equal((byte)'M', peBytes[0]);
        Assert.Equal((byte)'Z', peBytes[1]);

        int eLfanew = BinaryPrimitives.ReadInt32LittleEndian(peBytes.AsSpan(0x3C, 4));
        Assert.True(eLfanew > 0 && eLfanew + 24 < peBytes.Length);

        // 2. Validate NT PE signature
        Assert.Equal((byte)'P', peBytes[eLfanew]);
        Assert.Equal((byte)'E', peBytes[eLfanew + 1]);

        // 3. Validate Machine AMD64 (0x8664) and 64-bit Magic (0x020B)
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.AsSpan(eLfanew + 4, 2));
        Assert.Equal(0x8664, machine);

        ushort optMagic = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.AsSpan(eLfanew + 24, 2));
        Assert.Equal(0x020B, optMagic);

        // 4. Validate PE Exports
        var exports = NativeBinaryManager.ExtractPeExportNames(guardPath);
        Assert.NotEmpty(exports);
        Assert.Equal(NativeGuardBinaryBuilder.RequiredExports.Length, exports.Count);

        foreach (string required in NativeGuardBinaryBuilder.RequiredExports)
        {
            Assert.Contains(required, exports);
        }

        // 5. Validate winmm.dll Imports for Dedicated Server 1.0ms timer resolution
        var importReport = Kongor.Core.Services.Native.PeImportAddressTableService.GetImports(guardPath);
        Assert.True(importReport.IsValidPe, "Generated NativeGuard DLL must be a valid PE.");
        Assert.Contains(importReport.ImportedModules, m => m.DllName.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase));
        var winmmModule = importReport.ImportedModules.First(m => m.DllName.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("timeBeginPeriod", winmmModule.ImportedFunctions);
        Assert.Contains("timeEndPeriod", winmmModule.ImportedFunctions);
    }

    [Fact]
    public void NativeGuardInjection_CalculateRipDisplacement_ValidatesAddressRange()
    {
        nint sourceAddress = unchecked((nint)0x00007FF710000000L);

        // 1. Target within +256 MB (well within 2 GB)
        nint targetNearAbove = unchecked((nint)((long)sourceAddress + 0x10000000L));
        bool fitsAbove = NativeProcessInjectionService.CalculateRipDisplacement(
            sourceAddress, targetNearAbove, out long dispAbove, out bool fits32Above);
        Assert.True(fitsAbove);
        Assert.True(fits32Above);
        Assert.Equal(0x10000000L - 5, dispAbove);

        // 2. Target within -256 MB (well within -2 GB)
        nint targetNearBelow = unchecked((nint)((long)sourceAddress - 0x10000000L));
        bool fitsBelow = NativeProcessInjectionService.CalculateRipDisplacement(
            sourceAddress, targetNearBelow, out long dispBelow, out bool fits32Below);
        Assert.True(fitsBelow);
        Assert.True(fits32Below);
        Assert.Equal(-0x10000000L - 5, dispBelow);

        // 3. Target exactly at boundary +2 GB - 100 bytes
        nint targetBoundary = unchecked((nint)((long)sourceAddress + 0x7FFF0000L));
        bool fitsBoundary = NativeProcessInjectionService.CalculateRipDisplacement(
            sourceAddress, targetBoundary, out long dispBoundary, out bool fits32Boundary);
        Assert.True(fitsBoundary);
        Assert.True(fits32Boundary);
        Assert.True(dispBoundary <= int.MaxValue);

        // 4. Target outside 32-bit range (+4 GB)
        nint targetFarAbove = unchecked((nint)((long)sourceAddress + 0x100000000L));
        bool fitsFarAbove = NativeProcessInjectionService.CalculateRipDisplacement(
            sourceAddress, targetFarAbove, out long dispFarAbove, out bool fits32FarAbove);
        Assert.False(fitsFarAbove);
        Assert.False(fits32FarAbove);
        Assert.True(dispFarAbove > int.MaxValue);

        // 5. Target outside 32-bit range (-4 GB)
        nint targetFarBelow = unchecked((nint)((long)sourceAddress - 0x100000000L));
        bool fitsFarBelow = NativeProcessInjectionService.CalculateRipDisplacement(
            sourceAddress, targetFarBelow, out long dispFarBelow, out bool fits32FarBelow);
        Assert.False(fitsFarBelow);
        Assert.False(fits32FarBelow);
        Assert.True(dispFarBelow < int.MinValue);
    }

    [Fact]
    public void InjectDll_InvalidArguments_ReturnsFailureWithoutThrowing()
    {
        // 1. Invalid process handle
        var resultNullHandle = NativeProcessInjectionService.InjectDll(nint.Zero, "some_fake.dll");
        Assert.False(resultNullHandle.Success);
        Assert.NotNull(resultNullHandle.ErrorMessage);

        // 2. Missing DLL file path
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string missingDll = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "non_existent_guard.dll");
        var resultMissing = NativeProcessInjectionService.InjectDll((nint)0x1234, missingDll);
        Assert.False(resultMissing.Success);
        Assert.Contains("does not exist", resultMissing.ErrorMessage);
    }

    [Fact]
    public void CreateSuspendedProcess_NonExistentExecutable_ThrowsFileNotFoundException()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<FileNotFoundException>(() =>
        {
            NativeProcessInjectionService.CreateSuspendedProcess(
                @"C:\Windows\System32\definitely_not_a_real_game_binary_12345.exe",
                "-dedicated",
                @"C:\Windows\System32");
        });
    }

    [Fact]
    public void CreateSuspendedProcessWithStreams_NonExistentExecutable_ThrowsFileNotFoundException()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<FileNotFoundException>(() =>
        {
            NativeProcessInjectionService.CreateSuspendedProcessWithStreams(
                @"C:\Windows\System32\definitely_not_a_real_game_binary_12345.exe",
                "-dedicated",
                @"C:\Windows\System32");
        });
    }

    [Fact]
    public async Task CreateSuspendedProcessWithStreams_SpawnsSuspendedProcessAndCapturesOutput()
    {
        if (!OperatingSystem.IsWindows()) return;

        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmdPath)) return;

        using var result = NativeProcessInjectionService.CreateSuspendedProcessWithStreams(
            cmdPath,
            "/c echo ProjectKongor_SuspendedLaunch_Test",
            Environment.SystemDirectory,
            createNoWindow: true);

        Assert.NotNull(result);
        Assert.True(result.ProcessInfo.ProcessId > 0);
        Assert.NotEqual(nint.Zero, result.ProcessInfo.ProcessHandle);
        Assert.NotEqual(nint.Zero, result.ProcessInfo.ThreadHandle);
        Assert.NotNull(result.StandardOutput);

        // Resume execution
        bool resumed = NativeProcessInjectionService.ResumeProcess(result.ProcessInfo.ThreadHandle);
        Assert.True(resumed);

        string? line = await result.StandardOutput.ReadLineAsync();
        Assert.NotNull(line);
        Assert.Contains("ProjectKongor_SuspendedLaunch_Test", line);
    }

    [Fact]
    public async Task CreateSuspendedProcessWithStreams_PropagatesChildEnvironmentVariablesViaUnicodeBlock()
    {
        if (!OperatingSystem.IsWindows()) return;

        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmdPath)) return;

        ProcessStartInfo startInfo = new();
        startInfo.EnvironmentVariables["HON_MODERN_HUD"] = "1";
        startInfo.EnvironmentVariables["HON_FSR_ENABLE"] = "1";
        startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"] = @"C:\Kongor\userdataKONGOR\dxvk.conf";

        using SuspendedProcessWithStreamsResult result = NativeProcessInjectionService.CreateSuspendedProcessWithStreams(
            cmdPath,
            "/c echo HUD=%HON_MODERN_HUD%;FSR=%HON_FSR_ENABLE%;DXVK=%DXVK_CONFIG_FILE%",
            Environment.SystemDirectory,
            createNoWindow: true,
            environmentVariables: startInfo.EnvironmentVariables);

        Assert.NotNull(result);
        Assert.NotNull(result.StandardOutput);

        bool resumed = NativeProcessInjectionService.ResumeProcess(result.ProcessInfo.ThreadHandle);
        Assert.True(resumed);

        string? line = await result.StandardOutput.ReadLineAsync();
        Assert.NotNull(line);
        Assert.Contains(@"HUD=1;FSR=1;DXVK=C:\Kongor\userdataKONGOR\dxvk.conf", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AllocateTrampolineWithin2Gb_InvalidHandle_ReturnsZeroWithoutThrowing()
    {
        nint result = NativeProcessInjectionService.AllocateTrampolineWithin2Gb(nint.Zero, unchecked((nint)0x180000000L));
        Assert.Equal(nint.Zero, result);
    }

    [Fact]
    public void ResumeProcess_InvalidHandle_ReturnsFalseWithoutThrowing()
    {
        bool result = NativeProcessInjectionService.ResumeProcess(nint.Zero);
        Assert.False(result);
    }

    [Fact]
    public void SynthesizeRel32Detour_ValidAddresses_GeneratesCorrectJmpRel32AndTrampoline()
    {
        nint sourceAddress = unchecked((nint)0x00007FF710000000L);
        nint trampolineAddress = unchecked((nint)0x00007FF725000000L); // +0x15000000 (~335 MB away)

        // 8 bytes of original instructions: mov [rsp+8], rbx (5 bytes) + nop + nop + nop (3 bytes)
        byte[] originalBytes = [0x48, 0x89, 0x5C, 0x24, 0x08, 0x90, 0x90, 0x90];

        var result = NativeProcessInjectionService.SynthesizeRel32Detour(sourceAddress, trampolineAddress, originalBytes);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);

        // 1. Detour verification
        Assert.Equal(originalBytes.Length, result.DetourBytes.Length);
        Assert.Equal(0xE9, result.DetourBytes[0]); // JMP rel32 opcode

        int dispForward = BinaryPrimitives.ReadInt32LittleEndian(result.DetourBytes.AsSpan(1, 4));
        long expectedForwardDisp = (long)trampolineAddress - ((long)sourceAddress + 5);
        Assert.Equal(expectedForwardDisp, dispForward);
        Assert.Equal(expectedForwardDisp, result.DisplacementToTrampoline);

        // Padding bytes (bytes 5, 6, 7) must be 0x90 (NOP)
        for (int i = 5; i < originalBytes.Length; i++)
        {
            Assert.Equal(0x90, result.DetourBytes[i]);
        }

        // 2. Trampoline verification
        // Expected length: originalBytes (8) + JMP rel32 (5) = 13 bytes
        Assert.Equal(originalBytes.Length + 5, result.TrampolineBytes.Length);

        // Original bytes copied verbatim
        for (int i = 0; i < originalBytes.Length; i++)
        {
            Assert.Equal(originalBytes[i], result.TrampolineBytes[i]);
        }

        // Trampoline return jump
        Assert.Equal(0xE9, result.TrampolineBytes[originalBytes.Length]);
        int dispReturn = BinaryPrimitives.ReadInt32LittleEndian(result.TrampolineBytes.AsSpan(originalBytes.Length + 1, 4));
        long expectedReturnTarget = (long)sourceAddress + originalBytes.Length;
        long expectedReturnFrom = (long)trampolineAddress + originalBytes.Length + 5;
        long expectedReturnDisp = expectedReturnTarget - expectedReturnFrom;
        Assert.Equal(expectedReturnDisp, dispReturn);
        Assert.Equal(expectedReturnDisp, result.DisplacementToReturn);
    }

    [Fact]
    public void SynthesizeRel32Detour_DisplacementExceeds2Gb_ReturnsFailure()
    {
        nint sourceAddress = unchecked((nint)0x00007FF710000000L);
        nint trampolineAddress = unchecked((nint)0x00007FF820000000L); // +0x110000000 (~4.2 GB away)

        byte[] originalBytes = [0x48, 0x89, 0x5C, 0x24, 0x08];

        var result = NativeProcessInjectionService.SynthesizeRel32Detour(sourceAddress, trampolineAddress, originalBytes);

        Assert.False(result.Success);
        Assert.Contains("±2 GB", result.ErrorMessage);
        Assert.Empty(result.DetourBytes);
        Assert.Empty(result.TrampolineBytes);
    }

    [Fact]
    public void SynthesizeRel32Detour_OriginalBytesTooShort_ReturnsFailure()
    {
        nint sourceAddress = unchecked((nint)0x00007FF710000000L);
        nint trampolineAddress = unchecked((nint)0x00007FF711000000L);

        byte[] tooShort = [0x90, 0x90, 0x90, 0x90]; // 4 bytes < 5 bytes

        var result = NativeProcessInjectionService.SynthesizeRel32Detour(sourceAddress, trampolineAddress, tooShort);

        Assert.False(result.Success);
        Assert.Contains("at least 5 bytes", result.ErrorMessage);
        Assert.Empty(result.DetourBytes);
        Assert.Empty(result.TrampolineBytes);
    }

    [Fact]
    public void GetRemoteModuleHandle_OnCurrentProcess_ResolvesCoreModulesWith64BitAddresses()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Process curProcess = Process.GetCurrentProcess();
        nint curProcessHandle = curProcess.Handle;
        Assert.NotEqual(nint.Zero, curProcessHandle);

        // 1. Resolve ntdll.dll
        nint ntdllBase = NativeProcessInjectionService.GetRemoteModuleHandle(curProcessHandle, "ntdll.dll");
        Assert.NotEqual(nint.Zero, ntdllBase);

        // 2. Resolve kernel32.dll
        nint kernel32Base = NativeProcessInjectionService.GetRemoteModuleHandle(curProcessHandle, "kernel32.dll");
        Assert.NotEqual(nint.Zero, kernel32Base);

        // 3. In 64-bit processes on modern Windows, DLLs are loaded in high 64-bit address space (> 4 GB)
        if (Environment.Is64BitProcess)
        {
            Assert.True((ulong)ntdllBase > 0x10000UL, "ntdll module base must be valid non-zero pointer.");
            Assert.True((ulong)kernel32Base > 0x10000UL, "kernel32 module base must be valid non-zero pointer.");
        }

        // 4. Non-existent module returns Zero
        nint nonExistent = NativeProcessInjectionService.GetRemoteModuleHandle(curProcessHandle, "non_existent_fake_module_12345.dll");
        Assert.Equal(nint.Zero, nonExistent);
    }

    [Fact]
    public void GetRemoteModuleHandle_InvalidParameters_ReturnsZeroWithoutThrowing()
    {
        // 1. Invalid process handle
        nint resultNullHandle = NativeProcessInjectionService.GetRemoteModuleHandle(nint.Zero, "ntdll.dll");
        Assert.Equal(nint.Zero, resultNullHandle);

        // 2. Empty or null module name
        nint resultEmptyName = NativeProcessInjectionService.GetRemoteModuleHandle((nint)0x1234, "");
        Assert.Equal(nint.Zero, resultEmptyName);

        nint resultNullName = NativeProcessInjectionService.GetRemoteModuleHandle((nint)0x1234, null!);
        Assert.Equal(nint.Zero, resultNullName);
    }

    [Fact]
    public void NativeGuardInjection_64BitAslrAddressSpace_CalculatesRipDisplacementAccurately()
    {
        // Base address high in 64-bit space (e.g. 0x00007FF7_A0000000, ~140 TB)
        nint highOrigin = unchecked((nint)0x00007FF7A0000000L);

        // Target +512 MB above
        nint targetAbove = unchecked((nint)((long)highOrigin + 0x20000000L));
        bool fitsAbove = NativeProcessInjectionService.CalculateRipDisplacement(
            highOrigin, targetAbove, out long dispAbove, out bool fits32Above);
        Assert.True(fitsAbove);
        Assert.True(fits32Above);
        Assert.Equal(0x20000000L - 5, dispAbove);

        // Target -512 MB below
        nint targetBelow = unchecked((nint)((long)highOrigin - 0x20000000L));
        bool fitsBelow = NativeProcessInjectionService.CalculateRipDisplacement(
            highOrigin, targetBelow, out long dispBelow, out bool fits32Below);
        Assert.True(fitsBelow);
        Assert.True(fits32Below);
        Assert.Equal(-0x20000000L - 5, dispBelow);

        // Target outside 2 GB range (+3 GB)
        nint targetTooFar = unchecked((nint)((long)highOrigin + 0xC0000000L));
        bool fitsTooFar = NativeProcessInjectionService.CalculateRipDisplacement(
            highOrigin, targetTooFar, out long dispTooFar, out bool fits32TooFar);
        Assert.False(fitsTooFar);
        Assert.False(fits32TooFar);
        Assert.True(dispTooFar > int.MaxValue);
    }
}

