// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;

namespace MERRICK.DatabaseContext.Repositories;

public class ClanRepository(
    MerrickContext db,
    ILogger<ClanRepository>? logger = null,
    INexusHybridCache? hybridCache = null) : IClanRepository, IDisposable
{
    private readonly MerrickContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<ClanRepository> _logger = logger ?? NullLogger<ClanRepository>.Instance;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    private static TimeSpan GetL2ExpirationWithJitter(int baseMinutes = 5, int jitterSeconds = 15)
    {
        int offset = Random.Shared.Next(-jitterSeconds, jitterSeconds + 1);
        return TimeSpan.FromMinutes(baseMinutes) + TimeSpan.FromSeconds(offset);
    }

    public async Task<Clan?> GetByIdAsync(int clanId, CancellationToken ct = default)
    {
        _logger.LogDebug("[ClanRepository] Fetching Clan #{ClanId}", clanId);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Clans
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ID == clanId, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Clan?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();
        string lower = trimmed.ToLower();
        _logger.LogDebug("[ClanRepository] Fetching Clan by Name '{Name}'", trimmed);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Clans
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Name.ToLower() == lower, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Clan?> GetByTagAsync(string tag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string trimmed = tag.Trim();
        string lower = trimmed.ToLower();
        _logger.LogDebug("[ClanRepository] Fetching Clan by Tag '{Tag}'", trimmed);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Clans
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Tag.ToLower() == lower, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<Account>> GetClanMembersAsync(int clanId, CancellationToken ct = default)
    {
        _logger.LogDebug("[ClanRepository] Fetching Clan Members for Clan #{ClanId}", clanId);
        if (hybridCache != null)
        {
            return await hybridCache.GetOrCreateAsync(
                $"clan:roster:{clanId}",
                async token =>
                {
                    await _dbLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        List<Account> members = await _db.Accounts
                            .AsNoTracking()
                            .Where(a => a.ClanID == clanId)
                            .OrderBy(a => a.ClanTier == ClanTier.Leader ? 0 : a.ClanTier == ClanTier.Officer ? 1 : 2)
                            .ThenBy(a => a.Name)
                            .ToListAsync(token);
                        return (IReadOnlyList<Account>)members;
                    }
                    finally
                    {
                        _dbLock.Release();
                    }
                },
                [$"clan:{clanId}"],
                TimeSpan.FromSeconds(30),
                GetL2ExpirationWithJitter(5),
                ct) ?? [];
        }

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Accounts
                .AsNoTracking()
                .Where(a => a.ClanID == clanId)
                .OrderBy(a => a.ClanTier == ClanTier.Leader ? 0 : a.ClanTier == ClanTier.Officer ? 1 : 2)
                .ThenBy(a => a.Name)
                .ToListAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Account?> GetAccountWithClanAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogDebug("[ClanRepository] Fetching Account #{AccountId} with Clan", accountId);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Accounts
                .AsNoTracking()
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.ID == accountId, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Account?> GetAccountWithClanByNameAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        string trimmed = accountName.Trim();
        string lower = trimmed.ToLower();
        _logger.LogDebug("[ClanRepository] Fetching Account '{AccountName}' with Clan", trimmed);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _db.Accounts
                .AsNoTracking()
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<ClanRankUpdateResult> SetMemberRankAsync(int requesterAccountId, int targetAccountId, int clanId, string rankAction, CancellationToken ct = default)
    {
        _logger.LogInformation("[ClanRepository] SetMemberRank: Requester #{RequesterId}, Target #{TargetId}, Clan #{ClanId}, Action '{Action}'",
            requesterAccountId, targetAccountId, clanId, rankAction);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Account? requester = await _db.Accounts
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.ID == requesterAccountId, ct);

            if (requester == null || requester.Clan?.ID != clanId)
            {
                _logger.LogWarning("[ClanRepository] Requester #{RequesterId} not in Clan #{ClanId}", requesterAccountId, clanId);
                return ClanRankUpdateResult.Fail("Requester not in specified clan", 400);
            }

            if (requester.ClanTier < ClanTier.Officer)
            {
                _logger.LogWarning("[ClanRepository] Requester #{RequesterId} has insufficient tier {Tier}", requesterAccountId, requester.ClanTier);
                return ClanRankUpdateResult.Fail("Insufficient Permissions", 400);
            }

            Account? target = await _db.Accounts
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.ID == targetAccountId, ct);

            if (target == null || target.Clan?.ID != clanId)
            {
                _logger.LogWarning("[ClanRepository] Target #{TargetId} not in Clan #{ClanId}", targetAccountId, clanId);
                return ClanRankUpdateResult.Fail("Target not in clan", 400);
            }

            string action = rankAction.Trim();
            ClanTier previousTargetTier = target.ClanTier;
            bool isKick = false;

            if (action.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            {
                if (requester.ClanTier <= target.ClanTier && requester.ID != target.ID)
                {
                    if (requester.ClanTier == ClanTier.Officer && target.ClanTier == ClanTier.Officer)
                    {
                        _logger.LogWarning("[ClanRepository] Officer cannot kick Officer");
                        return ClanRankUpdateResult.Fail("Officer cannot kick Officer", 400);
                    }
                    _logger.LogWarning("[ClanRepository] Cannot kick higher/equal rank member");
                    return ClanRankUpdateResult.Fail("Cannot kick higher/equal rank member", 400);
                }

                target.Clan = null;
                target.ClanID = null;
                target.ClanTier = ClanTier.None;
                target.TimestampJoinedClan = null;
                isKick = true;
                _logger.LogInformation("[ClanRepository] Account '{Requester}' kicked '{Target}' from Clan #{ClanId}", requester.Name, target.Name, clanId);
            }
            else
            {
                ClanTier newTier;
                if (action.Equals("Officer", StringComparison.OrdinalIgnoreCase)) newTier = ClanTier.Officer;
                else if (action.Equals("Member", StringComparison.OrdinalIgnoreCase)) newTier = ClanTier.Member;
                else if (action.Equals("Leader", StringComparison.OrdinalIgnoreCase)) newTier = ClanTier.Leader;
                else
                {
                    _logger.LogWarning("[ClanRepository] Unknown rank action '{Action}'", action);
                    return ClanRankUpdateResult.Fail($"Unknown Rank: {action}", 400);
                }

                if (newTier == ClanTier.Leader)
                {
                    if (requester.ClanTier != ClanTier.Leader)
                    {
                        _logger.LogWarning("[ClanRepository] Only Leader can promote to Leader");
                        return ClanRankUpdateResult.Fail("Only Leader can promote to Leader", 400);
                    }

                    requester.ClanTier = ClanTier.Officer;
                    target.ClanTier = ClanTier.Leader;
                    _logger.LogInformation("[ClanRepository] {Requester} transferred Leadership of Clan #{ClanId} to {Target}", requester.Name, clanId, target.Name);
                }
                else if (newTier == ClanTier.Officer)
                {
                    if (requester.ClanTier < ClanTier.Leader && target.ClanTier == ClanTier.Officer)
                    {
                        _logger.LogWarning("[ClanRepository] Officer cannot promote Officer");
                        return ClanRankUpdateResult.Fail("Officer cannot promote Officer", 400);
                    }

                    target.ClanTier = ClanTier.Officer;
                    _logger.LogInformation("[ClanRepository] {Requester} promoted {Target} to Officer in Clan #{ClanId}", requester.Name, target.Name, clanId);
                }
                else if (newTier == ClanTier.Member)
                {
                    if (requester.ClanTier <= target.ClanTier && requester.ID != target.ID)
                    {
                        _logger.LogWarning("[ClanRepository] Cannot demote higher/equal rank. Requester: {R}, Target: {T}", requester.ClanTier, target.ClanTier);
                        return ClanRankUpdateResult.Fail("Cannot demote higher/equal rank", 400);
                    }

                    target.ClanTier = ClanTier.Member;
                    _logger.LogInformation("[ClanRepository] {Requester} demoted {Target} to Member in Clan #{ClanId}", requester.Name, target.Name, clanId);
                }
            }

            await _db.SaveChangesAsync(ct);
            if (hybridCache != null)
            {
                await hybridCache.RemoveByTagAsync($"clan:{clanId}", ct);
            }

            return new ClanRankUpdateResult
            {
                Success = true,
                StatusCode = 200,
                TargetAccountId = target.ID,
                TargetAccountName = target.Name,
                NewTargetTier = target.ClanTier,
                PreviousTargetTier = previousTargetTier,
                RequesterAccountId = requester.ID,
                RequesterAccountName = requester.Name,
                NewRequesterTier = requester.ClanTier,
                ClanId = clanId,
                ClanName = requester.Clan?.Name ?? string.Empty,
                ClanTag = requester.Clan?.Tag ?? string.Empty,
                IsKick = isKick
            };
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<Clan> CreateClanAsync(string name, string tag, int creatorAccountId, IEnumerable<int>? foundingMemberAccountIds = null, CancellationToken ct = default)
    {
        string trimmedName = (name ?? string.Empty).Trim();
        string trimmedTag = (tag ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            throw new ArgumentException("Clan name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(trimmedTag))
            throw new ArgumentException("Clan tag is required.", nameof(tag));

        _logger.LogInformation("[ClanRepository] Creating Clan '{Name}' [{Tag}] by Account #{CreatorId}", trimmedName, trimmedTag, creatorAccountId);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string lowerTag = trimmedTag.ToLowerInvariant();
            bool tagTaken = await _db.Clans.AsNoTracking().AnyAsync(c => c.Tag.ToLower() == lowerTag, ct);
            if (tagTaken)
            {
                _logger.LogWarning("[ClanRepository] Rejected clan creation '{Name}' [{Tag}]: tag already taken", trimmedName, trimmedTag);
                throw new InvalidOperationException($"Clan tag '[{trimmedTag}]' is already taken by another clan.");
            }

            string lowerName = trimmedName.ToLowerInvariant();
            bool nameTaken = await _db.Clans.AsNoTracking().AnyAsync(c => c.Name.ToLower() == lowerName, ct);
            if (nameTaken)
            {
                _logger.LogWarning("[ClanRepository] Rejected clan creation '{Name}' [{Tag}]: name already taken", trimmedName, trimmedTag);
                throw new InvalidOperationException($"Clan name '{trimmedName}' is already taken by another clan.");
            }

            Clan clan = new()
            {
                Name = trimmedName,
                Tag = trimmedTag,
                TimestampCreated = TimeProvider.System.GetUtcNow()
            };

            await _db.Clans.AddAsync(clan, ct);

            Account? creator = await _db.Accounts.FirstOrDefaultAsync(a => a.ID == creatorAccountId, ct);
            if (creator != null)
            {
                creator.Clan = clan;
                creator.ClanTier = ClanTier.Leader;
                creator.TimestampJoinedClan = TimeProvider.System.GetUtcNow();
            }

            if (foundingMemberAccountIds != null)
            {
                List<int> memberIds = foundingMemberAccountIds.Where(id => id != creatorAccountId).ToList();
                if (memberIds.Count > 0)
                {
                    List<Account> members = await _db.Accounts.Where(a => memberIds.Contains(a.ID)).ToListAsync(ct);
                    foreach (Account member in members)
                    {
                        member.Clan = clan;
                        member.ClanTier = ClanTier.Member;
                        member.TimestampJoinedClan = TimeProvider.System.GetUtcNow();
                    }
                }
            }

            await _db.SaveChangesAsync(ct);
            return clan;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> AddMemberAsync(int accountId, int clanId, ClanTier tier = ClanTier.Member, CancellationToken ct = default)
    {
        _logger.LogInformation("[ClanRepository] Adding Account #{AccountId} to Clan #{ClanId} with Tier {Tier}", accountId, clanId, tier);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Account? account = await _db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, ct);
            if (account == null) return false;

            bool clanExists = await _db.Clans.AnyAsync(c => c.ID == clanId, ct);
            if (!clanExists) return false;

            account.ClanID = clanId;
            account.ClanTier = tier;
            account.TimestampJoinedClan = TimeProvider.System.GetUtcNow();

            await _db.SaveChangesAsync(ct);
            if (hybridCache != null)
            {
                await hybridCache.RemoveByTagAsync($"clan:{clanId}", ct);
            }
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> RemoveMemberAsync(int accountId, int clanId, CancellationToken ct = default)
    {
        _logger.LogInformation("[ClanRepository] Removing Account #{AccountId} from Clan #{ClanId}", accountId, clanId);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Account? account = await _db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId && a.ClanID == clanId, ct);
            if (account == null) return false;

            account.Clan = null;
            account.ClanID = null;
            account.ClanTier = ClanTier.None;
            account.TimestampJoinedClan = null;

            await _db.SaveChangesAsync(ct);
            if (hybridCache != null)
            {
                await hybridCache.RemoveByTagAsync($"clan:{clanId}", ct);
            }
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> DisbandClanAsync(int clanId, CancellationToken ct = default)
    {
        _logger.LogInformation("[ClanRepository] Disbanding Clan #{ClanId}", clanId);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Clan? clan = await _db.Clans.Include(c => c.Members).FirstOrDefaultAsync(c => c.ID == clanId, ct);
            if (clan == null) return false;

            foreach (Account member in clan.Members)
            {
                member.Clan = null;
                member.ClanID = null;
                member.ClanTier = ClanTier.None;
                member.TimestampJoinedClan = null;
            }

            _db.Clans.Remove(clan);
            await _db.SaveChangesAsync(ct);
            if (hybridCache != null)
            {
                await hybridCache.RemoveByTagAsync($"clan:{clanId}", ct);
            }
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> ChangeClanTagAsync(int clanId, string newTag, CancellationToken ct = default)
    {
        string trimmedTag = (newTag ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedTag))
            throw new ArgumentException("Clan tag is required.", nameof(newTag));

        _logger.LogInformation("[ClanRepository] Changing Clan #{ClanId} Tag to '{NewTag}'", clanId, trimmedTag);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Clan? clan = await _db.Clans.FirstOrDefaultAsync(c => c.ID == clanId, ct);
            if (clan == null) return false;

            string lowerTag = trimmedTag.ToLowerInvariant();
            bool tagTakenByOther = await _db.Clans
                .AsNoTracking()
                .AnyAsync(c => c.ID != clanId && c.Tag.ToLower() == lowerTag, ct);
            if (tagTakenByOther)
            {
                _logger.LogWarning("[ClanRepository] Rejected tag change for Clan #{ClanId} to '[{NewTag}]': tag already taken by another clan", clanId, trimmedTag);
                throw new InvalidOperationException($"Clan tag '[{trimmedTag}]' is already taken by another clan.");
            }

            clan.Tag = trimmedTag;
            await _db.SaveChangesAsync(ct);
            if (hybridCache != null)
            {
                await hybridCache.RemoveByTagAsync($"clan:{clanId}", ct);
            }
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dbLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
