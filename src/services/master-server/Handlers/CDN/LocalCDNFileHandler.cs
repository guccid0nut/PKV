// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics;

namespace KONGOR.MasterServer.Handlers.CDN;

/// <summary>
///     Serves CDN files from a local directory (e.g. {ROOT}/{SERVER}/CDN_Content), using the standard S2/KONGOR layout.
///     Includes partitioned concurrency limiting to avoid threadpool starvation during mass updates,
///     and extensive diagnostic logging.
/// </summary>
public static class LocalCDNFileHandler
{
    private const int ConcurrentTransferLimit = 32;
    private const int QueuedTransferLimit = 256;
    internal const string DefaultManifestJson =
        """{"version":"5.0.2","protocol":"pkv-v5","sequence":1,"hashAlgorithm":"SHA-256","mode":"overlay","objectLayout":"sha256","excludeFromSource":["manifest.json","manifest.sig"],"excludeFromTarget":[],"files":{}}""";

    /// <summary>
    ///     Adds static file serving for the local CDN directory when configured.
    /// </summary>
    public static void Use(WebApplication application, OperationalConfigurationCDN configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ServeFromLocalDirectoryURL) || string.IsNullOrWhiteSpace(configuration.LocalDirectory))
        {
            application.Logger.LogInformation("CDN: Local CDN file serving is disabled (ServeFromLocalDirectoryURL or LocalDirectory is not configured). Registering '/cdn' readiness probe.");
            application.MapMethods("/cdn", ["GET", "HEAD"], async (HttpContext ctx) =>
            {
                ctx.Response.Headers["X-Kongor-Update-Protocol"] = "pkv-v5";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json";
                if (HttpMethods.IsGet(ctx.Request.Method))
                {
                    await ctx.Response.WriteAsync("{\"status\":\"ok\",\"service\":\"kongor-local-cdn\",\"mode\":\"embedded\",\"protocol\":\"pkv-v5\"}", ctx.RequestAborted);
                }
            }).ExcludeFromDescription();

            application.MapGet("/cdn/pkv-info.json", async (HttpContext ctx) =>
            {
                ctx.Response.Headers["X-Kongor-Update-Protocol"] = "pkv-v5";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"protocol\":\"pkv-v5\",\"mode\":\"overlay\",\"objectLayout\":\"sha256\"}", ctx.RequestAborted);
            }).ExcludeFromDescription();
            return;
        }

        string requestPath = configuration.ServeFromLocalDirectoryURL.Trim('/');
        if (requestPath.Length == 0 || requestPath.Any(c => char.IsWhiteSpace(c) || c is ':' or '?' or '#'))
        {
            throw new InvalidOperationException($"Invalid Local CDN Directory URL: '{configuration.ServeFromLocalDirectoryURL}'");
        }

        string localDirectory = LocalCDNDirectoryPathResolver.Resolve(configuration.LocalDirectory);

        if (!Directory.Exists(localDirectory))
        {
            application.Logger.LogWarning("CDN: Local CDN directory '{LocalDirectory}' does not exist on disk. Auto-creating directory for local assets.", localDirectory);
            try
            {
                Directory.CreateDirectory(localDirectory);
                application.Logger.LogInformation("CDN: Successfully created local CDN directory '{LocalDirectory}'.", localDirectory);
            }
            catch (Exception ex)
            {
                application.Logger.LogError(ex, "CDN: Failed to auto-create directory '{LocalDirectory}'. File requests may return 404 until created.", localDirectory);
            }
        }

        string rootManifestPath = Path.Combine(localDirectory, "manifest.json");
        string wacManifestPath = Path.Combine(localDirectory, "wac", "manifest.json");
        EnsureManifestSeeded(application, rootManifestPath);
        EnsureManifestSeeded(application, wacManifestPath);

        if (!string.Equals(requestPath, "wac", StringComparison.OrdinalIgnoreCase))
        {
            application.MapMethods("/wac/manifest.json", ["GET", "HEAD"], async (HttpContext ctx) =>
            {
                await WriteManifestResponseAsync(ctx, wacManifestPath, application.Logger);
            }).ExcludeFromDescription();
        }

        application.Logger.LogInformation("CDN: [LOCAL CDN INITIALIZED] Serving '{LocalDirectory}' at '/{RequestPath}' (Concurrency Limit: {ConcurrentLimit}, Queue: {QueueLimit})",
            localDirectory, requestPath, ConcurrentTransferLimit, QueuedTransferLimit);

        application.Map(new PathString("/" + requestPath), branch =>
        {
            branch.UseRateLimiter(new RateLimiterOptions
            {
                GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetConcurrencyLimiter("LocalCDN", _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = ConcurrentTransferLimit,
                        QueueLimit = QueuedTransferLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }))
            });

            // Extensive diagnostic logger for CDN file access and transfer performance
            branch.Use(async (context, next) =>
            {
                string clientIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN";
                string subPath = context.Request.Path.Value ?? "/";
                application.Logger.LogInformation("CDN [TRANSFER START]: {Method} '{SubPath}' requested from {ClientIp}", context.Request.Method, subPath, clientIp);

                Stopwatch stopwatch = Stopwatch.StartNew();
                await next();
                stopwatch.Stop();

                int statusCode = context.Response.StatusCode;
                if (statusCode >= 200 && statusCode < 300)
                {
                    application.Logger.LogInformation("CDN [TRANSFER SUCCESS]: {Method} '{SubPath}' -> HTTP {StatusCode} ({ElapsedMs} ms) to {ClientIp}",
                        context.Request.Method, subPath, statusCode, stopwatch.ElapsedMilliseconds, clientIp);
                }
                else if (statusCode == StatusCodes.Status404NotFound)
                {
                    application.Logger.LogWarning("CDN [TRANSFER NOT FOUND]: {Method} '{SubPath}' -> HTTP 404 (File does not exist in '{LocalDirectory}') from {ClientIp}",
                        context.Request.Method, subPath, localDirectory, clientIp);
                }
                else
                {
                    application.Logger.LogWarning("CDN [TRANSFER RESULT]: {Method} '{SubPath}' -> HTTP {StatusCode} ({ElapsedMs} ms) from {ClientIp}",
                        context.Request.Method, subPath, statusCode, stopwatch.ElapsedMilliseconds, clientIp);
                }
            });

            branch.Use(async (context, next) =>
            {
                context.Response.Headers["X-Kongor-Update-Protocol"] = "pkv-v5";
                string subPath = context.Request.Path.Value ?? string.Empty;
                if (subPath.Equals("/pkv-info.json", StringComparison.OrdinalIgnoreCase) ||
                    subPath.Equals("pkv-info.json", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"protocol\":\"pkv-v5\",\"mode\":\"overlay\",\"objectLayout\":\"sha256\"}", context.RequestAborted);
                    return;
                }

                if ((subPath.Length == 0 || subPath == "/") &&
                    (HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsGet(context.Request.Method)))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    if (HttpMethods.IsGet(context.Request.Method))
                    {
                        await context.Response.WriteAsync("{\"status\":\"ok\",\"service\":\"kongor-local-cdn\",\"mode\":\"static\",\"protocol\":\"pkv-v5\"}", context.RequestAborted);
                    }
                    return;
                }

                if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
                    (subPath.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                     subPath.Equals("/wac/manifest.json", StringComparison.OrdinalIgnoreCase)))
                {
                    string diskPath = subPath.Equals("/wac/manifest.json", StringComparison.OrdinalIgnoreCase)
                        ? wacManifestPath
                        : rootManifestPath;
                    if (!File.Exists(diskPath))
                    {
                        await WriteManifestResponseAsync(context, diskPath, application.Logger);
                        return;
                    }
                }

                await next();
            });

            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(localDirectory),
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });
        });
    }

    private static void EnsureManifestSeeded(WebApplication application, string manifestPath)
    {
        if (File.Exists(manifestPath))
        {
            application.Logger.LogDebug("CDN: Manifest already exists at '{ManifestPath}'.", manifestPath);
            return;
        }

        try
        {
            string? parentDir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrWhiteSpace(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            byte[] payload = System.Text.Encoding.UTF8.GetBytes(DefaultManifestJson);
            using FileStream fs = new(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            fs.Write(payload, 0, payload.Length);
            application.Logger.LogInformation(
                "CDN: Auto-seeded minimal PKV manifest at '{ManifestPath}' ({ByteCount} bytes).",
                manifestPath,
                payload.Length);
        }
        catch (IOException ex) when (File.Exists(manifestPath))
        {
            application.Logger.LogInformation(
                "CDN: Manifest at '{ManifestPath}' was created concurrently during startup ({Message}).",
                manifestPath,
                ex.Message);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            application.Logger.LogWarning(
                ex,
                "CDN: Failed to auto-seed manifest at '{ManifestPath}' (read-only or I/O error); falling back to in-memory JSON response.",
                manifestPath);
        }
    }

    private static async Task WriteManifestResponseAsync(HttpContext ctx, string diskPath, ILogger logger)
    {
        ctx.Response.Headers["X-Kongor-Update-Protocol"] = "pkv-v5";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            return;
        }

        string body = DefaultManifestJson;
        if (File.Exists(diskPath))
        {
            try
            {
                body = await File.ReadAllTextAsync(diskPath, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "CDN: Unable to read manifest '{ManifestPath}' from disk; serving fallback JSON.", diskPath);
            }
        }

        await ctx.Response.WriteAsync(body, ctx.RequestAborted);
    }
}
