// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Prebaking.Tests.Fixtures;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Parsers;

public sealed class HeroesPreviewXmlParserTests
{
    [Fact]
    public async Task ParseStreamAsync_WithValidPreviewXml_ReturnsPresets()
    {
        // Arrange
        HeroesPreviewXmlParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidPreviewXml));

        // Act
        Dictionary<string, List<PreviewPresetDto>> result = await parser.ParseStreamAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.Should().ContainKey("Hero_Accursed.Alt");
        List<PreviewPresetDto> presets = result["Hero_Accursed.Alt"];
        presets.Should().HaveCount(1);
        presets[0].ModelPath.Should().Be("/heroes/accursed/alt/preview.mdf");
        presets[0].ModelScale.Should().Be(1.2f);
        presets[0].UsingEffect.Should().BeTrue();
    }
}
