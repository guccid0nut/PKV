// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.K2;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Diagnostics;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_inspect_model_mdf' (alias: 'inspect_model_mdf').
/// Provides deep single-hero / avatar asset forensics: parses the .mdf XML manifest,
/// decodes binary .model and .clip file headers, extracts skeletal bone hierarchies,
/// and performs bone-by-bone cross-validation to pinpoint animation desyncs and Utah teapots.
/// </summary>
public sealed class InspectModelMdfHandler
{
    private readonly IPreviewValidationService _validationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public InspectModelMdfHandler(
        IPreviewValidationService? validationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _validationService = validationService ?? new PreviewValidationService(
            timeProvider: _timeProvider,
            logger: logger);
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string? path = null;
        if ((arguments.TryGetProperty("path", out JsonElement pEl) ||
             arguments.TryGetProperty("mdf_path", out pEl) ||
             arguments.TryGetProperty("mdfPath", out pEl)) &&
            !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            path = pEl.GetString()!.Trim();
        }

        string? hero = null;
        if ((arguments.TryGetProperty("hero", out JsonElement hEl) ||
             arguments.TryGetProperty("hero_name", out hEl)) &&
            !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            hero = hEl.GetString()!.Trim();
        }

        string alt = "default";
        if ((arguments.TryGetProperty("alt", out JsonElement aEl) ||
             arguments.TryGetProperty("alt_name", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            alt = aEl.GetString()!.Trim();
        }

        // If path not directly supplied, formulate from hero and alt
        List<string> candidatePaths = [];
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(hero))
        {
            string cleanHero = hero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
                ? hero.Substring("Hero_".Length).ToLowerInvariant()
                : hero.ToLowerInvariant();
            if (cleanHero == "fayde")
            {
                cleanHero = "fade";
            }

            string cleanAlt = alt.ToLowerInvariant();
            if (cleanAlt is "default" or "base")
            {
                path = $"heroes/{cleanHero}/preview.mdf";
                candidatePaths.Add($"heroes/{cleanHero}/preview.mdf");
                candidatePaths.Add($"heroes/{cleanHero}/model.mdf");
                candidatePaths.Add($"heroes/{cleanHero}/hero.mdf");
            }
            else
            {
                path = $"heroes/{cleanHero}/{cleanAlt}/preview.mdf";
                candidatePaths.Add($"heroes/{cleanHero}/{cleanAlt}/preview.mdf");
                candidatePaths.Add($"heroes/{cleanHero}/{cleanAlt}/model.mdf");
                candidatePaths.Add($"heroes/{cleanHero}/{cleanAlt}/hero.mdf");
            }
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            candidatePaths.Add(path);
        }

        if ((arguments.TryGetProperty("archive_sample_path", out JsonElement archEl) ||
             arguments.TryGetProperty("scan_archive_models", out archEl)) &&
            archEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(archEl.GetString()))
        {
            string archiveSamplePath = archEl.GetString()!.Trim();
            int maxModels = 50;
            if (arguments.TryGetProperty("max_models", out JsonElement maxEl) && maxEl.TryGetInt32(out int parsedMax) && parsedMax > 0)
            {
                maxModels = parsedMax;
            }

            string? resolvedArchive = File.Exists(archiveSamplePath)
                ? archiveSamplePath
                : _validationService.ResolveVfsPath(archiveSamplePath) ?? archiveSamplePath;

            K2ModelMeshIntegrityAuditor.ArchiveModelEmpiricalAuditSummary empiricalSummary =
                K2ModelMeshIntegrityAuditor.ScanArchiveModelsEmpiricalSample(resolvedArchive, maxModels);
            sw.Stop();
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "success",
                success = true,
                action = "scan_archive_models",
                empiricalSummary,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: false));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(ToolCallResult.Error("Missing required parameter: specify either 'path' (path to .mdf), 'hero' (e.g. 'Hero_Fayde'), or 'archive_sample_path' (path to .s2z)."));
        }

