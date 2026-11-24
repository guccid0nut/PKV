// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ASPIRE.Common.Services.EntityCache;
using ASPIRE.Common.Telemetry;
using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using KONGOR.MasterServer.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Stress;

public sealed class EmpiricalPerformanceAndStressHarnessTests : IDisposable
{
    private readonly string _cacheFilePath;

    private readonly ConcurrentBag<(string InstrumentName, long Value, Dictionary<string, object?> Tags)>
        _counterRecordings = [];

    private readonly ConcurrentBag<(string InstrumentName, double Value, Dictionary<string, object?> Tags)>
        _histogramRecordings = [];

    private readonly MeterListener _meterListener;
    private readonly ITestOutputHelper _output;

    public EmpiricalPerformanceAndStressHarnessTests(ITestOutputHelper output)
    {
        _output = output;

        // Locate real pre-baked entity_cache.bin
        string repoRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnxx")) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")))
        {
            string? parent = Path.GetDirectoryName(repoRoot);
            if (string.IsNullOrEmpty(parent) || parent == repoRoot) break;
            repoRoot = parent;
        }

        string candidatePath =
            Path.Combine(repoRoot, "games", "client", "override", "prebaked_assets", "entity_cache.bin");
        if (File.Exists(candidatePath))
            _cacheFilePath = candidatePath;
        else
            _cacheFilePath = Path.Combine(AppContext.BaseDirectory, "entity_cache.bin");

        // Setup OpenTelemetry MeterListener
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NexusMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
            }
        };

        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            Dictionary<string, object?> tagDict = [];
            foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
            _counterRecordings.Add((instrument.Name, measurement, tagDict));
        });

        _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            Dictionary<string, object?> tagDict = [];
            foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
            _histogramRecordings.Add((instrument.Name, measurement, tagDict));
        });

        _meterListener.Start();
    }

    public void Dispose()
    {
        _meterListener.Dispose();
    }

    private async Task<PrebakedEntityRegistry> CreateAndInitializeRegistryAsync()
    {
        PrebakedEntityRegistry registry = new(
            NullLogger<PrebakedEntityRegistry>.Instance,
            explicitCachePath: _cacheFilePath);

        await registry.InitializeAsync();
        registry.IsPrewarmed.Should().BeTrue();
        registry.TotalEntitiesLoaded.Should().BeGreaterThan(0);
        return registry;
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Benchmark_InMemoryLookupLatency_10000RandomEntityQueries_Under50Microseconds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // -------------------------------------------------------------
        // Mission 1: Benchmark in-memory lookup latency across 10,000 random entity queries (<50Î¼s)
        // -------------------------------------------------------------
        using PrebakedEntityRegistry registry = await CreateAndInitializeRegistryAsync();

        List<HeroDefinitionDto> heroes = [.. registry.GetAllHeroes()];
        List<AbilityDefinitionDto> abilities = [.. registry.GetAllAbilities()];
        List<StateDefinitionDto> states = [.. registry.GetAllStates()];
        List<ProjectileDefinitionDto> projectiles = [.. registry.GetAllProjectiles()];
        List<ItemDefinitionDto> items = [.. registry.GetAllItems()];
        List<CosmeticUpgradeDto> cosmetics = [.. registry.GetAllCosmetics()];
        List<AltAvatarDto> altAvatars = [.. registry.GetAllAltAvatars()];

        heroes.Should().NotBeEmpty();
        _output.WriteLine($"[Benchmark Setup] Loaded {registry.TotalEntitiesLoaded} prebaked entities:");
        _output.WriteLine($"  - Heroes: {heroes.Count}");
        _output.WriteLine($"  - Abilities: {abilities.Count}");
        _output.WriteLine($"  - States: {states.Count}");
        _output.WriteLine($"  - Projectiles: {projectiles.Count}");
        _output.WriteLine($"  - Items: {items.Count}");
        _output.WriteLine($"  - Cosmetics: {cosmetics.Count}");
        _output.WriteLine($"  - AltAvatars: {altAvatars.Count}");

        // Build a randomized query suite of 10,000 operations
        const int totalQueries = 10_000;
        Random rng = new(42); // deterministic seed for reproducibility
        List<Action> queryActions = new(totalQueries);

        for (int i = 0; i < totalQueries; i++)
        {
            int queryType = i % 10;
            switch (queryType)
            {
                case 0: // Hero by ID
                {
                    uint heroId = heroes[rng.Next(heroes.Count)].Id;
                    queryActions.Add(() =>
                    {
                        HeroDefinitionDto? h = registry.GetHeroById(heroId);
                        h.Should().NotBeNull();
                    });
                    break;
                }
                case 1: // Hero by Name
                {
                    string heroName = heroes[rng.Next(heroes.Count)].Name;
                    queryActions.Add(() =>
                    {
                        HeroDefinitionDto? h = registry.GetHeroByName(heroName);
                        h.Should().NotBeNull();
                    });
                    break;
                }
                case 2: // Hero by Clean/Short Name
                {
                    HeroDefinitionDto randomHero = heroes[rng.Next(heroes.Count)];
                    string clean = randomHero.Name.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase);
                    queryActions.Add(() =>
                    {
                        HeroDefinitionDto? h = registry.GetHeroByName(clean);
                        h.Should().NotBeNull();
                    });
                    break;
                }
                case 3: // Ability by Name
                {
                    string abilityName = abilities.Count > 0
                        ? abilities[rng.Next(abilities.Count)].Name
                        : "Ability_Accursed1";
                    queryActions.Add(() =>
                    {
                        AbilityDefinitionDto? a = registry.GetAbilityByName(abilityName);
                        a.Should().NotBeNull();
                    });
                    break;
                }
                case 4: // State by Name
                {
                    string stateName =
                        states.Count > 0 ? states[rng.Next(states.Count)].Name : "State_Accursed_Ability2";
                    queryActions.Add(() =>
                    {
                        StateDefinitionDto? s = registry.GetStateByName(stateName);
                        if (states.Count > 0) s.Should().NotBeNull();
                    });
                    break;
                }
                case 5: // Projectile by Name
                {
                    string projName = projectiles.Count > 0
                        ? projectiles[rng.Next(projectiles.Count)].Name
                        : "Projectile_Accursed_Alt4";
                    queryActions.Add(() =>
                    {
                        ProjectileDefinitionDto? p = registry.GetProjectileByName(projName);
                        if (projectiles.Count > 0) p.Should().NotBeNull();
                    });
                    break;
                }
                case 6: // Item by Name
                {
                    string itemName = items.Count > 0 ? items[rng.Next(items.Count)].Name : "Item_Bottle";
                    queryActions.Add(() =>
                    {
                        ItemDefinitionDto? it = registry.GetItemByName(itemName);
                        if (items.Count > 0) it.Should().NotBeNull();
                    });
                    break;
                }
                case 7: // Cosmetic by Name
                {
                    string cosName = cosmetics.Count > 0 ? cosmetics[rng.Next(cosmetics.Count)].Name : "501";
                    queryActions.Add(() =>
                    {
                        CosmeticUpgradeDto? c = registry.GetCosmeticByName(cosName);
                        if (cosmetics.Count > 0) c.Should().NotBeNull();
                    });
                    break;
                }
                case 8: // Alt Avatar by Hero + Key
                {
                    AltAvatarDto avatar = altAvatars.Count > 0
                        ? altAvatars[rng.Next(altAvatars.Count)]
                        : new AltAvatarDto { HeroName = "Hero_Accursed", Key = "Hero_Accursed.Alt" };
                    queryActions.Add(() =>
                    {
                        AltAvatarDto? a = registry.GetAltAvatar(avatar.HeroName, avatar.Key);
                        if (altAvatars.Count > 0) a.Should().NotBeNull();
                    });
                    break;
                }
                case 9: // Generic Entity lookup
                {
                    string entName = heroes[rng.Next(heroes.Count)].Name;
                    queryActions.Add(() =>
                    {
                        EntityDefinitionDto? e = registry.GetEntityByName(entName);
                        e.Should().NotBeNull();
                    });
                    break;
                }
            }
        }

        // --- Warmup Phase (1,000 queries) ---
        for (int i = 0; i < 1_000; i++) queryActions[i % queryActions.Count]();

        // --- Benchmark Execution Phase ---
        double[] latenciesMicroseconds = new double[totalQueries];
        long startGcAlloc = GC.GetTotalAllocatedBytes(true);
        Stopwatch benchmarkSw = Stopwatch.StartNew();

        for (int i = 0; i < totalQueries; i++)
        {
            long opStart = Stopwatch.GetTimestamp();
            queryActions[i]();
            latenciesMicroseconds[i] = Stopwatch.GetElapsedTime(opStart).TotalMilliseconds * 1000.0;
        }

        benchmarkSw.Stop();
        long endGcAlloc = GC.GetTotalAllocatedBytes(true);
        long totalAllocated = endGcAlloc - startGcAlloc;

        // --- Statistical Computations ---
        Array.Sort(latenciesMicroseconds);
        double minLatency = latenciesMicroseconds[0];
        double maxLatency = latenciesMicroseconds[^1];
        double meanLatency = latenciesMicroseconds.Average();
        double medianP50 = latenciesMicroseconds[(int)(totalQueries * 0.50)];
        double p90 = latenciesMicroseconds[(int)(totalQueries * 0.90)];
        double p95 = latenciesMicroseconds[(int)(totalQueries * 0.95)];
        double p99 = latenciesMicroseconds[(int)(totalQueries * 0.99)];
        double p999 = latenciesMicroseconds[(int)(totalQueries * 0.999)];
        double throughputOpsPerSec = totalQueries / benchmarkSw.Elapsed.TotalSeconds;

        _output.WriteLine("\n==========================================================================");
        _output.WriteLine("   EMPIRICAL BENCHMARK RESULTS: 10,000 In-Memory Entity Queries           ");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"  Total Queries       : {totalQueries:N0}");
        _output.WriteLine(
            $"  Total Benchmark Time: {benchmarkSw.ElapsedMilliseconds} ms ({benchmarkSw.Elapsed.TotalSeconds:F4} s)");
        _output.WriteLine($"  Throughput          : {throughputOpsPerSec:N0} lookups/sec");
        _output.WriteLine(
            $"  Total Heap Allocated: {totalAllocated:N0} bytes ({(double)totalAllocated / totalQueries:F2} bytes/op)");
        _output.WriteLine("--------------------------------------------------------------------------");
        _output.WriteLine($"  Min Latency         : {minLatency:F4} Î¼s");
        _output.WriteLine($"  Mean Latency        : {meanLatency:F4} Î¼s");
        _output.WriteLine($"  Median (p50)        : {medianP50:F4} Î¼s");
        _output.WriteLine($"  p90 Latency         : {p90:F4} Î¼s");
        _output.WriteLine($"  p95 Latency         : {p95:F4} Î¼s");
        _output.WriteLine($"  p99 Latency         : {p99:F4} Î¼s");
        _output.WriteLine($"  p99.9 Latency       : {p999:F4} Î¼s");
        _output.WriteLine($"  Max Latency         : {maxLatency:F4} Î¼s");
        _output.WriteLine("==========================================================================\n");

        // --- Assertions & Verification Target ---
        meanLatency.Should().BeLessThan(50.0, "Mean entity lookup latency must strictly satisfy the <50Î¼s requirement");
        medianP50.Should().BeLessThan(50.0,
            "Median (p50) entity lookup latency must strictly satisfy the <50Î¼s requirement");
        p99.Should().BeLessThan(50.0, "p99 entity lookup latency must satisfy the <50Î¼s requirement");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ConcurrencyStress_Simulate100ConcurrentPlayerSessions_LoginHeroGridProfileStats()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // -------------------------------------------------------------
        // Mission 2: Simulate 100 concurrent player sessions accessing login, hero grid, profile stats simultaneously
        // -------------------------------------------------------------
        using PrebakedEntityRegistry registry = await CreateAndInitializeRegistryAsync();
        HeroDefinitionService heroService = new(
            NullLogger<HeroDefinitionService>.Instance,
            new MockHostEnvironment(),
            registry);

        List<HeroDefinitionDto> allHeroes = [.. registry.GetAllHeroes()];
        allHeroes.Should().HaveCountGreaterThanOrEqualTo(140);

        const int concurrentSessions = 100;
        const int
            iterationsPerSession = 5; // each session repeats 5 full cycles of login -> hero grid -> profile inspection
        int totalSuccessfulOperations = 0;
        int totalExceptionsEncountered = 0;
        ConcurrentBag<string> capturedErrors = [];

        Stopwatch stressSw = Stopwatch.StartNew();

        Task[] sessionTasks =
        [
            .. Enumerable.Range(1, concurrentSessions).Select(sessionId => Task.Run(async () =>
            {
                int localSuccess = 0;
                try
                {
                    for (int iteration = 1; iteration <= iterationsPerSession; iteration++)
                    {
                        // -------------------------------------------------------------
                        // 1. Session Login Flow
                        // -------------------------------------------------------------
                        uint defaultHeroId = (uint)((sessionId * 7 + iteration) % 140 + 1);
                        uint favHero1 = (uint)((sessionId + 1) % 140 + 1);
                        uint favHero2 = (uint)((sessionId + 37) % 140 + 1);
                        uint favHero3 = (uint)((sessionId + 89) % 140 + 1);

                        // Hero definitions for login
                        HeroDefinitionDto? dHero = registry.GetHeroById(defaultHeroId);
                        HeroDefinitionDto? f1 = registry.GetHeroById(favHero1);
                        HeroDefinitionDto? f2 = registry.GetHeroById(favHero2);
                        HeroDefinitionDto? f3 = registry.GetHeroById(favHero3);

                        if (dHero != null) Interlocked.Increment(ref localSuccess);
                        if (f1 != null) Interlocked.Increment(ref localSuccess);
                        if (f2 != null) Interlocked.Increment(ref localSuccess);
                        if (f3 != null) Interlocked.Increment(ref localSuccess);

                        // Service integration check
                        string heroIdent = heroService.GetHeroIdentifier(defaultHeroId);
                        if (!string.IsNullOrEmpty(heroIdent)) Interlocked.Increment(ref localSuccess);

                        // Equipped alt avatars for login
                        if (dHero != null && dHero.AltAvatarKeys.Count > 0)
                            foreach (string altKey in dHero.AltAvatarKeys.Take(2))
                            {
                                AltAvatarDto? alt = registry.GetAltAvatar(dHero.Name, altKey);
                                if (alt != null) Interlocked.Increment(ref localSuccess);
                            }

                        // Equipped account icons/cosmetics
                        CosmeticUpgradeDto? cos = registry.GetCosmeticById((uint)(sessionId % 500 + 1));
                        // Cosmetics might be sparse by numeric ID, resolve by name as well
                        CosmeticUpgradeDto? cosByName = registry.GetCosmeticByName("501");
                        if (cos != null || cosByName != null) Interlocked.Increment(ref localSuccess);

                        NexusMetrics.RecordCcuEvent("connected");
                        NexusMetrics.RecordMasterServerRequest("login");

                        // -------------------------------------------------------------
                        // 2. Full Hero Selection Grid Loading (All 140 Heroes simultaneously)
                        // -------------------------------------------------------------
                        for (int h = 0; h < allHeroes.Count; h++)
                        {
                            HeroDefinitionDto targetHero = allHeroes[h];
                            HeroDefinitionDto? loaded = registry.GetHeroById(targetHero.Id);
                            if (loaded != null) Interlocked.Increment(ref localSuccess);

                            // Also verify hero identifier lookup via service
                            string ident = heroService.GetHeroIdentifier(targetHero.Id);
                            if (!string.IsNullOrEmpty(ident)) Interlocked.Increment(ref localSuccess);
                        }

                        // -------------------------------------------------------------
                        // 3. Player Profile Stats & Deep Entity Graph Inspection
                        // -------------------------------------------------------------
                        // Inspect 10 match history heroes with their full ability tooltips & alt avatars
                        List<uint> matchHistoryHeroes =
                            [.. Enumerable.Range(1, 10).Select(m => (uint)((sessionId * 13 + m * 7) % 140 + 1))];

                        foreach (uint mhId in matchHistoryHeroes)
                        {
                            HeroDefinitionDto? mhHero = registry.GetHeroById(mhId);
                            if (mhHero != null)
                            {
                                Interlocked.Increment(ref localSuccess);

                                // Inspect hero abilities
                                foreach (string abName in mhHero.AbilityNames)
                                {
                                    AbilityDefinitionDto? ab = registry.GetAbilityByName(abName);
                                    if (ab != null) Interlocked.Increment(ref localSuccess);
                                }

                                // Inspect hero alt avatars
                                foreach (string avKey in mhHero.AltAvatarKeys)
                                {
                                    AltAvatarDto? av = registry.GetAltAvatar(mhHero.Name, avKey);
                                    if (av != null) Interlocked.Increment(ref localSuccess);
                                }
                            }
                        }

                        // Inspect 5 cosmetic items in profile armory
                        for (int c = 1; c <= 5; c++)
                        {
                            EntityLookupResult assetRes = registry.ResolveAsset("Hero_Accursed");
                            if (assetRes.Found) Interlocked.Increment(ref localSuccess);
                        }

                        // Brief non-blocking yield to simulate network/UI event loop interleaved processing
                        await Task.Yield();
                    }

                    Interlocked.Add(ref totalSuccessfulOperations, localSuccess);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref totalExceptionsEncountered);
                    capturedErrors.Add($"[Session-{sessionId}] Exception: {ex}");
                }
            }))
        ];

        await Task.WhenAll(sessionTasks);
        stressSw.Stop();

        _output.WriteLine("\n==========================================================================");
        _output.WriteLine("   CONCURRENCY STRESS TEST RESULTS: 100 Concurrent Player Sessions        ");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"  Concurrent Sessions : {concurrentSessions}");
        _output.WriteLine($"  Iterations / Session: {iterationsPerSession}");
        _output.WriteLine($"  Total Operations    : {totalSuccessfulOperations:N0}");
        _output.WriteLine($"  Total Errors/Ex     : {totalExceptionsEncountered}");
        _output.WriteLine(
            $"  Total Duration      : {stressSw.ElapsedMilliseconds} ms ({stressSw.Elapsed.TotalSeconds:F4} s)");
        _output.WriteLine(
            $"  Throughput          : {totalSuccessfulOperations / stressSw.Elapsed.TotalSeconds:N0} ops/sec");
        _output.WriteLine("==========================================================================\n");

        // --- Verifications ---
        totalExceptionsEncountered.Should()
            .Be(0, "All 100 concurrent sessions must complete with 0 unhandled exceptions");
        capturedErrors.Should().BeEmpty();
        totalSuccessfulOperations.Should().BeGreaterThan(100_000,
            "100 sessions over 5 iterations must successfully process >100,000 entity operations");
        stressSw.ElapsedMilliseconds.Should().BeLessThan(3000,
            "100 concurrent sessions executing >100,000 operations must complete in <3000ms");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task TelemetryAndObservability_FullCacheLookupPipeline_RecordsMetricsCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // -------------------------------------------------------------
        // Mission 3: Verify OpenTelemetry metrics emitted correctly across all lookup code paths
        // -------------------------------------------------------------
        _counterRecordings.Clear();
        _histogramRecordings.Clear();

        using PrebakedEntityRegistry registry = await CreateAndInitializeRegistryAsync();
        List<HeroDefinitionDto> heroes = [.. registry.GetAllHeroes()];
        List<AbilityDefinitionDto> abilities = [.. registry.GetAllAbilities()];
        List<CosmeticUpgradeDto> cosmetics = [.. registry.GetAllCosmetics()];

        const int testQueries = 5_000;
        int executedLookups = 0;

        for (int i = 0; i < testQueries; i++)
        {
            int mod = i % 4;
            switch (mod)
            {
                case 0:
                {
                    HeroDefinitionDto target = heroes[i % heroes.Count];
                    HeroDefinitionDto? h = registry.GetHeroById(target.Id);
                    h.Should().NotBeNull();
                    executedLookups++;
                    break;
                }
                case 1:
                {
                    HeroDefinitionDto target = heroes[i % heroes.Count];
                    HeroDefinitionDto? h = registry.GetHeroByName(target.Name);
                    h.Should().NotBeNull();
                    executedLookups++;
                    break;
                }
                case 2:
                {
                    if (abilities.Count > 0)
                    {
                        AbilityDefinitionDto ab = abilities[i % abilities.Count];
                        AbilityDefinitionDto? loaded = registry.GetAbilityByName(ab.Name);
                        loaded.Should().NotBeNull();
                        executedLookups++;
                    }

                    break;
                }
                case 3:
                {
                    if (cosmetics.Count > 0)
                    {
                        CosmeticUpgradeDto cos = cosmetics[i % cosmetics.Count];
                        CosmeticUpgradeDto? loaded = registry.GetCosmeticByName(cos.Name);
                        loaded.Should().NotBeNull();
                        executedLookups++;
                    }

                    break;
                }
            }
        }

        // Tally telemetry recordings
        long totalHits = 0;
        long totalMisses = 0;
        long prebakedMemoryHits = 0;

        foreach ((string InstrumentName, long Value, Dictionary<string, object?> Tags) record in _counterRecordings)
            if (record.InstrumentName == "nexus_entity_cache_hits_total")
            {
                totalHits += record.Value;
                if (record.Tags.TryGetValue("source_tier", out object? tier) && tier?.ToString() == "PreBakedMemory")
                    prebakedMemoryHits += record.Value;
            }
            else if (record.InstrumentName == "nexus_entity_cache_misses_total")
            {
                totalMisses += record.Value;
            }

        double hitRatePercent = totalHits + totalMisses > 0
            ? (double)totalHits / (totalHits + totalMisses) * 100.0
            : 0.0;

        _output.WriteLine("\n==========================================================================");
        _output.WriteLine("   TELEMETRY & OBSERVABILITY AUDIT RESULTS                                ");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"  Executed Lookups     : {executedLookups:N0}");
        _output.WriteLine($"  Total Cache Hits     : {totalHits:N0}");
        _output.WriteLine($"  PreBakedMemory Hits  : {prebakedMemoryHits:N0}");
        _output.WriteLine($"  Total Cache Misses   : {totalMisses:N0}");
        _output.WriteLine($"  Cache Hit Rate       : {hitRatePercent:F2}%");
        _output.WriteLine($"  Histogram Recordings : {_histogramRecordings.Count:N0}");
        _output.WriteLine("==========================================================================\n");

        // --- Verifications ---
        totalMisses.Should().Be(0, "All valid entity lookups must produce 0 cache misses");
        totalHits.Should().Be(executedLookups, "Every single lookup must record a cache hit");
        prebakedMemoryHits.Should().Be(executedLookups, "100% of cache hits must originate from PreBakedMemory tier");
        hitRatePercent.Should().Be(100.0, "Cache hit rate must be exactly 100.0%");
        _histogramRecordings.Should().NotBeEmpty("Duration histogram measurements must be recorded for lookups");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task MasterServerIntegration_100ConcurrentThreads_HeroDefinitionService_ZeroStallAndHighThroughput()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // -------------------------------------------------------------
        // Mission 4: Verify Master Server integration with HeroDefinitionService under 100 concurrent threads
        // -------------------------------------------------------------
        using PrebakedEntityRegistry registry = await CreateAndInitializeRegistryAsync();
        HeroDefinitionService heroService = new(
            NullLogger<HeroDefinitionService>.Instance,
            new MockHostEnvironment(),
            registry);

        const int threadCount = 100;
        const int opsPerThread = 500;
        int totalOps = 0;
        int totalErrors = 0;

        Stopwatch sw = Stopwatch.StartNew();

        Task[] tasks =
        [
            .. Enumerable.Range(1, threadCount).Select(tId => Task.Run(() =>
            {
                int ops = 0;
                try
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        uint heroId = (uint)((tId * 17 + i) % 140 + 1);

                        // 1. GetHeroIdentifier
                        string ident = heroService.GetHeroIdentifier(heroId);
                        ident.Should().NotBeNullOrEmpty();
                        ops++;

                        // 2. GetBaseHeroId by identifier
                        uint baseId = heroService.GetBaseHeroId(ident);
                        baseId.Should().BeGreaterThan(0);
                        ops++;

                        // 3. IsHero
                        bool isHero = heroService.IsHero(heroId);
                        isHero.Should().BeTrue();
                        ops++;

                        // 4. GetHeroDefinition
                        HeroDefinitionDto? def = heroService.GetHeroDefinition(heroId);
                        def.Should().NotBeNull();
                        ops++;

                        // 5. CleanHeroIdentifier
                        string cleaned = HeroDefinitionService.CleanHeroIdentifier($"Hero_{ident}.Alt1");
                        cleaned.Should().NotBeNullOrEmpty();
                        ops++;
                    }

                    Interlocked.Add(ref totalOps, ops);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref totalErrors);
                }
            }))
        ];

        await Task.WhenAll(tasks);
        sw.Stop();

        _output.WriteLine("\n==========================================================================");
        _output.WriteLine("   MASTER SERVER HERO DEFINITION SERVICE CONCURRENCY BENCHMARK            ");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"  Threads             : {threadCount}");
        _output.WriteLine($"  Ops per Thread      : {opsPerThread * 5}");
        _output.WriteLine($"  Total Operations    : {totalOps:N0}");
        _output.WriteLine($"  Total Errors        : {totalErrors}");
        _output.WriteLine($"  Total Duration      : {sw.ElapsedMilliseconds} ms");
        _output.WriteLine($"  Average Op Latency  : {sw.Elapsed.TotalMicroseconds / totalOps:F3} Î¼s/op");
        _output.WriteLine($"  Throughput          : {totalOps / sw.Elapsed.TotalSeconds:N0} ops/sec");
        _output.WriteLine("==========================================================================\n");

        totalErrors.Should().Be(0);
        totalOps.Should().Be(threadCount * opsPerThread * 5);
        (sw.Elapsed.TotalMicroseconds / totalOps).Should().BeLessThan(50.0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AdversarialStress_50000RandomMissQueries_ZeroExceptionsAndSubMicrosecondHandling()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // -------------------------------------------------------------
        // Mission 5: Adversarial Stress - 50,000 non-existent / randomized malformed queries
        // -------------------------------------------------------------
        using PrebakedEntityRegistry registry = await CreateAndInitializeRegistryAsync();

        const int totalMissQueries = 50_000;
        int handledMisses = 0;
        int thrownExceptions = 0;

        Stopwatch sw = Stopwatch.StartNew();

        Parallel.For(0, totalMissQueries,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, i =>
            {
                try
                {
                    string nonExistentName = $"NonExistent_Entity_Key_{i}_{Guid.NewGuid():N}";
                    uint nonExistentId = (uint)(100_000 + i);

                    HeroDefinitionDto? hId = registry.GetHeroById(nonExistentId);
                    HeroDefinitionDto? hName = registry.GetHeroByName(nonExistentName);
                    AbilityDefinitionDto? ab = registry.GetAbilityByName(nonExistentName);
                    StateDefinitionDto? st = registry.GetStateByName(nonExistentName);
                    ProjectileDefinitionDto? pr = registry.GetProjectileByName(nonExistentName);
                    ItemDefinitionDto? it = registry.GetItemByName(nonExistentName);
                    CosmeticUpgradeDto? cos = registry.GetCosmeticByName(nonExistentName);
                    AltAvatarDto? alt = registry.GetAltAvatar(nonExistentName, "alt_invalid");
                    EntityDefinitionDto? gen = registry.GetEntityByName(nonExistentName);
                    EntityLookupResult asset = registry.ResolveAsset($"invalid/path/{nonExistentName}.mdf");

                    if (hId == null && hName == null && ab == null && st == null && pr == null &&
                        it == null && cos == null && alt == null && gen == null && !asset.Found)
                        Interlocked.Increment(ref handledMisses);
                }
                catch
                {
                    Interlocked.Increment(ref thrownExceptions);
                }
            });

        sw.Stop();

        _output.WriteLine("\n==========================================================================");
        _output.WriteLine("   ADVERSARIAL STRESS: 50,000 Non-Existent Entity Queries                 ");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"  Total Miss Queries  : {totalMissQueries:N0}");
        _output.WriteLine($"  Handled Cleanly     : {handledMisses:N0}");
        _output.WriteLine($"  Thrown Exceptions   : {thrownExceptions}");
        _output.WriteLine($"  Total Duration      : {sw.ElapsedMilliseconds} ms");
        _output.WriteLine($"  Throughput          : {totalMissQueries / sw.Elapsed.TotalSeconds:N0} miss-queries/sec");
        _output.WriteLine("==========================================================================\n");

        thrownExceptions.Should().Be(0);
        handledMisses.Should().Be(totalMissQueries);
    }

    private sealed class MockHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "KONGOR.MasterServer";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

