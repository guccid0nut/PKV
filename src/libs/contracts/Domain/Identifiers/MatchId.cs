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
///     Strongly typed domain identifier for matches.
/// </summary>
[MemoryPackable]
[JsonConverter(typeof(MatchIdJsonConverter))]
public readonly partial record struct MatchId(int Value) :
    IComparable<MatchId>,
    IComparable,
    IFormattable,
    ISpanFormattable,
    IParsable<MatchId>
{
    public static readonly MatchId Empty = new(0);

    public static implicit operator int(MatchId id) => id.Value;
    public static implicit operator MatchId(int value) => new(value);

    public static bool operator <(MatchId left, MatchId right) => left.Value < right.Value;
    public static bool operator <=(MatchId left, MatchId right) => left.Value <= right.Value;
    public static bool operator >(MatchId left, MatchId right) => left.Value > right.Value;
    public static bool operator >=(MatchId left, MatchId right) => left.Value >= right.Value;

    public int CompareTo(MatchId other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        MatchId other => CompareTo(other),
        int otherInt => Value.CompareTo(otherInt),
        _ => throw new ArgumentException($"Object must be of type {nameof(MatchId)}", nameof(obj))
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format, formatProvider);

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format, provider);

    public static MatchId Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new MatchId(int.Parse(s, provider));
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out MatchId result)
    {
        if (s != null && int.TryParse(s, provider, out int value))
        {
            result = new MatchId(value);
            return true;
        }

        result = default;
        return false;
    }
}

public sealed class MatchIdJsonConverter : JsonConverter<MatchId>
{
    public override MatchId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new MatchId(reader.GetInt32());
        }

        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int value))
        {
            return new MatchId(value);
        }

        throw new JsonException($"Expected number or numeric string for MatchId, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, MatchId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}
