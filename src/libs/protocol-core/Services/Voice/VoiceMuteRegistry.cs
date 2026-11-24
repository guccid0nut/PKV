// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceMuteRegistry.cs
// System: ProjectKongor - Opus Voice Subsystem
// Policy: Strict Explicit Typing (NO 'var') - High-Performance In-Memory Mute Cache
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     Immutable record representing an active or scheduled mute sanction on an account.
/// </summary>
public readonly record struct VoiceMuteEntry(
    uint AccountId,
    DateTimeOffset? Expiration,
    string? Reason,
    DateTimeOffset CreatedAt)
{
    public bool IsPermanent => !Expiration.HasValue;

    public bool IsActive(DateTimeOffset now) => !Expiration.HasValue || Expiration.Value > now;
}

/// <summary>
///     Registry interface for querying and modifying account mute sanctions across the voice subsystem.
/// </summary>
public interface IVoiceMuteRegistry
{
    /// <summary>
    ///     Checks whether the specified account is currently muted.
    ///     Returns true if muted; false otherwise. Auto-purges expired mutes on the fly.
    /// </summary>
    bool IsMuted(uint accountId);

    /// <summary>
    ///     Registers or updates a voice mute on the specified account.
    /// </summary>
    void SetMute(uint accountId, DateTimeOffset? expiration = null, string? reason = null);

    /// <summary>
    ///     Removes a voice mute on the specified account (unmute / pardon).
    /// </summary>
    void RemoveMute(uint accountId);

    /// <summary>
    ///     Retrieves all currently active mute entries.
    /// </summary>
    IReadOnlyList<VoiceMuteEntry> GetActiveMutes();

    /// <summary>
    ///     Clears all mute entries.
    /// </summary>
    void Clear();

    /// <summary>
    ///     Checks whether a listener has ignored a specific speaker.
    /// </summary>
    bool IsParticipantIgnored(uint listenerAccountId, uint speakerAccountId);

    /// <summary>
    ///     Sets or clears an ignore relationship where listener ignores speaker.
    /// </summary>
    void SetIgnore(uint listenerAccountId, uint speakerAccountId, bool ignored);

    /// <summary>
    ///     Batch-sets ignored speakers for a listener account.
    /// </summary>
    void SetIgnores(uint listenerAccountId, IEnumerable<uint> ignoredSpeakerAccountIds);

    /// <summary>
    ///     Clears all ignores registered by a listener.
    /// </summary>
    void ClearIgnores(uint listenerAccountId);
}

/// <summary>
///     High-performance, thread-safe, lock-free in-memory registry for voice sanctions.
///     Provides O(1) lookup on hot UDP datagram paths in COMPEL and client-side suppression in Willowmaker.
/// </summary>
public sealed class VoiceMuteRegistry : IVoiceMuteRegistry
{
    private readonly ConcurrentDictionary<uint, VoiceMuteEntry> _mutes = new();
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, byte>> _ignoredSpeakersByListener = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VoiceMuteRegistry> _logger;

    public VoiceMuteRegistry(TimeProvider? timeProvider = null, ILogger<VoiceMuteRegistry>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VoiceMuteRegistry>.Instance;
    }

    public bool IsMuted(uint accountId)
    {
        if (accountId == 0)
        {
            return false;
        }

        if (_mutes.TryGetValue(accountId, out VoiceMuteEntry entry))
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (entry.IsActive(now))
            {
                return true;
            }

            // Mute has naturally expired; purge on access
            if (_mutes.TryRemove(accountId, out _))
            {
                _logger.LogInformation(
                    "[VoiceMuteRegistry] Expired mute for Account {AccountId} automatically purged.",
                    accountId);
            }
        }

