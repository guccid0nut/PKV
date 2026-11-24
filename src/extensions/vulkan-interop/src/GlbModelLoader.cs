// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;
using System.Text;
using System.Text.Json;
using ImageMagick;

namespace ReplayVulkanRenderer;

public struct Vertex3D
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Vector4 JointIndices;
    public Vector4 JointWeights;
}

public sealed class GlbMeshData
{
    public required string Name { get; set; }
    public required Vertex3D[] Vertices { get; set; }
    public required uint[] Indices { get; set; }
    public Vector4 BaseColor { get; set; } = new(0.85f, 0.85f, 0.85f, 1.0f);
    public byte[]? TextureRgbaPixels { get; set; }
    public int TextureWidth { get; set; }
    public int TextureHeight { get; set; }
}

public static class GlbModelLoader
{
    public static GlbMeshData? LoadGlbFile(string filePath)
    {
        Console.WriteLine($"[GlbModelLoader] Loading 3D GLB Asset: {filePath}");

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[GlbModelLoader] WARNING: GLB file missing at: {filePath}");
            return null;
        }

        try
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            if (fileBytes.Length < 20) return null;

            using BinaryReader reader = new(new MemoryStream(fileBytes));
            uint magic = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            uint length = reader.ReadUInt32();

            if (magic != 0x46546C67) // "glTF"
            {
                Console.WriteLine($"[GlbModelLoader] ERROR: Invalid glTF magic header in '{filePath}'");
                return null;
            }

            // Chunk 0: JSON
            uint jsonChunkLen = reader.ReadUInt32();
            uint jsonChunkType = reader.ReadUInt32();
            byte[] jsonBytes = reader.ReadBytes((int)jsonChunkLen);
            string jsonText = Encoding.UTF8.GetString(jsonBytes);

            // Chunk 1: BIN
            uint binChunkLen = reader.ReadUInt32();
            uint binChunkType = reader.ReadUInt32();
            byte[] binBytes = reader.ReadBytes((int)binChunkLen);

