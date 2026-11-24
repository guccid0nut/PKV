// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Data;

public interface ISessionRepository
{
    Task<string?> GetAccountNameForSessionCookieAsync(string cookie, CancellationToken ct = default);
    Task SetSessionCookieAsync(string cookie, string accountName, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveSessionCookieAsync(string cookie, CancellationToken ct = default);
    Task RevokeAllSessionsForAccountAsync(string accountName, CancellationToken ct = default);
    Task<Token?> ValidateTokenAsync(string tokenString, TokenPurpose purpose, CancellationToken ct = default);
    Task<Token?> ValidateTokenAsync(Guid tokenGuid, TokenPurpose purpose, CancellationToken ct = default);
    Task<Token> IssueTokenAsync(int userId, TokenPurpose purpose, TimeSpan ttl, CancellationToken ct = default);
    Task InvalidateTokenAsync(string tokenString, CancellationToken ct = default);
    Task InvalidateTokenAsync(Guid tokenGuid, CancellationToken ct = default);
    Task<Token?> GetTokenAsync(string tokenString, CancellationToken ct = default);
    Task<Token?> GetTokenAsync(Guid tokenGuid, CancellationToken ct = default);
    Task CleanExpiredTokensAsync(CancellationToken ct = default);
}
