// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies the decaying weighted attack indicator: that an attack burst raises the indicator immediately,
///     that the indicator clears once the burst stops, and that relative weights match the reference order.
/// </summary>
public sealed class AttackIndicatorContainerTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void An_Attack_Burst_Raises_The_Under_Attack_Indicator_Immediately_Within_The_Maintenance_Pass()
    {
        ControllableTimeProvider clock = new();
        AttackIndicatorContainer container = new(clock);

        container.IsUnderAttack.Should().BeFalse();

        // 11 Validation Drops At Weight 100 Push Score To 1100, Crossing UnderAttackThreshold (1000) Immediately Without Waiting 5 Minutes
        for (int index = 0; index < 11; index++)
            container.Charge(AttackIndicatorContainer.ValidationDropAttackWeight);

        container.IsUnderAttack.Should().BeTrue();
    }

    [Fact]
    public void The_Under_Attack_Indicator_Clears_Once_The_Burst_Stops_And_Time_Elapses()
    {
        ControllableTimeProvider clock = new();
        AttackIndicatorContainer container = new(clock);

        for (int index = 0; index < 11; index++)
            container.Charge(AttackIndicatorContainer.ValidationDropAttackWeight);

        container.IsUnderAttack.Should().BeTrue();

        // 6 Seconds Of Drain At 200 Score/Second Drains 1200 Score, Returning Below 1000
        for (int second = 0; second < 6; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        container.IsUnderAttack.Should().BeFalse();
    }

    [Fact]
    public void Validation_Drop_Weight_Is_Greater_Than_Cap_Refusal_Weight()
    {
        int validationWeight = AttackIndicatorContainer.ValidationDropAttackWeight;
        int capRefusalWeight = AttackIndicatorContainer.CapRefusalAttackWeight;
        int novelEndpointWeight = AttackIndicatorContainer.NovelEndpointAttackWeight;

        validationWeight.Should().BeGreaterThan(capRefusalWeight);
        capRefusalWeight.Should().BeGreaterThan(novelEndpointWeight);
    }
}
