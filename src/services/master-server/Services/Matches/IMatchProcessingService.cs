// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Background;

namespace KONGOR.MasterServer.Services.Matches;

public interface IMatchProcessingService
{
    Task ProcessMatchSubmissionAsync(MatchSubmissionPayload payload, CancellationToken cancellationToken = default);
}
