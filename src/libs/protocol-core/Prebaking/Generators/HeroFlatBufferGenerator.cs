// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;

namespace ASPIRE.Common.Prebaking.Generators;

/// <summary>
///     High-performance binary generator for Hero, Avatar, Ability, and Item entity descriptors.
///     Produces binary cache conforming to schemas/hero_descriptors.fbs with "KNFB" magic identifier and XxHash3 checksum
///     using pooled span writers (zero intermediate MemoryStream, BinaryWriter, or per-string byte[] allocations).
/// </summary>
public static class HeroFlatBufferGenerator
{
    public const uint MagicKnfb = 0x42464E4B; // "KNFB" in ASCII Little Endian (0x4B, 0x4E, 0x46, 0x42)
    public const uint SchemaVersion = 1;
    internal const int HeaderChecksumOffset = 20; // [0..3]=rootOffset, [4..7]=magic, [8..11]=version, [12..19]=timestamp, [20..27]=checksum

    /// <summary>
    ///     Generates binary KNFB byte array from an EntityCacheContainer with zero intermediate heap allocations.
    /// </summary>
    public static byte[] Generate(EntityCacheContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        int initialCapacity = Math.Max(1024, (container.Heroes.Count * 2048) + (container.Items.Count * 128) + 64);
        PooledSpanWriter writer = new(initialCapacity);
        try
        {
            // Header layout (36 bytes):
            //   Offset 0x00 (4B): Root Table Offset = 8
            //   Offset 0x04 (4B): Magic Identifier "KNFB" (0x42464E4B LE)
            //   Offset 0x08 (4B): SchemaVersion (uint32)
            //   Offset 0x0C (8B): GeneratedTimestampUtc (uint64)
            //   Offset 0x14 (8B): ChecksumXxHash3 (uint64, zeroed during hash calculation)
            //   Offset 0x1C (4B): HeroCount (uint32)
            //   Offset 0x20 (4B): ItemCount (uint32)
            writer.WriteUInt32(8U);
            writer.WriteUInt32(MagicKnfb);

            ulong timestamp = (ulong)TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
            uint heroCount = (uint)container.Heroes.Count;
            uint itemCount = (uint)container.Items.Count;

            writer.WriteUInt32(SchemaVersion);
            writer.WriteUInt64(timestamp);
            writer.WriteUInt64(0UL); // Checksum placeholder at HeaderChecksumOffset (byte 20)
            writer.WriteUInt32(heroCount);
            writer.WriteUInt32(itemCount);

            // Serialize Heroes
            for (int hIdx = 0; hIdx < container.Heroes.Count; hIdx++)
            {
                HeroDefinitionDto hero = container.Heroes[hIdx];
                writer.WriteUInt32(hero.Id);
                writer.WriteString(hero.Name);
                writer.WriteString(hero.DisplayName);
                writer.WriteString(hero.Path);
                writer.WriteString(hero.IconPath ?? hero.DefaultIcon);
                writer.WriteString(hero.ModelPath ?? hero.DefaultModel);
                writer.WriteString(hero.Category ?? "Legion");

                byte primAttr = ParsePrimaryAttribute(hero.PrimaryAttribute);
                writer.WriteByte(primAttr);

                byte atkType = string.Equals(hero.AttackType, "Ranged", StringComparison.OrdinalIgnoreCase)
                    ? (byte)1
                    : (byte)0;
                writer.WriteByte(atkType);

                // Stats
                writer.WriteSingle(hero.Strength);
                writer.WriteSingle(hero.StrengthPerLevel);
                writer.WriteSingle(hero.Agility);
                writer.WriteSingle(hero.AgilityPerLevel);
                writer.WriteSingle(hero.Intelligence);
                writer.WriteSingle(hero.IntelligencePerLevel);
                writer.WriteSingle(hero.Armor);
                writer.WriteSingle(hero.MagicArmor);
                writer.WriteSingle(hero.AttackDamageMin);
                writer.WriteSingle(hero.AttackDamageMax);
                writer.WriteSingle(hero.AttackRange);
                writer.WriteSingle(hero.MoveSpeed);
                writer.WriteSingle(hero.Difficulty);

                // Ratings
                writer.WriteInt32(hero.SoloRating);
                writer.WriteInt32(hero.CarryRating);
                writer.WriteInt32(hero.SupportRating);
                writer.WriteInt32(hero.InitiatorRating);
                writer.WriteInt32(hero.GankerRating);

                // Abilities
                List<AbilityDefinitionDto> abilities = hero.Abilities;
                writer.WriteUInt32((uint)abilities.Count);
                for (int aIdx = 0; aIdx < abilities.Count; aIdx++)
                {
                    AbilityDefinitionDto ab = abilities[aIdx];
                    writer.WriteInt32(ab.Slot);
                    writer.WriteString(ab.Name);
                    writer.WriteString(ab.DisplayName ?? ab.Name);
                    writer.WriteString(ab.IconPath);
                    writer.WriteString(ab.ManaCost ?? "0");
                    writer.WriteString(ab.CooldownTime ?? "0");
                    writer.WriteString(ab.ActionType ?? "target_entity");
                    _ = float.TryParse(ab.TargetRadius, NumberStyles.Float, CultureInfo.InvariantCulture, out float radiusVal);
                    writer.WriteSingle(radiusVal);
                    _ = float.TryParse(ab.CastRange, NumberStyles.Float, CultureInfo.InvariantCulture, out float rangeVal);
                    writer.WriteSingle(rangeVal);
                }

                // Alt Avatars (avoid intermediate List.FindAll allocation when hero.AltAvatars is empty)
                if (hero.AltAvatars.Count > 0)
                {
                    writer.WriteUInt32((uint)hero.AltAvatars.Count);
                    for (int avIdx = 0; avIdx < hero.AltAvatars.Count; avIdx++)
                    {
                        WriteAltAvatar(ref writer, hero.AltAvatars[avIdx]);
                    }
                }
                else if (container.AltAvatars.Count > 0)
                {
                    uint matchedCount = 0;
                    for (int avIdx = 0; avIdx < container.AltAvatars.Count; avIdx++)
                    {
                        if (string.Equals(container.AltAvatars[avIdx].HeroName, hero.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedCount++;
                        }
                    }

                    writer.WriteUInt32(matchedCount);
                    if (matchedCount > 0)
                    {
                        for (int avIdx = 0; avIdx < container.AltAvatars.Count; avIdx++)
                        {
                            AltAvatarDto av = container.AltAvatars[avIdx];
                            if (string.Equals(av.HeroName, hero.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                WriteAltAvatar(ref writer, av);
                            }
                        }
                    }
                }
                else
                {
                    writer.WriteUInt32(0U);
                }

                // Recommended Items / Inventory
                writer.WriteUInt32((uint)hero.InventorySlots.Count);
                for (int rIdx = 0; rIdx < hero.InventorySlots.Count; rIdx++)
                {
                    writer.WriteString(hero.InventorySlots[rIdx]);
                }

                // Preload Resources
                writer.WriteUInt32((uint)hero.PreloadResources.Count);
                for (int pIdx = 0; pIdx < hero.PreloadResources.Count; pIdx++)
                {
                    writer.WriteString(hero.PreloadResources[pIdx]);
                }
            }

            // Serialize Items
            for (int iIdx = 0; iIdx < container.Items.Count; iIdx++)
            {
                ItemDefinitionDto item = container.Items[iIdx];
                writer.WriteString(item.Name);
                writer.WriteString(item.DisplayName ?? item.Name);
                writer.WriteString(item.IconPath);
                writer.WriteInt32(item.Cost);
                writer.WriteString(item.Category ?? "Consumables");
            }

            byte[] buffer = writer.ToExactArray();

            // Compute XxHash3 checksum over entire buffer (checksum slot [20..27] is currently zeroed)
            ulong checksum = XxHash3.HashToUInt64(buffer);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(HeaderChecksumOffset, sizeof(ulong)), checksum);

            return buffer;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    ///     Generates binary FlatBuffer file to specified target path.
    /// </summary>
    public static async Task GenerateToFileAsync(
        EntityCacheContainer container,
        string targetFilePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        string? dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        byte[] binary = Generate(container);
        await File.WriteAllBytesAsync(targetFilePath, binary, ct).ConfigureAwait(false);
    }

    private static byte ParsePrimaryAttribute(string? attr)
    {
        if (string.IsNullOrEmpty(attr)) return 0;
        if (string.Equals(attr, "agility", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(attr, "intelligence", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    private static void WriteAltAvatar(ref PooledSpanWriter writer, AltAvatarDto av)
    {
        writer.WriteString(av.Key);
        writer.WriteString(av.HeroName);
        writer.WriteString(av.DisplayName ?? av.Key);
        writer.WriteString(av.IconPath);
        writer.WriteString(av.ModelPath);
        writer.WriteString(av.EffectPath);
        writer.WriteSingle(av.PreviewScale > 0 ? av.PreviewScale : 1.0f);
    }

    private ref struct PooledSpanWriter
    {
        private byte[]? _rented;
        private int _pos;

        public PooledSpanWriter(int initialCapacity)
        {
            _rented = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _pos = 0;
        }

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _rented![_pos++] = value;
        }

        public void WriteInt32(int value)
        {
            EnsureCapacity(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(_rented.AsSpan(_pos, sizeof(int)), value);
            _pos += sizeof(int);
        }

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(_rented.AsSpan(_pos, sizeof(uint)), value);
            _pos += sizeof(uint);
        }

        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(_rented.AsSpan(_pos, sizeof(ulong)), value);
            _pos += sizeof(ulong);
        }

        public void WriteSingle(float value)
        {
            EnsureCapacity(sizeof(float));
            BinaryPrimitives.WriteSingleLittleEndian(_rented.AsSpan(_pos, sizeof(float)), value);
            _pos += sizeof(float);
        }

        public void WriteString(string? str)
        {
            if (string.IsNullOrEmpty(str))
            {
                EnsureCapacity(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(_rented.AsSpan(_pos, sizeof(ushort)), 0);
                _pos += sizeof(ushort);
                return;
            }

            int maxBytes = Encoding.UTF8.GetMaxByteCount(str.Length);
            EnsureCapacity(sizeof(ushort) + maxBytes);
            int written = Encoding.UTF8.GetBytes(str.AsSpan(), _rented.AsSpan(_pos + sizeof(ushort), maxBytes));
            if (written > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"Serialized KNFB string exceeds 65535 UTF-8 bytes ({written} bytes). Inspect entity definitions with 'kongor prebake entities'.");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(_rented.AsSpan(_pos, sizeof(ushort)), (ushort)written);
            _pos += sizeof(ushort) + written;
        }

        public byte[] ToExactArray()
        {
            byte[] result = new byte[_pos];
            _rented.AsSpan(0, _pos).CopyTo(result);
            return result;
        }

        public void Dispose()
        {
            byte[]? toReturn = _rented;
            _rented = null;
            if (toReturn is not null)
            {
                ArrayPool<byte>.Shared.Return(toReturn);
            }
        }

        private void EnsureCapacity(int additionalBytes)
        {
            byte[] current = _rented!;
            int required = _pos + additionalBytes;
            if (required <= current.Length) return;

            int nextSize = Math.Max(current.Length * 2, required);
            byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
            current.AsSpan(0, _pos).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(current);
            _rented = grown;
        }
    }
}
