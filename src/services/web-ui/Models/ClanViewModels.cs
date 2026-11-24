// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.DTOs;

namespace DAWNBRINGER.WebPortal.UI.Models;

public sealed record ClanDirectoryViewModel(
    ClanDirectoryResultDto Directory,
    string SearchQuery,
    string CurrentSort,
    int? MyClanId,
    string? MyClanTag,
    string? MyClanName
);

public sealed record ClanDetailViewModel(
    ClanDetailDto? Clan,
    int RequestedClanId,
    bool IsMyClan
);
