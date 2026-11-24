// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ZORGATH.WebPortal.API.Features.Email;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Tests For Email Address Registration Functionality
/// </summary>
public sealed class EmailAddressRegistrationTests(ZORGATHTestFixture fixture) : IClassFixture<ZORGATHTestFixture>

{
    [Theory]
    [InlineData("test@kongor.com")]
    [InlineData("user@kongor.net")]
    public async Task RegisterEmailAddress_WithValidEmailAddress_ReturnsOKAndCreatesToken(string emailAddress)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> logger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController controller = new(new LocalEmailMediator(databaseContext, emailService, hostEnvironment), logger);


        IActionResult response =
            await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));


        Assert.IsType<OkObjectResult>(response);


        Token? token = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.EmailAddress.Equals(uniqueEmail) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));


        Assert.NotNull(token);

        Assert.Equal(uniqueEmail, token.EmailAddress);

        Assert.Equal(TokenPurpose.EmailAddressVerification, token.Purpose);

        Assert.Null(token.TimestampConsumed);
    }


    [Theory]
    [InlineData("test@kongor.com", "different@kongor.com")]
    [InlineData("user@kongor.net", "typo@kongor.net")]
    public async Task RegisterEmailAddress_WithMismatchedConfirmation_ReturnsBadRequest(string emailAddress,
        string confirmEmailAddress)

    {
        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> logger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController controller = new(new LocalEmailMediator(databaseContext, emailService, hostEnvironment), logger);


        IActionResult response =
            await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, confirmEmailAddress));


        Assert.IsType<BadRequestObjectResult>(response);
    }


    [Theory]
    [InlineData("duplicate@kongor.com")]
    [InlineData("existing@kongor.net")]
    public async Task RegisterEmailAddress_WhenAlreadyRegistered_ReturnsBadRequest(string emailAddress)

    {
        string uniqueEmail = $"{Guid.NewGuid():N}_{emailAddress}";


        using IServiceScope scope = fixture.CreateScope();


        ILogger<EmailAddressController> logger =
            scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();

        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();


        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        EmailAddressController controller = new(new LocalEmailMediator(databaseContext, emailService, hostEnvironment), logger);


        await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));


        IActionResult response =
            await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(uniqueEmail, uniqueEmail));


        Assert.IsType<BadRequestObjectResult>(response);
    }
}
