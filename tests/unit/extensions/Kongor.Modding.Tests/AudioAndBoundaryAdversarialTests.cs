// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Patching;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Challenger 2: Audio Header, Packaging &amp; Stringtable Boundary Adversarial Suite.
///     Rigorously validates:
///     1. 100% of all WAV audio files in games/client/mods/ut_announcer_pack/sounds/ (44-byte RIFF headers, valid sample
///     rates, non-empty payloads).
///     2. FuzzyStringPatcher and StringtablePatcher boundary cases (tabs vs spaces, comments # and //, mixed CRLF/LF,
///     Cyrillic/Unicode, HoN color codes ^r, ^084, ^*).
///     3. HonModArchiveReader and S2zArchiveReader on extraction, corrupted entries, and directory mirroring.
/// </summary>
public class AudioAndBoundaryAdversarialTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _tempDir;

    public AudioAndBoundaryAdversarialTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorChallenger2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        string baseDir = AppContext.BaseDirectory;
        string current = baseDir;
        while (!string.IsNullOrEmpty(current) && !File.Exists(Path.Combine(current, "PROJECT.md")))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        _repoRoot = current;
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

    #region 1. Audio Header & Payload Verification (UT Announcer Pack)

    [Fact]
    public void UtAnnouncerPack_AllWavFiles_ExistAndHaveValid44ByteRiffHeaders()
    {
        string soundsDir = Path.Combine(_repoRoot, "games", "client", "mods", "ut_announcer_pack", "sounds");
        if (!Directory.Exists(soundsDir))
        {
            // In cleanroom mode / public export verification, games/ is excluded from export.
            return;
        }

        string[] wavFiles = Directory.GetFiles(soundsDir, "*.wav");
        wavFiles.Length.Should()
            .BeGreaterThanOrEqualTo(24, "UT Announcer pack must contain at least 24 audio WAV clips.");

        foreach (string wavPath in wavFiles)
        {
            string fileName = Path.GetFileName(wavPath);
            byte[] fileBytes = File.ReadAllBytes(wavPath);

            fileBytes.Length.Should().BeGreaterThanOrEqualTo(44, $"Audio file '{fileName}' is smaller than 44 bytes.");

            using MemoryStream ms = new(fileBytes);
            using BinaryReader reader = new(ms, Encoding.ASCII);

            // 1. "RIFF"
            string riff = new(reader.ReadChars(4));
            riff.Should().Be("RIFF", $"File '{fileName}' must start with 'RIFF'");

            // 2. Chunk Size = File Size - 8
            int chunkSize = reader.ReadInt32();
            chunkSize.Should().Be(fileBytes.Length - 8, $"File '{fileName}' chunk size mismatch");

            // 3. "WAVE"
            string wave = new(reader.ReadChars(4));
            wave.Should().Be("WAVE", $"File '{fileName}' must have 'WAVE' format marker");

            // 4. "fmt "
            string fmt = new(reader.ReadChars(4));
            fmt.Should().Be("fmt ", $"File '{fileName}' must contain 'fmt ' subchunk");

            // 5. Subchunk1Size = 16 for PCM
            int subchunk1Size = reader.ReadInt32();
            subchunk1Size.Should().Be(16, $"File '{fileName}' subchunk1 size must be 16 for PCM");

            // 6. AudioFormat = 1 (PCM)
            short audioFormat = reader.ReadInt16();
            audioFormat.Should().Be(1, $"File '{fileName}' audio format must be 1 (PCM)");

            // 7. Channels (1 = Mono, 2 = Stereo)
            short channels = reader.ReadInt16();
            channels.Should().BeInRange(1, 2, $"File '{fileName}' channels must be Mono or Stereo");

            // 8. Sample Rate
            int sampleRate = reader.ReadInt32();
            sampleRate.Should().BeInRange(8000, 96000,
                $"File '{fileName}' sample rate {sampleRate} is outside standard audio range");

            // 9. Byte Rate = SampleRate * NumChannels * BitsPerSample/8
            int byteRate = reader.ReadInt32();

            // 10. Block Align = NumChannels * BitsPerSample/8
            short blockAlign = reader.ReadInt16();

            // 11. Bits Per Sample = 16 (or 8)
            short bitsPerSample = reader.ReadInt16();
            bitsPerSample.Should().BeInRange(8, 32, $"File '{fileName}' bit depth must be 8..32 bits");

            int expectedByteRate = sampleRate * channels * (bitsPerSample / 8);
            byteRate.Should().Be(expectedByteRate, $"File '{fileName}' byte rate mismatch");

            short expectedBlockAlign = (short)(channels * (bitsPerSample / 8));
            blockAlign.Should().Be(expectedBlockAlign, $"File '{fileName}' block align mismatch");

            // 12. "data"
            string data = new(reader.ReadChars(4));
            data.Should().Be("data", $"File '{fileName}' must contain 'data' subchunk");

            // 13. Data Size > 0
            int dataSize = reader.ReadInt32();
            dataSize.Should().BeGreaterThan(0, $"File '{fileName}' audio payload data size must be greater than 0");
            (dataSize + 44).Should()
                .BeLessThanOrEqualTo(fileBytes.Length, $"File '{fileName}' payload exceeds file bounds");
        }
    }

    #endregion

    #region 2. FuzzyStringPatcher & StringtablePatcher Boundary Challenges

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task FuzzyStringPatcher_Boundary_MixedLineEndings_MatchesAndReplacesAccurately(string newline)
    {
        string document =
            $"<interface>{newline}  <panel name=\"chat_box\" width=\"500\">{newline}    <label text=\"Hello\" />{newline}  </panel>{newline}</interface>";
        string searchTarget = "<panel name=\"chat_box\" width=\"500\">\n    <label text=\"Hello\" />";
        string replaceTarget = "<panel name=\"chat_box\" width=\"600\">\n    <label text=\"World\" />";

        PatchOperation[] ops =
        [
            new FindOperation(searchTarget),
            new ReplaceOperation(replaceTarget)
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(document, ops);
        patched.Should().Contain("<panel name=\"chat_box\" width=\"600\">");
        patched.Should().Contain("<label text=\"World\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_Boundary_TabsVsSpacesIndentation_MatchesRegardlessOfWhitespaceFormat()
    {
        string docWithTabs =
            "<interface>\n\t\t<panel\t\tname=\"custom_panel\"\t\twidth=\"100%\"\t\theight=\"200\">\n\t\t\t<label\ttext=\"Title\"\t/>\n\t\t</panel>\n</interface>";
        string searchTargetWithSpaces =
            "<panel name=\"custom_panel\" width=\"100%\" height=\"200\">\n  <label text=\"Title\" />\n</panel>";

        PatchOperation[] ops =
        [
            new FindOperation(searchTargetWithSpaces),
            new ReplaceOperation("<panel name=\"modified_panel\" width=\"100%\" height=\"350\" />")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(docWithTabs, ops);
        patched.Should().Contain("<panel name=\"modified_panel\" width=\"100%\" height=\"350\" />");
        patched.Should().NotContain("custom_panel");
    }

    [Fact]
    public async Task FuzzyStringPatcher_Boundary_HoNColorCodesAndSpecialRegexCharacters_EscapedCorrectly()
    {
        // HoN color codes use ^r, ^084, ^*, and XML might have brackets, parens, dollar signs, slashes
        string document = @"
// Announcements
first_kill	{killer_color}{killer}^* drew ^rFIRST BLOOD^*! (^y+{gold}^* gold!)
multikill0	{killer} scored a ^084DOUBLE KILL^*!
killstreak7	{killer_color}{killer}^* is ^840HOLY SHIT^*! Someone KILL THEM!
formula		$result = (100 * [modifier]) / 2.0;
";

        PatchOperation[] ops =
        [
            new FindOperation("{killer_color}{killer}^* drew ^rFIRST BLOOD^*! (^y+{gold}^* gold!)"),
            new ReplaceOperation("{killer_color}{killer}^* drew ^rUNREAL FIRST BLOOD^*! (^y+{gold}^* gold!)"),
            new FindOperation("$result = (100 * [modifier]) / 2.0;"),
            new ReplaceOperation("$result = (200 * [modifier]) / 2.0;")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(document, ops);
        patched.Should().Contain("UNREAL FIRST BLOOD");
        patched.Should().Contain("$result = (200 * [modifier]) / 2.0;");
    }

    [Fact]
    public async Task FuzzyStringPatcher_Boundary_CyrillicAndUnicodeCharacters_PreservedAndMatched()
    {
        string document =
            "<interface>\n  <label name=\"announcement\" text=\"ПЕРВАЯ КРОВЬ!\" color=\"#FF0000\" />\n  <label name=\"jp_status\" text=\"ダブルキル！\" />\n  <label name=\"de_status\" text=\"Erstes Blut &amp; Überraschung!\" />\n</interface>";

        PatchOperation[] ops =
        [
            new FindOperation("<label name=\"announcement\" text=\"ПЕРВАЯ КРОВЬ!\" color=\"#FF0000\" />"),
            new ReplaceOperation(
                "<label name=\"announcement\" text=\"ПЕРВАЯ КРОВЬ (СМЕРТЕЛЬНЫЙ УДАР)!\" color=\"#FF0000\" />"),
            new FindOperation("<label name=\"jp_status\" text=\"ダブルキル！\" />"),
            new ReplaceOperation("<label name=\"jp_status\" text=\"トリプルキル！\" />")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(document, ops);
        patched.Should().Contain("ПЕРВАЯ КРОВЬ (СМЕРТЕЛЬНЫЙ УДАР)!");
        patched.Should().Contain("トリプルキル！");
        patched.Should().Contain("Erstes Blut &amp; Überraschung!");
    }

    [Fact]
    public void StringtablePatcher_Boundary_CommentPreservation_HashAndSlashComments()
    {
        string originalStr = @"# Main Game Stringtable Configuration
// Category: Announcer Sounds
# Section 1: Multi-Kills
first_blood	/sounds/fb_orig.wav
// Section 2: Killstreaks
   // Indented slash comment
   # Indented hash comment
killstreak0	/sounds/ks0_orig.wav
";

        StringtableEntryOperation[] ops =
        [
            new("first_blood", "/sounds/fb_new.wav"),
            new("killstreak0", "/sounds/ks0_new.wav"),
            new("new_entry", "New Value")
        ];

        string patched = StringtablePatcher.ApplyPatch(originalStr, ops);

        // Verify comments are intact
        patched.Should().Contain("# Main Game Stringtable Configuration");
        patched.Should().Contain("// Category: Announcer Sounds");
        patched.Should().Contain("# Section 1: Multi-Kills");
        patched.Should().Contain("// Section 2: Killstreaks");
        patched.Should().Contain("   // Indented slash comment");
        patched.Should().Contain("   # Indented hash comment");

        // Verify values updated
        patched.Should().Contain("first_blood\t/sounds/fb_new.wav");
        patched.Should().Contain("killstreak0\t/sounds/ks0_new.wav");
        patched.Should().Contain("new_entry\tNew Value");
    }

    [Fact]
    public void StringtablePatcher_Boundary_TabsVsSpacesSeparators_PreservesExistingSeparator()
    {
        string originalStr = "tab_key\ttab_value\nspace_key space_value\n";

        StringtableEntryOperation[] ops =
        [
            new("tab_key", "updated_tab"),
            new("space_key", "updated_space"),
            new("added_key", "added_value")
        ];

        string patched = StringtablePatcher.ApplyPatch(originalStr, ops);

        // tab_key should retain tab
        patched.Should().Contain("tab_key\tupdated_tab");
        // space_key should retain space
        patched.Should().Contain("space_key updated_space");
        // added_key defaults to tab
        patched.Should().Contain("added_key\tadded_value");
    }

    [Fact]
    public void StringtablePatcher_Boundary_HoNColorCodesAndCyrillicUnicode_HandlesWithoutCorruption()
    {
        string originalStr = "announcer_fb\tOriginal FB\nmsg_killstreak\tOriginal KS\n";

        StringtableEntryOperation[] ops =
        [
            new("announcer_fb", "{killer_color}{killer}^* пролил ^rПЕРВУЮ КРОВЬ^*! (^084+200^* золота!)"),
            new("msg_killstreak", "^840HOLY SHIT^*! {killer} is ^800GODLIKE^*!!!"),
            new("chinese_entry", "玩家 {killer} 达成 ^r第一滴血^*！")
        ];

        string patched = StringtablePatcher.ApplyPatch(originalStr, ops);

        patched.Should().Contain("{killer_color}{killer}^* пролил ^rПЕРВУЮ КРОВЬ^*! (^084+200^* золота!)");
        patched.Should().Contain("^840HOLY SHIT^*! {killer} is ^800GODLIKE^*!!!");
        patched.Should().Contain("玩家 {killer} 达成 ^r第一滴血^*！");
    }

    #endregion

    #region 3. HonModArchiveReader & S2zArchiveReader Adversarial Challenges

    [Fact]
    public async Task HonModArchiveReader_UnpackedDirectoryMirroring_ReadsIdenticallyToZipPackage()
    {
        string modDir = Path.Combine(_tempDir, "unpacked_mod");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(Path.Combine(modDir, "ui"));

        string modXmlContent = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<modification application=""Heroes of Newerth"" name=""Mirroring Test Mod"" version=""1.2.3"" author=""Tester"">
  <copyfile name=""ui/test.interface"" source=""ui/test.interface"" />
</modification>";

        await File.WriteAllTextAsync(Path.Combine(modDir, "mod.xml"), modXmlContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(modDir, "ui", "test.interface"), "<interface name='test' />",
            Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(modDir, "icon.png"), [1, 2, 3, 4]);

        // 1. Read from directory
        using ModPackage dirPkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
        dirPkg.IsDirectorySource.Should().BeTrue();
        dirPkg.Manifest.Name.Should().Be("Mirroring Test Mod");
        dirPkg.Manifest.Version.Should().Be("1.2.3");
        string dirUiText = await dirPkg.GetPayloadTextAsync("ui/test.interface");
        dirUiText.Should().Be("<interface name='test' />");

        // 2. Package into .honmod zip
        string zipPath = Path.Combine(_tempDir, "packaged.honmod");
        Dictionary<string, byte[]> payloads = new()
        {
            ["ui/test.interface"] = Encoding.UTF8.GetBytes("<interface name='test' />"),
            ["icon.png"] = [1, 2, 3, 4]
        };
        await HonModArchiveWriter.WritePackageAsync(zipPath, dirPkg.Manifest, payloads, payloads["icon.png"]);

        // 3. Read from zip
        using ModPackage zipPkg = await HonModArchiveReader.ReadPackageAsync(zipPath);
        zipPkg.IsDirectorySource.Should().BeFalse();
        zipPkg.Manifest.Name.Should().Be("Mirroring Test Mod");
        zipPkg.Manifest.Version.Should().Be("1.2.3");
        string zipUiText = await zipPkg.GetPayloadTextAsync("ui/test.interface");
        zipUiText.Should().Be("<interface name='test' />");

        // Assert payload parity
        zipUiText.Should().Be(dirUiText);
    }

    [Fact]
    public async Task HonModArchiveReader_CorruptedArchive_ThrowsInvalidDataException()
    {
        // 1. Missing mod.xml
        string noXmlPath = Path.Combine(_tempDir, "no_xml.honmod");
        using (FileStream fs = File.Create(noXmlPath))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry e = zip.CreateEntry("dummy.txt");
            using StreamWriter w = new(e.Open());
            w.Write("no manifest");
        }

        Func<Task> readNoXml = async () => await HonModArchiveReader.ReadPackageAsync(noXmlPath);
        await readNoXml.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*Missing 'mod.xml'*");

        // 2. Corrupted / Truncated ZIP stream
        string corruptZipPath = Path.Combine(_tempDir, "corrupt.honmod");
        await File.WriteAllBytesAsync(corruptZipPath, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0xFF, 0xFF]);

        Func<Task> readCorrupt = async () => await HonModArchiveReader.ReadPackageAsync(corruptZipPath);
        await readCorrupt.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task S2zArchiveReader_WhenInvoked_MultiArchiveFallbackAndCaseInsensitiveLookup()
    {
        string s2zRes0 = Path.Combine(_tempDir, "resources0.s2z");
        string s2zRes1 = Path.Combine(_tempDir, "resources1.s2z");

        await S2zArchiveWriter.WriteArchiveAsync(s2zRes0, new Dictionary<string, byte[]>
        {
            ["ui/game.interface"] = Encoding.UTF8.GetBytes("<interface base='0' />"),
            ["sounds/fb.wav"] = Encoding.UTF8.GetBytes("BASE_FB_WAV")
        });

        await S2zArchiveWriter.WriteArchiveAsync(s2zRes1, new Dictionary<string, byte[]>
        {
            ["ui/game.interface"] = Encoding.UTF8.GetBytes("<interface patch='1' />")
        });

        using GameResourceResolver resolver = new([s2zRes0, s2zRes1]);

        // Higher archive (resources1) overrides lower archive (resources0)
        string? gameUi = await resolver.TryReadGameTextAsync("ui/game.interface");
        gameUi.Should().Be("<interface patch='1' />");

        // Case-insensitive lookup with mixed slashes
        string? fbSound = await resolver.TryReadGameTextAsync("SOUNDS\\FB.WAV");
        fbSound.Should().Be("BASE_FB_WAV");

        // Non-existent entry returns null
        string? missing = await resolver.TryReadGameTextAsync("non_existent/file.txt");
        missing.Should().BeNull();
    }

    [Fact]
    public async Task S2zArchiveReader_DisposedReader_ThrowsObjectDisposedException()
    {
        string s2zPath = Path.Combine(_tempDir, "test.s2z");
        await S2zArchiveWriter.WriteArchiveAsync(s2zPath, new Dictionary<string, byte[]>
        {
            ["data.txt"] = Encoding.UTF8.GetBytes("DATA")
        });

        S2zArchiveReader reader = S2zArchiveReader.Open(s2zPath);
        reader.HasEntry("data.txt").Should().BeTrue();
        reader.Dispose();

        Func<Task> readAfterDispose = async () => await reader.ReadEntryBytesAsync("data.txt");
        await readAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    #endregion
}
