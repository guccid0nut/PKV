// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kongor.Core.Security;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Infrastructure;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Models.RequestResponse.ServerRequester;
using KONGOR.MasterServer.Services.Store;
using NEXUS.Contracts.Security;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

public partial class ServerRequesterController
{
    private static readonly FrozenDictionary<string, MatchOptions> MatchOptionMap = new Dictionary<string, MatchOptions>
    {
        { "option[ap]", MatchOptions.AllPick },
        { "option[ar]", MatchOptions.AllRandom },
        { "option[alt_pick]", MatchOptions.AlternateHeroPicking },
        { "option[ab]", MatchOptions.AutoBalanced },
        { "option[br]", MatchOptions.BalancedRandom },
        { "option[veto]", MatchOptions.BanPhase },
        { "option[rapidfire]", MatchOptions.BlitzMode },
        { "option[cas]", MatchOptions.CasualMode },
        { "option[dev_heroes]", MatchOptions.DevelopmentHeroes },
        { "option[drp_itm]", MatchOptions.DropItems },
        { "option[dup_h]", MatchOptions.DuplicateHeroes },
        { "option[em]", MatchOptions.EasyMode },
        { "option[gated]", MatchOptions.Gated },
        { "option[hardcore]", MatchOptions.Hardcore },
        { "option[no_agi]", MatchOptions.NoAgilityHeroes },
        { "option[no_repick]", MatchOptions.NoHeroRepick },
        { "option[no_swap]", MatchOptions.NoHeroSwap },
        { "option[no_int]", MatchOptions.NoIntelligenceHeroes },
        { "option[nl]", MatchOptions.NoLeavers },
        { "option[no_pups]", MatchOptions.NoPowerUps },
        { "option[no_timer]", MatchOptions.NoRespawnTimer },
        { "option[no_stats]", MatchOptions.NoStatistics },
        { "option[no_str]", MatchOptions.NoStrengthHeroes },
        { "option[officl]", MatchOptions.Official },
        { "option[rev_hs]", MatchOptions.ReverseHeroSelection },
        { "option[rs]", MatchOptions.ReverseSelection },
        { "option[shuffleabilities]", MatchOptions.ShuffleAbilities },
        { "option[shuf]", MatchOptions.ShuffleTeams },
        { "option[tr]", MatchOptions.TournamentRules },
        { "option[verified_only]", MatchOptions.VerifiedOnly }
    }.ToFrozenDictionary();

    private async Task<IActionResult> HandleServerManagerAuthentication(CancellationToken cancellationToken = default)
    {
        string? hostAccountName = Request.Form["login"];

        if (hostAccountName is null) return BadRequest(@"Missing Value For Form Parameter ""login""");

        hostAccountName =
            hostAccountName
                .TrimEnd(':'); // The Semicolon Is Used To Separate The Account Name From The Server Instance, So We Need To Remove It Because It Is Not Needed For The Server Manager

        string? accountPasswordHash = Request.Form["pass"];

        if (accountPasswordHash is null) return BadRequest(@"Missing Value For Form Parameter ""pass""");

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "[ReplayAuth] ServerManager auth attempt for login '{HostAccountName}' with pass '{Pass}'",
                hostAccountName, "[REDACTED]");

        Account? account = AccountRepository is MERRICK.DatabaseContext.Repositories.AccountRepository
            ? await AccountRepository.GetAuthIdentityByNicknameAsync(hostAccountName, cancellationToken)
            : await AccountRepository.GetAuthIdentityByNicknameAsync(hostAccountName, cancellationToken)
              ?? await AccountRepository.GetByNicknameAsync(hostAccountName, cancellationToken);
        account ??= await TryEnsureDerivedArcadeHostAccountAsync(hostAccountName, accountPasswordHash, cancellationToken);

        if (account is null)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning("[ReplayAuth] Account '{HostAccountName}' was not found in database.",
                    hostAccountName);

