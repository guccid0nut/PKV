// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GlbModelLoaderAndValidatorTests.cs
// System: ReplayRenderer.Tests
// Policy: Strict Explicit Typing (NO 'var')
// Purpose: Phase 0 (#119 glb-scene-graph-and-normals, #75 gltf-validator-gate,
//          #122 vanity-gpu-dispose & store-ar-middle-click) verification suite.
// ============================================================================

using System.Numerics;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.K2;
using ReplayVulkanRenderer;
using Xunit;

namespace ReplayRenderer.Tests;

public sealed class GlbModelLoaderAndValidatorTests
{
    [Fact]
    public void LoadGlbFile_MultiMeshMultiPrimitiveWithNodeTransforms_LoadsAllPrimitivesAndAppliesTransforms()
    {
        // Protects: GlbModelLoader full scene->node->mesh->primitive traversal and node transform application (#119).
        // Fails when: GlbModelLoader only reads meshesEl[0] or primitives[0], or ignores scene/node transforms.
        // Seam: Real binary GLB parsing through GlbModelLoader.LoadGlbFile.
        byte[] glbBytes = BuildTwoMeshTwoPrimitiveTransformedGlb();
        string tempPath = Path.Combine(Path.GetTempPath(), $"kongor_test_2x2_{Guid.NewGuid():N}.glb");
        try
        {
            File.WriteAllBytes(tempPath, glbBytes);
            GlbMeshData? mesh = GlbModelLoader.LoadGlbFile(tempPath);

            Assert.NotNull(mesh);
            // 2 meshes * 2 primitives * 3 vertices = 12 vertices, 12 indices (4 triangles)
            Assert.Equal(12, mesh.Vertices.Length);
            Assert.Equal(12, mesh.Indices.Length);

            // Node 0 translates Mesh 0 by (+10, +20, +30). First vertex of Mesh 0 Prim 0 was (0, 0, 0) -> (10, 20, 30)
            Assert.True(Vector3.Distance(new Vector3(10f, 20f, 30f), mesh.Vertices[0].Position) < 1e-4f,
                $"Expected Node0 translated vertex at (10,20,30), got {mesh.Vertices[0].Position}");

            // Node 1 is child of Node 0 with local translation (+5, 0, 0) and scale (2, 2, 2).
            // Mesh 1 Prim 1 first vertex local (1, 0, 0) -> scaled (2, 0, 0) + Node1 (5, 0, 0) + Node0 (10, 20, 30) = (17, 20, 30)
            Assert.True(Vector3.Distance(new Vector3(17f, 20f, 30f), mesh.Vertices[9].Position) < 1e-4f,
                $"Expected child Node1 world-transformed vertex at (17,20,30), got {mesh.Vertices[9].Position}");

            // Verify indices for primitive 3 (vertices 9, 10, 11) are properly offset by baseVertexOffset
            Assert.Equal(9u, mesh.Indices[9]);
            Assert.Equal(10u, mesh.Indices[10]);
            Assert.Equal(11u, mesh.Indices[11]);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void LoadGlbFile_UnnormaledUnitCube_ComputesRealAreaWeightedFaceNormalsAlongAxes()
    {
        // Protects: GlbModelLoader.CalculateMeshNormals area-weighted face normal computation when NORMAL is absent (#119).
        // Fails when: CalculateMeshNormals is called before indices are read or stubs normals to Vector3.UnitY.
        // Seam: Real binary GLB without NORMAL accessor loaded through GlbModelLoader.LoadGlbFile.
        byte[] glbBytes = BuildUnnormaledUnitCubeGlb();
        string tempPath = Path.Combine(Path.GetTempPath(), $"kongor_test_cube_{Guid.NewGuid():N}.glb");
        try
        {
            File.WriteAllBytes(tempPath, glbBytes);
            GlbMeshData? mesh = GlbModelLoader.LoadGlbFile(tempPath);

            Assert.NotNull(mesh);
            Assert.Equal(24, mesh.Vertices.Length);
            Assert.Equal(36, mesh.Indices.Length);

            // Face 0 (+Z): vertices 0..3 should have normal (0, 0, +1)
            Assert.True(Vector3.Distance(Vector3.UnitZ, mesh.Vertices[0].Normal) < 1e-4f,
                $"Expected +Z face normal (0,0,1), got {mesh.Vertices[0].Normal}");
            // Face 1 (-Z): vertices 4..7 should have normal (0, 0, -1)
            Assert.True(Vector3.Distance(-Vector3.UnitZ, mesh.Vertices[4].Normal) < 1e-4f,
                $"Expected -Z face normal (0,0,-1), got {mesh.Vertices[4].Normal}");
            // Face 2 (+X): vertices 8..11 should have normal (+1, 0, 0)
            Assert.True(Vector3.Distance(Vector3.UnitX, mesh.Vertices[8].Normal) < 1e-4f,
                $"Expected +X face normal (1,0,0), got {mesh.Vertices[8].Normal}");
            // Face 3 (-X): vertices 12..15 should have normal (-1, 0, 0)
            Assert.True(Vector3.Distance(-Vector3.UnitX, mesh.Vertices[12].Normal) < 1e-4f,
                $"Expected -X face normal (-1,0,0), got {mesh.Vertices[12].Normal}");
            // Face 4 (+Y): vertices 16..19 should have normal (0, +1, 0)
            Assert.True(Vector3.Distance(Vector3.UnitY, mesh.Vertices[16].Normal) < 1e-4f,
                $"Expected +Y face normal (0,1,0), got {mesh.Vertices[16].Normal}");
            // Face 5 (-Y): vertices 20..23 should have normal (0, -1, 0)
            Assert.True(Vector3.Distance(-Vector3.UnitY, mesh.Vertices[20].Normal) < 1e-4f,
                $"Expected -Y face normal (0,-1,0), got {mesh.Vertices[20].Normal}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void ValidateGlbAndMesh_WhenValidK2ModelConverted_PassesValidationAndRejectsCorruptMutants()
    {
        // Protects: Structural GLB/mesh validation gate (#75 gltf-validator-gate) on K2ToGltfConverter output.
        // Fails when: Out-of-bounds triangle indices, non-multiple-of-3 indices, or NaN vertices are accepted.
        // Seam: K2ToGltfConverter.ConvertToGlb -> K2ToGltfConverter.ValidateGlbBytes -> GlbModelLoader.ValidateMesh.
        K2Model model = new()
        {
            Version = 3
        };

        model.Meshes.Add(new K2Mesh
        {
            Name = "BodyMesh",
            NumVertices = 3,
            Vertices =
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            ],
            Normals =
            [
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f)
            ],
            TexCoords =
            [
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f)
            ],
            Indices = [0, 1, 2]
        });

        model.Meshes.Add(new K2Mesh
        {
            Name = "WeaponMesh",
            NumVertices = 3,
            Vertices =
            [
                new Vector3(2f, 0f, 0f),
                new Vector3(3f, 0f, 0f),
                new Vector3(2f, 1f, 0f)
            ],
            Normals =
            [
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f)
            ],
            TexCoords =
            [
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f)
            ],
            Indices = [0, 1, 2]
        });

        byte[] glbBytes = K2ToGltfConverter.ConvertToGlb(model);
        bool isValid = K2ToGltfConverter.ValidateGlbBytes(glbBytes, out string validationError);
        Assert.True(isValid, $"Expected valid K2 converted GLB, but ValidateGlbBytes failed: {validationError}");

        string tempPath = Path.Combine(Path.GetTempPath(), $"kongor_test_k2_{Guid.NewGuid():N}.glb");
        try
        {
            File.WriteAllBytes(tempPath, glbBytes);
            GlbMeshData? loaded = GlbModelLoader.LoadGlbFile(tempPath);
            Assert.NotNull(loaded);
            // Both BodyMesh and WeaponMesh primitives must be loaded (6 vertices, 6 indices)
            Assert.Equal(6, loaded.Vertices.Length);
            Assert.Equal(6, loaded.Indices.Length);
            Assert.True(GlbModelLoader.ValidateMesh(loaded, out string meshErr), meshErr);

            // Mutant 1: Out-of-bounds triangle index (>= Vertices.Length)
            uint originalIdx = loaded.Indices[5];
            loaded.Indices[5] = 999u;
            Assert.False(GlbModelLoader.ValidateMesh(loaded, out string oobError));
            Assert.Contains("out of bounds", oobError, StringComparison.OrdinalIgnoreCase);
            loaded.Indices[5] = originalIdx;

            // Mutant 2: NaN vertex position
            Vertex3D originalVertex = loaded.Vertices[0];
            Vertex3D nanVertex = originalVertex;
            nanVertex.Position = new Vector3(float.NaN, 0f, 0f);
            loaded.Vertices[0] = nanVertex;
            Assert.False(GlbModelLoader.ValidateMesh(loaded, out string nanError));
            Assert.Contains("NaN", nanError, StringComparison.OrdinalIgnoreCase);
            loaded.Vertices[0] = originalVertex;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void VanityBoothAndStoreArScripts_EnforceGpuDisposalAndMiddleClickFocalPivot()
    {
        // Protects: #122 vanity-gpu-dispose & store-ar-middle-click in vanity_booth_3d.js and store_ar.js.
        // Fails when: clearCurrentModel() is omitted before modelGroup.clear() or e.button === 1 is blocked.
        // Seam: Live web-ui JavaScript assets on disk.
        string repoRoot = FindRepoRoot();
        string vanityPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "vanity_booth_3d.js");
        string storeArPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "store_ar.js");

        string vanityText = File.ReadAllText(vanityPath, Encoding.UTF8);
        string storeArText = File.ReadAllText(storeArPath, Encoding.UTF8);

        Assert.Contains("async function loadModelUrl(", vanityText, StringComparison.Ordinal);
        Assert.Contains("clearCurrentModel();\n                    if (modelGroup) {\n                        modelGroup.clear();",
            vanityText.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("[VanityBooth3D] [GPU-DISPOSE]", vanityText, StringComparison.Ordinal);

        Assert.DoesNotContain("if (e.button === 1 ||", storeArText, StringComparison.Ordinal);
        Assert.Contains("if (e.button !== 0 && e.button !== 1 && e.button !== 2)", storeArText, StringComparison.Ordinal);
        Assert.Contains("if (e.button === 1)", storeArText, StringComparison.Ordinal);
        Assert.Contains("[KongorStoreAR] [FOCAL-PIVOT]", storeArText, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string currentDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(currentDir) && !File.Exists(Path.Combine(currentDir, "ProjectKongor.slnx")))
        {
            string? parent = Directory.GetParent(currentDir)?.FullName;
            if (parent == null || parent == currentDir) break;
            currentDir = parent;
        }
        return currentDir;
    }

    private static byte[] BuildTwoMeshTwoPrimitiveTransformedGlb()
    {
        using MemoryStream binStream = new();
        using BinaryWriter binWriter = new(binStream);

        // 4 primitives * (3 Vector3 positions = 36 bytes + 3 ushort indices = 6 bytes padded to 8 bytes)
        Vector3[][] primPositions =
        [
            [new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f)],
            [new Vector3(0f, 2f, 0f), new Vector3(1f, 2f, 0f), new Vector3(0f, 3f, 0f)],
            [new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(0f, 1f, 1f)],
            [new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(1f, 1f, 0f)]
        ];

        List<object> bufferViews = [];
        List<object> accessors = [];

        for (int p = 0; p < 4; p++)
        {
            int posOffset = (int)binStream.Position;
            for (int v = 0; v < 3; v++)
            {
                binWriter.Write(primPositions[p][v].X);
                binWriter.Write(primPositions[p][v].Y);
                binWriter.Write(primPositions[p][v].Z);
            }
            bufferViews.Add(new { buffer = 0, byteOffset = posOffset, byteLength = 36, target = 34962 });
            accessors.Add(new { bufferView = p * 2, componentType = 5126, count = 3, type = "VEC3" });

            int idxOffset = (int)binStream.Position;
            binWriter.Write((ushort)0);
            binWriter.Write((ushort)1);
            binWriter.Write((ushort)2);
            binWriter.Write((ushort)0); // 4-byte alignment padding
            bufferViews.Add(new { buffer = 0, byteOffset = idxOffset, byteLength = 6, target = 34963 });
            accessors.Add(new { bufferView = p * 2 + 1, componentType = 5123, count = 3, type = "SCALAR" });
        }

        byte[] binBytes = binStream.ToArray();
        object gltfObj = new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new object[]
            {
                new { nodes = new int[] { 0 } }
            },
            nodes = new object[]
            {
                new
                {
                    name = "RootNode0",
                    mesh = 0,
                    translation = new float[] { 10f, 20f, 30f },
                    children = new int[] { 1 }
                },
                new
                {
                    name = "ChildNode1",
                    mesh = 1,
                    translation = new float[] { 5f, 0f, 0f },
                    scale = new float[] { 2f, 2f, 2f }
                }
            },
            meshes = new object[]
            {
                new
                {
                    name = "Mesh0",
                    primitives = new object[]
                    {
                        new { attributes = new { POSITION = 0 }, indices = 1 },
                        new { attributes = new { POSITION = 2 }, indices = 3 }
                    }
                },
                new
                {
                    name = "Mesh1",
                    primitives = new object[]
                    {
                        new { attributes = new { POSITION = 4 }, indices = 5 },
                        new { attributes = new { POSITION = 6 }, indices = 7 }
                    }
                }
            },
            buffers = new object[] { new { byteLength = binBytes.Length } },
            bufferViews,
            accessors
        };

