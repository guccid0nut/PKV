// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;

namespace ASPIRE.Common.K2;

public static class K2ModelReader
{
    public static K2Model Read(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, true);

        byte[] magicBytes = reader.ReadBytes(4);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != "SMDL") throw new InvalidDataException($"Invalid K2 Model header: expected 'SMDL', got '{magic}'");

        K2Model model = new();
        K2Mesh? currentMesh = null;

        while (stream.Position + 8 <= stream.Length)
        {
            byte[] tagBytes = reader.ReadBytes(4);
            string tag = Encoding.ASCII.GetString(tagBytes);
            int chunkSize = reader.ReadInt32();
            if (chunkSize < 0 || chunkSize > 64 * 1024 * 1024)
                throw new InvalidDataException($"Invalid chunk size: {chunkSize}");
            long chunkEnd = stream.Position + chunkSize;
            if (chunkEnd > stream.Length)
                throw new InvalidDataException("Chunk extends past end of stream");

            switch (tag)
            {
                case "head":
                    ReadHeadChunk(reader, model);
                    break;

                case "bone":
                    ReadBoneChunk(reader, model);
                    break;

                case "mesh":
                    currentMesh = ReadMeshChunk(reader);
                    model.Meshes.Add(currentMesh);
                    break;

                case "vrts" when currentMesh != null:
                    ReadVrtsChunk(reader, currentMesh);
                    break;

                case "nrml" when currentMesh != null:
                    ReadNrmlChunk(reader, currentMesh);
                    break;

                case "texc" when currentMesh != null:
                    ReadTexcChunk(reader, currentMesh);
                    break;

                case "tang" when currentMesh != null:
                    ReadTangChunk(reader, currentMesh);
                    break;

                case "face" when currentMesh != null:
                    ReadFaceChunk(reader, currentMesh);
                    break;

                case "lnk3" when currentMesh != null:
                case "link" when currentMesh != null:
                    ReadLinkChunk(reader, currentMesh);
                    break;
            }

            if (stream.Position < chunkEnd) stream.Seek(chunkEnd, SeekOrigin.Begin);
        }

        return model;
    }

    private static void ReadHeadChunk(BinaryReader reader, K2Model model)
    {
        model.Version = reader.ReadInt32();
        model.NumMeshes = reader.ReadInt32();
        int numSprites = reader.ReadInt32();
        int numSurfs = reader.ReadInt32();
        model.NumBones = reader.ReadInt32();
        model.BoundingMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        model.BoundingMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void ReadBoneChunk(BinaryReader reader, K2Model model)
    {
        for (int i = 0; i < model.NumBones; i++)
        {
            int parentIndex = reader.ReadInt32();

            float[] invMatFloats = new float[12];
            for (int j = 0; j < 12; j++) invMatFloats[j] = reader.ReadSingle();

            float[] locMatFloats = new float[12];
            for (int j = 0; j < 12; j++) locMatFloats[j] = reader.ReadSingle();

            byte nameLen = reader.ReadByte();
            byte[] nameBytes = reader.ReadBytes(nameLen);
            reader.ReadByte(); // null terminator
            string boneName = Encoding.UTF8.GetString(nameBytes);

            Matrix4x4 invMatrix = new(
                invMatFloats[0], invMatFloats[1], invMatFloats[2], 0f,
                invMatFloats[3], invMatFloats[4], invMatFloats[5], 0f,
                invMatFloats[6], invMatFloats[7], invMatFloats[8], 0f,
                invMatFloats[9], invMatFloats[10], invMatFloats[11], 1f);

            Matrix4x4 locMatrix = new(
                locMatFloats[0], locMatFloats[1], locMatFloats[2], 0f,
                locMatFloats[3], locMatFloats[4], locMatFloats[5], 0f,
                locMatFloats[6], locMatFloats[7], locMatFloats[8], 0f,
                locMatFloats[9], locMatFloats[10], locMatFloats[11], 1f);

            model.Bones.Add(new K2Bone
            {
                Index = i,
                ParentIndex = parentIndex,
                Name = boneName,
                InverseMatrix = invMatrix,
                LocalMatrix = locMatrix
            });
        }
    }

    private static K2Mesh ReadMeshChunk(BinaryReader reader)
    {
        int meshIndex = reader.ReadInt32();
        int mode = reader.ReadInt32();
        int numVerts = reader.ReadInt32();
        Vector3 bmin = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Vector3 bmax = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        int boneIndex = reader.ReadInt32();

        byte nameLen = reader.ReadByte();
        byte matLen = reader.ReadByte();
        byte[] nameBytes = reader.ReadBytes(nameLen);
        reader.ReadByte(); // null terminator
        string meshName = Encoding.UTF8.GetString(nameBytes);

        byte[] matBytes = reader.ReadBytes(matLen);
        reader.ReadByte(); // null terminator
        string matName = Encoding.UTF8.GetString(matBytes);

        return new K2Mesh { Index = meshIndex, Name = meshName, DefaultMaterial = matName, NumVertices = numVerts };
    }

    private static void ReadVrtsChunk(BinaryReader reader, K2Mesh mesh)
    {
        int meshIndex = reader.ReadInt32();
        mesh.Vertices = new Vector3[mesh.NumVertices];
        for (int i = 0; i < mesh.NumVertices; i++)
            mesh.Vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void ReadNrmlChunk(BinaryReader reader, K2Mesh mesh)
    {
        int meshIndex = reader.ReadInt32();
        mesh.Normals = new Vector3[mesh.NumVertices];
        for (int i = 0; i < mesh.NumVertices; i++)
            mesh.Normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void ReadTexcChunk(BinaryReader reader, K2Mesh mesh)
    {
        int meshIndex = reader.ReadInt32();
        int channelIndex = reader.ReadInt32();
        if (channelIndex == 0 || mesh.TexCoords.Length == 0)
        {
            mesh.TexCoords = new Vector2[mesh.NumVertices];
            for (int i = 0; i < mesh.NumVertices; i++)
                mesh.TexCoords[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }

    private static void ReadTangChunk(BinaryReader reader, K2Mesh mesh)
    {
        int meshIndex = reader.ReadInt32();
        int channelIndex = reader.ReadInt32();
        mesh.Tangents = new Vector3[mesh.NumVertices];
        for (int i = 0; i < mesh.NumVertices; i++)
            mesh.Tangents[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void ReadFaceChunk(BinaryReader reader, K2Mesh mesh)
    {
        int meshIndex = reader.ReadInt32();
        int numFaces = reader.ReadInt32();
        byte faceType = reader.ReadByte();
        mesh.NumFaces = numFaces;
        mesh.Indices = new int[numFaces * 3];

        if (faceType == 2 || (faceType == 0 && mesh.NumVertices <= 65535))
            for (int i = 0; i < mesh.Indices.Length; i++)
                mesh.Indices[i] = reader.ReadUInt16();
        else
            for (int i = 0; i < mesh.Indices.Length; i++)
                mesh.Indices[i] = reader.ReadInt32();
    }

    private static void ReadLinkChunk(BinaryReader reader, K2Mesh mesh)
    {
        int meshIndex = reader.ReadInt32();
        int numVerts = reader.ReadInt32();
        mesh.BoneLinks = new K2VertexBoneLink[numVerts];

        for (int i = 0; i < numVerts; i++)
        {
            int numWeights = reader.ReadInt32();
            float[] weights = new float[numWeights];
            for (int w = 0; w < numWeights; w++) weights[w] = reader.ReadSingle();

            int[] boneIndices = new int[numWeights];
            for (int w = 0; w < numWeights; w++) boneIndices[w] = reader.ReadInt32();

            K2VertexBoneLink link = new();
            if (numWeights > 0)
            {
                link.Weight0 = weights[0];
                link.BoneIndex0 = boneIndices[0];
            }

            if (numWeights > 1)
            {
                link.Weight1 = weights[1];
                link.BoneIndex1 = boneIndices[1];
            }

            if (numWeights > 2)
            {
                link.Weight2 = weights[2];
                link.BoneIndex2 = boneIndices[2];
            }

            if (numWeights > 3)
            {
                link.Weight3 = weights[3];
                link.BoneIndex3 = boneIndices[3];
            }

            mesh.BoneLinks[i] = link;
        }
    }
}
