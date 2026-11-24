// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.IO.Hashing;
using ASPIRE.Common.Prebaking.Generators;

namespace ASPIRE.Common.Prebaking.Serialization;

public enum FlatBufferAttributeType : byte
{
    Strength = 0,
    Agility = 1,
    Intelligence = 2
}

public enum FlatBufferAttackType : byte
{
    Melee = 0,
    Ranged = 1
}

public sealed record AbilityDescriptorRecord(
    int Slot,
    string Name,
    string DisplayName,
    string IconPath,
    string ManaCost,
    string CooldownTime,
    string ActionType,
    float TargetRadius,
    float CastRange);

public sealed record AltAvatarDescriptorRecord(
    string Key,
    string HeroName,
    string DisplayName,
    string IconPath,
    string ModelPath,
    string EffectPath,
    float PreviewScale);

public sealed record HeroDescriptorRecord(
    uint Id,
    string Name,
    string DisplayName,
    string Directory,
    string IconPath,
    string ModelPath,
    string Faction,
    FlatBufferAttributeType PrimaryAttribute,
    FlatBufferAttackType AttackType,
    float Strength,
    float StrengthPerLevel,
    float Agility,
    float AgilityPerLevel,
    float Intelligence,
    float IntelligencePerLevel,
    float Armor,
    float MagicArmor,
    float AttackDamageMin,
    float AttackDamageMax,
    float AttackRange,
    float MoveSpeed,
    float Difficulty,
    int SoloRating,
    int CarryRating,
    int SupportRating,
    int InitiatorRating,
    int GankerRating,
    IReadOnlyList<AbilityDescriptorRecord> Abilities,
    IReadOnlyList<AltAvatarDescriptorRecord> AltAvatars,
    IReadOnlyList<string> RecommendedItems,
    IReadOnlyList<string> PreloadResources);

public sealed record ItemDescriptorRecord(
    string Name,
    string DisplayName,
    string IconPath,
    int Cost,
    string Category);

public sealed record HeroFlatBufferManifest(
    uint Version,
    ulong GeneratedTimestampUtc,
    ulong ChecksumXxHash3,
    uint HeroCount,
    uint ItemCount);

/// <summary>
///     High-performance zero-buffer-copy ReadOnlySpan binary cache loader for Hero and Item descriptors.
///     Eliminates buffer.ToArray(), MemoryStream, BinaryReader, and per-string byte[] allocations while enforcing
///     XxHash3 payload integrity and strict bounds checks.
/// </summary>
public sealed class HeroFlatBufferLoader
{
    public const uint ExpectedMagic = 0x42464E4B; // "KNFB"
    public const uint ExpectedVersion = 1;
    private const int HeaderSizeBytes = 36;
    private const uint MaxReasonableCollectionEntries = 65536;

    [ThreadStatic]
    private static XxHash3? t_xxHash3;

    private readonly Dictionary<uint, HeroDescriptorRecord> _heroesById;
    private readonly Dictionary<string, HeroDescriptorRecord> _heroesByName;
    private readonly HeroDescriptorRecord[] _heroesList;
    private readonly Dictionary<string, ItemDescriptorRecord> _itemsByName;
    private readonly ItemDescriptorRecord[] _itemsList;

    private HeroFlatBufferLoader(
        HeroFlatBufferManifest manifest,
        HeroDescriptorRecord[] heroes,
        ItemDescriptorRecord[] items)
    {
        Manifest = manifest;
        _heroesList = heroes;
        _itemsList = items;

        _heroesByName = new Dictionary<string, HeroDescriptorRecord>(heroes.Length, StringComparer.OrdinalIgnoreCase);
        _heroesById = new Dictionary<uint, HeroDescriptorRecord>(heroes.Length);
        for (int i = 0; i < heroes.Length; i++)
        {
            HeroDescriptorRecord h = heroes[i];
            _heroesByName[h.Name] = h;
            _heroesById[h.Id] = h;
        }

        _itemsByName = new Dictionary<string, ItemDescriptorRecord>(items.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Length; i++)
        {
            ItemDescriptorRecord item = items[i];
            _itemsByName[item.Name] = item;
        }
    }

    public HeroFlatBufferManifest Manifest { get; }
    public IReadOnlyList<HeroDescriptorRecord> AllHeroes => _heroesList;
    public IReadOnlyList<ItemDescriptorRecord> AllItems => _itemsList;
    public int HeroCount => _heroesList.Length;
    public int ItemCount => _itemsList.Length;

