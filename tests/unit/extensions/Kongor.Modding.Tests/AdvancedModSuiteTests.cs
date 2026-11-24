// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Conditions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Patching;
using Kongor.Modding.Resolution;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive, 4-Tier Automated Verification Test Suite for the 11 ProjectKongor Advanced Mod Suite Mods:
///     Pillar 1 (Competitive & Tactical HUD):
///     1. Vector Smart Cast & Max-Blink Indicators (vector_smart_cast_indicators.honmod)
///     2. Rune & Kongor Timers HUD (rune_kongor_timers_hud.honmod)
///     3. Ward Vision Radius Overlay (ward_vision_radius_overlay.honmod)
///     Pillar 2 (Themed Map Overhauls):
///     4. Neon Caldavar Theme (neon_caldavar_theme.honmod)
///     5. Frozen Caldavar Theme (frozen_caldavar_theme.honmod)
///     6. Clean Tournament Arena (clean_tournament_arena.honmod)
///     7. Cyberpunk Mid Wars (cyberpunk_midwars.honmod)
///     8. Frozen Mid Wars (frozen_midwars.honmod)
///     9. Cyberpunk Devo Wars (cyberpunk_devowars.honmod)
///     Pillar 3 (S-Tier Audio Packs):
///     10. GLaDOS AI Announcer (glados_ai_announcer.honmod)
///     11. Retro 8-Bit Arcade SFX (retro_8bit_arcade_sfx.honmod)
[CollectionDefinition("ModdingDiskCollection")]
public class ModdingDiskCollectionDefinition
{
}

