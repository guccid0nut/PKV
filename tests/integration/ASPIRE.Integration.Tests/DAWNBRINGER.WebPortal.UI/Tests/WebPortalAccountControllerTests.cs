// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using global::DAWNBRINGER.WebPortal.UI.Utilities;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Moq;
using NEXUS.Contracts.Clients;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class WebPortalAccountControllerTests
{
    private (AccountController Controller, MerrickContext DbContext) CreateController()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(dbContext);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(dbContext);

        Mock<IHttpClientFactory> httpFactoryMock = new();
        Mock<ILogger<AccountController>> loggerMock = new();
        Mock<IOutputCacheStore> cacheStoreMock = new();
        Mock<IConfiguration> configMock = new();

        AccountController controller = new(userApi, adminApi, httpFactoryMock.Object, loggerMock.Object,
            cacheStoreMock.Object,
            configMock.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return (controller, dbContext);
    }

    [Fact]
    public async Task Login_UnauthenticatedUser_ReturnsView()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();

        IActionResult result = await controller.Login();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Null(viewResult.ViewName);
    }

    [Fact]
    public async Task Login_AuthenticatedUser_RedirectsToHomeIndex()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();
        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "TestUser")], "TestAuth");
        ClaimsPrincipal principal = new(identity);
        controller.ControllerContext.HttpContext.User = principal;

        IActionResult result = await controller.Login();

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirectResult.ActionName);
        Assert.Equal("Home", redirectResult.ControllerName);
    }

    [Fact]
    public void WebPortalAccountController_Signup_RedirectsToLogin()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();

        IActionResult result = controller.Signup();

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Login", redirectResult.ActionName);
    }

    [Fact]
    public async Task CompleteRegistration_InvalidModelState_ReturnsViewWithModel()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();
        controller.ModelState.AddModelError("AccountName", "Account name is required");

        string candidateSecret = Guid.NewGuid().ToString("N");
        RegisterViewModel model = new() { IGN = "a", Password = candidateSecret, ConfirmPassword = candidateSecret };

        IActionResult result = await controller.CompleteRegistration(model);

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal(model, viewResult.Model);
    }

    [Fact]
    public async Task DiscordHandshake_UserExistsWithMainAccount_RedirectsToHomeIndex()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();

        // Setup mock authentication service
        ServiceCollection services = [];
        Mock<IAuthenticationService> authServiceMock = new();
        Mock<IUrlHelperFactory> urlHelperFactoryMock = new();
        services.AddSingleton(authServiceMock.Object);
        services.AddSingleton(urlHelperFactoryMock.Object);
        controller.ControllerContext.HttpContext.RequestServices = services.BuildServiceProvider();

        const string discordId = "123456789";
        const string syntheticEmail = $"d_{discordId}";
        const string ign = "TestUser";

        Role role = new() { Name = UserRoles.User };
        dbContext.Roles.Add(role);

        User user = new()
        {
            EmailAddress = syntheticEmail,
            Role = role,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = ign, User = user, IsMain = true };
        dbContext.Accounts.Add(account);
        user.Accounts.Add(account);

        await dbContext.SaveChangesAsync();

        ClaimsIdentity identity = new([
            new Claim(ClaimTypes.NameIdentifier, discordId),
            new Claim(ClaimTypes.Email, "real@email.com")
        ], "DiscordTest");
        ClaimsPrincipal principal = new(identity);
        controller.ControllerContext.HttpContext.User = principal;

        IActionResult result = await controller.DiscordHandshake();

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirectResult.ActionName);
        Assert.Equal("Home", redirectResult.ControllerName);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task DiscordHandshake_UserExistsWithNoAccount_RedirectsToRegister()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (AccountController controller, MerrickContext dbContext) = CreateController();

        const string discordId = "123456789";
        const string syntheticEmail = $"d_{discordId}";

        Role role = new() { Name = UserRoles.User };
        dbContext.Roles.Add(role);

        User user = new()
        {
            EmailAddress = syntheticEmail,
            Role = role,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "hash"
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        ClaimsIdentity identity = new([
            new Claim(ClaimTypes.NameIdentifier, discordId),
            new Claim(ClaimTypes.Email, "real@email.com")
        ], "DiscordTest");
        ClaimsPrincipal principal = new(identity);
        controller.ControllerContext.HttpContext.User = principal;

        IActionResult result = await controller.DiscordHandshake();

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirectResult.ActionName);
    }

    [Fact]
    public async Task Settings_Unauthenticated_RedirectsToLogin()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();

        IActionResult result = await controller.Settings();

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Login", redirectResult.ActionName);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Settings_Authenticated_ReturnsViewWithSettingsModel()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (AccountController controller, MerrickContext dbContext) = CreateController();

        Role role = new() { Name = UserRoles.User };
        dbContext.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "testuser@nexus.local",
            Role = role,
            DiscordUsername = "TestDiscordUser",
            SRPPasswordSalt = "oldsalt",
            SRPPasswordHash = "oldhash",
            PBKDF2PasswordHash = "oldpbkdf2"
        };
        Account account = new() { Name = "TestIGN", User = user, IsMain = true, Type = AccountType.Normal };
        user.Accounts.Add(account);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "TestIGN")], "TestAuth");
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);

        IActionResult result = await controller.Settings();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        AccountSettingsViewModel model = Assert.IsType<AccountSettingsViewModel>(viewResult.Model);
        Assert.Equal("TestIGN", model.MainAccountName);
        Assert.Equal("testuser@nexus.local", model.EmailAddress);
        Assert.Equal("TestDiscordUser", model.DiscordUsername);
        Assert.Single(model.LinkedAccounts);
    }

    [Fact]
    public async Task ChangePassword_MismatchedPasswords_SetsErrorAndRedirects()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();
        controller.TempData = new TempDataDictionary(
            controller.ControllerContext.HttpContext,
            Mock.Of<ITempDataProvider>());

        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "TestIGN")], "TestAuth");
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);

        string candidateA = Guid.NewGuid().ToString("N");
        string candidateB = Guid.NewGuid().ToString("N");
        ChangePasswordViewModel form = new()
        {
            NewPassword = candidateA,
            ConfirmPassword = candidateB
        };

        IActionResult result = await controller.ChangePassword(form);

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Settings", redirectResult.ActionName);
        Assert.Equal("New password and confirmation password do not match.", controller.TempData["ErrorMessage"]);
    }

    [Fact]
    public async Task ChangePassword_ShortPassword_SetsErrorAndRedirects()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();
        controller.TempData = new TempDataDictionary(
            controller.ControllerContext.HttpContext,
            Mock.Of<ITempDataProvider>());

        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "TestIGN")], "TestAuth");
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);

        string shortCandidate = Guid.NewGuid().ToString("N")[..2];
        ChangePasswordViewModel form = new() { NewPassword = shortCandidate, ConfirmPassword = shortCandidate };

        IActionResult result = await controller.ChangePassword(form);

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Settings", redirectResult.ActionName);
        Assert.Equal("New password must be at least 4 characters long.", controller.TempData["ErrorMessage"]);
    }

    [Fact]
    public async Task ChangePassword_ValidPassword_UpdatesSrpAndPbkdf2Hash()
    {
        (AccountController controller, MerrickContext dbContext) = CreateController();
        controller.TempData = new TempDataDictionary(
            controller.ControllerContext.HttpContext,
            Mock.Of<ITempDataProvider>());

        Role role = new() { Name = UserRoles.User };
        dbContext.Roles.Add(role);

        string initialSecret = Guid.NewGuid().ToString("N");
        string nextSecret = Guid.NewGuid().ToString("N");
        string initialSalt = SRP.GenerateSRPPasswordSalt();
        string initialHash = SRP.ComputeSRPPasswordHash(initialSecret, initialSalt, passwordIsHashed: false);

        User user = new()
        {
            EmailAddress = "testuser@nexus.local",
            Role = role,
            SRPPasswordSalt = initialSalt,
            SRPPasswordHash = initialHash,
            PBKDF2PasswordHash = "initial_pbkdf2"
        };
        Account account = new() { Name = "TestIGN", User = user, IsMain = true, Type = AccountType.Normal };
        user.Accounts.Add(account);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "TestIGN")], "TestAuth");
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);

        ChangePasswordViewModel form = new()
        {
            CurrentPassword = initialSecret,
            NewPassword = nextSecret,
            ConfirmPassword = nextSecret
        };

        IActionResult result = await controller.ChangePassword(form);

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Settings", redirectResult.ActionName);
        Assert.NotNull(controller.TempData["SuccessMessage"]);

        User updatedUser = await dbContext.Users.FirstAsync(u => u.ID == user.ID);
        Assert.NotEqual(initialHash, updatedUser.SRPPasswordHash);
        Assert.NotEqual(initialSalt, updatedUser.SRPPasswordSalt);
        Assert.NotEqual("initial_pbkdf2", updatedUser.PBKDF2PasswordHash);
        Assert.Equal(64, updatedUser.SRPPasswordHash.Length);
    }
}
