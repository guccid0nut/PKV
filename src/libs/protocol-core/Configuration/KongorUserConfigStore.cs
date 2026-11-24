// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Kongor.Core.Configuration;

/// <summary>
/// Kind of verified Project Kongor installation root.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KongorWorkspaceKind>))]
public enum KongorWorkspaceKind
{
    SourceWorkspace,
    BundledInstall
}

/// <summary>
/// Persisted workspace or standalone bundle entry in <c>~/.kongor/config.json</c>.
/// </summary>
public sealed record KongorWorkspaceEntry
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public KongorWorkspaceKind Kind { get; init; } = KongorWorkspaceKind.SourceWorkspace;
    public DateTimeOffset LastVerifiedUtc { get; init; }
}

/// <summary>
/// User-level persistent CLI &amp; ecosystem configuration stored at <c>~/.kongor/config.json</c>.
/// </summary>
public sealed class KongorUserConfig
{
    public int Version { get; set; } = 1;
    public string? ActiveWorkspace { get; set; }
    public string? ActiveCliSlot { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<KongorWorkspaceEntry> Workspaces { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KongorUserConfig))]
internal sealed partial class KongorUserConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Concurrency-safe, AOT-compatible store for <c>~/.kongor/config.json</c>.
/// Uses a cross-process lock file (<c>config.lock</c>) around read-modify-write operations and atomic
/// temp-file replacement so concurrent CLI, MCP, and WILLOWMAKER invocations never corrupt or drop updates.
/// Never writes to <see cref="Console.Out"/>.
/// </summary>
public static class KongorUserConfigStore
{
    public const string KongorUserDirName = ".kongor";
    public const string ConfigFileName = "config.json";
    public const string LockFileName = "config.lock";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string ResolveKongorUserDir(string? userHomeOverride = null)
    {
        string home = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Path.Combine(Path.GetFullPath(home), KongorUserDirName);
    }

    public static string ResolveConfigPath(string? userHomeOverride = null)
        => Path.Combine(ResolveKongorUserDir(userHomeOverride), ConfigFileName);

    public static string ResolveLockPath(string? userHomeOverride = null)
        => Path.Combine(ResolveKongorUserDir(userHomeOverride), LockFileName);

    public static string ResolveBinDir(string? userHomeOverride = null)
        => Path.Combine(ResolveKongorUserDir(userHomeOverride), "bin");

    public static string ResolveCliSlotsDir(string? userHomeOverride = null)
        => Path.Combine(ResolveKongorUserDir(userHomeOverride), "cli");

    public static string ResolveLogsDir(string? userHomeOverride = null)
        => Path.Combine(ResolveKongorUserDir(userHomeOverride), "logs");

    public static string ResolveCompletionsDir(string? userHomeOverride = null)
        => Path.Combine(ResolveKongorUserDir(userHomeOverride), "completions");

    /// <summary>
    /// Loads the current user configuration without mutating disk. Returns an empty default config if absent or malformed.
    /// </summary>
    public static KongorUserConfig Load(
        string? userHomeOverride = null,
        Action<string>? diagnosticLogger = null)
    {
        string configPath = ResolveConfigPath(userHomeOverride);
        return ReadConfigUnsafe(configPath, diagnosticLogger);
    }

    /// <summary>
    /// Atomically registers or updates a verified Kongor workspace/bundle and optionally sets it as active.
    /// </summary>
    public static KongorUserConfig RegisterOrUpdateWorkspace(
        string rootPath,
        KongorWorkspaceKind kind,
        string? alias = null,
        bool setActive = true,
        string? userHomeOverride = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnosticLogger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string normalizedPath = NormalizePath(rootPath);
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        return UpdateAtomically(
            config =>
            {
                string resolvedName = DetermineWorkspaceName(config, normalizedPath, alias);
                DateTimeOffset now = clock.GetUtcNow();

                int existingByPath = config.Workspaces.FindIndex(w =>
                    PathComparer.Equals(NormalizePathSafe(w.Path), normalizedPath));

                if (existingByPath >= 0)
                {
                    KongorWorkspaceEntry current = config.Workspaces[existingByPath];
                    string finalName = !string.IsNullOrWhiteSpace(alias) ? resolvedName : current.Name;
                    config.Workspaces[existingByPath] = current with
                    {
                        Name = finalName,
                        Path = normalizedPath,
                        Kind = kind,
                        LastVerifiedUtc = now
                    };
                    if (setActive || string.IsNullOrWhiteSpace(config.ActiveWorkspace))
                    {
                        config.ActiveWorkspace = finalName;
                    }
                }
                else
                {
                    int existingByName = config.Workspaces.FindIndex(w =>
                        string.Equals(w.Name, resolvedName, StringComparison.OrdinalIgnoreCase));
                    KongorWorkspaceEntry entry = new()
                    {
                        Name = resolvedName,
                        Path = normalizedPath,
                        Kind = kind,
                        LastVerifiedUtc = now
                    };

                    if (existingByName >= 0)
                    {
                        config.Workspaces[existingByName] = entry;
                    }
                    else
                    {
                        config.Workspaces.Add(entry);
                    }

                    if (setActive || string.IsNullOrWhiteSpace(config.ActiveWorkspace))
                    {
                        config.ActiveWorkspace = resolvedName;
                    }
                }

                config.UpdatedUtc = now;
            },
            userHomeOverride,
            clock,
            diagnosticLogger);
    }

