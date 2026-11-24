// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;
using NLua;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class LuaManifestGenerationTests
{
    private readonly TestLuaManifestGenerator _luaGen = new();

    [Fact]
    public void LuaManifestGeneration_GenerateLuaManifest_ProducesValidLuaSyntax()
    {
        // Arrange
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(5, 2);

        // Act
        string luaCode = _luaGen.GenerateLuaManifest(catalog);

        // Assert
        luaCode.Should().NotBeNullOrWhiteSpace();
        luaCode.Should().Contain("HeroDefinitionsCache = {");
        luaCode.Should().Contain("hero_1");
    }

    [Fact]
    public void ExecuteLuaManifest_NLuaRuntime_LoadsGlobalCacheTable()
    {
        // Arrange
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(3, 2);
        string luaCode = _luaGen.GenerateLuaManifest(catalog);

        // Act
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);

        // Assert
        error.Should().BeNull();
        valid.Should().BeTrue();
    }

    [Fact]
    public void LuaManifestGeneration_LuaHeroLookup_ExtractsCorrectHeroAttributes()
    {
        // Arrange
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(3, 2);
        string luaCode = _luaGen.GenerateLuaManifest(catalog);

        // Act
        Dictionary<string, object> heroData = _luaGen.ExecuteAndExtractHero(luaCode, "Hero_1");

        // Assert
        heroData.Should().NotBeNull();
        heroData.Should().ContainKey("name").WhoseValue.Should().Be("Hero_1");
        heroData.Should().ContainKey("display_name").WhoseValue.Should().Be("Hero Display 1");
        heroData.Should().ContainKey("primary_attribute");
        heroData.Should().ContainKey("icon");
        heroData.Should().ContainKey("model");
    }

    [Fact]
    public void LuaManifestGeneration_LuaAltAvatarLookup_ContainsMappedAvatarProperties()
    {
        // Arrange
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(2, 2);
        string luaCode = _luaGen.GenerateLuaManifest(catalog);

        // Act
        using NLua.Lua lua = new();
        lua.DoString(luaCode);
        LuaTable? avatarTable = lua.DoString("return HeroDefinitionsCache.alt_avatars[\"hero_1_alt1\"]")[0] as LuaTable;

        // Assert
        avatarTable.Should().NotBeNull();
        avatarTable!["key"].Should().Be("alt1");
        avatarTable["hero_name"].Should().Be("hero_1");
        ((long)avatarTable["priority"]).Should().Be(10);
    }

    [Fact]
    public void LuaManifest_WhenExecuted_EscapesSpecialCharactersInNames()
    {
        // Arrange
        EntityCacheContainer catalog = new() { Version = 1, BuildTimestamp = "2026-08-28" };
        catalog.Heroes.Add(new HeroDefinitionDto
        {
            Id = 99,
            Name = "Hero_Quote\"Name\\Slash",
            DisplayName = "Hero with \"Quotes\" & \\Backslash\\",
            PrimaryAttribute = "Agility",
            IconPath = "/icon/quote.tga",
            ModelPath = "/model/quote.mdf"
        });

        // Act
        string luaCode = _luaGen.GenerateLuaManifest(catalog);
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);

        // Assert
        error.Should().BeNull();
        valid.Should().BeTrue();
    }

    [Fact]
    public void LuaManifest_WhenExecuted_ContainsAbilitiesAndAltAvatarLists()
    {
        // Arrange
        EntityCacheContainer catalog = TestPrebakeHarness.CreateSampleCatalog(1);
        string luaCode = _luaGen.GenerateLuaManifest(catalog);

        // Act
        using NLua.Lua lua = new();
        lua.DoString(luaCode);
        LuaTable? abilities = lua.DoString("return HeroDefinitionsCache.heroes[\"hero_1\"].abilities")[0] as LuaTable;
        LuaTable? avatars = lua.DoString("return HeroDefinitionsCache.heroes[\"hero_1\"].alt_avatars")[0] as LuaTable;

        // Assert
        abilities.Should().NotBeNull();
        abilities!.Values.Count.Should().Be(3);
        avatars.Should().NotBeNull();
        avatars!.Values.Count.Should().Be(3);
    }
}
