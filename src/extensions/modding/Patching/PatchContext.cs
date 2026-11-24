// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace Kongor.Modding.Patching;

/// <summary>
///     Execution state and cursor tracker for sequentially patching a file buffer.
/// </summary>
public sealed class PatchContext(string targetFile, string initialContent)
{
    public string TargetFile { get; } = targetFile;
    public string Content { get; private set; } = initialContent;

    public int Length => Content.Length;

    /// <summary>
    ///     Legacy cursor pointer num5 (match start index).
    /// </summary>
    public int CursorStart { get; set; }

    /// <summary>
    ///     Legacy cursor pointer num6 (match end index).
    /// </summary>
    public int CursorEnd { get; set; }

    /// <summary>
    ///     If findall was executed, holds the target string for subsequent global replacement/insertion/deletion.
    /// </summary>
    public string? FindAllTarget { get; set; }

    public int AppliedOperationsCount { get; set; }
    public bool LastFindSuccessful { get; set; } = true;
    public List<string> OperationLog { get; } = [];

    public void UpdateContent(string newContent, int newCursorStart, int newCursorEnd)
    {
        Content = newContent;
        CursorStart = Math.Clamp(newCursorStart, 0, Content.Length);
        CursorEnd = Math.Clamp(newCursorEnd, CursorStart, Content.Length);
    }

    public void SetCursors(int start, int end)
    {
        CursorStart = Math.Clamp(start, 0, Content.Length);
        CursorEnd = Math.Clamp(end, CursorStart, Content.Length);
    }
}
