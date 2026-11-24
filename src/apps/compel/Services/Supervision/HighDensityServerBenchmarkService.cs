// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HighDensityServerBenchmarkService.cs
// System: COMPEL Supervision - Dedicated Server Linux Containerization Benchmark
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Text;
using COMPEL.Configuration;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Immutable container allocation blueprint specifying CPU affinity, cgroups v2 resource limits,
///     and port bindings for a single dedicated game server container.
/// </summary>
public sealed record ContainerAllocationBlueprint(
    int InstanceIndex,
    int Port,
    long AffinityMask,
    long MemoryLimitBytes,
    long CpuQuotaMicroseconds,
    long CpuPeriodMicroseconds,
    string CgroupV2CpuMax,
    string CgroupV2MemoryMax);

/// <summary>
///     Immutable benchmark plan detailing fleet capacity, overprovisioning risk analysis,
///     and per-container allocation blueprints for high-density dedicated server deployments.
/// </summary>
public sealed record HighDensityBenchmarkPlan(
    int RequestedInstances,
    int AvailableLogicalCores,
    long TotalHostMemoryBytes,
    bool IsCpuOverprovisioned,
    bool IsMemoryOverprovisioned,
    IReadOnlyList<ContainerAllocationBlueprint> Allocations,
    IReadOnlyList<string> Warnings)
{
    public bool IsOverprovisioned => IsCpuOverprovisioned || IsMemoryOverprovisioned;
}

/// <summary>
///     Analyzes and benchmarks high-density dedicated server hosting configurations for Linux containers.
///     Computes cgroups v2 cpu.max / memory.max limits, core affinities, and generates infrastructure blueprints.
/// </summary>
public sealed class HighDensityServerBenchmarkService(ILogger<HighDensityServerBenchmarkService>? logger = null)
{
    public const long DefaultMemoryLimitPerInstance = 512L * 1024 * 1024; // 512 MB
    public const double DefaultCpuCoresPerInstance = 1.0;
    public const int DefaultBasePort = 11235;
    public const long DefaultCgroupPeriodMicroseconds = 100_000; // 100ms CFS period

    private readonly ILogger<HighDensityServerBenchmarkService>? _logger = logger;

    /// <summary>
    ///     Calculates a comprehensive containerization benchmark plan for high-density dedicated server fleets.
    /// </summary>
    public HighDensityBenchmarkPlan CalculateBenchmarkPlan(
        int instanceCount,
        int? overrideCoreCount = null,
        long? overrideTotalMemoryBytes = null,
        int basePort = DefaultBasePort,
        long memoryPerInstanceBytes = DefaultMemoryLimitPerInstance,
        double cpuCoresPerInstance = DefaultCpuCoresPerInstance)
    {
        int clampedInstances = Math.Max(1, instanceCount);
        int availableCores = overrideCoreCount ?? Math.Max(1, Environment.ProcessorCount);

        long hostMemory;
        if (overrideTotalMemoryBytes.HasValue && overrideTotalMemoryBytes.Value > 0)
        {
            hostMemory = overrideTotalMemoryBytes.Value;
        }
        else
        {
            long detectedMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            hostMemory = detectedMem > 0 ? detectedMem : 16L * 1024 * 1024 * 1024; // Fallback 16 GB
        }

        List<ContainerAllocationBlueprint> allocations = new(clampedInstances);
        List<string> warnings = [];

        long cpuPeriod = DefaultCgroupPeriodMicroseconds;
        long cpuQuota = Math.Max(1000, (long)(cpuCoresPerInstance * cpuPeriod));
        string cgroupCpuMax = $"{cpuQuota} {cpuPeriod}";
        string cgroupMemMax = memoryPerInstanceBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        for (int i = 0; i < clampedInstances; i++)
        {
            int port = basePort + i;
            long affinityMask = (long)CpuTopologyResolver.CalculateAffinityMask(i, clampedInstances);

            ContainerAllocationBlueprint blueprint = new(
                InstanceIndex: i,
                Port: port,
                AffinityMask: affinityMask,
                MemoryLimitBytes: memoryPerInstanceBytes,
                CpuQuotaMicroseconds: cpuQuota,
                CpuPeriodMicroseconds: cpuPeriod,
                CgroupV2CpuMax: cgroupCpuMax,
                CgroupV2MemoryMax: cgroupMemMax);

            allocations.Add(blueprint);
        }

        // Capacity and overprovisioning checks
        double totalCpuRequired = clampedInstances * cpuCoresPerInstance;
        bool isCpuOverprovisioned = totalCpuRequired > availableCores;
        if (isCpuOverprovisioned)
        {
            string warn = $"CPU overprovisioned: requested {totalCpuRequired:F1} cores on a host with {availableCores} logical cores. Context switching overhead and scheduling jitter may increase tick latency.";
            warnings.Add(warn);
            _logger?.LogWarning("[HIGH_DENSITY_BENCHMARK] {Warning}", warn);
        }

        long totalMemoryRequired = clampedInstances * memoryPerInstanceBytes;
        bool isMemoryOverprovisioned = totalMemoryRequired > hostMemory;
        if (isMemoryOverprovisioned)
        {
            string warn = $"Memory overprovisioned: requested {totalMemoryRequired / (1024 * 1024):N0} MB on a host with {hostMemory / (1024 * 1024):N0} MB available. Risk of Linux OOM killer invocation under burst loads.";
            warnings.Add(warn);
            _logger?.LogWarning("[HIGH_DENSITY_BENCHMARK] {Warning}", warn);
        }

        return new HighDensityBenchmarkPlan(
            RequestedInstances: clampedInstances,
            AvailableLogicalCores: availableCores,
            TotalHostMemoryBytes: hostMemory,
            IsCpuOverprovisioned: isCpuOverprovisioned,
            IsMemoryOverprovisioned: isMemoryOverprovisioned,
            Allocations: allocations,
            Warnings: warnings);
    }

    /// <summary>
    ///     Generates a production Docker Compose YAML snippet modeling the containerized fleet.
    /// </summary>
    public string GenerateDockerComposeSnippet(HighDensityBenchmarkPlan plan)
    {
        StringBuilder sb = new();
        sb.AppendLine("services:");

        foreach (ContainerAllocationBlueprint allocation in plan.Allocations)
        {
            double cpuLimit = (double)allocation.CpuQuotaMicroseconds / allocation.CpuPeriodMicroseconds;
            long memMb = allocation.MemoryLimitBytes / (1024 * 1024);

            sb.AppendLine($"  hon-server-{allocation.Port}:");
            sb.AppendLine("    image: projectkongor/hon-dedicated:latest");
            sb.AppendLine($"    container_name: hon-dedicated-{allocation.Port}");
            sb.AppendLine("    restart: unless-stopped");
            sb.AppendLine("    ports:");
            sb.AppendLine($"      - \"{allocation.Port}:{allocation.Port}/udp\"");
            sb.AppendLine("    deploy:");
            sb.AppendLine("      resources:");
            sb.AppendLine("        limits:");
            sb.AppendLine($"          cpus: '{cpuLimit:0.0}'");
            sb.AppendLine($"          memory: {memMb}M");
        }

        return sb.ToString();
    }
}
