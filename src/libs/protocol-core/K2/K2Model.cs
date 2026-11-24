// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;

namespace ASPIRE.Common.K2;

public class K2Model
{
    public int Version { get; set; }
    public int NumMeshes { get; set; }
    public int NumBones { get; set; }
    public Vector3 BoundingMin { get; set; }
    public Vector3 BoundingMax { get; set; }
    public List<K2Bone> Bones { get; set; } = [];
    public List<K2Mesh> Meshes { get; set; } = [];
}

public class K2Bone
{
    public int Index { get; set; }
    public int ParentIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public Matrix4x4 InverseMatrix { get; set; }
    public Matrix4x4 LocalMatrix { get; set; }
}

public class K2Mesh
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DefaultMaterial { get; set; } = string.Empty;
    public int NumVertices { get; set; }
    public int NumFaces { get; set; }
    public Vector3[] Vertices { get; set; } = [];
    public Vector3[] Normals { get; set; } = [];
    public Vector2[] TexCoords { get; set; } = [];
    public Vector3[] Tangents { get; set; } = [];
    public int[] Indices { get; set; } = [];
    public K2VertexBoneLink[] BoneLinks { get; set; } = [];
}

public struct K2VertexBoneLink
{
    public int BoneIndex0;
    public int BoneIndex1;
    public int BoneIndex2;
    public int BoneIndex3;
    public float Weight0;
    public float Weight1;
    public float Weight2;
    public float Weight3;
}
