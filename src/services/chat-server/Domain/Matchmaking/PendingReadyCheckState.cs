// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

namespace TRANSMUTANSTEIN.ChatServer.Services;

public sealed class PendingReadyCheckState
{
    public required int ReadyCheckId { get; init; }
    public required List<MatchmakingGroup> Groups { get; init; }
    public required HashSet<int> RequiredAccountIds { get; init; }
    public HashSet<int> AcceptedAccountIds { get; } = [];
    public HashSet<int> DeclinedAccountIds { get; } = [];
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string MapName { get; init; }
    public required string GameMode { get; init; }
    public Dictionary<int, DateTimeOffset> OriginalQueueStartTimes { get; init; } = [];
    public Dictionary<int, DateTimeOffset?> OriginalArcadePushCooldowns { get; init; } = [];
    public bool Resolved { get; set; }
    public int TotalPlayers => RequiredAccountIds.Count;
    public int AcceptedCount => AcceptedAccountIds.Count;
}
