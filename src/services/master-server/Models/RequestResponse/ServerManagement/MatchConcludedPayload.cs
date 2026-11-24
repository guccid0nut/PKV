// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

/// <summary>
///     Payload published to the "match:concluded" Redis Pub/Sub channel upon match finalization.
/// </summary>
public sealed record MatchConcludedPayload(int MatchID, DateTimeOffset ConcludedAt, int PlayerCount);
