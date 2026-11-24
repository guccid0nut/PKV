// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

// ReSharper disable EvaluateParameterInTemplate


using System.Diagnostics.CodeAnalysis;
using System.Net;
using Moq;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

[SuppressMessage("Performance",
    "CA1873:Evaluation of this argument may be expensive and unnecessary if logging is disabled")]
public class EmailServiceTests

{
    private readonly OperationalConfiguration _configuration;

    private readonly EmailService _emailService;

    private readonly Mock<IWebHostEnvironment> _mockEnvironment;

    private readonly Mock<ILogger<EmailService>> _mockLogger;

    private readonly Mock<ISendGridClient> _mockSendGridClient;


    public EmailServiceTests()

    {
        Mock<IOptions<OperationalConfiguration>> mockOptions = new();

        _mockLogger = new Mock<ILogger<EmailService>>();

        _mockEnvironment = new Mock<IWebHostEnvironment>();

        _mockSendGridClient = new Mock<ISendGridClient>();


        _configuration = new OperationalConfiguration

        {
            Email = new OperationalConfigurationEmail

            {
                ApiKey = "SG.TEST_API_KEY",
                FromEmail = "test@test.com",
                FromName = "PK Test"
            },

            JWT = new OperationalConfigurationJWT

            {
                SigningKey = "dummy-key-for-testing",

                Issuer = "test-issuer",

                Audience = "test-audience",

                DurationInHours = 1
            },

            UIBaseUrl = "https://test.localhost"
        };

        mockOptions.Setup(o => o.Value).Returns(_configuration);


        _emailService = new EmailService(
            mockOptions.Object,
            _mockLogger.Object,
            _mockEnvironment.Object,
            _mockSendGridClient.Object
        );
    }


    [Fact]
    public async Task SendEmailAddressRegistrationLink_WithValidConfig_CallsSendGridClient()

    {
        // Arrange

        const string email = "user@example.com";

        const string token = "test-token-123";

        const string expectedSubject = "Verify Email Address";

        const string expectedLink = "https://test.localhost/register/test-token-123";


        SendGridMessage? capturedMessage = null;


        _mockSendGridClient
            .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
            .Callback<SendGridMessage, CancellationToken>((msg, ct) => capturedMessage = msg)
            .ReturnsAsync(new Response(HttpStatusCode.Accepted, null, null));


        // Act

        bool result = await _emailService.SendEmailAddressRegistrationLink(email, token);


        // Assert

        Assert.True(result);


        Assert.NotNull(capturedMessage);

        // MailHelper.CreateSingleEmail might put the subject in Personalizations

        string? actualSubject = string.IsNullOrEmpty(capturedMessage!.Subject)
            ? capturedMessage.Personalizations[0].Subject
            : capturedMessage.Subject;


        Assert.Equal(expectedSubject, actualSubject);

        Assert.Equal(email, capturedMessage.Personalizations[0].Tos[0].Email);

        Assert.Equal(_configuration.Email.FromEmail, capturedMessage.From?.Email);

        // Check PlainTextContent property or Contents list

        string? content = capturedMessage.PlainTextContent;

        if (string.IsNullOrEmpty(content) && capturedMessage.Contents != null)
            content = capturedMessage.Contents.FirstOrDefault(c => c.Type == "text/plain")?.Value;


        Assert.NotNull(content);

        Assert.Contains(expectedLink, content);
    }


    [Fact]
    [SuppressMessage("ReSharper", "LogMessageIsConditional")]
    public async Task SendEmailAddressRegistrationLink_WithMissingApiKey_LogsVariableAndReturnsTrue()

    {
        // Arrange

        OperationalConfiguration invalidConfig = new()

        {
            Email = new OperationalConfigurationEmail

            {
                ApiKey = "", // Missing key

                FromEmail = "test@test.com",

                FromName = "PK Test"
            },

            JWT = new OperationalConfigurationJWT

            {
                SigningKey = "dummy",
                Issuer = "dummy",
                Audience = "dummy",
                DurationInHours = 1
            },

            UIBaseUrl = "https://test.localhost"
        };


        Mock<IOptions<OperationalConfiguration>> mockOptions = new();

        mockOptions.Setup(o => o.Value).Returns(invalidConfig);


        _mockLogger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);


        EmailService emailService = new(
            mockOptions.Object,
            _mockLogger.Object,
            _mockEnvironment.Object,
            _mockSendGridClient.Object
        );


        // Act

        bool result = await emailService.SendEmailAddressRegistrationLink("user@example.com", "token");


        // Assert

        Assert.True(result);


        // Verify SendGrid was NOT called

        _mockSendGridClient.Verify(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);


        // Verify Logger warning

        // ReSharper disable LogMessageIsConditional

        _mockLogger.Verify(logger => logger.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        // ReSharper restore LogMessageIsConditional
    }
}
