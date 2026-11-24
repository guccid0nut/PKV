// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeProcessInjectionService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 5 Runtime Supervision - Native Process Injection & Trampolines
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Kongor.Core.Services.Native;
using Microsoft.Extensions.Logging;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for low-level Win32 runtime process supervision,
///     CREATE_SUSPENDED process instantiation, ±2 GB RIP-relative displacement trampoline allocation,
///     and remote DLL injection.
/// </summary>
public static class NativeProcessInjectionService
{
    public static bool CalculateRipDisplacement(nint sourceAddress, nint targetAddress, out long displacement, out bool fitsIn32Bit)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.CalculateRipDisplacement(sourceAddress, targetAddress, out displacement, out fitsIn32Bit);

    public static nint AllocateTrampolineWithin2Gb(nint hProcess, nint targetModuleBase, int sizeBytes = 4096, long maxDisplacementBytes = 0x7FFF0000L)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.AllocateTrampolineWithin2Gb(hProcess, targetModuleBase, sizeBytes, maxDisplacementBytes);

    public static SuspendedProcessInfo CreateSuspendedProcess(
        string executablePath,
        string arguments,
        string workingDirectory,
        bool createNoWindow = true,
        ILogger? logger = null,
        StringDictionary? environmentVariables = null)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.CreateSuspendedProcess(
            executablePath, arguments, workingDirectory, createNoWindow, logger, environmentVariables);

    public static SuspendedProcessWithStreamsResult CreateSuspendedProcessWithStreams(
        string executablePath,
        string arguments,
        string workingDirectory,
        bool createNoWindow = false,
        ILogger? logger = null,
        StringDictionary? environmentVariables = null)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.CreateSuspendedProcessWithStreams(
            executablePath, arguments, workingDirectory, createNoWindow, logger, environmentVariables);

    public static nint GetRemoteModuleHandle(nint hProcess, string moduleName)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.GetRemoteModuleHandle(hProcess, moduleName);

    public static InjectionResult InjectDll(
        nint hProcess,
        string dllPath,
        int timeoutMs = 5000,
        ILogger? logger = null)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.InjectDll(hProcess, dllPath, timeoutMs, logger);

    public static bool ResumeProcess(nint hThread, ILogger? logger = null)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.ResumeProcess(hThread, logger);

    public static bool GetExitCode(nint hProcess, out uint exitCode)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.GetExitCode(hProcess, out exitCode);

    public static SupervisedProcessResult CreateSuspendedAndInject(
        string executablePath,
        string arguments,
        string workingDirectory,
        string dllPath,
        Action<nint>? preResumeCallback = null,
        int timeoutMs = 5000,
        bool createNoWindow = true,
        ILogger? logger = null,
        StringDictionary? environmentVariables = null)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.CreateSuspendedAndInject(
            executablePath, arguments, workingDirectory, dllPath, preResumeCallback, timeoutMs, createNoWindow, logger, environmentVariables);

    public static DetourSynthesisResult SynthesizeRel32Detour(
        nint sourceAddress,
        nint trampolineAddress,
        byte[] originalBytes)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.SynthesizeRel32Detour(sourceAddress, trampolineAddress, originalBytes);

    public static bool ApplyMemoryPatch(
        nint hProcess,
        nint targetAddress,
        byte[] patchBytes,
        out string? errorMessage,
        ILogger? logger = null)
        => Kongor.Core.Services.Native.NativeProcessInjectionService.ApplyMemoryPatch(hProcess, targetAddress, patchBytes, out errorMessage, logger);
}
