// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Constants;

/// <summary>
///     These are the mastery boost products that can be purchased in the in-game store.
/// </summary>
/// <remarks>
///     The values should always be in accordance with the store items list.
/// </remarks>
public static class MasteryBoost
{
    /// <summary>
    ///     The window of time after a match is recorded during which a mastery boost can be applied to it.
    ///     The applied boost cache entries expire after this window, so binding the boost eligibility to it guarantees that an
    ///     entry can never expire while its match is still eligible for boosting.
    /// </summary>
    public static readonly TimeSpan ApplicationWindow = TimeSpan.FromDays(7);

    public static class Regular
    {
        public const int ProductCode = 3609;
        public const int GoldCost = 150;
    }

    public static class Super
    {
        public const int ProductCode = 4605;
        public const int GoldCost = 1500;
    }

    public static class Bundle
    {
        public const int ProductCode = 4606;
        public const int GoldCost = 1500;
    }
}
