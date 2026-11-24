// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using ZORGATH.WebPortal.API.Services;
using ZORGATH.WebPortal.API.Features.Email;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Tests For Identity Policy Configuration (Password/Username Validation)
/// </summary>
public sealed class IdentityPolicyTests(ZORGATHTestFixture fixture) : IClassFixture<ZORGATHTestFixture>

{
    [Fact]
    public async Task RegisterUser_InProduction_WithWeakPassword_ReturnsBadRequest()

    {
        string emailAddress = $"production.weak_{Guid.NewGuid():N}@gmail.com";

        string accountName = $"ProdWeak_{Guid.NewGuid():N}"[..12];

        const string password = "weak"; // Fails length (8), complexity, etc.


        // Force Production Environment

        await using WebApplicationFactory<IZorgathAssemblyMarker> webApplicationFactory =
            fixture.Factory
                .WithWebHostBuilder(builder =>

                {
                    builder.UseEnvironment("Production");

                    builder.UseSetting("InternalServiceKey", "prod-test-valid-internal-key-secure");

                    builder.ConfigureServices(services =>

                    {
                        // Mock Email Service to prevent "Failed To Send Email" error which deletes the token

                        Mock<IEmailService> mockEmailService = new();

                        mockEmailService
                            .Setup(service =>
                                service.SendEmailAddressRegistrationLink(It.IsAny<string>(), It.IsAny<string>()))
                            .ReturnsAsync(true);


                        services.AddSingleton(mockEmailService.Object);
                    });
                });


        // 1. Register Email

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        // Seed Roles (Required for Registration)

        if (!await databaseContext.Roles.AnyAsync(role => role.Name.Equals(UserRoles.User)))

        {
            await databaseContext.Roles.AddAsync(new Role { Name = UserRoles.User });

            await databaseContext.SaveChangesAsync();
        }


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, hostEnvironment), emailLogger);

        IActionResult result =
            await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));


        Assert.IsType<OkObjectResult>(result);


        // 2. Get Token

        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(emailAddress) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));

        Assert.NotNull(registrationToken);


        // 3. Attempt Register User

        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), accountName, password, password));


        // Assert BadRequest (due to validation failure)

        Assert.IsType<BadRequestObjectResult>(response);
    }


    [Fact]
    public async Task RegisterUser_InDevelopment_WithWeakPassword_ReturnsCreated()

    {
        string emailAddress = $"development.weak_{Guid.NewGuid():N}@kongor.com";

        string accountName = $"DevWeak_{Guid.NewGuid():N}"[..12];

        const string password = "weak"; // Should pass in Dev


        // Force Development Environment

        await using WebApplicationFactory<IZorgathAssemblyMarker> webApplicationFactory =
            fixture.Factory
                .WithWebHostBuilder(builder =>

                {
                    builder.UseEnvironment("Development");

                    builder.ConfigureServices(services =>

                    {
                        // Mock Email Service

                        Mock<IEmailService> mockEmailService = new();

                        mockEmailService
                            .Setup(service =>
                                service.SendEmailAddressRegistrationLink(It.IsAny<string>(), It.IsAny<string>()))
                            .ReturnsAsync(true);


                        services.AddSingleton(mockEmailService.Object);
                    });
                });


        // 1. Register Email

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        // Seed Roles (Required for Registration)

        if (!await databaseContext.Roles.AnyAsync(role => role.Name.Equals(UserRoles.User)))

        {
            await databaseContext.Roles.AddAsync(new Role { Name = UserRoles.User });

            await databaseContext.SaveChangesAsync();
        }


        ILogger<EmailAddressController> emailLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        EmailAddressController emailController = new(new LocalEmailMediator(databaseContext, emailService, hostEnvironment), emailLogger);

        await emailController.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));


        // 2. Get Token

        Token? registrationToken = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(emailAddress) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));

        Assert.NotNull(registrationToken);


        // 3. Attempt Register User

        ILogger<UserController> userLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();

        IUserService userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();


        UserController userController =
            new(new AccountRepository(databaseContext), userLogger, userService, authService);


        IActionResult response = await userController.RegisterUserAndMainAccount(
            new RegisterUserAndMainAccountDTO(registrationToken.Value.ToString(), accountName, password, password));


        // Assert Created (validation passed)

        Assert.IsType<CreatedAtActionResult>(response);
    }
}
