// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Modding.Conditions;
using Xunit;

namespace Kongor.Modding.Tests;

public class ConditionEvaluatorTests
{
    private readonly Dictionary<string, string> _installedMods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Swift Picker"] = "5.5.0",
        ["AOE RI Complete Rework"] = "1.6.4",
        ["Constant Inventory"] = "1.2.0",
        ["CoreUI"] = "2.0.0"
    };

    [Theory]
    [InlineData("'Swift Picker'", true)]
    [InlineData("'NonExistentMod'", false)]
    [InlineData("not 'NonExistentMod'", true)]
    [InlineData("!NonExistentMod", true)]
    [InlineData("'Swift Picker' and 'Constant Inventory'", true)]
    [InlineData("'Swift Picker' and 'NonExistentMod'", false)]
    [InlineData("'Swift Picker' or 'NonExistentMod'", true)]
    [InlineData("not 'Swift Picker' or 'Constant Inventory'", true)]
    [InlineData("not ('Swift Picker' and 'NonExistentMod')", true)]
    [InlineData("not 'NonExistentMod' and ('Swift Picker [v5.5]' or 'OtherMod [>=1.0]')", true)]
    public void Evaluate_BooleanExpressions_ReturnsExpectedResult(string expression, bool expected)
    {
        bool result = ConditionEvaluator.Evaluate(expression, _installedMods);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Swift Picker [v5.5.0]", true)]
    [InlineData("Swift Picker [5.5]", true)]
    [InlineData("Swift Picker [5.4.0]", false)]
    [InlineData("Swift Picker [>=5.0.0]", true)]
    [InlineData("Swift Picker [>5.5.0]", false)]
    [InlineData("Swift Picker [<=5.5.0]", true)]
    [InlineData("Swift Picker [<5.0.0]", false)]
    [InlineData("Swift Picker [!=1.0.0]", true)]
    [InlineData("Swift Picker [1.0-6.0]", true)]
    [InlineData("Swift Picker [1.0..5.0]", false)]
    [InlineData("Swift Picker [*]", true)]
    [InlineData("Swift Picker [5.*]", true)]
    [InlineData("Swift Picker [6.*]", false)]
    public void Evaluate_VersionConstraints_EvaluatesAccurately(string expression, bool expected)
    {
        bool result = ConditionEvaluator.Evaluate(expression, _installedMods);
        result.Should().Be(expected);
    }

    [Fact]
    public void Evaluate_EmptyOrNull_ReturnsTrue()
    {
        ConditionEvaluator.Evaluate(null, _installedMods).Should().BeTrue();
        ConditionEvaluator.Evaluate(string.Empty, _installedMods).Should().BeTrue();
        ConditionEvaluator.Evaluate("   ", _installedMods).Should().BeTrue();
    }
}
