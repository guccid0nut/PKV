// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Core.Services.Session;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.DTOs;
using NEXUS.Contracts.Telemetry;
using ZORGATH.WebPortal.API.Features.PlayerStats;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// REST API controller providing player statistics, matchmaking history, leaderboards, hero/item compendium data, match telemetry, and moderation reporting.
/// </summary>
[ApiController]
[Route("api/stats")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class PlayerStatsApiController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of <see cref="PlayerStatsApiController"/> using MediatR dispatching.
    /// </summary>
    /// <param name="mediator">The MediatR mediator instance.</param>
    [ActivatorUtilitiesConstructor]
    public PlayerStatsApiController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Retrieves the public player profile summary by in-game name (IGN).
    /// </summary>
    /// <param name="ign">In-game nickname of the player.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Player profile summary DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Player profile summary retrieved successfully.</response>
    /// <response code="404">Player with the specified nickname was not found.</response>
    [HttpGet("player/{ign}")]
    [ProducesResponseType(typeof(PlayerProfileSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlayerProfile(string ign, CancellationToken ct = default)
    {
        PlayerProfileSummaryDto? profile = await _mediator.Send(new GetPlayerProfileQuery(ign), ct);
        return profile is not null ? Ok(profile) : NotFound("Player not found");
    }

    /// <summary>
    /// Retrieves historical account gameplay statistics by account ID.
    /// </summary>
    /// <param name="accountId">Unique identifier of the account.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account statistics if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Account statistics retrieved successfully.</response>
    /// <response code="404">Account statistics not found for the given ID.</response>
    [HttpGet("account/{accountId:int}")]
    [ProducesResponseType(typeof(AccountStatisticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountStatistics(int accountId, CancellationToken ct = default)
    {
        AccountStatisticsDto? stats = await _mediator.Send(new GetAccountStatisticsQuery(accountId), ct);
        return stats != null ? Ok(stats) : NotFound();
    }

    /// <summary>
    /// Retrieves the top 100 ranked accounts based on competitive MMR and ladder performance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of top ranked account statistics.</returns>
    /// <response code="200">Top ranked accounts retrieved successfully.</response>
    [HttpGet("top-ranked")]
    [ProducesResponseType(typeof(IReadOnlyList<AccountStatisticsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTopRanked(CancellationToken ct = default)
    {
        IReadOnlyList<AccountStatisticsDto> top = await _mediator.Send(new GetTopRankedAccountsQuery(100), ct);
        return Ok(top);
    }

    /// <summary>
    /// Retrieves a paginated and filtered competitive leaderboard.
    /// </summary>
    /// <param name="page">Page number (1-based index).</param>
    /// <param name="pageSize">Number of leaderboard entries per page.</param>
    /// <param name="search">Optional search query to filter player nicknames.</param>
    /// <param name="mode">Optional game mode filter.</param>
    /// <param name="map">Optional map filter.</param>
    /// <param name="sort">Sorting criterion (e.g., "winrate", "mmr", "wins").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated leaderboard results with total counts and entry details.</returns>
    /// <response code="200">Leaderboard results retrieved successfully.</response>
    [HttpGet("leaderboard")]
    [ProducesResponseType(typeof(LeaderboardResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLeaderboard(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? mode = null,
        [FromQuery] string? map = null,
        [FromQuery] string sort = "winrate",
        CancellationToken ct = default)
    {
        int safePage = Math.Max(1, page);
        int safePageSize = Math.Clamp(pageSize, 1, 100);
        LeaderboardResultDto result =
            await _mediator.Send(new GetLeaderboardQuery(safePage, safePageSize, search, mode, map, sort), ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves the host server leaderboard ranking hosts by games hosted and uptime reliability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of host leaderboard entries.</returns>
    /// <response code="200">Host leaderboard retrieved successfully.</response>
    [HttpGet("leaderboard/hosts")]
    [ProducesResponseType(typeof(IReadOnlyList<HostLeaderboardEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHostLeaderboard(CancellationToken ct = default)
    {
        IReadOnlyList<HostLeaderboardEntryDto> list = await _mediator.Send(new GetHostLeaderboardQuery(), ct);
        return Ok(list);
    }

    /// <summary>
    /// Retrieves recently completed matches across all servers.
    /// </summary>
    /// <param name="limit">Maximum number of recent matches to return (default: 50).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recent match statistics DTOs.</returns>
    /// <response code="200">Recent matches retrieved successfully.</response>
    [HttpGet("matches/recent")]
    [ProducesResponseType(typeof(List<MatchStatisticsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentMatches([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        IReadOnlyList<MatchStatisticsDto> matches = await _mediator.Send(new GetRecentMatchesQuery(safeLimit), ct);
        return Ok(matches);
    }

    /// <summary>
    /// Retrieves recent match history for a specific player account.
    /// </summary>
    /// <param name="accountId">Unique identifier of the account.</param>
    /// <param name="limit">Maximum number of matches to retrieve (default: 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of match statistics DTOs for the account.</returns>
    /// <response code="200">Matches for account retrieved successfully.</response>
    [HttpGet("matches/account/{accountId:int}")]
    [HttpGet("accounts/{accountId:int}/matches")]
    [ProducesResponseType(typeof(List<MatchStatisticsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMatchesForAccount(int accountId, [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        IReadOnlyList<MatchStatisticsDto> matches =
            await _mediator.Send(new GetAccountMatchesQuery(accountId, safeLimit), ct);
        return Ok(matches);
    }

    /// <summary>
    /// Wave 3B: Retrieves recent player match history via Dapper read projection.
    /// </summary>
    /// <param name="accountId">Unique identifier of the player account.</param>
    /// <param name="limit">Maximum number of history entries to return (1..100, default 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of lightweight match history projection rows.</returns>
    [HttpGet("player/{accountId:int}/history")]
    [ProducesResponseType(typeof(IReadOnlyList<PlayerMatchHistoryProjectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPlayerMatchHistoryProjection(
        int accountId,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (accountId <= 0)
        {
            return BadRequest("Invalid account ID.");
        }

        int safeLimit = Math.Clamp(limit, 1, 100);
        IReadOnlyList<PlayerMatchHistoryProjectionDto> rows =
            await _mediator.Send(new GetPlayerMatchHistoryProjectionQuery(accountId, safeLimit), ct);
        return Ok(rows);
    }

    /// <summary>
    /// Retrieves comprehensive match details and statistics by match ID.
    /// </summary>
    /// <param name="matchId">Unique match identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Match details DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Match found and details returned.</response>
    /// <response code="404">Match with the specified ID was not found.</response>
    [HttpGet("matches/{matchId:int}")]
    [ProducesResponseType(typeof(MatchStatisticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMatchById(int matchId, CancellationToken ct = default)
    {
        MatchStatisticsDto? match = await _mediator.Send(new GetMatchDetailsQuery(matchId), ct);
        return match != null ? Ok(match) : NotFound();
    }

    /// <summary>
    /// Retrieves individual player performance statistics for all participants in a match.
    /// </summary>
    /// <param name="matchId">Unique match identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of player statistics DTOs for the match.</returns>
    /// <response code="200">Player statistics for the match retrieved successfully.</response>
    [HttpGet("matches/{matchId:int}/players")]
    [ProducesResponseType(typeof(List<PlayerStatisticsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlayerStatisticsForMatch(int matchId, CancellationToken ct = default)
    {
        IReadOnlyList<PlayerStatisticsDto> players =
            await _mediator.Send(new GetMatchPlayerStatsQuery(matchId), ct);
        return Ok(players);
    }

    /// <summary>
    /// Retrieves recent player performance records across recent matches for an account.
    /// </summary>
    /// <param name="accountId">Unique identifier of the player account.</param>
    /// <param name="limit">Maximum number of player performance records to retrieve (default: 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recent player statistics DTOs.</returns>
    /// <response code="200">Recent player statistics retrieved successfully.</response>
    [HttpGet("players/{accountId:int}/recent")]
    [HttpGet("accounts/{accountId:int}/player-stats")]
    [HttpGet("accounts/{accountId:int}/recent")]
    [ProducesResponseType(typeof(List<PlayerStatisticsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentPlayerStatistics(int accountId, [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        IReadOnlyList<PlayerStatisticsDto> stats =
            await _mediator.Send(new GetRecentPlayerStatsQuery(accountId, safeLimit), ct);
        return Ok(stats);
    }

    /// <summary>
    /// Filters a list of match IDs and returns only those that have already been recorded in the database.
    /// </summary>
    /// <param name="matchIds">List of match IDs to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of match IDs that already exist.</returns>
    /// <response code="200">List of verified existing match IDs.</response>
    [HttpPost("matches/submitted-ids")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<int>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubmittedMatchIds([FromBody] List<int> matchIds, CancellationToken ct = default)
    {
        List<int> submitted = await _mediator.Send(new GetSubmittedMatchIdsQuery(matchIds), ct);
        return Ok(submitted);
    }

    /// <summary>
    /// Retrieves evidence assets (chat logs, replay status, voice tracks) recorded for a match.
    /// </summary>
    /// <param name="matchId">Unique match identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Match evidence DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Match evidence retrieved successfully.</response>
    /// <response code="404">No evidence recorded for the specified match ID.</response>
    [HttpGet("matches/{matchId:int}/evidence")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(MatchEvidenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMatchEvidence(int matchId, CancellationToken ct = default)
    {
        MatchEvidenceDto? evidence = await _mediator.Send(new GetMatchEvidenceQuery(matchId), ct);
        return evidence != null ? Ok(evidence) : NotFound();
    }

    /// <summary>
    /// Creates or updates evidence records (chat logs, replay files, voice communications) for a match.
    /// </summary>
    /// <param name="evidence">Match evidence data payload.</param>
    /// <param name="matchId">Optional match ID route parameter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if evidence was persisted successfully.</returns>
    /// <response code="200">Evidence saved successfully.</response>
    /// <response code="400">Match ID was missing or invalid.</response>
    [HttpPost("matches/evidence")]
    [HttpPost("matches/{matchId:int}/evidence")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveMatchEvidence([FromBody] MatchEvidenceDto evidence, [FromRoute] int? matchId = null,
        CancellationToken ct = default)
    {
        int targetMatchId = matchId ?? evidence.MatchID;
        if (targetMatchId <= 0) return BadRequest("Match ID is required");

        bool success = await _mediator.Send(new SaveMatchEvidenceCommand(targetMatchId, evidence), ct);
        return success ? Ok(true) : BadRequest("Failed to save match evidence");
    }

    /// <summary>
    /// Retrieves all approved community game server hosts.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of approved server hosts.</returns>
    /// <response code="200">Approved server hosts retrieved successfully.</response>
    [HttpGet("hosts")]
    [ProducesResponseType(typeof(List<ServerHostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetServerHosts(CancellationToken ct = default)
    {
        List<ServerHostDto> hosts = await _mediator.Send(new GetServerHostsQuery(), ct);
        return Ok(hosts);
    }

    /// <summary>
    /// Retrieves compendium definitions and stats for all Heroes of Newerth heroes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of hero details DTOs.</returns>
    /// <response code="200">Hero details retrieved successfully.</response>
    [HttpGet("heroes")]
    [ProducesResponseType(typeof(List<HeroDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllHeroDetails(CancellationToken ct = default)
    {
        List<HeroDetailDto> heroes = await _mediator.Send(new GetAllHeroDetailsQuery(), ct);
        return Ok(heroes);
    }

    /// <summary>
    /// Retrieves compendium details for a specific hero by hero code or display name.
    /// </summary>
    /// <param name="heroId">Hero code identifier (e.g., "Hero_Pyromancer") or display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hero details DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Hero details retrieved successfully.</response>
    /// <response code="404">Hero not found with the specified code or name.</response>
    [HttpGet("heroes/{heroId}")]
    [ProducesResponseType(typeof(HeroDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHeroDetail(string heroId, CancellationToken ct = default)
    {
        HeroDetailDto? hero = await _mediator.Send(new GetHeroDetailQuery(heroId), ct);
        return hero != null ? Ok(hero) : NotFound();
    }

    /// <summary>
    /// Retrieves compendium definitions, recipes, and attributes for all hero items.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of hero item DTOs.</returns>
    /// <response code="200">Hero items retrieved successfully.</response>
    [HttpGet("items")]
    [ProducesResponseType(typeof(List<HeroItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllHeroItems(CancellationToken ct = default)
    {
        List<HeroItemDto> items = await _mediator.Send(new GetAllHeroItemsQuery(), ct);
        return Ok(items);
    }

    /// <summary>
    /// Submits a player conduct report for moderation review.
    /// </summary>
    /// <param name="reportDto">Report submission payload containing reporter, offender, match, and incident details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if report was successfully logged.</returns>
    /// <response code="200">Player report recorded successfully.</response>
    [HttpPost("reports")]
    [HttpPost("reports/submit")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitPlayerReport([FromBody] PlayerReportDto reportDto, CancellationToken ct = default)
    {
        await _mediator.Send(new SubmitPlayerReportCommand(reportDto), ct);
        return Ok(true);
    }

    /// <summary>
    /// Retrieves a list of account login names for approved content creators and streamers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of approved streamer account names.</returns>
    /// <response code="200">Approved streamer logins retrieved successfully.</response>
    [HttpGet("streamers/approved")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetApprovedStreamerLogins(CancellationToken ct = default)
    {
        List<string> streamerLogins = await _mediator.Send(new GetApprovedStreamersQuery(), ct);
        return Ok(streamerLogins);
    }

    /// <summary>
    /// Retrieves real-time server cluster telemetry including active players and ongoing matches.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Live telemetry statistics DTO.</returns>
    /// <response code="200">Live telemetry data retrieved successfully.</response>
    [HttpGet("telemetry/live")]
    [ProducesResponseType(typeof(LiveTelemetryDataDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLiveTelemetry(CancellationToken ct = default)
    {
        LiveTelemetryDataDto dto = await _mediator.Send(new GetLiveClusterTelemetryQuery(), ct);
        return Ok(dto);
    }

    /// <summary>
    /// Searches player accounts matching a nickname query with pagination.
    /// </summary>
    /// <param name="query">Search term to match against account nicknames.</param>
    /// <param name="page">Page index (1-based).</param>
    /// <param name="pageSize">Number of accounts per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching account DTOs.</returns>
    /// <response code="200">Accounts matching query retrieved successfully.</response>
    [HttpGet("accounts/search")]
    [ProducesResponseType(typeof(List<AccountDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchAccounts([FromQuery] string? query, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        int safePage = Math.Max(1, page);
        int safePageSize = Math.Clamp(pageSize, 1, 100);
        IReadOnlyList<AccountDto> accounts =
            await _mediator.Send(new SearchAccountsQuery(query, safePage, safePageSize), ct);
        return Ok(accounts);
    }

    /// <summary>
    /// Retrieves an account by exact in-game nickname (IGN).
    /// </summary>
    /// <param name="ign">In-game nickname.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Account found and returned.</response>
    /// <response code="404">Account with specified IGN was not found.</response>
    [HttpGet("accounts/by-ign/{ign}")]
    [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountByIgn(string ign, CancellationToken ct = default)
    {
        AccountDto? account = await _mediator.Send(new GetAccountByIgnQuery(ign), ct);
        return account != null ? Ok(account) : NotFound();
    }

    /// <summary>
    /// Retrieves total number of recorded matches across all time.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total match count integer.</returns>
    /// <response code="200">Total match count retrieved successfully.</response>
    [HttpGet("matches/count")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTotalMatchesCount(CancellationToken ct = default)
    {
        int count = await _mediator.Send(new GetTotalMatchesCountQuery(), ct);
        return Ok(count);
    }

    /// <summary>
    /// Retrieves live in-memory/Redis cached match telemetry for an active match.
    /// </summary>
    /// <param name="matchId">Unique match identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Live server tick payload if found; otherwise 400 BadRequest or 404 NotFound.</returns>
    /// <response code="200">Live match telemetry state retrieved successfully.</response>
    /// <response code="400">Match ID is invalid.</response>
    /// <response code="404">No active telemetry found for the given match ID.</response>
    [HttpGet("telemetry/live/{matchId:int}")]
    [ProducesResponseType(typeof(LiveServerTickPayloadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLiveMatchTelemetry(int matchId, CancellationToken ct = default)
    {
        if (matchId <= 0) return BadRequest("Invalid match ID.");

        LiveServerTickPayloadDto? cachedState =
            await _mediator.Send(new GetLiveMatchTelemetryQuery(matchId), ct);

        if (cachedState != null)
        {
            return Ok(cachedState);
        }

        return NotFound("No active telemetry found for match.");
    }

    /// <summary>
    /// Retrieves custom-map match scoreboard columns, banner metadata, and per-player custom stats (up to 20 players).
    /// </summary>
    /// <param name="matchId">Unique match identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Custom match scoreboard DTO if found; otherwise 404 NotFound.</returns>
    [HttpGet("matches/{matchId:int}/custom-stats")]
    [ProducesResponseType(typeof(CustomMatchStatsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMatchCustomStats(int matchId, CancellationToken ct = default)
    {
        if (matchId <= 0) return BadRequest("Invalid match ID.");

        CustomMatchStatsResponseDto? result =
            await _mediator.Send(new GetMatchCustomStatsQuery(matchId), ct);
        return result is not null ? Ok(result) : NotFound("Match not found.");
    }

    /// <summary>
    /// Retrieves per-map career summary cards, top custom/standard heroes, and personal best records for a player.
    /// </summary>
    /// <param name="ign">In-game nickname of the player.</param>
    /// <param name="map">Canonical or alias map slug (e.g., <c>soccer</c>, <c>enfos</c>, <c>devowars</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Player map career statistics DTO if found; otherwise 404 NotFound.</returns>
    [HttpGet("player/{ign}/map-stats")]
    [ProducesResponseType(typeof(PlayerMapStatsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlayerMapStats(string ign, [FromQuery] string? map = "caldavar", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ign)) return BadRequest("Nickname is required.");

        PlayerMapStatsResponseDto? result =
            await _mediator.Send(new GetPlayerMapStatsQuery(ign, map), ct);
        return result is not null ? Ok(result) : NotFound("Player not found.");
    }

    /// <summary>
    /// Lists all maps with recorded statistics for a player.
    /// </summary>
    /// <param name="ign">In-game nickname of the player.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of per-map summary items.</returns>
    [HttpGet("player/{ign}/maps")]
    [ProducesResponseType(typeof(IReadOnlyList<PlayerMapOverviewItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlayerMapsOverview(string ign, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ign)) return BadRequest("Nickname is required.");

        IReadOnlyList<PlayerMapOverviewItemDto> overview =
            await _mediator.Send(new GetPlayerMapsOverviewQuery(ign), ct);
        return Ok(overview);
    }

    /// <summary>
    /// Retrieves a paginated, searchable, and sortable directory of active Newerth clans.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (1..500, default 24).</param>
    /// <param name="search">Optional search filter matching clan name, tag, or leader name.</param>
    /// <param name="sort">Sort criterion: mmr (default), members, winrate, matches, or newest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated clan directory response.</returns>
    [HttpGet("clans")]
    [ProducesResponseType(typeof(ClanDirectoryResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetClanDirectory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24,
        [FromQuery] string? search = null,
        [FromQuery] string sort = "mmr",
        CancellationToken ct = default)
    {
        int safePage = Math.Max(1, page);
        int safePageSize = Math.Clamp(pageSize, 1, 500);
        ClanDirectoryResultDto result =
            await _mediator.Send(new GetClanDirectoryQuery(safePage, safePageSize, search, sort), ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves public details and ordered active member roster for a single clan.
    /// </summary>
    /// <param name="clanId">Unique identifier of the clan.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Clan detail DTO if found and active; otherwise 404 NotFound.</returns>
    [HttpGet("clans/{clanId:int}")]
    [ProducesResponseType(typeof(ClanDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClanDetail(int clanId, CancellationToken ct = default)
    {
        if (clanId <= 0) return BadRequest("Invalid clan ID.");

        ClanDetailDto? detail = await _mediator.Send(new GetClanDetailQuery(clanId), ct);
        return detail is not null ? Ok(detail) : NotFound("Clan not found.");
    }
}
