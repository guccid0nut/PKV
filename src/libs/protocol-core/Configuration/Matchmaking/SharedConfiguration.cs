// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Configuration.Matchmaking;

public static class SharedConfiguration
{
    public static MatchmakingConfiguration MatchmakingConfiguration { get; set; } = new()
    {
        Ranked =
            new MatchmakingMapConfiguration
            {
                Map = "caldavar",
                Regions = ["EU", "USE", "USW", "AU", "BR", "RU", "TH", "SEA", "NEWERTH"],
                Modes = ["rb"],
                Match =
                    new MatchConfiguration { MaximumPlayerRatingDifference = 250, IsRanked = true, TeamSize = 5 },
                DisabledHeroes = ["Hero_Gwynplaine"]
            },
        Unranked =
            new MatchmakingMapConfiguration
            {
                Map = "caldavar",
                Regions = ["EU", "USE", "USW", "AU", "BR", "RU", "TH", "SEA", "NEWERTH"],
                Modes = ["rb"],
                Match =
                    new MatchConfiguration { MaximumPlayerRatingDifference = 2500, IsRanked = false, TeamSize = 5 },
                DisabledHeroes = ["Hero_Gwynplaine"]
            },
        MidWars = new MatchmakingMapConfiguration
        {
            Map = "midwars",
            Regions = ["EU", "USE", "USW", "AU", "BR", "RU", "TH", "SEA", "NEWERTH"],
            Modes = ["ap", "sd", "ar"],
            Match =
                new MatchConfiguration { MaximumPlayerRatingDifference = 250, IsRanked = true, TeamSize = 5 },
            DisabledHeroes = ["Hero_Gwynplaine"]
        },
        RiftWars = new MatchmakingMapConfiguration
        {
            Map = "riftwars",
            Regions = ["EU", "USE", "USW", "AU", "BR", "RU", "TH", "SEA", "NEWERTH"],
            Modes = ["ap", "km"],
            Match = new MatchConfiguration { MaximumPlayerRatingDifference = 2500, IsRanked = false, TeamSize = 5 }
        }
    };
}
