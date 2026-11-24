// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class StoreARFeatureTests
{
    private static string FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ProjectKongor.sln")) || Directory.Exists(Path.Combine(dir, "src", "services", "web-ui")))
            {
                return dir;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void StoreAR_ModalPartialView_ExistsAndContainsEssentialElements()
    {
        string repoRoot = FindRepoRoot();
        string modalPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Shared", "_StoreARModal.cshtml");

        Assert.True(File.Exists(modalPath), $"_StoreARModal.cshtml must exist at {modalPath}");

        string content = File.ReadAllText(modalPath);
        Assert.Contains("id=\"storeArModal\"", content);
        Assert.Contains("id=\"storeArVideo\"", content);
        Assert.Contains("id=\"storeArCanvasContainer\"", content);
        Assert.Contains("id=\"storeArCanvas\"", content);
        Assert.Contains("id=\"btnStoreArToggleCamera\"", content);
        Assert.Contains("id=\"storeArScaleSelect\"", content);
        Assert.Contains("id=\"btnStoreArToggleAudio\"", content);
        Assert.Contains("id=\"btnStoreArResetPlacement\"", content);
        Assert.Contains("id=\"storeArLoading\"", content);
    }

    [Fact]
    public void StoreAR_ClientScript_ExistsAndExposesPublicApi()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "store_ar.js");

        Assert.True(File.Exists(scriptPath), $"store_ar.js must exist at {scriptPath}");

        string content = File.ReadAllText(scriptPath);
        Assert.Contains("window.KongorStoreAR", content);
        Assert.Contains("open:", content);
        Assert.Contains("close:", content);
        Assert.Contains("toggleCamera:", content);
        Assert.Contains("toggleAudio:", content);
        Assert.Contains("applyScale:", content);
        Assert.Contains("repositionHeroTo:", content);
        Assert.Contains("isRotatingHero", content);
        Assert.Contains("isDraggingHeroPosition", content);
        Assert.Contains("e.button === 1", content);
        Assert.Contains("walkSpeed = 1.15", content);
        Assert.Contains("oncontextmenu", content);
        Assert.Contains("THREE.ShadowMaterial", content);
        Assert.Contains("getUserMedia", content);
    }

    [Fact]
    public void StoreAR_Layout_RegistersModalAndScript()
    {
        string repoRoot = FindRepoRoot();
        string layoutPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Shared", "_Layout.cshtml");

        Assert.True(File.Exists(layoutPath), $"_Layout.cshtml must exist at {layoutPath}");

        string content = File.ReadAllText(layoutPath);
        Assert.Contains("_StoreARModal", content);
        Assert.Contains("store_ar.js", content);
    }

    [Fact]
    public void StoreAR_ItemDetailView_ContainsARButtonAndTrigger()
    {
        string repoRoot = FindRepoRoot();
        string itemPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Store", "Item.cshtml");

        Assert.True(File.Exists(itemPath), $"Item.cshtml must exist at {itemPath}");

        string content = File.ReadAllText(itemPath);
        Assert.Contains("id=\"btnOpenStoreAR\"", content);
        Assert.Contains("KongorStoreAR.open", content);
    }

    [Fact]
    public void StoreAR_CatalogIndexView_ContainsARButtonAndTrigger()
    {
        string repoRoot = FindRepoRoot();
        string indexPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Store", "Index.cshtml");

        Assert.True(File.Exists(indexPath), $"Index.cshtml must exist at {indexPath}");

        string content = File.ReadAllText(indexPath);
        Assert.Contains("id=\"stageArBtn\"", content);
        Assert.Contains("openCurrentItemInAR", content);
        Assert.Contains("KongorStoreAR.open", content);
    }

    [Fact]
    public async Task StoreController_Item_ReturnsValid3DItemModelForAR()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        IStoreApiClient storeApi = TestApiClientFactory.CreateStoreApiClient(context);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        StoreController controller = new(storeApi, userApi, storeService, NullLogger<StoreController>.Instance);

        // Act - Fetch any 3D-previewable hero item from catalog (or Female Pyromancer #15)
        StaffStoreViewModel catalog = storeService.GetCatalog(null, null);
        var targetItem = catalog.Items.FirstOrDefault(i => i.CanPreview3D) ?? catalog.Items.First();
        IActionResult result = await controller.Item(targetItem.Id);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        StoreItemDetailViewModel model = Assert.IsType<StoreItemDetailViewModel>(viewResult.Model);
        Assert.NotNull(model.Item);
        Assert.Equal(targetItem.Id, model.Item.Id);
        Assert.False(string.IsNullOrWhiteSpace(model.Item.Name));
    }
}
