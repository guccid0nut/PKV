// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MemoryPack;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Bitmask flags indicating which component slices are present in an entity delta update.
/// </summary>
[Flags]
public enum DeltaFieldMask : uint
{
    None = 0,
    Coordinates = 1 << 0,
    Combat = 1 << 1,
    Animation = 1 << 2,
    Inventory = 1 << 3,
    Orders = 1 << 4,
    FullState = Coordinates | Combat | Animation | Inventory | Orders
}

/// <summary>
///     Spatial coordinate and kinematic velocity state for an entity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[MemoryPackable]
public partial struct EntityCoordinateDelta
{
    public float PosX;
    public float PosY;
    public float PosZ;
    public short Yaw;       // Quantized heading angle (0-65535 mapped to -32768..32767)
    public short Pitch;
    public short VelocityX; // Quantized velocity vector
    public short VelocityY;
    public short VelocityZ;
    public byte MovementFlags; // 0x01: Moving, 0x02: Pathing, 0x04: Stunned, 0x08: Incorporeal

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination[0..4], PosX);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..8], PosY);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..12], PosZ);
        BinaryPrimitives.WriteInt16LittleEndian(destination[12..14], Yaw);
        BinaryPrimitives.WriteInt16LittleEndian(destination[14..16], Pitch);
        BinaryPrimitives.WriteInt16LittleEndian(destination[16..18], VelocityX);
        BinaryPrimitives.WriteInt16LittleEndian(destination[18..20], VelocityY);
        BinaryPrimitives.WriteInt16LittleEndian(destination[20..22], VelocityZ);
        destination[22] = MovementFlags;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityCoordinateDelta ReadFrom(ReadOnlySpan<byte> source)
    {
        return new EntityCoordinateDelta
        {
            PosX = BinaryPrimitives.ReadSingleLittleEndian(source[0..4]),
            PosY = BinaryPrimitives.ReadSingleLittleEndian(source[4..8]),
            PosZ = BinaryPrimitives.ReadSingleLittleEndian(source[8..12]),
            Yaw = BinaryPrimitives.ReadInt16LittleEndian(source[12..14]),
            Pitch = BinaryPrimitives.ReadInt16LittleEndian(source[14..16]),
            VelocityX = BinaryPrimitives.ReadInt16LittleEndian(source[16..18]),
            VelocityY = BinaryPrimitives.ReadInt16LittleEndian(source[18..20]),
            VelocityZ = BinaryPrimitives.ReadInt16LittleEndian(source[20..22]),
            MovementFlags = source[22]
        };
    }

    public const int Size = 23;
}

/// <summary>
///     Combat vital stats, cooldowns, and status modifier bitfields.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[MemoryPackable]
public partial struct EntityCombatDelta
{
    public float CurrentHealth;
    public float MaxHealth;
    public float CurrentMana;
    public float MaxMana;
    public float AttackCooldown;
    public float CastCooldown;
    public uint StatusFlags; // 0x01: Invulnerable, 0x02: Silenced, 0x04: Disarmed, 0x08: Magic Immune
    public float Armor;
    public float MagicArmor;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination[0..4], CurrentHealth);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..8], MaxHealth);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..12], CurrentMana);
        BinaryPrimitives.WriteSingleLittleEndian(destination[12..16], MaxMana);
        BinaryPrimitives.WriteSingleLittleEndian(destination[16..20], AttackCooldown);
        BinaryPrimitives.WriteSingleLittleEndian(destination[20..24], CastCooldown);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], StatusFlags);
        BinaryPrimitives.WriteSingleLittleEndian(destination[28..32], Armor);
        BinaryPrimitives.WriteSingleLittleEndian(destination[32..36], MagicArmor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityCombatDelta ReadFrom(ReadOnlySpan<byte> source)
    {
        return new EntityCombatDelta
        {
            CurrentHealth = BinaryPrimitives.ReadSingleLittleEndian(source[0..4]),
            MaxHealth = BinaryPrimitives.ReadSingleLittleEndian(source[4..8]),
            CurrentMana = BinaryPrimitives.ReadSingleLittleEndian(source[8..12]),
            MaxMana = BinaryPrimitives.ReadSingleLittleEndian(source[12..16]),
            AttackCooldown = BinaryPrimitives.ReadSingleLittleEndian(source[16..20]),
            CastCooldown = BinaryPrimitives.ReadSingleLittleEndian(source[20..24]),
            StatusFlags = BinaryPrimitives.ReadUInt32LittleEndian(source[24..28]),
            Armor = BinaryPrimitives.ReadSingleLittleEndian(source[28..32]),
            MagicArmor = BinaryPrimitives.ReadSingleLittleEndian(source[32..36])
        };
    }

    public const int Size = 36;
}

