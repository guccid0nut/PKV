// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Registration;

/// <summary>
///     Command to initiate player account registration by generating and emailing an activation token.
/// </summary>
public record InitiateRegistrationCommand(string EmailAddress) : IRequest<InitiateRegistrationResult>;

/// <summary>
///     Result of initiating a registration request.
/// </summary>
public record InitiateRegistrationResult(
    bool Success,
    int StatusCode,
    string Message)
{
    public static InitiateRegistrationResult Succeeded(string message = "Verification email sent.") =>
        new(true, 200, message);

    public static InitiateRegistrationResult BadRequest(string message) =>
        new(false, 400, message);

    public static InitiateRegistrationResult Conflict(string message) =>
        new(false, 409, message);
}
