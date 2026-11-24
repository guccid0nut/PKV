// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Matchmaking.V1;
using TRANSMUTANSTEIN.ChatServer.Services.Grpc;
using Xunit;

namespace Grpc.Challenger.Tests;

public sealed class MatchmakingServiceChallengerTests
{
    [Fact]
    public async Task SubmitTicket_WithValidAndBoundaryValues_Succeeds()
    {
        GrpcMatchmakingService service = new(NullLogger<GrpcMatchmakingService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        SubmitTicketRequest req = new()
        {
            Player = new PlayerMatchCandidate
            {
                AccountId = uint.MaxValue,
                Nickname = "LegendaryMid",
                Mmr = 3000,
                RegionPreference = "EU"
            },
            GameMode = GameMode.MidWars
        };
        req.Player.PreferredHeroes.AddRange([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        req.AllowedRegions.AddRange(["USE", "USW", "EU", "SEA"]);

        SubmitTicketResponse res = await service.SubmitTicket(req, ctx);

        res.Should().NotBeNull();
        res.TicketId.Should().NotBeNullOrWhiteSpace();
        res.EstimatedWaitSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SubmitTicket_WithMissingPlayerOrUnspecifiedMode_HandlesSafely()
    {
        GrpcMatchmakingService service = new(NullLogger<GrpcMatchmakingService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        // Null player candidate (e.g. malformed protobuf payload)
        SubmitTicketRequest req = new()
        {
            Player = null,
            GameMode = GameMode.Unspecified
        };

        SubmitTicketResponse res = await service.SubmitTicket(req, ctx);

        res.Should().NotBeNull();
        res.TicketId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CancelTicket_WithNonExistentOrEmptyTicket_DoesNotCrash()
    {
        GrpcMatchmakingService service = new(NullLogger<GrpcMatchmakingService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        CancelTicketRequest reqNonExistent = new()
        {
            TicketId = "non_existent_ticket_" + Guid.NewGuid().ToString("N"),
            AccountId = 1234
        };

        CancelTicketResponse res1 = await service.CancelTicket(reqNonExistent, ctx);
        res1.Should().NotBeNull();
        res1.Cancelled.Should().BeTrue();

        CancelTicketRequest reqEmpty = new()
        {
            TicketId = "",
            AccountId = 0
        };

        CancelTicketResponse res2 = await service.CancelTicket(reqEmpty, ctx);
        res2.Should().NotBeNull();
        res2.Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionMatch_WithBoundaryAndExtremeValues_ReturnsValidAllocation()
    {
        GrpcMatchmakingService service = new(NullLogger<GrpcMatchmakingService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        ProvisionMatchRequest req = new()
        {
            MatchId = ulong.MaxValue,
            MapName = "caldavar",
            GameMode = GameMode.CaptainsMode,
            Region = "USE"
        };

        for (uint i = 1; i <= 5; i++)
        {
            req.LegionTeam.Add(
                new PlayerMatchCandidate { AccountId = i, Nickname = $"Legion_{i}", Mmr = 1500 + i * 50 });
            req.HellbourneTeam.Add(new PlayerMatchCandidate
                { AccountId = i + 10, Nickname = $"Hellbourne_{i}", Mmr = 1500 + i * 50 });
        }

        ProvisionMatchResponse res = await service.ProvisionMatch(req, ctx);

        res.Should().NotBeNull();
        res.ServerAllocated.Should().BeTrue();
        res.ServerIp.Should().NotBeNullOrWhiteSpace();
        res.ServerPort.Should().BeGreaterThan(0);
        res.MatchAuthToken.Should().NotBeNullOrWhiteSpace();
        res.MatchStartUnixTimestamp.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AcknowledgeMatchConnect_WithVariousTokens_ReturnsAuthorized()
    {
        GrpcMatchmakingService service = new(NullLogger<GrpcMatchmakingService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        AcknowledgeConnectRequest req = new()
        {
            MatchId = 999999,
            AccountId = 555,
            MatchAuthToken = "match_auth_token_xyz"
        };

        AcknowledgeConnectResponse res = await service.AcknowledgeMatchConnect(req, ctx);

        res.Should().NotBeNull();
        res.Authorized.Should().BeTrue();
        res.SlotIndex.Should().Be(0);
    }

    [Fact]
    public async Task MatchmakingService_HighConcurrencyStress_MaintainsStateConsistency()
    {
        GrpcMatchmakingService service = new(NullLogger<GrpcMatchmakingService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        List<Task> tasks = [];

        for (int i = 0; i < 100; i++)
        {
            uint accountId = (uint)(i + 1000);
            tasks.Add(Task.Run(async () =>
            {
                SubmitTicketResponse submitRes = await service.SubmitTicket(new SubmitTicketRequest
                {
                    Player = new PlayerMatchCandidate
                    {
                        AccountId = accountId,
                        Nickname = $"ConcurrentPlayer_{accountId}",
                        Mmr = 1600,
                        RegionPreference = "USE"
                    },
                    GameMode = GameMode.AllPick
                }, ctx);

                submitRes.TicketId.Should().NotBeNullOrWhiteSpace();

                CancelTicketResponse cancelRes = await service.CancelTicket(new CancelTicketRequest
                {
                    TicketId = submitRes.TicketId,
                    AccountId = accountId
                }, ctx);

                cancelRes.Cancelled.Should().BeTrue();
            }));
        }

        await Task.WhenAll(tasks);
    }
}
