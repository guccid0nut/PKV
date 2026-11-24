// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using COMPEL.Configuration;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Encapsulates CPU topology categorization distinguishing Performance Cores from Efficiency Cores,
///     physical cores from SMT siblings, AMD 3D V-Cache CCDs, and NUMA nodes across Windows and Linux platforms.
/// </summary>
public sealed record CpuTopologyInfo(
    IReadOnlyList<int> PCores,
    IReadOnlyList<int> ECores,
    bool HasHybridArchitecture,
    int PrimaryNumaNode,
    IReadOnlyList<int>? PhysicalPCores = null,
    IReadOnlyList<int>? SmtSiblings = null,
    IReadOnlyList<int>? V3DCacheCores = null)
{
    /// <summary>
    ///     Physical Performance Cores (excluding hyper-threading / SMT siblings).
    /// </summary>
    public IReadOnlyList<int> PhysicalPCores { get; init; } = PhysicalPCores ?? PCores;

    /// <summary>
    ///     Hyper-threading / SMT secondary logical processor threads.
    /// </summary>
    public IReadOnlyList<int> SmtSiblings { get; init; } = SmtSiblings ?? [];

    /// <summary>
    ///     Processor cores residing on large L3 cache domains (AMD 3D V-Cache CCD0).
    /// </summary>
    public IReadOnlyList<int> V3DCacheCores { get; init; } = V3DCacheCores ?? [];

    /// <summary>
    ///     Combined 64-bit affinity bitmask for all Performance Cores.
    /// </summary>
    public long AggregatePCoreMask => PCores.Where(c => c is >= 0 and < 64).Aggregate(0L, (m, c) => m | (1L << c));

    /// <summary>
    ///     Combined 64-bit affinity bitmask for all Efficiency Cores.
    /// </summary>
    public long AggregateECoreMask => ECores.Where(c => c is >= 0 and < 64).Aggregate(0L, (m, c) => m | (1L << c));

    /// <summary>
    ///     Combined 64-bit affinity bitmask for all physical Performance Cores.
    /// </summary>
    public long AggregatePhysicalPCoreMask => PhysicalPCores.Where(c => c is >= 0 and < 64).Aggregate(0L, (m, c) => m | (1L << c));

    /// <summary>
    ///     Combined 64-bit affinity bitmask for all AMD 3D V-Cache cores.
    /// </summary>
    public long AggregateV3DCacheMask => V3DCacheCores.Where(c => c is >= 0 and < 64).Aggregate(0L, (m, c) => m | (1L << c));

    /// <summary>
    ///     Full bitmask across all detected logical processor cores.
    /// </summary>
    public long FullMask => (1L << Math.Min(64, Math.Max(1, PCores.Count + ECores.Count))) - 1;
}

/// <summary>
///     Encapsulates dedicated physical CPU core and NUMA memory pinning for a dedicated server worker instance.
/// </summary>
public sealed record WorkerCpuPinning(
    int PrimaryPhysicalCore,
    int NumaNode,
    long AffinityMask,
    string CpusSetString,
    string MemsSetString);

