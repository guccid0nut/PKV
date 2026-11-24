// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature08_HardenedPhpDeserializerTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 8: Hardened PHP Serialization Engine)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class HardenedPhpDeserializerTests
{
    [Fact]
    public void PhpDeserializer_ScalarPrimitives_DeserializesCorrectly()
    {
        // Arrange & Act
        object? nullVal = PhpDeserializer.Deserialize("N;");
        object? trueVal = PhpDeserializer.Deserialize("b:1;");
        object? falseVal = PhpDeserializer.Deserialize("b:0;");
        object? intVal = PhpDeserializer.Deserialize("i:1337;");
        object? doubleVal = PhpDeserializer.Deserialize("d:3.14159;");
        object? strVal = PhpDeserializer.Deserialize("s:6:\"Kongor\";");

        // Assert
        nullVal.Should().BeNull();
        trueVal.Should().Be(true);
        falseVal.Should().Be(false);
        intVal.Should().Be(1337);
        doubleVal.Should().Be(3.14159);
        strVal.Should().Be("Kongor");
    }

    [Fact]
    public void PhpDeserializer_AssociativeArray_ParsesKeysAndValues()
    {
        // Arrange
        string serialized = "a:2:{s:4:\"name\";s:7:\"Kongor1\";s:5:\"level\";i:25;}";

        // Act
        object? result = PhpDeserializer.Deserialize(serialized);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<PhpHashtable>();
        PhpHashtable table = (PhpHashtable)result!;
        table["name"]!.ToString().Should().Be("Kongor1");
        Convert.ToInt32(table["level"]).Should().Be(25);
    }

    [Fact]
    public void PhpDeserializer_Utf8SpanInput_MatchesStringInput()
    {
        // Arrange
        string payload = "a:1:{s:3:\"key\";s:5:\"value\";}";
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(payload);

        // Act
        object? resultFromSpan = PhpDeserializer.Deserialize(utf8Bytes.AsSpan());
        object? resultFromString = PhpDeserializer.Deserialize(payload);

        // Assert
        resultFromSpan.Should().BeOfType<PhpHashtable>();
        resultFromString.Should().BeOfType<PhpHashtable>();
        ((PhpHashtable)resultFromSpan!)["key"]!.ToString().Should()
            .Be(((PhpHashtable)resultFromString!)["key"]!.ToString());
    }

    [Fact]
    public void PhpDeserializer_DeeplyNestedPayload_ParsesGracefully()
    {
        // Arrange - Build 10 levels of nested arrays
        StringBuilder sb = new();
        for (int i = 0; i < 10; i++) sb.Append("a:1:{s:1:\"x\";");
        sb.Append("s:4:\"deep\";");
        for (int i = 0; i < 10; i++) sb.Append('}');

        // Act
        object? result = PhpDeserializer.Deserialize(sb.ToString());

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<PhpHashtable>();
    }

    [Fact]
    public void PhpDeserializer_MalformedTruncatedInput_HandlesGracefullyWithoutCrashing()
    {
        // Arrange - Truncated and corrupt serialized strings
        string truncated = "a:5:{s:4:\"name\";s:10:\"incomplete";

        // Act
        object? result = PhpDeserializer.Deserialize(truncated);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<PhpHashtable>();
    }

    [Fact]
    public void PhpSerializer_RoundTrip_SerializesAndDeserializesIdentically()
    {
        // Arrange
        PhpHashtable original = new()
        {
            ["host"] = "127.0.0.1",
            ["port"] = 11235,
            ["online"] = true
        };

        // Act
        string serialized = PhpSerializer.Serialize(original);
        object? deserialized = PhpDeserializer.Deserialize(serialized);

        // Assert
        deserialized.Should().BeOfType<PhpHashtable>();
        PhpHashtable recovered = (PhpHashtable)deserialized!;
        recovered["host"]!.ToString().Should().Be("127.0.0.1");
        Convert.ToInt32(recovered["port"]).Should().Be(11235);
        Convert.ToBoolean(recovered["online"]).Should().BeTrue();
    }
}
