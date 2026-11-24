// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Kongor.Core.Configuration;

/// <summary>
/// Tier that resolved the active Project Kongor workspace or bundled installation.
/// </summary>
public enum WorkspaceResolutionSource
{
    CliFlag,
    EnvKongorRoot,
    EnvBundleRoot,
    EnvRepoRoot,
    CwdAncestor,
    ExeAncestor,
    UserConfigActive,
    UserConfigFallback
}

/// <summary>
/// Immutable descriptor of a resolved Project Kongor installation or developer checkout.
/// </summary>
public sealed record KongorWorkspaceResult
{
    public required string Root { get; init; }
    public required KongorWorkspaceKind Kind { get; init; }
    public required WorkspaceResolutionSource Source { get; init; }
    public required string InvocationCwd { get; init; }
    public BundledLayout? BundledLayout { get; init; }
    public string? RegisteredAlias { get; init; }

    public bool IsSourceWorkspace => Kind == KongorWorkspaceKind.SourceWorkspace;
    public bool IsBundledInstall => Kind == KongorWorkspaceKind.BundledInstall;

    /// <summary>
    /// True when the caller invoked the CLI from outside the resolved workspace tree.
    /// </summary>
    public bool IsOutOfTree => !KongorWorkspaceResolver.IsPathInsideDirectory(InvocationCwd, Root);
}

/// <summary>
/// Explicit overrides and test seams for <see cref="KongorWorkspaceResolver"/>.
/// </summary>
public sealed record WorkspaceResolveOptions
{
    /// <summary>
    /// Explicit CLI override (<c>--root</c> or <c>--workspace</c>): accepts either a directory path or registered workspace alias.
    /// </summary>
    public string? ExplicitRootOrWorkspace { get; init; }

    /// <summary>
    /// Caller's current working directory (defaults to <see cref="Directory.GetCurrentDirectory"/>).
    /// </summary>
    public string? InvocationCwd { get; init; }

    /// <summary>
    /// Executable base directory to probe in Tier 4 (defaults to <see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// Optional user home directory override for deterministic isolated tests.
    /// </summary>
    public string? UserHomeOverride { get; init; }

    /// <summary>
    /// Optional environment variable lookup seam for deterministic isolated tests.
    /// </summary>
    public Func<string, string?>? GetEnvironmentVariable { get; init; }

    /// <summary>
    /// Optional diagnostic logger (MUST NOT write to <see cref="Console.Out"/>).
    /// </summary>
    public Action<string>? DiagnosticLogger { get; init; }

    /// <summary>
    /// Optional RID override forwarded to <see cref="BundledLayoutResolver"/>.
    /// </summary>
    public string? RidOverride { get; init; }

    /// <summary>
    /// Optional Windows OS flag override forwarded to <see cref="BundledLayoutResolver"/>.
    /// </summary>
    public bool? IsWindowsOverride { get; init; }
}

/// <summary>
/// Actionable exception thrown when no valid Project Kongor workspace or bundled installation can be resolved.
/// Always specifies what failed, every tier checked, and the exact remediation commands.
/// </summary>
public sealed class KongorWorkspaceNotFoundException : InvalidOperationException
{
    public IReadOnlyList<string> ProbedLocations { get; }
    public bool RequiredSourceWorkspace { get; }

    public KongorWorkspaceNotFoundException(
        string message,
        IReadOnlyList<string> probedLocations,
        bool requiredSourceWorkspace = false)
        : base(message)
    {
        ProbedLocations = probedLocations;
        RequiredSourceWorkspace = requiredSourceWorkspace;
    }
}

