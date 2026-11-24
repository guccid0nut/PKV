// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Services.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using StackExchange.Redis;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class PrebakedCatalogCacheTests
{
    [Fact]
    public void PrebakedCatalogCache_Instance_BakesValidBuffers()
    {
        IPrebakedCatalogCache cache = PrebakedCatalogCache.Instance;

        Assert.NotNull(cache);
        Assert.True(cache.PrebakedPhpProductsBytes.Length > 0, "PrebakedPhpProductsBytes should not be empty");
        Assert.True(cache.PrebakedJsonProductsBytes.Length > 0, "PrebakedJsonProductsBytes should not be empty");
        Assert.True(cache.PrebakedPhpHeroesBytes.Length > 0, "PrebakedPhpHeroesBytes should not be empty");
        Assert.NotEqual(0, cache.ProductsCrc);
        Assert.Equal(global::ASPIRE.Common.Constants.Heroes.TotalHeroCount, cache.HeroesCount);
        Assert.True(cache.HeroesCount > 0, "HeroesCount should be greater than 0");

        // Verify PHP serialization header for products ("a:...")
        string phpProductsStr = Encoding.UTF8.GetString(cache.PrebakedPhpProductsBytes.Span);
        Assert.StartsWith("a:", phpProductsStr);
        Assert.Contains("products", phpProductsStr);
        Assert.Contains("crc", phpProductsStr);
        Assert.Contains("Hero_Artillery.Hero", phpProductsStr);
        Assert.Contains("Hero_Pyromancer.Hero", phpProductsStr);

        // Verify PHP serialization header for heroes ("a:...")
        string phpHeroesStr = Encoding.UTF8.GetString(cache.PrebakedPhpHeroesBytes.Span);
        Assert.StartsWith("a:", phpHeroesStr);
        Assert.Contains("Hero_Pyromancer", phpHeroesStr);
        Assert.Contains("Hero_Artillery", phpHeroesStr);
        Assert.Contains("Hero_Zephyr", phpHeroesStr);
    }

    [Fact]
    public async Task PhpResult_ReadOnlyMemoryBytes_StreamsDirectlyWithExactContentLength()
    {
        byte[] expectedBytes = "a:2:{s:4:\"test\";i:123;s:6:\"status\";s:2:\"OK\";}"u8.ToArray();
        ReadOnlyMemory<byte> memory = expectedBytes;

        DefaultHttpContext httpContext = new();
        using MemoryStream responseStream = new();
        httpContext.Response.Body = responseStream;

        PhpResult phpResult = new(memory);
        await phpResult.ExecuteAsync(httpContext);

        Assert.Equal(expectedBytes.Length, httpContext.Response.ContentLength);
        Assert.Equal("text/plain; charset=utf-8", httpContext.Response.ContentType);

        byte[] actualBytes = responseStream.ToArray();
        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public async Task PhpResult_ByteArray_StreamsDirectlyWithExactContentLength()
    {
        byte[] expectedBytes = "a:1:{s:3:\"msg\";s:5:\"hello\";}"u8.ToArray();

        DefaultHttpContext httpContext = new();
        using MemoryStream responseStream = new();
        httpContext.Response.Body = responseStream;

        PhpResult phpResult = new(expectedBytes);
        await phpResult.ExecuteAsync(httpContext);

        Assert.Equal(expectedBytes.Length, httpContext.Response.ContentLength);

        byte[] actualBytes = responseStream.ToArray();
        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public async Task ProductsHandler_WithPrebakedCache_ReturnsPrebakedBytes()
    {
        Mock<IDatabase> mockCache = new();
        IPrebakedCatalogCache cache = PrebakedCatalogCache.Instance;
        ProductsHandler handler = new(mockCache.Object, cache);

        DefaultHttpContext context = new()
        {
            Request =
            {
                Form = new FormCollection(new Dictionary<string, StringValues> { { "cookie", "valid-test-cookie" } })
            },
            Items = { ["SessionAccountName"] = "ValidUser" }
        };

        IActionResult result = await handler.HandleRequestAsync(context);

        PhpResult phpResult = Assert.IsType<PhpResult>(result);
        Assert.Equal(cache.PrebakedPhpProductsBytes, phpResult.Data);
    }

    [Fact]
    public async Task HeroesHandler_WithPrebakedCache_ReturnsPrebakedBytes()
    {
        IPrebakedCatalogCache cache = PrebakedCatalogCache.Instance;
        HeroesHandler handler = new(NullLogger<HeroesHandler>.Instance, cache);

        DefaultHttpContext context = new();
        IActionResult result = await handler.HandleRequestAsync(context);

        PhpResult phpResult = Assert.IsType<PhpResult>(result);
        Assert.Equal(cache.PrebakedPhpHeroesBytes, phpResult.Data);
    }
}
