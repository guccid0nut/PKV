// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using Kongor.Core.Security;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Controllers.ClientRequesterController;
using KONGOR.MasterServer.Controllers.Plinko;
using KONGOR.MasterServer.Controllers.ServerRequesterController;
using KONGOR.MasterServer.Controllers.StatsRequesterController;
using KONGOR.MasterServer.Controllers.Store;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Features.Store;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Models.Configuration;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Models.RequestResponse;
using KONGOR.MasterServer.Models.RequestResponse.Plinko;
using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Economy;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Matches;
using KONGOR.MasterServer.Services.Rating;
using KONGOR.MasterServer.Services.Requester;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Stats;
using KONGOR.MasterServer.Services.Store;
using KONGOR.MasterServer.Tests.Infrastructure;
using MediatR;
using MemoryPack;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Domain.Identifiers;
using NEXUS.Contracts.Domain.Matches;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Services;
using NEXUS.Contracts.Store;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class MasterServerZeroTrustAuthAndMatchStatsTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class SlowChunkedMemoryStream(byte[] data, int maxChunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length) return 0;
            int toCopy = Math.Min(Math.Min(count, maxChunkSize), data.Length - _position);
            Buffer.BlockCopy(data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_position >= data.Length) return 0;
            int toCopy = Math.Min(Math.Min(buffer.Length, maxChunkSize), data.Length - _position);
            data.AsMemory(_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static IOptions<OperationalConfiguration> CreateOperationalOptions() =>
        Options.Create(new OperationalConfiguration
        {
            CurrentSeason = "1",
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

    [Fact]
    public async Task SwitchAuth_ForgedUnknownCookie_RejectedInControllerAndHandlerWithoutBindingRedisSession()
    {
        // protects: f=switch_auth sub-account authentication against arbitrary account takeover via invented cookies.
        // fails_when: ClientRequesterController omits "switch_auth" from cookie validation or SwitchAuthHandler binds an unknown cookie to targetAccount.User.
        // seam: Real ClientRequesterController + real SwitchAuthHandler against MerrickContext and Mock<IDatabase>.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.local",
            Role = new DbRole { ID = 1, Name = "ADMINISTRATOR" },
            SRPPasswordSalt = "salt_1",
            SRPPasswordHash = "hash_1",
            PBKDF2PasswordHash = "pbkdf2_1"
        };
        Account targetAdmin = new()
        {
            ID = 1,
            UserID = 1,
            User = adminUser,
            Name = "KONGOR",
            IsMain = true,
            Type = AccountType.Staff
        };
        Account hostSubAccount = new()
        {
            ID = 2,
            UserID = 1,
            User = adminUser,
            Name = "HOST",
            IsMain = false,
            Type = AccountType.ServerHost
        };
        adminUser.Accounts = [targetAdmin, hostSubAccount];
        db.Users.Add(adminUser);
        db.Accounts.AddRange(targetAdmin, hostSubAccount);
        await db.SaveChangesAsync();

        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        ServiceCollection services = new();
        ClientRequestDispatcher dispatcher = new(services.BuildServiceProvider());
        ClientRequesterController controller = new(
            redisMock.Object,
            NullLogger<ClientRequesterController>.Instance,
            dispatcher);

        DefaultHttpContext controllerCtx = new();
        controllerCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.44");
        controllerCtx.Request.Method = "POST";
        controllerCtx.Request.ContentType = "application/x-www-form-urlencoded";
        controllerCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "switch_auth",
            ["cookie"] = "forged_attacker_cookie_001",
            ["login"] = "KONGOR"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = controllerCtx };

        IActionResult controllerResult = await controller.ProcessRequest(CancellationToken.None);
        controllerResult.Should().BeOfType<UnauthorizedObjectResult>();

        // Direct handler check: must reject forged cookie, never call StringSetAsync, and log [SECURITY-ALERT]
        CapturingLogger<SwitchAuthHandler> logger = new();
        Mock<INetworkTopologyResolver> netResolver = new();
        netResolver.Setup(n => n.ResolveChatServer(It.IsAny<IPAddress>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(("127.0.0.1", 11031));
        Mock<IEventBus> eventBus = new();

        SwitchAuthHandler handler = new(
            new AccountRepository(db, NullLogger<AccountRepository>.Instance),
            redisMock.Object,
            netResolver.Object,
            eventBus.Object,
            CreateOperationalOptions(),
            logger);

        IActionResult handlerResult = await handler.HandleRequestAsync(controllerCtx, CancellationToken.None);
        PhpResult phpFailure = handlerResult.Should().BeOfType<PhpResult>().Subject;
        phpFailure.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        redisMock.Verify(
            r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
            Times.Never);
        logger.Entries.Should().Contain(e => e.Level >= LogLevel.Warning && e.Message.Contains("[SECURITY-ALERT]", StringComparison.Ordinal));

        // Even with a valid owner cookie, switching to AccountType.ServerHost must be rejected
        redisMock.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("valid_admin_cookie")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("KONGOR");

        DefaultHttpContext hostSwitchCtx = new();
        hostSwitchCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.44");
        hostSwitchCtx.Request.ContentType = "application/x-www-form-urlencoded";
        hostSwitchCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "switch_auth",
            ["cookie"] = "valid_admin_cookie",
            ["login"] = "HOST"
        });

        IActionResult hostSwitchResult = await handler.HandleRequestAsync(hostSwitchCtx, CancellationToken.None);
        PhpResult hostSwitchPhp = hostSwitchResult.Should().BeOfType<PhpResult>().Subject;
        hostSwitchPhp.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task StoreController_WithUnlockAllItemsTrue_RejectsUnauthenticatedMutationsWithoutAccount1Fallback_And_BlocksRedeemMismatch()
    {
        // protects: StoreController mutation authorization when Store:UnlockAllItems == true, preventing Account #1 coin/code hijacking.
        // fails_when: StoreController falls back to Account #1 on missing/unknown cookies or permits REDEEM_CODE_REQUEST across mismatched account_id.
        // seam: Real StoreController + RedeemStoreCodeHandler via MediatR against MerrickContext.
        bool originalUnlockAll = UpgradesResolutionService.UnlockAllItems;
        UpgradesResolutionService.UnlockAllItems = true;
        try
        {
            using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

            User adminUser = new()
            {
                ID = 1,
                EmailAddress = "admin@kongor.local",
                Role = new DbRole { ID = 1, Name = "ADMINISTRATOR" },
                SRPPasswordSalt = "s1",
                SRPPasswordHash = "h1",
                PBKDF2PasswordHash = "p1",
                GoldCoins = 9000,
                SilverCoins = 9000
            };
            Account account1 = new()
            {
                ID = 1,
                UserID = 1,
                User = adminUser,
                Name = "KONGOR",
                IsMain = true
            };

            User playerUser = new()
            {
                ID = 20,
                EmailAddress = "player@kongor.local",
                Role = new DbRole { ID = 2, Name = "USER" },
                SRPPasswordSalt = "s2",
                SRPPasswordHash = "h2",
                PBKDF2PasswordHash = "p2",
                GoldCoins = 100,
                SilverCoins = 200
            };
            Account playerAccount = new()
            {
                ID = 20,
                UserID = 20,
                User = playerUser,
                Name = "TestPlayer20",
                IsMain = true
            };

            RedeemableCode promo = new()
            {
                Code = "WAVE1PROMO",
                GoldCoinsReward = 500
            };

            db.Users.AddRange(adminUser, playerUser);
            db.Accounts.AddRange(account1, playerAccount);
            db.RedeemableCodes.Add(promo);
            await db.SaveChangesAsync();

            Mock<IDatabase> redisMock = new();
            redisMock.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisValue.Null);
            redisMock.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("valid_player20_cookie")), It.IsAny<CommandFlags>()))
                .ReturnsAsync("TestPlayer20");

            Mock<IMediator> mediatorMock = new();
            RedeemStoreCodeHandler redeemHandler = new(db, NullLogger<RedeemStoreCodeHandler>.Instance);
            mediatorMock.Setup(m => m.Send(It.IsAny<RedeemStoreCodeCommand>(), It.IsAny<CancellationToken>()))
                .Returns<RedeemStoreCodeCommand, CancellationToken>((cmd, ct) => redeemHandler.Handle(cmd, ct));

            CapturingLogger<StoreController> logger = new();
            StoreController controller = new(
                new AccountRepository(db, NullLogger<AccountRepository>.Instance),
                redisMock.Object,
                logger,
                mediatorMock.Object);

            // 1. Unauthenticated REDEEM_CODE_REQUEST must return STORE_SESSION_ERROR and NOT touch Account #1
            DefaultHttpContext unauthCtx = new();
            unauthCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.55");
            unauthCtx.Request.ContentType = "application/x-www-form-urlencoded";
            unauthCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "unknown_attacker_cookie",
                ["request_code"] = "REDEEM_CODE_REQUEST",
                ["code"] = "WAVE1PROMO"
            });
            controller.ControllerContext = new ControllerContext { HttpContext = unauthCtx };

            IActionResult unauthResult = await controller.StoreRequester(CancellationToken.None);
            PhpResult unauthPhp = unauthResult.Should().BeOfType<PhpResult>().Subject;
            Dictionary<string, object> unauthDict = unauthPhp.Value.Should().BeOfType<Dictionary<string, object>>().Subject;
            unauthDict["errorCode"].Should().Be((int)StoreErrorCode.STORE_SESSION_ERROR);

            promo.TimestampRedeemed.Should().BeNull();
            adminUser.GoldCoins.Should().Be(9000);

            // 2. Authenticated player passing mismatched account_id=1 on REDEEM_CODE_REQUEST must be rejected
            DefaultHttpContext mismatchCtx = new();
            mismatchCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.56");
            mismatchCtx.Request.ContentType = "application/x-www-form-urlencoded";
            mismatchCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = "valid_player20_cookie",
                ["request_code"] = "REDEEM_CODE_REQUEST",
                ["account_id"] = "1",
                ["code"] = "WAVE1PROMO"
            });
            controller.ControllerContext = new ControllerContext { HttpContext = mismatchCtx };

            IActionResult mismatchResult = await controller.StoreRequester(CancellationToken.None);
            PhpResult mismatchPhp = mismatchResult.Should().BeOfType<PhpResult>().Subject;
            Dictionary<string, object> mismatchDict = mismatchPhp.Value.Should().BeOfType<Dictionary<string, object>>().Subject;
            mismatchDict["errorCode"].Should().Be((int)StoreErrorCode.STORE_ACCOUNT_INFORMATION_ERROR);

            promo.TimestampRedeemed.Should().BeNull();
        }
        finally
        {
            UpgradesResolutionService.UnlockAllItems = originalUnlockAll;
        }
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_EnforcesServerHostAuthBeforeIdempotency_And_ReadsMultiSegmentFrames()
    {
        // protects: /api/v5/matches/submit dedicated-server authentication, idempotency DoS prevention, and multi-segment TCP PipeReader framing.
        // fails_when: Unauthenticated callers reach MatchSubmissionIdempotency.TryAcquireAsync or single ReadAsync truncates multi-chunk streams.
        // seam: Real MatchesV5Controller.SubmitBinaryMatch with SlowChunkedMemoryStream and Redis session index.
        MatchFinalizationPayload payload = new()
        {
            MatchId = 778899,
            MapName = "caldavar",
            DurationSeconds = 1500,
            WinningTeam = 1,
            ServerId = 42,
            ServerAddress = "203.0.113.10",
            HostAccountName = "HostServer42",
            MatchType = AccountStatisticsType.Matchmaking,
            Players =
            [
                new PlayerMatchSummaryDto
                {
                    AccountId = 101,
                    AccountName = "PlayerOne",
                    Team = 1,
                    HeroKey = "Hero_Chronos",
                    Level = 20,
                    Kills = 10,
                    Deaths = 2,
                    Assists = 5
                }
            ]
        };
        byte[] payloadBytes = MemoryPackSerializer.Serialize(payload);

        Dictionary<string, RedisValue> sessionHashes = new(StringComparer.Ordinal);
        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, HashEntry[], CommandFlags>((key, entries, _) =>
            {
                if (key == "MATCH-SERVER-SESSIONS")
                {
                    foreach (HashEntry entry in entries)
                    {
                        sessionHashes[entry.Name!] = entry.Value;
                    }
                }
            })
            .Returns(Task.CompletedTask);
        redisMock.Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey _, RedisValue field, CommandFlags _) =>
                sessionHashes.TryGetValue(field.ToString(), out RedisValue val) ? val : RedisValue.Null);
        redisMock.Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        Mock<IMatchProcessingChannel> channelMock = new();
        channelMock.Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        CapturingLogger<MatchesV5Controller> logger = new();

        // 1. Unauthenticated call must fail with 401 BEFORE acquiring idempotency key in Redis
        DefaultHttpContext unauthCtx = new();
        unauthCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77");
        unauthCtx.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller controller = new(channelMock.Object, redisMock.Object, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = unauthCtx }
        };

        IActionResult unauthResult = await controller.SubmitBinaryMatch(CancellationToken.None);
        ObjectResult unauthObj = unauthResult.Should().BeAssignableTo<ObjectResult>().Subject;
        unauthObj.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        redisMock.Verify(
            r => r.StringSetAsync(It.Is<RedisKey>(k => k.ToString().Contains("778899")), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
            Times.Never);

        // 2. Register legitimate dedicated MatchServer session and send fragmented 7-byte chunks
        MatchServer registeredServer = new()
        {
            ID = 42,
            Instance = 1,
            HostAccountID = 500,
            HostAccountName = "HostServer42",
            IPAddress = "203.0.113.10",
            Port = 11235,
            Cookie = "srv_session_v5_valid",
            Name = "Official EU #42",
            Description = "Official Ranked Server",
            Location = "EU"
        };
        await redisMock.Object.SetMatchServer(registeredServer.HostAccountName, registeredServer);

        DefaultHttpContext authedCtx = new();
        authedCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        authedCtx.Request.Headers["X-Server-Session"] = "srv_session_v5_valid";
        authedCtx.Request.Body = new SlowChunkedMemoryStream(payloadBytes, maxChunkSize: 7);

        controller.ControllerContext = new ControllerContext { HttpContext = authedCtx };

        IActionResult authedResult = await controller.SubmitBinaryMatch(CancellationToken.None);
        OkObjectResult okResult = authedResult.Should().BeOfType<OkObjectResult>().Subject;
        MatchFinalizationReceipt receipt = okResult.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;
        receipt.Success.Should().BeTrue();
        receipt.MatchId.Should().Be(778899);
    }

    [Fact]
    public async Task MatchStatsPolicy_ServerHostIsRanked_ListenServerParticipantIsUnrankedZeroMmrZeroCoins_NonParticipantIsQuarantined()
    {
        // protects: Official Dedicated Server (Ranked MMR + Coins) vs P2P Listen Server (Unranked 0 MMR + 0 Coins, participant-verified) policy.
        // fails_when: Listen Server submissions award MMR/Coins, non-participant player cookies are accepted, or resubmit_stats allows non-ServerHost accounts.
        // seam: Real SubmitMatchStatsHandler + MatchProcessingService (MatchIngestionHandler + OpenSkillRatingCalculator + HeroMasteryProgressionService + EconomyService) + StatsRequesterController.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        DbRole sharedUserRole = new() { ID = 2, Name = "USER" };
        db.Roles.Add(sharedUserRole);

        User hostUser = new()
        {
            ID = 500,
            EmailAddress = "host@kongor.local",
            Role = sharedUserRole,
            RoleID = sharedUserRole.ID,
            SRPPasswordSalt = S2Crypto.GenerateSrpSalt(),
            SRPPasswordHash = "host_hash",
            PBKDF2PasswordHash = "host_pbkdf2"
        };
        Account hostAccount = new()
        {
            ID = 500,
            UserID = 500,
            User = hostUser,
            Name = "OfficialHost",
            Type = AccountType.ServerHost,
            IsMain = true
        };

        string normalSalt = S2Crypto.GenerateSrpSalt();
        string normalSecret = "plr-" + Guid.NewGuid().ToString("N");
        User p1User = new()
        {
            ID = 101,
            EmailAddress = "p1@kongor.local",
            Role = sharedUserRole,
            RoleID = sharedUserRole.ID,
            SRPPasswordSalt = normalSalt,
            SRPPasswordHash = S2Crypto.ComputeSrpFromPlaintext(normalSecret, normalSalt),
            PBKDF2PasswordHash = "p1_pbkdf2",
            GoldCoins = 0,
            SilverCoins = 0
        };
        Account p1Account = new()
        {
            ID = 101,
            UserID = 101,
            User = p1User,
            Name = "LegionPlayer",
            Type = AccountType.Normal,
            IsMain = true
        };

        User p2User = new()
        {
            ID = 102,
            EmailAddress = "p2@kongor.local",
            Role = sharedUserRole,
            RoleID = sharedUserRole.ID,
            SRPPasswordSalt = "s2",
            SRPPasswordHash = "h2",
            PBKDF2PasswordHash = "p2_pbkdf2",
            GoldCoins = 0,
            SilverCoins = 0
        };
        Account p2Account = new()
        {
            ID = 102,
            UserID = 102,
            User = p2User,
            Name = "HellbournePlayer",
            Type = AccountType.Normal,
            IsMain = true
        };

        User outsiderUser = new()
        {
            ID = 999,
            EmailAddress = "outsider@kongor.local",
            Role = sharedUserRole,
            RoleID = sharedUserRole.ID,
            SRPPasswordSalt = "s9",
            SRPPasswordHash = "h9",
            PBKDF2PasswordHash = "p9_pbkdf2"
        };
        Account outsiderAccount = new()
        {
            ID = 999,
            UserID = 999,
            User = outsiderUser,
            Name = "OutsiderSpoofer",
            Type = AccountType.Normal,
            IsMain = true
        };

        db.Users.AddRange(hostUser, p1User, p2User, outsiderUser);
        db.Accounts.AddRange(hostAccount, p1Account, p2Account, outsiderAccount);
        await db.SaveChangesAsync();

        Dictionary<string, RedisValue> sessionHashes = new(StringComparer.Ordinal);
        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, HashEntry[], CommandFlags>((key, entries, _) =>
            {
                if (key == "MATCH-SERVER-SESSIONS")
                {
                    foreach (HashEntry entry in entries) sessionHashes[entry.Name!] = entry.Value;
                }
            })
            .Returns(Task.CompletedTask);
        redisMock.Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey _, RedisValue field, CommandFlags _) =>
                sessionHashes.TryGetValue(field.ToString(), out RedisValue val) ? val : RedisValue.Null);
        redisMock.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("p1_session")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("LegionPlayer");
        redisMock.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("outsider_session")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("OutsiderSpoofer");
        redisMock.Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        MatchServer officialServer = new()
        {
            ID = 77,
            Instance = 1,
            HostAccountID = 500,
            HostAccountName = "OfficialHost",
            IPAddress = "203.0.113.77",
            Port = 11235,
            Cookie = "dedicated_srv_cookie",
            Name = "Official Ranked #77",
            Description = "Official Ranked Server",
            Location = "US"
        };
        await redisMock.Object.SetMatchServer(officialServer.HostAccountName, officialServer);

        List<MatchSubmissionPayload> enqueuedPayloads = [];
        Mock<IMatchProcessingChannel> channelMock = new();
        channelMock.Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .Callback<MatchSubmissionPayload, CancellationToken>((p, _) => enqueuedPayloads.Add(p))
            .ReturnsAsync(true);

        List<DeadLetterMatchJob> quarantinedJobs = [];
        Mock<IMatchDeadLetterQueue> dlqMock = new();
        dlqMock.Setup(q => q.EnqueueAsync(It.IsAny<DeadLetterMatchJob>(), It.IsAny<CancellationToken>()))
            .Callback<DeadLetterMatchJob, CancellationToken>((j, _) => quarantinedJobs.Add(j))
            .Returns(ValueTask.CompletedTask);

        AccountRepository accountRepo = new(db, NullLogger<AccountRepository>.Instance);
        CapturingLogger<SubmitMatchStatsHandler> submitLogger = new();
        SubmitMatchStatsHandler submitHandler = new(
            accountRepo,
            redisMock.Object,
            channelMock.Object,
            submitLogger,
            dlqMock.Object);

        static StatsForSubmissionRequestForm BuildMatchForm(int matchId, string session, string function = "submit_stats") => new()
        {
            Function = function,
            Session = session,
            MatchStats = new Dictionary<string, string>
            {
                ["match_id"] = matchId.ToString(),
                ["map"] = "caldavar",
                ["map_version"] = "4.10.0",
                ["time_played"] = "1800",
                ["Version"] = "4.10.0.0",
                ["r_t"] = "1",
                ["mvp"] = "101",
                ["nm"] = "1",
                ["winner"] = "1"
            },
            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>
            {
                [101] = new()
                {
                    ["Hero_Valkyrie"] = new()
                    {
                        ["nickname"] = "LegionPlayer",
                        ["team"] = "1",
                        ["cli_name"] = "Hero_Valkyrie",
                        ["wins"] = "1",
                        ["losses"] = "0",
                        ["amm_team_count"] = "1",
                        ["herokills"] = "8",
                        ["deaths"] = "2",
                        ["heroassists"] = "6",
                        ["secs"] = "1800",
                        ["level"] = "20"
                    }
                },
                [102] = new()
                {
                    ["Hero_Magmus"] = new()
                    {
                        ["nickname"] = "HellbournePlayer",
                        ["team"] = "2",
                        ["cli_name"] = "Hero_Magmus",
                        ["wins"] = "0",
                        ["losses"] = "1",
                        ["amm_team_count"] = "1",
                        ["herokills"] = "2",
                        ["deaths"] = "8",
                        ["heroassists"] = "1",
                        ["secs"] = "1800",
                        ["level"] = "16"
                    }
                }
            }
        };

        Mock<IHeroDefinitionService> heroDefs = new();
        heroDefs.Setup(h => h.GetBaseHeroId("Hero_Valkyrie")).Returns(20u);
        heroDefs.Setup(h => h.GetBaseHeroId("Hero_Magmus")).Returns(21u);

        EconomyService economyService = new(db, NullLogger<EconomyService>.Instance);
        MatchProcessingService processingService = new(
            new MatchIngestionHandler(db, heroDefs.Object, NullLogger<MatchIngestionHandler>.Instance),
            new HeroMasteryProgressionService(db, economyService, MasteryRewardService.Instance, NullLogger<HeroMasteryProgressionService>.Instance),
            new OpenSkillRatingCalculator(db, new OpenSkillRatingService(), NullLogger<OpenSkillRatingCalculator>.Instance),
            new ReplayPersistenceWorker(db, Mock.Of<IPlayerStatisticsService>(), redisMock.Object, NullLogger<ReplayPersistenceWorker>.Instance),
            NullLogger<MatchProcessingService>.Instance,
            db);

        // 1. Non-participant Listen Server player cookie -> Quarantined, NOT enqueued
        SubmitMatchStatsResponse nonParticipantResp = await submitHandler.HandleAsync(
            new SubmitMatchStatsCommand(new MatchId(9001), "outsider_session", BuildMatchForm(9001, "outsider_session"), "198.51.100.90"));
        nonParticipantResp.Success.Should().BeTrue();
        enqueuedPayloads.Should().BeEmpty();
        quarantinedJobs.Should().ContainSingle(j => j.MatchId == 9001);

        // 2. Participant Listen Server player cookie -> Enqueued as Unranked: history saved, 0 MMR delta, 0 Coins
        SubmitMatchStatsResponse listenParticipantResp = await submitHandler.HandleAsync(
            new SubmitMatchStatsCommand(new MatchId(9002), "p1_session", BuildMatchForm(9002, "p1_session"), "198.51.100.91"));
        listenParticipantResp.Success.Should().BeTrue();
        enqueuedPayloads.Should().ContainSingle(p => p.MatchID == 9002);

        await processingService.ProcessMatchSubmissionAsync(enqueuedPayloads.Single(p => p.MatchID == 9002));

        (await db.MatchStatistics.AnyAsync(m => m.MatchID == 9002)).Should().BeTrue("Unranked P2P match history must still be saved");
        PlayerStatistics p1UnrankedStats = await db.PlayerStatistics.SingleAsync(ps => ps.MatchID == 9002 && ps.AccountID == 101);
        p1UnrankedStats.RankedMatch.Should().Be(0);
        p1UnrankedStats.RankedSkillRatingChange.Should().Be(0);
        p1UnrankedStats.PublicSkillRatingChange.Should().Be(0);

        AccountStatistics? p1StandingAfterUnranked = await db.AccountStatistics
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.AccountID == 101 && s.Type == AccountStatisticsType.Matchmaking);
        if (p1StandingAfterUnranked is not null)
        {
            p1StandingAfterUnranked.SkillRating.Should().BeApproximately(1500.0, 0.001);
        }

        User p1AfterUnranked = await db.Users.AsNoTracking().SingleAsync(u => u.ID == 101);
        p1AfterUnranked.SilverCoins.Should().Be(0, "P2P Listen Server matches must award 0 Silver Coins");
        p1AfterUnranked.GoldCoins.Should().Be(0, "P2P Listen Server matches must award 0 Gold Coins");

        // 3. Registered Dedicated Server (ServerHost) -> Ranked MMR updated + Coins awarded
        SubmitMatchStatsResponse officialResp = await submitHandler.HandleAsync(
            new SubmitMatchStatsCommand(new MatchId(9003), "dedicated_srv_cookie", BuildMatchForm(9003, "dedicated_srv_cookie"), "203.0.113.77"));
        officialResp.Success.Should().BeTrue();
        await processingService.ProcessMatchSubmissionAsync(enqueuedPayloads.Single(p => p.MatchID == 9003));

        AccountStatistics p1StandingAfterRanked = await db.AccountStatistics
            .AsNoTracking()
            .SingleAsync(s => s.AccountID == 101 && s.Type == AccountStatisticsType.Matchmaking);
        p1StandingAfterRanked.SkillRating.Should().BeGreaterThan(1500.0);

        User p1AfterRanked = await db.Users.AsNoTracking().SingleAsync(u => u.ID == 101);
        (p1AfterRanked.SilverCoins + p1AfterRanked.GoldCoins).Should().BeGreaterThan(0);

        // 4. resubmit_stats with a Normal player account must be rejected with 401 Unauthorized
        StatsRequesterController statsController = new(
            accountRepo,
            redisMock.Object,
            channelMock.Object,
            NullLogger<StatsRequesterController>.Instance,
            submitHandler);

        string md5Pass = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(normalSecret)));
        string resubKey = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("9004s8c7xaduxAbRanaspUf3kadRachecrac9efeyupr8suwrewecrUphayeweqUmana")));

        StatsForSubmissionRequestForm resubForm = BuildMatchForm(9004, "", "resubmit_stats");
        resubForm.HostAccountName = "LegionPlayer:";
        resubForm.HostAccountPasswordHash = md5Pass;
        resubForm.StatsResubmissionKey = resubKey;
        resubForm.ServerID = 77;

        DefaultHttpContext resubCtx = new();
        resubCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.91");
        resubCtx.Request.ContentType = "application/x-www-form-urlencoded";
        resubCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "resubmit_stats"
        });
        statsController.ControllerContext = new ControllerContext { HttpContext = resubCtx };

        IActionResult resubResult = await statsController.StatsRequester(resubForm, CancellationToken.None);
        resubResult.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task MiniGame_And_TicketExchange_MidFlightFailure_NeverBurnsPlayerCoinsOrTickets()
    {
        // protects: Plinko Drop and Ticket Exchange atomicity so mid-request persistence failures never permanently burn currency.
        // fails_when: EconomyService.DeductCurrenciesAsync commits SaveChangesAsync before reward/inventory persistence fails without rollback/compensation.
        // seam: Real MiniGameController + TicketExchangeController + EconomyService against MerrickContext.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user = new()
        {
            ID = 70,
            EmailAddress = "plinko_atomic@kongor.local",
            Role = new DbRole { ID = 2, Name = "USER" },
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p",
            GoldCoins = 1000,
            SilverCoins = 2000,
            PlinkoTickets = 2000,
            InventoryItems = []
        };
        Account account = new()
        {
            ID = 700,
            UserID = 70,
            User = user,
            Name = "AtomicPlinkoHero",
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("atomic_cookie")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("AtomicPlinkoHero");

        Mock<IAccountRepository> throwingRepo = new();
        throwingRepo.Setup(r => r.GetByNicknameAsync("AtomicPlinkoHero", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
                db.Accounts.Include(a => a.User).ThenInclude(u => u.InventoryItems).FirstOrDefaultAsync(a => a.ID == 700, ct));
        throwingRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated mid-flight reward persistence crash"));

        EconomyService economyService = new(db, NullLogger<EconomyService>.Instance);

        // 1. MiniGameController Drop failure must leave GoldCoins == 1000
        MiniGameController miniGame = new(
            throwingRepo.Object,
            redisMock.Object,
            NullLogger<MiniGameController>.Instance,
            economyService);

        DefaultHttpContext dropCtx = new();
        dropCtx.Request.ContentType = "application/x-www-form-urlencoded";
        dropCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "atomic_cookie",
            ["currency"] = "gold"
        });
        miniGame.ControllerContext = new ControllerContext { HttpContext = dropCtx };

        IActionResult dropResult = await miniGame.Drop(CancellationToken.None);
        PhpResult dropPhp = dropResult.Should().BeOfType<PhpResult>().Subject;
        dropPhp.Data.Should().BeOfType<PlinkoDropResponseDTO>().Which.StatusCode.Should().Be(0);

        User afterFailedDrop = await db.Users.AsNoTracking().SingleAsync(u => u.ID == 70);
        afterFailedDrop.GoldCoins.Should().Be(1000, "Failed Plinko drop must not burn GoldCoins");

        // 2. TicketExchangeController Purchase failure must leave PlinkoTickets == 2000 (not deduct 1300 permanently)
        TicketExchangeController exchange = new(
            throwingRepo.Object,
            redisMock.Object,
            NullLogger<TicketExchangeController>.Instance,
            economyService);

        DefaultHttpContext exCtx = new();
        exCtx.Request.ContentType = "application/x-www-form-urlencoded";
        exCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "atomic_cookie",
            ["id"] = "1792"
        });
        exchange.ControllerContext = new ControllerContext { HttpContext = exCtx };

        IActionResult exResult = await exchange.Purchase(CancellationToken.None);
        PhpResult exPhp = exResult.Should().BeOfType<PhpResult>().Subject;
        exPhp.Data.Should().BeOfType<TicketExchangePurchaseResponseDTO>().Which.StatusCode.Should().Be(0);

        User afterFailedExchange = await db.Users.AsNoTracking().SingleAsync(u => u.ID == 70);
        afterFailedExchange.PlinkoTickets.Should().Be(2000, "Failed Ticket Exchange purchase must not burn PlinkoTickets");
    }

    [Fact]
    public async Task PerimeterHardening_PresenceHubAuth_ReportBatching_StorageLimitsAndRedaction_And_UntrustedForwardedIp()
    {
        // protects: SignalR PresenceHub state integrity, ReportController O(1) batched queries, StorageController upload bounds + 500 stack-trace redaction, and spoofed X-Forwarded-For rejection.
        // fails_when: Anonymous PresenceHub callers overwrite Redis presence, ReportController loops N queries, StorageController leaks StackTrace or accepts oversized cloud.zip, or direct WAN peers spoof CF-Connecting-IP.
        // seam: Real PresenceHub, ReportController, StorageController, and SwitchAuthHandler.

        // 1. PresenceHub must reject unauthenticated callers from overwriting Redis presence:dto:{accountId}
        Mock<IConnectionMultiplexer> muxMock = new();
        Mock<IDatabase> redisMock = new();
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(redisMock.Object);

        Mock<HubCallerContext> hubCallerMock = new();
        hubCallerMock.SetupGet(c => c.ConnectionId).Returns("conn_anon_1");
        hubCallerMock.SetupGet(c => c.User).Returns(new System.Security.Claims.ClaimsPrincipal());

        Mock<IGroupManager> groupManagerMock = new();
        Mock<IHubCallerClients> hubClientsMock = new();
        Mock<ISingleClientProxy> callerProxyMock = new();
        hubClientsMock.SetupGet(c => c.Caller).Returns(callerProxyMock.Object);

        using MerrickContext hubDb = TestDbContextFactory.CreateInMemoryContext();
        PresenceHub hub = new(hubDb, muxMock.Object, NullLogger<PresenceHub>.Instance)
        {
            Context = hubCallerMock.Object,
            Groups = groupManagerMock.Object,
            Clients = hubClientsMock.Object
        };

        await hub.SubscribeToPresence(777);

        redisMock.Verify(
            r => r.StringSetAsync(It.Is<RedisKey>(k => k == "presence:dto:777"), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
            Times.Never);

        // 2. ReportController must require valid session and batch-resolve accounts instead of 2*N per-row queries
        Mock<IAccountRepository> reportRepoMock = new();
        Account reporter = new()
        {
            ID = 10,
            Name = "ReporterHero",
            User = new User { ID = 10, EmailAddress = "r@kongor.local", SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p" }
        };
        Dictionary<int, Account> targetsById = Enumerable.Range(101, 5).ToDictionary(
            id => id,
            id => new Account
            {
                ID = id,
                Name = $"Target_{id}",
                User = new User { ID = id, EmailAddress = $"t{id}@kongor.local", SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p" }
            });

        reportRepoMock.Setup(r => r.GetAccountsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetsById.Values.ToList());

        Mock<ISessionContextResolver> sessionResolverMock = new();
        sessionResolverMock.Setup(s => s.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reporter);

        ReportController reportController = new(
            reportRepoMock.Object,
            Mock.Of<IMatchHistoryRepository>(),
            sessionResolverMock.Object,
            NullLogger<ReportController>.Instance);

        Dictionary<string, StringValues> reportForm = new()
        {
            ["match_id"] = "445566"
        };
        for (int i = 0; i < 5; i++)
        {
            reportForm[$"player_report[{i}][reported_id]"] = (101 + i).ToString();
            reportForm[$"player_report[{i}][reason]"] = "Griefing";
            reportForm[$"player_report[{i}][comment]"] = "Test report";
        }

        DefaultHttpContext reportCtx = new();
        reportCtx.Request.ContentType = "application/x-www-form-urlencoded";
        reportCtx.Request.Form = new FormCollection(reportForm);
        reportController.ControllerContext = new ControllerContext { HttpContext = reportCtx };

        IActionResult reportRes = await reportController.SubmitReport(CancellationToken.None);
        reportRes.Should().BeOfType<PhpResult>();
        reportRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "ReportController must batch target lookups instead of issuing N individual GetByIdAsync calls");

        // 3. StorageController must cap cloud.zip size and NEVER leak StackTrace in HTTP 500 responses
        Account storageAccount = new()
        {
            ID = 42,
            Name = "StorageHero",
            User = new User { ID = 42, EmailAddress = "st@kongor.local", SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p" }
        };
        Mock<IAccountRepository> storageAccountRepo = new();
        storageAccountRepo.Setup(r => r.GetByNicknameAsync("StorageHero", It.IsAny<CancellationToken>()))
            .ReturnsAsync(storageAccount);
        Mock<ISessionRepository> storageSessionRepo = new();
        storageSessionRepo.Setup(s => s.GetAccountNameForSessionCookieAsync("storage_cookie", It.IsAny<CancellationToken>()))
            .ReturnsAsync("StorageHero");
        Mock<IWebHostEnvironment> envMock = new();
        Mock<IConfiguration> cfgMock = new();
        Mock<IMediator> failingMediator = new();
        failingMediator.Setup(m => m.Send(It.IsAny<StoreCloudConfigCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database secret path failure"));

        StorageController storageController = new(
            storageAccountRepo.Object,
            storageSessionRepo.Object,
            redisMock.Object,
            envMock.Object,
            cfgMock.Object,
            supabaseStorageClient: null,
            logger: NullLogger<StorageController>.Instance,
            mediator: failingMediator.Object);

        // 3a. Exception redaction check
        byte[] smallBytes = [0x50, 0x4B, 0x05, 0x06, 0, 0];
        IFormFile smallFile = new FormFile(new MemoryStream(smallBytes), 0, smallBytes.Length, "file", "cloud.zip");
        DefaultHttpContext storageErrCtx = new();
        storageErrCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "storage_cookie",
            ["bucket"] = "configs"
        }, new FormFileCollection { smallFile });
        storageController.ControllerContext = new ControllerContext { HttpContext = storageErrCtx };

        IActionResult errRes = await storageController.Store("storage_cookie", "configs", null, smallFile, CancellationToken.None);
        ObjectResult errObj = errRes.Should().BeAssignableTo<ObjectResult>().Subject;
        errObj.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        string serializedErr = System.Text.Json.JsonSerializer.Serialize(errObj.Value);
        serializedErr.Should().NotContain("StackTrace");
        serializedErr.Should().NotContain("InvalidOperationException");

        // 3b. Oversized upload (> 5 MB) rejected with 400/413
        IFormFile hugeFile = new FormFile(new MemoryStream([1, 2, 3, 4]), 0, 10 * 1024 * 1024, "file", "cloud.zip");
        IActionResult hugeRes = await storageController.Store("storage_cookie", "configs", null, hugeFile, CancellationToken.None);
        ObjectResult hugeObj = hugeRes.Should().BeAssignableTo<ObjectResult>().Subject;
        hugeObj.StatusCode.Should().BeOneOf(StatusCodes.Status400BadRequest, StatusCodes.Status413PayloadTooLarge);

        // 4. Untrusted direct peer IP sending spoofed X-Forwarded-For must NOT override Connection.RemoteIpAddress in SwitchAuthHandler
        using MerrickContext switchDb = TestDbContextFactory.CreateInMemoryContext();
        User swUser = new()
        {
            ID = 50,
            EmailAddress = "sw@kongor.local",
            Role = new DbRole { ID = 2, Name = "USER" },
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account mainSw = new() { ID = 501, UserID = 50, User = swUser, Name = "MainHero", IsMain = true };
        Account subSw = new() { ID = 502, UserID = 50, User = swUser, Name = "SubHero", IsMain = false };
        swUser.Accounts = [mainSw, subSw];
        switchDb.Users.Add(swUser);
        switchDb.Accounts.AddRange(mainSw, subSw);
        await switchDb.SaveChangesAsync();

        Mock<IDatabase> swRedis = new();
        swRedis.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("sw_cookie")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("MainHero");

        IPAddress? observedClientIp = null;
        Mock<INetworkTopologyResolver> swNet = new();
        swNet.Setup(n => n.ResolveChatServer(It.IsAny<IPAddress>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<IPAddress, string?, string, string>((ip, _, _, _) => observedClientIp = ip)
            .Returns(("127.0.0.1", 11031));

        SwitchAuthHandler swHandler = new(
            new AccountRepository(switchDb, NullLogger<AccountRepository>.Instance),
            swRedis.Object,
            swNet.Object,
            Mock.Of<IEventBus>(),
            CreateOperationalOptions(),
            NullLogger<SwitchAuthHandler>.Instance);

        DefaultHttpContext spoofCtx = new();
        spoofCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.88");
        spoofCtx.Request.Headers["X-Forwarded-For"] = "203.0.113.1";
        spoofCtx.Request.ContentType = "application/x-www-form-urlencoded";
        spoofCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "switch_auth",
            ["cookie"] = "sw_cookie",
            ["login"] = "SubHero"
        });

        await swHandler.HandleRequestAsync(spoofCtx, CancellationToken.None);
        observedClientIp.Should().Be(IPAddress.Parse("198.51.100.88"),
            "Direct untrusted WAN peers must not spoof X-Forwarded-For to forge ClientIPAddress");
    }
}
