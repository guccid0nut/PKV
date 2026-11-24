// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

/// <summary>
///     Represents a hardware MAC address associated with an account client machine.
/// </summary>
[Table("AccountMacAddresses", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(MACAddress))]
[Index(nameof(AccountID), nameof(MACAddress), IsUnique = true)]
public class AccountMacAddress
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
    [InverseProperty(nameof(Core.Account.MACAddressCollection))]
    public Account Account { get; set; } = null!;

    [MaxLength(50)]
    public required string MACAddress { get; set; }

    public static implicit operator string(AccountMacAddress? item) => item?.MACAddress ?? string.Empty;
    public static implicit operator AccountMacAddress(string mac) => new() { MACAddress = mac };

    public override string ToString() => MACAddress;

    public override bool Equals(object? obj)
    {
        if (obj is AccountMacAddress other)
            return string.Equals(MACAddress, other.MACAddress, StringComparison.OrdinalIgnoreCase);
        if (obj is string str)
            return string.Equals(MACAddress, str, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(MACAddress);
}