        return false;
    }

    public void SetMute(uint accountId, DateTimeOffset? expiration = null, string? reason = null)
    {
        if (accountId == 0)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        VoiceMuteEntry entry = new VoiceMuteEntry(accountId, expiration, reason, now);
        _mutes[accountId] = entry;

        _logger.LogInformation(
            "[VoiceMuteRegistry] Account {AccountId} MUTED until {Expiration} UTC. Reason: {Reason}",
            accountId,
            expiration.HasValue ? expiration.Value.ToString("yyyy-MM-dd HH:mm:ss") : "PERMANENT",
            reason ?? "Staff Sanction");
    }

    public void RemoveMute(uint accountId)
    {
        if (accountId == 0)
        {
            return;
        }

        if (_mutes.TryRemove(accountId, out VoiceMuteEntry removed))
        {
            _logger.LogInformation(
                "[VoiceMuteRegistry] Account {AccountId} UNMUTED. Previous expiration: {Expiration}",
                accountId,
                removed.Expiration.HasValue ? removed.Expiration.Value.ToString("yyyy-MM-dd HH:mm:ss") : "PERMANENT");
        }
    }

    public IReadOnlyList<VoiceMuteEntry> GetActiveMutes()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<VoiceMuteEntry> active = new List<VoiceMuteEntry>();

        foreach (KeyValuePair<uint, VoiceMuteEntry> pair in _mutes)
        {
            if (pair.Value.IsActive(now))
            {
                active.Add(pair.Value);
            }
            else
            {
                _mutes.TryRemove(pair.Key, out _);
            }
        }

        return active;
    }

    public void Clear()
    {
        int count = _mutes.Count;
        _mutes.Clear();
        _ignoredSpeakersByListener.Clear();
        _logger.LogInformation("[VoiceMuteRegistry] Cleared all {Count} mute records and all ignore records.", count);
    }

    public bool IsParticipantIgnored(uint listenerAccountId, uint speakerAccountId)
    {
        if (listenerAccountId == 0 || speakerAccountId == 0)
        {
            return false;
        }

        if (_ignoredSpeakersByListener.TryGetValue(listenerAccountId, out ConcurrentDictionary<uint, byte>? ignored))
        {
            return ignored.ContainsKey(speakerAccountId);
        }

        return false;
    }

    public void SetIgnore(uint listenerAccountId, uint speakerAccountId, bool ignored)
    {
        if (listenerAccountId == 0 || speakerAccountId == 0)
        {
            return;
        }

        if (ignored)
        {
            ConcurrentDictionary<uint, byte> set = _ignoredSpeakersByListener.GetOrAdd(
                listenerAccountId,
                static _ => new ConcurrentDictionary<uint, byte>());
            set[speakerAccountId] = 1;
            _logger.LogInformation(
                "[VoiceMuteRegistry] Listener {ListenerAccountId} IGNORED speaker {SpeakerAccountId}.",
                listenerAccountId, speakerAccountId);
        }
        else
        {
            if (_ignoredSpeakersByListener.TryGetValue(listenerAccountId, out ConcurrentDictionary<uint, byte>? set))
            {
                set.TryRemove(speakerAccountId, out _);
                _logger.LogInformation(
                    "[VoiceMuteRegistry] Listener {ListenerAccountId} UNIGNORED speaker {SpeakerAccountId}.",
                    listenerAccountId, speakerAccountId);
            }
        }
    }

    public void SetIgnores(uint listenerAccountId, IEnumerable<uint> ignoredSpeakerAccountIds)
    {
        if (listenerAccountId == 0)
        {
            return;
        }

        ConcurrentDictionary<uint, byte> set = new ConcurrentDictionary<uint, byte>();
        foreach (uint id in ignoredSpeakerAccountIds)
        {
            if (id != 0)
            {
                set[id] = 1;
            }
        }

        _ignoredSpeakersByListener[listenerAccountId] = set;
        _logger.LogInformation(
            "[VoiceMuteRegistry] Listener {ListenerAccountId} updated ignores: {Count} active ignored accounts.",
            listenerAccountId, set.Count);
    }

    public void ClearIgnores(uint listenerAccountId)
    {
        if (listenerAccountId == 0)
        {
            return;
        }

        _ignoredSpeakersByListener.TryRemove(listenerAccountId, out _);
        _logger.LogInformation(
            "[VoiceMuteRegistry] Cleared all ignores for Listener {ListenerAccountId}.",
            listenerAccountId);
    }
}
