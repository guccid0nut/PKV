// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ASPIRE.Common.K2;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Pre-flight asset validation service for K2 hero preview models and animations.
/// Statically analyzes heroes_preview.xml, traverses the VFS dependency DAG (.mdf -> .model -> .clip),
/// and detects missing files, corrupt references, and skeletal bone mismatches before client boot,
/// eliminating the Utah teapot trial-and-error cycle.
/// </summary>
public sealed class PreviewValidationService : IPreviewValidationService
{
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    private readonly string[] _vfsPriorityRoots;

    public PreviewValidationService(
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        _vfsPriorityRoots = [
            Path.Combine(_repoRoot, "games", "client", "override"),
            Path.Combine(_repoRoot, "games", "client", "ClientPackageFiles"),
            Path.Combine(_repoRoot, "games", "client", "game"),
            Path.Combine(_repoRoot, "games", "server", "override"),
            Path.Combine(_repoRoot, "games", "server", "game"),
            Path.Combine(_repoRoot, "games", "shared", "override"),
            Path.Combine(_repoRoot, "games", "shared"),
            Path.Combine(_repoRoot, "heroes")
        ];
    }

    public string? ResolveVfsPath(string vfsPath)
    {
        if (string.IsNullOrWhiteSpace(vfsPath)) return null;

        string normalized = vfsPath.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            normalized = normalized[1..];
        }

        foreach (string root in _vfsPriorityRoots)
        {
            string candidate = Path.Combine(root, normalized);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Check if path is already rooted or direct relative
        string direct = Path.Combine(_repoRoot, normalized);
        if (File.Exists(direct))
        {
            return direct;
        }

        return null;
    }

