// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies one challenge's packet allowance: that a counter within the quota is admitted once,
///     that the quota is enforced, and that a repeated counter is rejected as a duplicate.
/// </summary>
public sealed class ChallengeWindowTests
{
    [Fact]
    public void A_Counter_Within_The_Quota_Is_Admitted()
    {
        ChallengeWindow window = new(challenge: 42, quota: 10);

        bool admitted = window.TryAdmit(0, out ChallengeAdmission admission);

        admitted.Should().BeTrue();
        admission.Should().Be(ChallengeAdmission.Admitted);
    }

    [Fact]
    public void A_Counter_At_Or_Above_The_Quota_Is_Over_Quota()
    {
        ChallengeWindow window = new(challenge: 42, quota: 10);

        window.TryAdmit(10, out ChallengeAdmission atQuota).Should().BeFalse();
        atQuota.Should().Be(ChallengeAdmission.OverQuota);

        window.TryAdmit(9999, out ChallengeAdmission farAbove).Should().BeFalse();
        farAbove.Should().Be(ChallengeAdmission.OverQuota);
    }

    // A Counter Beyond The Quota Must Be Refused Before It Is Used To Index The Duplicate Store, Which Is Sized To The Quota
    [Fact]
    public void The_Maximum_Possible_Counter_Does_Not_Index_Out_Of_Range()
    {
        ChallengeWindow window = new(challenge: 42, quota: 10);

        bool admitted = window.TryAdmit(ushort.MaxValue, out ChallengeAdmission admission);

        admitted.Should().BeFalse();
        admission.Should().Be(ChallengeAdmission.OverQuota);
    }

    [Fact]
    public void A_Repeated_Counter_Is_A_Duplicate()
    {
        ChallengeWindow window = new(challenge: 42, quota: 10);

        window.TryAdmit(3, out _);

        bool admitted = window.TryAdmit(3, out ChallengeAdmission admission);

        admitted.Should().BeFalse();
        admission.Should().Be(ChallengeAdmission.Duplicate);
    }

    [Fact]
    public void Every_Counter_Below_The_Quota_Is_Admitted_Exactly_Once()
    {
        const ushort quota = 64;

        ChallengeWindow window = new(challenge: 1, quota: quota);

        for (ushort counter = 0; counter < quota; counter++)
            window.TryAdmit(counter, out _).Should().BeTrue();

        for (ushort counter = 0; counter < quota; counter++)
            window.TryAdmit(counter, out _).Should().BeFalse();
    }

    // The Same Counter Under A New Challenge Is Legitimate, Because The Client Restarts Its Counter When It Accepts One
    [Fact]
    public void A_New_Window_Admits_A_Counter_The_Previous_One_Consumed()
    {
        ChallengeWindow first = new(challenge: 1, quota: 10);
        first.TryAdmit(5, out _);

        ChallengeWindow second = new(challenge: 2, quota: 10);

        second.TryAdmit(5, out _).Should().BeTrue();
    }

    // The Quota Sizes The Seen Set, So A Quota Of Zero Must Refuse Every Counter Rather Than Index An Empty Set
    [Fact]
    public void A_Window_With_No_Quota_Refuses_Every_Counter()
    {
        ChallengeWindow window = new(challenge: 42, quota: 0);

        window.TryAdmit(0, out ChallengeAdmission atZero).Should().BeFalse();
        atZero.Should().Be(ChallengeAdmission.OverQuota);

        window.TryAdmit(ushort.MaxValue, out ChallengeAdmission atMaximum).Should().BeFalse();
        atMaximum.Should().Be(ChallengeAdmission.OverQuota);
    }
}
