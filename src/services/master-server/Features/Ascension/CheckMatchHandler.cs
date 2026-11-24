// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Ascension;
using KONGOR.MasterServer.Extensions.Cache;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Ascension;

/// <summary>
///     MediatR query handler for checking if a match is an active season match before starting (<see cref="CheckMatchQuery"/>).
/// </summary>
public sealed class CheckMatchHandler(
    IConnectionMultiplexer redis,
    ILogger<CheckMatchHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<CheckMatchQuery, CheckMatchResultDto>
{
    private readonly IDatabase _distributedCache = redis.GetDatabase();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CheckMatchResultDto> Handle(
        CheckMatchQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [CheckMatchHandler] Checking season match status for Match ID {MatchId}",
            _timeProvider.GetUtcNow(),
            request.MatchId);

        MatchStartData? matchStartData = await _distributedCache.GetMatchStartData(request.MatchId);
        bool isSeasonMatch = matchStartData is not null;

        var responseData = new
        {
            error_code = 100,
            data = new
            {
                is_season_match = isSeasonMatch,
                comment = Array.Empty<string>(),
                referee = Array.Empty<string>()
            }
        };

        return new CheckMatchResultDto(isSeasonMatch, 100, responseData);
    }
}
