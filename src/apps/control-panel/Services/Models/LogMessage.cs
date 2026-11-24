// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NEXUS.ControlPanel.GUI.Services.Models;

public sealed partial class LogMessage : ObservableObject
{
    [ObservableProperty] private bool _isRevealed;

    public DateTimeOffset Timestamp { get; init; } = TimeProvider.System.GetUtcNow();
    public string ServiceName { get; init; } = "Aspire Host";
    public string Message { get; init; } = string.Empty;
    public string Level { get; init; } = "INFO";
    public bool IsError { get; init; }

    public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss");

    // Full Raw Unmasked Text (Always used for copying to clipboard)
    public string DisplayText => $"[{FormattedTimestamp}] [{ServiceName}] [{Level}] {Message}";

    // Masked Text for GUI presentation (Memoized for high UI rendering performance)
    public string MaskedDisplayText
    {
        get
        {
            if (IsRevealed) return DisplayText;

            return field ??= $"[{FormattedTimestamp}] [{ServiceName}] [{Level}] {MaskSensitivePaths(Message)}";
        }
    }

    partial void OnIsRevealedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(MaskedDisplayText));
    }

    public static string MaskSensitivePaths(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // 1. Double backslash escaped JSON paths
        input = DoubleBackslashPathRegex().Replace(input, match =>
        {
            string fileName = match.Groups[1].Value.TrimEnd('.');
            return $@"[PATH: ...\\{fileName}]";
        });

        // 2. Single backslash Windows paths
        input = SingleBackslashPathRegex().Replace(input, match =>
        {
            string fileName = match.Groups[1].Value.TrimEnd('.');
            return $"[PATH: ...\\{fileName}]";
        });

        // 3. Forward slash paths
        input = ForwardSlashPathRegex().Replace(input, match =>
        {
            string fileName = match.Groups[1].Value;
            return $"[PATH: .../{fileName}]";
        });

        // 4. Mask User Profile directory if still present anywhere in input (single, double, or forward slash)
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            string escapedSingle = Regex.Escape(userProfile);
            string escapedDouble = Regex.Escape(userProfile.Replace(@"\", @"\\"));
            string escapedForward = Regex.Escape(userProfile.Replace(@"\", "/"));

            input = Regex.Replace(input, escapedDouble, "[USER_DIR]", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, escapedSingle, "[USER_DIR]", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, escapedForward, "[USER_DIR]", RegexOptions.IgnoreCase);
        }

        // 5. Generic fallback for any remaining Users directories
        input = WindowsUserDirRegex().Replace(input, "[USER_DIR]");
        input = UnixUserDirRegex().Replace(input, "[USER_DIR]");

        return input;
    }

    [GeneratedRegex(@"[a-zA-Z]:(?:\\\\)(?:[^\\/:*?""<>|\r\n\s]+\\\\)+([a-zA-Z0-9_\-]+(?:\.[a-zA-Z0-9_\-]+)*)")]
    private static partial Regex DoubleBackslashPathRegex();

    [GeneratedRegex(@"(?:[a-zA-Z]:\\|~\\)(?:[^\\/:*?""<>|\r\n\s]+\\)+([a-zA-Z0-9_\-]+(?:\.[a-zA-Z0-9_\-]+)*)")]
    private static partial Regex SingleBackslashPathRegex();

    [GeneratedRegex(@"(?:/[a-zA-Z0-9_.\-]+)+/([a-zA-Z0-9_\-]+(?:\.[a-zA-Z0-9_\-]+)*)")]
    private static partial Regex ForwardSlashPathRegex();

    [GeneratedRegex(@"[a-zA-Z]:(?:\\\\|\\)Users(?:\\\\|\\)[a-zA-Z0-9_.\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserDirRegex();

    [GeneratedRegex(@"/Users/[a-zA-Z0-9_.\-]+|/home/[a-zA-Z0-9_.\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnixUserDirRegex();
}
