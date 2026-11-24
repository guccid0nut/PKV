// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Reflection;
using ASPIRE.Tests.MERRICK.DatabaseContext.Tests;
using MERRICK.DatabaseContext.Interceptors;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using ZORGATH.WebPortal.API.Validators;
using ZORGATH.WebPortal.API.Features.Registration;

namespace ASPIRE.Tests;

public sealed class BugFixesRegressionTests
{
    [Fact]
    public void SlowQueryInterceptor_ThresholdCalculation_UsesTotalMilliseconds()
    {
        Mock<ILogger<SlowQueryInterceptor>> mockLogger = new();
        mockLogger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);

        SlowQueryInterceptor interceptor = new(50, mockLogger.Object);

        Mock<DbCommand> mockCommand = new();
        mockCommand.SetupGet(c => c.CommandText).Returns("SELECT * FROM Matches");

        // A query taking 1,005ms has Milliseconds == 5, but TotalMilliseconds == 1005.
        // It must exceed the 50ms threshold and log the slow query warning.
        CommandExecutedEventData eventData =
            SlowQueryInterceptorTests.CreateEventData(mockCommand.Object, TimeSpan.FromMilliseconds(1005));

        interceptor.NonQueryExecuted(mockCommand.Object, eventData, 1);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("[SLOW SQL QUERY]") && v.ToString()!.Contains("1005ms")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void UserEntity_EmailAddressMaxLength_Is254Characters()
    {
        PropertyInfo? emailProperty = typeof(User).GetProperty(nameof(User.EmailAddress));
        Assert.NotNull(emailProperty);

        MaxLengthAttribute? maxLengthAttr = emailProperty.GetCustomAttribute<MaxLengthAttribute>();
        Assert.NotNull(maxLengthAttr);
        Assert.Equal(254, maxLengthAttr.Length);
    }

    [Fact]
    public void MatchmakingGroup_AverageRating_DoesNotThrowOnEmptyMembers()
    {
        MatchmakingGroup group = new()
        {
            Members = [],
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_SOLO,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                MapName = "caldavar",
                GameModes = ["normal"],
                GameRegions = ["USE"],
                Ranked = true,
                MatchFidelity = 0,
                BotDifficulty = 0,
                RandomizeBots = false
            }
        };

        Assert.Equal(0f, group.AverageRating);
        Assert.Equal(0f, group.RatingDisparity);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task RegistrationController_CompleteRegistration_RejectsWeakPassword()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        Guid validToken = Guid.NewGuid();
        context.Tokens.Add(new Token
        {
            Value = validToken,
            Purpose = TokenPurpose.EmailAddressVerification,
            Data = "player@newerth.com",
            EmailAddress = "player@newerth.com"
        });
        await context.SaveChangesAsync();

        Mock<IEmailService> mockEmailService = new();
        IdentityOptions identityOptions = new()
        {
            Password =
            {
                RequiredLength = 8, RequireDigit = true, RequireUppercase = true, RequireNonAlphanumeric = true
            }
        };
        IOptions<IdentityOptions> optionsWrapper =
            Options.Create(identityOptions);
        PasswordValidator validator = new(optionsWrapper);
        RegistrationController controller = new(new LocalRegistrationMediator(context, mockEmailService.Object, validator));

        RegisterUserWithTokenDTO payload = new(
            validToken.ToString(),
            "ValidPlayer",
            "weak" // Below 8 characters, missing uppercase/number/special
        );

        IActionResult result = await controller.CompleteRegistration(payload);
        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }
}