            return NotFound($@"Account ""{hostAccountName}"" Was Not Found");
        }

        if (account.Type is not AccountType.ServerHost)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    "[ReplayAuth] Account '{HostAccountName}' has Type '{Type}', expected ServerHost (2).",
                    hostAccountName, account.Type);

            return Unauthorized($@"Account ""{hostAccountName}"" Is Not A Server Host");
        }

        bool passwordMatches = false;
        if (accountPasswordHash.Length == 64 && accountPasswordHash.All(char.IsAsciiHexDigit))
        {
            string srpSha256 = S2Crypto.ComputeSrpFromPrehashedSha256(accountPasswordHash, account.User.SRPPasswordSalt);
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(srpSha256),
                Encoding.UTF8.GetBytes(account.User.SRPPasswordHash));
        }

        if (!passwordMatches)
        {
            string srpMd5 = S2Crypto.ComputeSrpFromPrehashedMd5(accountPasswordHash, account.User.SRPPasswordSalt);
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(srpMd5),
                Encoding.UTF8.GetBytes(account.User.SRPPasswordHash));
        }

        if (!passwordMatches)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    "[ReplayAuth] Password mismatch for '{HostAccountName}'. Computed: '{Computed}', Expected: '{Expected}'",
                    hostAccountName, "[REDACTED]", "[REDACTED]");

            return Unauthorized("Incorrect Password");
        }

        if (Request.HttpContext.Connection.RemoteIpAddress is null)
        {
            Logger.LogServerManagerIPNullBug(hostAccountName);

            return BadRequest("Unable To Resolve Remote IP Address");
        }

        int managerId = hostAccountName.GetDeterministicInt32Hash();
        await DistributedCache.RemoveMatchServerManagerByID(managerId);

        MatchServerManager matchServerManager = new()
        {
            HostAccountID = account.ID,
            HostAccountName = account.Name,
            ID = managerId,
            MatchServerIDs = [],
            IPAddress = Request.HttpContext.Connection.RemoteIpAddress.MapToIPv4().ToString()
        };

        await DistributedCache.SetMatchServerManager(hostAccountName, matchServerManager);

        string chatServerHost = Environment.GetEnvironmentVariable("CHAT_SERVER_HOST")
                                ?? Environment.GetEnvironmentVariable("ChatServer:Host")
                                ?? "127.0.0.1";

        int chatServerMatchServerManagerConnectionsPort =
            int.TryParse(Environment.GetEnvironmentVariable("CHAT_SERVER_PORT_MATCH_SERVER_MANAGER") ??
                         Environment.GetEnvironmentVariable("ChatServer:MatchServerManagerPort"), out int parsedManagerPort)
                ? parsedManagerPort
                : 11033;

        Dictionary<string, object> response = new()
        {
            ["server_id"] = matchServerManager.ID,
            ["official"] = 1, // If Not Official, It Is Considered To Be Un-Authorized
            ["session"] = matchServerManager.Cookie,
            ["chat_address"] = chatServerHost,
            ["chat_port"] = chatServerMatchServerManagerConnectionsPort,
            // Note(protocol): CDN upload targets configured for dedicated server match replay and dump publication.
            ["cdn_upload_host"] = Configuration.CDN.Host,
            ["cdn_upload_target"] = "upload"
        };

        Logger.LogServerManagerRegistered(matchServerManager.ID, matchServerManager.IPAddress,
            SecurityMasking.MaskToken(matchServerManager.Cookie));

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<IActionResult> HandleServerAuthentication(CancellationToken cancellationToken = default)
    {
        string? serverIdentifier = Request.Form["login"];

        if (serverIdentifier is null) return BadRequest(@"Missing Value For Form Parameter ""login""");

        if (serverIdentifier.Split(':').Length is not 2)
            return BadRequest(@"Missing Or Incorrect Value For Form Parameter ""login""");

        string hostAccountName = serverIdentifier.Split(':').First();
        string serverInstance = serverIdentifier.Split(':').Last();

        if (!int.TryParse(serverInstance, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedServerInstance) ||
            parsedServerInstance <= 0)
        {
            Logger.LogWarning(
                "[NewSession] Rejected new_session for '{ServerIdentifier}': invalid instance '{ServerInstance}' (must be positive integer).",
                serverIdentifier, serverInstance);
            return BadRequest(@"Missing Or Incorrect Value For Form Parameter ""login""");
        }

        string? accountPasswordHash = Request.Form["pass"];

        if (accountPasswordHash is null) return BadRequest(@"Missing Value For Form Parameter ""pass""");

        string? serverPort = Request.Form["port"];

        if (serverPort is null) return BadRequest(@"Missing Value For Form Parameter ""port""");

        string? formLocalPort = Request.Form["local_port"];
        string localPort = !string.IsNullOrEmpty(formLocalPort) ? formLocalPort : serverPort;

        if (!int.TryParse(serverPort, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedServerPort) ||
            parsedServerPort is < 1 or > 65535)
        {
            Logger.LogWarning(
                "[NewSession] Rejected new_session for '{ServerIdentifier}': invalid port '{ServerPort}'.",
                serverIdentifier, serverPort);
            return BadRequest(@"Incorrect Value For Form Parameter ""port""");
        }

        if (!int.TryParse(localPort, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedLocalPort) ||
            parsedLocalPort is < 1 or > 65535)
        {
            Logger.LogWarning(
                "[NewSession] Rejected new_session for '{ServerIdentifier}': invalid local_port '{LocalPort}'.",
                serverIdentifier, localPort);
            return BadRequest(@"Incorrect Value For Form Parameter ""local_port""");
        }

        string? publicPortOverride = Environment.GetEnvironmentVariable("PUBLIC_GAME_SERVER_PORT");
        if (!string.IsNullOrEmpty(publicPortOverride))
        {
            string[] overridePorts = publicPortOverride.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int instanceIndex = parsedServerInstance - 1;

            if (overridePorts.Length > instanceIndex)
                serverPort = overridePorts[instanceIndex];
            else if (overridePorts.Length == 1 && int.TryParse(overridePorts[0], out int parsedBasePort))
                serverPort = (parsedBasePort + instanceIndex).ToString();
            else
                serverPort = overridePorts.FirstOrDefault() ?? serverPort;

            if (int.TryParse(serverPort, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedOverridePort) &&
                parsedOverridePort is >= 1 and <= 65535)
            {
                parsedServerPort = parsedOverridePort;
            }
        }

        string? serverName = Request.Form["name"];

        if (serverName is null) return BadRequest(@"Missing Value For Form Parameter ""name""");

        string? serverDescription = Request.Form["desc"];

        if (serverDescription is null) return BadRequest(@"Missing Value For Form Parameter ""desc""");

        string? serverLocation = Request.Form["location"];

        if (serverLocation is null) return BadRequest(@"Missing Value For Form Parameter ""location""");

        if (string.IsNullOrWhiteSpace(serverLocation))
        {
            serverLocation = "NEWERTH";
        }

        string? serverIPAddress = Request.Form["ip"];

        string? remoteIpAddress = null;
        string? forwardedFor = Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                               ?? Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor) && global::KONGOR.MasterServer.Services.Security.TrustedProxyIpResolver.IsTrustedProxyPeer(Request.HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback))
        {
            string firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (IPAddress.TryParse(firstIp, out IPAddress? parsedForwarded))
            {
                remoteIpAddress = parsedForwarded.IsIPv4MappedToIPv6
                    ? parsedForwarded.MapToIPv4().ToString()
                    : parsedForwarded.ToString();
            }
        }
        remoteIpAddress ??= Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "[NewSession] Authenticating Server. Form IP: {IP} | Context IP (TruePublicIP): {TruePublicIP} (Forwarded: {Forwarded})",
                serverIPAddress, remoteIpAddress, forwardedFor);

        if (string.IsNullOrEmpty(serverIPAddress)) return BadRequest(@"Missing Value For Form Parameter ""ip""");

        string? publicIPOverride = Environment.GetEnvironmentVariable("PUBLIC_GAME_SERVER_IP");
        string localIPAddress = serverIPAddress;
        if (!string.IsNullOrEmpty(publicIPOverride))
        {
            string[] overrideIPs = publicIPOverride.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int instanceIndex = parsedServerInstance - 1;

            if (overrideIPs.Length > instanceIndex)
                serverIPAddress = overrideIPs[instanceIndex];
            else
                serverIPAddress = overrideIPs.FirstOrDefault() ?? publicIPOverride;
        }

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "[NewSession] GameServer new_session attempt for '{ServerIdentifier}' with pass '{Pass}'",
                serverIdentifier, "[REDACTED]");

        Account? account = AccountRepository is MERRICK.DatabaseContext.Repositories.AccountRepository
            ? await AccountRepository.GetAuthIdentityByNicknameAsync(hostAccountName, cancellationToken)
            : await AccountRepository.GetAuthIdentityByNicknameAsync(hostAccountName, cancellationToken)
              ?? await AccountRepository.GetByNicknameAsync(hostAccountName, cancellationToken);
        account ??= await TryEnsureDerivedArcadeHostAccountAsync(hostAccountName, accountPasswordHash, cancellationToken);

        if (account is null)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning("[NewSession] Account '{HostAccountName}' was not found in database.",
                    hostAccountName);

            return NotFound($@"Account ""{hostAccountName}"" Was Not Found");
        }

        if (account.Type is not AccountType.ServerHost)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    "[NewSession] Account '{HostAccountName}' has Type '{Type}', expected ServerHost (2).",
                    hostAccountName, account.Type);

            return Unauthorized($@"Account ""{hostAccountName}"" Is Not A Server Host");
        }

        bool passwordMatches = false;
        if (accountPasswordHash.Length == 64 && accountPasswordHash.All(char.IsAsciiHexDigit))
        {
            string srpSha256 = S2Crypto.ComputeSrpFromPrehashedSha256(accountPasswordHash, account.User.SRPPasswordSalt);
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(srpSha256),
                Encoding.UTF8.GetBytes(account.User.SRPPasswordHash));
        }

        if (!passwordMatches)
        {
            string srpMd5 = S2Crypto.ComputeSrpFromPrehashedMd5(accountPasswordHash, account.User.SRPPasswordSalt);
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(srpMd5),
                Encoding.UTF8.GetBytes(account.User.SRPPasswordHash));
        }

        if (!passwordMatches)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(
                    "[NewSession] Password mismatch for '{HostAccountName}'. Computed: '{Computed}', Expected: '{Expected}'",
                    hostAccountName, "[REDACTED]", "[REDACTED]");

            return Unauthorized("Incorrect Password");
        }

        // Note(protocol): Server binary version negotiation is validated at launcher handshake and compatible across current minor build lines.

        MatchServerManager? matchServerManager =
            (await DistributedCache.GetMatchServerManagersByAccountName(hostAccountName)).FirstOrDefault();

        int serverId = serverIdentifier.GetDeterministicInt32Hash();
        MatchServer? existingServer = await DistributedCache.GetMatchServerByID(serverId);
        if (existingServer != null && existingServer.LocalPort.HasValue && existingServer.Port != existingServer.LocalPort.Value && string.IsNullOrEmpty(formLocalPort))
        {
            // The existing server was registered through a proxy (Port is proxy port, LocalPort is local worker port).
            // This subsequent new_session request is from the raw dedicated server binary (which only knows its local port).
            // Preserve the public proxy port so pings and client connections continue reaching the proxy.
            serverPort = existingServer.Port.ToString();
            localPort = existingServer.LocalPort.Value.ToString();
            parsedServerPort = existingServer.Port;
            parsedLocalPort = existingServer.LocalPort.Value;
        }

        MatchServer matchServer = new()
        {
            HostAccountID = account.ID,
            HostAccountName = account.Name,
            ID = serverId,
            Name = serverName,
            MatchServerManagerID = matchServerManager?.ID,
            Instance = parsedServerInstance,
            IPAddress = serverIPAddress,
            TruePublicIPAddress = remoteIpAddress,
            LocalIPAddress = localIPAddress,
            Port = parsedServerPort,
            LocalPort = parsedLocalPort,
            Location = serverLocation,
            Description = serverDescription,
            Status = existingServer?.Status ?? ServerStatus.SERVER_STATUS_IDLE
        };

        if (existingServer != null)
        {
            long? reqPid = long.TryParse(Request.Form["pid"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedPid) && parsedPid > 0
                ? parsedPid
                : null;
            long? reqProcStartTicks = long.TryParse(Request.Form["process_start_utc"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedStartTicks) && parsedStartTicks > 0
                ? parsedStartTicks
                : null;
            string? reqBootNonce = SanitizeProcessToken(Request.Form["boot_nonce"]);
            bool hasIncomingProcessIdentity = reqPid.HasValue || reqProcStartTicks.HasValue || !string.IsNullOrEmpty(reqBootNonce);

            ProcessRegistrationIdentity? storedProc = await GetProcessIdentityAsync(serverId);
            bool sameHostAndInstance =
                string.Equals(existingServer.HostAccountName, hostAccountName, StringComparison.OrdinalIgnoreCase) &&
                existingServer.Instance == parsedServerInstance;

            bool isK2FollowUpAfterCompelPreReg = false;
            if (string.IsNullOrEmpty(formLocalPort) &&
                existingServer.LocalPort.HasValue &&
                sameHostAndInstance &&
                !string.IsNullOrWhiteSpace(existingServer.Cookie))
            {
                if (storedProc is not null)
                {
                    isK2FollowUpAfterCompelPreReg =
                        !storedProc.Consumed &&
                        string.Equals(storedProc.Cookie, existingServer.Cookie, StringComparison.Ordinal) &&
                        storedProc.MatchesIncoming(reqPid, reqProcStartTicks, reqBootNonce);
                }
                else if (!hasIncomingProcessIdentity)
                {
                    // Legacy mock fallback only when Redis string keys are unavailable and no PID/BootNonce was supplied.
                    isK2FollowUpAfterCompelPreReg =
                        !s_fallbackConsumedCookies.ContainsKey(existingServer.Cookie) &&
                        (TimeProvider.System.GetUtcNow() - existingServer.TimestampRegistered) <= TimeSpan.FromMinutes(2);
                }
            }

            if (isK2FollowUpAfterCompelPreReg)
            {
                matchServer.Cookie = existingServer.Cookie;
                if (storedProc is not null)
                {
                    await SaveProcessIdentityAsync(serverId, storedProc with { Consumed = true });
                }
                else
                {
                    TrackFallbackConsumedCookie(existingServer.Cookie);
                }

                Logger.LogNewSessionReusedExistingCookie(
                    SecurityMasking.MaskToken(matchServer.Cookie),
                    serverId,
                    serverIdentifier);
            }
            else if (!string.Equals(existingServer.Cookie, matchServer.Cookie, StringComparison.Ordinal))
            {
                bool isDifferentProcessIncarnation = storedProc is not null && !storedProc.MatchesIncoming(reqPid, reqProcStartTicks, reqBootNonce);
                await DistributedCache.RemoveMatchServerSessionCookie(existingServer.Cookie);

                if (isDifferentProcessIncarnation || !sameHostAndInstance)
                {
                    // Never grant a grace alias across a process crash/restart boundary or different host/instance.
                    await ClearServerPrevCookieGraceAsync(serverId);
                    Logger.LogWarning(
                        "[NewSession] Evicted superseded cookie '{Cookie}' for Server ID {ServerId} ({ServerIdentifier}) on new process incarnation (PID={Pid}, BootNonce={BootNonce}).",
                        SecurityMasking.MaskToken(existingServer.Cookie), serverId, serverIdentifier, reqPid, reqBootNonce ?? "<none>");
                }
                else if (!string.IsNullOrWhiteSpace(existingServer.Cookie))
                {
                    await StorePrevCookieGraceAsync(serverId, hostAccountName, existingServer.Cookie);
                    Logger.LogNewSessionEvictedSupersededCookie(
                        SecurityMasking.MaskToken(existingServer.Cookie),
                        serverId,
                        serverIdentifier);
                }
                else
                {
                    Logger.LogNewSessionEvictedSupersededCookie(
                        SecurityMasking.MaskToken(existingServer.Cookie),
                        serverId,
                        serverIdentifier);
                }

                if (!string.IsNullOrEmpty(formLocalPort))
                {
                    await SaveProcessIdentityAsync(serverId, new ProcessRegistrationIdentity(
                        reqPid, reqProcStartTicks, reqBootNonce, matchServer.Cookie, Consumed: false));
                }
                else if (storedProc is not null || hasIncomingProcessIdentity)
                {
                    await SaveProcessIdentityAsync(serverId, new ProcessRegistrationIdentity(
                        reqPid ?? storedProc?.Pid,
                        reqProcStartTicks ?? storedProc?.StartTicks,
                        reqBootNonce ?? storedProc?.BootNonce,
                        matchServer.Cookie,
                        Consumed: true));
                }
            }
        }
        else if (!string.IsNullOrEmpty(formLocalPort))
        {
            long? reqPid = long.TryParse(Request.Form["pid"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedPid) && parsedPid > 0
                ? parsedPid
                : null;
            long? reqProcStartTicks = long.TryParse(Request.Form["process_start_utc"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedStartTicks) && parsedStartTicks > 0
                ? parsedStartTicks
                : null;
            string? reqBootNonce = SanitizeProcessToken(Request.Form["boot_nonce"]);
            await SaveProcessIdentityAsync(serverId, new ProcessRegistrationIdentity(
                reqPid, reqProcStartTicks, reqBootNonce, matchServer.Cookie, Consumed: false));
        }

        await DistributedCache.SetMatchServer(hostAccountName, matchServer);

        if (matchServerManager is not null)
        {
            if (!matchServerManager.MatchServerIDs.Contains(matchServer.ID))
                matchServerManager.MatchServerIDs.Add(matchServer.ID);

            await DistributedCache.SetMatchServerManager(hostAccountName, matchServerManager);
        }

        // Note(protocol): COMPEL instances inject verifier signatures into description attributes for trusted server federation.
        // INFO: The Server Manager Doesn't Send Descriptions, So Use Name Instead Since We Can Override Them Anyway Via Remote Command (svr_name) On TCP Handshake

        string chatServerHost = Environment.GetEnvironmentVariable("CHAT_SERVER_HOST")
                                ?? Environment.GetEnvironmentVariable("ChatServer:Host")
                                ?? "127.0.0.1";

        int chatServerMatchServerConnectionsPort =
            int.TryParse(Environment.GetEnvironmentVariable("CHAT_SERVER_PORT_MATCH_SERVER") ??
                         Environment.GetEnvironmentVariable("ChatServer:MatchServerPort"), out int parsedMatchPort)
                ? parsedMatchPort
                : 11032;

        Dictionary<string, object> response = new()
        {
            ["session"] = matchServer.Cookie,
            ["server_id"] = matchServer.ID,
            ["chat_address"] = chatServerHost,
            ["chat_port"] = chatServerMatchServerConnectionsPort,
            ["leaverthreshold"] = 0.05
        };

        Logger.LogServerRegistered(matchServer.ID, matchServer.IPAddress, matchServer.Port,
            SecurityMasking.MaskToken(matchServer.Cookie));

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<IActionResult> HandleConnectClient(CancellationToken cancellationToken = default)
    {
        if (!Request.HasFormContentType) return BadRequest(@"Expected form content type");

        string? session = Request.Form["session"];

        if (session is null) return BadRequest(@"Missing Value For Form Parameter ""session""");

        string? cookie = Request.Form["cookie"];

        if (cookie is null) return BadRequest(@"Missing Value For Form Parameter ""cookie""");

        string? ip = Request.Form["ip"];

        if (ip is null) return BadRequest(@"Missing Value For Form Parameter ""ip""");

        string? casual = Request.Form["cas"];

        if (casual is null) return BadRequest(@"Missing Value For Form Parameter ""cas""");

        // This value is the ChatProtocol.ArrangedMatchType value plus 1. This enum seems to be 1-indexed on the client-side.
        // Example 1: a value of 1 means AM_PUBLIC, which is ChatProtocol.ArrangedMatchType value 0.
        // Example 2: a value of 2 means AM_MATCHMAKING, which is ChatProtocol.ArrangedMatchType value 1.
        string? arrangedMatchType = Request.Form["new"];

        if (arrangedMatchType is null) return BadRequest(@"Missing Value For Form Parameter ""new""");

        string? accountNameForSessionCookie = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountNameForSessionCookie is null) return Unauthorized("No Valid Client Session Cookie Could Be Found");

        Account? account = await AccountRepository.GetByNicknameAsync(accountNameForSessionCookie, cancellationToken);

        if (account is null)
        {
            Logger.LogAccountNotFoundForCookieBug(accountNameForSessionCookie, SecurityMasking.MaskToken(cookie));

            return BadRequest($@"Account With Name ""{accountNameForSessionCookie}"" Could Not Be Found");
        }

        await DistributedCache.StringSetAsync($"ACCOUNT-ID-SESSION-COOKIE:{account.ID}", cookie, TimeSpan.FromHours(24));

        MatchServer? targetMatchServer = await ResolveMatchServerBySessionWithGraceAsync(session, "ServerRequester:c_conn", cancellationToken);
        if (targetMatchServer is not null && targetMatchServer.IsQueueArcadeServer() && account.Type != AccountType.Staff)
        {
            if (string.Equals(arrangedMatchType, "1", StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "[ServerRequester:c_conn] Blocked direct AM_PUBLIC join (new=1) by '{AccountName}' (AccountID={AccountId}) to Queue Arcade Server #{ServerId} ('{ServerName}', HostAccount='{HostAccount}', Instance={Instance}, Port={Port}).",
                    account.Name,
                    account.ID,
                    targetMatchServer.ID,
                    targetMatchServer.Name,
                    targetMatchServer.HostAccountName,
                    targetMatchServer.Instance,
                    targetMatchServer.Port);
                return Unauthorized("Queue Arcade servers only accept players assigned by matchmaking.");
            }

            string allowKey = MatchServer.GetQueueArcadeAllowKey(targetMatchServer.ID, account.ID);
            bool hasArcadeTicket = await DistributedCache.KeyExistsAsync(allowKey);
            if (!hasArcadeTicket)
            {
                Logger.LogWarning(
                    "[ServerRequester:c_conn] Blocked unauthorized join (new={ArrangedMatchType}, missing '{AllowKey}') by '{AccountName}' (AccountID={AccountId}) to Queue Arcade Server #{ServerId} ('{ServerName}', HostAccount='{HostAccount}', Instance={Instance}, Port={Port}).",
                    arrangedMatchType,
                    allowKey,
                    account.Name,
                    account.ID,
                    targetMatchServer.ID,
                    targetMatchServer.Name,
                    targetMatchServer.HostAccountName,
                    targetMatchServer.Instance,
                    targetMatchServer.Port);
                return Unauthorized("Queue Arcade servers only accept players assigned by matchmaking.");
            }

            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(
                    "[ServerRequester:c_conn] Authorized matchmaking Queue Arcade join (new={ArrangedMatchType}, ticket='{AllowKey}') for '{AccountName}' (AccountID={AccountId}) -> Server #{ServerId} (Port={Port}).",
                    arrangedMatchType,
                    allowKey,
                    account.Name,
                    account.ID,
                    targetMatchServer.ID,
                    targetMatchServer.Port);
            }
        }

        if (targetMatchServer is not null)
        {
            MatchStartData? matchData = await DistributedCache.GetMatchStartDataByMatchServerID(targetMatchServer.ID);
            if (matchData is not null) await DistributedCache.AddMatchConnectedPlayer(matchData.MatchID, account.Name);

            try
            {
                await StartMatchHandler.ReclaimEmptyHostedLobbiesForPlayerAsync(account.Name, targetMatchServer.ID, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "[ServerRequester:c_conn] Non-fatal warning while checking abandoned 0-player hosted lobbies for '{AccountName}' moving to Server #{ServerId}.",
                    account.Name,
                    targetMatchServer.ID);
            }
        }

        /*
            public static List<Info> InfoForAccount(AccountDetails accountDetails, float tournamentRatingForActiveTeam)
           {
               Info info = new ()
               {
                   AccountId = accountDetails.AccountId.ToString(),
                   Standing = "3",
                   Level = "1",
                   LevelExp = "0",
                   // AllTimeTotalDisconnects: appears to be ignored
                   // PossibleDisconnects: appears to be ignored
                   // AllTimeGamesPlayed: appears to be ignored
                   // NumBotGamesWon: appears to be ignored
                   PSR = accountDetails.PublicRating,
                   // PublicGameWins = publicStats.Wins,
                   // PublicGameLosses = publicStats.Losses,
                   PublicGamesPlayed = accountDetails.PublicGamesPlayed,
                   PublicGameDisconnects = accountDetails.PublicTimesDisconnected,
                   // NormalRankedGamesMMR: unused in KONGOR
                   // NormalRankedGameWins: unused in KONGOR
                   // NormalRankedGameLosses: unused in KONGOR
                   // NormalRankedGamesPlayed: unused in KONGOR
                   // NormalRankedGameDisconnects: unused in KONGOR
                   // CasualModeMMR: unused in KONGOR
                   // CasualModeWins: unused in KONGOR
                   // CasualModeLosses: unused in KONGOR
                   // CasualModeGamesPlayed: unused in KONGOR
                   // CasualModeDisconnects: unused in KONGOR
                   MidWarsMMR = accountDetails.MidWarsRating,
                   MidWarsGamesPlayed = accountDetails.MidWarsGamesPlayed,
                   MidWarsTimesDisconnected = accountDetails.MidWarsTimesDisconnected,

                   // Number of Tournament matches played. Note: rift wars is used as a piggy-back.
                   RiftWarsGamesPlayed = accountDetails.TournamentGamesPlayed,
                   RiftWarsDisconnects = accountDetails.TournamentTimesDisconnected,
                   RiftWarsRating = tournamentRatingForActiveTeam,

                   IsNew = 0,
                   ChampionsOfNewerthNormalMMR = accountDetails.CoNNormalRating,
                   ChampionsOfNewerthNormalRank = accountDetails.CoNNormalRank,
                   ChampionsOfNewerthGamesPlayed = accountDetails.CoNNormalGamesPlayed,
                   ChampionsOfNewerthGameDisconnects = accountDetails.CoNNormalTimesDisconnected,

                   ChampionsOfNewerthCasualMMR = accountDetails.CoNCasualRating,
                   ChampionsOfNewerthCasualRank = accountDetails.CoNCasualRank,
                   ChampionsOfNewerthCasualGamesPlayed = accountDetails.CoNCasualGamesPlayed,
                   ChampionsOfNewerthCasualGameDisconnects = accountDetails.CoNCasualTimesDisconnected,

                   // Additional Public Games info requested by server_requester.php?f=c_conn
                   // Unclear if used or not.
                   // PublicHeroKills = account.PlayerSeasonStatsPublic.HeroKills,
                   // PublicHeroAssists = account.PlayerSeasonStatsPublic.HeroAssists,
                   // PublicDeaths = account.PlayerSeasonStatsPublic.Deaths,
                   // PublicWardsPlaced = account.PlayerSeasonStatsPublic.Wards,
                   // PublicGoldEarned = account.PlayerSeasonStatsPublic.Gold,
                   // PublicExpEarned = account.PlayerSeasonStatsPublic.Exp,
                   // PublicSecondsPlayed = account.PlayerSeasonStatsPublic.Secs,
                   // PublicTimeEarningExp = account.PlayerSeasonStatsPublic.TimeEarningExp,

                   // Additional TMM info requested by server_requester.php?f=c_conn

                   // Additional unknown fields requested by server_requester.php?f=c_conn
                   // Unclear if used or not.
                   rnk_amm_solo_conf = 0,
                   rnk_amm_team_conf = 0,
               };

               return new List<Info>() { info };
           }
         */

        ConnectClientResponse response = new()
        {
            Cookie = cookie,
            AccountID = account.ID,
            Nickname = account.Name,
            SuperID = account.User.Accounts.FirstOrDefault(record => record.IsMain)?.ID ?? account.ID,
            AccountType = (int)account.Type,
            Level = account.User.TotalLevel,
            ClanID = account.Clan?.ID,
            ClanTag = account.Clan?.Tag,
            GameCookie = cookie,
            Standing = "3",
            IsLegacy = "1",
            GameTokens = 999999,
            DiceTokens = 999999,
            MyUpgrades = UpgradesResolutionService.ExpandOwnedItems(account.User.InventoryItems?.Select(i => i.ItemCode) ?? account.User.OwnedStoreItems).Append("m.allmodes.pass").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SelectedUpgrades = account.SelectedStoreItemCodes
        };

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<IActionResult> HandleAcceptKey(CancellationToken cancellationToken = default)
    {
        string? session = Request.Form["session"];

        if (session is null)
            return BadRequest(@"Missing Value For Form Parameter ""session""");

        string? accountKey = Request.Form["acc_key"];

        if (accountKey is null)
            return BadRequest(@"Missing Value For Form Parameter ""acc_key""");

        MatchServer? matchServer = await ResolveMatchServerBySessionWithGraceAsync(session, "ServerRequester:AcceptKey", cancellationToken);

        if (matchServer is null)
        {
            Logger.LogWarning(
                "[ServerRequester:AcceptKey] Unauthorized: No Match Server found for session cookie '{Session}'",
                SecurityMasking.MaskToken(session));
            return Unauthorized($@"No Match Server Could Be Found For Session Cookie ""{SecurityMasking.MaskToken(session)}""");
        }

        int serverId = matchServer.ID;
        int official = !matchServer.IsPrivate ? 1 : 0; // 0 = Unofficial; 1 = Official With Stats; 2 = Official Without Stats;

        Dictionary<string, object> response = new()
        {
            { "server_id", serverId },
            { "official", official }
        };

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[ServerRequester] HandleAcceptKey: session={Session}, acc_key={AccountKey}, resolved server_id={ServerId}, official={Official}",
                SecurityMasking.MaskToken(session), SecurityMasking.MaskToken(accountKey), serverId, official);
        }

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<IActionResult> HandleSetOnline(CancellationToken cancellationToken = default)
    {
        IFormCollection form;
        try
        {
            if (!Request.HasFormContentType) return BadRequest("Invalid Form Content-Type");

            form = await Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            Logger.LogWarning(ex,
                "[SetOnline] Unexpected end of request content (client/dserver disconnected prematurely).");
            return BadRequest("Unexpected end of request content.");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "[SetOnline] Could not parse form payload.");
            return BadRequest("Invalid Form Content-Type");
        }

        // Debug Logging for SetOnline (single line, sensitive fields redacted)
        if (Logger.IsEnabled(LogLevel.Debug))
        {
            List<string> pairs = new(form.Keys.Count);
            foreach (string key in form.Keys)
            {
                pairs.Add($"{key}={RequestLoggingMiddleware.FormatSensitiveValue(key, form[key].ToString())}");
            }

            Logger.LogDebug("[SetOnline] Keys: {KeysSummary}", string.Join(", ", pairs));
        }

        string? session = form["session"];

        if (session is null) return BadRequest(@"Missing Value For Form Parameter ""session""");

        string? connectionsCount = form["num_conn"];

        if (connectionsCount is null) return BadRequest(@"Missing Value For Form Parameter ""num_conn""");

        string? gameTime = form["cgt"];

        if (gameTime is null) return BadRequest(@"Missing Value For Form Parameter ""cgt""");

        string? map = form["map"];

        if (map is null) return BadRequest(@"Missing Value For Form Parameter ""map""");

        string? isPrivate = form["private"];

        if (isPrivate is null) return BadRequest(@"Missing Value For Form Parameter ""private""");

        string? isVIP = form["vip"];

        if (isVIP is null) return BadRequest(@"Missing Value For Form Parameter ""vip""");

        string? connectionState = form["c_state"];

        if (connectionState is null) return BadRequest(@"Missing Value For Form Parameter ""c_state""");

        string? previousConnectionState = form["prev_c_state"];

        if (previousConnectionState is null) return BadRequest(@"Missing Value For Form Parameter ""prev_c_state""");

        // Note(cache): Server-to-Manager associations are established during session creation.
        // Distributed cache TTL renewal is evaluated upon c_state heartbeat transitions.

        MatchServer? matchServer = await ResolveMatchServerBySessionWithGraceAsync(session, "SetOnline", cancellationToken);

        if (matchServer is null)
        {
            Logger.LogWarning(
                "[SetOnline] Unauthorized: No Match Server found for session cookie '{Session}'",
                SecurityMasking.MaskToken(session));
            return Unauthorized($@"No Match Server Could Be Found For Session Cookie ""{SecurityMasking.MaskToken(session)}""");
        }

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        ServerStatus prevStatus = matchServer.Status;
        int prevConns = matchServer.ConnectedPlayersCount;
        string? prevMap = matchServer.Description;
        string? prevName = matchServer.Name;
        bool isAutoResurrected = now - matchServer.TimestampLastHeartbeat > TimeSpan.FromMinutes(2);
        if (isAutoResurrected)
        {
            Logger.LogInformation(
                "[SetOnline] Auto-resurrected match server ID {ServerId} ({HostAccountName}:{Port}) for session '{Session}'",
                matchServer.ID, matchServer.HostAccountName, matchServer.Port, SecurityMasking.MaskToken(session));
        }

        if (Enum.TryParse<ServerStatus>(connectionState, true, out ServerStatus parsedStatus))
        {
            matchServer.Status = parsedStatus;
        }
        else if (int.TryParse(connectionState, out int intStatus) && Enum.IsDefined(typeof(ServerStatus), intStatus))
        {
            matchServer.Status = (ServerStatus)intStatus;
        }

        // If clients are connected, the server is actively hosting a lobby or match; it is never IDLE.
        if (int.TryParse(connectionsCount, out int conns))
        {
            matchServer.ConnectedPlayersCount = conns;
            if (conns > 0 && matchServer.Status == ServerStatus.SERVER_STATUS_IDLE)
            {
                matchServer.Status = ServerStatus.SERVER_STATUS_ACTIVE;
            }
        }

        string? matchName = form["mname"];
        if (!string.IsNullOrWhiteSpace(matchName))
        {
            matchServer.Name = matchName;
        }
        if (!string.IsNullOrWhiteSpace(map))
        {
            matchServer.Description = map;
        }

        matchServer.TimestampLastHeartbeat = now;

        await DistributedCache.SetMatchServer(matchServer.HostAccountName, matchServer);

        // The "new" Parameter Being Present Indicates This Is A Match Initialisation Heartbeat
        string? newMatch = form["new"];

        bool isStateTransition =
            isAutoResurrected ||
            newMatch is not null ||
            matchServer.Status != prevStatus ||
            matchServer.ConnectedPlayersCount != prevConns ||
            !string.Equals(matchServer.Description, prevMap, StringComparison.Ordinal) ||
            !string.Equals(matchServer.Name, prevName, StringComparison.Ordinal);

        if (isStateTransition || RequestLoggingMiddleware.IsVerboseHeartbeatsEnabled())
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(
                    "[SetOnline] Heartbeat state transition for Server ID {ServerId} ({HostAccountName}:{Port}): Status {PrevStatus}->{Status}, Players {PrevConns}->{Conns}, Match '{MatchName}', Map '{Map}', New={IsNew}, AutoResurrected={AutoResurrected}",
                    matchServer.ID, matchServer.HostAccountName, matchServer.Port,
                    prevStatus, matchServer.Status,
                    prevConns, matchServer.ConnectedPlayersCount,
                    matchServer.Name, matchServer.Description,
                    newMatch is not null, isAutoResurrected);
            }
        }
        else if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                "[SetOnline] Heartbeat unchanged for Server ID {ServerId} ({HostAccountName}:{Port}) -> Status: {Status}, Players: {Conns}, Match: '{MatchName}', Map: '{Map}'",
                matchServer.ID, matchServer.HostAccountName, matchServer.Port,
                matchServer.Status, matchServer.ConnectedPlayersCount, matchServer.Name, matchServer.Description);
        }

        RecordSetOnlineHeartbeatAndMaybeEmitSummary(now, isStateTransition);

        if (newMatch is null)
        {
            MatchStartData? matchStartData =
                await DistributedCache.GetMatchStartDataByMatchServerSessionCookie(matchServer.Cookie);

            if (matchStartData is null)
            {
                // Only Log This Error If The Server Is In A State Where It Should Have Match Data (i.e. Not Idle, Sleeping, Or Unknown)
                if (matchServer.Status is not ServerStatus.SERVER_STATUS_IDLE and
                    not ServerStatus.SERVER_STATUS_SLEEPING and
                    not ServerStatus.SERVER_STATUS_UNKNOWN)
                    Logger.LogMatchInitHeartbeatBug(SecurityMasking.MaskToken(session), matchServer.Status);

                return Ok();
            }

            matchStartData.ConnectedPlayersCount = int.Parse(connectionsCount);
            matchStartData.Map = map;
        }

        if (newMatch is not null)
        {
            string? matchID = form["match_id"];

            if (matchID is null) return BadRequest(@"Invalid Value For Form Parameter ""match_id""");

            if (!int.TryParse(matchID, out int parsedMatchId) || parsedMatchId <= 0)
            {
                Logger.LogMatchInitHeartbeatMatchIdBug(matchID);

                return Ok();
            }

            MatchStartData? matchStartData = await DistributedCache.GetMatchStartData(parsedMatchId);

            if (matchStartData is null)
            {
                Logger.LogMatchInitHeartbeatMatchIdBug(matchID);

                return Ok();
            }

            if (int.TryParse(connectionsCount, out int parsedConnCount))
            {
                matchStartData.ConnectedPlayersCount = parsedConnCount;
            }
            matchStartData.Map = map;

            string? maximumPlayersCount = form["max_players"];

            if (maximumPlayersCount is null) return BadRequest(@"Invalid Value For Form Parameter ""max_players""");

            if (int.TryParse(maximumPlayersCount, out int parsedMaxPlayers))
            {
                matchStartData.MaximumPlayersCount = parsedMaxPlayers;
            }

            string? league = form["league"];

            if (league is null) return BadRequest(@"Invalid Value For Form Parameter ""league""");

            if (int.TryParse(league, out int parsedLeague))
            {
                matchStartData.League = parsedLeague;
            }

            string? matchMode = form["mode"];

            if (matchMode is null) return BadRequest(@"Invalid Value For Form Parameter ""mode""");

            // Fallback: If it's not an int, assume it's already a code (e.g. from local testing)
            matchStartData.MatchMode = !int.TryParse(matchMode, out int parsedMatchMode)
                ? matchMode
                : MatchModeDefinition.GetCodeFromId(parsedMatchMode);

            string? initMatchName = form["mname"];

            if (initMatchName is null) return BadRequest(@"Invalid Value For Form Parameter ""mname""");

            matchStartData.MatchName = initMatchName;

            MatchOptions options = MatchOptions.None;

            foreach ((string key, MatchOptions flag) in MatchOptionMap)
                if (form.ContainsKey(key))
                    options |= flag;

            matchStartData.Options = options;

            await DistributedCache.SetMatchStartData(matchStartData);

            Logger.LogCapturedMatchMode(matchID, matchStartData.MatchMode, matchStartData.MatchType);
        }

        return Ok();
    }

    private Task<IActionResult> HandleAuthentication(CancellationToken cancellationToken = default)
    {
        string? accountName = Request.Form["login"];

        if (accountName is not null) Logger.LogHttpServerAuthAttempt(accountName);

        string response =
            PhpSerialization.Serialize(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.SRPAuthenticationDisabled));

        return Task.FromResult<IActionResult>(BadRequest(response));
    }

    private async Task<Account?> TryEnsureDerivedArcadeHostAccountAsync(
        string hostAccountName,
        string providedPasswordHash,
        CancellationToken cancellationToken)
    {
        const string arcadeSuffix = "_arc";
        if (hostAccountName.Length > 15 ||
            !hostAccountName.EndsWith(arcadeSuffix, StringComparison.OrdinalIgnoreCase) ||
            hostAccountName.Length <= arcadeSuffix.Length)
        {
            return null;
        }

        string baseHostName = hostAccountName[..^arcadeSuffix.Length];
        Account? baseHost = await AccountRepository.GetForAuthenticationAsync(baseHostName, cancellationToken);
        const string localSuffix = "_local";
        if (baseHost is null && baseHostName.Length + localSuffix.Length <= 15)
        {
            baseHost = await AccountRepository.GetForAuthenticationAsync($"{baseHostName}{localSuffix}", cancellationToken);
        }

        if (baseHost is null && baseHostName.Length >= 10)
        {
            baseHost = await AccountRepository.FindBaseServerHostByPrefixAsync(baseHostName, cancellationToken);
        }

        if (baseHost?.User is null || baseHost.Type != AccountType.ServerHost || baseHost.IsBanned)
        {
            return null;
        }

        bool passwordMatches = false;
        if (providedPasswordHash.Length == 64 && providedPasswordHash.All(char.IsAsciiHexDigit))
        {
            string srpSha256 = S2Crypto.ComputeSrpFromPrehashedSha256(providedPasswordHash, baseHost.User.SRPPasswordSalt);
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(srpSha256),
                Encoding.UTF8.GetBytes(baseHost.User.SRPPasswordHash));
        }

        if (!passwordMatches)
        {
            string srpMd5 = S2Crypto.ComputeSrpFromPrehashedMd5(providedPasswordHash, baseHost.User.SRPPasswordSalt);
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(srpMd5),
                Encoding.UTF8.GetBytes(baseHost.User.SRPPasswordHash));
        }

        if (!passwordMatches)
        {
            return null;
        }

        Account? provisionedArcade = await AccountRepository.ProvisionDerivedArcadeHostAccountAsync(
            hostAccountName,
            baseHost,
            cancellationToken);
        if (provisionedArcade is null)
        {
            return null;
        }

        Account arcadeAccount = provisionedArcade;
        Logger.LogInformation(
            "[QueueArcadeAuth] Auto-provisioned dedicated Queue Arcade ServerHost account '{ArcadeHost}' from verified base host '{BaseHost}'",
            hostAccountName,
            baseHost.Name);

        return arcadeAccount;
    }

    private const string PrevCookieKeyPrefix = "MATCH-SERVER:PREV-COOKIE:";
    private const string ServerPrevCookieAliasKeyPrefix = "MATCH-SERVER:SERVER-PREV-COOKIE:";
    private const string ProcessIdentityKeyPrefix = "MATCH-SERVER:PROC-ID:";
    private static readonly TimeSpan PrevCookieGraceTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProcessIdentityTtl = TimeSpan.FromHours(2);

    private static readonly ConcurrentDictionary<string, byte> s_fallbackConsumedCookies = new(StringComparer.Ordinal);
    private static long s_heartbeatWindowTotalCount;
    private static long s_heartbeatWindowTransitionCount;
    private static long s_lastHeartbeatSummaryTicks;

    internal sealed record ProcessRegistrationIdentity(
        long? Pid,
        long? StartTicks,
        string? BootNonce,
        string Cookie,
        bool Consumed)
    {
        public bool MatchesIncoming(long? incomingPid, long? incomingStartTicks, string? incomingBootNonce)
        {
            if (!string.IsNullOrEmpty(BootNonce) && !string.IsNullOrEmpty(incomingBootNonce))
            {
                if (!string.Equals(BootNonce, incomingBootNonce, StringComparison.Ordinal))
                    return false;
            }

            if (Pid.HasValue && incomingPid.HasValue && Pid.Value != incomingPid.Value)
                return false;

            if (StartTicks.HasValue && incomingStartTicks.HasValue && StartTicks.Value != incomingStartTicks.Value)
                return false;

            return true;
        }

        public string Serialize() =>
            string.Join('|',
                Pid?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                StartTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                BootNonce ?? string.Empty,
                Cookie,
                Consumed ? "1" : "0");

        public static ProcessRegistrationIdentity? Deserialize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] parts = raw.Split('|');
            if (parts.Length < 5) return null;

            long? pid = long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long p) ? p : null;
            long? startTicks = long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long st) ? st : null;
            string? bootNonce = string.IsNullOrEmpty(parts[2]) ? null : parts[2];
            string cookie = parts[3];
            if (string.IsNullOrWhiteSpace(cookie)) return null;
            bool consumed = parts[4] == "1";
            return new ProcessRegistrationIdentity(pid, startTicks, bootNonce, cookie, consumed);
        }
    }

    private static string? SanitizeProcessToken(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().Replace("|", string.Empty, StringComparison.Ordinal);

    private static void TrackFallbackConsumedCookie(string cookie)
    {
        if (s_fallbackConsumedCookies.Count > 2048)
        {
            s_fallbackConsumedCookies.Clear();
        }
        s_fallbackConsumedCookies[cookie] = 1;
    }

    private async Task<ProcessRegistrationIdentity?> GetProcessIdentityAsync(int serverId)
    {
        RedisValue raw = await DistributedCache.StringGetAsync($"{ProcessIdentityKeyPrefix}{serverId}");
        return raw.IsNullOrEmpty ? null : ProcessRegistrationIdentity.Deserialize(raw.ToString());
    }

    private Task SaveProcessIdentityAsync(int serverId, ProcessRegistrationIdentity identity) =>
        DistributedCache.StringSetAsync($"{ProcessIdentityKeyPrefix}{serverId}", identity.Serialize(), ProcessIdentityTtl);

    private async Task StorePrevCookieGraceAsync(int serverId, string hostAccountName, string oldCookie)
    {
        await ClearServerPrevCookieGraceAsync(serverId);

        string safeHost = SanitizeProcessToken(hostAccountName) ?? string.Empty;
        string payload = $"{serverId.ToString(CultureInfo.InvariantCulture)}|{safeHost}";
        await DistributedCache.StringSetAsync($"{PrevCookieKeyPrefix}{oldCookie}", payload, PrevCookieGraceTtl);
        await DistributedCache.StringSetAsync($"{ServerPrevCookieAliasKeyPrefix}{serverId}", oldCookie, PrevCookieGraceTtl);
    }

    private async Task ClearServerPrevCookieGraceAsync(int serverId)
    {
        string serverAliasKey = $"{ServerPrevCookieAliasKeyPrefix}{serverId}";
        RedisValue prevCookieVal = await DistributedCache.StringGetAsync(serverAliasKey);
        if (!prevCookieVal.IsNullOrEmpty)
        {
            string prevCookie = prevCookieVal.ToString();
            if (!string.IsNullOrWhiteSpace(prevCookie))
            {
                await DistributedCache.KeyDeleteAsync($"{PrevCookieKeyPrefix}{prevCookie}");
            }
            await DistributedCache.KeyDeleteAsync(serverAliasKey);
        }
    }

    internal async Task<MatchServer?> ResolveMatchServerBySessionWithGraceAsync(
        string session,
        string endpointName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session))
            return null;

        MatchServer? matchServer = await DistributedCache.GetMatchServerBySessionCookie(session)
                                   ?? await DistributedCache.ResurrectMatchServerBySession(session);
        if (matchServer is not null)
        {
            if (string.Equals(session, matchServer.Cookie, StringComparison.Ordinal))
            {
                await ClearServerPrevCookieGraceAsync(matchServer.ID);
            }
            return matchServer;
        }

        string graceKey = $"{PrevCookieKeyPrefix}{session}";
        RedisValue graceRaw = await DistributedCache.StringGetAsync(graceKey);
        if (graceRaw.IsNullOrEmpty)
            return null;

        bool deleted = await DistributedCache.KeyDeleteAsync(graceKey);
        if (!deleted)
            return null;

        string[] parts = graceRaw.ToString().Split('|', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int expectedServerId))
            return null;

        string expectedHostAccountName = parts[1];
        MatchServer? currentServer = await DistributedCache.GetMatchServerByID(expectedServerId);
        if (currentServer is null ||
            currentServer.ID != expectedServerId ||
            !string.Equals(currentServer.HostAccountName, expectedHostAccountName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(
                "[{Endpoint}] Rejected single-use previous-cookie grace alias '{OldSession}': target Server ID {ServerId} ({HostAccountName}) missing or ownership mismatch.",
                endpointName, SecurityMasking.MaskToken(session), expectedServerId, expectedHostAccountName);
            return null;
        }

        await DistributedCache.KeyDeleteAsync($"{ServerPrevCookieAliasKeyPrefix}{expectedServerId}");
        Logger.LogWarning(
            "[{Endpoint}] Accepted single-use 45s previous-cookie grace alias '{OldSession}' -> current session '{NewSession}' for Server ID {ServerId} ({HostAccountName}:{Port}).",
            endpointName,
            SecurityMasking.MaskToken(session),
            SecurityMasking.MaskToken(currentServer.Cookie),
            currentServer.ID,
            currentServer.HostAccountName,
            currentServer.Port);
        return currentServer;
    }

    internal static (bool ShouldEmit, long TotalCount, long TransitionCount) TryAdvanceHeartbeatSummaryWindow(
        DateTimeOffset nowUtc,
        bool isTransition,
        ref long totalCounter,
        ref long transitionCounter,
        ref long lastSummaryTicks)
    {
        Interlocked.Increment(ref totalCounter);
        if (isTransition)
        {
            Interlocked.Increment(ref transitionCounter);
        }

        long nowTicks = nowUtc.UtcTicks;
        long previousTicks = Interlocked.Read(ref lastSummaryTicks);
        if (previousTicks == 0)
        {
            Interlocked.CompareExchange(ref lastSummaryTicks, nowTicks, 0);
            return (false, 0, 0);
        }

        long windowTicks = TimeSpan.FromSeconds(60).Ticks;
        if (nowTicks - previousTicks < windowTicks)
        {
            return (false, 0, 0);
        }

        if (Interlocked.CompareExchange(ref lastSummaryTicks, nowTicks, previousTicks) != previousTicks)
        {
            return (false, 0, 0);
        }

        long total = Interlocked.Exchange(ref totalCounter, 0);
        long transitions = Interlocked.Exchange(ref transitionCounter, 0);
        return (true, total, transitions);
    }

    private void RecordSetOnlineHeartbeatAndMaybeEmitSummary(DateTimeOffset nowUtc, bool isTransition)
    {
        (bool shouldEmit, long total, long transitions) = TryAdvanceHeartbeatSummaryWindow(
            nowUtc,
            isTransition,
            ref s_heartbeatWindowTotalCount,
            ref s_heartbeatWindowTransitionCount,
            ref s_lastHeartbeatSummaryTicks);

        if (shouldEmit && Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[HeartbeatSummary] 60s window: set_online={TotalHeartbeats}, transitions={Transitions}",
                total, transitions);
        }
    }
}
