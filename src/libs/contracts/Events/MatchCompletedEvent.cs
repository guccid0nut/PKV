// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a game match concludes and its statistics/rewards are finalized.
/// </summary>
[method: JsonConstructor]
public sealed record MatchCompletedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int MatchId,
    IReadOnlyList<int> AccountIds,
    int? WinnerTeam,
    TimeSpan Duration,
    string GameMode,
    int? ServerId = null,
    string? ServerAddress = null
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId" /> and UTC timestamp.
    /// </summary>
    public MatchCompletedEvent(
        int matchId,
        IReadOnlyList<int> accountIds,
        int? winnerTeam,
        TimeSpan duration,
        string gameMode,
        int? serverId = null,
        string? serverAddress = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), matchId, accountIds, winnerTeam, duration, gameMode,
            serverId, serverAddress)
    {
    }
}
