// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Generates realistic synthetic entity delta frames and compiles a 64KB RFC 8878-compliant static dictionary
///     optimized for MOBA entity coordinate, combat, animation, and inventory structs.
/// </summary>
public static class ZstdDictionaryTrainer
{
    public const uint ZstdDictMagicNumber = 0xEC30A437;
    public const uint NetcodeDictionaryId = 0x484F4E31; // "HON1"
    public const int TargetDictionarySizeBytes = 65536; // 64 KB

    /// <summary>
    ///     Generates a rich corpus of synthetic entity delta frames mimicking real game traffic.
    /// </summary>
    public static List<byte[]> GenerateTrainingCorpus(int frameCount = 1000)
    {
        List<byte[]> corpus = new(frameCount);
        Random rng = new(1337); // Deterministic seed for reproducible builds

        for (int f = 0; f < frameCount; f++)
        {
            uint tick = (uint)(f * 50); // 50ms tick (20Hz/50Hz)
            int entityCount = rng.Next(5, 35); // 5 to 35 entities per frame

            SnapshotFrameDelta frame = new()
            {
                Header = new SnapshotFrameHeader
                {
                    FrameSequence = (uint)f,
                    ServerTick = tick,
                    ClientAckSequence = (uint)Math.Max(0, f - 1),
                    EntityCount = (ushort)entityCount
                }
            };

            for (int e = 0; e < entityCount; e++)
            {
                ushort entityId = (ushort)(1000 + e);
                byte entityType = (byte)(e < 10 ? 1 : (e < 25 ? 2 : 3)); // Heroes, Creeps, Neutrals
                DeltaFieldMask mask = DeltaFieldMask.Coordinates;

                if (rng.NextDouble() < 0.8) mask |= DeltaFieldMask.Combat;
                if (rng.NextDouble() < 0.6) mask |= DeltaFieldMask.Animation;
                if (e < 10 && rng.NextDouble() < 0.3) mask |= DeltaFieldMask.Inventory;
                if (rng.NextDouble() < 0.4) mask |= DeltaFieldMask.Orders;

                SnapshotEntityDelta entity = new()
                {
                    EntityId = entityId,
                    EntityType = entityType,
                    ChangedMask = mask,
                    Coordinates = new EntityCoordinateDelta
                    {
                        PosX = 8192.0f + (float)(rng.NextDouble() * 2000.0 - 1000.0),
                        PosY = 8192.0f + (float)(rng.NextDouble() * 2000.0 - 1000.0),
                        PosZ = 128.0f + (float)(rng.NextDouble() * 32.0),
                        Yaw = (short)rng.Next(-32768, 32767),
                        Pitch = (short)rng.Next(-500, 500),
                        VelocityX = (short)rng.Next(-350, 350),
                        VelocityY = (short)rng.Next(-350, 350),
                        VelocityZ = 0,
                        MovementFlags = (byte)(rng.Next(0, 4))
                    },
                    Combat = new EntityCombatDelta
                    {
                        CurrentHealth = (float)rng.Next(200, 2500),
                        MaxHealth = (float)rng.Next(500, 3000),
                        CurrentMana = (float)rng.Next(50, 1200),
                        MaxMana = (float)rng.Next(200, 1500),
                        AttackCooldown = (float)(rng.NextDouble() * 1.5),
                        CastCooldown = 0.0f,
                        StatusFlags = (uint)(rng.NextDouble() < 0.1 ? 1 : 0),
                        Armor = (float)(rng.NextDouble() * 15.0 + 2.0),
                        MagicArmor = 5.5f
                    },
                    Animation = new EntityAnimationDelta
                    {
                        AnimationId = (ushort)rng.Next(1, 20),
                        SequenceIndex = (ushort)rng.Next(0, 5),
                        CurrentFrame = (ushort)rng.Next(0, 60),
                        PlaybackSpeed = 1.0f,
                        BlendWeight = 255
                    },
                    Orders = new EntityOrderDelta
                    {
                        OrderType = (byte)rng.Next(1, 4),
                        TargetEntityId = (ushort)rng.Next(1000, 1030),
                        TargetPosX = 8192.0f + (float)(rng.NextDouble() * 1000.0),
                        TargetPosY = 8192.0f + (float)(rng.NextDouble() * 1000.0),
                        TargetPosZ = 128.0f,
                        AbilitySlot = (byte)rng.Next(0, 4)
                    }
                };

                if (mask.HasFlag(DeltaFieldMask.Inventory))
                {
                    entity.Inventory = new EntityInventoryDelta
                    {
                        Slot0 = new ItemSlotDelta { ItemId = (ushort)rng.Next(100, 250), Charges = 1, CooldownRemaining = 0 },
                        Slot1 = new ItemSlotDelta { ItemId = (ushort)rng.Next(100, 250), Charges = 0, CooldownRemaining = 0 },
                        Slot2 = new ItemSlotDelta { ItemId = (ushort)rng.Next(100, 250), Charges = 3, CooldownRemaining = 0 },
                        Slot3 = new ItemSlotDelta { ItemId = 0, Charges = 0, CooldownRemaining = 0 },
                        Slot4 = new ItemSlotDelta { ItemId = 0, Charges = 0, CooldownRemaining = 0 },
                        Slot5 = new ItemSlotDelta { ItemId = 0, Charges = 0, CooldownRemaining = 0 },
                        Stash0 = new ItemSlotDelta { ItemId = 0, Charges = 0, CooldownRemaining = 0 },
                        Stash1 = new ItemSlotDelta { ItemId = 0, Charges = 0, CooldownRemaining = 0 },
                        Stash2 = new ItemSlotDelta { ItemId = 0, Charges = 0, CooldownRemaining = 0 }
                    };
                }

                frame.Entities.Add(entity);
            }

            corpus.Add(frame.Serialize());
        }

        return corpus;
    }

    /// <summary>
    ///     Compiles a static dictionary from the training corpus using libzstd's ZDICT training engine (RFC 8878).
    /// </summary>
    public static byte[] BuildStaticDictionary(List<byte[]> corpus)
    {
        if (corpus == null || corpus.Count == 0)
        {
            throw new ArgumentException("Training corpus cannot be empty.", nameof(corpus));
        }

        int totalLen = corpus.Sum(c => c.Length);
        byte[] samples = new byte[totalLen];
        int[] lengths = new int[corpus.Count];
        int offset = 0;
        for (int i = 0; i < corpus.Count; i++)
        {
            corpus[i].CopyTo(samples, offset);
            lengths[i] = corpus[i].Length;
            offset += corpus[i].Length;
        }

        byte[] result = ZstandardDictionary.Train(samples, lengths, TargetDictionarySizeBytes);
        // Enforce standardized HON1 Netcode Dictionary ID (0x484F4E31)
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), NetcodeDictionaryId);
        return result;
    }

    /// <summary>
    ///     Generates the static dictionary and writes it to disk.
    /// </summary>
    public static string GenerateAndSaveDictionary(string outputPath)
    {
        var corpus = GenerateTrainingCorpus(200);
        byte[] dict = BuildStaticDictionary(corpus);

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(outputPath, dict);
        return outputPath;
    }
}
