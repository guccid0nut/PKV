// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Configuration.Economy;

public class EconomyConfiguration
{
    public required SignupRewards SignupRewards { get; set; }

    public required EventRewards EventRewards { get; set; }

    public required MatchRewards MatchRewards { get; set; }
}
