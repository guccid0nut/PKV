// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies the per-source violation score: that a source within the expected packet rate is never actioned,
///     that one above it or violating the checks is, that the score saturates and drains as the reference's does,
///     and that an actioned source recovers or stays actioned according to whether it keeps sending below or above that rate.
/// </summary>
public sealed class ViolationScoreContainerTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static IPEndPoint Source(int port = 40000) => new(IPAddress.Parse("203.0.113.5"), port);

    [Fact]
    public void A_Source_At_The_Expected_Packet_Rate_Is_Never_Actioned()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        bool everActioned = false;

        for (int second = 0; second < 60; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond; packet++)
            {
                container.ChargeArrival(Source());

                if (container.IsWithinAllowance(Source()) is false)
                    everActioned = true;
            }

            clock.Advance(OneSecond);
            container.Drain();
        }

        everActioned.Should().BeFalse();
    }

    [Fact]
    public void A_Source_Above_The_Expected_Packet_Rate_Is_Eventually_Actioned()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        for (int second = 0; second < 120; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond * 2; packet++)
                container.ChargeArrival(Source());

            clock.Advance(OneSecond);
            container.Drain();
        }

        container.IsWithinAllowance(Source()).Should().BeFalse();
    }

    [Fact]
    public void A_Source_Well_Above_The_Threshold_Is_Actioned()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        bool actioned = false;

        for (int attempt = 0; attempt < 4005; attempt++)
        {
            container.ChargeArrival(Source());

            if (container.IsWithinAllowance(Source()) is false)
            {
                actioned = true;
                break;
            }
        }

        actioned.Should().BeTrue();
    }

    [Fact]
    public void Violations_Without_High_Arrival_Rate_Do_Not_Action_A_Source()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        for (int attempt = 0; attempt < 100; attempt++)
            container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        container.Score(Source()).Should().BeGreaterThan(ViolationScoreContainer.ActionableThreshold);
        container.IsWithinAllowance(Source()).Should().BeTrue();
    }

    [Fact]
    public void A_Weight_That_Does_Not_Divide_The_Threshold_Still_Accumulates()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        for (int attempt = 0; attempt < 400; attempt++)
            container.ChargeViolation(Source(), ViolationScoreContainer.DuplicateViolationWeight);

        container.Score(Source()).Should().BeGreaterThan(ViolationScoreContainer.ActionableThreshold);
    }

    [Fact]
    public void An_Actioned_Source_Recovers_After_Draining()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        while (container.IsWithinAllowance(Source()))
            container.ChargeArrival(Source());

        for (int second = 0; second < 60; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        container.IsWithinAllowance(Source()).Should().BeTrue();
    }

    [Fact]
    public void An_Actioned_Source_Sending_Below_The_Expected_Rate_Recovers()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        while (container.IsWithinAllowance(Source()))
            container.ChargeArrival(Source());

        for (int second = 0; second < 60; second++)
        {
            for (int packet = 0; packet < 30; packet++)
                container.ChargeArrival(Source());

            clock.Advance(OneSecond);
            container.Drain();
        }

        container.IsWithinAllowance(Source()).Should().BeTrue();
    }

    [Fact]
    public void An_Actioned_Source_Sending_Above_The_Expected_Rate_Stays_Actioned()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        while (container.IsWithinAllowance(Source()))
            container.ChargeArrival(Source());

        for (int second = 0; second < 60; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond * 2; packet++)
                container.ChargeArrival(Source());

            clock.Advance(OneSecond);
            container.Drain();
        }

        container.IsWithinAllowance(Source()).Should().BeFalse();
    }

    [Fact]
    public void The_Score_Saturates_At_The_Maximum()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            container.ChargeArrival(Source());
            container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);
        }

        int maximum = ViolationScoreContainer.MaximumViolationScore;
        container.Score(Source()).Should().Be(maximum);
    }

    [Fact]
    public void A_Drain_Removes_Score_In_Proportion_To_Elapsed_Time()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromSeconds(1));
        container.Drain();

        int afterOneSecond = container.Score(Source());

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);
        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromSeconds(2));
        container.Drain();

        afterOneSecond.Should().Be(60);
        container.Score(Source()).Should().Be(180);
    }

    [Fact]
    public void A_Drain_Over_A_Fractional_Interval_Truncates_The_Remaining_Score()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromMilliseconds(1234));
        container.Drain();

        container.Score(Source()).Should().Be(27);
    }

    [Fact]
    public void A_Drain_Before_The_Minimum_Interval_Does_Nothing_And_Keeps_The_Remainder()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        container.Drain();

        int afterHalfASecond = container.Score(Source());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        container.Drain();

        afterHalfASecond.Should().Be(ViolationScoreContainer.TooShortViolationWeight);
        container.Score(Source()).Should().Be(60);
    }

    [Fact]
    public void A_Source_Drained_To_Zero_Is_Forgotten()
    {
        ControllableTimeProvider clock = new();
        ViolationScoreContainer container = new(clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.DuplicateViolationWeight);

        clock.Advance(OneSecond);
        container.Drain();

        container.TrackedSourceCount.Should().Be(0);
    }

    [Fact]
    public void An_Unknown_Source_Is_Within_Allowance_And_Is_Not_Tracked()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        container.IsWithinAllowance(Source()).Should().BeTrue();
        container.Score(Source()).Should().Be(0);
        container.TrackedSourceCount.Should().Be(0);
    }

    [Fact]
    public void Sources_Are_Scored_Independently()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        while (container.IsWithinAllowance(Source(40000)))
            container.ChargeArrival(Source(40000));

        container.IsWithinAllowance(Source(40001)).Should().BeTrue();
    }

    [Fact]
    public void Every_Weight_Is_Below_The_Actionable_Threshold()
    {
        int[] weights =
        [
            ViolationScoreContainer.TooShortViolationWeight,
            ViolationScoreContainer.UnauthenticatedViolationWeight,
            ViolationScoreContainer.RateLimitViolationWeight,
            ViolationScoreContainer.DuplicateViolationWeight,
            ViolationScoreContainer.ChallengeViolationWeight,
            ViolationScoreContainer.PacketScore
        ];

        foreach (int weight in weights)
        {
            weight.Should().BeGreaterThan(0);
            weight.Should().BeLessThan(ViolationScoreContainer.ActionableThreshold);
        }
    }

    [Fact]
    public void The_Maximum_Is_Above_The_Actionable_Threshold()
    {
        int maximum = ViolationScoreContainer.MaximumViolationScore;
        maximum.Should().BeGreaterThan(ViolationScoreContainer.ActionableThreshold);
    }

    [Fact]
    public void A_Single_Violation_Of_Any_Weight_Never_Actions_A_Source()
    {
        ViolationScoreContainer container = new(new ControllableTimeProvider());

        container.ChargeViolation(Source(41000), ViolationScoreContainer.TooShortViolationWeight);
        container.ChargeViolation(Source(41001), ViolationScoreContainer.UnauthenticatedViolationWeight);
        container.ChargeViolation(Source(41002), ViolationScoreContainer.RateLimitViolationWeight);
        container.ChargeViolation(Source(41003), ViolationScoreContainer.DuplicateViolationWeight);

        container.IsWithinAllowance(Source(41000)).Should().BeTrue();
        container.IsWithinAllowance(Source(41001)).Should().BeTrue();
        container.IsWithinAllowance(Source(41002)).Should().BeTrue();
        container.IsWithinAllowance(Source(41003)).Should().BeTrue();
    }
}
