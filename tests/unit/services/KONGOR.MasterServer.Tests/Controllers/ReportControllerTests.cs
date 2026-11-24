// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using Xunit;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class ReportControllerTests
{
    [Fact]
    public async Task SubmitReport_WhenRapV2PayloadReceived_ParsesReporteeReasonAndReturnsSeaCompatibilityFields()
    {
        Mock<IAccountRepository> accountRepo = new();
        Mock<IMatchHistoryRepository> matchHistoryRepo = new();
        Mock<ISessionContextResolver> sessionResolver = new();

        Account reporter = CreateAccount(59, "guccidonut");
        Account offender = CreateAccount(61, "Clickslither");

        sessionResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reporter);
        accountRepo
            .Setup(r => r.GetAccountsByNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([offender]);

        List<PlayerReport> saved = [];
        accountRepo
            .Setup(r => r.SavePlayerReportsAsync(It.IsAny<IEnumerable<PlayerReport>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<PlayerReport>, CancellationToken>((items, _) =>
            {
                int id = 101;
                foreach (PlayerReport report in items)
                {
                    report.ID = id++;
                    saved.Add(report);
                }
            })
            .Returns(Task.CompletedTask);

        ReportController controller = CreateController(
            accountRepo.Object,
            matchHistoryRepo.Object,
            sessionResolver.Object,
            new Dictionary<string, StringValues>
            {
                ["reporter_id"] = "59",
                ["cookie"] = "valid-session-cookie",
                ["reportee_name"] = "Clickslither",
                ["reason_type"] = "2",
                ["report_description"] = "Intentional feeding mid"
            });

        IActionResult result = await controller.SubmitReport(CancellationToken.None);

        saved.Should().ContainSingle();
        saved[0].ReporterAccountID.Should().Be(59);
        saved[0].ReportedAccountID.Should().Be(61);
        saved[0].ReportedAccountName.Should().Be("Clickslither");
        saved[0].Reason.Should().Be("Intentional Griefing / Feeding");
        saved[0].Description.Should().Be("Intentional feeding mid");
        saved[0].Status.Should().Be("PENDING");

        string body = await ExecutePhpResultAsync(result);
        body.Should().Contain("success");
        body.Should().Contain("report_success");
        body.Should().Contain("report_enabled");
        body.Should().Contain("101");
    }

    [Fact]
    public async Task SubmitReport_WhenNaeuLegacyPayloadUsesReasonCode6_MapsToAfkLeaving()
    {
        Mock<IAccountRepository> accountRepo = new();
        Mock<IMatchHistoryRepository> matchHistoryRepo = new();
        Mock<ISessionContextResolver> sessionResolver = new();

        Account reporter = CreateAccount(59, "guccidonut");
        Account offender = CreateAccount(61, "Clickslither");

        sessionResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reporter);
        accountRepo
            .Setup(r => r.GetAccountsByNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([offender]);

        List<PlayerReport> saved = [];
        accountRepo
            .Setup(r => r.SavePlayerReportsAsync(It.IsAny<IEnumerable<PlayerReport>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<PlayerReport>, CancellationToken>((items, _) => saved.AddRange(items))
            .Returns(Task.CompletedTask);

        ReportController controller = CreateController(
            accountRepo.Object,
            matchHistoryRepo.Object,
            sessionResolver.Object,
            new Dictionary<string, StringValues>
            {
                ["account_id"] = "59",
                ["cookie"] = "valid-session-cookie",
                ["offenderName[]"] = new StringValues(["Clickslither"]),
                ["match_id[]"] = new StringValues(["1405"]),
                ["type[]"] = new StringValues(["6"]),
                ["body[]"] = new StringValues(["AFK in fountain"]),
                ["time[]"] = new StringValues(["12"])
            });

        await controller.SubmitReport(CancellationToken.None);

        saved.Should().ContainSingle();
        saved[0].MatchID.Should().Be(1405);
        saved[0].Reason.Should().Be("AFK / Leaving / Non-Participation");
        saved[0].Description.Should().Be("AFK in fountain (Match Time: 12m)");
    }

    [Fact]
    public async Task SubmitReport_WhenSessionUnauthenticated_ReturnsUnauthorizedWithoutPersisting()
    {
        Mock<IAccountRepository> accountRepo = new();
        Mock<IMatchHistoryRepository> matchHistoryRepo = new();
        Mock<ISessionContextResolver> sessionResolver = new();

        sessionResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        ReportController controller = CreateController(
            accountRepo.Object,
            matchHistoryRepo.Object,
            sessionResolver.Object,
            new Dictionary<string, StringValues>
            {
                ["offenderName[]"] = new StringValues(["Clickslither"])
            });

        IActionResult result = await controller.SubmitReport(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        accountRepo.Verify(
            r => r.SavePlayerReportsAsync(It.IsAny<IEnumerable<PlayerReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Account CreateAccount(int id, string name) => new()
    {
        ID = id,
        Name = name,
        UserID = id,
        User = new User
        {
            ID = id,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.test",
            SRPPasswordSalt = new string('a', 64),
            SRPPasswordHash = new string('b', 64)
        }
    };

    private static ReportController CreateController(
        IAccountRepository accountRepo,
        IMatchHistoryRepository matchHistoryRepo,
        ISessionContextResolver sessionResolver,
        Dictionary<string, StringValues> formFields)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Form = new FormCollection(formFields);

        ReportController controller = new(
            accountRepo,
            matchHistoryRepo,
            sessionResolver,
            NullLogger<ReportController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return controller;
    }

    private static async Task<string> ExecutePhpResultAsync(IActionResult result)
    {
        DefaultHttpContext httpContext = new();
        MemoryStream bodyStream = new();
        httpContext.Response.Body = bodyStream;
        ActionContext actionContext = new() { HttpContext = httpContext };

        await result.ExecuteResultAsync(actionContext);
        bodyStream.Position = 0;
        using StreamReader reader = new(bodyStream);
        return await reader.ReadToEndAsync();
    }
}
