// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Text;
using System.Globalization;

namespace ASPIRE.Common.Serialization.PHP;

// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UseIndexFromEndExpression
// ReSharper disable ConvertIfToOrExpression
// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable RedundantExplicitArrayCreation
// ReSharper disable SuggestBaseTypeForParameter
// ReSharper disable ConvertToAutoProperty
// ReSharper disable ConvertIfStatementToReturnStatement
// ReSharper disable SimplifyConditionalTernaryExpression
// ReSharper disable ReturnValueOfMethodIsUnused
// ReSharper disable MustUseReturnValue
/// <summary>
///     High-performance zero-allocation native PHP wire protocol deserializer.
/// </summary>
public static class PhpDeserializer
{
    /// <summary>
    ///     Deserializes a PHP wire format payload into a strongly-typed target object <typeparamref name="T" /> with zero
    ///     reflection.
    /// </summary>
    public static T? Deserialize<T>(string? payload) where T : IPhpDeserializable<T>
    {
        object? deserialized = Deserialize(payload);
        if (deserialized is PhpHashtable table) return T.DeserializePhp(table);

        return default;
    }

    /// <summary>
    ///     Deserializes a PHP wire format payload span into a strongly-typed target object <typeparamref name="T" /> with zero
    ///     reflection.
    /// </summary>
    public static T? Deserialize<T>(ReadOnlySpan<char> span) where T : IPhpDeserializable<T>
    {
        object? deserialized = Deserialize(span);
        if (deserialized is PhpHashtable table) return T.DeserializePhp(table);

        return default;
    }

    /// <summary>
    ///     Deserializes a UTF-8 PHP wire format payload span into a strongly-typed target object <typeparamref name="T" />
    ///     with zero reflection.
    /// </summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Span) where T : IPhpDeserializable<T>
    {
        object? deserialized = Deserialize(utf8Span);
        if (deserialized is PhpHashtable table) return T.DeserializePhp(table);

        return default;
    }

    /// <summary>
    ///     Deserializes a PHP wire format payload into native C# objects (primitives or <see cref="PhpHashtable" />).
    /// </summary>
    public static object? Deserialize(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;

        ReadOnlySpan<char> span = payload.AsSpan().Trim();
        int position = 0;
        return ParseValue(span, ref position);
    }

    /// <summary>
    ///     Deserializes a PHP wire format payload span into native C# objects.
    /// </summary>
    public static object? Deserialize(ReadOnlySpan<char> span)
    {
        ReadOnlySpan<char> trimmed = span.Trim();
        int position = 0;
        return ParseValue(trimmed, ref position);
    }

    /// <summary>
    ///     Deserializes a UTF-8 PHP wire format payload span into native C# objects.
    /// </summary>
    public static object? Deserialize(ReadOnlySpan<byte> utf8Span)
    {
        ReadOnlySpan<byte> trimmed = TrimUtf8(utf8Span);
        int position = 0;
        return ParseValue(trimmed, ref position);
    }

    private static ReadOnlySpan<byte> TrimUtf8(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && IsWhitespace(span[start])) start++;

        int end = span.Length - 1;
        while (end >= start && IsWhitespace(span[end])) end--;

        if (start > end) return [];

        return span.Slice(start, end - start + 1);

