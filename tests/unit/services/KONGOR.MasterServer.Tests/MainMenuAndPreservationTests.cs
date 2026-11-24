// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class MainMenuAndPreservationTests
{
    private static string GetRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ProjectKongor.slnx")) &&
                Directory.Exists(Path.Combine(dir, "src")) &&
                !string.IsNullOrEmpty(dir))
            {
                return dir;
            }
            DirectoryInfo? parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }
        throw new InvalidOperationException("Could not locate repo root.");
    }

    [Fact]
    public void MainMenu_HomepageV2Package_ShouldEliminateSceneBufferAndClampDimensions()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = GetRepoRoot();
        
        // VFS Resolution Chain: override/ (if active) -> ClientPackageFiles/ -> .s2z
        string overridePkg = Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "newui", "homepage_v2.package");
        string clientPackagePkg = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "newui", "homepage_v2.package");
        string resourceArchive = Path.Combine(repoRoot, "games", "client", "game", "resources99.s2z");

        File.Exists(clientPackagePkg).Should().BeTrue($"ClientPackageFiles package file must exist: {clientPackagePkg}");
        File.Exists(resourceArchive).Should().BeTrue($"Resource archive must exist: {resourceArchive}");

        var payloads = new List<(string Source, string Content)>
        {
            (clientPackagePkg, File.ReadAllText(clientPackagePkg))
        };
        if (File.Exists(overridePkg))
        {
            payloads.Add((overridePkg, File.ReadAllText(overridePkg)));
        }

        using (ZipArchive zip = ZipFile.OpenRead(resourceArchive))
        {
            ZipArchiveEntry? entry = zip.GetEntry("ui/fe2/newui/homepage_v2.package");
            entry.Should().NotBeNull($"ui/fe2/newui/homepage_v2.package must exist in {resourceArchive}");
            using StreamReader sr = new(entry!.Open());
            payloads.Add(("resources99.s2z:ui/fe2/newui/homepage_v2.package", sr.ReadToEnd()));
        }

        foreach ((string path, string content) in payloads)
        {
            // Bounding quad elimination: scenebuffer="true" must be completely removed
            content.Should().NotContain("scenebuffer=\"true\"", $"Scenebuffer true causes offscreen bounding quad in {path}");
            content.Should().Contain("scenebuffer=\"false\"", $"Modelpanel must explicitly declare scenebuffer false in {path}");

            // Clamp panel dimensions: 300% must be eliminated
            content.Should().NotContain("width=\"300%\"", $"300% oversized panel must be removed in {path}");
            content.Should().NotContain("height=\"300%\"", $"300% oversized panel must be removed in {path}");

            // Logo definition presence
            content.Should().Contain("main_midbar_logo", $"main_midbar_logo must be present in {path}");
        }
    }

    [Fact]
    public void MainMenuAndPreservation_CentralHonShieldLogo_ShouldBeUprightWithCleanFormat()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = GetRepoRoot();
        
        // VFS Resolution Chain: override/ (if active) -> ClientPackageFiles/ -> .s2z
        string overrideLogoDds = Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "newui", "res", "homepage", "logo.dds");
        string clientPackageLogoDds = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "00000000", "ui", "fe2", "newui", "res", "homepage", "logo.dds");
        string overrideLogoTga = Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "newui", "res", "homepage", "logo.tga");
        string resourceArchive = Path.Combine(repoRoot, "games", "client", "game", "resources99.s2z");

        File.Exists(clientPackageLogoDds).Should().BeTrue("ClientPackageFiles logo.dds must exist");
        File.Exists(resourceArchive).Should().BeTrue($"Resource archive must exist: {resourceArchive}");

        var ddsByteStreams = new List<(string Source, byte[] Bytes)>
        {
            ("ClientPackageFiles", File.ReadAllBytes(clientPackageLogoDds))
        };
        if (File.Exists(overrideLogoDds))
        {
            ddsByteStreams.Add(("override", File.ReadAllBytes(overrideLogoDds)));
        }

        byte[]? tgaBytes = File.Exists(overrideLogoTga) ? File.ReadAllBytes(overrideLogoTga) : null;

        using (ZipArchive zip = ZipFile.OpenRead(resourceArchive))
        {
            ZipArchiveEntry? entry = zip.GetEntry("00000000/ui/fe2/newui/res/homepage/logo.dds") ??
                                     zip.GetEntry("ui/fe2/newui/res/homepage/logo.dds");
            entry.Should().NotBeNull($"logo.dds must exist in {resourceArchive}");
            using MemoryStream ms = new();
            using (Stream stream = entry!.Open())
            {
                stream.CopyTo(ms);
            }
            ddsByteStreams.Add(("resources99.s2z", ms.ToArray()));

            if (tgaBytes == null)
            {
                ZipArchiveEntry? tgaEntry = zip.GetEntry("ui/fe2/newui/res/homepage/logo.tga") ??
                                            zip.GetEntry("00000000/ui/fe2/newui/res/homepage/logo.tga");
                tgaEntry.Should().NotBeNull($"logo.tga must exist in {resourceArchive}");
                using MemoryStream tgaMs = new();
                using Stream tgaStream = tgaEntry!.Open();
                tgaStream.CopyTo(tgaMs);
                tgaBytes = tgaMs.ToArray();
            }
        }

        foreach ((string source, byte[] ddsBytes) in ddsByteStreams)
        {
            ddsBytes.Length.Should().Be(87536, $"logo.dds in {source} with 9 mipmaps must be 87536 bytes");

            // DDS Header: Magic 'DDS '
            ddsBytes[0].Should().Be((byte)'D');
            ddsBytes[1].Should().Be((byte)'D');
            ddsBytes[2].Should().Be((byte)'S');
            ddsBytes[3].Should().Be((byte)' ');

            // Height and Width at offset 12 and 16 (little-endian 32-bit int)
            int height = BitConverter.ToInt32(ddsBytes, 12);
            int width = BitConverter.ToInt32(ddsBytes, 16);
            height.Should().Be(256, $"Height in {source} must be 256");
            width.Should().Be(256, $"Width in {source} must be 256");

            // DXT5 FourCC at offset 84: 'D', 'X', 'T', '5'
            ddsBytes[84].Should().Be((byte)'D');
            ddsBytes[85].Should().Be((byte)'X');
            ddsBytes[86].Should().Be((byte)'T');
            ddsBytes[87].Should().Be((byte)'5');
        }

        // Verify TGA has non-inverting bottom-left origin 0x08
        tgaBytes.Should().NotBeNull();
        tgaBytes!.Length.Should().BeGreaterThan(18);
        tgaBytes[2].Should().Be(2, "Uncompressed true-color TGA");
        tgaBytes[16].Should().Be(32, "32-bit depth with alpha");
        tgaBytes[17].Should().Be(0x08, "Non-inverting bottom-left origin descriptor");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void MainMenuAndPreservation_PlinkoBoardAndVanityTextures_ShouldBeUpright()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = GetRepoRoot();
        string plinkoBoardDds = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "00000000", "ui", "fe2", "plinko", "board.dds");
        File.Exists(plinkoBoardDds).Should().BeTrue("Plinko board.dds must exist");

        byte[] boardBytes = File.ReadAllBytes(plinkoBoardDds);
        boardBytes.Length.Should().BeGreaterThan(128);
        int height = BitConverter.ToInt32(boardBytes, 12);
        int width = BitConverter.ToInt32(boardBytes, 16);
        width.Should().Be(1024);
        height.Should().Be(512);

        // Verify all loose vanity TGAs in account_icons and upgrade_icons have top-down descriptor
        string[] vanityDirs = new[]
        {
            Path.Combine(repoRoot, "games", "client", "game", "ui", "fe2", "store", "icons", "account_icons"),
            Path.Combine(repoRoot, "games", "client", "game", "ui", "fe2", "store", "icons", "upgrade_icons")
        };

        foreach (string dir in vanityDirs)
        {
            Directory.Exists(dir).Should().BeTrue($"Vanity directory must exist: {dir}");
            string[] tgas = Directory.GetFiles(dir, "*.tga");
            tgas.Length.Should().BeGreaterThan(50);

            foreach (string tga in tgas)
            {
                using FileStream fs = File.OpenRead(tga);
                byte[] header = new byte[18];
                fs.ReadExactly(header, 0, 18);
                header[17].Should().Be(0x08, $"TGA {Path.GetFileName(tga)} must have non-inverting descriptor 0x08");
            }
        }
    }

    [Fact]
    public void MainMenuAndPreservation_Empath3DLoginScreen_ShouldBePreserved()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = GetRepoRoot();
        string bgPackagePath = Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "background.package");
        if (!File.Exists(bgPackagePath))
            bgPackagePath = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "background.package");

        string luaControllerPath = Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "scripts", "hero_login_controller.lua");
        if (!File.Exists(luaControllerPath))
            luaControllerPath = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "scripts", "hero_login_controller.lua");

        File.Exists(bgPackagePath).Should().BeTrue("background.package must exist");
        File.Exists(luaControllerPath).Should().BeTrue("hero_login_controller.lua must exist");

        string bgContent = File.ReadAllText(bgPackagePath);
        bgContent.Should().Contain("fe2_background_new", "fe2_background_new panel must be preserved");
        bgContent.Should().Contain("fe2_hero_3d_container", "fe2_hero_3d_container panel must be preserved");
        bgContent.Should().Contain("login_hero_pedestal", "login_hero_pedestal 3D platform must be preserved");
        bgContent.Should().Contain("login_hero_viewport", "login_hero_viewport 3D modelpanel must be preserved");
        (bgContent.Contains("/heroes/empath/hd_empath/preview.mdf") ||
         bgContent.Contains("/heroes/empath/hd_empath/model.mdf") ||
         bgContent.Contains("/heroes/empath/alt9/model.mdf"))
            .Should().BeTrue("Empath 3D login model (preview.mdf, hd_empath/model.mdf, or alt9/model.mdf) must be assigned");

        string luaContent = File.ReadAllText(luaControllerPath);
        luaContent.Should().Contain("HeroLoginController", "HeroLoginController table must be defined");
        luaContent.Should().Contain("empath = {", "Empath preset configuration must be defined");
        luaContent.Should().Contain("hd_empath = {", "HD Empath preset configuration must be defined");
    }

    [Fact]
    public void MainMenuAndPreservation_VipGuccidonutMotd_ShouldBePreserved()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = GetRepoRoot();
        string homepagePackage = Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "newui", "homepage_v2.package");
        if (!File.Exists(homepagePackage))
            homepagePackage = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "newui", "homepage_v2.package");

        string loginCfg = Path.Combine(repoRoot, "games", "client", "userdataKONGOR", "extensions", "login.cfg");

        File.Exists(homepagePackage).Should().BeTrue("homepage_v2.package must exist");
        File.Exists(loginCfg).Should().BeTrue("login.cfg must exist");

        string homepageContent = File.ReadAllText(homepagePackage);
        homepageContent.Should().Contain("motd_slide_3", "MOTD Slide 3 must be preserved");
        homepageContent.Should().Contain("guccidonut Live Stream", "guccidonut Live Stream title must be preserved");
        homepageContent.Should().Contain("twitch.tv/theguccidonut", "guccidonut Twitch stream link must be preserved");
        homepageContent.Should().Contain("motd_card_btn_3", "motd_card_btn_3 Stream Hub button must be preserved");

        string cfgContent = File.ReadAllText(loginCfg);
        cfgContent.Should().Contain("login_name guccidonut", "login.cfg must specify login_name guccidonut");
    }
}
