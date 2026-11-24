// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.Enumerations;
using MemoryPack;

namespace ASPIRE.Common.Models.ServerManagement;

// ReSharper disable InconsistentNaming
// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable PartialTypeWithSinglePart
/// <summary>
///     Represents the match information available at the time that the match starts.
///     This information is used to populate the match statistics once the match ends.
/// </summary>
[MemoryPackable]
public partial class MatchStartData
{
    public required int MatchID { get; set; }

    public required string MatchName { get; set; }

    public required int ServerID { get; set; }

    public required string ServerName { get; set; }

    public required string HostAccountName { get; set; }

    public required string Map { get; set; }

    public required string Version { get; set; }

    public required bool IsCasual { get; set; }

    public required int MatchType { get; set; }

    public required string MatchMode { get; set; }

    public DateTimeOffset TimestampStarted { get; set; } = TimeProvider.System.GetUtcNow();

    public int ConnectedPlayersCount { get; set; }

    public int MaximumPlayersCount { get; set; } = 10;

    public int Tier { get; set; } = 1;

    public int League { get; set; }

    public MatchOptions Options { get; set; } = MatchOptions.None;

    public bool IsStarted { get; set; }

    public int Phase { get; set; }
}
