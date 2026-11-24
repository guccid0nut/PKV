// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Xunit;

namespace Kongor.Core.Tests.Vfs;

public sealed class LayeredVirtualFileSystemTests
{
    [Fact]
    public async Task LayeredVfs_PriorityResolution_HighestPriorityOverridesLower()
    {
        using InMemoryVirtualFileSystem baseLayer = new();
        using InMemoryVirtualFileSystem overrideLayer = new();
        using LayeredVirtualFileSystem layered = new();

        baseLayer.WriteAllText("ui/game.interface", "<interface base='true' />");
        baseLayer.WriteAllText("shared/default.lua", "print('base')");

        overrideLayer.WriteAllText("ui/game.interface", "<interface override='true' />");

        layered.AddLayer(baseLayer, 10);
        layered.AddLayer(overrideLayer, 50);

        // ui/game.interface should resolve to override layer (priority 50 > 10)
        string gameUi = await layered.ReadAllTextAsync("ui/game.interface");
        gameUi.Should().Be("<interface override='true' />");

        // shared/default.lua should fall through to base layer (priority 10)
        string defaultLua = await layered.ReadAllTextAsync("shared/default.lua");
        defaultLua.Should().Be("print('base')");
    }

    [Fact]
    public void LayeredVfs_EnumerateFiles_MergesAcrossLayers()
    {
        using InMemoryVirtualFileSystem baseLayer = new();
        using InMemoryVirtualFileSystem modLayer = new();
        using LayeredVirtualFileSystem layered = new();

        baseLayer.WriteAllText("heroes/astryon/hero.entity", "base hero");
        baseLayer.WriteAllText("heroes/astryon/model.mdf", "base mdf");

        modLayer.WriteAllText("heroes/astryon/hero.entity", "modded hero");
        modLayer.WriteAllText("heroes/astryon/ability_1.entity", "ability 1");

        layered.AddLayer(baseLayer, 10);
        layered.AddLayer(modLayer, 100);

        List<string> files = [.. layered.EnumerateFiles("heroes/astryon", "*", SearchOption.AllDirectories)];
        files.Should().HaveCount(3);
        files.Should().Contain("heroes/astryon/hero.entity");
        files.Should().Contain("heroes/astryon/model.mdf");
        files.Should().Contain("heroes/astryon/ability_1.entity");

        // Content of hero.entity must come from modLayer
        layered.ReadAllText("heroes/astryon/hero.entity").Should().Be("modded hero");
    }

    [Fact]
    public void LayeredVfs_RemoveLayer_RemovesOverlayBehavior()
    {
        using InMemoryVirtualFileSystem baseLayer = new();
        using InMemoryVirtualFileSystem modLayer = new();
        using LayeredVirtualFileSystem layered = new();

        baseLayer.WriteAllText("config.xml", "<base />");
        modLayer.WriteAllText("config.xml", "<mod />");

        layered.AddLayer(baseLayer, 10);
        layered.AddLayer(modLayer, 100);

        layered.ReadAllText("config.xml").Should().Be("<mod />");

        layered.RemoveLayer(modLayer).Should().BeTrue();
        layered.ReadAllText("config.xml").Should().Be("<base />");
    }

    [Fact]
    public void LayeredVirtualFileSystem_LayeredVfs_WritesDefaultToPrimaryLayer()
    {
        using InMemoryVirtualFileSystem baseLayer = new();
        using InMemoryVirtualFileSystem topLayer = new();
        using LayeredVirtualFileSystem layered = new();

        layered.AddLayer(baseLayer, 10);
        layered.AddLayer(topLayer, 100);

        layered.WriteAllText("new_file.txt", "Top layer content");

        topLayer.FileExists("new_file.txt").Should().BeTrue();
        baseLayer.FileExists("new_file.txt").Should().BeFalse();
    }
}