        return PackGlbContainer(JsonSerializer.Serialize(gltfObj), binBytes);
    }

    private static byte[] BuildUnnormaledUnitCubeGlb()
    {
        // 6 faces * 4 vertices = 24 vertices; 6 faces * 2 triangles * 3 = 36 indices (CCW outward)
        Vector3[] positions =
        [
            // Face 0 (+Z): z = +1
            new Vector3(-1f, -1f,  1f), new Vector3( 1f, -1f,  1f), new Vector3( 1f,  1f,  1f), new Vector3(-1f,  1f,  1f),
            // Face 1 (-Z): z = -1
            new Vector3( 1f, -1f, -1f), new Vector3(-1f, -1f, -1f), new Vector3(-1f,  1f, -1f), new Vector3( 1f,  1f, -1f),
            // Face 2 (+X): x = +1
            new Vector3( 1f, -1f,  1f), new Vector3( 1f, -1f, -1f), new Vector3( 1f,  1f, -1f), new Vector3( 1f,  1f,  1f),
            // Face 3 (-X): x = -1
            new Vector3(-1f, -1f, -1f), new Vector3(-1f, -1f,  1f), new Vector3(-1f,  1f,  1f), new Vector3(-1f,  1f, -1f),
            // Face 4 (+Y): y = +1
            new Vector3(-1f,  1f,  1f), new Vector3( 1f,  1f,  1f), new Vector3( 1f,  1f, -1f), new Vector3(-1f,  1f, -1f),
            // Face 5 (-Y): y = -1
            new Vector3(-1f, -1f, -1f), new Vector3( 1f, -1f, -1f), new Vector3( 1f, -1f,  1f), new Vector3(-1f, -1f,  1f)
        ];

        using MemoryStream binStream = new();
        using BinaryWriter binWriter = new(binStream);

        int posByteLength = positions.Length * 12;
        for (int i = 0; i < positions.Length; i++)
        {
            binWriter.Write(positions[i].X);
            binWriter.Write(positions[i].Y);
            binWriter.Write(positions[i].Z);
        }

        int idxOffset = (int)binStream.Position;
        for (int face = 0; face < 6; face++)
        {
            ushort baseV = (ushort)(face * 4);
            binWriter.Write(baseV);
            binWriter.Write((ushort)(baseV + 1));
            binWriter.Write((ushort)(baseV + 2));
            binWriter.Write(baseV);
            binWriter.Write((ushort)(baseV + 2));
            binWriter.Write((ushort)(baseV + 3));
        }
        int idxByteLength = 36 * 2;

        byte[] binBytes = binStream.ToArray();
        object gltfObj = new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new object[] { new { nodes = new int[] { 0 } } },
            nodes = new object[] { new { name = "CubeNode", mesh = 0 } },
            meshes = new object[]
            {
                new
                {
                    name = "CubeMesh",
                    primitives = new object[]
                    {
                        new { attributes = new { POSITION = 0 }, indices = 1 }
                    }
                }
            },
            buffers = new object[] { new { byteLength = binBytes.Length } },
            bufferViews = new object[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = posByteLength, target = 34962 },
                new { buffer = 0, byteOffset = idxOffset, byteLength = idxByteLength, target = 34963 }
            },
            accessors = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = 24, type = "VEC3" },
                new { bufferView = 1, componentType = 5123, count = 36, type = "SCALAR" }
            }
        };

        return PackGlbContainer(JsonSerializer.Serialize(gltfObj), binBytes);
    }

    private static byte[] PackGlbContainer(string json, byte[] binChunk)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int binPad = (4 - binChunk.Length % 4) % 4;
        int totalLength = 12 + 8 + jsonBytes.Length + jsonPad + 8 + binChunk.Length + binPad;

        using MemoryStream ms = new(totalLength);
        using BinaryWriter bw = new(ms);
        bw.Write(0x46546C67U); // "glTF"
        bw.Write(2U);
        bw.Write((uint)totalLength);

        bw.Write((uint)(jsonBytes.Length + jsonPad));
        bw.Write(0x4E4F534AU); // "JSON"
        bw.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);

        bw.Write((uint)(binChunk.Length + binPad));
        bw.Write(0x004E4942U); // "BIN\0"
        bw.Write(binChunk);
        for (int i = 0; i < binPad; i++) bw.Write((byte)0x00);

        return ms.ToArray();
    }

    [Fact]
    public void ConvertToGlb_WithRiggedModelAndClip_EmitsTrueSkeletalSkinningAndRejectsWeightOrJointMutants()
    {
        // Protects: True glTF 2.0 skeletal skinning (#118 gltf-true-skinning) and validator mutant rejection (#75).
        // Fails when: Converter emits morph targets by default, omits GltfSkin/JOINTS_0/WEIGHTS_0/TRS channels,
        //             or ValidateGlbBytes accepts un-normalized WEIGHTS_0 (sum=0.5) or out-of-range JOINTS_0.
        // Seam: K2ToGltfConverter.ConvertToGlb -> ValidateGlbBytes -> GlbModelLoader.LoadGlbFile.
        K2Model model = new()
        {
            Version = 3,
            NumBones = 2,
            NumMeshes = 1
        };

        model.Bones.Add(new K2Bone
        {
            Index = 0,
            ParentIndex = -1,
            Name = "Bip01_Pelvis",
            LocalMatrix = Matrix4x4.Identity,
            InverseMatrix = Matrix4x4.Identity
        });

        model.Bones.Add(new K2Bone
        {
            Index = 1,
            ParentIndex = 0,
            Name = "Bip01_Spine",
            LocalMatrix = Matrix4x4.CreateTranslation(0f, 0f, 10f),
            InverseMatrix = Matrix4x4.CreateTranslation(0f, 0f, -10f)
        });

        const int vertexCount = 120;
        Vector3[] vertices = new Vector3[vertexCount];
        K2VertexBoneLink[] links = new K2VertexBoneLink[vertexCount];
        int[] indices = new int[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = new Vector3(i * 0.1f, (i % 3) * 0.5f, (i % 5) * 1.0f);
            links[i] = new K2VertexBoneLink
            {
                BoneIndex0 = 0,
                BoneIndex1 = 1,
                Weight0 = 0.75f,
                Weight1 = 0.25f
            };
            indices[i] = i;
        }

        model.Meshes.Add(new K2Mesh
        {
            Name = "SkinnedBody",
            NumVertices = vertexCount,
            Vertices = vertices,
            Normals = [], // Also exercises #81 automatic double-precision normal synthesis
            TexCoords = new Vector2[vertexCount],
            Indices = indices,
            BoneLinks = links
        });

        Vector3[] translations = new Vector3[24];
        Quaternion[] rotations = new Quaternion[24];
        Vector3[] scales = new Vector3[24];
        for (int f = 0; f < 24; f++)
        {
            translations[f] = new Vector3(0f, 0f, 10f + f * 0.2f);
            rotations[f] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, f * 0.05f);
            scales[f] = Vector3.One;
        }

        K2Clip runClip = new()
        {
            Name = "run_1",
            FrameRate = 30,
            NumFrames = 24,
            NumBones = 2,
            BoneMotions =
            [
                new K2BoneMotion
                {
                    BoneIndex = 1,
                    BoneName = "Bip01_Spine",
                    Translations = translations,
                    Rotations = rotations,
                    Scales = scales
                }
            ]
        };

        byte[] skinnedGlb = K2ToGltfConverter.ConvertToGlb(
            model,
            [runClip],
            meshMaterials:
            [
                new K2MeshMaterial
                {
                    Name = "GlowArmor",
                    IsUnlit = true,
                    SrgbBaseColorFactor = [0.5f, 0.5f, 0.5f, 1.0f]
                }
            ],
            useMorphTargetsFallback: false);

        byte[] morphFallbackGlb = K2ToGltfConverter.ConvertToGlb(
            model,
            [runClip],
            useMorphTargetsFallback: true);

        Assert.True(K2ToGltfConverter.ValidateGlbBytes(skinnedGlb, out string skinErr), skinErr);
        Assert.True(K2ToGltfConverter.ValidateGlbBytes(morphFallbackGlb, out string morphErr), morphErr);

        // True skeletal skinning must be dramatically smaller than per-vertex morph target baking
        Assert.True(skinnedGlb.Length * 3 < morphFallbackGlb.Length,
            $"Expected skinned GLB ({skinnedGlb.Length} B) to be >3x smaller than morph GLB ({morphFallbackGlb.Length} B).");

        // Inspect JSON chunk for skins, JOINTS_0, WEIGHTS_0, TRS channels, and KHR_materials_unlit (#81, #118)
        uint jsonLen = BitConverter.ToUInt32(skinnedGlb, 12);
        string jsonText = Encoding.UTF8.GetString(skinnedGlb, 20, (int)jsonLen);
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("skins", out JsonElement skinsEl) && skinsEl.GetArrayLength() == 1);
        Assert.Equal(2, skinsEl[0].GetProperty("joints").GetArrayLength());

        JsonElement prim0 = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        Assert.False(prim0.TryGetProperty("targets", out _));
        JsonElement attrs = prim0.GetProperty("attributes");
        Assert.True(attrs.TryGetProperty("JOINTS_0", out JsonElement joints0AccEl));
        Assert.True(attrs.TryGetProperty("WEIGHTS_0", out JsonElement weights0AccEl));

        JsonElement animChannels = root.GetProperty("animations")[0].GetProperty("channels");
        Assert.Equal(3, animChannels.GetArrayLength()); // translation, rotation, scale

        Assert.True(root.TryGetProperty("extensionsUsed", out JsonElement extUsedEl));
        Assert.Equal("KHR_materials_unlit", extUsedEl[0].GetString());

        // Verify GlbModelLoader reads the explicit JOINTS_0 & WEIGHTS_0
        string tempPath = Path.Combine(Path.GetTempPath(), $"kongor_skinned_{Guid.NewGuid():N}.glb");
        try
        {
            File.WriteAllBytes(tempPath, skinnedGlb);
            GlbMeshData? loaded = GlbModelLoader.LoadGlbFile(tempPath);
            Assert.NotNull(loaded);
            Assert.Equal(vertexCount, loaded.Vertices.Length);
            Assert.Equal(new Vector4(0f, 1f, 0f, 0f), loaded.Vertices[0].JointIndices);
            Assert.True(Vector4.Distance(new Vector4(0.75f, 0.25f, 0f, 0f), loaded.Vertices[0].JointWeights) < 1e-4f);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        // Mutant 1 (Phase 5 Exit Gate #4): Un-normalized WEIGHTS_0 (sum = 0.5) must be rejected
        JsonElement accessors = root.GetProperty("accessors");
        JsonElement bufferViews = root.GetProperty("bufferViews");
        int wViewIdx = accessors[weights0AccEl.GetInt32()].GetProperty("bufferView").GetInt32();
        int wByteOffset = bufferViews[wViewIdx].GetProperty("byteOffset").GetInt32();
        int binChunkDataStart = 20 + (int)jsonLen + 8;

        byte[] unnormalizedMutant = (byte[])skinnedGlb.Clone();
        BitConverter.GetBytes(0.25f).CopyTo(unnormalizedMutant, binChunkDataStart + wByteOffset);
        BitConverter.GetBytes(0.25f).CopyTo(unnormalizedMutant, binChunkDataStart + wByteOffset + 4);
        BitConverter.GetBytes(0.0f).CopyTo(unnormalizedMutant, binChunkDataStart + wByteOffset + 8);
        BitConverter.GetBytes(0.0f).CopyTo(unnormalizedMutant, binChunkDataStart + wByteOffset + 12);
        Assert.False(K2ToGltfConverter.ValidateGlbBytes(unnormalizedMutant, out string weightMutantErr));
        Assert.Contains("not normalized to 1.0", weightMutantErr, StringComparison.OrdinalIgnoreCase);

        // Mutant 2 (Phase 5 Exit Gate #4): Out-of-range JOINTS_0 index (99 > maxValidJoint 1) must be rejected
        int jViewIdx = accessors[joints0AccEl.GetInt32()].GetProperty("bufferView").GetInt32();
        int jByteOffset = bufferViews[jViewIdx].GetProperty("byteOffset").GetInt32();
        byte[] oobJointMutant = (byte[])skinnedGlb.Clone();
        BitConverter.GetBytes((ushort)99).CopyTo(oobJointMutant, binChunkDataStart + jByteOffset);
        Assert.False(K2ToGltfConverter.ValidateGlbBytes(oobJointMutant, out string jointMutantErr));
        Assert.Contains("exceeds max joint index", jointMutantErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackSecondOrderBytePlane_WithLinearVelocityTrajectories_RoundTripsLosslesslyWithZeroResiduals()
    {
        // Protects: 2nd-order linear velocity prediction (2*prev - older), ordered sign-bit float mapping,
        //           and byte-plane transpose (#94 manimgx-2nd-order-byte-plane).
        // Fails when: Ordered float bijection breaks monotonicity across negative/positive numbers, or
        //             byte-plane pack/unpack loses precision.
        // Seam: K2ToGltfConverter.FloatToOrderedUInt32 / PackSecondOrderBytePlane / UnpackSecondOrderBytePlane.
        float[] monotonicSamples = [-100.5f, -1.0f, -0.001f, 0.0f, 0.001f, 1.0f, 42.25f];
        for (int i = 0; i < monotonicSamples.Length - 1; i++)
        {
            uint u0 = K2ToGltfConverter.FloatToOrderedUInt32(monotonicSamples[i]);
            uint u1 = K2ToGltfConverter.FloatToOrderedUInt32(monotonicSamples[i + 1]);
            Assert.True(u0 < u1, $"Ordered uint mapping violated monotonicity between {monotonicSamples[i]} and {monotonicSamples[i + 1]}");
            Assert.Equal(monotonicSamples[i], K2ToGltfConverter.OrderedUInt32ToFloat(u0));
        }

        List<float[]> frames =
        [
            [1.0f, -2.0f, 0.5f, 10.0f],
            [2.0f, -4.0f, 1.5f, 20.25f],
            [3.0f, -6.0f, 2.5f, 30.5f],
            [4.0f, -8.0f, 3.5f, 40.75f]
        ];

        byte[] packed = K2ToGltfConverter.PackSecondOrderBytePlane(frames);
        Assert.Equal(frames.Count * 4 * sizeof(float), packed.Length);

        float[][] unpacked = K2ToGltfConverter.UnpackSecondOrderBytePlane(packed, frames.Count, 4);
        Assert.Equal(frames.Count, unpacked.Length);
        for (int f = 0; f < frames.Count; f++)
        {
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal(frames[f][c], unpacked[f][c]);
            }
        }
    }

    [Fact]
    public void VulkanNv12ReadbackRing_WithBt709FullRangeRgba_ReducesPcieBandwidthBy62Point5PercentAndWraps6Slots()
    {
        // Protects: 6-slot async NV12 readback staging ring and BT.709 full-range RGBA8->NV12 conversion (#95).
        // Fails when: NV12 byte size != 1.5 B/px (62.5% savings), ring buffer slot count != 6, or BT.709 luma/chroma drifts.
        // Seam: VulkanNv12ReadbackRing.EnqueueRgbaFrame / TryDequeueNv12Frame / ComputePcieBandwidthSavingsRatio.
        Assert.Equal(6, VulkanNv12ReadbackRing.SlotCount);
        double savings = VulkanNv12ReadbackRing.ComputePcieBandwidthSavingsRatio(1920, 1080);
        Assert.Equal(0.625, savings, 6);

        using VulkanNv12ReadbackRing ring = new(4, 2);
        Assert.Equal(12, ring.SlotByteLength); // 4 * 2 * 1.5 = 12 bytes (8 Y + 4 UV)

        // Pure BT.709 Green (R=0, G=255, B=0, A=255): Y = round(0.7152 * 255) = 182
        byte[] greenRgba = new byte[4 * 2 * 4];
        for (int i = 0; i < greenRgba.Length; i += 4)
        {
            greenRgba[i] = 0;
            greenRgba[i + 1] = 255;
            greenRgba[i + 2] = 0;
            greenRgba[i + 3] = 255;
        }

        for (int f = 0; f < 8; f++)
        {
            ring.EnqueueRgbaFrame(greenRgba, f);
        }

        // After enqueuing 8 frames into a 6-slot ring, 6 remain in flight starting at frame 2
        Assert.Equal(6, ring.InFlightCount);
        Assert.True(ring.TryDequeueNv12Frame(out ReadOnlySpan<byte> nv12, out long firstFrameNum, out int slotIdx));
        Assert.Equal(2L, firstFrameNum);
        Assert.Equal(2, slotIdx);
        Assert.Equal(182, nv12[0]); // BT.709 Luma Y for pure green
    }
}

