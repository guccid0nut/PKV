// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Collections.Frozen;
using KONGOR.MasterServer.Features.Matches.StartMatch;
using KONGOR.MasterServer.Helpers.Serialisation;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

[ApiController]
[Route("server_requester.php")]
public partial class ServerRequesterController(
    IAccountRepository accountRepository,
    IMatchHistoryRepository matchHistoryRepository,
    IDatabase distributedCache,
    ILogger<ServerRequesterController> logger,
    IOptions<OperationalConfiguration> configuration,
    IStartMatchHandler? startMatchHandler = null,
    IUpgradesResolutionService? upgradesResolver = null,
    IWebHostEnvironment? hostEnvironment = null,
    IConfiguration? rawConfiguration = null) : ControllerBase
{
    public static readonly FrozenSet<string> SupportedServerFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "replay_auth",
        "get_spectator_header",
        "set_replay_size",
        "get_quickstats",
        "accept_key",
        "auth",
        "c_conn",
        "new_session",
        "set_online",
        "shutdown",
        "start_game",
        "abandon_match",
        "cancel_match",
        "live_tick",
        "match_telemetry",
        "aids2cookie",
        "get_upgrades"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;
    private OperationalConfiguration Configuration { get; } = configuration.Value;
    private IWebHostEnvironment? HostEnvironment { get; } = hostEnvironment;
    private IConfiguration? RawConfiguration { get; } = rawConfiguration;

    private IStartMatchHandler StartMatchHandler { get; } =
        startMatchHandler ?? new StartMatchHandler(distributedCache, NullLogger<StartMatchHandler>.Instance);

    private IUpgradesResolutionService UpgradesResolver { get; } =
        upgradesResolver ?? new UpgradesResolutionService(NullLogger<UpgradesResolutionService>.Instance);

    private bool IsDevelopmentEnvironment() => HostEnvironment?.IsDevelopment() ?? false;

    private bool IsLocalDevelopmentLoopback()
    {
        if (!IsDevelopmentEnvironment()) return false;
        IPAddress? remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp is null || IPAddress.IsLoopback(remoteIp);
    }

    private bool IsAuthorizedInternalService()
    {
        if (!Request.Headers.TryGetValue(SecurityConstants.InternalServiceKeyHeader, out StringValues headerValues) ||
            StringValues.IsNullOrEmpty(headerValues))
        {
            return false;
        }

        return KONGOR.IsAuthorizedRenderCallback(
            headerValues.ToString(),
            RawConfiguration?["InternalServiceKey"],
            IsDevelopmentEnvironment(),
            Logger,
            HttpContext.Connection.RemoteIpAddress?.ToString());
    }

    private string? ExtractServerSessionToken()
    {
        string? session = null;
        if (Request.HasFormContentType)
        {
            session = Request.Form["session"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(session))
        {
            session = Request.Query["session"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(session) &&
            Request.Headers.TryGetValue(ReplayUploadAuthorizer.ServerSessionHeader, out StringValues headerSession) &&
            !StringValues.IsNullOrEmpty(headerSession))
        {
            session = headerSession.ToString();
        }

        return session?.Trim();
    }

    [HttpPost(Name = "Server Requester All-In-One")]
    [HttpGet]
    [HttpHead]
    public async Task<IActionResult> ServerRequester(CancellationToken cancellationToken = default)
    {
        return await HandleServerRequest(cancellationToken);
    }

    private async Task<IActionResult> HandleServerRequest(CancellationToken cancellationToken = default)
    {
        // Unified Query/Form coalescing pattern for functional routing parity.
        // 2026-01-07: Normalize to lowercase for case-insensitive handling (PHP parity).
        string? functionName = null;
        try
        {
            functionName = Request.Query["f"].FirstOrDefault();
            if (string.IsNullOrEmpty(functionName) && Request.HasFormContentType)
                functionName = Request.Form["f"].FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ServerRequester] Failed to parse form content for request.");
        }

        functionName = functionName?.ToLowerInvariant();

        return functionName switch
        {
            // server manager
            "replay_auth" => await HandleServerManagerAuthentication(cancellationToken),
            "get_spectator_header" => await HandleGetSpectatorHeader(cancellationToken),
            "set_replay_size" => await HandleSetReplaySize(cancellationToken),
            "get_quickstats" => await HandleGetQuickStats(cancellationToken),

            // server
            "accept_key" => await HandleAcceptKey(cancellationToken),
            "auth" => await HandleAuthentication(cancellationToken),
            "c_conn" => await HandleConnectClient(cancellationToken),
            "new_session" => await HandleServerAuthentication(cancellationToken),
            "set_online" => await HandleSetOnline(cancellationToken),
            "shutdown" => await HandleShutdown(cancellationToken),
            "start_game" => await HandleMatchStart(cancellationToken),
            "abandon_match" or "cancel_match" => await HandleMatchAbandon(cancellationToken),
            "live_tick" or "match_telemetry" => await HandleLiveTick(cancellationToken),
            "aids2cookie" => await HandleGetAccountIdFromCookie(cancellationToken),
            "get_upgrades" => await HandleGetServerUpgrades(cancellationToken),

            // default
            _ => HandleUnsupportedServerRequest(functionName)
        };
    }

    private IActionResult HandleUnsupportedServerRequest(string? functionName)
    {
        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning(
                "[ServerRequester] Unsupported Server Requester function parameter: f={FunctionName}. Returning safe PHP serialized boolean false (b:0;) to preserve client compatibility.",
                functionName ?? "<null>");
        }

        return Ok(PhpSerialization.Serialize(false));
    }

    private async Task<IActionResult> HandleGetAccountIdFromCookie(CancellationToken cancellationToken = default)
    {
        // Dedicated game server sends session + aids[] (or aids%5B%5D) containing account IDs to get session cookies for player authentication.
        // Require form content and a registered MatchServer session cookie before resolving any player session cookies.
        if (!Request.HasFormContentType)
        {
            Logger.LogWarning("[ServerRequester:aids2cookie] Rejected aids2cookie request without form content type.");
            return BadRequest("Invalid Form Content-Type");
        }

        string? session = Request.Form["session"];
        if (string.IsNullOrWhiteSpace(session))
        {
            Logger.LogWarning("[ServerRequester:aids2cookie] Rejected aids2cookie request missing 'session' form parameter.");
            return BadRequest(@"Missing Value For Form Parameter ""session""");
        }

        MatchServer? matchServer = await DistributedCache.GetMatchServerBySessionCookie(session);
        if (matchServer is null)
        {
            matchServer = await DistributedCache.ResurrectMatchServerBySession(session);
        }

        if (matchServer is null)
        {
            Logger.LogWarning(
                "[ServerRequester:aids2cookie] Unauthorized: No Match Server found for session cookie '{Session}'",
                SecurityMasking.MaskToken(session));
            // Plaintext 401 (never "auth_failure", which triggers a CGameServer pre-match frame use-after-free) and never echoing raw session.
            return Unauthorized("No Match Server Could Be Found For Provided Session Cookie");
        }

        List<string> accountIdStrings = [];
        if (Request.HasFormContentType)
        {
            foreach (string key in Request.Form.Keys)
            {
                if (key.StartsWith("aids", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string? val in Request.Form[key])
                    {
                        if (!string.IsNullOrWhiteSpace(val))
                            accountIdStrings.Add(val.Trim());
                    }
                }
            }
        }

        foreach (string key in Request.Query.Keys)
        {
            if (key.StartsWith("aids", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string? val in Request.Query[key])
                {
                    if (!string.IsNullOrWhiteSpace(val))
                        accountIdStrings.Add(val.Trim());
                }
            }
        }

        Dictionary<int, string> aids2cookie = new();
        Dictionary<int, string> aids2gcookie = new();

        foreach (string aidStr in accountIdStrings)
        {
            if (!int.TryParse(aidStr, out int accountId) || accountId <= 0)
            {
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation(
                        "[ServerRequester] Skipping non-account / FakePlayer slot aid={AidStr} in aids2cookie.",
                        aidStr);
                }

                continue;
            }

            // 1. Check Redis for direct account ID to session cookie mapping
            RedisValue cachedCookie = await DistributedCache.StringGetAsync($"ACCOUNT-ID-SESSION-COOKIE:{accountId}");
            string? cookie = null;

            if (!cachedCookie.IsNullOrEmpty)
            {
                cookie = cachedCookie.ToString();
            }
            else
            {
                // 2. Fallback: resolve account nickname and check active session cookies
                Account? account = await AccountRepository.GetByIdAsync(accountId, cancellationToken);
                if (account is not null)
                {
                    string userCookiesKey = $"ACCOUNT-ACTIVE-COOKIES:{account.Name.ToUpperInvariant()}";
                    RedisValue[] cookies = await DistributedCache.SetMembersAsync(userCookiesKey);
                    if (cookies is { Length: > 0 } && !cookies[0].IsNullOrEmpty)
                    {
                        cookie = cookies[0].ToString();
                    }
                    else
                    {
                        string userCookiesKeyAlt = $"ACCOUNT-ACTIVE-COOKIES:[\"{account.Name.ToLowerInvariant()}\"]";
                        RedisValue[] cookiesAlt = await DistributedCache.SetMembersAsync(userCookiesKeyAlt);
                        if (cookiesAlt is { Length: > 0 } && !cookiesAlt[0].IsNullOrEmpty)
                        {
                            cookie = cookiesAlt[0].ToString();
                        }
                    }

                    if (cookie is not null)
                    {
                        await DistributedCache.StringSetAsync($"ACCOUNT-ID-SESSION-COOKIE:{accountId}", cookie,
                            TimeSpan.FromHours(24));
                    }
                }
            }

            if (string.IsNullOrEmpty(cookie))
            {
                // Fallback for bots, practice players, or guests without an indexed cookie:
                // Provide a synthetic session cookie to prevent libgame_shared from logging
                // "Error: Failed to get a cookie for UpdateQuestsRequest".
                cookie = $"sess_quest_{accountId}_{Guid.NewGuid():N}";
            }

            aids2cookie[accountId] = cookie;
            aids2gcookie[accountId] = cookie;
        }

        Dictionary<string, object> response = new()
        {
            ["aids2cookie"] = aids2cookie,
            ["aids2gcookie"] = aids2gcookie
        };

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[ServerRequester] aids2cookie resolved {Count} account cookies for dedicated server auth.",
                aids2cookie.Count);
        }

        return Ok(PhpSerialization.Serialize(response));
    }
}
