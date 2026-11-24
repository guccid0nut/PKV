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

public sealed class GameMechanicsParserTests
{
    [Fact]
    public async Task ParseStreamAsync_WithValidMechanicsXml_ReturnsGameMechanicsDto()
    {
        // Arrange
        GameMechanicsParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidMechanicsXml));

        // Act
        GameMechanicsDto result = await parser.ParseStreamAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.AttackTypes.Should().HaveCount(2);
        result.CombatTables.Should().HaveCount(1);
        result.CombatTables[0].Name.Should().Be("Hero");
        result.CombatTables[0].AttackMultipliers["Building"].Should().Be(0.5f);
        result.Popups.Should().HaveCount(1);
        result.Popups[0].Name.Should().Be("Critical");
        result.ImmunityTypes.Should().HaveCount(1);
        result.TargetSchemes.Should().HaveCount(1);
        result.ArmorTypes.Should().HaveCount(1);
    }
}
