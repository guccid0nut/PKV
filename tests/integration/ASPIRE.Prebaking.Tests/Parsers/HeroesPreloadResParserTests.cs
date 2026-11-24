// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Prebaking.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Parsers;

public sealed class HeroesPreloadResParserTests
{
    [Fact]
    public async Task ParseStreamAsync_WithValidPreloadXml_ReturnsResourceMapping()
    {
        // Arrange
        HeroesPreloadResParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidPreloadResXml));

        // Act
        Dictionary<string, List<string>> result = await parser.ParseStreamAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.Should().ContainKey("Hero_Accursed");
        result["Hero_Accursed"].Should().HaveCount(4);
        result["Hero_Accursed"].Should().Contain("/heroes/accursed/model.model");

        result.Should().ContainKey("Hero_Pyromancer");
        result["Hero_Pyromancer"].Should().HaveCount(3);
    }
}
