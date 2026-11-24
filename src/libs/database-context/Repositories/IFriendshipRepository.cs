// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Enumerations;

namespace NEXUS.Contracts.Data;

public record FriendshipRemovalResult
{
    public bool Success { get; init; }
    public bool Removed { get; init; }
    public int AccountId { get; init; }
    public string? AccountName { get; init; }
    public int BuddyId { get; init; }
    public string? BuddyName { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; } = 200;

    public static FriendshipRemovalResult NotFound(string errorMessage, int statusCode = 404) => new()
    {
        Success = false,
        Removed = false,
        ErrorMessage = errorMessage,
        StatusCode = statusCode
    };

    public static FriendshipRemovalResult Unchanged(int accountId, string accountName, int buddyId, string buddyName) => new()
    {
        Success = true,
        Removed = false,
        AccountId = accountId,
        AccountName = accountName,
        BuddyId = buddyId,
        BuddyName = buddyName,
        StatusCode = 200
    };

    public static FriendshipRemovalResult Succeeded(int accountId, string accountName, int buddyId, string buddyName) => new()
    {
        Success = true,
        Removed = true,
        AccountId = accountId,
        AccountName = accountName,
        BuddyId = buddyId,
        BuddyName = buddyName,
        StatusCode = 200
    };
}

public enum FriendRequestOutcome
{
    Failed = 0,
    PendingCreated = 1,
    AcceptedMutual = 2
}

public interface IFriendshipRepository
{
    Task<IReadOnlyList<Account>> SearchAccountsByPrefixAsync(string prefix, int limit = 10, CancellationToken ct = default);
    Task<IReadOnlyList<FriendedPeer>> GetFriendedPeersAsync(int accountId, CancellationToken ct = default);
    Task<IReadOnlyList<Friendship>> GetFriendshipsAsync(int accountId, FriendshipStatus? status = null, CancellationToken ct = default);
    Task<bool> AreFriendsAsync(int accountId1, int accountId2, CancellationToken ct = default);
    Task<FriendshipRemovalResult> RemoveFriendshipBidirectionalAsync(int accountId, int buddyId, CancellationToken ct = default);
    Task<bool> AddFriendshipAsync(int accountId, int friendAccountId, string friendGroup = "General", CancellationToken ct = default);
    Task<bool> AcceptFriendshipAsync(int accountId, int requesterAccountId, string friendGroup = "General", CancellationToken ct = default);
    Task<FriendRequestOutcome> SendFriendRequestAsync(int accountId, int targetAccountId, string friendGroup = "General", CancellationToken ct = default);
    Task<bool> TryAcceptIncomingRequestAsync(int accountId, int requesterAccountId, string friendGroup = "General", CancellationToken ct = default);
    Task<bool> UpdateFriendGroupAsync(int accountId, int friendAccountId, string newGroup, CancellationToken ct = default);
}

