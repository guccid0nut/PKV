// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable UnusedAutoPropertyAccessor.Local

using System.Numerics;

namespace ASPIRE.Common.K2;

public static class K2ClipReader
{
    public static K2Clip Read(Stream stream, string clipName = "")
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, true);

        byte[] magicBytes = reader.ReadBytes(4);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != "CLIP") throw new InvalidDataException($"Invalid K2 Clip header: expected 'CLIP', got '{magic}'");

        K2Clip clip = new() { Name = clipName };

        Dictionary<int, BoneChannelData> boneChannels = [];

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

            if (tag == "head")
            {
                clip.Version = reader.ReadInt32();
                clip.NumBones = reader.ReadInt32();
                clip.NumFrames = reader.ReadInt32();
            }
            else if (tag == "bmtn")
            {
                int boneIndex = reader.ReadInt32();
                int channelType = reader.ReadInt32();
                int keyCount = reader.ReadInt32();
                if (keyCount < 0 || keyCount > 1_000_000)
                    throw new InvalidDataException($"Invalid keyCount: {keyCount}");

                byte nameLen = reader.ReadByte();
                byte[] nameBytes = reader.ReadBytes(nameLen);
                reader.ReadByte(); // null terminator
                string boneName = Encoding.UTF8.GetString(nameBytes);

                float[] keys = new float[keyCount];
                if (channelType == 6)
                {
                    byte[] visBytes = reader.ReadBytes(keyCount);
                    for (int k = 0; k < keyCount; k++) keys[k] = visBytes[k];
                }
                else
                {
                    for (int k = 0; k < keyCount; k++) keys[k] = reader.ReadSingle();
                }

                if (!boneChannels.TryGetValue(boneIndex, out BoneChannelData? channelData))
                {
                    channelData = new BoneChannelData { BoneIndex = boneIndex, BoneName = boneName };
                    boneChannels[boneIndex] = channelData;
                }

                channelData.Channels[channelType] = keys;
            }

            if (stream.Position < chunkEnd) stream.Seek(chunkEnd, SeekOrigin.Begin);
        }

        // Reconstruct translation vectors, rotation quaternions, and scale vectors for each bone
        foreach ((int boneIndex, BoneChannelData data) in boneChannels.OrderBy(kvp => kvp.Key))
        {
            K2BoneMotion motion = new()
            {
                BoneIndex = boneIndex,
                BoneName = data.BoneName,
                Translations = new Vector3[clip.NumFrames],
                Rotations = new Quaternion[clip.NumFrames],
                Scales = new Vector3[clip.NumFrames]
            };

            for (int f = 0; f < clip.NumFrames; f++)
            {
                float posX = GetChannelValue(data.Channels, 0, f, 0f);
                float posY = GetChannelValue(data.Channels, 1, f, 0f);
                float posZ = GetChannelValue(data.Channels, 2, f, 0f);
                motion.Translations[f] = new Vector3(posX, posY, posZ);

                float pitchDeg = GetChannelValue(data.Channels, 3, f, 0f);
                float rollDeg = GetChannelValue(data.Channels, 4, f, 0f);
                float yawDeg = GetChannelValue(data.Channels, 5, f, 0f);

                float pitchRad = pitchDeg * (MathF.PI / 180f);
                float rollRad = rollDeg * (MathF.PI / 180f);
                float yawRad = yawDeg * (MathF.PI / 180f);

                Quaternion qZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawRad);
                Quaternion qX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchRad);
                Quaternion qY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rollRad);
                Quaternion q = Quaternion.Normalize(qZ * qX * qY);
                motion.Rotations[f] = q;

                float scaleX = GetChannelValue(data.Channels, 7, f, 1f);
                float scaleY = GetChannelValue(data.Channels, 8, f, 1f);
                float scaleZ = GetChannelValue(data.Channels, 9, f, 1f);
                motion.Scales[f] = new Vector3(scaleX, scaleY, scaleZ);
            }

            clip.BoneMotions.Add(motion);
        }

        return clip;
    }

    private static float GetChannelValue(Dictionary<int, float[]> channels, int channelIndex, int frame,
        float defaultValue)
    {
        if (channels.TryGetValue(channelIndex, out float[]? keys) && keys.Length > 0)
            return keys.Length == 1 ? keys[0] : frame < keys.Length ? keys[frame] : keys[^1];

        return defaultValue;
    }

    private sealed class BoneChannelData
    {
        public int BoneIndex { get; set; }
        public string BoneName { get; init; } = string.Empty;
        public Dictionary<int, float[]> Channels { get; } = [];
    }
}
