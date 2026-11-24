// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Registration;
using ZORGATH.WebPortal.API.Services.Email;
using MERRICK.DatabaseContext.Services;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Features.Registration;

/// <summary>
/// In-process MediatR dispatcher for Registration handlers used in integration testing.
/// </summary>
public sealed class LocalRegistrationMediator(
    MerrickContext db,
    IEmailService emailService,
    PasswordValidator passwordValidator,
    IInviteRepository? inviteRepository = null,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null,
    IConfiguration? configuration = null) : IMediator
{
    private readonly InitiateRegistrationHandler _initiateHandler =
        new(db, emailService, NullLogger<InitiateRegistrationHandler>.Instance, timeProvider);

    private readonly CompleteRegistrationHandler _completeHandler =
        new(db, emailService, passwordValidator, NullLogger<CompleteRegistrationHandler>.Instance, timeProvider, argon2Hasher);

    private readonly RegisterDirectUserHandler _directHandler =
        new(db, passwordValidator, NullLogger<RegisterDirectUserHandler>.Instance, inviteRepository, timeProvider, argon2Hasher, configuration);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is InitiateRegistrationCommand initCmd && typeof(TResponse) == typeof(InitiateRegistrationResult))
        {
            return (Task<TResponse>)(object)_initiateHandler.Handle(initCmd, cancellationToken);
        }
        if (request is CompleteRegistrationCommand compCmd && typeof(TResponse) == typeof(RegistrationResult))
        {
            return (Task<TResponse>)(object)_completeHandler.Handle(compCmd, cancellationToken);
        }
        if (request is RegisterDirectUserCommand dirCmd && typeof(TResponse) == typeof(RegistrationResult))
        {
            return (Task<TResponse>)(object)_directHandler.Handle(dirCmd, cancellationToken);
        }

        throw new NotSupportedException($"LocalRegistrationMediator does not support request type {request.GetType().Name}");
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
}
