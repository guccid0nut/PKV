// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Resolution;
using Xunit;

namespace Kongor.Modding.Tests;

public class ResolutionTests
{
    private static ModPackage CreateTestMod(
        string name,
        string version = "1.0.0",
        IEnumerable<ModRequirement>? requirements = null,
        IEnumerable<ModIncompatibility>? incompatibilities = null,
        IEnumerable<ModOrderingRule>? applyBefore = null,
        IEnumerable<ModOrderingRule>? applyAfter = null,
        IEnumerable<CopyFileOperation>? copies = null,
        IEnumerable<EditFileOperation>? edits = null)
    {
        return new ModPackage
        {
            FilePath = name + ".honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = name,
                Version = version,
                Requirements = requirements?.ToList() ?? [],
                Incompatibilities = incompatibilities?.ToList() ?? [],
                ApplyBefore = applyBefore?.ToList() ?? [],
                ApplyAfter = applyAfter?.ToList() ?? [],
                FileCopies = copies?.ToList() ?? [],
                FileEdits = edits?.ToList() ?? []
            }
        };
    }

    [Fact]
    public void TopologicalSorter_ValidLinearDependencies_SortsInExactOrder()
    {
        ModPackage modC = CreateTestMod("ModC");
        ModPackage modB = CreateTestMod("ModB", requirements: [new ModRequirement("ModC")]);
        ModPackage modA = CreateTestMod("ModA", requirements: [new ModRequirement("ModB")]);

        SortResult result = TopologicalSorter.ResolveAndSort([modA, modB, modC]);

        result.Success.Should().BeTrue();
        result.ExecutionPlan.Select(m => m.Manifest.Name)
            .Should().ContainInOrder("ModC", "ModB", "ModA");
    }

    [Fact]
    public void TopologicalSorter_MissingRequirement_ReturnsValidationFailure()
    {
        ModPackage modA = CreateTestMod("ModA", requirements: [new ModRequirement("MissingCore")]);

        SortResult result = TopologicalSorter.ResolveAndSort([modA]);

        result.Success.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Contains("requires missing mod 'MissingCore'"));
    }

    [Fact]
    public void TopologicalSorter_IncompatibleMod_ReturnsValidationFailure()
    {
        ModPackage modA = CreateTestMod("ModA", incompatibilities: [new ModIncompatibility("ModB")]);
        ModPackage modB = CreateTestMod("ModB");

        SortResult result = TopologicalSorter.ResolveAndSort([modA, modB]);

        result.Success.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Contains("is incompatible with enabled mod 'ModB'"));
    }

    [Fact]
    public void TopologicalSorter_CyclicDependency_DetectsCycleViaTarjan()
    {
        ModPackage modA = CreateTestMod("ModA", applyAfter: [new ModOrderingRule("ModC")]);
        ModPackage modB = CreateTestMod("ModB", applyAfter: [new ModOrderingRule("ModA")]);
        ModPackage modC = CreateTestMod("ModC", applyAfter: [new ModOrderingRule("ModB")]);

        SortResult result = TopologicalSorter.ResolveAndSort([modA, modB, modC]);

        result.Success.Should().BeFalse();
        result.CycleReports.Should().NotBeEmpty();
        result.CycleReports[0].Should().Contain("Cyclic dependency detected");
    }

    [Fact]
    public void ConflictMatrix_WhenInvoked_DetectsFileCollisions()
    {
        ModPackage mod1 = CreateTestMod("Mod1", copies:
        [
            new CopyFileOperation { TargetName = "ui/main.interface", SourcePath = "main.interface" }
        ]);

        ModPackage mod2 = CreateTestMod("Mod2", copies:
        [
            new CopyFileOperation { TargetName = "ui/main.interface", SourcePath = "main.interface" }
        ]);

        ModPackage mod3 = CreateTestMod("Mod3", edits:
        [
            new EditFileOperation { TargetName = "ui/main.interface" }
        ]);

        ConflictReport report = ConflictMatrix.Analyze([mod1, mod2, mod3]);

        report.HasConflicts.Should().BeTrue();
        report.Collisions.Should()
            .Contain(c => c.TargetFile == "ui/main.interface" && c.Severity == CollisionSeverity.Conflict);
    }
}
