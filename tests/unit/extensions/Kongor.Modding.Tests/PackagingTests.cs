// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Patching;
using Xunit;

namespace Kongor.Modding.Tests;

public class PackagingTests : IDisposable
{
    private readonly string _tempDir;

    public PackagingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorModdingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    [Fact]
    public async Task WriteAndReadHonMod_RoundTrip_PreservesPayloadsAndMetadata()
    {
        string honmodPath = Path.Combine(_tempDir, "TestMod.honmod");
        ModManifest manifest = new()
        {
            Name = "Test Packaging Mod",
            Version = "2.1.0",
            Author = "Kongor Dev",
            Description = "Testing packaging reader and writer"
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["res/test.txt"] = Encoding.UTF8.GetBytes("Hello Kongor Modding!"),
            ["res/icon.png"] = [137, 80, 78, 71, 13, 10, 26, 10]
        };

        await HonModArchiveWriter.WritePackageAsync(honmodPath, manifest, payloads, payloads["res/icon.png"]);

        File.Exists(honmodPath).Should().BeTrue();

        using ModPackage modPackage = await HonModArchiveReader.ReadPackageAsync(honmodPath);
        modPackage.Manifest.Name.Should().Be("Test Packaging Mod");
        modPackage.Manifest.Version.Should().Be("2.1.0");
        modPackage.Sha256Hash.Should().NotBeNullOrWhiteSpace();
        modPackage.IconData.Should().NotBeNull();
        modPackage.IconData!.Length.Should().Be(8);

        string extractedText = await modPackage.GetPayloadTextAsync("res/test.txt");
        extractedText.Should().Be("Hello Kongor Modding!");
    }

    [Fact]
    public async Task S2zArchive_WriteAndRead_SupportsMultipleArchivesAndResolvers()
    {
        string res0Path = Path.Combine(_tempDir, "resources0.s2z");
        string res1Path = Path.Combine(_tempDir, "resources1.s2z");

        Dictionary<string, byte[]> baseFiles = new()
        {
            ["ui/main.interface"] = Encoding.UTF8.GetBytes("<interface base='true' />"),
            ["shared/common.lua"] = Encoding.UTF8.GetBytes("print('base')")
        };

        Dictionary<string, byte[]> patchFiles = new()
        {
            ["ui/main.interface"] = Encoding.UTF8.GetBytes("<interface patch1='true' />")
        };

        await S2zArchiveWriter.WriteArchiveAsync(res0Path, baseFiles);
        await S2zArchiveWriter.WriteArchiveAsync(res1Path, patchFiles);

        using GameResourceResolver resolver = new([res0Path, res1Path]);

        // ui/main.interface should resolve to resources1.s2z (highest archive priority)
        string? uiContent = await resolver.TryReadGameTextAsync("ui/main.interface");
        uiContent.Should().Be("<interface patch1='true' />");

        // shared/common.lua should fall through to resources0.s2z
        string? luaContent = await resolver.TryReadGameTextAsync("shared/common.lua");
        luaContent.Should().Be("print('base')");
    }

    [Fact]
    public async Task GameResourceResolver_WithCorruptedArchive_LogsWarningAndDoesNotThrow()
    {
        string res0Path = Path.Combine(_tempDir, "res_good.s2z");
        string corruptedPath = Path.Combine(_tempDir, "res_bad.s2z");

        Dictionary<string, byte[]> baseFiles = new()
        {
            ["ui/valid.txt"] = Encoding.UTF8.GetBytes("valid")
        };
        await S2zArchiveWriter.WriteArchiveAsync(res0Path, baseFiles);

        // Create corrupt file (truncated, no EOCD)
        await File.WriteAllBytesAsync(corruptedPath, [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03]);

        List<string> loggedWarnings = [];
        using GameResourceResolver resolver = new([res0Path, corruptedPath], null, null, w => loggedWarnings.Add(w));

        resolver.Warnings.Should().HaveCount(1);
        resolver.Warnings[0].Should().Contain("Failed to open archive");
        loggedWarnings.Should().HaveCount(1);

        // Good archive should still resolve successfully
        string? text = await resolver.TryReadGameTextAsync("ui/valid.txt");
        text.Should().Be("valid");
    }

