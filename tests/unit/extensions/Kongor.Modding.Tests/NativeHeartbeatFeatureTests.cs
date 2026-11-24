// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using NLua;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive test suite for ProjectKongor's native low-health heartbeat feature:
///     Dual visual alert system (Hero Circle & Screen Border Vignette), dedicated in-game settings,
///     strict 1 HP - 20% trigger threshold, spectator/replay suppression, and Aegis/resurrection handling.
/// </summary>
public sealed class NativeHeartbeatFeatureTests
{
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

    [Fact]
    public void HeartbeatAssets_OnQuery_ExistAndAreNonEmptyAcrossAllLocations()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string gameLoc = Path.Combine(repoRoot, "games", "client", "game", "ui", "heartbeat");
        string r99Path = Path.Combine(repoRoot, "games", "client", "game", "resources99.s2z");

        string[] requiredAssets = ["bloodborder.tga", "hb.ogg", "hero_circle.tga"];

        Directory.Exists(gameLoc).Should().BeTrue($"Heartbeat asset directory must exist at {gameLoc}");
        foreach (string asset in requiredAssets)
        {
            string assetPath = Path.Combine(gameLoc, asset);
            File.Exists(assetPath).Should().BeTrue($"Asset {asset} must exist at {assetPath}");
            new FileInfo(assetPath).Length.Should().BeGreaterThan(0, $"Asset {asset} must not be empty");
        }

