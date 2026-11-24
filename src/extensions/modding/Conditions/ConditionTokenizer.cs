// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;

namespace Kongor.Modding.Conditions;

public enum ConditionTokenType
{
    Identifier,
    StringLiteral,
    VersionSpec,
    Not,
    And,
    Or,
    OpenParen,
    CloseParen,
    True,
    False,
    Eof
}

public sealed record ConditionToken(ConditionTokenType Type, string Value, int Position);

/// <summary>
///     Tokenizes Boolean condition expressions for HoN modding rules.
/// </summary>
public static class ConditionTokenizer
{
    public static IReadOnlyList<ConditionToken> Tokenize(string expression)
    {
        List<ConditionToken> tokens = [];
        if (string.IsNullOrWhiteSpace(expression))
        {
            tokens.Add(new ConditionToken(ConditionTokenType.Eof, string.Empty, 0));
            return tokens;
        }

        int pos = 0;
        int len = expression.Length;

        while (pos < len)
        {
            char ch = expression[pos];

            if (char.IsWhiteSpace(ch))
            {
                pos++;
                continue;
            }

            if (ch == '(')
            {
                tokens.Add(new ConditionToken(ConditionTokenType.OpenParen, "(", pos));
                pos++;
                continue;
            }

            if (ch == ')')
            {
                tokens.Add(new ConditionToken(ConditionTokenType.CloseParen, ")", pos));
                pos++;
                continue;
            }

            if (ch == '!')
            {
                tokens.Add(new ConditionToken(ConditionTokenType.Not, "!", pos));
                pos++;
                continue;
            }

            if (ch == '&')
            {
                int start = pos;
                pos++;
                if (pos < len && expression[pos] == '&') pos++;
                tokens.Add(new ConditionToken(ConditionTokenType.And, "and", start));
                continue;
            }

            if (ch == '|')
            {
                int start = pos;
                pos++;
                if (pos < len && expression[pos] == '|') pos++;
                tokens.Add(new ConditionToken(ConditionTokenType.Or, "or", start));
                continue;
            }

            // Quoted string '...' or "..."
            if (ch is '\'' or '"')
            {
                char quote = ch;
                int start = pos;
                pos++;
                StringBuilder sb = new();
                while (pos < len && expression[pos] != quote)
                {
                    if (expression[pos] == '\\' && pos + 1 < len)
                    {
                        pos++;
                        sb.Append(expression[pos]);
                    }
                    else
                    {
                        sb.Append(expression[pos]);
                    }

                    pos++;
                }

                if (pos < len && expression[pos] == quote) pos++;

                string strVal = sb.ToString();
                tokens.Add(new ConditionToken(ConditionTokenType.StringLiteral, strVal, start));
                continue;
            }

            // Bracketed version spec [v1.2] or [>=1.0]
            if (ch == '[')
            {
                int start = pos;
                pos++;
                StringBuilder sb = new();
                while (pos < len && expression[pos] != ']')
                {
                    sb.Append(expression[pos]);
                    pos++;
                }

                if (pos < len && expression[pos] == ']') pos++;
                tokens.Add(new ConditionToken(ConditionTokenType.VersionSpec, sb.ToString().Trim(), start));
                continue;
            }

            // Word / Identifier / Keyword
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
            {
                int start = pos;
                StringBuilder sb = new();
                while (pos < len && (char.IsLetterOrDigit(expression[pos]) || expression[pos] == '_' ||
                                     expression[pos] == '-' || expression[pos] == '.'))
                {
                    sb.Append(expression[pos]);
                    pos++;
                }

                string word = sb.ToString();
                string lower = word.ToLowerInvariant();

                switch (lower)
                {
                    case "not":
                        tokens.Add(new ConditionToken(ConditionTokenType.Not, word, start));
                        break;
                    case "and":
                        tokens.Add(new ConditionToken(ConditionTokenType.And, word, start));
                        break;
                    case "or":
                        tokens.Add(new ConditionToken(ConditionTokenType.Or, word, start));
                        break;
                    case "true":
                        tokens.Add(new ConditionToken(ConditionTokenType.True, word, start));
                        break;
                    case "false":
                        tokens.Add(new ConditionToken(ConditionTokenType.False, word, start));
                        break;
                    default:
                        tokens.Add(new ConditionToken(ConditionTokenType.Identifier, word, start));
                        break;
                }

                continue;
            }

            // Skip unrecognized character
            pos++;
        }

        tokens.Add(new ConditionToken(ConditionTokenType.Eof, string.Empty, pos));
        return tokens;
    }
}