/// <summary>
///     Skeletal animation playback state and channel blend weights.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[MemoryPackable]
public partial struct EntityAnimationDelta
{
    public ushort AnimationId;
    public ushort SequenceIndex;
    public ushort CurrentFrame;
    public float PlaybackSpeed;
    public byte BlendWeight; // 0-255 representing 0.0 to 1.0

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0..2], AnimationId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..4], SequenceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], CurrentFrame);
        BinaryPrimitives.WriteSingleLittleEndian(destination[6..10], PlaybackSpeed);
        destination[10] = BlendWeight;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityAnimationDelta ReadFrom(ReadOnlySpan<byte> source)
    {
        return new EntityAnimationDelta
        {
            AnimationId = BinaryPrimitives.ReadUInt16LittleEndian(source[0..2]),
            SequenceIndex = BinaryPrimitives.ReadUInt16LittleEndian(source[2..4]),
            CurrentFrame = BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]),
            PlaybackSpeed = BinaryPrimitives.ReadSingleLittleEndian(source[6..10]),
            BlendWeight = source[10]
        };
    }

    public const int Size = 11;
}

/// <summary>
///     Item slot descriptor.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[MemoryPackable]
public partial struct ItemSlotDelta
{
    public ushort ItemId;
    public byte Charges;
    public float CooldownRemaining;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0..2], ItemId);
        destination[2] = Charges;
        BinaryPrimitives.WriteSingleLittleEndian(destination[3..7], CooldownRemaining);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ItemSlotDelta ReadFrom(ReadOnlySpan<byte> source)
    {
        return new ItemSlotDelta
        {
            ItemId = BinaryPrimitives.ReadUInt16LittleEndian(source[0..2]),
            Charges = source[2],
            CooldownRemaining = BinaryPrimitives.ReadSingleLittleEndian(source[3..7])
        };
    }

    public const int Size = 7;
}

