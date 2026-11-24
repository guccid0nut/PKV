// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("UserInvites", Schema = "core")]
[Index(nameof(Code), IsUnique = true)]
[Index(nameof(CreatedByUserID))]
[Index(nameof(RedeemedByUserID))]
public class UserInvite
{
    [Key] public int ID { get; set; }

    [MaxLength(32)] public required string Code { get; set; }

    public int CreatedByUserID { get; set; }

    [ForeignKey(nameof(CreatedByUserID))]
    public User? CreatedByUser { get; set; }

    public int? RedeemedByUserID { get; set; }

    [ForeignKey(nameof(RedeemedByUserID))]
    public User? RedeemedByUser { get; set; }

    public int? RedeemedByAccountID { get; set; }

    [ForeignKey(nameof(RedeemedByAccountID))]
    public Account? RedeemedByAccount { get; set; }

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampExpires { get; set; }

    public DateTimeOffset? TimestampRedeemed { get; set; }

    public bool IsRevoked { get; set; }

    [MaxLength(256)] public string? RevocationReason { get; set; }

    [MaxLength(45)] public string? CreatorIpAddress { get; set; }

    [MaxLength(45)] public string? RedeemerIpAddress { get; set; }

    public const string MultiUseCreatorMarker = "MULTI_USE";
    public const string SharedWeeklyInviteCode = "KONGOR-1111";
    public static readonly DateTimeOffset SharedWeeklyInviteCreatedUtc = new(2026, 11, 12, 2, 3, 3, TimeSpan.Zero);
    public static readonly DateTimeOffset SharedWeeklyInviteExpiresUtc = new(2026, 11, 19, 2, 3, 3, TimeSpan.Zero);

    [NotMapped]
    public bool IsMultiUse => string.Equals(CreatorIpAddress, MultiUseCreatorMarker, StringComparison.OrdinalIgnoreCase);

    [NotMapped] public bool IsRedeemed => !IsMultiUse && TimestampRedeemed.HasValue;

    [NotMapped]
    public bool IsValid => IsValidAt(TimeProvider.System.GetUtcNow());

    public bool IsValidAt(DateTimeOffset utcNow) =>
        !IsRevoked && !IsRedeemed && (!TimestampExpires.HasValue || TimestampExpires.Value > utcNow);
}
