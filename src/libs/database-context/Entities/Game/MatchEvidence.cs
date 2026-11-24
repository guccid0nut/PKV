// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

[Table("MatchEvidence", Schema = "misc")]
[Index(nameof(MatchID), IsUnique = true)]
public class MatchEvidence
{
    [Key] public int ID { get; set; }

    public required int MatchID { get; set; }

    [ForeignKey(nameof(MatchID))] public MatchStatistics? Match { get; set; }

    public bool HasReplay { get; set; }

    public bool HasVoice { get; set; }

    [MaxLength(32768)] public string ChatLogJson { get; set; } = "[]";

    [MaxLength(1024)] public string? VoiceTrackLegionUrl { get; set; }

    [MaxLength(1024)] public string? VoiceTrackHellbourneUrl { get; set; }

    public DateTimeOffset TimestampExtracted { get; set; } = TimeProvider.System.GetUtcNow();
}
