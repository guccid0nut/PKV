// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;

namespace Kongor.Modding.Patching;

/// <summary>
///     Specialized patcher for HoN Lua UI and game scripts.
/// </summary>
internal static class LuaScriptPatcher
{
    /// <summary>
    ///     Injects code at the very top of a Lua script (e.g. compatibility shims or imports).
    /// </summary>
    public static string InjectHeader(string luaContent, string headerCode)
    {
        StringBuilder sb = new();
        sb.AppendLine("-- [Kongor Mod Injected Header]");
        sb.AppendLine(headerCode.Trim());
        sb.AppendLine("-- [End Injected Header]");
        sb.AppendLine();
        sb.Append(luaContent);
        return sb.ToString();
    }

    /// <summary>
    ///     Appends code at the very bottom of a Lua script.
    /// </summary>
    public static string AppendFooter(string luaContent, string footerCode)
    {
        StringBuilder sb = new(luaContent);
        if (!luaContent.EndsWith('\n')) sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("-- [Kongor Mod Injected Footer]");
        sb.AppendLine(footerCode.Trim());
        sb.AppendLine("-- [End Injected Footer]");
        return sb.ToString();
    }

    /// <summary>
    ///     Hooks a named Lua function by inserting <paramref name="wrapperImplementation" /> immediately AFTER the end of
    ///     the target function's definition.
    ///     <para>
    ///         The wrapper is expected to capture and redefine the target itself, e.g.
    ///         <c>local _orig = ChatBox.OnMessage; function ChatBox:OnMessage(...) ... return _orig(self, ...) end</c>.
    ///     </para>
    ///     <para>
    ///         Inserting right after the definition (rather than at the top, where the target is still <c>nil</c>, or at
    ///         the end of the file) guarantees the original exists when the wrapper runs AND that code later in the same
    ///         file which captures the function by reference (callbacks, tables) picks up the hooked version.
    ///     </para>
    ///     <para>
    ///         Recognised declaration forms: <c>function Name(</c>, <c>local function Name(</c>, <c>Name = function(</c>
    ///         (also <c>A.B</c> / <c>A:B</c> names). If no declaration is found, or its <c>end</c> cannot be located, the
    ///         wrapper is appended to the end of the file inside an explicitly marked fallback block.
    ///     </para>
    /// </summary>
    public static string HookFunction(string luaContent, string functionName, string wrapperImplementation)
    {
        if (!ValidateLuaSyntax(wrapperImplementation, out string? wrapperError))
        {
            StringBuilder rejected = new(luaContent);
            if (!luaContent.EndsWith('\n')) rejected.AppendLine();
            rejected.AppendLine();
            rejected.AppendLine($"-- [Kongor Hook Rejected: {functionName} (Invalid wrapper Lua syntax: {wrapperError})]");
            return rejected.ToString();
        }

        bool originalWasValid = ValidateLuaSyntax(luaContent, out _);

        string escapedName = Regex.Escape(functionName);
        Match match = Regex.Match(luaContent,
            $@"(?m)^[ \t]*(?:local[ \t]+)?(?<kw>function)[ \t]+{escapedName}[ \t]*\(");
        if (!match.Success)
            match = Regex.Match(luaContent, $@"(?<![\w.:]){escapedName}[ \t]*=[ \t]*(?<kw>function)[ \t]*\(");

        string hookBlock =
            $"-- [Kongor Hook: {functionName}]{Environment.NewLine}{wrapperImplementation.Trim()}{Environment.NewLine}-- [End Hook: {functionName}]";

        int insertAt = match.Success ? FindMatchingEnd(luaContent, match.Groups["kw"].Index) : -1;
        if (insertAt < 0)
        {
            string reason = match.Success ? "declaration found but matching 'end' not located" : "declaration not found";
            StringBuilder fallback = new(luaContent);
            if (!luaContent.EndsWith('\n')) fallback.AppendLine();
            fallback.AppendLine();
            fallback.AppendLine($"-- [Kongor Hook Fallback: {functionName} ({reason}); appended at end of file]");
            fallback.AppendLine(hookBlock);
            return fallback.ToString();
        }

        // Insert on a fresh line directly after the function's closing 'end'.
        StringBuilder sb = new(luaContent.Length + hookBlock.Length + 8);
        sb.Append(luaContent, 0, insertAt);
        sb.AppendLine();
        sb.Append(hookBlock);
        if (insertAt >= luaContent.Length || (luaContent[insertAt] != '\r' && luaContent[insertAt] != '\n'))
            sb.AppendLine();
        sb.Append(luaContent, insertAt, luaContent.Length - insertAt);

        string patched = sb.ToString();
        if (originalWasValid && !ValidateLuaSyntax(patched, out string? postPatchError))
        {
            StringBuilder rollback = new(luaContent);
            if (!luaContent.EndsWith('\n')) rollback.AppendLine();
            rollback.AppendLine();
            rollback.AppendLine($"-- [Kongor Hook Rejected: {functionName} (Post-patch syntax check failed: {postPatchError})]");
            return rollback.ToString();
        }

        return patched;
    }