        File.Exists(r99Path).Should().BeTrue($"Consolidated archive must exist at {r99Path}");
        using ZipArchive zip = ZipFile.OpenRead(r99Path);
        foreach (string asset in requiredAssets)
        {
            ZipArchiveEntry? entry = zip.GetEntry($"ui/heartbeat/{asset}") ?? zip.GetEntry($"00000000/ui/heartbeat/{asset}");
            entry.Should().NotBeNull($"Asset {asset} must exist in consolidated resources99.s2z");
            entry!.Length.Should().BeGreaterThan(0, $"Asset {asset} in resources99.s2z must not be empty");
        }
    }

    [Fact]
    public void HeartbeatPackage_OnQuery_ExistsAndIsWellFormedXml()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] packagePaths =
        [
            Path.Combine(repoRoot, "games", "client", "game", "ui", "heartbeat.package")
        ];

        string r99Path = Path.Combine(repoRoot, "games", "client", "game", "resources99.s2z");
        List<string> xmlPayloads = [];
        foreach (string pkg in packagePaths)
        {
            File.Exists(pkg).Should().BeTrue($"heartbeat.package must exist at {pkg}");
            xmlPayloads.Add(File.ReadAllText(pkg));
        }

        using (ZipArchive zip = ZipFile.OpenRead(r99Path))
        {
            ZipArchiveEntry? entry = zip.GetEntry("ui/heartbeat.package");
            entry.Should().NotBeNull("ui/heartbeat.package must exist in resources99.s2z");
            using StreamReader sr = new(entry!.Open());
            xmlPayloads.Add(sr.ReadToEnd());
        }

        foreach (string xmlText in xmlPayloads)
        {
            var doc = XDocument.Parse(xmlText);
            doc.Root.Should().NotBeNull();
            doc.Root!.Name.LocalName.Should().Be("package");

            // Verify lua include and template definition
            xmlText.Should().Contain("<lua file=\"/ui/scripts/heartbeat_controller.lua\" />");
            xmlText.Should().Contain("<template name=\"heartbeat_overlay\">");
            xmlText.Should().Contain("name=\"hb_border_bg\"");
            xmlText.Should().Contain("name=\"hb_border_pulse\"");
            xmlText.Should().Contain("name=\"hb_hero_circle_panel\"");
            xmlText.Should().Contain("name=\"hb_hero_circle\"");
            xmlText.Should().Contain("/ui/heartbeat/bloodborder.tga");
            (xmlText.Contains("/ui/heartbeat/hero_circle.tga") || xmlText.Contains("/ui/heartbeat/hero_sphere_pulse.tga"))
                .Should().BeTrue("heartbeat.package must reference hero_circle.tga or hero_sphere_pulse.tga");
        }
    }

    [Fact]
    public void GameInterfaces_WhenInvoked_IncludeHeartbeatPackageAndOverlay()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] interfaces =
        [
            Path.Combine(repoRoot, "games", "client", "game", "ui", "game_apex.interface"),
            Path.Combine(repoRoot, "games", "client", "game", "ui", "game_legacy.interface")
        ];

        foreach (string iface in interfaces)
        {
            File.Exists(iface).Should().BeTrue($"Interface file must exist at {iface}");
            string content = File.ReadAllText(iface);
            content.Should().Contain("<include file=\"/ui/heartbeat.package\" />", $"Interface {iface} must include heartbeat.package");
            content.Should().Contain("<instance name=\"heartbeat_overlay\" />", $"Interface {iface} must instantiate heartbeat_overlay");
        }

        string r99Path = Path.Combine(repoRoot, "games", "client", "game", "resources99.s2z");
        using ZipArchive zip = ZipFile.OpenRead(r99Path);
        foreach (string entryName in new[] { "ui/game_apex.interface", "ui/game_legacy.interface" })
        {
            ZipArchiveEntry? entry = zip.GetEntry(entryName);
            entry.Should().NotBeNull($"{entryName} must exist in resources99.s2z");
            using StreamReader sr = new(entry!.Open());
            string content = sr.ReadToEnd();
            content.Should().Contain("<include file=\"/ui/heartbeat.package\" />");
            content.Should().Contain("<instance name=\"heartbeat_overlay\" />");
        }
    }

    [Fact]
    public void GameOptionsPackage_WhenInvoked_HasSubcategory10HeartbeatAndAllRequiredCvars()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] optionsPackages =
        [
            Path.Combine(repoRoot, "games", "client", "game", "ui", "fe2", "game_options.package"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "game_options.package")
        ];

        foreach (string pkg in optionsPackages)
        {
            File.Exists(pkg).Should().BeTrue($"game_options.package must exist at {pkg}");
            string content = File.ReadAllText(pkg);

            // Submenu registration in options_submenu4
            content.Should().Contain("<instance name=\"options_sub_menu\" cat=\"4\" id=\"10\" label=\"Heartbeat\" />");

            // Subcategory header in options_main_category4
            content.Should().Contain("<instance name=\"options_subcategory_header\" cat=\"4\" id=\"10\" label=\"Heartbeat\" />");

            // Required CVars in the settings controls
            content.Should().Contain("cvar=\"cg_heartbeat_enabled\"");
            content.Should().Contain("cvar=\"cg_heartbeat_threshold\"");
            content.Should().Contain("cvar=\"cg_heartbeat_sound\"");
            content.Should().Contain("cvar=\"cg_heartbeat_freq\"");
            content.Should().Contain("cvar=\"cg_heartbeat_show_border\"");
            content.Should().Contain("cvar=\"cg_heartbeat_border_pulse\"");
            content.Should().Contain("cvar=\"cg_heartbeat_border_bg\"");
            content.Should().Contain("cvar=\"cg_heartbeat_show_circle\"");
            content.Should().Contain("cvar=\"cg_heartbeat_circle_size\"");
            content.Should().Contain("cvar=\"cg_heartbeat_circle_opacity\"");

            // Verify XML validity
            var doc = XDocument.Parse(content);
            doc.Root.Should().NotBeNull();

            // Verify cvar declarations or KongorInitModCvar bootstrap with 20% default and enabled
            (content.Contains("<cvar name=\"cg_heartbeat_enabled\" type=\"bool\" value=\"true\" />") ||
             content.Contains("KongorInitModCvar('cg_heartbeat_enabled', true, 'bool')")).Should().BeTrue();
            (content.Contains("<cvar name=\"cg_heartbeat_threshold\" type=\"float\" value=\"0.20\" />") ||
             content.Contains("KongorInitModCvar('cg_heartbeat_threshold', 0.20, 'float')")).Should().BeTrue();
            (content.Contains("<cvar name=\"cg_heartbeat_sound\" type=\"bool\" value=\"true\" />") ||
             content.Contains("KongorInitModCvar('cg_heartbeat_sound', true, 'bool')")).Should().BeTrue();
            (content.Contains("<cvar name=\"cg_heartbeat_freq\" type=\"int\" value=\"80\" />") ||
             content.Contains("KongorInitModCvar('cg_heartbeat_freq', 80, 'int')")).Should().BeTrue();
            (content.Contains("<cvar name=\"cg_heartbeat_show_border\" type=\"bool\" value=\"true\" />") ||
             content.Contains("KongorInitModCvar('cg_heartbeat_show_border', true, 'bool')")).Should().BeTrue();
            (content.Contains("<cvar name=\"cg_heartbeat_show_circle\" type=\"bool\" value=\"true\" />") ||
             content.Contains("KongorInitModCvar('cg_heartbeat_show_circle', true, 'bool')")).Should().BeTrue();

            // Verify onloadlua SetSave registration (direct or via KongorInitModCvar helper)
            (content.Contains("SetSave('cg_heartbeat_enabled', true, 'bool', true)") ||
             content.Contains("SetSave(cvarName, defaultVal, cvarType, true)") ||
             content.Contains("pcall(SetSave, cvarName, defaultVal, cvarType, true)")).Should().BeTrue();

            // Verify slider onevent calls SetValue({cvar})
            content.Should().Contain("SetValue({cvar});");
        }
    }

    [Fact]
    public void EngineConfigurations_WhenLoaded_IncludeHeartbeatDefaults()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] configFiles =
        [
            Path.Combine(repoRoot, "games", "client", "startup.cfg"),
            Path.Combine(repoRoot, "games", "client", "autoexec.cfg"),
            Path.Combine(repoRoot, "games", "client", "game", "autoexec.cfg")
        ];

        foreach (string cfg in configFiles)
        {
            File.Exists(cfg).Should().BeTrue($"Config file must exist at {cfg}");
            string content = File.ReadAllText(cfg);
            content.Should().Contain("cg_heartbeat_enabled", $"Config {cfg} must configure cg_heartbeat_enabled");
            content.Should().Contain("cg_heartbeat_threshold", $"Config {cfg} must configure cg_heartbeat_threshold");
        }
    }

    [Fact]
    public void ModsState_WhenInvoked_DoesNotContainLegacyRobroboHeartbeatMod()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsStatePath = Path.Combine(repoRoot, "games", "client", "mods", "mods_state.json");
        File.Exists(modsStatePath).Should().BeTrue();

        string json = File.ReadAllText(modsStatePath);
        using var doc = JsonDocument.Parse(json);
        var enabledMods = doc.RootElement.GetProperty("EnabledMods");

        foreach (var mod in enabledMods.EnumerateArray())
        {
            string name = mod.GetString() ?? "";
            name.Should().NotContain("heartbeat", "Legacy robrobo heartbeat mod must be removed from EnabledMods");
            name.Should().NotContain("hbmod", "Legacy hbmod must be removed from EnabledMods");
        }
    }

    [Fact]
    public void HeartbeatController_NLua_BootstrapsCVars_EnforcesRange_AndGuardsSpectators()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "games", "shared", "ui", "scripts", "heartbeat_controller.lua");
        File.Exists(scriptPath).Should().BeTrue();

        using Lua lua = new();
        lua.LoadCLRPackage();

        Dictionary<string, object> cvars = new(StringComparer.OrdinalIgnoreCase);
        List<string> savedCvars = [];
        List<string> playedSounds = [];
        Dictionary<string, List<LuaFunction>> watches = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object> widgetMockStates = new(StringComparer.OrdinalIgnoreCase);

        // Register mock C# functions into Lua
        lua["_CS_CreateBool"] = new Action<string, bool>((cvar, val) => cvars[cvar] = val);
        lua["_CS_CreateFloat"] = new Action<string, double>((cvar, val) => cvars[cvar] = val);
        lua["_CS_GetCvarBool"] = new Func<string, bool>(cvar => cvars.TryGetValue(cvar, out var v) && (v is bool b ? b : (v?.ToString() == "1" || string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase))));
        lua["_CS_GetCvarFloat"] = new Func<string, double>(cvar => cvars.TryGetValue(cvar, out var v) && double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0.0);
        lua["_CS_GetCvarString"] = new Func<string, string>(cvar => cvars.TryGetValue(cvar, out var v) && v != null ? v.ToString() ?? string.Empty : string.Empty);
        lua["_CS_VarExists"] = new Func<string, bool>(cvar => cvars.ContainsKey(cvar));
        lua["_CS_SetSave"] = new Action<string, object>((cvar, val) =>
        {
            savedCvars.Add(cvar);
            string s = val?.ToString() ?? string.Empty;
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
                cvars[cvar] = true;
            else if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
                cvars[cvar] = false;
            else if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                cvars[cvar] = d;
            else if (val != null)
                cvars[cvar] = val;
        });
        lua["_CS_PlaySound"] = new Action<string>(snd => playedSounds.Add(snd));
        lua["_CS_RegisterWatch"] = new Action<string, LuaFunction>((name, fn) =>
        {
            if (!watches.TryGetValue(name, out var list))
            {
                list = [];
                watches[name] = list;
            }
            list.Add(fn);
        });

        // Initialize K2 Lua environment mocks
        lua.DoString(@"
            _G = _G or {}
            os = nil
            function getfenv(n) return _G end
            function RegisterScript2(name, ver) end
            function CreateBool(cvar, val) _CS_CreateBool(cvar, val) end
            function CreateFloat(cvar, val) _CS_CreateFloat(cvar, val) end
            function GetCvarBool(cvar) return _CS_GetCvarBool(cvar) end
            function GetCvarFloat(cvar) return _CS_GetCvarFloat(cvar) end
            function GetCvarString(cvar) return _CS_GetCvarString(cvar) end
            function VarExists(cvar) return _CS_VarExists(cvar) end
            function SetSave(cvar, val) _CS_SetSave(cvar, val) end
            function PlaySound(path) _CS_PlaySound(path) end

            local mockWidgets = {}
            local function createWidget(name)
                local w = {
                    name = name,
                    visible = false,
                    color = '1 1 1 1',
                    width = '100%',
                    height = '100%',
                    FadeIn = function(self, dur) self.visible = true end,
                    FadeOut = function(self, dur) self.visible = false end,
                    SetVisible = function(self, v) self.visible = v end,
                    IsVisible = function(self) return self.visible end,
                    BringToFront = function(self) end,
                    SetColor = function(self, c) self.color = c end,
                    SetWidth = function(self, w) self.width = w end,
                    SetHeight = function(self, h) self.height = h end,
                    RegisterWatch = function(self, name, fn) _CS_RegisterWatch(name, fn) end
                }
                mockWidgets[name] = w
                return w
            end

            createWidget('hb_border_bg')
            createWidget('hb_border_pulse')
            createWidget('hb_hero_circle_panel')
            createWidget('hb_hero_circle')

            function GetWidget(name) return mockWidgets[name] end
            function WidgetExists(name) return mockWidgets[name] ~= nil end

            object = {
                GetName = function() return 'game' end,
                RegisterWatch = function(self, name, fn) _CS_RegisterWatch(name, fn) end
            }

            _IsReplay = false
            _IsStreaming = false
            function ViewingReplay() return _IsReplay end
            function ViewingStreaming() return _IsStreaming end
        ");

        // Load the actual heartbeat_controller.lua
        lua.DoFile(scriptPath);

        // 1. Verify CVar Bootstrap
        cvars.Should().ContainKey("cg_heartbeat_enabled");
        cvars["cg_heartbeat_enabled"].Should().Be(true);
        cvars.Should().ContainKey("cg_heartbeat_threshold");
        ((double)cvars["cg_heartbeat_threshold"]).Should().BeApproximately(0.20, 0.001);
        cvars.Should().ContainKey("cg_heartbeat_sound");
        cvars["cg_heartbeat_sound"].Should().Be(true);
        cvars.Should().ContainKey("cg_heartbeat_freq");
        ((double)cvars["cg_heartbeat_freq"]).Should().BeApproximately(80.0, 0.001);
        cvars.Should().ContainKey("cg_heartbeat_show_border");
        cvars["cg_heartbeat_show_border"].Should().Be(true);
        cvars.Should().ContainKey("cg_heartbeat_show_circle");
        cvars["cg_heartbeat_show_circle"].Should().Be(false);

        savedCvars.Should().Contain("cg_heartbeat_enabled");
        savedCvars.Should().Contain("cg_heartbeat_threshold");
        savedCvars.Should().Contain("cg_heartbeat_freq");

        // Helper to trigger watches
        void FireHeroHealth(double hp, double maxHp, double pct)
        {
            if (watches.TryGetValue("HeroHealth", out var fns))
            {
                foreach (var fn in fns) fn.Call(null, hp.ToString(), maxHp.ToString(), pct.ToString(), pct.ToString());
            }
        }

        void FireHeroStatus(bool alive)
        {
            if (watches.TryGetValue("HeroStatus", out var fns))
            {
                foreach (var fn in fns) fn.Call(null, alive ? "1" : "0");
            }
        }

        void FireMatchTime(int ms)
        {
            if (watches.TryGetValue("MatchTime", out var fns))
            {
                foreach (var fn in fns) fn.Call(null, ms.ToString());
            }
        }

        bool IsCritical() => (bool)lua.DoString("return Heartbeat.isCritical")[0];
        bool IsPulseActive() => (bool)lua.DoString("return Heartbeat.isPulseActive")[0];

        // 2. High Health (100% = 1000/1000) -> INACTIVE
        FireHeroHealth(1000, 1000, 1.0);
        IsCritical().Should().BeFalse("Heartbeat must not activate at full health");

        // 3. Medium Health (30% = 300/1000) -> INACTIVE (Fixes Blood Hunter / hbmod 20-40% bug)
        FireHeroHealth(300, 1000, 0.30);
        IsCritical().Should().BeFalse("Heartbeat must NOT activate at 30% health (resolving Blood Hunter / hbmod 20-40% range bug)");

        // 4. Critical Health (20% = 200/1000) -> ACTIVE
        FireHeroHealth(200, 1000, 0.20);
        IsCritical().Should().BeTrue("Heartbeat must activate at 20% health");

        // 5. Critical Health (1 HP = 1/1000) -> ACTIVE
        FireHeroHealth(1, 1000, 0.001);
        IsCritical().Should().BeTrue("Heartbeat must remain active at 1 HP");

        // 6. Audio Throttling & Visual Pulse on MatchTime
        playedSounds.Clear();
        FireMatchTime(1000); // 1000ms: first pulse triggered
        playedSounds.Should().Contain("/ui/heartbeat/hb.ogg");
        IsPulseActive().Should().BeTrue();

        playedSounds.Clear();
        FireMatchTime(1100); // 1100ms: only 100ms later (freq 80 BPM = 750ms interval), must NOT trigger sound
        playedSounds.Should().BeEmpty("Audio must be throttled to prevent channel clipping");

        FireMatchTime(1800); // 1800ms: 800ms >= 750ms interval, triggers next pulse
        playedSounds.Should().Contain("/ui/heartbeat/hb.ogg");

        // 7. Death (0 HP) -> IMMEDIATELY DEACTIVATED
        FireHeroHealth(0, 1000, 0.0);
        IsCritical().Should().BeFalse("Heartbeat must immediately deactivate when health reaches 0");

        // 8. Death via HeroStatus event -> IMMEDIATELY DEACTIVATED
        FireHeroHealth(150, 1000, 0.15); // Trigger active
        IsCritical().Should().BeTrue();
        FireHeroStatus(false); // Dead
        IsCritical().Should().BeFalse("Heartbeat must immediately deactivate on HeroStatus dead event");

        // 9. Aegis / Token Resurrection -> Reactivates cleanly if respawning with low health
        FireHeroStatus(true); // Resurrected while health is still 150
        IsCritical().Should().BeTrue("Heartbeat must cleanly reactivate upon resurrection if health is below threshold");

        // 10. Healed above threshold (e.g. to 40%) -> Deactivates cleanly
        FireHeroHealth(400, 1000, 0.40);
        IsCritical().Should().BeFalse("Heartbeat must fade out when healed above threshold");

        // 11. Spectator / Replay Suppression Guard
        FireHeroHealth(150, 1000, 0.15);
        IsCritical().Should().BeTrue();
        lua.DoString("_IsReplay = true");
        FireHeroHealth(100, 1000, 0.10);
        IsCritical().Should().BeFalse("Heartbeat alerts must be suppressed when watching replays");
    }
}
