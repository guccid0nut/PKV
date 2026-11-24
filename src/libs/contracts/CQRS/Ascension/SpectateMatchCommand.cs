// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Ascension;

/// <summary>
///     Command to initiate spectating of an active match, publishing hydration events.
/// </summary>
public record SpectateMatchCommand(int MatchId, int SpectatorAccountId, string? Cookie)
    : IRequest<SpectateMatchResultDto>;

/// <summary>
///     Result of spectate match request with star player and server directory.
/// </summary>
public record SpectateMatchResultDto(
    int Result,
    string StarPlayer,
    int MatchId,
    string SpectateDir,
    string MatchVersion,
    object ResponseData);
