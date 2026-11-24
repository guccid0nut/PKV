// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Kongor.Core.Security;
using Xunit;

namespace Kongor.Core.Tests.Security;

public sealed class S2CryptoTests
{
    [Fact]
    public void ComputeSrpFromPlaintext_ShouldMatch_ComputeSrpFromPrehashedMd5()
    {
        string[] testPasswords = ["secret123", "password", "Kongor2026!#", "SuperLongAndComplexPasswordWithSymbols@123456789"];
        string salt = S2Crypto.GenerateSrpSalt();

        foreach (string password in testPasswords)
        {
            string md5Hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password)));

            string verifier1 = S2Crypto.ComputeSrpFromPlaintext(password, salt);
            string verifier2 = S2Crypto.ComputeSrpFromPrehashedMd5(md5Hash, salt);

            verifier1.Should().Be(verifier2);
            verifier1.Should().HaveLength(64);
            verifier1.Should().MatchRegex("^[0-9a-f]{64}$");
        }
    }

    [Fact]
    public void S2Crypto_GenerateSrpSalt_ReturnsDistinct64CharHexStrings()
    {
        string salt1 = S2Crypto.GenerateSrpSalt();
        string salt2 = S2Crypto.GenerateSrpSalt();

        salt1.Should().HaveLength(64);
        salt2.Should().HaveLength(64);
        salt1.Should().NotBe(salt2);
        salt1.Should().MatchRegex("^[0-9a-f]{64}$");
        salt2.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void S2Crypto_ComputeChatServerCookieHash_ReturnsPredictableDeterministicSha1()
    {
        int accountId = 12345;
        string ip = "127.0.0.1";
        string cookie = "abc123cookie";

        string hash1 = S2Crypto.ComputeChatServerCookieHash(accountId, ip, cookie);
        string hash2 = S2Crypto.ComputeChatServerCookieHash(accountId, ip, cookie);

        hash1.Should().Be(hash2);
        hash1.Should().HaveLength(40); // SHA1 hex is 40 characters
        hash1.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void S2Crypto_ComputeMatchServerChatAuthenticationHash_ReturnsPredictableDeterministicSha1()
    {
        string key = "match_key_999";
        string cookie = "server_cookie_xyz";

        string hash1 = S2Crypto.ComputeMatchServerChatAuthenticationHash(key, cookie);
        string hash2 = S2Crypto.ComputeMatchServerChatAuthenticationHash(key, cookie);

        hash1.Should().Be(hash2);
        hash1.Should().HaveLength(40);
        hash1.Should().MatchRegex("^[0-9a-f]{40}$");
    }
}
