// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Configuration.Economy;

public class MatchRewards
{
    public required Solo Solo { get; set; }

    public required TwoPersonGroup TwoPersonGroup { get; set; }

    public required ThreePersonGroup ThreePersonGroup { get; set; }

    public required FourPersonGroup FourPersonGroup { get; set; }

    public required FivePersonGroup FivePersonGroup { get; set; }
}
