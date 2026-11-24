// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive verification test fixture confirming all 5 remediation items:
///     1. Genuine 64x64 icon.png on all 6 Themed Map Overhaul folders.
///     2. Genuine 18-byte binary Truevision TGA textures (64x64 32-bit BGRA) for HUD mods and Map Overhauls.
///     3. 16-Bit RIFF WAVE Audio Alignment (Subchunk2Size % 2 == 0) across all 59 WAV files.
///     4. 11 genuine binary .honmod (ZIP format) archive files directly in games/client/mods/.
///     5. Hardened ModManager and VFS injection verification.
/// </summary>
[Collection("ModdingDiskCollection")]
public sealed class RemediationVerificationTests : IDisposable
{
    private static readonly string[] Expected11ModFolderNames =
    [
        "vector_smart_cast_indicators",
        "rune_kongor_timers_hud",
        "ward_vision_radius_overlay",
        "neon_caldavar_theme",
        "frozen_caldavar_theme",
        "clean_tournament_arena",
        "cyberpunk_midwars",
        "frozen_midwars",
        "cyberpunk_devowars",
        "glados_ai_announcer",
        "retro_8bit_arcade_sfx"
    ];

    private readonly string _clientDir;
    private readonly string _modsDir;
    private readonly string _repoRoot;
    private readonly string _tempDir;

