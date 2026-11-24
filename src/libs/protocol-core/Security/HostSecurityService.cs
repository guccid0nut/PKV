// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

namespace ASPIRE.Common.Security;

public static class HostSecurityService
{
    private const string AllowedChars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$*";
    private const string MagicStringOne = "[!~esTo0}";

    private const string MagicStringTwo = "taquzaph_?98phab&junaj=z=kuChusu";

    // 256-bit static key and 128-bit IV for host secret encryption at rest
    private static readonly byte[] EncryptionKey = SHA256.HashData("NEXUS_VOLUNTEER_HOST_CREDENTIAL_KEY_2026"u8);
    private static readonly byte[] EncryptionIV = SHA256.HashData("NEXUS_HOST_IV_2026"u8)[..16];

    /// <summary>
    ///     Generates a cryptographically strong random password for host servers (length 16 by default).
    ///     Avoids visually ambiguous characters (e.g. 0/O, 1/l/I).
    /// </summary>
    public static string GenerateRandomHostPassword(int length = 16)
    {
        StringBuilder builder = new(length);
        byte[] randomBytes = new byte[length];
        RandomNumberGenerator.Fill(randomBytes);

        for (int i = 0; i < length; i++)
        {
            int index = randomBytes[i] % AllowedChars.Length;
            builder.Append(AllowedChars[index]);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Generates a cryptographically strong, URL-safe random authentication token for COMPEL remote management.
    ///     Prefixes with 'compel_' for clear identification.
    /// </summary>
    public static string GenerateRandomAuthToken(int byteLength = 24)
    {
        byte[] randomBytes = new byte[byteLength];
        RandomNumberGenerator.Fill(randomBytes);
        string hex = Convert.ToHexStringLower(randomBytes);
        return $"compel_{hex}";
    }

    /// <summary>
    ///     Encrypts the cleartext host password for safe storage in the database.
    /// </summary>
    public static string EncryptPassword(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        using Aes aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.IV = EncryptionIV;

        using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using MemoryStream ms = new();
        using (CryptoStream cs = new(ms, encryptor, CryptoStreamMode.Write))
        {
            using StreamWriter sw = new(cs);
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    ///     Decrypts the stored host password for display to the verified account owner.
    /// </summary>
    public static string DecryptPassword(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            using Aes aes = Aes.Create();
            aes.Key = EncryptionKey;
            aes.IV = EncryptionIV;

            using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using MemoryStream ms = new(cipherBytes);
            using CryptoStream cs = new(ms, decryptor, CryptoStreamMode.Read);
            using StreamReader sr = new(cs);
            return sr.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Generates a valid 64-character hex SRP salt.
    /// </summary>
    public static string GenerateSrpSalt()
    {
        byte[] saltBytes = new byte[32];
        RandomNumberGenerator.Fill(saltBytes);
        return Convert.ToHexStringLower(saltBytes);
    }

    /// <summary>
    ///     Computes the SRP password hash compatible with KONGOR master server and game server authentication.
    /// </summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "HoN legacy game authentication protocol requirement")]
    // ReSharper disable once InconsistentNaming
    public static string ComputeSrpHash(string password, string salt, bool passwordIsHashed = true)
    {
#pragma warning disable CA5351 // Do not use broken cryptographic algorithms (HoN legacy SRP protocol compatibility)
        string passwordHash = passwordIsHashed
            ? password
            : Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password)));

        string magickedPasswordHash = passwordHash + salt + MagicStringOne;
        string magickedPasswordHashHash =
            Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(magickedPasswordHash)));

        string magickedMagickedPasswordHashHash = magickedPasswordHashHash + MagicStringTwo;

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(magickedMagickedPasswordHashHash)));
#pragma warning restore CA5351
    }
}