    [Fact]
    public async Task GameResourceResolver_WithCorruptedArchive_InVfs_LogsWarningAndDoesNotThrow()
    {
        using InMemoryVirtualFileSystem vfs = new();
        string res0Path = "game/res_good.s2z";
        string corruptedPath = "game/res_bad.s2z";

        Dictionary<string, byte[]> baseFiles = new()
        {
            ["ui/valid.txt"] = Encoding.UTF8.GetBytes("valid_vfs")
        };
        await S2zArchiveWriter.WriteArchiveAsync(vfs, res0Path, baseFiles);

        // Create corrupt file in VFS (truncated, no EOCD)
        await vfs.WriteAllBytesAsync(corruptedPath, [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03]);

        List<string> loggedWarnings = [];
        using GameResourceResolver resolver = new([res0Path, corruptedPath], null, vfs, w => loggedWarnings.Add(w));

        resolver.Warnings.Should().HaveCount(1);
        resolver.Warnings[0].Should().Contain("Failed to open archive");
        loggedWarnings.Should().HaveCount(1);

        // Good archive should still resolve successfully
        string? text = await resolver.TryReadGameTextAsync("ui/valid.txt");
        text.Should().Be("valid_vfs");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task TacticalHudMod_PackageAndVerify_IntegrityAndPatchResolution()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (!NexusTestEnvironment.HasGameAssets) return;
        string baseDir = AppContext.BaseDirectory;
        string repoRoot = baseDir;
        while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnxx")) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")))
        {
            string? parent = Path.GetDirectoryName(repoRoot);
            if (string.IsNullOrEmpty(parent) || parent == repoRoot) break;
            repoRoot = parent;
        }

        string unpackedModDir = Path.Combine(repoRoot, "games", "client", "mods", "tactical_hud_neutral_overlays");
        string honmodPath = Path.Combine(Path.GetTempPath(), $"tactical_hud_{Guid.NewGuid():N}.honmod");

        Directory.Exists(unpackedModDir).Should().BeTrue();
        File.Exists(Path.Combine(unpackedModDir, "mod.xml")).Should().BeTrue();
        File.Exists(Path.Combine(unpackedModDir, "ui", "tactical_hud.package")).Should().BeTrue();
        File.Exists(Path.Combine(unpackedModDir, "ui", "scripts", "tactical_hud.lua")).Should().BeTrue();

        string modXmlContent = await File.ReadAllTextAsync(Path.Combine(unpackedModDir, "mod.xml"));
        ModManifest manifest = ModManifestParser.Parse(modXmlContent);

        manifest.Name.Should().Be("Tactical HUD & Neutral Camp Overlays");
        manifest.Version.Should().NotBeNullOrWhiteSpace();
        manifest.FileCopies.Should().HaveCount(2);
        manifest.FileEdits.Should().HaveCount(2);

        Dictionary<string, byte[]> payloads = new()
        {
            ["ui/tactical_hud.package"] =
                await File.ReadAllBytesAsync(Path.Combine(unpackedModDir, "ui", "tactical_hud.package")),
            ["ui/scripts/tactical_hud.lua"] =
                await File.ReadAllBytesAsync(Path.Combine(unpackedModDir, "ui", "scripts", "tactical_hud.lua"))
        };

        byte[]? iconBytes = null;
        string iconSrc = Path.Combine(repoRoot, "apps", "control-panel", "src", "Assets", "nexus-icon.png");
        if (File.Exists(iconSrc))
        {
            iconBytes = await File.ReadAllBytesAsync(iconSrc);
            string modIconPath = Path.Combine(unpackedModDir, "icon.png");
            if (!File.Exists(modIconPath)) await File.WriteAllBytesAsync(modIconPath, iconBytes);
        }

        await HonModArchiveWriter.WritePackageAsync(honmodPath, manifest, payloads, iconBytes);
        File.Exists(honmodPath).Should().BeTrue();

        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(honmodPath);
        pkg.Manifest.Name.Should().Be("Tactical HUD & Neutral Camp Overlays");
        pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();
        pkg.FileSizeBytes.Should().BeGreaterThan(0);

        string hudPackageContent = await pkg.GetPayloadTextAsync("ui/tactical_hud.package");
        hudPackageContent.Should().Contain("tactical_hud_root");
        hudPackageContent.Should().Contain("tactical_buyback_panel");
        hudPackageContent.Should().Contain("tactical_ally_ult_panel");
        hudPackageContent.Should().Contain("tactical_camp_overlay");
        hudPackageContent.Should().Contain("L1. Well (Hard)");
        hudPackageContent.Should().Contain("H6. Ancients");

        string hudLuaContent = await pkg.GetPayloadTextAsync("ui/scripts/tactical_hud.lua");
        hudLuaContent.Should().Contain("TacticalHud = TacticalHud or {}");
        hudLuaContent.Should().Contain("TacticalHud.UpdateNeutralCamps");
        hudLuaContent.Should().Contain("TacticalHud.UpdateAllyUlt");
        hudLuaContent.Should().Contain("TacticalHud.UpdateBuyback");

        string apexInterfacePath = Path.Combine(repoRoot, "games", "client", "game", "ui", "game_apex.interface");
        if (File.Exists(apexInterfacePath))
        {
            string apexContent = await File.ReadAllTextAsync(apexInterfacePath);
            EditFileOperation apexEdit = manifest.FileEdits.First(e => e.TargetName == "ui/game_apex.interface");
            string patchedApex = await FuzzyStringPatcher.ApplyPatchAsync(apexContent, apexEdit.Operations);
            patchedApex.Should().Contain("<include file=\"/ui/tactical_hud.package\" />");
            patchedApex.Should().Contain("<lua file=\"/ui/scripts/tactical_hud.lua\" />");
        }

        string legacyInterfacePath = Path.Combine(repoRoot, "games", "client", "game", "ui", "game_legacy.interface");
        if (File.Exists(legacyInterfacePath))
        {
            string legacyContent = await File.ReadAllTextAsync(legacyInterfacePath);
            EditFileOperation legacyEdit = manifest.FileEdits.First(e => e.TargetName == "ui/game_legacy.interface");
            string patchedLegacy = await FuzzyStringPatcher.ApplyPatchAsync(legacyContent, legacyEdit.Operations);
            patchedLegacy.Should().Contain("<include file=\"/ui/tactical_hud.package\" />");
            patchedLegacy.Should().Contain("<lua file=\"/ui/scripts/tactical_hud.lua\" />");
        }
    }

    [Fact]
    public async Task UnrealTournamentAnnouncerMod_PackageAndVerify_IntegrityAndStringtables()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string baseDir = AppContext.BaseDirectory;
        string repoRoot = baseDir;
        while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnxx")) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")))
        {
            string? parent = Path.GetDirectoryName(repoRoot);
            if (string.IsNullOrEmpty(parent) || parent == repoRoot) break;
            repoRoot = parent;
        }

        string unpackedModDir = Path.Combine(repoRoot, "games", "client", "mods", "ut_announcer_pack");
        string honmodDir = Path.Combine(repoRoot, "games", "client", "mods", "ut_announcer_pack.honmod");
        string honmodPath = Path.Combine(Path.GetTempPath(), $"ut_announcer_pack_{Guid.NewGuid():N}.honmod.zip");

        Directory.CreateDirectory(unpackedModDir);
        string soundsDir = Path.Combine(unpackedModDir, "sounds");
        Directory.CreateDirectory(soundsDir);

        // Ensure all audio WAV clips are generated
        string[] soundKeys = UtAudioSynthesizer.AllSoundNames;
        foreach (string key in soundKeys)
        {
            string wavPath = Path.Combine(soundsDir, key + ".wav");
            if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 1000)
            {
                byte[] audioBytes = UtAudioSynthesizer.GenerateWav(key);
                await File.WriteAllBytesAsync(wavPath, audioBytes);
            }
        }

        string modXmlPath = Path.Combine(unpackedModDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue();

        string modXmlContent = await File.ReadAllTextAsync(modXmlPath);
        ModManifest manifest = ModManifestParser.Parse(modXmlContent);

        manifest.Name.Should().Be("Unreal Tournament Announcer Pack");
        manifest.Version.Should().NotBeNullOrWhiteSpace();
        manifest.Author.Should().NotBeNullOrWhiteSpace();
        manifest.Category.Should().Be("Audio");
        manifest.FileCopies.Count.Should().BeGreaterThanOrEqualTo(24);
        manifest.StringtableEdits.Count.Should().Be(2);

        // Verify audio headers and payloads
        Dictionary<string, byte[]> payloads = [];
        foreach (CopyFileOperation copyOp in manifest.FileCopies)
        {
            string srcFile = Path.Combine(unpackedModDir, copyOp.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(srcFile).Should().BeTrue($"Expected audio payload '{copyOp.SourcePath}' to exist on disk.");

            byte[] fileBytes = await File.ReadAllBytesAsync(srcFile);
            fileBytes.Length.Should().BeGreaterThan(1000, "Audio WAV files must be non-empty valid PCM streams.");

            // Validate RIFF header
            Encoding.ASCII.GetString(fileBytes, 0, 4).Should().Be("RIFF");
            Encoding.ASCII.GetString(fileBytes, 8, 4).Should().Be("WAVE");
            Encoding.ASCII.GetString(fileBytes, 12, 4).Should().Be("fmt ");

            payloads[copyOp.SourcePath] = fileBytes;
        }

        // Verify Stringtable Patches
        string clientSoundsPath = Path.Combine(repoRoot, "games", "shared", "stringtables", "client_sounds_en.str");
        if (File.Exists(clientSoundsPath))
        {
            string origClientSounds = await File.ReadAllTextAsync(clientSoundsPath);
            EditStringtableOperation soundEdit =
                manifest.StringtableEdits.First(e => e.TargetName.Contains("client_sounds_en.str"));
            string patchedSounds = StringtablePatcher.ApplyPatch(origClientSounds, soundEdit.Entries);

            patchedSounds.Should().Contain("/shared/sounds/announcer/unreal/first_blood.wav");
            patchedSounds.Should().Contain("/shared/sounds/announcer/unreal/double_kill.wav");
            patchedSounds.Should().Contain("/shared/sounds/announcer/unreal/3_kills.wav");
            patchedSounds.Should().Contain("/shared/sounds/announcer/unreal/10_kills.wav");
            patchedSounds.Should().Contain("/shared/sounds/announcer/unreal/immortal.wav");
        }

        string gameMessagesPath = Path.Combine(repoRoot, "games", "shared", "stringtables", "game_messages_en.str");
        if (File.Exists(gameMessagesPath))
        {
            string origGameMessages = await File.ReadAllTextAsync(gameMessagesPath);
            EditStringtableOperation msgEdit =
                manifest.StringtableEdits.First(e => e.TargetName.Contains("game_messages_en.str"));
            string patchedMessages = StringtablePatcher.ApplyPatch(origGameMessages, msgEdit.Entries);

            patchedMessages.Should().Contain("^rFIRST BLOOD^*");
            patchedMessages.Should().Contain("^084DOUBLE KILL^*");
            patchedMessages.Should().Contain("^082KILLING SPREE^*");
            patchedMessages.Should().Contain("^800GODLIKE^*");
            patchedMessages.Should().Contain("^840HOLY SHIT^*");
        }

        // Package into .honmod archive
        byte[]? iconBytes = null;
        string iconPath = Path.Combine(unpackedModDir, "icon.png");
        if (File.Exists(iconPath)) iconBytes = await File.ReadAllBytesAsync(iconPath);

        try
        {
            await HonModArchiveWriter.WritePackageAsync(honmodPath, manifest, payloads, iconBytes);
            File.Exists(honmodPath).Should().BeTrue();

            using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(honmodPath);
            pkg.Manifest.Name.Should().Be("Unreal Tournament Announcer Pack");
            pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();
            pkg.FileSizeBytes.Should().BeGreaterThan(0);

            byte[] fbBytes = await pkg.GetPayloadBytesAsync("sounds/first_blood.wav");
            fbBytes.Length.Should().BeGreaterThan(1000);
            Encoding.ASCII.GetString(fbBytes, 0, 4).Should().Be("RIFF");
        }
        finally
        {
            if (File.Exists(honmodPath))
            {
                try { File.Delete(honmodPath); } catch { }
            }
        }
    }
}

