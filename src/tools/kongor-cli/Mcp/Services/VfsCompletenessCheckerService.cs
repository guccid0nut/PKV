// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ASPIRE.Common.K2;
using Kongor.Cli.Mcp.Services.Models;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Pre-flight automated static VFS completeness checker for Project Kongor.
/// Eliminates silent white dummy textures ($white quads), neon magenta checkerboards (#FF00FF),
/// and Utah Teapot model fallbacks (null.mdf) by statically verifying 100% of game asset references
/// against the live multi-layer K2 Virtual File System before the client boots.
/// </summary>
public sealed class VfsCompletenessCheckerService : IVfsCompletenessCheckerService
{
    private static readonly HashSet<string> EngineVirtualTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "$white", "$black", "$invis", "$invisible", "$flat_norm", "$checker", "$empty", "$none", "$host", "$red", "$green", "$blue"
    };

    private static readonly string[] Frame9SliceSuffixes = ["_c", "_tl", "_t", "_tr", "_l", "_r", "_bl", "_b", "_br"];
    private static readonly Regex UnescapedAmpRegex = new(@"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", RegexOptions.Compiled);
    private static readonly Regex ParamPlaceholderRegex = new(@"\{[a-zA-Z0-9_\-\.]+\}", RegexOptions.Compiled);

    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public VfsCompletenessCheckerService(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():HH:mm:ss.fff}] [VfsCompleteness] {msg}"));
    }

    public async Task<VfsCompletenessReport> AuditAsync(VfsCompletenessOptions? options = null, CancellationToken ct = default)
    {
        options ??= new VfsCompletenessOptions();
        Stopwatch totalSw = Stopwatch.StartNew();

        string repoRoot = FindRepoRoot();
        string clientDir = options.ClientDirectory ?? Path.Combine(repoRoot, "games", "client");

        _logger($"Starting VFS completeness audit (Scope: '{options.Scope}', TargetPath: '{options.TargetPath ?? "all"}', Strict: {options.Strict})...");

        // 1. Build In-Memory High-Speed VFS Index
        Stopwatch indexSw = Stopwatch.StartNew();
        VfsIndex vfsIndex = new(_logger);
        vfsIndex.BuildIndex(clientDir, repoRoot);
        indexSw.Stop();

        // 2. Discover Target Files to Scan Based on Scope
        List<string> filesToScan = DiscoverTargetFiles(repoRoot, clientDir, options);
        _logger($"Discovered {filesToScan.Count:N0} candidate source files to extract asset references from.");

        // 3. Parallel AST & Manifest Extraction + Resolution
        Stopwatch scanSw = Stopwatch.StartNew();
        ConcurrentBag<VfsAssetReference> allReferences = [];
        ConcurrentBag<VfsMissingAssetIssue> allIssues = [];

        int filesProcessed = 0;
        int maxParallel = Math.Max(1, Environment.ProcessorCount);

        await Parallel.ForEachAsync(filesToScan, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallel,
            CancellationToken = ct
        }, async (filePath, token) =>
        {
            token.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string relToRepo = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
            string declaringDir = Path.GetDirectoryName(relToRepo)?.Replace('\\', '/') ?? string.Empty;

            // Normalize declaringDir so it aligns with VFS root
            // e.g. "games/client/override/ui/fe2/store" -> "ui/fe2/store"
            string vfsDeclaringDir = StripVfsMountPrefix(declaringDir);

            List<VfsAssetReference> fileRefs = [];

            try
            {
                if (ext is ".package" or ".interface")
                {
                    string content = await File.ReadAllTextAsync(filePath, token).ConfigureAwait(false);
                    ExtractFromUiXml(filePath, relToRepo, content, fileRefs);
                }
                else if (ext == ".mdf")
                {
                    string content = await File.ReadAllTextAsync(filePath, token).ConfigureAwait(false);
                    ExtractFromMdf(filePath, relToRepo, content, fileRefs);
                }
                else if (ext == ".material")
                {
                    string content = await File.ReadAllTextAsync(filePath, token).ConfigureAwait(false);
                    ExtractFromMaterial(filePath, relToRepo, content, fileRefs);
                }
                else if (Path.GetFileName(filePath).Equals("heroes_preview.xml", StringComparison.OrdinalIgnoreCase))
                {
                    string content = await File.ReadAllTextAsync(filePath, token).ConfigureAwait(false);
                    ExtractFromHeroesPreview(filePath, relToRepo, content, fileRefs);
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Could not parse file '{relToRepo}': {ex.Message}");
            }

            foreach (VfsAssetReference reference in fileRefs)
            {
                allReferences.Add(reference);

                // Cross-reference against VFS index
                List<string> probed = [];
                bool isMaterialSampler = reference.AssetType == "MaterialSampler";
                if (!vfsIndex.TryResolve(reference.VirtualPath, vfsDeclaringDir, out VfsEntryLocation? winningLoc, out string? matchedKey, probed, allowTextureArchiveTiers: isMaterialSampler))
                {
                    // Special handling for Frame 9-slice: check if center slice or direct texture exists
                    if (reference.AssetType == "Frame")
                    {
                        string baseNoExt = reference.VirtualPath;
                        string frameExt = Path.GetExtension(reference.VirtualPath);
                        if (!string.IsNullOrEmpty(frameExt)) baseNoExt = baseNoExt[..^frameExt.Length];

                        bool hasCenter = vfsIndex.TryResolve($"{baseNoExt}_c{frameExt}", vfsDeclaringDir, out _, out _);
                        if (!hasCenter)
                        {
                            // Neither direct texture nor center slice exists. Frame is totally missing!
                            allIssues.Add(new VfsMissingAssetIssue
                            {
                                Severity = "FATAL",
                                Category = "MISSING_FRAME_SLICE",
                                VirtualPath = reference.VirtualPath,
                                DeclaringFile = relToRepo,
                                LineNumber = reference.LineNumber,
                                Context = reference.Context,
                                ProbedPaths = probed.Take(12).ToList(),
                                RemediationHint = $"Frame widget base '{reference.VirtualPath}' and center slice '{baseNoExt}_c{frameExt}' missing. Frame will render as untextured white box."
                            });
                            continue;
                        }

                        // Center slice exists! Frame renders properly. If strict mode is enabled, verify all 9 border slices.
                        if (options.Strict)
                        {
                            List<string> missingSlices = [];
                            foreach (string sfx in Frame9SliceSuffixes)
                            {
                                if (!vfsIndex.TryResolve($"{baseNoExt}{sfx}{frameExt}", vfsDeclaringDir, out _, out _))
                                {
                                    missingSlices.Add($"{baseNoExt}{sfx}{frameExt}");
                                }
                            }

                            if (missingSlices.Count > 0)
                            {
                                allIssues.Add(new VfsMissingAssetIssue
                                {
                                    Severity = "WARNING",
                                    Category = "MISSING_FRAME_SLICE",
                                    VirtualPath = reference.VirtualPath,
                                    DeclaringFile = relToRepo,
                                    LineNumber = reference.LineNumber,
                                    Context = reference.Context,
                                    ProbedPaths = missingSlices.Take(12).ToList(),
                                    RemediationHint = $"Frame has center slice but is missing {missingSlices.Count} border slices: {string.Join(", ", missingSlices.Take(3))}..."
                                });
                            }
                        }

                        continue;
                    }

                    string category = reference.AssetType switch
                    {
                        "Model" => "MISSING_MODEL",
                        "Clip" => "MISSING_CLIP",
                        "Sampler" => "MISSING_MATERIAL_SAMPLER",
                        "Cubemap" => "MISSING_CUBEMAP",
                        _ => "MISSING_UI_TEXTURE"
                    };

                    string remediation = category switch
                    {
                        "MISSING_MODEL" => $"Referenced .model mesh '{reference.VirtualPath}' not found across any VFS mount layer. Guaranteed Utah Teapot (null.mdf).",
                        "MISSING_CLIP" => $"Animation clip '{reference.VirtualPath}' missing in VFS. Will cause T-pose or frozen animation.",
                        "MISSING_MATERIAL_SAMPLER" => $"Sampler texture '{reference.VirtualPath}' ({reference.Context ?? "diffuse"}) not found. Will trigger pink checkerboard (#FF00FF) or white quad.",
                        _ => $"UI texture '{reference.VirtualPath}' not found. In-game client will silently display an untextured white box."
                    };

                    allIssues.Add(new VfsMissingAssetIssue
                    {
                        Severity = reference.IsFatalIfMissing ? "FATAL" : "WARNING",
                        Category = category,
                        VirtualPath = reference.VirtualPath,
                        DeclaringFile = relToRepo,
                        LineNumber = reference.LineNumber,
                        Context = reference.Context,
                        ProbedPaths = probed.Take(12).ToList(),
                        RemediationHint = remediation
                    });
                }
            }

            int currentCount = Interlocked.Increment(ref filesProcessed);
            if (currentCount % 500 == 0 || currentCount == filesToScan.Count)
            {
                _logger($"Progress: processed {currentCount:N0}/{filesToScan.Count:N0} files ({allReferences.Count:N0} refs verified, {allIssues.Count:N0} issues flagged)...");
            }
        });

        scanSw.Stop();
        totalSw.Stop();

        // 4. Summarize and Format Report
        List<VfsMissingAssetIssue> issuesList = allIssues
            .OrderByDescending(i => i.Severity == "FATAL")
            .ThenBy(i => i.Category)
            .ThenBy(i => i.DeclaringFile)
            .Take(options.Limit)
            .ToList();

        int fatalCount = allIssues.Count(i => i.Severity == "FATAL");
        int warnCount = allIssues.Count(i => i.Severity == "WARNING");

        bool success = options.Strict ? (fatalCount == 0 && warnCount == 0) : (fatalCount == 0);
        string status = fatalCount > 0 ? "fatal_errors" : (warnCount > 0 ? "warnings" : "clean");
        bool truncated = allIssues.Count > options.Limit;

        _logger($"[VfsCompleteness] Audit completed in {totalSw.ElapsedMilliseconds}ms. Scanned {filesToScan.Count} files, checked {allReferences.Count:N0} references. Fatal: {fatalCount}, Warnings: {warnCount}. Status: {status}");

        return new VfsCompletenessReport
        {
            Success = success,
            Status = status,
            TotalFilesScanned = filesToScan.Count,
            TotalReferencesChecked = allReferences.Count,
            MissingFatalCount = fatalCount,
            MissingWarningCount = warnCount,
            TotalIndexedVfsEntries = vfsIndex.TotalIndexedEntries,
            IndexDurationMs = Math.Round(indexSw.Elapsed.TotalMilliseconds, 2),
            ScanDurationMs = Math.Round(scanSw.Elapsed.TotalMilliseconds, 2),
            TotalDurationMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
            ScanTruncated = truncated,
            CompleteScannedRoots = ["override", "game", "ClientPackageFiles", "heroes", "shared"],
            Issues = issuesList,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    #region Observe-Effect VFS Completeness Invariant (#175)

    /// <summary>
    /// Classifies a single VFS path's state transition between pre- and post-snapshots (#175).
    /// Enforces the epistemic invariant that Created (absent -> present) and Deleted (present -> absent)
    /// may ONLY be asserted when the absent side's root is in completeRoots and the scan was NOT truncated.
    /// </summary>
    public static VfsFilesystemEffectClassification ClassifyFilesystemEffect(
        string relativePath,
        VfsSnapshotEntry? preEntry,
        VfsSnapshotEntry? postEntry,
        IReadOnlySet<string> preCompleteRoots,
        IReadOnlySet<string> postCompleteRoots,
        bool preScanTruncated = false,
        bool postScanTruncated = false)
    {
        string normPath = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        string rootPrefix = ExtractRootPrefix(normPath);

        bool preRootComplete = IsRootCovered(normPath, rootPrefix, preCompleteRoots);
        bool postRootComplete = IsRootCovered(normPath, rootPrefix, postCompleteRoots);
        bool anyTruncated = preScanTruncated || postScanTruncated;

        if (preEntry != null && postEntry != null)
        {
            bool sizeChanged = preEntry.SizeBytes != postEntry.SizeBytes;
            bool hasBothHashes = !string.IsNullOrEmpty(preEntry.Sha256Hex) &&
                                 !string.IsNullOrEmpty(postEntry.Sha256Hex);

            if (!sizeChanged && !hasBothHashes)
            {
                return new VfsFilesystemEffectClassification(
                    RelativePath: normPath,
                    RootPrefix: rootPrefix,
                    Effect: VfsFilesystemEffectKind.Unknown,
                    PreRootComplete: preRootComplete,
                    PostRootComplete: postRootComplete,
                    ScanTruncated: anyTruncated,
                    UnobservedAbsenceReason: $"[REA-OBSERVE-EFFECT] missing_sha256_digest: Cannot assert Unchanged for '{normPath}' when sizes match ({preEntry.SizeBytes} bytes) but SHA-256 digest is missing.");
            }

            bool hashChanged = hasBothHashes &&
                               !string.Equals(preEntry.Sha256Hex, postEntry.Sha256Hex, StringComparison.OrdinalIgnoreCase);

            VfsFilesystemEffectKind kind = (sizeChanged || hashChanged)
                ? VfsFilesystemEffectKind.Modified
                : VfsFilesystemEffectKind.Unchanged;

            return new VfsFilesystemEffectClassification(
                RelativePath: normPath,
                RootPrefix: rootPrefix,
                Effect: kind,
                PreRootComplete: preRootComplete,
                PostRootComplete: postRootComplete,
                ScanTruncated: anyTruncated,
                UnobservedAbsenceReason: null);
        }

        if (preEntry == null && postEntry != null)
        {
            if (!preRootComplete || preScanTruncated)
            {
                string reason = preScanTruncated
                    ? $"[REA-OBSERVE-EFFECT] scan_truncated: Cannot assert Created for '{normPath}': pre-snapshot scan was truncated, so prior absence is unverified."
                    : $"[REA-OBSERVE-EFFECT] root_not_completely_indexed: Cannot assert Created for '{normPath}': root '{rootPrefix}' is absent from preCompleteRoots, so prior absence is unverified.";
                return new VfsFilesystemEffectClassification(
                    RelativePath: normPath,
                    RootPrefix: rootPrefix,
                    Effect: VfsFilesystemEffectKind.Unknown,
                    PreRootComplete: preRootComplete,
                    PostRootComplete: postRootComplete,
                    ScanTruncated: anyTruncated,
                    UnobservedAbsenceReason: reason);
            }

            return new VfsFilesystemEffectClassification(
                RelativePath: normPath,
                RootPrefix: rootPrefix,
                Effect: VfsFilesystemEffectKind.Created,
                PreRootComplete: true,
                PostRootComplete: postRootComplete,
                ScanTruncated: anyTruncated,
                UnobservedAbsenceReason: null);
        }

        if (preEntry != null && postEntry == null)
        {
            if (!postRootComplete || postScanTruncated)
            {
                string reason = postScanTruncated
                    ? $"[REA-OBSERVE-EFFECT] scan_truncated: Cannot assert Deleted for '{normPath}': post-snapshot scan was truncated, so post-operation absence is unverified."
                    : $"[REA-OBSERVE-EFFECT] root_not_completely_indexed: Cannot assert Deleted for '{normPath}': root '{rootPrefix}' is absent from postCompleteRoots, so post-operation absence is unverified.";
                return new VfsFilesystemEffectClassification(
                    RelativePath: normPath,
                    RootPrefix: rootPrefix,
                    Effect: VfsFilesystemEffectKind.Unknown,
                    PreRootComplete: preRootComplete,
                    PostRootComplete: postRootComplete,
                    ScanTruncated: anyTruncated,
                    UnobservedAbsenceReason: reason);
            }

            return new VfsFilesystemEffectClassification(
                RelativePath: normPath,
                RootPrefix: rootPrefix,
                Effect: VfsFilesystemEffectKind.Deleted,
                PreRootComplete: preRootComplete,
                PostRootComplete: true,
                ScanTruncated: anyTruncated,
                UnobservedAbsenceReason: null);
        }

        // Both preEntry and postEntry are null
        if (preRootComplete && postRootComplete && !anyTruncated)
        {
            return new VfsFilesystemEffectClassification(
                RelativePath: normPath,
                RootPrefix: rootPrefix,
                Effect: VfsFilesystemEffectKind.Unchanged,
                PreRootComplete: true,
                PostRootComplete: true,
                ScanTruncated: false,
                UnobservedAbsenceReason: null);
        }

        string bothNullToken = anyTruncated ? "scan_truncated" : "root_not_completely_indexed";
        return new VfsFilesystemEffectClassification(
            RelativePath: normPath,
            RootPrefix: rootPrefix,
            Effect: VfsFilesystemEffectKind.Unknown,
            PreRootComplete: preRootComplete,
            PostRootComplete: postRootComplete,
            ScanTruncated: anyTruncated,
            UnobservedAbsenceReason: $"[REA-OBSERVE-EFFECT] {bothNullToken}: Path absent in both snapshots and root completeness is not established.");
    }

    /// <summary>
    /// Computes an Observe-Effect diff across pre- and post-VFS snapshots (#175).
    /// </summary>
    public static VfsObserveEffectDiffReport DiffSnapshotsWithCompleteness(
        IReadOnlyDictionary<string, VfsSnapshotEntry> preSnapshot,
        IReadOnlyDictionary<string, VfsSnapshotEntry> postSnapshot,
        IReadOnlySet<string> preCompleteRoots,
        IReadOnlySet<string> postCompleteRoots,
        bool preScanTruncated = false,
        bool postScanTruncated = false)
    {
        SortedSet<string> allPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (preSnapshot != null)
        {
            foreach (string k in preSnapshot.Keys) allPaths.Add(k.Replace('\\', '/').TrimStart('/'));
        }
        if (postSnapshot != null)
        {
            foreach (string k in postSnapshot.Keys) allPaths.Add(k.Replace('\\', '/').TrimStart('/'));
        }

        List<VfsFilesystemEffectClassification> effects = new List<VfsFilesystemEffectClassification>(allPaths.Count);
        int createdCount = 0;
        int deletedCount = 0;
        int modifiedCount = 0;
        int unknownCount = 0;

        foreach (string path in allPaths)
        {
            VfsSnapshotEntry? pre = null;
            if (preSnapshot != null)
            {
                preSnapshot.TryGetValue(path, out pre);
            }

            VfsSnapshotEntry? post = null;
            if (postSnapshot != null)
            {
                postSnapshot.TryGetValue(path, out post);
            }

            VfsFilesystemEffectClassification classification = ClassifyFilesystemEffect(
                path,
                pre,
                post,
                preCompleteRoots,
                postCompleteRoots,
                preScanTruncated,
                postScanTruncated);

            effects.Add(classification);
            switch (classification.Effect)
            {
                case VfsFilesystemEffectKind.Created:
                    createdCount++;
                    break;
                case VfsFilesystemEffectKind.Deleted:
                    deletedCount++;
                    break;
                case VfsFilesystemEffectKind.Modified:
                    modifiedCount++;
                    break;
                case VfsFilesystemEffectKind.Unknown:
                    unknownCount++;
                    break;
            }
        }

        bool anyTruncated = preScanTruncated || postScanTruncated;
        return new VfsObserveEffectDiffReport(
            IsAuthoritative: !anyTruncated && unknownCount == 0,
            ScanTruncated: anyTruncated,
            PreCompleteRoots: preCompleteRoots != null ? preCompleteRoots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList() : [],
            PostCompleteRoots: postCompleteRoots != null ? postCompleteRoots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList() : [],
            Effects: effects,
            CreatedCount: createdCount,
            DeletedCount: deletedCount,
            ModifiedCount: modifiedCount,
            UnknownCount: unknownCount);
    }

    private static string ExtractRootPrefix(string normPath)
    {
        if (string.IsNullOrEmpty(normPath)) return ".";
        int slashIdx = normPath.IndexOf('/');
        return slashIdx > 0 ? normPath.Substring(0, slashIdx) : ".";
    }

    private static bool IsRootCovered(string normPath, string rootPrefix, IReadOnlySet<string>? completeRoots)
    {
        if (completeRoots == null || completeRoots.Count == 0) return false;
        if (completeRoots.Contains("*") || completeRoots.Contains("all")) return true;
        if (completeRoots.Contains(rootPrefix)) return true;

        foreach (string candidate in completeRoots)
        {
            string cleanCandidate = candidate.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(cleanCandidate)) continue;
            if (string.Equals(normPath, cleanCandidate, StringComparison.OrdinalIgnoreCase) ||
                normPath.StartsWith(cleanCandidate + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Asset Extraction Parsers

    private static void ExtractFromUiXml(string fullPath, string relPath, string content, List<VfsAssetReference> references)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        // Fast XML sanitization for unescaped ampersands common in K2 UI
        string sanitized = content;
        if (sanitized.StartsWith('\uFEFF')) sanitized = sanitized[1..];
        sanitized = UnescapedAmpRegex.Replace(sanitized, "&amp;");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(sanitized, LoadOptions.SetLineInfo);
        }
        catch
        {
            // If XML parse fails, fallback to regex extraction for textures
            ExtractTexturesViaRegex(relPath, content, references);
            return;
        }

        if (doc.Root == null) return;

        foreach (XElement el in doc.Root.DescendantsAndSelf())
        {
            string tag = el.Name.LocalName.ToLowerInvariant();
            System.Xml.IXmlLineInfo lineInfo = (System.Xml.IXmlLineInfo)el;
            int line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

            if (tag is "image" or "animatedimage" or "button" or "panel" or "frame")
            {
                string? texture = el.Attribute("texture")?.Value ??
                                  el.Attribute("texturepath")?.Value ??
                                  el.Attribute("image")?.Value ??
                                  el.Attribute("icon")?.Value;

                if (!string.IsNullOrWhiteSpace(texture) && !IsIgnoredTexture(texture))
                {
                    string name = el.Attribute("name")?.Value ?? el.Attribute("id")?.Value ?? tag;

                    if (tag == "frame")
                    {
                        references.Add(new VfsAssetReference
                        {
                            VirtualPath = texture,
                            DeclaringFile = relPath,
                            LineNumber = line,
                            AssetType = "Frame",
                            Context = name,
                            IsFatalIfMissing = true
                        });
                    }
                    else if (tag == "animatedimage")
                    {
                        references.Add(new VfsAssetReference
                        {
                            VirtualPath = texture,
                            DeclaringFile = relPath,
                            LineNumber = line,
                            AssetType = "AnimatedImage",
                            Context = name,
                            IsFatalIfMissing = false
                        });
                    }
                    else
                    {
                        references.Add(new VfsAssetReference
                        {
                            VirtualPath = texture,
                            DeclaringFile = relPath,
                            LineNumber = line,
                            AssetType = "Texture",
                            Context = name,
                            IsFatalIfMissing = true
                        });
                    }
                }
            }
        }
    }

    private static void ExtractTexturesViaRegex(string relPath, string content, List<VfsAssetReference> references)
    {
        Regex attrRegex = new(@"\b(?:texture|texturepath|image|icon)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
        string[] lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = attrRegex.Match(lines[i]);
            if (match.Success)
            {
                string val = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(val) && !IsIgnoredTexture(val))
                {
                    references.Add(new VfsAssetReference
                    {
                        VirtualPath = val,
                        DeclaringFile = relPath,
                        LineNumber = i + 1,
                        AssetType = "Texture",
                        Context = "RegexFallback",
                        IsFatalIfMissing = true
                    });
                }
            }
        }
    }

    private static void ExtractFromMdf(string fullPath, string relPath, string content, List<VfsAssetReference> references)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        string sanitized = content;
        if (sanitized.StartsWith('\uFEFF')) sanitized = sanitized[1..];

        XDocument doc;
        try { doc = XDocument.Parse(sanitized, LoadOptions.SetLineInfo); }
        catch { return; }

        XElement? root = doc.Root;
        if (root == null) return;

        System.Xml.IXmlLineInfo lineInfo = (System.Xml.IXmlLineInfo)root;
        int rootLine = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

        // 1. Model Mesh attribute
        string? modelFile = root.Attribute("high")?.Value ??
                            root.Attribute("file")?.Value ??
                            root.Attribute("med")?.Value ??
                            root.Attribute("low")?.Value;

        if (!string.IsNullOrWhiteSpace(modelFile))
        {
            references.Add(new VfsAssetReference
            {
                VirtualPath = modelFile,
                DeclaringFile = relPath,
                LineNumber = rootLine,
                AssetType = "Model",
                Context = root.Attribute("name")?.Value ?? "root_model",
                IsFatalIfMissing = true
            });
        }

        // 2. Attachments
        foreach (XElement attach in root.Elements("attachment"))
        {
            string? attachModel = attach.Attribute("model")?.Value ?? attach.Attribute("file")?.Value;
            System.Xml.IXmlLineInfo aLine = (System.Xml.IXmlLineInfo)attach;
            if (!string.IsNullOrWhiteSpace(attachModel))
            {
                references.Add(new VfsAssetReference
                {
                    VirtualPath = attachModel,
                    DeclaringFile = relPath,
                    LineNumber = aLine.HasLineInfo() ? aLine.LineNumber : rootLine,
                    AssetType = "Model",
                    Context = $"attachment_{attach.Attribute("bone")?.Value ?? "bone"}",
                    IsFatalIfMissing = false
                });
            }
        }

        // 3. Animation Clips
        foreach (XElement anim in root.Elements("anim"))
        {
            string? clip = anim.Attribute("clip")?.Value;
            string animName = anim.Attribute("name")?.Value ?? "anim";
            System.Xml.IXmlLineInfo anLine = (System.Xml.IXmlLineInfo)anim;

            if (!string.IsNullOrWhiteSpace(clip))
            {
                bool isCriticalAnim = animName.StartsWith("idle", StringComparison.OrdinalIgnoreCase) ||
                                      animName.StartsWith("store", StringComparison.OrdinalIgnoreCase) ||
                                      animName.StartsWith("default", StringComparison.OrdinalIgnoreCase);

                references.Add(new VfsAssetReference
                {
                    VirtualPath = clip,
                    DeclaringFile = relPath,
                    LineNumber = anLine.HasLineInfo() ? anLine.LineNumber : rootLine,
                    AssetType = "Clip",
                    Context = animName,
                    IsFatalIfMissing = isCriticalAnim
                });
            }
        }
    }

    private static void ExtractFromMaterial(string fullPath, string relPath, string content, List<VfsAssetReference> references)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        string sanitized = content;
        if (sanitized.StartsWith('\uFEFF')) sanitized = sanitized[1..];

        XDocument doc;
        try { doc = XDocument.Parse(sanitized, LoadOptions.SetLineInfo); }
        catch { return; }

        if (doc.Root == null) return;

        // 1. 2D Samplers
        foreach (XElement sampler in doc.Root.Descendants("sampler"))
        {
            string? texture = sampler.Attribute("texture")?.Value;
            string samplerName = sampler.Attribute("name")?.Value ?? "diffuse";
            System.Xml.IXmlLineInfo lineInfo = (System.Xml.IXmlLineInfo)sampler;
            int line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

            if (!string.IsNullOrWhiteSpace(texture) && !IsIgnoredTexture(texture))
            {
                bool isFatal = string.Equals(samplerName, "diffuse", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(samplerName, "color", StringComparison.OrdinalIgnoreCase);

                references.Add(new VfsAssetReference
                {
                    VirtualPath = texture,
                    DeclaringFile = relPath,
                    LineNumber = line,
                    AssetType = "Sampler",
                    Context = samplerName,
                    IsFatalIfMissing = isFatal
                });
            }
        }

        // 2. Cubemaps
        foreach (XElement cube in doc.Root.Descendants("samplercube"))
        {
            string? texture = cube.Attribute("texture")?.Value;
            System.Xml.IXmlLineInfo lineInfo = (System.Xml.IXmlLineInfo)cube;
            int line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

            if (!string.IsNullOrWhiteSpace(texture) && !IsIgnoredTexture(texture))
            {
                references.Add(new VfsAssetReference
                {
                    VirtualPath = texture,
                    DeclaringFile = relPath,
                    LineNumber = line,
                    AssetType = "Cubemap",
                    Context = cube.Attribute("name")?.Value ?? "cube",
                    IsFatalIfMissing = false
                });
            }
        }
    }

    private static void ExtractFromHeroesPreview(string fullPath, string relPath, string content, List<VfsAssetReference> references)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        string sanitized = content;
        if (sanitized.StartsWith('\uFEFF')) sanitized = sanitized[1..];

        XDocument doc;
        try { doc = XDocument.Parse(sanitized, LoadOptions.SetLineInfo); }
        catch { return; }

        if (doc.Root == null) return;

        foreach (XElement hero in doc.Root.Elements("hero"))
        {
            string heroName = hero.Attribute("name")?.Value ?? string.Empty;
            if (string.Equals(heroName, "Default_Values", StringComparison.OrdinalIgnoreCase)) continue;

            IEnumerable<XElement> avatars = hero.Elements("altavatar").Concat(hero.Elements("avatar"));
            foreach (XElement av in avatars)
            {
                System.Xml.IXmlLineInfo lineInfo = (System.Xml.IXmlLineInfo)av;
                int line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;
                string avKey = av.Attribute("key")?.Value ?? av.Attribute("name")?.Value ?? heroName;

                string? modelPath = av.Attribute("modelPath")?.Value ??
                                    av.Attribute("preview_mdf")?.Value ??
                                    av.Attribute("previewMdf")?.Value ??
                                    av.Attribute("model")?.Value;

                if (!string.IsNullOrWhiteSpace(modelPath))
                {
                    references.Add(new VfsAssetReference
                    {
                        VirtualPath = modelPath,
                        DeclaringFile = relPath,
                        LineNumber = line,
                        AssetType = "PreviewMdf",
                        Context = $"{heroName}:{avKey}",
                        IsFatalIfMissing = true
                    });
                }

                string? icon = av.Attribute("icon")?.Value ?? av.Attribute("iconPath")?.Value;
                if (!string.IsNullOrWhiteSpace(icon) && !IsIgnoredTexture(icon))
                {
                    references.Add(new VfsAssetReference
                    {
                        VirtualPath = icon,
                        DeclaringFile = relPath,
                        LineNumber = line,
                        AssetType = "Texture",
                        Context = $"{heroName}:{avKey}:icon",
                        IsFatalIfMissing = true
                    });
                }
            }
        }
    }

    private static bool IsIgnoredTexture(string texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return true;
        string trimmed = texture.Trim();
        if (EngineVirtualTokens.Contains(trimmed)) return true;

        // Ignore unresolved dynamic template parameters e.g. "{hero}", "{icon}"
        if (ParamPlaceholderRegex.IsMatch(trimmed)) return true;

        return false;
    }

    #endregion

    #region Helper Methods

    private static List<string> DiscoverTargetFiles(string repoRoot, string clientDir, VfsCompletenessOptions options)
    {
        List<string> files = [];
        string scope = options.Scope.ToLowerInvariant();

        List<string> candidateDirs =
        [
            Path.Combine(clientDir, "override"),
            Path.Combine(clientDir, "game"),
            Path.Combine(clientDir, "ClientPackageFiles"),
            Path.Combine(repoRoot, "heroes"),
            Path.Combine(repoRoot, "games", "shared")
        ];

        if (!string.IsNullOrWhiteSpace(options.TargetPath) && !options.TargetPath.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string targetResolved = Path.IsPathRooted(options.TargetPath)
                ? options.TargetPath
                : Path.Combine(repoRoot, options.TargetPath);

            if (File.Exists(targetResolved)) return [targetResolved];
            if (Directory.Exists(targetResolved))
            {
                candidateDirs = [targetResolved];
            }
            else
            {
                List<string> matched = candidateDirs
                    .Select(d => Path.Combine(d, options.TargetPath.TrimStart('/', '\\')))
                    .Where(d => Directory.Exists(d) || File.Exists(d))
                    .ToList();

                if (matched.Any(File.Exists))
                {
                    return matched.Where(File.Exists).ToList();
                }

                if (matched.Count > 0)
                {
                    candidateDirs = matched;
                }
            }
        }

        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            if (scope is "all" or "ui")
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.package", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(dir, "*.interface", SearchOption.AllDirectories));
            }

            if (scope is "all" or "models")
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.mdf", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(dir, "heroes_preview.xml", SearchOption.AllDirectories));
            }

            if (scope is "all" or "materials")
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.material", SearchOption.AllDirectories));
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string StripVfsMountPrefix(string relPath)
    {
        string norm = relPath.Replace('\\', '/').TrimStart('/');

        string[] prefixes =
        [
            "games/client/override/",
            "games/client/game/",
            "games/client/ClientPackageFiles/",
            "games/client/KONGOR/updates/",
            "games/client/KONGOR/extensions/",
            "games/shared/override/",
            "games/shared/",
            "override/",
            "game/",
            "ClientPackageFiles/"
        ];

        foreach (string pfx in prefixes)
        {
            if (norm.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            {
                return norm[pfx.Length..];
            }
        }

        return norm;
    }

    private static string FindRepoRoot()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            return ws.Root;
        }

        return Directory.GetCurrentDirectory();
    }

    #endregion
}
