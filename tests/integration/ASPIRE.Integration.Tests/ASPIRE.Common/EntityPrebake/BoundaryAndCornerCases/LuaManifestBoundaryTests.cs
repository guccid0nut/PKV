// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class LuaManifestBoundaryTests
{
    private readonly TestLuaManifestGenerator _luaGen = new();

    [Fact]
    public void GenerateLuaManifest_EmptyContainer_ProducesValidEmptyTables()
    {
        // Arrange
        EntityCacheContainer empty = new() { Version = 1, BuildTimestamp = "2026-08-28" };

        // Act
        string luaCode = _luaGen.GenerateLuaManifest(empty);
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);

        // Assert
        error.Should().BeNull();
        valid.Should().BeTrue();
    }

    [Fact]
    public void GenerateLuaManifest_HeroWithQuotesAndNewlines_EscapesSafely()
    {
        // Arrange
        EntityCacheContainer container = new() { Version = 1, BuildTimestamp = "2026-08-28" };
        container.Heroes.Add(new HeroDefinitionDto
        {
            Id = 1,
            Name = "Hero_Quote\nLineBreak",
            DisplayName = "Hero with \"Quotes\" and 'Single' and \t Tabs",
            PrimaryAttribute = "Strength",
            IconPath = "/icon/test.tga",
            ModelPath = "/model/test.mdf"
        });

        // Act
        string luaCode = _luaGen.GenerateLuaManifest(container);
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);

        // Assert
        error.Should().BeNull();
        valid.Should().BeTrue();
    }

    [Fact]
    public void ExecuteAndExtractHero_MissingHeroName_ThrowsKeyNotFoundException()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(2, 1);
        string luaCode = _luaGen.GenerateLuaManifest(container);

        // Act
        Action act = () => _luaGen.ExecuteAndExtractHero(luaCode, "NonExistentHero");

        // Assert
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GenerateLuaManifest_ExtremeUnicodeStrings_ExecutesInNLua()
    {
        // Arrange
        EntityCacheContainer container = new() { Version = 1, BuildTimestamp = "2026-08-28" };
        container.Heroes.Add(new HeroDefinitionDto
        {
            Id = 1,
            Name = "Hero_??????_??_??",
            DisplayName = "Katyusha (??????) ?? ??",
            PrimaryAttribute = "Intelligence",
            IconPath = "/icon/katyusha.tga",
            ModelPath = "/model/katyusha.mdf"
        });

        // Act
        string luaCode = _luaGen.GenerateLuaManifest(container);
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);

        // Assert
        error.Should().BeNull();
        valid.Should().BeTrue();
    }

    [Fact]
    public void GenerateLuaManifest_AltAvatarWithZeroPriority_EmitsZeroPriorityField()
    {
        // Arrange
        EntityCacheContainer container = new() { Version = 1, BuildTimestamp = "2026-08-28" };
        container.AltAvatars.Add(new AltAvatarDto
        {
            Key = "default",
            HeroName = "pyromancer",
            DisplayName = "Default Avatar",
            Priority = 0
        });

        // Act
        string luaCode = _luaGen.GenerateLuaManifest(container);

        // Assert
        luaCode.Should().Contain("priority = 0");
        bool valid = _luaGen.ValidateLuaSyntaxAndExecution(luaCode, out string? error);
        valid.Should().BeTrue();
    }
}
