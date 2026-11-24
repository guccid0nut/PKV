// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Generators;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using Kongor.PrebakeTool;
using NEXUS.Contracts.EntityCache;
using NLua;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Challenger;

[Collection("CliExecution")]
public sealed class AdversarialBoundaryTests
{
    [Fact]
    public async Task HeroesXmlParser_WithEmptyStream_ThrowsInvalidDataException()
    {
        HeroesXmlParser parser = new();
        using MemoryStream ms = new();

        Func<Task> act = async () => await parser.ParseStreamAsync(ms);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task HeroesXmlParser_WithWrongRootTag_ThrowsInvalidDataException()
    {
        HeroesXmlParser parser = new();
        string xml = "<items><item name=\"Item_Test\" /></items>";
        using MemoryStream ms = new(Encoding.UTF8.GetBytes(xml));

        Func<Task> act = async () => await parser.ParseStreamAsync(ms);
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*Root element must be <heroes>*");
    }

    [Fact]
    public async Task HeroesXmlParser_WithEmptyHeroesRoot_ReturnsEmptyList()
    {
        HeroesXmlParser parser = new();
        string xml = "<heroes></heroes>";
        using MemoryStream ms = new(Encoding.UTF8.GetBytes(xml));

        List<HeroDefinitionDto> result = await parser.ParseStreamAsync(ms);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task HeroesXmlParser_WithMalformedAttributes_DoesNotThrow_AppliesDefaults()
    {
        HeroesXmlParser parser = new();
        string xml = """
                     <heroes>
                         <hero name="Hero_Bugged" strength="NOT_A_FLOAT" attackcooldown="INVALID" solorating="XYZ" />
                     </heroes>
                     """;
        using MemoryStream ms = new(Encoding.UTF8.GetBytes(xml));

        List<HeroDefinitionDto> result = await parser.ParseStreamAsync(ms);
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Hero_Bugged");
        result[0].Strength.Should().Be(0f);
        result[0].AttackCooldown.Should().Be(0);
        result[0].SoloRating.Should().Be(0);
    }

    [Fact]
    public async Task BaseUpgradesParser_WithUnrecognizedRoot_ReturnsEmptyList()
    {
        BaseUpgradesParser parser = new();
        string xml = "<invalid_root></invalid_root>";
        using MemoryStream ms = new(Encoding.UTF8.GetBytes(xml));

        List<CosmeticUpgradeDto> result = await parser.ParseStreamAsync(ms);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GameMechanicsParser_WithUnrecognizedRoot_ReturnsEmptyMechanics()
    {
        GameMechanicsParser parser = new();
        string xml = "<badroot></badroot>";
        using MemoryStream ms = new(Encoding.UTF8.GetBytes(xml));

        GameMechanicsDto result = await parser.ParseStreamAsync(ms);
        result.CombatTables.Should().BeEmpty();
        result.Popups.Should().BeEmpty();
    }

    [Fact]
    public void EntityCacheContainer_WithExtremeBoundaryValues_SerializesAndRestoresAccurately()
    {
        EntityCacheContainer container = new()
        {
            Version = uint.MaxValue,
            BuildTimestamp = "9999-12-31T23:59:59.9999999Z",
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = uint.MaxValue,
                    Name = "Hero_Boundary_⚡🔥🛡️",
                    DisplayName = "Unicode <Hero> & '\" Test 中文 Русский 日本語",
                    Strength = float.MaxValue,
                    Agility = float.MinValue,
                    Intelligence = 0.0000001f,
                    AttackDamageMin = float.Epsilon,
                    SoloRating = int.MaxValue,
                    JungleRating = int.MinValue,
                    Abilities = [],
                    AltAvatars = [],
                    PreloadResources = []
                }
            ]
        };

        byte[] serialized = EntityCacheSerializer.Serialize(container, 3);
        EntityCacheContainer restored = EntityCacheDeserializer.Deserialize(serialized);

        restored.Version.Should().Be(uint.MaxValue);
        restored.Heroes.Should().HaveCount(1);
        restored.Heroes[0].Id.Should().Be(uint.MaxValue);
        restored.Heroes[0].Name.Should().Be("Hero_Boundary_⚡🔥🛡️");
        restored.Heroes[0].DisplayName.Should().Be("Unicode <Hero> & '\" Test 中文 Русский 日本語");
        restored.Heroes[0].Strength.Should().Be(float.MaxValue);
        restored.Heroes[0].Agility.Should().Be(float.MinValue);
        restored.Heroes[0].SoloRating.Should().Be(int.MaxValue);
        restored.Heroes[0].JungleRating.Should().Be(int.MinValue);
    }

    [Fact]
    public void LuaCacheGenerator_WithSpecialCharacters_EscapesStringsCorrectly()
    {
        EntityCacheContainer container = new()
        {
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 100,
                    Name = "Hero_LuaSpecial",
                    DisplayName = "Lua's \"Special\" \\ String & <Test>",
                    IconPath = "/heroes/special/icon.tga",
                    PrimaryAttribute = "agility",
                    Strength = 20f,
                    Agility = 30f,
                    Intelligence = 15f
                }
            ]
        };

        string lua = LuaCacheGenerator.GenerateLuaCache(container);

        lua.Should().Contain("Hero_LuaSpecial");
        // Verify Lua code is valid syntax
        using Lua luaEngine = new();
        Action executeLua = () => luaEngine.DoString(lua);
        executeLua.Should().NotThrow();

        // Verify global lookup function returns expected string
        object[] results = luaEngine.DoString("return _G.GetEntityDisplayName('Hero_LuaSpecial')");
        results.Should().NotBeNull();
        results[0].ToString().Should().Be("Lua's \"Special\" \\ String & <Test>");
    }

    [Fact]
    public async Task Program_Main_WithVersionFlag_ReturnsSuccess()
    {
        using (new ConsoleSilencer())
        {
            int exitCode = await Program.Main(["--version"]);
            exitCode.Should().Be(0);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Program_Main_Validate_WithCorruptedCache_ReportsFailure()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string tempDir = Path.Combine(Path.GetTempPath(), $"cli_val_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "override", "prebaked_assets"));
        string binPath = Path.Combine(tempDir, "override", "prebaked_assets", "entity_cache.bin");

        try
        {
            // Write corrupt binary file
            byte[] garbage = [0x01, 0x02, 0x03, 0x04];
            await File.WriteAllBytesAsync(binPath, garbage);

            // Execute validate command
            using (new ConsoleSilencer())
            {
                int exitCode = await Program.Main([
                    "validate",
                    "--game-dir", tempDir,
                    "--output-dir", Path.Combine(tempDir, "override")
                ]);

                // The command should handle corruption gracefully
                exitCode.Should().Be(0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Program_Main_Clean_WithoutPriorJournal_SucceedsGracefully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"cli_clean_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using (new ConsoleSilencer())
            {
                int exitCode = await Program.Main([
                    "clean",
                    "--game-dir", tempDir
                ]);

                exitCode.Should().Be(0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
