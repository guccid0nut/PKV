// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Value-Weighted Testing Gate (Wave 4 Track 4 — Remote MCP OAuth 2.1):
/// - protects: RFC 9728 Protected Resource Metadata discovery, RFC 8707 Resource Indicator audience binding,
///   OAuth 2.1 ECDSA P-256 / RSA / symmetric JWT signature + issuer + expiry validation,
///   scope enforcement (mcp:tools vs mcp:read), WWW-Authenticate resource_metadata challenge headers,
///   and 100% backwards-compatible opt-out static Bearer behavior.
/// - fails_when: Wrong aud passes RFC 8707 check, missing mcp:tools scope succeeds on POST /mcp instead of 403,
///   expired/forged JWT is accepted, 401 omits WWW-Authenticate resource_metadata, or default static Bearer breaks.
/// - seam: Real Kestrel WebApplication from OfficialMcpHttpHost.BuildHttpApp + McpOAuth21ProtectedResource.
/// </summary>
[Collection("McpHttpAuthEnvCollection")]
public sealed class Wave4RemoteMcpOAuth21Tests
{
    private const string TestIssuer = "https://auth.kongor.example";

    private static int GetEphemeralPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string MintJwt(
        SecurityKey signingKey,
        string algorithm,
        string issuer,
        string audience,
        string? scope,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        string subject = "agent-operator-42")
    {
        Dictionary<string, object> claims = new()
        {
            ["sub"] = subject
        };

        if (!string.IsNullOrWhiteSpace(scope))
        {
            claims["scope"] = scope;
        }

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = nowUtc.UtcDateTime,
            NotBefore = nowUtc.AddSeconds(-5).UtcDateTime,
            Expires = nowUtc.Add(lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey, algorithm)
        };

