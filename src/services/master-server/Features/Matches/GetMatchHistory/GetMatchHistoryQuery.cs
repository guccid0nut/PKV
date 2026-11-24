// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Domain.Identifiers;

namespace KONGOR.MasterServer.Features.Matches.GetMatchHistory;

public sealed record GetMatchHistoryQuery(
    AccountId? AccountId,
    string? Nickname,
    int Limit = 100);