        string? physicalMdfPath = null;
        foreach (string cand in candidatePaths)
        {
            string? resolved = _validationService.ResolveVfsPath(cand);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                physicalMdfPath = resolved;
                path = cand;
                break;
            }
        }

        if ((physicalMdfPath == null || !File.Exists(physicalMdfPath)) && !GameServerManageHandler.IsTestExecution)
        {
            foreach (string cand in candidatePaths)
            {
                string? extracted = TryExtractMdfDirectoryFromArchives(cand);
                if (!string.IsNullOrWhiteSpace(extracted) && File.Exists(extracted))
                {
                    physicalMdfPath = extracted;
                    path = cand;
                    break;
                }
            }
        }

        if (physicalMdfPath == null || !File.Exists(physicalMdfPath))
        {
            sw.Stop();
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                path,
                searchedCandidates = candidatePaths,
                error = $"MDF file '{path}' could not be resolved across any K2 VFS directory mounts or .s2z archives.",
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true));
        }

        try
        {
            K2Mdf mdf;
            using (FileStream fs = new FileStream(physicalMdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                mdf = K2MdfReader.Read(fs);
            }

            string mdfDir = Path.GetDirectoryName(physicalMdfPath) ?? string.Empty;
            List<string> diagnostics = [];

            // 1. Inspect Model
            object? modelDetails = null;
            K2Model? k2Model = null;
            ModelIntegrityAuditResult? meshIntegrityAudit = null;

            if (string.IsNullOrWhiteSpace(mdf.ModelFile))
            {
                diagnostics.Add("[ERROR] MDF root does not declare a model file (high/med/file attribute missing).");
            }
            else
            {
                string candidateModel = Path.Combine(mdfDir, mdf.ModelFile);
                if (!File.Exists(candidateModel))
                {
                    candidateModel = _validationService.ResolveVfsPath(mdf.ModelFile) ?? candidateModel;
                }

                bool modelExists = File.Exists(candidateModel);
                if (!modelExists)
                {
                    diagnostics.Add($"[ERROR] Referenced model file '{mdf.ModelFile}' does not exist on disk (Utah teapot guaranteed).");
                }
                else
                {
                    try
                    {
                        using FileStream mStream = new FileStream(candidateModel, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        k2Model = K2ModelReader.Read(mStream);
                        meshIntegrityAudit = K2ModelMeshIntegrityAuditor.AuditModel(k2Model);
                        if (!meshIntegrityAudit.IsRenderSafe)
                        {
                            diagnostics.Add($"[ERROR] Model '{mdf.ModelFile}' failed mesh integrity audit: outOfBoundsFaces={meshIntegrityAudit.TotalOutOfBoundsFaces}, outOfBoundsBoneLinks={meshIntegrityAudit.TotalOutOfBoundsBoneLinks}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"[WARN] Failed to parse model binary '{mdf.ModelFile}': {ex.Message}");
                    }
                }

                modelDetails = new
                {
                    file = mdf.ModelFile,
                    exists = modelExists,
                    physicalPath = modelExists ? candidateModel : null,
                    meshCount = k2Model?.Meshes.Count ?? 0,
                    meshes = k2Model?.Meshes.Select(m =>
                    {
                        K2ModelMeshIntegrityAuditor.MeshVertexCacheAndOrphanMetrics orphanAcmr =
                            K2ModelMeshIntegrityAuditor.ComputeMeshOrphanVerticesAndAcmr(
                                m.Vertices?.Length ?? 0,
                                m.Indices ?? Array.Empty<int>());
                        return new
                        {
                            name = m.Name,
                            material = m.DefaultMaterial,
                            vertexCount = m.NumVertices,
                            faceCount = m.NumFaces,
                            orphanVertexCount = orphanAcmr.OrphanedVertexCount,
                            orphanRatio = orphanAcmr.OrphanedVertexRatio,
                            acmr = orphanAcmr.Acmr,
                            hasExcessiveCacheMisses = orphanAcmr.HasExcessiveCacheMisses
                        };
                    }),
                    boneCount = k2Model?.Bones.Count ?? 0,
                    bones = k2Model?.Bones.Select((b, idx) => new
                    {
                        index = idx,
                        name = b.Name,
                        parentIndex = b.ParentIndex
                    }),
                    meshIntegrityAudit
                };
            }

            // 2. Inspect Animations
            List<object> animDetails = new List<object>();
            foreach (K2MdfAnim anim in mdf.Animations)
            {
                string candidateClip = Path.Combine(mdfDir, anim.ClipPath);
                if (!File.Exists(candidateClip))
                {
                    candidateClip = _validationService.ResolveVfsPath(anim.ClipPath) ?? candidateClip;
                }

                bool clipExists = File.Exists(candidateClip);
                int? clipBones = null;
                int? clipFrames = null;
                bool boneMatch = true;

                if (!clipExists)
                {
                    diagnostics.Add($"[WARN] Animation '{anim.Name}' points to non-existent clip '{anim.ClipPath}'.");
                }
                else
                {
                    try
                    {
                        using FileStream cStream = new FileStream(candidateClip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        K2Clip clip = K2ClipReader.Read(cStream, anim.Name);
                        clipBones = clip.NumBones;
                        clipFrames = clip.NumFrames;

                        if (k2Model != null && k2Model.Bones.Count > 0 && clip.NumBones > 0)
                        {
                            if (clip.NumBones != k2Model.Bones.Count)
                            {
                                boneMatch = false;
                                diagnostics.Add($"[SKELETON MISMATCH] Anim '{anim.Name}' clip has {clip.NumBones} bones, but model has {k2Model.Bones.Count} bones.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"[WARN] Failed to parse clip binary '{anim.ClipPath}': {ex.Message}");
                    }
                }

                animDetails.Add(new
                {
                    name = anim.Name,
                    clipPath = anim.ClipPath,
                    exists = clipExists,
                    fps = anim.Fps,
                    loop = anim.Loop,
                    frames = clipFrames,
                    clipBones,
                    boneMatch
                });
            }

            sw.Stop();
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = diagnostics.Any(d => d.StartsWith("[ERROR]")) ? "teapot_risk" : "success",
                success = true,
                path,
                physicalPath = physicalMdfPath,
                model = modelDetails,
                animationCount = mdf.Animations.Count,
                animations = animDetails,
                attachmentCount = mdf.Attachments.Count,
                attachments = mdf.Attachments.Select(a => new { bone = a.Bone, model = a.ModelPath }),
                diagnosticsCount = diagnostics.Count,
                diagnostics,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Invoke($"[InspectModelMdfHandler] Forensics failed: {ex.Message}");
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                path,
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true));
        }
    }

    private static string? TryExtractMdfDirectoryFromArchives(string vfsPath)
    {
        try
        {
            string normalizedVfs = vfsPath.Replace('\\', '/').TrimStart('/');
            string vfsDirPrefix = (Path.GetDirectoryName(normalizedVfs) ?? string.Empty).Replace('\\', '/');
            if (!string.IsNullOrEmpty(vfsDirPrefix) && !vfsDirPrefix.EndsWith('/'))
            {
                vfsDirPrefix += "/";
            }

            string cacheRoot = Path.Combine(Path.GetTempPath(), "kongor_mdf_cache");
            string cachedMdfPath = Path.Combine(cacheRoot, normalizedVfs.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cachedMdfPath))
            {
                return cachedMdfPath;
            }

            string cwd = Directory.GetCurrentDirectory();
            string gameDir = Path.Combine(cwd, "games", "client", "game");
            List<string> archiveCandidates = [];
            if (Directory.Exists(gameDir))
            {
                archiveCandidates.AddRange(
                    Directory.GetFiles(gameDir, "resources*.s2z", SearchOption.TopDirectoryOnly)
                        .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
            }
            string kongorArchive = Path.Combine(cwd, "games", "client", "KONGOR", "resources0.s2z");
            if (File.Exists(kongorArchive))
            {
                archiveCandidates.Add(kongorArchive);
            }

            foreach (string archivePath in archiveCandidates)
            {
                if (!File.Exists(archivePath)) continue;
                using FileStream fs = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);

                ZipArchiveEntry? mdfEntry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.Replace('\\', '/'), normalizedVfs, StringComparison.OrdinalIgnoreCase));
                if (mdfEntry == null) continue;

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string entryNorm = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (!entryNorm.StartsWith(vfsDirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext is not (".mdf" or ".model" or ".clip" or ".material")) continue;

                    string outFile = Path.Combine(cacheRoot, entryNorm.Replace('/', Path.DirectorySeparatorChar));
                    string? outDir = Path.GetDirectoryName(outFile);
                    if (!string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                    }
                    entry.ExtractToFile(outFile, overwrite: true);
                }

                if (File.Exists(cachedMdfPath))
                {
                    return cachedMdfPath;
                }
            }
        }
        catch
        {
            // Fall through to null
        }

        return null;
    }
}
