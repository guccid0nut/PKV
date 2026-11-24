// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryPack;

namespace NEXUS.Contracts.Domain.Identifiers;

/// <summary>
///     Strongly typed domain identifier for platform users.
/// </summary>
[MemoryPackable]
[JsonConverter(typeof(UserIdJsonConverter))]
public readonly partial record struct UserId(int Value) :
    IComparable<UserId>,
    IComparable,
    IFormattable,
    ISpanFormattable,
    IParsable<UserId>
{
    public static readonly UserId Empty = new(0);

    public static implicit operator int(UserId id) => id.Value;
    public static implicit operator UserId(int value) => new(value);

    public static bool operator <(UserId left, UserId right) => left.Value < right.Value;
    public static bool operator <=(UserId left, UserId right) => left.Value <= right.Value;
    public static bool operator >(UserId left, UserId right) => left.Value > right.Value;
    public static bool operator >=(UserId left, UserId right) => left.Value >= right.Value;

    public int CompareTo(UserId other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        UserId other => CompareTo(other),
        int otherInt => Value.CompareTo(otherInt),
        _ => throw new ArgumentException($"Object must be of type {nameof(UserId)}", nameof(obj))
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format, formatProvider);

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format, provider);

    public static UserId Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new UserId(int.Parse(s, provider));
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out UserId result)
    {
        if (s != null && int.TryParse(s, provider, out int value))
        {
            result = new UserId(value);
            return true;
        }

        result = default;
        return false;
    }
}

public sealed class UserIdJsonConverter : JsonConverter<UserId>
{
    public override UserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new UserId(reader.GetInt32());
        }

        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int value))
        {
            return new UserId(value);
        }

        throw new JsonException($"Expected number or numeric string for UserId, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}
