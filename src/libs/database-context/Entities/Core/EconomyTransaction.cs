// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("EconomyTransactions", Schema = "core")]
[Index(nameof(UserID))]
[Index(nameof(AccountID))]
[Index(nameof(TransactionType))]
[Index(nameof(Timestamp))]
[Index(nameof(UserID), nameof(Timestamp))]
public class EconomyTransaction
{
    [Key]
    [Column("Id")]
    public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int UserID { get; set; }

    [ForeignKey(nameof(UserID))] public User? User { get; set; }

    public int? AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public Account? Account { get; set; }

    [Column("GoldDelta")] public int GoldCoinsDelta { get; set; }

    [NotMapped]
    public int GoldDelta
    {
        get => GoldCoinsDelta;
        set => GoldCoinsDelta = value;
    }

    [Column("SilverDelta")] public int SilverCoinsDelta { get; set; }

    [NotMapped]
    public int SilverDelta
    {
        get => SilverCoinsDelta;
        set => SilverCoinsDelta = value;
    }

    [Column("PlinkoDelta")] public int PlinkoTicketsDelta { get; set; }

    [NotMapped]
    public int PlinkoDelta
    {
        get => PlinkoTicketsDelta;
        set => PlinkoTicketsDelta = value;
    }

    [Column("GoldBalanceAfter")] public int GoldCoinsBalanceAfter { get; set; }

    [NotMapped]
    public int GoldBalanceAfter
    {
        get => GoldCoinsBalanceAfter;
        set => GoldCoinsBalanceAfter = value;
    }

    [Column("SilverBalanceAfter")] public int SilverCoinsBalanceAfter { get; set; }

    [NotMapped]
    public int SilverBalanceAfter
    {
        get => SilverCoinsBalanceAfter;
        set => SilverCoinsBalanceAfter = value;
    }

    [Column("PlinkoBalanceAfter")] public int PlinkoTicketsBalanceAfter { get; set; }

    [NotMapped]
    public int PlinkoBalanceAfter
    {
        get => PlinkoTicketsBalanceAfter;
        set => PlinkoTicketsBalanceAfter = value;
    }

    [MaxLength(64)] public required string TransactionType { get; set; }

    [MaxLength(128)] public string? ReferenceId { get; set; }

    [MaxLength(128)] public string? IdempotencyKey { get; set; }

    [MaxLength(255)] public string? Description { get; set; }

    public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();
}
