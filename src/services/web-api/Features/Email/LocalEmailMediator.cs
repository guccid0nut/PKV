// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Email;
using ZORGATH.WebPortal.API.Services.Email;

namespace ZORGATH.WebPortal.API.Features.Email;

/// <summary>
/// In-process MediatR dispatcher for Email registration handlers used in integration testing.
/// </summary>
public sealed class LocalEmailMediator(
    MerrickContext db,
    IEmailService emailService,
    IWebHostEnvironment env) : IMediator
{
    private readonly RegisterEmailAddressHandler _handler =
        new(db, emailService, env, NullLogger<RegisterEmailAddressHandler>.Instance);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is RegisterEmailAddressCommand cmd && typeof(TResponse) == typeof(RegisterEmailAddressResult))
        {
            return (Task<TResponse>)(object)_handler.Handle(cmd, cancellationToken);
        }
        throw new NotSupportedException($"LocalEmailMediator only supports {nameof(RegisterEmailAddressCommand)}");
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
}
