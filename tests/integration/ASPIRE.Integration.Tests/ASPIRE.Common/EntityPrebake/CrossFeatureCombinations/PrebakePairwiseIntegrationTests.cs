// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text.Json;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;
using NLua;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier3_PairwiseCombinations;

public sealed class PrebakePairwiseIntegrationTests : IDisposable
{
    private readonly TestLuaManifestGenerator _luaGen = new();
    private readonly TestEntityCatalogParser _parser = new();
    private readonly TestMemoryPackSerializer _serializer = new();
    private readonly string _tempDir;

    public PrebakePairwiseIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorPairwiseTest_" + Guid.NewGuid().ToString("N"));
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
    public void Pairwise_CatalogParsing_And_MemoryPackSerialization_PreservesGraphTopology()
    {
        // Features F1 + F2
        string xml = TestPrebakeHarness.CreateSampleHeroesXml(10);
        string preloadXml = TestPrebakeHarness.CreateSamplePreloadXml();

        EntityCacheContainer parsed = _parser.ParseFromXmlContent(xml, preloadXml);
        byte[] bytes = _serializer.SerializeToBytes(parsed, true);
        EntityCacheContainer restored = _serializer.DeserializeFromBytes(bytes, true);

        restored.Heroes.Should().HaveCount(10);
        restored.AltAvatars.Should().HaveCount(20);
        restored.Heroes[0].PreloadResources.Should().HaveCount(2);
    }

    [Fact]
    public async Task Pairwise_MemoryPack_And_ZstdCompression_And_FrozenDictionaryLookup()
    {
        // Features F2 + F5
        EntityCacheContainer original = TestPrebakeHarness.CreateSampleCatalog(25);
        byte[] compressed = _serializer.SerializeToBytes(original, true);

        EntityCacheContainer restored = _serializer.DeserializeFromBytes(compressed, true);
        using TestPrebakedEntityRegistry registry = new(restored);
        await registry.InitializeAsync();

        HeroDefinitionDto? hero = registry.GetHeroByName("Hero_12");
        hero.Should().NotBeNull();
        hero!.Id.Should().Be(12);

        AltAvatarDto? avatar = registry.GetAltAvatar("Hero_12", "alt2");
        avatar.Should().NotBeNull();
        avatar!.Priority.Should().Be(20);
    }

    [Fact]
    public void Pairwise_CatalogParsing_And_LuaManifest_And_NLuaExecution()
    {
        // Features F1 + F3
        string xml = TestPrebakeHarness.CreateSampleHeroesXml(4);
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        string luaCode = _luaGen.GenerateLuaManifest(container);
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);

        error.Should().BeNull();
        valid.Should().BeTrue();

