// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.Chat;

/// <summary>
///     Distributed user presence record shared across chat cluster nodes via Redis.
/// </summary>
[MemoryPackable]
public partial class DistributedUserPresence
{
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string NodeID { get; set; } = string.Empty;
    public byte ClientStatus { get; set; }
    public byte ChatMode { get; set; }
    public int? ClanID { get; set; }
    public byte ClientFlags { get; set; }
    public string? ChatSymbol { get; set; }
    public string? NameColour { get; set; }
    public string? AccountIcon { get; set; }
    public int AscensionLevel { get; set; }
    public string? MatchAddress { get; set; }
    public int? MatchID { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
}

/// <summary>
///     Distributed channel metadata tracked cluster-wide.
/// </summary>
[MemoryPackable]
public partial class DistributedChannelMetadata
{
    public int ChannelID { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string? Topic { get; set; }
    public string? PasswordHash { get; set; }
    public uint Flags { get; set; }
    public int OwnerAccountID { get; set; }
    public int MemberCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
///     Represents a channel member's cluster-wide presence and node affinity.
/// </summary>
[MemoryPackable]
public partial class DistributedChannelMember
{
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string NodeID { get; set; } = string.Empty;
    public byte AdminLevel { get; set; }
    public DateTimeOffset JoinedAtUtc { get; set; }
}

/// <summary>
///     Directed whisper message envelope routed across nodes.
/// </summary>
[MemoryPackable]
public partial class DistributedWhisperEnvelope
{
    public string SenderName { get; set; } = string.Empty;
    public int SenderAccountID { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public int TargetAccountID { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OriginNodeID { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>
///     Channel message envelope broadcast to all subscribed cluster nodes.
/// </summary>
[MemoryPackable]
public partial class DistributedChannelMessageEnvelope
{
    public int ChannelID { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public int SenderAccountID { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OriginNodeID { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>
///     Clan whisper envelope broadcast to nodes hosting online clan members.
/// </summary>
[MemoryPackable]
public partial class DistributedClanWhisperEnvelope
{
    public int ClanID { get; set; }
    public int SenderAccountID { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OriginNodeID { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>
///     Cluster-wide presence update event.
/// </summary>
[MemoryPackable]
public partial class DistributedPresenceUpdateEnvelope
{
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string NodeID { get; set; } = string.Empty;
    public byte OldStatus { get; set; }
    public byte NewStatus { get; set; }
    public byte ChatMode { get; set; }
    public string? MatchAddress { get; set; }
    public int? MatchID { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>
///     Eviction notice for terminating stale or duplicate concurrent sessions across nodes.
/// </summary>
[MemoryPackable]
public partial class DistributedEvictionEnvelope
{
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string RequestingNodeID { get; set; } = string.Empty;
    public byte ReasonCode { get; set; }
}
