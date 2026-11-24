// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using ASPIRE.Common.Prebaking.Generators;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace Kongor.Core.Tests.Prebaking;

public sealed class LuaCacheGeneratorTests
{
    [Fact]
    public void LuaCacheGenerator_GenerateLuaCache_ShouldGuardEmptyAndZeroHeroIdentifiers()
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = "2026-09-02 22:45:00",
            BuildTimestamp = "2026-09-02 22:45:00",
            TotalEntityCount = 0
        };

        string lua = LuaCacheGenerator.GenerateLuaCache(container);

        // 1. Initial parameter guard in GetHeroIcon
        lua.Should().Contain(
            "if not heroIdentifier or heroIdentifier == '' or heroIdentifier == '0' or heroIdentifier == 'none' or heroIdentifier == 'null' then",
            "Empty or '0' hero identifiers must not attempt to resolve to nonexistent /heroes/0/icon.tga");

        // 2. Clean fallback guard in GetHeroIcon
        lua.Should().Contain(
            "if clean == '' or clean == '0' or clean == 'none' or clean == 'null' then return '/ui/common/ability_coverup.tga' end",
            "Hero identifiers that clean down to '0' or empty must return coverup texture");
    }

    [Fact]
    public void LuaCacheGenerator_GenerateLuaCache_ShouldStripCompoundDotSyntaxInGetAvatarIcon()
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = "2026-09-02 22:45:00",
            BuildTimestamp = "2026-09-02 22:45:00",
            TotalEntityCount = 0
        };

        string lua = LuaCacheGenerator.GenerateLuaCache(container);

        // Avatar key dot stripping
        lua.Should().Contain(
            "local baseHeroKey = tostring(avatarKey or ''):gsub('%..*$', '')",
            "Compound avatar keys such as Hero_Aluna.Hero must strip dot syntax before fallback");

        lua.Should().Contain(
            "return HeroDefinitionsCache.GetHeroIcon(baseHeroKey)",
            "GetAvatarIcon must route stripped base hero key to GetHeroIcon");
    }

    [Fact]
    public void HeroDefinitionsCacheLua_OnDisk_ShouldMatchGuardsAndDotStripping()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string? dir = AppContext.BaseDirectory;
        string? foundPath = null;
        while (!string.IsNullOrEmpty(dir))
        {
            string[] candidates =
            [
                Path.Combine(dir, "games", "client", "override", "ui", "scripts", "newui", "hero_definitions_cache.lua"),
                Path.Combine(dir, "games", "client", "ClientPackageFiles", "ui", "scripts", "newui", "hero_definitions_cache.lua"),
                Path.Combine(dir, "games", "client", "game", "ui", "scripts", "newui", "hero_definitions_cache.lua")
            ];
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    foundPath = candidate;
                    break;
                }
            }
            if (foundPath != null) break;
            DirectoryInfo? parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        foundPath.Should().NotBeNull("hero_definitions_cache.lua must exist on disk");
        byte[] rawBytes = File.ReadAllBytes(foundPath!);
        if (rawBytes.Length >= 4 && rawBytes[0] == 0x1B && rawBytes[1] == 0x4C && rawBytes[2] == 0x75 && rawBytes[3] == 0x61)
        {
            // Valid compiled 64-bit Lua 5.1 bytecode
            rawBytes.Length.Should().BeGreaterThan(100_000);
            return;
        }

        string content = File.ReadAllText(foundPath!);

        content.Should().Contain("heroIdentifier == '0'");
        content.Should().Contain("clean == '0'");
        (content.Contains("local baseHeroKey = tostring(avatarKey or ''):gsub('%..*$', '')") ||
         content.Contains("local baseHeroKey = cleanKey:gsub('%..*$', '')"))
            .Should().BeTrue("hero_definitions_cache.lua must strip dot suffixes when deriving baseHeroKey");
    }

    [Fact]
    public void LuaCacheGenerator_GenerateLuaCache_ShouldGuardAccountIconAndChatSymbolIndependence()
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = "2026-09-02 22:45:00",
            BuildTimestamp = "2026-09-02 22:45:00",
            TotalEntityCount = 0
        };

        string lua = LuaCacheGenerator.GenerateLuaCache(container);

        // 1. GetAccountIcon guards against custom icon, returning empty to trigger Discord avatar
        lua.Should().Contain("raw:find('^ai%.custom_icon') or raw:find('^custom_icon')");
        // 2. GetAccountIcon guards against chat symbols and colors
        lua.Should().Contain("raw:find('^cs%.') or raw:find('^cc%.')");
        // 3. GetChatSymbol guards against account icons and custom icons
        lua.Should().Contain("raw:find('^ai%.') or raw:find('^cc%.') or raw:find('^t%.') or raw:find('^c%.') or raw:find('^av%.') or raw:find('^w%.') or raw:find('^custom_icon')");
        // 4. Token extraction helper exists
        lua.Should().Contain("extractUpgradeTokens(selected_upgrade)");
    }
}
