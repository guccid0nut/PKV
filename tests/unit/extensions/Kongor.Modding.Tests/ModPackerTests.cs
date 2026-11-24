// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ModPackerTests.cs
// System: Kongor.Modding.Tests - Single-Layer S2Z/HonMod Packager Tests
// ============================================================================

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Packaging;
using Xunit;

namespace Kongor.Modding.Tests;

public sealed class ModPackerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceDir;
    private readonly string _outputDir;

    public ModPackerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ModPackerTests_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_testRoot, "override_source");
        _outputDir = Path.Combine(_testRoot, "output");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ModPacker_PackDirectoryAsync_PackagesLooseFiles_AndStripsTemporaryFiles()
    {
        // Arrange
        string uiDir = Path.Combine(_sourceDir, "ui");
        Directory.CreateDirectory(uiDir);

        await File.WriteAllTextAsync(Path.Combine(uiDir, "custom.interface"), "<interface name='custom' />");
        await File.WriteAllTextAsync(Path.Combine(uiDir, "script.lua"), "Echo('Hello HoN')");
        
        // Temporary / backup files that MUST be excluded
        await File.WriteAllTextAsync(Path.Combine(uiDir, "custom.interface.bak"), "OLD_BACKUP");
        await File.WriteAllTextAsync(Path.Combine(uiDir, "temp.r2bp"), "RESTORE_POINT");
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, ".hotreload.trigger"), "MOD_RELOAD_123");

        string targetS2z = Path.Combine(_outputDir, "resources999_override.s2z");

        // Act
        ModPackResult result = await ModPacker.PackDirectoryAsync(_sourceDir, targetS2z, ModPackFormat.S2z);

        // Assert
        result.Success.Should().BeTrue();
        result.TotalFiles.Should().Be(2, "Only legitimate non-backup files should be packed.");
        result.PackedEntries.Should().Contain("ui/custom.interface");
        result.PackedEntries.Should().Contain("ui/script.lua");
        result.PackedEntries.Should().NotContain(e => e.EndsWith(".bak") || e.EndsWith(".r2bp"));
        result.Sha256.Should().NotBeNullOrWhiteSpace();

        // Verify physical zip archive validity
        File.Exists(targetS2z).Should().BeTrue();
        using ZipArchive zip = ZipFile.OpenRead(targetS2z);
        zip.Entries.Should().HaveCount(2);
        zip.GetEntry("ui/custom.interface").Should().NotBeNull();
        zip.GetEntry("ui/script.lua").Should().NotBeNull();
    }

    [Fact]
    public async Task ModPacker_PackDirectoryAsync_HonModFormat_GeneratesValidPackageWithManifest()
    {
        // Arrange
        string scriptsDir = Path.Combine(_sourceDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "test.lua"), "local a = 1");

        string targetHonmod = Path.Combine(_outputDir, "TestPackage.honmod");

        // Act
        ModPackResult result = await ModPacker.PackDirectoryAsync(_sourceDir, targetHonmod, ModPackFormat.HonMod);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(targetHonmod).Should().BeTrue();

        using ZipArchive zip = ZipFile.OpenRead(targetHonmod);
        zip.GetEntry("mod.xml").Should().NotBeNull("HonMod format must generate an authoritative mod.xml manifest.");
        zip.GetEntry("scripts/test.lua").Should().NotBeNull();
    }

    [Fact]
    public async Task ModPacker_PackDirectoryAsync_WhenLuaFileHasSyntaxError_FailsWithLuacDiagnostic()
    {
        // Arrange: balanced blocks, but invalid Lua 5.1 expression grammar ('local x = ;')
        string scriptsDir = Path.Combine(_sourceDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        await File.WriteAllTextAsync(
            Path.Combine(scriptsDir, "broken.lua"),
            "function Init()\n  local x = ;\nend\n");

        string targetHonmod = Path.Combine(_outputDir, "BrokenPackage.honmod");

        // Act
        ModPackResult result = await ModPacker.PackDirectoryAsync(_sourceDir, targetHonmod, ModPackFormat.HonMod);

        // Assert
        result.Success.Should().BeFalse("ModPacker must reject .lua files that fail luac51 compilation.");
        result.ErrorMessage.Should().Contain("scripts/broken.lua");
        result.ErrorMessage.Should().Contain("[luac51]");
        File.Exists(targetHonmod).Should().BeFalse();
    }
}
