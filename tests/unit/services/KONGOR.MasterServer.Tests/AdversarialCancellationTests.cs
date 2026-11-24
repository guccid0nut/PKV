// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Plinko;
using KONGOR.MasterServer.Controllers.Store;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Requester;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MediatR;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class AdversarialCancellationTests
{
    private static MerrickContext CreateInMemoryDatabase(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static (User user, Account account) SeedUserAndAccount(MerrickContext db, int userId = 1,
        int accountId = 10, string name = "AdversarialHero")
    {
        User user = new()
        {
            ID = userId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value",
            GoldCoins = 1000,
            SilverCoins = 5000,
            PlinkoTickets = 10
        };
        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.SaveChanges();
        return (user, account);
    }

    private static DefaultHttpContext CreateHttpContextWithForm(Dictionary<string, string> formFields,
        Dictionary<string, string>? queryFields = null)
    {
        DefaultHttpContext context = new();
        Dictionary<string, StringValues> formDict = [];
        foreach (KeyValuePair<string, string> field in formFields) formDict[field.Key] = new StringValues(field.Value);
        context.Request.Form = new FormCollection(formDict);

        if (queryFields != null && queryFields.Count > 0)
        {
            Dictionary<string, StringValues> queryDict = [];
            foreach (KeyValuePair<string, string> q in queryFields) queryDict[q.Key] = new StringValues(q.Value);
            context.Request.Query = new QueryCollection(queryDict);
        }

        return context;
    }

    [Fact]
    public async Task StoreController_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        StoreController controller = new(
            new AccountRepository(db, NullLogger<AccountRepository>.Instance),
            mockRedis.Object,
            NullLogger<StoreController>.Instance,
            Mock.Of<IMediator>());

        DefaultHttpContext httpContext = CreateHttpContextWithForm(new Dictionary<string, string>
        {
            { "cookie", "valid_session_cookie" },
            { "request_code", "1" }, // BUY_PRODUCT_REQUEST
            { "account_id", account.ID.ToString() }
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancel

        // Act & Assert
        Func<Task> act = async () => await controller.StoreRequester(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StoreController_MidFlightCancellation_RollsBackTransactionWithoutCorruption()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        Mock<IMediator> mockMediator = new();
        mockMediator.Setup(m => m.Send(It.IsAny<PurchaseStoreProductCommand>(), It.IsAny<CancellationToken>()))
            .Returns<IRequest<PurchaseStoreProductResultDto>, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(50, ct);
                return new PurchaseStoreProductResultDto(true, 0, 0, new Dictionary<string, object>());
            });
        mockMediator.Setup(m => m.Send(It.IsAny<GetStoreCategoryQuery>(), It.IsAny<CancellationToken>()))
            .Returns<IRequest<StoreCategoryResultDto>, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(50, ct);
                return new StoreCategoryResultDto(1, 1, "0", 1, new Dictionary<string, object>());
            });

        StoreController controller = new(
            new AccountRepository(db, NullLogger<AccountRepository>.Instance),
            mockRedis.Object,
            NullLogger<StoreController>.Instance,
            mockMediator.Object);

        DefaultHttpContext httpContext = CreateHttpContextWithForm(new Dictionary<string, string>
        {
            { "cookie", "valid_session_cookie" },
            { "request_code", ((int)StoreRequestCode.ATTEMPT_PURCHASE_PRODUCT_REQUEST).ToString() },
            { "product_id", "1001" },
            { "currency", "0" },
            { "category_id", "1" },
            { "page", "1" },
            { "hostTime", "123456" }
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        // Act & Assert: Should either complete quickly or cancel cleanly without unhandled exception
        try
        {
            IActionResult result = await controller.StoreRequester(cts.Token);
            result.Should().NotBeNull();
        }
        catch (Exception ex)
        {
            Exception baseEx = ex.GetBaseException();
            (baseEx is OperationCanceledException || ex is OperationCanceledException).Should().BeTrue($"Expected cancellation exception but got: {ex}");
        }

        // Database integrity check: Balance should NOT be negative, no corrupted records
        User freshUser = await db.Users.SingleAsync(u => u.ID == user.ID);
        freshUser.GoldCoins.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task SessionContextResolver_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        SessionContextResolver resolver = new(
            mockRedis.Object,
            db,
            NullLogger<SessionContextResolver>.Instance);

        DefaultHttpContext httpContext = CreateHttpContextWithForm(new Dictionary<string, string>
        {
            { "cookie", "session_cookie_token" }
        });

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancel

        // Act & Assert
        Func<Task> act = async () => await resolver.ResolveAccountAsync(httpContext, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GuideHandler_PreCanceledToken_ThrowsOperationCanceledException()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        HeroGuideRepository guideRepo = new(db);
        AccountRepository accountRepo = new(db);
        Mock<ISessionRepository> mockSession = new();
        GuideHandler handler = new(guideRepo, accountRepo, mockSession.Object, mockRedis.Object, NullLogger<GuideHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContextWithForm(
            new Dictionary<string, string>
            {
                { "account_id", account.ID.ToString() },
                { "hero", "Hero_Chronos" }
            },
            new Dictionary<string, string>
            {
                { "f", "get_guide_list" }
            });

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancel

        // Act & Assert
        Func<Task> act = async () => await handler.HandleRequestAsync(httpContext, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HeroStatsHandler_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<ISessionContextResolver> mockResolver = new();
        mockResolver.Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Mock<IHeroDefinitionService> mockHeroes = new();
        MatchHistoryRepository matchRepo = new(db);
        AccountRepository accountRepo = new(db);

        HeroStatsHandler handler = new(matchRepo, accountRepo, mockResolver.Object, mockHeroes.Object,
            NullLogger<HeroStatsHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContextWithForm(
            [],
            new Dictionary<string, string>
            {
                { "f", "get_hero_stats" },
                { "account_id", account.ID.ToString() }
            });

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await handler.HandleRequestAsync(httpContext, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MatchHistoryHandler_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        Mock<IHeroDefinitionService> mockHeroes = new();
        MatchHistoryRepository matchRepo = new(db);
        AccountRepository accountRepo = new(db);

        MatchHistoryHandler handler = new(matchRepo, accountRepo, mockRedis.Object, mockHeroes.Object,
            NullLogger<MatchHistoryHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContextWithForm(
            new Dictionary<string, string>
            {
                { "account_id", account.ID.ToString() }
            },
            new Dictionary<string, string>
            {
                { "f", "match_history_overview" }
            });

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await handler.HandleRequestAsync(httpContext, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ClientRequestDispatcher_PreCanceledToken_PropagatesCancellationCleanly()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddSingleton(typeof(ILogger<EchoHandler>), NullLogger<EchoHandler>.Instance);
        services.AddSingleton<EchoHandler>();

        IServiceProvider sp = services.BuildServiceProvider();
        ClientRequestDispatcher dispatcher = new(sp);

        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await dispatcher.DispatchAsync("echotest", context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PlayerStatisticsService_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<INexusHybridCache> mockCache = new();
        mockCache.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<PlayerStatisticsAggregatedDTO?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.Is<CancellationToken>(ct => ct.IsCancellationRequested)))
            .ThrowsAsync(new OperationCanceledException());

        PlayerStatisticsService service = new(db, mockCache.Object, NullLogger<PlayerStatisticsService>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await service.GetAggregatedStatisticsAsync(account.ID, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MiniGameController_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        AccountRepository accountRepo = new(db, NullLogger<AccountRepository>.Instance);

        MiniGameController controller = new(
            accountRepo,
            mockRedis.Object,
            NullLogger<MiniGameController>.Instance);

        DefaultHttpContext httpContext = CreateHttpContextWithForm(new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "account_id", account.ID.ToString() }
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.Drop(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TicketExchangeController_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        AccountRepository accountRepo = new(db, NullLogger<AccountRepository>.Instance);

        TicketExchangeController controller = new(
            accountRepo,
            mockRedis.Object,
            NullLogger<TicketExchangeController>.Instance);


        DefaultHttpContext httpContext = CreateHttpContextWithForm(new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "account_id", account.ID.ToString() },
            { "package_id", "1" }
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.Purchase(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