    public async Task<PreviewValidationResultDto> ValidatePreviewsAsync(PreviewValidationOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PreviewValidationOptions();
        Stopwatch sw = Stopwatch.StartNew();

        var result = new PreviewValidationResultDto
        {
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        string? previewXmlPath = ResolveVfsPath("heroes_preview.xml") ??
                                 Path.Combine(_repoRoot, "games", "client", "override", "heroes_preview.xml");

        if (!File.Exists(previewXmlPath))
        {
            previewXmlPath = Path.Combine(_repoRoot, "games", "client", "game", "heroes_preview.xml");
        }

        if (!File.Exists(previewXmlPath))
        {
            sw.Stop();
            result.Status = "error";
            result.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            result.Issues.Add(new PreviewValidationIssueDto
            {
                Severity = "ERROR",
                IssueType = "MISSING_PREVIEW_XML",
                Description = $"heroes_preview.xml not found across any K2 VFS directory mounts."
            });
            return result;
        }

        _logger?.Invoke($"[PreviewValidator] Parsing '{previewXmlPath}'...");

        XDocument doc;
        using (var fs = new FileStream(previewXmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            doc = await XDocument.LoadAsync(fs, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }

        XElement? root = doc.Root;
        if (root == null)
        {
            sw.Stop();
            result.Status = "empty";
            result.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            return result;
        }

        var heroElements = root.Elements("hero");
        HashSet<string> inspectedMdfs = new(StringComparer.OrdinalIgnoreCase);

        foreach (var heroEl in heroElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string heroName = heroEl.Attribute("name")?.Value ?? string.Empty;
            if (string.Equals(heroName, "Default_Values", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(options.HeroFilter))
            {
                string filter = options.HeroFilter.Trim();
                if (!heroName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            result.TotalHeroesChecked++;

            var altElements = System.Linq.Enumerable.Concat(heroEl.Elements("altavatar"), heroEl.Elements("avatar"));
            foreach (var altEl in altElements)
            {
                result.TotalAvatarsChecked++;

                string altKey = altEl.Attribute("key")?.Value ?? altEl.Attribute("name")?.Value ?? heroName;
                string altType = altEl.Attribute("type")?.Value ?? "default";
                string? modelPath = altEl.Attribute("modelPath")?.Value ??
                                    altEl.Attribute("preview_mdf")?.Value ??
                                    altEl.Attribute("previewMdf")?.Value ??
                                    altEl.Attribute("model")?.Value;

                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    continue;
                }

                // Deduplicate repetitive checks across small/middle/large if identical MDF
                string mdfKey = $"{heroName}|{modelPath}";
                if (inspectedMdfs.Contains(mdfKey))
                {
                    continue;
                }
                inspectedMdfs.Add(mdfKey);

                string? resolvedMdf = ResolveVfsPath(modelPath);
                if (resolvedMdf == null)
                {
                    result.TeapotRiskCount++;
                    result.Issues.Add(new PreviewValidationIssueDto
                    {
                        HeroName = heroName,
                        AltKey = altKey,
                        AvatarType = altType,
                        MdfPath = modelPath,
                        Severity = "ERROR",
                        IssueType = "MISSING_MDF",
                        MissingFile = modelPath,
                        Description = $"MDF file '{modelPath}' does not exist on disk. Client will display a Utah teapot."
                    });
                    continue;
                }

                // MDF exists. Parse and inspect model & clips
                ValidateMdfContent(heroName, altKey, altType, modelPath, resolvedMdf, options, result);
            }

            if (result.Issues.Count >= options.Limit)
            {
                break;
            }
        }

        sw.Stop();
        result.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
        result.HasTeapotRisk = result.TeapotRiskCount > 0;
        result.Status = result.HasTeapotRisk ? "teapot_risk" : (result.WarningsCount > 0 ? "warnings" : "clean");

        _logger?.Invoke($"[PreviewValidator] Completed validation across {result.TotalHeroesChecked} heroes ({result.TotalAvatarsChecked} avatars) in {result.ElapsedMs}ms. Teapot risks: {result.TeapotRiskCount}, Warnings: {result.WarningsCount}");
        return result;
    }

    private void ValidateMdfContent(
        string heroName,
        string altKey,
        string altType,
        string vfsMdfPath,
        string physicalMdfPath,
        PreviewValidationOptions options,
        PreviewValidationResultDto result)
    {
        try
        {
            K2Mdf mdf;
            using (var mdfStream = new FileStream(physicalMdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                mdf = K2MdfReader.Read(mdfStream);
            }

            string mdfDir = Path.GetDirectoryName(physicalMdfPath) ?? string.Empty;
            string vfsMdfDir = Path.GetDirectoryName(vfsMdfPath)?.Replace('\\', '/') ?? string.Empty;

            // 1. Verify Model Mesh File
            if (string.IsNullOrWhiteSpace(mdf.ModelFile))
            {
                result.TeapotRiskCount++;
                result.Issues.Add(new PreviewValidationIssueDto
                {
                    HeroName = heroName,
                    AltKey = altKey,
                    AvatarType = altType,
                    MdfPath = vfsMdfPath,
                    Severity = "ERROR",
                    IssueType = "MISSING_MODEL_ATTR",
                    Description = $"MDF file '{vfsMdfPath}' does not specify a model file (high/med/file attribute missing)."
                });
                return;
            }

            string candidateModel1 = Path.Combine(mdfDir, mdf.ModelFile);
            string candidateModel2 = ResolveVfsPath($"{vfsMdfDir}/{mdf.ModelFile}") ?? string.Empty;
            string? physicalModel = File.Exists(candidateModel1) ? candidateModel1 : (File.Exists(candidateModel2) ? candidateModel2 : null);

            if (physicalModel == null)
            {
                result.TeapotRiskCount++;
                result.Issues.Add(new PreviewValidationIssueDto
                {
                    HeroName = heroName,
                    AltKey = altKey,
                    AvatarType = altType,
                    MdfPath = vfsMdfPath,
                    Severity = "ERROR",
                    IssueType = "MISSING_MODEL",
                    MissingFile = $"{vfsMdfDir}/{mdf.ModelFile}",
                    Description = $"Referenced .model mesh '{mdf.ModelFile}' does not exist in MDF directory. Client will display a Utah teapot."
                });
            }

            K2Model? parsedModel = null;
            if (options.StrictBoneCheck && physicalModel != null)
            {
                try
                {
                    using var modelStream = new FileStream(physicalModel, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    parsedModel = K2ModelReader.Read(modelStream);
                }
                catch (Exception ex)
                {
                    result.WarningsCount++;
                    result.Issues.Add(new PreviewValidationIssueDto
                    {
                        HeroName = heroName,
                        AltKey = altKey,
                        AvatarType = altType,
                        MdfPath = vfsMdfPath,
                        Severity = "WARN",
                        IssueType = "MODEL_PARSE_ERROR",
                        Description = $"Failed to parse binary model header in '{physicalModel}': {ex.Message}"
                    });
                }
            }

            // 2. Verify Animation Clips
            bool hasIdleOrStore = false;
            foreach (var anim in mdf.Animations)
            {
                if (string.Equals(anim.Name, "idle", StringComparison.OrdinalIgnoreCase) ||
                    anim.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase) ||
                    anim.Name.StartsWith("default", StringComparison.OrdinalIgnoreCase))
                {
                    hasIdleOrStore = true;
                }

                string candidateClip1 = Path.Combine(mdfDir, anim.ClipPath);
                string candidateClip2 = ResolveVfsPath($"{vfsMdfDir}/{anim.ClipPath}") ?? string.Empty;
                string? physicalClip = File.Exists(candidateClip1) ? candidateClip1 : (File.Exists(candidateClip2) ? candidateClip2 : null);

                if (physicalClip == null)
                {
                    bool isCritical = string.Equals(anim.Name, "idle", StringComparison.OrdinalIgnoreCase) ||
                                      anim.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase);

                    if (isCritical)
                    {
                        result.TeapotRiskCount++;
                    }
                    else
                    {
                        result.WarningsCount++;
                    }

                    result.Issues.Add(new PreviewValidationIssueDto
                    {
                        HeroName = heroName,
                        AltKey = altKey,
                        AvatarType = altType,
                        MdfPath = vfsMdfPath,
                        Severity = isCritical ? "ERROR" : "WARN",
                        IssueType = "MISSING_CLIP",
                        MissingFile = $"{vfsMdfDir}/{anim.ClipPath}",
                        Description = $"Animation '{anim.Name}' points to missing clip '{anim.ClipPath}'. {(isCritical ? "Will trigger Utah teapot or crash in store preview." : "Will cause animation freeze during gameplay.")}"
                    });
                }
                else if (options.StrictBoneCheck && parsedModel != null)
                {
                    try
                    {
                        using var clipStream = new FileStream(physicalClip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var clip = K2ClipReader.Read(clipStream, anim.Name);

                        if (clip.NumBones > 0 && parsedModel.Bones.Count > 0 && clip.NumBones != parsedModel.Bones.Count)
                        {
                            result.WarningsCount++;
                            result.Issues.Add(new PreviewValidationIssueDto
                            {
                                HeroName = heroName,
                                AltKey = altKey,
                                AvatarType = altType,
                                MdfPath = vfsMdfPath,
                                Severity = "WARN",
                                IssueType = "SKELETAL_MISMATCH",
                                Description = $"Clip '{anim.Name}' has {clip.NumBones} bones, but mesh '{mdf.ModelFile}' has {parsedModel.Bones.Count} bones. May distort or T-pose."
                            });
                        }
                    }
                    catch { }
                }
            }

            if (mdf.Animations.Count > 0 && !hasIdleOrStore)
            {
                result.WarningsCount++;
                result.Issues.Add(new PreviewValidationIssueDto
                {
                    HeroName = heroName,
                    AltKey = altKey,
                    AvatarType = altType,
                    MdfPath = vfsMdfPath,
                    Severity = "WARN",
                    IssueType = "NO_IDLE_ANIMATION",
                    Description = $"MDF has {mdf.Animations.Count} animations, but lacks 'idle' or 'store' animation. Character will stand frozen."
                });
            }
        }
        catch (Exception ex)
        {
            result.WarningsCount++;
            result.Issues.Add(new PreviewValidationIssueDto
            {
                HeroName = heroName,
                AltKey = altKey,
                AvatarType = altType,
                MdfPath = vfsMdfPath,
                Severity = "WARN",
                IssueType = "MDF_PARSE_ERROR",
                Description = $"Failed to parse MDF file '{physicalMdfPath}': {ex.Message}"
            });
        }
    }

    private static string FindRepoRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "games")) &&
                Directory.Exists(Path.Combine(current, "src")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
