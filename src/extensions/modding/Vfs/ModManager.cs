// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using Kongor.Modding.Conditions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Patching;
using Kongor.Modding.Resolution;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Vfs;

public sealed record ApplyOptions
{
    public required string GameClientDirectory { get; init; }
    public required IReadOnlyList<ModPackage> ModsToApply { get; init; }
    public InjectionMode Mode { get; init; } = InjectionMode.Vfs;
    public string? TargetArchiveName { get; init; } = "resources999.s2z";
    public bool StrictIntegrity { get; init; }
    public IVirtualFileSystem? Vfs { get; init; }
    public Action<string>? DiagnosticLogger { get; init; }
}

public sealed record ApplyResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<ModPackage> AppliedMods { get; init; } = [];
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public JournalEntry? Journal { get; init; }
}

public sealed record RollbackResult
{
    public required bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record VerificationResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<ModPackage> ValidMods { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public ConflictReport? Conflicts { get; init; }
}

public interface IModManager
{
    Task<IReadOnlyList<ModPackage>> DiscoverModsAsync(string modsDirectory, CancellationToken ct = default);
    Task<ApplyResult> ApplyModsAsync(ApplyOptions options, CancellationToken ct = default);
    Task<RollbackResult> RollbackAsync(string gameClientDirectory, CancellationToken ct = default);
    VerificationResult VerifyMods(IEnumerable<ModPackage> mods);
}

/// <summary>
///     Main orchestration manager for discovering, resolving, applying, and rolling back HoN mods over disk or VFS.
/// </summary>
public sealed class ModManager(IVirtualFileSystem? vfs = null) : IModManager
{
    public IVirtualFileSystem? Vfs { get; } = vfs;

    public async Task<IReadOnlyList<ModPackage>> DiscoverModsAsync(string modsDirectory, CancellationToken ct = default)
    {
        if (Vfs != null) return await DiscoverModsInternalAsync(Vfs, modsDirectory, ct).ConfigureAwait(false);

        if (!Directory.Exists(modsDirectory)) return [];

        List<ModPackage> results = [];

        // 1. Discover .honmod files
        string[] honmodFiles = Directory.GetFiles(modsDirectory, "*.honmod", SearchOption.TopDirectoryOnly);
        foreach (string file in honmodFiles)
            try
            {
                ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(file, ct).ConfigureAwait(false);
                results.Add(pkg);
            }
            catch
            {
                // Skip unreadable files or log
            }

        // 2. Discover unpacked mod subdirectories (containing mod.xml)
        string[] subdirs = Directory.GetDirectories(modsDirectory);
        foreach (string dir in subdirs)
            if (File.Exists(Path.Combine(dir, "mod.xml")))
                try
                {
                    ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(dir, ct).ConfigureAwait(false);
                    if (!results.Any(r =>
                            r.Manifest.Name.Equals(pkg.Manifest.Name, StringComparison.OrdinalIgnoreCase)))
                        results.Add(pkg);
                }
                catch
                {
                    // Skip
                }

        return results;
    }

    public VerificationResult VerifyMods(IEnumerable<ModPackage> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        List<string> errors = [];
        List<ModPackage> valid = [];

        foreach (ModPackage mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Manifest.Name))
            {
                errors.Add($"Mod at '{mod.FilePath}' has an empty or missing name.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(mod.Manifest.Version))
            {
                errors.Add($"Mod '{mod.Manifest.Name}' has an empty or missing version.");
                continue;
            }

            valid.Add(mod);
        }

        ConflictReport conflictReport = ConflictMatrix.Analyze(valid);
        if (conflictReport.HasConflicts)
            foreach (FileCollision collision in conflictReport.Collisions.Where(c =>
                         c.Severity == CollisionSeverity.Conflict))
                errors.Add($"Conflict on '{collision.TargetFile}': {collision.Description}");

        return new VerificationResult
        {
            Success = errors.Count == 0,
            ValidMods = valid,
            Errors = errors,
            Conflicts = conflictReport
        };
    }

    public async Task<ApplyResult> ApplyModsAsync(ApplyOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GameClientDirectory);

        IVirtualFileSystem? effectiveVfs = options.Vfs ?? Vfs;

