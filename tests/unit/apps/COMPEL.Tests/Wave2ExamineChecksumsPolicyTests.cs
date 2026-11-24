// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class Wave2ExamineChecksumsPolicyTests
{
    [Fact]
    public void ResolveExamineChecksumsPolicy_EnforcesOnRanked_AllowsModsOnQueueArcadeAndCustomServers()
    {
        // protects: Explicit server-side anti-cheat policy contract between COMPEL and native_guard (KONGOR_EXAMINE_ARCHIVE_CHECKSUMS).
        // fails_when: Ranked/TMM servers run without checksum enforcement or custom/modded/QueueArcade pools fail to allow mods.
        // seam: DedicatedWorkerArguments.ResolveExamineChecksumsPolicy + MatchServerManagerOptions defaults.
        MatchServerManagerOptions rankedDefault = new();
        rankedDefault.ExamineChecksumsPolicy.Should().Be(ExamineChecksumsPolicy.RankedOnly);

        (bool enforceRanked, string envRanked, _) = DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(rankedDefault);
        enforceRanked.Should().BeTrue();
        envRanked.Should().Be("1");

        MatchServerManagerOptions queueArcade = new() { QueueArcadeMode = true };
        (bool enforceArcade, string envArcade, _) = DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(queueArcade);
        enforceArcade.Should().BeFalse();
        envArcade.Should().Be("0");

        MatchServerManagerOptions customModded = new() { AllowCustomMods = true };
        (bool enforceCustom, string envCustom, _) = DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(customModded);
        enforceCustom.Should().BeFalse();
        envCustom.Should().Be("0");

        MatchServerManagerOptions alwaysEnforce = new()
        {
            ExamineChecksumsPolicy = ExamineChecksumsPolicy.Always,
            AllowCustomMods = true
        };
        (bool enforceAlways, string envAlways, _) = DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(alwaysEnforce);
        enforceAlways.Should().BeTrue();
        envAlways.Should().Be("1");

        MatchServerManagerOptions disabledPolicy = new()
        {
            ExamineChecksumsPolicy = ExamineChecksumsPolicy.Disabled
        };
        (bool enforceDisabled, string envDisabled, _) = DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(disabledPolicy);
        enforceDisabled.Should().BeFalse();
        envDisabled.Should().Be("0");
    }
}