/// <summary>
///     Analyzes host processor topology to identify and prioritize Performance Cores (P-Cores),
///     physical cores over SMT siblings, and AMD 3D V-Cache domains (CCD0) for tournament-grade simulation,
///     while steering background tasks to Efficiency Cores (E-Cores) with Windows 11 EcoQoS for zero-disruption hosting.
/// </summary>
public static class CpuTopologyResolver
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumaProcessorNode(byte processor, out byte nodeNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumaNodeProcessorMask(byte node, out ulong processorMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private static readonly Lazy<CpuTopologyInfo> s_topologyInfo = new(DetectTopologyInfo);

    /// <summary>
    ///     Returns cached CPU topology categorization distinguishing P-cores, physical cores, E-cores, and cache topology.
    /// </summary>
    public static CpuTopologyInfo GetTopologyInfo() => s_topologyInfo.Value;

    /// <summary>
    ///     Returns the logical core indices allowed for the current process by OS container constraints
    ///     (such as Docker or Kubernetes CPU quotas).
    /// </summary>
    public static IReadOnlyList<int> GetContainerAllowedCores()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                nint affinity = Process.GetCurrentProcess().ProcessorAffinity;
                if (affinity != 0)
                {
                    List<int> allowed = [];
                    long mask = affinity;
                    for (int i = 0; i < Math.Min(64, Environment.ProcessorCount); i++)
                    {
                        if ((mask & (1L << i)) != 0) allowed.Add(i);
                    }
                    if (allowed.Count > 0) return allowed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] ProcessorAffinity query fallback: {ex.Message}");
            }
        }

        return [.. Enumerable.Range(0, Environment.ProcessorCount)];
    }

    /// <summary>
    ///     Returns an ordered list of logical processor indices with Performance Cores (P-Cores) prioritized first.
    /// </summary>
    public static int[] GetOptimizedProcessorOrder()
    {
        CpuTopologyInfo info = GetTopologyInfo();
        return [.. info.PCores, .. info.ECores];
    }

    /// <summary>
    ///     Returns the optimal processor cores ordered by performance capability:
    ///     1. AMD 3D V-Cache physical cores (96 MB L3 cache).
    ///     2. Intel / AMD physical Performance Cores (skipping SMT siblings).
    ///     3. AMD 3D V-Cache SMT siblings.
    ///     4. Other SMT siblings.
    ///     5. Efficiency Cores.
    ///     All selections are intersected with the container's allowed CPU set to prevent EINVAL.
    /// </summary>
    public static int[] GetBestAvailableProcessors(int count = -1)
    {
        CpuTopologyInfo info = GetTopologyInfo();
        IReadOnlyList<int> containerAllowed = GetContainerAllowedCores();
        HashSet<int> allowedSet = [.. containerAllowed];
        List<int> ordered = [];

        // 1. AMD 3D V-Cache physical cores
        foreach (int core in info.V3DCacheCores.Where(c => info.PhysicalPCores.Contains(c) && allowedSet.Contains(c)))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 2. Standard physical P-cores
        foreach (int core in info.PhysicalPCores.Where(allowedSet.Contains))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 3. AMD 3D V-Cache SMT siblings
        foreach (int core in info.V3DCacheCores.Where(c => info.SmtSiblings.Contains(c) && allowedSet.Contains(c)))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 4. Other SMT siblings
        foreach (int core in info.SmtSiblings.Where(allowedSet.Contains))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 5. Efficiency Cores
        foreach (int core in info.ECores.Where(allowedSet.Contains))
            if (!ordered.Contains(core)) ordered.Add(core);

        // Fallback: any remaining container-allowed cores
        foreach (int core in containerAllowed)
            if (!ordered.Contains(core)) ordered.Add(core);

        if (count <= 0 || count >= ordered.Count)
            return [.. ordered];

        return [.. ordered.Take(count)];
    }

    /// <summary>
    ///     Returns processor cores suited for background tasks to produce 0% disruption to foreground gaming:
    ///     1. Efficiency Cores (E-Cores).
    ///     2. Upper-half secondary cores on non-hybrid CPUs (leaving cores 0..N/2 free for games).
    ///     3. SMT siblings.
    /// </summary>
    public static int[] GetBackgroundProcessors(int count = -1)
    {
        CpuTopologyInfo info = GetTopologyInfo();
        IReadOnlyList<int> containerAllowed = GetContainerAllowedCores();
        HashSet<int> allowedSet = [.. containerAllowed];
        List<int> ordered = [];

        // 1. Efficiency Cores first
        foreach (int core in info.ECores.Where(allowedSet.Contains))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 2. On non-hybrid architectures, use the upper half of processor cores
        int halfPoint = Math.Max(1, Environment.ProcessorCount / 2);
        foreach (int core in containerAllowed.Where(c => c >= halfPoint))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 3. SMT siblings
        foreach (int core in info.SmtSiblings.Where(allowedSet.Contains))
            if (!ordered.Contains(core)) ordered.Add(core);

        // 4. Fallback to any remaining allowed cores
        foreach (int core in containerAllowed)
            if (!ordered.Contains(core)) ordered.Add(core);

        if (count <= 0 || count >= ordered.Count)
            return [.. ordered];

        return [.. ordered.Take(count)];
    }

    /// <summary>
    ///     Calculates a dedicated CPU core affinity bitmask for the specified instance index,
    ///     allocating from the optimal processor list based on priority mode.
    /// </summary>
    public static nint CalculateAffinityMask(
        int instanceIndex,
        int totalInstances,
        HostPriorityMode priorityMode,
        CpuAffinityMode mode = CpuAffinityMode.PCoresOnly,
        string? explicitMaskHex = null)
    {
        if (mode == CpuAffinityMode.ExplicitMask && !string.IsNullOrWhiteSpace(explicitMaskHex))
        {
            string cleanHex = explicitMaskHex.Trim();
            if (cleanHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                cleanHex = cleanHex[2..];

            if (long.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out long explicitMask) && explicitMask != 0)
                return (nint)explicitMask;
        }

        IReadOnlyList<int> targetCores = priorityMode switch
        {
            HostPriorityMode.HighPriority => GetBestAvailableProcessors(),
            HostPriorityMode.Background => GetBackgroundProcessors(),
            _ => mode == CpuAffinityMode.AllCores ? GetOptimizedProcessorOrder() : GetBestAvailableProcessors()
        };

        if (targetCores.Count == 0) return 1;

        totalInstances = Math.Max(1, totalInstances);
        instanceIndex = Math.Max(0, instanceIndex);

        int coresPerInstance = Math.Max(1, targetCores.Count / totalInstances);
        int startIndex = (instanceIndex * coresPerInstance) % targetCores.Count;

        long mask = 0;
        for (int i = 0; i < coresPerInstance; i++)
        {
            int core = targetCores[(startIndex + i) % targetCores.Count];
            if (core is >= 0 and < 64) mask |= 1L << core;
        }

        if (mask == 0) mask = 1L << (targetCores[0] % 64);

        return (nint)mask;
    }

    /// <summary>
    ///     Calculates dedicated physical CPU core and NUMA memory pinning for a dedicated server worker instance,
    ///     guaranteeing non-overlapping physical cores for tournament simulation while respecting desktop background priorities.
    /// </summary>
    public static WorkerCpuPinning GetDedicatedWorkerPinning(
        int instanceIndex,
        int totalInstances,
        HostPriorityMode priorityMode,
        CpuAffinityMode mode = CpuAffinityMode.PCoresOnly,
        string? explicitMaskHex = null)
    {
        if (mode == CpuAffinityMode.ExplicitMask && !string.IsNullOrWhiteSpace(explicitMaskHex))
        {
            string cleanHex = explicitMaskHex.Trim();
            if (cleanHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                cleanHex = cleanHex[2..];

            if (long.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out long explicitMask) && explicitMask != 0)
            {
                int primary = GetPrimaryCoreFromMask((nint)explicitMask);
                int numa = GetNumaNodeForProcessor(primary);
                List<int> maskCores = [];
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((explicitMask & (1L << bit)) != 0)
                        maskCores.Add(bit);
                }
                string cpus = maskCores.Count > 0 ? string.Join(",", maskCores) : primary.ToString();
                return new WorkerCpuPinning(primary, numa, explicitMask, cpus, numa.ToString());
            }
        }

        IReadOnlyList<int> targetCores = priorityMode switch
        {
            HostPriorityMode.HighPriority => GetBestAvailableProcessors(),
            HostPriorityMode.Background => GetBackgroundProcessors(),
            _ => mode == CpuAffinityMode.AllCores ? GetOptimizedProcessorOrder() : GetBestAvailableProcessors()
        };

        if (targetCores.Count == 0)
        {
            targetCores = [0];
        }

        totalInstances = Math.Max(1, totalInstances);
        instanceIndex = Math.Max(0, instanceIndex);

        int coresPerInstance = Math.Max(1, targetCores.Count / totalInstances);
        int startIndex = (instanceIndex * coresPerInstance) % targetCores.Count;

        List<int> assignedCores = [];
        long mask = 0;
        for (int i = 0; i < coresPerInstance; i++)
        {
            int core = targetCores[(startIndex + i) % targetCores.Count];
            assignedCores.Add(core);
            if (core is >= 0 and < 64) mask |= 1L << core;
        }

        if (mask == 0)
        {
            int fallbackCore = targetCores[0];
            assignedCores.Add(fallbackCore);
            mask = 1L << (fallbackCore % 64);
        }

        int primaryPhysicalCore = assignedCores[0];
        int numaNode = GetNumaNodeForProcessor(primaryPhysicalCore);
        string cpusSetString = string.Join(",", assignedCores.Distinct());
        string memsSetString = numaNode.ToString();

        return new WorkerCpuPinning(primaryPhysicalCore, numaNode, mask, cpusSetString, memsSetString);
    }

    /// <summary>
    ///     Backwards-compatible legacy overload for CalculateAffinityMask allocating strictly from PCores.
    /// </summary>
    public static nint CalculateAffinityMask(
        int instanceIndex,
        int totalInstances = 1,
        CpuAffinityMode mode = CpuAffinityMode.PCoresOnly,
        string? explicitMaskHex = null)
    {
        if (mode == CpuAffinityMode.ExplicitMask && !string.IsNullOrWhiteSpace(explicitMaskHex))
        {
            string cleanHex = explicitMaskHex.Trim();
            if (cleanHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                cleanHex = cleanHex[2..];

            if (long.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out long explicitMask) && explicitMask != 0)
                return (nint)explicitMask;
        }

        CpuTopologyInfo topology = GetTopologyInfo();
        IReadOnlyList<int> targetCores = mode switch
        {
            CpuAffinityMode.AllCores => GetOptimizedProcessorOrder(),
            _ => topology.PCores.Count > 0 ? topology.PCores : GetOptimizedProcessorOrder()
        };

        if (targetCores.Count == 0) return 1;

        totalInstances = Math.Max(1, totalInstances);
        instanceIndex = Math.Max(0, instanceIndex);

        int coresPerInstance = Math.Max(1, targetCores.Count / totalInstances);
        int startIndex = (instanceIndex * coresPerInstance) % targetCores.Count;

        long mask = 0;
        for (int i = 0; i < coresPerInstance; i++)
        {
            int core = targetCores[(startIndex + i) % targetCores.Count];
            if (core is >= 0 and < 64) mask |= 1L << core;
        }

        if (mask == 0) mask = 1L << (targetCores[0] % 64);

        return (nint)mask;
    }

    /// <summary>
    ///     Returns the aggregate bitmask covering all Performance Cores on the host.
    /// </summary>
    public static nint GetAggregatePcoreMask()
    {
        CpuTopologyInfo topology = GetTopologyInfo();
        long mask = topology.AggregatePCoreMask;
        if (mask == 0)
        {
            int count = Math.Clamp(Environment.ProcessorCount, 1, 64);
            mask = count == 64 ? -1L : (1L << count) - 1;
        }
        return (nint)mask;
    }

    /// <summary>
    ///     Returns the aggregate bitmask for the best processors available on the host.
    /// </summary>
    public static nint GetAggregateBestProcessorsMask()
    {
        int[] cores = GetBestAvailableProcessors();
        long mask = cores.Where(c => c is >= 0 and < 64).Aggregate(0L, (m, c) => m | (1L << c));
        if (mask == 0) return GetAggregatePcoreMask();
        return (nint)mask;
    }

    /// <summary>
    ///     Resolves the NUMA node index for a given logical processor core.
    /// </summary>
    public static int GetNumaNodeForProcessor(int logicalProcessorIndex)
    {
        if (OperatingSystem.IsWindows() && logicalProcessorIndex >= 0 && logicalProcessorIndex < 256)
        {
            try
            {
                if (GetNumaProcessorNode((byte)logicalProcessorIndex, out byte nodeNumber)) return nodeNumber;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] GetNumaProcessorNode fallback: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsLinux() && logicalProcessorIndex >= 0)
        {
            try
            {
                string cpuDir = $"/sys/devices/system/cpu/cpu{logicalProcessorIndex}";
                if (Directory.Exists(cpuDir))
                {
                    foreach (string dir in Directory.GetDirectories(cpuDir, "node*"))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (dirName.StartsWith("node", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(dirName[4..], out int nodeIndex))
                        {
                            return nodeIndex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] Linux NUMA sysfs fallback: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    ///     Retrieves the affinity mask for all logical processors residing on a specific NUMA node.
    /// </summary>
    public static nint GetNumaNodeAffinityMask(int numaNodeIndex)
    {
        if (OperatingSystem.IsWindows() && numaNodeIndex >= 0 && numaNodeIndex < 64)
            try
            {
                if (GetNumaNodeProcessorMask((byte)numaNodeIndex, out ulong processorMask) && processorMask != 0)
                    return (nint)(long)processorMask;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] GetNumaNodeProcessorMask fallback: {ex.Message}");
            }

        return -1;
    }

    /// <summary>
    ///     Configures Windows 11 EcoQoS (Efficiency Mode / Execution Speed Throttling) for a process.
    /// </summary>
    public static bool SetProcessEcoQoS(IntPtr processHandle, bool enable)
    {
        if (!OperatingSystem.IsWindows() || processHandle == IntPtr.Zero) return false;
        try
        {
            PROCESS_POWER_THROTTLING_STATE throttling = new()
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            return SetProcessInformation(
                processHandle,
                ProcessPowerThrottling,
                ref throttling,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Applies dedicated CPU core affinity mask, NUMA node assignment, and dual-channel process priority.
    /// </summary>
    public static bool AssignProcessAffinity(
        Process process,
        int instanceIndex,
        int totalInstances,
        HostPriorityMode priorityMode,
        CpuAffinityMode mode = CpuAffinityMode.PCoresOnly,
        string? explicitMask = null,
        ILogger? logger = null)
    {
        if (process == null) return false;

        try
        {
            if (process.HasExited) return false;

            nint mask = CalculateAffinityMask(instanceIndex, totalInstances, priorityMode, mode, explicitMask);
            if (mask != 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                process.ProcessorAffinity = mask;

                int primaryCore = GetPrimaryCoreFromMask(mask);
                int numaNode = GetNumaNodeForProcessor(primaryCore);
                CpuTopologyInfo topology = GetTopologyInfo();
                bool isPCore = topology.PCores.Contains(primaryCore);
                bool is3DCache = topology.V3DCacheCores.Contains(primaryCore);

                // Dual-channel process priority application with graceful capability fallback
                try
                {
                    if (priorityMode == HostPriorityMode.HighPriority)
                    {
                        process.PriorityClass = ProcessPriorityClass.High;
                        if (OperatingSystem.IsWindows())
                            SetProcessEcoQoS(process.Handle, enable: false);
                    }
                    else
                    {
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                        if (OperatingSystem.IsWindows())
                            SetProcessEcoQoS(process.Handle, enable: true);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[CpuTopologyResolver] Could not set ProcessPriorityClass directly on PID {Pid} (e.g. unprivileged user/container).", process.Id);
                }

                logger?.LogInformation(
                    "[CpuTopologyResolver] Assigned CPU Affinity Mask 0x{Mask:X} (Primary Core: {Core}, P-Core: {IsPCore}, 3D V-Cache: {Is3DCache}, NUMA: {Numa}, Priority: {Priority}) to Process PID {Pid} (Instance {Instance}/{Total})",
                    (long)mask, primaryCore, isPCore, is3DCache, numaNode, priorityMode, process.Id, instanceIndex + 1, totalInstances);

                return true;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[CpuTopologyResolver] Failed to assign processor affinity to PID {Pid}", process.Id);
        }

        return false;
    }

    /// <summary>
    ///     Backwards-compatible overload for AssignProcessAffinity.
    /// </summary>
    public static bool AssignProcessAffinity(
        Process process,
        int instanceIndex = 0,
        int totalInstances = 1,
        CpuAffinityMode mode = CpuAffinityMode.PCoresOnly,
        string? explicitMask = null,
        ILogger? logger = null) =>
        AssignProcessAffinity(process, instanceIndex, totalInstances, HostPriorityMode.Background, mode, explicitMask, logger);

    /// <summary>
    ///     Overload preserving backwards compatibility for callers without mode parameters.
    /// </summary>
    public static bool AssignProcessAffinity(
        Process process,
        int instanceIndex,
        int totalInstances,
        ILogger? logger) =>
        AssignProcessAffinity(process, instanceIndex, totalInstances, HostPriorityMode.Background, CpuAffinityMode.PCoresOnly, explicitMask: null, logger);

    /// <summary>
    ///     Extracts the lowest set logical core index from an affinity mask.
    /// </summary>
    public static int GetPrimaryCoreFromMask(nint mask)
    {
        long m = mask;
        for (int i = 0; i < 64; i++)
            if ((m & (1L << i)) != 0)
                return i;

        return 0;
    }

    private static CpuTopologyInfo DetectTopologyInfo()
    {
        int logicalCoreCount = Environment.ProcessorCount;
        if (logicalCoreCount <= 1)
        {
            return new CpuTopologyInfo([0], [], false, 0, [0], [], []);
        }

        if (OperatingSystem.IsWindows())
            try
            {
                CpuTopologyInfo? winInfo = ResolveWindowsTopology(logicalCoreCount);
                if (winInfo != null && winInfo.PCores.Count > 0) return winInfo;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] ResolveWindowsTopology fallback: {ex.Message}");
            }
        else if (OperatingSystem.IsLinux())
            try
            {
                CpuTopologyInfo? linuxInfo = ResolveLinuxTopology(logicalCoreCount);
                if (linuxInfo != null && linuxInfo.PCores.Count > 0) return linuxInfo;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] ResolveLinuxTopology fallback: {ex.Message}");
            }

        // Uniform symmetric fallback: all cores are P-Cores
        return new CpuTopologyInfo(
            [.. Enumerable.Range(0, logicalCoreCount)],
            [],
            false,
            0,
            [.. Enumerable.Range(0, logicalCoreCount)],
            [],
            []);
    }

    private static CpuTopologyInfo? ResolveWindowsTopology(int totalLogicalProcessors)
    {
        int bufferLength = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref bufferLength);
        if (bufferLength == 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref bufferLength)) return null;

            List<(int LogicalIndex, byte EfficiencyClass, bool IsPhysicalPrimary)> coreList = [];
            int offset = 0;
            int currentLogicalIndex = 0;

            while (offset < bufferLength && currentLogicalIndex < totalLogicalProcessors)
            {
                IntPtr currentPtr = IntPtr.Add(buffer, offset);
                int relationship = Marshal.ReadInt32(currentPtr, 0);
                int structSize = Marshal.ReadInt32(currentPtr, 4);

                if (structSize <= 0) break;

                if (relationship == RelationProcessorCore)
                {
                    byte flags = Marshal.ReadByte(currentPtr, 8);
                    byte efficiencyClass = Marshal.ReadByte(currentPtr, 9);
                    bool isHyperthreaded = (flags & 1) != 0; // LTP_PC_SMT

                    // Primary physical core thread
                    coreList.Add((currentLogicalIndex, efficiencyClass, true));
                    currentLogicalIndex++;

                    // SMT secondary sibling thread
                    if (isHyperthreaded && currentLogicalIndex < totalLogicalProcessors)
                    {
                        coreList.Add((currentLogicalIndex, efficiencyClass, false));
                        currentLogicalIndex++;
                    }
                }

                offset += structSize;
            }

            if (coreList.Count == 0) return null;

            byte maxClass = coreList.Max(c => c.EfficiencyClass);
            byte minClass = coreList.Min(c => c.EfficiencyClass);
            bool isHybrid = maxClass > minClass;

            List<int> pCores = coreList
                .Where(c => !isHybrid || c.EfficiencyClass == maxClass)
                .OrderBy(c => c.LogicalIndex)
                .Select(c => c.LogicalIndex)
                .ToList();

            List<int> physicalPCores = coreList
                .Where(c => (!isHybrid || c.EfficiencyClass == maxClass) && c.IsPhysicalPrimary)
                .OrderBy(c => c.LogicalIndex)
                .Select(c => c.LogicalIndex)
                .ToList();

            List<int> smtSiblings = coreList
                .Where(c => !c.IsPhysicalPrimary)
                .OrderBy(c => c.LogicalIndex)
                .Select(c => c.LogicalIndex)
                .ToList();

            List<int> eCores = isHybrid
                ? coreList
                    .Where(c => c.EfficiencyClass < maxClass)
                    .OrderBy(c => c.LogicalIndex)
                    .Select(c => c.LogicalIndex)
                    .ToList()
                : [];

            HashSet<int> v3dCacheCores = DetectV3DCacheProcessorsWindows();

            int primaryNuma = pCores.Count > 0 ? GetNumaNodeForProcessor(pCores[0]) : 0;
            return new CpuTopologyInfo(
                pCores,
                eCores,
                isHybrid,
                primaryNuma,
                physicalPCores,
                smtSiblings,
                [.. v3dCacheCores]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HashSet<int> DetectV3DCacheProcessorsWindows()
    {
        HashSet<int> v3dProcessors = [];
        int bufferLength = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref bufferLength);
        if (bufferLength == 0) return v3dProcessors;

        IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref bufferLength))
                return v3dProcessors;

            List<(uint Size, long Mask)> l3Caches = [];
            int offset = 0;
            while (offset < bufferLength)
            {
                IntPtr currentPtr = IntPtr.Add(buffer, offset);
                int relationship = Marshal.ReadInt32(currentPtr, 0);
                int structSize = Marshal.ReadInt32(currentPtr, 4);
                if (structSize <= 0) break;

                if (relationship == RelationCache)
                {
                    byte level = Marshal.ReadByte(currentPtr, 8);
                    uint cacheSize = (uint)Marshal.ReadInt32(currentPtr, 12);
                    // In Win32 CACHE_RELATIONSHIP, GroupMask is at offset 40
                    long groupMask = Marshal.ReadInt64(currentPtr, 40);

                    if (level == 3 && cacheSize > 0 && groupMask != 0)
                    {
                        l3Caches.Add((cacheSize, groupMask));
                    }
                }

                offset += structSize;
            }

            if (l3Caches.Count > 1)
            {
                uint maxSize = l3Caches.Max(c => c.Size);
                uint minSize = l3Caches.Min(c => c.Size);
                // Asymmetric L3 cache across CCDs (e.g. AMD Ryzen 7950X3D / 9950X3D: 96MB vs 32MB)
                if (maxSize > minSize)
                {
                    foreach (var cache in l3Caches.Where(c => c.Size == maxSize))
                    {
                        for (int bit = 0; bit < 64; bit++)
                            if ((cache.Mask & (1L << bit)) != 0)
                                v3dProcessors.Add(bit);
                    }
                }
            }
            else if (l3Caches.Count == 1 && l3Caches[0].Size >= 64 * 1024 * 1024)
            {
                // Single-CCD with 3D V-Cache (e.g. Ryzen 7800X3D / 9800X3D)
                for (int bit = 0; bit < 64; bit++)
                    if ((l3Caches[0].Mask & (1L << bit)) != 0)
                        v3dProcessors.Add(bit);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CpuTopologyResolver] DetectV3DCacheProcessorsWindows fallback: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return v3dProcessors;
    }

    private static CpuTopologyInfo? ResolveLinuxTopology(int totalLogicalProcessors)
    {
        List<(int LogicalIndex, long MaxFreqKHz)> coreFrequencies = [];

        for (int i = 0; i < totalLogicalProcessors; i++)
        {
            string freqPath = $"/sys/devices/system/cpu/cpu{i}/cpufreq/cpuinfo_max_freq";
            long maxFreq = 0;
            if (File.Exists(freqPath))
            {
                string text = File.ReadAllText(freqPath).Trim();
                _ = long.TryParse(text, out maxFreq);
            }

            coreFrequencies.Add((i, maxFreq));
        }

        if (!coreFrequencies.Any(c => c.MaxFreqKHz > 0)) return null;

        long maxFreqKHz = coreFrequencies.Max(c => c.MaxFreqKHz);
        long minFreqKHz = coreFrequencies.Min(c => c.MaxFreqKHz);
        bool isHybrid = maxFreqKHz > 0 && minFreqKHz > 0 && minFreqKHz <= (long)(maxFreqKHz * 0.85);

        List<int> pCores = coreFrequencies
            .Where(c => !isHybrid || c.MaxFreqKHz >= (long)(maxFreqKHz * 0.90))
            .OrderBy(c => c.LogicalIndex)
            .Select(c => c.LogicalIndex)
            .ToList();

        List<int> eCores = isHybrid
            ? coreFrequencies
                .Where(c => c.MaxFreqKHz < (long)(maxFreqKHz * 0.90))
                .OrderBy(c => c.LogicalIndex)
                .Select(c => c.LogicalIndex)
                .ToList()
            : [];

        // Distinguish physical cores from SMT siblings via Linux sysfs thread_siblings_list
        List<int> physicalPCores = [];
        List<int> smtSiblings = [];
        HashSet<int> seenPhysical = [];

        for (int i = 0; i < totalLogicalProcessors; i++)
        {
            string siblingsPath = $"/sys/devices/system/cpu/cpu{i}/topology/thread_siblings_list";
            if (File.Exists(siblingsPath))
            {
                string line = File.ReadAllText(siblingsPath).Trim();
                string[] parts = line.Split(',', '-');
                if (parts.Length > 0 && int.TryParse(parts[0], out int primaryCpu))
                {
                    if (primaryCpu == i && seenPhysical.Add(i))
                    {
                        if (pCores.Contains(i)) physicalPCores.Add(i);
                    }
                    else
                    {
                        smtSiblings.Add(i);
                    }
                }
            }
        }

        if (physicalPCores.Count == 0) physicalPCores = [.. pCores];

        HashSet<int> v3dCacheCores = DetectV3DCacheProcessorsLinux(totalLogicalProcessors);

        int primaryNuma = pCores.Count > 0 ? GetNumaNodeForProcessor(pCores[0]) : 0;
        return new CpuTopologyInfo(
            pCores,
            eCores,
            isHybrid,
            primaryNuma,
            physicalPCores,
            smtSiblings,
            [.. v3dCacheCores]);
    }

    private static HashSet<int> DetectV3DCacheProcessorsLinux(int totalLogicalProcessors)
    {
        HashSet<int> v3dProcessors = [];
        List<(int Cpu, long L3SizeBytes)> l3Sizes = [];

        for (int i = 0; i < totalLogicalProcessors; i++)
        {
            string sizePath = $"/sys/devices/system/cpu/cpu{i}/cache/index3/size";
            if (File.Exists(sizePath))
            {
                string text = File.ReadAllText(sizePath).Trim().ToUpperInvariant();
                long bytes = 0;
                if (text.EndsWith('K'))
                {
                    if (long.TryParse(text[..^1], out bytes)) bytes *= 1024;
                }
                else if (text.EndsWith('M'))
                {
                    if (long.TryParse(text[..^1], out bytes)) bytes *= 1024 * 1024;
                }
                else
                {
                    _ = long.TryParse(text, out bytes);
                }

                l3Sizes.Add((i, bytes));
            }
        }

        if (l3Sizes.Count > 0)
        {
            long maxSize = l3Sizes.Max(c => c.L3SizeBytes);
            long minSize = l3Sizes.Min(c => c.L3SizeBytes);
            if (maxSize > minSize && maxSize >= 64L * 1024 * 1024)
            {
                foreach (var item in l3Sizes.Where(c => c.L3SizeBytes == maxSize))
                    v3dProcessors.Add(item.Cpu);
            }
        }

        return v3dProcessors;
    }
}
