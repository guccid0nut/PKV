// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Hosts the Project Kongor MCP Server over persistent Streamable HTTP/SSE (<c>/mcp</c>, <c>/healthz</c>)
/// or via the official C# <c>ModelContextProtocol</c> stdio server transport (<c>sdk-stdio</c>).
/// Includes DNS-rebinding (Host validation), Cross-Origin (Origin validation), and Bearer Token authentication security guards.
/// </summary>
public static class OfficialMcpHttpHost
{
    /// <summary>
    /// Builds and configures a Kestrel <see cref="WebApplication" /> serving the official MCP Streamable HTTP/SSE transport
    /// at <c>/mcp</c> alongside a diagnostic health endpoint at <c>/healthz</c>.
    /// </summary>
    public static WebApplication BuildHttpApp(
        McpServer kongorServer,
        string host = "127.0.0.1",
        int port = 19520,
        string? requiredBearerToken = null,
        bool requireAuth = true,
        bool? allowRemoteHttp = null,
        McpOAuth21Options? oauth21Options = null)
    {
        ArgumentNullException.ThrowIfNull(kongorServer);

        string defaultResourceUrl = $"http://{host}:{port}/mcp";
        McpOAuth21Options? resolvedOAuth21Options = oauth21Options ?? McpOAuth21Options.TryFromEnvironment(defaultResourceUrl);
        McpOAuth21ProtectedResource? oauth21Resource = resolvedOAuth21Options is not null
            ? new McpOAuth21ProtectedResource(resolvedOAuth21Options)
            : null;

        string? effectiveToken = requiredBearerToken ?? Environment.GetEnvironmentVariable("KONGOR_MCP_AUTH_TOKEN");
        if (requireAuth && oauth21Resource is null && string.IsNullOrWhiteSpace(effectiveToken))
        {
            throw new InvalidOperationException(
                "[SECURITY-ALERT] OfficialMcpHttpHost.BuildHttpApp requires a non-empty Bearer token when requireAuth=true. " +
                "Pass requiredBearerToken, set KONGOR_MCP_AUTH_TOKEN, configure OAuth 2.1 via McpOAuth21Options / KONGOR_MCP_OAUTH_ISSUER, " +
                "or pass requireAuth: false only in isolated loopback unit tests.");
        }

        bool enforceAuth = requireAuth;
        bool resolvedAllowRemote = allowRemoteHttp ??
            string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_REMOTE_HTTP"), "1", StringComparison.OrdinalIgnoreCase);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");

        builder.Services.AddSingleton(kongorServer);
        builder.Services.AddSingleton(kongorServer.Registry);
        if (oauth21Resource is not null)
        {
            builder.Services.AddSingleton(oauth21Resource);
        }

