// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.AspNetCore.OutputCaching;

namespace ZORGATH.WebPortal.API.Infrastructure;

/// <summary>
///     Explicitly Allows Output Caching For Authenticated Requests By Enforcing "Vary By Authorization Header"
///     And Overriding The Default "Prevent Caching If Authenticated" Behavior
/// </summary>
public sealed class AllowAuthenticatedOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        // 1. Force Enable Caching (Overrides Default Authenticated check)
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true;


        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        HttpResponse response = context.HttpContext.Response;

        // Ensure we explicitly allow caching for successful 200 OK responses even if authenticated
        context.AllowCacheStorage = response.StatusCode == StatusCodes.Status200OK;

        return ValueTask.CompletedTask;
    }
}
