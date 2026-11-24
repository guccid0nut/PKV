// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Registration;

/// <summary>
///     Command for direct user onboarding with credentials, optional Discord integration, and optional invite code.
/// </summary>
public record RegisterDirectUserCommand(
    string Email,
    string Name,
    string Password,
    string? DiscordId = null,
    string? DiscordUsername = null,
    string? DiscordAvatar = null,
    string? InviteCode = null,
    string? RemoteIpAddress = null) : IRequest<RegistrationResult>;