        builder.Services
            .AddMcpServer(options =>
            {
                // Leave ProtocolVersion unset so the official MCP SDK dynamically negotiates the client's
                // requested version (2024-11-05, 2025-03-26, 2025-06-18, or 2025-11-25).
                options.ServerInfo = new Implementation
                {
                    Name = McpProtocolVersion.ServerName,
                    Version = McpProtocolVersion.ServerVersion
                };
                options.Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities
                {
                    Tools = new ModelContextProtocol.Protocol.ToolsCapability { ListChanged = true },
                    Resources = new ModelContextProtocol.Protocol.ResourcesCapability { ListChanged = true, Subscribe = true },
                    Prompts = new ModelContextProtocol.Protocol.PromptsCapability { ListChanged = true }
                };
            })
            .WithHttpTransport()
            .ConfigureKongorHandlers(kongorServer);

        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            // 1. Host header validation (DNS-rebinding protection)
            bool allowRemote = resolvedAllowRemote;
            if (!allowRemote)
            {
                HostString hostHeader = context.Request.Host;
                string? hostName = hostHeader.Host;
                bool isLoopback = !string.IsNullOrEmpty(hostName) &&
                    (string.Equals(hostName, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(hostName, "[::1]", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(hostName, "::1", StringComparison.OrdinalIgnoreCase));

                if (!isLoopback)
                {
                    Console.Error.WriteLine($"[MCP HTTP Security] Rejected non-loopback Host '{hostName}'. Set KONGOR_MCP_ALLOW_REMOTE_HTTP=1 to permit remote hosts.");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Forbidden: Invalid Host header for loopback MCP server. Set KONGOR_MCP_ALLOW_REMOTE_HTTP=1 to permit remote hosts.\"}");
                    return;
                }
            }

            // 2. Origin header validation (Cross-Origin Protection)
            if (context.Request.Headers.TryGetValue("Origin", out var originValues) && !string.IsNullOrWhiteSpace(originValues))
            {
                string originStr = originValues.ToString().Trim();
                bool isAllowed = false;
                string? allowedOriginsEnv = Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOWED_ORIGINS");
                if (!string.IsNullOrEmpty(allowedOriginsEnv))
                {
                    var allowedList = allowedOriginsEnv.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (allowedList.Any(a => string.Equals(a, originStr, StringComparison.OrdinalIgnoreCase)))
                    {
                        isAllowed = true;
                    }
                }

                if (!isAllowed)
                {
                    if (Uri.TryCreate(originStr, UriKind.Absolute, out Uri? originUri))
                    {
                        if (originUri.Scheme is "http" or "https")
                        {
                            string oHost = originUri.Host;
                            if (string.Equals(oHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(oHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(oHost, "[::1]", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(oHost, "::1", StringComparison.OrdinalIgnoreCase))
                            {
                                isAllowed = true;
                            }
                        }
                    }
                }

                if (!isAllowed)
                {
                    Console.Error.WriteLine($"[MCP HTTP Security] Rejected cross-origin Origin '{originStr}'. Configure KONGOR_MCP_ALLOWED_ORIGINS to permit.");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Forbidden: Cross-origin requests from this Origin are blocked. Configure KONGOR_MCP_ALLOWED_ORIGINS to permit trusted origins.\"}");
                    return;
                }
            }

            // 3. Bearer Token / Opt-In OAuth 2.1 Authentication
            //    Always enforced on /mcp, and also on /healthz when remote HTTP is enabled or non-loopback host bound.
            //    RFC 9728 /.well-known/oauth-protected-resource remains publicly readable for OAuth 2.1 discovery.
            bool isProtectedResourceDiscovery = context.Request.Path.StartsWithSegments(McpOAuth21Options.WellKnownProtectedResourcePath);
            bool nonLoopbackBind = !string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
            bool requiresBearerOnPath = !isProtectedResourceDiscovery &&
                                        (context.Request.Path.StartsWithSegments("/mcp") ||
                                         ((allowRemote || nonLoopbackBind) && context.Request.Path.StartsWithSegments("/healthz")));
            if (enforceAuth && requiresBearerOnPath)
            {
                if (oauth21Resource is not null)
                {
                    McpOAuth21ValidationResult oauthResult = await oauth21Resource.ValidateRequestAsync(context.Request).ConfigureAwait(false);
                    if (!oauthResult.IsAuthorized)
                    {
                        if (!string.IsNullOrWhiteSpace(oauthResult.WwwAuthenticateHeader))
                        {
                            context.Response.Headers.WWWAuthenticate = oauthResult.WwwAuthenticateHeader;
                        }

                        context.Response.StatusCode = oauthResult.StatusCode;
                        context.Response.ContentType = "application/json";
                        string metadataUrl = oauth21Resource.BuildResourceMetadataUrl(context.Request);
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = oauthResult.ErrorCode ?? "invalid_token",
                            error_description = oauthResult.ErrorDescription ?? "OAuth 2.1 authorization failed.",
                            required_scope = oauthResult.RequiredScope,
                            resource_metadata = metadataUrl
                        }).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    bool authenticated = false;
                    if (context.Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
                    {
                        string authHeader = authHeaderValues.ToString().Trim();
                        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            string providedToken = authHeader["Bearer ".Length..].Trim();
                            byte[] expectedBytes = System.Text.Encoding.UTF8.GetBytes(effectiveToken!);
                            byte[] providedBytes = System.Text.Encoding.UTF8.GetBytes(providedToken);
                            if (expectedBytes.Length == providedBytes.Length &&
                                CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
                            {
                                authenticated = true;
                            }
                        }
                    }

                    if (!authenticated)
                    {
                        Console.Error.WriteLine($"[MCP HTTP Security] Rejected missing or invalid Bearer token on {context.Request.Path}.");
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"error\":\"Unauthorized: Missing or invalid Bearer token. Provide Authorization: Bearer <token>.\"}");
                        return;
                    }
                }
            }

            await next(context);
        });

