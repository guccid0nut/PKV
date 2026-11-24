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
///     Strongly typed domain identifier for player accounts.
/// </summary>
[MemoryPackable]
[JsonConverter(typeof(AccountIdJsonConverter))]
public readonly partial record struct AccountId(int Value) :
    IComparable<AccountId>,
    IComparable,
    IFormattable,
    ISpanFormattable,
    IParsable<AccountId>
{
    public static readonly AccountId Empty = new(0);

    public static implicit operator int(AccountId id) => id.Value;
    public static implicit operator AccountId(int value) => new(value);

    public static bool operator <(AccountId left, AccountId right) => left.Value < right.Value;
    public static bool operator <=(AccountId left, AccountId right) => left.Value <= right.Value;
    public static bool operator >(AccountId left, AccountId right) => left.Value > right.Value;
    public static bool operator >=(AccountId left, AccountId right) => left.Value >= right.Value;

    public int CompareTo(AccountId other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        AccountId other => CompareTo(other),
        int otherInt => Value.CompareTo(otherInt),
        _ => throw new ArgumentException($"Object must be of type {nameof(AccountId)}", nameof(obj))
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format, formatProvider);

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format, provider);

    public static AccountId Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new AccountId(int.Parse(s, provider));
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out AccountId result)
    {
        if (s != null && int.TryParse(s, provider, out int value))
        {
            result = new AccountId(value);
            return true;
        }

        result = default;
        return false;
    }
}

public sealed class AccountIdJsonConverter : JsonConverter<AccountId>
{
    public override AccountId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new AccountId(reader.GetInt32());
        }

        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out int value))
        {
            return new AccountId(value);
        }

        throw new JsonException($"Expected number or numeric string for AccountId, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, AccountId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}
