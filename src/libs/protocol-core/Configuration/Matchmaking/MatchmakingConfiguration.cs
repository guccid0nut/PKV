// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Configuration.Matchmaking;

public class MatchmakingConfiguration
{
    public required MatchmakingMapConfiguration Ranked { get; init; }

    public required MatchmakingMapConfiguration Unranked { get; init; }

    public required MatchmakingMapConfiguration MidWars { get; init; }

    public required MatchmakingMapConfiguration RiftWars { get; init; }
}
