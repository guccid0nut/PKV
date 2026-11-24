// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeProcessInjectionService.cs
// System: Kongor Protocol Core - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 5 Runtime Supervision - Native Process Injection & Trampolines
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Kongor.Core.Services.Native;

public sealed class SuspendedProcessWithStreamsResult : IDisposable
{
    private bool _disposed;

    public SuspendedProcessInfo ProcessInfo { get; }
    public StreamReader? StandardOutput { get; }
    public StreamReader? StandardError { get; }

    public SuspendedProcessWithStreamsResult(
        SuspendedProcessInfo processInfo,
        StreamReader? standardOutput,
        StreamReader? standardError)
    {
        ProcessInfo = processInfo;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StandardOutput?.Dispose();
        StandardError?.Dispose();
    }
}

public sealed record SuspendedProcessInfo(
    nint ProcessHandle,
    nint ThreadHandle,
    int ProcessId,
    int ThreadId);

public sealed record InjectionResult(
    bool Success,
    nint ModuleBase,
    string? ErrorMessage);

public sealed record SupervisedProcessResult(
    bool Success,
    int ProcessId,
    nint ProcessHandle,
    nint ModuleBase,
    string? ErrorMessage);

public sealed record DetourSynthesisResult(
    bool Success,
    nint SourceAddress,
    nint TrampolineAddress,
    byte[] DetourBytes,
    byte[] TrampolineBytes,
    long DisplacementToTrampoline,
    long DisplacementToReturn,
    string? ErrorMessage);

/// <summary>
///     Provides low-level Win32 runtime process supervision, CREATE_SUSPENDED process instantiation,
///     ±2 GB RIP-relative displacement trampoline allocation, and remote DLL injection.
/// </summary>
public static class NativeProcessInjectionService
{
    // Creation flags
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint HANDLE_FLAG_INHERIT = 0x00000001;

    // Memory flags
    public const uint MEM_COMMIT = 0x00001000;
    public const uint MEM_RESERVE = 0x00002000;
    public const uint MEM_RELEASE = 0x00008000;
    public const uint MEM_FREE = 0x00010000;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_EXECUTE = 0x10;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public nint lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        string? lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out nint hReadPipe,
        out nint hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(
        nint hObject,
        uint dwMask,
        uint dwFlags);

#if !PLAYTEST_BUILD
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAllocEx(
        nint hProcess,
        nint lpAddress,
        nuint dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(
        nint hProcess,
        nint lpAddress,
        nuint dwSize,
        uint dwFreeType);
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        nint hProcess,
        nint lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        nuint dwLength);

#if !PLAYTEST_BUILD
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesWritten);
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

