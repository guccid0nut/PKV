// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.CQRS.Telemetry;

namespace KONGOR.MasterServer.Features.Telemetry;

/// <summary>
/// In-process MediatR dispatcher for LiveTelemetry queries used in testing.
/// </summary>
public sealed class LocalLiveTelemetryMediator(
    GetLiveTelemetryHandler handler,
    GetPopulationBreakdownHandler? populationHandler = null) : IMediator
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GetLiveTelemetryQuery q && typeof(TResponse) == typeof(LiveTelemetryResponseDto))
            return (Task<TResponse>)(object)handler.Handle(q, cancellationToken);

        if (request is GetPopulationBreakdownQuery pq &&
            typeof(TResponse) == typeof(PopulationBreakdownResponseDto) &&
            populationHandler is not null)
            return (Task<TResponse>)(object)populationHandler.Handle(pq, cancellationToken);

        throw new NotSupportedException($"LocalLiveTelemetryMediator does not support {request.GetType().Name}");
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
}
