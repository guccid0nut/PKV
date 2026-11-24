// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Opt-in configuration for OAuth 2.1 (RFC 9728 Protected Resource Metadata + RFC 8707 Resource Indicators)
/// authentication on the Kongor Streamable HTTP/SSE MCP server.
/// Disabled by default so local static Bearer-token workflows remain 100% unchanged.
/// </summary>
public sealed class McpOAuth21Options
{
    public const string DefaultReadScope = "mcp:read";
    public const string DefaultToolsScope = "mcp:tools";
    public const string WellKnownProtectedResourcePath = "/.well-known/oauth-protected-resource";

    public required string Issuer { get; init; }

    /// <summary>
    /// Canonical RFC 8707 Resource Indicator URI for this MCP server (enforced against the JWT <c>aud</c> claim).
    /// </summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Authorization server issuer URLs advertised by RFC 9728 <c>/.well-known/oauth-protected-resource</c>.
    /// Defaults to <see cref="Issuer" /> when empty.
    /// </summary>
    public IReadOnlyList<string> AuthorizationServers { get; init; } = [];

    /// <summary>
    /// OAuth scopes advertised by RFC 9728 metadata.
    /// </summary>
    public IReadOnlyList<string> ScopesSupported { get; init; } = [DefaultReadScope, DefaultToolsScope];

    /// <summary>
    /// Required OAuth scope for read-only MCP HTTP requests (e.g. <c>GET /mcp</c>, <c>GET /healthz</c>).
    /// Holders of <see cref="RequiredToolsScope" /> also satisfy read access.
    /// </summary>
    public string RequiredReadScope { get; init; } = DefaultReadScope;

    /// <summary>
    /// Required OAuth scope for mutating / tool-invocation MCP requests (<c>POST /mcp</c>, <c>DELETE /mcp</c>).
    /// </summary>
    public string RequiredToolsScope { get; init; } = DefaultToolsScope;

    /// <summary>
    /// Verification signing keys (ECDSA P-256, RSA, or symmetric test keys).
    /// </summary>
    public IReadOnlyList<SecurityKey> SigningKeys { get; init; } = [];

    /// <summary>
    /// Optional inline JWKS JSON payload for pre-provisioned verification keys.
    /// </summary>
    public string? JwksJson { get; init; }

    /// <summary>
    /// Optional PEM-encoded ECDSA or RSA public key for signature verification.
    /// </summary>
    public string? PublicKeyPem { get; init; }

    /// <summary>
    /// Allowed clock skew during <c>exp</c> / <c>nbf</c> lifetime validation.
    /// </summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional deterministic clock override for testing token expiration. Defaults to <see cref="System.TimeProvider.System" />.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Documentation URI advertised in RFC 9728 metadata.
    /// </summary>
    public string? ResourceDocumentation { get; init; } = McpProtocolVersion.ServerRepositoryUrl;

    /// <summary>
    /// Opt-in factory reading <c>KONGOR_MCP_OAUTH_ISSUER</c> and related <c>KONGOR_MCP_OAUTH_*</c> environment variables.
    /// Returns <see langword="null" /> when <c>KONGOR_MCP_OAUTH_ISSUER</c> is unset.
    /// </summary>
    public static McpOAuth21Options? TryFromEnvironment(string defaultResourceUrl)
    {
        string? issuer = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER");
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        string trimmedIssuer = issuer.Trim();
        string? envResource = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_RESOURCE");
        string resource = string.IsNullOrWhiteSpace(envResource) ? defaultResourceUrl : envResource.Trim();

        string? envAuthServers = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_AUTH_SERVERS");
        string[] authServers = string.IsNullOrWhiteSpace(envAuthServers)
            ? [trimmedIssuer]
            : envAuthServers.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string readScope = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_REQUIRED_READ_SCOPE")?.Trim() ?? DefaultReadScope;
        if (string.IsNullOrWhiteSpace(readScope)) readScope = DefaultReadScope;

        string toolsScope = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_REQUIRED_TOOLS_SCOPE")?.Trim() ?? DefaultToolsScope;
        if (string.IsNullOrWhiteSpace(toolsScope)) toolsScope = DefaultToolsScope;

        Console.Error.WriteLine(
            $"[MCP OAuth 2.1] Opt-in environment configuration detected: issuer='{trimmedIssuer}', resource='{resource}', authServers={authServers.Length}.");

        return new McpOAuth21Options
        {
            Issuer = trimmedIssuer,
            Resource = resource,
            AuthorizationServers = authServers,
            ScopesSupported = [readScope, toolsScope],
            RequiredReadScope = readScope,
            RequiredToolsScope = toolsScope,
            JwksJson = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_JWKS_JSON"),
            PublicKeyPem = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_PUBLIC_KEY_PEM")
        };
    }
}