/// <summary>
/// Single canonical Deep Module for discovering the active Project Kongor workspace or standalone bundle
/// from any working directory.
/// <para>
/// Precedence order:
/// 1) Explicit CLI flag (<c>--root</c> / <c>--workspace</c>)
/// 2) Environment variables (<c>KONGOR_ROOT</c> &gt; <c>KONGOR_BUNDLE_ROOT</c> &gt; <c>KONGOR_REPO_ROOT</c>)
/// 3) <c>InvocationCwd</c> ancestor walk using strict Kongor-only sentinels (never bare <c>.git</c>)
/// 4) <c>AppContext.BaseDirectory</c> ancestor walk (excluding ephemeral shadow/slot/tool-store dirs)
/// 5) Saved workspaces in <c>~/.kongor/config.json</c>
/// 6) Fail-fast <see cref="KongorWorkspaceNotFoundException"/> (never silently returns unverified CWD).
/// </para>
/// Never writes to <see cref="Console.Out"/> so MCP stdio JSON-RPC framing stays pure.
/// </summary>
public static class KongorWorkspaceResolver
{
    public const string EnvKongorRoot = "KONGOR_ROOT";
    public const string EnvBundleRoot = "KONGOR_BUNDLE_ROOT";
    public const string EnvRepoRoot = "KONGOR_REPO_ROOT";
    public const string EnvCallerCwd = "KONGOR_CALLER_CWD";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly string[] EphemeralBaseDirMarkers =
    [
        "kongor_mcp_runtime",
        "kongor_app_runtime",
        "runtime-cache",
        ".dotnet\\tools\\.store",
        ".dotnet/tools/.store",
        ".kongor\\cli",
        ".kongor/cli",
        ".kongor\\bin",
        ".kongor/bin"
    ];

    /// <summary>
    /// Attempts to resolve a valid Project Kongor workspace or standalone bundle using the 6-tier precedence chain.
    /// </summary>
    public static bool TryResolve(
        [MaybeNullWhen(false)] out KongorWorkspaceResult result,
        WorkspaceResolveOptions? options = null)
    {
        return TryResolveInternal(options ?? new WorkspaceResolveOptions(), out result, out _);
    }

    /// <summary>
    /// Resolves any valid Project Kongor installation (source checkout or standalone bundle),
    /// or throws an actionable <see cref="KongorWorkspaceNotFoundException"/>.
    /// </summary>
    public static KongorWorkspaceResult RequireAnyWorkspace(WorkspaceResolveOptions? options = null)
    {
        WorkspaceResolveOptions effective = options ?? new WorkspaceResolveOptions();
        if (TryResolveInternal(effective, out KongorWorkspaceResult? resolved, out List<string> probes))
        {
            return resolved;
        }

        string configPath = KongorUserConfigStore.ResolveConfigPath(effective.UserHomeOverride);
        string msg =
            "Could not locate a Project Kongor workspace or standalone installation.\n" +
            $"Checked:\n  - {string.Join("\n  - ", probes)}\n" +
            "Fix:\n" +
            "  1. Register your Project Kongor folder once: kongor workspace use \"<path-to-ProjectKongor>\"\n" +
            "  2. Or pass an explicit root for one command: kongor --root \"<path-to-ProjectKongor>\" <command>\n" +
            $"  3. Or inspect saved workspaces at: {configPath}";
        throw new KongorWorkspaceNotFoundException(msg, probes, requiredSourceWorkspace: false);
    }

    /// <summary>
    /// Resolves a verified Project Kongor developer source checkout (containing <c>ProjectKongor.slnx</c>),
    /// or throws an actionable <see cref="KongorWorkspaceNotFoundException"/>.
    /// </summary>
    public static KongorWorkspaceResult RequireSourceWorkspace(WorkspaceResolveOptions? options = null)
    {
        WorkspaceResolveOptions effective = options ?? new WorkspaceResolveOptions();
        KongorWorkspaceResult resolved = RequireAnyWorkspace(effective);
        if (resolved.IsSourceWorkspace)
        {
            return resolved;
        }

        string msg =
            $"This command requires a Project Kongor developer source checkout (ProjectKongor.slnx), " +
            $"but resolved '{resolved.Root}' is a standalone bundled installation (via {resolved.Source}).\n" +
            "Fix:\n" +
            "  1. Switch to a source checkout: kongor workspace use \"<path-to-source-checkout>\"\n" +
            "  2. Or pass --root \"<path-to-source-checkout>\" for this command.";
        throw new KongorWorkspaceNotFoundException(msg, [resolved.Root], requiredSourceWorkspace: true);
    }

