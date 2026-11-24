// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Kongor.Core.Security;
using KONGOR.MasterServer.Controllers.ServerRequesterController;
using KONGOR.MasterServer.Infrastructure;
using KONGOR.MasterServer.Models.Configuration;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class ServerCookieRotationAndMaskingTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private sealed class InMemoryRedisHarness
    {
        public Dictionary<string, RedisValue> ServersHash { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RedisValue> IndexHash { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RedisValue> SessionsHash { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RedisValue> MatchDataHash { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RedisValue> StringKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan?> StringTtls { get; } = new(StringComparer.Ordinal);
        public Mock<IDatabase> MockRedis { get; } = new();
        private readonly Mock<IConnectionMultiplexer> _mockMux = new();
        private readonly Mock<IServer> _mockServer = new();

        public InMemoryRedisHarness()
        {
            IPEndPoint endpoint = new(IPAddress.Loopback, 6379);
            _mockMux.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([endpoint]);
            _mockMux.Setup(m => m.GetServer(endpoint, It.IsAny<object?>())).Returns(_mockServer.Object);
            _mockServer.Setup(s => s.Keys(
                    It.IsAny<int>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<int>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CommandFlags>()))
                .Returns([]);
            MockRedis.SetupGet(db => db.Multiplexer).Returns(_mockMux.Object);
            MockRedis
                .Setup(db => db.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue field, CommandFlags _) => key.ToString() switch
                {
                    "MATCH-SERVERS" => ServersHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                    "MATCH-SERVER-INDEX" => IndexHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                    "MATCH-SERVER-SESSIONS" => SessionsHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                    "MATCH-START-DATA" => MatchDataHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                    _ => RedisValue.Null
                });

            MockRedis
                .Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey key, HashEntry[] entries, CommandFlags _) =>
                {
                    Dictionary<string, RedisValue>? target = key.ToString() switch
                    {
                        "MATCH-SERVERS" => ServersHash,
                        "MATCH-SERVER-INDEX" => IndexHash,
                        "MATCH-SERVER-SESSIONS" => SessionsHash,
                        "MATCH-START-DATA" => MatchDataHash,
                        _ => null
                    };
                    if (target != null)
                    {
                        foreach (HashEntry entry in entries)
                        {
                            target[entry.Name.ToString()] = entry.Value;
                        }
                    }
                })
                .Returns(Task.CompletedTask);

            MockRedis
                .Setup(db => db.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue field, CommandFlags _) => key.ToString() switch
                {
                    "MATCH-SERVERS" => ServersHash.Remove(field.ToString()),
                    "MATCH-SERVER-INDEX" => IndexHash.Remove(field.ToString()),
                    "MATCH-SERVER-SESSIONS" => SessionsHash.Remove(field.ToString()),
                    "MATCH-START-DATA" => MatchDataHash.Remove(field.ToString()),
                    _ => false
                });

            MockRedis
                .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, CommandFlags _) =>
                    StringKeys.TryGetValue(key.ToString(), out RedisValue val) ? val : RedisValue.Null);

            MockRedis
                .Setup(db => db.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<bool>(),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue val, TimeSpan? expiry, bool _, When _, CommandFlags _) =>
                {
                    StringKeys[key.ToString()] = val;
                    StringTtls[key.ToString()] = expiry;
                    return true;
                });

            MockRedis
                .Setup(db => db.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue val, Expiration expiry, ValueCondition _, CommandFlags _) =>
                {
                    StringKeys[key.ToString()] = val;
                    StringTtls[key.ToString()] = expiry.Equals(default(Expiration)) ? null : TimeSpan.FromSeconds(45);
                    return true;
                });

            MockRedis
                .Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, CommandFlags _) =>
                {
                    StringTtls.Remove(key.ToString());
                    return StringKeys.Remove(key.ToString());
                });
        }
    }

    private static (ServerRequesterController Controller, InMemoryRedisHarness Redis, CapturingLogger<ServerRequesterController> Logger, string Md5Prehash) CreateControllerHarness(
        string hostAccountName = "compel_host_rot",
        int accountId = 850)
    {
        string syntheticInput = Guid.NewGuid().ToString("N");
        string md5Prehash = Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(syntheticInput)));
        string salt = "rotation_salt_value";
        string expectedSrp = S2Crypto.ComputeSrpFromPrehashedMd5(md5Prehash, salt);

        Account hostAccount = new()
        {
            ID = accountId,
            Name = hostAccountName,
            Type = AccountType.ServerHost,
            User = new User
            {
                ID = accountId,
                EmailAddress = $"{hostAccountName}@kongor.local",
                SRPPasswordSalt = salt,
                SRPPasswordHash = expectedSrp,
                PBKDF2PasswordHash = "pbkdf2"
            }
        };

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetAuthIdentityByNicknameAsync(hostAccountName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostAccount);
        mockAccountRepo.Setup(r => r.GetForAuthenticationAsync(hostAccountName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostAccount);
        mockAccountRepo.Setup(r => r.GetByNicknameAsync(hostAccountName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostAccount);

        InMemoryRedisHarness redis = new();
        CapturingLogger<ServerRequesterController> logger = new();
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

        ServerRequesterController controller = new(
            mockAccountRepo.Object,
            Mock.Of<IMatchHistoryRepository>(),
            redis.MockRedis.Object,
            logger,
            config);

        return (controller, redis, logger, md5Prehash);
    }

    private static string ExtractSessionCookie(string phpPayload)
    {
        System.Text.RegularExpressions.Match match = Regex.Match(phpPayload, @"s:7:""session"";s:\d+:""([^""]+)""");
        match.Success.Should().BeTrue($"expected PHP 'session' field in response: {phpPayload}");
        return match.Groups[1].Value;
    }

    private static async Task<IActionResult> PostFormAsync(
        ServerRequesterController controller,
        Dictionary<string, StringValues> fields)
    {
        DefaultHttpContext ctx = new();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        ctx.Request.Form = new FormCollection(fields);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return await controller.ServerRequester();
    }

    [Fact]
    public void RequestLoggingMiddleware_RedactsSensitiveFieldsInQueryAndBody_AndDemotesHeartbeats()
    {
        // protects: Layer 6C credential/token redaction on QueryString and form/JSON Body at all log levels + heartbeat log level demotion.
        // fails_when: Plaintext pass/password/secret/session/cookie leaks in logs, or /healthz / f=set_online polls flood Information level.
        // seam: Real RequestLoggingMiddleware.RedactQueryString, RedactBody, and ResolveLogLevel.
        string rawQuery = "?f=new_session&session=deadbeef1234567890&token=tok987654321&pass=mysecretpass&key=k123";
        string redactedQuery = RequestLoggingMiddleware.RedactQueryString(rawQuery);
        redactedQuery.Should().Contain("session=deadbeef...");
        redactedQuery.Should().NotContain("deadbeef1234567890");
        redactedQuery.Should().Contain("token=tok98765...");
        redactedQuery.Should().NotContain("tok987654321");
        redactedQuery.Should().Contain("pass=[REDACTED]");
        redactedQuery.Should().NotContain("mysecretpass");
        redactedQuery.Should().Contain("key=[REDACTED]");
        redactedQuery.Should().NotContain("k123");

        string rawJson = """{"cookie":"cookievalue12345","auth_header":"BearerSecretToken","old_password":"pw1","new_password":"pw2","secret":"s99","name":"midwars"}""";
        string redactedJson = RequestLoggingMiddleware.RedactBody(rawJson, "application/json");
        redactedJson.Should().Contain("\"cookie\":\"cookieva...\"");
        redactedJson.Should().Contain("\"auth_header\":\"BearerSe...\"");
        redactedJson.Should().Contain("\"old_password\":\"[REDACTED]\"");
        redactedJson.Should().Contain("\"new_password\":\"[REDACTED]\"");
        redactedJson.Should().Contain("\"secret\":\"[REDACTED]\"");
        redactedJson.Should().NotContain("cookievalue12345");
        redactedJson.Should().NotContain("BearerSecretToken");
        redactedJson.Should().NotContain("pw1");
        redactedJson.Should().NotContain("pw2");

        DefaultHttpContext setOnlinePoll = new();
        setOnlinePoll.Request.Path = "/server_requester.php";
        string pollBody = "f=set_online&session=abcdef0123456789&c_state=1";
        RequestLoggingMiddleware.ResolveLogLevel(setOnlinePoll.Request, string.Empty, pollBody, verboseHeartbeatsOverride: false)
            .Should().Be(LogLevel.Debug);

        string initMatchBody = "f=set_online&session=abcdef0123456789&new=1&match_id=1434";
        RequestLoggingMiddleware.ResolveLogLevel(setOnlinePoll.Request, string.Empty, initMatchBody, verboseHeartbeatsOverride: false)
            .Should().Be(LogLevel.Information);

        DefaultHttpContext healthReq = new();
        healthReq.Request.Path = "/health";
        RequestLoggingMiddleware.ResolveLogLevel(healthReq.Request, string.Empty, string.Empty, verboseHeartbeatsOverride: false)
            .Should().Be(LogLevel.Debug);
        RequestLoggingMiddleware.ResolveLogLevel(healthReq.Request, string.Empty, string.Empty, verboseHeartbeatsOverride: true)
            .Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task HandleServerAuthentication_ProcessIdentityBinding_BlocksDeadPidReuseAfterRestart()
    {
        // protects: Layer 2A process-identity-bound initial session reuse — crashed/restarted worker never inherits dead PID's cookie or grace alias.
        // fails_when: A restarted worker with a new PID/boot_nonce grants a grace alias for the dead PID's cookie, or a stale PID can claim the new worker's cookie.
        // seam: Real ServerRequesterController f=new_session & f=set_online backed by in-memory Redis Hash + String state.
        (ServerRequesterController controller, InMemoryRedisHarness redis, CapturingLogger<ServerRequesterController> _, string md5Prehash) =
            CreateControllerHarness("compel_pid_host", 851);

        // 1. COMPEL launches PID 101 (boot_nonce=boot-101) with local_port=11485
        IActionResult r1 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_pid_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["local_port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars"),
            ["pid"] = new("101"),
            ["process_start_utc"] = new("638990000000000000"),
            ["boot_nonce"] = new("boot-101")
        });
        string cookiePid101 = ExtractSessionCookie(r1.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);

        // 2. K2 from PID 101 handshakes cleanly without local_port -> reuses cookiePid101
        IActionResult r2 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_pid_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars"),
            ["pid"] = new("101"),
            ["process_start_utc"] = new("638990000000000000"),
            ["boot_nonce"] = new("boot-101")
        });
        string k2CookiePid101 = ExtractSessionCookie(r2.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);
        k2CookiePid101.Should().Be(cookiePid101);

        // 3. Worker crashes and COMPEL spawns PID 202 (boot_nonce=boot-202) -> fresh cookie, NO grace alias for dead PID 101!
        IActionResult r3 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_pid_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["local_port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars"),
            ["pid"] = new("202"),
            ["process_start_utc"] = new("638990005000000000"),
            ["boot_nonce"] = new("boot-202")
        });
        string cookiePid202 = ExtractSessionCookie(r3.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);
        cookiePid202.Should().NotBe(cookiePid101);
        redis.StringKeys.Should().NotContainKey($"MATCH-SERVER:PREV-COOKIE:{cookiePid101}");

        // 4. Stale zombie PID 101 calling new_session must NEVER inherit PID 202's cookie
        IActionResult rZombie = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_pid_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars"),
            ["pid"] = new("101"),
            ["process_start_utc"] = new("638990000000000000"),
            ["boot_nonce"] = new("boot-101")
        });
        string zombieCookie = ExtractSessionCookie(rZombie.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);
        zombieCookie.Should().NotBe(cookiePid202);

        // 5. Dead PID 101's old cookie is strictly 401 Unauthorized on set_online
        IActionResult deadSetOnline = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("set_online"),
            ["session"] = new(cookiePid101),
            ["num_conn"] = new("0"),
            ["cgt"] = new("00:00:00"),
            ["map"] = new("midwars"),
            ["private"] = new("0"),
            ["vip"] = new("0"),
            ["c_state"] = new("1"),
            ["prev_c_state"] = new("1")
        });
        deadSetOnline.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task HandleServerAuthentication_LegitimateRotation_GrantsSingleUse45sGraceAliasAndRejectsForgedCookies()
    {
        // protects: Layer 2A single-use 45s previous-cookie grace alias, warning log auditability, immediate deletion on redemption/new-cookie,
        //           and strict rejection of arbitrary forged/unauthenticated session cookies.
        // fails_when: Arbitrary forged cookies are accepted, grace alias works twice, or TTL != 45s.
        // seam: Real ServerRequesterController HandleServerAuthentication + HandleSetOnline + in-memory Redis.
        (ServerRequesterController controller, InMemoryRedisHarness redis, CapturingLogger<ServerRequesterController> logger, string md5Prehash) =
            CreateControllerHarness("compel_grace_host", 852);

        // 1. COMPEL pre-reg + K2 handshake -> c1 (initial single-use handshake consumed)
        IActionResult r1 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_grace_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["local_port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars"),
            ["pid"] = new("300"),
            ["boot_nonce"] = new("boot-300")
        });
        string c1 = ExtractSessionCookie(r1.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);

        IActionResult r2 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_grace_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars")
        });
        ExtractSessionCookie(r2.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!).Should().Be(c1);

        // 2. Legitimate post-EndGame K2 password-authenticated f=new_session rotation -> c2 != c1 + 45s PREV-COOKIE alias for c1
        IActionResult r3 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_grace_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars")
        });
        string c2 = ExtractSessionCookie(r3.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);
        c2.Should().NotBe(c1);

        string prevKey = $"MATCH-SERVER:PREV-COOKIE:{c1}";
        redis.StringKeys.Should().ContainKey(prevKey);
        redis.StringTtls[prevKey].Should().Be(TimeSpan.FromSeconds(45));

        // 3. Forged/unauthenticated cookie MUST always 401
        IActionResult forgedRes = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("set_online"),
            ["session"] = new("forged-unauthenticated-cookie-deadbeef"),
            ["num_conn"] = new("0"),
            ["cgt"] = new("00:00:00"),
            ["map"] = new("midwars"),
            ["private"] = new("0"),
            ["vip"] = new("0"),
            ["c_state"] = new("1"),
            ["prev_c_state"] = new("1")
        });
        forgedRes.Should().BeOfType<UnauthorizedObjectResult>();

        // 4. First in-flight set_online with c1 succeeds via single-use grace alias, logs Warning, and deletes alias
        IActionResult graceUse1 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("set_online"),
            ["session"] = new(c1),
            ["num_conn"] = new("0"),
            ["cgt"] = new("00:00:00"),
            ["map"] = new("midwars"),
            ["private"] = new("0"),
            ["vip"] = new("0"),
            ["c_state"] = new("1"),
            ["prev_c_state"] = new("1")
        });
        graceUse1.Should().BeOfType<OkResult>();
        redis.StringKeys.Should().NotContainKey(prevKey);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("previous-cookie grace alias", StringComparison.Ordinal));

        // 5. Second set_online with c1 MUST fail 401 (strictly single-use!)
        IActionResult graceUse2 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("set_online"),
            ["session"] = new(c1),
            ["num_conn"] = new("0"),
            ["cgt"] = new("00:00:00"),
            ["map"] = new("midwars"),
            ["private"] = new("0"),
            ["vip"] = new("0"),
            ["c_state"] = new("1"),
            ["prev_c_state"] = new("1")
        });
        graceUse2.Should().BeOfType<UnauthorizedObjectResult>();

        // 6. Rotate c2 -> c3, then see c3 on set_online -> PREV-COOKIE:c2 is deleted immediately before ever being redeemed
        IActionResult r4 = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_grace_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars")
        });
        string c3 = ExtractSessionCookie(r4.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);
        redis.StringKeys.Should().ContainKey($"MATCH-SERVER:PREV-COOKIE:{c2}");

        IActionResult newCookieSeen = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("set_online"),
            ["session"] = new(c3),
            ["num_conn"] = new("0"),
            ["cgt"] = new("00:00:00"),
            ["map"] = new("midwars"),
            ["private"] = new("0"),
            ["vip"] = new("0"),
            ["c_state"] = new("1"),
            ["prev_c_state"] = new("1")
        });
        newCookieSeen.Should().BeOfType<OkResult>();
        redis.StringKeys.Should().NotContainKey($"MATCH-SERVER:PREV-COOKIE:{c2}");
    }

    [Fact]
    public async Task HandleSetOnline_SmartHeartbeatDemotion_EmitsInformationOnlyOnTransitionsAnd60sSummaryRollup()
    {
        // protects: Layer 6C [SetOnline] unchanged repeat demotion to Debug, masked single-line Keys log, transition Information logs, and 60s [HeartbeatSummary] rollup.
        // fails_when: Unchanged heartbeats log at Information, status/player transitions demote to Debug, raw cookie leaks in Keys log, or 60s rollup triggers early.
        // seam: Real ServerRequesterController.HandleSetOnline + TryAdvanceHeartbeatSummaryWindow.
        (ServerRequesterController controller, InMemoryRedisHarness _, CapturingLogger<ServerRequesterController> logger, string md5Prehash) =
            CreateControllerHarness("compel_hb_host", 853);

        IActionResult regRes = await PostFormAsync(controller, new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_hb_host:4001"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("QUEUE ARCADE #Q1"),
            ["port"] = new("11485"),
            ["local_port"] = new("11485"),
            ["instance"] = new("4001"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["desc"] = new("midwars")
        });
        string cookie = ExtractSessionCookie(regRes.Should().BeOfType<OkObjectResult>().Subject.Value!.ToString()!);

        Dictionary<string, StringValues> hbIdle = new()
        {
            ["f"] = new("set_online"),
            ["session"] = new(cookie),
            ["num_conn"] = new("0"),
            ["cgt"] = new("00:00:00"),
            ["map"] = new("midwars"),
            ["mname"] = new("QUEUE ARCADE #Q1"),
            ["private"] = new("0"),
            ["vip"] = new("0"),
            ["c_state"] = new("1"),
            ["prev_c_state"] = new("1")
        };

        // First unchanged idle heartbeat -> Debug, NOT Information (status/players/map/name unchanged from new_session)
        logger.Entries.Clear();
        (await PostFormAsync(controller, hbIdle)).Should().BeOfType<OkResult>();

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug &&
            e.Message.StartsWith("[SetOnline] Keys:", StringComparison.Ordinal) &&
            !e.Message.Contains('\n') &&
            !e.Message.Contains(cookie, StringComparison.Ordinal));
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug &&
            e.Message.StartsWith("[SetOnline] Heartbeat unchanged", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Information &&
            e.Message.StartsWith("[SetOnline] Heartbeat state transition", StringComparison.Ordinal));

        // Transition: num_conn 0 -> 1, status IDLE -> ACTIVE => Information!
        logger.Entries.Clear();
        Dictionary<string, StringValues> hbActive = new(hbIdle)
        {
            ["num_conn"] = new("1"),
            ["c_state"] = new("3")
        };
        (await PostFormAsync(controller, hbActive)).Should().BeOfType<OkResult>();

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Heartbeat state transition", StringComparison.Ordinal) &&
            e.Message.Contains("0->1", StringComparison.Ordinal));

        // 60s HeartbeatSummary rollup check
        long total = 0;
        long transitions = 0;
        long lastTicks = 0;
        DateTimeOffset t0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        (bool emit0, _, _) = ServerRequesterController.TryAdvanceHeartbeatSummaryWindow(t0, isTransition: false, ref total, ref transitions, ref lastTicks);
        emit0.Should().BeFalse();

        (bool emit30, _, _) = ServerRequesterController.TryAdvanceHeartbeatSummaryWindow(t0.AddSeconds(30), isTransition: true, ref total, ref transitions, ref lastTicks);
        emit30.Should().BeFalse();

        (bool emit61, long windowTotal, long windowTransitions) = ServerRequesterController.TryAdvanceHeartbeatSummaryWindow(t0.AddSeconds(61), isTransition: true, ref total, ref transitions, ref lastTicks);
        emit61.Should().BeTrue();
        windowTotal.Should().Be(3);
        windowTransitions.Should().Be(2);
    }
}