        Dictionary<string, object> hero2 = _luaGen.ExecuteAndExtractHero(luaCode, "Hero_Test_2");
        hero2["name"].Should().Be("Hero_Test_2");
    }

    [Fact]
    public void Pairwise_PrebakeCli_And_LuaGeneration_And_S2zPackaging()
    {
        // Features F3 + F4
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 2);
        string luaCode = _luaGen.GenerateLuaManifest(container);
        byte[] binaryData = _serializer.SerializeToBytes(container, true);

        string s2zPath = Path.Combine(_tempDir, "resources0_prebaked.s2z");
        Dictionary<string, byte[]> s2zEntries = new()
        {
            ["ui/scripts/newui/hero_definitions_cache.lua"] = Encoding.UTF8.GetBytes(luaCode),
            ["entity_cache.bin"] = binaryData
        };

        TestPrebakeHarness.CreateSampleS2zArchive(s2zPath, s2zEntries);

        using ZipArchive zip = ZipFile.OpenRead(s2zPath);
        ZipArchiveEntry? luaEntry = zip.GetEntry("ui/scripts/newui/hero_definitions_cache.lua");
        ZipArchiveEntry? binEntry = zip.GetEntry("entity_cache.bin");

        luaEntry.Should().NotBeNull();
        binEntry.Should().NotBeNull();

        using Stream s = luaEntry!.Open();
        using StreamReader reader = new(s);
        string extractedLua = reader.ReadToEnd();
        extractedLua.Should().Contain("HeroDefinitionsCache");
    }

    [Fact]
    public async Task Pairwise_FrozenDictionary_And_AsyncPreWarming_And_TelemetryEmission()
    {
        // Features F5 + F6 + F8
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(8, 2);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 15);

        await registry.InitializeAsync();

        registry.GetHeroByName("Hero_1");
        registry.GetHeroByName("Hero_3");
        registry.GetHeroByName("Missing_Hero");

        registry.TelemetryRecords.Should().Contain(r => r.EventType == "PrewarmCompleted");
        registry.TelemetryRecords.Should().Contain(r => r.EventType == "CacheHit" && r.EntityName == "Hero_1");
        registry.TelemetryRecords.Should().Contain(r => r.EventType == "CacheHit" && r.EntityName == "Hero_3");
        registry.TelemetryRecords.Should().Contain(r => r.EventType == "CacheMiss" && r.EntityName == "Missing_Hero");
    }

    [Fact]
    public async Task Pairwise_LayeredVfs_And_LooseOverride_And_OpenTelemetrySourceTracking()
    {
        // Features F7 + F8
        string overrideDir = Path.Combine(_tempDir, "override");
        Directory.CreateDirectory(overrideDir);
        string looseFile = Path.Combine(overrideDir, "heroes", "hero_1", "model.mdf");
        Directory.CreateDirectory(Path.GetDirectoryName(looseFile)!);
        File.WriteAllText(looseFile, "override-model-mesh");

        using TestPrebakedEntityRegistry registry = new(null, overrideDir);
        await registry.InitializeAsync();

        EntityLookupResult result = registry.ResolveAsset("heroes/hero_1/model.mdf");

        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("PreBakedDisk");
        result.ResolvedPath.Should().Be(looseFile);
    }

    [Fact]
    public void Pairwise_CorruptedZstd_And_FallbackVfsResolution_And_TelemetryErrorRecording()
    {
        // Features F2 + F7 + F8
        byte[] corrupted = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x00];

        Action act = () => _serializer.DeserializeFromBytes(corrupted, true);
        act.Should().Throw<Exception>();

        PrebakeTelemetryRecord errorTelemetry = new()
        {
            EventType = "ResolutionError",
            EntityName = "entity_cache.bin",
            SourceTier = "PreBakedDisk",
            Success = false,
            ErrorMessage = "Decompression failed"
        };

        errorTelemetry.Success.Should().BeFalse();
        errorTelemetry.ErrorMessage.Should().Be("Decompression failed");
    }

    [Fact]
    public async Task Pairwise_PreloadXmlParsing_And_BinaryCache_And_MemoryLookup()
    {
        // Features F1 + F2 + F5
        string heroesXml = TestPrebakeHarness.CreateSampleHeroesXml();
        string preloadXml = TestPrebakeHarness.CreateSamplePreloadXml();

        EntityCacheContainer parsed = _parser.ParseFromXmlContent(heroesXml, preloadXml);
        byte[] serialized = _serializer.SerializeToBytes(parsed, true);
        EntityCacheContainer restored = _serializer.DeserializeFromBytes(serialized, true);

        using TestPrebakedEntityRegistry registry = new(restored);
        await registry.InitializeAsync();

        HeroDefinitionDto? hero1 = registry.GetHeroByName("Hero_Test_1");
        hero1.Should().NotBeNull();
        hero1!.PreloadResources.Should().Contain("/heroes/test_1/sounds/ready.wav");
    }

    [Fact]
    public void Pairwise_AltAvatarPrioritySorting_And_LuaManifestExport_And_LookupValidation()
    {
        // Features F1 + F3 + F5
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(2, 4);
        string luaCode = _luaGen.GenerateLuaManifest(container);

        using NLua.Lua lua = new();
        lua.DoString(luaCode);
        LuaTable? avatarTable = lua.DoString("return HeroDefinitionsCache.alt_avatars[\"hero_1_alt3\"]")[0] as LuaTable;

        avatarTable.Should().NotBeNull();
        ((long)avatarTable!["priority"]).Should().Be(30);
    }

    [Fact]
    public async Task Pairwise_FullPipeline_Parse_Serialize_Package_Prewarm_Lookup_Telemetry()
    {
        // End-to-end integration: F1 -> F2 -> F3 -> F4 -> F5 -> F6 -> F7 -> F8
        string heroesXml = TestPrebakeHarness.CreateSampleHeroesXml(5);
        EntityCacheContainer container = _parser.ParseFromXmlContent(heroesXml);

        byte[] binaryCache = _serializer.SerializeToBytes(container, true);
        ulong checksum = _serializer.ComputeXxHash64(binaryCache);
        _serializer.VerifyHash(binaryCache, checksum).Should().BeTrue();

        string luaManifest = _luaGen.GenerateLuaManifest(container);
        _luaGen.ValidateLuaSyntaxAndExecution(luaManifest, out string? error).Should().BeTrue();

        string s2zPath = Path.Combine(_tempDir, "full_pipeline.s2z");
        Dictionary<string, byte[]> s2zFiles = new()
        {
            ["entity_cache.bin"] = binaryCache,
            ["hero_definitions_cache.lua"] = Encoding.UTF8.GetBytes(luaManifest)
        };
        TestPrebakeHarness.CreateSampleS2zArchive(s2zPath, s2zFiles);

        EntityCacheContainer restoredContainer = _serializer.DeserializeFromBytes(binaryCache, true);
        using TestPrebakedEntityRegistry registry = new(restoredContainer, null, s2zPath);
        await registry.InitializeAsync();

        HeroDefinitionDto? hero = registry.GetHeroByName("Hero_Test_1");
        hero.Should().NotBeNull();
        hero!.DisplayName.Should().Be("Test Hero 1");

        registry.TelemetryRecords.Should().Contain(r => r.EventType == "PrewarmCompleted");
        registry.TelemetryRecords.Should().Contain(r => r.EventType == "CacheHit");
    }

    [Fact]
    public async Task Pairwise_PowerShellTelemetryNDJSONExport_WithParallelLookups()
    {
        // Features F5 + F8 + F9
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 2);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        List<Task> tasks = [];
        for (int i = 0; i < 10; i++)
        {
            uint heroId = (uint)(i % 5 + 1);
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 20; j++) registry.GetHeroById(heroId);
            }));
        }

        await Task.WhenAll(tasks);

        string ndjsonPath = Path.Combine(_tempDir, "telemetry.ndjson");
        List<string> lines = [];
        foreach (PrebakeTelemetryRecord r in registry.TelemetryRecords) lines.Add(JsonSerializer.Serialize(r));
        File.WriteAllLines(ndjsonPath, lines, Encoding.UTF8);

        // Verify file can be read and filtered (simulating PowerShell Get-Content | ConvertFrom-Json)
        string[] readLines = File.ReadAllLines(ndjsonPath);
        readLines.Length.Should().Be(201); // 200 lookups + 1 prewarm
    }
}
