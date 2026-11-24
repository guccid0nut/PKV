// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MemoryPack;

namespace ASPIRE.Common.Models.ServerManagement;

// ReSharper disable InconsistentNaming
// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable PartialTypeWithSinglePart
[MemoryPackable]
public partial class MatchServer
{
    public required int HostAccountID { get; set; }

    public required string HostAccountName { get; set; }

    public required int ID { get; set; }

    public required string Name { get; set; }

    public int? MatchServerManagerID { get; set; }

    public required int Instance { get; set; }

    public required string IPAddress { get; set; }

    public string? TruePublicIPAddress { get; set; }

    public string? LocalIPAddress { get; set; }

    public required int Port { get; set; }

    public int? LocalPort { get; set; }

    public required string Location { get; set; }

    public required string Description { get; set; }

    public bool IsPrivate { get; set; }

    public ServerStatus Status { get; set; } = ServerStatus.SERVER_STATUS_UNKNOWN;

    public int ConnectedPlayersCount { get; set; } = 0;

    public string Cookie { get; set; } = Guid.CreateVersion7().ToString();

    public DateTimeOffset TimestampRegistered { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset TimestampLastHeartbeat { get; set; } = TimeProvider.System.GetUtcNow();

    public static string GetQueueArcadeAllowKey(int serverId, int accountId) =>
        $"ARCADE-ALLOW:{serverId}:{accountId}";

    public static bool IsQueueArcadeHostAccount(string? hostAccountName) =>
        !string.IsNullOrWhiteSpace(hostAccountName) &&
        (hostAccountName.EndsWith("_arc", StringComparison.OrdinalIgnoreCase) ||
         hostAccountName.StartsWith("kongor_arcade", StringComparison.OrdinalIgnoreCase));

    public static bool IsQueueArcadeInstance(int instance) =>
        instance is >= 4000 and < 5000;

    public static bool IsQueueArcadePort(int port) =>
        port is (>= 11485 and <= 11534) or (>= 21485 and <= 21534);

    public static bool IsStandardDedicatedPort(int port) =>
        port is (>= 11235 and <= 11484) or (>= 21235 and <= 21484);

    /// <summary>
    /// Classifies whether this MatchServer belongs to the dedicated Queue Arcade (Tier Q) pool using
    /// server-authenticated attributes (HostAccountName, COMPEL Tier Q Instance 4000..4999, and Tier Q ports).
    /// Deliberately ignores mutable <see cref="Name"/> and <see cref="Description"/> so player-named lobbies
    /// on standard dedicated servers cannot spoof Queue Arcade classification.
    /// </summary>
    public bool IsQueueArcadeServer() =>
        IsQueueArcadeHostAccount(HostAccountName) ||
        IsQueueArcadeInstance(Instance) ||
        IsQueueArcadePort(Port) ||
        (LocalPort.HasValue && IsQueueArcadePort(LocalPort.Value));
}
