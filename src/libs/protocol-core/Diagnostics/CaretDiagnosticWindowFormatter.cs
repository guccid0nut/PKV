// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Text;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Structured result of formatting a source-code diagnostic with word-boundary windowing and a <c>^~~~~</c> caret underline.
/// </summary>
public sealed record FormattedCaretDiagnostic(
    int LineOneBased,
    int ColumnOneBased,
    int TokenLength,
    string WindowedLineText,
    string CaretLineText,
    int CaretOffsetInWindow,
    bool TruncatedLeft,
    bool TruncatedRight,
    string FormattedBlock);

/// <summary>
/// Phase 13 (#165): Word-Boundary-Aware 120-Column Source Windowing, <c>^~~~~</c> Caret Formatter
/// &amp; Reference-Counted Buffer Double-Free Auditor (<c>PatternLanguage/error.cpp:35-116</c> &amp;
/// <c>evaluator.cpp:1254-1363</c> port).
/// </summary>
public static class CaretDiagnosticWindowFormatter
{
    public const int DefaultMaxLineLength = 120;
    private const string EllipsisPrefix = "... ";
    private const string EllipsisSuffix = " ...";

    /// <summary>
    /// Formats a compiler/linter-style diagnostic for <paramref name="sourceText"/> at 1-based
    /// <c>(lineOneBased, columnOneBased)</c> with a <c>^~~~~</c> underline. When the target line exceeds
    /// <paramref name="maxLineLength"/>, centers a window around the token, snaps outward to whitespace boundaries
    /// (<c>error.cpp:59-79</c>), prepends/appends <c>"..."</c>, and adjusts the caret column offset accordingly.
    /// </summary>
    public static FormattedCaretDiagnostic FormatDiagnostic(
        string sourceText,
        int lineOneBased,
        int columnOneBased,
        int tokenLength,
        string message,
        int maxLineLength = DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(message);

        if (lineOneBased < 1) lineOneBased = 1;
        if (columnOneBased < 1) columnOneBased = 1;
        if (tokenLength < 1) tokenLength = 1;
        if (maxLineLength < 20) maxLineLength = 20;

        string rawLine = ExtractLine(sourceText, lineOneBased).Replace('\t', ' ');
        int zeroBasedCol = Math.Clamp(columnOneBased - 1, 0, Math.Max(0, rawLine.Length));
        int effectiveTokenLen = Math.Max(1, tokenLength);

        bool truncatedLeft = false;
        bool truncatedRight = false;
        string windowedLine;
        int caretColumnInWindow;

        if (rawLine.Length <= maxLineLength)
        {
            windowedLine = rawLine;
            caretColumnInWindow = zeroBasedCol;
        }
        else
        {
            int center = zeroBasedCol + (effectiveTokenLen / 2);
            int halfWindow = maxLineLength / 2;

            int start = Math.Max(0, center - halfWindow);
            int end = Math.Min(rawLine.Length, start + maxLineLength);
            if (end - start < maxLineLength)
            {
                start = Math.Max(0, end - maxLineLength);
            }

            // Ensure the token start itself is inside [start, end)
            if (zeroBasedCol < start)
            {
                start = zeroBasedCol;
                end = Math.Min(rawLine.Length, start + maxLineLength);
            }

            // Snap `start` outward (left) to a whitespace word boundary if within 16 chars (error.cpp:59-68)
            if (start > 0)
            {
                int snapLeft = start;
                int maxLeftProbe = Math.Max(0, start - 16);
                while (snapLeft > maxLeftProbe && !char.IsWhiteSpace(rawLine[snapLeft]))
                {
                    snapLeft--;
                }

                if (char.IsWhiteSpace(rawLine[snapLeft]))
                {
                    start = snapLeft + 1;
                }
            }

            // Snap `end` outward (right) to a whitespace word boundary if within 16 chars (error.cpp:70-79)
            if (end < rawLine.Length)
            {
                int snapRight = end;
                int maxRightProbe = Math.Min(rawLine.Length, end + 16);
                while (snapRight < maxRightProbe && !char.IsWhiteSpace(rawLine[snapRight]))
                {
                    snapRight++;
                }

                if (snapRight < rawLine.Length && char.IsWhiteSpace(rawLine[snapRight]))
                {
                    end = snapRight;
                }
            }

            truncatedLeft = start > 0;
            truncatedRight = end < rawLine.Length;

            string slice = rawLine.Substring(start, end - start);
            StringBuilder lineBuilder = new StringBuilder(slice.Length + 8);
            caretColumnInWindow = zeroBasedCol - start;

            if (truncatedLeft)
            {
                lineBuilder.Append(EllipsisPrefix);
                caretColumnInWindow += EllipsisPrefix.Length;
            }

            lineBuilder.Append(slice);

            if (truncatedRight)
            {
                lineBuilder.Append(EllipsisSuffix);
            }

            windowedLine = lineBuilder.ToString();
        }

        int underlineTildes = Math.Max(0, Math.Min(effectiveTokenLen - 1, Math.Max(0, windowedLine.Length - caretColumnInWindow - 1)));
        string caretLine = new string(' ', Math.Max(0, caretColumnInWindow)) + "^" + new string('~', underlineTildes);

        string lineNumStr = lineOneBased.ToString();
        string gutterPad = new string(' ', lineNumStr.Length);

        StringBuilder fullBlock = new StringBuilder(256);
        fullBlock.Append("error[").Append(lineOneBased).Append(':').Append(columnOneBased).Append("]: ").AppendLine(message);
        fullBlock.Append(' ').Append(gutterPad).AppendLine(" |");
        fullBlock.Append(' ').Append(lineNumStr).Append(" | ").AppendLine(windowedLine);
        fullBlock.Append(' ').Append(gutterPad).Append(" | ").Append(caretLine);

        return new FormattedCaretDiagnostic(
            LineOneBased: lineOneBased,
            ColumnOneBased: columnOneBased,
            TokenLength: effectiveTokenLen,
            WindowedLineText: windowedLine,
            CaretLineText: caretLine,
            CaretOffsetInWindow: caretColumnInWindow,
            TruncatedLeft: truncatedLeft,
            TruncatedRight: truncatedRight,
            FormattedBlock: fullBlock.ToString());
    }

