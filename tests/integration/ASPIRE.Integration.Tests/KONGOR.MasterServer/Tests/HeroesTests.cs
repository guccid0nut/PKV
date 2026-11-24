// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Constants;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class HeroesTests

{
    [Fact]
    public void Heroes_AllHeroIdentifiers_ReturnsAllIdentifiers()

    {
        List<string> identifiers = Heroes.AllHeroIdentifiers();


        Assert.NotNull(identifiers);

        Assert.NotEmpty(identifiers);


        // Assert a few well known heroes to make sure reflection works properly

        Assert.Contains("Hero_Accursed", identifiers);

        Assert.Contains("Hero_Devourer", identifiers);

        Assert.Contains("Hero_Armadon", identifiers);


        // Ensure there are roughly enough heroes parsed

        Assert.True(identifiers.Count > 50);
    }


    [Theory]
    [InlineData("Hero_Frosty", "Glacius")]
    [InlineData("Frosty", "Glacius")]
    [InlineData("Hero_Taint", "Gravekeeper")]
    [InlineData("Taint", "Gravekeeper")]
    [InlineData("Hero_BabaYaga", "Wretched Hag")]
    [InlineData("Hero_Hunter", "Blood Hunter")]
    [InlineData("Hero_Legionnaire", "Legionnaire")]
    [InlineData("", "Legionnaire")]
    public void Heroes_ResolveHeroDisplayName_ResolvesCorrectDisplayNames(string heroIdentifier, string expectedName)

    {
        string resolvedName = global::ASPIRE.Common.Constants.Heroes.ResolveHeroDisplayName(heroIdentifier);

        Assert.Equal(expectedName, resolvedName);
    }
}
