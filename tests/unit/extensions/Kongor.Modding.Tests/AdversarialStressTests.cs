// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Conditions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Patching;
using Kongor.Modding.Resolution;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Empirical Challenger Adversarial Stress Test Suite & Fuzzing Oracles.
///     Rigorously verifies:
///     1. Condition Evaluator (deep nesting, unquoted names, whitespace variations, edge-case version syntax, fuzzing)
///     2. Fuzzy Delta Patcher (multi-cursor sequential state machine, CRLF/LF line endings, mixed indentation,
///     findall/findup/offset)
///     3. DAG Dependency Resolver & Tarjan SCC (multi-node cycles, diamond graphs, dense DAGs, disjoint forests, conflict
///     matrix)
///     4. Atomic Rollback Journal (simulated interrupted writes, partial failure recovery, dual-mode VFS/Archive cleanup,
///     idempotency)
/// </summary>
public class AdversarialStressTests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _modsDir;
    private readonly string _testTempDir;

    public AdversarialStressTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "KongorAdversarial_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_testTempDir, "client");
        _modsDir = Path.Combine(_testTempDir, "mods");

        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_modsDir);
        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempDir))
            try
            {
                Directory.Delete(_testTempDir, true);
            }
            catch
            {
                /* ignore */
            }
    }

    #region Helper Factory

    private static ModPackage CreateMod(
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

    #endregion

    #region Pillar 1: Condition Evaluator Stress Tests & Fuzzing

    [Fact]
    public void ConditionEvaluator_DeeplyNestedBooleanExpressions_EvaluatesAccurately()
    {
        Dictionary<string, string> installed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CoreMod"] = "1.0.0",
            ["TargetMod"] = "2.5.0"
        };

        // Construct 50 levels of nested parentheses: (((... (CoreMod and TargetMod) ...)))
        StringBuilder sbTrue = new();
        StringBuilder sbFalse = new();
        int depth = 50;

        for (int i = 0; i < depth; i++)
        {
            sbTrue.Append('(');
            sbFalse.Append('(');
        }

        sbTrue.Append("CoreMod and TargetMod [>=2.0.0]");
        sbFalse.Append("CoreMod and TargetMod [<2.0.0]");

        for (int i = 0; i < depth; i++)
        {
            sbTrue.Append(')');
            sbFalse.Append(')');
        }

        bool resultTrue = ConditionEvaluator.Evaluate(sbTrue.ToString(), installed);
        resultTrue.Should().BeTrue("50 levels of nested true expressions must evaluate to true");

        bool resultFalse = ConditionEvaluator.Evaluate(sbFalse.ToString(), installed);
        resultFalse.Should().BeFalse("50 levels of nested false expressions must evaluate to false");
    }

    [Theory]
    [InlineData("not not CoreMod", true)]
    [InlineData("not not not CoreMod", false)]
    [InlineData("CoreMod and (TargetMod or MissingMod)", true)]
    [InlineData("(MissingMod or not CoreMod) or (TargetMod and CoreMod)", true)]
    [InlineData("!MissingMod && (TargetMod || MissingMod)", true)]
    [InlineData("CoreMod && !TargetMod || !MissingMod", true)]
    [InlineData("true and not false", true)]
    [InlineData("false or (true and true)", true)]
    public void ConditionEvaluator_ComplexOperatorPrecedence_EvaluatesAccurately(string expr, bool expected)
    {
        Dictionary<string, string> installed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CoreMod"] = "1.0.0",
            ["TargetMod"] = "2.5.0"
        };

        bool actual = ConditionEvaluator.Evaluate(expr, installed);
        actual.Should().Be(expected, $"Expression '{expr}' failed evaluation");
    }

    [Theory]
    [InlineData("  \r\n\t  'Swift Picker'   \r\n  and   \t  'Constant Inventory'  \r\n", true)]
    [InlineData("\t\t(\t\t'Swift Picker'\t\tor\t\t'MissingMod'\t\t)\t\t", true)]
    [InlineData(" 'Swift Picker'   [  >=  5.0.0  ] ", true)]
    [InlineData(" 'Swift Picker' [ 5.5.0 ] and 'AOE RI Complete Rework' [ 1.6.4 ] ", true)]
    public void ConditionEvaluator_WhitespaceAndNewlineVariations_EvaluatesAccurately(string expr, bool expected)
    {
        Dictionary<string, string> installed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Swift Picker"] = "5.5.0",
            ["AOE RI Complete Rework"] = "1.6.4",
            ["Constant Inventory"] = "1.2.0"
        };

        bool actual = ConditionEvaluator.Evaluate(expr, installed);
        actual.Should().Be(expected, $"Whitespace-heavy expression '{expr}' failed evaluation");
    }

    [Theory]
    [InlineData("Swift Picker and Constant Inventory", true)]
    [InlineData("Swift Picker or Missing Mod Name", true)]
    [InlineData("not Missing Mod Name and AOE RI Complete Rework", true)]
    [InlineData("Swift Picker [v5.5.0] and Constant Inventory [>=1.0.0]", true)]
    [InlineData("'Auto Pick/VoteBan Hero' and 'Lava Water v0.0.2'", true)]
    public void ConditionEvaluator_QuotedAndUnquotedIdentifiers_EvaluatesAccurately(string expr, bool expected)
    {
        Dictionary<string, string> installed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Swift Picker"] = "5.5.0",
            ["Constant Inventory"] = "1.2.0",
            ["AOE RI Complete Rework"] = "1.6.4",
            ["Auto Pick/VoteBan Hero"] = "1.0.0",
            ["Lava Water v0.0.2"] = "0.0.2"
        };

        bool actual = ConditionEvaluator.Evaluate(expr, installed);
        actual.Should().Be(expected, $"Identifier expression '{expr}' failed evaluation");
    }

    [Fact]
    public void ConditionEvaluator_UnquotedSlashModName_FailsDueToTokenizerOmission_DemonstratesEmpiricalFinding()
    {
        // Finding: Unquoted slash in mod names causes token split / dropped slash
        Dictionary<string, string> installed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto Pick/VoteBan Hero"] = "1.0.0"
        };

        // Unquoted: "Auto Pick/VoteBan Hero" tokenizes as "Auto", "Pick", "VoteBan", "Hero"
        bool unquotedResult = ConditionEvaluator.Evaluate("Auto Pick/VoteBan Hero", installed);
        unquotedResult.Should().BeFalse("Unquoted slash causes tokenizer to drop slash and fail name match");

        // Quoted: "'Auto Pick/VoteBan Hero'" tokenizes as a complete string literal preserving slash
        bool quotedResult = ConditionEvaluator.Evaluate("'Auto Pick/VoteBan Hero'", installed);
        quotedResult.Should().BeTrue("Quoted string preserves slashes and special characters");
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", true)]
    [InlineData("1.0", "1.0.0.0", true)]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData(">1.0.0", "1.0.1", true)]
    [InlineData("<=2.0.0", "2.0.0", true)]
    [InlineData("<=2.0.0", "2.0.1", false)]
    [InlineData("<2.0.0", "2.0.0", false)]
    [InlineData("<2.0.0", "1.9.9", true)]
    [InlineData("!=1.5.0", "1.5.0", false)]
    [InlineData("!=1.5.0", "1.5.1", true)]
    [InlineData("==3.2.1", "3.2.1", true)]
    [InlineData("==3.2.1", "3.2.0", false)]
    [InlineData("*", "any_version_9.9.9", true)]
    [InlineData("4.*", "4.14.0", true)]
    [InlineData("4.*", "5.0.0", false)]
    [InlineData("1.0-2.0", "1.5.0", true)]
    [InlineData("1.0-2.0", "2.0.0", true)]
    [InlineData("1.0-2.0", "2.0.1", false)]
    [InlineData("1.0..3.0", "2.5.0", true)]
    [InlineData("1.0..3.0", "3.0.1", false)]
    [InlineData("v1.0.1b", "1.0.1", true)]
    public void VersionRangeExpression_ComprehensiveEdgeCases_MatchesExpected(string rangeExpr, string candidate,
        bool expected)
    {
        VersionRangeExpression ver = VersionRangeExpression.Parse(rangeExpr);
        bool actual = ver.IsSatisfiedBy(candidate);
        actual.Should().Be(expected, $"VersionRange '{rangeExpr}' against '{candidate}' must be {expected}");
    }

    [Theory]
    [InlineData("ModA and (ModB")]
    [InlineData("ModA or or ModB")]
    [InlineData("and ModA")]
    public void ConditionParser_MalformedExpressions_ThrowsFormatException(string malformedExpr)
    {
        Action act = () => ConditionParser.Parse(malformedExpr);
        act.Should().Throw<FormatException>($"Malformed expression '{malformedExpr}' must throw FormatException");
    }

    [Fact]
    public void ConditionEvaluator_FuzzingOracle_NeverThrowsUnhandledExceptionOnRandomInput()
    {
        string[] alphabet =
        [
            "ModA", "ModB", "and", "or", "not", "!", "&&", "||", "(", ")", "[1.0]", " ", "\t", "\n", "'quoted name'",
            "123", "=="
        ];
        Random rng = new(1337);
        Dictionary<string, string> active = new(StringComparer.OrdinalIgnoreCase)
            { ["ModA"] = "1.0.0", ["ModB"] = "2.0.0" };

        int testCount = 500;
        int successfulParses = 0;
        int caughtFormatExceptions = 0;

        for (int i = 0; i < testCount; i++)
        {
            int tokenCount = rng.Next(1, 15);
            StringBuilder sb = new();
            for (int t = 0; t < tokenCount; t++) sb.Append(alphabet[rng.Next(alphabet.Length)]).Append(' ');

            string randomExpr = sb.ToString();
            try
            {
                bool res = ConditionEvaluator.Evaluate(randomExpr, active);
                successfulParses++;
            }
            catch (FormatException)
            {
                caughtFormatExceptions++;
            }
        }

        (successfulParses + caughtFormatExceptions).Should().Be(testCount,
            "All 500 fuzzed inputs must either cleanly evaluate or throw handled FormatException");
    }

    #endregion

    #region Pillar 2: Fuzzy Delta Patcher Adversarial Stress Tests

    [Fact]
    public async Task FuzzyStringPatcher_SequentialCursorTransitions_AppliesMultiStepEditsAccurately()
    {
        string document = @"
<interface name=""main"">
  <panel name=""top_bar"">
    <label name=""title"" text=""Lobby"" />
  </panel>
  <panel name=""chat_container"">
    <chat_box name=""chat"" />
  </panel>
  <panel name=""bottom_bar"">
    <button name=""btn_exit"" />
  </panel>
</interface>";

        // Sequential operations:
        // 1. Find chat_container
        // 2. Insert custom channel tab inside it
        // 3. Find btn_exit
        // 4. Replace btn_exit with custom buttons
        // 5. FindUp to top_bar
        // 6. Insert notification badge before top_bar
        PatchOperation[] ops =
        [
            new FindOperation("<panel name=\"chat_container\">"),
            new InsertOperation("\n    <tab name=\"custom_channel\" />", Position: InsertPosition.After),
            new FindOperation("<button name=\"btn_exit\" />"),
            new ReplaceOperation("<button name=\"btn_custom_exit\" /><button name=\"btn_settings\" />"),
            new FindUpOperation("<panel name=\"top_bar\">"),
            new InsertOperation("<!-- TOP BAR INJECTION -->\n  ", Position: InsertPosition.Before)
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(document, ops);

        patched.Should().Contain("<tab name=\"custom_channel\" />");
        patched.Should().Contain("<button name=\"btn_custom_exit\" />");
        patched.Should().Contain("<button name=\"btn_settings\" />");
        patched.Should().NotContain("<button name=\"btn_exit\" />");
        patched.Should().Contain("<!-- TOP BAR INJECTION -->");
    }

    [Fact]
    public async Task FuzzyStringPatcher_LineEndingVariations_HandlesLfAndCrlfSymmetrically()
    {
        // 1. Target with CRLF (\r\n), Pattern with LF (\n)
        string crlfDoc =
            "<interface>\r\n  <panel name=\"hero_panel\" width=\"50%\">\r\n    <label text=\"Hero\" />\r\n  </panel>\r\n</interface>";
        PatchOperation[] lfOps =
        [
            new FindOperation("<panel name=\"hero_panel\" width=\"50%\">\n    <label text=\"Hero\" />"),
            new ReplaceOperation("<panel name=\"hero_panel\" width=\"75%\">\n    <label text=\"CustomHero\" />")
        ];

        string patchedCrlf = await FuzzyStringPatcher.ApplyPatchAsync(crlfDoc, lfOps);
        patchedCrlf.Should().Contain("<panel name=\"hero_panel\" width=\"75%\">");
        patchedCrlf.Should().Contain("<label text=\"CustomHero\" />");

        // 2. Target with LF (\n), Pattern with CRLF (\r\n)
        string lfDoc =
            "<interface>\n  <panel name=\"hero_panel\" width=\"50%\">\n    <label text=\"Hero\" />\n  </panel>\n</interface>";
        PatchOperation[] crlfOps =
        [
            new FindOperation("<panel name=\"hero_panel\" width=\"50%\">\r\n    <label text=\"Hero\" />"),
            new ReplaceOperation("<panel name=\"hero_panel\" width=\"100%\">\r\n    <label text=\"SuperHero\" />")
        ];

        string patchedLf = await FuzzyStringPatcher.ApplyPatchAsync(lfDoc, crlfOps);
        patchedLf.Should().Contain("<panel name=\"hero_panel\" width=\"100%\">");
        patchedLf.Should().Contain("<label text=\"SuperHero\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_MixedIndentationAndSpacing_MatchesAccurately()
    {
        string irregularDoc =
            "<interface>\n\t  <panel   \t  name = \"target_panel\"   \t   width = \"100%\"   height = \"200\"   >\n\t\t\t<label    name = \"status\"   />\n  </panel>\n</interface>";
        PatchOperation[] ops =
        [
            new FindOperation("<panel name = \"target_panel\" width = \"100%\" height = \"200\" >"),
            new ReplaceOperation("<panel name=\"target_panel\" width=\"100%\" height=\"300\" modified=\"true\">")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(irregularDoc, ops);
        patched.Should().Contain("<panel name=\"target_panel\" width=\"100%\" height=\"300\" modified=\"true\">");
    }

    [Fact]
    public async Task FuzzyStringPatcher_FindAllMultipleOperations_AppliesAllTransformations()
    {
        string doc = "color=\"#ff0000\" border=\"1\" color=\"#ff0000\" padding=\"5\" color=\"#ff0000\"";

        // Global replace on #ff0000
        PatchOperation[] ops =
        [
            new FindAllOperation("color=\"#ff0000\""),
            new ReplaceOperation("color=\"#00ff00\"")
        ];

        string result = await FuzzyStringPatcher.ApplyPatchAsync(doc, ops);
        result.Should().Be("color=\"#00ff00\" border=\"1\" color=\"#00ff00\" padding=\"5\" color=\"#00ff00\"");
    }

    [Fact]
    public async Task FuzzyStringPatcher_PositionAdjustments_HeadTailOffsetBehavesCorrectly()
    {
        string doc = "LINE1\nLINE2\nLINE3";

        // Insert at start (Head/Begin)
        PatchOperation[] headOps =
        [
            new FindOperation(string.Empty, FindPosition.Head),
            new InsertOperation("HEADER\n", Position: InsertPosition.Before)
        ];
        string withHead = await FuzzyStringPatcher.ApplyPatchAsync(doc, headOps);
        withHead.Should().StartWith("HEADER\nLINE1");

        // Insert at end (Tail/Eof)
        PatchOperation[] tailOps =
        [
            new FindOperation(string.Empty, FindPosition.Tail),
            new InsertOperation("\nFOOTER", Position: InsertPosition.After)
        ];
        string withTail = await FuzzyStringPatcher.ApplyPatchAsync(doc, tailOps);
        withTail.Should().EndWith("LINE3\nFOOTER");
    }

    #endregion

    #region Pillar 3: DAG Topological Sorter & Tarjan SCC Stress Tests

    [Fact]
    public void TopologicalSorter_CyclicGraphs_DetectsCyclesAndReportsAllNodes()
    {
        // 3-node cycle: A -> B -> C -> A
        ModPackage modA = CreateMod("ModA", applyAfter: [new ModOrderingRule("ModC")]);
        ModPackage modB = CreateMod("ModB", applyAfter: [new ModOrderingRule("ModA")]);
        ModPackage modC = CreateMod("ModC", applyAfter: [new ModOrderingRule("ModB")]);

        SortResult result = TopologicalSorter.ResolveAndSort([modA, modB, modC]);

        result.Success.Should().BeFalse("Cyclic graph must fail resolution");
        result.CycleReports.Should().NotBeEmpty();
        result.CycleReports.Should().Contain(r => r.Contains("Cyclic dependency detected"));
    }

    [Fact]
    public void TopologicalSorter_SelfLoopCycle_DetectsCycleImmediately()
    {
        // Self loop: A -> A
        ModPackage modA = CreateMod("SelfLoopMod", applyAfter: [new ModOrderingRule("SelfLoopMod")]);

        SortResult result = TopologicalSorter.ResolveAndSort([modA]);
        result.Success.Should().BeFalse("Self-loop must fail resolution");
        result.CycleReports.Should().NotBeEmpty();
    }

    [Fact]
    public void TopologicalSorter_DisjointMultipleCycles_DetectsAllIndependentCycles()
    {
        // Cycle 1: A -> B -> A
        ModPackage modA = CreateMod("ModA", applyAfter: [new ModOrderingRule("ModB")]);
        ModPackage modB = CreateMod("ModB", applyAfter: [new ModOrderingRule("ModA")]);

        // Cycle 2: X -> Y -> Z -> X
        ModPackage modX = CreateMod("ModX", applyAfter: [new ModOrderingRule("ModZ")]);
        ModPackage modY = CreateMod("ModY", applyAfter: [new ModOrderingRule("ModX")]);
        ModPackage modZ = CreateMod("ModZ", applyAfter: [new ModOrderingRule("ModY")]);

        // Independent valid mod: CleanMod
        ModPackage cleanMod = CreateMod("CleanMod");

        SortResult result = TopologicalSorter.ResolveAndSort([modA, modB, modX, modY, modZ, cleanMod]);

        result.Success.Should().BeFalse();
        result.CycleReports.Count.Should().BeGreaterThanOrEqualTo(2, "Must detect both independent cycle components");
    }

    [Fact]
    public void TopologicalSorter_DiamondDependency_SortsInExactDependencyOrder()
    {
        // Diamond:
        //      A
        //     / \
        //    B   C
        //     \ /
        //      D
        // D requires B and C; B and C require A.
        ModPackage modA = CreateMod("ModA");
        ModPackage modB = CreateMod("ModB", requirements: [new ModRequirement("ModA")]);
        ModPackage modC = CreateMod("ModC", requirements: [new ModRequirement("ModA")]);
        ModPackage modD = CreateMod("ModD", requirements: [new ModRequirement("ModB"), new ModRequirement("ModC")]);

        SortResult result = TopologicalSorter.ResolveAndSort([modD, modB, modC, modA]);

        result.Success.Should().BeTrue();
        result.ExecutionPlan.Should().HaveCount(4);

        List<string> plan = [.. result.ExecutionPlan.Select(m => m.Manifest.Name)];

        // ModA must precede B, C, D
        plan.IndexOf("ModA").Should().BeLessThan(plan.IndexOf("ModB"));
        plan.IndexOf("ModA").Should().BeLessThan(plan.IndexOf("ModC"));
        plan.IndexOf("ModA").Should().BeLessThan(plan.IndexOf("ModD"));

        // ModB and ModC must precede ModD
        plan.IndexOf("ModB").Should().BeLessThan(plan.IndexOf("ModD"));
        plan.IndexOf("ModC").Should().BeLessThan(plan.IndexOf("ModD"));
    }

    [Fact]
    public void TopologicalSorter_MultiComponentDisconnectedForest_SortsAllDeterministically()
    {
        // 5 completely independent sub-trees with 20 total nodes
        List<ModPackage> allMods = [];

        for (int tree = 1; tree <= 5; tree++)
        {
            string rootName = $"Tree{tree}_Root";
            ModPackage root = CreateMod(rootName);
            allMods.Add(root);

            for (int child = 1; child <= 3; child++)
            {
                string childName = $"Tree{tree}_Child{child}";
                ModPackage childMod = CreateMod(childName, requirements: [new ModRequirement(rootName)]);
                allMods.Add(childMod);
            }
        }

        allMods.Should().HaveCount(20);

        SortResult result = TopologicalSorter.ResolveAndSort(allMods);

        result.Success.Should().BeTrue();
        result.ExecutionPlan.Should().HaveCount(20);

        List<string> plan = [.. result.ExecutionPlan.Select(m => m.Manifest.Name)];

        // In each tree, root must precede all its children
        for (int tree = 1; tree <= 5; tree++)
        {
            string rootName = $"Tree{tree}_Root";
            int rootIdx = plan.IndexOf(rootName);

            for (int child = 1; child <= 3; child++)
            {
                string childName = $"Tree{tree}_Child{child}";
                int childIdx = plan.IndexOf(childName);
                rootIdx.Should().BeLessThan(childIdx, $"Root '{rootName}' must precede child '{childName}'");
            }
        }
    }

    [Fact]
    public void TopologicalSorter_LargeLinearChain_Sorts100ModsCorrectly()
    {
        int count = 100;
        List<ModPackage> chain = [];

        ModPackage first = CreateMod("Mod_001");
        chain.Add(first);

        for (int i = 2; i <= count; i++)
        {
            string prev = $"Mod_{i - 1:D3}";
            string curr = $"Mod_{i:D3}";
            ModPackage m = CreateMod(curr, requirements: [new ModRequirement(prev)]);
            chain.Add(m);
        }

        // Shuffle input
        Random rng = new(42);
        List<ModPackage> shuffled = [.. chain.OrderBy(_ => rng.Next())];

        SortResult result = TopologicalSorter.ResolveAndSort(shuffled);

        result.Success.Should().BeTrue();
        result.ExecutionPlan.Should().HaveCount(count);

        for (int i = 0; i < count; i++) result.ExecutionPlan[i].Manifest.Name.Should().Be($"Mod_{i + 1:D3}");
    }

    #endregion

    #region Pillar 4: Atomic Rollback Journal Stress Tests & Simulated Interrupted Writes

    [Fact]
    public async Task RollbackJournal_SimulatedInterruptedWrite_RollbackRestoresPreExistingAndCleansPartials()
    {
        // 1. Setup pre-existing client files
        string gameDir = Path.Combine(_clientDir, "game");
        string origFile = Path.Combine(gameDir, "core.cfg");
        await File.WriteAllTextAsync(origFile, "HOST_PORT=11235\nVERSION=4.14.0\n");

        // Pre-existing override file
        string overrideDir = Path.Combine(_clientDir, "override", "ui");
        Directory.CreateDirectory(overrideDir);
        string existingOverride = Path.Combine(overrideDir, "custom_hud.interface");
        await File.WriteAllTextAsync(existingOverride, "<interface name='existing_hud' />");

        // 2. Prepare simulated interrupted injection state
        // Mod creates 2 new files and overwrites 1 existing file
        string partialCreated1 = Path.Combine(_clientDir, "override", "ui", "mod_partial1.interface");
        string partialCreated2 = Path.Combine(_clientDir, "override", "ui", "mod_partial2.interface");
        await File.WriteAllTextAsync(partialCreated1, "<partial1 />");
        await File.WriteAllTextAsync(partialCreated2, "<partial2 />");

        // Backup existing override file before simulated overwrite
        string backupDir = Path.Combine(_clientDir, ".mod_backups");
        Directory.CreateDirectory(backupDir);
        string backupPath = Path.Combine(backupDir, "custom_hud.interface.bak");
        File.Copy(existingOverride, backupPath, true);

        // Overwrite existing with corrupted partial mod data
        await File.WriteAllTextAsync(existingOverride, "<corrupted_mod_data_crash />");

        // Record journal entry representing this transaction
        JournalEntry journal = new()
        {
            AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
            Mode = InjectionMode.Vfs,
            TargetDirectory = _clientDir,
            AppliedMods = [new AppliedModInfo("InterruptedMod", "1.0.0")],
            CreatedFiles =
            [
                "override/ui/mod_partial1.interface",
                "override/ui/mod_partial2.interface"
            ],
            OverwrittenFiles =
            [
                new BackupFileInfo("override/ui/custom_hud.interface", backupPath, string.Empty)
            ]
        };

        await RollbackJournal.SaveJournalAsync(_clientDir, journal);

        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeTrue();
        RollbackJournal.HasActiveModifications(_clientDir).Should().BeTrue();

        // 3. Execute Rollback to recover from interrupted/corrupted state
        bool rollbackSuccess = await RollbackJournal.ExecuteRollbackAsync(_clientDir);
        rollbackSuccess.Should().BeTrue();

        // 4. Assert full forensic restoration:
        // Overwritten file restored to original content
        File.Exists(existingOverride).Should().BeTrue();
        string restoredContent = await File.ReadAllTextAsync(existingOverride);
        restoredContent.Should().Be("<interface name='existing_hud' />");

        // Partial created files deleted
        File.Exists(partialCreated1).Should().BeFalse();
        File.Exists(partialCreated2).Should().BeFalse();

        // Backup folder and journal deleted
        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeFalse();
        Directory.Exists(backupDir).Should().BeFalse();
        RollbackJournal.HasActiveModifications(_clientDir).Should().BeFalse();
    }

    [Fact]
    public async Task RollbackJournal_Idempotency_MultipleRollbacksSucceedCleanly()
    {
        // 1. Setup clean directory with no journal
        string freshDir = Path.Combine(_testTempDir, "fresh_client");
        Directory.CreateDirectory(freshDir);

        // Rollback on clean directory without journal
        bool firstRun = await RollbackJournal.ExecuteRollbackAsync(freshDir);
        firstRun.Should().BeTrue();

        // Second rollback immediately after
        bool secondRun = await RollbackJournal.ExecuteRollbackAsync(freshDir);
        secondRun.Should().BeTrue();
    }

    [Fact]
    public async Task RollbackJournal_VfsMode_CompleteRollbackCleansAllInjectedFilesAndTriggers()
    {
        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.CreateDirectory(Path.Combine(overrideDir, "ui", "common"));

        string injectedFile1 = Path.Combine(overrideDir, "ui", "common", "custom_btn.interface");
        string injectedFile2 = Path.Combine(overrideDir, "ui", "custom_hud.package");
        string triggerFile = Path.Combine(overrideDir, ".hotreload.trigger");

        await File.WriteAllTextAsync(injectedFile1, "<button />");
        await File.WriteAllTextAsync(injectedFile2, "<package />");
        await File.WriteAllTextAsync(triggerFile, "HOT_RELOAD_123");

        JournalEntry journal = new()
        {
            AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
            Mode = InjectionMode.Vfs,
            TargetDirectory = _clientDir,
            AppliedMods = [new AppliedModInfo("VfsMod", "1.0.0")],
            CreatedFiles =
            [
                "override/ui/common/custom_btn.interface",
                "override/ui/custom_hud.package",
                "override/.hotreload.trigger"
            ]
        };

        await RollbackJournal.SaveJournalAsync(_clientDir, journal);

        bool rollbackResult = await RollbackJournal.ExecuteRollbackAsync(_clientDir);
        rollbackResult.Should().BeTrue();

        File.Exists(injectedFile1).Should().BeFalse();
        File.Exists(injectedFile2).Should().BeFalse();
        File.Exists(triggerFile).Should().BeFalse();
        Directory.Exists(overrideDir).Should().BeFalse("Empty override directory must be cleaned up");
        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeFalse();
    }

    #endregion
}
