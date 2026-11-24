// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class GuideSanitizationSecurityTests
{
    [Fact]
    public void HeroGuide_PerimeterSanitization_CapsOversizedItemListsAt16()
    {
        // Arrange: 100 items in a single category
        string oversizedItems = string.Join('|', Enumerable.Range(1, 100).Select(i => $"Item_Item{i}"));

        // Act
        string sanitized = PerimeterSecuritySanitizer.SanitizeItemList(oversizedItems);
        string[] tokens = sanitized.Split('|', StringSplitOptions.RemoveEmptyEntries);

        // Assert
        tokens.Length.Should().Be(16);
        tokens.First().Should().Be("Item_Item1");
        tokens.Last().Should().Be("Item_Item16");
    }

    [Fact]
    public void HeroGuide_PerimeterSanitization_FiltersSqlInjectionAndHtmlTokens()
    {
        // Arrange
        string maliciousItems = "Item_LoggersHatchet|Item_Boots; DROP TABLE HeroGuides;--|<script>alert('xss')</script>|Item_PortalKey";

        // Act
        string sanitized = PerimeterSecuritySanitizer.SanitizeItemList(maliciousItems);
        string[] tokens = sanitized.Split('|', StringSplitOptions.RemoveEmptyEntries);

        // Assert
        tokens.Should().Equal(["Item_LoggersHatchet", "Item_PortalKey"]);
    }

    [Fact]
    public void HeroGuide_PerimeterSanitization_CapsAbilityQueueAt25()
    {
        // Arrange: 50 ability levels
        string oversizedQueue = string.Join('|', Enumerable.Repeat("Ability_1", 50));

        // Act
        string sanitized = PerimeterSecuritySanitizer.SanitizeAbilityQueue(oversizedQueue);
        string[] tokens = sanitized.Split('|', StringSplitOptions.RemoveEmptyEntries);

        // Assert
        tokens.Length.Should().Be(25);
    }

    [Fact]
    public void HeroGuide_PerimeterSanitization_SanitizesGuideTextAndStripsCaretCodes()
    {
        // Arrange
        string rawIntro = "^rRed ^050Custom <b onclick='hack()'>Bold</b> Text \0 with control \x07 characters";

        // Act
        string sanitized = PerimeterSecuritySanitizer.SanitizeGuideText(rawIntro, 100);

        // Assert
        sanitized.Should().NotContain("^r");
        sanitized.Should().NotContain("^050");
        sanitized.Should().NotContain("<b");
        sanitized.Should().NotContain("</b>");
        sanitized.Should().NotContain("\0");
        sanitized.Should().NotContain("\x07");
        sanitized.Should().Be("Red Custom Bold Text  with control  characters");
    }

    [Theory]
    [InlineData("ValidUser_01", true)]
    [InlineData("Pyro_Player", true)]
    [InlineData("ab", false)]                    // Too short (<3)
    [InlineData("ThisIsFarTooLongForANick", false)] // Too long (>15)
    [InlineData("Nick with space", false)]       // Contains space
    [InlineData("Nick^rColor", false)]           // Contains caret code
    [InlineData("Admin;--", false)]              // Contains punctuation/SQL injection
    [InlineData("<script>", false)]              // HTML tags
    [InlineData("Robert'); DROP", false)]        // Malicious injection
    public void NicknameValidation_PerimeterCharset_EnforcesStrictAlphanumeric(string nickname, bool expected)
    {
        PerimeterSecuritySanitizer.IsValidNickname(nickname).Should().Be(expected);
    }
}