        static bool IsWhitespace(byte b)
        {
            return b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
        }
    }

    private static object? ParseValue(ReadOnlySpan<char> span, ref int pos, int depth = 0)
    {
        if (depth > 32) throw new FormatException("Exceeded maximum recursion depth of 32");

        if (pos >= span.Length) return null;

        char typeMarker = span[pos];
        switch (typeMarker)
        {
            case 'N': // Null: N;
            case 'n':
                pos += 2; // Skip N;
                return null;

            case 'b': // Bool: b:1; or b:0;
            case 'B':
                if (pos + 2 >= span.Length) return null;

                pos += 2; // Skip b:
                bool boolVal = span[pos] == '1';
                pos++; // Skip 1 or 0
                if (pos < span.Length && span[pos] == ';') pos++; // Skip ';'

                return boolVal;

            case 'i': // Integer: i:123;
            case 'I':
                if (pos + 2 > span.Length) return null;

                pos += 2; // Skip i:
                int endIntPos = span[pos..].IndexOf(';');
                if (endIntPos < 0) endIntPos = span.Length - pos;

                ReadOnlySpan<char> intSpan = span.Slice(pos, endIntPos);
                pos += endIntPos;
                if (pos < span.Length && span[pos] == ';') pos++; // Skip ';'

                if (int.TryParse(intSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt))
                    return parsedInt;

                return long.TryParse(intSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong)
                    ? parsedLong
                    : 0;

            case 'd': // Double/Float: d:123.45;
            case 'D':
                if (pos + 2 > span.Length) return null;

                pos += 2; // Skip d:
                int endDoublePos = span[pos..].IndexOf(';');
                if (endDoublePos < 0) endDoublePos = span.Length - pos;

                ReadOnlySpan<char> doubleSpan = span.Slice(pos, endDoublePos);
                pos += endDoublePos;
                if (pos < span.Length && span[pos] == ';') pos++; // Skip ';'

                return double.TryParse(doubleSpan, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out double parsedDouble)
                    ? parsedDouble
                    : 0.0;

            case 's': // String: s:5:"hello";
            case 'S':
                if (pos + 2 > span.Length) return null;

                pos += 2; // Skip s:
                int lenEndPos = span[pos..].IndexOf(':');
                if (lenEndPos < 0) return null;

                ReadOnlySpan<char> lenSpan = span.Slice(pos, lenEndPos);
                if (!int.TryParse(lenSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stringLength) ||
                    stringLength < 0) return null;

                pos += lenEndPos + 1; // Skip len and ':'
                if (pos < span.Length && span[pos] == '"') pos++; // Skip opening '"'

                // In PHP serialization, stringLength specifies the UTF-8 byte count, which differs from UTF-16 code unit count for multi-byte characters.
                int charCount = 0;
                int byteCount = 0;
                ReadOnlySpan<char> remaining = span[pos..];
                while (charCount < remaining.Length && byteCount < stringLength)
                {
                    char c = remaining[charCount];
                    if (char.IsHighSurrogate(c))
                    {
                        if (charCount + 1 >= remaining.Length || !char.IsLowSurrogate(remaining[charCount + 1]))
                            return null;
                        byteCount += 4;
                        charCount += 2;
                    }
                    else
                    {
                        byteCount += c switch
                        {
                            <= '\u007F' => 1,
                            <= '\u07FF' => 2,
                            _ => 3
                        };
                        charCount++;
                    }
                }

                if (byteCount != stringLength || charCount > span.Length - pos) return null;

                ReadOnlySpan<char> strSpan = span.Slice(pos, charCount);
                pos += charCount;

                if (pos < span.Length && span[pos] == '"') pos++; // Skip closing quote

                if (pos < span.Length && span[pos] == ';') pos++; // Skip trailing ';'

                return strSpan.ToString();

            case 'a': // Array: a:2:{...}
            case 'A':
                if (depth > 32) throw new FormatException("Exceeded maximum recursion depth of 32");

                if (pos + 2 > span.Length) return new PhpHashtable();

                pos += 2; // Skip a:
                int countEndPos = span[pos..].IndexOf(':');
                if (countEndPos < 0) return new PhpHashtable();

                ReadOnlySpan<char> countSpan = span.Slice(pos, countEndPos);
                if (!int.TryParse(countSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arrayCount) ||
                    arrayCount < 0) return new PhpHashtable();

                pos += countEndPos + 1; // Skip count and ':'
                if (pos < span.Length && span[pos] == '{') pos++; // Skip '{'

                PhpHashtable table = new(Math.Min(arrayCount, 1024));
                for (int i = 0; i < arrayCount; i++)
                {
                    if (pos >= span.Length || span[pos] == '}') break;

                    object? key = ParseValue(span, ref pos, depth + 1);
                    object? val = ParseValue(span, ref pos, depth + 1);

                    if (key != null) table[key] = val;
                }

                if (pos < span.Length && span[pos] == '}') pos++; // Skip closing '}'

                return table;

            default:
                return null;
        }
    }

    private static object? ParseValue(ReadOnlySpan<byte> span, ref int pos, int depth = 0)
    {
        if (depth > 32) throw new FormatException("Exceeded maximum recursion depth of 32");

        if (pos >= span.Length) return null;

        byte typeMarker = span[pos];
        switch (typeMarker)
        {
            case (byte)'N': // Null: N;
            case (byte)'n':
                pos += 2; // Skip N;
                return null;

            case (byte)'b': // Bool: b:1; or b:0;
            case (byte)'B':
                if (pos + 2 >= span.Length) return null;

                pos += 2; // Skip b:
                bool boolVal = span[pos] == (byte)'1';
                pos++; // Skip 1 or 0
                if (pos < span.Length && span[pos] == (byte)';') pos++; // Skip ';'

                return boolVal;

            case (byte)'i': // Integer: i:123;
            case (byte)'I':
                if (pos + 2 > span.Length) return null;

                pos += 2; // Skip i:
                int endIntPos = span[pos..].IndexOf((byte)';');
                if (endIntPos < 0) endIntPos = span.Length - pos;

                ReadOnlySpan<byte> intSpan = span.Slice(pos, endIntPos);
                pos += endIntPos;
                if (pos < span.Length && span[pos] == (byte)';') pos++; // Skip ';'

                if (Utf8Parser.TryParse(intSpan, out int parsedInt, out _)) return parsedInt;

                return Utf8Parser.TryParse(intSpan, out long parsedLong, out _)
                    ? parsedLong
                    : 0;

            case (byte)'d': // Double/Float: d:123.45;
            case (byte)'D':
                if (pos + 2 > span.Length) return null;

                pos += 2; // Skip d:
                int endDoublePos = span[pos..].IndexOf((byte)';');
                if (endDoublePos < 0) endDoublePos = span.Length - pos;

                ReadOnlySpan<byte> doubleSpan = span.Slice(pos, endDoublePos);
                pos += endDoublePos;
                if (pos < span.Length && span[pos] == (byte)';') pos++; // Skip ';'

                return Utf8Parser.TryParse(doubleSpan, out double parsedDouble, out _)
                    ? parsedDouble
                    : 0.0;

            case (byte)'s': // String: s:5:"hello";
            case (byte)'S':
                if (pos + 2 > span.Length) return null;

                pos += 2; // Skip s:
                int lenEndPos = span[pos..].IndexOf((byte)':');
                if (lenEndPos < 0) return null;

                ReadOnlySpan<byte> lenSpan = span.Slice(pos, lenEndPos);
                if (!Utf8Parser.TryParse(lenSpan, out int stringLength, out _) || stringLength < 0) return null;

                pos += lenEndPos + 1; // Skip len and ':'
                if (pos < span.Length && span[pos] == (byte)'"') pos++; // Skip opening '"'

                if (stringLength > span.Length - pos) return null;

                ReadOnlySpan<byte> strSpan = span.Slice(pos, stringLength);
                pos += stringLength;

                if (pos < span.Length && span[pos] == (byte)'"') pos++; // Skip closing quote

                if (pos < span.Length && span[pos] == (byte)';') pos++; // Skip trailing ';'

                return Encoding.UTF8.GetString(strSpan);

            case (byte)'a': // Array: a:2:{...}
            case (byte)'A':
                if (depth > 32) throw new FormatException("Exceeded maximum recursion depth of 32");

                if (pos + 2 > span.Length) return new PhpHashtable();

                pos += 2; // Skip a:
                int countEndPos = span[pos..].IndexOf((byte)':');
                if (countEndPos < 0) return new PhpHashtable();

                ReadOnlySpan<byte> countSpan = span.Slice(pos, countEndPos);
                if (!Utf8Parser.TryParse(countSpan, out int arrayCount, out _) || arrayCount < 0)
                    return new PhpHashtable();

                pos += countEndPos + 1; // Skip count and ':'
                if (pos < span.Length && span[pos] == (byte)'{') pos++; // Skip '{'

                PhpHashtable table = new(Math.Min(arrayCount, 1024));
                for (int i = 0; i < arrayCount; i++)
                {
                    if (pos >= span.Length || span[pos] == (byte)'}') break;

                    object? key = ParseValue(span, ref pos, depth + 1);
                    object? val = ParseValue(span, ref pos, depth + 1);

                    if (key != null) table[key] = val;
                }

                if (pos < span.Length && span[pos] == (byte)'}') pos++; // Skip closing '}'

                return table;

            default:
                return null;
        }
    }
}