#if !PLAYTEST_BUILD
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint CreateRemoteThread(
        nint hProcess,
        nint lpThreadAttributes,
        nuint dwStackSize,
        nint lpStartAddress,
        nint lpParameter,
        uint dwCreationFlags,
        out uint lpThreadId);
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeThread(nint hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(nint hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint hObject);

#if !PLAYTEST_BUILD
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true, ExactSpelling = true)]
    internal static extern nint GetProcAddress(nint hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(
        nint hProcess,
        nint lpAddress,
        nuint dwSize,
        uint flNewProtect,
        out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(
        nint hProcess,
        nint lpBaseAddress,
        nuint dwSize);

    public const uint LIST_MODULES_DEFAULT = 0x0;
    public const uint LIST_MODULES_32BIT = 0x01;
    public const uint LIST_MODULES_64BIT = 0x02;
    public const uint LIST_MODULES_ALL = 0x03;

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumProcessModulesEx(
        nint hProcess,
        [Out] nint[]? lphModule,
        uint cb,
        out uint lpcbNeeded,
        uint dwFilterFlag);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetModuleBaseNameW(
        nint hProcess,
        nint hModule,
        [Out] StringBuilder lpBaseName,
        uint nSize);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetModuleFileNameExW(
        nint hProcess,
        nint hModule,
        [Out] StringBuilder lpFilename,
        uint nSize);

    /// <summary>
    ///     Performs a dynamic Array-of-Bytes (AOB) signature scan across the memory space of a target process module.
    ///     Removes the necessity for static/hardcoded RVAs across changing engine builds.
    /// </summary>
    public static AobScanResult ScanModuleAob(
        nint processHandle,
        nint moduleBase,
        int moduleSize,
        AobPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (processHandle == nint.Zero || moduleBase == nint.Zero || moduleSize <= 0)
        {
            return AobScanResult.NotFound();
        }

        byte[] buffer = new byte[moduleSize];
        if (!ReadProcessMemory(processHandle, moduleBase, buffer, (nuint)buffer.Length, out nuint bytesRead) || (int)bytesRead < pattern.Length)
        {
            return new AobScanResult(false, -1, false, 0, $"ReadProcessMemory failed at module base 0x{moduleBase:X}");
        }

        int foundOffset = AobScanner.FindPattern(buffer.AsSpan(0, (int)bytesRead), pattern);
        if (foundOffset < 0)
        {
            return AobScanResult.NotFound();
        }

        return AobScanResult.ScanSuccess(foundOffset);
    }

    /// <summary>
    ///     Calculates the signed 64-bit relative RIP displacement between a source instruction and target.
    ///     In AMD64 architecture, rel32 displacement: offset = target - (source + 5).
    ///     Returns true if offset fits within [-2 GB, +2 GB - 1] (signed 32-bit integer range).
    /// </summary>
    public static bool CalculateRipDisplacement(nint sourceAddress, nint targetAddress, out long displacement, out bool fitsIn32Bit)
    {
        long source = sourceAddress;
        long target = targetAddress;
        // In x86_64, RIP relative displacement is relative to the address of the NEXT instruction (source + 5)
        displacement = target - (source + 5);
        fitsIn32Bit = displacement >= int.MinValue && displacement <= int.MaxValue;
        return fitsIn32Bit;
    }

    /// <summary>
    ///     Synthesizes an AMD64 ±2 GB rel32 detour hook.
    ///     Generates a 5-byte JMP rel32 (0xE9 disp32) with NOP padding for the source instruction,
    ///     and a trampoline executing the original instruction bytes followed by a JMP rel32 return.
    /// </summary>
    public static DetourSynthesisResult SynthesizeRel32Detour(
        nint sourceAddress,
        nint trampolineAddress,
        byte[] originalBytes)
    {
        if (originalBytes == null || originalBytes.Length < 5)
        {
            return new DetourSynthesisResult(
                false, sourceAddress, trampolineAddress, [], [], 0, 0,
                "Original instruction bytes must be at least 5 bytes to accommodate AMD64 JMP rel32.");
        }

        // 1. Calculate displacement from source to trampoline
        bool fitsForward = CalculateRipDisplacement(sourceAddress, trampolineAddress, out long dispForward, out bool fits32Forward);
        if (!fitsForward || !fits32Forward)
        {
            return new DetourSynthesisResult(
                false, sourceAddress, trampolineAddress, [], [], dispForward, 0,
                $"Target trampoline 0x{trampolineAddress:X16} is outside the ±2 GB signed 32-bit displacement range of source 0x{sourceAddress:X16} (disp: {dispForward} bytes).");
        }

        // 2. Build detour bytes: E9 [disp32] + NOPs
        byte[] detourBytes = new byte[originalBytes.Length];
        detourBytes[0] = 0xE9; // JMP rel32
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(detourBytes.AsSpan(1, 4), (int)dispForward);
        for (int i = 5; i < detourBytes.Length; i++)
        {
            detourBytes[i] = 0x90; // NOP
        }

        // 3. Calculate displacement from trampoline back to return address (sourceAddress + originalBytes.Length)
        nint returnAddress = unchecked((nint)((long)sourceAddress + originalBytes.Length));
        nint trampolineJumpInstructionAddress = unchecked((nint)((long)trampolineAddress + originalBytes.Length));
        bool fitsReturn = CalculateRipDisplacement(trampolineJumpInstructionAddress, returnAddress, out long dispReturn, out bool fits32Return);
        if (!fitsReturn || !fits32Return)
        {
            return new DetourSynthesisResult(
                false, sourceAddress, trampolineAddress, detourBytes, [], dispForward, dispReturn,
                $"Return address 0x{returnAddress:X16} is outside the ±2 GB range of trampoline jump 0x{trampolineJumpInstructionAddress:X16} (disp: {dispReturn} bytes).");
        }

        // 4. Build trampoline bytes: original instructions + E9 [returnDisp32]
        byte[] trampolineBytes = new byte[originalBytes.Length + 5];
        originalBytes.CopyTo(trampolineBytes.AsSpan(0, originalBytes.Length));
        trampolineBytes[originalBytes.Length] = 0xE9; // JMP rel32
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(trampolineBytes.AsSpan(originalBytes.Length + 1, 4), (int)dispReturn);

        return new DetourSynthesisResult(
            true,
            sourceAddress,
            trampolineAddress,
            detourBytes,
            trampolineBytes,
            dispForward,
            dispReturn,
            null);
    }

    /// <summary>
    ///     Finds and allocates an executable memory page in a remote 64-bit process within ±2 GB (0x7FFF0000 bytes)
    ///     of the specified target module base address. This guarantees that JMP [RIP + disp32] trampoline stubs
    ///     remain within 32-bit displacement reach.
    /// </summary>
#if !PLAYTEST_BUILD
    public static nint AllocateTrampolineWithin2Gb(
        nint hProcess,
        nint targetModuleBase,
        int sizeBytes = 4096,
        long maxDisplacementBytes = 0x7FFF0000L)
    {
        if (hProcess == nint.Zero || !OperatingSystem.IsWindows())
            return nint.Zero;

        long origin = targetModuleBase;
        long minAddr = Math.Max(0x10000L, origin - maxDisplacementBytes);
        long maxAddr = origin + maxDisplacementBytes;

        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        long currentAddr = origin;

        // 1. First probe forward (higher memory)
        while (currentAddr < maxAddr)
        {
            if (VirtualQueryEx(hProcess, (nint)currentAddr, out MEMORY_BASIC_INFORMATION mbi, (nuint)mbiSize) == 0)
                break;

            if (mbi.State == MEM_FREE && (long)mbi.RegionSize >= sizeBytes)
            {
                long regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;
                long candidateAddr = Math.Max((long)mbi.BaseAddress, currentAddr);
                candidateAddr = (candidateAddr + 0xFFFFL) & ~0xFFFFL; // Align to 64 KB allocation granularity

                if (candidateAddr + sizeBytes <= regionEnd && candidateAddr < maxAddr)
                {
                    nint allocated = VirtualAllocEx(
                        hProcess,
                        (nint)candidateAddr,
                        (nuint)sizeBytes,
                        MEM_COMMIT | MEM_RESERVE,
                        PAGE_EXECUTE_READWRITE);

                    if (allocated != nint.Zero)
                    {
                        CalculateRipDisplacement((nint)origin, allocated, out _, out bool fits);
                        if (fits) return allocated;
                        VirtualFreeEx(hProcess, allocated, 0, MEM_RELEASE);
                    }
                }
            }

            long next = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            if (next <= currentAddr) break;
            currentAddr = next;
        }

        // 2. Probe backward (lower memory)
        currentAddr = origin - 0x10000L;
        while (currentAddr > minAddr)
        {
            if (VirtualQueryEx(hProcess, (nint)currentAddr, out MEMORY_BASIC_INFORMATION mbi, (nuint)mbiSize) == 0)
                break;

            if (mbi.State == MEM_FREE && (long)mbi.RegionSize >= sizeBytes)
            {
                long regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;
                // Target the top of the free region nearest to origin
                long candidateAddr = Math.Min(currentAddr, regionEnd - sizeBytes);
                candidateAddr &= ~0xFFFFL; // Align to 64 KB allocation granularity

                if (candidateAddr >= (long)mbi.BaseAddress && candidateAddr >= minAddr)
                {
                    nint allocated = VirtualAllocEx(
                        hProcess,
                        (nint)candidateAddr,
                        (nuint)sizeBytes,
                        MEM_COMMIT | MEM_RESERVE,
                        PAGE_EXECUTE_READWRITE);

                    if (allocated != nint.Zero)
                    {
                        CalculateRipDisplacement((nint)origin, allocated, out _, out bool fits);
                        if (fits) return allocated;
                        VirtualFreeEx(hProcess, allocated, 0, MEM_RELEASE);
                    }
                }
            }

            if ((long)mbi.BaseAddress <= 0x1000L) break;
            long prev = (long)mbi.BaseAddress - 0x1000L;
            if (prev >= currentAddr) break;
            currentAddr = prev;
        }

        return nint.Zero;
    }
#else
    public static nint AllocateTrampolineWithin2Gb(
        nint hProcess,
        nint targetModuleBase,
        int sizeBytes = 4096,
        long maxDisplacementBytes = 0x7FFF0000L)
    {
        return nint.Zero;
    }
#endif

    /// <summary>
    ///     Builds a Win32 <c>CREATE_UNICODE_ENVIRONMENT</c> UTF-16LE environment block from a
    ///     <see cref="StringDictionary"/> (such as <see cref="ProcessStartInfo.EnvironmentVariables"/>).
    ///     Keys are sorted case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>) as required by
    ///     <c>CreateProcessW</c>, formatted as <c>KEY=VALUE\0</c>, and terminated by a trailing <c>\0</c>.
    /// </summary>
    internal static byte[] BuildUnicodeEnvironmentBlock(StringDictionary environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        Dictionary<string, string?> entries = new(environmentVariables.Count, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in environmentVariables)
        {
            if (entry.Key is not string key || string.IsNullOrEmpty(key))
            {
                continue;
            }

            entries[key] = entry.Value as string ?? string.Empty;
        }

        return BuildUnicodeEnvironmentBlock(entries);
    }

    /// <summary>
    ///     Builds a Win32 <c>CREATE_UNICODE_ENVIRONMENT</c> UTF-16LE environment block from a key-value dictionary.
    ///     Keys are sorted case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>), formatted as
    ///     <c>KEY=VALUE\0</c>, and terminated with a trailing <c>\0</c> (double UTF-16LE null terminator).
    /// </summary>
    internal static byte[] BuildUnicodeEnvironmentBlock(IDictionary<string, string?> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        Dictionary<string, string> deduped = new(environmentVariables.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> entry in environmentVariables)
        {
            if (string.IsNullOrEmpty(entry.Key))
            {
                continue;
            }

            deduped[entry.Key] = entry.Value ?? string.Empty;
        }

        if (deduped.Count == 0)
        {
            return Encoding.Unicode.GetBytes("\0\0");
        }

        List<KeyValuePair<string, string>> sortedEntries = new(deduped);
        sortedEntries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));

        StringBuilder sb = new(sortedEntries.Count * 48);
        foreach (KeyValuePair<string, string> kvp in sortedEntries)
        {
            sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\0');
        }

        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    /// <summary>
    ///     Creates a new child process in a SUSPENDED state with optional redirected streams, window suppression,
    ///     and an explicit child environment variable block.
    /// </summary>
    public static SuspendedProcessInfo CreateSuspendedProcess(
        string executablePath,
        string arguments,
        string workingDirectory,
        bool createNoWindow = true,
        ILogger? logger = null,
        StringDictionary? environmentVariables = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32 CREATE_SUSPENDED process creation is only supported on Windows.");

        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"Executable not found at '{executablePath}'.", executablePath);

        STARTUPINFO si = new()
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFO>()
        };

        SECURITY_ATTRIBUTES pSec = new() { nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        SECURITY_ATTRIBUTES tSec = new() { nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>() };

        uint flags = CREATE_SUSPENDED;
        if (createNoWindow)
        {
            flags |= CREATE_NO_WINDOW;
        }

        string fullCommandLine = $"\"{executablePath}\" {arguments}".Trim();

        byte[]? envBlock = environmentVariables is { Count: > 0 }
            ? BuildUnicodeEnvironmentBlock(environmentVariables)
            : null;
        int envVarCount = environmentVariables?.Count ?? 0;

        nint lpEnvironment = nint.Zero;
        bool success;
        int createProcessError = 0;
        PROCESS_INFORMATION pi;

        try
        {
            if (envBlock is { Length: > 0 })
            {
                flags |= CREATE_UNICODE_ENVIRONMENT;
                lpEnvironment = Marshal.AllocHGlobal(envBlock.Length);
                Marshal.Copy(envBlock, 0, lpEnvironment, envBlock.Length);
            }

            success = CreateProcessW(
                null,
                fullCommandLine,
                ref pSec,
                ref tSec,
                bInheritHandles: false,
                dwCreationFlags: flags,
                lpEnvironment: lpEnvironment,
                lpCurrentDirectory: workingDirectory,
                ref si,
                out pi);

            if (!success)
            {
                createProcessError = Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            if (lpEnvironment != nint.Zero)
            {
                Marshal.FreeHGlobal(lpEnvironment);
            }
        }

        if (!success)
        {
            logger?.LogError(
                "[NativeProcessInjection] CreateProcessW failed with Win32 error {Error} (EnvVars={EnvVarCount}).",
                createProcessError, envVarCount);
            throw new Win32Exception(createProcessError, $"Failed to launch '{executablePath}' in suspended state (Win32 error {createProcessError}).");
        }

        logger?.LogInformation(
            "[NativeProcessInjection] Created SUSPENDED process PID {Pid} (TID {Tid}) for '{Exe}' (EnvVars={EnvVarCount}, UnicodeEnvBlockBytes={EnvBytes}).",
            pi.dwProcessId, pi.dwThreadId, Path.GetFileName(executablePath), envVarCount, envBlock?.Length ?? 0);

        return new SuspendedProcessInfo(pi.hProcess, pi.hThread, (int)pi.dwProcessId, (int)pi.dwThreadId);
    }

    /// <summary>
    ///     Creates a new child process in a SUSPENDED state with redirected standard output and standard error pipes
    ///     and an optional explicit child environment variable block.
    ///     Returns a <see cref="SuspendedProcessWithStreamsResult"/> containing process/thread handles and asynchronous readers.
    /// </summary>
    public static SuspendedProcessWithStreamsResult CreateSuspendedProcessWithStreams(
        string executablePath,
        string arguments,
        string workingDirectory,
        bool createNoWindow = false,
        ILogger? logger = null,
        StringDictionary? environmentVariables = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32 CREATE_SUSPENDED process creation is only supported on Windows.");

        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"Executable not found at '{executablePath}'.", executablePath);

        SECURITY_ATTRIBUTES pipeSec = new()
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
            lpSecurityDescriptor = nint.Zero
        };

        // Create inheritable pipe for stdout with 1MB buffer to prevent child write blocking
        if (!CreatePipe(out nint hStdOutRead, out nint hStdOutWrite, ref pipeSec, 1048576))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Failed to create stdout pipe (Win32 error {err}).");
        }
        // Ensure read handle is NOT inherited by child
        SetHandleInformation(hStdOutRead, HANDLE_FLAG_INHERIT, 0);

        // Create inheritable pipe for stderr with 1MB buffer
        if (!CreatePipe(out nint hStdErrRead, out nint hStdErrWrite, ref pipeSec, 1048576))
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(hStdOutRead);
            CloseHandle(hStdOutWrite);
            throw new Win32Exception(err, $"Failed to create stderr pipe (Win32 error {err}).");
        }
        // Ensure read handle is NOT inherited by child
        SetHandleInformation(hStdErrRead, HANDLE_FLAG_INHERIT, 0);

        // Create inheritable pipe for stdin with 4KB buffer
        if (!CreatePipe(out nint hStdInRead, out nint hStdInWrite, ref pipeSec, 4096))
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(hStdOutRead);
            CloseHandle(hStdOutWrite);
            CloseHandle(hStdErrRead);
            CloseHandle(hStdErrWrite);
            throw new Win32Exception(err, $"Failed to create stdin pipe (Win32 error {err}).");
        }
        // Ensure write handle is NOT inherited by child
        SetHandleInformation(hStdInWrite, HANDLE_FLAG_INHERIT, 0);

        STARTUPINFO si = new()
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdInput = hStdInRead,
            hStdOutput = hStdOutWrite,
            hStdError = hStdErrWrite
        };

        SECURITY_ATTRIBUTES pSec = new() { nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        SECURITY_ATTRIBUTES tSec = new() { nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>() };

        uint flags = CREATE_SUSPENDED;
        if (createNoWindow)
        {
            flags |= CREATE_NO_WINDOW;
        }

        string fullCommandLine = $"\"{executablePath}\" {arguments}".Trim();

        byte[]? envBlock = environmentVariables is { Count: > 0 }
            ? BuildUnicodeEnvironmentBlock(environmentVariables)
            : null;
        int envVarCount = environmentVariables?.Count ?? 0;

        nint lpEnvironment = nint.Zero;
        bool success;
        int createProcessError = 0;
        PROCESS_INFORMATION pi;

        try
        {
            if (envBlock is { Length: > 0 })
            {
                flags |= CREATE_UNICODE_ENVIRONMENT;
                lpEnvironment = Marshal.AllocHGlobal(envBlock.Length);
                Marshal.Copy(envBlock, 0, lpEnvironment, envBlock.Length);
            }

            success = CreateProcessW(
                null,
                fullCommandLine,
                ref pSec,
                ref tSec,
                bInheritHandles: true,
                dwCreationFlags: flags,
                lpEnvironment: lpEnvironment,
                lpCurrentDirectory: workingDirectory,
                ref si,
                out pi);

            if (!success)
            {
                createProcessError = Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            if (lpEnvironment != nint.Zero)
            {
                Marshal.FreeHGlobal(lpEnvironment);
            }

            // Immediately close child's inherited pipe ends in parent process
            CloseHandle(hStdInRead);
            CloseHandle(hStdInWrite);
            CloseHandle(hStdOutWrite);
            CloseHandle(hStdErrWrite);
        }

        if (!success)
        {
            CloseHandle(hStdOutRead);
            CloseHandle(hStdErrRead);
            logger?.LogError(
                "[NativeProcessInjection] CreateProcessW failed with Win32 error {Error} (EnvVars={EnvVarCount}).",
                createProcessError, envVarCount);
            throw new Win32Exception(createProcessError, $"Failed to launch '{executablePath}' in suspended state with redirected streams (Win32 error {createProcessError}).");
        }

        logger?.LogInformation(
            "[NativeProcessInjection] Created SUSPENDED process PID {Pid} (TID {Tid}) with redirected streams for '{Exe}' (EnvVars={EnvVarCount}, UnicodeEnvBlockBytes={EnvBytes}).",
            pi.dwProcessId, pi.dwThreadId, Path.GetFileName(executablePath), envVarCount, envBlock?.Length ?? 0);

        SafeFileHandle stdoutHandle = new(hStdOutRead, ownsHandle: true);
        FileStream stdoutStream = new(stdoutHandle, FileAccess.Read, 4096, isAsync: true);
        StreamReader stdoutReader = new(stdoutStream, Encoding.UTF8);

        SafeFileHandle stderrHandle = new(hStdErrRead, ownsHandle: true);
        FileStream stderrStream = new(stderrHandle, FileAccess.Read, 4096, isAsync: true);
        StreamReader stderrReader = new(stderrStream, Encoding.UTF8);

        SuspendedProcessInfo procInfo = new(pi.hProcess, pi.hThread, (int)pi.dwProcessId, (int)pi.dwThreadId);
        return new SuspendedProcessWithStreamsResult(procInfo, stdoutReader, stderrReader);
    }

    /// <summary>
    ///     Queries the remote 64-bit process module table via PSAPI to retrieve the full 64-bit base address
    ///     (HMODULE) of a loaded DLL matching an exact full filesystem path. Prevents false-positive basename
    ///     collisions against system DLLs such as C:\Windows\System32\version.dll.
    /// </summary>
    public static nint GetRemoteModuleHandleByFullPath(nint hProcess, string expectedFullPath)
    {
        if (hProcess == nint.Zero || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(expectedFullPath))
            return nint.Zero;

        string normalizedTarget;
        try
        {
            normalizedTarget = Path.GetFullPath(expectedFullPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NativeProcessInjectionService] Failed to normalize expectedFullPath '{expectedFullPath}': {ex.Message}");
            return nint.Zero;
        }

        nint[] modules = new nint[1024];
        uint cb = (uint)(modules.Length * nint.Size);
        bool ok = EnumProcessModulesEx(hProcess, modules, cb, out uint cbNeeded, LIST_MODULES_ALL);
        if (!ok || cbNeeded == 0)
        {
            ok = EnumProcessModulesEx(hProcess, modules, cb, out cbNeeded, LIST_MODULES_64BIT);
        }
        if (!ok || cbNeeded == 0)
        {
            ok = EnumProcessModulesEx(hProcess, modules, cb, out cbNeeded, LIST_MODULES_DEFAULT);
        }

        if (cbNeeded == 0)
        {
            return nint.Zero;
        }

        int count = (int)(cbNeeded / (uint)nint.Size);
        if (count > modules.Length)
        {
            modules = new nint[count];
            cb = (uint)(modules.Length * nint.Size);
            if (!EnumProcessModulesEx(hProcess, modules, cb, out _, LIST_MODULES_ALL) &&
                !EnumProcessModulesEx(hProcess, modules, cb, out _, LIST_MODULES_64BIT) &&
                !EnumProcessModulesEx(hProcess, modules, cb, out _, LIST_MODULES_DEFAULT))
            {
                count = 1024;
            }
        }

        StringBuilder pathBuilder = new(1024);
        for (int i = 0; i < count; i++)
        {
            nint hMod = modules[i];
            if (hMod == nint.Zero) continue;

            pathBuilder.Clear();
            uint pathLen = GetModuleFileNameExW(hProcess, hMod, pathBuilder, (uint)pathBuilder.Capacity);
            if (pathLen > 0)
            {
                try
                {
                    string modFullPath = Path.GetFullPath(pathBuilder.ToString());
                    if (string.Equals(modFullPath, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return hMod;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[NativeProcessInjectionService] Ignoring non-standard module path '{pathBuilder}': {ex.Message}");
                }
            }
        }

        return nint.Zero;
    }

    /// <summary>
    ///     Queries the remote 64-bit process module table via PSAPI to retrieve the full 64-bit base address
    ///     (HMODULE) of a loaded DLL by name (or exact path when rooted). Avoids 32-bit truncation from thread exit codes.
    /// </summary>
    public static nint GetRemoteModuleHandle(nint hProcess, string moduleName)
    {
        if (hProcess == nint.Zero || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(moduleName))
            return nint.Zero;

        if (Path.IsPathRooted(moduleName))
        {
            return GetRemoteModuleHandleByFullPath(hProcess, moduleName);
        }

        string targetName = Path.GetFileName(moduleName);

        // Initial buffer for module handles
        nint[] modules = new nint[1024];
        uint cb = (uint)(modules.Length * nint.Size);
        bool ok = EnumProcessModulesEx(hProcess, modules, cb, out uint cbNeeded, LIST_MODULES_ALL);
        if (!ok || cbNeeded == 0)
        {
            ok = EnumProcessModulesEx(hProcess, modules, cb, out cbNeeded, LIST_MODULES_64BIT);
        }
        if (!ok || cbNeeded == 0)
        {
            ok = EnumProcessModulesEx(hProcess, modules, cb, out cbNeeded, LIST_MODULES_DEFAULT);
        }

        // On 64-bit Windows, ERROR_PARTIAL_COPY (299) can occur when probing non-existent WOW64 PEBs,
        // but 64-bit module handles are still successfully populated into the buffer if cbNeeded > 0.
        if (cbNeeded == 0)
        {
            return nint.Zero;
        }

        int count = (int)(cbNeeded / (uint)nint.Size);
        if (count > modules.Length)
        {
            modules = new nint[count];
            cb = (uint)(modules.Length * nint.Size);
            if (!EnumProcessModulesEx(hProcess, modules, cb, out _, LIST_MODULES_ALL) &&
                !EnumProcessModulesEx(hProcess, modules, cb, out _, LIST_MODULES_64BIT) &&
                !EnumProcessModulesEx(hProcess, modules, cb, out _, LIST_MODULES_DEFAULT))
            {
                count = 1024;
            }
        }

        StringBuilder nameBuilder = new(1024);
        for (int i = 0; i < count; i++)
        {
            nint hMod = modules[i];
            if (hMod == nint.Zero) continue;

            nameBuilder.Clear();
            uint len = GetModuleBaseNameW(hProcess, hMod, nameBuilder, (uint)nameBuilder.Capacity);
            if (len > 0)
            {
                string baseName = nameBuilder.ToString();
                if (string.Equals(baseName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return hMod;
                }
            }

            // Fallback to full path check
            nameBuilder.Clear();
            uint pathLen = GetModuleFileNameExW(hProcess, hMod, nameBuilder, (uint)nameBuilder.Capacity);
            if (pathLen > 0)
            {
                string fileName = Path.GetFileName(nameBuilder.ToString());
                if (string.Equals(fileName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return hMod;
                }
            }
        }

        return nint.Zero;
    }

    /// <summary>
    ///     Injects a native DLL into a remote process by allocating memory and invoking LoadLibraryW via CreateRemoteThread.
    /// </summary>
#if !PLAYTEST_BUILD
    public static InjectionResult InjectDll(
        nint hProcess,
        string dllPath,
        int timeoutMs = 5000,
        ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows() || hProcess == nint.Zero)
            return new InjectionResult(false, nint.Zero, "Invalid process handle or non-Windows OS.");

        if (!File.Exists(dllPath))
            return new InjectionResult(false, nint.Zero, $"Target DLL does not exist at '{dllPath}'.");

        string fullDllPath = Path.GetFullPath(dllPath);
        byte[] pathBytes = Encoding.Unicode.GetBytes(fullDllPath + "\0");

        // 1. Allocate buffer in target process
        nint remoteMemory = VirtualAllocEx(
            hProcess,
            nint.Zero,
            (nuint)pathBytes.Length,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE);

        if (remoteMemory == nint.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            logger?.LogWarning("[NativeProcessInjection] VirtualAllocEx failed in target process: {Error}", err);
            return new InjectionResult(false, nint.Zero, $"VirtualAllocEx failed with Win32 error {err}.");
        }

        try
        {
            // 2. Write DLL path to remote process
            bool writeOk = WriteProcessMemory(
                hProcess,
                remoteMemory,
                pathBytes,
                (nuint)pathBytes.Length,
                out nuint bytesWritten);

            if (!writeOk || (int)bytesWritten != pathBytes.Length)
            {
                int err = Marshal.GetLastWin32Error();
                logger?.LogWarning("[NativeProcessInjection] WriteProcessMemory failed: {Error}", err);
                return new InjectionResult(false, nint.Zero, $"WriteProcessMemory failed with Win32 error {err}.");
            }

            // 3. Locate LoadLibraryW in kernel32.dll
            nint hKernel32 = GetModuleHandleW("kernel32.dll");
            if (hKernel32 == nint.Zero)
                return new InjectionResult(false, nint.Zero, "Failed to get handle for kernel32.dll.");

            nint pLoadLibraryW = GetProcAddress(hKernel32, "LoadLibraryW");
            if (pLoadLibraryW == nint.Zero)
                return new InjectionResult(false, nint.Zero, "Failed to get address for LoadLibraryW.");

            // 4. Create remote thread
            nint hThread = CreateRemoteThread(
                hProcess,
                nint.Zero,
                0,
                pLoadLibraryW,
                remoteMemory,
                0,
                out uint remoteTid);

            if (hThread == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                logger?.LogWarning("[NativeProcessInjection] CreateRemoteThread failed: {Error}", err);
                return new InjectionResult(false, nint.Zero, $"CreateRemoteThread failed with Win32 error {err}.");
            }

            try
            {
                // 5. Wait for LoadLibraryW thread to complete
                uint waitResult = WaitForSingleObject(hThread, (uint)Math.Max(100, timeoutMs));
                if (waitResult != WAIT_OBJECT_0)
                {
                    logger?.LogWarning("[NativeProcessInjection] Remote LoadLibraryW thread timed out or failed (Result: 0x{Result:X})", waitResult);
                    return new InjectionResult(false, nint.Zero, $"Remote LoadLibraryW thread timed out (wait result: 0x{waitResult:X8}).");
                }

                // 6. Resolve 64-bit module base address from the target process loader table.
                // On 64-bit Windows, GetExitCodeThread only returns the lower 32 bits of HMODULE.
                // If ASLR maps the DLL at a 4GB boundary (0x..._00000000), exitCode is 0 even on success.
                // Furthermore, casting exitCode to nint strips the upper 32 bits of the ASLR pointer.
                string dllFileName = Path.GetFileName(dllPath);
                nint remoteModuleBase = GetRemoteModuleHandleByFullPath(hProcess, fullDllPath);
                if (remoteModuleBase == nint.Zero && !string.Equals(dllFileName, "version.dll", StringComparison.OrdinalIgnoreCase))
                {
                    remoteModuleBase = GetRemoteModuleHandle(hProcess, dllFileName);
                }

                bool exitOk = GetExitCodeThread(hThread, out uint exitCode);

                if (remoteModuleBase != nint.Zero)
                {
                    logger?.LogInformation(
                        "[NativeProcessInjection] Injected '{Dll}' successfully (Remote 64-bit HMODULE: 0x{HModule:X16}).",
                        dllFileName, (ulong)remoteModuleBase);
                    return new InjectionResult(true, remoteModuleBase, null);
                }

                // Fallback: If EnumProcessModulesEx was restricted by OS permissions but exitCode is non-zero,
                // preserve lower 32 bits with an informational notice.
                if (exitOk && exitCode != 0)
                {
                    logger?.LogWarning(
                        "[NativeProcessInjection] Module '{Dll}' not found in loader table via PSAPI; fallback to 32-bit thread exit code: 0x{ExitCode:X8}.",
                        dllFileName, exitCode);
                    return new InjectionResult(true, (nint)exitCode, null);
                }

                logger?.LogWarning("[NativeProcessInjection] LoadLibraryW returned NULL in remote process (injection failed).");
                return new InjectionResult(false, nint.Zero, "LoadLibraryW returned NULL in target process (injection failed).");
            }
            finally
            {
                CloseHandle(hThread);
            }
        }
        finally
        {
            VirtualFreeEx(hProcess, remoteMemory, 0, MEM_RELEASE);
        }
    }
#else
    public static InjectionResult InjectDll(
        nint hProcess,
        string dllPath,
        int timeoutMs = 5000,
        ILogger? logger = null)
    {
        logger?.LogInformation("[NativeProcessInjection] Runtime DLL injection is compiled out in PLAYTEST_BUILD.");
        return new InjectionResult(false, nint.Zero, "Runtime DLL injection is compiled out in PLAYTEST_BUILD.");
    }
#endif

    /// <summary>
    ///     Resumes execution of a suspended thread.
    /// </summary>
    public static bool ResumeProcess(nint hThread, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows() || hThread == nint.Zero) return false;

        uint previousSuspendCount = ResumeThread(hThread);
        if (previousSuspendCount == 0xFFFFFFFF)
        {
            int err = Marshal.GetLastWin32Error();
            logger?.LogWarning("[NativeProcessInjection] ResumeThread failed with Win32 error {Error}.", err);
            return false;
        }

        logger?.LogDebug("[NativeProcessInjection] Thread resumed (previous suspend count: {Count}).", previousSuspendCount);
        return true;
    }

    /// <summary>
    ///     Safely queries the exit code of a process handle using Win32 GetExitCodeProcess.
    ///     Returns false if process is still active (STILL_ACTIVE = 259) or call failed.
    /// </summary>
    public static bool GetExitCode(nint hProcess, out uint exitCode)
    {
        exitCode = 0;
        if (!OperatingSystem.IsWindows() || hProcess == nint.Zero) return false;
        return GetExitCodeProcess(hProcess, out exitCode);
    }

    /// <summary>
    ///     End-to-end orchestration: creates a process suspended, invokes a callback (e.g. Job Object assignment),
    ///     injects the specified DLL, and resumes the primary thread.
    /// </summary>
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
    {
        SuspendedProcessInfo procInfo;
        try
        {
            procInfo = CreateSuspendedProcess(executablePath, arguments, workingDirectory, createNoWindow, logger, environmentVariables);
        }
        catch (Exception ex)
        {
            return new SupervisedProcessResult(false, 0, nint.Zero, nint.Zero, $"Failed to create suspended process: {ex.Message}");
        }

        try
        {
            // Execute pre-resume callback (such as Job Object assignment)
            preResumeCallback?.Invoke(procInfo.ProcessHandle);

            // Inject DLL
            InjectionResult injectResult = InjectDll(procInfo.ProcessHandle, dllPath, timeoutMs, logger);
            if (!injectResult.Success)
            {
                logger?.LogWarning("[NativeProcessInjection] DLL injection failed: {Error}. Resuming process anyway.", injectResult.ErrorMessage);
            }

            // Resume process execution
            ResumeProcess(procInfo.ThreadHandle, logger);

            return new SupervisedProcessResult(
                true,
                procInfo.ProcessId,
                procInfo.ProcessHandle,
                injectResult.ModuleBase,
                injectResult.ErrorMessage);
        }
        finally
        {
            // Close the primary thread handle once resumed
            CloseHandle(procInfo.ThreadHandle);
        }
    }

    /// <summary>
    ///     Applies a machine-code patch or detour trampoline to a remote process at the specified target address.
    ///     Changes memory protection to PAGE_EXECUTE_READWRITE, writes bytes, flushes instruction cache,
    ///     and restores the original page protection.
    /// </summary>
#if !PLAYTEST_BUILD
    public static bool ApplyMemoryPatch(
        nint hProcess,
        nint targetAddress,
        byte[] patchBytes,
        out string? errorMessage,
        ILogger? logger = null)
    {
        errorMessage = null;
        if (!OperatingSystem.IsWindows() || hProcess == nint.Zero)
        {
            errorMessage = "Invalid process handle or non-Windows OS.";
            return false;
        }

        if (patchBytes.Length == 0)
        {
            errorMessage = "Patch bytes array is empty.";
            return false;
        }

        // Change memory protection to PAGE_EXECUTE_READWRITE
        if (!VirtualProtectEx(hProcess, targetAddress, (nuint)patchBytes.Length, PAGE_EXECUTE_READWRITE, out uint oldProtect))
        {
            int err = Marshal.GetLastWin32Error();
            errorMessage = $"VirtualProtectEx (PAGE_EXECUTE_READWRITE) failed with Win32 error {err}.";
            logger?.LogWarning("[NativeProcessInjection] {Error}", errorMessage);
            return false;
        }

        try
        {
            bool writeOk = WriteProcessMemory(
                hProcess,
                targetAddress,
                patchBytes,
                (nuint)patchBytes.Length,
                out nuint bytesWritten);

            if (!writeOk || (int)bytesWritten != patchBytes.Length)
            {
                int err = Marshal.GetLastWin32Error();
                errorMessage = $"WriteProcessMemory failed with Win32 error {err}.";
                logger?.LogWarning("[NativeProcessInjection] {Error}", errorMessage);
                return false;
            }

            // Flush CPU instruction cache
            FlushInstructionCache(hProcess, targetAddress, (nuint)patchBytes.Length);
            logger?.LogInformation("[NativeProcessInjection] Applied {Count} patch bytes at 0x{Addr:X16}.", patchBytes.Length, targetAddress);
            return true;
        }
        finally
        {
            VirtualProtectEx(hProcess, targetAddress, (nuint)patchBytes.Length, oldProtect, out _);
        }
    }
#else
    public static bool ApplyMemoryPatch(
        nint hProcess,
        nint targetAddress,
        byte[] patchBytes,
        out string? errorMessage,
        ILogger? logger = null)
    {
        errorMessage = "Process memory patching is compiled out in PLAYTEST_BUILD.";
        logger?.LogInformation("[NativeProcessInjection] {Error}", errorMessage);
        return false;
    }
#endif

    // =========================================================================
    // #220 (Gem 3F): Anti-Cheat Safe WH_GETMESSAGE Hook Injection & Rolling-XOR
    // API Obfuscation (obs-studio/shared/obfuscate.c & inject-library.c)
    // =========================================================================

    public const ulong ObsRollingXorInitialKey = 0x159876543210ABCDUL;
    public const int WH_GETMESSAGE = 3;
    public const uint WM_USER = 0x0400U;
    public const uint WM_OBS_HOOK_WAKEUP = WM_USER + 432U;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookExW(
        int idHook,
        nint lpfn,
        nint hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(
        uint idThread,
        uint Msg,
        nuint wParam,
        nint lParam);

    public sealed record WhGetMessageInjectionPlan(
        bool AntiCheatSafeMode,
        int HookId,
        uint WakeupMessageId,
        uint TargetThreadId,
        int MaxWakeupAttempts,
        byte[] ObfuscatedSetWindowsHookExA,
        byte[] ObfuscatedPostThreadMessageW,
        byte[] ObfuscatedUnhookWindowsHookEx);

    public sealed record WhGetMessageInjectionResult(
        WhGetMessageInjectionPlan Plan,
        bool ExecutedLiveHook,
        nint HookHandle,
        int WakeupMessagesDelivered,
        bool UnhookedCleanly);

    /// <summary>
    ///     #220 (Gem 3F): Applies OBS-Studio's rolling 64-bit XOR cipher (`obfuscate.c`) where the 64-bit key
    ///     is rotated left by 4 bits after each byte (`key = (key &lt;&lt; 4) | (key &gt;&gt; 60)`).
    ///     Because XOR is symmetric, calling this method again with the same <paramref name="initialKey"/>
    ///     reconstructs the original plaintext bytes in-memory without leaving static ASCII strings in `.rdata`.
    /// </summary>
    public static byte[] ObfuscateObsRollingXorSymbol(
        ReadOnlySpan<byte> inputBytes,
        ulong initialKey = ObsRollingXorInitialKey)
    {
        byte[] output = new byte[inputBytes.Length];
        ulong key = initialKey;

        for (int i = 0; i < inputBytes.Length; i++)
        {
            output[i] = (byte)(inputBytes[i] ^ (byte)(key & 0xFFUL));
            key = (key << 4) | (key >> 60);
        }

        return output;
    }

    /// <summary>
    ///     #220 (Gem 3F): Deobfuscates a byte buffer encoded with <see cref="ObfuscateObsRollingXorSymbol"/>.
    /// </summary>
    public static byte[] DeobfuscateObsRollingXorSymbol(
        ReadOnlySpan<byte> obfuscatedBytes,
        ulong initialKey = ObsRollingXorInitialKey)
    {
        return ObfuscateObsRollingXorSymbol(obfuscatedBytes, initialKey);
    }

    /// <summary>
    ///     #220 (Gem 3F): Obfuscates an ASCII symbol name using the OBS rolling 64-bit XOR cipher.
    /// </summary>
    public static byte[] ObfuscateObsRollingXorString(
        string asciiSymbol,
        ulong initialKey = ObsRollingXorInitialKey)
    {
        ArgumentNullException.ThrowIfNull(asciiSymbol);
        byte[] raw = Encoding.ASCII.GetBytes(asciiSymbol);
        return ObfuscateObsRollingXorSymbol(raw, initialKey);
    }

    /// <summary>
    ///     #220 (Gem 3F): Reconstructs an ASCII symbol name from rolling-XOR obfuscated bytes.
    /// </summary>
    public static string DeobfuscateObsRollingXorString(
        ReadOnlySpan<byte> obfuscatedBytes,
        ulong initialKey = ObsRollingXorInitialKey)
    {
        byte[] decoded = DeobfuscateObsRollingXorSymbol(obfuscatedBytes, initialKey);
        int nullIdx = Array.IndexOf(decoded, (byte)0);
        int length = nullIdx >= 0 ? nullIdx : decoded.Length;
        return Encoding.ASCII.GetString(decoded, 0, length);
    }

    /// <summary>
    ///     #220 (Gem 3F): Builds an anti-cheat safe <c>WH_GETMESSAGE</c> (<c>3</c>) window-message hook injection plan
    ///     with <c>WM_USER + 432</c> thread-message wakeup and rolling-XOR obfuscated User32 import symbols.
    /// </summary>
    public static WhGetMessageInjectionPlan PlanWhGetMessageSafeInjection(
        uint targetThreadId,
        int maxWakeupAttempts = 200)
    {
        int clampedAttempts = Math.Clamp(maxWakeupAttempts, 1, 2000);
        return new WhGetMessageInjectionPlan(
            AntiCheatSafeMode: true,
            HookId: WH_GETMESSAGE,
            WakeupMessageId: WM_OBS_HOOK_WAKEUP,
            TargetThreadId: targetThreadId,
            MaxWakeupAttempts: clampedAttempts,
            ObfuscatedSetWindowsHookExA: ObfuscateObsRollingXorString("SetWindowsHookExA"),
            ObfuscatedPostThreadMessageW: ObfuscateObsRollingXorString("PostThreadMessageW"),
            ObfuscatedUnhookWindowsHookEx: ObfuscateObsRollingXorString("UnhookWindowsHookEx"));
    }

    /// <summary>
    ///     #220 (Gem 3F): Plans or executes an anti-cheat safe <c>SetWindowsHookExW(WH_GETMESSAGE = 3, ...)</c>
    ///     injection with <c>PostThreadMessageW(targetThreadId, WM_USER + 432, ...)</c> wakeup and cleanup via
    ///     <c>UnhookWindowsHookEx</c>.
    /// </summary>
    public static WhGetMessageInjectionResult PlanOrExecuteWhGetMessageInjection(
        uint targetThreadId,
        nint hookModuleHandle = 0,
        nint hookProcAddress = 0,
        bool executeLiveHook = false,
        int maxWakeupAttempts = 10,
        Func<uint, uint, nuint, nint, bool>? postThreadMessageOverride = null)
    {
        WhGetMessageInjectionPlan plan = PlanWhGetMessageSafeInjection(targetThreadId, maxWakeupAttempts);
        if (!executeLiveHook || targetThreadId == 0)
        {
            return new WhGetMessageInjectionResult(
                Plan: plan,
                ExecutedLiveHook: false,
                HookHandle: nint.Zero,
                WakeupMessagesDelivered: 0,
                UnhookedCleanly: false);
        }

        nint hHook = hookProcAddress != nint.Zero && OperatingSystem.IsWindows()
            ? SetWindowsHookExW(WH_GETMESSAGE, hookProcAddress, hookModuleHandle, targetThreadId)
            : (postThreadMessageOverride != null ? (nint)0x55AA : nint.Zero);

        if (hHook == nint.Zero)
        {
            return new WhGetMessageInjectionResult(
                Plan: plan,
                ExecutedLiveHook: true,
                HookHandle: nint.Zero,
                WakeupMessagesDelivered: 0,
                UnhookedCleanly: false);
        }

        int delivered = ExecuteWhGetMessageWakeupLoop(
            targetThreadId,
            hHook,
            maxWakeupAttempts,
            postThreadMessageOverride);

        bool unhooked = hookProcAddress != nint.Zero && OperatingSystem.IsWindows()
            ? UnhookWindowsHookEx(hHook)
            : true;

        return new WhGetMessageInjectionResult(
            Plan: plan,
            ExecutedLiveHook: true,
            HookHandle: hHook,
            WakeupMessagesDelivered: delivered,
            UnhookedCleanly: unhooked);
    }

    /// <summary>
    ///     #220 (Gem 3F): Drives the <c>PostThreadMessageW(targetThreadId, WM_USER + 432, 0, hookHandle)</c>
    ///     wakeup retry loop to force the target UI thread's message pump to load the <c>WH_GETMESSAGE</c> hook DLL.
    /// </summary>
    public static int ExecuteWhGetMessageWakeupLoop(
        uint targetThreadId,
        nint hookHandle,
        int maxAttempts = 10,
        Func<uint, uint, nuint, nint, bool>? postThreadMessageOverride = null)
    {
        if (targetThreadId == 0 || hookHandle == nint.Zero || maxAttempts <= 0)
        {
            return 0;
        }

        int boundedAttempts = Math.Clamp(maxAttempts, 1, 200);
        int deliveredCount = 0;

        for (int attempt = 0; attempt < boundedAttempts; attempt++)
        {
            bool posted = postThreadMessageOverride != null
                ? postThreadMessageOverride(targetThreadId, WM_OBS_HOOK_WAKEUP, 0, hookHandle)
                : (OperatingSystem.IsWindows() && PostThreadMessageW(targetThreadId, WM_OBS_HOOK_WAKEUP, 0, hookHandle));

            if (posted)
            {
                deliveredCount++;
            }
        }

        return deliveredCount;
    }
}

