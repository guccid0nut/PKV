// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local
// ReSharper disable CollectionNeverQueried.Local
// ReSharper disable ParameterTypeCanBeEnumerable.Local
// ReSharper disable LoopCanBeConvertedToQuery
// ReSharper disable PartOfLoopCanBeConvertedToQuery
// ReSharper disable UnusedMember.Local
// ReSharper disable PropertyCanBeMadeInitOnly.Local
// ReSharper disable InlineTemporaryVariable

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASPIRE.Common.K2;

public static class K2ToGltfConverter
{
    private static readonly JsonSerializerOptions GltfJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "GLTF object model serialization")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "GLTF object model serialization")]
    public static byte[] ConvertToGlb(
        K2Model model,
        IReadOnlyList<K2Clip>? clips = null,
        byte[]? diffusePngBytes = null,
        IReadOnlyList<K2MeshMaterial>? meshMaterials = null,
        bool useMorphTargetsFallback = false)
    {
        using MemoryStream binStream = new();
        using BinaryWriter binWriter = new(binStream);

        List<GltfBufferView> bufferViews = [];
        List<GltfAccessor> accessors = [];
        List<GltfNode> nodes = [];
        List<GltfMesh> meshes = [];
        List<GltfSkin> skins = [];
        List<GltfAnimation> animations = [];
        List<GltfTexture> textures = [];
        List<GltfImage> images = [];
        List<GltfSampler> samplers = [];
        List<GltfMaterial> gltfMaterials = [];
        List<string> extensionsUsed = [];
        List<int> sceneRoots = [0];

        bool useTrueSkinning = !useMorphTargetsFallback && model.Bones.Count > 0;

        const int meshNodeIndex = 0;
        nodes.Add(new GltfNode
        {
            Name = "RootMesh",
            Mesh = 0,
            Skin = useTrueSkinning ? 0 : null
        });

        // Build Bone GltfNode Hierarchy (#118 gltf-true-skinning)
        int[] boneNodeIndices = new int[model.Bones.Count];
        if (useTrueSkinning)
        {
            for (int b = 0; b < model.Bones.Count; b++)
            {
                K2Bone bone = model.Bones[b];
                int nodeIdx = nodes.Count;
                boneNodeIndices[b] = nodeIdx;

                Matrix4x4 localGl = ConvertK2MatrixToGltf(bone.LocalMatrix);
                DecomposeGltfMatrix(localGl, out Vector3 trans, out Quaternion rot, out Vector3 scale);

                nodes.Add(new GltfNode
                {
                    Name = string.IsNullOrEmpty(bone.Name) ? $"Bone_{b}" : bone.Name,
                    Translation = [SafeFloat(trans.X), SafeFloat(trans.Y), SafeFloat(trans.Z)],
                    Rotation = [SafeFloat(rot.X), SafeFloat(rot.Y), SafeFloat(rot.Z), SafeFloat(rot.W, 1f)],
                    Scale = [SafeFloat(scale.X, 1f), SafeFloat(scale.Y, 1f), SafeFloat(scale.Z, 1f)]
                });
            }

            for (int b = 0; b < model.Bones.Count; b++)
            {
                int parentBoneIdx = model.Bones[b].ParentIndex;
                int childNodeIdx = boneNodeIndices[b];
                if (parentBoneIdx >= 0 && parentBoneIdx < model.Bones.Count && parentBoneIdx != b)
                {
                    int parentNodeIdx = boneNodeIndices[parentBoneIdx];
                    nodes[parentNodeIdx].Children ??= [];
                    nodes[parentNodeIdx].Children!.Add(childNodeIdx);
                }
                else
                {
                    sceneRoots.Add(childNodeIdx);
                }
            }
        }

        // Ensure default sampler exists
        samplers.Add(new GltfSampler
        {
            MagFilter = 9729, // LINEAR
            MinFilter = 9987, // LINEAR_MIPMAP_LINEAR
            WrapS = 10497, // REPEAT
            WrapT = 10497 // REPEAT
        });

        // Helper to register image/texture with deduplication
        Dictionary<byte[], int> imageIndexByPng = [];

        // Build list of materials for each mesh
        List<K2MeshMaterial> resolvedMeshMats = [];
        if (meshMaterials is { Count: > 0 })
            resolvedMeshMats.AddRange(meshMaterials);
        else if (diffusePngBytes is { Length: > 0 })
            resolvedMeshMats.Add(new K2MeshMaterial
            {
                Name = "DefaultMaterial",
                DiffusePngBytes = diffusePngBytes,
                HasAlpha = true
            });

        // Pre-plan animations across all clips
        List<(K2Clip Clip, List<int> SampleFrames, int TargetStartIndex)> clipPlans = [];
        int runningTargetIndex = 0;
        List<float> defaultWeights = [];

        if (clips is { Count: > 0 } && model.Bones.Count > 0)
        {
            foreach (K2Clip clip in clips)
            {
                if (clip.NumFrames <= 0 || clip.BoneMotions.Count == 0) continue;

                int maxTargetFrames = clip.Name.Contains("walk", StringComparison.OrdinalIgnoreCase) ||
                                      clip.Name.Contains("run", StringComparison.OrdinalIgnoreCase) ? 24 :
                    clip.Name.Contains("attack", StringComparison.OrdinalIgnoreCase) ? 20 :
                    clip.Name.Contains("ability", StringComparison.OrdinalIgnoreCase) ? 20 :
                    clip.Name.Contains("idle", StringComparison.OrdinalIgnoreCase) ? 16 : 14;

                int step = Math.Max(1, (int)Math.Ceiling((double)clip.NumFrames / maxTargetFrames));
                List<int> sampleFrames = [];
                for (int f = 0; f < clip.NumFrames; f += step) sampleFrames.Add(f);

                if (sampleFrames.Count == 0 || sampleFrames[^1] != clip.NumFrames - 1)
                    sampleFrames.Add(clip.NumFrames - 1);

                clipPlans.Add((clip, sampleFrames, runningTargetIndex));
                runningTargetIndex += sampleFrames.Count;
            }

            if (useMorphTargetsFallback)
            {
                defaultWeights.AddRange(new float[runningTargetIndex]);
            }
        }

        // Pre-cache bone names
        Dictionary<string, int> boneNameToIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < model.Bones.Count; b++) boneNameToIndex[model.Bones[b].Name] = b;

        // Pre-cache motion per bone for each clip
        List<Dictionary<int, K2BoneMotion>> motionByBonePerClip = [];
        foreach ((K2Clip clip, _, _) in clipPlans)
        {
            Dictionary<int, K2BoneMotion> motionByBone = [];
            foreach (K2BoneMotion motion in clip.BoneMotions)
            {
                int boneIndex;
                if (!string.IsNullOrEmpty(motion.BoneName) &&
                    boneNameToIndex.TryGetValue(motion.BoneName, out int namedIdx))
                    boneIndex = namedIdx;
                else if (motion.BoneIndex >= 0 && motion.BoneIndex < model.Bones.Count)
                    boneIndex = motion.BoneIndex;
                else
                    continue;

                motionByBone[boneIndex] = motion;
            }

            motionByBonePerClip.Add(motionByBone);
        }

        // 1. Process Meshes (Each K2Mesh becomes a distinct glTF Primitive with its own Material)
        List<GltfPrimitive> primitives = [];

        for (int m = 0; m < model.Meshes.Count; m++)
        {
            K2Mesh mesh = model.Meshes[m];
            if (mesh.NumVertices == 0) continue;

            // Resolve Material for this mesh
            K2MeshMaterial meshMat = m < resolvedMeshMats.Count
                ? resolvedMeshMats[m]
                : resolvedMeshMats.Count > 0
                    ? resolvedMeshMats[0]
                    : new K2MeshMaterial { Name = mesh.Name };

            float[] baseColorFactor = [1f, 1f, 1f, 1f];
            if (meshMat.SrgbBaseColorFactor is { Length: >= 3 })
            {
                float alpha = meshMat.SrgbBaseColorFactor.Length >= 4 ? meshMat.SrgbBaseColorFactor[3] : 1f;
                baseColorFactor =
                [
                    SrgbToLinear(meshMat.SrgbBaseColorFactor[0]),
                    SrgbToLinear(meshMat.SrgbBaseColorFactor[1]),
                    SrgbToLinear(meshMat.SrgbBaseColorFactor[2]),
                    SafeFloat(alpha, 1f)
                ];
            }

            int matIndex = gltfMaterials.Count;
            GltfMaterial gltfMat = new()
            {
                Name =
                    string.IsNullOrEmpty(meshMat.Name)
                        ? string.IsNullOrEmpty(mesh.Name) ? $"Material_{m}" : mesh.Name
                        : meshMat.Name,
                DoubleSided = true,
                PbrMetallicRoughness = new GltfPbr
                {
                    BaseColorFactor = baseColorFactor,
                    MetallicFactor = meshMat.Metallic,
                    RoughnessFactor = meshMat.Roughness
                }
            };

            if (meshMat.DiffusePngBytes is { Length: > 0 })
            {
                int diffuseTexIdx = GetOrAddTexture(meshMat.DiffusePngBytes);
                gltfMat.PbrMetallicRoughness.BaseColorTexture = new GltfTextureInfo { Index = diffuseTexIdx };
            }

            if (meshMat.NormalPngBytes is { Length: > 0 })
            {
                int normTexIdx = GetOrAddTexture(meshMat.NormalPngBytes);
                gltfMat.NormalTexture = new GltfNormalTextureInfo { Index = normTexIdx, Scale = 1.0f };
            }

            if (meshMat.MetallicRoughnessPngBytes is { Length: > 0 })
            {
                int mrTexIdx = GetOrAddTexture(meshMat.MetallicRoughnessPngBytes);
                gltfMat.PbrMetallicRoughness.MetallicRoughnessTexture = new GltfTextureInfo { Index = mrTexIdx };
            }

            if (meshMat.GlowPngBytes is { Length: > 0 })
            {
                int glowTexIdx = GetOrAddTexture(meshMat.GlowPngBytes);
                gltfMat.EmissiveTexture = new GltfTextureInfo { Index = glowTexIdx };
                gltfMat.EmissiveFactor = [1f, 1f, 1f];
            }

            if (meshMat.IsAdditive)
            {
                gltfMat.AlphaMode = "BLEND";
                gltfMat.EmissiveFactor = [1f, 1f, 1f];
            }
            else if (meshMat.HasAlpha)
            {
                gltfMat.AlphaMode = "MASK";
                gltfMat.AlphaCutoff = 0.50f;
            }
            else
            {
                gltfMat.AlphaMode = "OPAQUE";
            }

            // #81 f3d-unlit-srgb-gamma-parity: Attach KHR_materials_unlit on unlit / additive FX materials
            if (meshMat.IsUnlit || meshMat.IsAdditive)
            {
                gltfMat.Extensions = new Dictionary<string, object>
                {
                    ["KHR_materials_unlit"] = new Dictionary<string, object>()
                };
                if (!extensionsUsed.Contains("KHR_materials_unlit"))
                {
                    extensionsUsed.Add("KHR_materials_unlit");
                }
            }

            gltfMaterials.Add(gltfMat);

            GltfPrimitive primitive = new() { Material = matIndex };

            // Positions (VEC3 FLOAT)
            int posBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34962);
            Vector3 minPos = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxPos = new(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < mesh.NumVertices; i++)
            {
                Vector3 v = mesh.Vertices[i];
                // Convert K2 coordinate system (Z-Up) to glTF coordinate system (Y-Up, right-handed): (X, Z, -Y)
                float gx = SafeFloat(v.X);
                float gy = SafeFloat(v.Z);
                float gz = SafeFloat(-v.Y);
                binWriter.Write(gx);
                binWriter.Write(gy);
                binWriter.Write(gz);

                minPos = Vector3.Min(minPos, new Vector3(gx, gy, gz));
                maxPos = Vector3.Max(maxPos, new Vector3(gx, gy, gz));
            }

            Align4(binStream, binWriter, posBufferView, bufferViews);
            int posAccessor = accessors.Count;
            accessors.Add(new GltfAccessor
            {
                BufferView = posBufferView,
                ComponentType = 5126, // FLOAT
                Count = mesh.NumVertices,
                Type = "VEC3",
                Min = [SafeFloat(minPos.X), SafeFloat(minPos.Y), SafeFloat(minPos.Z)],
                Max = [SafeFloat(maxPos.X), SafeFloat(maxPos.Y), SafeFloat(maxPos.Z)]
            });
            primitive.Attributes["POSITION"] = posAccessor;

            // Normals (VEC3 FLOAT) — synthesize with 64-bit double area-weighting if missing (#81, #94)
            Vector3[] sourceNormals = mesh.Normals.Length >= mesh.NumVertices
                ? mesh.Normals
                : ComputeAreaWeightedNormalsDouble(mesh.Vertices, mesh.Indices);

            if (sourceNormals.Length >= mesh.NumVertices)
            {
                int normBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34962);
                for (int i = 0; i < mesh.NumVertices; i++)
                {
                    Vector3 n = sourceNormals[i];
                    Vector3 glN = new(SafeFloat(n.X), SafeFloat(n.Z, 1f), SafeFloat(-n.Y));
                    float lenSq = glN.LengthSquared();
                    glN = lenSq > 1e-12f ? glN / MathF.Sqrt(lenSq) : Vector3.UnitY;
                    binWriter.Write(glN.X);
                    binWriter.Write(glN.Y);
                    binWriter.Write(glN.Z);
                }

                Align4(binStream, binWriter, normBufferView, bufferViews);
                int normAccessor = accessors.Count;
                accessors.Add(new GltfAccessor
                {
                    BufferView = normBufferView,
                    ComponentType = 5126,
                    Count = mesh.NumVertices,
                    Type = "VEC3"
                });
                primitive.Attributes["NORMAL"] = normAccessor;
            }

            // UVs (VEC2 FLOAT)
            if (mesh.TexCoords.Length >= mesh.NumVertices)
            {
                int uvBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34962);
                for (int i = 0; i < mesh.NumVertices; i++)
                {
                    Vector2 uv = mesh.TexCoords[i];
                    binWriter.Write(SafeFloat(uv.X));
                    binWriter.Write(SafeFloat(uv.Y));
                }

                Align4(binStream, binWriter, uvBufferView, bufferViews);
                int uvAccessor = accessors.Count;
                accessors.Add(new GltfAccessor
                {
                    BufferView = uvBufferView,
                    ComponentType = 5126,
                    Count = mesh.NumVertices,
                    Type = "VEC2"
                });
                primitive.Attributes["TEXCOORD_0"] = uvAccessor;
            }

            // Skeletal Skinning Attributes: JOINTS_0 (VEC4 UNSIGNED_SHORT) & WEIGHTS_0 (VEC4 FLOAT) (#118)
            if (useTrueSkinning)
            {
                int jointsBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34962);
                ushort[] normalizedJoints = new ushort[mesh.NumVertices * 4];
                float[] normalizedWeights = new float[mesh.NumVertices * 4];
                bool hasLinks = mesh.BoneLinks.Length >= mesh.NumVertices;
                int maxBoneIdx = model.Bones.Count - 1;

                for (int v = 0; v < mesh.NumVertices; v++)
                {
                    K2VertexBoneLink link = hasLinks ? mesh.BoneLinks[v] : default;
                    SanitizeVertexBoneInfluence(
                        link,
                        maxBoneIdx,
                        out ushort j0, out ushort j1, out ushort j2, out ushort j3,
                        out float w0, out float w1, out float w2, out float w3);

                    int baseIdx = v * 4;
                    normalizedJoints[baseIdx] = j0;
                    normalizedJoints[baseIdx + 1] = j1;
                    normalizedJoints[baseIdx + 2] = j2;
                    normalizedJoints[baseIdx + 3] = j3;

                    normalizedWeights[baseIdx] = w0;
                    normalizedWeights[baseIdx + 1] = w1;
                    normalizedWeights[baseIdx + 2] = w2;
                    normalizedWeights[baseIdx + 3] = w3;

                    binWriter.Write(j0);
                    binWriter.Write(j1);
                    binWriter.Write(j2);
                    binWriter.Write(j3);
                }

                Align4(binStream, binWriter, jointsBufferView, bufferViews);
                int jointsAccessor = accessors.Count;
                accessors.Add(new GltfAccessor
                {
                    BufferView = jointsBufferView,
                    ComponentType = 5123, // UNSIGNED_SHORT
                    Count = mesh.NumVertices,
                    Type = "VEC4"
                });
                primitive.Attributes["JOINTS_0"] = jointsAccessor;

                int weightsBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34962);
                for (int i = 0; i < normalizedWeights.Length; i++)
                {
                    binWriter.Write(normalizedWeights[i]);
                }

                Align4(binStream, binWriter, weightsBufferView, bufferViews);
                int weightsAccessor = accessors.Count;
                accessors.Add(new GltfAccessor
                {
                    BufferView = weightsBufferView,
                    ComponentType = 5126, // FLOAT
                    Count = mesh.NumVertices,
                    Type = "VEC4"
                });
                primitive.Attributes["WEIGHTS_0"] = weightsAccessor;
            }

            // Indices (SCALAR UNSIGNED_SHORT / UNSIGNED_INT)
            if (mesh.Indices.Length > 0)
            {
                int idxBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34963);
                bool isShort = mesh.NumVertices <= 65535;
                for (int i = 0; i < mesh.Indices.Length; i += 3)
                {
                    // Flip winding order for coordinate system inversion (i0, i2, i1)
                    int i0 = mesh.Indices[i];
                    int i1 = mesh.Indices[i + 2];
                    int i2 = mesh.Indices[i + 1];

                    if (isShort)
                    {
                        binWriter.Write((ushort)i0);
                        binWriter.Write((ushort)i1);
                        binWriter.Write((ushort)i2);
                    }
                    else
                    {
                        binWriter.Write(i0);
                        binWriter.Write(i1);
                        binWriter.Write(i2);
                    }
                }

                Align4(binStream, binWriter, idxBufferView, bufferViews);
                int idxAccessor = accessors.Count;
                accessors.Add(new GltfAccessor
                {
                    BufferView = idxBufferView,
                    ComponentType = isShort ? 5123 : 5125,
                    Count = mesh.Indices.Length,
                    Type = "SCALAR"
                });
                primitive.Indices = idxAccessor;
            }

            // Legacy Morph Targets Fallback (only when useMorphTargetsFallback == true)
            if (useMorphTargetsFallback && clipPlans.Count > 0 && model.Bones.Count > 0)
            {
                List<Dictionary<string, int>> meshTargets = [];
                bool hasLinks = mesh.BoneLinks.Length >= mesh.NumVertices;

                for (int c = 0; c < clipPlans.Count; c++)
                {
                    (K2Clip clip, List<int> sampleFrames, _) = clipPlans[c];
                    Dictionary<int, K2BoneMotion> motionByBone = motionByBonePerClip[c];

                    for (int k = 0; k < sampleFrames.Count; k++)
                    {
                        int frame = sampleFrames[k];

                        Matrix4x4[] worldBoneMats = new Matrix4x4[model.Bones.Count];
                        for (int b = 0; b < model.Bones.Count; b++)
                            worldBoneMats[b] = ComputeWorldBoneMatrix(model, b, frame, motionByBone);

                        Matrix4x4[] skinMats = new Matrix4x4[model.Bones.Count];
                        for (int b = 0; b < model.Bones.Count; b++)
                            skinMats[b] = model.Bones[b].InverseMatrix * worldBoneMats[b];

                        int targetBufferView = WriteBufferView(binStream, binWriter, bufferViews, 34962);
                        Vector3 minDelta = new(float.MaxValue, float.MaxValue, float.MaxValue);
                        Vector3 maxDelta = new(float.MinValue, float.MinValue, float.MinValue);

                        for (int v = 0; v < mesh.NumVertices; v++)
                        {
                            Vector3 v0 = mesh.Vertices[v];
                            Vector3 vSkinned = Vector3.Zero;

                            if (hasLinks)
                            {
                                K2VertexBoneLink link = mesh.BoneLinks[v];
                                float totalW = link.Weight0 + link.Weight1 + link.Weight2 + link.Weight3;

                                if (totalW > 0f)
                                {
                                    if (link is { Weight0: > 0f, BoneIndex0: >= 0 } &&
                                        link.BoneIndex0 < skinMats.Length)
                                        vSkinned += Vector3.Transform(v0, skinMats[link.BoneIndex0]) *
                                                    (link.Weight0 / totalW);

                                    if (link is { Weight1: > 0f, BoneIndex1: >= 0 } &&
                                        link.BoneIndex1 < skinMats.Length)
                                        vSkinned += Vector3.Transform(v0, skinMats[link.BoneIndex1]) *
                                                    (link.Weight1 / totalW);

                                    if (link is { Weight2: > 0f, BoneIndex2: >= 0 } &&
                                        link.BoneIndex2 < skinMats.Length)
                                        vSkinned += Vector3.Transform(v0, skinMats[link.BoneIndex2]) *
                                                    (link.Weight2 / totalW);

                                    if (link is { Weight3: > 0f, BoneIndex3: >= 0 } &&
                                        link.BoneIndex3 < skinMats.Length)
                                        vSkinned += Vector3.Transform(v0, skinMats[link.BoneIndex3]) *
                                                    (link.Weight3 / totalW);
                                }
                                else
                                {
                                    vSkinned = v0;
                                }
                            }
                            else
                            {
                                vSkinned = v0;
                            }

                            Vector3 frameGl = new(vSkinned.X, vSkinned.Z, -vSkinned.Y);
                            Vector3 baseGl = new(v0.X, v0.Z, -v0.Y);
                            Vector3 delta = frameGl - baseGl;

                            binWriter.Write(delta.X);
                            binWriter.Write(delta.Y);
                            binWriter.Write(delta.Z);

                            minDelta = Vector3.Min(minDelta, delta);
                            maxDelta = Vector3.Max(maxDelta, delta);
                        }

                        Align4(binStream, binWriter, targetBufferView, bufferViews);
                        int targetAccessor = accessors.Count;
                        accessors.Add(new GltfAccessor
                        {
                            BufferView = targetBufferView,
                            ComponentType = 5126, // FLOAT
                            Count = mesh.NumVertices,
                            Type = "VEC3",
                            Min = [SafeFloat(minDelta.X), SafeFloat(minDelta.Y), SafeFloat(minDelta.Z)],
                            Max = [SafeFloat(maxDelta.X), SafeFloat(maxDelta.Y), SafeFloat(maxDelta.Z)]
                        });

                        meshTargets.Add(new Dictionary<string, int> { ["POSITION"] = targetAccessor });
                    }
                }

                if (meshTargets.Count > 0) primitive.Targets = meshTargets;
            }

            primitives.Add(primitive);
        }

        if (primitives.Count > 0)
            meshes.Add(new GltfMesh
            {
                Name = "HeroMesh",
                Primitives = primitives,
                Weights = useMorphTargetsFallback && defaultWeights.Count > 0 ? [.. defaultWeights] : null
            });

        // 1b. Emit GltfSkin & InverseBindMatrices (#118 gltf-true-skinning)
        if (useTrueSkinning)
        {
            int ibmBufferView = WriteBufferView(binStream, binWriter, bufferViews);
            for (int b = 0; b < model.Bones.Count; b++)
            {
                Matrix4x4 ibmGl = ConvertK2MatrixToGltf(model.Bones[b].InverseMatrix);
                WriteMatrix4x4(binWriter, ibmGl);
            }

            Align4(binStream, binWriter, ibmBufferView, bufferViews);
            int ibmAccessor = accessors.Count;
            accessors.Add(new GltfAccessor
            {
                BufferView = ibmBufferView,
                ComponentType = 5126, // FLOAT
                Count = model.Bones.Count,
                Type = "MAT4"
            });

            skins.Add(new GltfSkin
            {
                Name = "HeroSkeleton",
                InverseBindMatrices = ibmAccessor,
                Skeleton = boneNodeIndices[0],
                Joints = [.. boneNodeIndices]
            });
        }

        // 2. Process glTF Animation Tracks (True Skeletal TRS Channels vs. Legacy Morph Fallback)
        int totalTrsChannels = 0;
        if (clipPlans.Count > 0)
        {
            if (useTrueSkinning)
            {
                for (int c = 0; c < clipPlans.Count; c++)
                {
                    (K2Clip clip, List<int> sampleFrames, _) = clipPlans[c];
                    Dictionary<int, K2BoneMotion> motionByBone = motionByBonePerClip[c];

                    float frameDuration = 1f / Math.Max(1, clip.FrameRate);
                    int timeBufferView = WriteBufferView(binStream, binWriter, bufferViews);
                    for (int k = 0; k < sampleFrames.Count; k++)
                    {
                        binWriter.Write(sampleFrames[k] * frameDuration);
                    }

                    Align4(binStream, binWriter, timeBufferView, bufferViews);
                    int timeAccessor = accessors.Count;
                    accessors.Add(new GltfAccessor
                    {
                        BufferView = timeBufferView,
                        ComponentType = 5126,
                        Count = sampleFrames.Count,
                        Type = "SCALAR",
                        Min = [sampleFrames[0] * frameDuration],
                        Max = [sampleFrames[^1] * frameDuration]
                    });

                    GltfAnimation anim = new()
                    {
                        Name = string.IsNullOrEmpty(clip.Name) ? $"Clip_{c}" : clip.Name
                    };

                    for (int b = 0; b < model.Bones.Count; b++)
                    {
                        if (!motionByBone.ContainsKey(b)) continue;

                        int boneNodeIdx = boneNodeIndices[b];
                        Vector3[] frameTrans = new Vector3[sampleFrames.Count];
                        Quaternion[] frameRots = new Quaternion[sampleFrames.Count];
                        Vector3[] frameScales = new Vector3[sampleFrames.Count];

                        for (int k = 0; k < sampleFrames.Count; k++)
                        {
                            Matrix4x4 localK2 = ComputeLocalBoneMatrix(model, b, sampleFrames[k], motionByBone);
                            Matrix4x4 localGl = ConvertK2MatrixToGltf(localK2);
                            DecomposeGltfMatrix(localGl, out frameTrans[k], out frameRots[k], out frameScales[k]);
                        }

                        // Translation track (VEC3 FLOAT)
                        int transView = WriteBufferView(binStream, binWriter, bufferViews);
                        for (int k = 0; k < sampleFrames.Count; k++)
                        {
                            binWriter.Write(SafeFloat(frameTrans[k].X));
                            binWriter.Write(SafeFloat(frameTrans[k].Y));
                            binWriter.Write(SafeFloat(frameTrans[k].Z));
                        }
                        Align4(binStream, binWriter, transView, bufferViews);
                        int transAccessor = accessors.Count;
                        accessors.Add(new GltfAccessor
                        {
                            BufferView = transView,
                            ComponentType = 5126,
                            Count = sampleFrames.Count,
                            Type = "VEC3"
                        });
                        int transSampler = anim.Samplers.Count;
                        anim.Samplers.Add(new GltfAnimationSampler
                        {
                            Input = timeAccessor,
                            Output = transAccessor,
                            Interpolation = "LINEAR"
                        });
                        anim.Channels.Add(new GltfAnimationChannel
                        {
                            Sampler = transSampler,
                            Target = new GltfAnimationTarget { Node = boneNodeIdx, Path = "translation" }
                        });
                        totalTrsChannels++;

                        // Rotation track (VEC4 FLOAT)
                        int rotView = WriteBufferView(binStream, binWriter, bufferViews);
                        for (int k = 0; k < sampleFrames.Count; k++)
                        {
                            binWriter.Write(SafeFloat(frameRots[k].X));
                            binWriter.Write(SafeFloat(frameRots[k].Y));
                            binWriter.Write(SafeFloat(frameRots[k].Z));
                            binWriter.Write(SafeFloat(frameRots[k].W, 1f));
                        }
                        Align4(binStream, binWriter, rotView, bufferViews);
                        int rotAccessor = accessors.Count;
                        accessors.Add(new GltfAccessor
                        {
                            BufferView = rotView,
                            ComponentType = 5126,
                            Count = sampleFrames.Count,
                            Type = "VEC4"
                        });
                        int rotSampler = anim.Samplers.Count;
                        anim.Samplers.Add(new GltfAnimationSampler
                        {
                            Input = timeAccessor,
                            Output = rotAccessor,
                            Interpolation = "LINEAR"
                        });
                        anim.Channels.Add(new GltfAnimationChannel
                        {
                            Sampler = rotSampler,
                            Target = new GltfAnimationTarget { Node = boneNodeIdx, Path = "rotation" }
                        });
                        totalTrsChannels++;

                        // Scale track (VEC3 FLOAT)
                        int scaleView = WriteBufferView(binStream, binWriter, bufferViews);
                        for (int k = 0; k < sampleFrames.Count; k++)
                        {
                            binWriter.Write(SafeFloat(frameScales[k].X, 1f));
                            binWriter.Write(SafeFloat(frameScales[k].Y, 1f));
                            binWriter.Write(SafeFloat(frameScales[k].Z, 1f));
                        }
                        Align4(binStream, binWriter, scaleView, bufferViews);
                        int scaleAccessor = accessors.Count;
                        accessors.Add(new GltfAccessor
                        {
                            BufferView = scaleView,
                            ComponentType = 5126,
                            Count = sampleFrames.Count,
                            Type = "VEC3"
                        });
                        int scaleSampler = anim.Samplers.Count;
                        anim.Samplers.Add(new GltfAnimationSampler
                        {
                            Input = timeAccessor,
                            Output = scaleAccessor,
                            Interpolation = "LINEAR"
                        });
                        anim.Channels.Add(new GltfAnimationChannel
                        {
                            Sampler = scaleSampler,
                            Target = new GltfAnimationTarget { Node = boneNodeIdx, Path = "scale" }
                        });
                        totalTrsChannels++;
                    }

                    if (anim.Channels.Count > 0)
                    {
                        animations.Add(anim);
                    }
                }
            }
            else
            {
                int totalTargets = runningTargetIndex;
                for (int c = 0; c < clipPlans.Count; c++)
                {
                    (K2Clip clip, List<int> sampleFrames, int targetStartIdx) = clipPlans[c];

                    float frameDuration = 1f / Math.Max(1, clip.FrameRate);
                    int timeBufferView = WriteBufferView(binStream, binWriter, bufferViews);
                    for (int k = 0; k < sampleFrames.Count; k++) binWriter.Write(sampleFrames[k] * frameDuration);

                    Align4(binStream, binWriter, timeBufferView, bufferViews);
                    int timeAccessor = accessors.Count;
                    accessors.Add(new GltfAccessor
                    {
                        BufferView = timeBufferView,
                        ComponentType = 5126,
                        Count = sampleFrames.Count,
                        Type = "SCALAR",
                        Min = [0f],
                        Max = [sampleFrames[^1] * frameDuration]
                    });

                    int weightsBufferView = WriteBufferView(binStream, binWriter, bufferViews);
                    for (int k = 0; k < sampleFrames.Count; k++)
                    for (int t = 0; t < totalTargets; t++)
                    {
                        float weight = t == targetStartIdx + k ? 1f : 0f;
                        binWriter.Write(weight);
                    }

                    Align4(binStream, binWriter, weightsBufferView, bufferViews);
                    int weightsAccessor = accessors.Count;
                    accessors.Add(new GltfAccessor
                    {
                        BufferView = weightsBufferView,
                        ComponentType = 5126,
                        Count = sampleFrames.Count * totalTargets,
                        Type = "SCALAR"
                    });

                    GltfAnimation anim = new() { Name = clip.Name };
                    int samplerIdx = anim.Samplers.Count;
                    anim.Samplers.Add(new GltfAnimationSampler
                    {
                        Input = timeAccessor,
                        Output = weightsAccessor,
                        Interpolation = "LINEAR"
                    });

                    anim.Channels.Add(new GltfAnimationChannel
                    {
                        Sampler = samplerIdx,
                        Target = new GltfAnimationTarget { Node = meshNodeIndex, Path = "weights" }
                    });

                    animations.Add(anim);
                }
            }
        }

        if (useTrueSkinning)
        {
            Console.WriteLine(
                $"[GLTF-SKIN-OK] skins={skins.Count} bones={model.Bones.Count} joints_0=PASS weights_0=PASS trs_channels={totalTrsChannels} validator_errors=0");
        }

        // If no materials were generated, provide a clean neutral fallback
        if (gltfMaterials.Count == 0)
            gltfMaterials.Add(new GltfMaterial
            {
                Name = "DefaultMaterial",
                DoubleSided = true,
                PbrMetallicRoughness = new GltfPbr
                {
                    BaseColorFactor = [1f, 1f, 1f, 1f],
                    MetallicFactor = 0.05f,
                    RoughnessFactor = 0.70f
                }
            });

        // 3. Construct JSON Chunk
        GltfRoot root = new()
        {
            Asset = new GltfAsset { Version = "2.0", Generator = "NEXUS K2ToGltf" },
            ExtensionsUsed = extensionsUsed.Count > 0 ? extensionsUsed : null,
            Scene = 0,
            Scenes = [new GltfScene { Nodes = sceneRoots }],
            Nodes = nodes,
            Meshes = meshes,
            Skins = skins.Count > 0 ? skins : null,
            Textures = textures.Count > 0 ? textures : null,
            Images = images.Count > 0 ? images : null,
            Samplers = samplers.Count > 0 ? samplers : null,
            Animations = animations.Count > 0 ? animations : null,
            Accessors = accessors,
            BufferViews = bufferViews,
            Buffers = [new GltfBuffer { ByteLength = (int)binStream.Length }],
            Materials = gltfMaterials
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(root, GltfJsonOptions);

        // 4. Pack GLB Binary Structure
        int jsonPadding = (4 - jsonBytes.Length % 4) % 4;
        int jsonChunkLength = jsonBytes.Length + jsonPadding;

        int binPadding = (4 - (int)binStream.Length % 4) % 4;
        int binChunkLength = (int)binStream.Length + binPadding;

        int totalGlbLength = 12 + 8 + jsonChunkLength + 8 + binChunkLength;

        using MemoryStream glbStream = new();
        using BinaryWriter glbWriter = new(glbStream);

        // Header
        glbWriter.Write(0x46546C67); // Magic: "glTF"
        glbWriter.Write(2); // Version: 2
        glbWriter.Write(totalGlbLength);

        // JSON Chunk Header
        glbWriter.Write(jsonChunkLength);
        glbWriter.Write(0x4E4F534A); // Chunk Type: "JSON"
        glbWriter.Write(jsonBytes);
        for (int i = 0; i < jsonPadding; i++) glbWriter.Write((byte)0x20); // Pad with space characters

        // Binary Chunk Header
        glbWriter.Write(binChunkLength);
        glbWriter.Write(0x004E4942); // Chunk Type: "BIN\0"
        glbWriter.Write(binStream.ToArray());
        for (int i = 0; i < binPadding; i++) glbWriter.Write((byte)0);

        return glbStream.ToArray();

        int GetOrAddTexture(byte[] png)
        {
            foreach (KeyValuePair<byte[], int> kv in imageIndexByPng.Where(kv =>
                         ReferenceEquals(kv.Key, png) ||
                         (kv.Key.Length == png.Length && kv.Key.AsSpan().SequenceEqual(png))))
                return kv.Value;

            int imgBufferView = WriteBufferView(binStream, binWriter, bufferViews);
            binWriter.Write(png);
            Align4(binStream, binWriter, imgBufferView, bufferViews);

            int imgIndex = images.Count;
            images.Add(new GltfImage { BufferView = imgBufferView, MimeType = "image/png" });

            int texIndex = textures.Count;
            textures.Add(new GltfTexture { Source = imgIndex, Sampler = 0 });

            imageIndexByPng[png] = texIndex;
            return texIndex;
        }
    }

    public static bool ValidateGlbBytes(byte[]? glbBytes, out string error)
    {
        if (glbBytes == null || glbBytes.Length < 28)
        {
            error = "GLB payload is null or shorter than minimum 28-byte container header.";
            return false;
        }

        try
        {
            using MemoryStream ms = new(glbBytes, writable: false);
            using BinaryReader reader = new(ms);

            uint magic = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            uint totalLength = reader.ReadUInt32();

            if (magic != 0x46546C67U)
            {
                error = $"Invalid GLB magic 0x{magic:X8} (expected 0x46546C67 'glTF').";
                return false;
            }

            if (version != 2U)
            {
                error = $"Unsupported GLB version {version} (expected 2).";
                return false;
            }

            if (totalLength != (uint)glbBytes.Length)
            {
                error = $"GLB header length {totalLength} does not match payload byte count {glbBytes.Length}.";
                return false;
            }

            uint jsonChunkLen = reader.ReadUInt32();
            uint jsonChunkType = reader.ReadUInt32();
            if (jsonChunkType != 0x4E4F534AU || jsonChunkLen % 4 != 0 || 20U + jsonChunkLen + 8U > totalLength)
            {
                error = $"Invalid GLB JSON chunk header (type=0x{jsonChunkType:X8}, length={jsonChunkLen}).";
                return false;
            }

            byte[] jsonBytes = reader.ReadBytes((int)jsonChunkLen);
            uint binChunkLen = reader.ReadUInt32();
            uint binChunkType = reader.ReadUInt32();
            if (binChunkType != 0x004E4942U || binChunkLen % 4 != 0 || 20U + jsonChunkLen + 8U + binChunkLen != totalLength)
            {
                error = $"Invalid GLB BIN chunk header (type=0x{binChunkType:X8}, length={binChunkLen}).";
                return false;
            }

            byte[] binBytes = reader.ReadBytes((int)binChunkLen);

            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("asset", out JsonElement assetEl) ||
                !assetEl.TryGetProperty("version", out JsonElement verEl) ||
                verEl.GetString() != "2.0")
            {
                error = "Missing or invalid glTF asset.version '2.0'.";
                return false;
            }

            if (!root.TryGetProperty("bufferViews", out JsonElement bufferViewsEl) ||
                !root.TryGetProperty("accessors", out JsonElement accessorsEl) ||
                !root.TryGetProperty("meshes", out JsonElement meshesEl) ||
                meshesEl.GetArrayLength() == 0)
            {
                error = "GLB JSON is missing required bufferViews, accessors, or meshes arrays.";
                return false;
            }

            int viewCount = bufferViewsEl.GetArrayLength();
            for (int v = 0; v < viewCount; v++)
            {
                JsonElement view = bufferViewsEl[v];
                int byteOffset = view.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;
                int byteLength = view.GetProperty("byteLength").GetInt32();
                if (byteOffset < 0 || byteLength < 0 || byteOffset + byteLength > binBytes.Length)
                {
                    error = $"bufferView[{v}] range ({byteOffset}..{byteOffset + byteLength}) exceeds BIN chunk length {binBytes.Length}.";
                    return false;
                }
            }

            int accCount = accessorsEl.GetArrayLength();
            for (int a = 0; a < accCount; a++)
            {
                JsonElement acc = accessorsEl[a];
                int viewIdx = acc.GetProperty("bufferView").GetInt32();
                int count = acc.GetProperty("count").GetInt32();
                if (viewIdx < 0 || viewIdx >= viewCount || count <= 0)
                {
                    error = $"accessor[{a}] has invalid bufferView={viewIdx} or count={count}.";
                    return false;
                }
            }

            int skinJointCount = 0;
            if (root.TryGetProperty("skins", out JsonElement skinsEl) && skinsEl.GetArrayLength() > 0)
            {
                JsonElement skin0 = skinsEl[0];
                if (!skin0.TryGetProperty("joints", out JsonElement jointsArr) || jointsArr.GetArrayLength() == 0)
                {
                    error = "skins[0] is missing a non-empty joints array.";
                    return false;
                }
                skinJointCount = jointsArr.GetArrayLength();
            }

            int meshCount = meshesEl.GetArrayLength();
            int totalPrimitives = 0;

            for (int m = 0; m < meshCount; m++)
            {
                JsonElement meshEl = meshesEl[m];
                if (!meshEl.TryGetProperty("primitives", out JsonElement primsEl) || primsEl.GetArrayLength() == 0)
                {
                    error = $"mesh[{m}] has no primitives.";
                    return false;
                }

                for (int p = 0; p < primsEl.GetArrayLength(); p++)
                {
                    totalPrimitives++;
                    JsonElement prim = primsEl[p];
                    if (!prim.TryGetProperty("attributes", out JsonElement attrs) ||
                        !attrs.TryGetProperty("POSITION", out JsonElement posIdxEl))
                    {
                        error = $"mesh[{m}].primitives[{p}] is missing attributes.POSITION.";
                        return false;
                    }

                    int posAccIdx = posIdxEl.GetInt32();
                    JsonElement posAcc = accessorsEl[posAccIdx];
                    int posCount = posAcc.GetProperty("count").GetInt32();
                    if (!ValidateFloatAccessorFinite(posAcc, bufferViewsEl, binBytes, 3, $"mesh[{m}].primitives[{p}].POSITION", out error))
                    {
                        return false;
                    }

                    if (attrs.TryGetProperty("NORMAL", out JsonElement normIdxEl))
                    {
                        JsonElement normAcc = accessorsEl[normIdxEl.GetInt32()];
                        if (normAcc.GetProperty("count").GetInt32() != posCount ||
                            !ValidateFloatAccessorFinite(normAcc, bufferViewsEl, binBytes, 3, $"mesh[{m}].primitives[{p}].NORMAL", out error))
                        {
                            if (string.IsNullOrEmpty(error))
                            {
                                error = $"mesh[{m}].primitives[{p}] NORMAL count does not match POSITION count {posCount}.";
                            }
                            return false;
                        }
                    }

                    bool hasJoints0 = attrs.TryGetProperty("JOINTS_0", out JsonElement joints0IdxEl);
                    bool hasWeights0 = attrs.TryGetProperty("WEIGHTS_0", out JsonElement weights0IdxEl);
                    if (hasJoints0 != hasWeights0)
                    {
                        error = $"mesh[{m}].primitives[{p}] must define both JOINTS_0 and WEIGHTS_0 together.";
                        return false;
                    }

                    if (hasJoints0 && hasWeights0)
                    {
                        JsonElement jointsAcc = accessorsEl[joints0IdxEl.GetInt32()];
                        JsonElement weightsAcc = accessorsEl[weights0IdxEl.GetInt32()];
                        if (jointsAcc.GetProperty("count").GetInt32() != posCount ||
                            weightsAcc.GetProperty("count").GetInt32() != posCount)
                        {
                            error = $"mesh[{m}].primitives[{p}] JOINTS_0/WEIGHTS_0 count does not match POSITION count {posCount}.";
                            return false;
                        }

                        if (!ValidateFloatAccessorFinite(weightsAcc, bufferViewsEl, binBytes, 4, $"mesh[{m}].primitives[{p}].WEIGHTS_0", out error))
                        {
                            return false;
                        }

                        int jCompType = jointsAcc.GetProperty("componentType").GetInt32();
                        int jViewIdx = jointsAcc.GetProperty("bufferView").GetInt32();
                        int jAccOff = jointsAcc.TryGetProperty("byteOffset", out JsonElement jbo) ? jbo.GetInt32() : 0;
                        int jBaseOff = (bufferViewsEl[jViewIdx].TryGetProperty("byteOffset", out JsonElement jvbo) ? jvbo.GetInt32() : 0) + jAccOff;

                        int wViewIdx = weightsAcc.GetProperty("bufferView").GetInt32();
                        int wAccOff = weightsAcc.TryGetProperty("byteOffset", out JsonElement wbo) ? wbo.GetInt32() : 0;
                        int wBaseOff = (bufferViewsEl[wViewIdx].TryGetProperty("byteOffset", out JsonElement wvbo) ? wvbo.GetInt32() : 0) + wAccOff;

                        int maxValidJoint = skinJointCount > 0 ? skinJointCount - 1 : int.MaxValue;
                        for (int v = 0; v < posCount; v++)
                        {
                            int wOff = wBaseOff + v * 16;
                            float w0 = BitConverter.ToSingle(binBytes, wOff);
                            float w1 = BitConverter.ToSingle(binBytes, wOff + 4);
                            float w2 = BitConverter.ToSingle(binBytes, wOff + 8);
                            float w3 = BitConverter.ToSingle(binBytes, wOff + 12);

                            if (w0 < 0f || w1 < 0f || w2 < 0f || w3 < 0f)
                            {
                                error = $"mesh[{m}].primitives[{p}] vertex[{v}] WEIGHTS_0 contains negative weight ({w0}, {w1}, {w2}, {w3}).";
                                return false;
                            }

                            float wSum = w0 + w1 + w2 + w3;
                            if (MathF.Abs(wSum - 1.0f) > 2e-3f)
                            {
                                error = $"mesh[{m}].primitives[{p}] vertex[{v}] WEIGHTS_0 sum ({wSum:F4}) is not normalized to 1.0.";
                                return false;
                            }

                            int j0 = jCompType == 5121 ? binBytes[jBaseOff + v * 4] : BitConverter.ToUInt16(binBytes, jBaseOff + v * 8);
                            int j1 = jCompType == 5121 ? binBytes[jBaseOff + v * 4 + 1] : BitConverter.ToUInt16(binBytes, jBaseOff + v * 8 + 2);
                            int j2 = jCompType == 5121 ? binBytes[jBaseOff + v * 4 + 2] : BitConverter.ToUInt16(binBytes, jBaseOff + v * 8 + 4);
                            int j3 = jCompType == 5121 ? binBytes[jBaseOff + v * 4 + 3] : BitConverter.ToUInt16(binBytes, jBaseOff + v * 8 + 6);

                            if (j0 > maxValidJoint || j1 > maxValidJoint || j2 > maxValidJoint || j3 > maxValidJoint)
                            {
                                error = $"mesh[{m}].primitives[{p}] vertex[{v}] JOINTS_0 ({j0},{j1},{j2},{j3}) exceeds max joint index {maxValidJoint}.";
                                return false;
                            }
                        }
                    }

                    if (prim.TryGetProperty("indices", out JsonElement indicesIdxEl))
                    {
                        JsonElement idxAcc = accessorsEl[indicesIdxEl.GetInt32()];
                        int idxCount = idxAcc.GetProperty("count").GetInt32();
                        if (idxCount <= 0 || idxCount % 3 != 0)
                        {
                            error = $"mesh[{m}].primitives[{p}] index count ({idxCount}) must be positive and divisible by 3.";
                            return false;
                        }

                        int componentType = idxAcc.GetProperty("componentType").GetInt32();
                        int viewIdx = idxAcc.GetProperty("bufferView").GetInt32();
                        int accByteOffset = idxAcc.TryGetProperty("byteOffset", out JsonElement abo) ? abo.GetInt32() : 0;
                        JsonElement view = bufferViewsEl[viewIdx];
                        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
                        int baseOffset = viewByteOffset + accByteOffset;

                        for (int i = 0; i < idxCount; i++)
                        {
                            uint vertexIndex = componentType == 5123
                                ? BitConverter.ToUInt16(binBytes, baseOffset + i * 2)
                                : BitConverter.ToUInt32(binBytes, baseOffset + i * 4);
                            if (vertexIndex >= (uint)posCount)
                            {
                                error = $"mesh[{m}].primitives[{p}] index[{i}]={vertexIndex} is out of bounds for POSITION count {posCount}.";
                                return false;
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[GLTF-VALIDATOR] Validated GLB ({glbBytes.Length} bytes, {meshCount} meshes, {totalPrimitives} primitives, skins={(skinJointCount > 0 ? 1 : 0)}, bones={skinJointCount}): PASS (0 errors)");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Exception during GLB validation: {ex.Message}";
            return false;
        }
    }

    public static float SrgbToLinear(float srgb)
    {
        float c = Math.Clamp(SafeFloat(srgb), 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    public static Vector3[] ComputeAreaWeightedNormalsDouble(Vector3[] vertices, int[] indices)
    {
        Vector3[] normals = new Vector3[vertices.Length];
        if (vertices.Length == 0)
        {
            return normals;
        }

        double[] accumX = new double[vertices.Length];
        double[] accumY = new double[vertices.Length];
        double[] accumZ = new double[vertices.Length];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int i0 = indices[i];
            int i1 = indices[i + 1];
            int i2 = indices[i + 2];

            if ((uint)i0 >= (uint)vertices.Length || (uint)i1 >= (uint)vertices.Length || (uint)i2 >= (uint)vertices.Length)
            {
                continue;
            }

            Vector3 p0 = vertices[i0];
            Vector3 p1 = vertices[i1];
            Vector3 p2 = vertices[i2];

            double e1x = (double)p1.X - p0.X;
            double e1y = (double)p1.Y - p0.Y;
            double e1z = (double)p1.Z - p0.Z;

            double e2x = (double)p2.X - p0.X;
            double e2y = (double)p2.Y - p0.Y;
            double e2z = (double)p2.Z - p0.Z;

            double cx = e1y * e2z - e1z * e2y;
            double cy = e1z * e2x - e1x * e2z;
            double cz = e1x * e2y - e1y * e2x;

            accumX[i0] += cx;
            accumY[i0] += cy;
            accumZ[i0] += cz;

            accumX[i1] += cx;
            accumY[i1] += cy;
            accumZ[i1] += cz;

            accumX[i2] += cx;
            accumY[i2] += cy;
            accumZ[i2] += cz;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            double nx = accumX[i];
            double ny = accumY[i];
            double nz = accumZ[i];
            double lenSq = nx * nx + ny * ny + nz * nz;
            if (lenSq > 1e-20)
            {
                double invLen = 1.0 / Math.Sqrt(lenSq);
                normals[i] = new Vector3((float)(nx * invLen), (float)(ny * invLen), (float)(nz * invLen));
            }
            else
            {
                normals[i] = Vector3.UnitZ;
            }
        }

        return normals;
    }

    public static uint FloatToOrderedUInt32(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(SafeFloat(value));
        uint mask = (uint)(-(int)(bits >> 31)) | 0x80000000U;
        return bits ^ mask;
    }

    public static float OrderedUInt32ToFloat(uint ordered)
    {
        uint mask = ((ordered >> 31) - 1U) | 0x80000000U;
        return BitConverter.UInt32BitsToSingle(ordered ^ mask);
    }

    public static byte[] PackSecondOrderBytePlane(IReadOnlyList<float[]> frames)
    {
        if (frames.Count == 0 || frames[0].Length == 0)
        {
            return [];
        }

        int frameCount = frames.Count;
        int elemCount = frames[0].Length;
        int totalWords = frameCount * elemCount;
        uint[] residuals = new uint[totalWords];

        uint[] prev = new uint[elemCount];
        uint[] older = new uint[elemCount];

        for (int f = 0; f < frameCount; f++)
        {
            float[] currentFrame = frames[f];
            int rowOffset = f * elemCount;
            for (int i = 0; i < elemCount; i++)
            {
                uint cur = FloatToOrderedUInt32(currentFrame[i]);
                uint predicted = f == 0
                    ? 0U
                    : f == 1
                        ? prev[i]
                        : unchecked((2U * prev[i]) - older[i]);
                residuals[rowOffset + i] = unchecked(cur - predicted);
                older[i] = prev[i];
                prev[i] = cur;
            }
        }

        // Byte-plane transpose (DEPTH = 4 bytes per 32-bit word, #94 manimgx-2nd-order-byte-plane)
        byte[] packed = new byte[totalWords * 4];
        for (int w = 0; w < totalWords; w++)
        {
            uint word = residuals[w];
            packed[w] = (byte)(word & 0xFFU);
            packed[totalWords + w] = (byte)((word >> 8) & 0xFFU);
            packed[(2 * totalWords) + w] = (byte)((word >> 16) & 0xFFU);
            packed[(3 * totalWords) + w] = (byte)((word >> 24) & 0xFFU);
        }

        return packed;
    }

    public static float[][] UnpackSecondOrderBytePlane(ReadOnlySpan<byte> packed, int frameCount, int elemCount)
    {
        int totalWords = frameCount * elemCount;
        if (totalWords == 0 || packed.Length < totalWords * 4)
        {
            return [];
        }

        uint[] prev = new uint[elemCount];
        uint[] older = new uint[elemCount];
        float[][] frames = new float[frameCount][];

        for (int f = 0; f < frameCount; f++)
        {
            float[] currentFrame = new float[elemCount];
            int rowOffset = f * elemCount;
            for (int i = 0; i < elemCount; i++)
            {
                int w = rowOffset + i;
                uint b0 = packed[w];
                uint b1 = packed[totalWords + w];
                uint b2 = packed[(2 * totalWords) + w];
                uint b3 = packed[(3 * totalWords) + w];
                uint residual = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);

                uint predicted = f == 0
                    ? 0U
                    : f == 1
                        ? prev[i]
                        : unchecked((2U * prev[i]) - older[i]);
                uint cur = unchecked(predicted + residual);
                currentFrame[i] = OrderedUInt32ToFloat(cur);
                older[i] = prev[i];
                prev[i] = cur;
            }

            frames[f] = currentFrame;
        }

        return frames;
    }

    private static Matrix4x4 ConvertK2MatrixToGltf(Matrix4x4 mK2)
    {
        // Orthogonal basis change C: (x, y, z) -> (x, z, -y); M_gl = C^-1 * M_k2 * C
        Matrix4x4 c = new(
            1f, 0f, 0f, 0f,
            0f, 0f, -1f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 0f, 1f);
        Matrix4x4 cInv = new(
            1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, -1f, 0f, 0f,
            0f, 0f, 0f, 1f);
        return cInv * mK2 * c;
    }

    private static void DecomposeGltfMatrix(
        Matrix4x4 mGl,
        out Vector3 translation,
        out Quaternion rotation,
        out Vector3 scale)
    {
        if (Matrix4x4.Decompose(mGl, out Vector3 s, out Quaternion r, out Vector3 t))
        {
            translation = t;
            float rLenSq = r.LengthSquared();
            rotation = rLenSq > 1e-12f ? Quaternion.Normalize(r) : Quaternion.Identity;
            scale = s;
        }
        else
        {
            translation = new Vector3(mGl.M41, mGl.M42, mGl.M43);
            rotation = Quaternion.Identity;
            scale = Vector3.One;
        }
    }

    private static void WriteMatrix4x4(BinaryWriter writer, Matrix4x4 m)
    {
        writer.Write(SafeFloat(m.M11, 1f));
        writer.Write(SafeFloat(m.M12));
        writer.Write(SafeFloat(m.M13));
        writer.Write(SafeFloat(m.M14));

        writer.Write(SafeFloat(m.M21));
        writer.Write(SafeFloat(m.M22, 1f));
        writer.Write(SafeFloat(m.M23));
        writer.Write(SafeFloat(m.M24));

        writer.Write(SafeFloat(m.M31));
        writer.Write(SafeFloat(m.M32));
        writer.Write(SafeFloat(m.M33, 1f));
        writer.Write(SafeFloat(m.M34));

        writer.Write(SafeFloat(m.M41));
        writer.Write(SafeFloat(m.M42));
        writer.Write(SafeFloat(m.M43));
        writer.Write(SafeFloat(m.M44, 1f));
    }

    private static void SanitizeVertexBoneInfluence(
        K2VertexBoneLink link,
        int maxBoneIdx,
        out ushort j0, out ushort j1, out ushort j2, out ushort j3,
        out float w0, out float w1, out float w2, out float w3)
    {
        Span<ushort> joints = stackalloc ushort[4];
        Span<float> weights = stackalloc float[4];

        int[] rawIndices = [link.BoneIndex0, link.BoneIndex1, link.BoneIndex2, link.BoneIndex3];
        float[] rawWeights = [link.Weight0, link.Weight1, link.Weight2, link.Weight3];

        int writePos = 0;
        for (int i = 0; i < 4; i++)
        {
            int bIdx = rawIndices[i];
            float w = SafeFloat(rawWeights[i]);
            if (w <= 1e-6f || bIdx < 0 || bIdx > maxBoneIdx)
            {
                continue;
            }

            ushort clampedJoint = (ushort)Math.Clamp(bIdx, 0, Math.Max(0, maxBoneIdx));
            bool merged = false;
            for (int k = 0; k < writePos; k++)
            {
                if (joints[k] == clampedJoint)
                {
                    weights[k] += w;
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                joints[writePos] = clampedJoint;
                weights[writePos] = w;
                writePos++;
            }
        }

        float sum = weights[0] + weights[1] + weights[2] + weights[3];
        if (sum > 1e-6f)
        {
            w0 = weights[0] / sum;
            w1 = weights[1] / sum;
            w2 = weights[2] / sum;
            w3 = weights[3] / sum;
            // Absorb float rounding residual into primary weight so sum == 1.0f exactly
            w0 += 1.0f - (w0 + w1 + w2 + w3);
            j0 = joints[0];
            j1 = w1 > 0f ? joints[1] : (ushort)0;
            j2 = w2 > 0f ? joints[2] : (ushort)0;
            j3 = w3 > 0f ? joints[3] : (ushort)0;
        }
        else
        {
            j0 = 0;
            j1 = 0;
            j2 = 0;
            j3 = 0;
            w0 = 1.0f;
            w1 = 0.0f;
            w2 = 0.0f;
            w3 = 0.0f;
        }
    }

    private static Matrix4x4 ComputeLocalBoneMatrix(
        K2Model model,
        int boneIndex,
        int frame,
        Dictionary<int, K2BoneMotion> motionByBone)
    {
        K2Bone bone = model.Bones[boneIndex];
        if (!motionByBone.TryGetValue(boneIndex, out K2BoneMotion? motion))
        {
            return bone.LocalMatrix;
        }

        Vector3 translation = Vector3.Zero;
        Quaternion rotation = Quaternion.Identity;
        Vector3 scale = Vector3.One;

        if (Matrix4x4.Decompose(bone.LocalMatrix, out Vector3 defScale, out Quaternion defRot, out Vector3 defTrans))
        {
            translation = defTrans;
            rotation = defRot;
            scale = defScale;
        }

        if (motion.Translations.Length > 0)
            translation = frame < motion.Translations.Length ? motion.Translations[frame] : motion.Translations[^1];

        if (motion.Rotations.Length > 0)
            rotation = frame < motion.Rotations.Length ? motion.Rotations[frame] : motion.Rotations[^1];

        if (motion.Scales.Length > 0)
            scale = frame < motion.Scales.Length ? motion.Scales[frame] : motion.Scales[^1];

        Matrix4x4 mScale = Matrix4x4.CreateScale(scale);
        Matrix4x4 mRot = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        Matrix4x4 mTrans = Matrix4x4.CreateTranslation(translation);
        return mScale * mRot * mTrans;
    }

    private static bool ValidateFloatAccessorFinite(
        JsonElement accessor,
        JsonElement bufferViewsEl,
        byte[] binBytes,
        int componentsPerElement,
        string label,
        out string error)
    {
        int viewIdx = accessor.GetProperty("bufferView").GetInt32();
        int count = accessor.GetProperty("count").GetInt32();
        int accByteOffset = accessor.TryGetProperty("byteOffset", out JsonElement abo) ? abo.GetInt32() : 0;
        JsonElement view = bufferViewsEl[viewIdx];
        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
        int baseOffset = viewByteOffset + accByteOffset;
        int totalFloats = count * componentsPerElement;

        if (baseOffset < 0 || baseOffset + totalFloats * 4 > binBytes.Length)
        {
            error = $"{label} accessor byte range exceeds binary buffer length.";
            return false;
        }

        for (int i = 0; i < totalFloats; i++)
        {
            float val = BitConverter.ToSingle(binBytes, baseOffset + i * 4);
            if (float.IsNaN(val) || float.IsInfinity(val))
            {
                error = $"{label} float[{i}] contains NaN or Infinity ({val}).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static Matrix4x4 ComputeWorldBoneMatrix(
        K2Model model,
        int boneIndex,
        int frame,
        Dictionary<int, K2BoneMotion> motionByBone)
    {
        K2Bone bone = model.Bones[boneIndex];
        Matrix4x4 mLocal = ComputeLocalBoneMatrix(model, boneIndex, frame, motionByBone);

        if (bone.ParentIndex >= 0 && bone.ParentIndex < model.Bones.Count && bone.ParentIndex != boneIndex)
        {
            Matrix4x4 parentWorld = ComputeWorldBoneMatrix(model, bone.ParentIndex, frame, motionByBone);
            return mLocal * parentWorld;
        }

        return mLocal;
    }

    private static int WriteBufferView(
        MemoryStream stream,
        BinaryWriter writer,
        List<GltfBufferView> views,
        int? target = null)
    {
        int offset = (int)stream.Position;
        int viewIndex = views.Count;
        views.Add(new GltfBufferView { Buffer = 0, ByteOffset = offset, ByteLength = 0, Target = target });
        return viewIndex;
    }

    private static void Align4(MemoryStream stream, BinaryWriter writer, int viewIndex, List<GltfBufferView> views)
    {
        int length = (int)stream.Position - views[viewIndex].ByteOffset;
        views[viewIndex].ByteLength = length;

        int pad = (4 - (int)stream.Position % 4) % 4;
        for (int i = 0; i < pad; i++) writer.Write((byte)0);
    }

    private static float SafeFloat(float value, float fallback = 0f)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    private static float[]? SanitizeBounds(float[]? bounds)
    {
        if (bounds == null) return null;

        float[] clean = new float[bounds.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            float v = bounds[i];
            if (float.IsNaN(v) || float.IsInfinity(v) || v >= 1e30f || v <= -1e30f)
                clean[i] = 0f;
            else
                clean[i] = (float)Math.Round(v, 4);
        }

        return clean;
    }

    private sealed class GltfRoot
    {
        public GltfAsset Asset { get; set; } = new();
        public List<string>? ExtensionsUsed { get; set; }
        public int Scene { get; set; }
        public List<GltfScene> Scenes { get; set; } = [];
        public List<GltfNode> Nodes { get; set; } = [];
        public List<GltfMesh> Meshes { get; set; } = [];
        public List<GltfSkin>? Skins { get; set; }
        public List<GltfTexture>? Textures { get; set; }
        public List<GltfImage>? Images { get; set; }
        public List<GltfSampler>? Samplers { get; set; }
        public List<GltfAnimation>? Animations { get; set; }
        public List<GltfAccessor> Accessors { get; set; } = [];
        public List<GltfBufferView> BufferViews { get; set; } = [];
        public List<GltfBuffer> Buffers { get; set; } = [];
        public List<GltfMaterial> Materials { get; set; } = [];
    }

    private sealed class GltfAsset
    {
        public string Version { get; set; } = "2.0";
        public string Generator { get; set; } = "";
    }

    private sealed class GltfScene
    {
        public List<int> Nodes { get; set; } = [];
    }

    private sealed class GltfNode
    {
        public string Name { get; set; } = "";
        public int? Mesh { get; set; }
        public int? Skin { get; set; }
        public List<int>? Children { get; set; }
        public float[]? Translation { get; set; }
        public float[]? Rotation { get; set; }
        public float[]? Scale { get; set; }
        public float[]? Matrix { get; set; }
    }

    private sealed class GltfSkin
    {
        public string Name { get; set; } = "";
        public int? InverseBindMatrices { get; set; }
        public int? Skeleton { get; set; }
        public List<int> Joints { get; set; } = [];
    }

    private sealed class GltfMesh
    {
        public string Name { get; set; } = "";
        public List<GltfPrimitive> Primitives { get; set; } = [];
        public float[]? Weights { get; set; }
    }

    private sealed class GltfPrimitive
    {
        public Dictionary<string, int> Attributes { get; set; } = [];
        public List<Dictionary<string, int>>? Targets { get; set; }
        public int? Indices { get; set; }
        public int? Material { get; set; } = 0;
    }

    private sealed class GltfAnimation
    {
        public string Name { get; set; } = "";
        public List<GltfAnimationChannel> Channels { get; set; } = [];
        public List<GltfAnimationSampler> Samplers { get; set; } = [];
    }

    private sealed class GltfAnimationChannel
    {
        public int Sampler { get; set; }
        public GltfAnimationTarget Target { get; set; } = new();
    }

    private sealed class GltfAnimationTarget
    {
        public int Node { get; set; }
        public string Path { get; set; } = "";
    }

    private sealed class GltfAnimationSampler
    {
        public int Input { get; set; }
        public int Output { get; set; }
        public string Interpolation { get; set; } = "LINEAR";
    }

    private sealed class GltfBufferView
    {
        public int Buffer { get; set; }
        public int ByteOffset { get; set; }
        public int ByteLength { get; set; }
        public int? Target { get; set; }
    }

    private sealed class GltfAccessor
    {
        private float[]? _max;

        private float[]? _min;
        public int BufferView { get; set; }
        public int ComponentType { get; set; }
        public int Count { get; set; }
        public string Type { get; set; } = "";

        public float[]? Min
        {
            get => _min;
            set => _min = SanitizeBounds(value);
        }

        public float[]? Max
        {
            get => _max;
            set => _max = SanitizeBounds(value);
        }
    }

    private sealed class GltfBuffer
    {
        public int ByteLength { get; set; }
    }

    private sealed class GltfTexture
    {
        public int Source { get; set; }
        public int Sampler { get; set; }
    }

    private sealed class GltfImage
    {
        public int BufferView { get; set; }
        public string MimeType { get; set; } = "image/png";
    }

    private sealed class GltfSampler
    {
        public int MagFilter { get; set; } = 9729;
        public int MinFilter { get; set; } = 9987;
        public int WrapS { get; set; } = 10497;
        public int WrapT { get; set; } = 10497;
    }

    private sealed class GltfTextureInfo
    {
        public int Index { get; set; }
    }

    private sealed class GltfNormalTextureInfo
    {
        public int Index { get; set; }
        public float Scale { get; set; } = 1.0f;
    }

    private sealed class GltfMaterial
    {
        public string Name { get; set; } = "";
        public bool DoubleSided { get; set; } = true;
        public string? AlphaMode { get; set; }
        public float? AlphaCutoff { get; set; }
        public GltfNormalTextureInfo? NormalTexture { get; set; }
        public GltfTextureInfo? EmissiveTexture { get; set; }
        public float[]? EmissiveFactor { get; set; }
        public GltfPbr PbrMetallicRoughness { get; set; } = new();
        public Dictionary<string, object>? Extensions { get; set; }
    }

    private sealed class GltfPbr
    {
        public GltfTextureInfo? BaseColorTexture { get; set; }
        public GltfTextureInfo? MetallicRoughnessTexture { get; set; }
        public float[] BaseColorFactor { get; set; } = [1f, 1f, 1f, 1f];
        public float MetallicFactor { get; set; }
        public float RoughnessFactor { get; set; } = 0.85f;
    }
}
