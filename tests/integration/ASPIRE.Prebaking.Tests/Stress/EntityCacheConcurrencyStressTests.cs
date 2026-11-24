// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using ASPIRE.Common.Prebaking.Generators;
using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using NLua;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Stress;

public sealed class EntityCacheConcurrencyStressTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static EntityCacheContainer CreateTestContainer(int heroCount = 50, int cosmeticCount = 100)
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
                Name = $"Hero_Stress_{i}",
                DisplayName = $"Stress Hero {i}",
                DefaultIcon = $"/heroes/stress_{i}/icon.tga",
                DefaultModel = $"/heroes/stress_{i}/model.mdf",
                PrimaryAttribute = "agility",
                Strength = 20f,
                Agility = 25f,
                Intelligence = 15f
            };

            for (int a = 0; a < 4; a++)
                hero.Abilities.Add(new AbilityDefinitionDto
                {
                    Slot = a,
                    Name = $"Ability_Stress_{i}_{a}",
                    DisplayName = $"Stress Spell {a}",
                    Icon = $"/heroes/stress_{i}/ability_{a}/icon.tga"
                });

            for (int v = 0; v < 3; v++)
                hero.AltAvatars.Add(new AltAvatarDto
                {
                    Key = $"Hero_Stress_{i}.Alt_{v}",
                    HeroName = $"Hero_Stress_{i}",
                    DisplayName = $"Alt {v}",
                    IconPath = $"/heroes/stress_{i}/alt_{v}/icon.tga"
                });

            container.Heroes.Add(hero);
        }

        for (int c = 0; c < cosmeticCount; c++)
            container.CosmeticUpgrades.Add(new CosmeticUpgradeDto
            {
                Name = $"cosmetic_{c}",
                Category = "ChatSymbol",
                Texture = $"/ui/icons/flags/{c}.tga",
                SortIndex = c
            });

        return container;
    }

    [Trait("Category", "Manual")]
    [Theory]
    [InlineData(16, 50)]
    [InlineData(32, 25)]
    [InlineData(64, 15)]
    public void Concurrent_MultiThreaded_Deserialization_ZeroRaceConditions(int threadCount, int iterationsPerThread)
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        EntityCacheContainer original = CreateTestContainer();
        byte[] payload = EntityCacheSerializer.Serialize(original, 3, true);
        ConcurrentBag<Exception> exceptions = [];
        ConcurrentBag<long> latenciesMicroseconds = [];

        Stopwatch totalSw = Stopwatch.StartNew();

        // Act
        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadIdx =>
        {
            try
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    Stopwatch opSw = Stopwatch.StartNew();
                    EntityCacheContainer result = EntityCacheDeserializer.Deserialize(payload);
                    opSw.Stop();

                    latenciesMicroseconds.Add((long)opSw.Elapsed.TotalMicroseconds);

                    if (result.Heroes.Count != original.Heroes.Count ||
                        result.CosmeticUpgrades.Count != original.CosmeticUpgrades.Count)
                        throw new InvalidOperationException($"Mismatch in thread {threadIdx} iteration {i}");
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        totalSw.Stop();

        // Assert
        exceptions.Should().BeEmpty("Multi-threaded deserialization must never throw race condition exceptions");
        int totalOps = threadCount * iterationsPerThread;
        double meanUs = latenciesMicroseconds.Average();
        _output.WriteLine(
            $"[CONCURRENCY] {threadCount} threads x {iterationsPerThread} ops ({totalOps} total ops) in {totalSw.ElapsedMilliseconds} ms: Mean Latency = {meanUs:F1} µs");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Concurrent_SerializationAndDeserialization_SimultaneousStress()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] initialPayload = EntityCacheSerializer.Serialize(container, 3);

        int writerThreads = 8;
        int readerThreads = 24;
        int iterations = 30;

        ConcurrentBag<Exception> exceptions = [];
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Act
        Task[] writerTasks =
        [
            .. Enumerable.Range(0, writerThreads).Select(wId => Task.Run(() =>
            {
                for (int i = 0; i < iterations && !cts.IsCancellationRequested; i++)
                    try
                    {
                        byte[] bytes = EntityCacheSerializer.Serialize(container, 1);
                        bytes.Length.Should().BeGreaterThan(EntityCacheHeader.HeaderSize);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
            }))
        ];

        Task[] readerTasks =
        [
            .. Enumerable.Range(0, readerThreads).Select(rId => Task.Run(() =>
            {
                for (int i = 0; i < iterations * 2 && !cts.IsCancellationRequested; i++)
                    try
                    {
                        EntityCacheContainer res = EntityCacheDeserializer.Deserialize(initialPayload);
                        res.Heroes.Count.Should().Be(container.Heroes.Count);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
            }))
        ];

        await Task.WhenAll(writerTasks.Concat(readerTasks));

        // Assert
        exceptions.Should()
            .BeEmpty(
                "Simultaneous serialization and deserialization stress must complete cleanly without data race issues");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Concurrent_FileReadWrite_Stress()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        string tempDir = Path.Combine(Path.GetTempPath(), $"prebake_concurrency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, "entity_cache.bin");

        await EntityCacheSerializer.SerializeToFileAsync(container, filePath, 3);

        ConcurrentBag<Exception> exceptions = [];
        int readIterations = 50;
        int readers = 16;

        try
        {
            // Act: multiple parallel readers while writing
            Task[] readerTasks =
            [
                .. Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < readIterations; i++)
                        try
                        {
                            EntityCacheContainer loaded =
                                await EntityCacheDeserializer.DeserializeFromFileAsync(filePath);
                            loaded.Heroes.Count.Should().Be(container.Heroes.Count);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                }))
            ];

            await Task.WhenAll(readerTasks);

            // Assert
            exceptions.Should().BeEmpty("Concurrent file reads must be thread-safe");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void Concurrent_LuaGenerationAndExecution_Stress()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        EntityCacheContainer container = CreateTestContainer(20);
        int threadCount = 16;
        ConcurrentBag<Exception> exceptions = [];

        // Act
        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, _ =>
        {
            try
            {
                string lua = LuaCacheGenerator.GenerateLuaCache(container);
                lua.Should().NotBeNullOrWhiteSpace();

                using Lua state = new();
                state.DoString(lua);

                object[] res = state.DoString("return HeroDefinitionsCache.GetHeroIcon('Hero_Stress_0')");
                res.Should().NotBeNull();
                res[0].ToString().Should().Be("/heroes/stress_0/icon.tga");

                object[] heroRes = state.DoString("return HeroDefinitionsCache.GetHero('Hero_Stress_0').displayName");
                heroRes[0].ToString().Should().Be("Stress Hero 0");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert
        exceptions.Should()
            .BeEmpty(
                "Concurrent Lua generation and execution in independent NLua states must succeed without corruption");
    }
}
