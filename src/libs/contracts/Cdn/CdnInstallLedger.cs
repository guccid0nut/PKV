// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     Maintains a record of all files installed by emergency CDN overlay updates.
///     Enables safe rollbacks and clean deletions without touching base game archives
///     or user configuration files.
/// </summary>
public sealed class CdnInstallLedger
{
    /// <summary>
    ///     The manifest or bundle version string associated with this installation ledger.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    ///     Monotonic update sequence counter seen for replay and downgrade prevention.
    /// </summary>
    public long MaxSequenceSeen { get; set; } = 0;

    /// <summary>
    ///     ISO 8601 UTC timestamp of when this ledger was last updated.
    /// </summary>
    public string UpdatedAtUtc { get; set; } = string.Empty;

    /// <summary>
    ///     Relative forward-slash paths mapped to lowercase SHA-256 hashes of files installed by CDN updates.
    /// </summary>
    public Dictionary<string, string> InstalledFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Relative forward-slash paths that existed on disk prior to CDN installation and must not be deleted on rollback.
    /// </summary>
    public HashSet<string> PreExistingPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Loads an existing installation ledger from the specified path, or returns a new empty ledger if not found or corrupted.
    /// </summary>
    /// <param name="path">The filesystem path to the ledger JSON file.</param>
    /// <returns>The deserialized or default empty ledger.</returns>
    public static CdnInstallLedger LoadOrEmpty(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new CdnInstallLedger();
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            CdnInstallLedger? ledger = JsonSerializer.Deserialize(json, CdnInstallLedgerJsonContext.Default.CdnInstallLedger);
            if (ledger is null)
            {
                return new CdnInstallLedger();
            }

            // Ensure case-insensitive dictionary for path lookups
            if (ledger.InstalledFiles is not null && ledger.InstalledFiles.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                ledger.InstalledFiles = new Dictionary<string, string>(ledger.InstalledFiles, StringComparer.OrdinalIgnoreCase);
            }
            ledger.InstalledFiles ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (ledger.PreExistingPaths is not null && ledger.PreExistingPaths.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                ledger.PreExistingPaths = new HashSet<string>(ledger.PreExistingPaths, StringComparer.OrdinalIgnoreCase);
            }
            ledger.PreExistingPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return ledger;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CdnInstallLedger] Failed to deserialize ledger at '{path}', returning empty ledger: {ex.Message}");
            return new CdnInstallLedger();
        }
    }

    /// <summary>
    ///     Saves the updated ledger atomically to disk if and only if contents have changed compared to the on-disk state.
    ///     If the contents match the existing file, performs zero disk writes.
    /// </summary>
    /// <param name="path">The destination ledger file path.</param>
    /// <param name="updated">The updated ledger instance to persist.</param>
    public static void SaveIfChangedAtomically(string path, CdnInstallLedger updated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(updated);

        if (File.Exists(path))
        {
            CdnInstallLedger existing = LoadOrEmpty(path);
            if (AreEquivalent(existing, updated))
            {
                // Zero-byte write optimization: nothing changed, leave disk untouched
                return;
            }
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(dir ?? ".", $"{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");
        try
        {
            string json = JsonSerializer.Serialize(updated, CdnInstallLedgerJsonContext.Default.CdnInstallLedger);
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[CdnInstallLedger] Temp ledger cleanup skipped for '{tempPath}': {ex.Message}");
                }
            }
        }
    }

    private static bool AreEquivalent(CdnInstallLedger a, CdnInstallLedger b)
    {
        if (!string.Equals(a.Version, b.Version, StringComparison.Ordinal))
            return false;

        if (a.MaxSequenceSeen != b.MaxSequenceSeen)
            return false;

        var aPre = a.PreExistingPaths ?? [];
        var bPre = b.PreExistingPaths ?? [];
        if (!aPre.SetEquals(bPre))
            return false;

        var aFiles = a.InstalledFiles ?? [];
        var bFiles = b.InstalledFiles ?? [];

        if (aFiles.Count != bFiles.Count)
            return false;

        foreach (var (key, value) in bFiles)
        {
            if (!aFiles.TryGetValue(key, out string? existingValue))
                return false;

            if (!string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

/// <summary>
///     Source-generated JSON serialization context for <see cref="CdnInstallLedger" />.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(CdnInstallLedger))]
public sealed partial class CdnInstallLedgerJsonContext : JsonSerializerContext;
