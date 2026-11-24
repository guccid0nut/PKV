// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Domain;
using Xunit;

namespace Kongor.Core.Tests.Vfs;

public sealed class GameArchiveVfsServiceTests
{
    [Fact]
    public void GameArchiveVfsService_WithVirtualFileSystem_ReadsFilesProperly()
    {
        using InMemoryVirtualFileSystem inMemVfs = new();
        inMemVfs.WriteAllText("heroes/astryon/hero.entity", "<entity name='Hero_Astryon' />");
        inMemVfs.WriteAllBytes("heroes/astryon/textures/icon.png", [1, 2, 3, 4]);

        IConfiguration configuration = new ConfigurationBuilder().Build();
        using GameArchiveVfsService service = new(
            configuration,
            NullLogger<GameArchiveVfsService>.Instance,
            virtualFileSystem: inMemVfs);

        service.FileExists("heroes/astryon/hero.entity").Should().BeTrue();
        service.FileExists("heroes/astryon/textures/icon.png").Should().BeTrue();
        service.FileExists("heroes/astryon/missing.txt").Should().BeFalse();

        string? text = service.ReadAllText("heroes/astryon/hero.entity");
        text.Should().Be("<entity name='Hero_Astryon' />");

        byte[]? bytes = service.ReadAllBytes("heroes/astryon/textures/icon.png");
        bytes.Should().Equal(1, 2, 3, 4);

        List<string> found = [.. service.FindFiles("heroes/astryon")];
        found.Should().Contain("heroes/astryon/hero.entity");
        found.Should().Contain("heroes/astryon/textures/icon.png");
    }

    [Theory]
    [InlineData("heroes/astryon/model.mdf", "heroes/astryon/model.mdf")]
    [InlineData("heroes\\astryon\\model.mdf", "heroes/astryon/model.mdf")]
    [InlineData("/heroes/astryon/model.mdf/", "heroes/astryon/model.mdf")]
    [InlineData("\\heroes\\astryon\\model.mdf\\", "heroes/astryon/model.mdf")]
    [InlineData("heroes/./astryon/../astryon/model.mdf", "heroes/astryon/model.mdf")]
    [InlineData("HEROES/ASTRYON/MODEL.MDF", "heroes/astryon/model.mdf")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void GameArchiveVfsService_NormalizePath_ReturnsExpectedNormalizedPath(string input, string expected)
    {
        string actual = GameArchiveVfsService.NormalizePath(input);
        actual.Should().Be(expected);
    }

    [Fact]
    public void GameArchiveVfsService_HeroFolderAliases_ResolvesExpectedAliases()
    {
        using InMemoryVirtualFileSystem inMemVfs = new();
        inMemVfs.WriteAllText("heroes/scar/hero.entity",
            "<hero displayname='Madman' primaryattribute='Agility' difficulty='3' />");
        inMemVfs.WriteAllText("heroes/scar/icons/hero.dds", "DDS");

        IConfiguration configuration = new ConfigurationBuilder().Build();
        using GameArchiveVfsService service = new(
            configuration,
            NullLogger<GameArchiveVfsService>.Instance,
            virtualFileSystem: inMemVfs);

        HeroEntityInfoDto? info = service.GetHeroEntityInfo("madman");
        info.Should().NotBeNull();
        info!.HeroName.Should().Be("scar");
        info.DisplayName.Should().Be("Madman");
    }
}
