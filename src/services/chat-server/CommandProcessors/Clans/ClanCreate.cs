// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Clans;

public readonly struct ClanCreateRequestData
{
    public readonly string Name;
    public readonly string Tag;
    public readonly List<string> Members;

    public ClanCreateRequestData(ChatBuffer buffer)
    {
        // Skip Header
        buffer.ReadCommandBytes();

        List<string> members = [];
        Name = buffer.ReadString();
        Tag = buffer.ReadString();

        // Legacy: NumberOfFoundingMembers = 4 (filter empty or sentinel slots so 1-4 founding invitees work)
        for (int i = 0; i < 4; i++)
        {
            string m = buffer.ReadString().Trim();
            if (!string.IsNullOrWhiteSpace(m) && !m.StartsWith("__kongor_slot", StringComparison.OrdinalIgnoreCase))
            {
                members.Add(m);
            }
        }

        Members = members;
    }
}

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_REQUEST)]
public partial class ClanCreate(
    MerrickContext merrick,
    IPendingClanService pendingClanService,
    IChatContext chatContext)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        Account? account = (Account?)session.Account;
        if (account is null) return;

        ClanCreateRequestData requestData = new(buffer);

        pendingClanService.RemoveObsoledPendingClans();

        // Validation - Name/Tag
        ChatBuffer? failResponse = await ClanTagNameAndDuplicatesSanityCheck(account, requestData.Name,
            requestData.Tag, requestData.Members);
        if (failResponse != null)
        {
            session.Send(failResponse);
            return;
        }

        // Validation - Members
        List<Account> clanMemberAccounts = [];
        failResponse = await MembersSanityCheck(account, requestData.Members, clanMemberAccounts);
        if (failResponse != null)
        {
            session.Send(failResponse);
            return;
        }

        // Check if all founding members are connected
        foreach (Account? memberAccount in from memberAccount in clanMemberAccounts
                 let isOnline = chatContext.ClientChatSessions.Values.Any(cs =>
                     ((Account?)cs.Account)?.ID == memberAccount.ID)
                 where !isOnline
                 select memberAccount)
        {
            session.Send(new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_INVITE,
                memberAccount.Name));
            return;
        }

        List<int> memberIds = [.. clanMemberAccounts.Select(a => a.ID)];

        PendingClan pendingClan = new()
        {
            ClanName = requestData.Name,
            ClanTag = requestData.Tag,
            Accepted = [.. Enumerable.Repeat(false, Math.Max(1, memberIds.Count))],
            CreationTime = TimeProvider.System.GetUtcNow().UtcDateTime,
            CreatorAccountId = account.ID,
            MembersAccountId = memberIds
        };

        pendingClanService.InsertPendingClan(pendingClan);

        // Send all invites
        foreach (ChatSession targetSession in clanMemberAccounts.Select(memberAccount => chatContext.ClientChatSessions
                         .Values
                         .FirstOrDefault(cs => ((Account?)cs.Account)?.ID == memberAccount.ID))
                     .OfType<ChatSession>())
            targetSession.Send(new ClanAddMemberResponse(account.Name, requestData.Name));
    }

    [GeneratedRegex("^[a-zA-Z0-9 ]{1,25}$")]
    private static partial Regex ClanNameRegex();

    [GeneratedRegex("^[a-zA-Z0-9]{1,4}$")]
    private static partial Regex ClanTagRegex();

    private async Task<ChatBuffer?> MembersSanityCheck(Account account, List<string> members,
        List<Account> clanMemberAccounts)
    {
        if (members.Count == 0)
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_PARAM);

        // check if Creator is in a clan
        if (account.Clan != null)
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_CLAN, account.Name);

        // check if Creator is in any pending invites or clan creation
        if (pendingClanService.IsUserInPendingClans(account))
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_INVITE, account.Name);

        List<string> lowerMembers = [.. members.Select(m => m.ToLowerInvariant())];
        List<Account> foundAccounts = await merrick.Accounts
            .Include(acc => acc.Clan)
            .Where(acc => lowerMembers.Contains(acc.Name.ToLower()))
            .ToListAsync();

        Dictionary<string, Account> foundAccountsDict =
            foundAccounts.ToDictionary(acc => acc.Name, StringComparer.OrdinalIgnoreCase);

        foreach (string member in members)
        {
            foundAccountsDict.TryGetValue(member, out Account? memberAccount);

            if (memberAccount == null)
                return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_FIND, member);

            if (memberAccount.Clan != null)
                return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_CLAN,
                    memberAccount.Name);

            if (pendingClanService.IsUserInPendingClans(memberAccount))
                return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_INVITE,
                    memberAccount.Name);

            clanMemberAccounts.Add(memberAccount);
        }

        return null;
    }

    private async Task<ChatBuffer?> ClanTagNameAndDuplicatesSanityCheck(Account account,
        string name, string tag, List<string> members)
    {
        Match match = ClanNameRegex().Match(name);

        if (!match.Success) return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_NAME);

        # region Clan Name Visual Formatting Checks

        const int availableScreenSpaceInPixels = 220;

        bool tooWide = EstimateClanNameWidth(name) > availableScreenSpaceInPixels;

        bool tooManyConsecutiveSpaces = name.Contains("  ");

        if (tooWide || tooManyConsecutiveSpaces)
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_NAME);

        # endregion

        match = ClanTagRegex().Match(tag);

        if (!match.Success) return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_TAG);

        string lowerName = name.ToLowerInvariant();
        bool clanNameExists = await merrick.Clans.AnyAsync(clan => clan.Name.ToLower() == lowerName);
        if (clanNameExists || pendingClanService.IsClanNameInPendingClans(name))
        {
            Log.Warning("[ClanCreate] Rejected clan creation by '{Creator}': name '{Name}' is already taken or pending.", account.Name, name);
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_NAME);
        }

        string lowerTag = tag.ToLowerInvariant();
        bool clanTagExists = await merrick.Clans.AnyAsync(clan => clan.Tag.ToLower() == lowerTag);
        if (clanTagExists || pendingClanService.IsClanTagInPendingClans(tag))
        {
            Log.Warning("[ClanCreate] Rejected clan creation by '{Creator}': tag '[{Tag}]' is already taken or pending.", account.Name, tag);
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_TAG);
        }

        // check if creator included himself
        if (members.Contains(account.Name, StringComparer.OrdinalIgnoreCase))
            return new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_DUPE);

        // check if there are duplicated members
        return members.Count > members.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            ? new ClanCreateFailResponse(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_DUPE)
            : null;
    }

    private static int EstimateClanNameWidth(string name)
    {
        int clanNameWidthInPixels = 0;

        foreach (char letter in name)
            if (letter is 'i' or 'I' or 'l' or ' ')
                clanNameWidthInPixels += 6; // Skinny Letter Or Space: 6 pixels
            else if (letter is 'm' or 'w')
                clanNameWidthInPixels += 16; // Lowercase Wide Letter: 16 pixels
            else if (letter is 'M' or 'W')
                clanNameWidthInPixels += 19; // Uppercase Wide Letter: 19 pixels
            else if (letter is 'O')
                clanNameWidthInPixels += 17; // Uppercase Semi-Wide Letter: 17 pixels
            else if (char.IsLower(letter))
                clanNameWidthInPixels += 10; // Average Lowercase Regular Letter: 10 pixels
            else if (char.IsUpper(letter)) clanNameWidthInPixels += 15; // Average Uppercase Regular Letter: 15 pixels

        return clanNameWidthInPixels;
    }
}
