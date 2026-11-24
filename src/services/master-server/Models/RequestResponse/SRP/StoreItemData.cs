// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

public partial class StoreItemData
{
    /// <summary>
    ///     Unknown.
    ///     Seems to be an empty string in all the network packet dumps.
    /// </summary>
    [PHPProperty("data")]
    public string Data { get; set; } = string.Empty;

    /// <summary>
    ///     The availability start time of a limited-time store item (e.g. early-access hero).
    ///     Expected format is "yyyy-MM-dd HH:mm:ss" (e.g. "2015-12-23 00:00:00").
    ///     Empty for permanently owned items; only set for trial/time-limited items.
    /// </summary>
    [PHPProperty("start_time")]
    public string AvailableFrom { get; set; } = string.Empty;

    /// <summary>
    ///     The availability end time of a limited-time store item (e.g. early-access hero).
    ///     Expected format is "yyyy-MM-dd HH:mm:ss" (e.g. "2015-12-23 00:00:00").
    ///     Empty for permanently owned items; only set for trial/time-limited items.
    ///     When non-empty, the C++ client treats the item as a trial and displays a "you have access for [time]" card.
    /// </summary>
    [PHPProperty("end_time")]
    public string AvailableUntil { get; set; } = string.Empty;

    /// <summary>
    ///     Unknown.
    ///     If "score" is not set, then this property is not set either.
    ///     But if "score" is set, then this property is set to "0".
    /// </summary>
    [PHPProperty("used")]
    public int Used { get; set; } = 0;
}
