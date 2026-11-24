// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.DTOs;

// ReSharper disable InconsistentNaming
// ReSharper disable PartialTypeWithSinglePart
public partial record RegisterDirectUserDTO(
    string Email,
    string Name,
    string Password,
    string? DiscordId = null,
    string? DiscordUsername = null,
    string? DiscordAvatar = null,
    string? InviteCode = null);
