// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 12 (#153: imhex-peb-cmdline-and-virtualquery-map):
/// Binary-searched VirtualQueryEx memory region classifier and cross-process PEB
/// RTL_USER_PROCESS_PARAMETERS.CommandLine reader adapted from ImHex process_memory_provider.cpp.
/// </summary>
public sealed record MemoryRegionDescriptor(
    ulong BaseAddress,
    ulong AllocationBase,
    ulong RegionSize,
    uint State,
    uint Protect,
    uint Type,
    string ProtectionSummary,
    string RegionClassification,
    string AssociatedModuleOrLabel);

public static class ProcessMemoryMapInspector
{
    private static readonly ILogger s_logger = NullLogger.Instance;

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_FREE = 0x10000;

    public const uint MEM_PRIVATE = 0x20000;
    public const uint MEM_MAPPED = 0x40000;
    public const uint MEM_IMAGE = 0x1000000;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_WRITECOPY = 0x08;
    public const uint PAGE_EXECUTE = 0x10;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    public const uint PAGE_GUARD = 0x100;

    /// <summary>
    /// Classifies raw VirtualQueryEx / minidump memory regions into human- and agent-readable
    /// protection strings (R/W/X/G) and semantic region categories (ImageSection, MappedView,
    /// PrivateHeapOrStack, UnallocatedOrReserved), sorted by BaseAddress for O(log N) lookup.
    /// </summary>
    public static IReadOnlyList<MemoryRegionDescriptor> ClassifyMemoryRegions(
        IEnumerable<(ulong BaseAddress, ulong AllocationBase, ulong RegionSize, uint State, uint Protect, uint Type, string? ModuleName)> rawRegions)
    {
        ArgumentNullException.ThrowIfNull(rawRegions);
        List<MemoryRegionDescriptor> list = new List<MemoryRegionDescriptor>();

        foreach ((ulong baseAddr, ulong allocBase, ulong size, uint state, uint protect, uint type, string? moduleName) in rawRegions)
        {
            if (size == 0)
            {
                continue;
            }

            string protSummary = FormatProtectionFlags(state, protect);
            string classification = ClassifyRegionSemantics(state, protect, type, moduleName);
            string label = !string.IsNullOrWhiteSpace(moduleName)
                ? moduleName!
                : $"{classification}@0x{baseAddr:X}";

            list.Add(new MemoryRegionDescriptor(
                baseAddr,
                allocBase,
                size,
                state,
                protect,
                type,
                protSummary,
                classification,
                label));
        }

        list.Sort(static (MemoryRegionDescriptor x, MemoryRegionDescriptor y) => x.BaseAddress.CompareTo(y.BaseAddress));
        return list;
    }

