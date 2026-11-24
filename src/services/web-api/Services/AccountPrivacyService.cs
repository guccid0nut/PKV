// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;

namespace ZORGATH.WebPortal.API.Services;

/// <summary>
/// Application service implementing self-service GDPR/privacy export and account deletion/anonymization
/// (keeping <c>AccountPrivacyController</c> decoupled from <c>MerrickContext</c> per Clean Architecture governance).
/// </summary>
public interface IAccountPrivacyService
{
    Task<AccountDataExportDto?> ExportUserDataAsync(int userId, CancellationToken ct = default);

    Task<AccountDeletionResultDto?> DeleteAndAnonymizeUserAsync(int userId, CancellationToken ct = default);
}

public sealed class AccountPrivacyService(
    MerrickContext dbContext,
    ISessionRepository sessionRepository,
    ILogger<AccountPrivacyService>? logger = null,
    TimeProvider? timeProvider = null) : IAccountPrivacyService
{
    private readonly ILogger<AccountPrivacyService> _logger = logger ?? NullLogger<AccountPrivacyService>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AccountDataExportDto?> ExportUserDataAsync(int userId, CancellationToken ct = default)
    {
        User? user = await dbContext.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts)
            .Include(u => u.InventoryItems)
            .FirstOrDefaultAsync(u => u.ID == userId, ct);

        if (user is null)
        {
            return null;
        }

        List<int> accountIds = user.Accounts.Select(a => a.ID).ToList();

        List<ExportedPlayerReportItem> reportsFiled = accountIds.Count == 0
            ? []
            : await dbContext.PlayerReports
                .AsNoTracking()
                .Where(r => r.ReporterAccountID != null && accountIds.Contains(r.ReporterAccountID.Value))
                .OrderByDescending(r => r.TimestampCreated)
                .Take(200)
                .Select(r => new ExportedPlayerReportItem(
                    r.ID,
                    r.MatchID,
                    r.ReportedAccountName,
                    r.Reason,
                    r.Status,
                    r.TimestampCreated))
                .ToListAsync(ct);

        AccountDataExportDto export = new(
            ExportedAtUtc: _timeProvider.GetUtcNow(),
            UserId: user.ID,
            DiscordId: user.DiscordId,
            DiscordUsername: user.DiscordUsername,
            RoleName: user.Role?.Name ?? "User",
            TimestampCreated: user.TimestampCreated,
            TimestampLastActive: user.TimestampLastActive,
            GoldCoins: user.GoldCoins,
            SilverCoins: user.SilverCoins,
            PlinkoTickets: user.PlinkoTickets,
            TotalLevel: user.TotalLevel,
            TotalExperience: user.TotalExperience,
            Accounts: user.Accounts
                .OrderByDescending(a => a.IsMain)
                .ThenBy(a => a.ID)
                .Select(a => new ExportedAccountItem(
                    a.ID,
                    a.Name,
                    a.IsMain,
                    a.Type.ToString(),
                    a.AscensionLevel,
                    a.TimestampCreated,
                    a.TimestampLastActive))
                .ToList(),
            OwnedInventoryItemCodes: user.InventoryItems
                .Select(i => i.ProductCode)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ReportsFiled: reportsFiled);

        _logger.LogInformation(
            "[PRIVACY-AUDIT] Exported personal data archive for UserId={UserId} ({AccountCount} account(s), {ItemCount} inventory item(s), {ReportCount} filed report(s)).",
            user.ID,
            export.Accounts.Count,
            export.OwnedInventoryItemCodes.Count,
            export.ReportsFiled.Count);

        return export;
    }

    public async Task<AccountDeletionResultDto?> DeleteAndAnonymizeUserAsync(int userId, CancellationToken ct = default)
    {
        User? user = await dbContext.Users
            .Include(u => u.Accounts)
            .Include(u => u.Sessions)
            .FirstOrDefaultAsync(u => u.ID == userId, ct);

        if (user is null)
        {
            return null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<string> previousAccountNames = user.Accounts.Select(a => a.Name).ToList();

        // 1. Revoke all active Valkey/DB sessions for every owned account before anonymization
        foreach (string acctName in previousAccountNames)
        {
            await sessionRepository.RevokeAllSessionsForAccountAsync(acctName, ct);
        }

        if (user.Sessions.Count > 0)
        {
            dbContext.AccountSessions.RemoveRange(user.Sessions);
        }

        // 2. Anonymize user identity & unlink OAuth identifiers (Discord / Twitch / Email)
        string randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        user.EmailAddress = $"deleted_{user.ID}_{randomSuffix}@anonymized.local";
        user.DiscordId = null;
        user.DiscordUsername = null;
        user.DiscordAvatar = null;
        user.TwitchId = null;
        user.TwitchUsername = null;
        user.TimestampLastActive = now;

        // 3. Scrub credential verifiers with unrecoverable cryptographic noise
        user.SRPPasswordSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        user.SRPPasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        user.PBKDF2PasswordHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        user.Argon2idPasswordHash = null;

        // 4. Anonymize & disable all linked accounts (Account.Name MaxLength = 15)
        foreach (Account account in user.Accounts)
        {
            string anonymizedName = $"del_{account.ID}";
            if (anonymizedName.Length > 15)
            {
                anonymizedName = anonymizedName[..15];
            }

            account.Name = anonymizedName;
            account.Type = AccountType.Disabled;
            account.TimestampLastActive = now;
        }

        await dbContext.SaveChangesAsync(ct);

        _logger.LogWarning(
            "[PRIVACY-AUDIT] Completed self-service account deletion & PII anonymization for UserId={UserId} ({AccountCount} account(s) disabled and sessions revoked).",
            user.ID,
            previousAccountNames.Count);

        return new AccountDeletionResultDto(
            Deleted: true,
            UserId: user.ID,
            AnonymizedAccountsCount: previousAccountNames.Count,
            CompletedAtUtc: now,
            Message: "Your account PII has been anonymized, Discord link removed, sessions revoked, and game accounts disabled.");
    }
}
