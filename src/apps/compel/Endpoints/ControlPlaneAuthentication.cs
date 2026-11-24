// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Endpoints;

/// <summary>
///     Validates the shared bearer token presented against the control plane's management endpoints.
///     The token is compared with a fixed-time comparison so a mismatch does not leak the configured token's contents
///     through timing.
/// </summary>
internal static class ControlPlaneAuthentication
{
    // The Default, Unconfigured Token In "COMPEL.json". Treated The Same As An Empty Token (Remote Management Disabled) So The Generated Configuration Does Not Enable Management With A Known Placeholder.
    private const string PlaceholderToken = "...";
    private const string LegacyDevToken = "devtoken";

    /// <summary>
    ///     Returns <see langword="null" /> when the request is authorised, or the failing <see cref="IResult" /> otherwise.
    /// </summary>
    public static IResult? Validate(HttpContext httpContext)
    {
        ControlPlaneOptions options =
            httpContext.RequestServices.GetRequiredService<IOptions<ControlPlaneOptions>>().Value;
        Microsoft.Extensions.Hosting.IHostEnvironment? env =
            httpContext.RequestServices.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        ILogger? logger = httpContext.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("COMPEL.ControlPlaneAuthentication");

        if (string.IsNullOrEmpty(options.AuthenticationToken) || options.AuthenticationToken == PlaceholderToken)
        {
            return Results.Text(
                "The Control Plane Authentication Token Is Not Configured",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (TryGetBearerToken(httpContext.Request, out string presentedToken) is false)
        {
            logger?.LogWarning(
                "[SECURITY-ALERT] Missing or malformed Control Plane Authorization header for {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);
            return Results.Unauthorized();
        }

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presentedToken);
        byte[] legacyDevBytes = Encoding.UTF8.GetBytes(LegacyDevToken);
        bool presentedIsDevToken = CryptographicOperations.FixedTimeEquals(legacyDevBytes, presentedBytes);
        bool configuredIsDevToken = CryptographicOperations.FixedTimeEquals(
            legacyDevBytes,
            Encoding.UTF8.GetBytes(options.AuthenticationToken));

        if (presentedIsDevToken || configuredIsDevToken)
        {
            if (env?.IsDevelopment() == true && options.AllowInsecureDevToken && presentedIsDevToken)
            {
                return null;
            }

            logger?.LogWarning(
                "[SECURITY-ALERT] Rejected insecure 'devtoken' bearer credential on {Method} {Path} (AllowInsecureDevToken={AllowInsecureDevToken})",
                httpContext.Request.Method,
                httpContext.Request.Path,
                options.AllowInsecureDevToken);
            return Results.Unauthorized();
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(options.AuthenticationToken);
        if (CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes))
        {
            return null;
        }

        logger?.LogWarning(
            "[SECURITY-ALERT] Rejected invalid Control Plane bearer token on {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);
        return Results.Unauthorized();
    }

    private static bool TryGetBearerToken(HttpRequest request, out string presentedToken)
    {
        presentedToken = string.Empty;

        string? header = request.Headers.Authorization;

        if (string.IsNullOrWhiteSpace(header))
            return false;

        const string scheme = "Bearer ";

        if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) is false)
            return false;

        presentedToken = header[scheme.Length..].Trim();

        return string.IsNullOrEmpty(presentedToken) is false;
    }
}
