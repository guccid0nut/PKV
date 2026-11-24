// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HighDensityServerBenchmarkTests.cs
// System: COMPEL Unit Tests - Dedicated Server Linux Containerization Benchmark
// ============================================================================

using COMPEL.Services.Supervision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class HighDensityServerBenchmarkTests
{
    [Fact]
    public void CalculateBenchmarkPlan_StandardDeployment_GeneratesAccurateAllocations()
    {
        // Arrange
        HighDensityServerBenchmarkService service = new(NullLogger<HighDensityServerBenchmarkService>.Instance);

        // Act
        HighDensityBenchmarkPlan plan = service.CalculateBenchmarkPlan(
            instanceCount: 4,
            overrideCoreCount: 8,
            overrideTotalMemoryBytes: 8L * 1024 * 1024 * 1024,
            basePort: 11235);

        // Assert
        Assert.Equal(4, plan.RequestedInstances);
        Assert.Equal(8, plan.AvailableLogicalCores);
        Assert.False(plan.IsOverprovisioned);
        Assert.False(plan.IsCpuOverprovisioned);
        Assert.False(plan.IsMemoryOverprovisioned);
        Assert.Empty(plan.Warnings);
        Assert.Equal(4, plan.Allocations.Count);

        for (int i = 0; i < 4; i++)
        {
            ContainerAllocationBlueprint alloc = plan.Allocations[i];
            Assert.Equal(i, alloc.InstanceIndex);
            Assert.Equal(11235 + i, alloc.Port);
            Assert.NotEqual(0L, alloc.AffinityMask);
            Assert.Equal(512L * 1024 * 1024, alloc.MemoryLimitBytes);
            Assert.Equal(100_000L, alloc.CpuQuotaMicroseconds);
            Assert.Equal(100_000L, alloc.CpuPeriodMicroseconds);
            Assert.Equal("100000 100000", alloc.CgroupV2CpuMax);
            Assert.Equal("536870912", alloc.CgroupV2MemoryMax);
        }
    }

    [Fact]
    public void CalculateBenchmarkPlan_CpuOverprovisioned_GeneratesWarning()
    {
        // Arrange
        HighDensityServerBenchmarkService service = new(NullLogger<HighDensityServerBenchmarkService>.Instance);

        // Act
        HighDensityBenchmarkPlan plan = service.CalculateBenchmarkPlan(
            instanceCount: 16,
            overrideCoreCount: 4,
            overrideTotalMemoryBytes: 64L * 1024 * 1024 * 1024);

        // Assert
        Assert.True(plan.IsCpuOverprovisioned);
        Assert.True(plan.IsOverprovisioned);
        Assert.False(plan.IsMemoryOverprovisioned);
        Assert.Contains(plan.Warnings, w => w.Contains("CPU overprovisioned"));
    }

    [Fact]
    public void CalculateBenchmarkPlan_MemoryOverprovisioned_GeneratesWarning()
    {
        // Arrange
        HighDensityServerBenchmarkService service = new(NullLogger<HighDensityServerBenchmarkService>.Instance);

        // Act
        HighDensityBenchmarkPlan plan = service.CalculateBenchmarkPlan(
            instanceCount: 10,
            overrideCoreCount: 16,
            overrideTotalMemoryBytes: 4L * 1024 * 1024 * 1024, // 4 GB host
            memoryPerInstanceBytes: 1024L * 1024 * 1024); // 1 GB per instance => 10 GB total

        // Assert
        Assert.False(plan.IsCpuOverprovisioned);
        Assert.True(plan.IsMemoryOverprovisioned);
        Assert.True(plan.IsOverprovisioned);
        Assert.Contains(plan.Warnings, w => w.Contains("Memory overprovisioned"));
    }

    [Fact]
    public void CalculateBenchmarkPlan_FractionalCores_CalculatesCorrectCgroupQuota()
    {
        // Arrange
        HighDensityServerBenchmarkService service = new(NullLogger<HighDensityServerBenchmarkService>.Instance);

        // Act
        HighDensityBenchmarkPlan plan = service.CalculateBenchmarkPlan(
            instanceCount: 2,
            overrideCoreCount: 8,
            cpuCoresPerInstance: 0.5);

        // Assert
        Assert.Equal(2, plan.Allocations.Count);
        Assert.Equal(50_000L, plan.Allocations[0].CpuQuotaMicroseconds);
        Assert.Equal(100_000L, plan.Allocations[0].CpuPeriodMicroseconds);
        Assert.Equal("50000 100000", plan.Allocations[0].CgroupV2CpuMax);
    }

    [Fact]
    public void GenerateDockerComposeSnippet_ValidPlan_OutputsValidYamlStructure()
    {
        // Arrange
        HighDensityServerBenchmarkService service = new(NullLogger<HighDensityServerBenchmarkService>.Instance);
        HighDensityBenchmarkPlan plan = service.CalculateBenchmarkPlan(instanceCount: 2, overrideCoreCount: 4);

        // Act
        string yaml = service.GenerateDockerComposeSnippet(plan);

        // Assert
        Assert.Contains("services:", yaml);
        Assert.Contains("hon-server-11235:", yaml);
        Assert.Contains("hon-server-11236:", yaml);
        Assert.Contains("\"11235:11235/udp\"", yaml);
        Assert.Contains("\"11236:11236/udp\"", yaml);
        Assert.Contains("cpus: '1.0'", yaml);
        Assert.Contains("memory: 512M", yaml);
    }
}