public static class UtAudioSynthesizer
{
    public static readonly string[] AllSoundNames =
    [
        "first_blood",
        "double_kill",
        "triple_kill",
        "quad_kill",
        "annihilation",
        "3_kills",
        "4_kills",
        "5_kills",
        "6_kills",
        "7_kills",
        "8_kills",
        "9_kills",
        "10_kills",
        "immortal",
        "massacre",
        "genocide",
        "denied",
        "humiliation",
        "smackdown",
        "rage_quit",
        "prepare_for_battle",
        "startgame",
        "victory",
        "defeat",
        "rival",
        "payback"
    ];

    public static byte[] GenerateWav(string soundType, double durationSeconds = 1.0, int sampleRate = 16128)
    {
        int numSamples = (int)(sampleRate * durationSeconds);
        if (numSamples < 8000) numSamples = 16128;
        int dataSize = numSamples * 2;
        int chunkSize = 36 + dataSize;

        using MemoryStream ms = new(44 + dataSize);
        using BinaryWriter writer = new(ms);

        // RIFF header (44 bytes, standard 16-bit PCM Mono)
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(chunkSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size
        writer.Write((short)1); // AudioFormat PCM
        writer.Write((short)1); // NumChannels Mono
        writer.Write(sampleRate); // SampleRate
        writer.Write(sampleRate * 2); // ByteRate
        writer.Write((short)2); // BlockAlign
        writer.Write((short)16); // BitsPerSample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double val = 0.0;

            switch (soundType.ToLowerInvariant())
            {
                case "first_blood":
                {
                    double env = Math.Exp(-2.5 * t);
                    double bass = Math.Sin(2 * Math.PI * 85 * (1.0 - 0.2 * t) * t) * Math.Exp(-4.0 * t);
                    double lead = 0.4 * Math.Sin(2 * Math.PI * 220 * t) +
                                  0.3 * Math.Sin(2 * Math.PI * 330 * t) +
                                  0.3 * Math.Sin(2 * Math.PI * 440 * t) +
                                  0.2 * Math.Sin(2 * Math.PI * 660 * t);
                    val = (0.6 * bass + 0.4 * lead) * env;
                    break;
                }
                case "double_kill":
                {
                    double pulse = Math.Exp(-6.0 * (t % 0.35));
                    double f = t < 0.25 ? 261.63 : t < 0.5 ? 329.63 : t < 0.75 ? 392.0 : 523.25;
                    val = (0.5 * Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(2 * Math.PI * f * 2 * t)) * pulse;
                    break;
                }
                case "triple_kill":
                {
                    double pulse = Math.Exp(-5.0 * (t % 0.3));
                    val = (0.35 * Math.Sin(2 * Math.PI * 329.63 * t) +
                           0.30 * Math.Sin(2 * Math.PI * 415.30 * t) +
                           0.25 * Math.Sin(2 * Math.PI * 493.88 * t) +
                           0.20 * Math.Sin(2 * Math.PI * 659.25 * t)) * pulse;
                    break;
                }
                case "quad_kill":
                case "ultra_kill":
                {
                    double vib = 1.0 + 0.05 * Math.Sin(2 * Math.PI * 6.0 * t);
                    double env = Math.Exp(-1.8 * t);
                    val = (0.35 * Math.Sin(2 * Math.PI * 440 * vib * t) +
                           0.30 * Math.Sin(2 * Math.PI * 554.37 * vib * t) +
                           0.25 * Math.Sin(2 * Math.PI * 659.25 * vib * t) +
                           0.20 * Math.Sin(2 * Math.PI * 880 * vib * t)) * env;
                    break;
                }
                case "annihilation":
                case "monster_kill":
                case "8_kills":
                {
                    double sub = Math.Sin(2 * Math.PI * 50 * t) * Math.Exp(-1.5 * t);
                    double harm = 0.35 * Math.Sin(2 * Math.PI * 110 * t) +
                                  0.30 * Math.Sin(2 * Math.PI * 220 * t) +
                                  0.25 * Math.Sin(2 * Math.PI * 330 * t) +
                                  0.20 * Math.Sin(2 * Math.PI * 550 * t);
                    val = (0.5 * sub + 0.5 * harm) * Math.Exp(-1.8 * t);
                    break;
                }
                case "3_kills":
                {
                    double f = t < 0.3 ? 220 : t < 0.6 ? 277.18 : 329.63;
                    double env = Math.Exp(-2.0 * (t % 0.3));
                    val = (0.6 * Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(2 * Math.PI * f * 2 * t)) * env;
                    break;
                }
                case "4_kills":
                {
                    double env = Math.Exp(-2.0 * t);
                    val = (0.4 * Math.Sin(2 * Math.PI * 196 * t) +
                           0.3 * Math.Sin(2 * Math.PI * 246.94 * t) +
                           0.3 * Math.Sin(2 * Math.PI * 293.66 * t) +
                           0.2 * Math.Sin(2 * Math.PI * 392.0 * t)) * env;
                    break;
                }
                case "5_kills":
                {
                    double env = Math.Exp(-1.8 * t);
                    val = (0.35 * Math.Sin(2 * Math.PI * 261.63 * t) +
                           0.30 * Math.Sin(2 * Math.PI * 329.63 * t) +
                           0.25 * Math.Sin(2 * Math.PI * 392.00 * t) +
                           0.25 * Math.Sin(2 * Math.PI * 523.25 * t)) * env;
                    break;
                }
                case "6_kills":
                {
                    double mod = Math.Sin(2 * Math.PI * 8 * t);
                    double env = Math.Exp(-1.6 * t);
                    val = (0.4 * Math.Sin(2 * Math.PI * (174.61 + 20 * mod) * t) +
                           0.3 * Math.Sin(2 * Math.PI * 349.23 * t) +
                           0.3 * Math.Sin(2 * Math.PI * 523.25 * t)) * env;
                    break;
                }
                case "7_kills":
                {
                    double env = Math.Exp(-1.8 * t);
                    val = (0.4 * Math.Sin(2 * Math.PI * 587.33 * t) +
                           0.3 * Math.Sin(2 * Math.PI * 739.99 * t) +
                           0.3 * Math.Sin(2 * Math.PI * 880.00 * t)) * env;
                    break;
                }
                case "9_kills":
                {
                    double vib = 1.0 + 0.03 * Math.Sin(2 * Math.PI * 5.0 * t);
                    double env = Math.Exp(-1.4 * t);
                    val = (0.35 * Math.Sin(2 * Math.PI * 293.66 * vib * t) +
                           0.30 * Math.Sin(2 * Math.PI * 369.99 * vib * t) +
                           0.25 * Math.Sin(2 * Math.PI * 440.00 * vib * t) +
                           0.20 * Math.Sin(2 * Math.PI * 587.33 * vib * t)) * env;
                    break;
                }
                case "10_kills":
                {
                    double impact = Math.Sin(2 * Math.PI * 60 * t) * Math.Exp(-4.0 * t);
                    double lead = (0.35 * Math.Sin(2 * Math.PI * 440 * t) +
                                   0.30 * Math.Sin(2 * Math.PI * 554.37 * t) +
                                   0.30 * Math.Sin(2 * Math.PI * 659.25 * t) +
                                   0.25 * Math.Sin(2 * Math.PI * 880 * t)) * Math.Exp(-1.5 * t);
                    val = 0.5 * impact + 0.5 * lead;
                    break;
                }
                case "immortal":
                {
                    double drone = Math.Sin(2 * Math.PI * 110 * t);
                    double fanfare = 0.3 * Math.Sin(2 * Math.PI * 220 * t) +
                                     0.3 * Math.Sin(2 * Math.PI * 330 * t) +
                                     0.3 * Math.Sin(2 * Math.PI * 440 * t) +
                                     0.2 * Math.Sin(2 * Math.PI * 550 * t);
                    val = (0.4 * drone + 0.6 * fanfare) * Math.Exp(-1.2 * t);
                    break;
                }
                case "massacre":
                {
                    double env = Math.Exp(-3.0 * (t % 0.25));
                    val = (0.5 * Math.Sin(2 * Math.PI * 300 * t) + 0.4 * Math.Sin(2 * Math.PI * 450 * t)) * env;
                    break;
                }
                case "genocide":
                {
                    val = Math.Sin(2 * Math.PI * 75 * t) * Math.Exp(-2.0 * t);
                    break;
                }
                case "denied":
                {
                    double f = 440 - 150 * t;
                    val = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-2.5 * t);
                    break;
                }
                case "humiliation":
                {
                    double f = 220 + 300 * Math.Sin(2 * Math.PI * 4 * t);
                    val = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-2.0 * t);
                    break;
                }
                case "smackdown":
                {
                    val = Math.Sin(2 * Math.PI * 90 * t) * Math.Exp(-6.0 * t);
                    break;
                }
                case "rage_quit":
                {
                    double buzz = Math.Sin(2 * Math.PI * (800 - 600 * t) * t) * Math.Exp(-2.5 * t);
                    val = buzz;
                    break;
                }
                case "prepare_for_battle":
                {
                    double drum = Math.Sin(2 * Math.PI * 100 * t) * Math.Exp(-5.0 * (t % 0.25));
                    double horn = 0.4 * Math.Sin(2 * Math.PI * 220 * t) + 0.3 * Math.Sin(2 * Math.PI * 330 * t);
                    val = 0.5 * drum + 0.5 * horn * Math.Exp(-1.5 * t);
                    break;
                }
                case "startgame":
                {
                    val = Math.Sin(2 * Math.PI * (200 + 400 * t) * t) * Math.Exp(-1.5 * t);
                    break;
                }
                case "victory":
                {
                    double env = Math.Exp(-1.2 * t);
                    val = (0.35 * Math.Sin(2 * Math.PI * 523.25 * t) +
                           0.30 * Math.Sin(2 * Math.PI * 659.25 * t) +
                           0.25 * Math.Sin(2 * Math.PI * 783.99 * t) +
                           0.20 * Math.Sin(2 * Math.PI * 1046.50 * t)) * env;
                    break;
                }
                case "defeat":
                {
                    double env = Math.Exp(-1.5 * t);
                    double f = t < 0.33 ? 392 : t < 0.66 ? 349.23 : 261.63;
                    val = (0.6 * Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(2 * Math.PI * f * 1.5 * t)) * env;
                    break;
                }
                case "rival":
                {
                    double env = Math.Exp(-2.0 * t);
                    val = (0.5 * Math.Sin(2 * Math.PI * 350 * t) + 0.4 * Math.Sin(2 * Math.PI * 520 * t)) * env;
                    break;
                }
                case "payback":
                {
                    double env = Math.Exp(-2.2 * t);
                    val = (0.5 * Math.Sin(2 * Math.PI * 420 * t) + 0.4 * Math.Sin(2 * Math.PI * 630 * t)) * env;
                    break;
                }
                default:
                {
                    val = Math.Sin(2 * Math.PI * 440 * t) * Math.Exp(-2.0 * t);
                    break;
                }
            }

            val = Math.Max(-0.95, Math.Min(0.95, val));
            short sampleValue = (short)(val * 30000.0);
            writer.Write(sampleValue);
        }

        return ms.ToArray();
    }
}

