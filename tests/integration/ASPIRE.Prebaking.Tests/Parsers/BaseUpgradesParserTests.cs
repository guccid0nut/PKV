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

public sealed class BaseUpgradesParserTests
{
    [Fact]
    public async Task ParseStreamAsync_WithValidUpgradesXml_ReturnsCosmeticList()
    {
        // Arrange
        BaseUpgradesParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidUpgradesXml));

        // Act
        List<CosmeticUpgradeDto> result = await parser.ParseStreamAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(8);
        result.Should().Contain(u => u.Name == "afghanistan" && u.Category == "ChatSymbol");
        result.Should().Contain(u => u.Name == "aluna_ward" && u.Category == "Ward");
        result.Should().Contain(u => u.Name == "flamboyant" && u.Category == "AnnouncerVoice");
    }
}
