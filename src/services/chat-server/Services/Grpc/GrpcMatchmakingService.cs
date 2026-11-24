// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Grpc.Core;
using NEXUS.Contracts.Matchmaking.V1;
using ChatMatchmaking = TRANSMUTANSTEIN.ChatServer.Services.IMatchmakingService;

namespace TRANSMUTANSTEIN.ChatServer.Services.Grpc;

public sealed class GrpcMatchmakingService(
    ILogger<GrpcMatchmakingService> logger,
    ChatMatchmaking? matchmakingService = null,
    TimeProvider? timeProvider = null) : NEXUS.Contracts.Matchmaking.V1.MatchmakingService.MatchmakingServiceBase
{
    private readonly ConcurrentDictionary<string, SubmitTicketRequest> _activeTickets = new();
    private readonly ILogger<GrpcMatchmakingService> _logger = logger;
    private readonly ChatMatchmaking? _matchmakingService = matchmakingService;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public override Task<SubmitTicketResponse> SubmitTicket(SubmitTicketRequest request, ServerCallContext context)
    {
        string ticketId = Guid.NewGuid().ToString("N");
        _logger.LogInformation(
            "[GrpcMatchmakingService] SubmitTicket: TicketId={TicketId}, Player={Nick} ({AccountId}), Mode={Mode}",
            ticketId, request.Player?.Nickname, request.Player?.AccountId, request.GameMode);

        _activeTickets[ticketId] = request;

        SubmitTicketResponse response = new()
        {
            TicketId = ticketId,
            EstimatedWaitSeconds = 15
        };

        return Task.FromResult(response);
    }

    public override Task<CancelTicketResponse> CancelTicket(CancelTicketRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[GrpcMatchmakingService] CancelTicket: TicketId={TicketId}, AccountId={AccountId}",
            request.TicketId, request.AccountId);

        bool removed = _activeTickets.TryRemove(request.TicketId, out _);

        return Task.FromResult(new CancelTicketResponse
        {
            Cancelled = true
        });
    }

    public override Task<ProvisionMatchResponse> ProvisionMatch(ProvisionMatchRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "[GrpcMatchmakingService] ProvisionMatch: MatchId={MatchId}, Region={Region}, Mode={Mode}",
            request.MatchId, request.Region, request.GameMode);

        ProvisionMatchResponse response = new()
        {
            ServerAllocated = true,
            ServerIp = "127.0.0.1",
            ServerPort = 11235,
            MatchAuthToken = "match_auth_" + Guid.NewGuid().ToString("N"),
            MatchStartUnixTimestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds()
        };

        return Task.FromResult(response);
    }

    public override Task<AcknowledgeConnectResponse> AcknowledgeMatchConnect(AcknowledgeConnectRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "[GrpcMatchmakingService] AcknowledgeMatchConnect: MatchId={MatchId}, AccountId={AccountId}",
            request.MatchId, request.AccountId);

        return Task.FromResult(new AcknowledgeConnectResponse
        {
            Authorized = true,
            SlotIndex = 0
        });
    }
}
