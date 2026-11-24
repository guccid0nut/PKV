// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Real-time Discord Rich Presence state update DTO.
/// </summary>
public sealed class DiscordPresenceUpdateDto
{
    public long AccountId { get; set; }
    public string State { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string LargeImageKey { get; set; } = string.Empty;
    public string LargeImageText { get; set; } = string.Empty;
    public string SmallImageKey { get; set; } = string.Empty;
    public string SmallImageText { get; set; } = string.Empty;
    public string PartyId { get; set; } = string.Empty;
    public int? PartySize { get; set; }
    public int? PartyMax { get; set; }
    public long? StartTimestamp { get; set; }
}
