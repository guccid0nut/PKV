// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Domain.Clans;

public interface IPendingClanService
{
    void RemoveObsoledPendingClans();
    bool IsUserInPendingClans(Account account);
    bool IsClanInPendingClans(string name, string tag);
    bool IsClanNameInPendingClans(string name);
    bool IsClanTagInPendingClans(string tag);
    void InsertPendingClan(PendingClan pendingClan);
    void RemovePendingClan(string pendingClanTagAndName);
    PendingClan? GetPendingClanForUser(Account account);

    // Invite Helpers
    void RemoveObsoledPendingClanInvites();
    bool IsUserInPendingClanInvites(string username);
    string? GetPendingClanInviteKeyForUser(Account account);
    void InsertPendingClanInvite(string key, PendingClanInvite invite);
    void RemovePendingClanInvite(string key);
    PendingClanInvite? GetPendingClanInvite(string key);
}

public class PendingClanService : IPendingClanService
{
    // Key: Invite Key? Legacy didn't specify key format clearly in helper but used Dictionary.
    // Legacy InsertPendingClanInvite just added to dictionary. 
    // We will assume Key is Username or something unique. 
    // Legacy GetPendingClanInviteKeyForUser iterates values.
    private static readonly ConcurrentDictionary<string, PendingClanInvite> _pendingClanInvites = new();
    private static readonly ConcurrentDictionary<string, PendingClan> _pendingClans = new();

    public void RemoveObsoledPendingClans()
    {
        foreach (string key in _pendingClans.Where(kvp => !kvp.Value.IsValid).Select(kvp => kvp.Key).ToList())
            _pendingClans.TryRemove(key, out _);
    }

    public bool IsUserInPendingClans(Account account)
    {
        return _pendingClans.Any(kvp =>
            kvp.Value.MembersAccountId.Contains(account.ID) || kvp.Value.CreatorAccountId == account.ID);
    }

    public bool IsClanInPendingClans(string name, string tag)
    {
        return IsClanNameInPendingClans(name) || IsClanTagInPendingClans(tag);
    }

    public bool IsClanNameInPendingClans(string name)
    {
        return _pendingClans.Any(kvp =>
            kvp.Value.ClanName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsClanTagInPendingClans(string tag)
    {
        return _pendingClans.Any(kvp =>
            kvp.Value.ClanTag.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    public void InsertPendingClan(PendingClan pendingClan)
    {
        string key = $"[{pendingClan.ClanTag.ToLowerInvariant()}]{pendingClan.ClanName.ToLowerInvariant()}";
        _pendingClans[key] = pendingClan;
    }

    public void RemovePendingClan(string pendingClanTagAndName)
    {
        _pendingClans.TryRemove(pendingClanTagAndName, out _);
    }

    public PendingClan? GetPendingClanForUser(Account account)
    {
        return (from kvp in _pendingClans where kvp.Value.MembersAccountId.Contains(account.ID) select kvp.Value)
            .FirstOrDefault();
    }

    public void RemoveObsoledPendingClanInvites()
    {
        foreach (string key in _pendingClanInvites.Where(kvp => !kvp.Value.IsValid).Select(kvp => kvp.Key).ToList())
            _pendingClanInvites.TryRemove(key, out _);
    }

    public bool IsUserInPendingClanInvites(string username)
    {
        return _pendingClanInvites.ContainsKey(username);
    }

    public string? GetPendingClanInviteKeyForUser(Account account)
    {
        return (from kvp in _pendingClanInvites where kvp.Value.InvitedAccountId == account.ID select kvp.Key)
            .FirstOrDefault();
    }

    public void InsertPendingClanInvite(string key, PendingClanInvite invite)
    {
        _pendingClanInvites[key] = invite;
    }

    public void RemovePendingClanInvite(string key)
    {
        _pendingClanInvites.TryRemove(key, out _);
    }

    public PendingClanInvite? GetPendingClanInvite(string key)
    {
        return _pendingClanInvites.TryGetValue(key, out PendingClanInvite? invite) ? invite : null;
    }
}
