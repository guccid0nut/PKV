// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Configuration.Matchmaking;

// ReSharper disable UnusedAutoPropertyAccessor.Global
public class MatchConfiguration
{
    public required int MaximumPlayerRatingDifference { get; init; }

    public required bool IsRanked { get; init; }

    public required int TeamSize { get; init; }
}
