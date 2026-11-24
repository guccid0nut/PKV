// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Collections;
using KONGOR.MasterServer.Extensions;
using System.Globalization;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

[method: ActivatorUtilitiesConstructor]
public partial class ClanHandler(
    IClanRepository clanRepository,
    IAccountRepository accountRepository,
    IFriendshipRepository? friendshipRepository,
    IDatabase distributedCache,
    ILogger<ClanHandler> logger) : IClientRequestHandler
{
    public ClanHandler(
        IClanRepository clanRepository,
        IAccountRepository accountRepository,
        IDatabase distributedCache,
        ILogger<ClanHandler> logger)
        : this(clanRepository, accountRepository, null, distributedCache, logger)
    {
    }

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? functionName = Request.GetQueryOrFormValue("f")?.Trim().ToLowerInvariant();

        if (functionName is "get_clan_info" or "clan_info" or "get_clan_roster" or "clan_roster")
        {
            return await HandleGetClanInfoAsync(context, Request, cancellationToken);
        }

        if (functionName is "leave_clan" or "clan_leave" or "remove_clan_member" or "clan_kick")
        {
            return await HandleLeaveOrRemoveMemberAsync(context, Request, cancellationToken);
        }

        if (functionName is "disband_clan" or "clan_disband")
        {
            return await HandleDisbandClanAsync(context, Request, cancellationToken);
        }

        // Safe query/form parameter extraction without logging sensitive cookies
        // Extracted via safe form/query helpers without raw cookie logging

        string? cookie = ClientRequestHelper.GetCookie(Request);
        string? targetIdStr = Request.GetFormOrQueryValue("target_id");
        string? clanIdStr = Request.GetFormOrQueryValue("clan_id");
        string? rankStr = Request.GetFormOrQueryValue("rank");
        LogSetRankRequest(SecurityMasking.MaskToken(cookie), targetIdStr ?? "NULL", clanIdStr ?? "NULL", rankStr ?? "NULL");

        if (cookie == null || targetIdStr == null || clanIdStr == null || rankStr == null)
        {
            LogMissingParameters(cookie != null, targetIdStr, clanIdStr, rankStr);
            return new BadRequestObjectResult("Missing parameters");
        }

        if (!int.TryParse(targetIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetId) ||
            !int.TryParse(clanIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int clanId))
        {
            LogInvalidId(targetIdStr, clanIdStr);
            return new BadRequestObjectResult("Invalid ID parameters");
        }

        rankStr = rankStr.Trim();
        if (rankStr.Length > 16 || !rankStr.All(char.IsLetterOrDigit))
        {
            return new BadRequestObjectResult("Invalid rank parameter");
        }

        (bool isValid, string? accountName) = await distributedCache.ValidateAccountSessionCookie(cookie);
        if (!isValid || accountName == null)
        {
            LogInvalidSession(SecurityMasking.MaskToken(cookie));
            return new UnauthorizedObjectResult("Invalid Session");
        }

        Account? requester = await clanRepository.GetAccountWithClanByNameAsync(accountName, cancellationToken);
        if (requester == null)
        {
            requester = await accountRepository.GetByNicknameAsync(accountName, cancellationToken);
        }

        if (requester == null || requester.Clan?.ID != clanId)
        {
            LogRequesterNotInClan(requester?.ID, clanId);
            return new BadRequestObjectResult("Requester not in specified clan");
        }

        if (requester.ClanTier < ClanTier.Officer)
        {
            LogInsufficientPermissions(requester.ClanTier);
            return new BadRequestObjectResult("Insufficient Permissions");
        }

        ClanRankUpdateResult result = await clanRepository.SetMemberRankAsync(
            requester.ID, targetId, clanId, rankStr, cancellationToken);

        if (!result.Success)
        {
            return new BadRequestObjectResult(result.ErrorMessage ?? "Action failed");
        }

        string key1 = Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);
        string redisPayload = result.RedisPublishPayload;
        await distributedCache.PublishAsync(RedisChannel.Literal("nexus.clan.updates"), redisPayload);
        if (logger.IsEnabled(LogLevel.Information)) LogRedisUpdate(redisPayload);

        Dictionary<string, object> response = new()
        {
            { "set_rank", "OK" },
            { "notification", new Dictionary<string, int> { { key1, requester.ID } } }
        };

        if (result.IsKick)
        {
            response.Add("kick", "OK");
            response.Add("remove_member", "OK");
        }

        string serialized = PhpSerialization.Serialize(response);
        if (logger.IsEnabled(LogLevel.Information)) LogResponse(serialized);

        return new PhpResult(response);
    }

    private async Task<IActionResult> HandleGetClanInfoAsync(HttpContext context, HttpRequest Request, CancellationToken cancellationToken)
    {
        string? cookie = ClientRequestHelper.GetCookie(Request);
        string? sessionAccountName = context.Items["SessionAccountName"] as string;
        if (string.IsNullOrWhiteSpace(sessionAccountName) && !string.IsNullOrWhiteSpace(cookie))
        {
            sessionAccountName = await distributedCache.GetAccountNameForSessionCookie(cookie);
        }

        string? requestedNick = Request.GetFormOrQueryValue("nickname");
        string? accountName = !string.IsNullOrWhiteSpace(requestedNick) ? requestedNick : sessionAccountName;

        if (string.IsNullOrWhiteSpace(accountName))
        {
            return new PhpResult(new Hashtable { ["error"] = "Account not specified" });
        }

        string cleanName = accountName.Trim();
        if (cleanName.StartsWith('[') && cleanName.IndexOf(']') is int closeBracket and > 0)
        {
            cleanName = cleanName[(closeBracket + 1)..].Trim();
        }

        Account? account = await clanRepository.GetAccountWithClanByNameAsync(cleanName, cancellationToken)
                           ?? await accountRepository.GetByNicknameAsync(cleanName, cancellationToken);

        if (account == null)
        {
            return new PhpResult(new Hashtable { ["error"] = "Account not found" });
        }

        bool isAuthenticatedOwner = !string.IsNullOrWhiteSpace(sessionAccountName) &&
                                    string.Equals(sessionAccountName.Trim(), account.Name, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<Friendship> friendships = (isAuthenticatedOwner && friendshipRepository != null)
            ? await friendshipRepository.GetFriendshipsAsync(account.ID, FriendshipStatus.Accepted, cancellationToken)
            : [];
        Hashtable friendsTable = [];
        List<string> friendsTokens = [];
        int fIdx = 0;
        foreach (Friendship f in friendships.DistinctBy(x => x.FriendAccountID))
        {
            string fName = f.FriendAccount?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fName)) continue;
            string fTag = f.FriendAccount?.Clan?.Tag ?? string.Empty;
            string fGroup = string.IsNullOrWhiteSpace(f.FriendGroup) || f.FriendGroup.Equals("General", StringComparison.OrdinalIgnoreCase) ? "friends" : f.FriendGroup;
            friendsTable[fIdx.ToString(CultureInfo.InvariantCulture)] = new Hashtable
            {
                ["account_id"] = f.FriendAccountID.ToString(CultureInfo.InvariantCulture),
                ["nickname"] = fName,
                ["clan_tag"] = fTag,
                ["buddy_group"] = fGroup,
                ["is_online"] = "1"
            };
            friendsTokens.Add($"{f.FriendAccountID}|{fName}|{fTag}|{fGroup}|1");
            fIdx++;
        }

        if (account.Clan == null)
        {
            Hashtable noClanResponse = new()
            {
                ["clan_id"] = "0",
                ["clan_name"] = string.Empty,
                ["clan_tag"] = string.Empty,
                ["clan_roster"] = new Hashtable(),
                ["clan_roster_str"] = string.Empty,
                ["friends"] = friendsTable
            };
            if (isAuthenticatedOwner)
            {
                noClanResponse["friends_str"] = string.Join(";", friendsTokens);
            }
            return new PhpResult(noClanResponse);
        }

        IReadOnlyList<Account> members = await clanRepository.GetClanMembersAsync(account.Clan.ID, cancellationToken);
        Hashtable rosterTable = [];
        List<string> rosterTokens = [];
        int mIdx = 0;
        foreach (Account member in members.OrderByDescending(m => m.ClanTier).ThenBy(m => m.Name))
        {
            string fullNick = $"[{account.Clan.Tag}]{member.Name}";
            string rankName = member.GetClanTierName();
            string isOnline = (member.ID == account.ID || friendships.Any(fr => fr.FriendAccountID == member.ID)) ? "1" : "0";
            string icon = "/ui/fe2/store/icons/account_icons/default.tga";
            string color = member.Type == AccountType.Staff ? "#FF0000" : string.Empty;

            rosterTable[mIdx.ToString(CultureInfo.InvariantCulture)] = new Hashtable
            {
                ["account_id"] = member.ID.ToString(CultureInfo.InvariantCulture),
                ["nickname"] = fullNick,
                ["rank"] = rankName,
                ["is_online"] = isOnline,
                ["icon"] = icon,
                ["color"] = color
            };
            rosterTokens.Add($"{member.ID}|{fullNick}|{rankName}|{isOnline}|{icon}|{color}");
            mIdx++;
        }

        Hashtable clanResponse = new()
        {
            ["clan_id"] = account.Clan.ID.ToString(CultureInfo.InvariantCulture),
            ["clan_name"] = account.Clan.Name,
            ["clan_tag"] = account.Clan.Tag,
            ["clan_roster"] = rosterTable,
            ["clan_roster_str"] = string.Join(";", rosterTokens),
            ["friends"] = friendsTable
        };
        if (isAuthenticatedOwner)
        {
            clanResponse["friends_str"] = string.Join(";", friendsTokens);
        }

        return new PhpResult(clanResponse);
    }

    private async Task<IActionResult> HandleLeaveOrRemoveMemberAsync(HttpContext context, HttpRequest Request, CancellationToken cancellationToken)
    {
        string? cookie = ClientRequestHelper.GetCookie(Request);
        if (string.IsNullOrWhiteSpace(cookie)) return new UnauthorizedObjectResult("Missing cookie");

        (bool isValid, string? accountName) = await distributedCache.ValidateAccountSessionCookie(cookie);
        if (!isValid || string.IsNullOrWhiteSpace(accountName)) return new UnauthorizedObjectResult("Invalid session");

        Account? requester = await clanRepository.GetAccountWithClanByNameAsync(accountName, cancellationToken);
        if (requester?.Clan == null) return new BadRequestObjectResult("Not in a clan");

        string? targetIdStr = Request.GetFormOrQueryValue("target_id");
        int targetId = int.TryParse(targetIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tid) ? tid : requester.ID;

        if (targetId != requester.ID && requester.ClanTier < ClanTier.Officer)
        {
            return new BadRequestObjectResult("Insufficient permissions");
        }

        bool removed = await clanRepository.RemoveMemberAsync(targetId, requester.Clan.ID, cancellationToken);
        return new PhpResult(new Hashtable { ["remove_member"] = removed ? "OK" : "FAIL" });
    }

    private async Task<IActionResult> HandleDisbandClanAsync(HttpContext context, HttpRequest Request, CancellationToken cancellationToken)
    {
        string? cookie = ClientRequestHelper.GetCookie(Request);
        if (string.IsNullOrWhiteSpace(cookie)) return new UnauthorizedObjectResult("Missing cookie");

        (bool isValid, string? accountName) = await distributedCache.ValidateAccountSessionCookie(cookie);
        if (!isValid || string.IsNullOrWhiteSpace(accountName)) return new UnauthorizedObjectResult("Invalid session");

        Account? requester = await clanRepository.GetAccountWithClanByNameAsync(accountName, cancellationToken);
        if (requester?.Clan == null || requester.ClanTier != ClanTier.Leader)
        {
            return new BadRequestObjectResult("Only the clan leader can disband the clan");
        }

        bool disbanded = await clanRepository.DisbandClanAsync(requester.Clan.ID, cancellationToken);
        return new PhpResult(new Hashtable { ["disband_clan"] = disbanded ? "OK" : "FAIL" });
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Clans] HandleSetRank: Cookie={CookiePrefix}, Target={TargetId}, Clan={ClanId}, Rank={Rank}")]
    private partial void LogSetRankRequest(string cookiePrefix, string targetId, string clanId, string rank);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[Clans] HandleSetRank Missing parameters: C:{HasCookie} T:{TargetId} Cl:{ClanId} R:{Rank}")]
    private partial void LogMissingParameters(bool hasCookie, string? targetId, string? clanId, string? rank);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Clans] HandleSetRank Invalid ID: T:{TargetId} Cl:{ClanId}")]
    private partial void LogInvalidId(string? targetId, string? clanId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Clans] HandleSetRank Invalid Session: {CookiePrefix}")]
    private partial void LogInvalidSession(string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[Clans] HandleSetRank Requester (ID:{RequesterId}) not in specified clan ({ClanId}) or null.")]
    private partial void LogRequesterNotInClan(int? requesterId, int clanId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[Clans] HandleSetRank Requester (Tier:{Tier}) has insufficient permissions.")]
    private partial void LogInsufficientPermissions(ClanTier tier);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[Clans] HandleSetRank Target (ID:{TargetId}) not in clan ({ClanId}) or null.")]
    private partial void LogTargetNotInClan(int targetId, int clanId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Clans] HandleSetRank Officer cannot kick Officer.")]
    private partial void LogOfficerCannotKickOfficer();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Clans] Account {RequesterName} kicked {TargetName} from Clan {ClanId}.")]
    private partial void LogKickedMember(string requesterName, string targetName, int clanId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Clans] HandleSetRank Unknown Rank: {Rank}")]
    private partial void LogUnknownRank(string rank);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Clans] HandleSetRank Only Leader can promote to Leader.")]
    private partial void LogOnlyLeaderCanPromoteToLeader();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Clans] {RequesterName} transferred Leadership to {TargetName}.")]
    private partial void LogLeadershipTransferred(string requesterName, string targetName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Clans] HandleSetRank Officer cannot promote Officer.")]
    private partial void LogOfficerCannotPromoteOfficer();

    [LoggerMessage(Level = LogLevel.Information, Message = "[Clans] {RequesterName} promoted {TargetName} to Officer.")]
    private partial void LogPromotedToOfficer(string requesterName, string targetName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[Clans] HandleSetRank Cannot demote higher/equal rank. Req:{RequesterTier} Tgt:{TargetTier}")]
    private partial void LogCannotDemote(ClanTier requesterTier, ClanTier targetTier);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Clans] {RequesterName} demoted {TargetName} to Member.")]
    private partial void LogDemotedToMember(string requesterName, string targetName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Clans] Published Redis Update: {Payload}")]
    private partial void LogRedisUpdate(string payload);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Clans] set_rank Response: {Response}")]
    private partial void LogResponse(string response);
}
