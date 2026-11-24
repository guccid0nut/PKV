// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Numerics;
using ASPIRE.Common.K2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 12 (#148: imhex-3d-mesh-oob-and-normal-auditor):
/// Pre-flight K2 .model (SMDL) mesh integrity auditor adapted from ImHex 3d_model.cpp
/// (processIndexedMesh / calculateNormals / calculateBoundingBox — including the upstream Z-axis
/// bounding box fix). Detects out-of-bounds triangle indices that crash Vulkan / D3D9 draw calls,
/// degenerate zero-area faces, non-finite or zero-length vertex normals, and synthesizes
/// area-weighted vertex normals and full 3D (X,Y,Z) auto-scale bounding boxes.
/// </summary>
public sealed record OutOfBoundsFaceDiagnostic(
    int MeshIndex,
    string MeshName,
    int FaceIndex,
    int IndexA,
    int IndexB,
    int IndexC,
    int NumVertices);

public sealed record MeshIntegrityReport(
    int MeshIndex,
    string MeshName,
    int DeclaredVertexCount,
    int ActualVertexCount,
    int DeclaredFaceCount,
    int ValidFaceCount,
    int OutOfBoundsFaceCount,
    int DegenerateZeroAreaFaceCount,
    int InvalidOrZeroNormalCount,
    int OutOfBoundsBoneIndexCount,
    Vector3 ComputedMinBounds,
    Vector3 ComputedMaxBounds,
    Vector3 ComputedAutoScaleCenter,
    float ComputedMaxExtent,
    bool IsRenderSafe,
    IReadOnlyList<OutOfBoundsFaceDiagnostic> OutOfBoundsFaces);

public sealed record ModelIntegrityAuditResult(
    int Version,
    int DeclaredMeshCount,
    int ActualMeshCount,
    int TotalVertices,
    int TotalFaces,
    int TotalOutOfBoundsFaces,
    int TotalDegenerateFaces,
    int TotalInvalidNormals,
    int TotalOutOfBoundsBoneLinks,
    Vector3 GlobalMinBounds,
    Vector3 GlobalMaxBounds,
    Vector3 GlobalCenter,
    float RecommendedAutoScaleFactor,
    bool IsRenderSafe,
    IReadOnlyList<MeshIntegrityReport> MeshReports);

public static class K2ModelMeshIntegrityAuditor
{
    private static readonly ILogger s_logger = NullLogger.Instance;

    public static ModelIntegrityAuditResult AuditModel(K2Model model, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ILogger activeLogger = logger ?? s_logger;

        List<MeshIntegrityReport> meshReports = new List<MeshIntegrityReport>(model.Meshes.Count);
        int totalVertices = 0;
        int totalFaces = 0;
        int totalOobFaces = 0;
        int totalDegenerate = 0;
        int totalInvalidNormals = 0;
        int totalOobBones = 0;

        Vector3 globalMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 globalMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool hasAnyFiniteVertex = false;

        for (int m = 0; m < model.Meshes.Count; m++)
        {
            K2Mesh mesh = model.Meshes[m];
            MeshIntegrityReport report = AuditMesh(mesh, model.Bones.Count);
            meshReports.Add(report);

            totalVertices += report.ActualVertexCount;
            totalFaces += report.DeclaredFaceCount;
            totalOobFaces += report.OutOfBoundsFaceCount;
            totalDegenerate += report.DegenerateZeroAreaFaceCount;
            totalInvalidNormals += report.InvalidOrZeroNormalCount;
            totalOobBones += report.OutOfBoundsBoneIndexCount;

            if (report.ActualVertexCount > 0)
            {
                globalMin = Vector3.Min(globalMin, report.ComputedMinBounds);
                globalMax = Vector3.Max(globalMax, report.ComputedMaxBounds);
                hasAnyFiniteVertex = true;
            }
        }

        if (!hasAnyFiniteVertex)
        {
            globalMin = Vector3.Zero;
            globalMax = Vector3.Zero;
        }

        Vector3 globalCenter = (globalMin + globalMax) * 0.5f;
        Vector3 globalExtentVec = globalMax - globalMin;
        // Full 3D (X, Y, Z) max extent — fixes ImHex's 2D-only max(X,Y) bug in calculateBoundingBox()
        float maxExtent = MathF.Max(globalExtentVec.X, MathF.Max(globalExtentVec.Y, globalExtentVec.Z));
        float recommendedScale = maxExtent > 1e-6f ? (1.0f / maxExtent) : 1.0f;
        bool isRenderSafe = totalOobFaces == 0 && totalOobBones == 0;

        activeLogger.LogDebug(
            "[K2ModelAuditor] meshes={Meshes} verts={Verts} faces={Faces} oobFaces={OobFaces} degenFaces={Degen} invalidNormals={Normals} oobBones={OobBones} safe={Safe}",
            model.Meshes.Count,
            totalVertices,
            totalFaces,
            totalOobFaces,
            totalDegenerate,
            totalInvalidNormals,
            totalOobBones,
            isRenderSafe);

        return new ModelIntegrityAuditResult(
            model.Version,
            model.NumMeshes,
            model.Meshes.Count,
            totalVertices,
            totalFaces,
            totalOobFaces,
            totalDegenerate,
            totalInvalidNormals,
            totalOobBones,
            globalMin,
            globalMax,
            globalCenter,
            recommendedScale,
            isRenderSafe,
            meshReports);
    }

    public static MeshIntegrityReport AuditMesh(K2Mesh mesh, int modelBoneCount = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        Vector3[] vertices = mesh.Vertices ?? Array.Empty<Vector3>();
        Vector3[] normals = mesh.Normals ?? Array.Empty<Vector3>();
        int[] indices = mesh.Indices ?? Array.Empty<int>();
        int numVerts = vertices.Length;
        int numFaces = indices.Length / 3;

        Vector3 minBounds = numVerts > 0
            ? new Vector3(float.MaxValue, float.MaxValue, float.MaxValue)
            : Vector3.Zero;
        Vector3 maxBounds = numVerts > 0
            ? new Vector3(float.MinValue, float.MinValue, float.MinValue)
            : Vector3.Zero;

        for (int v = 0; v < numVerts; v++)
        {
            Vector3 pos = vertices[v];
            if (float.IsFinite(pos.X) && float.IsFinite(pos.Y) && float.IsFinite(pos.Z))
            {
                minBounds = Vector3.Min(minBounds, pos);
                maxBounds = Vector3.Max(maxBounds, pos);
            }
        }

        int invalidNormalCount = 0;
        if (normals.Length < numVerts)
        {
            invalidNormalCount += (numVerts - normals.Length);
        }

        int checkNormals = Math.Min(numVerts, normals.Length);
        for (int n = 0; n < checkNormals; n++)
        {
            Vector3 nrm = normals[n];
            float lenSq = nrm.LengthSquared();
            if (!float.IsFinite(lenSq) || lenSq < 1e-6f)
            {
                invalidNormalCount++;
            }
        }

        List<OutOfBoundsFaceDiagnostic> oobFaces = new List<OutOfBoundsFaceDiagnostic>();
        int validFaceCount = 0;
        int oobFaceCount = 0;
        int degenerateFaceCount = 0;

        for (int f = 0; f < numFaces; f++)
        {
            int idxA = indices[f * 3];
            int idxB = indices[(f * 3) + 1];
            int idxC = indices[(f * 3) + 2];

            if (idxA < 0 || idxA >= numVerts ||
                idxB < 0 || idxB >= numVerts ||
                idxC < 0 || idxC >= numVerts)
            {
                oobFaceCount++;
                if (oobFaces.Count < 32)
                {
                    oobFaces.Add(new OutOfBoundsFaceDiagnostic(
                        mesh.Index,
                        mesh.Name ?? string.Empty,
                        f,
                        idxA,
                        idxB,
                        idxC,
                        numVerts));
                }
                continue;
            }

            if (idxA == idxB || idxB == idxC || idxA == idxC)
            {
                degenerateFaceCount++;
                continue;
            }

            Vector3 v0 = vertices[idxA];
            Vector3 v1 = vertices[idxB];
            Vector3 v2 = vertices[idxC];
            Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0);
            if (!float.IsFinite(cross.LengthSquared()) || cross.LengthSquared() < 1e-12f)
            {
                degenerateFaceCount++;
                continue;
            }

            validFaceCount++;
        }

        int oobBoneCount = 0;
        if (mesh.BoneLinks != null && modelBoneCount > 0)
        {
            for (int b = 0; b < mesh.BoneLinks.Length; b++)
            {
                K2VertexBoneLink link = mesh.BoneLinks[b];
                if ((link.Weight0 > 0f && (link.BoneIndex0 < 0 || link.BoneIndex0 >= modelBoneCount)) ||
                    (link.Weight1 > 0f && (link.BoneIndex1 < 0 || link.BoneIndex1 >= modelBoneCount)) ||
                    (link.Weight2 > 0f && (link.BoneIndex2 < 0 || link.BoneIndex2 >= modelBoneCount)) ||
                    (link.Weight3 > 0f && (link.BoneIndex3 < 0 || link.BoneIndex3 >= modelBoneCount)))
                {
                    oobBoneCount++;
                }
            }
        }

        Vector3 center = (minBounds + maxBounds) * 0.5f;
        Vector3 extentVec = maxBounds - minBounds;
        float maxExtent = MathF.Max(extentVec.X, MathF.Max(extentVec.Y, extentVec.Z));
        bool isSafe = oobFaceCount == 0 && oobBoneCount == 0 && mesh.NumVertices == numVerts;

        return new MeshIntegrityReport(
            mesh.Index,
            mesh.Name ?? string.Empty,
            mesh.NumVertices,
            numVerts,
            mesh.NumFaces,
            validFaceCount,
            oobFaceCount,
            degenerateFaceCount,
            invalidNormalCount,
            oobBoneCount,
            minBounds,
            maxBounds,
            center,
            maxExtent,
            isSafe,
            oobFaces);
    }

    /// <summary>
    /// Synthesizes area-weighted vertex normals for a mesh when its 'nrml' chunk is missing or contains
    /// zero-length normals, skipping out-of-bounds or degenerate triangles safely.
    /// </summary>
    public static Vector3[] SynthesizeVertexNormals(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        Vector3[] normals = new Vector3[vertices.Length];
        int faceCount = indices.Length / 3;

        for (int f = 0; f < faceCount; f++)
        {
            int ia = indices[f * 3];
            int ib = indices[(f * 3) + 1];
            int ic = indices[(f * 3) + 2];

            if ((uint)ia >= (uint)vertices.Length ||
                (uint)ib >= (uint)vertices.Length ||
                (uint)ic >= (uint)vertices.Length)
            {
                continue;
            }

            Vector3 v0 = vertices[ia];
            Vector3 v1 = vertices[ib];
            Vector3 v2 = vertices[ic];
            Vector3 faceNormal = Vector3.Cross(v1 - v0, v2 - v0);
            if (faceNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            normals[ia] += faceNormal;
            normals[ib] += faceNormal;
            normals[ic] += faceNormal;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            float lenSq = normals[i].LengthSquared();
            normals[i] = lenSq > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
        }

        return normals;
    }

    // ========================================================================
    // (#263) meshoptimizer Post-Transform Vertex Cache ACMR & Orphan-Vertex Gate
    // ========================================================================
    public sealed record MeshVertexCacheAndOrphanMetrics(
        int VertexCount,
        int ValidTriangleCount,
        int ReferencedVertexCount,
        int OrphanedVertexCount,
        float OrphanedVertexRatio,
        int CacheMissCount,
        float Acmr,
        bool HasExcessiveCacheMisses);

    public sealed record EmpiricalModelSampleEntry(
        string EntryPath,
        int MeshCount,
        int TotalVertices,
        int TotalTriangles,
        int OrphanedVertices,
        float WorstMeshAcmr,
        bool IsRenderSafe);

    public sealed record ArchiveModelEmpiricalAuditSummary(
        string ArchivePath,
        int ModelsScanned,
        int DistinctHeroFoldersScanned,
        int TotalMeshesScanned,
        int TotalVerticesScanned,
        int TotalTrianglesScanned,
        int TotalOrphanedVertices,
        int ModelsWithOrphanVertices,
        int TotalOutOfBoundsFaces,
        int SkippedMalformedEntries,
        float AverageAcmr,
        float MaxAcmr,
        IReadOnlyList<EmpiricalModelSampleEntry> SampledModels);

    /// <summary>
    /// Computes unreferenced orphan vertices and simulates a 32-entry FIFO post-transform
    /// vertex cache (adapted from zeux/meshoptimizer vcacheanalyzer.cpp) to compute ACMR
    /// (Average Cache Miss Ratio = cache_misses / triangle_count).
    /// </summary>
    public static MeshVertexCacheAndOrphanMetrics ComputeMeshOrphanVerticesAndAcmr(
        int vertexCount,
        ReadOnlySpan<int> indices,
        int fifoCacheSize = 32)
    {
        if (vertexCount <= 0 || indices.Length < 3)
        {
            return new MeshVertexCacheAndOrphanMetrics(
                VertexCount: Math.Max(0, vertexCount),
                ValidTriangleCount: 0,
                ReferencedVertexCount: 0,
                OrphanedVertexCount: Math.Max(0, vertexCount),
                OrphanedVertexRatio: vertexCount > 0 ? 1.0f : 0.0f,
                CacheMissCount: 0,
                Acmr: 0.0f,
                HasExcessiveCacheMisses: false);
        }

        int effectiveCacheSize = Math.Clamp(fifoCacheSize, 4, 128);
        int[] fifoCache = new int[effectiveCacheSize];
        Array.Fill(fifoCache, -1);
        int fifoWriteCursor = 0;

        bool[] referenced = new bool[vertexCount];
        int referencedCount = 0;
        int validTriangles = 0;
        int cacheMisses = 0;
        int faceCount = indices.Length / 3;

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = indices[f * 3];
            int i1 = indices[(f * 3) + 1];
            int i2 = indices[(f * 3) + 2];

            if ((uint)i0 >= (uint)vertexCount ||
                (uint)i1 >= (uint)vertexCount ||
                (uint)i2 >= (uint)vertexCount)
            {
                continue;
            }

            if (i0 == i1 || i1 == i2 || i0 == i2)
            {
                continue;
            }

            validTriangles++;

            int triIndices0 = i0;
            int triIndices1 = i1;
            int triIndices2 = i2;

            for (int corner = 0; corner < 3; corner++)
            {
                int vIdx = corner == 0 ? triIndices0 : (corner == 1 ? triIndices1 : triIndices2);
                if (!referenced[vIdx])
                {
                    referenced[vIdx] = true;
                    referencedCount++;
                }

                bool hit = false;
                for (int c = 0; c < effectiveCacheSize; c++)
                {
                    if (fifoCache[c] == vIdx)
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    cacheMisses++;
                    fifoCache[fifoWriteCursor] = vIdx;
                    fifoWriteCursor = (fifoWriteCursor + 1) % effectiveCacheSize;
                }
            }
        }

        int orphanCount = vertexCount - referencedCount;
        float orphanRatio = (float)orphanCount / vertexCount;
        float acmr = validTriangles > 0 ? (float)cacheMisses / validTriangles : 0.0f;
        bool excessiveMisses = validTriangles >= 32 && acmr > 2.50f;

        return new MeshVertexCacheAndOrphanMetrics(
            VertexCount: vertexCount,
            ValidTriangleCount: validTriangles,
            ReferencedVertexCount: referencedCount,
            OrphanedVertexCount: orphanCount,
            OrphanedVertexRatio: orphanRatio,
            CacheMissCount: cacheMisses,
            Acmr: acmr,
            HasExcessiveCacheMisses: excessiveMisses);
    }

    /// <summary>
    /// (#263, #269) Scans up to <paramref name="maxModels"/> K2 .model assets from a .s2z zip archive
    /// (e.g. games/client/game/resources0.s2z) to gather empirical orphan-vertex, OOB-index, and ACMR metrics.
    /// When <paramref name="distinctHeroFoldersOnly"/> is true, selects at most one primary .model per
    /// distinct heroes/&lt;hero_name&gt;/ folder so all 50 samples come from distinct hero character rigs.
    /// </summary>
    public static ArchiveModelEmpiricalAuditSummary ScanArchiveModelsEmpiricalSample(
        string archivePath,
        int maxModels = 50,
        bool distinctHeroFoldersOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!System.IO.File.Exists(archivePath))
        {
            return new ArchiveModelEmpiricalAuditSummary(
                archivePath, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0f, 0.0f, Array.Empty<EmpiricalModelSampleEntry>());
        }

        int cap = Math.Clamp(maxModels, 1, 500);
        List<EmpiricalModelSampleEntry> samples = new List<EmpiricalModelSampleEntry>(cap);
        HashSet<string> sampledHeroFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int modelsScanned = 0;
        int totalMeshes = 0;
        int totalVerts = 0;
        int totalTris = 0;
        int totalOrphans = 0;
        int modelsWithOrphans = 0;
        int totalOobFaces = 0;
        int skippedMalformedEntries = 0;
        double acmrSum = 0.0;
        int acmrMeshCount = 0;
        float maxAcmr = 0.0f;

        List<string> candidateArchives = new List<string> { archivePath };
        if (distinctHeroFoldersOnly)
        {
            string? archiveDir = System.IO.Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(archiveDir))
            {
                string[] siblingShards = ["resources1.s2z", "resources2.s2z", "resources3.s2z"];
                for (int s = 0; s < siblingShards.Length; s++)
                {
                    string siblingPath = System.IO.Path.Combine(archiveDir, siblingShards[s]);
                    if (!string.Equals(siblingPath, archivePath, StringComparison.OrdinalIgnoreCase) &&
                        System.IO.File.Exists(siblingPath))
                    {
                        candidateArchives.Add(siblingPath);
                    }
                }
            }
        }

        for (int a = 0; a < candidateArchives.Count && modelsScanned < cap; a++)
        {
            string currentArchivePath = candidateArchives[a];
            using System.IO.FileStream fs = new System.IO.FileStream(
                currentArchivePath,
                System.IO.FileMode.Open,
                System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite);
            using System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(
                fs,
                System.IO.Compression.ZipArchiveMode.Read,
                leaveOpen: false);

            foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
            {
                if (modelsScanned >= cap)
                {
                    break;
                }
                if (!entry.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) || entry.Length <= 16)
                {
                    continue;
                }

                string? heroFolder = null;
                if (distinctHeroFoldersOnly)
                {
                    if (!TryExtractHeroFolderName(entry.FullName, out string extractedHero) ||
                        sampledHeroFolders.Contains(extractedHero) ||
                        entry.Length < 1024)
                    {
                        continue;
                    }
                    heroFolder = extractedHero;
                }

                try
                {
                    using System.IO.Stream entryStream = entry.Open();
                    using System.IO.MemoryStream ms = new System.IO.MemoryStream((int)Math.Min(entry.Length, 16 * 1024 * 1024));
                    entryStream.CopyTo(ms);
                    ms.Position = 0;

                    K2Model model = K2ModelReader.Read(ms);
                    ModelIntegrityAuditResult audit = AuditModel(model);

                    int modelOrphans = 0;
                    int modelTris = 0;
                    float worstMeshAcmr = 0.0f;

                    for (int m = 0; m < model.Meshes.Count; m++)
                    {
                        K2Mesh mesh = model.Meshes[m];
                        MeshVertexCacheAndOrphanMetrics vcache = ComputeMeshOrphanVerticesAndAcmr(
                            mesh.Vertices?.Length ?? 0,
                            mesh.Indices ?? Array.Empty<int>());
                        modelOrphans += vcache.OrphanedVertexCount;
                        modelTris += vcache.ValidTriangleCount;
                        if (vcache.ValidTriangleCount > 0)
                        {
                            acmrSum += vcache.Acmr;
                            acmrMeshCount++;
                            if (vcache.Acmr > worstMeshAcmr)
                            {
                                worstMeshAcmr = vcache.Acmr;
                            }
                            if (vcache.Acmr > maxAcmr)
                            {
                                maxAcmr = vcache.Acmr;
                            }
                        }
                    }

                    modelsScanned++;
                    if (heroFolder is not null)
                    {
                        sampledHeroFolders.Add(heroFolder);
                    }
                    totalMeshes += model.Meshes.Count;
                    totalVerts += audit.TotalVertices;
                    totalTris += modelTris;
                    totalOrphans += modelOrphans;
                    totalOobFaces += audit.TotalOutOfBoundsFaces;
                    if (modelOrphans > 0)
                    {
                        modelsWithOrphans++;
                    }

                    samples.Add(new EmpiricalModelSampleEntry(
                        EntryPath: entry.FullName,
                        MeshCount: model.Meshes.Count,
                        TotalVertices: audit.TotalVertices,
                        TotalTriangles: modelTris,
                        OrphanedVertices: modelOrphans,
                        WorstMeshAcmr: (float)Math.Round(worstMeshAcmr, 3),
                        IsRenderSafe: audit.IsRenderSafe));
                }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or ArgumentException)
                {
                    skippedMalformedEntries++;
                    System.Diagnostics.Debug.WriteLine($"[K2ModelMeshIntegrityAuditor] Skipping non-SMDL or truncated model entry '{entry.FullName}' (skippedMalformedEntries={skippedMalformedEntries}): {ex.Message}");
                }
            }
        }

        float avgAcmr = acmrMeshCount > 0 ? (float)Math.Round(acmrSum / acmrMeshCount, 3) : 0.0f;
        return new ArchiveModelEmpiricalAuditSummary(
            ArchivePath: archivePath,
            ModelsScanned: modelsScanned,
            DistinctHeroFoldersScanned: sampledHeroFolders.Count,
            TotalMeshesScanned: totalMeshes,
            TotalVerticesScanned: totalVerts,
            TotalTrianglesScanned: totalTris,
            TotalOrphanedVertices: totalOrphans,
            ModelsWithOrphanVertices: modelsWithOrphans,
            TotalOutOfBoundsFaces: totalOobFaces,
            SkippedMalformedEntries: skippedMalformedEntries,
            AverageAcmr: avgAcmr,
            MaxAcmr: (float)Math.Round(maxAcmr, 3),
            SampledModels: samples);
    }

    private static bool TryExtractHeroFolderName(string entryPath, out string heroFolder)
    {
        heroFolder = string.Empty;
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        string normalized = entryPath.Replace('\\', '/');
        const string marker = "heroes/";
        int idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        int start = idx + marker.Length;
        int slash = normalized.IndexOf('/', start);
        if (slash <= start)
        {
            return false;
        }

        heroFolder = normalized.Substring(start, slash - start);
        return !string.IsNullOrWhiteSpace(heroFolder);
    }
}
