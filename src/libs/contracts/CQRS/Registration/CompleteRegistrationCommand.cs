// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Registration;

/// <summary>
///     Command to complete player registration with an activation token and password.
/// </summary>
public record CompleteRegistrationCommand(
    string Token,
    string Name,
    string Password) : IRequest<RegistrationResult>;

/// <summary>
///     Result of an account registration completion attempt.
/// </summary>
public record RegistrationResult(
    bool Success,
    int StatusCode,
    string? ErrorMessage,
    int? UserId = null,
    object? User = null)
{
    public static RegistrationResult Created(int userId, object userDto) =>
        new(true, 201, null, userId, userDto);

    public static RegistrationResult BadRequest(string message) =>
        new(false, 400, message);

    public static RegistrationResult NotFound(string message) =>
        new(false, 404, message);

    public static RegistrationResult Conflict(string message) =>
        new(false, 409, message);
}
