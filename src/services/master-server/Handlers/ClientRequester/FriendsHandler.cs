// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Globalization;
using System.Text.Json;
using KONGOR.MasterServer.Extensions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Data;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class FriendsHandler(
    IFriendshipRepository friendshipRepository,
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<FriendsHandler> logger) : IClientRequestHandler
{
    private IFriendshipRepository FriendshipRepository { get; } = friendshipRepository;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? functionName = Request.GetQueryOrFormValue("f");

        // Dispatch based on function name
        if (functionName is "new_buddy" or "add_buddy")
            return await HandleSendFriendRequest(context, Request, cancellationToken);
        if (functionName is "approve_buddy" or "accept_buddy")
            return await HandleAcceptFriendRequest(context, Request, cancellationToken);
        if (functionName is "remove_buddy" or "reject_buddy")
            return await HandleRemoveFriend(context, Request, cancellationToken);
        if (functionName == "new_banned") return await HandleNewBanned(context, Request, cancellationToken);
        if (functionName == "remove_banned") return await HandleRemoveBanned(context, Request, cancellationToken);
        if (functionName == "new_ignored") return await HandleNewIgnored(context, Request, cancellationToken);
        if (functionName == "remove_ignored") return await HandleRemoveIgnored(context, Request, cancellationToken);

        if (functionName is "autocomplete" or "user_search" or "search_user" or "nick_search")
            return await HandleAutocomplete(context, Request, cancellationToken);

        LogUnknownFunction(functionName ?? "NULL");
        return new BadRequestObjectResult("Unknown function");
    }

    private async Task<IActionResult> HandleSendFriendRequest(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        (Account? account, int targetId, _, IActionResult? errorResult) =
            await ResolvePeerRequestParameters(context, Request, "buddy_id", "friend_id", cancellationToken);

        if (errorResult != null) return errorResult;

        string group = Request.GetFormOrQueryValue("group") ?? "General";
        FriendRequestOutcome outcome =
            await FriendshipRepository.SendFriendRequestAsync(account!.ID, targetId, group, cancellationToken);

        if (outcome == FriendRequestOutcome.Failed)
        {
            Logger.LogWarning("[FriendsHandler] Failed friend request from Account #{AccountId} to #{TargetId}", account.ID, targetId);
            return new PhpResult(new Hashtable { ["error"] = "Unable to send friend request." });
        }

        if (outcome == FriendRequestOutcome.AcceptedMutual)
        {
            Logger.LogInformation("[FriendsHandler] Reciprocal friend request mutually accepted between #{AccountId} and #{TargetId}", account.ID, targetId);
            await PublishPeerInvalidationAsync(account.ID, targetId, "Friend", "Added");

            string notificationKey = Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> acceptedResponse = new()
            {
                { "new_buddy", "OK" },
                { "approve_buddy", "OK" },
                { "buddy_id", targetId.ToString(CultureInfo.InvariantCulture) },
                { "notification", new Dictionary<string, int> { { notificationKey, targetId } } }
            };
            return new PhpResult(acceptedResponse);
        }

        Logger.LogInformation("[FriendsHandler] Created Pending friend request from Account #{AccountId} to #{TargetId}", account.ID, targetId);
        await PublishPeerInvalidationAsync(account.ID, targetId, "Friend", "Pending");
        Dictionary<string, object> pendingResponse = new()
        {
            { "new_buddy", "OK" },
            { "status", "PENDING" },
            { "buddy_id", targetId.ToString(CultureInfo.InvariantCulture) }
        };
        return new PhpResult(pendingResponse);
    }

    private async Task<IActionResult> HandleAcceptFriendRequest(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        (Account? account, int targetId, _, IActionResult? errorResult) =
            await ResolvePeerRequestParameters(context, Request, "buddy_id", "friend_id", cancellationToken);

        if (errorResult != null) return errorResult;

        string group = Request.GetFormOrQueryValue("group") ?? "General";
        bool accepted = await FriendshipRepository.TryAcceptIncomingRequestAsync(account!.ID, targetId, group, cancellationToken);

        if (!accepted)
        {
            Logger.LogWarning("[FriendsHandler] Rejected accept_buddy by Account #{AccountId} for #{TargetId}: no incoming Pending request", account.ID, targetId);
            return new PhpResult(new Hashtable
            {
                ["error"] = "No pending friend request found from target player.",
                ["approve_buddy"] = "FAILED"
            });
        }

        Logger.LogInformation("[FriendsHandler] Account #{AccountId} accepted friend request from #{TargetId}", account.ID, targetId);
        await PublishPeerInvalidationAsync(account.ID, targetId, "Friend", "Added");

        string notificationKey = Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);
        Dictionary<string, object> response = new()
        {
            { "approve_buddy", "OK" },
            { "buddy_id", targetId.ToString(CultureInfo.InvariantCulture) },
            { "notification", new Dictionary<string, int> { { notificationKey, targetId } } }
        };
        return new PhpResult(response);
    }

    private async Task<IActionResult> HandleAutocomplete(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        string? query = Request.GetQueryOrFormValue("nickname")
                        ?? Request.GetQueryOrFormValue("name")
                        ?? Request.GetQueryOrFormValue("q")
                        ?? Request.GetQueryOrFormValue("search");

        if (string.IsNullOrWhiteSpace(query)) return new PhpResult(new Hashtable());

        string trimmedQuery = query.Trim();
        if (trimmedQuery.Length > 24) trimmedQuery = trimmedQuery[..24];
        trimmedQuery = new string(trimmedQuery.Where(c => !char.IsControl(c) && c != '\'' && c != '`').ToArray());
        if (string.IsNullOrWhiteSpace(trimmedQuery)) return new PhpResult(new Hashtable());

        IReadOnlyList<Account> matches = await FriendshipRepository.SearchAccountsByPrefixAsync(trimmedQuery, 10, cancellationToken);

        Hashtable response = [];
        int index = 0;
        foreach (Account match in matches)
        {
            Hashtable userEntry = new()
            {
                ["account_id"] = match.ID.ToString(CultureInfo.InvariantCulture),
                ["nickname"] = match.Name
            };
            response[index.ToString(CultureInfo.InvariantCulture)] = userEntry;
            index++;
        }

        return new PhpResult(response);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[FriendsHandler] Unknown function '{FunctionName}'.")]
    private partial void LogUnknownFunction(string functionName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[FriendsHandler] RemoveFriend: BuddyID={BuddyId}")]
    private partial void LogRemoveFriend(string buddyId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[FriendsHandler] Removed friendship between {AccountName} and {BuddyName}")]
    private partial void LogFriendshipRemoved(string accountName, string buddyName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[FriendsHandler] Friendship not found between {AccountName} and {BuddyName}")]
    private partial void LogFriendshipNotFound(string accountName, string buddyName);

    private async Task<IActionResult> HandleRemoveFriend(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        string? cookie = ClientRequestHelper.GetCookie(Request);
        string? buddyIdStr = Request.GetFormOrQueryValue("buddy_id");

        LogRemoveFriend(buddyIdStr ?? "NULL");

        if (cookie == null || buddyIdStr == null) return new BadRequestObjectResult("Missing parameters");

        if (!int.TryParse(buddyIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buddyId))
            return new BadRequestObjectResult("Invalid Buddy ID");

        string? accountName = context.Items["SessionAccountName"] as string
                              ?? await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountName is null) return new UnauthorizedObjectResult("Session Not Found");

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account is null) return new NotFoundObjectResult("Account Not Found");

        FriendshipRemovalResult result = await FriendshipRepository.RemoveFriendshipBidirectionalAsync(
            account.ID, buddyId, cancellationToken);

        if (!result.Success)
        {
            return new NotFoundObjectResult(result.ErrorMessage ?? "Buddy Not Found");
        }

        if (result.Removed)
        {
            LogFriendshipRemoved(result.AccountName ?? account.Name, result.BuddyName ?? buddyId.ToString(CultureInfo.InvariantCulture));
            await PublishPeerInvalidationAsync(account.ID, buddyId, "Friend", "Removed");

            // Construct notification payload (random key -> buddy ID)
            // This mirrors legacy behavior where the client receives a notification trigger
            string notificationKey = Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);

            Dictionary<string, object> response = new()
            {
                { "remove_buddy", "OK" },
                { "notification", new Dictionary<string, int> { { notificationKey, result.BuddyId } } }
            };

            return new PhpResult(response);
        }

        LogFriendshipNotFound(result.AccountName ?? account.Name, result.BuddyName ?? buddyId.ToString(CultureInfo.InvariantCulture));
        // Return OK even if nothing removed, to match legacy "idempotent" behavior often found
        return new PhpResult(new { remove_buddy = "OK" });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[FriendsHandler] AddPeer: Account #{AccountId}, Peer #{PeerAccountId}, Type={Type}")]
    private partial void LogAddPeer(int accountId, int peerAccountId, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "[FriendsHandler] RemovePeer: Account #{AccountId}, Peer #{PeerAccountId}, Type={Type}")]
    private partial void LogRemovePeer(int accountId, int peerAccountId, string type);

    private async Task<(Account? account, int targetId, string reason, IActionResult? errorResult)> ResolvePeerRequestParameters(
        HttpContext context,
        HttpRequest Request,
        string idParamName,
        string altIdParamName,
        CancellationToken cancellationToken)
    {
        string? cookie = ClientRequestHelper.GetCookie(Request);
        if (cookie == null) return (null, 0, string.Empty, new UnauthorizedObjectResult("Missing cookie"));

        string? accountName = context.Items["SessionAccountName"] as string
                              ?? await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountName is null) return (null, 0, string.Empty, new UnauthorizedObjectResult("Session Not Found"));

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);
        if (account is null) return (null, 0, string.Empty, new NotFoundObjectResult("Account Not Found"));

        string? targetIdStr = Request.GetFormOrQueryValue(idParamName)
                              ?? Request.GetFormOrQueryValue(altIdParamName)
                              ?? Request.GetFormOrQueryValue("target_id")
                              ?? Request.GetFormOrQueryValue("account_id");

        string? targetName = Request.GetFormOrQueryValue("target_name")
                             ?? Request.GetFormOrQueryValue("nickname")
                             ?? Request.GetFormOrQueryValue("name");

        string reason = Request.GetFormOrQueryValue("reason") ?? string.Empty;

        int targetId = 0;
        if (!string.IsNullOrWhiteSpace(targetIdStr) && int.TryParse(targetIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId))
        {
            targetId = parsedId;
        }
        else if (!string.IsNullOrWhiteSpace(targetName))
        {
            Account? targetAccount = await AccountRepository.GetByNicknameAsync(targetName, cancellationToken);
            if (targetAccount == null)
            {
                return (null, 0, string.Empty, new PhpResult(new Hashtable { ["error"] = $"Player '{targetName}' not found." }));
            }
            targetId = targetAccount.ID;
        }
        else
        {
            return (null, 0, string.Empty, new PhpResult(new Hashtable { ["error"] = "Invalid target account ID or nickname." }));
        }

        if (account.ID == targetId)
        {
            return (null, 0, string.Empty, new PhpResult(new Hashtable { ["error"] = "You cannot target your own account." }));
        }

        return (account, targetId, reason, null);
    }

    private async Task<IActionResult> HandleNewBanned(HttpContext context, HttpRequest Request, CancellationToken cancellationToken = default)
    {
        (Account? account, int targetId, string reason, IActionResult? errorResult) =
            await ResolvePeerRequestParameters(context, Request, "banned_id", "banned_account_id", cancellationToken);

        if (errorResult != null) return errorResult;

        LogAddPeer(account!.ID, targetId, "Banned");

        AccountPeerResult result = await AccountRepository.AddPeerAsync(account.ID, targetId, PeerType.Banned, reason, ct: cancellationToken);
        if (!result.Success)
        {
            return new PhpResult(new Hashtable { ["error"] = result.ErrorMessage ?? "Failed to add to banlist." });
        }

        await PublishPeerInvalidationAsync(account.ID, targetId, "Banned", "Added");

        Hashtable response = new()
        {
            ["banned_id"] = targetId.ToString(CultureInfo.InvariantCulture),
            ["reason"] = reason
        };
        return new PhpResult(response);
    }

    private async Task<IActionResult> HandleRemoveBanned(HttpContext context, HttpRequest Request, CancellationToken cancellationToken = default)
    {
        (Account? account, int targetId, _, IActionResult? errorResult) =
            await ResolvePeerRequestParameters(context, Request, "banned_id", "banned_account_id", cancellationToken);

        if (errorResult != null) return errorResult;

        LogRemovePeer(account!.ID, targetId, "Banned");

        await AccountRepository.RemovePeerAsync(account!.ID, targetId, PeerType.Banned, cancellationToken);
        await PublishPeerInvalidationAsync(account.ID, targetId, "Banned", "Removed");

        Hashtable response = new()
        {
            ["remove_banned"] = "OK",
            ["banned_id"] = targetId.ToString(CultureInfo.InvariantCulture)
        };
        return new PhpResult(response);
    }

    private async Task<IActionResult> HandleNewIgnored(HttpContext context, HttpRequest Request, CancellationToken cancellationToken = default)
    {
        (Account? account, int targetId, string reason, IActionResult? errorResult) =
            await ResolvePeerRequestParameters(context, Request, "ignored_id", "ignored_account_id", cancellationToken);

        if (errorResult != null) return errorResult;

        LogAddPeer(account!.ID, targetId, "Ignored");

        AccountPeerResult result = await AccountRepository.AddPeerAsync(account.ID, targetId, PeerType.Ignored, reason, ct: cancellationToken);
        if (!result.Success)
        {
            return new PhpResult(new Hashtable { ["error"] = result.ErrorMessage ?? "Failed to add to ignore list." });
        }

        await PublishPeerInvalidationAsync(account.ID, targetId, "Ignored", "Added");

        Hashtable response = new()
        {
            ["ignored_id"] = targetId.ToString(CultureInfo.InvariantCulture),
            ["reason"] = reason
        };
        return new PhpResult(response);
    }

    private async Task<IActionResult> HandleRemoveIgnored(HttpContext context, HttpRequest Request, CancellationToken cancellationToken = default)
    {
        (Account? account, int targetId, _, IActionResult? errorResult) =
            await ResolvePeerRequestParameters(context, Request, "ignored_id", "ignored_account_id", cancellationToken);

        if (errorResult != null) return errorResult;

        LogRemovePeer(account!.ID, targetId, "Ignored");

        await AccountRepository.RemovePeerAsync(account!.ID, targetId, PeerType.Ignored, cancellationToken);
        await PublishPeerInvalidationAsync(account.ID, targetId, "Ignored", "Removed");

        Hashtable response = new()
        {
            ["remove_ignored"] = "OK",
            ["ignored_id"] = targetId.ToString(CultureInfo.InvariantCulture)
        };
        return new PhpResult(response);
    }

    private async Task PublishPeerInvalidationAsync(int accountId, int peerAccountId, string type, string action)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                AccountId = accountId,
                PeerAccountId = peerAccountId,
                Type = type,
                Action = action
            });
            await DistributedCache.Multiplexer.GetSubscriber().PublishAsync(
                RedisChannel.Literal("account:peer_invalidation"), payload);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[FriendsHandler] Failed publishing peer invalidation to Redis for Account #{AccountId}", accountId);
        }
    }
}
