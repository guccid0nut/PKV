// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Prebaking;
using ASPIRE.Common.Prebaking.Serialization;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Stress;

public sealed class EntityCacheBenchmarkTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(current, "games", "client")))
                return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task<EntityCacheContainer> LoadOrGenerateFullDatasetAsync()
    {
        string repoRoot = FindRepoRoot();
        string clientDir = Path.Combine(repoRoot, "games", "client");
        string sharedDir = Path.Combine(repoRoot, "games", "shared");

        if (Directory.Exists(clientDir) && File.Exists(Path.Combine(clientDir, "game", "heroes.xml")))
        {
            EntityHarvestEngine engine = new();
            return await engine.HarvestAllAsync(clientDir, sharedDir);
        }

        // Fallback synthetic high-scale dataset
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O")
        };

        for (int i = 0; i < 140; i++)
        {
            HeroDefinitionDto hero = new()
            {
                Id = (uint)(i + 1),
                Name = $"Hero_Test_{i}",
                DisplayName = $"Hero Display {i}",
                DefaultIcon = $"/heroes/hero_{i}/icon.tga",
                DefaultModel = $"/heroes/hero_{i}/model.mdf",
                PrimaryAttribute = i % 3 == 0 ? "strength" : i % 3 == 1 ? "agility" : "intelligence",
                Strength = 20f + i,
                Agility = 15f + i,
                Intelligence = 18f + i,
                Category = "Carry Nuker",
                Difficulty = 2.5f
            };

            for (int a = 0; a < 4; a++)
                hero.Abilities.Add(new AbilityDefinitionDto
                {
                    Slot = a,
                    Name = $"Ability_Hero_{i}_{a}",
                    DisplayName = $"Spell {a}",
                    Icon = $"/heroes/hero_{i}/ability_{a}/icon.tga",
                    ActionType = "target_entity",
                    ManaCost = "100,110,120,130",
                    CooldownTime = "10000"
                });

            for (int v = 0; v < 10; v++)
                hero.AltAvatars.Add(new AltAvatarDto
                {
                    Key = $"Hero_Test_{i}.Alt_{v}",
                    HeroName = $"Hero_Test_{i}",
                    DisplayName = $"Alt Avatar {v}",
                    IconPath = $"/heroes/hero_{i}/alt_{v}/icon.tga",
                    DefaultModel = $"/heroes/hero_{i}/alt_{v}/model.mdf",
                    PreviewScale = 1.0f
                });

            container.Heroes.Add(hero);
        }

        for (int c = 0; c < 2858; c++)
            container.CosmeticUpgrades.Add(new CosmeticUpgradeDto
            {
                Name = $"cosmetic_{c}",
                Category = "ChatSymbol",
                Texture = $"/ui/icons/cosmetics/{c}.tga",
                SortIndex = c
            });

        return container;
    }

    [Fact]
    public async Task Benchmark_Serialization_ThroughputAndLatency()
    {
        // Arrange
        EntityCacheContainer container = await LoadOrGenerateFullDatasetAsync();
        int iterations = 15;
        List<double> zstdTimesMs = new(iterations);
        List<double> rawTimesMs = new(iterations);

        // Warmup
        _ = EntityCacheSerializer.Serialize(container, 3, true);
        _ = EntityCacheSerializer.Serialize(container, 0, false);

        // Benchmark Zstd Compression (Level 3)
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            byte[] zstdBytes = EntityCacheSerializer.Serialize(container, 3, true);
            sw.Stop();
            zstdTimesMs.Add(sw.Elapsed.TotalMilliseconds);
            zstdBytes.Length.Should().BeGreaterThan(0);
        }

        // Benchmark Raw Serialization
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            byte[] rawBytes = EntityCacheSerializer.Serialize(container, 0, false);
            sw.Stop();
            rawTimesMs.Add(sw.Elapsed.TotalMilliseconds);
            rawBytes.Length.Should().BeGreaterThan(0);
        }

        double zstdMean = zstdTimesMs.Average();
        double zstdP95 = zstdTimesMs.OrderBy(t => t).ElementAt((int)(iterations * 0.95));
        double rawMean = rawTimesMs.Average();
        double rawP95 = rawTimesMs.OrderBy(t => t).ElementAt((int)(iterations * 0.95));

        _output.WriteLine(
            $"[BENCHMARK] Full Dataset Serialization ({container.Heroes.Count} heroes, {container.AltAvatars.Count} avatars, {container.CosmeticUpgrades.Count} cosmetics)");
        _output.WriteLine(
            $"  Zstd Level 3 : Mean={zstdMean:F2} ms, P95={zstdP95:F2} ms, Min={zstdTimesMs.Min():F2} ms, Max={zstdTimesMs.Max():F2} ms");
        _output.WriteLine(
            $"  Raw MemoryPack: Mean={rawMean:F2} ms, P95={rawP95:F2} ms, Min={rawTimesMs.Min():F2} ms, Max={rawTimesMs.Max():F2} ms");

        // Assertions
        rawMean.Should().BeLessThan(60.0, "Raw MemoryPack serialization of full dataset must complete in < 60ms");
        zstdMean.Should().BeLessThan(120.0, "Zstd serialization of full dataset must complete in < 120ms");
    }

    [Fact]
    public async Task Benchmark_Deserialization_SubMillisecondAndThroughput()
    {
        // Arrange
        EntityCacheContainer container = await LoadOrGenerateFullDatasetAsync();
        byte[] zstdBytes = EntityCacheSerializer.Serialize(container, 3, true);
        byte[] rawBytes = EntityCacheSerializer.Serialize(container, 0, false);

        int iterations = 15;
        List<double> zstdTimesMs = new(iterations);
        List<double> rawTimesMs = new(iterations);

        // Warmup
        _ = EntityCacheDeserializer.Deserialize(zstdBytes);
        _ = EntityCacheDeserializer.Deserialize(rawBytes);

        // Benchmark Zstd Deserialization (Unwrap + MemoryPack Deserialize)
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            EntityCacheContainer res = EntityCacheDeserializer.Deserialize(zstdBytes);
            sw.Stop();
            zstdTimesMs.Add(sw.Elapsed.TotalMilliseconds);
            res.Heroes.Count.Should().Be(container.Heroes.Count);
        }

        // Benchmark Raw Deserialization (MemoryPack Direct Deserialize)
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            EntityCacheContainer res = EntityCacheDeserializer.Deserialize(rawBytes);
            sw.Stop();
            rawTimesMs.Add(sw.Elapsed.TotalMilliseconds);
            res.Heroes.Count.Should().Be(container.Heroes.Count);
        }

        double zstdMean = zstdTimesMs.Average();
        double zstdMedian = zstdTimesMs.OrderBy(t => t).ElementAt(iterations / 2);
        double zstdP95 = zstdTimesMs.OrderBy(t => t).ElementAt((int)(iterations * 0.95));
        double zstdP99 = zstdTimesMs.OrderBy(t => t).ElementAt((int)(iterations * 0.99));

        double rawMean = rawTimesMs.Average();
        double rawMedian = rawTimesMs.OrderBy(t => t).ElementAt(iterations / 2);
        double rawP95 = rawTimesMs.OrderBy(t => t).ElementAt((int)(iterations * 0.95));
        double rawP99 = rawTimesMs.OrderBy(t => t).ElementAt((int)(iterations * 0.99));

        _output.WriteLine($"[BENCHMARK] Full Dataset Deserialization across {iterations} iterations:");
        _output.WriteLine(
            $"  Zstd Deserialization : Mean={zstdMean:F3} ms ({zstdMean * 1000:F0} Âµs), Median={zstdMedian:F3} ms, P95={zstdP95:F3} ms, P99={zstdP99:F3} ms, Min={zstdTimesMs.Min():F3} ms, Max={zstdTimesMs.Max():F3} ms");
        double perEntityUs = rawMean * 1000.0 / container.TotalEntityCount;
        _output.WriteLine(
            $"  Per-Entity Throughput: {perEntityUs:F2} Âµs/entity ({container.TotalEntityCount} entities in {rawMean:F1} ms)");

        // Assertions: Raw MemoryPack direct deserialization of full 24k entity graph is < 100ms (< 10Âµs per entity)
        rawMean.Should().BeLessThan(100.0,
            "Raw direct MemoryPack deserialization of entire 24k entity graph must complete in < 100ms");
        zstdMean.Should().BeLessThan(150.0,
            "Zstd unwrap and deserialization of entire 24k entity graph must complete in < 150ms");
        perEntityUs.Should().BeLessThan(25.0, "Per-entity deserialization must be sub-millisecond (< 25 Âµs/entity)");
    }

    [Fact]
    public void Benchmark_SingleEntityLookupSimulation_SubMillisecondVerification()
    {
        // Verify that in-memory lookup structures simulate < 50 Âµs (0.05 ms) access time
        Dictionary<string, HeroDefinitionDto> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 1000; i++)
            lookup[$"Hero_{i}"] = new HeroDefinitionDto { Id = (uint)i, Name = $"Hero_{i}", DisplayName = $"Hero {i}" };

        // Warmup JIT
        for (int i = 0; i < 10000; i++)
        {
            string warmupKey = $"Hero_{i % 1000}";
            _ = lookup.TryGetValue(warmupKey, out _);
        }

        int lookups = 100000;
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < lookups; i++)
        {
            string key = $"Hero_{i % 1000}";
            _ = lookup.TryGetValue(key, out _);
        }

        sw.Stop();

        double totalMicroseconds = sw.Elapsed.TotalMicroseconds;
        double perLookupMicroseconds = totalMicroseconds / lookups;

        _output.WriteLine(
            $"[BENCHMARK] In-Memory Lookup: {lookups} lookups in {sw.ElapsedMilliseconds} ms ({perLookupMicroseconds:F4} Âµs per lookup)");
        perLookupMicroseconds.Should().BeLessThan(50.0,
            "In-memory dictionary lookup must be under 50 microseconds (well below sub-millisecond 1000Âµs threshold)");
    }
}