/// <summary>
///     Full 6-slot hero inventory and 3-slot stash state.
/// </summary>
[MemoryPackable]
public partial class EntityInventoryDelta
{
    public ItemSlotDelta Slot0;
    public ItemSlotDelta Slot1;
    public ItemSlotDelta Slot2;
    public ItemSlotDelta Slot3;
    public ItemSlotDelta Slot4;
    public ItemSlotDelta Slot5;
    public ItemSlotDelta Stash0;
    public ItemSlotDelta Stash1;
    public ItemSlotDelta Stash2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        Slot0.WriteTo(destination[0..7]);
        Slot1.WriteTo(destination[7..14]);
        Slot2.WriteTo(destination[14..21]);
        Slot3.WriteTo(destination[21..28]);
        Slot4.WriteTo(destination[28..35]);
        Slot5.WriteTo(destination[35..42]);
        Stash0.WriteTo(destination[42..49]);
        Stash1.WriteTo(destination[49..56]);
        Stash2.WriteTo(destination[56..63]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityInventoryDelta ReadFrom(ReadOnlySpan<byte> source)
    {
        return new EntityInventoryDelta
        {
            Slot0 = ItemSlotDelta.ReadFrom(source[0..7]),
            Slot1 = ItemSlotDelta.ReadFrom(source[7..14]),
            Slot2 = ItemSlotDelta.ReadFrom(source[14..21]),
            Slot3 = ItemSlotDelta.ReadFrom(source[21..28]),
            Slot4 = ItemSlotDelta.ReadFrom(source[28..35]),
            Slot5 = ItemSlotDelta.ReadFrom(source[35..42]),
            Stash0 = ItemSlotDelta.ReadFrom(source[42..49]),
            Stash1 = ItemSlotDelta.ReadFrom(source[49..56]),
            Stash2 = ItemSlotDelta.ReadFrom(source[56..63])
        };
    }

    public const int Size = ItemSlotDelta.Size * 9; // 63 bytes
}

/// <summary>
///     Active unit command or queued action order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[MemoryPackable]
public partial struct EntityOrderDelta
{
    public byte OrderType; // 0: None, 1: Move, 2: Attack, 3: CastAbility, 4: Stop, 5: Hold
    public ushort TargetEntityId;
    public float TargetPosX;
    public float TargetPosY;
    public float TargetPosZ;
    public byte AbilitySlot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        destination[0] = OrderType;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..3], TargetEntityId);
        BinaryPrimitives.WriteSingleLittleEndian(destination[3..7], TargetPosX);
        BinaryPrimitives.WriteSingleLittleEndian(destination[7..11], TargetPosY);
        BinaryPrimitives.WriteSingleLittleEndian(destination[11..15], TargetPosZ);
        destination[15] = AbilitySlot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityOrderDelta ReadFrom(ReadOnlySpan<byte> source)
    {
        return new EntityOrderDelta
        {
            OrderType = source[0],
            TargetEntityId = BinaryPrimitives.ReadUInt16LittleEndian(source[1..3]),
            TargetPosX = BinaryPrimitives.ReadSingleLittleEndian(source[3..7]),
            TargetPosY = BinaryPrimitives.ReadSingleLittleEndian(source[7..11]),
            TargetPosZ = BinaryPrimitives.ReadSingleLittleEndian(source[11..15]),
            AbilitySlot = source[15]
        };
    }

    public const int Size = 16;
}

/// <summary>
///     Full delta slice for a single entity.
/// </summary>
[MemoryPackable]
public partial class SnapshotEntityDelta
{
    public ushort EntityId { get; set; }
    public byte EntityType { get; set; } // 1: Hero, 2: Creep, 3: Neutral, 4: Building, 5: Projectile
    public DeltaFieldMask ChangedMask { get; set; }
    public EntityCoordinateDelta Coordinates { get; set; }
    public EntityCombatDelta Combat { get; set; }
    public EntityAnimationDelta Animation { get; set; }
    public EntityInventoryDelta? Inventory { get; set; }
    public EntityOrderDelta Orders { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSerializedSize()
    {
        return SimdEntityDelta.GetTotalSerializedSize(ChangedMask);
    }

    public void WriteTo(ref Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0..2], EntityId);
        destination[2] = EntityType;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[3..7], (uint)ChangedMask);
        destination = destination[7..];

        if (ChangedMask.HasFlag(DeltaFieldMask.Coordinates))
        {
            Coordinates.WriteTo(destination[0..EntityCoordinateDelta.Size]);
            destination = destination[EntityCoordinateDelta.Size..];
        }

        if (ChangedMask.HasFlag(DeltaFieldMask.Combat))
        {
            Combat.WriteTo(destination[0..EntityCombatDelta.Size]);
            destination = destination[EntityCombatDelta.Size..];
        }

        if (ChangedMask.HasFlag(DeltaFieldMask.Animation))
        {
            Animation.WriteTo(destination[0..EntityAnimationDelta.Size]);
            destination = destination[EntityAnimationDelta.Size..];
        }

        if (ChangedMask.HasFlag(DeltaFieldMask.Inventory))
        {
            (Inventory ?? new EntityInventoryDelta()).WriteTo(destination[0..EntityInventoryDelta.Size]);
            destination = destination[EntityInventoryDelta.Size..];
        }

        if (ChangedMask.HasFlag(DeltaFieldMask.Orders))
        {
            Orders.WriteTo(destination[0..EntityOrderDelta.Size]);
            destination = destination[EntityOrderDelta.Size..];
        }
    }

    public static SnapshotEntityDelta ReadFrom(ref ReadOnlySpan<byte> source)
    {
        ushort entityId = BinaryPrimitives.ReadUInt16LittleEndian(source[0..2]);
        byte entityType = source[2];
        DeltaFieldMask mask = (DeltaFieldMask)BinaryPrimitives.ReadUInt32LittleEndian(source[3..7]);
        source = source[7..];

        SnapshotEntityDelta delta = new()
        {
            EntityId = entityId,
            EntityType = entityType,
            ChangedMask = mask
        };

        if (mask.HasFlag(DeltaFieldMask.Coordinates))
        {
            delta.Coordinates = EntityCoordinateDelta.ReadFrom(source[0..EntityCoordinateDelta.Size]);
            source = source[EntityCoordinateDelta.Size..];
        }

        if (mask.HasFlag(DeltaFieldMask.Combat))
        {
            delta.Combat = EntityCombatDelta.ReadFrom(source[0..EntityCombatDelta.Size]);
            source = source[EntityCombatDelta.Size..];
        }

        if (mask.HasFlag(DeltaFieldMask.Animation))
        {
            delta.Animation = EntityAnimationDelta.ReadFrom(source[0..EntityAnimationDelta.Size]);
            source = source[EntityAnimationDelta.Size..];
        }

        if (mask.HasFlag(DeltaFieldMask.Inventory))
        {
            delta.Inventory = EntityInventoryDelta.ReadFrom(source[0..EntityInventoryDelta.Size]);
            source = source[EntityInventoryDelta.Size..];
        }

        if (mask.HasFlag(DeltaFieldMask.Orders))
        {
            delta.Orders = EntityOrderDelta.ReadFrom(source[0..EntityOrderDelta.Size]);
            source = source[EntityOrderDelta.Size..];
        }

        return delta;
    }
}