    /// <summary>
    /// Checks whether a specific directory is itself a valid Project Kongor root (non-recursive).
    /// Strictly checks Kongor-specific sentinels — never accepts bare <c>.git</c> or bare <c>Directory.Build.props</c>.
    /// </summary>
    public static bool TryIdentifyRoot(
        string? directoryPath,
        out KongorWorkspaceKind kind,
        out BundledLayout? bundledLayout,
        string? ridOverride = null,
        bool? isWindowsOverride = null,
        Action<string>? diagnosticLogger = null)
    {
        kind = default;
        bundledLayout = null;

        string? normalized = KongorUserConfigStore.TryNormalizePath(directoryPath);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            return false;
        }

        // Source checkout sentinel: ProjectKongor.slnx OR (Directory.Build.props + src/tools/kongor-cli/Kongor.Cli.csproj)
        if (File.Exists(Path.Combine(normalized, "ProjectKongor.slnx")) ||
            (File.Exists(Path.Combine(normalized, "Directory.Build.props")) &&
             File.Exists(Path.Combine(normalized, "src", "tools", "kongor-cli", "Kongor.Cli.csproj"))))
        {
            kind = KongorWorkspaceKind.SourceWorkspace;
            return true;
        }

        // Standalone bundled installation sentinel: playtest_policy.json + games/client/
        if (File.Exists(Path.Combine(normalized, BundledLayoutResolver.PolicyFileName)) &&
            Directory.Exists(Path.Combine(normalized, BundledLayoutResolver.GamesFolderName, BundledLayoutResolver.ClientFolderName)))
        {
            string rid = !string.IsNullOrWhiteSpace(ridOverride)
                ? ridOverride
                : BundledLayout.DetectCurrentRid();
            bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
            bundledLayout = new BundledLayout(normalized, rid, isWindows);
            kind = KongorWorkspaceKind.BundledInstall;
            diagnosticLogger?.Invoke($"[KongorWorkspace] Identified BundledInstall at '{normalized}' (rid={rid}).");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="candidatePath"/> is equal to or nested beneath <paramref name="directoryPath"/>.
    /// </summary>
    public static bool IsPathInsideDirectory(string? candidatePath, string? directoryPath)
    {
        string? normCandidate = KongorUserConfigStore.TryNormalizePath(candidatePath);
        string? normDir = KongorUserConfigStore.TryNormalizePath(directoryPath);
        if (string.IsNullOrWhiteSpace(normCandidate) || string.IsNullOrWhiteSpace(normDir))
        {
            return false;
        }

        if (PathComparer.Equals(normCandidate, normDir))
        {
            return true;
        }

        string prefix = normDir + Path.DirectorySeparatorChar;
        return normCandidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects whether <c>KONGOR_ROOT</c> is persisted in User or Machine environment variables,
    /// which would override CWD worktree auto-switching. Intended for <c>kongor doctor</c>.
    /// </summary>
    public static string? GetPermanentRootEnvWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? userVal = Environment.GetEnvironmentVariable(EnvKongorRoot, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(userVal))
        {
            return $"User environment variable {EnvKongorRoot}='{userVal}' is permanently set and overrides CWD worktree auto-switching. " +
                   $"Fix: Remove {EnvKongorRoot} from User env vars and use 'kongor workspace use <path>' instead.";
        }

        string? machineVal = Environment.GetEnvironmentVariable(EnvKongorRoot, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(machineVal))
        {
            return $"Machine environment variable {EnvKongorRoot}='{machineVal}' is permanently set and overrides CWD worktree auto-switching.";
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="directory"/> points inside a temporary shadow-copy, CLI slot, or dotnet tool store.
    /// </summary>
    public static bool IsEphemeralRuntimeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }

        foreach (string marker in EphemeralBaseDirMarkers)
        {
            if (directory.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveInternal(
        WorkspaceResolveOptions options,
        [MaybeNullWhen(false)] out KongorWorkspaceResult result,
        out List<string> probes)
    {
        result = null;
        probes = [];

        Func<string, string?> getEnv = options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        Action<string>? log = options.DiagnosticLogger;
        string invocationCwd = KongorUserConfigStore.TryNormalizePath(options.InvocationCwd)
            ?? KongorUserConfigStore.NormalizePath(Directory.GetCurrentDirectory());
        KongorUserConfig? cachedConfig = null;

        KongorUserConfig GetConfig() => cachedConfig ??= KongorUserConfigStore.Load(options.UserHomeOverride, log);

        // Tier 1: Explicit CLI flag (--root / --workspace)
        if (!string.IsNullOrWhiteSpace(options.ExplicitRootOrWorkspace))
        {
            string rawExplicit = options.ExplicitRootOrWorkspace.Trim();
            KongorWorkspaceEntry? aliasMatch = GetConfig().Workspaces.FirstOrDefault(w =>
                string.Equals(w.Name, rawExplicit, StringComparison.OrdinalIgnoreCase));
            string candidate = aliasMatch?.Path ?? rawExplicit;
            if (!Path.IsPathRooted(candidate))
            {
                candidate = Path.Combine(invocationCwd, candidate);
            }

            if (TryWalkUpForRoot(candidate, out string? foundRoot, out KongorWorkspaceKind kind, out BundledLayout? layout, options.RidOverride, options.IsWindowsOverride, log))
            {
                result = BuildResult(foundRoot, kind, WorkspaceResolutionSource.CliFlag, invocationCwd, layout, aliasMatch?.Name, log);
                return true;
            }

            probes.Add($"CLI flag (--root/--workspace): '{rawExplicit}' -> '{candidate}' (not a valid Kongor workspace/bundle)");
            return false;
        }

        // Tier 2: Environment variables (KONGOR_ROOT > KONGOR_BUNDLE_ROOT > KONGOR_REPO_ROOT)
        (string VarName, WorkspaceResolutionSource Source)[] envPriority =
        [
            (EnvKongorRoot, WorkspaceResolutionSource.EnvKongorRoot),
            (EnvBundleRoot, WorkspaceResolutionSource.EnvBundleRoot),
            (EnvRepoRoot, WorkspaceResolutionSource.EnvRepoRoot)
        ];

        foreach ((string varName, WorkspaceResolutionSource source) in envPriority)
        {
            string? envVal = getEnv(varName);
            if (string.IsNullOrWhiteSpace(envVal))
            {
                continue;
            }

            if (TryWalkUpForRoot(envVal, out string? foundRoot, out KongorWorkspaceKind kind, out BundledLayout? layout, options.RidOverride, options.IsWindowsOverride, log))
            {
                result = BuildResult(foundRoot, kind, source, invocationCwd, layout, registeredAlias: null, log);
                return true;
            }

            probes.Add($"Env {varName}='{envVal}' (not a valid Kongor workspace/bundle)");
        }

        // Tier 3: InvocationCwd ancestor walk (strict Kongor sentinels only — enables worktree auto-switching)
        if (TryWalkUpForRoot(invocationCwd, out string? cwdRoot, out KongorWorkspaceKind cwdKind, out BundledLayout? cwdLayout, options.RidOverride, options.IsWindowsOverride, log))
        {
            string? alias = FindRegisteredAlias(GetConfig(), cwdRoot);
            result = BuildResult(cwdRoot, cwdKind, WorkspaceResolutionSource.CwdAncestor, invocationCwd, cwdLayout, alias, log);
            return true;
        }
        probes.Add($"CWD ancestor walk from '{invocationCwd}' (no ProjectKongor.slnx or playtest_policy.json)");

        // Tier 4: AppContext.BaseDirectory ancestor walk (skipping ephemeral shadow-copy / slot / dotnet tool store paths)
        string baseDir = options.BaseDirectory ?? AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir) && !IsEphemeralRuntimeDirectory(baseDir))
        {
            if (TryWalkUpForRoot(baseDir, out string? exeRoot, out KongorWorkspaceKind exeKind, out BundledLayout? exeLayout, options.RidOverride, options.IsWindowsOverride, log))
            {
                string? alias = FindRegisteredAlias(GetConfig(), exeRoot);
                result = BuildResult(exeRoot, exeKind, WorkspaceResolutionSource.ExeAncestor, invocationCwd, exeLayout, alias, log);
                return true;
            }
            probes.Add($"Executable directory ancestor walk from '{baseDir}'");
        }
        else
        {
            probes.Add($"Executable directory '{baseDir}' skipped (ephemeral runtime/slot directory)");
        }

        // Tier 5: Persisted ~/.kongor/config.json
        KongorUserConfig config = GetConfig();
        string configPath = KongorUserConfigStore.ResolveConfigPath(options.UserHomeOverride);
        if (!string.IsNullOrWhiteSpace(config.ActiveWorkspace))
        {
            KongorWorkspaceEntry? activeEntry = config.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Name, config.ActiveWorkspace, StringComparison.OrdinalIgnoreCase))
                ?? config.Workspaces.FirstOrDefault(w =>
                    PathComparer.Equals(KongorUserConfigStore.NormalizePathSafe(w.Path), KongorUserConfigStore.NormalizePathSafe(config.ActiveWorkspace)));

            string activePath = activeEntry?.Path ?? config.ActiveWorkspace;
            if (TryIdentifyRoot(activePath, out KongorWorkspaceKind cfgKind, out BundledLayout? cfgLayout, options.RidOverride, options.IsWindowsOverride, log))
            {
                result = BuildResult(
                    KongorUserConfigStore.NormalizePath(activePath),
                    cfgKind,
                    WorkspaceResolutionSource.UserConfigActive,
                    invocationCwd,
                    cfgLayout,
                    activeEntry?.Name,
                    log);
                return true;
            }

            probes.Add($"Saved active workspace '{config.ActiveWorkspace}' -> '{activePath}' in '{configPath}' (missing or invalid)");
        }

        foreach (KongorWorkspaceEntry entry in config.Workspaces)
        {
            if (TryIdentifyRoot(entry.Path, out KongorWorkspaceKind fbKind, out BundledLayout? fbLayout, options.RidOverride, options.IsWindowsOverride, log))
            {
                log?.Invoke($"[KongorWorkspace] Active workspace unavailable; falling back to registered workspace '{entry.Name}' at '{entry.Path}'.");
                result = BuildResult(
                    KongorUserConfigStore.NormalizePath(entry.Path),
                    fbKind,
                    WorkspaceResolutionSource.UserConfigFallback,
                    invocationCwd,
                    fbLayout,
                    entry.Name,
                    log);
                return true;
            }
        }

        probes.Add($"User config '{configPath}' ({config.Workspaces.Count} registered workspace(s), none valid)");
        return false;
    }