        JsonWebTokenHandler handler = new();
        return handler.CreateToken(descriptor);
    }

    private static HttpRequestMessage CreateMcpInitializePost(string url, string? bearerToken)
    {
        const string payload = """
        {
          "jsonrpc": "2.0",
          "id": 1,
          "method": "initialize",
          "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": { "name": "wave4-oauth-test", "version": "1.0.0" }
          }
        }
        """;

        HttpRequestMessage req = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(bearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return req;
    }

    [Fact]
    public async Task OAuth21_Rfc9728Metadata_And_ValidEcdsaP256Jwt_SucceedsOnMcpAndHealthz()
    {
        int port = GetEphemeralPort();
        string resourceUri = $"http://127.0.0.1:{port}/mcp";
        using ECDsa signingEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECDsaSecurityKey ecKey = new(signingEc) { KeyId = "kongor-test-es256-1" };

        McpOAuth21Options oauthOptions = new()
        {
            Issuer = TestIssuer,
            Resource = resourceUri,
            AuthorizationServers = [TestIssuer],
            ScopesSupported = ["mcp:read", "mcp:tools"],
            SigningKeys = [ecKey]
        };

        McpToolRegistry registry = new(registerDefaults: false);
        using McpServer server = new(registry, TextWriter.Null);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
            server,
            host: "127.0.0.1",
            port: port,
            requireAuth: true,
            allowRemoteHttp: true,
            oauth21Options: oauthOptions);
        await app.StartAsync();

        try
        {
            using HttpClient client = new();

            // 1. RFC 9728 Protected Resource Metadata must be unauthenticated and complete
            HttpResponseMessage metaResp = await client.GetAsync($"http://127.0.0.1:{port}/.well-known/oauth-protected-resource");
            metaResp.StatusCode.Should().Be(HttpStatusCode.OK);
            string metaJson = await metaResp.Content.ReadAsStringAsync();
            using JsonDocument metaDoc = JsonDocument.Parse(metaJson);
            metaDoc.RootElement.GetProperty("resource").GetString().Should().Be(resourceUri);
            metaDoc.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).Should().ContainSingle(TestIssuer);
            metaDoc.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["mcp:read", "mcp:tools"]);
            metaDoc.RootElement.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["header"]);

            // Path-insertion alias /.well-known/oauth-protected-resource/mcp also responds 200 OK
            HttpResponseMessage aliasMetaResp = await client.GetAsync($"http://127.0.0.1:{port}/.well-known/oauth-protected-resource/mcp");
            aliasMetaResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 2. Valid ECDSA P-256 JWT with matching RFC 8707 aud + mcp:tools succeeds on POST /mcp
            string validJwt = MintJwt(
                ecKey,
                SecurityAlgorithms.EcdsaSha256,
                TestIssuer,
                resourceUri,
                "mcp:read mcp:tools",
                TimeProvider.System.GetUtcNow(),
                TimeSpan.FromMinutes(10));

            using HttpRequestMessage initReq = CreateMcpInitializePost(resourceUri, validJwt);
            HttpResponseMessage initResp = await client.SendAsync(initReq);
            initResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Valid JWT also authorizes /healthz when remote HTTP is enabled
            using HttpRequestMessage healthReq = new(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            healthReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validJwt);
            HttpResponseMessage healthResp = await client.SendAsync(healthReq);
            healthResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task OAuth21_Rfc8707WrongAudience_MissingToken_ExpiredJwt_And_UntrustedKey_Return401WithResourceMetadata()
    {
        int port = GetEphemeralPort();
        string resourceUri = $"http://127.0.0.1:{port}/mcp";
        string expectedMetadataUrl = $"http://127.0.0.1:{port}/.well-known/oauth-protected-resource";

        using ECDsa trustedEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa rogueEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECDsaSecurityKey trustedKey = new(trustedEc) { KeyId = "trusted-k1" };
        ECDsaSecurityKey rogueKey = new(rogueEc) { KeyId = "rogue-k1" };

        McpOAuth21Options oauthOptions = new()
        {
            Issuer = TestIssuer,
            Resource = resourceUri,
            SigningKeys = [trustedKey],
            ClockSkew = TimeSpan.Zero
        };

        McpToolRegistry registry = new(registerDefaults: false);
        using McpServer server = new(registry, TextWriter.Null);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
            server,
            host: "127.0.0.1",
            port: port,
            requireAuth: true,
            allowRemoteHttp: true,
            oauth21Options: oauthOptions);
        await app.StartAsync();

        try
        {
            using HttpClient client = new();
            DateTimeOffset now = TimeProvider.System.GetUtcNow();

            // A. Missing Authorization header -> 401 + WWW-Authenticate: Bearer resource_metadata="..."
            using HttpRequestMessage missingReq = CreateMcpInitializePost(resourceUri, bearerToken: null);
            HttpResponseMessage missingResp = await client.SendAsync(missingReq);
            missingResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            string missingWwwAuth = string.Join(" ", missingResp.Headers.WwwAuthenticate.Select(h => h.ToString()));
            missingWwwAuth.Should().Contain($"Bearer resource_metadata=\"{expectedMetadataUrl}\"");

            // B. Wrong RFC 8707 audience ('aud') -> 401 + WWW-Authenticate resource_metadata
            string wrongAudJwt = MintJwt(
                trustedKey,
                SecurityAlgorithms.EcdsaSha256,
                TestIssuer,
                "https://other-service.kongor.example/mcp",
                "mcp:read mcp:tools",
                now,
                TimeSpan.FromMinutes(10));
            using HttpRequestMessage wrongAudReq = CreateMcpInitializePost(resourceUri, wrongAudJwt);
            HttpResponseMessage wrongAudResp = await client.SendAsync(wrongAudReq);
            wrongAudResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            string wrongAudWwwAuth = string.Join(" ", wrongAudResp.Headers.WwwAuthenticate.Select(h => h.ToString()));
            wrongAudWwwAuth.Should().Contain($"resource_metadata=\"{expectedMetadataUrl}\"");
            wrongAudWwwAuth.Should().Contain("error=\"invalid_token\"");

            // C. Expired JWT -> 401 + WWW-Authenticate resource_metadata
            string expiredJwt = MintJwt(
                trustedKey,
                SecurityAlgorithms.EcdsaSha256,
                TestIssuer,
                resourceUri,
                "mcp:read mcp:tools",
                now.AddMinutes(-15),
                TimeSpan.FromMinutes(5));
            using HttpRequestMessage expiredReq = CreateMcpInitializePost(resourceUri, expiredJwt);
            HttpResponseMessage expiredResp = await client.SendAsync(expiredReq);
            expiredResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            string expiredWwwAuth = string.Join(" ", expiredResp.Headers.WwwAuthenticate.Select(h => h.ToString()));
            expiredWwwAuth.Should().Contain($"resource_metadata=\"{expectedMetadataUrl}\"");

            // D. Wrong issuer -> 401
            string wrongIssuerJwt = MintJwt(
                trustedKey,
                SecurityAlgorithms.EcdsaSha256,
                "https://untrusted-issuer.example",
                resourceUri,
                "mcp:read mcp:tools",
                now,
                TimeSpan.FromMinutes(10));
            using HttpRequestMessage wrongIssReq = CreateMcpInitializePost(resourceUri, wrongIssuerJwt);
            HttpResponseMessage wrongIssResp = await client.SendAsync(wrongIssReq);
            wrongIssResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // E. Untrusted signing key -> 401
            string rogueJwt = MintJwt(
                rogueKey,
                SecurityAlgorithms.EcdsaSha256,
                TestIssuer,
                resourceUri,
                "mcp:read mcp:tools",
                now,
                TimeSpan.FromMinutes(10));
            using HttpRequestMessage rogueReq = CreateMcpInitializePost(resourceUri, rogueJwt);
            HttpResponseMessage rogueResp = await client.SendAsync(rogueReq);
            rogueResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task OAuth21_MissingToolsScope_Returns403ForbiddenOnPostMcp_WhileReadScopePassesHealthz()
    {
        int port = GetEphemeralPort();
        string resourceUri = $"http://127.0.0.1:{port}/mcp";
        string expectedMetadataUrl = $"http://127.0.0.1:{port}/.well-known/oauth-protected-resource";

        using ECDsa signingEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECDsaSecurityKey ecKey = new(signingEc) { KeyId = "scope-test-k1" };

        McpOAuth21Options oauthOptions = new()
        {
            Issuer = TestIssuer,
            Resource = resourceUri,
            SigningKeys = [ecKey]
        };

        McpToolRegistry registry = new(registerDefaults: false);
        using McpServer server = new(registry, TextWriter.Null);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
            server,
            host: "127.0.0.1",
            port: port,
            requireAuth: true,
            allowRemoteHttp: true,
            oauth21Options: oauthOptions);
        await app.StartAsync();

        try
        {
            using HttpClient client = new();
            string readOnlyJwt = MintJwt(
                ecKey,
                SecurityAlgorithms.EcdsaSha256,
                TestIssuer,
                resourceUri,
                "mcp:read",
                TimeProvider.System.GetUtcNow(),
                TimeSpan.FromMinutes(10));

            // Read-only scope can query /healthz
            using HttpRequestMessage healthReq = new(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            healthReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readOnlyJwt);
            HttpResponseMessage healthResp = await client.SendAsync(healthReq);
            healthResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Read-only scope is FORBIDDEN (403) on POST /mcp (which requires mcp:tools)
            using HttpRequestMessage postReq = CreateMcpInitializePost(resourceUri, readOnlyJwt);
            HttpResponseMessage postResp = await client.SendAsync(postReq);
            postResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            string wwwAuth = string.Join(" ", postResp.Headers.WwwAuthenticate.Select(h => h.ToString()));
            wwwAuth.Should().Contain($"resource_metadata=\"{expectedMetadataUrl}\"");
            wwwAuth.Should().Contain("error=\"insufficient_scope\"");
            wwwAuth.Should().Contain("scope=\"mcp:tools\"");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task OAuth21_OptInDisabledByDefault_PreservesStaticBearerAndReturns404ForWellKnown()
    {
        int port = GetEphemeralPort();
        string ephemeralBearer = RandomNumberGenerator.GetHexString(32);
        string? prevIssuer = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER");
        Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER", null);

        try
        {
            McpToolRegistry registry = new(registerDefaults: false);
            using McpServer server = new(registry, TextWriter.Null);
            await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
                server,
                host: "127.0.0.1",
                port: port,
                requiredBearerToken: ephemeralBearer,
                requireAuth: true,
                allowRemoteHttp: false);
            await app.StartAsync();

            try
            {
                using HttpClient client = new();

                // Well-known metadata endpoint is NOT exposed when OAuth 2.1 is disabled
                HttpResponseMessage wellKnownResp = await client.GetAsync($"http://127.0.0.1:{port}/.well-known/oauth-protected-resource");
                wellKnownResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

                // Static bearer token still works for POST /mcp
                using HttpRequestMessage postReq = CreateMcpInitializePost($"http://127.0.0.1:{port}/mcp", ephemeralBearer);
                HttpResponseMessage postResp = await client.SendAsync(postReq);
                postResp.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER", prevIssuer);
        }
    }

    [Fact]
    public async Task OAuth21_EnvironmentOptIn_WithPublicKeyPem_ValidatesSignedJwt()
    {
        int port = GetEphemeralPort();
        string resourceUri = $"http://127.0.0.1:{port}/mcp";
        using ECDsa signingEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = signingEc.ExportSubjectPublicKeyInfoPem();

        string? prevIssuer = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER");
        string? prevResource = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_RESOURCE");
        string? prevPem = Environment.GetEnvironmentVariable("KONGOR_MCP_OAUTH_PUBLIC_KEY_PEM");

        try
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER", TestIssuer);
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_RESOURCE", resourceUri);
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_PUBLIC_KEY_PEM", publicPem);

            McpToolRegistry registry = new(registerDefaults: false);
            using McpServer server = new(registry, TextWriter.Null);
            await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
                server,
                host: "127.0.0.1",
                port: port,
                requireAuth: true,
                allowRemoteHttp: false);
            await app.StartAsync();

            try
            {
                using HttpClient client = new();
                string jwt = MintJwt(
                    new ECDsaSecurityKey(signingEc),
                    SecurityAlgorithms.EcdsaSha256,
                    TestIssuer,
                    resourceUri,
                    "mcp:tools",
                    TimeProvider.System.GetUtcNow(),
                    TimeSpan.FromMinutes(5));

                using HttpRequestMessage postReq = CreateMcpInitializePost(resourceUri, jwt);
                HttpResponseMessage postResp = await client.SendAsync(postReq);
                postResp.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_ISSUER", prevIssuer);
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_RESOURCE", prevResource);
            Environment.SetEnvironmentVariable("KONGOR_MCP_OAUTH_PUBLIC_KEY_PEM", prevPem);
        }
    }

    [Fact]
    public async Task OAuth21_SupportsRsaAndSymmetricSigningKeys()
    {
        int port = GetEphemeralPort();
        string resourceUri = $"http://127.0.0.1:{port}/mcp";
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey rsaKey = new(rsa) { KeyId = "rsa-k1" };
        byte[] symKeyBytes = RandomNumberGenerator.GetBytes(32);
        SymmetricSecurityKey symKey = new(symKeyBytes) { KeyId = "sym-k1" };

        McpOAuth21Options oauthOptions = new()
        {
            Issuer = TestIssuer,
            Resource = resourceUri,
            SigningKeys = [rsaKey, symKey]
        };

        McpToolRegistry registry = new(registerDefaults: false);
        using McpServer server = new(registry, TextWriter.Null);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
            server,
            host: "127.0.0.1",
            port: port,
            requireAuth: true,
            allowRemoteHttp: false,
            oauth21Options: oauthOptions);
        await app.StartAsync();

        try
        {
            using HttpClient client = new();
            DateTimeOffset now = TimeProvider.System.GetUtcNow();

            string rsaJwt = MintJwt(rsaKey, SecurityAlgorithms.RsaSha256, TestIssuer, resourceUri, "mcp:tools", now, TimeSpan.FromMinutes(5));
            using HttpRequestMessage rsaReq = CreateMcpInitializePost(resourceUri, rsaJwt);
            HttpResponseMessage rsaResp = await client.SendAsync(rsaReq);
            rsaResp.StatusCode.Should().Be(HttpStatusCode.OK);

            string symJwt = MintJwt(symKey, SecurityAlgorithms.HmacSha256, TestIssuer, resourceUri, "mcp:tools", now, TimeSpan.FromMinutes(5));
            using HttpRequestMessage symReq = CreateMcpInitializePost(resourceUri, symJwt);
            HttpResponseMessage symResp = await client.SendAsync(symReq);
            symResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
