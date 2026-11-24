// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using KONGOR.MasterServer.Constants;
using KONGOR.MasterServer.Features.Telemetry;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Telemetry;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers;

/// <summary>
/// REST API controller exposing real-time ecosystem telemetry, player counts, active matches, and leaderboards.
/// </summary>
[ApiController]
[Route("api/telemetry")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public sealed partial class LiveTelemetryController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<LiveTelemetryController> _logger;

    [ActivatorUtilitiesConstructor]
    public LiveTelemetryController(
        IMediator mediator,
        ILogger<LiveTelemetryController> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }



    /// <summary>
    /// Retrieves live ecosystem telemetry including concurrent players, active games, and top players.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Live ecosystem telemetry payload.</returns>
    [HttpGet("live")]
    [ProducesResponseType(typeof(LiveTelemetryResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLiveTelemetry(CancellationToken cancellationToken)
    {
        LogIncomingTelemetryRequest();

        try
        {
            LiveTelemetryResponseDto response = await _mediator.Send(new GetLiveTelemetryQuery(), cancellationToken);
            LogReturningTelemetry(response.OnlinePlayers, response.MatchesInProgress, response.TopWinratePlayers.Count);
            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            // Client closed connection or cancelled polling request - standard HTTP cancellation
            return NoContent();
        }
        catch (Exception ex)
        {
            LogTelemetryError(ex);
            return Ok(new LiveTelemetryResponseDto
            {
                OnlinePlayers = 0,
                MatchesInProgress = 0,
                TopWinratePlayers = []
            });
        }
    }

    /// <summary>
    /// Retrieves detailed live population breakdown for the SysBar "Players Online" popup:
    /// live online players, games in progress by region, available non-arcade servers by region,
    /// and players queued for TMM by queue title and region pool.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detailed population breakdown JSON payload.</returns>
    [HttpGet("population")]
    [EnableRateLimiting(RateLimiterPolicies.Relaxed)]
    [Produces("application/json")]
    [ProducesResponseType(typeof(PopulationBreakdownResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPopulationBreakdown(CancellationToken cancellationToken)
    {
        LogIncomingPopulationRequest();

        try
        {
            PopulationBreakdownResponseDto response = await _mediator.Send(
                new GetPopulationBreakdownQuery(),
                cancellationToken);
            int availableServersTotal = response.ServersAvailableByRegion.Sum(static r => r.Count);
            int totalQueuedPlayers = response.TmmQueues.Sum(static q => q.TotalQueued);
            LogReturningPopulation(
                response.OnlinePlayers,
                response.GamesInProgressTotal,
                availableServersTotal,
                totalQueuedPlayers);
            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return NoContent();
        }
        catch (Exception ex)
        {
            LogPopulationError(ex);
            return Ok(PopulationBreakdownDefaults.CreateEmpty(TimeProvider.System.GetUtcNow()));
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LiveTelemetry] Received request for live ecosystem metrics.")]
    private partial void LogIncomingTelemetryRequest();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LiveTelemetry] Returning {OnlineCount} online, {MatchCount} matches, {TopCount} leaderboard entries.")]
    private partial void LogReturningTelemetry(int onlineCount, int matchCount, int topCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "[LiveTelemetry] Failed to compute live telemetry.")]
    private partial void LogTelemetryError(Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LiveTelemetry][Population] Received request for detailed population breakdown.")]
    private partial void LogIncomingPopulationRequest();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LiveTelemetry][Population] Returning OnlinePlayers={OnlinePlayers}, GamesInProgressTotal={GamesInProgressTotal}, AvailableServersTotal={AvailableServersTotal}, TotalQueuedPlayers={TotalQueuedPlayers}.")]
    private partial void LogReturningPopulation(
        int onlinePlayers,
        int gamesInProgressTotal,
        int availableServersTotal,
        int totalQueuedPlayers);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[LiveTelemetry][Population] Failed to compute population breakdown; returning safe empty fallback.")]
    private partial void LogPopulationError(Exception ex);
}
