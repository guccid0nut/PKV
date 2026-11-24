// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Tests;

public sealed class CpuTopologyResolverTests
{
    [Fact]
    public void CpuTopologyResolver_GetOptimizedProcessorOrder_ReturnsValidCoreIndices()
    {
        int[] order = CpuTopologyResolver.GetOptimizedProcessorOrder();

        Assert.NotNull(order);
        Assert.Equal(Environment.ProcessorCount, order.Length);

        // Every core index from 0 to ProcessorCount - 1 must be present exactly once
        HashSet<int> coreSet = [.. order];
        Assert.Equal(Environment.ProcessorCount, coreSet.Count);

        for (int i = 0; i < Environment.ProcessorCount; i++) Assert.Contains(i, coreSet);
    }

    [Fact]
    public void CalculateAffinityMask_ValidInstances_ReturnsNonZeroDedicatedMasks()
    {
        nint mask1 = CpuTopologyResolver.CalculateAffinityMask(0, 4);
        nint mask2 = CpuTopologyResolver.CalculateAffinityMask(1, 4);

        Assert.NotEqual(0, mask1);
        Assert.NotEqual(0, mask2);

        if (Environment.ProcessorCount >= 2)
            // With multiple cores, different instances should have distinct core affinity allocations
            Assert.NotEqual(mask1, mask2);
    }

    [Fact]
    public void CpuTopologyResolver_GetNumaNodeForProcessor_ReturnsValidNodeIndex()
    {
        int numaNode = CpuTopologyResolver.GetNumaNodeForProcessor(0);
        Assert.True(numaNode >= 0);
    }

    [Fact]
    public void CpuTopologyResolver_GetNumaNodeAffinityMask_ReturnsValidMask()
    {
        nint mask = CpuTopologyResolver.GetNumaNodeAffinityMask(0);
        Assert.NotEqual(0, mask);
    }

