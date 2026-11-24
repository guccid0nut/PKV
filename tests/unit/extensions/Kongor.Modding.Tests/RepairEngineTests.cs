// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Patching;
using Kongor.Modding.Repair;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive unit and integration test suite for ProjectKongor Automated Compatibility & Repair Engine (Milestone
///     4).
///     Verifies:
///     - XmlSyntaxNormalizer: inline event handlers, entity escaping, legacy tag conversion, CDATA preservation, unquoted
///     attributes.
///     - UiAnchorRescaler: responsive 'h' percentage conversions, negative/fractional pixels, unit preservation, border
///     handling.
///     - TargetFileRelocator: path relocations (game.interface, game_lobby, player_stats.lua, alt_info.lua), manifest
///     updates, raw XML rewrites.
///     - LuaApiCompatibilityShim: shim header injection, safe hook error boundaries, symbol call mapping, standalone shim
///     asset.
///     - ModRepairEngine: full transformation pipeline, appversion constraint stripping, batch processing, and >= 70%
///     repair success rate.
/// </summary>
public class RepairEngineTests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _tempDir;

    public RepairEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorRepairTests_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_tempDir, "client");

        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_clientDir, "ui"));
        Directory.CreateDirectory(Path.Combine(_clientDir, "ui", "fe2", "newui"));

        // Setup mock modern client files
        File.WriteAllText(Path.Combine(_clientDir, "ui", "game_legacy.interface"), @"<interface name=""game_legacy"">
  <panel name=""hud_root"" width=""100%"" height=""100%"">
    <panel name=""player_portrait"" width=""10h"" height=""10h"" />
    <panel name=""inventory_panel"" x=""20h"" y=""90h"" />
    <panel name=""gold_display"" x=""50%"" y=""5h"" />
  </panel>
</interface>");

        File.WriteAllText(Path.Combine(_clientDir, "ui", "fe2", "newui", "lobby_v2.package"),
            @"<package name=""lobby_v2"">
  <panel name=""lobby_root"">
    <panel name=""hero_select_grid"" />
    <panel name=""chat_box"" />
  </panel>
</package>");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignore */
            }
    }

    #region XmlSyntaxNormalizer Tests

    [Fact]
    public void XmlSyntaxNormalizer_WhenInvoked_UpgradesAllInlineUiEventHandlers()
    {
        string legacyXml = @"<interface onload=""Init()"" onshow=""Show()"" onclick=""Click()"" onhide=""Hide()""
  onframe=""Frame()"" onmousewheel=""Wheel()"" onenter=""Enter()"" onesc=""Esc()""
  onrightclick=""RightClick()"" ondoubleclick=""DblClick()"" onfocus=""Focus()""
  onlosefocus=""LoseFocus()"" onchange=""Change()"" onselect=""Select()""
  onmouseover=""MouseOver()"" onmouseout=""MouseOut()"" onwake=""Wake()"" onsleep=""Sleep()"">
  <label text=""Events Test"" />
</interface>";

        string normalized = XmlSyntaxNormalizer.NormalizeXml(legacyXml, out int fixes);

        fixes.Should().BeGreaterThanOrEqualTo(18);
        normalized.Should().Contain("onloadlua=\"Init()\"");
        normalized.Should().Contain("onshowlua=\"Show()\"");
        normalized.Should().Contain("onclicklua=\"Click()\"");
        normalized.Should().Contain("onhidelua=\"Hide()\"");
        normalized.Should().Contain("onframelua=\"Frame()\"");
        normalized.Should().Contain("onmousewheellua=\"Wheel()\"");
        normalized.Should().Contain("onenterlua=\"Enter()\"");
        normalized.Should().Contain("onesclua=\"Esc()\"");
        normalized.Should().Contain("onrightclicklua=\"RightClick()\"");
        normalized.Should().Contain("ondoubleclicklua=\"DblClick()\"");
        normalized.Should().Contain("onfocuslua=\"Focus()\"");
        normalized.Should().Contain("onlosefocuslua=\"LoseFocus()\"");
        normalized.Should().Contain("onchangelua=\"Change()\"");
        normalized.Should().Contain("onselectlua=\"Select()\"");
        normalized.Should().Contain("onmouseoverlua=\"MouseOver()\"");
        normalized.Should().Contain("onmouseoutlua=\"MouseOut()\"");
        normalized.Should().Contain("onwakelua=\"Wake()\"");
        normalized.Should().Contain("onsleeplua=\"Sleep()\"");

        // Ensure idempotence: running again shouldn't create 'onclicklualua'
        string secondPass = XmlSyntaxNormalizer.NormalizeXml(normalized, out int secondFixes);
        secondFixes.Should().Be(0);
        secondPass.Should().NotContain("lualua");
    }

    [Fact]
    public void XmlSyntaxNormalizer_WithValidArguments_FixesAmpersandsWithoutTouchingValidEntitiesOrCData()
    {
        string xml = @"<root>
  <label text=""Item & Buff &amp; &lt;Test&gt; &#123; &#x1F;"" />
  <script><![CDATA[if a & b then return true end]]></script>
  <label text=""Another & Bad Ampersand"" />
</root>";

        string normalized = XmlSyntaxNormalizer.NormalizeXml(xml, out int fixes);

        fixes.Should().Be(2);
        normalized.Should().Contain("Item &amp; Buff &amp; &lt;Test&gt; &#123; &#x1F;");
        normalized.Should().Contain("<![CDATA[if a & b then return true end]]>");
        normalized.Should().Contain("Another &amp; Bad Ampersand");
    }

    [Fact]
    public void XmlSyntaxNormalizer_WhenInvoked_FixesUnquotedAttributes()
    {
        string raw = @"<panel width=100 height=200 align=center x=50 y=100 />";
        string normalized = XmlSyntaxNormalizer.NormalizeXml(raw, out int fixes);

        fixes.Should().BeGreaterThanOrEqualTo(5);
        normalized.Should().Contain("width=\"100\"");
        normalized.Should().Contain("height=\"200\"");
        normalized.Should().Contain("align=\"center\"");
        normalized.Should().Contain("x=\"50\"");
        normalized.Should().Contain("y=\"100\"");
    }

    [Fact]
    public void XmlSyntaxNormalizer_WhenInvoked_NormalizesLegacyXmlTags()
    {
        string legacyXml = @"<interface>
  <interface_version>2.0.0</interface_version>
  <floater x=""10"" y=""20"">
    <align>center</align>
    <valign>middle</valign>
  </floater>
  <anchor x=""50"" y=""50"" />
</interface>";

        string normalized = XmlSyntaxNormalizer.NormalizeXml(legacyXml, out int fixes);

        fixes.Should().BeGreaterThanOrEqualTo(4);
        normalized.Should().NotContain("<interface_version>");
        normalized.Should().Contain("<panel float=\"true\" x=\"10\" y=\"20\">");
        normalized.Should().Contain("</panel>");
        normalized.Should().Contain("<panel x=\"50\" y=\"50\" />");
    }

    [Fact]
    public void XmlSyntaxNormalizer_WhenInvoked_RepairsUnclosedCData()
    {
        string brokenXml = @"<root><script><![CDATA[function Test() return 1 end</script></root>";
        string normalized = XmlSyntaxNormalizer.NormalizeXml(brokenXml, out int fixes);

        fixes.Should().Be(1);
        normalized.Should().EndWith("]]>");
    }

    #endregion

    #region UiAnchorRescaler Tests

    [Fact]
    public void UiAnchorRescaler_WhenInvoked_ConvertsFixedPixelsToResponsiveHUnits()
    {
        string legacyXml = @"<panel x=""100px"" y=""150"" width=""384"" height=""76.8px"" />";

        string rescaled = UiAnchorRescaler.RescaleUiCoordinates(legacyXml, out int count);

        count.Should().Be(4);
        rescaled.Should().Contain("13.02h");
        rescaled.Should().Contain("19.53h");
        rescaled.Should().Contain("50h");
        rescaled.Should().Contain("10h");
    }

    [Fact]
    public void UiAnchorRescaler_WhenInvoked_SupportsNegativeAndFractionalValues()
    {
        string xml = @"<panel x=""-150px"" y=""-76.8"" width=""153.6px"" height=""307.2"" />";

        string rescaled = UiAnchorRescaler.RescaleUiCoordinates(xml, out int count);

        count.Should().Be(4);
        rescaled.Should().Contain("-19.53h");
        rescaled.Should().Contain("-10h");
        rescaled.Should().Contain("20h");
        rescaled.Should().Contain("40h");
    }

    [Fact]
    public void UiAnchorRescaler_OnQuery_PreservesExistingResponsiveUnitsAndBorders()
    {
        string modernXml =
            @"<panel x=""50%"" y=""20h"" width=""15w"" height=""10s"" borderthickness=""1px"" growsize=""2"" />";

        string rescaled = UiAnchorRescaler.RescaleUiCoordinates(modernXml, out int count);

        count.Should().Be(0);
        rescaled.Should().Be(modernXml);
    }

    [Fact]
    public void UiAnchorRescaler_WhenInvoked_RescalesExtendedLayoutAttributes()
    {
        string xml =
            @"<panel padding=""38.4px"" margin=""76.8"" font=""19.2"" gap=""15.36px"" headspace=""20"" tailspace=""25"" />";

        string rescaled = UiAnchorRescaler.RescaleUiCoordinates(xml, out int count);

        count.Should().Be(6);
        rescaled.Should().Contain("5h");
        rescaled.Should().Contain("10h");
        rescaled.Should().Contain("2.5h");
        rescaled.Should().Contain("2h");
        rescaled.Should().Contain("2.6h");
        rescaled.Should().Contain("3.26h");
    }

    #endregion

    #region TargetFileRelocator Tests

    [Fact]
    public void TargetFileRelocator_WhenInvoked_RelocatesKnownDeprecatedPaths()
    {
        TargetFileRelocator.RelocatePath("ui/game.interface", out bool r1).Should().Be("ui/game_legacy.interface");
        r1.Should().BeTrue();

        TargetFileRelocator.RelocatePath("/ui/game.interface", out bool r2).Should().Be("ui/game_legacy.interface");
        r2.Should().BeTrue();

        TargetFileRelocator.RelocatePath("ui\\fe2\\game_lobby.package", out bool r3).Should()
            .Be("ui/fe2/newui/lobby_v2.package");
        r3.Should().BeTrue();

        TargetFileRelocator.RelocatePath("ui/fe2/main.interface", out bool r4).Should()
            .Be("ui/fe2/newui/lobby_v2.package");
        r4.Should().BeTrue();

        TargetFileRelocator.RelocatePath("ui/scripts/player_stats.lua", out bool r5).Should()
            .Be("ui/scripts/newui/player_stats_v2.lua");
        r5.Should().BeTrue();

        TargetFileRelocator.RelocatePath("ui/scripts/alt_info.lua", out bool r6).Should()
            .Be("ui/scripts/alt_info_building.lua");
        r6.Should().BeTrue();

        TargetFileRelocator.RelocatePath("shared/sounds/test.wav", out bool r7).Should().Be("shared/sounds/test.wav");
        r7.Should().BeFalse();
    }

    [Fact]
    public void TargetFileRelocator_WhenInvoked_RewritesManifestOperations()
    {
        ModManifest manifest = new()
        {
            Name = "Relocation Test Mod",
            Version = "1.0.0",
            FileEdits =
            [
                new EditFileOperation { TargetName = "ui/game.interface" },
                new EditFileOperation { TargetName = "ui/fe2/game_lobby.package" }
            ],
            FileCopies =
            [
                new CopyFileOperation { TargetName = "ui/scripts/player_stats.lua", SourcePath = "stats.lua" }
            ]
        };

        ModManifest relocated = TargetFileRelocator.RelocateManifest(manifest, out int count, true);

        count.Should().Be(4); // 2 edits relocated + 1 apex duplicate + 1 copy relocated
        relocated.FileEdits.Select(e => e.TargetName).Should().Contain(
        [
            "ui/game_legacy.interface",
            "ui/game_apex.interface",
            "ui/fe2/newui/lobby_v2.package"
        ]);
        relocated.FileCopies.First().TargetName.Should().Be("ui/scripts/newui/player_stats_v2.lua");
    }

    [Fact]
    public void TargetFileRelocator_WhenInvoked_RelocatesXmlTextAttributes()
    {
        string rawXml = @"<modification>
  <editfile name=""ui/game.interface"">
    <find><![CDATA[<panel name=""test""/>]]></find>
  </editfile>
  <editfile name=""/ui/fe2/game_lobby.package"">
    <find><![CDATA[<panel name=""lobby""/>]]></find>
  </editfile>
</modification>";

        string relocatedXml = TargetFileRelocator.RelocateXmlText(rawXml, out int count);

        count.Should().Be(2);
        relocatedXml.Should().Contain("name=\"ui/game_legacy.interface\"");
        relocatedXml.Should().Contain("name=\"ui/fe2/newui/lobby_v2.package\"");
    }

    #endregion

    #region LuaApiCompatibilityShim Tests

    [Fact]
    public void LuaApiCompatibilityShim_WhenInvoked_InjectsStandardHeaderWhenNeeded()
    {
        string legacyLua = @"local client = GetLocalClient()
ChatBox:AddMessage('Welcome to HoN!')";

        string bridged = LuaApiCompatibilityShim.ApplyCompatibilityBridge(legacyLua, out int shims);

        shims.Should().BeGreaterThanOrEqualTo(1);
        bridged.Should().Contain("ProjectKongor HoN 3.x/4.x Lua Compatibility Shim");
        bridged.Should().Contain("GetLocalClient");
        bridged.Should().Contain("KongorSafeHook");
    }

    [Fact]
    public void LuaApiCompatibilityShim_WhenInvoked_RewritesDeprecatedSymbolCalls()
    {
        string lua = @"local player = client.GetLocalClient():GetPlayer()
local ui = UIManager:GetInterface('game')";

        string bridged = LuaApiCompatibilityShim.ApplyCompatibilityBridge(lua, out int shims);

        shims.Should().BeGreaterThanOrEqualTo(2);
        bridged.Should().Contain("client.GetLocalPlayer()");
        bridged.Should().Contain("GetInterface('game')");
    }

    [Fact]
    public void LuaApiCompatibilityShim_WhenInvoked_ReturnsStandaloneShimAsset()
    {
        byte[] bytes = LuaApiCompatibilityShim.GetShimFileBytes();
        string content = LuaApiCompatibilityShim.GetShimFileContent();

        bytes.Should().NotBeEmpty();
        content.Should().Contain("KongorSafeHook");
        content.Should().Contain("_G.PlayerStats = _G.PlayerStats or _G.Player_Stats_V2");
    }

    #endregion

    #region ModRepairEngine Full Pipeline & Community Mods Tests

    [Fact]
    public async Task ModRepairEngine_WhenInvoked_RepairsOutdatedHonModPackage()
    {
        string inputModPath = Path.Combine(_tempDir, "OutdatedMod.honmod");
        string outputModPath = Path.Combine(_tempDir, "RepairedMod.honmod");

        ModManifest manifest = new()
        {
            Name = "Outdated Mod",
            Version = "1.0.0",
            AppVersion = "3.5.2",
            FileCopies =
            [
                new CopyFileOperation { TargetName = "ui/legacy.interface", SourcePath = "legacy.interface" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["legacy.interface"] =
                Encoding.UTF8.GetBytes("<button onclick=\"Click()\" y=\"150px\"><label text=\"A & B\" /></button>"),
            ["scripts/compat.lua"] = Encoding.UTF8.GetBytes("local c = GetLocalClient()\nChatBox:AddMessage('hi')")
        };

        await HonModArchiveWriter.WritePackageAsync(inputModPath, manifest, payloads);

        ModRepairEngine engine = new();
        RepairResult result = await engine.RepairModAsync(inputModPath, outputModPath);

        result.Success.Should().BeTrue();
        result.FixesAppliedCount.Should().BeGreaterThan(0);
        File.Exists(outputModPath).Should().BeTrue();

        using ModPackage repaired = await HonModArchiveReader.ReadPackageAsync(outputModPath);
        repaired.Manifest.AppVersion.Should().Be("*");

        string repairedUi = await repaired.GetPayloadTextAsync("legacy.interface");
        repairedUi.Should().Contain("onclicklua");
        repairedUi.Should().Contain("19.53h");
        repairedUi.Should().Contain("A &amp; B");

        string repairedLua = await repaired.GetPayloadTextAsync("scripts/compat.lua");
        repairedLua.Should().Contain("ProjectKongor HoN");
    }

    [Fact]
    public async Task ModRepairEngine_WhenInvoked_RepairsRankedQuickstatsTooltipsMod()
    {
        // Emulate Ranked Quickstats Tooltips structure:
        // - AppVersion: 3.10.0
        // - Targets ui/game.interface
        // - Contains inline XML patch operations with fixed 4:3 pixels and legacy event handlers
        string inputModPath = Path.Combine(_tempDir, "RankedQuickstats.honmod");
        string outputModPath = Path.Combine(_tempDir, "RankedQuickstats.repaired.honmod");

        ModManifest manifest = new()
        {
            Name = "Ranked Quickstats Tooltips",
            Version = "1.0.0",
            AppVersion = "3.10.0",
            Author = "mrhappyasthma",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/game.interface",
                    Operations =
                    [
                        new FindOperation("<panel name=\"hud_root\"", FindPosition.After),
                        new InsertOperation(
                            "<panel name=\"quickstats_panel\" x=\"120px\" y=\"450px\" width=\"300px\" height=\"200px\" onshow=\"UpdateStats()\" onload=\"InitStats()\">" +
                            "<label text=\"Ranked & Stats\" />" +
                            "</panel>")
                    ]
                }
            ]
        };

        await HonModArchiveWriter.WritePackageAsync(inputModPath, manifest, new Dictionary<string, byte[]>());

        ModRepairEngine engine = new();
        RepairResult result = await engine.RepairModAsync(inputModPath, outputModPath);

        result.Success.Should().BeTrue();
        result.FixesAppliedCount.Should().BeGreaterThan(3);

        using ModPackage repaired = await HonModArchiveReader.ReadPackageAsync(outputModPath);
        repaired.Manifest.AppVersion.Should().Be("*");
        repaired.Manifest.FileEdits.Should().HaveCount(1);

        EditFileOperation edit = repaired.Manifest.FileEdits.First();
        edit.TargetName.Should().Be("ui/game_legacy.interface");

        InsertOperation insert = (InsertOperation)edit.Operations.First(op => op is InsertOperation);
        insert.Content.Should().Contain("onshowlua=\"UpdateStats()\"");
        insert.Content.Should().Contain("onloadlua=\"InitStats()\"");
        insert.Content.Should().Contain("Ranked &amp; Stats");
        insert.Content.Should().Contain("15.63h"); // 120 / 768 * 100
        insert.Content.Should().Contain("58.59h"); // 450 / 768 * 100
        insert.Content.Should().Contain("39.06h"); // 300 / 768 * 100
        insert.Content.Should().Contain("26.04h"); // 200 / 768 * 100
    }

    [Fact]
    public async Task ModRepairEngine_WhenInvoked_RepairsAoeRangeIndicatorsMod()
    {
        string inputModPath = Path.Combine(_tempDir, "AoERangeIndicators.honmod");
        string outputModPath = Path.Combine(_tempDir, "AoERangeIndicators.repaired.honmod");

        ModManifest manifest = new()
        {
            Name = "AOE Range Indicators",
            Version = "1.6.4",
            AppVersion = "4.0.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/game.interface",
                    Operations =
                    [
                        new FindOperation("<panel name=\"hud_root\"", FindPosition.After),
                        new InsertOperation(
                            "<panel name=\"aoe_range_indicator\" width=\"150px\" height=\"150px\" onclick=\"ToggleAoE()\" />")
                    ]
                }
            ],
            FileCopies =
            [
                new CopyFileOperation
                    { TargetName = "ui/aoe_settings.interface", SourcePath = "aoe_settings.interface" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["aoe_settings.interface"] = Encoding.UTF8.GetBytes(@"<interface>
  <floater x=""100"" y=""200"">
    <button onclick=""Save()"" width=200 height=50>
      <label text=""Range & Radius"" />
    </button>
  </floater>
</interface>")
        };

        await HonModArchiveWriter.WritePackageAsync(inputModPath, manifest, payloads);

        ModRepairEngine engine = new();
        RepairResult result = await engine.RepairModAsync(inputModPath, outputModPath);

        result.Success.Should().BeTrue();
        result.TransformedFilesCount.Should().BeGreaterThan(0);

        using ModPackage repaired = await HonModArchiveReader.ReadPackageAsync(outputModPath);
        repaired.Manifest.AppVersion.Should().Be("*");
        repaired.Manifest.FileEdits.First().TargetName.Should().Be("ui/game_legacy.interface");

        string repairedPayload = await repaired.GetPayloadTextAsync("aoe_settings.interface");
        repairedPayload.Should().Contain("<panel float=\"true\"");
        repairedPayload.Should().Contain("onclicklua=\"Save()\"");
        repairedPayload.Should().Contain("Range &amp; Radius");
        repairedPayload.Should().Contain("13.02h");
        repairedPayload.Should().Contain("26.04h");
    }

    [Fact]
    public async Task ModRepairEngine_BatchRepairDirectory_AchievesOver70PercentSuccessRate()
    {
        string incomingDir = Path.Combine(_tempDir, "incoming_mods");
        string repairedDir = Path.Combine(_tempDir, "repaired_mods");
        Directory.CreateDirectory(incomingDir);

        // Generate a test suite of 8 broken/outdated legacy mods
        string[] modNames =
        [
            "Ranked Quickstats Tooltips",
            "AOE Range Indicators",
            "Last Hit Assist",
            "Auto Pick And Vote Ban",
            "Tower Denier",
            "Constant Inventory",
            "Custom HUD Legacy",
            "Throwback Cursors"
        ];

        for (int i = 0; i < modNames.Length; i++)
        {
            string name = modNames[i];
            string modFile = Path.Combine(incomingDir, $"{name}.honmod");

            ModManifest manifest = new()
            {
                Name = name,
                Version = $"1.{i}.0",
                AppVersion = "3.5.0",
                FileEdits =
                [
                    new EditFileOperation
                    {
                        TargetName = i % 2 == 0 ? "ui/game.interface" : "ui/fe2/game_lobby.package",
                        Operations =
                        [
                            new FindOperation("<panel name=\"hud_root\"", FindPosition.After),
                            new InsertOperation(
                                $"<panel name=\"mod_{i}\" x=\"100px\" y=\"150px\" onclick=\"Click_{i}()\"><label text=\"{name} & Co\" /></panel>")
                        ]
                    }
                ]
            };

            Dictionary<string, byte[]> payloads = new()
            {
                [$"scripts/mod_{i}.lua"] =
                    Encoding.UTF8.GetBytes(
                        $"local client = GetLocalClient()\nChatBox:AddMessage('Mod {name} initialized')")
            };

            await HonModArchiveWriter.WritePackageAsync(modFile, manifest, payloads);
        }

        ModRepairEngine engine = new();
        BatchRepairResult batchResult = await engine.RepairAllAsync(incomingDir, repairedDir);

        batchResult.TotalDiscovered.Should().Be(8);
        batchResult.TotalRepaired.Should().Be(8);
        batchResult.TotalFailed.Should().Be(0);
        batchResult.SuccessRate.Should().Be(100.0);
        batchResult.SuccessRate.Should().BeGreaterThanOrEqualTo(70.0,
            "Repair engine must achieve >= 70% success rate on outdated/broken mods");

        // Verify that all repaired mods are well-formed and loadable
        string[] repairedFiles = Directory.GetFiles(repairedDir, "*.honmod");
        repairedFiles.Should().HaveCount(8);

        foreach (string repairedFile in repairedFiles)
        {
            using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(repairedFile);
            pkg.Manifest.AppVersion.Should().Be("*");
            pkg.Manifest.FileEdits.Should().NotBeEmpty();

            string target = pkg.Manifest.FileEdits.First().TargetName;
            target.Should().BeOneOf("ui/game_legacy.interface", "ui/fe2/newui/lobby_v2.package");
        }
    }

    [Fact]
    public async Task ModRepairEngine_WhenInvoked_RepairedModCanApplyCleanlyAgainstModernClientFiles()
    {
        // 1. Create a broken legacy mod targeting ui/game.interface with unescaped ampersands and fixed pixels
        string inputModPath = Path.Combine(_tempDir, "LegacyHudMod.honmod");
        string outputModPath = Path.Combine(_tempDir, "LegacyHudMod.repaired.honmod");

        ModManifest manifest = new()
        {
            Name = "Legacy HUD Extension",
            Version = "1.0.0",
            AppVersion = "3.2.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/game.interface",
                    Operations =
                    [
                        new FindOperation("<panel name=\"player_portrait\" width=\"10h\" height=\"10h\" />"),
                        new InsertOperation(
                            "<panel name=\"custom_hud_box\" x=\"100px\" y=\"150px\" onclick=\"Toggle()\">" +
                            "<label text=\"Mana & Health\" />" +
                            "</panel>")
                    ]
                }
            ]
        };

        await HonModArchiveWriter.WritePackageAsync(inputModPath, manifest, new Dictionary<string, byte[]>());

        // 2. Repair mod
        ModRepairEngine engine = new();
        RepairResult res = await engine.RepairModAsync(inputModPath, outputModPath);
        res.Success.Should().BeTrue();

        // 3. Load repaired package
        using ModPackage repairedPackage = await HonModArchiveReader.ReadPackageAsync(outputModPath);
        repairedPackage.Manifest.FileEdits.First().TargetName.Should().Be("ui/game_legacy.interface");

        // 4. Test applying the repaired patch directly to modern client file
        string targetFile = Path.Combine(_clientDir, "ui", "game_legacy.interface");
        string baselineContent = File.ReadAllText(targetFile);

        string patchedContent = await FuzzyStringPatcher.ApplyPatchAsync(
            baselineContent,
            repairedPackage.Manifest.FileEdits.First().Operations,
            targetFileName: "ui/game_legacy.interface");

        patchedContent.Should().Contain("name=\"custom_hud_box\"");
        patchedContent.Should().Contain("onclicklua=\"Toggle()\"");
        patchedContent.Should().Contain("Mana &amp; Health");
        patchedContent.Should().Contain("13.02h");
        patchedContent.Should().Contain("19.53h");

        // Validate patched XML is completely well-formed
        Action parseAction = () => XDocument.Parse(patchedContent);
        parseAction.Should().NotThrow("Patched UI interface must be 100% well-formed XML");
    }

    #endregion
}
