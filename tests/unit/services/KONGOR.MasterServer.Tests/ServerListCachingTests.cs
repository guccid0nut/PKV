// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class ServerListCachingTests
{
    private static DefaultHttpContext CreateHttpContext(string? cookie, string? gameType, string? region = "USE")
    {
        DefaultHttpContext context = new();
        List<string> queryParts = [];
        Dictionary<string, StringValues> formOrQuery = [];

        if (cookie != null)
        {
            queryParts.Add($"cookie={Uri.EscapeDataString(cookie)}");
            formOrQuery["cookie"] = new(cookie);
        }

        if (gameType != null)
        {
            queryParts.Add($"gametype={Uri.EscapeDataString(gameType)}");
            formOrQuery["gametype"] = new(gameType);
        }

        if (region != null)
        {
            queryParts.Add($"region={Uri.EscapeDataString(region)}");
            formOrQuery["region"] = new(region);
        }

        if (queryParts.Count > 0)
        {
            context.Request.QueryString = new QueryString("?" + string.Join("&", queryParts));
        }

        context.Request.Query = new QueryCollection(formOrQuery);
        return context;
    }

    [Fact]
    public async Task HandleRequestAsync_WithHybridCacheHit_ReturnsPhpResultWithCachedPayload()
    {
        // Arrange
        Mock<IDatabase> mockDatabase = new();
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<INexusHybridCache> mockHybridCache = new();

        const string preSerializedPayload = "a:1:{s:4:\"test\";i:42;}";

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                It.Is<string>(k => k.StartsWith("SERVER-LIST:10:")),
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("server-list")),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(2)),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(5)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(preSerializedPayload);

        ServerListHandler handler = new(
            mockDatabase.Object,
            memoryCache,
            NullLogger<ServerListHandler>.Instance,
            mockHybridCache.Object);

        DefaultHttpContext context = CreateHttpContext("test_session_cookie", "10", "USE");

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.Data.Should().Be(preSerializedPayload);

        mockHybridCache.Verify(
            c => c.GetOrCreateAsync<string>(
                It.Is<string>(k => k.Contains("SERVER-LIST:10")),
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequestAsync_WithHybridCacheMiss_InvokesFactoryAndSerializesServers()
    {
        // Arrange
        Mock<IDatabase> mockDatabase = new();
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<INexusHybridCache> mockHybridCache = new();

        List<MatchServer> servers =
        [
            new MatchServer
            {
                ID = 1,
                HostAccountID = 100,
                HostAccountName = "Host1",
                Instance = 1,
                Name = "USE_Server_1",
                IPAddress = "192.168.1.10",
                Port = 11235,
                Location = "USE",
                Description = "US East Server",
                Status = ServerStatus.SERVER_STATUS_ACTIVE
            }
        ];

        // Setup Redis hash returning JSON serialized single MatchServer
        byte[] serverBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(servers[0], MasterServerJsonContext.Default.MatchServer);
        mockDatabase.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([new HashEntry("Host1:1", serverBytes)]);

        // Setup HybridCache to execute the underlying factory
        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<CancellationToken, Task<string?>>, IReadOnlyCollection<string>?, TimeSpan?, TimeSpan?, CancellationToken>(
                async (key, factory, tags, l1, l2, ct) => await factory(ct));

        ServerListHandler handler = new(
            mockDatabase.Object,
            memoryCache,
            NullLogger<ServerListHandler>.Instance,
            mockHybridCache.Object);

        DefaultHttpContext context = CreateHttpContext("test_session_cookie", "10", "USE");

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.Data.Should().NotBeNull();
        string payload = (string)phpResult.Data!;
        payload.Should().Contain("USE_Server_1");
    }

    [Fact]
    public async Task HandleRequestAsync_WithoutHybridCache_FallsBackToMemoryCache()
    {
        // Arrange
        Mock<IDatabase> mockDatabase = new();
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());

        List<MatchServer> servers =
        [
            new MatchServer
            {
                ID = 2,
                HostAccountID = 101,
                HostAccountName = "Host2",
                Instance = 1,
                Name = "USW_Server_2",
                IPAddress = "192.168.1.20",
                Port = 11236,
                Location = "USW",
                Description = "US West Server"
            }
        ];

        byte[] serverBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(servers[0], MasterServerJsonContext.Default.MatchServer);
        mockDatabase.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([new HashEntry("Host2:1", serverBytes)]);

        // Instantiate handler with null hybridCache (backward compatibility)
        ServerListHandler handler = new(
            mockDatabase.Object,
            memoryCache,
            NullLogger<ServerListHandler>.Instance,
            hybridCache: null);

        DefaultHttpContext context = CreateHttpContext("cookie_xyz", "90", "USW");

        // Act
        IActionResult result1 = await handler.HandleRequestAsync(context);
        IActionResult result2 = await handler.HandleRequestAsync(context);

        // Assert
        result1.Should().BeOfType<PhpResult>();
        result2.Should().BeOfType<PhpResult>();

        // Second call should have hit MemoryCache without querying Redis again
        mockDatabase.Verify(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task HandleRequestAsync_WithInvalidGameType_ReturnsUnprocessableEntity()
    {
        // Arrange
        Mock<IDatabase> mockDatabase = new();
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<INexusHybridCache> mockHybridCache = new();

        ServerListHandler handler = new(
            mockDatabase.Object,
            memoryCache,
            NullLogger<ServerListHandler>.Instance,
            mockHybridCache.Object);

        DefaultHttpContext context = CreateHttpContext("valid_cookie", "999", "ALL");

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        mockHybridCache.Verify(
            c => c.GetOrCreateAsync<string>(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequestAsync_MissingCookie_ReturnsBadRequest()
    {
        // Arrange
        Mock<IDatabase> mockDatabase = new();
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<INexusHybridCache> mockHybridCache = new();

        ServerListHandler handler = new(
            mockDatabase.Object,
            memoryCache,
            NullLogger<ServerListHandler>.Instance,
            mockHybridCache.Object);

        DefaultHttpContext context = CreateHttpContext(null, "10", "ALL");

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        BadRequestObjectResult badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Missing cookie");
    }
}
