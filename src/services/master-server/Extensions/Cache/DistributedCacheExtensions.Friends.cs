// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using SharedCache = ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions;

namespace KONGOR.MasterServer.Extensions.Cache;

// SINGLE SOURCE OF TRUTH: ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions (src/libs/protocol-core).
// Key format: FRIEND-REQUEST:["{requesterID}:{targetID}"], value "{requesterNotificationID}:{targetNotificationID}", 24h TTL.
public static partial class DistributedCacheExtensions
{
    /// <summary>
    ///     Stores a pending friend request in the distributed cache store.
    ///     The value of this entry is the requester's and target's notification IDs as a tuple.
    /// </summary>
    public static Task SetFriendRequest(this IDatabase distributedCacheStore, int requesterID, int targetID,
        int requesterNotificationID, int targetNotificationID) =>
        SharedCache.SetFriendRequest(distributedCacheStore, requesterID, targetID, requesterNotificationID,
            targetNotificationID);

    /// <summary>
    ///     Retrieves a pending friend request from the distributed cache store.
    ///     Returns a tuple containing the requester's and target's notification IDs, or NULL if the request doesn't exist or
    ///     has expired.
    /// </summary>
    public static Task<(int RequesterNotificationID, int TargetNotificationID)?> GetFriendRequest(
        this IDatabase distributedCacheStore, int requesterID, int targetID) =>
        SharedCache.GetFriendRequest(distributedCacheStore, requesterID, targetID);

    /// <summary>
    ///     Removes a friend request from the distributed cache store, usually because it has been accepted or declined.
    /// </summary>
    public static Task RemoveFriendRequest(this IDatabase distributedCacheStore, int requesterID, int targetID) =>
        SharedCache.RemoveFriendRequest(distributedCacheStore, requesterID, targetID);

    /// <summary>
    ///     Checks whether a pending friend request already exists.
    /// </summary>
    public static Task<bool> PendingFriendRequestExists(this IDatabase distributedCacheStore, int requesterID,
        int targetID) =>
        SharedCache.PendingFriendRequestExists(distributedCacheStore, requesterID, targetID);
}