    /// <summary>
    /// Updates the recorded active CLI slot path in <c>~/.kongor/config.json</c>.
    /// </summary>
    public static KongorUserConfig SetActiveCliSlot(
        string slotPath,
        string? userHomeOverride = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnosticLogger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotPath);
        string normalizedSlot = NormalizePath(slotPath);
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        return UpdateAtomically(
            config =>
            {
                config.ActiveCliSlot = normalizedSlot;
                config.UpdatedUtc = clock.GetUtcNow();
            },
            userHomeOverride,
            clock,
            diagnosticLogger);
    }

    /// <summary>
    /// Removes a workspace by alias or path. If the active workspace is removed, promotes the first remaining entry or clears it.
    /// </summary>
    public static (KongorUserConfig Config, bool Removed) RemoveWorkspace(
        string nameOrPath,
        string? userHomeOverride = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnosticLogger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        string trimmed = nameOrPath.Trim();
        string? normalizedCandidate = TryNormalizePath(trimmed);
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        bool removed = false;

        KongorUserConfig updated = UpdateAtomically(
            config =>
            {
                int idx = config.Workspaces.FindIndex(w =>
                    string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    (normalizedCandidate != null && PathComparer.Equals(NormalizePathSafe(w.Path), normalizedCandidate)));
                if (idx < 0)
                {
                    return;
                }

                KongorWorkspaceEntry target = config.Workspaces[idx];
                config.Workspaces.RemoveAt(idx);
                removed = true;

                if (string.Equals(config.ActiveWorkspace, target.Name, StringComparison.OrdinalIgnoreCase) ||
                    (normalizedCandidate != null && PathComparer.Equals(NormalizePathSafe(config.ActiveWorkspace), normalizedCandidate)))
                {
                    config.ActiveWorkspace = config.Workspaces.Count > 0 ? config.Workspaces[0].Name : null;
                }

                config.UpdatedUtc = clock.GetUtcNow();
            },
            userHomeOverride,
            clock,
            diagnosticLogger);

        return (updated, removed);
    }

    /// <summary>
    /// Prunes entries whose directories no longer exist or no longer contain valid Kongor workspace sentinels.
    /// </summary>
    public static (KongorUserConfig Config, IReadOnlyList<KongorWorkspaceEntry> Pruned) PruneInvalidWorkspaces(
        string? userHomeOverride = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnosticLogger = null)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        List<KongorWorkspaceEntry> pruned = [];

        KongorUserConfig updated = UpdateAtomically(
            config =>
            {
                for (int i = config.Workspaces.Count - 1; i >= 0; i--)
                {
                    KongorWorkspaceEntry entry = config.Workspaces[i];
                    if (!KongorWorkspaceResolver.TryIdentifyRoot(entry.Path, out _, out _))
                    {
                        pruned.Add(entry);
                        config.Workspaces.RemoveAt(i);
                    }
                }

                if (pruned.Count > 0)
                {
                    bool activeStillExists = !string.IsNullOrWhiteSpace(config.ActiveWorkspace) &&
                        config.Workspaces.Any(w => string.Equals(w.Name, config.ActiveWorkspace, StringComparison.OrdinalIgnoreCase));
                    if (!activeStillExists)
                    {
                        config.ActiveWorkspace = config.Workspaces.Count > 0 ? config.Workspaces[0].Name : null;
                    }
                    config.UpdatedUtc = clock.GetUtcNow();
                }
            },
            userHomeOverride,
            clock,
            diagnosticLogger);

        return (updated, pruned);
    }

    /// <summary>
    /// Executes a concurrency-safe read-modify-write cycle protected by <c>~/.kongor/config.lock</c>.
    /// </summary>
    public static KongorUserConfig UpdateAtomically(
        Action<KongorUserConfig> mutator,
        string? userHomeOverride = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnosticLogger = null,
        int lockTimeoutMs = 4000)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        string userDir = ResolveKongorUserDir(userHomeOverride);
        string configPath = ResolveConfigPath(userHomeOverride);
        string lockPath = ResolveLockPath(userHomeOverride);
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        Directory.CreateDirectory(userDir);

        using FileStream lockStream = AcquireLockFile(lockPath, clock, lockTimeoutMs, diagnosticLogger);
        KongorUserConfig config = ReadConfigUnsafe(configPath, diagnosticLogger);
        mutator(config);
        WriteConfigAtomicUnsafe(configPath, config, diagnosticLogger);
        return config;
    }

    private static FileStream AcquireLockFile(
        string lockPath,
        TimeProvider clock,
        int lockTimeoutMs,
        Action<string>? diagnosticLogger)
    {
        DateTimeOffset deadline = clock.GetUtcNow().AddMilliseconds(Math.Max(200, lockTimeoutMs));
        int attempts = 0;
        Exception? lastError = null;

        using ManualResetEventSlim waitEvent = new(false);
        while (true)
        {
            attempts++;
            try
            {
                FileStream fs = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (attempts > 1)
                {
                    diagnosticLogger?.Invoke($"[KongorConfig] Acquired '{lockPath}' after {attempts} attempt(s).");
                }
                return fs;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                throw new IOException(
                    $"Timed out waiting {lockTimeoutMs}ms for Kongor config lock '{lockPath}' after {attempts} attempts. " +
                    $"Fix: Close any stalled 'kongor' or 'WILLOWMAKER' process holding '{lockPath}' and retry.",
                    lastError);
            }

            int delayMs = Random.Shared.Next(8, 32);
            waitEvent.Wait(delayMs);
        }
    }

    private static KongorUserConfig ReadConfigUnsafe(string configPath, Action<string>? diagnosticLogger)
    {
        if (!File.Exists(configPath))
        {
            return new KongorUserConfig();
        }

        try
        {
            using FileStream fs = new(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            KongorUserConfig? parsed = JsonSerializer.Deserialize(fs, KongorUserConfigJsonContext.Default.KongorUserConfig);
            if (parsed is null)
            {
                diagnosticLogger?.Invoke($"[KongorConfig] Warning: '{configPath}' deserialized to null; using empty defaults.");
                return new KongorUserConfig();
            }

            parsed.Workspaces ??= [];
            return parsed;
        }
        catch (Exception ex)
        {
            diagnosticLogger?.Invoke($"[KongorConfig] Warning: Failed to read '{configPath}' ({ex.Message}); falling back to empty defaults.");
            return new KongorUserConfig();
        }
    }

    private static void WriteConfigAtomicUnsafe(string configPath, KongorUserConfig config, Action<string>? diagnosticLogger)
    {
        string dir = Path.GetDirectoryName(configPath) ?? ResolveKongorUserDir();
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"{ConfigFileName}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");

        try
        {
            string json = JsonSerializer.Serialize(config, KongorUserConfigJsonContext.Default.KongorUserConfig) + "\n";
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, configPath, overwrite: true);
            diagnosticLogger?.Invoke(
                $"[KongorConfig] Persisted '{configPath}' (active='{config.ActiveWorkspace ?? "<none>"}', count={config.Workspaces.Count}).");
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                diagnosticLogger?.Invoke($"[KongorConfig] Warning: Could not delete temp config '{tempPath}': {cleanupEx.Message}");
            }

            throw;
        }
    }

    private static string DetermineWorkspaceName(KongorUserConfig config, string normalizedPath, string? requestedAlias)
    {
        if (!string.IsNullOrWhiteSpace(requestedAlias))
        {
            return SanitizeAlias(requestedAlias);
        }

        string leaf = Path.GetFileName(normalizedPath);
        string baseAlias = string.IsNullOrWhiteSpace(leaf) ? "kongor" : SanitizeAlias(leaf);

        bool collidesWithOtherPath = config.Workspaces.Any(w =>
            string.Equals(w.Name, baseAlias, StringComparison.OrdinalIgnoreCase) &&
            !PathComparer.Equals(NormalizePathSafe(w.Path), normalizedPath));
        if (!collidesWithOtherPath)
        {
            return baseAlias;
        }

        string? parentLeaf = Path.GetFileName(Path.GetDirectoryName(normalizedPath) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(parentLeaf))
        {
            string scopedAlias = SanitizeAlias($"{parentLeaf}-{baseAlias}");
            bool scopedCollides = config.Workspaces.Any(w =>
                string.Equals(w.Name, scopedAlias, StringComparison.OrdinalIgnoreCase) &&
                !PathComparer.Equals(NormalizePathSafe(w.Path), normalizedPath));
            if (!scopedCollides)
            {
                return scopedAlias;
            }
        }

        int suffix = 2;
        while (config.Workspaces.Any(w =>
            string.Equals(w.Name, $"{baseAlias}-{suffix}", StringComparison.OrdinalIgnoreCase) &&
            !PathComparer.Equals(NormalizePathSafe(w.Path), normalizedPath)))
        {
            suffix++;
        }

        return $"{baseAlias}-{suffix}";
    }

    private static string SanitizeAlias(string raw)
    {
        string trimmed = raw.Trim();
        StringBuilder sb = new(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('-');
            }
        }

        string cleaned = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "kongor" : cleaned;
    }

    public static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    public static string NormalizePathSafe(string? path)
        => TryNormalizePath(path) ?? string.Empty;

    public static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return NormalizePath(path);
        }
        catch
        {
            return null;
        }
    }
}
