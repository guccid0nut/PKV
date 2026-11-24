// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;

namespace Kongor.Modding.Repair;

public sealed record RepairResult
{
    public required bool Success { get; init; }
    public required string ModName { get; init; }
    public required string OutputPath { get; init; }
    public int TransformedFilesCount { get; init; }
    public int FixesAppliedCount { get; init; }
    public IReadOnlyList<string> TransformationLog { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

public sealed record BatchRepairResult
{
    public required int TotalDiscovered { get; init; }
    public required int TotalRepaired { get; init; }
    public required int TotalFailed { get; init; }
    public required IReadOnlyList<RepairResult> Results { get; init; }
    public double SuccessRate => TotalDiscovered == 0 ? 100.0 : (double)TotalRepaired / TotalDiscovered * 100.0;
}

public interface IModRepairEngine
{
    Task<RepairResult> RepairModAsync(string inputHonModPath, string outputHonModPath, CancellationToken ct = default);

    Task<BatchRepairResult> RepairAllAsync(string inputDirectory, string outputDirectory,
        CancellationToken ct = default);
}

/// <summary>
///     Automated compatibility repair pipeline for transforming broken/outdated legacy HoN mods
///     into modern, v4.14+ compatible packages.
/// </summary>
public sealed class ModRepairEngine : IModRepairEngine
{
    public async Task<RepairResult> RepairModAsync(string inputHonModPath, string outputHonModPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHonModPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHonModPath);

        if (!File.Exists(inputHonModPath))
            throw new FileNotFoundException($"Input mod file not found: {inputHonModPath}");

        List<string> log = [];
        int totalFixes = 0;
        int filesChanged = 0;

        Dictionary<string, byte[]> payloadFiles = new(StringComparer.OrdinalIgnoreCase);
        byte[]? iconData = null;
        string modXmlContent;

        // 1. Read input archive
        using (FileStream fs = File.OpenRead(inputHonModPath))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Read, false))
        {
            ZipArchiveEntry? modXmlEntry =
                zip.Entries.FirstOrDefault(e => e.FullName.Equals("mod.xml", StringComparison.OrdinalIgnoreCase));
            if (modXmlEntry == null) throw new InvalidDataException($"Missing 'mod.xml' in {inputHonModPath}");

            using (Stream s = modXmlEntry.Open())
            using (StreamReader r = new(s, Encoding.UTF8))
            {
                modXmlContent = await r.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.Equals("mod.xml", StringComparison.OrdinalIgnoreCase)) continue;

                using Stream es = entry.Open();
                using MemoryStream ms = new((int)entry.Length);
                await es.CopyToAsync(ms, ct).ConfigureAwait(false);
                byte[] bytes = ms.ToArray();

                if (entry.FullName.Equals("icon.png", StringComparison.OrdinalIgnoreCase))
                    iconData = bytes;
                else
                    payloadFiles[entry.FullName] = bytes;
            }
        }

        // 2. Repair mod.xml text and targets
        string normXml = XmlSyntaxNormalizer.NormalizeXml(modXmlContent, out int xmlFixes);
        if (xmlFixes > 0)
        {
            totalFixes += xmlFixes;
            log.Add($"Fixed {xmlFixes} XML syntax issues in mod.xml");
        }

        normXml = TargetFileRelocator.RelocateXmlText(normXml, out int relFixes);
        if (relFixes > 0)
        {
            totalFixes += relFixes;
            log.Add($"Relocated {relFixes} deprecated target paths in mod.xml");
        }

        ModManifest manifest = ModManifestParser.Parse(normXml);

        // Relocate manifest targets (FileEdits, FileCopies, Stringtables)
        manifest = TargetFileRelocator.RelocateManifest(manifest, out int manRelFixes);
        if (manRelFixes > 0)
        {
            totalFixes += manRelFixes;
            log.Add($"Relocated {manRelFixes} manifest operation target paths");
        }

        // Neutralize restrictive appversion check
        if (manifest.AppVersion is not null and not "*")
        {
            log.Add($"Neutralized appversion check '{manifest.AppVersion}' -> '*'");
            totalFixes++;
            manifest = manifest with { AppVersion = "*" };
        }

        // Modernize inline XML operations inside FileEdits
        List<EditFileOperation> modernEdits = [];
        foreach (EditFileOperation edit in manifest.FileEdits)
        {
            List<PatchOperation> modernOps = [];
            foreach (PatchOperation op in edit.Operations)
                switch (op)
                {
                    case InsertOperation ins when !string.IsNullOrEmpty(ins.Content):
                    {
                        string norm = XmlSyntaxNormalizer.NormalizeXml(ins.Content, out int fXml);
                        norm = UiAnchorRescaler.RescaleUiCoordinates(norm, out int fScale);
                        norm = LuaApiCompatibilityShim.ApplyCompatibilityBridge(norm, out int fLua);
                        int opFixes = fXml + fScale + fLua;
                        if (opFixes > 0) totalFixes += opFixes;
                        modernOps.Add(ins with { Content = norm });
                    }
                        break;

                    case ReplaceOperation rep when !string.IsNullOrEmpty(rep.Content):
                    {
                        string norm = XmlSyntaxNormalizer.NormalizeXml(rep.Content, out int fXml);
                        norm = UiAnchorRescaler.RescaleUiCoordinates(norm, out int fScale);
                        norm = LuaApiCompatibilityShim.ApplyCompatibilityBridge(norm, out int fLua);
                        int opFixes = fXml + fScale + fLua;
                        if (opFixes > 0) totalFixes += opFixes;
                        modernOps.Add(rep with { Content = norm });
                    }
                        break;

                    default:
                        modernOps.Add(op);
                        break;
                }

            modernEdits.Add(edit with { Operations = modernOps });
        }

        manifest = manifest with { FileEdits = modernEdits };

        // 3. Repair payload files (.xml, .interface, .package, .entity, .lua, .str)
        Dictionary<string, byte[]> repairedPayloads = new(StringComparer.OrdinalIgnoreCase);
        bool hasLuaPayload = false;

        foreach (KeyValuePair<string, byte[]> kvp in payloadFiles)
        {
            string entryPath = kvp.Key;
            byte[] rawBytes = kvp.Value;

            string ext = Path.GetExtension(entryPath).ToLowerInvariant();
            if (ext is ".xml" or ".interface" or ".package" or ".entity")
            {
                string text = Encoding.UTF8.GetString(rawBytes);
                string transformed = XmlSyntaxNormalizer.NormalizeXml(text, out int fXml);
                transformed = UiAnchorRescaler.RescaleUiCoordinates(transformed, out int fScale);
                transformed = TargetFileRelocator.RelocateXmlText(transformed, out int fRel);

                if (fXml > 0 || fScale > 0 || fRel > 0)
                {
                    totalFixes += fXml + fScale + fRel;
                    filesChanged++;
                    log.Add(
                        $"Transformed XML/UI in '{entryPath}': {fXml} syntax fixes, {fScale} UI rescalings, {fRel} path relocations");
                    repairedPayloads[entryPath] = Encoding.UTF8.GetBytes(transformed);
                }
                else
                {
                    repairedPayloads[entryPath] = rawBytes;
                }
            }
            else if (ext is ".lua")
            {
                hasLuaPayload = true;
                string text = Encoding.UTF8.GetString(rawBytes);
                string transformed = LuaApiCompatibilityShim.ApplyCompatibilityBridge(text, out int fLua);

                if (fLua > 0)
                {
                    totalFixes += fLua;
                    filesChanged++;
                    log.Add($"Injected Lua compatibility shim in '{entryPath}': {fLua} shims");
                    repairedPayloads[entryPath] = Encoding.UTF8.GetBytes(transformed);
                }
                else
                {
                    repairedPayloads[entryPath] = rawBytes;
                }
            }
            else if (ext is ".str")
            {
                string text = Encoding.UTF8.GetString(rawBytes);
                string transformed = XmlSyntaxNormalizer.NormalizeXml(text, out int fStr);
                if (fStr > 0)
                {
                    totalFixes += fStr;
                    filesChanged++;
                    log.Add($"Normalized stringtable entities in '{entryPath}': {fStr} fixes");
                    repairedPayloads[entryPath] = Encoding.UTF8.GetBytes(transformed);
                }
                else
                {
                    repairedPayloads[entryPath] = rawBytes;
                }
            }
            else
            {
                repairedPayloads[entryPath] = rawBytes;
            }
        }

        // Invalidate or inject kongor_compat_shim.lua into package if Lua scripts or event handlers are present
        if (hasLuaPayload && !repairedPayloads.ContainsKey("scripts/kongor_compat_shim.lua") &&
            !repairedPayloads.ContainsKey("kongor_compat_shim.lua"))
        {
            repairedPayloads["scripts/kongor_compat_shim.lua"] = LuaApiCompatibilityShim.GetShimFileBytes();
            totalFixes++;
            filesChanged++;
            log.Add("Embedded 'scripts/kongor_compat_shim.lua' into mod package");
        }

        // 4. Ensure destination directory exists and write repaired package
        string? destDir = Path.GetDirectoryName(outputHonModPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        await HonModArchiveWriter
            .WritePackageAsync(outputHonModPath, manifest, repairedPayloads, iconData, CompressionLevel.Optimal, ct)
            .ConfigureAwait(false);

        return new RepairResult
        {
            Success = true,
            ModName = manifest.Name,
            OutputPath = outputHonModPath,
            TransformedFilesCount = filesChanged,
            FixesAppliedCount = totalFixes,
            TransformationLog = log
        };
    }

    /// <summary>
    ///     Discovers and repairs all .honmod packages in a directory.
    /// </summary>
    public async Task<BatchRepairResult> RepairAllAsync(string inputDirectory, string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException($"Input directory not found: {inputDirectory}");

        Directory.CreateDirectory(outputDirectory);

        string[] modFiles =
        [
            .. Directory.GetFiles(inputDirectory, "*.honmod", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(inputDirectory, "*.HONMOD", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        List<RepairResult> results = [];
        int repaired = 0;
        int failed = 0;

        foreach (string file in modFiles)
        {
            ct.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(inputDirectory, file);
            string outPath = Path.Combine(outputDirectory, relativePath);

            try
            {
                RepairResult res = await RepairModAsync(file, outPath, ct).ConfigureAwait(false);
                results.Add(res);
                if (res.Success) repaired++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new RepairResult
                {
                    Success = false,
                    ModName = Path.GetFileNameWithoutExtension(file),
                    OutputPath = outPath,
                    TransformedFilesCount = 0,
                    FixesAppliedCount = 0,
                    TransformationLog = [],
                    ErrorMessage = ex.Message
                });
            }
        }

        return new BatchRepairResult
        {
            TotalDiscovered = modFiles.Length,
            TotalRepaired = repaired,
            TotalFailed = failed,
            Results = results
        };
    }
}
