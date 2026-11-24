// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Core.Configuration;

namespace Kongor.Cli.Infrastructure;

/// <summary>
/// Process-wide execution context captured once at <c>Program.Main</c> startup.
/// Preserves the caller's original shell working directory (<see cref="InvocationCwd"/>),
/// parses global <c>--root</c>/<c>--workspace</c> overrides, resolves the active workspace via
/// <see cref="KongorWorkspaceResolver"/>, and emits a single stderr context notice when running out-of-tree.
/// </summary>
internal static class CliRuntimeContext
{
    private static readonly Lock SyncRoot = new();
    private static string _invocationCwd = Directory.GetCurrentDirectory();
    private static string? _explicitRootOrWorkspace;
    private static string? _userHomeOverride;
    private static bool _suppressContextBanner;
    private static bool _bannerEmitted;
    private static KongorWorkspaceResult? _cachedWorkspace;

    public static string InvocationCwd
    {
        get { lock (SyncRoot) { return _invocationCwd; } }
    }

    public static string? ExplicitRootOrWorkspace
    {
        get { lock (SyncRoot) { return _explicitRootOrWorkspace; } }
    }

    public static KongorWorkspaceResult? LastResolvedWorkspace
    {
        get { lock (SyncRoot) { return _cachedWorkspace; } }
    }

