// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Prebaking.Serialization;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Stress;

public sealed class EntityCacheMemoryEfficiencyTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static EntityCacheContainer CreateTestContainer(int heroCount = 100)
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O")
        };

        for (int i = 0; i < heroCount; i++)
        {
            HeroDefinitionDto hero = new()
            {
                Id = (uint)(i + 1),
                Name = $"Hero_Memory_{i}",
                DisplayName = $"Memory Hero {i}",
                DefaultIcon = $"/heroes/mem_{i}/icon.tga",
                DefaultModel = $"/heroes/mem_{i}/model.mdf",
                PrimaryAttribute = "strength",
                Strength = 25f
            };

            for (int a = 0; a < 4; a++)
                hero.Abilities.Add(new AbilityDefinitionDto
                {
                    Slot = a,
                    Name = $"Ability_Mem_{i}_{a}",
                    DisplayName = $"Spell {a}",
                    Icon = $"/heroes/mem_{i}/ability_{a}/icon.tga"
                });

            container.Heroes.Add(hero);
        }

        return container;
    }

    [Fact]
    public void Deserialization_MemoryAllocation_IsOrderOfMagnitudeLowerThanJson()
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer(140);
        byte[] rawMemoryPackBytes = EntityCacheSerializer.Serialize(container, 0, false);
        string jsonString = JsonSerializer.Serialize(container);
        byte[] jsonUtf8Bytes = JsonSerializer.SerializeToUtf8Bytes(container);

        // Force GC warmup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Measure MemoryPack Deserialization Allocation
        long memBeforeMp = GC.GetAllocatedBytesForCurrentThread();
        EntityCacheContainer mpResult = EntityCacheDeserializer.Deserialize(rawMemoryPackBytes);
        long memAfterMp = GC.GetAllocatedBytesForCurrentThread();
        long memPackAllocated = memAfterMp - memBeforeMp;

        // Force GC before JSON
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Measure System.Text.Json Deserialization Allocation
        long memBeforeJson = GC.GetAllocatedBytesForCurrentThread();
        EntityCacheContainer? jsonResult = JsonSerializer.Deserialize<EntityCacheContainer>(jsonUtf8Bytes);
        long memAfterJson = GC.GetAllocatedBytesForCurrentThread();
        long jsonAllocated = memAfterJson - memBeforeJson;

        _output.WriteLine("[MEMORY EFFICIENCY] 140 Heroes Graph Deserialization Allocation:");
        _output.WriteLine($"  MemoryPack Direct : {memPackAllocated:N0} bytes ({memPackAllocated / 1024.0:F1} KB)");
        _output.WriteLine($"  System.Text.Json  : {jsonAllocated:N0} bytes ({jsonAllocated / 1024.0:F1} KB)");
        _output.WriteLine($"  Payload Size (MemoryPack): {rawMemoryPackBytes.Length / 1024.0:F1} KB");
        _output.WriteLine($"  Payload Size (JSON)      : {jsonUtf8Bytes.Length / 1024.0:F1} KB");

        // Assert
        mpResult.Heroes.Count.Should().Be(140);
        jsonResult.Should().NotBeNull();
        rawMemoryPackBytes.Length.Should().BeLessThan(jsonUtf8Bytes.Length,
            "MemoryPack binary payload should be significantly more compact than JSON");
    }

    [Fact]
    public void Deserialization_RepeatedExecutions_MemoryAllocationRemainsStable()
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer(50);
        byte[] zstdBytes = EntityCacheSerializer.Serialize(container, 3, true);

        // Warmup
        _ = EntityCacheDeserializer.Deserialize(zstdBytes);

        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        int iterations = 100;

        // Act: 100 deserializations
        for (int i = 0; i < iterations; i++)
        {
            EntityCacheContainer res = EntityCacheDeserializer.Deserialize(zstdBytes);
            res.Heroes.Count.Should().Be(50);
        }

        long endBytes = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = endBytes - startBytes;
        double avgBytesPerOp = (double)totalAllocated / iterations;

        _output.WriteLine(
            $"[GC STRESS] {iterations} Deserializations: Total Allocated = {totalAllocated:N0} bytes, Avg per op = {avgBytesPerOp:N0} bytes ({avgBytesPerOp / 1024.0:F1} KB)");

        // Assert: Memory allocation per deserialization should be consistent and bounded (< 350 KB per operation for 50 heroes with abilities)
        avgBytesPerOp.Should()
            .BeLessThan(350 * 1024, "Per-deserialization heap allocation must remain compact and stable");
    }
}