/// <summary>
///     Header framing for a tick snapshot delta packet.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[MemoryPackable]
public partial struct SnapshotFrameHeader
{
    public uint FrameSequence;
    public uint ServerTick;
    public uint ClientAckSequence;
    public ushort EntityCount;

    public const int Size = 14;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], FrameSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], ServerTick);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], ClientAckSequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..14], EntityCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SnapshotFrameHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        return new SnapshotFrameHeader
        {
            FrameSequence = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
            ServerTick = BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
            ClientAckSequence = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]),
            EntityCount = BinaryPrimitives.ReadUInt16LittleEndian(source[12..14])
        };
    }
}

/// <summary>
///     Complete multi-entity delta snapshot frame.
/// </summary>
[MemoryPackable]
public partial class SnapshotFrameDelta
{
    public SnapshotFrameHeader Header;
    public List<SnapshotEntityDelta> Entities { get; set; } = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSerializedSize()
    {
        return SnapshotFrameHeader.Size + SimdEntityDelta.ComputeTotalSerializedSize(Entities);
    }

    public byte[] Serialize()
    {
        Header.EntityCount = (ushort)Entities.Count;
        byte[] buffer = new byte[GetSerializedSize()];
        Span<byte> span = buffer;
        Header.WriteTo(span[0..SnapshotFrameHeader.Size]);
        span = span[SnapshotFrameHeader.Size..];

        foreach (SnapshotEntityDelta entity in Entities)
            entity.WriteTo(ref span);

        return buffer;
    }

    public static SnapshotFrameDelta Deserialize(ReadOnlySpan<byte> buffer)
    {
        SnapshotFrameHeader header = SnapshotFrameHeader.ReadFrom(buffer[0..SnapshotFrameHeader.Size]);
        ReadOnlySpan<byte> span = buffer[SnapshotFrameHeader.Size..];

        List<SnapshotEntityDelta> entities = new(header.EntityCount);
        for (int i = 0; i < header.EntityCount && span.Length >= 7; i++)
        {
            entities.Add(SnapshotEntityDelta.ReadFrom(ref span));
        }

        return new SnapshotFrameDelta
        {
            Header = header,
            Entities = entities
        };
    }
}
