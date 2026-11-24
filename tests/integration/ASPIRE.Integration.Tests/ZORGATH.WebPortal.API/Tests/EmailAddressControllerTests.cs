// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using ZORGATH.WebPortal.API.Features.Email;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public class EmailAddressControllerTests

{
    private readonly Mock<IEmailService> _mockEmailService = new();

    private readonly Mock<IWebHostEnvironment> _mockEnvironment = new();

    private readonly Mock<ILogger<EmailAddressController>> _mockLogger = new();


    private static MerrickContext GetInMemoryContext()

    {
        return TestDbContextFactory.CreateInMemoryContext();
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task RegisterEmailAddress_WithConsumedToken_ReturnsBadRequest()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        MerrickContext context = GetInMemoryContext();

        Token token = new()

        {
            Purpose = TokenPurpose.EmailAddressVerification,

            EmailAddress = "consumed@test.com",

            Value = Guid.NewGuid(),

            Data = "consumed@test.com",

            TimestampConsumed = TimeProvider.System.GetUtcNow()
        };

        context.Tokens.Add(token);

        await context.SaveChangesAsync();


        EmailAddressController controller =
            new(new LocalEmailMediator(context, _mockEmailService.Object, _mockEnvironment.Object), _mockLogger.Object);


        IActionResult result =
            await controller.RegisterEmailAddress(new RegisterEmailAddressDTO("consumed@test.com",
                "consumed@test.com"));


        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);

        Assert.Equal("Email Address \"consumed@test.com\" Is Already Registered", badRequest.Value);
    }


    [Fact]
    public async Task RegisterEmailAddress_WithInvalidEmail_ReturnsBadRequest()

    {
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Production");

        MerrickContext context = GetInMemoryContext();

        EmailAddressController controller =
            new(new LocalEmailMediator(context, _mockEmailService.Object, _mockEnvironment.Object), _mockLogger.Object);


        IActionResult result =
            await controller.RegisterEmailAddress(new RegisterEmailAddressDTO("invalidemail", "invalidemail"));


        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);

        Assert.Equal("Email Address \"invalidemail\" Is Not Valid", badRequest.Value);
    }


    [Fact]
    public async Task RegisterEmailAddress_WhenEmailFailsToSend_ReturnsServiceUnavailable()

    {
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Development");
        _mockEmailService.Setup(e => e.SendEmailAddressRegistrationLink(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);


        MerrickContext context = GetInMemoryContext();

        EmailAddressController controller =
            new(new LocalEmailMediator(context, _mockEmailService.Object, _mockEnvironment.Object), _mockLogger.Object);


        IActionResult result =
            await controller.RegisterEmailAddress(new RegisterEmailAddressDTO("test@example.com", "test@example.com"));


        ObjectResult statusCodeResult = Assert.IsType<ObjectResult>(result);

        Assert.Equal(503, statusCodeResult.StatusCode);

        Assert.Equal("Failed To Send Email Address Verification Email", statusCodeResult.Value);


        // Assert token is removed

        Assert.Empty(context.Tokens);
    }
}