        if (oauth21Resource is not null)
        {
            Func<HttpContext, IResult> metadataHandler = httpContext =>
            {
                McpProtectedResourceMetadata metadata = oauth21Resource.BuildMetadata();
                Console.Error.WriteLine(
                    $"[MCP OAuth 2.1] Served RFC 9728 Protected Resource Metadata on {httpContext.Request.Path} (resource='{metadata.Resource}', authServers={metadata.AuthorizationServers.Count}).");
                return Results.Json(metadata);
            };

            app.MapGet(McpOAuth21Options.WellKnownProtectedResourcePath, metadataHandler);
            app.MapGet($"{McpOAuth21Options.WellKnownProtectedResourcePath}/mcp", metadataHandler);
        }

        app.MapMcp("/mcp");

        app.MapGet("/healthz", () =>
        {
            IReadOnlyList<ToolDefinition> tools = kongorServer.Registry.ListTools();
            IReadOnlyDictionary<string, string> aliases = kongorServer.Registry.ListAliases();
            return Results.Json(new
            {
                status = "healthy",
                transport = "streamable-http",
                serverName = McpProtocolVersion.ServerName,
                serverVersion = McpProtocolVersion.ServerVersion,
                author = McpProtocolVersion.ServerAuthor,
                license = McpProtocolVersion.ServerLicense,
                correspondingSourceUrl = McpProtocolVersion.ServerRepositoryUrl,
                protocolVersion = McpProtocolVersion.Current,
                readOnlyMode = kongorServer.IsReadOnlyMode,
                canonicalToolCount = tools.Count,
                aliasCount = aliases.Count,
                activeSubscriptions = kongorServer.Subscriptions.Count,
                timestamp = TimeProvider.System.GetUtcNow().ToString("o")
            });
        });