    /// <summary>
    ///     Validates Lua 5.1 lexical and block-structure syntax (strings, long brackets, comments,
    ///     parentheses/braces/brackets, C-style operator traps, and function/if/do/repeat/end/until balance).
    /// </summary>
    public static bool ValidateLuaSyntax(string luaContent, out string? errorReason)
    {
        errorReason = null;
        if (string.IsNullOrWhiteSpace(luaContent))
        {
            return true;
        }

        Stack<string> blockStack = new();
        Stack<char> delimiterStack = new();
        int i = 0;
        int n = luaContent.Length;

        while (i < n)
        {
            char c = luaContent[i];

            // Comments: --[[ ... ]] / --[==[ ... ]==] or -- to end of line
            if (c == '-' && i + 1 < n && luaContent[i + 1] == '-')
            {
                int longLevel = LongBracketLevel(luaContent, i + 2);
                if (longLevel >= 0)
                {
                    i = SkipLongBracket(luaContent, i + 2, longLevel);
                    if (i < 0)
                    {
                        errorReason = "unterminated long-bracket comment";
                        return false;
                    }
                }
                else
                {
                    while (i < n && luaContent[i] != '\n') i++;
                }
                continue;
            }

            // Short strings
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                bool closed = false;
                while (i < n)
                {
                    if (luaContent[i] == '\\')
                    {
                        i++;
                        if (i < n && luaContent[i] == '\r' && i + 1 < n && luaContent[i + 1] == '\n')
                        {
                            i += 2;
                        }
                        else if (i < n)
                        {
                            i++;
                        }
                        continue;
                    }
                    if (luaContent[i] == '\n' || luaContent[i] == '\r')
                    {
                        errorReason = $"unterminated string literal ({quote})";
                        return false;
                    }
                    if (luaContent[i] == quote)
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    i++;
                }

                if (!closed)
                {
                    errorReason = $"unterminated string literal ({quote})";
                    return false;
                }
                continue;
            }

            // Long-bracket strings [[ ... ]] / [=[ ... ]=]
            if (c == '[')
            {
                int longLevel = LongBracketLevel(luaContent, i);
                if (longLevel >= 0)
                {
                    i = SkipLongBracket(luaContent, i, longLevel);
                    if (i < 0)
                    {
                        errorReason = "unterminated long-bracket string";
                        return false;
                    }
                    continue;
                }
            }

            // C-style operator traps outside strings/comments
            if (c == '!' && i + 1 < n && luaContent[i + 1] == '=')
            {
                errorReason = "invalid operator '!=' (Lua uses '~=')";
                return false;
            }
            if (c == '&' && i + 1 < n && luaContent[i + 1] == '&')
            {
                errorReason = "invalid operator '&&' (Lua uses 'and')";
                return false;
            }
            if (c == '|' && i + 1 < n && luaContent[i + 1] == '|')
            {
                errorReason = "invalid operator '||' (Lua uses 'or')";
                return false;
            }

            // Delimiters
            if (c is '(' or '{' or '[')
            {
                delimiterStack.Push(c);
                i++;
                continue;
            }

            if (c is ')' or '}' or ']')
            {
                char expectedOpener = c switch
                {
                    ')' => '(',
                    '}' => '{',
                    ']' => '[',
                    _ => '\0'
                };
                if (delimiterStack.Count == 0 || delimiterStack.Pop() != expectedOpener)
                {
                    errorReason = $"mismatched closing delimiter '{c}'";
                    return false;
                }
                i++;
                continue;
            }

            // Identifiers & keywords
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(luaContent[i]) || luaContent[i] == '_')) i++;

                bool isField = start > 0 && (luaContent[start - 1] == '.' || luaContent[start - 1] == ':') &&
                               !(start > 1 && luaContent[start - 2] == '.');
                if (isField) continue;

                ReadOnlySpan<char> token = luaContent.AsSpan(start, i - start);
                switch (token)
                {
                    case "function":
                        blockStack.Push("function");
                        break;
                    case "if":
                        blockStack.Push("if");
                        break;
                    case "do":
                        blockStack.Push("do");
                        break;
                    case "repeat":
                        blockStack.Push("repeat");
                        break;
                    case "elseif":
                    case "else":
                        if (blockStack.Count == 0 || blockStack.Peek() != "if")
                        {
                            errorReason = $"unexpected '{token.ToString()}' outside 'if' block";
                            return false;
                        }
                        break;
                    case "end":
                        if (blockStack.Count == 0)
                        {
                            errorReason = "unexpected 'end' statement (no open block)";
                            return false;
                        }
                        string poppedEnd = blockStack.Pop();
                        if (poppedEnd == "repeat")
                        {
                            errorReason = "'repeat' block must be closed with 'until', not 'end'";
                            return false;
                        }
                        break;
                    case "until":
                        if (blockStack.Count == 0 || blockStack.Pop() != "repeat")
                        {
                            errorReason = "unexpected 'until' without matching 'repeat'";
                            return false;
                        }
                        break;
                }

                continue;
            }

            i++;
        }

        if (delimiterStack.Count > 0)
        {
            errorReason = $"unclosed delimiter '{delimiterStack.Peek()}'";
            return false;
        }

        if (blockStack.Count > 0)
        {
            string unclosed = blockStack.Peek();
            string expectedCloser = unclosed == "repeat" ? "until" : "end";
            errorReason = $"unclosed '{unclosed}' block (missing '{expectedCloser}')";
            return false;
        }

        (bool luacValid, string? luacError) =
            ASPIRE.Common.Prebaking.Generators.LuaBytecodeCompilerService.ValidateSyntaxWithLuac(
                luaContent,
                "patched_script.lua");
        if (!luacValid)
        {
            errorReason = luacError ?? "luac51 syntax verification failed";
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Given the index of a <c>function</c> keyword, returns the index just past its matching <c>end</c>,
    ///     or -1 if it cannot be determined. Skips strings (short and long-bracket) and comments.
    ///     Block openers: <c>function</c>, <c>if</c>, <c>do</c> (covers while/for), <c>repeat</c>;
    ///     closers: <c>end</c>, <c>until</c>.
    /// </summary>
    private static int FindMatchingEnd(string src, int functionKeywordIndex)
    {
        int depth = 0;
        int i = functionKeywordIndex;
        int n = src.Length;

        while (i < n)
        {
            char c = src[i];

            // Comments: --[[ ... ]] / --[==[ ... ]==] or -- to end of line
            if (c == '-' && i + 1 < n && src[i + 1] == '-')
            {
                int longLevel = LongBracketLevel(src, i + 2);
                if (longLevel >= 0)
                {
                    i = SkipLongBracket(src, i + 2, longLevel);
                    if (i < 0) return -1;
                }
                else
                {
                    while (i < n && src[i] != '\n') i++;
                }
                continue;
            }

            // Strings
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < n && src[i] != c)
                {
                    if (src[i] == '\\') i++;
                    else if (src[i] == '\n') return -1; // unterminated short string
                    i++;
                }
                i++;
                continue;
            }

            if (c == '[')
            {
                int longLevel = LongBracketLevel(src, i);
                if (longLevel >= 0)
                {
                    i = SkipLongBracket(src, i, longLevel);
                    if (i < 0) return -1;
                    continue;
                }
            }

            // Identifiers / keywords
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;

                // Ignore field accesses like obj.end / obj:do (not valid Lua keywords there anyway, but be safe).
                bool isField = start > 0 && (src[start - 1] == '.' || src[start - 1] == ':') &&
                               !(start > 1 && src[start - 2] == '.');
                if (isField) continue;

                switch (src.AsSpan(start, i - start))
                {
                    case "function":
                    case "if":
                    case "do":
                    case "repeat":
                        depth++;
                        break;
                    case "end":
                    case "until":
                        depth--;
                        if (depth == 0) return i;
                        if (depth < 0) return -1;
                        break;
                }

                continue;
            }

            i++;
        }

        return -1;
    }

    /// <summary>Returns the '=' level of a long bracket opening at <paramref name="i" /> ("[[" = 0, "[=[" = 1 ...), or -1.</summary>
    private static int LongBracketLevel(string src, int i)
    {
        if (i >= src.Length || src[i] != '[') return -1;
        int j = i + 1;
        int level = 0;
        while (j < src.Length && src[j] == '=') { level++; j++; }
        return j < src.Length && src[j] == '[' ? level : -1;
    }

    /// <summary>Skips a long bracket starting at <paramref name="i" />; returns the index after the closing bracket, or -1.</summary>
    private static int SkipLongBracket(string src, int i, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int contentStart = i + level + 2;
        int closeIndex = src.IndexOf(close, contentStart, StringComparison.Ordinal);
        return closeIndex < 0 ? -1 : closeIndex + close.Length;
    }

    /// <summary>
    ///     Replaces occurrences of a deprecated global or function name in Lua code.
    /// </summary>
    public static string ReplaceSymbol(string luaContent, string oldSymbol, string newSymbol)
    {
        string pattern = $@"\b{Regex.Escape(oldSymbol)}\b";
        return Regex.Replace(luaContent, pattern, newSymbol);
    }
}
