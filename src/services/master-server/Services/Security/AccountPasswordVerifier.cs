// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using Kongor.Core.Security;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace KONGOR.MasterServer.Services.Security;

/// <summary>
///     Provides password verification logic for account management operations (SRP prehashed MD5, SRP plaintext, Argon2id, and PBKDF2).
/// </summary>
public interface IAccountPasswordVerifier
{
    bool VerifyPassword(User user, string password);
    ValueTask<bool> VerifyPasswordAsync(User user, string password, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(VerifyPassword(user, password));
}

/// <summary>
///     Default implementation of <see cref="IAccountPasswordVerifier"/>.
/// </summary>
public sealed class AccountPasswordVerifier(
    ILogger<AccountPasswordVerifier> logger,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null) : IAccountPasswordVerifier
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Argon2idHasher _argon2Hasher = argon2Hasher ?? new();

    public async ValueTask<bool> VerifyPasswordAsync(User user, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[{Timestamp}] [AccountPasswordVerifier] Password verification failed: Password was null or empty for User ID {UserId}",
                    _timeProvider.GetUtcNow(),
                    user.ID);
            }

            return false;
        }

        // 1. Check SRP hash assuming password was provided as MD5 (standard client name change form: _store_namechange_confirmpass)
        string srpHashFromMd5 = S2Crypto.ComputeSrpFromPrehashedMd5(password, user.SRPPasswordSalt);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(srpHashFromMd5.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(user.SRPPasswordHash.ToLowerInvariant())))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via SRP (MD5 mode) for User ID {UserId}",
                    _timeProvider.GetUtcNow(),
                    user.ID);
            }

            return true;
        }

        // 2. Check SRP hash assuming password was provided as plaintext (standard client subaccount form: store_SubAccount_confirmpass)
        string srpHashFromPlain = S2Crypto.ComputeSrpFromPlaintext(password, user.SRPPasswordSalt);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(srpHashFromPlain.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(user.SRPPasswordHash.ToLowerInvariant())))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via SRP (Plaintext mode) for User ID {UserId}",
                    _timeProvider.GetUtcNow(),
                    user.ID);
            }

            return true;
        }

        // 2.5. Check Argon2id hash if set on the User (modern shadow-upgraded accounts)
        if (!string.IsNullOrEmpty(user.Argon2idPasswordHash))
        {
            if (await _argon2Hasher.VerifyPasswordAsync(password, user.Argon2idPasswordHash, cancellationToken).ConfigureAwait(false))
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via Argon2id for User ID {UserId}",
                        _timeProvider.GetUtcNow(),
                        user.ID);
                }

                return true;
            }
        }

        // 3. Fallback: Check PBKDF2 hash if set on the User
        if (!string.IsNullOrEmpty(user.PBKDF2PasswordHash))
        {
            PasswordHasher<User> hasher = new();
            PasswordVerificationResult result = hasher.VerifyHashedPassword(user, user.PBKDF2PasswordHash, password);
            if (result != PasswordVerificationResult.Failed)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via PBKDF2 for User ID {UserId}",
                        _timeProvider.GetUtcNow(),
                        user.ID);
                }

                return true;
            }
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "[{Timestamp}] [AccountPasswordVerifier] Password verification failed for User ID {UserId}: credentials mismatch",
                _timeProvider.GetUtcNow(),
                user.ID);
        }

        return false;
    }

    public bool VerifyPassword(User user, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[{Timestamp}] [AccountPasswordVerifier] Password verification failed: Password was null or empty for User ID {UserId}",
                    _timeProvider.GetUtcNow(),
                    user.ID);
            }

            return false;
        }

        // 1. Check SRP hash assuming password was provided as MD5 (standard client name change form: _store_namechange_confirmpass)
        string srpHashFromMd5 = S2Crypto.ComputeSrpFromPrehashedMd5(password, user.SRPPasswordSalt);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(srpHashFromMd5.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(user.SRPPasswordHash.ToLowerInvariant())))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via SRP (MD5 mode) for User ID {UserId}",
                    _timeProvider.GetUtcNow(),
                    user.ID);
            }

            return true;
        }

        // 2. Check SRP hash assuming password was provided as plaintext (standard client subaccount form: store_SubAccount_confirmpass)
        string srpHashFromPlain = S2Crypto.ComputeSrpFromPlaintext(password, user.SRPPasswordSalt);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(srpHashFromPlain.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(user.SRPPasswordHash.ToLowerInvariant())))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via SRP (Plaintext mode) for User ID {UserId}",
                    _timeProvider.GetUtcNow(),
                    user.ID);
            }

            return true;
        }

        // 2.5. Check Argon2id hash if set on the User (modern shadow-upgraded accounts)
        if (!string.IsNullOrEmpty(user.Argon2idPasswordHash))
        {
            if (_argon2Hasher.VerifyPassword(password, user.Argon2idPasswordHash))
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via Argon2id for User ID {UserId}",
                        _timeProvider.GetUtcNow(),
                        user.ID);
                }

                return true;
            }
        }

        // 3. Fallback: Check PBKDF2 hash if set on the User
        if (!string.IsNullOrEmpty(user.PBKDF2PasswordHash))
        {
            PasswordHasher<User> hasher = new();
            PasswordVerificationResult result = hasher.VerifyHashedPassword(user, user.PBKDF2PasswordHash, password);
            if (result != PasswordVerificationResult.Failed)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[{Timestamp}] [AccountPasswordVerifier] Password verified successfully via PBKDF2 for User ID {UserId}",
                        _timeProvider.GetUtcNow(),
                        user.ID);
                }

                return true;
            }
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "[{Timestamp}] [AccountPasswordVerifier] Password verification failed for User ID {UserId}: credentials mismatch",
                _timeProvider.GetUtcNow(),
                user.ID);
        }

        return false;
    }
}
