// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using NEXUS.Contracts.Security;
using Xunit;

namespace NEXUS.Contracts.Tests;

public class SecurityMaskingAdversarialTests
{
    [Theory]
    [InlineData(null, "<none>")]
    [InlineData("", "<none>")]
    [InlineData(" ", "<none>")]
    [InlineData("   ", "<none>")]
    [InlineData("\t", "<none>")]
    [InlineData("\r\n", "<none>")]
    [InlineData(" \t \r\n \v \f ", "<none>")]
    public void MaskToken_NullEmptyOrWhitespace_ReturnsNoneMarker(string? input, string expected)
    {
        string result = SecurityMasking.MaskToken(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("a", 8, "a...")]
    [InlineData("ab", 8, "ab...")]
    [InlineData("1234567", 8, "1234567...")]
    [InlineData("12345678", 8, "12345678...")]
    [InlineData("123456789", 8, "12345678...")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", 8, "abcdefgh...")]
    public void MaskToken_StandardStrings_ExposesPrefixUpToDefaultLength(string input, int prefixLen, string expected)
    {
        string result = SecurityMasking.MaskToken(input, prefixLen);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("secret_token_12345", 0, "...")]
    [InlineData("secret_token_12345", -1, "...")]
    [InlineData("secret_token_12345", -100, "...")]
    [InlineData("secret_token_12345", int.MinValue, "...")]
    [InlineData("short", 100, "short...")]
    [InlineData("exact8ch", int.MaxValue, "exact8ch...")]
    public void MaskToken_ExtremePrefixLengths_HandlesGracefullyWithoutThrowing(string input, int prefixLength,
        string expected)
    {
        string result = SecurityMasking.MaskToken(input, prefixLength);
        result.Should().Be(expected);
    }

    [Fact]
    public void MaskToken_ExtremeLengthString_HandlesInstantlyWithoutOOM()
    {
        string massiveToken = new('X', 1_000_000);
        string result = SecurityMasking.MaskToken(massiveToken);
        result.Should().Be("XXXXXXXX...");
    }

    [Theory]
    [InlineData("🔑🔐💎🚀🎉✨🌟⚡️", 8)]
    [InlineData("こんにちは世界12345678", 8)]
    [InlineData("مرحبا_بالعالم_123456", 8)]
    [InlineData("русский_текст_123456", 8)]
    [InlineData("👨‍👩‍👧‍👦FamilyToken987654", 8)]
    public void MaskToken_MultiByteAndUnicodeStrings_NeverThrows(string input, int prefixLength)
    {
        Action act = () =>
        {
            string result = SecurityMasking.MaskToken(input, prefixLength);
            result.Should().EndWith("...");
            result.Length.Should().BeLessThanOrEqualTo(input.Length + 3);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void MaskToken_SurrogatePairSplit_NeverThrows()
    {
        // 😀 is \uD83D\uDE00 (length 2). Slicing at 1 splits the pair.
        string emoji = "\uD83D\uDE00";
        Action act = () =>
        {
            string result = SecurityMasking.MaskToken(emoji, 1);
            result.Should().Be("\uD83D...");
        };
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("' OR '1'='1", "8", "' OR '1'...")]
    [InlineData("<script>alert('xss')</script>", "8", "<script>...")]
    [InlineData("token\r\n[CRITICAL] Injected log", "8", "token\r\n[...")]
    [InlineData("\0\0\0\0\0\0\0\0nullbytes", "8", "\0\0\0\0\0\0\0\0...")]
    [InlineData("\u001B[31mANSI_ESCAPE\u001B[0m", "8", "\u001B[31mANS...")]
    public void MaskToken_AdversarialPayloads_MasksPrefixWithoutThrowing(string payload, string prefixLenStr,
        string expected)
    {
        int prefixLen = int.Parse(prefixLenStr);
        string result = SecurityMasking.MaskToken(payload, prefixLen);
        result.Should().Be(expected);
    }

    [Fact]
    public void MaskToken_HighConcurrencyStress_ThreadSafeAndConsistent()
    {
        const int iterations = 100_000;
        Parallel.For(0, iterations, i =>
        {
            string token = $"session_token_guid_{i}_{Guid.NewGuid():N}";
            string masked = SecurityMasking.MaskToken(token);
            masked.Should().Be("session_...");
        });
    }
}