/// <summary>
/// RFC 9728 OAuth 2.0 Protected Resource Metadata document returned from
/// <c>GET /.well-known/oauth-protected-resource</c>.
/// </summary>
public sealed record McpProtectedResourceMetadata
{
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    [JsonPropertyName("scopes_supported")]
    public required IReadOnlyList<string> ScopesSupported { get; init; }

    [JsonPropertyName("bearer_methods_supported")]
    public required IReadOnlyList<string> BearerMethodsSupported { get; init; }

    [JsonPropertyName("resource_documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceDocumentation { get; init; }
}

/// <summary>
/// Outcome of OAuth 2.1 Bearer JWT + RFC 8707 audience + scope authorization on an MCP HTTP request.
/// </summary>
public sealed record McpOAuth21ValidationResult(
    bool IsAuthorized,
    int StatusCode,
    string? ErrorCode,
    string? ErrorDescription,
    string? Subject,
    IReadOnlyList<string> GrantedScopes,
    string? RequiredScope,
    string? WwwAuthenticateHeader);

/// <summary>
/// Enforces OAuth 2.1 Protected Resource Metadata (RFC 9728), Resource Indicator audience binding (RFC 8707),
/// cryptographic JWT signature/issuer/expiry validation, and MCP scope checks (<c>mcp:read</c>, <c>mcp:tools</c>).
/// </summary>
public sealed class McpOAuth21ProtectedResource
{
    private static readonly string[] AllowedJwtAlgorithms =
    [
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.HmacSha256,
        SecurityAlgorithms.HmacSha384,
        SecurityAlgorithms.HmacSha512
    ];

    private readonly JsonWebTokenHandler _tokenHandler = new() { MapInboundClaims = false };
    private readonly IReadOnlyList<SecurityKey> _signingKeys;
    private readonly TimeProvider _timeProvider;

    public McpOAuth21Options Options { get; }

    public McpOAuth21ProtectedResource(McpOAuth21Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            throw new ArgumentException("McpOAuth21Options.Issuer must be a non-empty issuer URI.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Resource))
        {
            throw new ArgumentException("McpOAuth21Options.Resource must be a non-empty RFC 8707 resource URI.", nameof(options));
        }

        Options = options;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _signingKeys = ResolveSigningKeys(options);

        Console.Error.WriteLine(
            $"[MCP OAuth 2.1] Initialized RFC 9728 / RFC 8707 Protected Resource validator: issuer='{options.Issuer}', resource='{options.Resource}', signingKeys={_signingKeys.Count}, scopes=[{string.Join(", ", options.ScopesSupported)}].");
    }

    public McpProtectedResourceMetadata BuildMetadata()
    {
        IReadOnlyList<string> authServers = Options.AuthorizationServers.Count > 0
            ? Options.AuthorizationServers
            : [Options.Issuer];

        IReadOnlyList<string> scopes = Options.ScopesSupported.Count > 0
            ? Options.ScopesSupported
            : [McpOAuth21Options.DefaultReadScope, McpOAuth21Options.DefaultToolsScope];

        return new McpProtectedResourceMetadata
        {
            Resource = Options.Resource,
            AuthorizationServers = authServers,
            ScopesSupported = scopes,
            BearerMethodsSupported = ["header"],
            ResourceDocumentation = Options.ResourceDocumentation
        };
    }

    public string BuildResourceMetadataUrl(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Host.HasValue && !string.IsNullOrWhiteSpace(request.Scheme))
        {
            return $"{request.Scheme}://{request.Host}{McpOAuth21Options.WellKnownProtectedResourcePath}";
        }

        if (Uri.TryCreate(Options.Resource, UriKind.Absolute, out Uri? resourceUri))
        {
            return $"{resourceUri.Scheme}://{resourceUri.Authority}{McpOAuth21Options.WellKnownProtectedResourcePath}";
        }

        return McpOAuth21Options.WellKnownProtectedResourcePath;
    }

    public string ResolveRequiredScope(string httpMethod, PathString path)
    {
        if (path.StartsWithSegments("/mcp") &&
            (HttpMethods.IsPost(httpMethod) || HttpMethods.IsPut(httpMethod) || HttpMethods.IsPatch(httpMethod) || HttpMethods.IsDelete(httpMethod)))
        {
            return Options.RequiredToolsScope;
        }

        return Options.RequiredReadScope;
    }

    public async Task<McpOAuth21ValidationResult> ValidateRequestAsync(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string metadataUrl = BuildResourceMetadataUrl(request);
        string requiredScope = ResolveRequiredScope(request.Method, request.Path);

        if (!request.Headers.TryGetValue("Authorization", out var authHeaderValues) ||
            string.IsNullOrWhiteSpace(authHeaderValues))
        {
            Console.Error.WriteLine(
                $"[MCP OAuth 2.1] Rejected {request.Method} {request.Path}: missing Authorization header (401, resource_metadata='{metadataUrl}').");
            return new McpOAuth21ValidationResult(
                IsAuthorized: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "missing_token",
                ErrorDescription: "Missing OAuth 2.1 Bearer access token.",
                Subject: null,
                GrantedScopes: [],
                RequiredScope: requiredScope,
                WwwAuthenticateHeader: FormatWwwAuthenticate(metadataUrl, error: null, errorDescription: null, scope: null));
        }

        string authHeader = authHeaderValues.ToString().Trim();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"[MCP OAuth 2.1] Rejected {request.Method} {request.Path}: non-Bearer Authorization scheme (401).");
            return new McpOAuth21ValidationResult(
                IsAuthorized: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "invalid_token",
                ErrorDescription: "Authorization header must use the Bearer scheme.",
                Subject: null,
                GrantedScopes: [],
                RequiredScope: requiredScope,
                WwwAuthenticateHeader: FormatWwwAuthenticate(metadataUrl, "invalid_token", "Authorization header must use the Bearer scheme.", null));
        }

        string token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                $"[MCP OAuth 2.1] Rejected {request.Method} {request.Path}: empty Bearer token (401).");
            return new McpOAuth21ValidationResult(
                IsAuthorized: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "invalid_token",
                ErrorDescription: "Bearer access token is empty.",
                Subject: null,
                GrantedScopes: [],
                RequiredScope: requiredScope,
                WwwAuthenticateHeader: FormatWwwAuthenticate(metadataUrl, "invalid_token", "Bearer access token is empty.", null));
        }

        if (_signingKeys.Count == 0)
        {
            Console.Error.WriteLine(
                $"[MCP OAuth 2.1] Fail-closed on {request.Method} {request.Path}: zero signing keys configured. Provide McpOAuth21Options.SigningKeys, KONGOR_MCP_OAUTH_JWKS_JSON, or KONGOR_MCP_OAUTH_PUBLIC_KEY_PEM.");
            return new McpOAuth21ValidationResult(
                IsAuthorized: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "invalid_token",
                ErrorDescription: "OAuth 2.1 server has no configured verification signing keys.",
                Subject: null,
                GrantedScopes: [],
                RequiredScope: requiredScope,
                WwwAuthenticateHeader: FormatWwwAuthenticate(metadataUrl, "invalid_token", "No verification signing keys configured.", null));
        }

        TokenValidationParameters validationParameters = new()
        {
            ValidateIssuer = true,
            ValidIssuer = Options.Issuer,
            ValidateAudience = true,
            ValidAudience = Options.Resource,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = Options.ClockSkew,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKeys = _signingKeys,
            ValidAlgorithms = AllowedJwtAlgorithms,
            LifetimeValidator = ValidateLifetimeWithTimeProvider
        };

        TokenValidationResult validationResult = await _tokenHandler
            .ValidateTokenAsync(token, validationParameters)
            .ConfigureAwait(false);

        if (!validationResult.IsValid)
        {
            string failureDetail = ClassifyFailure(validationResult.Exception);
            Console.Error.WriteLine(
                $"[MCP OAuth 2.1] Rejected {request.Method} {request.Path}: {failureDetail} (401, expected iss='{Options.Issuer}', aud='{Options.Resource}').");
            return new McpOAuth21ValidationResult(
                IsAuthorized: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "invalid_token",
                ErrorDescription: failureDetail,
                Subject: null,
                GrantedScopes: [],
                RequiredScope: requiredScope,
                WwwAuthenticateHeader: FormatWwwAuthenticate(metadataUrl, "invalid_token", failureDetail, null));
        }

        List<string> grantedScopes = ExtractScopes(validationResult.ClaimsIdentity);
        bool scopeSatisfied = IsScopeSatisfied(grantedScopes, requiredScope, Options.RequiredToolsScope, Options.RequiredReadScope);
        string? subject = validationResult.ClaimsIdentity?.FindFirst("sub")?.Value
            ?? validationResult.ClaimsIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!scopeSatisfied)
        {
            Console.Error.WriteLine(
                $"[MCP OAuth 2.1] Forbidden {request.Method} {request.Path} for sub='{subject ?? "<none>"}': missing required scope '{requiredScope}' (granted=[{string.Join(", ", grantedScopes)}], 403).");
            return new McpOAuth21ValidationResult(
                IsAuthorized: false,
                StatusCode: StatusCodes.Status403Forbidden,
                ErrorCode: "insufficient_scope",
                ErrorDescription: $"Access token is missing required scope '{requiredScope}'.",
                Subject: subject,
                GrantedScopes: grantedScopes,
                RequiredScope: requiredScope,
                WwwAuthenticateHeader: FormatWwwAuthenticate(metadataUrl, "insufficient_scope", $"Missing required scope '{requiredScope}'.", requiredScope));
        }

        Console.Error.WriteLine(
            $"[MCP OAuth 2.1] Authorized {request.Method} {request.Path} for sub='{subject ?? "<none>"}' (aud='{Options.Resource}', scopes=[{string.Join(" ", grantedScopes)}]).");

        return new McpOAuth21ValidationResult(
            IsAuthorized: true,
            StatusCode: StatusCodes.Status200OK,
            ErrorCode: null,
            ErrorDescription: null,
            Subject: subject,
            GrantedScopes: grantedScopes,
            RequiredScope: requiredScope,
            WwwAuthenticateHeader: null);
    }

    private bool ValidateLifetimeWithTimeProvider(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        if (!expires.HasValue)
        {
            return false;
        }

        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan skew = validationParameters.ClockSkew;

        if (notBefore.HasValue && notBefore.Value.ToUniversalTime() > nowUtc + skew)
        {
            return false;
        }

        return expires.Value.ToUniversalTime() > nowUtc - skew;
    }

    private static List<string> ExtractScopes(ClaimsIdentity? identity)
    {
        HashSet<string> scopes = new(StringComparer.Ordinal);
        if (identity is null)
        {
            return [];
        }

        foreach (Claim claim in identity.Claims)
        {
            if (string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string token in claim.Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(token);
                }
            }
        }

        return [.. scopes.OrderBy(s => s, StringComparer.Ordinal)];
    }

    private static bool IsScopeSatisfied(
        IReadOnlyList<string> grantedScopes,
        string requiredScope,
        string toolsScope,
        string readScope)
    {
        if (grantedScopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            return true;
        }

        // Holders of mcp:tools are permitted to perform mcp:read calls (e.g. SSE GET /mcp or /healthz).
        if (string.Equals(requiredScope, readScope, StringComparison.Ordinal) &&
            grantedScopes.Contains(toolsScope, StringComparer.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string ClassifyFailure(Exception? exception)
    {
        return exception switch
        {
            SecurityTokenInvalidAudienceException => "RFC 8707 audience ('aud') claim does not match protected MCP resource indicator.",
            SecurityTokenExpiredException => "Access token has expired ('exp').",
            SecurityTokenInvalidLifetimeException => "Access token lifetime ('exp'/'nbf') is invalid or expired.",
            SecurityTokenNotYetValidException => "Access token is not yet valid ('nbf').",
            SecurityTokenInvalidIssuerException => "Access token issuer ('iss') is not trusted.",
            SecurityTokenInvalidSignatureException => "Access token cryptographic signature verification failed.",
            SecurityTokenNoExpirationException => "Access token is missing required expiration ('exp') claim.",
            SecurityTokenMalformedException => "Malformed JWT access token.",
            _ => $"Invalid access token: {exception?.Message ?? "signature or claim validation failed"}."
        };
    }

    private static string FormatWwwAuthenticate(string metadataUrl, string? error, string? errorDescription, string? scope)
    {
        string escapedMeta = EscapeHeaderParam(metadataUrl);
        List<string> parts = [$"resource_metadata=\"{escapedMeta}\""];
        if (!string.IsNullOrWhiteSpace(error))
        {
            parts.Add($"error=\"{EscapeHeaderParam(error)}\"");
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            parts.Add($"scope=\"{EscapeHeaderParam(scope)}\"");
        }

        if (!string.IsNullOrWhiteSpace(errorDescription))
        {
            parts.Add($"error_description=\"{EscapeHeaderParam(errorDescription)}\"");
        }

        return "Bearer " + string.Join(", ", parts);
    }

    private static string EscapeHeaderParam(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static List<SecurityKey> ResolveSigningKeys(McpOAuth21Options options)
    {
        List<SecurityKey> keys = [];
        if (options.SigningKeys.Count > 0)
        {
            keys.AddRange(options.SigningKeys);
        }

        if (!string.IsNullOrWhiteSpace(options.JwksJson))
        {
            JsonWebKeySet jwks = new(options.JwksJson);
            keys.AddRange(jwks.GetSigningKeys());
        }

        if (!string.IsNullOrWhiteSpace(options.PublicKeyPem))
        {
            string pem = options.PublicKeyPem.Trim();
            SecurityKey? parsedPemKey = TryImportPemPublicKey(pem);
            if (parsedPemKey is not null)
            {
                keys.Add(parsedPemKey);
            }
            else
            {
                Console.Error.WriteLine("[MCP OAuth 2.1] Warning: Could not parse PublicKeyPem as ECDSA or RSA public key.");
            }
        }

        return keys;
    }

    private static SecurityKey? TryImportPemPublicKey(string pem)
    {
        try
        {
            ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return new ECDsaSecurityKey(ecdsa);
        }
        catch (Exception ecEx)
        {
            Console.Error.WriteLine($"[MCP OAuth 2.1] PEM key is not ECDSA ({ecEx.Message}); trying RSA...");
        }

        try
        {
            RSA rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return new RsaSecurityKey(rsa);
        }
        catch (Exception rsaEx)
        {
            Console.Error.WriteLine($"[MCP OAuth 2.1] PEM key is not RSA ({rsaEx.Message}).");
            return null;
        }
    }
}
