// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Services;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public class StoreAudioAndCategoryFilterTests
{
    [Fact]
    public void StaffStoreService_Filters_SingularAndPlural_Taunts_Correctly()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService service = new(config, NullLogger<StaffStoreService>.Instance);

        StaffStoreViewModel singularResult = service.GetCatalog("taunt", null);
        StaffStoreViewModel pluralResult = service.GetCatalog("taunts", null);

        Assert.Equal("taunts", singularResult.MainCategory);
        Assert.Equal("taunts", singularResult.SubCategory);
        Assert.Equal("taunts", pluralResult.MainCategory);
        Assert.Equal("taunts", pluralResult.SubCategory);

        Assert.NotEmpty(singularResult.Items);
        Assert.NotEmpty(pluralResult.Items);
        Assert.All(singularResult.Items,
            item => Assert.True(item.Type == 5 || item.Type == 2 ||
                                item.Name.Contains("taunt", StringComparison.OrdinalIgnoreCase) ||
                                item.Code.EndsWith("_Taunt", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void StaffStoreService_Filters_SingularAndPlural_Announcers_Correctly()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService service = new(config, NullLogger<StaffStoreService>.Instance);

        StaffStoreViewModel singularResult = service.GetCatalog("announcer", null);
        StaffStoreViewModel pluralResult = service.GetCatalog("announcers", null);

        Assert.Equal("announcers", singularResult.MainCategory);
        Assert.Equal("announcers", singularResult.SubCategory);
        Assert.Equal("announcers", pluralResult.MainCategory);
        Assert.Equal("announcers", pluralResult.SubCategory);

        Assert.NotEmpty(singularResult.Items);
        Assert.NotEmpty(pluralResult.Items);
        Assert.All(singularResult.Items,
            item => Assert.True(item.Type == 4 ||
                                item.TypeName.Contains("Announcer", StringComparison.OrdinalIgnoreCase) ||
                                item.Code.StartsWith("av.", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void StaffStoreService_Filters_CouriersAndWards_Correctly()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService service = new(config, NullLogger<StaffStoreService>.Instance);

        StaffStoreViewModel courierResult = service.GetCatalog("couriers", null);
        StaffStoreViewModel wardResult = service.GetCatalog("wards", null);

        Assert.Equal("game_vanity", courierResult.MainCategory);
        Assert.Equal("couriers", courierResult.SubCategory);
        Assert.Equal("game_vanity", wardResult.MainCategory);
        Assert.Equal("wards", wardResult.SubCategory);

        Assert.NotEmpty(courierResult.Items);
        Assert.NotEmpty(wardResult.Items);
    }

    [Fact]
    public void Assets3DController_GetSound_ReturnsAnnouncerAudio_WhenAnnouncerProvided()
    {
        FakeVfsService fakeVfs = new();
        Assets3DController controller = new(fakeVfs, NullLogger<Assets3DController>.Instance);

        IActionResult result = controller.GetSound(announcer: "flamboyant", action: "annihilation");

        FileContentResult fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/ogg", fileResult.ContentType);
        Assert.NotEmpty(fileResult.FileContents);
    }

    [Fact]
    public void Assets3DController_GetSound_ReturnsTauntAudio_WhenTauntProvided()
    {
        FakeVfsService fakeVfs = new();
        Assets3DController controller = new(fakeVfs, NullLogger<Assets3DController>.Instance);

        IActionResult result = controller.GetSound(taunt: "cryingbaby", action: "cast");

        FileContentResult fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/ogg", fileResult.ContentType);
        Assert.NotEmpty(fileResult.FileContents);
    }

    private sealed class FakeVfsService : IGameArchiveVfsService
    {
        public bool FileExists(string vfsPath)
        {
            return true;
        }

        public Stream? OpenFile(string vfsPath)
        {
            return null;
        }

        public byte[]? ReadAllBytes(string vfsPath)
        {
            if (vfsPath.Contains("first_blood.ogg") || vfsPath.Contains("annihilation.ogg") ||
                vfsPath.Contains("deathmetal.ogg") || vfsPath.Contains("cast.ogg"))
                // Fake Ogg header: OggS (0x4F, 0x67, 0x67, 0x53)
                return [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00];

            return null;
        }

        public string? ReadAllText(string vfsPath)
        {
            return null;
        }

        public byte[]? GetModelGlb(string mdfPath)
        {
            return null;
        }

        public byte[]? GetHeroModelGlb(string heroName, string? altAvatar = null, string? resourcePath = null)
        {
            return null;
        }

        public byte[]? GetTextureBytes(string texturePath)
        {
            return null;
        }

        public byte[]? GetTexturePng(string texturePath)
        {
            return null;
        }

        public byte[]? GetHeroSound(string heroName, string? altAvatar = null, string? actionName = null)
        {
            return null;
        }

        public HeroEntityInfoDto? GetHeroEntityInfo(string heroName)
        {
            return null;
        }

        public bool PrebakeHeroModel(string heroName, string? altAvatar = null)
        {
            return true;
        }

        public IEnumerable<string> FindFiles(string prefix)
        {
            return [];
        }
    }
}