    /// <summary>
    ///     Loads, verifies, and parses the binary KNFB buffer directly from a ReadOnlySpan with zero buffer copies.
    /// </summary>
    public static HeroFlatBufferLoader Load(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSizeBytes)
        {
            throw new InvalidDataException(
                $"FlatBuffer buffer is too small: {buffer.Length} bytes (minimum header is {HeaderSizeBytes} bytes). Regenerate cache with 'kongor prebake entities'.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, sizeof(uint)));
        if (magic != ExpectedMagic)
        {
            string magicStr = Encoding.ASCII.GetString(buffer.Slice(4, sizeof(uint)));
            throw new InvalidDataException(
                $"Invalid FlatBuffer identifier: expected 'KNFB' (0x{ExpectedMagic:X8}), got '{magicStr}' (0x{magic:X8}). Regenerate cache with 'kongor prebake entities'.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, sizeof(uint)));
        if (version != ExpectedVersion)
        {
            throw new InvalidDataException(
                $"Unsupported schema version: {version}. Expected: {ExpectedVersion}. Regenerate cache with 'kongor prebake entities'.");
        }

        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(12, sizeof(ulong)));
        ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(HeroFlatBufferGenerator.HeaderChecksumOffset, sizeof(ulong)));
        uint heroCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(28, sizeof(uint)));
        uint itemCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(32, sizeof(uint)));

        VerifyChecksumOrThrow(buffer, ref timestamp, ref checksum);

        if (heroCount > MaxReasonableCollectionEntries || itemCount > MaxReasonableCollectionEntries)
        {
            throw new InvalidDataException(
                $"Corrupted KNFB header counts (heroes={heroCount}, items={itemCount}). Regenerate cache with 'kongor prebake entities'.");
        }

        HeroFlatBufferManifest manifest = new(version, timestamp, checksum, heroCount, itemCount);
        HeroDescriptorRecord[] heroes = heroCount == 0 ? [] : new HeroDescriptorRecord[heroCount];
        ItemDescriptorRecord[] items = itemCount == 0 ? [] : new ItemDescriptorRecord[itemCount];

        SpanCursor cursor = new(buffer, HeaderSizeBytes);
        for (int i = 0; i < heroes.Length; i++)
        {
            uint id = cursor.ReadUInt32();
            string name = cursor.ReadString();
            string displayName = cursor.ReadString();
            string directory = cursor.ReadString();
            string iconPath = cursor.ReadString();
            string modelPath = cursor.ReadString();
            string faction = cursor.ReadString();
            FlatBufferAttributeType primAttr = (FlatBufferAttributeType)cursor.ReadByte();
            FlatBufferAttackType atkType = (FlatBufferAttackType)cursor.ReadByte();

            float str = cursor.ReadSingle();
            float strPerLvl = cursor.ReadSingle();
            float agi = cursor.ReadSingle();
            float agiPerLvl = cursor.ReadSingle();
            float intl = cursor.ReadSingle();
            float intlPerLvl = cursor.ReadSingle();
            float armor = cursor.ReadSingle();
            float magicArmor = cursor.ReadSingle();
            float atkDmgMin = cursor.ReadSingle();
            float atkDmgMax = cursor.ReadSingle();
            float atkRange = cursor.ReadSingle();
            float moveSpeed = cursor.ReadSingle();
            float difficulty = cursor.ReadSingle();

            int soloRating = cursor.ReadInt32();
            int carryRating = cursor.ReadInt32();
            int supportRating = cursor.ReadInt32();
            int initiatorRating = cursor.ReadInt32();
            int gankerRating = cursor.ReadInt32();

            uint abilityCount = cursor.ReadCount("ability");
            AbilityDescriptorRecord[] abilities = abilityCount == 0
                ? []
                : new AbilityDescriptorRecord[abilityCount];
            for (int a = 0; a < abilities.Length; a++)
            {
                int slot = cursor.ReadInt32();
                string abName = cursor.ReadString();
                string abDisplay = cursor.ReadString();
                string abIcon = cursor.ReadString();
                string manaCost = cursor.ReadString();
                string cdTime = cursor.ReadString();
                string actType = cursor.ReadString();
                float radius = cursor.ReadSingle();
                float range = cursor.ReadSingle();
                abilities[a] = new AbilityDescriptorRecord(
                    slot, abName, abDisplay, abIcon, manaCost, cdTime, actType, radius, range);
            }

            uint avatarCount = cursor.ReadCount("alt_avatar");
            AltAvatarDescriptorRecord[] avatars = avatarCount == 0
                ? []
                : new AltAvatarDescriptorRecord[avatarCount];
            for (int av = 0; av < avatars.Length; av++)
            {
                string avKey = cursor.ReadString();
                string avHeroName = cursor.ReadMatchingOrString(name);
                string avDisplay = cursor.ReadString();
                string avIcon = cursor.ReadString();
                string avModel = cursor.ReadString();
                string avEffect = cursor.ReadString();
                float scale = cursor.ReadSingle();
                avatars[av] = new AltAvatarDescriptorRecord(
                    avKey, avHeroName, avDisplay, avIcon, avModel, avEffect, scale);
            }

            uint itemCountForHero = cursor.ReadCount("recommended_item");
            string[] recItems = itemCountForHero == 0 ? [] : new string[itemCountForHero];
            for (int r = 0; r < recItems.Length; r++)
            {
                recItems[r] = cursor.ReadString();
            }

            uint preloadCount = cursor.ReadCount("preload_resource");
            string[] preloads = preloadCount == 0 ? [] : new string[preloadCount];
            for (int p = 0; p < preloads.Length; p++)
            {
                preloads[p] = cursor.ReadString();
            }

            heroes[i] = new HeroDescriptorRecord(
                id, name, displayName, directory, iconPath, modelPath, faction,
                primAttr, atkType, str, strPerLvl, agi, agiPerLvl, intl, intlPerLvl,
                armor, magicArmor, atkDmgMin, atkDmgMax, atkRange, moveSpeed, difficulty,
                soloRating, carryRating, supportRating, initiatorRating, gankerRating,
                abilities, avatars, recItems, preloads);
        }

