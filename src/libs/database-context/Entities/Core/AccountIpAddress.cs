// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

/// <summary>
///     Represents a network IP address associated with an account login or session.
/// </summary>
[Table("AccountIpAddresses", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(IPAddress))]
[Index(nameof(AccountID), nameof(IPAddress), IsUnique = true)]
public class AccountIpAddress
{
    [Key]
    public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))]
    [InverseProperty(nameof(Core.Account.IPAddressCollection))]
    public Account Account { get; set; } = null!;

    [MaxLength(50)]
    public required string IPAddress { get; set; }

    public static implicit operator string(AccountIpAddress? item) => item?.IPAddress ?? string.Empty;
    public static implicit operator AccountIpAddress(string ip) => new() { IPAddress = ip };

    public override string ToString() => IPAddress;

    public override bool Equals(object? obj)
    {
        if (obj is AccountIpAddress other)
            return string.Equals(IPAddress, other.IPAddress, StringComparison.OrdinalIgnoreCase);
        if (obj is string str)
            return string.Equals(IPAddress, str, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(IPAddress);
}
