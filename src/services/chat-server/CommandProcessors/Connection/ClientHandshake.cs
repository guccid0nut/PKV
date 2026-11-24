// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT, AllowUnauthenticated = true, AllowedRoles = ChatPeerRole.Client)]
public class ClientHandshake(
    IAccountRepository accountRepository,
    IDatabase distributedCacheStore,
    IChatContext chatContext)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        if ((object?)session.Account is not null)
            // Session is already authenticated; ignore duplicate connect request
            return;

        ClientHandshakeRequestData requestData = new(buffer);

        // Set Client Metadata On Session.
        // This Needs To Be Set At The First Opportunity So That Any Subsequent Code Logic Can Have Access To The Client's Metadata
        session.ClientMetadata = requestData.ToMetadata();

        string? cachedAccountName =
            await distributedCacheStore.GetAccountNameForSessionCookie(requestData.SessionCookie);

        // Ensure Session Cookie Exists In Cache
        if (cachedAccountName is null)
        {
            string maskedCookie = SecurityMasking.MaskToken(requestData.SessionCookie);
            Console.WriteLine(
                $"[AUTH_FAIL_COOKIE] Account ID {requestData.AccountID}, Cookie '{maskedCookie}' NOT found in Redis cache!");
            Log.Warning(
                @"Authentication Failed For Account ID ""{RequestData.AccountID}"": Session Cookie Prefix ""{CookiePrefix}"" Not Found In Cache",
                requestData.AccountID, maskedCookie);

            session
                .Reject(ChatProtocol.ChatRejectReason.ECR_AUTH_FAILED)
                .TerminateClient();

            return;
        }

        try
        {
            Account? account = await accountRepository.GetByIdAsync(requestData.AccountID);

            if (account is null)
            {
                Log.Error(@"[BUG] Account With ID ""{RequestData.AccountID}"" Could Not Be Found",
                    requestData.AccountID);

                session
                    .Reject(ChatProtocol.ChatRejectReason.ECR_UNKNOWN)
                    .TerminateClient();

                return;
            }

            // Reject Banned Or Disabled Accounts
            if (account.Type is AccountType.Disabled || account.IsBanned)
            {
                Console.WriteLine(
                    $"[AUTH_FAIL_BANNED] Account ID {requestData.AccountID} (Name: '{account.Name}') is banned or disabled.");
                Log.Warning(
                    @"Authentication Failed: Account ID ""{RequestData.AccountID}"" (Name: ""{Account.Name}"") is disabled or banned",
                    requestData.AccountID, account.Name);

                session
                    .Reject(ChatProtocol.ChatRejectReason.ECR_AUTH_FAILED)
                    .TerminateClient();

                return;
            }

            // Ensure Account Name Matches Cached Account Name From Session Cookie
            if (!account.Name.Equals(cachedAccountName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"[AUTH_FAIL_NAME] Account ID {requestData.AccountID}, DB Name '{account.Name}' != Cached Name '{cachedAccountName}'");
                Log.Warning(
                    @"Authentication Failed: Account ID ""{RequestData.AccountID}"" (Name: ""{Account.Name}"") Does Not Match Cached Account Name ""{CachedAccountName}""",
                    requestData.AccountID, account.Name, cachedAccountName);

                session
                    .Reject(ChatProtocol.ChatRejectReason.ECR_AUTH_FAILED)
                    .TerminateClient();

                return;
            }

            // Ensure Authentication Hash (AccountID + RemoteIP + Cookie + Salt) Matches Expected Value
            IPAddress? socketAddr = (((Socket?)session.Socket)?.RemoteEndPoint as IPEndPoint)?.Address;
            string? socketIp = socketAddr?.ToString();
            string? socketIPv4 = socketAddr is not null && socketAddr.IsIPv4MappedToIPv6
                ? socketAddr.MapToIPv4().ToString()
                : socketIp;

            HashSet<string> candidateIPs = new(StringComparer.OrdinalIgnoreCase)
            {
                "127.0.0.1",
                "::1",
                requestData.RemoteIP,
                socketIp ?? "",
                socketIPv4 ?? ""
            };

            bool isValidHash =
                (from candidateIp in candidateIPs
                    where !string.IsNullOrWhiteSpace(candidateIp)
                    select SRPAuthenticationHandlers.ComputeChatServerCookieHash(requestData.AccountID, candidateIp,
                        requestData.SessionCookie)).Any(testHash =>
                    requestData.SessionAuthenticationHash.Equals(testHash, StringComparison.OrdinalIgnoreCase));

            if (!isValidHash)
            {
                Console.WriteLine(
                    $"[AUTH_FAIL_HASH] Account '{account.Name}', ClientHash '{SecurityMasking.MaskToken(requestData.SessionAuthenticationHash)}' did not match candidate IPs ({string.Join(", ", candidateIPs)})");
                Log.Warning(@"Authentication Failed For Account ""{Account.Name}"": Invalid Authentication Hash",
                    account.Name);

                session
                    .Reject(ChatProtocol.ChatRejectReason.ECR_AUTH_FAILED)
                    .TerminateClient();

                return;
            }

            // Check For Concurrent Connections: Disconnect Any Other Existing Sessions For This Account Or For Any Sub-Account Of The Same User
            // Ignore Staff And Guest Accounts For Concurrent Connection Checks
            if (account.Type is not AccountType.Staff and not AccountType.Guest)
            {
                List<int> subAccountIDs = (object?)account.User.Accounts is not null
                    ?
                    [
                        .. account.User.Accounts
                            .Where(sub =>
                                (object?)sub.User is not null && sub.User.ID == account.User.ID &&
                                sub.ID != account.ID).Select(subAccount => subAccount.ID)
                    ]
                    : [];

                foreach (int subAccountID in subAccountIDs)
                {
                    ChatSession? existingSessionMatch = chatContext.ClientChatSessions.Values
                        .FirstOrDefault(eSession =>
                            eSession != session && (object?)eSession.Account is not null &&
                            eSession.Account.ID == subAccountID);

                    if (existingSessionMatch is not null)
                    {
                        Console.WriteLine(
                            $"[DEBUG_SUB_REJECT] SubAccountID={subAccountID}, ExistingMatch={existingSessionMatch.Account.Name} (ID: {existingSessionMatch.Account.ID}), CurrentConnectingAccount={account.Name} (ID: {account.ID})");
                        Log.Warning(
                            @"[DEBUG_SUB_REJECT] Disconnecting Existing Session For Account ID ""{SubAccountID}"" (Account ""{ExistingSessionMatchAccountName}"") Due To Concurrent Connection Attempt",
                            subAccountID, existingSessionMatch.Account.Name);

                        SendSystemSupersedeNotice(existingSessionMatch);
                        existingSessionMatch
                            .Reject(ChatProtocol.ChatRejectReason.ECR_ACCOUNT_SHARING)
                            .TerminateClient();
                    }
                }
            }

            if (chatContext.ClientChatSessions.Values.FirstOrDefault(eSession =>
                    eSession != session && (object?)eSession.Account is not null && eSession.Account.ID == account.ID)
                is { } existingSession)
            {
                Console.WriteLine(
                    $"[DEBUG_REJECT] AccountID={account.ID}, ExistingAccountID={((Account?)existingSession.Account)?.ID}, AccountName={account.Name}, ExistingName={existingSession.Account?.Name}");
                Log.Warning(
                    @"[DEBUG_REJECT] Disconnecting Existing Session For Account ""{AccountName}"" (ID: {AccountID}). ExistingSession Account: ""{ExistingAccountName}"" (ID: {ExistingAccountID})",
                    account.Name, account.ID, existingSession.Account?.Name,
                    existingSession.Account?.ID);

                SendSystemSupersedeNotice(existingSession);
                existingSession
                    .Reject(ChatProtocol.ChatRejectReason.ECR_ACCOUNT_SHARING)
                    .TerminateClient();
            }

            // Validate Client Version (Support both legacy 4.10.1.0 and modern 5.0.0.0)
            string clientVersion = $"{requestData.ClientVersionMajor}.{requestData.ClientVersionMinor}.{requestData.ClientVersionPatch}.{requestData.ClientVersionRevision}";
            if (clientVersion is not "4.10.1.0" and not "5.0.0.0")
            {
                Log.Warning("Authentication Failed: Bad Version {Version}", clientVersion);
                session
                    .Reject(ChatProtocol.ChatRejectReason.ECR_BAD_VERSION)
                    .TerminateClient();

                return;
            }

            Log.Information("Authentication Successful For Account {AccountID}. Accepting Connection.",
                requestData.AccountID);

            // Accept Connection, Send Options, And Broadcast Connection To Friends And Clan Members
            session
                .Accept(account);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CRITICAL] ClientHandshake Processing Failed!");
            throw;
        }
    }

    private static void SendSystemSupersedeNotice(ChatSession targetSession)
    {
        try
        {
            ChatBuffer notice = new();
            notice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            notice.WriteString("System");
            notice.WriteString("Your chat session was disconnected because this account logged in from another session.");
            targetSession.Send(notice);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[ClientHandshake] Failed to dispatch system supersede whisper prior to ECR_ACCOUNT_SHARING.");
        }
    }
}
