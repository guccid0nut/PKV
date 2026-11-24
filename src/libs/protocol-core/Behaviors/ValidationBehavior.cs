// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Behaviors;

/// <summary>
///     MediatR pipeline behavior that automatically runs all registered FluentValidation
///     validators for the incoming request before passing it to the handler.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators,
    ILogger<ValidationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
        {
            return await next(cancellationToken);
        }

        string requestName = typeof(TRequest).Name;
        ValidationContext<TRequest> context = new(request);

        ValidationResult[] validationResults = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        List<ValidationFailure> failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f != null)
            .ToList();

        if (failures.Count > 0)
        {
            logger.LogWarning(
                "[MediatR:Validation] Validation failed for {RequestName} with {Count} errors: {@Errors}",
                requestName,
                failures.Count,
                failures.Select(f => new { f.PropertyName, f.ErrorMessage, f.AttemptedValue }));

            throw new ValidationException(failures);
        }

        return await next(cancellationToken);
    }
}
