// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;

namespace MERRICK.DatabaseContext.Repositories;

public class FriendshipRepository(MerrickContext db, ILogger<FriendshipRepository>? logger = null) : IFriendshipRepository
{
    private readonly MerrickContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<FriendshipRepository> _logger = logger ?? NullLogger<FriendshipRepository>.Instance;

    public async Task<IReadOnlyList<Account>> SearchAccountsByPrefixAsync(string prefix, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return [];

        string trimmed = prefix.Trim();
        _logger.LogDebug("[FriendshipRepository] Searching Accounts by Prefix '{Prefix}', Limit={Limit}", trimmed, limit);

        bool isPostgres = _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        if (isPostgres)
        {
            return await _db.Accounts
                .AsNoTracking()
                .Where(a => EF.Functions.ILike(EF.Functions.Collate(a.Name, "default"), $"{trimmed}%"))
                .OrderBy(a => a.Name)
                .Take(limit)
                .ToListAsync(ct);
        }

        string lower = trimmed.ToLower();
        return await _db.Accounts
            .AsNoTracking()
            .Where(a => a.Name.ToLower().StartsWith(lower))
            .OrderBy(a => a.Name)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Authoritative query joining core.Friendships with core.Accounts and core.Clans to produce FriendedPeer projection.
    /// Completely eliminates reads to the legacy Account.FriendedPeers JSON document.
    /// </summary>
    public async Task<IReadOnlyList<FriendedPeer>> GetFriendedPeersAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogDebug("[FriendshipRepository] Fetching FriendedPeers for Account #{AccountId}", accountId);

        return await _db.Friendships
            .AsNoTracking()
            .Where(f => f.AccountID == accountId && f.Status == FriendshipStatus.Accepted)
            .OrderBy(f => f.FriendAccount != null ? f.FriendAccount.Name : string.Empty)
            .Select(f => new FriendedPeer
            {
                ID = f.FriendAccountID,
                Name = f.FriendAccount != null ? f.FriendAccount.Name : string.Empty,
                ClanTag = f.FriendAccount != null && f.FriendAccount.Clan != null ? f.FriendAccount.Clan.Tag : null,
                FriendGroup = f.FriendGroup
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Authoritative query returning core.Friendships entities eagerly including FriendAccount and FriendAccount.Clan.
    /// Supports optional status filtering (defaulting to all relationships).
    /// </summary>
    public async Task<IReadOnlyList<Friendship>> GetFriendshipsAsync(int accountId, FriendshipStatus? status = null, CancellationToken ct = default)
    {
        _logger.LogDebug("[FriendshipRepository] Fetching Friendships for Account #{AccountId} (Status: {Status})", accountId, status);

        IQueryable<Friendship> query = _db.Friendships
            .AsNoTracking()
            .Include(f => f.FriendAccount)
                .ThenInclude(a => a!.Clan)
            .Where(f => f.AccountID == accountId);

        if (status.HasValue)
        {
            query = query.Where(f => f.Status == status.Value);
        }

        return await query
            .OrderBy(f => f.FriendAccount != null ? f.FriendAccount.Name : string.Empty)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Authoritatively checks whether two accounts are mutual friends in core.Friendships.
    /// Eliminates all dual-reads against Account.FriendedPeers.
    /// </summary>
    public async Task<bool> AreFriendsAsync(int accountId1, int accountId2, CancellationToken ct = default)
    {
        _logger.LogDebug("[FriendshipRepository] Checking friendship between #{Id1} and #{Id2}", accountId1, accountId2);

        if (accountId1 <= 0 || accountId2 <= 0 || accountId1 == accountId2)
            return false;

        return await _db.Friendships
            .AsNoTracking()
            .AnyAsync(f => ((f.AccountID == accountId1 && f.FriendAccountID == accountId2) ||
                            (f.AccountID == accountId2 && f.FriendAccountID == accountId1)) &&
                           f.Status == FriendshipStatus.Accepted, ct);
    }

    /// <summary>
    /// Atomically removes bidirectional friendship records between two accounts in core.Friendships.
    /// Validates existence of both accounts and returns structured FriendshipRemovalResult.
    /// Eliminates all reads and mutations of Account.FriendedPeers.
    /// </summary>
    public async Task<FriendshipRemovalResult> RemoveFriendshipBidirectionalAsync(int accountId, int buddyId, CancellationToken ct = default)
    {
        _logger.LogInformation("[FriendshipRepository] Bidirectional friend removal: Account #{AccountId}, Buddy #{BuddyId}", accountId, buddyId);

        Account? account = await _db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account == null)
        {
            _logger.LogWarning("[FriendshipRepository] Account #{AccountId} not found for friend removal", accountId);
            return FriendshipRemovalResult.NotFound("Account Not Found", 404);
        }

        Account? buddy = await _db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ID == buddyId, ct);

        if (buddy == null)
        {
            _logger.LogWarning("[FriendshipRepository] Buddy #{BuddyId} not found for friend removal", buddyId);
            return FriendshipRemovalResult.NotFound("Buddy Not Found", 404);
        }

        List<Friendship> relationalFriendships = await _db.Friendships
            .Where(f => (f.AccountID == accountId && f.FriendAccountID == buddyId) ||
                        (f.AccountID == buddyId && f.FriendAccountID == accountId))
            .ToListAsync(ct);

        if (relationalFriendships.Count > 0)
        {
            _db.Friendships.RemoveRange(relationalFriendships);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("[FriendshipRepository] Successfully removed friendship between '{AccountName}' and '{BuddyName}'", account.Name, buddy.Name);
            return FriendshipRemovalResult.Succeeded(account.ID, account.Name, buddy.ID, buddy.Name);
        }

        _logger.LogWarning("[FriendshipRepository] Friendship not found between '{AccountName}' and '{BuddyName}' (no-op)", account.Name, buddy.Name);
        return FriendshipRemovalResult.Unchanged(account.ID, account.Name, buddy.ID, buddy.Name);
    }

    /// <summary>
    /// Adds bidirectional accepted friendship records in core.Friendships.
    /// Idempotently handles existing records (e.g. pending requests) by promoting them to Accepted.
    /// Eliminates all mutations to Account.FriendedPeers.
    /// </summary>
    public async Task<bool> AddFriendshipAsync(int accountId, int friendAccountId, string friendGroup = "General", CancellationToken ct = default)
    {
        _logger.LogInformation("[FriendshipRepository] Adding friendship between #{AccountId} and #{FriendId} (Group: {Group})", accountId, friendAccountId, friendGroup);

        if (accountId <= 0 || friendAccountId <= 0 || accountId == friendAccountId)
            return false;

        bool accountExists = await _db.Accounts.AnyAsync(a => a.ID == accountId, ct);
        bool friendExists = await _db.Accounts.AnyAsync(a => a.ID == friendAccountId, ct);

        if (!accountExists || !friendExists) return false;

        string sanitizedGroup = string.IsNullOrWhiteSpace(friendGroup) ? "General" : friendGroup.Trim();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        List<Friendship> existing = await _db.Friendships
            .Where(f => (f.AccountID == accountId && f.FriendAccountID == friendAccountId) ||
                        (f.AccountID == friendAccountId && f.FriendAccountID == accountId))
            .ToListAsync(ct);

        // Direction 1: account -> friend
        Friendship? f1 = existing.FirstOrDefault(f => f.AccountID == accountId && f.FriendAccountID == friendAccountId);
        if (f1 == null)
        {
            _db.Friendships.Add(new Friendship
            {
                AccountID = accountId,
                FriendAccountID = friendAccountId,
                FriendGroup = sanitizedGroup,
                Status = FriendshipStatus.Accepted,
                TimestampCreated = now
            });
        }
        else
        {
            f1.Status = FriendshipStatus.Accepted;
            f1.FriendGroup = sanitizedGroup;
            f1.TimestampUpdated = now;
        }

        // Direction 2: friend -> account
        Friendship? f2 = existing.FirstOrDefault(f => f.AccountID == friendAccountId && f.FriendAccountID == accountId);
        if (f2 == null)
        {
            _db.Friendships.Add(new Friendship
            {
                AccountID = friendAccountId,
                FriendAccountID = accountId,
                FriendGroup = sanitizedGroup,
                Status = FriendshipStatus.Accepted,
                TimestampCreated = now
            });
        }
        else
        {
            f2.Status = FriendshipStatus.Accepted;
            f2.FriendGroup = sanitizedGroup;
            f2.TimestampUpdated = now;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Accepts a friend request from requesterAccountId, ensuring both directed rows in core.Friendships
    /// are transitioned to Accepted status and assigned appropriate friend groups.
    /// </summary>
    public async Task<bool> AcceptFriendshipAsync(int accountId, int requesterAccountId, string friendGroup = "General", CancellationToken ct = default)
    {
        _logger.LogInformation("[FriendshipRepository] Account #{AccountId} accepting friendship from Requester #{RequesterId} (Group: {Group})",
            accountId, requesterAccountId, friendGroup);

        if (accountId <= 0 || requesterAccountId <= 0 || accountId == requesterAccountId)
            return false;

        bool accountExists = await _db.Accounts.AnyAsync(a => a.ID == accountId, ct);
        bool requesterExists = await _db.Accounts.AnyAsync(a => a.ID == requesterAccountId, ct);

        if (!accountExists || !requesterExists) return false;

        string sanitizedGroup = string.IsNullOrWhiteSpace(friendGroup) ? "General" : friendGroup.Trim();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        List<Friendship> existing = await _db.Friendships
            .Where(f => (f.AccountID == accountId && f.FriendAccountID == requesterAccountId) ||
                        (f.AccountID == requesterAccountId && f.FriendAccountID == accountId))
            .ToListAsync(ct);

        // 1. Ensure requester -> acceptor is Accepted
        Friendship? incoming = existing.FirstOrDefault(f => f.AccountID == requesterAccountId && f.FriendAccountID == accountId);
        if (incoming == null)
        {
            _db.Friendships.Add(new Friendship
            {
                AccountID = requesterAccountId,
                FriendAccountID = accountId,
                FriendGroup = "General",
                Status = FriendshipStatus.Accepted,
                TimestampCreated = now
            });
        }
        else
        {
            incoming.Status = FriendshipStatus.Accepted;
            incoming.TimestampUpdated = now;
        }

        // 2. Ensure acceptor -> requester is Accepted with the acceptor's friendGroup
        Friendship? outgoing = existing.FirstOrDefault(f => f.AccountID == accountId && f.FriendAccountID == requesterAccountId);
        if (outgoing == null)
        {
            _db.Friendships.Add(new Friendship
            {
                AccountID = accountId,
                FriendAccountID = requesterAccountId,
                FriendGroup = sanitizedGroup,
                Status = FriendshipStatus.Accepted,
                TimestampCreated = now
            });
        }
        else
        {
            outgoing.Status = FriendshipStatus.Accepted;
            outgoing.FriendGroup = sanitizedGroup;
            outgoing.TimestampUpdated = now;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Creates a unilateral Pending friend request from <paramref name="accountId" /> to
    /// <paramref name="targetAccountId" /> unless a reciprocal Pending (or Accepted) request from
    /// <paramref name="targetAccountId" /> already exists, in which case both directions are
    /// mutually accepted.
    /// </summary>
    public async Task<FriendRequestOutcome> SendFriendRequestAsync(
        int accountId,
        int targetAccountId,
        string friendGroup = "General",
        CancellationToken ct = default)
    {
        if (accountId <= 0 || targetAccountId <= 0 || accountId == targetAccountId)
            return FriendRequestOutcome.Failed;

        bool accountExists = await _db.Accounts.AnyAsync(a => a.ID == accountId, ct);
        bool targetExists = await _db.Accounts.AnyAsync(a => a.ID == targetAccountId, ct);
        if (!accountExists || !targetExists)
        {
            _logger.LogWarning(
                "[FriendshipRepository] Cannot send friend request from #{AccountId} to #{TargetId}: missing account record",
                accountId, targetAccountId);
            return FriendRequestOutcome.Failed;
        }

        string sanitizedGroup = string.IsNullOrWhiteSpace(friendGroup) ? "General" : friendGroup.Trim();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        List<Friendship> existing = await _db.Friendships
            .Where(f => (f.AccountID == accountId && f.FriendAccountID == targetAccountId) ||
                        (f.AccountID == targetAccountId && f.FriendAccountID == accountId))
            .ToListAsync(ct);

        Friendship? outgoing = existing.FirstOrDefault(f => f.AccountID == accountId && f.FriendAccountID == targetAccountId);
        Friendship? incoming = existing.FirstOrDefault(f => f.AccountID == targetAccountId && f.FriendAccountID == accountId);

        if (outgoing?.Status == FriendshipStatus.Accepted && incoming?.Status == FriendshipStatus.Accepted)
        {
            if (!string.Equals(outgoing.FriendGroup, sanitizedGroup, StringComparison.Ordinal))
            {
                outgoing.FriendGroup = sanitizedGroup;
                outgoing.TimestampUpdated = now;
                await _db.SaveChangesAsync(ct);
            }

            return FriendRequestOutcome.AcceptedMutual;
        }

        if (incoming is { Status: FriendshipStatus.Pending or FriendshipStatus.Accepted })
        {
            _logger.LogInformation(
                "[FriendshipRepository] Reciprocal request found from #{TargetId} -> #{AccountId}; promoting mutual Accepted friendship",
                targetAccountId, accountId);
            bool accepted = await AcceptFriendshipAsync(accountId, targetAccountId, sanitizedGroup, ct);
            return accepted ? FriendRequestOutcome.AcceptedMutual : FriendRequestOutcome.Failed;
        }

        if (outgoing is null)
        {
            _db.Friendships.Add(new Friendship
            {
                AccountID = accountId,
                FriendAccountID = targetAccountId,
                FriendGroup = sanitizedGroup,
                Status = FriendshipStatus.Pending,
                TimestampCreated = now
            });
        }
        else
        {
            outgoing.Status = FriendshipStatus.Pending;
            outgoing.FriendGroup = sanitizedGroup;
            outgoing.TimestampUpdated = now;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[FriendshipRepository] Recorded unilateral Pending friend request #{AccountId} -> #{TargetId} (Group: {Group})",
            accountId, targetAccountId, sanitizedGroup);
        return FriendRequestOutcome.PendingCreated;
    }

    /// <summary>
    /// Accepts an incoming Pending friend request from <paramref name="requesterAccountId" /> to
    /// <paramref name="accountId" />. Rejects acceptance if no incoming Pending (or existing Accepted)
    /// relationship from <paramref name="requesterAccountId" /> exists.
    /// </summary>
    public async Task<bool> TryAcceptIncomingRequestAsync(
        int accountId,
        int requesterAccountId,
        string friendGroup = "General",
        CancellationToken ct = default)
    {
        if (accountId <= 0 || requesterAccountId <= 0 || accountId == requesterAccountId)
            return false;

        List<Friendship> existing = await _db.Friendships
            .AsNoTracking()
            .Where(f => (f.AccountID == accountId && f.FriendAccountID == requesterAccountId) ||
                        (f.AccountID == requesterAccountId && f.FriendAccountID == accountId))
            .ToListAsync(ct);

        Friendship? incoming = existing.FirstOrDefault(f => f.AccountID == requesterAccountId && f.FriendAccountID == accountId);
        Friendship? outgoing = existing.FirstOrDefault(f => f.AccountID == accountId && f.FriendAccountID == requesterAccountId);

        if (incoming?.Status == FriendshipStatus.Accepted && outgoing?.Status == FriendshipStatus.Accepted)
        {
            return true;
        }

        if (incoming?.Status != FriendshipStatus.Pending)
        {
            _logger.LogWarning(
                "[FriendshipRepository] Rejected accept request by #{AccountId} for #{RequesterId}: no incoming Pending request exists (incomingStatus={Status})",
                accountId, requesterAccountId, incoming?.Status.ToString() ?? "None");
            return false;
        }

        return await AcceptFriendshipAsync(accountId, requesterAccountId, friendGroup, ct);
    }

    /// <summary>
    /// Updates the FriendGroup for a specific directed friendship in core.Friendships.
    /// Operates directly on the indexed Friendship entity without loading the Account aggregate.
    /// </summary>
    public async Task<bool> UpdateFriendGroupAsync(int accountId, int friendAccountId, string newGroup, CancellationToken ct = default)
    {
        _logger.LogInformation("[FriendshipRepository] Updating FriendGroup for #{AccountId} -> #{FriendId} to '{NewGroup}'", accountId, friendAccountId, newGroup);

        string sanitizedGroup = string.IsNullOrWhiteSpace(newGroup) ? "General" : newGroup.Trim();

        Friendship? friendship = await _db.Friendships
            .FirstOrDefaultAsync(f => f.AccountID == accountId && f.FriendAccountID == friendAccountId, ct);

        if (friendship == null)
        {
            _logger.LogWarning("[FriendshipRepository] Friendship not found between Account #{AccountId} and Friend #{FriendId}", accountId, friendAccountId);
            return false;
        }

        friendship.FriendGroup = sanitizedGroup;
        friendship.TimestampUpdated = TimeProvider.System.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
