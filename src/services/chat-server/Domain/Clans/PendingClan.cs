// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Domain.Clans;

public class PendingClan
{
    public required string ClanName { get; set; }
    public required string ClanTag { get; set; }
    public int CreatorAccountId { get; set; }
    public required List<int> MembersAccountId { get; set; }
    public required List<bool> Accepted { get; set; }
    public DateTime CreationTime { get; set; }

    public bool IsValid => CreationTime.AddMinutes(2) >= TimeProvider.System.GetUtcNow().UtcDateTime;
}

public class PendingClanInvite
{
    public required string ClanName { get; set; }
    public required string ClanTag { get; set; }
    public int ClanId { get; set; }
    public int InitiatorAccountId { get; set; }
    public int InvitedAccountId { get; set; }
    public DateTime CreationTime { get; set; }
    public bool IsValid => CreationTime.AddMinutes(2) >= TimeProvider.System.GetUtcNow().UtcDateTime;
}
