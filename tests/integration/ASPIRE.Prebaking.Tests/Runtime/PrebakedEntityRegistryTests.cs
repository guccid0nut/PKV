// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Common.Services.EntityCache;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Runtime;

public sealed class PrebakedEntityRegistryTests
{
    private static EntityCacheContainer CreateSampleContainer()
    {
        return new EntityCacheContainer
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("O"),
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 153,
                    Name = "Hero_Accursed",
                    DisplayName = "Accursed",
                    PrimaryAttribute = "Strength",
                    DefaultIcon = "/heroes/accursed/icon.tga",
                    DefaultModel = "hd_accursed/preview.mdf",
                    Abilities =
                    [
                        new AbilityDefinitionDto
                        {
                            Name = "Ability_Accursed1",
                            DisplayName = "Cauterize",
                            Icon = "/heroes/accursed/ability_01/icon.tga",
                            ManaCost = "75",
                            Cooldown = "6000"
                        }
                    ],
                    AltAvatars =
                    [
                        new AltAvatarDto
                        {
                            Key = "Hero_Accursed.Alt",
                            HeroName = "Hero_Accursed",
                            DisplayName = "Bloodstone Accursed",
                            DefaultModel = "/heroes/accursed/alt/preview.mdf"
                        }
                    ]
                },
                new HeroDefinitionDto
                {
                    Id = 26,
                    Name = "Hero_Pyromancer",
                    DisplayName = "Pyromancer",
                    PrimaryAttribute = "Intelligence",
                    DefaultIcon = "/heroes/pyromancer/icon.tga",
                    DefaultModel = "hd_pyromancer/preview.mdf"
                }
            ],
            Abilities =
            [
                new AbilityDefinitionDto
                {
                    Name = "Ability_Pyromancer1",
                    DisplayName = "Phoenix Wave",
                    Icon = "/heroes/pyromancer/ability_01/icon.tga",
                    ManaCost = "100,110,120,130",
                    Cooldown = "9000"
                }
            ],
            States =
            [
                new StateDefinitionDto
                {
                    Name = "State_Accursed_Ability2",
                    Icon = "/heroes/accursed/ability_02/icon.tga",
                    Duration = 5000,
                    EffectType = "StatusBuff"
                }
            ],
            Projectiles =
            [
                new ProjectileDefinitionDto
                {
                    Name = "Projectile_Accursed_Alt4_Dragon",
                    Model = "/heroes/accursed/projectiles/dragon.mdf",
                    Speed = 1200f
                }
            ],
            Items =
            [
                new ItemDefinitionDto
                {
                    Name = "Item_Bottle",
                    DisplayName = "Bottle",
                    Icon = "/items/bottle/icon.tga",
                    Cost = 600
                }
            ],
            CosmeticUpgrades =
            [
                new CosmeticUpgradeDto
                {
                    Name = "afghanistan",
                    Category = "AccountIcon",
                    Texture = "/ui/icons/flags/afghanistan.tga"
                }
            ]
        };
    }

    [Fact]
    public async Task PrebakedEntityRegistry_InitializeFromContainerAsync_PopulatesAllFrozenDictionaries()
    {
        EntityCacheContainer container = CreateSampleContainer();
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);

        Assert.False(registry.IsPrewarmed);
        Assert.Equal(0, registry.TotalEntitiesLoaded);

        await registry.InitializeFromContainerAsync(container);

        Assert.True(registry.IsPrewarmed);
        Assert.True(registry.TotalEntitiesLoaded > 0);
        Assert.NotNull(registry.Container);

        // Verify Heroes
        HeroDefinitionDto? heroById = registry.GetHeroById(153);
        Assert.NotNull(heroById);
        Assert.Equal("Hero_Accursed", heroById.Name);

        HeroDefinitionDto? heroByName = registry.GetHeroByName("Hero_Accursed");
        Assert.NotNull(heroByName);
        Assert.Equal(153u, heroByName.Id);

        // Case-insensitive & clean name
        HeroDefinitionDto? heroClean = registry.GetHeroByName("accursed");
        Assert.NotNull(heroClean);
        Assert.Equal(153u, heroClean.Id);

        // Display Name
        HeroDefinitionDto? heroDisp = registry.GetHeroByName("Accursed");
        Assert.NotNull(heroDisp);

        // Verify Abilities
        AbilityDefinitionDto? ability = registry.GetAbilityByName("Ability_Accursed1");
        Assert.NotNull(ability);
        Assert.Equal("Cauterize", ability.DisplayName);

        AbilityDefinitionDto? ability2 = registry.GetAbilityByName("Ability_Pyromancer1");
        Assert.NotNull(ability2);
        Assert.Equal("Phoenix Wave", ability2.DisplayName);

        // Verify States
        StateDefinitionDto? state = registry.GetStateByName("State_Accursed_Ability2");
        Assert.NotNull(state);
        Assert.Equal(5000, state.Duration);

        // Verify Projectiles
        ProjectileDefinitionDto? proj = registry.GetProjectileByName("Projectile_Accursed_Alt4_Dragon");
        Assert.NotNull(proj);
        Assert.Equal(1200f, proj.Speed);

        // Verify Items
        ItemDefinitionDto? item = registry.GetItemByName("Item_Bottle");
        Assert.NotNull(item);
        Assert.Equal(600, item.Cost);

        // Verify Cosmetics
        CosmeticUpgradeDto? cosmetic = registry.GetCosmeticByName("afghanistan");
        Assert.NotNull(cosmetic);
        Assert.Equal("AccountIcon", cosmetic.Category);

        // Verify Alt Avatars
        AltAvatarDto? alt = registry.GetAltAvatar("Hero_Accursed", "Hero_Accursed.Alt");
        Assert.NotNull(alt);
        Assert.Equal("Bloodstone Accursed", alt.DisplayName);

        // Verify Generic Entity Catalog
        EntityDefinitionDto? genHero = registry.GetEntityByName("Hero_Accursed");
        Assert.NotNull(genHero);
        Assert.Equal("Hero", genHero.EntityType);

        EntityDefinitionDto? genAbility = registry.GetEntityByName("Ability_Accursed1");
        Assert.NotNull(genAbility);
        Assert.Equal("Ability", genAbility.EntityType);

        EntityDefinitionDto? genItem = registry.GetEntityByName("Item_Bottle");
        Assert.NotNull(genItem);
        Assert.Equal("Item", genItem.EntityType);
    }

    [Fact]
    public async Task InitializeAsync_FromFile_LoadsAndVerifiesCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "nexus_reg_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string cacheFile = Path.Combine(tempDir, "entity_cache.bin");

        try
        {
            EntityCacheContainer sample = CreateSampleContainer();
            byte[] serialized = EntityCacheSerializer.Serialize(sample);
            await File.WriteAllBytesAsync(cacheFile, serialized);

            using PrebakedEntityRegistry registry = new(
                NullLogger<PrebakedEntityRegistry>.Instance,
                explicitCachePath: cacheFile);

            await registry.InitializeAsync();

            Assert.True(registry.IsPrewarmed);
            Assert.NotNull(registry.GetHeroById(153));
            Assert.NotNull(registry.GetHeroByName("Hero_Pyromancer"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InitializeAsync_Idempotent_DoubleCallIsSafe()
    {
        EntityCacheContainer container = CreateSampleContainer();
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);

        await registry.InitializeFromContainerAsync(container);
        Assert.True(registry.IsPrewarmed);

        // Second call should return immediately without error
        await registry.InitializeFromContainerAsync(container);
        Assert.True(registry.IsPrewarmed);
    }

    [Fact]
    public void PrebakedEntityRegistry_LookupsOnUninitializedRegistry_ReturnNullSafelyWithoutException()
    {
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);

        Assert.False(registry.IsPrewarmed);
        Assert.Null(registry.GetHeroById(999));
        Assert.Null(registry.GetHeroByName("Hero_Unknown"));
        Assert.Null(registry.GetAbilityByName("Ability_Unknown"));
        Assert.Null(registry.GetStateByName("State_Unknown"));
        Assert.Null(registry.GetProjectileByName("Projectile_Unknown"));
        Assert.Null(registry.GetItemByName("Item_Unknown"));
        Assert.Null(registry.GetCosmeticByName("Cosmetic_Unknown"));
        Assert.Null(registry.GetAltAvatar("Hero_Unknown", "Unknown_Alt"));
        Assert.Null(registry.GetEntityByName("Unknown"));

        EntityLookupResult result = registry.ResolveAsset("unknown/path");
        Assert.False(result.Found);
        Assert.Equal("NotFound", result.SourceTier);
    }

    [Fact]
    public async Task PrebakedEntityRegistry_EnumerationHelpers_ReturnAllDistinctEntities()
    {
        EntityCacheContainer container = CreateSampleContainer();
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        await registry.InitializeFromContainerAsync(container);

        Assert.NotEmpty(registry.GetAllHeroes());
        Assert.NotEmpty(registry.GetAllAbilities());
        Assert.NotEmpty(registry.GetAllStates());
        Assert.NotEmpty(registry.GetAllProjectiles());
        Assert.NotEmpty(registry.GetAllItems());
        Assert.NotEmpty(registry.GetAllCosmetics());
        Assert.NotEmpty(registry.GetAllAltAvatars());
    }
}
