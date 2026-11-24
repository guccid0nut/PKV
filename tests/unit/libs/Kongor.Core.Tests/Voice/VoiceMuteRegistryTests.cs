// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceMuteRegistryTests.cs
// System: ProjectKongor - Voice Subsystem Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class VoiceMuteRegistryTests
{
    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IsMuted_WhenNotMuted_ReturnsFalse()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);

        bool isMuted = registry.IsMuted(12345);

        isMuted.Should().BeFalse();
    }

    [Fact]
    public void IsMuted_WhenZeroAccountId_ReturnsFalse()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetMute(0, _timeProvider.GetUtcNow().AddDays(1), "Invalid");

        bool isMuted = registry.IsMuted(0);

        isMuted.Should().BeFalse();
    }

    [Fact]
    public void IsMuted_WhenActiveMute_ReturnsTrue()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        DateTimeOffset expiration = _timeProvider.GetUtcNow().AddHours(2);

        registry.SetMute(5001, expiration, "Griefing in match 9999");

        registry.IsMuted(5001).Should().BeTrue();
    }

    [Fact]
    public void IsMuted_WhenPermanentMute_ReturnsTrueIndefinitely()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);

        registry.SetMute(6001, expiration: null, "Severe hate speech violation");

        registry.IsMuted(6001).Should().BeTrue();

        _timeProvider.Advance(TimeSpan.FromDays(365 * 10));

        registry.IsMuted(6001).Should().BeTrue();
    }

    [Fact]
    public void IsMuted_WhenMuteExpired_ReturnsFalseAndPurgesEntry()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        DateTimeOffset expiration = _timeProvider.GetUtcNow().AddHours(1);

        registry.SetMute(7001, expiration, "Temporary cooldown");
        registry.IsMuted(7001).Should().BeTrue();

        // Advance beyond expiration
        _timeProvider.Advance(TimeSpan.FromHours(2));

        registry.IsMuted(7001).Should().BeFalse();
        registry.GetActiveMutes().Should().BeEmpty();
    }

    [Fact]
    public void RemoveMute_ClearsSanctionImmediately()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetMute(8001, _timeProvider.GetUtcNow().AddDays(7), "Toxicity");

        registry.IsMuted(8001).Should().BeTrue();

        registry.RemoveMute(8001);

        registry.IsMuted(8001).Should().BeFalse();
    }

    [Fact]
    public void GetActiveMutes_FiltersOutExpiredEntries()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetMute(1001, _timeProvider.GetUtcNow().AddHours(1), "Active 1");
        registry.SetMute(1002, _timeProvider.GetUtcNow().AddHours(3), "Active 2");
        registry.SetMute(1003, _timeProvider.GetUtcNow().AddMinutes(30), "Will expire");

        _timeProvider.Advance(TimeSpan.FromMinutes(45));

        IReadOnlyList<VoiceMuteEntry> active = registry.GetActiveMutes();

        active.Should().HaveCount(2);
        active.Should().Contain(m => m.AccountId == 1001);
        active.Should().Contain(m => m.AccountId == 1002);
        active.Should().NotContain(m => m.AccountId == 1003);
    }

    [Fact]
    public void Clear_RemovesAllRecords()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetMute(2001, _timeProvider.GetUtcNow().AddDays(1), "Mute 1");
        registry.SetMute(2002, _timeProvider.GetUtcNow().AddDays(2), "Mute 2");

        registry.Clear();

        registry.IsMuted(2001).Should().BeFalse();
        registry.IsMuted(2002).Should().BeFalse();
        registry.GetActiveMutes().Should().BeEmpty();
    }

    [Fact]
    public void ConcurrentAccess_MaintainsThreadSafety()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);

        Parallel.For(1, 200, i =>
        {
            uint accId = (uint)i;
            registry.SetMute(accId, _timeProvider.GetUtcNow().AddHours(1), $"Test mute {accId}");
            registry.IsMuted(accId).Should().BeTrue();

            if (accId % 2 == 0)
            {
                registry.RemoveMute(accId);
                registry.IsMuted(accId).Should().BeFalse();
            }
        });

        IReadOnlyList<VoiceMuteEntry> active = registry.GetActiveMutes();
        active.Should().HaveCount(100); // 100 odd IDs from 1 to 199
    }

    [Fact]
    public void IsParticipantIgnored_WhenNotIgnored_ReturnsFalse()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.IsParticipantIgnored(1001, 2002).Should().BeFalse();
        registry.IsParticipantIgnored(0, 2002).Should().BeFalse();
        registry.IsParticipantIgnored(1001, 0).Should().BeFalse();
    }

    [Fact]
    public void IsParticipantIgnored_WhenSetIgnoreTrue_ReturnsTrue()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetIgnore(1001, 2002, true);

        registry.IsParticipantIgnored(1001, 2002).Should().BeTrue();
        // Ignore is directional: 2002 has not ignored 1001
        registry.IsParticipantIgnored(2002, 1001).Should().BeFalse();
    }

    [Fact]
    public void IsParticipantIgnored_WhenSetIgnoreFalse_ReturnsFalse()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetIgnore(1001, 2002, true);
        registry.IsParticipantIgnored(1001, 2002).Should().BeTrue();

        registry.SetIgnore(1001, 2002, false);
        registry.IsParticipantIgnored(1001, 2002).Should().BeFalse();
    }

    [Fact]
    public void SetIgnores_BatchSetsIgnoredSpeakers()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetIgnores(1001, [2001, 2002, 2003]);

        registry.IsParticipantIgnored(1001, 2001).Should().BeTrue();
        registry.IsParticipantIgnored(1001, 2002).Should().BeTrue();
        registry.IsParticipantIgnored(1001, 2003).Should().BeTrue();
        registry.IsParticipantIgnored(1001, 2004).Should().BeFalse();

        // Updating replaces previous set
        registry.SetIgnores(1001, [2004]);
        registry.IsParticipantIgnored(1001, 2001).Should().BeFalse();
        registry.IsParticipantIgnored(1001, 2004).Should().BeTrue();
    }

    [Fact]
    public void ClearIgnores_ClearsAllIgnoresForListener()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetIgnores(1001, [2001, 2002]);
        registry.SetIgnores(1002, [2003]);

        registry.ClearIgnores(1001);

        registry.IsParticipantIgnored(1001, 2001).Should().BeFalse();
        registry.IsParticipantIgnored(1001, 2002).Should().BeFalse();
        registry.IsParticipantIgnored(1002, 2003).Should().BeTrue();
    }

    [Fact]
    public void Clear_ClearsAllIgnoresGlobally()
    {
        VoiceMuteRegistry registry = new VoiceMuteRegistry(_timeProvider);
        registry.SetIgnores(1001, [2001, 2002]);
        registry.SetIgnores(1002, [2003]);

        registry.Clear();

        registry.IsParticipantIgnored(1001, 2001).Should().BeFalse();
        registry.IsParticipantIgnored(1002, 2003).Should().BeFalse();
    }
}
