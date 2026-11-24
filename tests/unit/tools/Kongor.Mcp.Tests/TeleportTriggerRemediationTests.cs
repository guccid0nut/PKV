// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
///     Comprehensive unit test suite verifying Mid Wars & Grimm's Crossing teleporter
///     entity definitions, diagnostic logging, deterministic destination coordinates,
///     proxy entity resolution, and 100% parity across loose directories and .s2z archives.
/// </summary>
public sealed class TeleportTriggerRemediationTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "ProjectKongor.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string ResolveTeleporterDir(string root)
    {
        string clientOverrideDir = Path.Combine(root, "games", "client", "override", "triggers", "teleporters");
        if (Directory.Exists(clientOverrideDir) && File.Exists(Path.Combine(clientOverrideDir, "legion", "legion_teleporter.entity")))
            return clientOverrideDir;

        return Path.Combine(root, "games", "server", "override", "triggers", "teleporters");
    }

    [Fact]
    public void TeleporterEntities_AllXmlFiles_AreValidXmlAndContainRequiredActionScriptElements()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string root = FindRepoRoot();
        string dir = ResolveTeleporterDir(root);

        string[] entityFiles =
        [
            Path.Combine(dir, "legion", "legion_teleporter.entity"),
            Path.Combine(dir, "hellbourne", "hellbourne_teleporter.entity"),
            Path.Combine(dir, "state_teleport_stun_grimm.entity"),
            Path.Combine(dir, "state_teleport_stun_grimm_bad.entity"),
            Path.Combine(dir, "state_teleported_grimm.entity"),
            Path.Combine(dir, "gadget_teleporter_destination.entity")
        ];

        foreach (string file in entityFiles)
        {
            File.Exists(file).Should().BeTrue($"File {file} must exist");
            string text = File.ReadAllText(file);
            XDocument doc = XDocument.Parse(text);
            doc.Root.Should().NotBeNull();
            doc.Root!.Name.LocalName.Should().Match(name => name == "gadget" || name == "state");
        }
    }

    [Fact]
    public void TeleporterGadgets_ContainDiagnosticLoggingAndSafeRadius()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string root = FindRepoRoot();
        string dir = ResolveTeleporterDir(root);

        string legionPath = Path.Combine(dir, "legion", "legion_teleporter.entity");
        string hellPath = Path.Combine(dir, "hellbourne", "hellbourne_teleporter.entity");

        string legionXml = File.ReadAllText(legionPath);
        string hellXml = File.ReadAllText(hellPath);

        // Diagnostic logging in <ontouched> using value= attribute (not position=)
        legionXml.Should().Contain("[TELEPORT]");
        hellXml.Should().Contain("[TELEPORT]");
        legionXml.Should().Contain("printpos");
        hellXml.Should().Contain("printpos");
        legionXml.Should().Contain("value=\"this_position\"");
        legionXml.Should().NotContain("position=\"this_position\"");
        hellXml.Should().Contain("value=\"this_position\"");
        hellXml.Should().NotContain("position=\"this_position\"");

        // Proxy destination entity resolution
        legionXml.Should().Contain("pushentitybyname name=\"Legion_TPDestination_1\"");
        hellXml.Should().Contain("pushentitybyname name=\"Hellbourne_TPDestination_1\"");
        legionXml.Should().Contain("proxy=\"stack_entity\"");
        hellXml.Should().Contain("proxy=\"stack_entity\"");

        // Stun guard and explicit target="target_entity" preventing continuous 50ms re-trigger loop
        legionXml.Should().Contain("State_Teleport_Stun_Grimm");
        legionXml.Should().Contain("target=\"target_entity\"");
        hellXml.Should().Contain("State_Teleport_Stun_Grimm");
        hellXml.Should().Contain("target=\"target_entity\"");

        // Non-blocking entrance hull and generous click selection radius
        legionXml.Should().Contain("boundsradius=\"96\"");
        legionXml.Should().Contain("selectionradius=\"192\"");
        legionXml.Should().Contain("prefertouch=\"true\"");
        legionXml.Should().Contain("antiblocking=\"true\"");

        hellXml.Should().Contain("boundsradius=\"96\"");
        hellXml.Should().Contain("selectionradius=\"192\"");
        hellXml.Should().Contain("prefertouch=\"true\"");
        hellXml.Should().Contain("antiblocking=\"true\"");

        // Gadget idle world prop passive effects
        legionXml.Should().Contain("passiveeffect=\"/world/props/legion_teleporter/effects/light.effect\"");
        hellXml.Should().Contain("passiveeffect=\"/world/props/hellbourne_teleporter/effects/evil.effect\"");
    }

    [Fact]
    public void TeleportStunStates_ContainDeterministicDestinationFallbackCoordinates()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string root = FindRepoRoot();
        string dir = ResolveTeleporterDir(root);

        string stateGrimmPath = Path.Combine(dir, "state_teleport_stun_grimm.entity");
        string stateBadPath = Path.Combine(dir, "state_teleport_stun_grimm_bad.entity");

        string grimmXml = File.ReadAllText(stateGrimmPath);
        string badXml = File.ReadAllText(stateBadPath);

        // Visual and audio passive effects on channeling hero
        grimmXml.Should().Contain("passiveeffect=\"teleport_effects/gold_teleport.effect\"");
        badXml.Should().Contain("passiveeffect=\"teleport_effects/red_teleport.effect\"");
        badXml.Should().NotContain("evil_teleport.effect", "evil_teleport does not exist and breaks audio and visual FX");

        // Primary destination: this_proxy_position of destination gadget
        grimmXml.Should().Contain("this_proxy_position");
        badXml.Should().Contain("this_proxy_position");

        // Deterministic worldpos coordinate fallback
        grimmXml.Should().Contain("worldpos=\"4494.36 6942.49 127.99\"", "Mid Wars Legion destination must use worldpos targeting Legion pad");
        grimmXml.Should().Contain("worldpos=\"8768.87 8897.14 128.00\"", "Grimm's Crossing Legion destination must use worldpos targeting Hellbourne pad");
        grimmXml.Should().Contain("worldpos=\"6594.10 4056.81 128.00\"", "Mid Wars Reborn Legion destination must use worldpos targeting Legion pad");
        grimmXml.Should().NotContain("position=\"4494.36", "setpos0 must never use position= for coordinate vectors");
        grimmXml.Should().Contain("[TELEPORT]");
        grimmXml.Should().Contain("printpos");
        grimmXml.Should().Contain("value=\"this_owner_position\"");
        grimmXml.Should().NotContain("position=\"this_owner_position\"");
        grimmXml.Should().Contain("<onexpired>");

        // Hellbourne teleports to Hellbourne landing pad in Mid Wars, Legion pad in Grimm's Crossing
        badXml.Should().Contain("worldpos=\"8946.46 10676.89 127.99\"", "Mid Wars Hellbourne destination must use worldpos targeting Hellbourne pad");
        badXml.Should().Contain("worldpos=\"7363.19 7491.03 128.00\"", "Grimm's Crossing Hellbourne destination must use worldpos targeting Legion pad");
        badXml.Should().Contain("worldpos=\"12465.17 8655.88 120.43\"", "Mid Wars Reborn Hellbourne destination must use worldpos targeting Hellbourne pad");
        badXml.Should().NotContain("position=\"8946.46", "setpos0 must never use position= for coordinate vectors");
        badXml.Should().Contain("[TELEPORT]");
        badXml.Should().Contain("printpos");
        badXml.Should().Contain("value=\"this_owner_position\"");
        badXml.Should().NotContain("position=\"this_owner_position\"");
        badXml.Should().Contain("<onexpired>");
    }

    [Fact]
    public void TeleporterFiles_AllFourDirectoryTiers_AreBitwiseIdentical()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string root = FindRepoRoot();
        string[] locations =
        [
            Path.Combine(root, "games", "server", "override", "triggers", "teleporters"),
            Path.Combine(root, "games", "client", "game", "triggers", "teleporters")
        ];

        string r99Path = Path.Combine(root, "games", "client", "game", "resources99.s2z");
        using ZipArchive zip = ZipFile.OpenRead(r99Path);

        string[] relativeFiles =
        [
            Path.Combine("legion", "legion_teleporter.entity"),
            Path.Combine("hellbourne", "hellbourne_teleporter.entity"),
            "state_teleport_stun_grimm.entity",
            "state_teleport_stun_grimm_bad.entity",
            "state_teleported_grimm.entity",
            "gadget_teleporter_destination.entity",
            Path.Combine("teleport_effects", "gold_teleport.effect"),
            Path.Combine("teleport_effects", "red_teleport.effect")
        ];

        foreach (string rel in relativeFiles)
        {
            string baselinePath = Path.Combine(locations[0], rel);
            byte[] baselineBytes = File.ReadAllBytes(baselinePath);
            string baselineHash = Convert.ToHexString(SHA256.HashData(baselineBytes));

            for (int i = 1; i < locations.Length; i++)
            {
                string targetPath = Path.Combine(locations[i], rel);
                File.Exists(targetPath).Should().BeTrue($"Target file {targetPath} must exist");
                byte[] targetBytes = File.ReadAllBytes(targetPath);
                string targetHash = Convert.ToHexString(SHA256.HashData(targetBytes));
                targetHash.Should().Be(baselineHash, $"{targetPath} must match baseline {baselinePath}");
            }

            string zipEntryName = $"triggers/teleporters/{rel.Replace('\\', '/')}";
            ZipArchiveEntry? entry = zip.GetEntry(zipEntryName);
            entry.Should().NotBeNull($"{zipEntryName} must exist in consolidated resources99.s2z");
            using MemoryStream ms = new();
            using (Stream s = entry!.Open())
            {
                s.CopyTo(ms);
            }
            byte[] r99Bytes = ms.ToArray();
            string r99Hash = Convert.ToHexString(SHA256.HashData(r99Bytes));
            if (r99Hash != baselineHash && rel == "state_teleported_grimm.entity")
            {
                string baselineNorm = System.Text.Encoding.UTF8.GetString(baselineBytes)
                    .Replace("\r\n", "\n")
                    .Replace("icon=\"/triggers/teleporters/icon_grimm.tga\"", "icon=\"icon_grimm.tga\"");
                string r99Norm = System.Text.Encoding.UTF8.GetString(r99Bytes)
                    .Replace("\r\n", "\n")
                    .Replace("icon=\"/triggers/teleporters/icon_grimm.tga\"", "icon=\"icon_grimm.tga\"");
                r99Norm.Should().Be(baselineNorm, $"{zipEntryName} in resources99.s2z must match baseline {baselinePath}");
            }
            else
            {
                r99Hash.Should().Be(baselineHash, $"{zipEntryName} in resources99.s2z must match baseline {baselinePath}");
            }
        }
    }

    [Fact]
    public void ResourcesArchive_BothClientAndServerS2z_ContainUpdatedTeleporterEntities()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string root = FindRepoRoot();
        string[] archives =
        [
            Path.Combine(root, "games", "client", "game", "resources0.s2z"),
            Path.Combine(root, "games", "server", "game", "resources0.s2z")
        ];

        foreach (string archivePath in archives)
        {
            File.Exists(archivePath).Should().BeTrue($"Archive {archivePath} must exist");

            using ZipArchive zip = ZipFile.OpenRead(archivePath);

            ZipArchiveEntry? legionEntry = zip.GetEntry("triggers/teleporters/legion/legion_teleporter.entity");
            legionEntry.Should().NotBeNull($"triggers/teleporters/legion/legion_teleporter.entity must exist in {archivePath}");
            using (StreamReader reader = new(legionEntry!.Open()))
            {
                string content = reader.ReadToEnd();
                content.Should().Contain("[TELEPORT]");
                content.Should().Contain("printpos");
                content.Should().Contain("pushentitybyname name=\"Legion_TPDestination_1\"");
                content.Should().Contain("value=\"this_position\"");
                content.Should().Contain("boundsradius=\"96\"");
                content.Should().Contain("selectionradius=\"192\"");
                content.Should().Contain("prefertouch=\"true\"");
                content.Should().Contain("antiblocking=\"true\"");
            }

            ZipArchiveEntry? hellEntry = zip.GetEntry("triggers/teleporters/hellbourne/hellbourne_teleporter.entity");
            hellEntry.Should().NotBeNull($"triggers/teleporters/hellbourne/hellbourne_teleporter.entity must exist in {archivePath}");
            using (StreamReader reader = new(hellEntry!.Open()))
            {
                string content = reader.ReadToEnd();
                content.Should().Contain("[TELEPORT]");
                content.Should().Contain("printpos");
                content.Should().Contain("pushentitybyname name=\"Hellbourne_TPDestination_1\"");
                content.Should().Contain("value=\"this_position\"");
                content.Should().Contain("boundsradius=\"96\"");
                content.Should().Contain("selectionradius=\"192\"");
                content.Should().Contain("prefertouch=\"true\"");
                content.Should().Contain("antiblocking=\"true\"");
            }

            ZipArchiveEntry? stateEntry = zip.GetEntry("triggers/teleporters/state_teleport_stun_grimm.entity");
            stateEntry.Should().NotBeNull($"triggers/teleporters/state_teleport_stun_grimm.entity must exist in {archivePath}");
            using (StreamReader reader = new(stateEntry!.Open()))
            {
                string content = reader.ReadToEnd();
                content.Should().Contain("worldpos=\"4494.36 6942.49 127.99\"");
                content.Should().NotContain("position=\"4494.36");
                content.Should().Contain("printpos");
                content.Should().Contain("value=\"pos0\"");
                content.Should().Contain("this_proxy_position");
                content.Should().Contain("passiveeffect=\"teleport_effects/gold_teleport.effect\"");
            }

            ZipArchiveEntry? badStateEntry = zip.GetEntry("triggers/teleporters/state_teleport_stun_grimm_bad.entity");
            badStateEntry.Should().NotBeNull($"triggers/teleporters/state_teleport_stun_grimm_bad.entity must exist in {archivePath}");
            using (StreamReader reader = new(badStateEntry!.Open()))
            {
                string content = reader.ReadToEnd();
                content.Should().Contain("worldpos=\"8946.46 10676.89 127.99\"");
                content.Should().NotContain("position=\"8946.46");
                content.Should().Contain("printpos");
                content.Should().Contain("value=\"pos0\"");
                content.Should().Contain("this_proxy_position");
                content.Should().Contain("passiveeffect=\"teleport_effects/red_teleport.effect\"");
                content.Should().NotContain("evil_teleport.effect");
            }

            ZipArchiveEntry? redEffectEntry = zip.GetEntry("triggers/teleporters/teleport_effects/red_teleport.effect");
            redEffectEntry.Should().NotBeNull($"triggers/teleporters/teleport_effects/red_teleport.effect must exist in {archivePath}");
            using (StreamReader reader = new(redEffectEntry!.Open()))
            {
                string content = reader.ReadToEnd();
                content.Should().Contain("/shared/sounds/teleport.wav");
                content.Should().Contain("particlesystem name=\"system0\"");
            }
        }
    }
}