[Collection("ModdingDiskCollection")]
public sealed class AdvancedModSuiteTests : IDisposable
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

    public AdvancedModSuiteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorAdvModSuite_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_tempDir, "client");
        _modsDir = Path.Combine(_tempDir, "mods");

        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
        Directory.CreateDirectory(_modsDir);

        _repoRoot = FindRepoRoot();
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
                // Suppress Windows filesystem file lock race conditions in temp cleanup
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

    #region Fixture Helpers and Synthesizers

    public sealed record WavHeaderInfo(
        bool IsValid,
        string? Error,
        int ChunkSize,
        int Subchunk1Size,
        short AudioFormat,
        short Channels,
        int SampleRate,
        int ByteRate,
        short BlockAlign,
        short BitsPerSample,
        int Subchunk2Size);

    public static WavHeaderInfo VerifyLinearPcmWavHeader(byte[] fileBytes, string fileName)
    {
        if (fileBytes.Length < 44)
            return new WavHeaderInfo(false,
                $"Audio file '{fileName}' is smaller than the required 44-byte RIFF header (Length: {fileBytes.Length} bytes).",
                0, 0, 0, 0, 0, 0, 0, 0, 0);

        using MemoryStream ms = new(fileBytes);
        using BinaryReader reader = new(ms, Encoding.ASCII);

        // 1. "RIFF" (0x00..0x03)
        string riff = new(reader.ReadChars(4));
        if (riff != "RIFF")
            return new WavHeaderInfo(false, $"File '{fileName}' does not start with RIFF marker (got '{riff}').", 0, 0,
                0, 0, 0, 0, 0, 0, 0);

        // 2. ChunkSize = FileLength - 8 (0x04..0x07)
        int chunkSize = reader.ReadInt32();
        if (chunkSize != fileBytes.Length - 8)
            return new WavHeaderInfo(false,
                $"File '{fileName}' chunk size mismatch. Expected {fileBytes.Length - 8}, got {chunkSize}.", chunkSize,
                0, 0, 0, 0, 0, 0, 0, 0);

        // 3. "WAVE" (0x08..0x0B)
        string wave = new(reader.ReadChars(4));
        if (wave != "WAVE")
            return new WavHeaderInfo(false, $"File '{fileName}' format marker is not 'WAVE' (got '{wave}').", chunkSize,
                0, 0, 0, 0, 0, 0, 0, 0);

        // 4. "fmt " (0x0C..0x0F)
        string fmt = new(reader.ReadChars(4));
        if (fmt != "fmt ")
            return new WavHeaderInfo(false, $"File '{fileName}' subchunk 1 is not 'fmt ' (got '{fmt}').", chunkSize, 0,
                0, 0, 0, 0, 0, 0, 0);

        // 5. Subchunk1Size = 16 for PCM (0x10..0x13)
        int subchunk1Size = reader.ReadInt32();
        if (subchunk1Size != 16)
            return new WavHeaderInfo(false, $"File '{fileName}' Subchunk1Size is not 16 for PCM (got {subchunk1Size}).",
                chunkSize, subchunk1Size, 0, 0, 0, 0, 0, 0, 0);

        // 6. AudioFormat = 1 (Linear PCM) (0x14..0x15)
        short audioFormat = reader.ReadInt16();
        if (audioFormat != 1)
            return new WavHeaderInfo(false, $"File '{fileName}' AudioFormat is not 1 (PCM) (got {audioFormat}).",
                chunkSize, subchunk1Size, audioFormat, 0, 0, 0, 0, 0, 0);

        // 7. NumChannels = 1 (Mono) or 2 (Stereo) (0x16..0x17)
        short channels = reader.ReadInt16();
        if (channels is < 1 or > 2)
            return new WavHeaderInfo(false,
                $"File '{fileName}' channel count must be 1 (Mono) or 2 (Stereo) (got {channels}).", chunkSize,
                subchunk1Size, audioFormat, channels, 0, 0, 0, 0, 0);

        // 8. SampleRate (0x18..0x1B)
        int sampleRate = reader.ReadInt32();
        if (sampleRate is < 8000 or > 96000)
            return new WavHeaderInfo(false,
                $"File '{fileName}' SampleRate {sampleRate} is out of standard range (8000-96000).", chunkSize,
                subchunk1Size, audioFormat, channels, sampleRate, 0, 0, 0, 0);

        // 9. ByteRate = SampleRate * NumChannels * (BitsPerSample / 8) (0x1C..0x1F)
        int byteRate = reader.ReadInt32();

        // 10. BlockAlign = NumChannels * (BitsPerSample / 8) (0x20..0x21)
        short blockAlign = reader.ReadInt16();

        // 11. BitsPerSample (0x22..0x23)
        short bitsPerSample = reader.ReadInt16();
        if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
            return new WavHeaderInfo(false,
                $"File '{fileName}' BitsPerSample {bitsPerSample} is not standard (8, 16, 24, 32).", chunkSize,
                subchunk1Size, audioFormat, channels, sampleRate, byteRate, blockAlign, bitsPerSample, 0);

        int expectedBlockAlign = channels * (bitsPerSample / 8);
        if (blockAlign != expectedBlockAlign)
            return new WavHeaderInfo(false,
                $"File '{fileName}' BlockAlign mismatch. Expected {expectedBlockAlign}, got {blockAlign}.", chunkSize,
                subchunk1Size, audioFormat, channels, sampleRate, byteRate, blockAlign, bitsPerSample, 0);

        int expectedByteRate = sampleRate * channels * (bitsPerSample / 8);
        if (byteRate != expectedByteRate)
            return new WavHeaderInfo(false,
                $"File '{fileName}' ByteRate mismatch. Expected {expectedByteRate}, got {byteRate}.", chunkSize,
                subchunk1Size, audioFormat, channels, sampleRate, byteRate, blockAlign, bitsPerSample, 0);

        // 12. "data" (0x24..0x27)
        string data = new(reader.ReadChars(4));
        if (data != "data")
            return new WavHeaderInfo(false, $"File '{fileName}' subchunk 2 is not 'data' (got '{data}').", chunkSize,
                subchunk1Size, audioFormat, channels, sampleRate, byteRate, blockAlign, bitsPerSample, 0);

        // 13. Subchunk2Size = FileLength - 44 (0x28..0x2B)
        int subchunk2Size = reader.ReadInt32();
        if (subchunk2Size != fileBytes.Length - 44)
            return new WavHeaderInfo(false,
                $"File '{fileName}' Subchunk2Size mismatch. Expected {fileBytes.Length - 44}, got {subchunk2Size}.",
                chunkSize, subchunk1Size, audioFormat, channels, sampleRate, byteRate, blockAlign, bitsPerSample,
                subchunk2Size);

        return new WavHeaderInfo(true, null, chunkSize, subchunk1Size, audioFormat, channels, sampleRate, byteRate,
            blockAlign, bitsPerSample, subchunk2Size);
    }

    public static byte[] CreateValidWavBytes(int sampleRate = 44100, short channels = 2, short bitsPerSample = 16,
        int sampleCount = 100)
    {
        int bytesPerSample = bitsPerSample / 8;
        int subChunk2Size = sampleCount * channels * bytesPerSample;
        int chunkSize = 36 + subChunk2Size;
        int byteRate = sampleRate * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);

        using MemoryStream stream = new(44 + subChunk2Size);
        using BinaryWriter writer = new(stream, Encoding.ASCII);

        writer.Write(['R', 'I', 'F', 'F']);
        writer.Write(chunkSize);
        writer.Write(['W', 'A', 'V', 'E']);

        writer.Write(['f', 'm', 't', ' ']);
        writer.Write(16); // Subchunk1Size
        writer.Write((short)1); // AudioFormat = 1 (PCM)
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        writer.Write(['d', 'a', 't', 'a']);
        writer.Write(subChunk2Size);

        byte[] samples = new byte[subChunk2Size];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 128);
        writer.Write(samples);
        writer.Flush();
        return stream.ToArray();
    }

    public static (bool IsValid, string? Error, short Width, short Height, byte PixelDepth) VerifyTgaHeader(
        byte[] bytes, string fileName)
    {
        if (bytes.Length < 18) return (false, $"TGA file '{fileName}' is smaller than 18 bytes.", 0, 0, 0);

        byte imageType = bytes[2];
        if (imageType is not 2 and not 3 and not 10)
            return (false,
                $"TGA file '{fileName}' image type {imageType} is not supported uncompressed true-color/grayscale.", 0,
                0, 0);

        short width = (short)(bytes[12] | (bytes[13] << 8));
        short height = (short)(bytes[14] | (bytes[15] << 8));
        byte pixelDepth = bytes[16];

        if (width <= 0 || height <= 0)
            return (false, $"TGA file '{fileName}' has invalid dimensions {width}x{height}.", width, height,
                pixelDepth);

        if (pixelDepth is not 24 and not 32 and not 8)
            return (false, $"TGA file '{fileName}' has invalid pixel depth {pixelDepth} bits.", width, height,
                pixelDepth);

        return (true, null, width, height, pixelDepth);
    }

    public static (bool IsValid, string? Error, int Width, int Height) VerifyPngHeader(byte[] bytes, string fileName)
    {
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24) return (false, $"PNG file '{fileName}' is smaller than 24 bytes.", 0, 0);

        for (int i = 0; i < 8; i++)
            if (bytes[i] != pngSignature[i])
                return (false, $"PNG file '{fileName}' has invalid magic signature.", 0, 0);

        string ihdrChunk = Encoding.ASCII.GetString(bytes, 12, 4);
        if (ihdrChunk != "IHDR") return (false, $"PNG file '{fileName}' missing IHDR chunk.", 0, 0);

        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

        if (width <= 0 || height <= 0)
            return (false, $"PNG file '{fileName}' has invalid dimensions {width}x{height}.", width, height);

        return (true, null, width, height);
    }

    public static byte[] CreateSynthesizedTgaBytes(short width, short height, byte b = 255, byte g = 0, byte r = 255,
        byte a = 255)
    {
        int pixelCount = width * height;
        byte[] buffer = new byte[18 + pixelCount * 4];

        buffer[2] = 2; // Uncompressed true-color image
        buffer[12] = (byte)(width & 0xFF);
        buffer[13] = (byte)((width >> 8) & 0xFF);
        buffer[14] = (byte)(height & 0xFF);
        buffer[15] = (byte)((height >> 8) & 0xFF);
        buffer[16] = 32; // 32-bit BGRA
        buffer[17] = 8; // 8 bits alpha

        for (int i = 0; i < pixelCount; i++)
        {
            int offset = 18 + i * 4;
            buffer[offset + 0] = b;
            buffer[offset + 1] = g;
            buffer[offset + 2] = r;
            buffer[offset + 3] = a;
        }

        return buffer;
    }

    public static byte[] CreateSynthesizedPngBytes()
    {
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x40,
            0x00, 0x00, 0x00, 0x40,
            0x08, 0x06, 0x00, 0x00, 0x00,
            0xE2, 0x98, 0x61, 0x99,
            0x00, 0x00, 0x00, 0x0A,
            0x49, 0x44, 0x41, 0x54,
            0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
            0x0D, 0x0A, 0x2D, 0xB4,
            0x00, 0x00, 0x00, 0x00,
            0x49, 0x45, 0x4E, 0x44,
            0xAE, 0x42, 0x60, 0x82
        ];
    }

    #endregion

    #region Tier 1: Feature Coverage & Manifest Ingestion (12 Tests)

    [Fact]
    public async Task VectorSmartCastIndicators_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "vector_smart_cast_indicators");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Vector Smart Cast & Max-Blink Indicators");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Bombardier").And.Contain("Rally").And.Contain("Zephyr").And
            .Contain("Chipper").And.Contain("1200-range");

        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileCopies.Select(c => c.TargetName).Should().Contain(
        [
            "ui/vector_smart_cast.package",
            "ui/scripts/vector_smart_cast.lua",
            "materials/vector_arrow.material",
            "materials/blink_ring_1200.material",
            "materials/reticle_ground.material",
            "textures/vector_arrow.tga",
            "textures/blink_ring_1200.tga",
            "textures/reticle_ground.tga"
        ]);

        manifest.FileEdits.Should().HaveCount(2);
        manifest.FileEdits.Select(e => e.TargetName).Should().Contain(
        [
            "ui/game_apex.interface",
            "ui/game_legacy.interface"
        ]);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0, $"Source file '{copy.SourcePath}' must not be empty");
        }
    }

    [Fact]
    public async Task RuneKongorTimersHud_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "rune_kongor_timers_hud");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Rune & Kongor Timers HUD");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("2:00 interval").And.Contain("8:00\u201311:00");

        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileCopies.Select(c => c.TargetName).Should().Contain(
        [
            "ui/rune_kongor_timers.package",
            "ui/scripts/rune_kongor_timers.lua",
            "materials/rune_timer_icon.material",
            "materials/kongor_timer_icon.material",
            "textures/rune_timer_icon.tga",
            "textures/kongor_timer_icon.tga",
            "sounds/rune_spawn_warning.wav",
            "sounds/kongor_window_open.wav"
        ]);

        manifest.StringtableEdits.Should().HaveCount(2);
        manifest.FileEdits.Should().HaveCount(2);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task WardVisionRadiusOverlay_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "ward_vision_radius_overlay");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Ward Vision Radius Overlay");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Observer Wards (1600").And.Contain("Sentry Wards (850");

        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileEdits.Should().HaveCount(2);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task NeonCaldavarTheme_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "neon_caldavar_theme");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Neon Caldavar Theme");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Maps");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Cyberpunk Caldavar").And.Contain("synthwave neon river");

        manifest.FileCopies.Should().HaveCount(39);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task FrozenCaldavarTheme_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "frozen_caldavar_theme");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Frozen Caldavar Theme");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Maps");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Winter Caldavar").And.Contain("crystalline translucent ice");

        manifest.FileCopies.Should().HaveCount(38);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task CleanTournamentArena_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "clean_tournament_arena");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Clean Tournament Arena");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Maps");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Esports-grade tournament").And
            .Contain("desaturated, low-noise flat matte");

        manifest.FileCopies.Should().HaveCount(35);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task CyberpunkMidwars_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "cyberpunk_midwars");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Cyberpunk Mid Wars");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Maps");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("neon underglow center bridge");

        manifest.FileCopies.Should().HaveCount(15);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task FrozenMidwars_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "frozen_midwars");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Frozen Mid Wars");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Maps");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("solid glacial ice sheet river");

        manifest.FileCopies.Should().HaveCount(15);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task CyberpunkDevowars_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "cyberpunk_devowars");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Cyberpunk Devo Wars");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Maps");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Devourer ability 1").And.Contain("laser tether hooks").And
            .Contain("neon abyss");

        manifest.FileCopies.Should().HaveCount(18);

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string srcPath = Path.Combine(modDir, copy.SourcePath);
            File.Exists(srcPath).Should().BeTrue($"Source file '{copy.SourcePath}' must exist on disk");
            FileInfo info = new(srcPath);
            info.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task GladosAiAnnouncer_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "glados_ai_announcer");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("GLaDOS AI Announcer");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Audio");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Aperture Science GLaDOS AI Announcer");

        // 28 dedicated voice lines + 28 fallback copies = 56 copy operations
        manifest.FileCopies.Should().HaveCount(56);
        manifest.StringtableEdits.Should().HaveCount(2);

        string soundsDir = Path.Combine(modDir, "sounds");
        Directory.Exists(soundsDir).Should().BeTrue();
        string[] wavFiles = Directory.GetFiles(soundsDir, "*.wav");
        wavFiles.Length.Should().Be(28, "GLaDOS Announcer must contain 28 voice line WAV files");

        foreach (string wavPath in wavFiles)
        {
            FileInfo info = new(wavPath);
            info.Length.Should()
                .BeGreaterThanOrEqualTo(44, $"WAV file '{Path.GetFileName(wavPath)}' must be at least 44 bytes");
        }
    }

    [Fact]
    public async Task Retro8BitArcadeSfx_ManifestParsingAndPayloadVerification_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "retro_8bit_arcade_sfx");
        string modXmlPath = Path.Combine(modDir, "mod.xml");
        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Retro 8-Bit Arcade SFX");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.Category.Should().Be("Audio");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");
        manifest.Description.Should().Contain("Vintage 8-bit chiptune").And.Contain("arcade");

        // 29 arcade SFX + 3 UI fallback copies = 32 copy operations
        manifest.FileCopies.Should().HaveCount(32);
        manifest.StringtableEdits.Should().HaveCount(2);

        string soundsDir = Path.Combine(modDir, "sounds");
        Directory.Exists(soundsDir).Should().BeTrue();
        string[] wavFiles = Directory.GetFiles(soundsDir, "*.wav");
        wavFiles.Length.Should().Be(29, "Retro 8-bit SFX must contain 29 chiptune WAV files");

        foreach (string wavPath in wavFiles)
        {
            FileInfo info = new(wavPath);
            info.Length.Should()
                .BeGreaterThanOrEqualTo(44, $"WAV file '{Path.GetFileName(wavPath)}' must be at least 44 bytes");
        }
    }

    [Fact]
    public async Task All11AdvancedMods_UniversalMetadataIntegrityAndStandards_ConformsToContract()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");

        foreach (string modFolderName in Expected11ModFolderNames)
        {
            string modDir = Path.Combine(modsBase, modFolderName);
            Directory.Exists(modDir).Should().BeTrue($"Mod directory '{modDir}' must exist");

            string modXml = Path.Combine(modDir, "mod.xml");
            File.Exists(modXml).Should().BeTrue($"Manifest '{modXml}' must exist");

            string xml = await File.ReadAllTextAsync(modXml, Encoding.UTF8);
            ModManifest manifest = ModManifestParser.Parse(xml);

            manifest.Author.Should().Be("guccidonut", $"Mod in '{modFolderName}' must be authored by 'guccidonut'");
            manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion, $"Mod in '{modFolderName}' must have version '{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}'");
            manifest.Date.Should().NotBeNullOrWhiteSpace($"Mod in '{modFolderName}' must define date");
            manifest.AppVersion.Should().NotBeNullOrWhiteSpace($"Mod in '{modFolderName}' must define AppVersion");
            manifest.AppVersion.Should().Be(">=4.10.1");

            manifest.Category.Should().BeOneOf("UI & HUD", "Maps", "Audio");
            manifest.Description.Should().NotBeNullOrWhiteSpace();

            string iconPath = Path.Combine(modDir, manifest.Icon ?? "icon.png");
            File.Exists(iconPath).Should().BeTrue($"Icon file '{iconPath}' must exist");
        }
    }

    #endregion

    #region Boundary & Audio RIFF / Asset Validation (10 Tests)

    [Fact]
    public void Audio_GladosAiAnnouncer_All28WavFiles_Strict44ByteLinearPcmHeaders()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string soundsDir = Path.Combine(_repoRoot, "games", "client", "mods", "glados_ai_announcer", "sounds");
        Directory.Exists(soundsDir).Should().BeTrue();

        string[] wavFiles = Directory.GetFiles(soundsDir, "*.wav");
        wavFiles.Length.Should().Be(28);

        foreach (string wavPath in wavFiles)
        {
            string fileName = Path.GetFileName(wavPath);
            byte[] fileBytes = File.ReadAllBytes(wavPath);

            WavHeaderInfo result = VerifyLinearPcmWavHeader(fileBytes, fileName);
            result.IsValid.Should().BeTrue($"WAV '{fileName}' failed validation: {result.Error}");
            result.AudioFormat.Should().Be(1);
            result.Channels.Should().BeInRange(1, 2);
            result.SampleRate.Should().BeInRange(8000, 48000);
            result.BitsPerSample.Should().Be(16);
            result.Subchunk1Size.Should().Be(16);
            result.Subchunk2Size.Should().Be(fileBytes.Length - 44);
        }
    }

    [Fact]
    public void Audio_Retro8BitArcadeSfx_All29WavFiles_Strict44ByteLinearPcmHeaders()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string soundsDir = Path.Combine(_repoRoot, "games", "client", "mods", "retro_8bit_arcade_sfx", "sounds");
        Directory.Exists(soundsDir).Should().BeTrue();

        string[] wavFiles = Directory.GetFiles(soundsDir, "*.wav");
        wavFiles.Length.Should().Be(29);

        foreach (string wavPath in wavFiles)
        {
            string fileName = Path.GetFileName(wavPath);
            byte[] fileBytes = File.ReadAllBytes(wavPath);

            WavHeaderInfo result = VerifyLinearPcmWavHeader(fileBytes, fileName);
            result.IsValid.Should().BeTrue($"WAV '{fileName}' failed validation: {result.Error}");
            result.AudioFormat.Should().Be(1);
            result.Channels.Should().BeInRange(1, 2);
            result.SampleRate.Should().BeInRange(8000, 48000);
            result.BitsPerSample.Should().Be(16);
            result.Subchunk1Size.Should().Be(16);
            result.Subchunk2Size.Should().Be(fileBytes.Length - 44);
        }
    }

    [Fact]
    public void Audio_RuneKongorTimers_AllWavFiles_Strict44ByteLinearPcmHeaders()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string soundsDir = Path.Combine(_repoRoot, "games", "client", "mods", "rune_kongor_timers_hud", "sounds");
        Directory.Exists(soundsDir).Should().BeTrue();

        string[] wavFiles = Directory.GetFiles(soundsDir, "*.wav");
        wavFiles.Length.Should().Be(2);

        foreach (string wavPath in wavFiles)
        {
            string fileName = Path.GetFileName(wavPath);
            byte[] fileBytes = File.ReadAllBytes(wavPath);

            WavHeaderInfo result = VerifyLinearPcmWavHeader(fileBytes, fileName);
            result.IsValid.Should().BeTrue($"WAV '{fileName}' failed validation: {result.Error}");
            result.AudioFormat.Should().Be(1);
            result.Channels.Should().BeInRange(1, 2);
            result.BitsPerSample.Should().Be(16);
        }
    }

    [Theory]
    [InlineData(11025, (short)1, (short)16, 200)]
    [InlineData(22050, (short)2, (short)16, 400)]
    [InlineData(44100, (short)2, (short)16, 1000)]
    [InlineData(48000, (short)1, (short)8, 500)]
    public void Audio_MathematicalRIFFIntegrity_RoundTripSynthesizer(int sampleRate, short channels,
        short bitsPerSample, int sampleCount)
    {
        byte[] wavBytes = CreateValidWavBytes(sampleRate, channels, bitsPerSample, sampleCount);
        wavBytes.Length.Should().Be(44 + sampleCount * channels * (bitsPerSample / 8));

        WavHeaderInfo result = VerifyLinearPcmWavHeader(wavBytes, "synthesized_test.wav");
        result.IsValid.Should().BeTrue(result.Error);
        result.SampleRate.Should().Be(sampleRate);
        result.Channels.Should().Be(channels);
        result.BitsPerSample.Should().Be(bitsPerSample);
        result.Subchunk2Size.Should().Be(sampleCount * channels * (bitsPerSample / 8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(36)]
    [InlineData(43)]
    public void Audio_Adversarial_TruncatedHeaders_RejectedByValidator(int truncatedLength)
    {
        byte[] validWav = CreateValidWavBytes(44100, 2, 16, 50);
        byte[] truncated = new byte[truncatedLength];
        Array.Copy(validWav, truncated, truncatedLength);

        WavHeaderInfo result = VerifyLinearPcmWavHeader(truncated, "truncated_test.wav");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("smaller than the required 44-byte RIFF header");
    }

    [Fact]
    public void Audio_Adversarial_CorruptedFields_RejectedByValidator()
    {
        // 1. Invalid RIFF marker
        byte[] badRiff = CreateValidWavBytes(44100, 2, 16, 50);
        badRiff[0] = (byte)'R';
        badRiff[1] = (byte)'I';
        badRiff[2] = (byte)'F';
        badRiff[3] = (byte)'X';
        VerifyLinearPcmWavHeader(badRiff, "bad_riff.wav").IsValid.Should().BeFalse();

        // 2. Invalid WAVE format
        byte[] badWave = CreateValidWavBytes(44100, 2, 16, 50);
        badWave[8] = (byte)'A';
        badWave[9] = (byte)'I';
        badWave[10] = (byte)'F';
        badWave[11] = (byte)'F';
        VerifyLinearPcmWavHeader(badWave, "bad_wave.wav").IsValid.Should().BeFalse();

        // 3. Non-PCM audio format (e.g. IEEE float format = 3)
        byte[] nonPcm = CreateValidWavBytes(44100, 2, 16, 50);
        nonPcm[20] = 3;
        nonPcm[21] = 0;
        VerifyLinearPcmWavHeader(nonPcm, "non_pcm.wav").IsValid.Should().BeFalse();

        // 4. Invalid Subchunk1Size (e.g. 18 bytes instead of 16)
        byte[] badFmtSize = CreateValidWavBytes(44100, 2, 16, 50);
        badFmtSize[16] = 18;
        VerifyLinearPcmWavHeader(badFmtSize, "bad_fmt_size.wav").IsValid.Should().BeFalse();

        // 5. Chunk size mismatch
        byte[] badChunkSize = CreateValidWavBytes(44100, 2, 16, 50);
        badChunkSize[4] = 0;
        badChunkSize[5] = 0;
        badChunkSize[6] = 0;
        badChunkSize[7] = 0;
        VerifyLinearPcmWavHeader(badChunkSize, "bad_chunk_size.wav").IsValid.Should().BeFalse();

        // 6. ByteRate mismatch
        byte[] badByteRate = CreateValidWavBytes(44100, 2, 16, 50);
        badByteRate[28] = 0xFF;
        badByteRate[29] = 0xFF;
        VerifyLinearPcmWavHeader(badByteRate, "bad_byterate.wav").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Image_All11Mods_IconPng_ValidPngSignatureAndChunks()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");

        foreach (string modFolder in Expected11ModFolderNames)
        {
            string iconPath = Path.Combine(modsBase, modFolder, "icon.png");
            File.Exists(iconPath).Should().BeTrue($"Icon '{iconPath}' must exist");

            byte[] iconBytes = File.ReadAllBytes(iconPath);
            (bool isValid, string? error, int width, int height) = VerifyPngHeader(iconBytes, $"{modFolder}/icon.png");
            isValid.Should().BeTrue($"PNG '{modFolder}/icon.png' invalid: {error}");
            width.Should().BeGreaterThan(0);
            height.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Image_ThemedMapsAndHud_AllTgaTextures_Valid18ByteHeaders()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");
        List<string> tgaFiles = [];

        foreach (string modFolder in Expected11ModFolderNames)
        {
            string modDir = Path.Combine(modsBase, modFolder);
            string[] found = Directory.GetFiles(modDir, "*.tga", SearchOption.AllDirectories);
            tgaFiles.AddRange(found);
        }

        tgaFiles.Count.Should().BeGreaterThanOrEqualTo(30, "Advanced Mod Suite must contain 30+ TGA texture assets");

        foreach (string tgaPath in tgaFiles)
        {
            string rel = Path.GetRelativePath(modsBase, tgaPath);
            byte[] tgaBytes = File.ReadAllBytes(tgaPath);

            (bool isValid, string? error, short width, short height, byte depth) = VerifyTgaHeader(tgaBytes, rel);
            isValid.Should().BeTrue($"TGA '{rel}' failed validation: {error}");
            width.Should().BeGreaterThan(0);
            height.Should().BeGreaterThan(0);
            depth.Should().BeOneOf(24, 32);
        }
    }

    [Fact]
    public void Image_Adversarial_CorruptedPngAndTgaHeaders_RejectedByValidator()
    {
        // Truncated PNG
        byte[] badPng = [0x89, 0x50, 0x4E];
        VerifyPngHeader(badPng, "truncated.png").IsValid.Should().BeFalse();

        // Invalid PNG Magic
        byte[] wrongMagicPng = CreateSynthesizedPngBytes();
        wrongMagicPng[0] = 0x00;
        VerifyPngHeader(wrongMagicPng, "wrong_magic.png").IsValid.Should().BeFalse();

        // Truncated TGA
        byte[] badTga = [0x00, 0x00, 0x02, 0x00];
        VerifyTgaHeader(badTga, "truncated.tga").IsValid.Should().BeFalse();

        // Unsupported TGA Depth
        byte[] invalidDepthTga = CreateSynthesizedTgaBytes(64, 64);
        invalidDepthTga[16] = 15; // 15-bit unsupported
        VerifyTgaHeader(invalidDepthTga, "invalid_depth.tga").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Manifest_Adversarial_CorruptedXmlAndMissingAttributes_HandledGracefully()
    {
        // 1. Empty XML throws ArgumentException
        Action actEmpty = () => ModManifestParser.Parse("");
        actEmpty.Should().Throw<ArgumentException>();

        // 2. Malformed XML tags throw Exception
        string malformed = "<modification name='BadMod'><unclosedTag>";
        Action actMalformed = () => ModManifestParser.Parse(malformed);
        actMalformed.Should().Throw<Exception>();

        // 3. Invalid root element throws FormatException
        string badRoot = "<wrongroot name='Test'></wrongroot>";
        Action actBadRoot = () => ModManifestParser.Parse(badRoot);
        actBadRoot.Should().Throw<FormatException>();

        // 4. Default fallbacks for minimal manifest
        string minimal = "<modification />";
        ModManifest minimalManifest = ModManifestParser.Parse(minimal);
        minimalManifest.Name.Should().Be("Unnamed Mod");
        minimalManifest.Version.Should().Be("1.0.0");
    }

    #endregion

    #region DAG Topological Sort & Conflict Resolution (8 Tests)

    [Fact]
    public async Task DAG_All11AdvancedMods_BuildGraphAndSort_DeterministicAndCycleFree()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");
        List<ModPackage> packages = [];

        foreach (string modFolder in Expected11ModFolderNames)
        {
            string dir = Path.Combine(modsBase, modFolder);
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(dir);
            packages.Add(pkg);
        }

        packages.Should().HaveCount(11);

        DependencyGraph graph = DependencyGraph.Build(packages);
        graph.Nodes.Should().HaveCount(11);

        SortResult sortResult = TopologicalSorter.ResolveAndSort(packages);
        sortResult.Success.Should().BeTrue();
        sortResult.ExecutionPlan.Should().HaveCount(11);
        sortResult.CycleReports.Should().BeEmpty();
        sortResult.ValidationErrors.Should().BeEmpty();

        // Deterministic check: sorting same collection twice must produce identical sequence
        SortResult secondSort = TopologicalSorter.ResolveAndSort(packages.OrderByDescending(p => p.Manifest.Name));
        secondSort.Success.Should().BeTrue();
        List<string> firstOrder = [.. sortResult.ExecutionPlan.Select(p => p.Manifest.Name)];
        List<string> secondOrder = [.. secondSort.ExecutionPlan.Select(p => p.Manifest.Name)];
        secondOrder.Should().Equal(firstOrder);
    }

    [Fact]
    public void DAG_OrderingRules_ApplyBeforeAndApplyAfter_GuaranteesStrictExecutionPlan()
    {
        ModManifest modA = new()
        {
            Name = "ModA",
            Version = "1.0.0",
            ApplyBefore = [new ModOrderingRule("ModB")]
        };

        ModManifest modB = new()
        {
            Name = "ModB",
            Version = "1.0.0"
        };

        ModManifest modC = new()
        {
            Name = "ModC",
            Version = "1.0.0",
            ApplyAfter = [new ModOrderingRule("ModB")]
        };

        ModPackage pkgA = new() { Manifest = modA, FilePath = "modA" };
        ModPackage pkgB = new() { Manifest = modB, FilePath = "modB" };
        ModPackage pkgC = new() { Manifest = modC, FilePath = "modC" };

        SortResult result = TopologicalSorter.ResolveAndSort([pkgC, pkgB, pkgA]);
        result.Success.Should().BeTrue();
        List<string> names = [.. result.ExecutionPlan.Select(p => p.Manifest.Name)];

        int idxA = names.IndexOf("ModA");
        int idxB = names.IndexOf("ModB");
        int idxC = names.IndexOf("ModC");

        idxA.Should().BeLessThan(idxB, "ModA must apply before ModB");
        idxB.Should().BeLessThan(idxC, "ModB must apply before ModC");
    }

    [Fact]
    public void DAG_MultiHopRequirementChains_ResolvedTopologically()
    {
        ModManifest core = new() { Name = "CoreFramework", Version = "1.0.0" };
        ModManifest addon1 = new()
        {
            Name = "Addon1",
            Version = "1.0.0",
            Requirements = [new ModRequirement("CoreFramework")]
        };
        ModManifest addon2 = new()
        {
            Name = "Addon2",
            Version = "1.0.0",
            Requirements = [new ModRequirement("Addon1")]
        };
        ModManifest ext = new()
        {
            Name = "Extension",
            Version = "1.0.0",
            Requirements = [new ModRequirement("Addon2")]
        };

        ModPackage pCore = new() { Manifest = core, FilePath = "core" };
        ModPackage pAddon1 = new() { Manifest = addon1, FilePath = "addon1" };
        ModPackage pAddon2 = new() { Manifest = addon2, FilePath = "addon2" };
        ModPackage pExt = new() { Manifest = ext, FilePath = "ext" };

        SortResult result = TopologicalSorter.ResolveAndSort([pExt, pCore, pAddon2, pAddon1]);
        result.Success.Should().BeTrue();
        List<string> names = [.. result.ExecutionPlan.Select(p => p.Manifest.Name)];

        names.Should().Equal("CoreFramework", "Addon1", "Addon2", "Extension");
    }

    [Fact]
    public void DAG_CyclicDependency_TarjanDetector_IdentifiesAndReportsCycle()
    {
        ModManifest modA = new()
        {
            Name = "CycleModA",
            Version = "1.0.0",
            Requirements = [new ModRequirement("CycleModB")]
        };
        ModManifest modB = new()
        {
            Name = "CycleModB",
            Version = "1.0.0",
            Requirements = [new ModRequirement("CycleModC")]
        };
        ModManifest modC = new()
        {
            Name = "CycleModC",
            Version = "1.0.0",
            Requirements = [new ModRequirement("CycleModA")]
        };

        ModPackage pkgA = new() { Manifest = modA, FilePath = "a" };
        ModPackage pkgB = new() { Manifest = modB, FilePath = "b" };
        ModPackage pkgC = new() { Manifest = modC, FilePath = "c" };

        SortResult result = TopologicalSorter.ResolveAndSort([pkgA, pkgB, pkgC]);
        result.Success.Should().BeFalse();
        result.CycleReports.Should().NotBeEmpty();
        result.CycleReports.Any(c => c.Contains("Cyclic dependency detected")).Should().BeTrue();
    }

    [Fact]
    public void DAG_MissingRequirement_FailsValidationWithClearError()
    {
        ModManifest mod = new()
        {
            Name = "DependentMod",
            Version = "1.0.0",
            Requirements = [new ModRequirement("NonExistentCoreMod")]
        };

        ModPackage pkg = new() { Manifest = mod, FilePath = "dep" };
        SortResult result = TopologicalSorter.ResolveAndSort([pkg]);

        result.Success.Should().BeFalse();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("requires missing mod 'NonExistentCoreMod'"));
    }

    [Fact]
    public void DAG_IncompatibleMod_FailsValidationWithClearError()
    {
        ModManifest modA = new()
        {
            Name = "ModA",
            Version = "1.0.0",
            Incompatibilities = [new ModIncompatibility("ModB")]
        };
        ModManifest modB = new()
        {
            Name = "ModB",
            Version = "1.0.0"
        };

        ModPackage pkgA = new() { Manifest = modA, FilePath = "a" };
        ModPackage pkgB = new() { Manifest = modB, FilePath = "b" };

        SortResult result = TopologicalSorter.ResolveAndSort([pkgA, pkgB]);
        result.Success.Should().BeFalse();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("is incompatible with enabled mod 'ModB'"));
    }

    [Fact]
    public void DAG_VersionRangeExpression_EvaluatesComplexSemVerConstraints()
    {
        VersionRangeExpression expr = VersionRangeExpression.Parse(">=4.10.1");
        expr.IsSatisfiedBy("4.10.1").Should().BeTrue();
        expr.IsSatisfiedBy("4.10.5").Should().BeTrue();
        expr.IsSatisfiedBy("5.0.0").Should().BeTrue();
        expr.IsSatisfiedBy("4.10.0").Should().BeFalse();
        expr.IsSatisfiedBy("4.9.9").Should().BeFalse();

        VersionRangeExpression wildcard = VersionRangeExpression.Parse("4.10.*");
        wildcard.IsSatisfiedBy("4.10.0").Should().BeTrue();
        wildcard.IsSatisfiedBy("4.10.99").Should().BeTrue();
        wildcard.IsSatisfiedBy("4.11.0").Should().BeFalse();
    }

    [Fact]
    public async Task DAG_FullEcosystem_11AdvancedMods_Plus_CommunityMods_ResolvesCleanly()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        ModManager modManager = new();
        string modsDir = Path.Combine(_repoRoot, "games", "client", "mods");

        IReadOnlyList<ModPackage> allMods = await modManager.DiscoverModsAsync(modsDir);
        allMods.Should().HaveCountGreaterThanOrEqualTo(11);

        SortResult sortResult = TopologicalSorter.ResolveAndSort(allMods);
        sortResult.Success.Should()
            .BeTrue(
                $"All discovered mods must resolve cleanly. Errors: {string.Join(", ", sortResult.ValidationErrors)}");
        sortResult.ExecutionPlan.Count.Should().Be(allMods.Count);
    }

    #endregion

    #region Layered VFS Injection & Rollback (8 Tests)

    [Fact]
    public async Task VFS_VectorSmartCastIndicators_InjectAndVerifyOverrideFiles()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "vector_smart_cast_indicators");
        ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);

        Dictionary<string, byte[]> prepared = [];
        foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
        {
            string fullPath = Path.Combine(modDir, copy.SourcePath);
            prepared[copy.TargetName] = await File.ReadAllBytesAsync(fullPath);
        }

        JournalEntry journal = await LayeredVfsInjector.InjectAsync(_clientDir, prepared, [pkg]);

        journal.Should().NotBeNull();
        journal.Mode.Should().Be(InjectionMode.Vfs);
        journal.AppliedMods.Should().ContainSingle(m => m.Name == "Vector Smart Cast & Max-Blink Indicators");

        // Verify loose file existence in override/
        string overrideDir = Path.Combine(_clientDir, "override");
        File.Exists(Path.Combine(overrideDir, "ui", "vector_smart_cast.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "vector_smart_cast.lua")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "materials", "vector_arrow.material")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "textures", "vector_arrow.tga")).Should().BeTrue();

        // Verify .hotreload.trigger
        string triggerPath = Path.Combine(overrideDir, ".hotreload.trigger");
        File.Exists(triggerPath).Should().BeTrue();
        string triggerContent = await File.ReadAllTextAsync(triggerPath);
        triggerContent.Should().StartWith("MOD_RELOAD_");

        // Verify rollback journal file
        string journalFile = RollbackJournal.GetJournalPath(_clientDir);
        File.Exists(journalFile).Should().BeTrue();
    }

    [Fact]
    public async Task VFS_GladosAiAnnouncer_InjectAndVerifyAudioAssetsInOverride()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "glados_ai_announcer");
        ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);

        Dictionary<string, byte[]> prepared = [];
        foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
        {
            string fullPath = Path.Combine(modDir, copy.SourcePath);
            prepared[copy.TargetName] = await File.ReadAllBytesAsync(fullPath);
        }

        JournalEntry journal = await LayeredVfsInjector.InjectAsync(_clientDir, prepared, [pkg]);
        journal.CreatedFiles.Should().HaveCountGreaterThanOrEqualTo(56);

        string overrideDir = Path.Combine(_clientDir, "override");
        string testAudio = Path.Combine(overrideDir, "shared", "sounds", "announcer", "glados", "first_blood.wav");
        File.Exists(testAudio).Should().BeTrue();

        byte[] audioBytes = await File.ReadAllBytesAsync(testAudio);
        WavHeaderInfo header = VerifyLinearPcmWavHeader(audioBytes, "injected_glados_first_blood.wav");
        header.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VFS_Retro8BitArcadeSfx_InjectAndVerifyAudioAssetsInOverride()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "retro_8bit_arcade_sfx");
        ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);

        Dictionary<string, byte[]> prepared = [];
        foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
        {
            string fullPath = Path.Combine(modDir, copy.SourcePath);
            prepared[copy.TargetName] = await File.ReadAllBytesAsync(fullPath);
        }

        JournalEntry journal = await LayeredVfsInjector.InjectAsync(_clientDir, prepared, [pkg]);
        journal.CreatedFiles.Should().HaveCountGreaterThanOrEqualTo(32);

        string overrideDir = Path.Combine(_clientDir, "override");
        string coinSfx = Path.Combine(overrideDir, "shared", "sounds", "retro", "gold_pickup.wav");
        File.Exists(coinSfx).Should().BeTrue();

        byte[] sfxBytes = await File.ReadAllBytesAsync(coinSfx);
        WavHeaderInfo header = VerifyLinearPcmWavHeader(sfxBytes, "injected_gold_pickup.wav");
        header.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task VFS_ThemedMaps_LayeredInjection_OverwritesTerrainAndWater()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modDir = Path.Combine(_repoRoot, "games", "client", "mods", "neon_caldavar_theme");
        ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);

        Dictionary<string, byte[]> prepared = [];
        foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
        {
            string fullPath = Path.Combine(modDir, copy.SourcePath);
            prepared[copy.TargetName] = await File.ReadAllBytesAsync(fullPath);
        }

        JournalEntry journal = await LayeredVfsInjector.InjectAsync(_clientDir, prepared, [pkg]);
        journal.CreatedFiles.Should().HaveCount(40); // 39 assets + 1 .hotreload.trigger

        string overrideDir = Path.Combine(_clientDir, "override");
        File.Exists(Path.Combine(overrideDir, "world", "water", "materials", "caldavar.material")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "world", "water", "textures", "neon_water_diffuse.tga")).Should()
            .BeTrue();
        File.Exists(Path.Combine(overrideDir, "world", "terrain", "materials", "reborn_dirtmix.material")).Should()
            .BeTrue();
    }

    [Fact]
    public async Task VFS_All11AdvancedMods_SimultaneousInjection_CreatesAllOverrides()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");
        List<ModPackage> all11 = [];
        Dictionary<string, byte[]> allPrepared = [];

        foreach (string modFolder in Expected11ModFolderNames)
        {
            string modDir = Path.Combine(modsBase, modFolder);
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
            all11.Add(pkg);

            foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
            {
                string fullSrc = Path.Combine(modDir, copy.SourcePath);
                allPrepared[copy.TargetName] = await File.ReadAllBytesAsync(fullSrc);
            }
        }

        JournalEntry journal = await LayeredVfsInjector.InjectAsync(_clientDir, allPrepared, all11);

        journal.AppliedMods.Should().HaveCount(11);
        journal.CreatedFiles.Count.Should().Be(allPrepared.Count + 1); // all assets + .hotreload.trigger

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();
        Directory.GetFiles(overrideDir, "*.*", SearchOption.AllDirectories).Length.Should().Be(allPrepared.Count + 1);
    }

    [Fact]
    public async Task VFS_RollbackJournal_AtomicRollback_RestoresCleanClientDirectory()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");
        List<ModPackage> all11 = [];
        Dictionary<string, byte[]> allPrepared = [];

        foreach (string modFolder in Expected11ModFolderNames)
        {
            string modDir = Path.Combine(modsBase, modFolder);
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
            all11.Add(pkg);

            foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
            {
                string fullSrc = Path.Combine(modDir, copy.SourcePath);
                allPrepared[copy.TargetName] = await File.ReadAllBytesAsync(fullSrc);
            }
        }

        await LayeredVfsInjector.InjectAsync(_clientDir, allPrepared, all11);

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();
        RollbackJournal.HasActiveModifications(_clientDir).Should().BeTrue();

        // Execute rollback
        bool rollbackSuccess = await RollbackJournal.ExecuteRollbackAsync(_clientDir);
        rollbackSuccess.Should().BeTrue();

        // Verify clean client state: override/ directory removed, rollback_journal.json removed
        Directory.Exists(overrideDir).Should().BeFalse("Override directory must be completely removed on rollback");
        RollbackJournal.HasActiveModifications(_clientDir).Should().BeFalse();
    }

    [Fact]
    public void VFS_StringtablePatching_MergesKeysAcrossMultipleMods()
    {
        string initialStringtable = "first_blood\tDefault First Blood!\nmultikill0\tDefault Double Kill!\n";

        List<StringtableEntryOperation> opsGlados =
        [
            new("first_blood", "/shared/sounds/announcer/glados/first_blood.wav"),
            new("multikill0", "/shared/sounds/announcer/glados/double_kill.wav"),
            new("annihilation", "/shared/sounds/announcer/glados/annihilation.wav")
        ];

        List<StringtableEntryOperation> opsRetro =
        [
            new("gold_pickup", "/shared/sounds/retro/gold_pickup.wav"),
            new("last_hit", "/shared/sounds/retro/last_hit.wav"),
            new("level_up", "/shared/sounds/retro/level_up.wav")
        ];

        string patched = StringtablePatcher.ApplyPatch(initialStringtable, opsGlados);
        patched = StringtablePatcher.ApplyPatch(patched, opsRetro);

        patched.Should().Contain("first_blood\t/shared/sounds/announcer/glados/first_blood.wav");
        patched.Should().Contain("multikill0\t/shared/sounds/announcer/glados/double_kill.wav");
        patched.Should().Contain("annihilation\t/shared/sounds/announcer/glados/annihilation.wav");
        patched.Should().Contain("gold_pickup\t/shared/sounds/retro/gold_pickup.wav");
        patched.Should().Contain("last_hit\t/shared/sounds/retro/last_hit.wav");
        patched.Should().Contain("level_up\t/shared/sounds/retro/level_up.wav");
    }

    [Fact]
    public async Task EndToEnd_FullLifecycle_Ingest_Sort_Inject_Verify_Rollback()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string modsBase = Path.Combine(_repoRoot, "games", "client", "mods");

        // 1. Ingest all 11 packages
        List<ModPackage> packages = [];
        foreach (string modFolder in Expected11ModFolderNames)
        {
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsBase, modFolder));
            packages.Add(pkg);
        }

        packages.Count.Should().Be(11);

        // 2. Topological Sort
        SortResult sortResult = TopologicalSorter.ResolveAndSort(packages);
        sortResult.Success.Should().BeTrue();
        sortResult.ExecutionPlan.Count.Should().Be(11);

        // 3. Inject sorted plan into isolated VFS sandbox
        Dictionary<string, byte[]> prepared = [];
        foreach (ModPackage pkg in sortResult.ExecutionPlan)
        foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
        {
            string path = Path.Combine(pkg.FilePath, copy.SourcePath);
            prepared[copy.TargetName] = await File.ReadAllBytesAsync(path);
        }

        JournalEntry journal = await LayeredVfsInjector.InjectAsync(_clientDir, prepared, sortResult.ExecutionPlan);
        journal.AppliedMods.Count.Should().Be(11);

        // 4. Verify Sandbox integrity
        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, ".hotreload.trigger")).Should().BeTrue();

        // 5. Atomic Rollback
        bool rollback = await RollbackJournal.ExecuteRollbackAsync(_clientDir);
        rollback.Should().BeTrue();

        // 6. Verify pristine restoration
        Directory.Exists(overrideDir).Should().BeFalse();
        RollbackJournal.HasActiveModifications(_clientDir).Should().BeFalse();
    }

    #endregion
}

