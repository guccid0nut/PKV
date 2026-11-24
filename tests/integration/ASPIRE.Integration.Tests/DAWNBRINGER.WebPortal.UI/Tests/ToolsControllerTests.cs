// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class ToolsControllerTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static IStaffStoreService CreateStoreService()
    {
        return new StaffStoreService(new ConfigurationBuilder().Build(), NullLogger<StaffStoreService>.Instance);
    }

    [Fact]
    public void ToolsController_MessageGenerator_ReturnsViewResult()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.MessageGenerator();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("HoN Color Message Generator", controller.ViewData["Title"]);
    }

    [Fact]
    public void ToolsController_EmpathAR_RedirectsPermanentlyToArMiniGames()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.EmpathAR();

        RedirectResult redirectResult = Assert.IsType<RedirectResult>(result);
        Assert.True(redirectResult.Permanent);
        Assert.Equal("/ar-mini-games/empath", redirectResult.Url);
    }

    [Fact]
    public void ToolsController_DevoWars_RedirectsPermanentlyToArMiniGames()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.DevoWars("HOOK");

        RedirectResult redirectResult = Assert.IsType<RedirectResult>(result);
        Assert.True(redirectResult.Permanent);
        Assert.Equal("/ar-mini-games/devo-wars?room=HOOK", redirectResult.Url);
    }

    [Fact]
    public void ToolsController_GetAvatarsCatalog_ReturnsJsonOfAvatars()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.GetAvatarsCatalog();

        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void ToolsController_Inventory_RedirectsToStaffInventory()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.Inventory("TestAccount");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);
        Assert.Equal("Staff", redirectResult.ControllerName);
        Assert.Equal("TestAccount", redirectResult.RouteValues?["accountName"]);
    }

    [Fact]
    public void ToolsController_AddItem_RedirectsToStaffInventory()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.AddItem("AddItemAccount", "Announcer_Flamboyant");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);
        Assert.Equal("Staff", redirectResult.ControllerName);
    }

    [Fact]
    public void ToolsController_RemoveItem_RedirectsToStaffInventory()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.RemoveItem("RemoveItemAccount", "Taunt_Kongor");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);
        Assert.Equal("Staff", redirectResult.ControllerName);
    }

    [Fact]
    public void ToolsController_ResetInventory_RedirectsToStaffInventory()
    {
        using MerrickContext context = CreateInMemoryContext();
        ToolsController controller = new(CreateStoreService(), NullLogger<ToolsController>.Instance);

        IActionResult result = controller.ResetInventory("ResetItemAccount");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);
        Assert.Equal("Staff", redirectResult.ControllerName);
    }
}
