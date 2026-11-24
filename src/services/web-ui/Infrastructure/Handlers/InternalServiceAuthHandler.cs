// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Primitives;

namespace DAWNBRINGER.WebPortal.UI.Infrastructure.Handlers;

public class InternalServiceAuthHandler(
    IConfiguration configuration,
    IHttpContextAccessor? httpContextAccessor = null,
    ILogger<InternalServiceAuthHandler>? logger = null) : DelegatingHandler
{
    public const string ForwardedForHeader = "X-Forwarded-For";
    public const string CfConnectingIpHeader = "CF-Connecting-IP";
    public const string AuthenticatedAccountHeader = "X-Authenticated-Account";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("KONGOR_STANDALONE"))
        {
            string? configuredKey = configuration["InternalServiceKey"];
            if (string.IsNullOrWhiteSpace(configuredKey) ||
                string.Equals(configuredKey, "...", StringComparison.Ordinal) ||
                string.Equals(configuredKey, SecurityConstants.DefaultInternalServiceKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "[Standalone] InternalServiceKey is missing or set to the default development key. Start DAWNBRINGER via NEXUS Control Panel or configure userdata/secrets/nexus_secrets.json.");
            }
        }

        string key = configuration["InternalServiceKey"] ?? SecurityConstants.DefaultInternalServiceKey;

        if (!request.Headers.Contains(SecurityConstants.InternalServiceKeyHeader))
            request.Headers.TryAddWithoutValidation(SecurityConstants.InternalServiceKeyHeader, key);

        HttpContext? httpContext = httpContextAccessor?.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated == true &&
            !string.IsNullOrWhiteSpace(httpContext.User.Identity.Name) &&
            !request.Headers.Contains(AuthenticatedAccountHeader))
        {
            request.Headers.TryAddWithoutValidation(AuthenticatedAccountHeader, httpContext.User.Identity.Name.Trim());
        }

        if (!request.Headers.Contains(ForwardedForHeader) &&
            TryResolveOriginatingClientIp(httpContext, configuration, logger, out string clientIp))
        {
            request.Headers.TryAddWithoutValidation(ForwardedForHeader, clientIp);
            logger?.LogDebug(
                "[InternalServiceAuthHandler] Propagated originating client IP '{ClientIp}' via '{Header}' for outbound '{Method} {RequestUri}'.",
                clientIp, ForwardedForHeader, request.Method, request.RequestUri);
        }

        return base.SendAsync(request, cancellationToken);
    }

    public static bool TryResolveOriginatingClientIp(HttpContext? httpContext, out string clientIp) =>
        TryResolveOriginatingClientIp(
            httpContext,
            httpContext?.RequestServices?.GetService<IConfiguration>(),
            httpContext?.RequestServices?.GetService<ILogger<InternalServiceAuthHandler>>(),
            out clientIp);

    public static bool TryResolveOriginatingClientIp(
        HttpContext? httpContext,
        IConfiguration? configuration,
        ILogger? logger,
        out string clientIp)
    {
        clientIp = string.Empty;
        if (httpContext is null) return false;

        IPAddress? remoteIp = httpContext.Connection.RemoteIpAddress;
        bool isTrustedProxy = IsTrustedProxySocket(remoteIp, configuration);

        if (isTrustedProxy)
        {
            if (httpContext.Request.Headers.TryGetValue(CfConnectingIpHeader, out StringValues cfValues) &&
                !StringValues.IsNullOrEmpty(cfValues))
            {
                string cfCandidate = cfValues.ToString().Trim();
                if (IPAddress.TryParse(cfCandidate, out IPAddress? parsedCf))
                {
                    clientIp = parsedCf.IsIPv4MappedToIPv6 ? parsedCf.MapToIPv4().ToString() : parsedCf.ToString();
                    return true;
                }
            }

            if (httpContext.Request.Headers.TryGetValue(ForwardedForHeader, out StringValues xffValues) &&
                !StringValues.IsNullOrEmpty(xffValues))
            {
                string rawXff = xffValues.ToString();
                foreach (string part in rawXff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IPAddress.TryParse(part, out IPAddress? parsedXff))
                    {
                        clientIp = parsedXff.IsIPv4MappedToIPv6 ? parsedXff.MapToIPv4().ToString() : parsedXff.ToString();
                        return true;
                    }
                }
            }
        }
        else if (httpContext.Request.Headers.ContainsKey(CfConnectingIpHeader) ||
                 httpContext.Request.Headers.ContainsKey(ForwardedForHeader))
        {
            logger?.LogWarning(
                "[SECURITY-ALERT] [InternalServiceAuthHandler] Ignored spoofed forwarding headers from untrusted remote socket '{RemoteIp}'",
                remoteIp?.ToString() ?? "unknown");
        }

        if (remoteIp is not null)
        {
            clientIp = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4().ToString() : remoteIp.ToString();
            return true;
        }

        return false;
    }

    private static bool IsTrustedProxySocket(IPAddress? remoteIp, IConfiguration? configuration)
    {
        if (remoteIp is null)
        {
            return false;
        }

        IPAddress normalized = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        if (IPAddress.IsLoopback(remoteIp) || IPAddress.IsLoopback(normalized) || IsPrivateNetworkSocket(normalized))
        {
            return true;
        }

        if (configuration is null)
        {
            return false;
        }

        string? rawProxies = configuration["ReverseProxy:TrustedProxies"] ??
                             configuration["ForwardedHeaders:KnownProxies"];
        if (!string.IsNullOrWhiteSpace(rawProxies))
        {
            foreach (string entry in rawProxies.Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(entry, out IPAddress? configuredProxy))
                {
                    IPAddress normConfigured = configuredProxy.IsIPv4MappedToIPv6
                        ? configuredProxy.MapToIPv4()
                        : configuredProxy;
                    if (normalized.Equals(normConfigured))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsPrivateNetworkSocket(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            byte[] bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }
}