        for (int it = 0; it < items.Length; it++)
        {
            string itName = cursor.ReadString();
            string itDisplay = cursor.ReadString();
            string itIcon = cursor.ReadString();
            int cost = cursor.ReadInt32();
            string category = cursor.ReadString();
            items[it] = new ItemDescriptorRecord(itName, itDisplay, itIcon, cost, category);
        }

        return new HeroFlatBufferLoader(manifest, heroes, items);
    }

    /// <summary>
    ///     Loads and parses the binary FlatBuffer from a file path.
    /// </summary>
    public static async Task<HeroFlatBufferLoader> LoadFromFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"FlatBuffers binary file not found: {filePath}. Run 'kongor prebake entities' to generate it.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        return Load(bytes);
    }

    /// <summary>
    ///     Synchronous file loader.
    /// </summary>
    public static HeroFlatBufferLoader LoadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"FlatBuffers binary file not found: {filePath}. Run 'kongor prebake entities' to generate it.");
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        return Load(bytes);
    }

    public HeroDescriptorRecord? GetHero(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _heroesByName.GetValueOrDefault(name);
    }

    public HeroDescriptorRecord? GetHeroById(uint id)
    {
        return _heroesById.GetValueOrDefault(id);
    }

    public ItemDescriptorRecord? GetItem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _itemsByName.GetValueOrDefault(name);
    }

    public bool HasHero(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _heroesByName.ContainsKey(name);
    }

    public bool HasItem(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _itemsByName.ContainsKey(name);
    }

    private static void VerifyChecksumOrThrow(ReadOnlySpan<byte> buffer, ref ulong timestamp, ref ulong storedChecksum)
    {
        XxHash3 hasher = t_xxHash3 ??= new XxHash3();
        hasher.Reset();

        Span<byte> zero8 = stackalloc byte[8];
        zero8.Clear();

        hasher.Append(buffer.Slice(0, HeroFlatBufferGenerator.HeaderChecksumOffset));
        hasher.Append(zero8);
        hasher.Append(buffer.Slice(HeroFlatBufferGenerator.HeaderChecksumOffset + sizeof(ulong)));
        ulong computed = hasher.GetCurrentHashAsUInt64();
        if (computed == storedChecksum && storedChecksum != 0UL)
        {
            return;
        }

        // Backwards-compatibility check for legacy v1 buffers written with checksum at byte offset 16..23
        if ((storedChecksum >> 32) == 0UL)
        {
            ulong legacyStored = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(16, sizeof(ulong)));
            if (legacyStored != 0UL)
            {
                Span<byte> zero12 = stackalloc byte[12];
                zero12.Clear();
                hasher.Reset();
                hasher.Append(buffer.Slice(0, 16));
                hasher.Append(zero12);
                hasher.Append(buffer.Slice(28));
                if (hasher.GetCurrentHashAsUInt64() == legacyStored)
                {
                    timestamp &= 0xFFFFFFFFUL;
                    storedChecksum = legacyStored;
                    return;
                }
            }
        }

        throw new InvalidDataException(
            $"KNFB XxHash3 checksum mismatch (stored=0x{storedChecksum:X16}, computed=0x{computed:X16}). Buffer is corrupted or truncated; regenerate via 'kongor prebake entities'.");
    }

    private ref struct SpanCursor(ReadOnlySpan<byte> buffer, int initialPosition)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _pos = initialPosition;

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _buffer[_pos++];
        }

        public int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            int val = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_pos, sizeof(int)));
            _pos += sizeof(int);
            return val;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(sizeof(uint));
            uint val = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_pos, sizeof(uint)));
            _pos += sizeof(uint);
            return val;
        }

        public uint ReadCount(string fieldLabel)
        {
            uint count = ReadUInt32();
            if (count > MaxReasonableCollectionEntries)
            {
                throw new InvalidDataException(
                    $"Corrupted KNFB {fieldLabel} count ({count}) at byte offset {_pos - sizeof(uint)}. Regenerate via 'kongor prebake entities'.");
            }

            return count;
        }

        public float ReadSingle()
        {
            EnsureAvailable(sizeof(float));
            float val = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_pos, sizeof(float)));
            _pos += sizeof(float);
            return val;
        }

        public string ReadString()
        {
            ReadOnlySpan<byte> slice = ReadUtf8Slice();
            if (slice.IsEmpty) return string.Empty;
            string? wellKnown = TryMatchWellKnownToken(slice);
            return wellKnown ?? Encoding.UTF8.GetString(slice);
        }

        public string ReadMatchingOrString(string expectedAsciiCandidate)
        {
            ReadOnlySpan<byte> slice = ReadUtf8Slice();
            if (slice.IsEmpty) return string.Empty;
            if (slice.Length == expectedAsciiCandidate.Length && AsciiEquals(slice, expectedAsciiCandidate))
            {
                return expectedAsciiCandidate;
            }

            return Encoding.UTF8.GetString(slice);
        }

        private ReadOnlySpan<byte> ReadUtf8Slice()
        {
            EnsureAvailable(sizeof(ushort));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_pos, sizeof(ushort)));
            _pos += sizeof(ushort);
            if (len == 0) return ReadOnlySpan<byte>.Empty;

            EnsureAvailable(len);
            ReadOnlySpan<byte> slice = _buffer.Slice(_pos, len);
            _pos += len;
            return slice;
        }

        private void EnsureAvailable(int byteCount)
        {
            if ((uint)(_buffer.Length - _pos) < (uint)byteCount)
            {
                throw new InvalidDataException(
                    $"Unexpected end of KNFB buffer at offset {_pos} while reading {byteCount} bytes (total length={_buffer.Length}). Regenerate via 'kongor prebake entities'.");
            }
        }

        private static bool AsciiEquals(ReadOnlySpan<byte> utf8, string candidate)
        {
            for (int i = 0; i < utf8.Length; i++)
            {
                if (utf8[i] != candidate[i]) return false;
            }

            return true;
        }

        private static string? TryMatchWellKnownToken(ReadOnlySpan<byte> utf8)
        {
            return utf8.Length switch
            {
                1 when utf8[0] == (byte)'0' => "0",
                4 when utf8.SequenceEqual("self"u8) => "self",
                5 when utf8.SequenceEqual("Boots"u8) => "Boots",
                5 when utf8.SequenceEqual("Morph"u8) => "Morph",
                6 when utf8.SequenceEqual("Legion"u8) => "Legion",
                6 when utf8.SequenceEqual("Caster"u8) => "Caster",
                7 when utf8.SequenceEqual("passive"u8) => "passive",
                7 when utf8.SequenceEqual("Support"u8) => "Support",
                9 when utf8.SequenceEqual("Combative"u8) => "Combative",
                9 when utf8.SequenceEqual("no_target"u8) => "no_target",
                10 when utf8.SequenceEqual("Hellbourne"u8) => "Hellbourne",
                10 when utf8.SequenceEqual("Protective"u8) => "Protective",
                11 when utf8.SequenceEqual("Consumables"u8) => "Consumables",
                13 when utf8.SequenceEqual("target_entity"u8) => "target_entity",
                15 when utf8.SequenceEqual("target_position"u8) => "target_position",
                _ => null
            };
        }
    }
}
