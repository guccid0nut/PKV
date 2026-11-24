// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

public partial class HeroList
{
    /// <summary>
    ///     The list of free heroes in the rotation, from when HoN became free-to-play.
    ///     The assigned value is the latest list of heroes sent by the legacy HoN master server before the services shut down.
    ///     <br />
    ///     This property is obsolete.
    /// </summary>
    [PHPProperty("free")]
    public string FreeHeroes { get; set; } = "Hero_Genesis,Hero_Dorin_Tal,Hero_Adeve";
}
