// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Clients;

public sealed record DeleteAccountRequestDto(string ConfirmationPhrase);

public sealed record AccountDeletionResultDto(
    bool Deleted,
    int UserId,
    int AnonymizedAccountsCount,
    DateTimeOffset CompletedAtUtc,
    string Message);

public sealed record ExportedAccountItem(
    int AccountId,
    string AccountName,
    bool IsMain,
    string AccountType,
    int AscensionLevel,
    DateTimeOffset TimestampCreated,
    DateTimeOffset TimestampLastActive);

public sealed record ExportedPlayerReportItem(
    int ReportId,
    int? MatchId,
    string ReportedAccountName,
    string Reason,
    string Status,
    DateTimeOffset TimestampCreated);

public sealed record AccountDataExportDto(
    DateTimeOffset ExportedAtUtc,
    int UserId,
    string? DiscordId,
    string? DiscordUsername,
    string RoleName,
    DateTimeOffset TimestampCreated,
    DateTimeOffset TimestampLastActive,
    int GoldCoins,
    int SilverCoins,
    int PlinkoTickets,
    int TotalLevel,
    int TotalExperience,
    List<ExportedAccountItem> Accounts,
    List<string> OwnedInventoryItemCodes,
    List<ExportedPlayerReportItem> ReportsFiled);
