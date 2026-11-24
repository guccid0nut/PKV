// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Index(nameof(TwitchUsername))]
[Index(nameof(Status))]
public class StreamerApplication
{
    [Key] public int ID { get; set; }

    public int UserID { get; set; }

    public User User { get; set; } = null!;

    public int MainAccountID { get; set; }

    public Account MainAccount { get; set; } = null!;

    [MaxLength(64)] public string TwitchUsername { get; set; } = string.Empty;

    [MaxLength(32)] public string? TwitchId { get; set; }

    [MaxLength(256)] public string? TwitchChannelUrl { get; set; }

    [NotMapped]
    public string? ChannelUrl
    {
        get => TwitchChannelUrl;
        set => TwitchChannelUrl = value;
    }

    [NotMapped] public string? Platform { get; set; }

    public StreamerApplicationStatus Status { get; set; } = StreamerApplicationStatus.Pending;

    [MaxLength(500)] public string? ApplicationNotes { get; set; }

    [MaxLength(500)] public string? StaffNotes { get; set; }

    [MaxLength(64)] public string? ReviewedByStaffName { get; set; }

    public DateTimeOffset TimestampApplied { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampReviewed { get; set; }
}
