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
///     Strongly typed domain identifier for player clans.
/// </summary>
[MemoryPackable]
[JsonConverter(typeof(ClanIdJsonConverter))]
public readonly partial record struct ClanId(int Value) :
    IComparable<ClanId>,
    IComparable,
    IFormattable,
    ISpanFormattable,
    IParsable<ClanId>
{
    public static readonly ClanId Empty = new(0);

    public static implicit operator int(ClanId id) => id.Value;
    public static implicit operator ClanId(int value) => new(value);

    public static bool operator <(ClanId left, ClanId right) => left.Value < right.Value;
    public static bool operator <=(ClanId left, ClanId right) => left.Value <= right.Value;
    public static bool operator >(ClanId left, ClanId right) => left.Value > right.Value;
    public static bool operator >=(ClanId left, ClanId right) => left.Value >= right.Value;

    public int CompareTo(ClanId other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        ClanId other => CompareTo(other),
        int otherInt => Value.CompareTo(otherInt),
        _ => throw new ArgumentException($"Object must be of type {nameof(ClanId)}", nameof(obj))
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format, formatProvider);

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format, provider);

    public static ClanId Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new ClanId(int.Parse(s, provider));
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ClanId result)
    {
        if (s != null && int.TryParse(s, provider, out int value))
        {
            result = new ClanId(value);
            return true;
        }

        result = default;
        return false;
    }
}

public sealed class ClanIdJsonConverter : JsonConverter<ClanId>
{
    public override ClanId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new ClanId(reader.GetInt32());
        }

        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int value))
        {
            return new ClanId(value);
        }

        throw new JsonException($"Expected number or numeric string for ClanId, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, ClanId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}
