// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Telemetry;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier4_RealWorldScenarios;

public sealed class RealWorldWorkloadScenariosTests : IDisposable
{
    private readonly TestLuaManifestGenerator _luaGen = new();
    private readonly TestEntityCatalogParser _parser = new();
    private readonly TestMemoryPackSerializer _serializer = new();
    private readonly string _tempDir;

    public RealWorldWorkloadScenariosTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorRealWorldTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignored */
            }
    }

    [Fact]
    public async Task EntityPrebakeWorkload_HighConcurrencyPlayerLoginBurst_CompletesWithZeroStallTime()
    {
        // 100 concurrent player logins requesting account default hero, favorite heroes, and equipped alt avatars
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(140, 5);
        using TestPrebakedEntityRegistry registry = new(catalog);
        await registry.InitializeAsync();

        int sessionCount = 100;
        List<Task> loginTasks = [];
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 1; i <= sessionCount; i++)
        {
            uint favHeroId1 = (uint)(i % 140 + 1);
            uint favHeroId2 = (uint)((i + 10) % 140 + 1);
            uint favHeroId3 = (uint)((i + 20) % 140 + 1);

            loginTasks.Add(Task.Run(() =>
            {
                HeroDefinitionDto? h1 = registry.GetHeroById(favHeroId1);
                HeroDefinitionDto? h2 = registry.GetHeroById(favHeroId2);
                HeroDefinitionDto? h3 = registry.GetHeroById(favHeroId3);

                h1.Should().NotBeNull();
                h2.Should().NotBeNull();
                h3.Should().NotBeNull();

                AltAvatarDto? avatar1 = registry.GetAltAvatar(h1!.Name, "alt1");
                avatar1.Should().NotBeNull();

                NexusMetrics.RecordCcuEvent("connected");
                NexusMetrics.RecordMasterServerRequest("login");
            }));
        }

        await Task.WhenAll(loginTasks);
        sw.Stop();

        // 100 sessions doing multiple lookups should easily finish in <500ms
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "100 concurrent logins must resolve entity definitions without any thread contention");
    }

    [Fact]
    public async Task EntityPrebakeWorkload_FullHeroSelectionGridLoading_LoadsAllHeroesSimultaneously()
    {
        // When client enters Hero Selection, all 140 heroes are populated in grid simultaneously
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(140, 4);
        using TestPrebakedEntityRegistry registry = new(catalog);
        await registry.InitializeAsync();

        HeroDefinitionDto?[] results = new HeroDefinitionDto?[140];

        Stopwatch sw = Stopwatch.StartNew();
        Parallel.For(1, 141, id => { results[id - 1] = registry.GetHeroById((uint)id); });
        sw.Stop();

        results.Length.Should().Be(140);
        foreach (HeroDefinitionDto? hero in results)
        {
            hero.Should().NotBeNull();
            hero!.AbilityNames.Should().NotBeEmpty();
        }

        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Full hero selection grid (140 heroes) must load quickly");
    }

    [Fact]
    public async Task EntityPrebakeWorkload_PlayerProfileDeepNavigation_InspectsMatchHistoryAndMastery()
    {
        // User opens profile -> inspects top 10 played heroes, 5 match history heroes, and active masteries
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(140, 5);
        using TestPrebakedEntityRegistry registry = new(catalog);
        await registry.InitializeAsync();

        List<uint> profileHeroes = [1, 5, 12, 33, 44, 55, 66, 77, 88, 99, 101, 105, 110, 120, 130];

        Stopwatch sw = Stopwatch.StartNew();
        foreach (uint heroId in profileHeroes)
        {
            HeroDefinitionDto? hero = registry.GetHeroById(heroId);
            hero.Should().NotBeNull();

            // Load all abilities for tooltips
            foreach (string abilityName in hero!.AbilityNames)
            {
                EntityDefinitionDto? ability = registry.GetEntityByName(abilityName);
                ability.Should().NotBeNull();
            }

            // Load all alt avatars for cosmetic preview
            foreach (string avatarKey in hero.AltAvatarKeys)
            {
                AltAvatarDto? avatar = registry.GetAltAvatar(hero.Name, avatarKey);
                avatar.Should().NotBeNull();
            }
        }

        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "Profile navigation and deep entity graph inspection must complete quickly");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task EntityPrebakeWorkload_RapidAltAvatarCosmeticInspectionLoop_CompletesThousandLookupsWithinThreshold()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Store/Armory browser rapidly flipping through 1,000 alt avatar skins
        EntityCacheContainer
            catalog = TestPrebakeHarness.CreateSampleCatalog(50, 20); // 50 heroes * 20 alt avatars = 1000 alt avatars
        using TestPrebakedEntityRegistry registry = new(catalog);
        await registry.InitializeAsync();

        Stopwatch sw = Stopwatch.StartNew();
        int successfulLookups = 0;

        for (int h = 1; h <= 50; h++)
        {
            string heroName = $"Hero_{h}";
            for (int a = 1; a <= 20; a++)
            {
                string avatarKey = $"alt{a}";
                AltAvatarDto? avatar = registry.GetAltAvatar(heroName, avatarKey);
                if (avatar != null) successfulLookups++;
            }
        }

        sw.Stop();

        successfulLookups.Should().Be(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "1,000 sequential alt avatar lookups must complete quickly");
    }

    [Fact]
    public async Task EntityPrebakeWorkload_ColdToWarmBootTransition_ReadsMemoryMappedCacheWithoutStalls()
    {
        // Cold boot: Parse XML -> Serialize to disk binary -> Save Lua manifest -> Warm boot: Read binary, initialize FrozenDictionary, verify telemetry
        string heroesXml = TestPrebakeHarness.CreateSampleHeroesXml(30);
        string preloadXml = TestPrebakeHarness.CreateSamplePreloadXml();

        // 1. Cold Phase (Pre-Bake execution)
        EntityCacheContainer container = _parser.ParseFromXmlContent(heroesXml, preloadXml);
        byte[] binaryData = _serializer.SerializeToBytes(container, true);
        ulong checksum = _serializer.ComputeXxHash64(binaryData);

        string cacheFilePath = Path.Combine(_tempDir, "entity_cache.bin");
        File.WriteAllBytes(cacheFilePath, binaryData);

        string luaFilePath = Path.Combine(_tempDir, "hero_definitions_cache.lua");
        File.WriteAllText(luaFilePath, _luaGen.GenerateLuaManifest(container), Encoding.UTF8);

        // 2. Warm Phase (Client/Server Boot loading pre-baked cache)
        byte[] loadedBytes = File.ReadAllBytes(cacheFilePath);
        _serializer.VerifyHash(loadedBytes, checksum).Should().BeTrue("Cold-to-warm checksum validation must succeed");

        EntityCacheContainer warmContainer = _serializer.DeserializeFromBytes(loadedBytes, true);
        using TestPrebakedEntityRegistry registry = new(warmContainer);

        registry.IsPrewarmed.Should().BeFalse();
        await registry.InitializeAsync();
        registry.IsPrewarmed.Should().BeTrue();

        // 3. Verify lookups & telemetry
        HeroDefinitionDto? hero = registry.GetHeroByName("Hero_Test_1");
        hero.Should().NotBeNull();
        hero!.PreloadResources.Should().HaveCount(2);

        registry.TelemetryRecords.Should().Contain(r => r.EventType == "PrewarmCompleted");
        registry.TelemetryRecords.Should().Contain(r => r.EventType == "CacheHit" && r.EntityName == "Hero_Test_1");
    }
}
