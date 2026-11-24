// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Constants;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.DTOs;
using NEXUS.Contracts.EntityCache;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers;

/// <summary>
///     Serves structured custom-map match scoreboards (including 20-player 10v10 and Enfo's classes)
///     and per-map career statistics directly to both the K2 client JSON forms (<c>&lt;form php="false"&gt;</c>)
///     and HTTP portal callers.
/// </summary>
[ApiController]
[Produces("application/json")]
public sealed class CustomMapStatsController(
    ICustomMapStatsRepository customMapStatsRepository,
    IHeroDefinitionService heroDefinitionService,
    ILogger<CustomMapStatsController> logger,
    IDatabase? distributedCache = null) : ControllerBase
{
    [HttpGet("api/matches/{matchId:int}/custom-stats")]
    [ProducesResponseType(typeof(CustomMatchStatsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMatchCustomStats(int matchId, CancellationToken ct = default)
    {
        long startTs = Stopwatch.GetTimestamp();
        if (matchId <= 0)
        {
            return BadRequest(new { error = "Invalid matchId. Provide a positive match identifier." });
        }

        CustomMatchStatsResponseDto? result = await customMapStatsRepository.GetMatchCustomStatsAsync(
            matchId, ResolveStandardHero, ct);

        if (result is null)
        {
            bool awaited = await MatchSubmissionIdempotency.WaitForCompletionAsync(
                distributedCache, matchId, TimeSpan.FromSeconds(4.0), cancellationToken: ct);
            if (awaited)
            {
                logger.LogInformation("[CustomMapStats] Awaited in-flight stats ingestion for Match #{MatchId}; retrying read.", matchId);
                result = await customMapStatsRepository.GetMatchCustomStatsAsync(
                    matchId, ResolveStandardHero, ct);
            }
        }

        if (result is null)
        {
            logger.LogInformation("[CustomMapStats] Match #{MatchId} not found", matchId);
            return NotFound(new { error = $"Match {matchId} not found" });
        }

        double elapsedMs = Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
        logger.LogInformation(
            "[CustomMapStats] Served match #{MatchId} (Map='{MapSlug}', Schema='{SchemaKey}', Players={PlayerCount}) in {ElapsedMs:F1}ms",
            matchId, result.Banner.MapSlug, result.Banner.SchemaKey, result.Players.Count, elapsedMs);

        return Ok(result);
    }

    [HttpGet("api/stats/player/{nickname}/map-stats")]
    [ProducesResponseType(typeof(PlayerMapStatsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlayerMapStats(
        string nickname,
        [FromQuery] string? map = "caldavar",
        CancellationToken ct = default)
    {
        long startTs = Stopwatch.GetTimestamp();
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return BadRequest(new { error = "Nickname is required." });
        }

        PlayerMapStatsResponseDto? result = await customMapStatsRepository.GetPlayerMapStatsAsync(
            nickname, map, ResolveStandardHero, ct);

        if (result is null)
        {
            logger.LogInformation("[CustomMapStats] Player '{Nickname}' not found for map '{MapSlug}'", nickname, map);
            return NotFound(new { error = $"Account '{nickname}' not found" });
        }

        double elapsedMs = Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
        logger.LogInformation(
            "[CustomMapStats] Served player '{Nickname}' map='{MapSlug}' (Matches={Matches}, Cards={Cards}) in {ElapsedMs:F1}ms",
            result.Nickname, result.MapSlug, result.MatchesPlayed, result.SummaryCards.Count, elapsedMs);

        return Ok(result);
    }

    [HttpGet("api/stats/player/{nickname}/maps")]
    [ProducesResponseType(typeof(IReadOnlyList<PlayerMapOverviewItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlayerMapOverview(string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return BadRequest(new { error = "Nickname is required." });
        }

        IReadOnlyList<PlayerMapOverviewItemDto> overview =
            await customMapStatsRepository.GetPlayerMapOverviewAsync(nickname, ct);
        return Ok(overview);
    }

    private (string? DisplayName, string? IconVfs) ResolveStandardHero(string rawIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier))
        {
            return (null, null);
        }

        if (CustomHeroCatalog.TryStripArenaHeroPrefix(rawIdentifier, out string arenaBase))
        {
            string cleanArenaBase = heroDefinitionService.CleanHeroIdentifier(arenaBase);
            HeroDefinitionDto? arenaDef = heroDefinitionService.GetHeroDefinition(cleanArenaBase);
            if (arenaDef is not null)
            {
                return (arenaDef.Name, arenaDef.IconPath);
            }
        }

        string cleanId = heroDefinitionService.CleanHeroIdentifier(rawIdentifier);
        HeroDefinitionDto? def = heroDefinitionService.GetHeroDefinition(cleanId);
        return def is not null ? (def.Name, def.IconPath) : (null, null);
    }
}
