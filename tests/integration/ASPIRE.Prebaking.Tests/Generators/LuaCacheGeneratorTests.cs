// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Generators;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using NLua;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Generators;

public sealed class LuaCacheGeneratorTests
{
    private static EntityCacheContainer CreateSampleContainer()
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
                    DefaultIcon = "/heroes/accursed/icon.tga",
                    DefaultModel = "hd_accursed/preview.mdf",
                    PrimaryAttribute = "strength",
                    Category = "Legion Strength Tank",
                    Difficulty = 2.5f,
                    Abilities =
                    [
                        new AbilityDefinitionDto
                        {
                            Slot = 0,
                            Name = "Ability_Accursed1",
                            DisplayName = "Cauterize",
                            Icon = "/heroes/accursed/ability_01/icon.tga"
                        }
                    ],
                    AltAvatars =
                    [
                        new AltAvatarDto
                        {
                            Key = "Hero_Accursed.Alt",
                            DisplayName = "Horseman Accursed",
                            HeroName = "Hero_Accursed",
                            IconPath = "/heroes/accursed/alt/icon.tga",
                            DefaultModel = "preview.mdf",
                            PreviewScale = 1.2f
                        }
                    ]
                }
            ],
            AltAvatars =
            [
                new AltAvatarDto
                {
                    Key = "Hero_Accursed.Alt",
                    DisplayName = "Horseman Accursed",
                    HeroName = "Hero_Accursed",
                    IconPath = "/heroes/accursed/alt/icon.tga",
                    DefaultModel = "preview.mdf",
                    PreviewScale = 1.2f
                }
            ],
            Abilities =
            [
                new AbilityDefinitionDto
                {
                    Name = "Ability_Accursed1",
                    DisplayName = "Cauterize",
                    Icon = "/heroes/accursed/ability_01/icon.tga",
                    Slot = 0,
                    ActionType = "target_entity",
                    ManaCost = "50,60,70,80",
                    CooldownTime = "4500"
                }
            ]
        };
    }

    [Fact]
    public void GenerateLuaCache_ProducesValidLuaSyntax_AndExecutesInNLua()
    {
        // Arrange
        EntityCacheContainer container = CreateSampleContainer();

        // Act
        string luaScript = LuaCacheGenerator.GenerateLuaCache(container);

        // Assert
        luaScript.Should().NotBeNullOrWhiteSpace();
        luaScript.Should().Contain("HeroDefinitionsCache.Heroes");
        luaScript.Should().Contain("Hero_Accursed");

        // Execute in NLua
        using Lua lua = new();
        lua.DoString(luaScript);

        // Test querying HeroDefinitionsCache
        using (LuaTable heroesTable = (LuaTable)lua["HeroDefinitionsCache.Heroes"])
        {
            heroesTable.Should().NotBeNull();
        }

        // Test GetHero function
        using (LuaFunction getHeroFunc = (LuaFunction)lua["HeroDefinitionsCache.GetHero"])
        {
            object[] results = getHeroFunc.Call("Hero_Accursed");
            results.Should().NotBeEmpty();
            results[0].Should().BeOfType<LuaTable>();

            using (LuaTable heroResult = (LuaTable)results[0])
            {
                heroResult["name"].ToString().Should().Be("Hero_Accursed");
                heroResult["displayName"].ToString().Should().Be("Accursed");
            }

            // Test short name lookup via alias
            object[] shortResults = getHeroFunc.Call("accursed");
            shortResults.Should().NotBeEmpty();
            shortResults[0].Should().BeOfType<LuaTable>();
            using LuaTable shortHeroResult = (LuaTable)shortResults[0];
            shortHeroResult["name"].ToString().Should().Be("Hero_Accursed");
        }

        // Test GetHeroIcon function
        using (LuaFunction getHeroIconFunc = (LuaFunction)lua["HeroDefinitionsCache.GetHeroIcon"])
        {
            object[] iconResults = getHeroIconFunc.Call("accursed");
            iconResults[0].ToString().Should().Be("/heroes/accursed/icon.tga");
        }

        // Test GetAvatar function
        using (LuaFunction getAvatarFunc = (LuaFunction)lua["HeroDefinitionsCache.GetAvatar"])
        {
            object[] avatarResults = getAvatarFunc.Call("Hero_Accursed.Alt");
            avatarResults.Should().NotBeEmpty();
            using LuaTable avatarTable = (LuaTable)avatarResults[0];
            avatarTable["displayName"].ToString().Should().Be("Horseman Accursed");
        }

        // Test GetAbility function
        using LuaFunction getAbilityFunc = (LuaFunction)lua["HeroDefinitionsCache.GetAbility"];
        object[] abilityResults = getAbilityFunc.Call("Ability_Accursed1");
        abilityResults.Should().NotBeEmpty();
        using LuaTable abilityTable = (LuaTable)abilityResults[0];
        abilityTable["displayName"].ToString().Should().Be("Cauterize");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LuaCacheGenerator_GenerateLuaCacheAsync_WritesFileToDisk()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        EntityCacheContainer container = CreateSampleContainer();
        string tempFile = Path.Combine(Path.GetTempPath(), $"lua_test_{Guid.NewGuid():N}.lua");

        try
        {
            // Act
            await LuaCacheGenerator.GenerateLuaCacheAsync(container, tempFile);

            // Assert
            File.Exists(tempFile).Should().BeTrue();
            string content = await File.ReadAllTextAsync(tempFile);
            content.Should().Contain("HeroDefinitionsCache");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