        if (effectiveVfs != null)
        {
            if (!effectiveVfs.DirectoryExists(options.GameClientDirectory))
                effectiveVfs.CreateDirectory(options.GameClientDirectory);
        }
        else if (!Directory.Exists(options.GameClientDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Game client directory does not exist: {options.GameClientDirectory}");
        }

        List<ModPackage> enabledMods;
        if (options.ModsToApply == null || options.ModsToApply.Count == 0)
        {
            string modsDir = effectiveVfs != null
                ? $"{options.GameClientDirectory.TrimEnd('/')}/mods"
                : Path.Combine(options.GameClientDirectory, "mods");

            IReadOnlyList<ModPackage> allMods = effectiveVfs != null
                ? await DiscoverModsAsync(effectiveVfs, modsDir, ct).ConfigureAwait(false)
                : await DiscoverModsAsync(modsDir, ct).ConfigureAwait(false);

            enabledMods = [.. allMods.Where(m => m.IsEnabled)];
        }
        else
        {
            enabledMods = [.. options.ModsToApply];
        }

        // Clean rollback of previously applied mod deltas BEFORE resolving loose/archive baselines
        // so prior mod runs in override/, ClientPackageFiles/, ../shared/, or game/ never double-patch.
        options.DiagnosticLogger?.Invoke(
            $"[ModManager] Executing pre-apply rollback on '{options.GameClientDirectory}' (Mode={options.Mode}, Mods={enabledMods.Count}).");
        if (effectiveVfs != null)
        {
            await RollbackJournal.ExecuteRollbackAsync(effectiveVfs, options.GameClientDirectory, ct).ConfigureAwait(false);
        }
        else
        {
            await RollbackJournal.ExecuteRollbackAsync(options.GameClientDirectory, ct).ConfigureAwait(false);
        }

        // 1. Resolve and topologically sort
        SortResult sortResult = TopologicalSorter.ResolveAndSort(enabledMods);
        if (!sortResult.Success)
        {
            List<string> allErrors = [.. sortResult.ValidationErrors, .. sortResult.CycleReports];
            return new ApplyResult
            {
                Success = false,
                Errors = allErrors
            };
        }

        IReadOnlyList<ModPackage> executionPlan = sortResult.ExecutionPlan;
        Dictionary<string, byte[]> preparedFiles = new(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        List<string> errors = [];

        // 2. Build K2-faithful multi-tier baseline resolver aligned with ClientModMountSpec
        string targetArchiveName = options.TargetArchiveName ?? "resources999.s2z";
        using GameResourceResolver resourceResolver = GameResourceResolver.CreateForClient(
            options.GameClientDirectory,
            effectiveVfs,
            includeOverride: true,
            excludedArchiveName: targetArchiveName,
            warningLogger: w => warnings.Add(w));

        // 3. Process execution plan
        foreach (ModPackage mod in executionPlan)
        {
            ModManifest manifest = mod.Manifest;

            // 3a. File copies
            foreach (CopyFileOperation copy in manifest.FileCopies)
            {
                if (!string.IsNullOrEmpty(copy.Condition))
                    if (!ConditionEvaluator.Evaluate(copy.Condition, executionPlan))
                        continue;

                try
                {
                    byte[] fileBytes = await mod.GetPayloadFileAsync(copy.SourcePath, ct).ConfigureAwait(false);
                    string normTarget = ModPackage.NormalizeZipPath(copy.TargetName);

                    if (copy.Overwrite == FileOverwriteMode.No && preparedFiles.ContainsKey(normTarget)) continue;

                    preparedFiles[normTarget] = fileBytes;
                    options.DiagnosticLogger?.Invoke(
                        $"[ModManager] Mod '{manifest.Name}' staged copyfile '{normTarget}' ({fileBytes.Length} bytes).");
                }
                catch (System.Security.SecurityException secEx)
                {
                    options.DiagnosticLogger?.Invoke(
                        $"[SECURITY-ALERT][MODDING-ZIPSLIP] Mod '{manifest.Name}' blocked on copyfile '{copy.SourcePath}' -> '{copy.TargetName}': {secEx.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    string err =
                        $"Mod '{manifest.Name}' failed to copy '{copy.SourcePath}' to '{copy.TargetName}': {ex.Message}";
                    warnings.Add(err);
                    if (options.StrictIntegrity) errors.Add(err);
                }
            }

            // 3b. File edits
            foreach (EditFileOperation edit in manifest.FileEdits)
            {
                if (!string.IsNullOrEmpty(edit.Condition))
                    if (!ConditionEvaluator.Evaluate(edit.Condition, executionPlan))
                        continue;

                string normTarget = ModPackage.NormalizeZipPath(edit.TargetName);
                string? baseContent;
                string? sourceTier;

                if (preparedFiles.TryGetValue(normTarget, out byte[]? existingBytes))
                {
                    baseContent = GameResourceResolver.DecodeUtf8Text(existingBytes);
                    sourceTier = "prepared-mod-payload";
                }
                else
                {
                    (baseContent, sourceTier) = await resourceResolver
                        .TryResolveGameTextWithSourceAsync(normTarget, ct)
                        .ConfigureAwait(false);
                }

                if (baseContent == null)
                {
                    string missingMsg =
                        $"Mod '{manifest.Name}' skipped editfile '{edit.TargetName}': base file '{normTarget}' was not found in any K2 VFS mount tier. Verify ClientModMountSpec mount precedence or bundle the file via <copyfile>.";
                    warnings.Add(missingMsg);
                    options.DiagnosticLogger?.Invoke($"[ModManager][WARN] {missingMsg}");
                    continue;
                }

                options.DiagnosticLogger?.Invoke(
                    $"[ModManager] Mod '{manifest.Name}' resolved editfile '{normTarget}' from '{sourceTier}' ({baseContent.Length} chars).");

                try
                {
                    string patchedContent = await FuzzyStringPatcher.ApplyPatchAsync(
                        baseContent,
                        edit.Operations,
                        path => mod.GetPayloadTextAsync(path, ct),
                        normTarget
                    ).ConfigureAwait(false);

                    if (normTarget.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        patchedContent = FuzzyStringPatcher.StripLeadingBomForLua(patchedContent, normTarget);

                        (bool isValid, string? luacErr) =
                            await ASPIRE.Common.Prebaking.Generators.LuaBytecodeCompilerService
                                .ValidateSyntaxWithLuacAsync(patchedContent, normTarget, ct: ct)
                                .ConfigureAwait(false);
                        if (!isValid)
                        {
                            string syntaxErr =
                                $"Mod '{manifest.Name}' produced invalid Lua 5.1 syntax in '{edit.TargetName}' (resolved from {sourceTier}): {luacErr}";
                            warnings.Add(syntaxErr);
                            options.DiagnosticLogger?.Invoke($"[ModManager][ERROR] {syntaxErr}");
                            if (options.StrictIntegrity)
                            {
                                errors.Add(syntaxErr);
                                continue;
                            }
                        }
                    }

                    preparedFiles[normTarget] = Encoding.UTF8.GetBytes(patchedContent);
                }
                catch (System.Security.SecurityException secEx)
                {
                    options.DiagnosticLogger?.Invoke(
                        $"[SECURITY-ALERT][MODDING-ZIPSLIP] Mod '{manifest.Name}' blocked on editfile '{edit.TargetName}': {secEx.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    string err = $"Mod '{manifest.Name}' failed to patch '{edit.TargetName}': {ex.Message}";
                    warnings.Add(err);
                    options.DiagnosticLogger?.Invoke($"[ModManager][ERROR] {err}");
                    if (options.StrictIntegrity) errors.Add(err);
                }
            }

            // 3c. Stringtable edits
            foreach (EditStringtableOperation strEdit in manifest.StringtableEdits)
            {
                if (!string.IsNullOrEmpty(strEdit.Condition))
                    if (!ConditionEvaluator.Evaluate(strEdit.Condition, executionPlan))
                        continue;

                string normTarget = ModPackage.NormalizeZipPath(strEdit.TargetName);
                string? baseContent;
                string? sourceTier;

                if (preparedFiles.TryGetValue(normTarget, out byte[]? existingBytes))
                {
                    baseContent = GameResourceResolver.DecodeUtf8Text(existingBytes);
                    sourceTier = "prepared-mod-payload";
                }
                else
                {
                    (baseContent, sourceTier) = await resourceResolver
                        .TryResolveGameTextWithSourceAsync(normTarget, ct)
                        .ConfigureAwait(false);
                }

                options.DiagnosticLogger?.Invoke(
                    $"[ModManager] Mod '{manifest.Name}' stringtable '{normTarget}' resolved from '{sourceTier ?? "new-empty-table"}'.");

                baseContent ??= string.Empty;

                try
                {
                    string patchedContent = StringtablePatcher.ApplyPatch(baseContent, strEdit.Entries);
                    preparedFiles[normTarget] = Encoding.UTF8.GetBytes(patchedContent);
                }
                catch (System.Security.SecurityException secEx)
                {
                    options.DiagnosticLogger?.Invoke(
                        $"[SECURITY-ALERT][MODDING-ZIPSLIP] Mod '{manifest.Name}' blocked on stringtable '{strEdit.TargetName}': {secEx.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    string err =
                        $"Mod '{manifest.Name}' failed to patch stringtable '{strEdit.TargetName}': {ex.Message}";
                    warnings.Add(err);
                    if (options.StrictIntegrity) errors.Add(err);
                }
            }
        }

        if (options.StrictIntegrity && errors.Count > 0)
            return new ApplyResult
            {
                Success = false,
                AppliedMods = executionPlan,
                ModifiedFiles = [.. preparedFiles.Keys],
                Errors = errors,
                Warnings = warnings
            };

        // 4. Inject
        JournalEntry journal;
        if (effectiveVfs != null)
        {
            if (options.Mode == InjectionMode.Archive)
            {
                journal = await DirectArchiveInjector.InjectAsync(
                    effectiveVfs,
                    options.GameClientDirectory,
                    preparedFiles,
                    executionPlan,
                    targetArchiveName,
                    ct
                ).ConfigureAwait(false);
            }
            else
            {
                journal = await LayeredVfsInjector.InjectAsync(
                    effectiveVfs,
                    options.GameClientDirectory,
                    preparedFiles,
                    executionPlan,
                    ct
                ).ConfigureAwait(false);
            }
        }
        else
        {
            if (options.Mode == InjectionMode.Archive)
            {
                journal = await DirectArchiveInjector.InjectAsync(
                    options.GameClientDirectory,
                    preparedFiles,
                    executionPlan,
                    targetArchiveName,
                    ct
                ).ConfigureAwait(false);
            }
            else
            {
                journal = await LayeredVfsInjector.InjectAsync(
                    options.GameClientDirectory,
                    preparedFiles,
                    executionPlan,
                    ct
                ).ConfigureAwait(false);
            }
        }

        return new ApplyResult
        {
            Success = errors.Count == 0,
            AppliedMods = executionPlan,
            ModifiedFiles = [.. preparedFiles.Keys],
            Errors = errors,
            Warnings = warnings,
            Journal = journal
        };
    }

    public async Task<RollbackResult> RollbackAsync(string gameClientDirectory, CancellationToken ct = default)
    {
        if (Vfs != null)
        {
            bool success = await RollbackJournal.ExecuteRollbackAsync(Vfs, gameClientDirectory, ct)
                .ConfigureAwait(false);
            return new RollbackResult
            {
                Success = success,
                Message = success ? "Rollback executed cleanly." : "Rollback completed with warnings."
            };
        }
        else
        {
            bool success = await RollbackJournal.ExecuteRollbackAsync(gameClientDirectory, ct).ConfigureAwait(false);
            return new RollbackResult
            {
                Success = success,
                Message = success ? "Rollback executed cleanly." : "Rollback completed with warnings."
            };
        }
    }

    public async Task<IReadOnlyList<ModPackage>> DiscoverModsAsync(IVirtualFileSystem vfs, string modsDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        return await DiscoverModsInternalAsync(vfs, modsDirectory, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ModPackage>> DiscoverModsInternalAsync(
        IVirtualFileSystem vfs,
        string modsDirectory,
        CancellationToken ct)
    {
        if (!vfs.DirectoryExists(modsDirectory)) return [];

        List<ModPackage> results = [];

        // 1. Discover .honmod files
        IEnumerable<string> honmodFiles = vfs.EnumerateFiles(modsDirectory, "*.honmod");
        foreach (string file in honmodFiles)
            try
            {
                ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(vfs, file, ct).ConfigureAwait(false);
                results.Add(pkg);
            }
            catch
            {
                // Skip unreadable
            }

        // 2. Discover unpacked mod subdirectories
        IEnumerable<string> subdirs = vfs.EnumerateDirectories(modsDirectory);
        foreach (string dir in subdirs)
        {
            string modXmlPath = $"{dir.TrimEnd('/')}/mod.xml";
            if (vfs.FileExists(modXmlPath))
                try
                {
                    ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(vfs, dir, ct)
                        .ConfigureAwait(false);
                    if (!results.Any(r =>
                            r.Manifest.Name.Equals(pkg.Manifest.Name, StringComparison.OrdinalIgnoreCase)))
                        results.Add(pkg);
                }
                catch
                {
                    // Skip
                }
        }

        return results;
    }

    public async Task<RollbackResult> RollbackAsync(IVirtualFileSystem vfs, string gameClientDirectory = "",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        bool success = await RollbackJournal.ExecuteRollbackAsync(vfs, gameClientDirectory, ct).ConfigureAwait(false);
        return new RollbackResult
        {
            Success = success,
            Message = success ? "Rollback executed cleanly." : "Rollback completed with warnings."
        };
    }
}
