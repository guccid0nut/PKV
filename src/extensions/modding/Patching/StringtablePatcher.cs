// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Patching;

/// <summary>
///     Parser and patcher for Heroes of Newerth localized string tables (.str files).
/// </summary>
public static class StringtablePatcher
{
    public static string ApplyPatch(string strContent, IEnumerable<StringtableEntryOperation> operations)
    {
        List<StrLine> lines = ParseLines(strContent);
        Dictionary<string, StrLine> keyMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (StrLine l in lines)
            if (!l.IsCommentOrBlank && l.Key != null)
                keyMap[l.Key] = l;

        foreach (StringtableEntryOperation op in operations)
            if (op.Action == StringtableAction.Delete)
            {
                if (keyMap.TryGetValue(op.Key, out StrLine? existing))
                {
                    lines.Remove(existing);
                    keyMap.Remove(op.Key);
                }
            }
            else
            {
                if (keyMap.TryGetValue(op.Key, out StrLine? existing))
                {
                    existing.Value = op.Value;
                }
                else
                {
                    StrLine newLine = new()
                    {
                        IsCommentOrBlank = false,
                        Key = op.Key,
                        Value = op.Value,
                        Separator = "\t",
                        RawLine = $"{op.Key}\t{op.Value}"
                    };
                    lines.Add(newLine);
                    keyMap[op.Key] = newLine;
                }
            }

        return SerializeLines(lines);
    }

    private static List<StrLine> ParseLines(string content)
    {
        List<StrLine> result = [];
        if (string.IsNullOrEmpty(content)) return result;

        using StringReader reader = new(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith('#'))
            {
                result.Add(new StrLine { IsCommentOrBlank = true, RawLine = line });
                continue;
            }

            int i = 0;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            int keyEnd = i;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int valueStart = i;

            if (keyEnd > 0 && valueStart > keyEnd)
            {
                string key = line[..keyEnd];
                string separator = line[keyEnd..valueStart];
                string value = line[valueStart..];
                result.Add(new StrLine
                {
                    IsCommentOrBlank = false,
                    Key = key,
                    Value = value,
                    Separator = separator,
                    RawLine = line
                });
            }
            else
            {
                result.Add(new StrLine { IsCommentOrBlank = true, RawLine = line });
            }
        }

        return result;
    }

    private static string SerializeLines(List<StrLine> lines)
    {
        StringBuilder sb = new();
        foreach (StrLine l in lines)
            if (l.IsCommentOrBlank)
                sb.AppendLine(l.RawLine);
            else
                sb.AppendLine($"{l.Key}{l.Separator}{l.Value}");

        return sb.ToString();
    }

    private sealed class StrLine
    {
        public bool IsCommentOrBlank { get; set; }
        public string RawLine { get; set; } = string.Empty;
        public string? Key { get; set; }
        public string? Value { get; set; }
        public string Separator { get; set; } = "\t";
    }
}