    /// <summary>
    /// Initializes runtime context from raw CLI arguments, returning the remaining arguments with global
    /// <c>--root</c> and <c>--workspace</c> options extracted.
    /// </summary>
    public static string[] Initialize(
        string[] rawArgs,
        string? invocationCwdOverride = null,
        string? userHomeOverride = null)
    {
        string capturedCwd = !string.IsNullOrWhiteSpace(invocationCwdOverride)
            ? Path.GetFullPath(invocationCwdOverride)
            : (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KongorWorkspaceResolver.EnvCallerCwd))
                ? Path.GetFullPath(Environment.GetEnvironmentVariable(KongorWorkspaceResolver.EnvCallerCwd)!)
                : Directory.GetCurrentDirectory());

        (string[] cleanedArgs, string? explicitRoot) = ExtractGlobalOptions(rawArgs);

        bool suppressBanner = ShouldSuppressBanner(cleanedArgs);

        lock (SyncRoot)
        {
            _invocationCwd = capturedCwd;
            _explicitRootOrWorkspace = explicitRoot;
            _userHomeOverride = userHomeOverride;
            _suppressContextBanner = suppressBanner;
            _bannerEmitted = false;
            _cachedWorkspace = null;
        }

        return cleanedArgs;
    }

    /// <summary>
    /// Resets cached resolution state (intended for unit tests and after <c>kongor workspace use</c>).
    /// </summary>
    public static void InvalidateWorkspaceCache()
    {
        lock (SyncRoot)
        {
            _cachedWorkspace = null;
        }
    }

    /// <summary>
    /// Extracts global <c>--root</c> and <c>--workspace</c> flags (supporting both <c>--root &lt;val&gt;</c>
    /// and <c>--root=&lt;val&gt;</c>) appearing anywhere before a <c>--</c> pass-through separator.
    /// </summary>
    public static (string[] RemainingArgs, string? ExplicitRootOrWorkspace) ExtractGlobalOptions(string[] args)
    {
        if (args.Length == 0)
        {
            return (args, null);
        }

        List<string> remaining = new(args.Length);
        string? explicitRoot = null;
        bool passthrough = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (passthrough)
            {
                remaining.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                passthrough = true;
                remaining.Add(arg);
                continue;
            }

            if (arg.Equals("--root", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--workspace", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    explicitRoot = args[++i];
                    continue;
                }
            }
            else if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            {
                explicitRoot = arg["--root=".Length..];
                continue;
            }
            else if (arg.StartsWith("--workspace=", StringComparison.OrdinalIgnoreCase))
            {
                explicitRoot = arg["--workspace=".Length..];
                continue;
            }

            remaining.Add(arg);
        }

        return ([.. remaining], explicitRoot);
    }

    /// <summary>
    /// Determines whether raw CLI arguments should be routed to Kongor AI (<c>ask</c>).
    /// Only triggers on explicit leading tokens (<c>ask</c>, <c>ai</c>, <c>q</c>, <c>query</c>, <c>?</c>, <c>-?</c>,
    /// or a first token prefixed with <c>?</c>) so URLs and filters containing <c>?</c> are never hijacked.
    /// </summary>
    public static bool TryExtractAskQuestion(string[] args, out string[] questionArgs)
    {
        questionArgs = [];
        if (args.Length == 0)
        {
            return false;
        }

        string first = args[0];
        if (first.Equals("ask", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("query", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("?", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("-?", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length <= 1)
            {
                return false;
            }

            // Let 'kongor ask --help' flow to Spectre help output
            if (args.Length == 2 && (args[1].Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                                     args[1].Equals("-h", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            questionArgs = [.. args.Skip(1)];
            return questionArgs.Length > 0;
        }

        if (first.StartsWith('?') && first.Length > 1)
        {
            questionArgs = [first.TrimStart('?'), .. args.Skip(1)];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the active Project Kongor workspace/bundle using <see cref="KongorWorkspaceResolver"/>.
    /// Returns false without throwing if no valid workspace can be resolved.
    /// </summary>
    public static bool TryResolveWorkspace(out KongorWorkspaceResult? result)
    {
        WorkspaceResolveOptions options;
        lock (SyncRoot)
        {
            if (_cachedWorkspace != null)
            {
                result = _cachedWorkspace;
                EmitOutOfTreeBannerIfNeededUnsafe(result);
                return true;
            }

            options = BuildResolveOptionsUnsafe();
        }

        if (KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? resolved, options))
        {
            lock (SyncRoot)
            {
                _cachedWorkspace = resolved;
                EmitOutOfTreeBannerIfNeededUnsafe(resolved);
            }
            result = resolved;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Resolves any valid Project Kongor installation or throws <see cref="KongorWorkspaceNotFoundException"/>.
    /// </summary>
    public static KongorWorkspaceResult RequireAnyWorkspace()
    {
        WorkspaceResolveOptions options;
        lock (SyncRoot)
        {
            if (_cachedWorkspace != null)
            {
                EmitOutOfTreeBannerIfNeededUnsafe(_cachedWorkspace);
                return _cachedWorkspace;
            }

            options = BuildResolveOptionsUnsafe();
        }

        KongorWorkspaceResult resolved = KongorWorkspaceResolver.RequireAnyWorkspace(options);
        lock (SyncRoot)
        {
            _cachedWorkspace = resolved;
            EmitOutOfTreeBannerIfNeededUnsafe(resolved);
        }
        return resolved;
    }

    /// <summary>
    /// Resolves a verified Project Kongor developer source checkout or throws <see cref="KongorWorkspaceNotFoundException"/>.
    /// </summary>
    public static KongorWorkspaceResult RequireSourceWorkspace()
    {
        WorkspaceResolveOptions options;
        lock (SyncRoot)
        {
            if (_cachedWorkspace is { IsSourceWorkspace: true })
            {
                EmitOutOfTreeBannerIfNeededUnsafe(_cachedWorkspace);
                return _cachedWorkspace;
            }

            options = BuildResolveOptionsUnsafe();
        }

        KongorWorkspaceResult resolved = KongorWorkspaceResolver.RequireSourceWorkspace(options);
        lock (SyncRoot)
        {
            _cachedWorkspace = resolved;
            EmitOutOfTreeBannerIfNeededUnsafe(resolved);
        }
        return resolved;
    }

    /// <summary>
    /// Computes the closest matching command name (Damerau-Levenshtein distance &lt;= 2) for typo recovery.
    /// </summary>
    public static string? SuggestClosestCommand(string? unknownCommand, IEnumerable<string> knownCommands)
    {
        if (string.IsNullOrWhiteSpace(unknownCommand) || unknownCommand.StartsWith('-'))
        {
            return null;
        }

        string needle = unknownCommand.Trim().ToLowerInvariant();
        string? best = null;
        int bestDist = int.MaxValue;
        int maxAllowed = needle.Length <= 3 ? 1 : 2;

        foreach (string candidate in knownCommands)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string lower = candidate.ToLowerInvariant();
            if (lower == needle)
            {
                return candidate;
            }

            int dist = ComputeDamerauLevenshteinDistance(needle, lower);
            if (dist <= maxAllowed && dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }

    private static WorkspaceResolveOptions BuildResolveOptionsUnsafe()
    {
        return new WorkspaceResolveOptions
        {
            ExplicitRootOrWorkspace = _explicitRootOrWorkspace,
            InvocationCwd = _invocationCwd,
            UserHomeOverride = _userHomeOverride
        };
    }

    private static void EmitOutOfTreeBannerIfNeededUnsafe(KongorWorkspaceResult resolved)
    {
        if (_bannerEmitted || _suppressContextBanner || !resolved.IsOutOfTree)
        {
            return;
        }

        _bannerEmitted = true;
        string kindLabel = resolved.IsSourceWorkspace ? "source" : "bundle";
        string aliasSuffix = string.IsNullOrWhiteSpace(resolved.RegisteredAlias)
            ? string.Empty
            : $" '{resolved.RegisteredAlias}'";
        Console.Error.WriteLine(
            $"[kongor] Workspace{aliasSuffix}: {resolved.Root} ({kindLabel}, via {resolved.Source})");
    }

    private static bool ShouldSuppressBanner(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string cmd = args[0].ToLowerInvariant();
        if (cmd is "mcp" or "mcp-server" or "mcp_server" or "where" or "workspace" or "ws" or "completion" or "version" or "-v" or "--version" or "-h" or "--help")
        {
            return true;
        }

        return args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    }

    private static int ComputeDamerauLevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                int val = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                {
                    val = Math.Min(val, d[i - 2, j - 2] + 1);
                }

                d[i, j] = val;
            }
        }

        return d[n, m];
    }
}