    [Fact]
    public void AssignProcessAffinity_CurrentProcess_SetsAffinitySuccessfully()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        using Process currentProcess = Process.GetCurrentProcess();
        nint originalAffinity = currentProcess.ProcessorAffinity;
        try
        {
            bool success = CpuTopologyResolver.AssignProcessAffinity(currentProcess);
            if (OperatingSystem.IsWindows()) Assert.True(success);
        }
        finally
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                try { currentProcess.ProcessorAffinity = originalAffinity; } catch { }
            }
        }
    }

    [Fact]
    public void CpuTopologyResolver_GetTopologyInfo_ReturnsValidPcoreAndEcoreLists()
    {
        CpuTopologyInfo info = CpuTopologyResolver.GetTopologyInfo();

        Assert.NotNull(info);
        Assert.NotEmpty(info.PCores);
        Assert.Equal(Environment.ProcessorCount, info.PCores.Count + info.ECores.Count);
        Assert.NotEqual(0L, info.AggregatePCoreMask);
        Assert.True(info.PrimaryNumaNode >= 0);
    }

    [Fact]
    public void CalculateAffinityMask_PCoresOnly_NeverTouchesEcores()
    {
        CpuTopologyInfo info = CpuTopologyResolver.GetTopologyInfo();
        if (!info.HasHybridArchitecture || info.ECores.Count == 0)
        {
            // Non-hybrid CPU has 0 E-cores
            Assert.Empty(info.ECores);
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            nint mask = CpuTopologyResolver.CalculateAffinityMask(i, 8, CpuAffinityMode.PCoresOnly);
            long ecoreOverlap = (long)mask & info.AggregateECoreMask;
            Assert.Equal(0L, ecoreOverlap);
        }
    }

    [Fact]
    public void CalculateAffinityMask_ExplicitMask_ReturnsParsedHex()
    {
        nint mask = CpuTopologyResolver.CalculateAffinityMask(0, 1, CpuAffinityMode.ExplicitMask, "0x00FF");
        Assert.Equal((nint)0x00FF, mask);

        nint maskPlainHex = CpuTopologyResolver.CalculateAffinityMask(0, 1, CpuAffinityMode.ExplicitMask, "F0");
        Assert.Equal((nint)0x00F0, maskPlainHex);
    }

    [Fact]
    public void CpuTopologyResolver_GetAggregatePcoreMask_ReturnsNonZero()
    {
        nint pcoreMask = CpuTopologyResolver.GetAggregatePcoreMask();
        Assert.NotEqual(0, pcoreMask);
    }

    [Fact]
    public void CpuTopologyResolver_GetPrimaryCoreFromMask_ReturnsLowestSetBit()
    {
        Assert.Equal(0, CpuTopologyResolver.GetPrimaryCoreFromMask(1));
        Assert.Equal(1, CpuTopologyResolver.GetPrimaryCoreFromMask(2));
        Assert.Equal(2, CpuTopologyResolver.GetPrimaryCoreFromMask(4));
        Assert.Equal(3, CpuTopologyResolver.GetPrimaryCoreFromMask(8));
        Assert.Equal(2, CpuTopologyResolver.GetPrimaryCoreFromMask(0b1100));
    }

    [Fact]
    public void CpuTopologyResolver_GetBestAvailableProcessors_ReturnsValidCores()
    {
        int[] bestCores = CpuTopologyResolver.GetBestAvailableProcessors();
        Assert.NotNull(bestCores);
        Assert.NotEmpty(bestCores);
        Assert.True(bestCores.Length <= Environment.ProcessorCount);

        // All returned cores must be within valid range
        foreach (int core in bestCores)
        {
            Assert.InRange(core, 0, Environment.ProcessorCount - 1);
        }

        // Test with count parameter
        int[] topTwo = CpuTopologyResolver.GetBestAvailableProcessors(Math.Min(2, Environment.ProcessorCount));
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), topTwo.Length);
    }

    [Fact]
    public void CpuTopologyResolver_GetBackgroundProcessors_ReturnsValidCores()
    {
        int[] bgCores = CpuTopologyResolver.GetBackgroundProcessors();
        Assert.NotNull(bgCores);
        Assert.NotEmpty(bgCores);
        Assert.True(bgCores.Length <= Environment.ProcessorCount);

        foreach (int core in bgCores)
        {
            Assert.InRange(core, 0, Environment.ProcessorCount - 1);
        }
    }

    [Fact]
    public void CalculateAffinityMask_WithHostPriorityModes_ReturnsValidMasks()
    {
        nint highMask = CpuTopologyResolver.CalculateAffinityMask(0, 2, HostPriorityMode.HighPriority);
        nint bgMask = CpuTopologyResolver.CalculateAffinityMask(0, 2, HostPriorityMode.Background);

        Assert.NotEqual(0, highMask);
        Assert.NotEqual(0, bgMask);

        CpuTopologyInfo info = CpuTopologyResolver.GetTopologyInfo();
        if (info.HasHybridArchitecture && info.ECores.Count > 0)
        {
            // In hybrid architecture, background mask should overlap with E-cores
            long bgOverlap = (long)bgMask & info.AggregateECoreMask;
            Assert.NotEqual(0L, bgOverlap);
        }
    }

    [Fact]
    public void CpuTopologyResolver_GetAggregateBestProcessorsMask_ReturnsNonZero()
    {
        nint mask = CpuTopologyResolver.GetAggregateBestProcessorsMask();
        Assert.NotEqual(0, mask);
    }

    [Fact]
    public void CpuTopologyInfo_PhysicalAndSmtSeparation_IsConsistent()
    {
        CpuTopologyInfo info = CpuTopologyResolver.GetTopologyInfo();
        Assert.NotNull(info.PhysicalPCores);
        Assert.NotNull(info.SmtSiblings);

        // Every physical core should be distinct
        HashSet<int> physicalSet = [.. info.PhysicalPCores];
        Assert.Equal(info.PhysicalPCores.Count, physicalSet.Count);

        // Physical P-cores and SMT siblings should be mutually disjoint
        foreach (int smt in info.SmtSiblings)
        {
            Assert.DoesNotContain(smt, physicalSet);
        }
    }

    [Fact]
    public void GetDedicatedWorkerPinning_HighPriority_ReturnsNonOverlappingPinning()
    {
        WorkerCpuPinning pinning0 = CpuTopologyResolver.GetDedicatedWorkerPinning(
            instanceIndex: 0,
            totalInstances: 4,
            priorityMode: HostPriorityMode.HighPriority);

        WorkerCpuPinning pinning1 = CpuTopologyResolver.GetDedicatedWorkerPinning(
            instanceIndex: 1,
            totalInstances: 4,
            priorityMode: HostPriorityMode.HighPriority);

        Assert.NotNull(pinning0);
        Assert.NotNull(pinning1);
        Assert.NotEqual(0L, pinning0.AffinityMask);
        Assert.NotEqual(0L, pinning1.AffinityMask);
        Assert.NotEmpty(pinning0.CpusSetString);
        Assert.NotEmpty(pinning1.CpusSetString);
        Assert.True(pinning0.NumaNode >= 0);
        Assert.Equal(pinning0.NumaNode.ToString(), pinning0.MemsSetString);

        if (Environment.ProcessorCount >= 2)
        {
            // With multiple available cores, instance 0 and 1 must receive distinct non-overlapping physical cores
            Assert.Equal(0L, pinning0.AffinityMask & pinning1.AffinityMask);
        }
    }

    [Fact]
    public void GetDedicatedWorkerPinning_BackgroundPriority_ReturnsValidPinning()
    {
        WorkerCpuPinning bgPinning = CpuTopologyResolver.GetDedicatedWorkerPinning(
            instanceIndex: 0,
            totalInstances: 2,
            priorityMode: HostPriorityMode.Background);

        Assert.NotNull(bgPinning);
        Assert.NotEqual(0L, bgPinning.AffinityMask);
        Assert.NotEmpty(bgPinning.CpusSetString);
        Assert.True(bgPinning.PrimaryPhysicalCore >= 0);
        Assert.True(bgPinning.NumaNode >= 0);
        Assert.Equal(bgPinning.NumaNode.ToString(), bgPinning.MemsSetString);
    }

    [Fact]
    public void GetDedicatedWorkerPinning_ExplicitMask_ParsesCorrectly()
    {
        WorkerCpuPinning explicitPinning = CpuTopologyResolver.GetDedicatedWorkerPinning(
            instanceIndex: 0,
            totalInstances: 1,
            priorityMode: HostPriorityMode.HighPriority,
            mode: CpuAffinityMode.ExplicitMask,
            explicitMaskHex: "0x0C");

        Assert.NotNull(explicitPinning);
        Assert.Equal(0x0CL, explicitPinning.AffinityMask);
        Assert.Equal(2, explicitPinning.PrimaryPhysicalCore);
        Assert.Equal("2,3", explicitPinning.CpusSetString);
        Assert.Equal(explicitPinning.NumaNode.ToString(), explicitPinning.MemsSetString);
    }
}
