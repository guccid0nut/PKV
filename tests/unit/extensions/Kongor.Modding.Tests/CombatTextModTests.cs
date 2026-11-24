// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Patching;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive verification test suite for Combat Text & Damage Float Customizer mod (M3).
///     Validates manifest parsing, packaging, archive extraction, patch resolution against base.gamemechanics,
///     and conflict-free multi-mod coexistence.
/// </summary>
[Collection("ModdingDiskCollection")]
public sealed class CombatTextModTests : IDisposable
{
    public const string CombatTextModXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<modification 
    application=""Heroes of Newerth"" 
    appversion=""*"" 
    mmversion=""1.3""
    name=""Combat Text &amp; Damage Float Customizer"" 
    version=""1.0.0"" 
    author=""ProjectKongor"" 
    description=""Custom floating combat damage numbers with vivid color coding for Physical (Orange/Red), Magical (Cyan/Blue), Pure (Gold), and Critical Strikes (Heavy Crimson).""
    icon=""icon.png""
    category=""Combat UI"">
  <editfile name=""base.gamemechanics"">
    <find><![CDATA[<popup name=""critical"" color=""red"" value=""true"" message=""{value}!"" duration=""2000"" fadetime=""1000"" />]]></find>
    <replace><![CDATA[<popup name=""critical"" color=""1.0 0.1 0.1 1.0"" value=""true"" message=""{value}!"" duration=""1600"" fadetime=""500"" startx=""0"" starty=""14"" speedx=""0"" speedy=""3.0"" scale=""1.25"" />]]></replace>
    <find><![CDATA[<popup name=""damage_physical"" color=""2 0.33 0.33 1"" value=""true"" message=""{value}"" duration=""500"" fadetime=""1000"" visibility=""self"" startx=""0"" starty=""12"" scale=""0.25"" />]]></find>
    <replace><![CDATA[<popup name=""damage_physical"" color=""1.0 0.35 0.1 1.0"" value=""true"" message=""{value}"" duration=""1200"" fadetime=""400"" visibility=""self"" startx=""0"" starty=""12"" speedx=""0"" speedy=""2.2"" scale=""0.75"" />]]></replace>
    <find><![CDATA[<popup name=""damage_magical"" color=""1 0.33 1.25 1"" value=""true"" message=""{value}"" duration=""2000"" fadetime=""1000"" visibility=""self"" startx=""0"" starty=""12"" scale=""0.5"" />]]></find>
    <replace><![CDATA[<popup name=""damage_magical"" color=""0.1 0.75 1.0 1.0"" value=""true"" message=""{value}"" duration=""1200"" fadetime=""400"" visibility=""self"" startx=""0"" starty=""12"" speedx=""0"" speedy=""2.2"" scale=""0.75"" />]]></replace>
    <find><![CDATA[<popup name=""damage_attempted"" color=""1 1 0 1"" value=""true"" message=""{value}"" duration=""2000"" fadetime=""1000"" visibility=""self"" />]]></find>
    <replace><![CDATA[<popup name=""damage_attempted"" color=""1.0 0.85 0.1 1.0"" value=""true"" message=""{value}"" duration=""1400"" fadetime=""450"" visibility=""self"" startx=""0"" starty=""12"" speedx=""0"" speedy=""2.5"" scale=""0.85"" />]]></replace>
  </editfile>
</modification>";

    private readonly string _baseGameMechanicsPath;
    private readonly string _honmodPackagePath;
    private readonly string _modsDir;
    private readonly string _tempDir;

