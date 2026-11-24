// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Enumerations.Statistics;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace MERRICK.DatabaseContext.Repositories;

public class InviteRepository(
    MerrickContext db,
    IConnectionMultiplexer? redis = null,
    ILogger<InviteRepository>? logger = null,
    TimeProvider? timeProvider = null) : IInviteRepository
{
    private const string RedisToggleKey = "kongor:settings:invite_only";
    private readonly ILogger<InviteRepository> _logger = logger ?? NullLogger<InviteRepository>.Instance;
    private readonly StackExchange.Redis.IDatabase? _redisDb = redis?.GetDatabase();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<int> GetTotalMatchmakingGamesAsync(int userId, CancellationToken ct = default)
    {
        return await db.Accounts
            .Where(a => a.UserID == userId)
            .SelectMany(a => a.Statistics)
            .Where(s => s.Type == AccountStatisticsType.Matchmaking ||
                        s.Type == AccountStatisticsType.MatchmakingCasual ||
                        s.Type == AccountStatisticsType.MidWars)
            .SumAsync(s => s.MatchesPlayed, ct);
    }

    public async Task<(int TotalGames, int MaxAllowedActiveCodes, int ActiveUnusedCodes, int RemainingCapacity, bool CanGenerate)>
        GetUserQuotaStatusAsync(int userId, CancellationToken ct = default)
    {
        User? user = await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == userId, ct);

        if (user == null)
        {
            return (0, 0, 0, 0, false);
        }

        int totalGames = await GetTotalMatchmakingGamesAsync(userId, ct);
        int maxAllowedActiveCodes;

        if (user.IsStaff)
        {
            // Staff members permanently hold a capacity of 5 active invite codes
            maxAllowedActiveCodes = 5;
        }
        else
        {
            // Standard player tiered quota:
            // < 100 matches: 0 codes
            // 100-199 matches: 1 code
            // 200-299 matches: 2 codes
            // 300-399 matches: 3 codes
            // 400-999 matches: 4 codes (capped)
            // 1000+ matches: 5 codes (max holding capacity)
            if (totalGames < 100)
            {
                maxAllowedActiveCodes = 0;
            }
            else if (totalGames >= 1000)
            {
                maxAllowedActiveCodes = 5;
            }
            else
            {
                maxAllowedActiveCodes = Math.Min(4, totalGames / 100);
            }
        }

        int activeUnusedCodes = await db.UserInvites
            .CountAsync(i => i.CreatedByUserID == userId &&
                             !i.IsRevoked &&
                             i.TimestampRedeemed == null &&
                             i.CreatorIpAddress != UserInvite.MultiUseCreatorMarker, ct);

        int remainingCapacity = Math.Max(0, maxAllowedActiveCodes - activeUnusedCodes);
        bool canGenerate = remainingCapacity > 0 && activeUnusedCodes < 5;

        return (totalGames, maxAllowedActiveCodes, activeUnusedCodes, remainingCapacity, canGenerate);
    }

    public async Task<UserInvite?> CreateInviteCodeAsync(int userId, string? ipAddress = null, CancellationToken ct = default)
    {
        var quota = await GetUserQuotaStatusAsync(userId, ct);
        if (!quota.CanGenerate)
        {
            _logger.LogWarning("[InviteRepository] User #{UserId} requested invite code but quota exhausted (Active: {Active}/{Max}, Matches: {Matches})",
                userId, quota.ActiveUnusedCodes, quota.MaxAllowedActiveCodes, quota.TotalGames);
            return null;
        }

        string code = await GenerateUniqueInviteCodeAsync(ct);
        UserInvite invite = new()
        {
            Code = code,
            CreatedByUserID = userId,
            CreatorIpAddress = ipAddress,
            TimestampCreated = _timeProvider.GetUtcNow()
        };

        db.UserInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("[InviteRepository] Generated new invite code '{Code}' for User #{UserId}", code, userId);
        return invite;
    }

    public async Task<(bool IsValid, string? Message, UserInvite? Invite)> ValidateInviteCodeAsync(string code,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (false, "Invite code cannot be empty.", null);
        }

        string normalized = code.Trim().ToUpperInvariant();
        UserInvite? invite = await db.UserInvites
            .Include(i => i.CreatedByUser).ThenInclude(u => u!.Accounts)
            .FirstOrDefaultAsync(i => i.Code == normalized, ct);

        if (invite == null)
        {
            _logger.LogWarning("[InviteRepository] Invite code validation failed: unknown code '{Code}'", normalized);
            return (false, "Invalid invite code. Please check your code and try again.", null);
        }

        if (invite.IsRevoked)
        {
            _logger.LogWarning("[InviteRepository] Invite code '{Code}' rejected: revoked ({Reason})",
                normalized, invite.RevocationReason ?? "no reason");
            return (false, $"This invite code was revoked. {(string.IsNullOrWhiteSpace(invite.RevocationReason) ? "" : $"Reason: {invite.RevocationReason}")}".Trim(), invite);
        }

        if (invite.IsRedeemed)
        {
            _logger.LogWarning("[InviteRepository] Single-use invite code '{Code}' rejected: already redeemed at {RedeemedAt:O}",
                normalized, invite.TimestampRedeemed);
            return (false, "This invite code has already been redeemed by another player.", invite);
        }

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (invite.TimestampExpires.HasValue && invite.TimestampExpires.Value <= nowUtc)
        {
            _logger.LogWarning("[InviteRepository] Invite code '{Code}' rejected: expired at {ExpiresAt:O} (now {NowUtc:O})",
                normalized, invite.TimestampExpires.Value, nowUtc);
            return (false, "This invite code has expired.", invite);
        }

        _logger.LogInformation("[InviteRepository] Invite code '{Code}' validated (multiUse={IsMultiUse}, expires={ExpiresAt:O})",
            normalized, invite.IsMultiUse, invite.TimestampExpires);
        return (true, "Invite code is valid!", invite);
    }

    public async Task<bool> RedeemInviteCodeAsync(string code, int redeemedUserId, int redeemedAccountId,
        string? ipAddress = null, CancellationToken ct = default)
    {
        var validation = await ValidateInviteCodeAsync(code, ct);
        if (!validation.IsValid || validation.Invite == null)
        {
            return false;
        }

        UserInvite invite = validation.Invite;
        if (!invite.IsMultiUse)
        {
            invite.RedeemedByUserID = redeemedUserId;
            invite.RedeemedByAccountID = redeemedAccountId;
            invite.TimestampRedeemed = _timeProvider.GetUtcNow();
            invite.RedeemerIpAddress = ipAddress;
        }

        User? redeemer = await db.Users.FirstOrDefaultAsync(u => u.ID == redeemedUserId, ct);
        if (redeemer != null)
        {
            redeemer.InvitedByUserID = invite.CreatedByUserID;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[InviteRepository] Successfully redeemed {Mode} invite code '{Code}' (expires={ExpiresAt:O}) for User #{RedeemedUserId} (Account #{AccountId}) invited by User #{InviterId}",
            invite.IsMultiUse ? "multi-use" : "single-use",
            invite.Code,
            invite.TimestampExpires,
            redeemedUserId,
            redeemedAccountId,
            invite.CreatedByUserID);
        return true;
    }

    public async Task<IReadOnlyList<UserInvite>> GetUserInvitesAsync(int userId, CancellationToken ct = default)
    {
        return await db.UserInvites
            .AsNoTracking()
            .Include(i => i.RedeemedByAccount)
            .Where(i => i.CreatedByUserID == userId)
            .OrderByDescending(i => i.TimestampCreated)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<User>> GetInviteLineageTreeAsync(int rootUserId, int maxDepth = 4,
        CancellationToken ct = default)
    {
        List<User> lineage = [];
        User? root = await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == rootUserId, ct);

        if (root == null) return lineage;
        lineage.Add(root);

        HashSet<int> parentIds = [rootUserId];
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            List<User> children = await db.Users
                .AsNoTracking()
                .Include(u => u.Role)
                .Include(u => u.Accounts)
                .Where(u => u.InvitedByUserID.HasValue && parentIds.Contains(u.InvitedByUserID.Value))
                .ToListAsync(ct);

            if (children.Count == 0) break;

            lineage.AddRange(children);
            parentIds = [.. children.Select(c => c.ID)];
        }

        return lineage;
    }

    public async Task<int> RevokeUnusedInvitesForUserAsync(int userId, string reason, CancellationToken ct = default)
    {
        List<UserInvite> unused = await db.UserInvites
            .Where(i => i.CreatedByUserID == userId &&
                        !i.IsRevoked &&
                        i.TimestampRedeemed == null &&
                        i.CreatorIpAddress != UserInvite.MultiUseCreatorMarker)
            .ToListAsync(ct);

        if (unused.Count == 0) return 0;

        foreach (var inv in unused)
        {
            inv.IsRevoked = true;
            inv.RevocationReason = reason;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[InviteRepository] Revoked {Count} unused invite codes for User #{UserId}. Reason: {Reason}",
            unused.Count, userId, reason);
        return unused.Count;
    }

    public async Task LogModeToggleAsync(int staffAccountId, string staffAccountName, bool enabled,
        string? notes = null, string? ip = null, CancellationToken ct = default)
    {
        InviteSystemAuditLog log = new()
        {
            StaffAccountID = staffAccountId,
            StaffAccountName = staffAccountName,
            InviteOnlyEnabled = enabled,
            Notes = notes,
            IpAddress = ip,
            Timestamp = _timeProvider.GetUtcNow()
        };

        db.InviteSystemAuditLogs.Add(log);
        await db.SaveChangesAsync(ct);

        await SetInviteOnlyModeAsync(enabled, ct);

        _logger.LogInformation("[InviteRepository] Staff '{StaffName}' (#{StaffId}) toggled Invite-Only Mode to {Status}. Notes: {Notes}",
            staffAccountName, staffAccountId, enabled ? "ENABLED" : "DISABLED", notes ?? "None");
    }

    public async Task<IReadOnlyList<InviteSystemAuditLog>> GetModeAuditLogsAsync(int count = 50,
        CancellationToken ct = default)
    {
        return await db.InviteSystemAuditLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<bool> IsInviteOnlyModeEnabledAsync(CancellationToken ct = default)
    {
        if (_redisDb != null)
        {
            try
            {
                RedisValue val = await _redisDb.StringGetAsync(RedisToggleKey);
                if (val.HasValue)
                {
                    return val == "1" || val == "true";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InviteRepository] Failed reading Redis invite toggle. Falling back to DB.");
            }
        }

        InviteSystemAuditLog? latest = await db.InviteSystemAuditLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync(ct);

        return latest?.InviteOnlyEnabled ?? false;
    }

    public async Task SetInviteOnlyModeAsync(bool enabled, CancellationToken ct = default)
    {
        if (_redisDb != null)
        {
            try
            {
                await _redisDb.StringSetAsync(RedisToggleKey, enabled ? "1" : "0");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InviteRepository] Failed setting Redis invite toggle.");
            }
        }
    }

    public async Task<(int TotalGenerated, int TotalRedeemed)> GetInviteCountsAsync(CancellationToken ct = default)
    {
        int totalGenerated = await db.UserInvites.CountAsync(ct);
        int totalRedeemed = await db.UserInvites.CountAsync(i => i.TimestampRedeemed != null, ct);
        return (totalGenerated, totalRedeemed);
    }

    public async Task<int?> ResolveBootstrapAdminUserIdAsync(CancellationToken ct = default)
    {
        User? adminUser = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.EmailAddress == "admin@kongor.local", ct)
            ?? await db.Users
                .Include(u => u.Role)
                .OrderBy(u => u.ID)
                .FirstOrDefaultAsync(u => u.Role.Name == Constants.UserRoles.Administrator, ct);

        return adminUser?.ID;
    }

    public async Task<IReadOnlyList<UserInvite>> CreateBootstrapInviteCodesAsync(int adminUserId, int count, string? ipAddress = null, CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return Array.Empty<UserInvite>();
        }

        List<UserInvite> createdInvites = new(count);
        for (int i = 0; i < count; i++)
        {
            string code = await GenerateUniqueInviteCodeAsync(db, ct);
            UserInvite invite = new()
            {
                Code = code,
                CreatedByUserID = adminUserId,
                CreatorIpAddress = ipAddress,
                TimestampCreated = _timeProvider.GetUtcNow()
            };
            db.UserInvites.Add(invite);
            createdInvites.Add(invite);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[InviteRepository] Generated {Count} bootstrap invite codes for User #{UserId}", count, adminUserId);
        return createdInvites;
    }

    private Task<string> GenerateUniqueInviteCodeAsync(CancellationToken ct) =>
        GenerateUniqueInviteCodeAsync(db, ct);

    internal static async Task<string> GenerateUniqueInviteCodeAsync(MerrickContext db, CancellationToken ct = default)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Omit 0, 1, I, O to prevent transcription ambiguity
        while (true)
        {
            StringBuilder sb = new("KONGOR-");
            byte[] bytes = RandomNumberGenerator.GetBytes(8);
            for (int i = 0; i < 4; i++)
            {
                sb.Append(chars[bytes[i] % chars.Length]);
            }
            sb.Append('-');
            for (int i = 4; i < 8; i++)
            {
                sb.Append(chars[bytes[i] % chars.Length]);
            }

            string candidate = sb.ToString();
            if (db.UserInvites.Local.Any(i => i.Code == candidate))
            {
                continue;
            }

            if (!await db.UserInvites.AnyAsync(i => i.Code == candidate, ct))
            {
                return candidate;
            }
        }
    }
}