        return app;
    }

    /// <summary>
    /// Runs the persistent Streamable HTTP/SSE MCP daemon until <paramref name="cancellationToken" /> is triggered.
    /// </summary>
    public static Task RunHttpDaemonAsync(
        McpServer kongorServer,
        string host = "127.0.0.1",
        int port = 19520,
        CancellationToken cancellationToken = default)
        => RunHttpDaemonAsync(kongorServer, host, port, requiredBearerToken: null, requireAuth: true, cancellationToken: cancellationToken);

    /// <summary>
    /// Runs the persistent Streamable HTTP/SSE MCP daemon with explicit auth configuration until <paramref name="cancellationToken" /> is triggered.
    /// </summary>
    public static async Task RunHttpDaemonAsync(
        McpServer kongorServer,
        string host = "127.0.0.1",
        int port = 19520,
        string? requiredBearerToken = null,
        bool requireAuth = true,
        CancellationToken cancellationToken = default)
    {
        string? token = requiredBearerToken ?? Environment.GetEnvironmentVariable("KONGOR_MCP_AUTH_TOKEN");
        bool oauthOptInConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER"));
        if (requireAuth && !oauthOptInConfigured && string.IsNullOrEmpty(token))
        {
            token = RandomNumberGenerator.GetHexString(32);
            string tokenFilePath = WriteOwnerOnlyEphemeralTokenFile(token, port);
            string maskedPrefix = token[..6];
            Console.Error.WriteLine(
                $"[MCP HTTP Host] Generated ephemeral bearer auth token ({maskedPrefix}...redacted) written to owner-only file '{tokenFilePath}'.");
        }

        await using WebApplication app = BuildHttpApp(kongorServer, host, port, requiredBearerToken: token, requireAuth: requireAuth);
        kongorServer.LogDiagnostic(
            $"[MCP HTTP Host] Starting Official MCP Streamable HTTP/SSE daemon on http://{host}:{port}/mcp (Health: http://{host}:{port}/healthz, ReadOnly: {kongorServer.IsReadOnlyMode})...");
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string WriteOwnerOnlyEphemeralTokenFile(string token, int port)
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectKongor",
            "mcp");
        Directory.CreateDirectory(baseDir);
        string tokenPath = Path.Combine(baseDir, $"mcp-http-{port}.token");
        File.WriteAllText(tokenPath, token);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP HTTP Host] Warning: Could not set 0600 mode on '{tokenPath}': {ex.Message}");
            }
        }
        else
        {
            try
            {
                FileInfo fi = new(tokenPath);
                System.Security.AccessControl.FileSecurity security = new();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (identity.User is not null)
                {
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        identity.User,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    fi.SetAccessControl(security);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP HTTP Host] Warning: Could not restrict ACL on '{tokenPath}': {ex.Message}");
            }
        }

        return tokenPath;
    }

    /// <summary>
    /// Runs the MCP server over standard input/output using the official C# <c>ModelContextProtocol</c> SDK stdio transport.
    /// </summary>
    public static Task RunSdkStdioAsync(
        McpServer kongorServer,
        CancellationToken cancellationToken = default)
        => RunSdkStdioAsync(kongorServer, inputStream: null, outputStream: null, cancellationToken: cancellationToken);

    /// <summary>
    /// Runs the MCP server over explicit input/output streams (preserving the stdout firewall when <c>Console.Out</c> is redirected to <c>Console.Error</c>)
    /// or standard input/output using the official C# <c>ModelContextProtocol</c> SDK server transport.
    /// </summary>
    public static async Task RunSdkStdioAsync(
        McpServer kongorServer,
        Stream? inputStream,
        Stream? outputStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kongorServer);

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddSingleton(kongorServer);
        builder.Services.AddSingleton(kongorServer.Registry);

        IMcpServerBuilder mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                // Leave ProtocolVersion unset so the official MCP SDK dynamically negotiates the client's
                // requested version (2024-11-05, 2025-03-26, 2025-06-18, or 2025-11-25).
                options.ServerInfo = new Implementation
                {
                    Name = McpProtocolVersion.ServerName,
                    Version = McpProtocolVersion.ServerVersion
                };
                options.Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities
                {
                    Tools = new ModelContextProtocol.Protocol.ToolsCapability { ListChanged = true },
                    Resources = new ModelContextProtocol.Protocol.ResourcesCapability { ListChanged = true, Subscribe = true },
                    Prompts = new ModelContextProtocol.Protocol.PromptsCapability { ListChanged = true }
                };
            });

        if (inputStream is not null && outputStream is not null)
        {
            mcpBuilder.WithStreamServerTransport(inputStream, outputStream);
        }
        else
        {
            mcpBuilder.WithStdioServerTransport();
        }

        mcpBuilder.ConfigureKongorHandlers(kongorServer);

        using IHost host = builder.Build();
        kongorServer.LogDiagnostic(
            $"[MCP SDK Stdio] Starting Official ModelContextProtocol SDK stdio transport v{McpProtocolVersion.ServerVersion} (ReadOnly: {kongorServer.IsReadOnlyMode})...");
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
