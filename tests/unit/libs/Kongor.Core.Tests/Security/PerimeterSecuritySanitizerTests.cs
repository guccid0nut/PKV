// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Security;

public sealed class PerimeterSecuritySanitizerTests
{
    [Fact]
    public void SanitizeItemList_StandardItems_PreservesTokens()
    {
        string input = "Item_RunesOfTheBlight|Item_FlamingEye|Item_GoldenApple|Item_ManaPotion|Item_MinorTotem|Item_MinorTotem";
        string result = PerimeterSecuritySanitizer.SanitizeItemList(input);

        result.Should().Be(input);
    }

    [Fact]
    public void SanitizeItemList_MoreThan16Items_TruncatesTo16()
    {
        string input = string.Join('|', Enumerable.Repeat("Item_Marchers", 30));
        string result = PerimeterSecuritySanitizer.SanitizeItemList(input);

        string[] tokens = result.Split('|');
        tokens.Length.Should().Be(16);
    }

    [Fact]
    public void SanitizeItemList_MaliciousTokens_DropsInvalidTokens()
    {
        string input = "Item_Marchers|DROP TABLE;--|<script>|Item_PortalKey";
        string result = PerimeterSecuritySanitizer.SanitizeItemList(input);

        result.Should().Be("Item_Marchers|Item_PortalKey");
    }

    [Fact]
    public void SanitizeAbilityQueue_MoreThan25Levels_TruncatesTo25()
    {
        string input = string.Join('|', Enumerable.Repeat("Ability_1", 30));
        string result = PerimeterSecuritySanitizer.SanitizeAbilityQueue(input);

        string[] tokens = result.Split('|');
        tokens.Length.Should().Be(25);
    }

    [Theory]
    [InlineData("ValidNick", true)]
    [InlineData("User_123", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("ThisNicknameIsFarTooLong", false)]
    [InlineData("Bad Nick", false)]
    [InlineData("Bad^rColor", false)]
    [InlineData("Bad'Quote", false)]
    [InlineData("__kongor", false)]
    [InlineData("__kongor_bot", false)]
    [InlineData("__KONGOR_ADMIN", false)]
    [InlineData(null, false)]
    public void IsValidNickname_ValidatesCorrectly(string? nickname, bool expected)
    {
        PerimeterSecuritySanitizer.IsValidNickname(nickname).Should().Be(expected);
    }

    [Fact]
    public void SanitizePerimeterString_StripsLoneSurrogatesAndBiDi()
    {
        // Lone high surrogate \uD800 followed by normal char
        string input = "Hello\uD800World\u202ETest\u0000Done";
        string result = PerimeterSecuritySanitizer.SanitizePerimeterString(input);

        result.Should().Be("HelloWorldTestDone");
    }

    [Fact]
    public void SanitizePerimeterString_PreservesValidSurrogatePair()
    {
        // \uD83D\uDE00 is 😀
        string input = "Hello 😀 World";
        string result = PerimeterSecuritySanitizer.SanitizePerimeterString(input);

        result.Should().Be("Hello 😀 World");
    }
}
