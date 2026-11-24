// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class HotReloadWatcherTests
{
    [Fact]
    public void ValidateLuaSyntax_ValidScript_ReturnsTrue()
    {
        // Arrange
        string validLua = """
                          local Nexus = {}
                          function Nexus.Init(param)
                              if param > 0 then
                                  Echo("Active: " .. tostring(param))
                              else
                                  Echo("Idle")
                              end
                          end
                          return Nexus
                          """;

        // Act
        (bool isValid, string? errorMessage) = HotReloadWatcherService.ValidateLuaSyntaxString(validLua);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateLuaSyntax_StringsWithCommentDelimiters_ReturnsTrue()
    {
        // Arrange
        string luaWithCommentInStrings = """
            local msg1 = "SELECT * FROM users WHERE note = '--foo' and active = 1"
            local msg2 = 'User prompt: -- this is not a comment'
            local rawStr = [=[
                function inside_raw_string()
                    -- this is raw [[ text ]]
                end
            ]=]
            function Run()
                local x = 42
                return x
            end
            """;

        // Act
        (bool isValid, string? errorMessage) = HotReloadWatcherService.ValidateLuaSyntaxString(luaWithCommentInStrings);

        // Assert
        Assert.True(isValid, errorMessage);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateLuaSyntax_UnclosedFunction_ReturnsFalseWithError()
    {
        // Arrange
        string brokenLua = """
                           function BrokenFunction()
                               if true then
                                   Echo("Missing end")
                           """;

        // Act
        (bool isValid, string? errorMessage) = HotReloadWatcherService.ValidateLuaSyntaxString(brokenLua);

        // Assert
        Assert.False(isValid);
        Assert.NotNull(errorMessage);
        Assert.Contains("Unbalanced blocks", errorMessage);
    }

    [Fact]
    public void ValidateLuaSyntax_UnmatchedParenthesis_ReturnsFalse()
    {
        // Arrange
        string brokenLua = """
                           local x = (10 + 20
                           function Test()
                           end
                           """;

        // Act
        (bool isValid, string? errorMessage) = HotReloadWatcherService.ValidateLuaSyntaxString(brokenLua);

        // Assert
        Assert.False(isValid);
        Assert.NotNull(errorMessage);
        Assert.Contains("parenthesis", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VfsAuditService_AuditArchives_DetectsShadowedArchiveEntriesAndHazardousNesting()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "VfsAudit_Test_" + Guid.NewGuid().ToString("N"));
        string overrideDir = Path.Combine(tempDir, "override");
        string gameDir = Path.Combine(tempDir, "game");

        Directory.CreateDirectory(overrideDir);
        Directory.CreateDirectory(gameDir);

        try
        {
            // Create test resources0.s2z (zip archive)
            string s2zPath = Path.Combine(gameDir, "resources0.s2z");
            using (ZipArchive zip = ZipFile.Open(s2zPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry1 = zip.CreateEntry("ui/scripts/store.lua");
                using StreamWriter writer = new(entry1.Open());
                writer.Write("-- retail store.lua");
            }

            // Create valid override file shadowing archive
            string overrideStoreDir = Path.Combine(overrideDir, "ui", "scripts");
            Directory.CreateDirectory(overrideStoreDir);
            File.WriteAllText(Path.Combine(overrideStoreDir, "store.lua"), "-- modded store.lua");

            // Create nested hazard file (override/override/...)
            string nestedHazardDir = Path.Combine(overrideDir, "override", "ui");
            Directory.CreateDirectory(nestedHazardDir);
            File.WriteAllText(Path.Combine(nestedHazardDir, "hazard.lua"), "-- hazard");

            // Act
            VfsAuditService service = new(NullLogger<VfsAuditService>.Instance);
            VfsAuditReport report = service.PerformAudit(tempDir);

            // Assert
            Assert.Equal(2, report.TotalLooseFiles);
            Assert.Equal(1, report.TotalShadowedFiles);
            Assert.Equal(1, report.TotalWarnings);
            Assert.Single(report.ScannedArchives);

            VfsAuditItem? storeItem = report.Items.Find(i =>
                i.VfsPath.Equals("/ui/scripts/store.lua", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(storeItem);
            Assert.True(storeItem.IsShadowingArchive);
            Assert.Equal("resources0.s2z", storeItem.ShadowedArchiveName);

            VfsAuditItem? hazardItem =
                report.Items.Find(i => i.VfsPath.Contains("hazard.lua", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(hazardItem);
            Assert.True(hazardItem.HasPathWarning);
            Assert.Contains("PowerShell Nesting Hazard", hazardItem.PathWarningMessage);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void VfsAuditService_SyncOverridesToArchives_UpdatesShadowedEntries()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "VfsSync_Test_" + Guid.NewGuid().ToString("N"));
        string overrideDir = Path.Combine(tempDir, "override", "ui", "scripts");
        string gameDir = Path.Combine(tempDir, "game");

        Directory.CreateDirectory(overrideDir);
        Directory.CreateDirectory(gameDir);

        try
        {
            string s2zPath = Path.Combine(gameDir, "resources0.s2z");
            using (ZipArchive zip = ZipFile.Open(s2zPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("ui/scripts/global_main.lua");
                using StreamWriter writer = new(entry.Open());
                writer.Write("-- old global_main");
            }

            File.WriteAllText(Path.Combine(overrideDir, "global_main.lua"), "-- new fixed global_main with gmatch");

            // Act
            VfsAuditService service = new(NullLogger<VfsAuditService>.Instance);
            int synced = service.SyncOverridesToArchives(tempDir);

            // Assert
            Assert.Equal(1, synced);

            using (ZipArchive zip = ZipFile.OpenRead(s2zPath))
            {
                ZipArchiveEntry? updatedEntry = zip.GetEntry("ui/scripts/global_main.lua");
                Assert.NotNull(updatedEntry);
                using StreamReader reader = new(updatedEntry.Open());
                string content = reader.ReadToEnd();
                Assert.Contains("with gmatch", content);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void VfsAuditService_SyncGameClientOverrides_IfClientPresent_PerformsFullSync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VfsClientSync_Test_" + Guid.NewGuid().ToString("N"));
        string overrideDir = Path.Combine(tempDir, "override", "ui", "scripts");
        string gameDir = Path.Combine(tempDir, "game");

        Directory.CreateDirectory(overrideDir);
        Directory.CreateDirectory(gameDir);

        try
        {
            string s2zPath = Path.Combine(gameDir, "resources0.s2z");
            using (ZipArchive zip = ZipFile.Open(s2zPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry1 = zip.CreateEntry("ui/scripts/store.lua");
                using (StreamWriter writer1 = new(entry1.Open()))
                {
                    writer1.Write("-- retail store");
                }

                ZipArchiveEntry entry2 = zip.CreateEntry("ui/scripts/store2.lua");
                using StreamWriter writer2 = new(entry2.Open());
                writer2.Write("-- retail store2");
            }

            File.WriteAllText(Path.Combine(overrideDir, "store.lua"), "-- override store with improvements");
            File.WriteAllText(Path.Combine(overrideDir, "store2.lua"), "-- override store2 with improvements");

            VfsAuditService service = new(NullLogger<VfsAuditService>.Instance);
            int synced = service.SyncOverridesToArchives(tempDir);
            Assert.Equal(2, synced);

            using (ZipArchive zip = ZipFile.OpenRead(s2zPath))
            {
                ZipArchiveEntry? entry = zip.GetEntry("ui/scripts/store.lua");
                Assert.NotNull(entry);
                using StreamReader reader = new(entry.Open());
                string content = reader.ReadToEnd();
                Assert.Contains("improvements", content);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void StoreScripts_ValidateSyntax_ConfirmsValidSyntax()
    {
        string validStoreLua = """
                               local Store = {}
                               function Store.Init()
                                   Store.Items = { "item1", "item2" }
                               end
                               function Store.GetCount()
                                   return #Store.Items
                               end
                               return Store
                               """;

        string tempFile = Path.Combine(Path.GetTempPath(), "store_test_" + Guid.NewGuid().ToString("N") + ".lua");
        try
        {
            File.WriteAllText(tempFile, validStoreLua);
            (bool isValid, string? err) = HotReloadWatcherService.ValidateLuaSyntax(tempFile);
            Assert.True(isValid, err);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private static string ResolveClientAssetPath(string relativeSubPath)
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string overridePath = Path.Combine(repoRoot, "games", "client", "override", relativeSubPath);
        if (File.Exists(overridePath))
        {
            return overridePath;
        }

        string packagePath = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", relativeSubPath);
        if (File.Exists(packagePath))
        {
            return packagePath;
        }

        return Path.Combine(repoRoot, "games", "client", "game", relativeSubPath);
    }

    [Fact]
    public void MainV2Script_ValidateSyntax_ConfirmsValidSyntax()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string path = ResolveClientAssetPath(Path.Combine("ui", "scripts", "main_v2.lua"));
        Assert.True(File.Exists(path), $"File must exist at: {path}");
        (bool isValid, string? err) = HotReloadWatcherService.ValidateLuaSyntax(path);
        Assert.True(isValid, err);
    }

    [Fact]
    public void MainV2Interface_ValidXml_ConfirmsWellFormed()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string path = ResolveClientAssetPath(Path.Combine("ui", "fe2", "main_v2.interface"));
        Assert.True(File.Exists(path), $"File must exist at: {path}");
        var doc = System.Xml.Linq.XDocument.Load(path);
        Assert.NotNull(doc.Root);
        var escPanel = doc.Root.Descendants("panel").FirstOrDefault(p => (string?)p.Attribute("name") == "main_esc_options_hotkey");
        Assert.NotNull(escPanel);
        string onhotkeylua = (string?)escPanel.Attribute("onhotkeylua") ?? string.Empty;
        Assert.Contains("MainMenu_HandleEscape", onhotkeylua);

        string luaPath = ResolveClientAssetPath(Path.Combine("ui", "scripts", "main_v2.lua"));
        Assert.True(File.Exists(luaPath), $"File must exist at: {luaPath}");
        string luaCode = File.ReadAllText(luaPath);
        Assert.Contains("main_midbar_left", luaCode);
        Assert.Contains("fe2_background_new", luaCode);
        Assert.Contains("_uiReloadCount", luaCode);
    }

    [Fact]
    public void BackgroundV2Package_ValidXml_ConfirmsWellFormed()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string path = ResolveClientAssetPath(Path.Combine("ui", "fe2", "newui", "background_v2.package"));
        Assert.True(File.Exists(path), $"File must exist at: {path}");
        var doc = System.Xml.Linq.XDocument.Load(path);
        Assert.NotNull(doc.Root);
        Assert.Equal("package", doc.Root.Name.LocalName);
    }

    [Fact]
    public void GamePhaseLoginGuard_AllMirrorsAndFiles_GuardsPresentAndCorrect()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string[] interfacePaths = new[]
        {
            Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "main_v2.interface"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "main_v2.interface"),
            Path.Combine(repoRoot, "games", "shared", "override", "ui", "fe2", "main_v2.interface"),
            Path.Combine(repoRoot, "games", "shared", "ui", "fe2", "main_v2.interface")
        }.Where(File.Exists).ToArray();
        Assert.NotEmpty(interfacePaths);

        foreach (string ifacePath in interfacePaths)
        {
            string xml = File.ReadAllText(ifacePath);
            Assert.Contains("name=\"prelogin_loginbox_disabled_login\"", xml);
            Assert.Contains("watch=\"LoginStatus GamePhase\"", xml);
            Assert.Contains("name=\"prelogin_loginbox\" width=\"56.0h\" height=\"56.0h\" align=\"center\" valign=\"center\" noclick=\"1\"", xml);
            Assert.Contains("x=\"44.0h\" y=\"-10.0h\"", xml);
            Assert.Contains("prelogin_loginbox_contents\" width=\"100%\" height=\"100%\" color=\"invisible\" noclick=\"1\"", xml);
            Assert.Contains("watch=\"LoginStatus GamePhase\"", xml);
            Assert.Contains("local phase = (UIGamePhase and UIGamePhase()) or (interface and interface.UICmd and AtoN(interface:UICmd('GetCurrentGamePhase()'))) or 0", xml);
            Assert.Contains("name=\"main_status\" width=\"56.0h\" height=\"56.0h\" align=\"center\" valign=\"center\" x=\"44.0h\" y=\"-10.0h\" visible=\"0\" noclick=\"1\"", xml);
            Assert.Contains("watch=\"LoginStatus GamePhase\"", xml);
        }

        string[] luaPaths = new[]
        {
            Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "client", "game", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "override", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "ui", "scripts", "main_v2.lua")
        }.Where(File.Exists).ToArray();
        Assert.NotEmpty(luaPaths);

        foreach (string luaPath in luaPaths)
        {
            (bool isValid, string? err) = HotReloadWatcherService.ValidateLuaSyntax(luaPath);
            Assert.True(isValid, $"Lua syntax check failed on {luaPath}: {err}");
            string lua = File.ReadAllText(luaPath);
            Assert.Contains("local function LoggedOut()", lua);
            Assert.Contains("local function CheckPostLoadLoginState()", lua);
            Assert.Contains("local function LoginStatus(self, accountStatus, statusDescription, isLoggedIn, pwordExpired, isLoggedInChanged, updaterStatus)", lua);
            Assert.Contains("local function CheckForAutoLogin()", lua);
            Assert.Contains("tonumber(phase) > 0", lua);
            Assert.DoesNotContain("HideBackgroundScene2(hideImmediately)\r\n\tif GetWidget('hellbourne_scene'", lua.Replace("\n", "\r\n").Contains("tonumber(gamePhase) > 0") ? "FAIL" : "NOT_PRESENT");
        }

        string[] packagePaths = new[]
        {
            Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "newui", "background_v2.package"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "newui", "background_v2.package"),
            Path.Combine(repoRoot, "games", "shared", "override", "ui", "fe2", "newui", "background_v2.package"),
            Path.Combine(repoRoot, "games", "shared", "ui", "fe2", "newui", "background_v2.package")
        }.Where(File.Exists).ToArray();
        Assert.NotEmpty(packagePaths);

        foreach (string pkgPath in packagePaths)
        {
            string xml = File.ReadAllText(pkgPath);
            Assert.Contains("fe2_hero_3d_container\" width=\"100%\" height=\"100%\" align=\"center\" valign=\"center\" noclick=\"1\" visible=\"1\"", xml);
            Assert.Contains("login_hero_pedestal\"", xml);
            Assert.Contains("login_hero_viewport\"", xml);
            Assert.Contains("main_logo\"", xml);
            Assert.Contains("main_logo_fb\"", xml);
            Assert.Contains("watch=\"GamePhase LoginStatus\"", xml);
            Assert.Contains("ontrigger=\"SetVisible(GetCurrentGamePhase() == 0 and !IsLoggedIn() and !_loggedin);\"", xml);
        }
    }

    [Fact]
    public void ValidateLoginScreenAndRealCommunicatorParity()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;

        string[] mainLuaPaths = new[]
        {
            Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "override", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "client", "game", "ui", "scripts", "main_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "ui", "ui", "scripts", "main_v2.lua")
        }.Where(File.Exists).ToArray();
        Assert.NotEmpty(mainLuaPaths);

        foreach (string luaPath in mainLuaPaths)
        {
            (bool isValid, string? err) = HotReloadWatcherService.ValidateLuaSyntax(luaPath);
            Assert.True(isValid, $"Lua syntax check failed on {luaPath}: {err}");

            string lua = File.ReadAllText(luaPath);
            Assert.Contains("local isHotReload = (_G._isSubsequentShow == true) or ((_G._uiReloadCount or 0) > 0)", lua);
            Assert.Contains("local nativeLoggedIn = (type(IsLoggedIn) == 'function' and IsLoggedIn())", lua);
            Assert.Contains("local honMainLoggedIn = (HoN_Main and HoN_Main.isFullyLoggedIn == true)", lua);
            Assert.Contains("local loggedIn = isHotReload and nativeLoggedIn and honMainLoggedIn", lua);
            Assert.DoesNotContain("GetAccountName() ~= ''", lua);
            Assert.Contains("Set('_loggedin', false, 'bool')", lua);
            Assert.Contains("Set('ui_isLoggedIn', false, 'bool')", lua);
            Assert.Contains("Set('ui_suppressAutoLogin', true, 'bool')", lua);
        }

        string[] commLuaPaths = new[]
        {
            Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "newui", "communicator_v2.lua"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "newui", "communicator_v2.lua"),
            Path.Combine(repoRoot, "games", "client", "game", "ui", "scripts", "newui", "communicator_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "override", "ui", "scripts", "newui", "communicator_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "ui", "scripts", "newui", "communicator_v2.lua"),
            Path.Combine(repoRoot, "games", "shared", "ui", "ui", "scripts", "newui", "communicator_v2.lua")
        }.Where(File.Exists).ToArray();
        Assert.NotEmpty(commLuaPaths);

        foreach (string luaPath in commLuaPaths)
        {
            (bool isValid, string? err) = HotReloadWatcherService.ValidateLuaSyntax(luaPath);
            Assert.True(isValid, $"Lua syntax check failed on {luaPath}: {err}");

            string lua = File.ReadAllText(luaPath);
            Assert.Contains("local isHotReload = (_G._isSubsequentShow == true) or ((_G._uiReloadCount or 0) > 0)", lua);
            Assert.Contains("local nativeLoggedIn = (type(IsLoggedIn) == 'function' and IsLoggedIn())", lua);
            Assert.Contains("local honMainLoggedIn = (HoN_Main and HoN_Main.isFullyLoggedIn == true)", lua);
            Assert.Contains("local loggedIn = isHotReload and nativeLoggedIn and honMainLoggedIn", lua);
            Assert.DoesNotContain("GetAccountName() ~= ''", lua);
            Assert.DoesNotContain("GetAccountID() or 0", lua);
            Assert.DoesNotContain("UIGetAccountID() or 0", lua);
            Assert.DoesNotContain("PopulateDefaultChannelUsers", lua);
            Assert.DoesNotContain("defaultRosters", lua);
            Assert.DoesNotContain("GM_BreakyCPK", lua);
            Assert.Contains("function Communicator_V2:OnChatUserNames(...)", lua);
        }

        string clientS2z = Path.Combine(repoRoot, "games", "client", "game", "resources0.s2z");
        if (File.Exists(clientS2z))
        {
            using ZipArchive zip = ZipFile.OpenRead(clientS2z);
            ZipArchiveEntry? mainEntry = zip.GetEntry("ui/scripts/main_v2.lua");
            if (mainEntry != null)
            {
                using StreamReader sr = new(mainEntry.Open());
                string archiveLua = sr.ReadToEnd();
                Assert.DoesNotContain("GetAccountName() ~= ''", archiveLua);
            }
        }
    }
}
