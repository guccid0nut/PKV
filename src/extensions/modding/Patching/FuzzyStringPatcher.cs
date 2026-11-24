// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Patching;

/// <summary>
///     Resilient, whitespace-tolerant delta text patcher for HoN mod operations.
/// </summary>
public static class FuzzyStringPatcher
{
    private static readonly HashSet<string> NonXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua",
        ".cfg",
        ".str",
        ".shader",
        ".ps",
        ".vs",
        ".hlsl",
        ".glsl",
        ".txt",
        ".ini",
        ".json",
        ".csv"
    };

    public static bool IsXmlLikeTarget(string? targetFileName)
    {
        if (string.IsNullOrWhiteSpace(targetFileName)) return true;
        string ext = Path.GetExtension(targetFileName);
        return string.IsNullOrEmpty(ext) || !NonXmlExtensions.Contains(ext);
    }

    public static string StripLeadingBomForLua(string content, string? targetFileName)
    {
        if (!string.IsNullOrEmpty(content)
            && content[0] == '\uFEFF'
            && !string.IsNullOrWhiteSpace(targetFileName)
            && targetFileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            return content[1..];
        }

        return content;
    }

    public static async Task<string> ApplyPatchAsync(
        string originalContent,
        IEnumerable<PatchOperation> operations,
        Func<string, Task<string>>? sourceFileReader = null,
        string targetFileName = "")
    {
        string sanitizedOriginal = StripLeadingBomForLua(originalContent, targetFileName);
        PatchContext context = new(targetFileName, sanitizedOriginal);

        foreach (PatchOperation op in operations)
            await ApplyOperationAsync(context, op, sourceFileReader).ConfigureAwait(false);

        return StripLeadingBomForLua(context.Content, targetFileName);
    }

    public static async Task ApplyOperationAsync(
        PatchContext context,
        PatchOperation operation,
        Func<string, Task<string>>? sourceFileReader = null)
    {
        switch (operation)
        {
            case FindOperation find:
                ApplyFind(context, find);
                break;

            case FindUpOperation findUp:
                ApplyFindUp(context, findUp);
                break;

            case FindAllOperation findAll:
                context.FindAllTarget = findAll.Target;
                context.AppliedOperationsCount++;
                context.OperationLog.Add($"FindAll: '{Truncate(findAll.Target, 30)}'");
                break;

            case InsertOperation insert:
            {
                string content = insert.Content;
                if (!string.IsNullOrEmpty(insert.SourceFile) && sourceFileReader != null)
                    content = await sourceFileReader(insert.SourceFile).ConfigureAwait(false);
                content = StripLeadingBomForLua(content, context.TargetFile);
                ApplyInsert(context, content, insert.Position);
            }
                break;

            case ReplaceOperation replace:
            {
                string content = replace.Content;
                if (!string.IsNullOrEmpty(replace.SourceFile) && sourceFileReader != null)
                    content = await sourceFileReader(replace.SourceFile).ConfigureAwait(false);
                content = StripLeadingBomForLua(content, context.TargetFile);
                ApplyReplace(context, content);
            }
                break;

            case DeleteOperation:
                ApplyDelete(context);
                break;
        }
    }

    private static void ApplyFind(PatchContext context, FindOperation find)
    {
        context.FindAllTarget = null;
        string text = context.Content;
        string target = find.Target;

        if (string.IsNullOrEmpty(target))
        {
            ApplyPositionAdjustment(context, find.Position, find.Offset);
            return;
        }

        int searchStart = Math.Clamp(context.CursorEnd, 0, text.Length);
        int matchIdx = -1;
        int matchLen = target.Length;

        // 1. Exact match forward
        StringComparison comparison =
            find.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        matchIdx = text.IndexOf(target, searchStart, comparison);

        // 2. Wrap-around exact match if not found from cursor
        if (matchIdx < 0 && searchStart > 0) matchIdx = text.IndexOf(target, 0, searchStart, comparison);

        // 3. Fuzzy whitespace match forward if exact failed
        if (matchIdx < 0)
        {
            if (TryFuzzyMatchForward(text, target, searchStart, find.CaseSensitive, out int fIdx, out int fLen))
            {
                matchIdx = fIdx;
                matchLen = fLen;
            }
            else if (searchStart > 0 &&
                     TryFuzzyMatchForward(text, target, 0, find.CaseSensitive, out int wIdx, out int wLen))
            {
                matchIdx = wIdx;
                matchLen = wLen;
            }
        }

        if (matchIdx < 0)
        {
            // If target not found, record warning in log
            context.LastFindSuccessful = false;
            context.OperationLog.Add($"Find MISS: '{Truncate(target, 40)}'");
            return;
        }

        if (IsXmlLikeTarget(context.TargetFile)
            && target.TrimStart().StartsWith('<')
            && !target.TrimEnd().EndsWith('>'))
        {
            int tagEnd = text.IndexOf('>', matchIdx + matchLen);
            if (tagEnd >= 0) matchLen = tagEnd + 1 - matchIdx;
        }

        context.LastFindSuccessful = true;
        context.SetCursors(matchIdx, matchIdx + matchLen);
        ApplyPositionAdjustment(context, find.Position, find.Offset);
        context.AppliedOperationsCount++;
        context.OperationLog.Add($"Find HIT [{context.CursorStart}..{context.CursorEnd}]: '{Truncate(target, 30)}'");
    }

    private static void ApplyFindUp(PatchContext context, FindUpOperation findUp)
    {
        context.FindAllTarget = null;
        string text = context.Content;
        string target = findUp.Target;

        if (string.IsNullOrEmpty(target))
        {
            ApplyPositionAdjustment(context, findUp.Position, findUp.Offset);
            return;
        }

        int searchStart = context.CursorStart > 0 ? context.CursorStart - 1 : text.Length - 1;
        searchStart = Math.Clamp(searchStart, 0, text.Length - 1);
        int matchIdx = -1;
        int matchLen = target.Length;

        StringComparison comparison =
            findUp.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (searchStart >= 0) matchIdx = text.LastIndexOf(target, searchStart, comparison);

        if (matchIdx < 0)
            if (TryFuzzyMatchBackward(text, target, searchStart, findUp.CaseSensitive, out int fIdx, out int fLen))
            {
                matchIdx = fIdx;
                matchLen = fLen;
            }

        if (matchIdx < 0)
        {
            context.LastFindSuccessful = false;
            context.OperationLog.Add($"FindUp MISS: '{Truncate(target, 40)}'");
            return;
        }

        if (IsXmlLikeTarget(context.TargetFile)
            && target.TrimStart().StartsWith('<')
            && !target.TrimEnd().EndsWith('>'))
        {
            int tagEnd = text.IndexOf('>', matchIdx + matchLen);
            if (tagEnd >= 0) matchLen = tagEnd + 1 - matchIdx;
        }

        context.LastFindSuccessful = true;
        context.SetCursors(matchIdx, matchIdx + matchLen);
        ApplyPositionAdjustment(context, findUp.Position, findUp.Offset);
        context.AppliedOperationsCount++;
        context.OperationLog.Add($"FindUp HIT [{context.CursorStart}..{context.CursorEnd}]: '{Truncate(target, 30)}'");
    }

    private static void ApplyPositionAdjustment(PatchContext context, FindPosition position, int offset)
    {
        int len = context.Length;
        switch (position)
        {
            case FindPosition.Before:
                context.SetCursors(context.CursorStart, context.CursorStart);
                break;

            case FindPosition.After:
                context.SetCursors(context.CursorEnd, context.CursorEnd);
                break;

            case FindPosition.Begin:
            case FindPosition.Start:
            case FindPosition.Head:
                context.SetCursors(0, 0);
                break;

            case FindPosition.End:
            case FindPosition.Tail:
            case FindPosition.Eof:
                context.SetCursors(len, len);
                break;

            case FindPosition.Offset:
                context.SetCursors(context.CursorStart + offset, context.CursorEnd + offset);
                break;
        }
    }

    private static void ApplyInsert(PatchContext context, string content, InsertPosition position)
    {
        if (!context.LastFindSuccessful)
        {
            context.OperationLog.Add("Insert SKIPPED (previous Find missed)");
            return;
        }

        string text = context.Content;

        if (!string.IsNullOrEmpty(context.FindAllTarget))
        {
            string target = context.FindAllTarget;
            context.FindAllTarget = null;

            string replacement = position == InsertPosition.Before ? content + target : target + content;
            string updated = text.Replace(target, replacement, StringComparison.Ordinal);
            context.UpdateContent(updated, 0, 0);
            context.AppliedOperationsCount++;
            context.OperationLog.Add($"Insert (FindAll {position}): +{content.Length} chars");
            return;
        }

        int insertIndex = position == InsertPosition.Before ? context.CursorStart : context.CursorEnd;
        insertIndex = Math.Clamp(insertIndex, 0, text.Length);

        string newContent = text.Insert(insertIndex, content);
        int newCursorStart = insertIndex;
        int newCursorEnd = insertIndex + content.Length;

        context.UpdateContent(newContent, newCursorStart, newCursorEnd);
        context.AppliedOperationsCount++;
        context.OperationLog.Add($"Insert [{insertIndex}]: +{content.Length} chars");
    }

    private static void ApplyReplace(PatchContext context, string content)
    {
        if (!context.LastFindSuccessful)
        {
            context.OperationLog.Add("Replace SKIPPED (previous Find missed)");
            return;
        }

        string text = context.Content;

        if (!string.IsNullOrEmpty(context.FindAllTarget))
        {
            string target = context.FindAllTarget;
            context.FindAllTarget = null;

            string updated = text.Replace(target, content, StringComparison.Ordinal);
            context.UpdateContent(updated, 0, 0);
            context.AppliedOperationsCount++;
            context.OperationLog.Add($"Replace (FindAll): -> {content.Length} chars");
            return;
        }

        int start = Math.Clamp(context.CursorStart, 0, text.Length);
        int end = Math.Clamp(context.CursorEnd, start, text.Length);
        int removeLen = end - start;

        StringBuilder sb = new(text.Length - removeLen + content.Length);
        sb.Append(text, 0, start);
        sb.Append(content);
        sb.Append(text, end, text.Length - end);

        string newContent = sb.ToString();
        int newStart = start;
        int newEnd = start + content.Length;

        context.UpdateContent(newContent, newStart, newEnd);
        context.AppliedOperationsCount++;
        context.OperationLog.Add($"Replace [{start}..{end}]: {removeLen} chars -> {content.Length} chars");
    }

    private static void ApplyDelete(PatchContext context)
    {
        if (!context.LastFindSuccessful)
        {
            context.OperationLog.Add("Delete SKIPPED (previous Find missed)");
            return;
        }

        string text = context.Content;

        if (!string.IsNullOrEmpty(context.FindAllTarget))
        {
            string target = context.FindAllTarget;
            context.FindAllTarget = null;

            string updated = text.Replace(target, string.Empty, StringComparison.Ordinal);
            context.UpdateContent(updated, 0, 0);
            context.AppliedOperationsCount++;
            context.OperationLog.Add($"Delete (FindAll): removed all '{Truncate(target, 20)}'");
            return;
        }

        int start = Math.Clamp(context.CursorStart, 0, text.Length);
        int end = Math.Clamp(context.CursorEnd, start, text.Length);
        int removeLen = end - start;

        if (removeLen > 0)
        {
            string newContent = text.Remove(start, removeLen);
            context.UpdateContent(newContent, start, start);
            context.AppliedOperationsCount++;
            context.OperationLog.Add($"Delete [{start}..{end}]: removed {removeLen} chars");
        }
    }

    /// <summary>
    ///     Performs whitespace-normalized fuzzy search forward.
    /// </summary>
    public static bool TryFuzzyMatchForward(
        string source,
        string target,
        int startIndex,
        bool caseSensitive,
        out int matchIndex,
        out int matchLength)
    {
        matchIndex = -1;
        matchLength = 0;

        Regex? regexPattern = BuildFuzzyRegexPattern(target, caseSensitive);
        if (regexPattern == null) return false;

        RegexOptions options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        Match match = regexPattern.Match(source, startIndex);
        if (match.Success)
        {
            matchIndex = match.Index;
            matchLength = match.Length;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Performs whitespace-normalized fuzzy search backward.
    /// </summary>
    public static bool TryFuzzyMatchBackward(
        string source,
        string target,
        int startIndex,
        bool caseSensitive,
        out int matchIndex,
        out int matchLength)
    {
        matchIndex = -1;
        matchLength = 0;

        Regex? regexPattern = BuildFuzzyRegexPattern(target, caseSensitive);
        if (regexPattern == null) return false;

        MatchCollection matches = regexPattern.Matches(source);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            Match m = matches[i];
            if (m.Index <= startIndex)
            {
                matchIndex = m.Index;
                matchLength = m.Length;
                return true;
            }
        }

        return false;
    }

    private static Regex? BuildFuzzyRegexPattern(string target, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        // Split tokens on whitespace runs
        string[] tokens = target.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        StringBuilder sb = new();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (i > 0) sb.Append(@"\s+");
            sb.Append(Regex.Escape(tokens[i]));
        }

        RegexOptions options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex(sb.ToString(), options | RegexOptions.CultureInvariant);
    }

    private static string Truncate(string str, int maxLen)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        string singleLine = str.Replace("\r", "").Replace("\n", " ");
        return singleLine.Length <= maxLen ? singleLine : singleLine[..maxLen] + "...";
    }
}
