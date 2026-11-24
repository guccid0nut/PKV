// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies that the advertised and enforced packet quotas are derived from one rate, so they cannot diverge.
/// </summary>
public sealed class ChallengeQuotaTests
{
    [Fact]
    public void The_Quota_Is_The_Rate_Multiplied_By_The_Renewal_Interval()
    {
        ChallengeQuota.Derive(144, TimeSpan.FromSeconds(5)).Should().Be((ushort)720);
        ChallengeQuota.Derive(144, TimeSpan.FromSeconds(10)).Should().Be((ushort)1440);
    }

    // The Reference Grants 720 Packets Over A Five-Second Refresh, So A Five-Second Interval Must Reproduce The Reference Exactly
    [Fact]
    public void A_Five_Second_Interval_Reproduces_The_Reference_Quotas()
    {
        TimeSpan referenceRefresh = TimeSpan.FromSeconds(5);

        ChallengeQuota.ForKind(ProxyForwarderKind.Game, referenceRefresh).Should().Be((ushort)720);
        ChallengeQuota.ForKind(ProxyForwarderKind.Voice, referenceRefresh).Should().Be((ushort)50);
        ChallengeQuota.GameCommandForInterval(referenceRefresh).Should().Be((ushort)40);
    }

    [Fact]
    public void Voice_Is_Quoted_Far_Lower_Than_Game()
    {
        TimeSpan interval = TimeSpan.FromSeconds(10);

        ChallengeQuota.ForKind(ProxyForwarderKind.Voice, interval)
            .Should().BeLessThan(ChallengeQuota.ForKind(ProxyForwarderKind.Game, interval));
    }

    // A Long Interval Must Not Wrap The Unsigned Sixteen-Bit Field The Quota Is Advertised In
    [Fact]
    public void An_Interval_Large_Enough_To_Overflow_Is_Clamped()
    {
        ChallengeQuota.Derive(144, TimeSpan.FromHours(1)).Should().Be(ushort.MaxValue);
    }

    [Fact]
    public void No_Rate_Is_Zero_Or_Negative()
    {
        ChallengeQuota.GamePacketsPerSecond.Should().BeGreaterThan(0);
        ChallengeQuota.GameCommandPacketsPerSecond.Should().BeGreaterThan(0);
        ChallengeQuota.VoicePacketsPerSecond.Should().BeGreaterThan(0);
    }

    // A Compliant Client Self-Limits To Fifteen Sixteenths Of The Advertised Quota, So That Rate Must Stay Below The Drain Or Every Player Accumulates Score While Behaving Perfectly
    [Fact]
    public void A_Client_Honouring_Its_Own_Soft_Limit_Stays_Below_The_Drain()
    {
        int softLimitedRate = ChallengeQuota.GamePacketsPerSecond - (ChallengeQuota.GamePacketsPerSecond / 16);

        softLimitedRate.Should().BeLessThan(ViolationScoreContainer.EstimatedPacketsPerSecond);
    }
}
