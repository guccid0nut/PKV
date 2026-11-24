// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Models.Configuration;
using KONGOR.MasterServer.Controllers.ServerRequesterController;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class ServerRequesterFallbackTests
{
    private static ServerRequesterController CreateController()
    {
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();
        Mock<IDatabase> mockRedis = new();
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

        return new ServerRequesterController(
            mockAccountRepo.Object,
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config);
    }

    [Fact]
    public async Task ServerRequester_UnknownFunctionInQuery_ReturnsOkWithSerializedFalse()
    {
        // Arrange
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("nonexistent_command_xyz")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be("b:0;");
    }

    [Fact]
    public async Task ServerRequester_MissingFunctionParameter_ReturnsOkWithSerializedFalse()
    {
        // Arrange
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be("b:0;");
    }

    [Fact]
    public async Task ServerRequester_UnknownFunctionInPostForm_ReturnsOkWithSerializedFalse()
    {
        // Arrange
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("legacy_unsupported_probe")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be("b:0;");
    }

    [Fact]
    public async Task ServerRequester_AcceptKey_MissingSession_ReturnsBadRequest()
    {
        // Arrange
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("accept_key"),
            ["acc_key"] = new("sample_key_123")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ServerRequester_AcceptKey_MissingAccountKey_ReturnsBadRequest()
    {
        // Arrange
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("accept_key"),
            ["session"] = new("sample_session_cookie")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ServerRequester_AcceptKey_UnregisteredSession_ReturnsUnauthorized()
    {
        // Arrange
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("accept_key"),
            ["session"] = new("sample_session_cookie"),
            ["acc_key"] = new("sample_acc_key")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ServerRequester_AcceptKey_RegisteredServer_ReturnsSerializedResponse()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();
        Mock<IDatabase> mockRedis = new();
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

        ASPIRE.Common.Models.ServerManagement.MatchServer server = new()
        {
            ID = 1337,
            HostAccountID = 1,
            HostAccountName = "HostAccount",
            Name = "Official_Server_1",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "US",
            Description = "Dedicated Match Server",
            Cookie = "registered_session_cookie",
            IsPrivate = false
        };
        byte[] serializedServer = MemoryPack.MemoryPackSerializer.Serialize(server);

        mockRedis
            .Setup(r => r.HashGetAsync(It.Is<RedisKey>(k => (string)k! == "MATCH-SERVER-SESSIONS"), "registered_session_cookie", It.IsAny<CommandFlags>()))
            .ReturnsAsync(serializedServer);

        ServerRequesterController controller = new(
            mockAccountRepo.Object,
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("accept_key"),
            ["session"] = new("registered_session_cookie"),
            ["acc_key"] = new("sample_acc_key")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester();

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        string serialized = okResult.Value.Should().BeOfType<string>().Subject;
        serialized.Should().Contain("1337");
        serialized.Should().Contain("official");
    }

    [Fact]
    public async Task ServerRequester_Aids2Cookie_MissingFormContentType_ReturnsBadRequest()
    {
        // Protects: server_requester.php?f=aids2cookie against unauthenticated GET / non-form probes.
        // Fails when: Handler omits form/session validation and returns 200 OK or throws InvalidOperationException.
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("aids2cookie"),
            ["aids[]"] = new("42")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ServerRequester_Aids2Cookie_MissingSession_ReturnsBadRequest()
    {
        // Protects: server_requester.php?f=aids2cookie against unauthenticated form posts omitting server session.
        // Fails when: Handler serves player cookies without requiring a match-server session parameter.
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("aids2cookie"),
            ["aids[]"] = new("42")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ServerRequester_Aids2Cookie_UnregisteredSession_ReturnsPlaintextUnauthorized_WithoutRawCookieOrAuthFailure()
    {
        // Protects: aids2cookie against forged server sessions, prevents CGameServer frame use-after-free on "auth_failure",
        // and prevents echoing raw unmasked session tokens in 401 bodies.
        const string forgedSession = "forged_secret_server_session_987654321";
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("aids2cookie"),
            ["session"] = new(forgedSession),
            ["aids[]"] = new("42")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        UnauthorizedObjectResult unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        string body = unauthorized.Value.Should().BeOfType<string>().Subject;
        body.Should().NotContain("auth_failure");
        body.Should().NotContain(forgedSession);
    }

    [Fact]
    public async Task ServerRequester_AcceptKey_UnregisteredSession_DoesNotEchoRawSessionCookie()
    {
        // Protects: HTTP 401 error responses on accept_key from echoing raw session tokens.
        const string rawSession = "raw_secret_accept_key_session_abcdef123456";
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("accept_key"),
            ["session"] = new(rawSession),
            ["acc_key"] = new("sample_acc_key")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        UnauthorizedObjectResult unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeOfType<string>().Which.Should().NotContain(rawSession);
    }

    [Fact]
    public async Task ServerRequester_GetUpgrades_InvalidCookie_DoesNotEchoRawCookie()
    {
        // Protects: HTTP 401 error responses on get_upgrades from echoing raw player session cookies.
        const string rawPlayerCookie = "raw_player_session_cookie_deadbeefcafe1234";
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("get_upgrades"),
            ["cookie"] = new(rawPlayerCookie)
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        UnauthorizedObjectResult unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeOfType<string>().Which.Should().NotContain(rawPlayerCookie);
    }

    [Theory]
    [InlineData("Host:0", "11235", null)]
    [InlineData("Host:abc", "11235", null)]
    [InlineData("Host:-2", "11235", null)]
    [InlineData("Host:1", "0", null)]
    [InlineData("Host:1", "70000", null)]
    [InlineData("Host:1", "notaport", null)]
    [InlineData("Host:1", "20000", "0")]
    [InlineData("Host:1", "20000", "invalid_local")]
    public async Task ServerRequester_NewSession_InvalidInstanceOrPort_ReturnsBadRequest_InsteadOfThrowing(
        string login,
        string port,
        string? localPort)
    {
        // Protects: HandleServerAuthentication (new_session) against FormatException / IndexOutOfRangeException on invalid numeric inputs.
        ServerRequesterController controller = CreateController();
        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";

        Dictionary<string, StringValues> fields = new()
        {
            ["f"] = new("new_session"),
            ["login"] = new(login),
            ["pass"] = new("dummy_pass"),
            ["port"] = new(port),
            ["name"] = new("Server 1"),
            ["desc"] = new("Desc"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1")
        };
        if (localPort is not null)
            fields["local_port"] = new(localPort);

        httpContext.Request.Form = new FormCollection(fields);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ServerRequester_NewSession_RepeatedRegistration_DeduplicatesMatchServerIDs()
    {
        // Protects: MatchServerManager.MatchServerIDs against unbounded duplicate growth when COMPEL and K2_SERVER both call new_session.
        const string hostName = "FleetHost";
        const string rawPassMd5 = "5f4dcc3b5aa765d61d8327deb882cf99";
        const string salt = "test_salt_123";
        string srpHash = Kongor.Core.Security.S2Crypto.ComputeSrpFromPrehashedMd5(rawPassMd5, salt);

        MERRICK.DatabaseContext.Entities.Core.User hostUser = new()
        {
            ID = 10,
            EmailAddress = "fleethost@kongor.net",
            PBKDF2PasswordHash = "pbkdf2",
            SRPPasswordSalt = salt,
            SRPPasswordHash = srpHash
        };
        MERRICK.DatabaseContext.Entities.Core.Account hostAccount = new()
        {
            ID = 20,
            Name = hostName,
            Type = NEXUS.Contracts.Domain.AccountType.ServerHost,
            User = hostUser
        };

        ASPIRE.Common.Models.ServerManagement.MatchServerManager manager = new()
        {
            ID = 777,
            HostAccountID = hostAccount.ID,
            HostAccountName = hostName,
            IPAddress = "127.0.0.1",
            MatchServerIDs = []
        };

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync(hostName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostAccount);

        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();
        Mock<IDatabase> mockRedis = new();

        ASPIRE.Common.Models.ServerManagement.MatchServerManager? savedManager = null;
        mockRedis
            .Setup(r => r.HashScanAsync(
                It.Is<RedisKey>(k => (string)k! == "MATCH-SERVER-MANAGERS"),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(() => ToAsyncEnumerable(new HashEntry(hostName, MemoryPack.MemoryPackSerializer.Serialize(savedManager ?? manager))));

        mockRedis
            .Setup(r => r.HashSetAsync(
                It.Is<RedisKey>(k => (string)k! == "MATCH-SERVER-MANAGERS"),
                It.IsAny<HashEntry[]>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, HashEntry[], CommandFlags>((_, entries, _) =>
            {
                savedManager = MemoryPack.MemoryPackSerializer.Deserialize<ASPIRE.Common.Models.ServerManagement.MatchServerManager>((byte[])entries[0].Value!);
            })
            .Returns(Task.CompletedTask);

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
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config);

        for (int call = 0; call < 2; call++)
        {
            DefaultHttpContext httpContext = new();
            httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = new("new_session"),
                ["login"] = new($"{hostName}:1"),
                ["pass"] = new(rawPassMd5),
                ["port"] = new("20000"),
                ["local_port"] = new("11235"),
                ["name"] = new("Arena #L1"),
                ["desc"] = new(""),
                ["location"] = new("US"),
                ["ip"] = new("127.0.0.1")
            });
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            IActionResult res = await controller.ServerRequester();
            res.Should().BeOfType<OkObjectResult>();
        }

        savedManager.Should().NotBeNull();
        savedManager!.MatchServerIDs.Should().HaveCount(1);
    }

    [Fact]
    public async Task ServerRequester_Aids2Cookie_SkipsNonPositiveAccountIds_OmitZeroAndNegativeFromResponse()
    {
        // Protects: aids2cookie from emitting synthetic session cookies for FakePlayers / non-account slots (aid <= 0).
        // Fails when: aids2cookie includes keys <= 0, causing dedicated server cookie mismatch and FakePlayer termination.
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();
        Mock<IDatabase> mockRedis = new();
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

        ASPIRE.Common.Models.ServerManagement.MatchServer server = new()
        {
            ID = 1002,
            HostAccountID = 1,
            HostAccountName = "HostAccount",
            Name = "Official_Server_1002",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "US",
            Description = "Dedicated Match Server",
            Cookie = "registered_server_cookie",
            IsPrivate = false
        };
        byte[] serializedServer = MemoryPack.MemoryPackSerializer.Serialize(server);

        mockRedis
            .Setup(r => r.HashGetAsync(It.Is<RedisKey>(k => (string)k! == "MATCH-SERVER-SESSIONS"), "registered_server_cookie", It.IsAny<CommandFlags>()))
            .ReturnsAsync(serializedServer);

        mockRedis
            .Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => (string)k! == "ACCOUNT-ID-SESSION-COOKIE:42"), It.IsAny<CommandFlags>()))
            .ReturnsAsync("session_for_account_42");

        ServerRequesterController controller = new(
            mockAccountRepo.Object,
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("aids2cookie"),
            ["session"] = new("registered_server_cookie"),
            ["aids[0]"] = new("0"),
            ["aids[1]"] = new("-1"),
            ["aids[2]"] = new("42")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.ServerRequester();

        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        string serialized = okResult.Value.Should().BeOfType<string>().Subject;

        serialized.Should().Contain("session_for_account_42");
        serialized.Should().Contain("i:42;");
        serialized.Should().NotContain("i:0;");
        serialized.Should().NotContain("i:-1;");
    }

    private static async IAsyncEnumerable<HashEntry> ToAsyncEnumerable(params HashEntry[] entries)
    {
        foreach (HashEntry entry in entries)
        {
            yield return entry;
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ReplayAuth_WhenDerivedArcadeHostMissing_AutoProvisionsWhenBaseServerHostPasswordMatches_AndRejectsWrongPassword()
    {
        // protects: Zero-friction on-demand <host>_arcade ServerHost provisioning in ServerRequesterController (f=replay_auth)
        //           gated strictly by constant-time SRP password verification against an active non-banned ServerHost base account.
        // fails_when: External self-hosters or Supabase Cloud DB installs fail login on <host>_arcade, OR wrong passwords auto-create accounts.
        // seam: Real ServerRequesterController f=replay_auth + S2Crypto SRP verification + IAccountRepository.ProvisionDerivedArcadeHostAccountAsync.
        string syntheticSecret = Guid.NewGuid().ToString("N");
        string salt = "arcade_salt_01";
        string md5Prehash = System.Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(syntheticSecret))).ToLowerInvariant();
        string expectedSrp = Kongor.Core.Security.S2Crypto.ComputeSrpFromPrehashedMd5(md5Prehash, salt);

        MERRICK.DatabaseContext.Entities.Core.User baseUser = new()
        {
            ID = 700,
            EmailAddress = "host_alice@nexus.local",
            Role = new MERRICK.DatabaseContext.Entities.Utility.Role { ID = 2, Name = "User" },
            SRPPasswordSalt = salt,
            SRPPasswordHash = expectedSrp,
            PBKDF2PasswordHash = "pbkdf2"
        };
        MERRICK.DatabaseContext.Entities.Core.Account baseHost = new()
        {
            ID = 701,
            Name = "alicehost",
            Type = NEXUS.Contracts.Domain.AccountType.ServerHost,
            User = baseUser
        };
        MERRICK.DatabaseContext.Entities.Core.Account provisionedArcade = new()
        {
            ID = 702,
            Name = "alicehost_arc",
            Type = NEXUS.Contracts.Domain.AccountType.ServerHost,
            User = baseUser
        };

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();
        Mock<IDatabase> mockRedis = new();

        mockAccountRepo
            .Setup(r => r.GetForAuthenticationAsync("alicehost_arc", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MERRICK.DatabaseContext.Entities.Core.Account?)null);
        mockAccountRepo
            .Setup(r => r.GetForAuthenticationAsync("alicehost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(baseHost);
        mockAccountRepo
            .Setup(r => r.ProvisionDerivedArcadeHostAccountAsync("alicehost_arc", baseHost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provisionedArcade);

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
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config);

        // 1. Wrong password hash -> rejected, ProvisionDerivedArcadeHostAccountAsync NEVER called!
        DefaultHttpContext badCtx = new();
        badCtx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        badCtx.Request.ContentType = "application/x-www-form-urlencoded";
        badCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("replay_auth"),
            ["login"] = new("alicehost_arc"),
            ["pass"] = new("00000000000000000000000000000000")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = badCtx };

        IActionResult badResult = await controller.ServerRequester();
        string badBody = badResult.Should().BeOfType<NotFoundObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;
        badBody.Should().Contain("alicehost_arc");
        mockAccountRepo.Verify(
            r => r.ProvisionDerivedArcadeHostAccountAsync(It.IsAny<string>(), It.IsAny<MERRICK.DatabaseContext.Entities.Core.Account>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // 2. Matching base host password -> auto-provisions alicehost_arc and issues valid session!
        DefaultHttpContext goodCtx = new();
        goodCtx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        goodCtx.Request.ContentType = "application/x-www-form-urlencoded";
        goodCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("replay_auth"),
            ["login"] = new("alicehost_arc"),
            ["pass"] = new(md5Prehash)
        });
        controller.ControllerContext = new ControllerContext { HttpContext = goodCtx };

        IActionResult goodResult = await controller.ServerRequester();
        string goodBody = goodResult.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;
        goodBody.Should().Contain("session");
        goodBody.Should().Contain("server_id");
        mockAccountRepo.Verify(
            r => r.ProvisionDerivedArcadeHostAccountAsync("alicehost_arc", baseHost, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NewSession_CompelPreRegFollowedByK2NativeWithoutLocalPort_ReusesExistingCookie()
    {
        const string passSecret = "secret";
        const string salt = "compel_salt_01";
        string md5Prehash = System.Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(passSecret))).ToLowerInvariant();
        string expectedSrp = Kongor.Core.Security.S2Crypto.ComputeSrpFromPrehashedMd5(md5Prehash, salt);

        MERRICK.DatabaseContext.Entities.Core.User hostUser = new()
        {
            ID = 800,
            EmailAddress = "compel_host_1@nexus.local",
            Role = new MERRICK.DatabaseContext.Entities.Utility.Role { ID = 2, Name = "User" },
            SRPPasswordSalt = salt,
            SRPPasswordHash = expectedSrp,
            PBKDF2PasswordHash = "pbkdf2"
        };
        MERRICK.DatabaseContext.Entities.Core.Account hostAccount = new()
        {
            ID = 801,
            Name = "compel_host_1",
            Type = NEXUS.Contracts.Domain.AccountType.ServerHost,
            User = hostUser
        };

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetForAuthenticationAsync("compel_host_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostAccount);
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("compel_host_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostAccount);

        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();

        Dictionary<string, RedisValue> serversHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> indexHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> sessionsHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> managersHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> mgrIndexHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> mgrSessionsHash = new(StringComparer.Ordinal);

        Mock<IDatabase> mockRedis = new();
        mockRedis
            .Setup(db => db.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue field, CommandFlags _) => key.ToString() switch
            {
                "MATCH-SERVERS" => serversHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                "MATCH-SERVER-INDEX" => indexHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                "MATCH-SERVER-SESSIONS" => sessionsHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                "MATCH-SERVER-MANAGERS" => managersHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                "MATCH-SERVER-MANAGER-INDEX" => mgrIndexHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                "MATCH-SERVER-MANAGER-SESSIONS" => mgrSessionsHash.GetValueOrDefault(field.ToString(), RedisValue.Null),
                _ => RedisValue.Null
            });
        mockRedis
            .Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey key, HashEntry[] entries, CommandFlags _) =>
            {
                Dictionary<string, RedisValue>? target = key.ToString() switch
                {
                    "MATCH-SERVERS" => serversHash,
                    "MATCH-SERVER-INDEX" => indexHash,
                    "MATCH-SERVER-SESSIONS" => sessionsHash,
                    "MATCH-SERVER-MANAGERS" => managersHash,
                    "MATCH-SERVER-MANAGER-INDEX" => mgrIndexHash,
                    "MATCH-SERVER-MANAGER-SESSIONS" => mgrSessionsHash,
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
        mockRedis
            .Setup(db => db.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue field, CommandFlags _) => key.ToString() switch
            {
                "MATCH-SERVERS" => serversHash.Remove(field.ToString()),
                "MATCH-SERVER-INDEX" => indexHash.Remove(field.ToString()),
                "MATCH-SERVER-SESSIONS" => sessionsHash.Remove(field.ToString()),
                _ => false
            });

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
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config);

        static string ExtractSessionCookie(string phpPayload)
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                phpPayload,
                @"s:7:""session"";s:\d+:""([^""]+)""");
            match.Success.Should().BeTrue($"expected PHP 'session' field in response: {phpPayload}");
            return match.Groups[1].Value;
        }

        // 1. COMPEL supervisor pre-registers worker with local_port set
        DefaultHttpContext compelCtx = new();
        compelCtx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        compelCtx.Request.ContentType = "application/x-www-form-urlencoded";
        compelCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_host_1:4002"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("Slave2002"),
            ["port"] = new("11485"),
            ["local_port"] = new("11485"),
            ["instance"] = new("4002"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["local_ip"] = new("127.0.0.1"),
            ["desc"] = new("DevoWars 10v10")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = compelCtx };

        IActionResult r1 = await controller.ServerRequester();
        string body1 = r1.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;
        string compelCookie = ExtractSessionCookie(body1);
        sessionsHash.Should().ContainKey(compelCookie);

        // 2. Native K2 hon-x86_64-server calls new_session seconds later WITHOUT local_port
        DefaultHttpContext k2Ctx = new();
        k2Ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        k2Ctx.Request.ContentType = "application/x-www-form-urlencoded";
        k2Ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_host_1:4002"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("Slave2002"),
            ["port"] = new("11485"),
            ["instance"] = new("4002"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["local_ip"] = new("127.0.0.1"),
            ["desc"] = new("DevoWars 10v10")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = k2Ctx };

        IActionResult r2 = await controller.ServerRequester();
        string body2 = r2.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;
        string k2Cookie = ExtractSessionCookie(body2);

        k2Cookie.Should().Be(compelCookie, "COMPEL pre-registration and K2 native new_session must unify onto a single cookie");
        sessionsHash.Keys.Should().Equal([compelCookie]);

        // 3. Subsequent COMPEL-driven session rotation (with local_port) rotates cookie and evicts the old cookie
        DefaultHttpContext rotateCtx = new();
        rotateCtx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        rotateCtx.Request.ContentType = "application/x-www-form-urlencoded";
        rotateCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("new_session"),
            ["login"] = new("compel_host_1:4002"),
            ["pass"] = new(md5Prehash),
            ["name"] = new("Slave2002"),
            ["port"] = new("11485"),
            ["local_port"] = new("11485"),
            ["instance"] = new("4002"),
            ["location"] = new("US"),
            ["ip"] = new("127.0.0.1"),
            ["local_ip"] = new("127.0.0.1"),
            ["desc"] = new("DevoWars 10v10")
        });
        controller.ControllerContext = new ControllerContext { HttpContext = rotateCtx };

        IActionResult r3 = await controller.ServerRequester();
        string body3 = r3.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;
        string rotatedCookie = ExtractSessionCookie(body3);

        rotatedCookie.Should().NotBe(compelCookie);
        sessionsHash.Should().NotContainKey(compelCookie, "superseded cookie must be evicted on rotation");
        sessionsHash.Should().ContainKey(rotatedCookie);
    }
}
