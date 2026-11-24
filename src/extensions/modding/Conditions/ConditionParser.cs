// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;

namespace Kongor.Modding.Conditions;

/// <summary>
///     Recursive-descent AST parser for HoN mod condition expressions.
/// </summary>
public sealed class ConditionParser(IReadOnlyList<ConditionToken> tokens)
{
    private readonly IReadOnlyList<ConditionToken> _tokens = tokens;
    private int _index;

    public static ConditionNode Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return new ConstantNode(true);

        IReadOnlyList<ConditionToken> tokens = ConditionTokenizer.Tokenize(expression);
        ConditionParser parser = new(tokens);
        return parser.ParseExpression();
    }

    public ConditionNode ParseExpression()
    {
        return ParseOr();
    }

    private ConditionNode ParseOr()
    {
        ConditionNode left = ParseAnd();

        while (Match(ConditionTokenType.Or))
        {
            ConditionNode right = ParseAnd();
            left = new OrNode(left, right);
        }

        return left;
    }

    private ConditionNode ParseAnd()
    {
        ConditionNode left = ParseUnary();

        while (Match(ConditionTokenType.And))
        {
            ConditionNode right = ParseUnary();
            left = new AndNode(left, right);
        }

        return left;
    }

    private ConditionNode ParseUnary()
    {
        if (Match(ConditionTokenType.Not))
        {
            ConditionNode operand = ParseUnary();
            return new NotNode(operand);
        }

        return ParsePrimary();
    }

    private ConditionNode ParsePrimary()
    {
        ConditionToken current = Peek();

        if (Match(ConditionTokenType.OpenParen))
        {
            ConditionNode inner = ParseExpression();
            Consume(ConditionTokenType.CloseParen, "Expected ')' after parenthesized condition expression.");
            return inner;
        }

        if (Match(ConditionTokenType.True)) return new ConstantNode(true);

        if (Match(ConditionTokenType.False)) return new ConstantNode(false);

        if (Match(ConditionTokenType.StringLiteral) || Match(ConditionTokenType.Identifier))
        {
            ConditionToken firstToken = Previous();
            string modString = firstToken.Value;

            if (firstToken.Type == ConditionTokenType.Identifier)
            {
                StringBuilder sb = new(modString);
                while (Peek().Type == ConditionTokenType.Identifier) sb.Append(' ').Append(Advance().Value);
                modString = sb.ToString();
            }

            VersionRangeExpression? verConstraint = null;

            // Check if string literal itself contains bracketed version: 'ModName [v1.2.0]'
            int bracketIdx = modString.IndexOf('[');
            if (bracketIdx >= 0)
            {
                int bracketEnd = modString.IndexOf(']', bracketIdx);
                if (bracketEnd > bracketIdx)
                {
                    string rawVer = modString.Substring(bracketIdx + 1, bracketEnd - bracketIdx - 1);
                    verConstraint = VersionRangeExpression.Parse(rawVer);
                    modString = modString[..bracketIdx].Trim();
                }
            }

            // Or if next token is a VersionSpec: [v1.2.0]
            if (Peek().Type == ConditionTokenType.VersionSpec)
            {
                ConditionToken verToken = Consume(ConditionTokenType.VersionSpec, "Expected version specification.");
                verConstraint = VersionRangeExpression.Parse(verToken.Value);
            }

            return new ModPredicateNode(modString, verConstraint);
        }

        if (current.Type == ConditionTokenType.Eof) return new ConstantNode(true);

        throw new FormatException(
            $"Unexpected token '{current.Value}' at position {current.Position} in condition expression.");
    }

    private bool Match(ConditionTokenType type)
    {
        if (Check(type))
        {
            Advance();
            return true;
        }

        return false;
    }

    private ConditionToken Consume(ConditionTokenType type, string errorMessage)
    {
        if (Check(type)) return Advance();
        ConditionToken current = Peek();
        throw new FormatException(
            $"{errorMessage} Got '{current.Value}' ({current.Type}) at position {current.Position}.");
    }

    private bool Check(ConditionTokenType type)
    {
        if (IsAtEnd()) return type == ConditionTokenType.Eof;
        return Peek().Type == type;
    }

    private ConditionToken Advance()
    {
        if (!IsAtEnd()) _index++;
        return Previous();
    }

    private bool IsAtEnd()
    {
        return _index >= _tokens.Count || _tokens[_index].Type == ConditionTokenType.Eof;
    }

    private ConditionToken Peek()
    {
        return _index < _tokens.Count ? _tokens[_index] : _tokens[^1];
    }

    private ConditionToken Previous()
    {
        return _tokens[_index - 1];
    }
}
