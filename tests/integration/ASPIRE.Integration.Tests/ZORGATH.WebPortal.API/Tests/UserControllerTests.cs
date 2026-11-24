// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Moq;
using ZORGATH.WebPortal.API.Services;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public class UserControllerTests

{
    private readonly Mock<IAuthenticationService> _mockAuthService = new();

    private readonly Mock<ILogger<UserController>> _mockLogger = new();

    private readonly Mock<IUserService> _mockUserService = new();


    private static MerrickContext GetInMemoryContext()

    {
        return TestDbContextFactory.CreateInMemoryContext();
    }


    [Fact]
    public async Task GetUser_ReturnsNotFound_WhenUserDoesNotExist()

    {
        MerrickContext context = GetInMemoryContext();

        UserController controller = new(new AccountRepository(context), _mockLogger.Object, _mockUserService.Object,
            _mockAuthService.Object);


        IActionResult result = await controller.GetUser(999);


        NotFoundObjectResult notFoundResult = Assert.IsType<NotFoundObjectResult>(result);

        Assert.Equal("User With ID \"999\" Was Not Found", notFoundResult.Value);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetUser_ReturnsBadRequest_WhenRoleIsUnknown()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        MerrickContext context = GetInMemoryContext();

        context.Users.Add(new User

        {
            ID = 1,

            EmailAddress = "test@test.com",

            Role = new Role { Name = "USER" },

            SRPPasswordSalt = "",

            SRPPasswordHash = "",

            PBKDF2PasswordHash = ""
        });

        await context.SaveChangesAsync();


        UserController controller = new(new AccountRepository(context), _mockLogger.Object, _mockUserService.Object,
            _mockAuthService.Object);


        List<Claim> claims = [new(Claims.UserRole, "UNKNOWN")];

        ClaimsIdentity identity = new(claims, "TestAuthType");

        controller.ControllerContext = new ControllerContext

        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };


        IActionResult result = await controller.GetUser(1);


        BadRequestObjectResult badRequestResult = Assert.IsType<BadRequestObjectResult>(result);

        Assert.Equal("Unknown User Role \"UNKNOWN\"", badRequestResult.Value);
    }


    [Fact]
    public async Task GetUser_ReturnsUserDTO_WhenRoleIsAdministrator()

    {
        MerrickContext context = GetInMemoryContext();

        User user = new()

        {
            ID = 2,

            EmailAddress = "admin@test.com",

            Role = new Role { Name = "ADMIN" },

            SRPPasswordSalt = "",

            SRPPasswordHash = "",

            PBKDF2PasswordHash = ""
        };

        Account account = new()

        {
            ID = 10,

            Name = "AdminAcc",

            IsMain = true,

            Clan = new Clan { Tag = "TAG", Name = "ClanName" },

            User = user
        };

        user.Accounts = [account];

        context.Users.Add(user);

        await context.SaveChangesAsync();


        UserController controller = new(new AccountRepository(context), _mockLogger.Object, _mockUserService.Object,
            _mockAuthService.Object);


        List<Claim> claims = [new(Claims.UserRole, UserRoles.Administrator)];

        controller.ControllerContext = new ControllerContext

        {
            HttpContext =
                new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
        };


        IActionResult result = await controller.GetUser(2);


        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);

        GetBasicUserDTO dto = Assert.IsType<GetBasicUserDTO>(okResult.Value);

        Assert.Equal("admin@test.com", dto.EmailAddress);

        Assert.Single(dto.Accounts);

        Assert.Equal("[TAG]AdminAcc", dto.Accounts[0].Name);
    }


    [Fact]
    public async Task GetUser_ReturnsObfuscatedUserDTO_WhenRoleIsUser()

    {
        MerrickContext context = GetInMemoryContext();

        User user = new()

        {
            ID = 3,

            EmailAddress = "user@test.com",

            Role = new Role { Name = "USER" },

            SRPPasswordSalt = "",

            SRPPasswordHash = "",

            PBKDF2PasswordHash = ""
        };

        Account account = new() { ID = 11, Name = "UserAcc", IsMain = true, User = user };

        user.Accounts = [account];

        context.Users.Add(user);

        await context.SaveChangesAsync();


        UserController controller = new(new AccountRepository(context), _mockLogger.Object, _mockUserService.Object,
            _mockAuthService.Object);


        List<Claim> claims = [new(Claims.UserRole, UserRoles.User)];

        controller.ControllerContext = new ControllerContext

        {
            HttpContext =
                new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
        };


        IActionResult result = await controller.GetUser(3);


        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);

        GetBasicUserDTO dto = Assert.IsType<GetBasicUserDTO>(okResult.Value);

        Assert.Equal("****@****.***", dto.EmailAddress);

        Assert.Single(dto.Accounts);

        Assert.Equal("UserAcc", dto.Accounts[0].Name);
    }
}
