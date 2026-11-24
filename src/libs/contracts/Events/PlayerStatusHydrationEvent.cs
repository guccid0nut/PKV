// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event published when a client initiates spectating on a match,
///     requesting the Chat Server to hydrate the spectator client's local user cache
///     with the target star player's server address via CHAT_CMD_UPDATE_STATUS (0x000C).
/// </summary>
[method: JsonConstructor]
public sealed record PlayerStatusHydrationEvent(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("spectator_account_id")] int SpectatorAccountId,
    [property: JsonPropertyName("star_player_account_id")] int StarPlayerAccountId,
    [property: JsonPropertyName("match_id")] int MatchId,
    [property: JsonPropertyName("server_address")] string? ServerAddress = null,
    [property: JsonPropertyName("match_name")] string? MatchName = null,
    [property: JsonPropertyName("star_player_name")] string? StarPlayerName = null,
    [property: JsonPropertyName("spectator_account_name")] string? SpectatorAccountName = null
) : IDomainEvent
{
    public PlayerStatusHydrationEvent(
        int spectatorAccountId,
        int starPlayerAccountId,
        int matchId,
        string? serverAddress = null,
        string? matchName = null,
        string? starPlayerName = null,
        string? spectatorAccountName = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), spectatorAccountId, starPlayerAccountId, matchId,
            serverAddress, matchName, starPlayerName, spectatorAccountName)
    {
    }
}
