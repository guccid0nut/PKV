// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Email;

/// <summary>
///     Command to initiate email address verification for portal registration.
/// </summary>
public record RegisterEmailAddressCommand(
    string EmailAddress,
    string ConfirmEmailAddress) : IRequest<RegisterEmailAddressResult>;

/// <summary>
///     Result of an email address registration request.
/// </summary>
public record RegisterEmailAddressResult(
    bool Success,
    int StatusCode,
    string Message)
{
    public static RegisterEmailAddressResult Succeeded(string message) =>
        new(true, 200, message);

    public static RegisterEmailAddressResult BadRequest(string message) =>
        new(false, 400, message);

    public static RegisterEmailAddressResult Unprocessable(string message) =>
        new(false, 422, message);

    public static RegisterEmailAddressResult ServiceUnavailable(string message) =>
        new(false, 503, message);
}
