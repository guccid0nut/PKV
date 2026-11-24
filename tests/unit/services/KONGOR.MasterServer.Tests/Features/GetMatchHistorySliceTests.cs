// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Features.Matches.GetMatchHistory;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using Xunit;

namespace KONGOR.MasterServer.Tests.Features;

public class GetMatchHistorySliceTests
{
    [Fact]
    public async Task Handler_WhenAccountNotFound_ReturnsNotFound()
    {
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();

        mockAccountRepo.Setup(a => a.GetByNicknameAsync("MissingUser", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        GetMatchHistoryHandler handler = new(
            mockMatchRepo.Object,
            mockAccountRepo.Object,
            NullLogger<GetMatchHistoryHandler>.Instance);

        GetMatchHistoryQuery query = new(AccountId: null, Nickname: "MissingUser", Limit: 50);
        GetMatchHistoryResponse response = await handler.HandleAsync(query);

        response.Success.Should().BeFalse();
        response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Handler_WhenAccountFound_ReturnsRecentMatchOverview()
    {
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();

        Account account = new()
        {
            ID = 100,
            Name = "ExistingUser",
            User = new User
            {
                ID = 1,
                EmailAddress = "test@example.com",
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash"
            }
        };

        mockAccountRepo.Setup(a => a.GetByNicknameAsync("ExistingUser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        mockMatchRepo.Setup(m => m.GetRecentMatchIdsByAccountAsync(100, "ExistingUser", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<int> { 5001, 5002, 5003 });

        GetMatchHistoryHandler handler = new(
            mockMatchRepo.Object,
            mockAccountRepo.Object,
            NullLogger<GetMatchHistoryHandler>.Instance);

        GetMatchHistoryQuery query = new(AccountId: null, Nickname: "ExistingUser", Limit: 100);
        GetMatchHistoryResponse response = await handler.HandleAsync(query);

        response.Success.Should().BeTrue();
        response.StatusCode.Should().Be(200);
        response.Data.Should().ContainKey("matches");
        response.Data!["matches"].Should().Be("5001,5002,5003");
        response.Data.Should().ContainKey("success");
        response.Data!["success"].Should().Be("True");
    }
}
