// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PhpDeserializerBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary and Corner Cases (Feature 8: Hardened PHP Deserializer)
// Policy: Strict Explicit Typing, Boundary Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class PhpDeserializerBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Boundary_NullOrWhitespacePayload_ReturnsNullSafely(string? payload)
    {
        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Boundary_MaxRecursionDepth_DeserializesSafelyWithoutCrashing()
    {
        // Arrange - Construct 32 levels of nested PHP arrays: a:1:{s:1:"k";a:1:{...i:1;...}}
        StringBuilder sb = new();
        for (int i = 0; i < 32; i++) sb.Append("a:1:{s:1:\"k\";");

        sb.Append("i:1;");
        for (int i = 0; i < 32; i++) sb.Append('}');

        // Act
        object? result = PhpDeserializer.Deserialize(sb.ToString());

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<PhpHashtable>();
    }

    [Fact]
    public void Boundary_Depth33_HandlesGracefully()
    {
        // Arrange - Construct 33 levels of nested PHP arrays
        StringBuilder sb = new();
        for (int i = 0; i < 33; i++) sb.Append("a:1:{s:1:\"k\";");

        sb.Append("i:1;");
        for (int i = 0; i < 33; i++) sb.Append('}');

        // Act & Assert - Enforces recursion limit <= 32 and throws FormatException
        Action act = () => PhpDeserializer.Deserialize(sb.ToString());
        act.Should().Throw<FormatException>().WithMessage("*Exceeded maximum recursion depth of 32*");
    }

    [Theory]
    [InlineData("i:2147483647;", 2147483647)]
    [InlineData("i:-2147483648;", -2147483648)]
    [InlineData("i:0;", 0)]
    public void Boundary_IntegerLimits_DeserializesMinMaxValues(string payload, int expected)
    {
        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Boundary_NegativeStringLength_ThrowsOrRejectsSafely()
    {
        // Arrange
        string payload = "s:-5:\"invalid\";";

        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert - Rejects negative length safely by returning null without crashing
        result.Should().BeNull();
    }

    [Fact]
    public void Boundary_ZeroLengthString_DeserializesToEmptyString()
    {
        // Arrange
        string payload = "s:0:\"\";";

        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Boundary_EmptyHashtable_DeserializesToEmptyPhpHashtable()
    {
        // Arrange
        string payload = "a:0:{}";

        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert
        result.Should().BeOfType<PhpHashtable>();
        PhpHashtable table = (PhpHashtable)result!;
        table.Count.Should().Be(0);
    }

    [Fact]
    public void Boundary_TruncatedPayload_HandlesSafelyWithoutCrashing()
    {
        // Arrange - payload truncated mid-string
        string payload = "s:20:\"short\";";

        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert - Strict bounds validation safely returns null on truncated string payload
        result.Should().BeNull();
    }

    [Fact]
    public void Boundary_MassiveArrayCountDeclared_DoesNotAllocateExorbitantMemory()
    {
        // Arrange - Declares 100 million elements but provides none
        string payload = "a:100000000:{s:1:\"a\";i:1;}";

        // Act
        object? result = PhpDeserializer.Deserialize(payload);

        // Assert - Clamping prevents OOM; returns parsed elements
        result.Should().NotBeNull();
        result.Should().BeOfType<PhpHashtable>();
    }
}
