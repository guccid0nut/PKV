// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;
using NEXUS.Contracts.Domain.Identifiers;

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     High-performance binary stream envelope for Redis Streams match ingestion.
/// </summary>
[MemoryPackable]
public partial class MatchJobStreamEnvelope
{
    public MatchId MatchId { get; set; }
    public AccountId AccountId { get; set; }
    public int Partition { get; set; }
    public byte[] Payload { get; set; } = [];
    public byte SerializationFormat { get; set; } // 0 = Utf8Json, 1 = MemoryPack
    public DateTimeOffset EnqueuedAt { get; set; } = TimeProvider.System.GetUtcNow();
}
