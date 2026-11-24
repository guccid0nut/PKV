// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Xml.Linq;
using ASPIRE.Common.Prebaking.Generators;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Serialization;

/// <summary>
///     Comprehensive verification test suite for FlatBuffers binary serialization and zero-copy loading
///     (schemas/hero_descriptors.fbs).
/// </summary>
public sealed class HeroFlatBufferSerializationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private static EntityCacheContainer CreateTestContainer()
    {
        return new EntityCacheContainer
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 1,
                    Name = "Hero_Accursed",
                    DisplayName = "Accursed",
                    Path = "/heroes/accursed/hero.entity",
                    DefaultIcon = "/heroes/accursed/icon.tga",
                    DefaultModel = "/heroes/accursed/model.mdf",
                    Category = "Hellbourne",
                    PrimaryAttribute = "strength",
                    AttackType = "Melee",
                    Strength = 23.0f,
                    StrengthPerLevel = 2.7f,
                    Agility = 17.0f,
                    AgilityPerLevel = 1.5f,
                    Intelligence = 21.0f,
                    IntelligencePerLevel = 2.0f,
                    Armor = 2.4f,
                    MagicArmor = 5.5f,
                    AttackDamageMin = 51.0f,
                    AttackDamageMax = 53.0f,
                    AttackRange = 128.0f,
                    MoveSpeed = 300.0f,
                    Difficulty = 2.0f,
                    SoloRating = 4,
                    CarryRating = 2,
                    SupportRating = 3,
                    InitiatorRating = 4,
                    GankerRating = 1,
                    Abilities =
                    [
                        new AbilityDefinitionDto
                        {
                            Slot = 0,
                            Name = "Ability_Accursed1",
                            DisplayName = "Cauterize",
                            IconPath = "/heroes/accursed/ability_01/icon.tga",
                            ManaCost = "50,60,70,80",
                            CooldownTime = "4500",
                            ActionType = "target_entity",
                            TargetRadius = "0",
                            CastRange = "800"
                        },
                        new AbilityDefinitionDto
                        {
                            Slot = 3,
                            Name = "Ability_Accursed4",
                            DisplayName = "Flame Shield",
                            IconPath = "/heroes/accursed/ability_04/icon.tga",
                            ManaCost = "100,110,120,130",
                            CooldownTime = "14000",
                            ActionType = "target_entity",
                            TargetRadius = "0",
                            CastRange = "700"
                        }
                    ],
                    AltAvatars =
                    [
                        new AltAvatarDto
                        {
                            Key = "Hero_Accursed.Alt",
                            HeroName = "Hero_Accursed",
                            DisplayName = "Lord Accursed",
                            IconPath = "/heroes/accursed/alt/icon.tga",
                            ModelPath = "/heroes/accursed/alt/model.mdf",
                            EffectPath = "/heroes/accursed/alt/effects/body.effect",
                            PreviewScale = 1.2f
                        }
                    ],
                    InventorySlots = ["Item_Bottle", "Item_PostHaste", "Item_FrostfieldPlate"],
                    PreloadResources = ["/heroes/accursed/model.mdf", "/heroes/accursed/alt/model.mdf"]
                },
                new HeroDefinitionDto
                {
                    Id = 2,
                    Name = "Hero_Valkyrie",
                    DisplayName = "Valkyrie",
                    Path = "/heroes/valkyrie/hero.entity",
                    DefaultIcon = "/heroes/valkyrie/icon.tga",
                    DefaultModel = "/heroes/valkyrie/model.mdf",
                    Category = "Legion",
                    PrimaryAttribute = "agility",
                    AttackType = "Ranged",
                    Strength = 18.0f,
                    StrengthPerLevel = 1.9f,
                    Agility = 21.0f,
                    AgilityPerLevel = 2.75f,
                    Intelligence = 19.0f,
                    IntelligencePerLevel = 1.65f,
                    Armor = 1.94f,
                    MagicArmor = 5.5f,
                    AttackDamageMin = 38.0f,
                    AttackDamageMax = 49.0f,
                    AttackRange = 600.0f,
                    MoveSpeed = 300.0f,
                    Difficulty = 4.0f,
                    SoloRating = 3,
                    CarryRating = 4,
                    SupportRating = 1,
                    InitiatorRating = 4,
                    GankerRating = 4
                }
            ],
            Items =
            [
                new ItemDefinitionDto
                {
                    Name = "Item_Bottle",
                    DisplayName = "Bottle",
                    IconPath = "/items/bottle/icon.tga",
                    Cost = 600,
                    Category = "Consumables"
                },
                new ItemDefinitionDto
                {
                    Name = "Item_PostHaste",
                    DisplayName = "Striders",
                    IconPath = "/items/striders/icon.tga",
                    Cost = 1450,
                    Category = "Boots"
                }
            ]
        };
    }

    [Fact]
    public void GenerateAndLoad_Roundtrip_RestoresAllFieldsAndChecksum()
    {
        // Arrange
        EntityCacheContainer original = CreateTestContainer();

        // Act
        byte[] flatBufferBytes = HeroFlatBufferGenerator.Generate(original);
        flatBufferBytes.Length.Should().BeGreaterThan(32);

        HeroFlatBufferLoader loader = HeroFlatBufferLoader.Load(flatBufferBytes);

        // Assert Manifest
        loader.Manifest.Version.Should().Be(HeroFlatBufferGenerator.SchemaVersion);
        loader.Manifest.HeroCount.Should().Be(2);
        loader.Manifest.ItemCount.Should().Be(2);
        loader.Manifest.ChecksumXxHash3.Should().BeGreaterThan(0);
        loader.HeroCount.Should().Be(2);
        loader.ItemCount.Should().Be(2);

        // Assert Hero 1 (Accursed)
        HeroDescriptorRecord? accursed = loader.GetHero("Hero_Accursed");
        accursed.Should().NotBeNull();
        accursed!.Id.Should().Be(1);
        accursed.DisplayName.Should().Be("Accursed");
        accursed.PrimaryAttribute.Should().Be(FlatBufferAttributeType.Strength);
        accursed.AttackType.Should().Be(FlatBufferAttackType.Melee);
        accursed.Strength.Should().Be(23.0f);
        accursed.StrengthPerLevel.Should().Be(2.7f);
        accursed.Agility.Should().Be(17.0f);
        accursed.Armor.Should().Be(2.4f);
        accursed.SoloRating.Should().Be(4);
        accursed.CarryRating.Should().Be(2);

        // Assert Abilities
        accursed.Abilities.Should().HaveCount(2);
        accursed.Abilities[0].Name.Should().Be("Ability_Accursed1");
        accursed.Abilities[0].DisplayName.Should().Be("Cauterize");
        accursed.Abilities[0].CastRange.Should().Be(800.0f);

        // Assert Alt Avatars
        accursed.AltAvatars.Should().HaveCount(1);
        accursed.AltAvatars[0].Key.Should().Be("Hero_Accursed.Alt");
        accursed.AltAvatars[0].DisplayName.Should().Be("Lord Accursed");
        accursed.AltAvatars[0].PreviewScale.Should().Be(1.2f);

        // Assert Inventory & Preload
        accursed.RecommendedItems.Should().Contain("Item_Bottle");
        accursed.RecommendedItems.Should().Contain("Item_PostHaste");
        accursed.PreloadResources.Should().Contain("/heroes/accursed/model.mdf");

        // Assert Hero 2 (Valkyrie)
        HeroDescriptorRecord? valk = loader.GetHero("hero_valkyrie");
        valk.Should().NotBeNull();
        valk!.Id.Should().Be(2);
        valk.PrimaryAttribute.Should().Be(FlatBufferAttributeType.Agility);
        valk.AttackType.Should().Be(FlatBufferAttackType.Ranged);
        valk.AttackRange.Should().Be(600.0f);

        // Assert Items
        ItemDescriptorRecord? bottle = loader.GetItem("Item_Bottle");
        bottle.Should().NotBeNull();
        bottle!.Cost.Should().Be(600);
        bottle.Category.Should().Be("Consumables");
    }

    [Fact]
    public void HeroFlatBufferLoader_Lookups_PerformO1LookupsByNameAndId()
    {
        EntityCacheContainer original = CreateTestContainer();
        byte[] flatBufferBytes = HeroFlatBufferGenerator.Generate(original);
        HeroFlatBufferLoader loader = HeroFlatBufferLoader.Load(flatBufferBytes);

        // Lookup by Name (case-insensitive)
        loader.HasHero("Hero_Accursed").Should().BeTrue();
        loader.HasHero("hero_accursed").Should().BeTrue();
        loader.HasHero("HERO_ACCURSED").Should().BeTrue();
        loader.HasHero("Hero_NonExistent").Should().BeFalse();

        // Lookup by ID
        loader.GetHeroById(1).Should().NotBeNull();
        loader.GetHeroById(1)!.Name.Should().Be("Hero_Accursed");
        loader.GetHeroById(2)!.Name.Should().Be("Hero_Valkyrie");
        loader.GetHeroById(999).Should().BeNull();

        // Item Lookup
        loader.HasItem("Item_Bottle").Should().BeTrue();
        loader.HasItem("item_bottle").Should().BeTrue();
        loader.HasItem("Item_Fake").Should().BeFalse();
        loader.GetItem("Item_PostHaste")!.Cost.Should().Be(1450);
    }

    [Fact]
    public async Task HeroFlatBufferLoader_LoadFromFile_RoundtripsFromIsolatedTempFile()
    {
        // protects: HeroFlatBufferLoader.LoadFromFile disk I/O + checksum contract without mutating live repo override/ directories.
        // fails_when: LoadFromFile corrupts buffer length, fails checksum validation, or drops serialized descriptors.
        // seam: HeroFlatBufferGenerator.Generate + real filesystem temp file + HeroFlatBufferLoader.LoadFromFile.
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_fb_roundtrip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EntityCacheContainer original = CreateTestContainer();
            byte[] flatBufferBytes = HeroFlatBufferGenerator.Generate(original);
            string tempFbPath = Path.Combine(tempDir, "hero_descriptors.bin");
            await File.WriteAllBytesAsync(tempFbPath, flatBufferBytes);

            HeroFlatBufferLoader loader = HeroFlatBufferLoader.LoadFromFile(tempFbPath);
            loader.HeroCount.Should().Be(2);
            loader.Manifest.Version.Should().Be(HeroFlatBufferGenerator.SchemaVersion);
            loader.Manifest.ChecksumXxHash3.Should().BeGreaterThan(0);

            HeroDescriptorRecord? accursed = loader.GetHero("Hero_Accursed");
            accursed.Should().NotBeNull();
            accursed!.DisplayName.Should().Be("Accursed");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task HeroFlatBufferSerialization_GeneratePrebakedHeroDescriptorsBin_WritesToRepoPrebakedAssets()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string current = AppContext.BaseDirectory;
        string repoRoot = current;
        while (!string.IsNullOrEmpty(repoRoot))
        {
            if (File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(repoRoot, "games", "client")))
                break;
            string? parent = Path.GetDirectoryName(repoRoot);
            if (string.IsNullOrEmpty(parent) || parent == repoRoot) break;
            repoRoot = parent;
        }

        string clientDir = Path.Combine(repoRoot, "games", "client");
        string sharedDir = Path.Combine(repoRoot, "games", "shared");
        string heroesXmlPath = Path.Combine(clientDir, "game", "heroes.xml");

        if (!File.Exists(heroesXmlPath)) return;

        int expectedHeroCount = XDocument.Load(heroesXmlPath).Root!.Elements("hero").Count();
        expectedHeroCount.Should().BeGreaterThanOrEqualTo(140);

        MasterXmlParser masterParser = new();
        EntityCacheContainer container = await masterParser.ParseMasterDataAsync(clientDir, sharedDir);

        container.Heroes.Should().HaveCount(expectedHeroCount);

        // Generate FlatBuffers binary
        byte[] flatBufferBytes = HeroFlatBufferGenerator.Generate(container);
        flatBufferBytes.Length.Should().BeGreaterThan(1000);

        // Stage into an isolated temp directory during test execution so offline test runs never mutate games/client/override/
        string tempStageRoot = Path.Combine(Path.GetTempPath(), $"kongor_prebaked_stage_{Guid.NewGuid():N}");
        try
        {
            string overridePrebakedDir = Path.Combine(tempStageRoot, "games", "client", "override", "prebaked_assets");
            Directory.CreateDirectory(overridePrebakedDir);
            string overrideFbPath = Path.Combine(overridePrebakedDir, "hero_descriptors.bin");
            await File.WriteAllBytesAsync(overrideFbPath, flatBufferBytes);
            File.Exists(overrideFbPath).Should().BeTrue();

            HeroFlatBufferLoader loader = HeroFlatBufferLoader.LoadFromFile(overrideFbPath);
            loader.HeroCount.Should().Be(expectedHeroCount);
            loader.Manifest.Version.Should().Be(1);
            loader.Manifest.ChecksumXxHash3.Should().BeGreaterThan(0);

            HeroDescriptorRecord? accursed = loader.GetHero("Hero_Accursed");
            accursed.Should().NotBeNull();
            accursed!.DisplayName.Should().Be("Accursed");
        }
        finally
        {
            if (Directory.Exists(tempStageRoot))
            {
                Directory.Delete(tempStageRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void HeroFlatBufferLoader_InvalidMagicOrCorruptedBuffer_ThrowsInvalidDataException()
    {
        // 1. Truncated buffer
        byte[] truncated = new byte[16];
        Action actTruncated = () => HeroFlatBufferLoader.Load(truncated);
        actTruncated.Should().Throw<InvalidDataException>();

        // 2. Bad magic bytes
        byte[] badMagic = new byte[64];
        badMagic[0] = 8;
        badMagic[4] = (byte)'X';
        badMagic[5] = (byte)'X';
        badMagic[6] = (byte)'X';
        badMagic[7] = (byte)'X';

        Action actBadMagic = () => HeroFlatBufferLoader.Load(badMagic);
        actBadMagic.Should().Throw<InvalidDataException>()
            .WithMessage("*Invalid FlatBuffer identifier*");

        // 3. Flipped payload byte triggers XxHash3 checksum mismatch
        byte[] valid = HeroFlatBufferGenerator.Generate(CreateTestContainer());
        valid[^1] ^= 0xFF;
        Action actCorruptedPayload = () => HeroFlatBufferLoader.Load(valid);
        actCorruptedPayload.Should().Throw<InvalidDataException>()
            .WithMessage("*checksum mismatch*");

        // 4. Timestamp is preserved without upper-dword clobbering
        ulong nowSec = (ulong)TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        byte[] fresh = HeroFlatBufferGenerator.Generate(CreateTestContainer());
        HeroFlatBufferLoader freshLoader = HeroFlatBufferLoader.Load(fresh);
        freshLoader.Manifest.GeneratedTimestampUtc.Should().BeInRange(nowSec - 10UL, nowSec + 10UL);
        (freshLoader.Manifest.ChecksumXxHash3 >> 32).Should().NotBe(0UL);
    }

    [Fact]
    public void HeroFlatBufferLoader_OnDiskPrebakedAsset_LoadsAndVerifiesLegacyOrCurrentV1Checksum()
    {
        // protects: Backwards-compatibility with prebaked_assets/hero_descriptors.bin on disk (legacy offset-16 and current offset-20 v1 checksums).
        // fails_when: Loader rejects existing prebaked_assets/hero_descriptors.bin or fails to reject bit-flipped corruption on legacy buffers.
        // seam: Real prebaked_assets/hero_descriptors.bin file on disk + HeroFlatBufferLoader.Load.
        string repoRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")))
        {
            string? parent = Path.GetDirectoryName(repoRoot);
            if (string.IsNullOrEmpty(parent) || parent == repoRoot) break;
            repoRoot = parent;
        }

        string onDiskPath = Path.Combine(repoRoot, "games", "client", "override", "prebaked_assets", "hero_descriptors.bin");
        if (!File.Exists(onDiskPath))
        {
            return;
        }

        byte[] onDiskBytes = File.ReadAllBytes(onDiskPath);
        HeroFlatBufferLoader loader = HeroFlatBufferLoader.Load(onDiskBytes);
        loader.HeroCount.Should().BeGreaterThanOrEqualTo(140);
        loader.ItemCount.Should().BeGreaterThanOrEqualTo(200);
        loader.Manifest.ChecksumXxHash3.Should().NotBe(0UL);

        // Flip a byte in the legacy buffer and ensure XxHash3 verification rejects it
        onDiskBytes[^1] ^= 0x01;
        Action actTampered = () => HeroFlatBufferLoader.Load(onDiskBytes);
        actTampered.Should().Throw<InvalidDataException>()
            .WithMessage("*checksum mismatch*");
    }

    [Fact]
    public void HeroFlatBuffer_FullScaleBenchmark_MeasuresGenerateAndLoadLatencyAndAllocations()
    {
        // protects: Zero-buffer-copy Span<byte> throughput and GC allocation budget on 145-hero KNFB payloads.
        // fails_when: HeroFlatBufferLoader regresses to full buffer.ToArray()/BinaryReader heap copies or slow parsing.
        // seam: Real HeroFlatBufferGenerator.Generate + HeroFlatBufferLoader.Load across 145 heroes, 580 abilities, 870 avatars, 220 items.
        EntityCacheContainer scaleContainer = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O")
        };

        for (int h = 0; h < 145; h++)
        {
            HeroDefinitionDto hero = new()
            {
                Id = (uint)(h + 1),
                Name = $"Hero_Bench_{h}",
                DisplayName = $"Bench Hero {h}",
                Path = $"/heroes/hero_{h}/hero.entity",
                DefaultIcon = $"/heroes/hero_{h}/icon.tga",
                DefaultModel = $"/heroes/hero_{h}/model.mdf",
                Category = h % 2 == 0 ? "Legion" : "Hellbourne",
                PrimaryAttribute = h % 3 == 0 ? "strength" : h % 3 == 1 ? "agility" : "intelligence",
                AttackType = h % 2 == 0 ? "Melee" : "Ranged",
                Strength = 22.0f,
                StrengthPerLevel = 2.5f,
                Agility = 18.0f,
                AgilityPerLevel = 2.1f,
                Intelligence = 20.0f,
                IntelligencePerLevel = 1.9f,
                Armor = 3.1f,
                MagicArmor = 5.5f,
                AttackDamageMin = 48.0f,
                AttackDamageMax = 54.0f,
                AttackRange = h % 2 == 0 ? 128.0f : 600.0f,
                MoveSpeed = 300.0f,
                Difficulty = 3.0f,
                SoloRating = 3,
                CarryRating = 4,
                SupportRating = 2,
                InitiatorRating = 3,
                GankerRating = 3,
                InventorySlots = ["Item_Bottle", "Item_PostHaste", "Item_PortalKey", "Item_ShrunkenHead", "Item_SavageMace", "Item_BehemothsHeart"],
                PreloadResources = [$"/heroes/hero_{h}/model.mdf", $"/heroes/hero_{h}/effects/body.effect", $"/heroes/hero_{h}/sounds/attack.wav"]
            };

            for (int a = 0; a < 4; a++)
            {
                hero.Abilities.Add(new AbilityDefinitionDto
                {
                    Slot = a,
                    Name = $"Ability_Bench_{h}_{a}",
                    DisplayName = $"Bench Ability {h}-{a}",
                    IconPath = $"/heroes/hero_{h}/ability_{a}/icon.tga",
                    ManaCost = "100,110,120,130",
                    CooldownTime = "12000",
                    ActionType = a == 2 ? "passive" : "target_entity",
                    TargetRadius = "300",
                    CastRange = "650"
                });
            }

            for (int av = 0; av < 6; av++)
            {
                hero.AltAvatars.Add(new AltAvatarDto
                {
                    Key = $"Hero_Bench_{h}.Alt{av}",
                    HeroName = $"Hero_Bench_{h}",
                    DisplayName = $"Avatar {h}-{av}",
                    IconPath = $"/heroes/hero_{h}/alt{av}/icon.tga",
                    ModelPath = $"/heroes/hero_{h}/alt{av}/model.mdf",
                    EffectPath = $"/heroes/hero_{h}/alt{av}/body.effect",
                    PreviewScale = 1.1f
                });
            }

            scaleContainer.Heroes.Add(hero);
        }

        for (int it = 0; it < 220; it++)
        {
            scaleContainer.Items.Add(new ItemDefinitionDto
            {
                Name = $"Item_Bench_{it}",
                DisplayName = $"Bench Item {it}",
                IconPath = $"/items/item_{it}/icon.tga",
                Cost = 500 + (it * 25),
                Category = it % 4 == 0 ? "Consumables" : "Combative"
            });
        }

        // Warmup JIT
        for (int w = 0; w < 5; w++)
        {
            byte[] warmBuf = HeroFlatBufferGenerator.Generate(scaleContainer);
            _ = HeroFlatBufferLoader.Load(warmBuf);
        }

        const int iterations = 25;
        List<double> genMs = new(iterations);
        List<double> loadMs = new(iterations);

        long genAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        byte[] sampleBuffer = [];
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            sampleBuffer = HeroFlatBufferGenerator.Generate(scaleContainer);
            sw.Stop();
            genMs.Add(sw.Elapsed.TotalMilliseconds);
        }
        long genAllocPerCall = (GC.GetAllocatedBytesForCurrentThread() - genAllocBefore) / iterations;

        long loadAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        HeroFlatBufferLoader? lastLoader = null;
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            lastLoader = HeroFlatBufferLoader.Load(sampleBuffer);
            sw.Stop();
            loadMs.Add(sw.Elapsed.TotalMilliseconds);
        }
        long loadAllocPerCall = (GC.GetAllocatedBytesForCurrentThread() - loadAllocBefore) / iterations;

        genMs.Sort();
        loadMs.Sort();
        double genMedianMs = genMs[iterations / 2];
        double loadMedianMs = loadMs[iterations / 2];

        _output.WriteLine(
            $"[KNFB-BENCH] Payload={sampleBuffer.Length:N0} B | Generate: Median={genMedianMs:F3} ms, Alloc={genAllocPerCall:N0} B | Load: Median={loadMedianMs:F3} ms, Alloc={loadAllocPerCall:N0} B");

        lastLoader.Should().NotBeNull();
        lastLoader!.HeroCount.Should().Be(145);
        lastLoader.ItemCount.Should().Be(220);
    }
}
