// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Features.Matches.StartMatch;

public sealed record StartMatchCommand(
    string? Session,
    string? Map,
    string? Version,
    string? MatchName,
    string? HostAccountName,
    bool IsCasual,
    int MatchType,
    string? MatchMode);
