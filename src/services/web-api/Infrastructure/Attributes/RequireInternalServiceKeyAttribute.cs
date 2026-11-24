// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace ZORGATH.WebPortal.API.Infrastructure.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireInternalServiceKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        IConfiguration configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        IHostEnvironment hostEnvironment = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        ILogger<RequireInternalServiceKeyAttribute>? logger =
            context.HttpContext.RequestServices.GetService<ILogger<RequireInternalServiceKeyAttribute>>();

        bool isStandalone = configuration.GetValue<bool>("KONGOR_STANDALONE");
        bool isDevelopment = hostEnvironment.IsDevelopment() && !isStandalone;
        string? expectedKey = configuration["InternalServiceKey"];

        if (string.IsNullOrWhiteSpace(expectedKey) ||
            string.Equals(expectedKey, "...", StringComparison.Ordinal))
        {
            if (isDevelopment)
            {
                expectedKey = SecurityConstants.DefaultInternalServiceKey;
                logger?.LogWarning(
                    "[RequireInternalServiceKey] 'InternalServiceKey' is unconfigured. Allowing development default key in Development environment only.");
            }
            else
            {
                logger?.LogError(
                    "[SECURITY-ALERT] [RequireInternalServiceKey] CRITICAL SECURITY REJECTION: 'InternalServiceKey' is missing in configuration for Path='{Path}', IP={RemoteIp}.",
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress);
                context.Result = new UnauthorizedObjectResult("Invalid or missing inter-service authentication key.");
                return;
            }
        }
        else if (!isDevelopment &&
                 string.Equals(expectedKey, SecurityConstants.DefaultInternalServiceKey, StringComparison.Ordinal))
        {
            logger?.LogError(
                "[SECURITY-ALERT] [RequireInternalServiceKey] CRITICAL SECURITY REJECTION: Default development 'InternalServiceKey' is forbidden in non-Development/standalone environments (Path='{Path}', IP={RemoteIp}).",
                context.HttpContext.Request.Path,
                context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new UnauthorizedObjectResult("Invalid or missing inter-service authentication key.");
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(SecurityConstants.InternalServiceKeyHeader,
                out StringValues extractedKey) ||
            string.IsNullOrEmpty(extractedKey.ToString()))
        {
            logger?.LogWarning(
                "[SECURITY-ALERT] [RequireInternalServiceKey] Missing '{Header}' header for Path='{Path}' from IP={RemoteIp}",
                SecurityConstants.InternalServiceKeyHeader,
                context.HttpContext.Request.Path,
                context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new UnauthorizedObjectResult("Invalid or missing inter-service authentication key.");
            return;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        byte[] extractedBytes = Encoding.UTF8.GetBytes(extractedKey.ToString());

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, extractedBytes))
        {
            logger?.LogWarning(
                "[SECURITY-ALERT] [RequireInternalServiceKey] Invalid inter-service key supplied for Path='{Path}' from IP={RemoteIp}",
                context.HttpContext.Request.Path,
                context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new UnauthorizedObjectResult("Invalid or missing inter-service authentication key.");
            return;
        }

        await next();
    }
}
