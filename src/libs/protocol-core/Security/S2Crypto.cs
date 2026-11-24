// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Kongor.Core.Security;

/// <summary>
///     Unified cryptographic primitives for the legacy Heroes of Newerth S2 protocol,
///     including Secure Remote Password (SRP) hashing, salts, and chat server authentication tokens.
/// </summary>
[SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
    Justification = "Required for legacy HoN S2 SRP and chat authentication protocol compatibility")]
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
    Justification = "Required for legacy HoN S2 SRP protocol compatibility (MD5)")]
public static class S2Crypto
{
    #region Constants

    // Magic strings used in client-side SRP password hashing.
    // Present in k2_x64 DLL between offsets 0xF2F4D0 and 0xF2F4D0.
    public const string MagicStringOne = "[!~esTo0}";
    public const string MagicStringTwo = "taquzaph_?98phab&junaj=z=kuChusu";

    public const string ChatServerAuthenticationSalt =
        "8roespiemlasToUmiuglEhOaMiaSWlesplUcOAniupr2esPOeBRiudOEphiutOuJ";

    #endregion

    /// <summary>
    ///     Computes the 64-character lowercase SHA256 SRP password hash starting from a plaintext password.
    ///     First hashes the plaintext with MD5 to obtain a lowercase hex digest, then computes the magicked SRP hash.
    ///     Used during web registration, password changes, and subaccount management.
    /// </summary>
    public static string ComputeSrpFromPlaintext(string plaintextPassword, string salt)
    {
        string md5Hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plaintextPassword)));
        return ComputeSrpFromPrehashedMd5(md5Hash, salt);
    }

    /// <summary>
    ///     Computes the 64-character lowercase SHA256 SRP password hash starting from an already MD5-hashed password.
    ///     Formula: SHA256(MD5(md5Hash + salt + MagicStringOne) + MagicStringTwo) in lowercase hex.
    ///     Used during game client SRP authentication (stage 1 / 2) where the client sends pre-hashed MD5 credentials.
    /// </summary>
    public static string ComputeSrpFromPrehashedMd5(string md5PasswordHash, string salt)
    {
        string magickedPasswordHash = md5PasswordHash + salt + MagicStringOne;
        string magickedPasswordHashHash =
            Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(magickedPasswordHash)));
        string magickedMagickedPasswordHashHash = magickedPasswordHashHash + MagicStringTwo;

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(magickedMagickedPasswordHashHash)));
    }

    /// <summary>
    ///     Computes the 64-character lowercase SHA256 SRP password hash starting from an already SHA256-hashed password or token.
    ///     Formula: SHA256(SHA256(sha256Hash + salt + MagicStringOne) + MagicStringTwo) in lowercase hex.
    ///     Used during modern dedicated server manager and cloud fleet worker authentication.
    /// </summary>
    public static string ComputeSrpFromPrehashedSha256(string sha256PasswordHash, string salt)
    {
        string magickedPasswordHash = sha256PasswordHash + salt + MagicStringOne;
        string magickedPasswordHashHash =
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(magickedPasswordHash)));
        string magickedMagickedPasswordHashHash = magickedPasswordHashHash + MagicStringTwo;

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(magickedMagickedPasswordHashHash)));
    }

    /// <summary>
    ///     Generates a cryptographically secure 64-character hexadecimal salt for SRP authentication.
    ///     32 random bytes -> 64 lowercase hex characters.
    /// </summary>
    public static string GenerateSrpSalt()
    {
        byte[] saltBytes = new byte[32];
        RandomNumberGenerator.Fill(saltBytes);
        return Convert.ToHexStringLower(saltBytes);
    }

    /// <summary>
    ///     Computes the SHA1 hash for chat server cookie authentication.
    /// </summary>
    public static string ComputeChatServerCookieHash(int accountId, string remoteIpAddress, string cookie)
    {
        string chatServerCookie = accountId + remoteIpAddress + cookie + ChatServerAuthenticationSalt;
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(chatServerCookie)));
    }

    /// <summary>
    ///     Computes the SHA1 hash for match server chat authentication.
    /// </summary>
    public static string ComputeMatchServerChatAuthenticationHash(string key, string cookie)
    {
        return Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes(key + cookie + ChatServerAuthenticationSalt)));
    }
}