    /// <summary>
    /// Performs an O(log N) binary search over a sorted list of MemoryRegionDescriptors to find the
    /// region containing targetAddress: region.BaseAddress &lt;= targetAddress &lt; region.BaseAddress + region.RegionSize.
    /// Adapted from ImHex ProcessMemoryProvider::getRegionAtAddress().
    /// </summary>
    public static MemoryRegionDescriptor? LookupRegion(
        IReadOnlyList<MemoryRegionDescriptor> sortedRegions,
        ulong targetAddress)
    {
        if (sortedRegions == null || sortedRegions.Count == 0)
        {
            return null;
        }

        int low = 0;
        int high = sortedRegions.Count - 1;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            MemoryRegionDescriptor candidate = sortedRegions[mid];
            ulong endExclusive = unchecked(candidate.BaseAddress + candidate.RegionSize);

            if (targetAddress < candidate.BaseAddress)
            {
                high = mid - 1;
            }
            else if (targetAddress >= endExclusive && endExclusive > candidate.BaseAddress)
            {
                low = mid + 1;
            }
            else
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Phase 13 (#155): Builds a flat augmented interval tree over committed/reserved memory regions
    /// for O(log N + K) range overlap queries and MEM_FREE hole skipping.
    /// </summary>
    public static FlatAugmentedIntervalTree<MemoryRegionDescriptor> BuildRegionIntervalTree(
        IReadOnlyList<MemoryRegionDescriptor> regions,
        bool excludeFreeRegions = true)
    {
        ArgumentNullException.ThrowIfNull(regions);
        FlatAugmentedIntervalTree<MemoryRegionDescriptor> tree = new FlatAugmentedIntervalTree<MemoryRegionDescriptor>();

        for (int i = 0; i < regions.Count; i++)
        {
            MemoryRegionDescriptor region = regions[i];
            if (excludeFreeRegions && region.State == MEM_FREE)
            {
                continue;
            }

            if (region.RegionSize == 0)
            {
                continue;
            }

            ulong endExclusive = unchecked(region.BaseAddress + region.RegionSize);
            ulong endInclusive = endExclusive > region.BaseAddress
                ? endExclusive - 1UL
                : region.BaseAddress;
            tree.Insert(region.BaseAddress, endInclusive, region);
        }

        tree.ComputeTree();
        return tree;
    }

    public static string FormatProtectionFlags(uint state, uint protect)
    {
        if (state == MEM_FREE)
        {
            return "--- (FREE)";
        }

        if (state == MEM_RESERVE)
        {
            return "--- (RESERVED)";
        }

        uint baseProt = protect & 0xFFU;
        string rwx = baseProt switch
        {
            PAGE_READONLY => "R--",
            PAGE_READWRITE => "RW-",
            PAGE_WRITECOPY => "RWC",
            PAGE_EXECUTE => "--X",
            PAGE_EXECUTE_READ => "R-X",
            PAGE_EXECUTE_READWRITE => "RWX",
            PAGE_EXECUTE_WRITECOPY => "RWXC",
            _ => "---"
        };

        if ((protect & PAGE_GUARD) != 0)
        {
            return rwx + "+G";
        }

        return rwx;
    }

    public static string ClassifyRegionSemantics(uint state, uint protect, uint type, string? moduleName)
    {
        if (state == MEM_FREE)
        {
            return "UnallocatedFree";
        }

        if (state == MEM_RESERVE)
        {
            return "ReservedUncommitted";
        }

        if ((protect & PAGE_GUARD) != 0)
        {
            return "StackGuardPage";
        }

        uint baseProt = protect & 0xFFU;
        if (baseProt == PAGE_EXECUTE_READWRITE || baseProt == PAGE_EXECUTE_WRITECOPY)
        {
            return "ExecutableWritableJitOrHookTrampoline";
        }

        if (type == MEM_IMAGE || !string.IsNullOrWhiteSpace(moduleName))
        {
            bool isExecutable = baseProt == PAGE_EXECUTE || baseProt == PAGE_EXECUTE_READ;
            return isExecutable ? "ImageCodeSection" : "ImageDataSection";
        }

        if (type == MEM_MAPPED)
        {
            return "MappedViewOrSharedMemory";
        }

        return "PrivateHeapOrStack";
    }

    /// <summary>
    /// Decodes a 64-bit Windows PEB RTL_USER_PROCESS_PARAMETERS block to extract the CommandLine
    /// UNICODE_STRING (Length at +0x70, MaximumLength at +0x72, Buffer pointer at +0x78 on x64).
    /// Can be used against either a live ReadProcessMemory callback or a minidump memory reader.
    /// </summary>
    public static bool TryExtractCommandLineFromProcessParameters(
        ReadOnlySpan<byte> rtlUserProcessParametersX64,
        Func<ulong, int, byte[]?> memoryReader,
        out string commandLine)
    {
        commandLine = string.Empty;
        ArgumentNullException.ThrowIfNull(memoryReader);

        // On x64 Windows, RTL_USER_PROCESS_PARAMETERS.CommandLine is a UNICODE_STRING at offset 0x70:
        // +0x70: USHORT Length (bytes)
        // +0x72: USHORT MaximumLength (bytes)
        // +0x78: ULONGLONG Buffer
        if (rtlUserProcessParametersX64.Length < 0x80)
        {
            return false;
        }

        ushort byteLength = MemoryMarshal.Read<ushort>(rtlUserProcessParametersX64.Slice(0x70, 2));
        ulong bufferAddress = MemoryMarshal.Read<ulong>(rtlUserProcessParametersX64.Slice(0x78, 8));

        if (byteLength == 0 || byteLength > 32768 || bufferAddress == 0)
        {
            return false;
        }

        byte[]? rawChars = memoryReader(bufferAddress, byteLength);
        if (rawChars == null || rawChars.Length < byteLength)
        {
            return false;
        }

        commandLine = Encoding.Unicode.GetString(rawChars, 0, byteLength).TrimEnd('\0');
        return true;
    }

    /// <summary>
    /// Attempts to read the command line of a live Windows x64 process by querying
    /// NtQueryInformationProcess(ProcessBasicInformation) -> PebBaseAddress + 0x20 -> ProcessParameters + 0x70.
    /// Fails safely without throwing if the process is inaccessible or non-Windows.
    /// </summary>
    public static bool TryReadLiveProcessCommandLine(int processId, out string commandLine, ILogger? logger = null)
    {
        commandLine = string.Empty;
        ILogger activeLogger = logger ?? s_logger;

        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return false;
        }

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint PROCESS_VM_READ = 0x0010;

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)processId);
        if (hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            PROCESS_BASIC_INFORMATION pbi = default;
            int status = NtQueryInformationProcess(
                hProcess,
                0,
                ref pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out int _);

            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
            {
                return false;
            }

            // PEB + 0x20 holds RTL_USER_PROCESS_PARAMETERS* on x64
            ulong pebAddr = (ulong)pbi.PebBaseAddress.ToInt64();
            byte[]? procParamsPtrBytes = ReadProcessBytes(hProcess, pebAddr + 0x20UL, 8);
            if (procParamsPtrBytes == null)
            {
                return false;
            }

            ulong procParamsAddr = BitConverter.ToUInt64(procParamsPtrBytes, 0);
            if (procParamsAddr == 0)
            {
                return false;
            }

            byte[]? procParamsHeader = ReadProcessBytes(hProcess, procParamsAddr, 0x80);
            if (procParamsHeader == null)
            {
                return false;
            }

            bool ok = TryExtractCommandLineFromProcessParameters(
                procParamsHeader,
                (ulong addr, int len) => ReadProcessBytes(hProcess, addr, len),
                out commandLine);

            if (ok)
            {
                activeLogger.LogDebug("[ProcessMemoryMapInspector] pid={Pid} cmdLine={CmdLine}", processId, commandLine);
            }

            return ok;
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
    }

    private static byte[]? ReadProcessBytes(IntPtr hProcess, ulong address, int length)
    {
        if (length <= 0 || address == 0)
        {
            return null;
        }

        byte[] buffer = new byte[length];
        bool ok = ReadProcessMemory(hProcess, (nuint)address, buffer, (nuint)length, out nuint bytesRead);
        if (!ok || (int)bytesRead < length)
        {
            return null;
        }

        return buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        nuint lpBaseAddress,
        [Out] byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);
}
