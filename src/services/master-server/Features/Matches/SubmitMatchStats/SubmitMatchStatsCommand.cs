// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Models.RequestResponse.Stats;
using NEXUS.Contracts.Domain.Identifiers;

namespace KONGOR.MasterServer.Features.Matches.SubmitMatchStats;

public sealed record SubmitMatchStatsCommand(
    MatchId MatchId,
    string? Session,
    StatsForSubmissionRequestForm Form,
    string RemoteIp);