    private static string ExtractLine(string text, int lineOneBased)
    {
        int currentLine = 1;
        int lineStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (currentLine == lineOneBased)
            {
                int lineEnd = i;
                while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
                {
                    lineEnd++;
                }

                return text.Substring(i, lineEnd - i);
            }

            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                currentLine++;
                lineStart = i + 1;
            }
            else if (text[i] == '\n')
            {
                currentLine++;
                lineStart = i + 1;
            }
        }

        if (currentLine == lineOneBased && lineStart <= text.Length)
        {
            return text.Substring(lineStart);
        }

        return string.Empty;
    }
}

/// <summary>
/// Reference-counted buffer &amp; pattern-creation limiter guard (<c>PatternLanguage/evaluator.cpp:1254-1363</c>),
/// detecting double-free bugs, leaked buffer handles, and runaway recursive pattern expansion.
/// </summary>
public sealed class ReferenceCountedBufferGuard
{
    private readonly Dictionary<int, int> _refCounts = new Dictionary<int, int>();
    private readonly int _maxPatternLimit;
    private readonly int _maxRecursionDepth;
    private int _createdPatterns;
    private int _currentDepth;
    private int _nextBufferId = 1;

    public ReferenceCountedBufferGuard(int maxPatternLimit = 65536, int maxRecursionDepth = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPatternLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecursionDepth);
        _maxPatternLimit = maxPatternLimit;
        _maxRecursionDepth = maxRecursionDepth;
    }

    public int CreatedPatternCount => _createdPatterns;
    public int CurrentRecursionDepth => _currentDepth;
    public int ActiveBufferCount => _refCounts.Count;

    public int AllocateBuffer()
    {
        int id = _nextBufferId++;
        _refCounts[id] = 1;
        return id;
    }

    public void AddReference(int bufferId)
    {
        if (!_refCounts.TryGetValue(bufferId, out int count) || count <= 0)
        {
            throw new InvalidOperationException($"Cannot AddReference to unallocated or already-freed buffer #{bufferId}.");
        }

        _refCounts[bufferId] = count + 1;
    }

    public int ReleaseBuffer(int bufferId)
    {
        if (!_refCounts.TryGetValue(bufferId, out int count) || count <= 0)
        {
            throw new InvalidOperationException($"Double-free detected on buffer #{bufferId} (reference count was already 0).");
        }

        int updated = count - 1;
        if (updated == 0)
        {
            _refCounts.Remove(bufferId);
        }
        else
        {
            _refCounts[bufferId] = updated;
        }

        return updated;
    }

    public void EnterPatternScope()
    {
        if (_currentDepth + 1 > _maxRecursionDepth)
        {
            throw new InvalidOperationException($"Pattern evaluation exceeded maximum recursion depth ({_maxRecursionDepth}).");
        }

        if (_createdPatterns + 1 > _maxPatternLimit)
        {
            throw new InvalidOperationException($"Pattern evaluation exceeded maximum pattern creation limit ({_maxPatternLimit}).");
        }

        _currentDepth++;
        _createdPatterns++;
    }

    public void ExitPatternScope()
    {
        if (_currentDepth <= 0)
        {
            throw new InvalidOperationException("Unbalanced ExitPatternScope call at depth 0.");
        }

        _currentDepth--;
    }
}
