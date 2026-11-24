// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Logging;

public static partial class Log
{
    // Authentication Handlers
    [GeneratedRegex(
        @"(?>S2 Games)\/(?>Heroes [oO]f Newerth)\/(?<version>\d{1,2}\.\d{1,2}\.\d{1,2}\.\d{1,2})\/(?<platform>[wlm]a[cs])\/(?<architecture>x86_64|x86-biarch|universal-64|arm64)")]
    public static partial Regex UserAgentRegex();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Unable To Retrieve Cached SRP Authentication Session Data For Account Name \"{AccountName}\"")]
    public static partial void LogMissingSRPDataBug(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[BUG] Unable To Retrieve Cached System Information For Account Name \"{AccountName}\"")]
    public static partial void LogMissingSysInfoBug(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[BUG] Remote IP Address For Account Name \"{AccountName}\" Is NULL")]
    public static partial void LogRemoteIPNullBug(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Account \"{AccountName}\" Has Made A Request To Log In Using Unexpected User Agent \"{Agent}\"")]
    public static partial void LogUnexpectedUserAgent(this ILogger logger, string accountName, string agent);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "Account \"{AccountName}\" Has Made A Request To Log In Using Unexpected System Information \"{SysInfo}\"")]
    public static partial void LogUnexpectedSysInfo(this ILogger logger, string accountName, string sysInfo);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "Account \"{AccountName}\" Has Made A Request To Log In Using Unexpected System Information Hashes \"{Hashes}\"")]
    public static partial void LogUnexpectedSysInfoHashes(this ILogger logger, string accountName, string hashes);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Persisted Cookie {CookiePrefix} for Account {AccountName} to Database")]
    public static partial void LogPersistedCookie(this ILogger logger, string cookiePrefix, string accountName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Account \"{AccountName}\" Is Attempting To Use HTTP Client Authentication")]
    public static partial void LogAttemptingHttpClientAuth(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Missing Cookie In aids2cookie Request")]
    public static partial void LogMissingCookie(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "Cookie \"{CookiePrefix}\" Validated In Controller But Account Name Not Found In Cache (Context Missing, Redis Check Failed)")]
    public static partial void LogCookieValidatedButNotFound(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account name \"{AccountName}\" not found in DB during aids2cookie lookup")]
    public static partial void LogAccountNameNotFoundInDB(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PreAuth] Received pre_auth request for login: {AccountName}")]
    public static partial void LogPreAuthReceived(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PreAuth] Account not found: {AccountName}")]
    public static partial void LogPreAuthAccountNotFound(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[PreAuth] Blocked login attempt for banned account: {AccountName} (Expires: {BanExpiration}, Reason: {Reason})")]
    public static partial void LogPreAuthBannedAccount(this ILogger logger, string accountName, string banExpiration, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PreAuth] PreAuth success for {AccountName}")]
    public static partial void LogPreAuthSuccess(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[SRPAuth] Blocked login attempt for banned account: {AccountName} (Expires: {BanExpiration}, Reason: {Reason})")]
    public static partial void LogSRPAuthBlockedBannedAccount(this ILogger logger, string accountName, string banExpiration, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SRPAuth] Blocked login attempt for server host account: {AccountName}")]
    public static partial void LogSRPAuthBlockedServerHost(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[SRPAuth] Authentication success for {AccountName} (ID: #{AccountId})")]
    public static partial void LogSRPAuthSuccess(this ILogger logger, string accountName, int accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SRPAuth] Non-fatal notice: Failed to record session metadata in SQL database for Account #{AccountId}")]
    public static partial void LogSRPAuthFailedSessionSave(this ILogger logger, Exception ex, int accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[SwitchAuth] Received switch_auth request. Cookie: '{CookiePrefix}'")]
    public static partial void LogSwitchAuthReceived(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SwitchAuth] Missing session cookie in request")]
    public static partial void LogSwitchAuthMissingCookie(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SwitchAuth] Invalid or expired session cookie: {CookiePrefix}")]
    public static partial void LogSwitchAuthInvalidCookie(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SwitchAuth] Current authenticated account not found in DB: {AccountName}")]
    public static partial void LogSwitchAuthAccountNotFound(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SwitchAuth] Missing target account login or account_id for user {UserId}")]
    public static partial void LogSwitchAuthMissingTarget(this ILogger logger, int userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SwitchAuth] Target account '{TargetLogin}' (ID: {TargetId}) not found in DB")]
    public static partial void LogSwitchAuthTargetNotFound(this ILogger logger, string targetLogin, int targetId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[SwitchAuth] Unauthorized switch attempt: Account '{TargetAccount}' (User {TargetUserId}) does not belong to User {CurrentUserId}")]
    public static partial void LogSwitchAuthUnauthorizedUser(this ILogger logger, string targetAccount, int targetUserId, int currentUserId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SwitchAuth] Blocked switch attempt for banned/disabled account: {AccountName}")]
    public static partial void LogSwitchAuthBlockedBanned(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[SwitchAuth] Successfully switched user {UserId} from '{OldNick}' to '{NewNick}' (New Cookie: {NewCookiePrefix})")]
    public static partial void LogSwitchAuthSuccess(this ILogger logger, int userId, string oldNick, string newNick, string newCookiePrefix);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ChatRouting] Account: {AccountName}, AccountType: {AccountType}, clientIP: {ClientIP}, HostHeader: {HostHeader}, ResolvedHost: {ResolvedHost}, ResolvedPort: {ResolvedPort}")]
    public static partial void LogChatServerRoutingResolved(this ILogger logger, string accountName, string accountType, string clientIP, string hostHeader, string resolvedHost, int resolvedPort);

    // ClientRequesterController
    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[ClientRequester] Processing '{FunctionName}'. Cookie Prefix: '{CookiePrefix}' (RequiresValidation: {RequiresValidation})")]
    public static partial void LogProcessingRequest(this ILogger logger, string functionName, string cookiePrefix,
        bool requiresValidation);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[ClientRequester] Redis Miss for '{FunctionName}' with cookie prefix '{CookiePrefix}'.")]
    public static partial void LogRedisMiss(this ILogger logger, string functionName, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ClientRequester] Attempting DB Fallback for cookie prefix '{CookiePrefix}'...")]
    public static partial void LogAttemptingDBFallback(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[ClientRequester] DB Hit! Restored Session From Database For Cookie Prefix '{CookiePrefix}' (User: {AccountName})")]
    public static partial void LogDBHit(this ILogger logger, string cookiePrefix, string accountName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[ClientRequester] DB Miss! Cookie prefix '{CookiePrefix}' not found in Accounts table.")]
    public static partial void LogDBMiss(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "IP Address \"{IPAddress}\" Has Made A Client Request With Forged Cookie \"{CookiePrefix}\"")]
    public static partial void LogForgedCookie(this ILogger logger, string ipAddress, string cookiePrefix);

    // IconsController
    [LoggerMessage(Level = LogLevel.Debug, Message = "Icon Not Found: {Path}")]
    public static partial void LogIconNotFound(this ILogger logger, string path);

    // PatcherController
    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Patcher] Request received: OS=\"{OperatingSystem}\", Arch=\"{Architecture}\", CurrentVersion=\"{CurrentPatchVersion}\", CookiePresent={CookiePresent}")]
    public static partial void LogPatcherRequestReceived(
        this ILogger logger,
        string operatingSystem,
        string architecture,
        string currentPatchVersion,
        bool cookiePresent);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[Patcher] Rejected unsupported client distribution: OS=\"{OperatingSystem}\", Arch=\"{Architecture}\", CurrentVersion=\"{CurrentPatchVersion}\"")]
    public static partial void LogPatcherUnsupportedClient(
        this ILogger logger,
        string operatingSystem,
        string architecture,
        string currentPatchVersion);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[Patcher] Current patch details not found: OS=\"{OperatingSystem}\", Arch=\"{Architecture}\", CurrentVersion=\"{CurrentPatchVersion}\"")]
    public static partial void LogPatcherCurrentVersionNotFound(
        this ILogger logger,
        string operatingSystem,
        string architecture,
        string currentPatchVersion);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[Patcher] Dispatched patch metadata: OS=\"{OperatingSystem}\", Arch=\"{Architecture}\", CurrentVersion=\"{CurrentPatchVersion}\" -> LatestVersion=\"{LatestVersion}\", ManifestSHA1=\"{ManifestSHA1}\", ManifestSize={ManifestSize}")]
    public static partial void LogPatcherResponseDispatched(
        this ILogger logger,
        string operatingSystem,
        string architecture,
        string currentPatchVersion,
        string latestVersion,
        string manifestSHA1,
        string manifestSize);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Latest Patch Details Not Found For Distribution \"{OperatingSystem}\"")]
    public static partial void LogLatestPatchNotFound(this ILogger logger, string operatingSystem);

    // ReplayController
    [LoggerMessage(Level = LogLevel.Information, Message = "Saved Replay (Form): {Path}")]
    public static partial void LogSavedReplayForm(this ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved Replay (Raw): {Path}")]
    public static partial void LogSavedReplayRaw(this ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Replay Upload Failed")]
    public static partial void LogUploadFailed(this ILogger logger, Exception ex);

    // QuestController
    [LoggerMessage(Level = LogLevel.Information,
        Message = "[QuestController] GetCurrentQuests called. Cookie present: {CookiePresent}")]
    public static partial void LogGetCurrentQuests(this ILogger logger, bool cookiePresent);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[QuestController] Cookie validation result: IsValid={IsValid}, Account={AccountName}")]
    public static partial void LogCookieValidationResult(this ILogger logger, bool isValid, string? accountName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "[QuestController] Unauthorized access attempt. Invalid cookie prefix: {CookiePrefix}. Proceeding with dummy response to prevent client logout.")]
    public static partial void LogUnauthorizedAccess(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[QuestController] Returning success response: {SerializedResponse}")]
    public static partial void LogSuccessResponse(this ILogger logger, string serializedResponse);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QuestController] Unhandled exception in GetCurrentQuests")]
    public static partial void LogGetCurrentQuestsError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "[QuestController] Unauthorized access attempt (PlayerQuests). Invalid cookie prefix: {CookiePrefix}. Proceeding with dummy response.")]
    public static partial void LogUnauthorizedPlayerQuests(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Error, Message = "[QuestController] Unhandled exception in GetPlayerQuests")]
    public static partial void LogGetPlayerQuestsError(this ILogger logger, Exception ex);

    // ServerRequesterController - Authentication
    [LoggerMessage(Level = LogLevel.Error,
        Message = "[BUG] Remote IP Address For Server Manager With Host Account Name \"{HostAccountName}\" Is NULL")]
    public static partial void LogServerManagerIPNullBug(this ILogger logger, string hostAccountName);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "Server Manager ID \"{MatchServerManagerID}\" Was Registered At \"{MatchServerManagerIPAddress}\" With Cookie Prefix \"{CookiePrefix}\"")]
    public static partial void LogServerManagerRegistered(this ILogger logger, int matchServerManagerID,
        string matchServerManagerIPAddress, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "Server ID \"{MatchServerID}\" Was Registered At \"{MatchServerIPAddress}\":\"{MatchServerPort}\" With Cookie Prefix \"{CookiePrefix}\"")]
    public static partial void LogServerRegistered(this ILogger logger, int matchServerID, string matchServerIPAddress,
        int matchServerPort, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "[BUG] No Account Could Be Found For Account Name \"{AccountName}\" With Session Cookie Prefix \"{CookiePrefix}\"")]
    public static partial void LogAccountNotFoundForCookieBug(this ILogger logger, string accountName,
        string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Message}")]
    public static partial void LogSetOnlineDebug(this ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Debug,
        Message =
            "Received Match Initialisation Heartbeat For Session Prefix \"{SessionPrefix}\", But No MatchStartData Found In Cache. Server Status: {ServerStatus}")]
    public static partial void LogMatchInitHeartbeatBug(this ILogger logger, string sessionPrefix,
        ServerStatus serverStatus);

    [LoggerMessage(Level = LogLevel.Debug,
        Message =
            "Received Match Initialisation Heartbeat For Match ID \"{MatchID}\", But No MatchStartData Found In Cache")]
    public static partial void LogMatchInitHeartbeatMatchIdBug(this ILogger logger, string matchID);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "Captured Match Mode And Options For Match ID {MatchID}: MatchMode={MatchMode}, ArrangedMatchType={ArrangedMatchType}")]
    public static partial void LogCapturedMatchMode(this ILogger logger, string matchID, string matchMode,
        int arrangedMatchType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Account \"{AccountName}\" Is Attempting To Use HTTP Server Authentication")]
    public static partial void LogHttpServerAuthAttempt(this ILogger logger, string accountName);

    // ServerRequesterController - Match
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Match ID {MatchID} Has Started - Host Name: {HostName}, Server ID: {ServerID}, Map: {Map}")]
    public static partial void LogMatchStarted(this ILogger logger, int matchID, string hostName, int serverID,
        string map);

    // ServerRequesterController - Replays
    [LoggerMessage(Level = LogLevel.Error,
        Message = "Missing Value For Form Parameter \"session\" In HandleSetReplaySize")]
    public static partial void LogMissingSessionInSetReplaySize(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No Match Server Found For Session Cookie Prefix \"{SessionPrefix}\" In HandleSetReplaySize")]
    public static partial void LogMatchServerNotFoundForSession(this ILogger logger, string sessionPrefix);

    [LoggerMessage(Level = LogLevel.Error, Message = "Missing Value For Form Parameter \"match_id\"")]
    public static partial void LogMissingMatchId(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Missing Value For Form Parameter \"file_size\"")]
    public static partial void LogMissingFileSize(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Received Replay Size For Match ID {MatchID}: {FileSize} Bytes")]
    public static partial void LogReceivedReplaySize(this ILogger logger, int matchID, int fileSize);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Invalid Match ID Or File Size Format: ID={MatchID}, Size={FileSize}")]
    public static partial void LogInvalidMatchIdOrFileSize(this ILogger logger, string matchID, string fileSize);

    // DistributedCacheExtensions
    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[SessionFallback] DB Hit! Restored Session From Database For Cookie Prefix '{CookiePrefix}' (User: {AccountName})")]
    public static partial void LogSessionFallbackRestored(this ILogger logger, string cookiePrefix, string accountName);

    // QuestController Recovery & MatchServer Session Validation
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[QuestController] Recovered cookie via raw form-body fallback after InvalidDataException on {Endpoint} (Exception: {Message})")]
    public static partial void LogQuestMalformedFormFallback(this ILogger logger, string endpoint, string message);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[QuestController] Validated MatchServer session cookie '{CookiePrefix}' for ServerID {ServerId} on {Endpoint}")]
    public static partial void LogQuestMatchServerValidated(this ILogger logger, string cookiePrefix, int serverId, string endpoint);

    // ServerRequesterController COMPEL/K2 Session Unification
    [LoggerMessage(Level = LogLevel.Information,
        Message = "[NewSession] Preserved existing session cookie '{CookiePrefix}' for ServerID {ServerId} ('{ServerIdentifier}') across COMPEL/K2 handshake.")]
    public static partial void LogNewSessionReusedExistingCookie(this ILogger logger, string cookiePrefix, int serverId, string serverIdentifier);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[NewSession] Evicted superseded session cookie '{OldCookiePrefix}' for ServerID {ServerId} ('{ServerIdentifier}') after cookie rotation.")]
    public static partial void LogNewSessionEvictedSupersededCookie(this ILogger logger, string oldCookiePrefix, int serverId, string serverIdentifier);
}
