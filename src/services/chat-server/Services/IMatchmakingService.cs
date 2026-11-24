// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Services;

public interface IMatchmakingService : IHostedService
{
    ConcurrentDictionary<int, MatchmakingGroup> Groups { get; }

    MatchmakingGroup? GetMatchmakingGroup(OneOf<int, string> memberIdentifier);
    MatchmakingGroup? GetMatchmakingGroupByMemberID(int memberID);
    MatchmakingGroup? GetMatchmakingGroupByMemberName(string memberName);
    MatchmakingGroup? GetMatchmakingGroupByInvitedUser(string accountName);

    Task ConfirmMatch(int matchupId, int matchId, string serverAddress, int serverPort, string? localAddress = null, int? localPort = null);

    PendingReadyCheckState? StartReadyCheck(IReadOnlyList<MatchmakingGroup> groups);
    bool SubmitReadyCheckResponse(int readyCheckId, int accountId, bool accepted);
    Task<bool> SubmitReadyCheckResponseAsync(int readyCheckId, int accountId, bool accepted);
    bool SubmitPendingMatchAccept(int accountId);
    Task<bool> SubmitPendingMatchAcceptAsync(int accountId);
    IReadOnlyList<PendingReadyCheckState> EvaluateExpiredReadyChecks(DateTimeOffset now);
    PendingReadyCheckState? GetPendingReadyCheck(int readyCheckId);
}
