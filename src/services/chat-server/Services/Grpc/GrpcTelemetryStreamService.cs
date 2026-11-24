// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Grpc.Core;
using NEXUS.Contracts.Telemetry.V1;

namespace TRANSMUTANSTEIN.ChatServer.Services.Grpc;

public sealed class GrpcTelemetryStreamService(
    ILogger<GrpcTelemetryStreamService> logger,
    ITelemetryPublisherService? telemetryPublisher = null) : TelemetryStreamService.TelemetryStreamServiceBase
{
    private readonly ILogger<GrpcTelemetryStreamService> _logger = logger;
    private readonly ITelemetryPublisherService? _telemetryPublisher = telemetryPublisher;

    public override async Task<StreamSummaryResponse> StreamCombatEvents(
        IAsyncStreamReader<CombatTelemetryFrame> requestStream,
        ServerCallContext context)
    {
        _logger.LogInformation("[GrpcTelemetryStreamService] StreamCombatEvents client stream started.");

        ulong totalFrames = 0;

        while (await requestStream.MoveNext(context.CancellationToken))
        {
            CombatTelemetryFrame frame = requestStream.Current;
            totalFrames++;
        }

        _logger.LogInformation("[GrpcTelemetryStreamService] StreamCombatEvents completed. Ingested {Count} frames.",
            totalFrames);

        return new StreamSummaryResponse
        {
            TotalFramesReceived = totalFrames,
            IsHealthy = true
        };
    }

    public override async Task<IngestSummaryResponse> IngestMatchSummary(
        MatchSummaryPayload request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "[GrpcTelemetryStreamService] IngestMatchSummary received for Match #{MatchId}, Duration: {Duration}s, WinningTeam: {WinningTeam}",
            request.MatchId, request.DurationSeconds, request.WinningTeam);

        if (_telemetryPublisher != null) await _telemetryPublisher.OnMatchEndedAsync((int)request.MatchId);

        return new IngestSummaryResponse
        {
            Success = true,
            StorageUri = $"/replays/M{request.MatchId}.honreplay"
        };
    }
}
