// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

namespace Kongor.Cli.Infrastructure;

/// <summary>
/// Resolves user-supplied relative input and output paths cleanly when <c>kongor</c> is invoked from
/// outside the workspace root.
/// <para>
/// - Input paths check <see cref="CliRuntimeContext.InvocationCwd"/> first, falling back to
///   <c>WorkspaceRoot</c> and <c>WorkspaceRoot/games/client</c> for workspace/VFS shorthands.
/// - Explicit output paths (<c>-o</c>/<c>--output</c>) always resolve against <c>InvocationCwd</c>.
/// - Omitted output paths default to <c>InvocationCwd</c> for explicit export commands (<c>capture</c>,
///   <c>model export</c>, <c>mixamo</c>) and to <c>WorkspaceRoot/artifacts</c> for engineering/audit commands.
/// </para>
/// </summary>
internal static class CliPathResolver
{
    public static string ResolveInputPath(
        string rawPath,
        string workspaceRoot,
        string? invocationCwdOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        string trimmed = rawPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        string callerCwd = !string.IsNullOrWhiteSpace(invocationCwdOverride)
            ? Path.GetFullPath(invocationCwdOverride)
            : CliRuntimeContext.InvocationCwd;

        // 1. Caller CWD takes top priority (e.g. '.\match.honreplay' or '.\my-mod' from Downloads)
        string callerCandidate = Path.GetFullPath(Path.Combine(callerCwd, trimmed));
        if (File.Exists(callerCandidate) || Directory.Exists(callerCandidate))
        {
            return callerCandidate;
        }

        // 2. Fallback to WorkspaceRoot (e.g. 'games/client/override/ui')
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string wsCandidate = Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));
            if (File.Exists(wsCandidate) || Directory.Exists(wsCandidate))
            {
                return wsCandidate;
            }

            // 3. Fallback to WorkspaceRoot/games/client (e.g. 'cgame_x64.dll' or 'ui/fe2')
            string clientCandidate = Path.GetFullPath(Path.Combine(workspaceRoot, "games", "client", trimmed));
            if (File.Exists(clientCandidate) || Directory.Exists(clientCandidate))
            {
                return clientCandidate;
            }
        }

        // Preserve caller-relative full path so 'not found' diagnostics match what the user typed
        return callerCandidate;
    }

    public static string ResolveOutputPath(
        string? rawPath,
        string workspaceRoot,
        string defaultRelativePath,
        bool isExplicitExportCommand,
        string? invocationCwdOverride = null)
    {
        string callerCwd = !string.IsNullOrWhiteSpace(invocationCwdOverride)
            ? Path.GetFullPath(invocationCwdOverride)
            : CliRuntimeContext.InvocationCwd;

        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            string trimmed = rawPath.Trim();
            return Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(callerCwd, trimmed));
        }

        return isExplicitExportCommand
            ? Path.GetFullPath(Path.Combine(callerCwd, defaultRelativePath))
            : Path.GetFullPath(Path.Combine(workspaceRoot, "artifacts", defaultRelativePath));
    }

    /// <summary>
    /// Normalizes path-like arguments inside raw-forwarded argument lists (e.g. <c>kongor replay highlight .\match.honreplay -o .\clip.mp4</c>)
    /// relative to <see cref="CliRuntimeContext.InvocationCwd"/> before child processes switch <c>WorkingDirectory</c>.
    /// </summary>
    public static string[] NormalizeForwardedPathArgs(
        string[] rawArgs,
        string workspaceRoot,
        string? invocationCwdOverride = null)
    {
        if (rawArgs.Length == 0)
        {
            return rawArgs;
        }

        string callerCwd = !string.IsNullOrWhiteSpace(invocationCwdOverride)
            ? Path.GetFullPath(invocationCwdOverride)
            : CliRuntimeContext.InvocationCwd;

        string[] normalized = new string[rawArgs.Length];
        for (int i = 0; i < rawArgs.Length; i++)
        {
            string arg = rawArgs[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                normalized[i] = arg;
                continue;
            }

            // Explicit output flag value (-o / --output / --out / --out-dir): resolve relative to caller CWD
            if (i > 0 && IsOutputFlag(rawArgs[i - 1]) && !arg.StartsWith('-'))
            {
                normalized[i] = Path.IsPathRooted(arg)
                    ? Path.GetFullPath(arg)
                    : Path.GetFullPath(Path.Combine(callerCwd, arg));
                continue;
            }

            // Non-flag token: if it exists relative to caller CWD (e.g. .\file.honreplay, .\scene.blend), promote to full path
            if (!arg.StartsWith('-') && !Path.IsPathRooted(arg) && !arg.Contains("://", StringComparison.Ordinal))
            {
                try
                {
                    string callerCandidate = Path.GetFullPath(Path.Combine(callerCwd, arg));
                    if (File.Exists(callerCandidate) || Directory.Exists(callerCandidate))
                    {
                        normalized[i] = callerCandidate;
                        continue;
                    }

                    string wsCandidate = Path.GetFullPath(Path.Combine(workspaceRoot, arg));
                    if (File.Exists(wsCandidate) || Directory.Exists(wsCandidate))
                    {
                        normalized[i] = wsCandidate;
                        continue;
                    }
                }
                catch
                {
                    // Not a valid path syntax (e.g. hero query string); pass through verbatim
                }
            }

            normalized[i] = arg;
        }

        return normalized;
    }

    private static bool IsOutputFlag(string token)
        => token.Equals("-o", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("--output", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("--out", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("--out-dir", StringComparison.OrdinalIgnoreCase);
}