            using JsonDocument doc = JsonDocument.Parse(jsonText);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("meshes", out JsonElement meshesEl) || meshesEl.GetArrayLength() == 0)
            {
                Console.WriteLine("[GlbModelLoader] ERROR: No meshes array in glTF JSON.");
                return null;
            }

            JsonElement accessors = root.GetProperty("accessors");
            JsonElement bufferViews = root.GetProperty("bufferViews");

            List<(int MeshIndex, Matrix4x4 WorldMatrix)> meshInstances = [];
            HashSet<int> referencedMeshes = [];

            if (root.TryGetProperty("nodes", out JsonElement nodesEl) && nodesEl.GetArrayLength() > 0)
            {
                HashSet<int> visitedNodes = [];
                if (root.TryGetProperty("scenes", out JsonElement scenesEl) && scenesEl.GetArrayLength() > 0)
                {
                    int sceneIdx = root.TryGetProperty("scene", out JsonElement sceneIdxEl) ? sceneIdxEl.GetInt32() : 0;
                    if (sceneIdx < 0 || sceneIdx >= scenesEl.GetArrayLength())
                    {
                        sceneIdx = 0;
                    }

                    JsonElement activeScene = scenesEl[sceneIdx];
                    if (activeScene.TryGetProperty("nodes", out JsonElement rootNodesEl))
                    {
                        for (int n = 0; n < rootNodesEl.GetArrayLength(); n++)
                        {
                            int rootNodeIdx = rootNodesEl[n].GetInt32();
                            CollectNodeMeshInstances(nodesEl, rootNodeIdx, Matrix4x4.Identity, meshInstances, referencedMeshes, visitedNodes);
                        }
                    }
                }
                else
                {
                    for (int n = 0; n < nodesEl.GetArrayLength(); n++)
                    {
                        if (!visitedNodes.Contains(n))
                        {
                            CollectNodeMeshInstances(nodesEl, n, Matrix4x4.Identity, meshInstances, referencedMeshes, visitedNodes);
                        }
                    }
                }
            }

            for (int m = 0; m < meshesEl.GetArrayLength(); m++)
            {
                if (!referencedMeshes.Contains(m))
                {
                    meshInstances.Add((m, Matrix4x4.Identity));
                }
            }

            List<Vector3> allPositions = [];
            List<Vector3> allNormals = [];
            List<Vector2> allUvs = [];
            List<Vector4> allJoints = [];
            List<Vector4> allWeights = [];
            List<bool> allHasExplicitSkinWeights = [];
            List<uint> allIndices = [];

            int totalPrimitivesLoaded = 0;
            bool computedFallbackNormals = false;

            for (int instIdx = 0; instIdx < meshInstances.Count; instIdx++)
            {
                (int meshIndex, Matrix4x4 worldMatrix) = meshInstances[instIdx];
                if (meshIndex < 0 || meshIndex >= meshesEl.GetArrayLength())
                {
                    continue;
                }

                JsonElement meshEl = meshesEl[meshIndex];
                if (!meshEl.TryGetProperty("primitives", out JsonElement primitives) || primitives.GetArrayLength() == 0)
                {
                    continue;
                }

                Matrix4x4 normalMatrix = Matrix4x4.Invert(worldMatrix, out Matrix4x4 invWorld)
                    ? Matrix4x4.Transpose(invWorld)
                    : worldMatrix;

                for (int primIdx = 0; primIdx < primitives.GetArrayLength(); primIdx++)
                {
                    JsonElement primitive = primitives[primIdx];
                    if (!primitive.TryGetProperty("attributes", out JsonElement attributes) ||
                        !attributes.TryGetProperty("POSITION", out JsonElement posAttrEl))
                    {
                        continue;
                    }

                    int posAccessorIdx = posAttrEl.GetInt32();
                    int? normAccessorIdx = attributes.TryGetProperty("NORMAL", out JsonElement normEl)
                        ? normEl.GetInt32()
                        : null;
                    int? uvAccessorIdx = attributes.TryGetProperty("TEXCOORD_0", out JsonElement uvEl)
                        ? uvEl.GetInt32()
                        : null;
                    int? jointsAccessorIdx = attributes.TryGetProperty("JOINTS_0", out JsonElement jointEl)
                        ? jointEl.GetInt32()
                        : null;
                    int? weightsAccessorIdx = attributes.TryGetProperty("WEIGHTS_0", out JsonElement weightEl)
                        ? weightEl.GetInt32()
                        : null;
                    int? indicesAccessorIdx = primitive.TryGetProperty("indices", out JsonElement idxEl)
                        ? idxEl.GetInt32()
                        : null;

                    Vector3[] positions = ReadVector3Array(accessors[posAccessorIdx], bufferViews, binBytes);
                    uint[] primIndices;
                    if (indicesAccessorIdx.HasValue)
                    {
                        primIndices = ReadIndexArray(accessors[indicesAccessorIdx.Value], bufferViews, binBytes);
                    }
                    else
                    {
                        primIndices = new uint[positions.Length];
                        for (uint vi = 0; vi < (uint)positions.Length; vi++)
                        {
                            primIndices[vi] = vi;
                        }
                    }

                    Vector3[] normals;
                    if (normAccessorIdx.HasValue)
                    {
                        normals = ReadVector3Array(accessors[normAccessorIdx.Value], bufferViews, binBytes);
                    }
                    else
                    {
                        normals = CalculateMeshNormals(positions, primIndices);
                        computedFallbackNormals = true;
                    }

                    Vector2[] uvs = uvAccessorIdx.HasValue
                        ? ReadVector2Array(accessors[uvAccessorIdx.Value], bufferViews, binBytes)
                        : new Vector2[positions.Length];
                    Vector4[] joints = jointsAccessorIdx.HasValue
                        ? ReadJointsArray(accessors[jointsAccessorIdx.Value], bufferViews, binBytes)
                        : new Vector4[positions.Length];
                    Vector4[] weights = weightsAccessorIdx.HasValue
                        ? ReadWeightsArray(accessors[weightsAccessorIdx.Value], bufferViews, binBytes)
                        : new Vector4[positions.Length];

                    uint baseVertexOffset = (uint)allPositions.Count;
                    bool hasExplicitSkin = jointsAccessorIdx.HasValue && weightsAccessorIdx.HasValue;

                    for (int i = 0; i < positions.Length; i++)
                    {
                        Vector3 worldPos = Vector3.Transform(positions[i], worldMatrix);
                        Vector3 rawNormal = i < normals.Length ? normals[i] : Vector3.UnitY;
                        Vector3 transformedNormal = Vector3.TransformNormal(rawNormal, normalMatrix);
                        float normalLenSq = transformedNormal.LengthSquared();
                        Vector3 worldNormal = normalLenSq > 1e-12f
                            ? transformedNormal / MathF.Sqrt(normalLenSq)
                            : Vector3.UnitY;

                        allPositions.Add(worldPos);
                        allNormals.Add(worldNormal);
                        allUvs.Add(i < uvs.Length ? uvs[i] : Vector2.Zero);
                        allJoints.Add(i < joints.Length ? joints[i] : Vector4.Zero);
                        allWeights.Add(i < weights.Length ? weights[i] : Vector4.Zero);
                        allHasExplicitSkinWeights.Add(hasExplicitSkin);
                    }

                    for (int i = 0; i < primIndices.Length; i++)
                    {
                        allIndices.Add(baseVertexOffset + primIndices[i]);
                    }

                    totalPrimitivesLoaded++;
                }
            }

            if (allPositions.Count == 0)
            {
                Console.WriteLine("[GlbModelLoader] ERROR: No valid vertex positions found in GLB meshes.");
                return null;
            }

            // Compute bounding box heights for anatomical skeletal joint assignment
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;
            for (int i = 0; i < allPositions.Count; i++)
            {
                minZ = Math.Min(minZ, allPositions[i].Z);
                maxZ = Math.Max(maxZ, allPositions[i].Z);
            }

            float meshHeight = Math.Max(0.001f, maxZ - minZ);

            Vertex3D[] vertices = new Vertex3D[allPositions.Count];
            for (int i = 0; i < allPositions.Count; i++)
            {
                Vector3 pos = allPositions[i];
                float normalizedHeight = (pos.Z - minZ) / meshHeight;

                Vector4 jIdx;
                Vector4 jWgt;

                if (allHasExplicitSkinWeights[i] &&
                    allWeights[i].X + allWeights[i].Y + allWeights[i].Z + allWeights[i].W > 0.01f)
                {
                    jIdx = allJoints[i];
                    jWgt = allWeights[i];
                }
                else
                {
                    // Assign Anatomical Skeletal Joint Weights based on mesh height & width
                    if (normalizedHeight < 0.40f) // Lower body (Legs, Feet)
                    {
                        int legJoint = pos.X < 0 ? 5 : 10; // 5 = Left Leg, 10 = Right Leg
                        jIdx = new Vector4(legJoint, 0, 0, 0);
                        jWgt = new Vector4(1f, 0f, 0f, 0f);
                    }
                    else if (normalizedHeight < 0.70f) // Mid Body (Spine, Pelvis)
                    {
                        if (Math.Abs(pos.X) > 0.35f) // Outer arms/shoulders
                        {
                            int armJoint = pos.X < 0 ? 15 : 23; // 15 = Left Arm, 23 = Right Arm
                            jIdx = new Vector4(armJoint, 1, 0, 0);
                            jWgt = new Vector4(0.85f, 0.15f, 0f, 0f);
                        }
                        else
                        {
                            jIdx = new Vector4(1, 0, 0, 0); // Spine / Chest
                            jWgt = new Vector4(1f, 0f, 0f, 0f);
                        }
                    }
                    else // Upper Body (Head, Neck, Shoulders)
                    {
                        if (Math.Abs(pos.X) > 0.30f) // Shoulders/Arms
                        {
                            int armJoint = pos.X < 0 ? 15 : 23;
                            jIdx = new Vector4(armJoint, 1, 0, 0);
                            jWgt = new Vector4(0.90f, 0.10f, 0f, 0f);
                        }
                        else // Head / Neck
                        {
                            jIdx = new Vector4(2, 1, 0, 0); // Head
                            jWgt = new Vector4(0.85f, 0.15f, 0f, 0f);
                        }
                    }
                }

                vertices[i] = new Vertex3D
                {
                    Position = pos,
                    Normal = allNormals[i],
                    TexCoord = allUvs[i],
                    JointIndices = jIdx,
                    JointWeights = jWgt
                };
            }

            uint[] indices = allIndices.ToArray();

            // Extract & Decode Embedded PNG Texture Map
            byte[]? texturePixels = null;
            int texWidth = 0;
            int texHeight = 0;

            if (root.TryGetProperty("images", out JsonElement imagesEl) && imagesEl.GetArrayLength() > 0)
            {
                JsonElement imgEl = imagesEl[0];
                if (imgEl.TryGetProperty("bufferView", out JsonElement imgBufViewIdx))
                {
                    int viewIdx = imgBufViewIdx.GetInt32();
                    JsonElement view = bufferViews[viewIdx];
                    int byteOffset = view.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;
                    int byteLength = view.GetProperty("byteLength").GetInt32();

                    byte[] diffusePngBytes = new byte[byteLength];
                    Array.Copy(binBytes, byteOffset, diffusePngBytes, 0, byteLength);

                    using MagickImage img = new(diffusePngBytes);
                    texWidth = (int)img.Width;
                    texHeight = (int)img.Height;
                    using IPixelCollection<ushort> pixels = img.GetPixelsUnsafe();
                    texturePixels = pixels.ToByteArray(PixelMapping.RGBA) ?? new byte[texWidth * texHeight * 4];

                    Console.WriteLine(
                        $"[GlbModelLoader] Decoded PNG Diffuse Texture ({texWidth}x{texHeight}, {texturePixels.Length} RGBA bytes)");
                }
            }

            GlbMeshData meshData = new()
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                Vertices = vertices,
                Indices = indices,
                TextureRgbaPixels = texturePixels,
                TextureWidth = texWidth,
                TextureHeight = texHeight
            };

            string normalSourceNote = computedFallbackNormals ? " (computed area-weighted normals)" : "";
            Console.WriteLine(
                $"[GlbModelLoader] Loaded {meshInstances.Count} meshes, {totalPrimitivesLoaded} primitives, {vertices.Length} vertices, {indices.Length / 3} triangles{normalSourceNote}");
            return meshData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlbModelLoader] ERROR parsing GLB file '{filePath}': {ex.Message}");
            return null;
        }
    }

    public static bool ValidateMesh(GlbMeshData? mesh, out string error)
    {
        if (mesh == null)
        {
            error = "Mesh is null.";
            return false;
        }

        if (mesh.Vertices.Length == 0)
        {
            error = $"Mesh '{mesh.Name}' contains 0 vertices.";
            return false;
        }

        if (mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0)
        {
            error = $"Mesh '{mesh.Name}' index count ({mesh.Indices.Length}) must be non-zero and divisible by 3.";
            return false;
        }

        uint vertexCount = (uint)mesh.Vertices.Length;
        for (int i = 0; i < mesh.Indices.Length; i++)
        {
            if (mesh.Indices[i] >= vertexCount)
            {
                error = $"Mesh '{mesh.Name}' triangle index [{i}]={mesh.Indices[i]} is out of bounds for vertex count {vertexCount}.";
                return false;
            }
        }

        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Vertex3D v = mesh.Vertices[i];
            if (!IsFiniteVector3(v.Position))
            {
                error = $"Mesh '{mesh.Name}' vertex [{i}] Position contains NaN or Infinity ({v.Position}).";
                return false;
            }

            if (!IsFiniteVector3(v.Normal))
            {
                error = $"Mesh '{mesh.Name}' vertex [{i}] Normal contains NaN or Infinity ({v.Normal}).";
                return false;
            }

            if (float.IsNaN(v.TexCoord.X) || float.IsInfinity(v.TexCoord.X) ||
                float.IsNaN(v.TexCoord.Y) || float.IsInfinity(v.TexCoord.Y))
            {
                error = $"Mesh '{mesh.Name}' vertex [{i}] TexCoord contains NaN or Infinity ({v.TexCoord}).";
                return false;
            }

            if (!IsFiniteVector4(v.JointWeights))
            {
                error = $"Mesh '{mesh.Name}' vertex [{i}] JointWeights contains NaN or Infinity ({v.JointWeights}).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFiniteVector3(Vector3 v)
    {
        return !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
               !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
               !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    private static bool IsFiniteVector4(Vector4 v)
    {
        return !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
               !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
               !float.IsNaN(v.Z) && !float.IsInfinity(v.Z) &&
               !float.IsNaN(v.W) && !float.IsInfinity(v.W);
    }

    private static void CollectNodeMeshInstances(
        JsonElement nodesEl,
        int nodeIdx,
        Matrix4x4 parentWorldMatrix,
        List<(int MeshIndex, Matrix4x4 WorldMatrix)> meshInstances,
        HashSet<int> referencedMeshes,
        HashSet<int> visitedNodes)
    {
        if (nodeIdx < 0 || nodeIdx >= nodesEl.GetArrayLength() || !visitedNodes.Add(nodeIdx))
        {
            return;
        }

        JsonElement nodeEl = nodesEl[nodeIdx];
        Matrix4x4 localMatrix = ReadNodeLocalMatrix(nodeEl);
        Matrix4x4 worldMatrix = localMatrix * parentWorldMatrix;

        if (nodeEl.TryGetProperty("mesh", out JsonElement meshIdxEl))
        {
            int meshIndex = meshIdxEl.GetInt32();
            meshInstances.Add((meshIndex, worldMatrix));
            referencedMeshes.Add(meshIndex);
        }

        if (nodeEl.TryGetProperty("children", out JsonElement childrenEl))
        {
            for (int c = 0; c < childrenEl.GetArrayLength(); c++)
            {
                int childIdx = childrenEl[c].GetInt32();
                CollectNodeMeshInstances(nodesEl, childIdx, worldMatrix, meshInstances, referencedMeshes, visitedNodes);
            }
        }
    }

    private static Matrix4x4 ReadNodeLocalMatrix(JsonElement nodeEl)
    {
        if (nodeEl.TryGetProperty("matrix", out JsonElement matrixEl) && matrixEl.GetArrayLength() == 16)
        {
            float[] m = new float[16];
            for (int i = 0; i < 16; i++)
            {
                m[i] = matrixEl[i].GetSingle();
            }

            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        Vector3 translation = Vector3.Zero;
        if (nodeEl.TryGetProperty("translation", out JsonElement transEl) && transEl.GetArrayLength() >= 3)
        {
            translation = new Vector3(
                transEl[0].GetSingle(),
                transEl[1].GetSingle(),
                transEl[2].GetSingle());
        }

        Quaternion rotation = Quaternion.Identity;
        if (nodeEl.TryGetProperty("rotation", out JsonElement rotEl) && rotEl.GetArrayLength() >= 4)
        {
            rotation = new Quaternion(
                rotEl[0].GetSingle(),
                rotEl[1].GetSingle(),
                rotEl[2].GetSingle(),
                rotEl[3].GetSingle());
        }

        Vector3 scale = Vector3.One;
        if (nodeEl.TryGetProperty("scale", out JsonElement scaleEl) && scaleEl.GetArrayLength() >= 3)
        {
            scale = new Vector3(
                scaleEl[0].GetSingle(),
                scaleEl[1].GetSingle(),
                scaleEl[2].GetSingle());
        }

        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static Vector3[] CalculateMeshNormals(Vector3[] positions, uint[] indices)
    {
        Vector3[] normals = new Vector3[positions.Length];
        if (positions.Length == 0)
        {
            return normals;
        }

        double[] accumX = new double[positions.Length];
        double[] accumY = new double[positions.Length];
        double[] accumZ = new double[positions.Length];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint i0 = indices[i];
            uint i1 = indices[i + 1];
            uint i2 = indices[i + 2];

            if (i0 >= (uint)positions.Length || i1 >= (uint)positions.Length || i2 >= (uint)positions.Length)
            {
                continue;
            }

            Vector3 p0 = positions[i0];
            Vector3 p1 = positions[i1];
            Vector3 p2 = positions[i2];

            double e1x = (double)p1.X - p0.X;
            double e1y = (double)p1.Y - p0.Y;
            double e1z = (double)p1.Z - p0.Z;

            double e2x = (double)p2.X - p0.X;
            double e2y = (double)p2.Y - p0.Y;
            double e2z = (double)p2.Z - p0.Z;

            // Cross product magnitude is 2x triangle area -> naturally area-weights vertex normals
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

        for (int i = 0; i < positions.Length; i++)
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
                normals[i] = Vector3.UnitY;
            }
        }

        return normals;
    }

    private static Vector3[] ReadVector3Array(JsonElement accessor, JsonElement bufferViews, byte[] binBytes)
    {
        int bufferViewIdx = accessor.GetProperty("bufferView").GetInt32();
        int count = accessor.GetProperty("count").GetInt32();
        int byteOffset = accessor.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;

        JsonElement view = bufferViews[bufferViewIdx];
        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
        int totalOffset = viewByteOffset + byteOffset;

        Vector3[] result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int idx = totalOffset + i * 12;
            float x = BitConverter.ToSingle(binBytes, idx);
            float y = BitConverter.ToSingle(binBytes, idx + 4);
            float z = BitConverter.ToSingle(binBytes, idx + 8);
            result[i] = new Vector3(x, y, z);
        }

        return result;
    }

    private static Vector2[] ReadVector2Array(JsonElement accessor, JsonElement bufferViews, byte[] binBytes)
    {
        int bufferViewIdx = accessor.GetProperty("bufferView").GetInt32();
        int count = accessor.GetProperty("count").GetInt32();
        int byteOffset = accessor.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;

        JsonElement view = bufferViews[bufferViewIdx];
        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
        int totalOffset = viewByteOffset + byteOffset;

        Vector2[] result = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int idx = totalOffset + i * 8;
            float x = BitConverter.ToSingle(binBytes, idx);
            float y = BitConverter.ToSingle(binBytes, idx + 4);
            result[i] = new Vector2(x, y);
        }

        return result;
    }

    private static Vector4[] ReadJointsArray(JsonElement accessor, JsonElement bufferViews, byte[] binBytes)
    {
        int bufferViewIdx = accessor.GetProperty("bufferView").GetInt32();
        int count = accessor.GetProperty("count").GetInt32();
        int componentType = accessor.GetProperty("componentType").GetInt32();
        int byteOffset = accessor.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;

        JsonElement view = bufferViews[bufferViewIdx];
        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
        int totalOffset = viewByteOffset + byteOffset;

        Vector4[] result = new Vector4[count];
        for (int i = 0; i < count; i++)
            if (componentType == 5121)
            {
                int idx = totalOffset + i * 4;
                result[i] = new Vector4(binBytes[idx], binBytes[idx + 1], binBytes[idx + 2], binBytes[idx + 3]);
            }
            else if (componentType == 5123)
            {
                int idx = totalOffset + i * 8;
                ushort j0 = BitConverter.ToUInt16(binBytes, idx);
                ushort j1 = BitConverter.ToUInt16(binBytes, idx + 2);
                ushort j2 = BitConverter.ToUInt16(binBytes, idx + 4);
                ushort j3 = BitConverter.ToUInt16(binBytes, idx + 6);
                result[i] = new Vector4(j0, j1, j2, j3);
            }
            else
            {
                int idx = totalOffset + i * 16;
                float j0 = BitConverter.ToSingle(binBytes, idx);
                float j1 = BitConverter.ToSingle(binBytes, idx + 4);
                float j2 = BitConverter.ToSingle(binBytes, idx + 8);
                float j3 = BitConverter.ToSingle(binBytes, idx + 12);
                result[i] = new Vector4(j0, j1, j2, j3);
            }

        return result;
    }

    private static Vector4[] ReadWeightsArray(JsonElement accessor, JsonElement bufferViews, byte[] binBytes)
    {
        int bufferViewIdx = accessor.GetProperty("bufferView").GetInt32();
        int count = accessor.GetProperty("count").GetInt32();
        int componentType = accessor.GetProperty("componentType").GetInt32();
        int byteOffset = accessor.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;

        JsonElement view = bufferViews[bufferViewIdx];
        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
        int totalOffset = viewByteOffset + byteOffset;

        Vector4[] result = new Vector4[count];
        for (int i = 0; i < count; i++)
            if (componentType == 5121)
            {
                int idx = totalOffset + i * 4;
                result[i] = new Vector4(binBytes[idx] / 255.0f, binBytes[idx + 1] / 255.0f, binBytes[idx + 2] / 255.0f,
                    binBytes[idx + 3] / 255.0f);
            }
            else if (componentType == 5123)
            {
                int idx = totalOffset + i * 8;
                float w0 = BitConverter.ToUInt16(binBytes, idx) / 65535.0f;
                float w1 = BitConverter.ToUInt16(binBytes, idx + 2) / 65535.0f;
                float w2 = BitConverter.ToUInt16(binBytes, idx + 4) / 65535.0f;
                float w3 = BitConverter.ToUInt16(binBytes, idx + 6) / 65535.0f;
                result[i] = new Vector4(w0, w1, w2, w3);
            }
            else
            {
                int idx = totalOffset + i * 16;
                float w0 = BitConverter.ToSingle(binBytes, idx);
                float w1 = BitConverter.ToSingle(binBytes, idx + 4);
                float w2 = BitConverter.ToSingle(binBytes, idx + 8);
                float w3 = BitConverter.ToSingle(binBytes, idx + 12);
                result[i] = new Vector4(w0, w1, w2, w3);
            }

        return result;
    }

    private static uint[] ReadIndexArray(JsonElement accessor, JsonElement bufferViews, byte[] binBytes)
    {
        int bufferViewIdx = accessor.GetProperty("bufferView").GetInt32();
        int count = accessor.GetProperty("count").GetInt32();
        int componentType = accessor.GetProperty("componentType").GetInt32();
        int byteOffset = accessor.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;

        JsonElement view = bufferViews[bufferViewIdx];
        int viewByteOffset = view.TryGetProperty("byteOffset", out JsonElement vbo) ? vbo.GetInt32() : 0;
        int totalOffset = viewByteOffset + byteOffset;

        uint[] result = new uint[count];
        for (int i = 0; i < count; i++)
            if (componentType == 5123)
            {
                int idx = totalOffset + i * 2;
                result[i] = BitConverter.ToUInt16(binBytes, idx);
            }
            else
            {
                int idx = totalOffset + i * 4;
                result[i] = BitConverter.ToUInt32(binBytes, idx);
            }

        return result;
    }
}