    public RemediationVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RemediationVerification_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_tempDir, "client");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));

        _repoRoot = KongorModRemediationInitializer.FindRepoRoot();
        _modsDir = Path.Combine(_repoRoot, "games", "client", "mods");

        KongorModRemediationInitializer.EnsureRemediationApplied();
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

    #region Item 1: Icon.png Verification (All 6 Map Themes & All 11 Mods)

    [Fact]
    public void All11Mods_IconPng_ExistsAndHasValid64x64PngStructure()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        foreach (string modName in Expected11ModFolderNames)
        {
            string iconPath = Path.Combine(_modsDir, modName, "icon.png");
            File.Exists(iconPath).Should().BeTrue($"Mod '{modName}' must contain persistent 'icon.png'");

            byte[] bytes = File.ReadAllBytes(iconPath);
            bytes.Length.Should().BeGreaterThanOrEqualTo(24, $"Icon '{iconPath}' is too small");

            for (int i = 0; i < 8; i++)
                bytes[i].Should().Be(pngSignature[i], $"Byte {i} of '{iconPath}' must match PNG signature");

            string ihdr = Encoding.ASCII.GetString(bytes, 12, 4);
            ihdr.Should().Be("IHDR", $"Icon '{iconPath}' must have IHDR chunk");

            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

            width.Should().Be(64, $"Icon '{iconPath}' width must be 64");
            height.Should().Be(64, $"Icon '{iconPath}' height must be 64");
        }
    }

    #endregion

    #region Item 3: 16-Bit RIFF WAVE Audio Alignment Verification

    [Fact]
    public void All59WavFilesAcrossSuite_WithValidArguments_PassStrict16BitPcmBlockAlignment()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string[] wavFiles = Directory.GetFiles(_modsDir, "*.wav", SearchOption.AllDirectories);
        wavFiles.Length.Should()
            .BeGreaterThanOrEqualTo(59, "ProjectKongor Mod Suite must contain at least 59 WAV files");

        foreach (string wavPath in wavFiles)
        {
            string fileName = Path.GetFileName(wavPath);
            byte[] fileBytes = File.ReadAllBytes(wavPath);

            fileBytes.Length.Should().BeGreaterThanOrEqualTo(44, $"Audio file '{fileName}' must be at least 44 bytes");

            // Total file length must be EVEN
            (fileBytes.Length % 2).Should().Be(0,
                $"WAV file '{fileName}' total byte length ({fileBytes.Length}) must be an even number for 16-bit PCM");

            using MemoryStream ms = new(fileBytes);
            using BinaryReader reader = new(ms, Encoding.ASCII);

            string riff = new(reader.ReadChars(4));
            riff.Should().Be("RIFF", $"WAV '{fileName}' must start with RIFF");

            int chunkSize = reader.ReadInt32();
            chunkSize.Should().Be(fileBytes.Length - 8, $"WAV '{fileName}' ChunkSize mismatch");

            string wave = new(reader.ReadChars(4));
            wave.Should().Be("WAVE", $"WAV '{fileName}' must have WAVE marker");

            string fmt = new(reader.ReadChars(4));
            fmt.Should().Be("fmt ", $"WAV '{fileName}' must have fmt marker");

            int subchunk1Size = reader.ReadInt32();
            subchunk1Size.Should().Be(16, $"WAV '{fileName}' Subchunk1Size must be 16");

            short audioFormat = reader.ReadInt16();
            audioFormat.Should().Be(1, $"WAV '{fileName}' AudioFormat must be 1 (PCM)");

            short channels = reader.ReadInt16();
            channels.Should().BeInRange(1, 2);

            int sampleRate = reader.ReadInt32();
            sampleRate.Should().BeInRange(8000, 48000);

            int byteRate = reader.ReadInt32();
            short blockAlign = reader.ReadInt16();
            short bitsPerSample = reader.ReadInt16();
            bitsPerSample.Should().Be(16, $"WAV '{fileName}' must be 16-bit audio");

            int expectedBlockAlign = channels * 2;
            blockAlign.Should().Be((short)expectedBlockAlign);

            int expectedByteRate = sampleRate * expectedBlockAlign;
            byteRate.Should().Be(expectedByteRate);

            string dataMarker = new(reader.ReadChars(4));
            dataMarker.Should().Be("data", $"WAV '{fileName}' must have data marker");

            int subchunk2Size = reader.ReadInt32();
            subchunk2Size.Should().Be(fileBytes.Length - 44, $"WAV '{fileName}' Subchunk2Size mismatch");

            // Subchunk2Size must be EVEN
            (subchunk2Size % 2).Should().Be(0,
                $"WAV file '{fileName}' Subchunk2Size ({subchunk2Size}) must be divisible by 2");
        }
    }

    #endregion

    #region Item 4: 11 .honmod (ZIP format) Archives Verification

    [Fact]
    public async Task All11HonmodArchives_WithValidArguments_ExistAsValidBinaryZipFiles()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        foreach (string modName in Expected11ModFolderNames)
        {
            string honmodPath = Path.Combine(_modsDir, modName + ".honmod");
            File.Exists(honmodPath).Should().BeTrue($"Archive '{honmodPath}' must exist as a persistent file");

            FileInfo info = new(honmodPath);
            info.Length.Should().BeGreaterThan(100, $"Archive '{honmodPath}' must be non-empty");

            // Open as ZIP archive and verify entries
            using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(honmodPath);
            pkg.IsDirectorySource.Should().BeFalse($"'{honmodPath}' must be a genuine binary ZIP package");
            pkg.Manifest.Name.Should().NotBeNullOrWhiteSpace();
            pkg.Manifest.Author.Should().Be("guccidonut");
            pkg.Manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
            pkg.Manifest.AppVersion.Should().Be(">=4.10.1");
            pkg.IconData.Should().NotBeNull();
            pkg.IconData!.Length.Should().BeGreaterThan(24);
            pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();

            // Verify all declared copyfile sources exist inside the archive
            foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
            {
                pkg.HasPayloadFile(copy.SourcePath).Should()
                    .BeTrue($"Archive '{honmodPath}' must contain payload file '{copy.SourcePath}'");
                byte[] payloadBytes = await pkg.GetPayloadFileAsync(copy.SourcePath);
                payloadBytes.Length.Should().BeGreaterThan(0);
            }
        }
    }

    #endregion

    #region Item 5: End-to-End VFS Injection & Rollback Verification

    [Fact]
    public async Task All11Mods_SimultaneousInjectionAndRollback_ZeroErrors()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        ModManager modManager = new();
        List<ModPackage> packages = [];

        foreach (string modName in Expected11ModFolderNames)
        {
            string honmodPath = Path.Combine(_modsDir, modName + ".honmod");
            ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(honmodPath);
            pkg.IsEnabled = true;
            packages.Add(pkg);
        }

        packages.Count.Should().Be(11);

        ApplyResult result = await modManager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = packages,
            Mode = InjectionMode.Vfs,
            StrictIntegrity = true
        });

        result.Success.Should()
            .BeTrue(
                $"ApplyModsAsync with StrictIntegrity must succeed with 0 errors. Errors: {string.Join("; ", result.Errors)}");
        result.Errors.Should().BeEmpty();
        result.AppliedMods.Should().HaveCount(11);

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, ".hotreload.trigger")).Should().BeTrue();

        // Rollback
        RollbackResult rollback = await modManager.RollbackAsync(_clientDir);
        rollback.Success.Should().BeTrue();
        Directory.Exists(overrideDir).Should().BeFalse();
    }

    #endregion

    #region Item 2: Real 18-Byte Binary TGA Textures Verification

    [Fact]
    public void CompetitiveHudMods_WhenInvoked_AllTgasAreRealBinaryNotTextStubs()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string[] hudMods =
        [
            "vector_smart_cast_indicators",
            "rune_kongor_timers_hud",
            "ward_vision_radius_overlay"
        ];

        foreach (string modName in hudMods)
        {
            string texDir = Path.Combine(_modsDir, modName, "textures");
            Directory.Exists(texDir).Should().BeTrue($"Directory '{texDir}' must exist");

            string[] tgas = Directory.GetFiles(texDir, "*.tga");
            tgas.Length.Should().BeGreaterThan(0, $"Mod '{modName}' must contain .tga textures in textures/");

            foreach (string tgaPath in tgas)
            {
                byte[] bytes = File.ReadAllBytes(tgaPath);
                bytes.Length.Should().BeGreaterThanOrEqualTo(18, $"TGA '{tgaPath}' must be at least 18 bytes");

                // Must NOT be ASCII text
                bytes[2].Should().Be(2, $"TGA '{tgaPath}' image type must be 2 (uncompressed true-color binary)");
                short width = BitConverter.ToInt16(bytes, 12);
                short height = BitConverter.ToInt16(bytes, 14);
                byte bpp = bytes[16];

                width.Should().Be(64, $"TGA '{tgaPath}' width must be 64");
                height.Should().Be(64, $"TGA '{tgaPath}' height must be 64");
                bpp.Should().Be(32, $"TGA '{tgaPath}' pixel depth must be 32 (BGRA)");
                bytes.Length.Should().Be(18 + 64 * 64 * 4, $"TGA '{tgaPath}' total binary size must be 16,402 bytes");
            }
        }
    }

    [Fact]
    public async Task ThemedMapOverhauls_OnQuery_AllDeclaredTexturesExistOnDiskAndAreBinary()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string[] mapMods =
        [
            "neon_caldavar_theme",
            "frozen_caldavar_theme",
            "clean_tournament_arena",
            "cyberpunk_midwars",
            "frozen_midwars",
            "cyberpunk_devowars"
        ];

        foreach (string modName in mapMods)
        {
            string modDir = Path.Combine(_modsDir, modName);
            string modXmlPath = Path.Combine(modDir, "mod.xml");
            File.Exists(modXmlPath).Should().BeTrue();

            string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
            ModManifest manifest = ModManifestParser.Parse(xml);

            List<CopyFileOperation> tgaCopies =
                [.. manifest.FileCopies.Where(c => c.SourcePath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))];

            tgaCopies.Count.Should().BeGreaterThan(0, $"Map overhaul '{modName}' must declare .tga texture copies");

            foreach (CopyFileOperation copy in tgaCopies)
            {
                string fullSourcePath = Path.Combine(modDir, copy.SourcePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(fullSourcePath).Should()
                    .BeTrue($"Referenced texture '{copy.SourcePath}' must exist at '{fullSourcePath}'");

                byte[] bytes = await File.ReadAllBytesAsync(fullSourcePath);
                bytes.Length.Should().BeGreaterThanOrEqualTo(18);

                bytes[2].Should().Be(2, $"TGA '{copy.SourcePath}' must be type 2 binary");
                short width = BitConverter.ToInt16(bytes, 12);
                short height = BitConverter.ToInt16(bytes, 14);
                byte bpp = bytes[16];

                width.Should().BeGreaterThan(0);
                height.Should().BeGreaterThan(0);
                bpp.Should().Be(32);
                bytes.Length.Should().Be(18 + width * height * 4);
            }
        }
    }

    #endregion
}
