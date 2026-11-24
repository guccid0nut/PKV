// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Verifies that UIEventBus Pub/Sub architecture and table pooling are properly integrated across NewUI scripts.
/// </summary>
public sealed class UiEventBusAndLuaTests
{
    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "PROJECT.md")))
                return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveClientUiPath(string repoRoot, string relativePath)
    {
        string fullPath = Path.Combine(repoRoot, relativePath);
        if (File.Exists(fullPath)) return fullPath;

        string fallbackRel = relativePath.Replace("games/client/override/", "games/client/ClientPackageFiles/", StringComparison.OrdinalIgnoreCase);
        string fallbackFull = Path.Combine(repoRoot, fallbackRel);
        return File.Exists(fallbackFull) ? fallbackFull : fullPath;
    }

    [Theory]
    [InlineData("games/client/override/ui/scripts/newui/player_stats_v2.lua")]
    [InlineData("games/client/override/ui/scripts/newui/lobby_v2.lua")]
    [InlineData("games/client/override/ui/scripts/newui/match_stats_v2.lua")]
    public void UiScripts_WhenInvoked_ContainUIEventBusAndTablePooling(string relativePath)
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string fullPath = ResolveClientUiPath(repoRoot, relativePath);

        File.Exists(fullPath).Should().BeTrue($"File {fullPath} must exist.");

        string content = File.ReadAllText(fullPath);

        if (content.StartsWith("\x1bLua"))
        {
            content.Should().Contain("UIEventBus");
            content.Should().Contain("AcquireTable");
            content.Should().Contain("ReleaseTable");
            content.Should().Contain("Subscribe");
            content.Should().Contain("Unsubscribe");
            content.Should().Contain("Publish");
        }
        else
        {
            // Verify UIEventBus singleton & methods
            content.Should().Contain("UIEventBus = _G.UIEventBus or {");
            content.Should().Contain("AcquireTable");
            content.Should().Contain("ReleaseTable");
            content.Should().Contain("Subscribe");
            content.Should().Contain("Unsubscribe");
            content.Should().Contain("Publish");

            // Verify table pooling utilization
            content.Should().Contain("UIEventBus:AcquireTable()");
        }
    }

    [Fact]
    public void HeroFlatBufferLoaderLua_OnQuery_ExistsAndContainsCoreMethods()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string fullPath = ResolveClientUiPath(repoRoot, "games/client/override/ui/scripts/newui/hero_flatbuffer_loader.lua");

        File.Exists(fullPath).Should().BeTrue($"File {fullPath} must exist.");

        string content = File.ReadAllText(fullPath);

        if (content.StartsWith("\x1bLua"))
        {
            content.Should().Contain("HeroFlatBufferLoader");
            content.Should().Contain("Load");
            content.Should().Contain("GetHero");
            content.Should().Contain("GetItem");
            content.Should().Contain("GetAllHeroes");
            content.Should().Contain("KNFB");
        }
        else
        {
            content.Should().Contain("HeroFlatBufferLoader = HeroFlatBufferLoader or {");
            content.Should().Contain("function HeroFlatBufferLoader:Load(");
            content.Should().Contain("function HeroFlatBufferLoader:GetHero(");
            content.Should().Contain("function HeroFlatBufferLoader:GetItem(");
            content.Should().Contain("function HeroFlatBufferLoader:GetAllHeroes()");
            content.Should().Contain("\"KNFB\"");
        }
    }
}