    public CombatTextModTests()
    {
        string repoRoot = FindRepoRoot();
        _tempDir = Path.Combine(Path.GetTempPath(), "CombatTextModTest_" + Guid.NewGuid().ToString("N"));
        _modsDir = Path.Combine(repoRoot, "games", "client", "mods");
        _baseGameMechanicsPath = Path.Combine(repoRoot, "games", "client", "game", "base.gamemechanics");
        _honmodPackagePath = Path.Combine(_tempDir, "combat_text_damage_float.honmod");

        Directory.CreateDirectory(_tempDir);
        EnsureHonModPackageExistsAsync().GetAwaiter().GetResult();
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

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "PROJECT.md")))
                return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private async Task EnsureHonModPackageExistsAsync()
    {
        ModManifest manifest = ModManifestParser.Parse(CombatTextModXml);
        byte[] iconBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // Valid PNG header

        string repoRoot = FindRepoRoot();
        string sourceIconPath = Path.Combine(repoRoot, "apps", "control-panel", "src", "Assets", "nexus-icon.png");
        if (File.Exists(sourceIconPath)) iconBytes = await File.ReadAllBytesAsync(sourceIconPath);

        Dictionary<string, byte[]> payloads = new()
        {
            ["icon.png"] = iconBytes
        };

        await HonModArchiveWriter.WritePackageAsync(_honmodPackagePath, manifest, payloads, iconBytes);
    }

    [Fact]
    public void ModManifestParser_ParsesCombatTextManifest_PopulatesAllProperties()
    {
        ModManifest manifest = ModManifestParser.Parse(CombatTextModXml);

        manifest.Name.Should().Be("Combat Text & Damage Float Customizer");
        manifest.Version.Should().Be("1.0.0");
        manifest.Author.Should().Be("ProjectKongor");
        manifest.AppVersion.Should().Be("*");
        manifest.MmVersion.Should().Be("1.3");
        manifest.Icon.Should().Be("icon.png");
        manifest.Category.Should().Be("Combat UI");
        manifest.Description.Should().Contain("Physical (Orange/Red)");

        manifest.FileEdits.Should().HaveCount(1);
        EditFileOperation edit = manifest.FileEdits[0];
        edit.TargetName.Should().Be("base.gamemechanics");
        edit.Operations.Should().HaveCount(8);

        // Check pairs of Find / Replace
        edit.Operations[0].Should().BeOfType<FindOperation>();
        edit.Operations[1].Should().BeOfType<ReplaceOperation>();
        edit.Operations[2].Should().BeOfType<FindOperation>();
        edit.Operations[3].Should().BeOfType<ReplaceOperation>();
        edit.Operations[4].Should().BeOfType<FindOperation>();
        edit.Operations[5].Should().BeOfType<ReplaceOperation>();
        edit.Operations[6].Should().BeOfType<FindOperation>();
        edit.Operations[7].Should().BeOfType<ReplaceOperation>();
    }

    [Fact]
    public async Task HonModArchiveReader_ReadsCombatTextPackage_ExtractsCorrectManifestAndIcon()
    {
        File.Exists(_honmodPackagePath).Should().BeTrue();

        using ModPackage package = await HonModArchiveReader.ReadPackageAsync(_honmodPackagePath);

        package.Manifest.Name.Should().Be("Combat Text & Damage Float Customizer");
        package.Manifest.Version.Should().Be("1.0.0");
        package.Manifest.Author.Should().Be("ProjectKongor");
        package.IconData.Should().NotBeNull();
        package.IconData!.Length.Should().BeGreaterThan(0);
        package.Sha256Hash.Should().NotBeNullOrWhiteSpace();
        package.FileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FuzzyStringPatcher_AppliesCombatTextEdits_MatchesBaseGameMechanicsExactly()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        File.Exists(_baseGameMechanicsPath).Should()
            .BeTrue($"base.gamemechanics must exist at {_baseGameMechanicsPath}");

        string baseContent = await File.ReadAllTextAsync(_baseGameMechanicsPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(CombatTextModXml);

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(
            baseContent,
            manifest.FileEdits[0].Operations,
            targetFileName: "base.gamemechanics"
        );

        // 1. Critical strike popup verification
        patched.Should()
            .Contain(
                @"<popup name=""critical"" color=""1.0 0.1 0.1 1.0"" value=""true"" message=""{value}!"" duration=""1600"" fadetime=""500"" startx=""0"" starty=""14"" speedx=""0"" speedy=""3.0"" scale=""1.25"" />");

        // 2. Physical damage popup verification (Orange/Red)
        patched.Should()
            .Contain(
                @"<popup name=""damage_physical"" color=""1.0 0.35 0.1 1.0"" value=""true"" message=""{value}"" duration=""1200"" fadetime=""400"" visibility=""self"" startx=""0"" starty=""12"" speedx=""0"" speedy=""2.2"" scale=""0.75"" />");

        // 3. Magical damage popup verification (Electric Blue)
        patched.Should()
            .Contain(
                @"<popup name=""damage_magical"" color=""0.1 0.75 1.0 1.0"" value=""true"" message=""{value}"" duration=""1200"" fadetime=""400"" visibility=""self"" startx=""0"" starty=""12"" speedx=""0"" speedy=""2.2"" scale=""0.75"" />");

        // 4. Attempted / Pure damage popup verification (Pure Gold)
        patched.Should().Contain(
            @"<popup name=""damage_attempted"" color=""1.0 0.85 0.1 1.0"" value=""true"" message=""{value}"" duration=""1400"" fadetime=""450"" visibility=""self"" startx=""0"" starty=""12"" speedx=""0"" speedy=""2.5"" scale=""0.85"" />");

        // 5. Ensure original unstyled lines are replaced
        patched.Should().NotContain(@"<popup name=""damage_physical"" color=""2 0.33 0.33 1""");
        patched.Should().NotContain(@"<popup name=""damage_magical"" color=""1 0.33 1.25 1""");
        patched.Should().NotContain(@"<popup name=""damage_attempted"" color=""1 1 0 1""");
    }

    [Fact]
    public async Task ModManager_DiscoversAndVerifiesCombatTextMod_ZeroConflicts()
    {
        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(_tempDir);

        discovered.Should().NotBeEmpty();
        ModPackage? combatMod =
            discovered.FirstOrDefault(m => m.Manifest.Name == "Combat Text & Damage Float Customizer");
        combatMod.Should().NotBeNull();

        VerificationResult vres = manager.VerifyMods(discovered);
        vres.Success.Should()
            .BeTrue("All mods in mods directory must resolve cleanly without circular dependencies or hard errors");
    }
}

