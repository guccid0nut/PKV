// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Handlers.CDN;

/// <summary>
///     Resolves local directory paths for CDN file serving, expanding path tokens ({ROOT}, {SERVER}, {MASTERSERVER}, {USER}, {TEMP})
///     and relative paths against the project root.
/// </summary>
public static class LocalCDNDirectoryPathResolver
{
    private static string SolutionRootDirectory { get; } = FindSolutionRootDirectory();

    /// <summary>
    ///     Resolves a local directory path for CDN file serving.
    ///     The <c>{ROOT}</c> token is replaced with the solution root directory,
    ///     the <c>{SERVER}</c> or <c>{MASTERSERVER}</c> token is replaced with the server name (e.g. "KONGOR"),
    ///     the <c>{USER}</c> token is replaced with the user's home profile directory,
    ///     and the <c>{TEMP}</c> token is replaced with the system temporary directory.
    ///     Relative paths are resolved against the solution root directory.
    /// </summary>
    /// <param name="path">The configured local directory path.</param>
    /// <param name="serverName">The master server name for {SERVER} token replacement (default: "KONGOR").</param>
    /// <returns>The fully qualified resolved local directory path.</returns>
    public static string Resolve(string path, string serverName = "KONGOR")
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        // Token values are trimmed of trailing directory separators so concatenation doesn't create doubled slashes
        string resolved = path.Trim()
            .Replace("{ROOT}", SolutionRootDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{SERVER}", serverName, StringComparison.OrdinalIgnoreCase)
            .Replace("{MASTERSERVER}", serverName, StringComparison.OrdinalIgnoreCase)
            .Replace("{USER}", Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.OrdinalIgnoreCase)
            .Replace("{TEMP}", Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);

        return Path.IsPathFullyQualified(resolved)
            ? Path.GetFullPath(resolved)
            : Path.GetFullPath(Path.Combine(SolutionRootDirectory, resolved));
    }

    private static string FindSolutionRootDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "license")) ||
                File.Exists(Path.Combine(directory.FullName, "LICENSE")) ||
                File.Exists(Path.Combine(directory.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "ProjectKongor.sln")))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }

        return Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    }
}
