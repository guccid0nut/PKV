// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using ZORGATH.WebPortal.API.Infrastructure;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public class AllowAuthenticatedOutputCachePolicyTests

{
    [Fact]
    public async Task ServeResponseAsync_AllowsCacheStorage_WhenStatusCodeIs200()

    {
        AllowAuthenticatedOutputCachePolicy policy = new();

        OutputCacheContext context = new() { HttpContext = new DefaultHttpContext(), AllowCacheStorage = true };

        context.HttpContext.Response.StatusCode = 200;


        await policy.ServeResponseAsync(context, CancellationToken.None);


        Assert.True(context.AllowCacheStorage);
    }


    [Fact]
    public async Task ServeResponseAsync_DisallowsCacheStorage_WhenStatusCodeIsNot200()

    {
        AllowAuthenticatedOutputCachePolicy policy = new();

        OutputCacheContext context = new() { HttpContext = new DefaultHttpContext(), AllowCacheStorage = true };

        context.HttpContext.Response.StatusCode = 404;


        await policy.ServeResponseAsync(context, CancellationToken.None);


        Assert.False(context.AllowCacheStorage);
    }


    [Fact]
    public async Task AllowAuthenticatedOutputCachePolicy_CacheRequestAsync_EnablesCachingAndLocking()

    {
        AllowAuthenticatedOutputCachePolicy policy = new();

        OutputCacheContext context = new()

        {
            HttpContext = new DefaultHttpContext(),

            EnableOutputCaching = false,

            AllowCacheLookup = false,

            AllowCacheStorage = false,

            AllowLocking = false
        };


        await policy.CacheRequestAsync(context, CancellationToken.None);


        Assert.True(context.EnableOutputCaching);

        Assert.True(context.AllowCacheLookup);

        Assert.True(context.AllowCacheStorage);

        Assert.True(context.AllowLocking);
    }


    [Fact]
    public async Task AllowAuthenticatedOutputCachePolicy_ServeFromCacheAsync_CompletesSuccessfully()

    {
        AllowAuthenticatedOutputCachePolicy policy = new();

        OutputCacheContext context = new() { HttpContext = new DefaultHttpContext() };


        Exception? exception = await Record.ExceptionAsync(async () =>
            await policy.ServeFromCacheAsync(context, CancellationToken.None));


        Assert.Null(exception);
    }
}
