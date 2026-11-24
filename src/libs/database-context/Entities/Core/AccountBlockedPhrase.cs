// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

/// <summary>
///     Represents a custom chat phrase blocked or filtered by an account.
/// </summary>
[Table("AccountBlockedPhrases", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(AccountID), nameof(Phrase), IsUnique = true)]
public class AccountBlockedPhrase
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
    [InverseProperty(nameof(Core.Account.BlockedPhrases))]
    public Account Account { get; set; } = null!;

    [MaxLength(200)]
    public required string Phrase { get; set; }

    public static implicit operator string(AccountBlockedPhrase? item) => item?.Phrase ?? string.Empty;
    public static implicit operator AccountBlockedPhrase(string phrase) => new() { Phrase = phrase };

    public override string ToString() => Phrase;

    public override bool Equals(object? obj)
    {
        if (obj is AccountBlockedPhrase other)
            return string.Equals(Phrase, other.Phrase, StringComparison.OrdinalIgnoreCase);
        if (obj is string str)
            return string.Equals(Phrase, str, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Phrase);
}
