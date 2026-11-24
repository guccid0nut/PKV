// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using MediatR;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class AuditStatsMediatRTests
{
    [Fact]
    public async Task AuditStatsHandler_WithMediator_QueriesGetPlayerProfileQuery()
    {
        Mock<IMediator> mockMediator = new();
        Mock<IClientStatsAuditorService> mockAuditor = new();

        PlayerProfileSummaryDto dummyProfile = new(
            new AccountDto { ID = 77, Name = "ValkyrieChampion" },
            new UserDto { ID = 88, EmailAddress = "valk@kongor.net", GoldCoins = 5000, SilverCoins = 25000 },
            new AccountStatisticsDto { SkillRating = 1750.5, MatchesWon = 120, MatchesLost = 30, Disconnects = 1 },
            null,
            new PlayerStatsAggregateDto(150, 450, 150, 300, 5000, 1200, 250, 50000, 60000, 150000, 45000, 0, 77),
            [], [], [], null, null,
            new HostTelemetryResultDto(false, false, [], [], [], 0, 0, 0, 0, null, null, "host"),
            []);

        mockMediator
            .Setup(m => m.Send(It.Is<GetPlayerProfileQuery>(q => q.InGameName == "ValkyrieChampion"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyProfile);

        StatsAuditReport report = new()
        {
            Nickname = "ValkyrieChampion",
            ExecutionMode = "synthetic",
            Discrepancies = [],
            LoggedIssueIds = [],
            ElapsedMs = 15
        };

        mockAuditor
            .Setup(a => a.RunFullAuditAsync(It.Is<StatsAuditRequest>(r =>
                r.Nickname == "ValkyrieChampion" &&
                r.CustomGroundTruth != null &&
                r.CustomGroundTruth.AccountId == 77 &&
                r.CustomGroundTruth.Mmr == 1750 &&
                r.CustomGroundTruth.Wins == 120), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AuditStatsHandler handler = new(
            auditorService: mockAuditor.Object,
            timeProvider: TimeProvider.System,
            mediator: mockMediator.Object);

        using var jsonDoc = JsonDocument.Parse("""{"nickname": "ValkyrieChampion", "tab": "overview", "synthetic": true}""");
        ToolCallResult result = await handler.HandleAsync(jsonDoc.RootElement, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        mockMediator.Verify(m => m.Send(It.Is<GetPlayerProfileQuery>(q => q.InGameName == "ValkyrieChampion"), It.IsAny<CancellationToken>()), Times.Once);
        mockAuditor.Verify(a => a.RunFullAuditAsync(It.IsAny<StatsAuditRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void McpToolRegistry_WithMediator_WiresMediatorToAuditStatsHandler()
    {
        Mock<IMediator> mockMediator = new();
        McpToolRegistry registry = new(registerDefaults: false, mediator: mockMediator.Object);

        registry.AuditStatsHandler.Should().NotBeNull();
    }
}
