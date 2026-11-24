// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

public interface ILegacyProtocolFormatter
{
    object[] CreateLegacyPositionalPlayerStats(ShowSimpleStatsResponse props);

    Dictionary<object, object> CreateHybridSimpleStats(ShowSimpleStatsResponse props, string? table = null);

    Dictionary<object, object> ToHashtable(SimpleSeasonStats stats, bool isCasual = false);

    object ConvertToPhp(object? value);
 
    byte[] Serialize(object? obj);

    byte[] Serialize(ShowSimpleStatsResponse props);
}