    private static bool TryWalkUpForRoot(
        string startDirectory,
        [MaybeNullWhen(false)] out string root,
        out KongorWorkspaceKind kind,
        out BundledLayout? layout,
        string? ridOverride,
        bool? isWindowsOverride,
        Action<string>? log)
    {
        root = null;
        kind = default;
        layout = null;

        string? normalized = KongorUserConfigStore.TryNormalizePath(startDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (File.Exists(normalized))
        {
            normalized = Path.GetDirectoryName(normalized);
        }

        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            return false;
        }

        DirectoryInfo? current = new(normalized);
        while (current != null)
        {
            if (TryIdentifyRoot(current.FullName, out kind, out layout, ridOverride, isWindowsOverride, log))
            {
                root = KongorUserConfigStore.NormalizePath(current.FullName);
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static string? FindRegisteredAlias(KongorUserConfig config, string resolvedRoot)
    {
        return config.Workspaces.FirstOrDefault(w =>
            PathComparer.Equals(KongorUserConfigStore.NormalizePathSafe(w.Path), resolvedRoot))?.Name;
    }

    private static KongorWorkspaceResult BuildResult(
        string root,
        KongorWorkspaceKind kind,
        WorkspaceResolutionSource source,
        string invocationCwd,
        BundledLayout? layout,
        string? registeredAlias,
        Action<string>? log)
    {
        KongorWorkspaceResult resolved = new()
        {
            Root = root,
            Kind = kind,
            Source = source,
            InvocationCwd = invocationCwd,
            BundledLayout = layout,
            RegisteredAlias = registeredAlias
        };
        log?.Invoke(
            $"[KongorWorkspace] resolved='{resolved.Root}' kind={resolved.Kind} via={resolved.Source} " +
            $"outOfTree={resolved.IsOutOfTree} cwd='{resolved.InvocationCwd}'");
        return resolved;
    }
}
