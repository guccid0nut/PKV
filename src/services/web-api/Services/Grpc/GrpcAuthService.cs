// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Grpc.Core;
using NEXUS.Contracts.Auth.V1;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using ZORGATH.WebPortal.API.Serialization;

namespace ZORGATH.WebPortal.API.Services.Grpc;

public sealed class GrpcAuthService(
    IAccountRepository accountRepository,
    IOptions<OperationalConfiguration> configuration,
    ILogger<GrpcAuthService> logger,
    IDatabase? distributedCache = null,
    TimeProvider? timeProvider = null,
    IConfiguration? rawConfiguration = null,
    Microsoft.Extensions.Hosting.IHostEnvironment? hostEnvironment = null,
    bool? allowInMemoryFallback = null) : AuthService.AuthServiceBase
{
    private const int MaxLocalSrpSessions = 1024;
    private const int MaxLocalRevokedTokens = 4096;

    private static readonly SearchValues<char> s_hexChars =
        SearchValues.Create("0123456789abcdefABCDEF");

    private static readonly ConcurrentDictionary<string, GrpcSrpSessionState> s_localSrpSessions = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> s_revokedTokens = new();

    private readonly IAccountRepository _accountRepository = accountRepository;
    private readonly IOptions<OperationalConfiguration> _configuration = configuration;
    private readonly IDatabase? _distributedCache = distributedCache;
    private readonly ILogger<GrpcAuthService> _logger = logger;
    private readonly IConfiguration? _rawConfiguration = rawConfiguration;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly bool _allowInMemoryFallback = allowInMemoryFallback ?? !(hostEnvironment?.IsProduction() == true && distributedCache != null);

    private static bool IsHexString(string? s)
    {
        return !string.IsNullOrEmpty(s) && !s.AsSpan().ContainsAnyExcept(s_hexChars);
    }

    private static PreAuthResponse CreateTimingSafeDummyPreAuthResponse()
    {
        return new PreAuthResponse
        {
            Salt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053",
            ServerEphemeralB = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            SessionNonce = Guid.NewGuid().ToString("N")
        };
    }

    private string ResolveSigningKey()
    {
        string key = _configuration.Value.JWT?.SigningKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key) ||
            string.Equals(key, "MANAGED_BY_ASPIRE", StringComparison.Ordinal) ||
            string.Equals(key, "...", StringComparison.Ordinal) ||
            Encoding.UTF8.GetByteCount(key) < 32)
        {
            if (_rawConfiguration != null)
                key = ZORGATH.ResolveJwtSigningKey(_rawConfiguration);
        }

        if (string.IsNullOrWhiteSpace(key) ||
            string.Equals(key, "MANAGED_BY_ASPIRE", StringComparison.Ordinal) ||
            string.Equals(key, "...", StringComparison.Ordinal) ||
            Encoding.UTF8.GetByteCount(key) < 32)
        {
            _logger.LogError(
                "[GrpcAuthService] Fatal JWT signing key misconfiguration: signing key is missing, placeholder, or shorter than 32 bytes.");
            throw new InvalidOperationException(
                "JWT signing key ('jwt-signing-key' or 'OperationalConfiguration:JWT:SigningKey') must be configured with a valid secret of at least 32 bytes.");
        }

        return key;
    }

    private static string ResolveRevocationId(ClaimsPrincipal principal, string rawToken)
    {
        string? jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                      ?? principal.FindFirst("jti")?.Value;
        if (!string.IsNullOrWhiteSpace(jti))
            return jti;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(digest);
    }

    private async Task RecordTokenRevocationAsync(string revocationId, TimeSpan ttl)
    {
        TimeSpan effectiveTtl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(5);

        if (_distributedCache != null)
            try
            {
                await _distributedCache.StringSetAsync($"grpc:jwt:revoked:{revocationId}", "1", effectiveTtl);
            }
            catch (Exception ex)
            {
                if (!_allowInMemoryFallback)
                {
                    _logger.LogError(ex,
                        "[SECURITY-ALERT] [GrpcAuthService] Redis revocation write failed in Production (AllowInMemoryFallback=false) for token '{RevocationPrefix}'. Failing closed.",
                        SecurityMasking.MaskToken(revocationId));
                    throw new RpcException(new Status(StatusCode.Unavailable, "Distributed revocation store temporarily unavailable."));
                }

                _logger.LogWarning(ex,
                    "[{Source}] Redis revocation write failed for token key '{RevocationPrefix}'. Fallback: '{FallbackTarget}'",
                    "GrpcAuthService",
                    SecurityMasking.MaskToken(revocationId),
                    "InMemory");
            }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (s_revokedTokens.Count >= MaxLocalRevokedTokens)
        {
            foreach (KeyValuePair<string, DateTimeOffset> entry in s_revokedTokens)
            {
                if (entry.Value < now)
                    s_revokedTokens.TryRemove(entry.Key, out _);
            }
        }

        s_revokedTokens[revocationId] = now.Add(effectiveTtl);
    }

    private async Task<bool> IsTokenRevokedAsync(string revocationId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (s_revokedTokens.TryGetValue(revocationId, out DateTimeOffset expiresAt))
        {
            if (expiresAt >= now)
                return true;

            s_revokedTokens.TryRemove(revocationId, out _);
        }

        if (_distributedCache != null)
            try
            {
                if (await _distributedCache.KeyExistsAsync($"grpc:jwt:revoked:{revocationId}"))
                    return true;
            }
            catch (Exception ex)
            {
                if (!_allowInMemoryFallback)
                {
                    _logger.LogError(ex,
                        "[SECURITY-ALERT] [GrpcAuthService] Redis revocation lookup failed in Production (AllowInMemoryFallback=false) for token '{RevocationPrefix}'. Treating token as revoked fail-closed.",
                        SecurityMasking.MaskToken(revocationId));
                    return true;
                }

                _logger.LogWarning(ex,
                    "[{Source}] Redis revocation lookup failed for token key '{RevocationPrefix}'. Fallback: '{FallbackTarget}'",
                    "GrpcAuthService",
                    SecurityMasking.MaskToken(revocationId),
                    "InMemory");
            }

        return false;
    }

    private async Task SetSessionStateAsync(string sessionNonce, GrpcSrpSessionState state)
    {
        if (_distributedCache != null)
            try
            {
                string json = JsonSerializer.Serialize(state, WebApiJsonContext.Default.GrpcSrpSessionState);
                await _distributedCache.StringSetAsync($"grpc:srp:{sessionNonce}", json, TimeSpan.FromMinutes(5));
                return;
            }
            catch (Exception ex)
            {
                if (!_allowInMemoryFallback)
                {
                    _logger.LogError(ex,
                        "[SECURITY-ALERT] [GrpcAuthService] Redis SRP session write failed in Production (AllowInMemoryFallback=false) for '{AccountName}'. Failing closed.",
                        state.AccountName);
                    throw new RpcException(new Status(StatusCode.Unavailable, "Distributed SRP session store temporarily unavailable."));
                }

                _logger.LogWarning(ex,
                    "[{Source}] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                    "GrpcAuthService",
                    SecurityMasking.MaskToken(sessionNonce),
                    state.AccountName,
                    "InMemory");
            }

        if (s_localSrpSessions.Count >= MaxLocalSrpSessions)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (KeyValuePair<string, GrpcSrpSessionState> entry in s_localSrpSessions)
            {
                if (entry.Value.ExpiresAt < now)
                    s_localSrpSessions.TryRemove(entry.Key, out _);
            }
        }

        s_localSrpSessions[sessionNonce] = state;
    }

    private async Task<GrpcSrpSessionState?> GetAndRemoveSessionStateAsync(string sessionNonce)
    {
        if (_distributedCache != null)
            try
            {
                RedisValue value = await _distributedCache.StringGetDeleteAsync($"grpc:srp:{sessionNonce}");
                if (value.HasValue)
                    return JsonSerializer.Deserialize(value.ToString()!, WebApiJsonContext.Default.GrpcSrpSessionState);
            }
            catch (Exception ex)
            {
                if (!_allowInMemoryFallback)
                {
                    _logger.LogError(ex,
                        "[SECURITY-ALERT] [GrpcAuthService] Redis SRP session consume failed in Production (AllowInMemoryFallback=false). Failing closed.");
                    return null;
                }

                _logger.LogWarning(ex,
                    "[{Source}] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                    "GrpcAuthService",
                    SecurityMasking.MaskToken(sessionNonce),
                    "<unknown>",
                    "InMemory");
            }

        if (s_localSrpSessions.TryRemove(sessionNonce, out GrpcSrpSessionState? state))
            if (state.ExpiresAt >= _timeProvider.GetUtcNow())
                return state;

        return null;
    }

    public override async Task<PreAuthResponse> PreAuth(PreAuthRequest request, ServerCallContext context)
    {
        _logger.LogInformation(
            "[GrpcAuthService] PreAuth request for account: '{AccountName}', Version: '{Version}', IP: '{IP}'",
            request.AccountName, request.ClientVersion, request.IpAddress);

        Account? account = await _accountRepository.GetByNicknameAsync(request.AccountName, context.CancellationToken);

        if (account == null || account.Type is AccountType.Disabled || account.IsBanned)
        {
            _logger.LogWarning(
                "[GrpcAuthService] PreAuth rejected: account '{AccountName}' not found, disabled, or banned.",
                request.AccountName);
            // Return dummy response to mitigate timing oracle attacks without caching session state
            return CreateTimingSafeDummyPreAuthResponse();
        }

        User user = account.User;
        string sessionSalt = user.SRPPasswordSalt;
        if (!IsHexString(sessionSalt) || !IsHexString(user.SRPPasswordHash))
        {
            _logger.LogWarning(
                "[GrpcAuthService] PreAuth rejected fail-closed: account '{AccountName}' has non-hex SRP salt or verifier hash.",
                account.Name);
            return CreateTimingSafeDummyPreAuthResponse();
        }

        string serverSecret;
        string serverPublic;
        string verifier;

        try
        {
            SrpParameters parameters = SrpParameters.Create<SHA256>(
                NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
            parameters.Generator = parameters.Pad(parameters.Generator);

            SrpClient client = new(parameters);
            string privateClientKey = client.DerivePrivateKey(sessionSalt, account.Name, user.SRPPasswordHash);
            verifier = client.DeriveVerifier(privateClientKey);

            SrpServer server = new(parameters);
            SrpEphemeral ephemeral = server.GenerateEphemeral(verifier);
            serverSecret = ephemeral.Secret;
            serverPublic = ephemeral.Public;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[GrpcAuthService] PreAuth SRP ephemeral derivation failed for '{AccountName}'; rejecting fail-closed without caching session state.",
                account.Name);
            return CreateTimingSafeDummyPreAuthResponse();
        }

        string sessionNonce = Guid.NewGuid().ToString("N");

        GrpcSrpSessionState sessionState = new()
        {
            AccountName = account.Name,
            SessionSalt = sessionSalt,
            ServerPrivateEphemeral = serverSecret,
            Verifier = verifier,
            ExpiresAt = _timeProvider.GetUtcNow().AddMinutes(5)
        };

        await SetSessionStateAsync(sessionNonce, sessionState);

        _logger.LogInformation(
            "[GrpcAuthService] PreAuth session created successfully for account '{AccountName}', Nonce: '{Nonce}'",
            account.Name, SecurityMasking.MaskToken(sessionNonce));

        return new PreAuthResponse
        {
            Salt = sessionSalt,
            ServerEphemeralB = serverPublic,
            SessionNonce = sessionNonce
        };
    }

    public override async Task<SrpProofResponse> VerifySrpProof(SrpProofRequest request, ServerCallContext context)
    {
        _logger.LogInformation(
            "[GrpcAuthService] VerifySrpProof request for account: '{AccountName}', Nonce: '{Nonce}'",
            request.AccountName, SecurityMasking.MaskToken(request.SessionNonce));

        if (string.IsNullOrWhiteSpace(request.AccountName) ||
            string.IsNullOrWhiteSpace(request.SessionNonce) ||
            string.IsNullOrWhiteSpace(request.ClientEphemeralA) ||
            string.IsNullOrWhiteSpace(request.ClientProofM1))
        {
            _logger.LogWarning(
                "[GrpcAuthService] VerifySrpProof missing required parameters for account: '{AccountName}'",
                request.AccountName);
            return new SrpProofResponse { Success = false };
        }

        GrpcSrpSessionState? sessionState = await GetAndRemoveSessionStateAsync(request.SessionNonce);
        if (sessionState == null)
        {
            _logger.LogWarning(
                "[GrpcAuthService] SRP session state not found or expired for nonce: '{SessionNonce}', account: '{AccountName}'",
                SecurityMasking.MaskToken(request.SessionNonce), request.AccountName);
            return new SrpProofResponse { Success = false };
        }

        if (!string.Equals(sessionState.AccountName, request.AccountName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[GrpcAuthService] SRP session account mismatch. Expected '{Expected}', got '{Actual}'",
                sessionState.AccountName, request.AccountName);
            return new SrpProofResponse { Success = false };
        }

        if (!IsHexString(sessionState.Verifier) ||
            !IsHexString(sessionState.ServerPrivateEphemeral) ||
            !IsHexString(request.ClientEphemeralA) ||
            !IsHexString(request.ClientProofM1))
        {
            _logger.LogWarning(
                "[GrpcAuthService] VerifySrpProof rejected non-hex SRP parameters for account: '{AccountName}', Nonce: '{Nonce}'",
                request.AccountName, SecurityMasking.MaskToken(request.SessionNonce));
            return new SrpProofResponse { Success = false };
        }

        SrpParameters parameters = SrpParameters.Create<SHA256>(
            NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpServer server = new(parameters);
        string? serverProofM2;

        try
        {
            SrpSession serverSession = server.DeriveSession(
                sessionState.ServerPrivateEphemeral,
                request.ClientEphemeralA,
                sessionState.SessionSalt,
                sessionState.AccountName,
                sessionState.Verifier,
                request.ClientProofM1
            );
            serverProofM2 = serverSession.Proof;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GrpcAuthService] SRP-6a proof derivation failed for account: '{AccountName}'",
                request.AccountName);
            return new SrpProofResponse { Success = false };
        }

        if (string.IsNullOrWhiteSpace(serverProofM2))
        {
            _logger.LogWarning("[GrpcAuthService] Invalid SRP-6a client proof M1 for account: '{AccountName}'",
                request.AccountName);
            return new SrpProofResponse { Success = false };
        }

        Account? account = await _accountRepository.GetByNicknameAsync(request.AccountName, context.CancellationToken);

        if (account == null || account.Type is AccountType.Disabled || account.IsBanned)
        {
            _logger.LogWarning(
                "[GrpcAuthService] Account '{AccountName}' not found or banned during SRP token generation.",
                request.AccountName);
            return new SrpProofResponse { Success = false };
        }

        User user = account.User;
        OperationalConfiguration config = _configuration.Value;
        string signingKey = ResolveSigningKey();
        string roleName = user.Role?.Name ?? UserRoles.User;

        // Generate JWT session token with both ZORGATH policy claim ("user_role") and standard ClaimTypes.Role
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, account.Name, ClaimValueTypes.String),
            new(JwtRegisteredClaimNames.Email, user.EmailAddress, ClaimValueTypes.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString(), ClaimValueTypes.String),
            new("user_id", user.ID.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.String),
            new("account_id", account.ID.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.String),
            new(Claims.UserRole, roleName, ClaimValueTypes.String),
            new(ClaimTypes.Role, roleName)
        ];

        DateTime expires = _timeProvider.GetUtcNow()
            .AddHours(config.JWT.DurationInHours > 0 ? config.JWT.DurationInHours : 24).UtcDateTime;

        JwtSecurityToken token = new(
            config.JWT.Issuer,
            config.JWT.Audience,
            claims,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256)
        );

        string sessionToken = new JwtSecurityTokenHandler().WriteToken(token);

        _logger.LogInformation(
            "[GrpcAuthService] SRP authentication successful for account '{AccountName}' (Role: '{Role}'). JWT issued.",
            account.Name, roleName);

        return new SrpProofResponse
        {
            Success = true,
            ServerProofM2 = serverProofM2,
            SessionToken = sessionToken,
            AccountId = (uint)account.ID,
            Nickname = account.Name,
            ExpiresAtUnixSeconds = new DateTimeOffset(expires).ToUnixTimeSeconds()
        };
    }

    public override Task<DiscordExchangeResponse> ExchangeDiscordCode(DiscordExchangeRequest request,
        ServerCallContext context)
    {
        _logger.LogWarning(
            "[GrpcAuthService] ExchangeDiscordCode rejected fail-closed (code length: {CodeLength}): Discord OAuth upstream token verification is not enabled.",
            request.AuthorizationCode?.Length ?? 0);

        return Task.FromResult(new DiscordExchangeResponse
        {
            Success = false
        });
    }

    public override async Task<ValidateTokenResponse> ValidateSessionToken(ValidateTokenRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SessionToken))
            return new ValidateTokenResponse { IsValid = false };

        OperationalConfiguration config = _configuration.Value;
        JwtSecurityTokenHandler handler = new();

        try
        {
            string signingKey = ResolveSigningKey();
            TokenValidationParameters validationParams = new()
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ValidateIssuer = true,
                ValidIssuer = config.JWT.Issuer,
                ValidateAudience = true,
                ValidAudience = config.JWT.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            ClaimsPrincipal principal = handler.ValidateToken(request.SessionToken, validationParams,
                out SecurityToken _);

            string revocationId = ResolveRevocationId(principal, request.SessionToken);
            if (await IsTokenRevokedAsync(revocationId))
            {
                _logger.LogWarning(
                    "[GrpcAuthService] ValidateSessionToken rejected revoked session token (Key: '{RevocationPrefix}').",
                    SecurityMasking.MaskToken(revocationId));
                return new ValidateTokenResponse { IsValid = false };
            }

            string? accountIdClaim =
                principal.FindFirst("account_id")?.Value ?? principal.FindFirst("AccountId")?.Value;
            uint accountId = uint.TryParse(accountIdClaim, out uint aid) ? aid : 0;
            string nickname = principal.FindFirst("sub")?.Value
                              ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                              ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? principal.FindFirst(ClaimTypes.Name)?.Value
                              ?? principal.Identity?.Name
                              ?? string.Empty;

            List<string> roles =
            [
                .. principal.FindAll(ClaimTypes.Role)
                    .Concat(principal.FindAll(Claims.UserRole))
                    .Concat(principal.FindAll("role"))
                    .Select(c => c.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.Ordinal)
            ];

            if (!string.IsNullOrWhiteSpace(request.RequiredPermission) && !roles.Contains(request.RequiredPermission))
            {
                _logger.LogWarning(
                    "[GrpcAuthService] Token missing required permission '{Permission}'. Roles: [{Roles}]",
                    request.RequiredPermission, string.Join(", ", roles));
                return new ValidateTokenResponse
                {
                    IsValid = false,
                    AccountId = accountId,
                    Nickname = nickname
                };
            }

            ValidateTokenResponse response = new()
            {
                IsValid = true,
                AccountId = accountId,
                Nickname = nickname
            };
            response.Roles.AddRange(roles);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GrpcAuthService] Token validation failed: {Message}", ex.Message);
            return new ValidateTokenResponse { IsValid = false };
        }
    }

    public override async Task<InvalidateSessionResponse> InvalidateSession(InvalidateSessionRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[GrpcAuthService] InvalidateSession requested for token prefix: '{TokenPrefix}'",
            SecurityMasking.MaskToken(request.SessionToken));

        if (string.IsNullOrWhiteSpace(request.SessionToken))
        {
            _logger.LogWarning("[GrpcAuthService] InvalidateSession rejected empty token.");
            return new InvalidateSessionResponse { Success = false };
        }

        OperationalConfiguration config = _configuration.Value;
        JwtSecurityTokenHandler handler = new();

        try
        {
            string signingKey = ResolveSigningKey();
            TokenValidationParameters validationParams = new()
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ValidateIssuer = true,
                ValidIssuer = config.JWT.Issuer,
                ValidateAudience = true,
                ValidAudience = config.JWT.Audience,
                ValidateLifetime = false
            };

            ClaimsPrincipal principal = handler.ValidateToken(request.SessionToken, validationParams,
                out SecurityToken validatedToken);

            string revocationId = ResolveRevocationId(principal, request.SessionToken);
            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            TimeSpan remainingTtl = validatedToken.ValidTo > nowUtc
                ? validatedToken.ValidTo - nowUtc + TimeSpan.FromMinutes(5)
                : TimeSpan.FromMinutes(5);

            await RecordTokenRevocationAsync(revocationId, remainingTtl);

            _logger.LogInformation(
                "[GrpcAuthService] Session invalidated and recorded in revocation list (Key: '{RevocationPrefix}', TTL: {TtlSeconds}s).",
                SecurityMasking.MaskToken(revocationId), (int)remainingTtl.TotalSeconds);

            return new InvalidateSessionResponse { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[GrpcAuthService] InvalidateSession failed token verification for prefix '{TokenPrefix}': {Message}",
                SecurityMasking.MaskToken(request.SessionToken), ex.Message);
            return new InvalidateSessionResponse { Success = false };
        }
    }

    public sealed class GrpcSrpSessionState
    {
        public required string AccountName { get; init; }
        public required string SessionSalt { get; init; }
        public required string ServerPrivateEphemeral { get; init; }
        public required string Verifier { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }
}
