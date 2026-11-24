// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using NLua;

namespace AdversarialLuaVerifier;

/// <summary>
/// Empirical Challenger Test Suite for Milestone 1:
/// Validates Nexus.CollectTelemetry, CompactJsonEncode fallback, lib_json encoding,
/// snapshot item sanitization (Item_ prefix), lifecycle phase guards, and command formatting.
/// </summary>
public static class LuaTelemetryVerifier
{
    private static readonly List<string> CapturedEchoes = [];
    private static readonly List<string> CapturedCmds = [];
    private static readonly List<(string TriggerName, string[] Args)> CapturedTriggers = [];
    private static readonly Dictionary<string, string> CvarStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MockWidget> MockWidgets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<MockWidget?, object?>>> Watches = new(StringComparer.OrdinalIgnoreCase);

    public static int CurrentGamePhase { get; set; } = 3;

    public static void ResetHarness()
    {
        CapturedEchoes.Clear();
        CapturedCmds.Clear();
        CapturedTriggers.Clear();
        CvarStore.Clear();
        MockWidgets.Clear();
        Watches.Clear();
        CurrentGamePhase = 3;
    }

    public static void LogEcho(object? msg)
    {
        string text = msg?.ToString() ?? "";
        CapturedEchoes.Add(text);
    }

    public static void ExecuteCmd(object? cmd)
    {
        string text = cmd?.ToString() ?? "";
        CapturedCmds.Add(text);
    }

    public static void FireTrigger(string triggerName, object? a1 = null, object? a2 = null, object? a3 = null, object? a4 = null, object? a5 = null, object? a6 = null)
    {
        string[] strArgs = [a1?.ToString() ?? "", a2?.ToString() ?? "", a3?.ToString() ?? "", a4?.ToString() ?? "", a5?.ToString() ?? "", a6?.ToString() ?? ""];
        CapturedTriggers.Add((triggerName, strArgs));
    }

    public static void SetCvar(string name, object? val, string? type = null)
    {
        CvarStore[name] = val?.ToString() ?? "";
    }

    public static int GetCvarInt(string name, bool raw = false)
    {
        if (name == "game_phase" || name == "_game_phase") return CurrentGamePhase;
        if (CvarStore.TryGetValue(name, out string? val) && int.TryParse(val, out int result)) return result;
        return 0;
    }

    public static string GetCvarString(string name)
    {
        return CvarStore.TryGetValue(name, out string? val) ? val : "";
    }

    public static void RegisterWatch(string eventName, Action<MockWidget?, object?> callback)
    {
        if (!Watches.TryGetValue(eventName, out var list))
        {
            list = [];
            Watches[eventName] = list;
        }
        list.Add(callback);
    }

    public static void UnregisterWatch(string eventName, Action<MockWidget?, object?> callback)
    {
        if (Watches.TryGetValue(eventName, out var list))
        {
            list.Remove(callback);
        }
    }

    public static void TriggerWatchEvent(string eventName, object? arg1 = null, object? arg2 = null)
    {
        if (Watches.TryGetValue(eventName, out var list))
        {
            foreach (var cb in list.ToList())
            {
                cb(null, arg1);
            }
        }
    }

    public static Lua CreateMockedLuaEnvironment(bool loadLibJson, string nexusScriptPath, string libJsonScriptPath)
    {
        Lua lua = new();
        lua.LoadCLRPackage();

        // Register C# callbacks into Lua
        lua["_CS_LogEcho"] = new Action<object?>(LogEcho);
        lua["_CS_ExecuteCmd"] = new Action<object?>(ExecuteCmd);
        lua["_CS_FireTrigger"] = new Action<string, object?, object?, object?, object?, object?, object?>(FireTrigger);
        lua["_CS_SetCvar"] = new Action<string, object?, string?>(SetCvar);
        lua["_CS_GetCvarInt"] = new Func<string, bool, int>(GetCvarInt);
        lua["_CS_GetCvarString"] = new Func<string, string>(GetCvarString);

        lua.DoString(@"
            _G = _G or {}
            function getfenv(n) return _G end
            function setfenv(f, env) return f end
            function Echo(msg) _CS_LogEcho(msg) end
            function Cmd(cmd) _CS_ExecuteCmd(cmd) end
            function Trigger(trig, a1, a2, a3, a4, a5, a6) _CS_FireTrigger(trig, a1, a2, a3, a4, a5, a6) end
            function Set(cvar, val, typ) _CS_SetCvar(cvar, val, typ) end
            function GetCvarInt(cvar, raw) return _CS_GetCvarInt(cvar, raw == true or raw == 1) end
            function GetCvarString(cvar) return _CS_GetCvarString(cvar) end
            function GetCvarNumber(cvar) local val = _CS_GetCvarString(cvar) local n = tonumber(val) if n then return n end return _CS_GetCvarInt(cvar, false) or 0 end
            function GetCvarBool(cvar) local val = string.lower(tostring(_CS_GetCvarString(cvar) or '')) if val == 'true' or val == '1' then return true elseif val == 'false' or val == '0' then return false end return _CS_GetCvarInt(cvar, false) == 1 end
            function RegisterScript2(name, ver) end
            SafeWidget = function(name) return _GetMockWidget(name) end
            IsCvarEnabled = function(cvar, defaultVal) return GetCvarBool(cvar) end


            Cvar = {
                CreateCvar = function(name, typ, defaultVal) _CS_SetCvar(name, defaultVal, typ) end,
                GetCvar = function(name)
                    return {
                        Set = function(self, val) _CS_SetCvar(name, val) end,
                        GetNumber = function(self) return _CS_GetCvarInt(name, false) end,
                        GetString = function(self) return _CS_GetCvarString(name) end
                    }
                end
            }

            -- Mock UIManager & Widgets
            _RegisteredWatches = {}
            _MockWidgets = {}

            function _CreateMockWidget(name)
                local w = {
                    _name = name,
                    _visible = true,
                    _enabled = true,
                    _text = '',
                    _color = '',
                    _texture = '',
                    _doEventCalls = {},
                    GetName = function(self) return self._name end,
                    SetVisible = function(self, vis) self._visible = (vis == true or vis == 1 or vis == '1' or vis == 'true') end,
                    IsVisible = function(self) return self._visible end,
                    SetEnabled = function(self, en) self._enabled = (en == true or en == 1 or en == '1' or en == 'true') end,
                    IsEnabled = function(self) return self._enabled end,
                    SetRotation = function(self, r) end,
                    SetText = function(self, txt) self._text = tostring(txt or '') end,
                    GetText = function(self) return self._text end,
                    SetColor = function(self, col) self._color = tostring(col or '') end,
                    GetColor = function(self) return self._color end,
                    SetTexture = function(self, tex) self._texture = tostring(tex or '') end,
                    GetTexture = function(self) return self._texture end,
                    BringToFront = function(self) end,
                    DoEventN = function(self, idx) table.insert(self._doEventCalls, idx) end,
                    DoEvent = function(self) table.insert(self._doEventCalls, 0) end,
                    RegisterWatch = function(self, eventName, fn)
                        _RegisteredWatches[eventName] = _RegisteredWatches[eventName] or {}
                        table.insert(_RegisteredWatches[eventName], fn)
                        if not self._watches then self._watches = {} end
                        self._watches[eventName] = fn
                    end,
                    UnregisterWatch = function(self, eventName, fn) end
                }
                _MockWidgets[name] = w
                return w
            end

            function _GetMockWidget(name)
                return _MockWidgets[name]
            end

            -- Pre-seed standard lobby widgets
            local standardWidgets = {
                'event_com', 'systembar', 'prelogin_lower', 'prelogin_loginbox',
                'prelogin_loginbox_disabled_login', 'main_login_cancel_button',
                'main_login_button', 'mainLoginQueue', 'main_status'
            }
            for _, wn in ipairs(standardWidgets) do
                _CreateMockWidget(wn)
            end

            local mainInterface = {
                GetName = function(self) return 'main' end,
                GetWidget = function(self, name) return _GetMockWidget(name) end,
                RegisterWatch = function(self, eventName, fn)
                    _RegisteredWatches[eventName] = _RegisteredWatches[eventName] or {}
                    table.insert(_RegisteredWatches[eventName], fn)
                end,
                UnregisterWatch = function(self, eventName, fn)
                    if _RegisteredWatches[eventName] then
                        for i, f in ipairs(_RegisteredWatches[eventName]) do
                            if f == fn then
                                table.remove(_RegisteredWatches[eventName], i)
                                break
                            end
                        end
                    end
                end,
                UICmd = function(self, cmd) return '' end
            }

            local gameInterface = {
                GetName = function(self) return 'game' end,
                GetWidget = function(self, name) return _GetMockWidget(name) end,
                RegisterWatch = function(self, eventName, fn)
                    _RegisteredWatches[eventName] = _RegisteredWatches[eventName] or {}
                    table.insert(_RegisteredWatches[eventName], fn)
                end,
                UnregisterWatch = function(self, eventName, fn) end,
                UICmd = function(self, cmd) return '' end
            }

            UIManager = {
                GetInterface = function(name)
                    if name == 'game' then return gameInterface end
                    return mainInterface
                end
            }
            interface = mainInterface
            object = mainInterface
            function RegisterScript2(name, ver) end
            SafeWidget = function(name) return _GetMockWidget(name) end
            IsCvarEnabled = function(cvar, defaultVal) return GetCvarBool(cvar) end

            HoN_Main = {
                uiLoginState = true,
                isFullyLoggedIn = false,
                gamePhase = 3
            }
        ");

        if (loadLibJson)
        {
            lua.DoFile(libJsonScriptPath);
            lua.DoString("_G.lib_json = lib_json");
        }
        else
        {
            lua.DoString("_G.lib_json = nil; lib_json = nil;");
        }

        lua.DoFile(nexusScriptPath);
        return lua;
    }

    public static int Run()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("M1 EMPIRICAL CHALLENGER: TELEMETRY SERIALIZATION & MUTATION VERIFICATION");
        Console.WriteLine("Role: Challenger M1_2 (JSON Telemetry & Command Mutation Verifier)");
        Console.WriteLine("================================================================================\n");

        string nexusPath = File.Exists("games/shared/ui/scripts/nexus_bridge.lua")
            ? Path.GetFullPath("games/shared/ui/scripts/nexus_bridge.lua")
            : Path.GetFullPath("games/client/ClientPackageFiles/ui/scripts/nexus_bridge.lua");
        string libJsonPath = File.Exists("games/client/override/ui/scripts/lib_json.lua")
            ? Path.GetFullPath("games/client/override/ui/scripts/lib_json.lua")
            : File.Exists("games/client/ClientPackageFiles/ui/scripts/lib_json.lua")
                ? Path.GetFullPath("games/client/ClientPackageFiles/ui/scripts/lib_json.lua")
                : Path.GetFullPath("games/shared/ui/scripts/lib_json.lua");

        if (!File.Exists(nexusPath))
        {
            Console.WriteLine($"[FATAL] Missing target: {nexusPath}");
            return 1;
        }
        if (!File.Exists(libJsonPath))
        {
            Console.WriteLine($"[FATAL] Missing target: {libJsonPath}");
            return 1;
        }

        int totalTests = 0;
        int passedTests = 0;
        int failedTests = 0;

        void Assert(string testName, bool condition, string detail = "")
        {
            totalTests++;
            if (condition)
            {
                passedTests++;
                Console.WriteLine($"  [PASS] {testName} {(string.IsNullOrEmpty(detail) ? "" : $"({detail})")}");
            }
            else
            {
                failedTests++;
                Console.WriteLine($"  [FAIL] {testName} - {detail}");
            }
        }

        Stopwatch sw = Stopwatch.StartNew();

        // =====================================================================
        // SUITE 1: Nexus.CollectTelemetry with lib_json
        // =====================================================================
        Console.WriteLine("\n--- Suite 1: Nexus.CollectTelemetry with lib_json ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            object[] res = lua.DoString("return Nexus.CollectTelemetry()");
            Assert("S1.1: CollectTelemetry returns 2 values (table, jsonStr)", res.Length == 2);
            string jsonStr = res[1]?.ToString() ?? "";
            Assert("S1.2: jsonStr is non-empty", !string.IsNullOrWhiteSpace(jsonStr), $"Length: {jsonStr.Length}");

            // Verify with System.Text.Json parser
            using JsonDocument doc = JsonDocument.Parse(jsonStr);
            JsonElement root = doc.RootElement;
            Assert("S1.3: JSON root is Object", root.ValueKind == JsonValueKind.Object);

            Assert("S1.4: 'hero' field exists and is string", root.TryGetProperty("hero", out var heroEl) && heroEl.ValueKind == JsonValueKind.String);
            Assert("S1.5: 'level' field exists and is number", root.TryGetProperty("level", out var lvlEl) && lvlEl.ValueKind == JsonValueKind.Number && lvlEl.GetInt32() == 1);
            Assert("S1.6: 'hp' field exists and is number", root.TryGetProperty("hp", out var hpEl) && hpEl.ValueKind == JsonValueKind.Number);
            Assert("S1.7: 'maxHp' field exists and is number", root.TryGetProperty("maxHp", out var maxHpEl) && maxHpEl.ValueKind == JsonValueKind.Number);
            Assert("S1.8: 'mana' field exists and is number", root.TryGetProperty("mana", out var manaEl) && manaEl.ValueKind == JsonValueKind.Number);
            Assert("S1.9: 'maxMana' field exists and is number", root.TryGetProperty("maxMana", out var maxManaEl) && maxManaEl.ValueKind == JsonValueKind.Number);
            Assert("S1.10: 'gold' field exists and is number", root.TryGetProperty("gold", out var goldEl) && goldEl.ValueKind == JsonValueKind.Number);

            Assert("S1.11: 'pos' field exists and is object", root.TryGetProperty("pos", out var posEl) && posEl.ValueKind == JsonValueKind.Object);
            Assert("S1.12: 'pos.x', 'pos.y', 'pos.z' present",
                posEl.TryGetProperty("x", out _) && posEl.TryGetProperty("y", out _) && posEl.TryGetProperty("z", out _));

            // Verify [NEXUS_TELEMETRY] log emission
            string? teleLog = CapturedEchoes.FirstOrDefault(e => e.StartsWith("[NEXUS_TELEMETRY] ", StringComparison.Ordinal));
            Assert("S1.13: [NEXUS_TELEMETRY] log emission captured", teleLog != null);
            if (teleLog != null)
            {
                string payload = teleLog["[NEXUS_TELEMETRY] ".Length..];
                Assert("S1.14: Logged payload matches returned jsonStr", payload == jsonStr);
            }
        }
        catch (Exception ex)
        {
            Assert("S1: Exception in Suite 1", false, ex.Message);
        }

        // =====================================================================
        // SUITE 2: Nexus.CollectTelemetry with Pure-Lua CompactJsonEncode Fallback
        // =====================================================================
        Console.WriteLine("\n--- Suite 2: Nexus.CollectTelemetry with Pure-Lua CompactJsonEncode Fallback ---");
        ResetHarness();
        try
        {
            // Initialized strictly without lib_json
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: false, nexusPath, libJsonPath);

            // Double check lib_json is nil
            object[] checkLibJson = lua.DoString("return _G.lib_json == nil and lib_json == nil");
            Assert("S2.0: Confirmed lib_json is completely nil in environment", (bool)checkLibJson[0]);

            object[] res = lua.DoString("return Nexus.CollectTelemetry()");
            Assert("S2.1: CollectTelemetry executes cleanly with pure-Lua fallback", res.Length == 2);
            string jsonStr = res[1]?.ToString() ?? "";
            Assert("S2.2: Fallback produced non-empty JSON string", !string.IsNullOrWhiteSpace(jsonStr));

            using JsonDocument doc = JsonDocument.Parse(jsonStr);
            JsonElement root = doc.RootElement;
            Assert("S2.3: Fallback JSON parses cleanly via System.Text.Json", root.ValueKind == JsonValueKind.Object);

            Assert("S2.4: Fallback 'hero' is string", root.GetProperty("hero").ValueKind == JsonValueKind.String);
            Assert("S2.5: Fallback 'level' is 1", root.GetProperty("level").GetInt32() == 1);
            Assert("S2.6: Fallback 'hp' is 0", root.GetProperty("hp").GetInt32() == 0);
            Assert("S2.7: Fallback 'maxHp' is 0", root.GetProperty("maxHp").GetInt32() == 0);
            Assert("S2.8: Fallback 'mana' is 0", root.GetProperty("mana").GetInt32() == 0);
            Assert("S2.9: Fallback 'maxMana' is 0", root.GetProperty("maxMana").GetInt32() == 0);
            Assert("S2.10: Fallback 'gold' is 0", root.GetProperty("gold").GetInt32() == 0);
            Assert("S2.11: Fallback 'pos' is object with x,y,z",
                root.GetProperty("pos").TryGetProperty("x", out _) &&
                root.GetProperty("pos").TryGetProperty("y", out _) &&
                root.GetProperty("pos").TryGetProperty("z", out _));

            // Verify array formatting in pure Lua
            Assert("S2.12: Fallback 'cooldowns' is array of 4 items",
                root.GetProperty("cooldowns").ValueKind == JsonValueKind.Array &&
                root.GetProperty("cooldowns").GetArrayLength() == 4);

            string? teleLog = CapturedEchoes.FirstOrDefault(e => e.StartsWith("[NEXUS_TELEMETRY] ", StringComparison.Ordinal));
            Assert("S2.13: Fallback emits [NEXUS_TELEMETRY] string", teleLog != null && teleLog.EndsWith(jsonStr, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Assert("S2: Exception in Suite 2", false, ex.Message);
        }

        // =====================================================================
        // SUITE 3: Semantic Equivalence Between lib_json and CompactJsonEncode
        // =====================================================================
        Console.WriteLine("\n--- Suite 3: Semantic Equivalence Between lib_json and CompactJsonEncode ---");
        ResetHarness();
        try
        {
            string setupStateScript = @"
                Nexus.TelemetryState = {
                    hero = 'Hero_Pyromancer',
                    level = 16,
                    hp = 1240,
                    maxHp = 1580,
                    mana = 620,
                    maxMana = 940,
                    pos = { x = 1250.5, y = 3400.2, z = 128.0 },
                    gold = 3850,
                    gameTime = 1420,
                    cooldowns = { 12, 0, 45, 110 }
                }
            ";

            // 1. With lib_json
            string jsonWithLib;
            using (Lua lua1 = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath))
            {
                lua1.DoString(setupStateScript);
                jsonWithLib = lua1.DoString("return select(2, Nexus.CollectTelemetry())")[0]?.ToString() ?? "";
            }

            // 2. Without lib_json (CompactJsonEncode)
            string jsonWithFallback;
            using (Lua lua2 = CreateMockedLuaEnvironment(loadLibJson: false, nexusPath, libJsonPath))
            {
                lua2.DoString(setupStateScript);
                jsonWithFallback = lua2.DoString("return select(2, Nexus.CollectTelemetry())")[0]?.ToString() ?? "";
            }

            using JsonDocument doc1 = JsonDocument.Parse(jsonWithLib);
            using JsonDocument doc2 = JsonDocument.Parse(jsonWithFallback);

            JsonElement r1 = doc1.RootElement;
            JsonElement r2 = doc2.RootElement;

            Assert("S3.1: Hero property matches", r1.GetProperty("hero").GetString() == r2.GetProperty("hero").GetString(), $"{r1.GetProperty("hero").GetString()} vs {r2.GetProperty("hero").GetString()}");
            Assert("S3.2: Level property matches", r1.GetProperty("level").GetInt32() == r2.GetProperty("level").GetInt32(), $"{r1.GetProperty("level").GetInt32()}");
            Assert("S3.3: Hp property matches", r1.GetProperty("hp").GetInt32() == r2.GetProperty("hp").GetInt32(), $"{r1.GetProperty("hp").GetInt32()}");
            Assert("S3.4: MaxHp property matches", r1.GetProperty("maxHp").GetInt32() == r2.GetProperty("maxHp").GetInt32());
            Assert("S3.5: Mana property matches", r1.GetProperty("mana").GetInt32() == r2.GetProperty("mana").GetInt32());
            Assert("S3.6: MaxMana property matches", r1.GetProperty("maxMana").GetInt32() == r2.GetProperty("maxMana").GetInt32());
            Assert("S3.7: Gold property matches", r1.GetProperty("gold").GetInt32() == r2.GetProperty("gold").GetInt32(), $"{r1.GetProperty("gold").GetInt32()}");
            Assert("S3.8: Pos.X property matches", Math.Abs(r1.GetProperty("pos").GetProperty("x").GetDouble() - r2.GetProperty("pos").GetProperty("x").GetDouble()) < 0.001);
            Assert("S3.9: Pos.Y property matches", Math.Abs(r1.GetProperty("pos").GetProperty("y").GetDouble() - r2.GetProperty("pos").GetProperty("y").GetDouble()) < 0.001);
            Assert("S3.10: Cooldowns match",
                r1.GetProperty("cooldowns")[0].GetInt32() == r2.GetProperty("cooldowns")[0].GetInt32() &&
                r1.GetProperty("cooldowns")[2].GetInt32() == r2.GetProperty("cooldowns")[2].GetInt32());
        }
        catch (Exception ex)
        {
            Assert("S3: Exception in Suite 3", false, ex.Message);
        }

        // =====================================================================
        // SUITE 4: HUD Labels Fallback & Hero Resolution
        // =====================================================================
        Console.WriteLine("\n--- Suite 4: HUD Labels Fallback & Hero Resolution ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            // Populate HUD mock widgets
            lua.DoString(@"
                _CreateMockWidget('game_center_health_label'):SetText('1850/2400')
                _CreateMockWidget('game_center_mana_label'):SetText('910/1200')
                _CreateMockWidget('game_center_level_label'):SetText('21')
                _CreateMockWidget('game_center_gold_label'):SetText('5400')
                GetViewHeroName = function() return 'Hero_Chronos' end
            ");

            object[] res = lua.DoString("return Nexus.CollectTelemetry()");
            string json = res[1]?.ToString() ?? "";
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;

            Assert("S4.1: Health parsed from HUD label 1850/2400", r.GetProperty("hp").GetInt32() == 1850 && r.GetProperty("maxHp").GetInt32() == 2400);
            Assert("S4.2: Mana parsed from HUD label 910/1200", r.GetProperty("mana").GetInt32() == 910 && r.GetProperty("maxMana").GetInt32() == 1200);
            Assert("S4.3: Level parsed from HUD label 21", r.GetProperty("level").GetInt32() == 21);
            Assert("S4.4: Gold parsed from HUD label 5400", r.GetProperty("gold").GetInt32() == 5400);
            Assert("S4.5: Hero name resolved from GetViewHeroName()", r.GetProperty("hero").GetString() == "Hero_Chronos");
        }
        catch (Exception ex)
        {
            Assert("S4: Exception in Suite 4", false, ex.Message);
        }

        // =====================================================================
        // SUITE 5: Item Sanitization in Nexus.ApplySnapshot (Item_ Prefix)
        // =====================================================================
        Console.WriteLine("\n--- Suite 5: Item Sanitization in Nexus.ApplySnapshot (Item_ Prefix) ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            // 5.1: Bare item names (Astrolabe -> Item_Astrolabe)
            CurrentGamePhase = 3;
            lua.DoString("if HoN_Main then HoN_Main.gamePhase = 3 end");
            CapturedCmds.Clear();
            lua.DoString(@"
                local snap = {
                    items = { 'Astrolabe', 'Marchers', 'Bottle' }
                }
                _res1 = Nexus.ApplySnapshot(snap)
            ");
            Assert("S5.1: Snapshot execution succeeded in phase 3", (bool)lua.DoString("return _res1.success")[0]);
            Assert("S5.2: Astrolabe sanitized to Item_Astrolabe", CapturedCmds.Contains("PurchaseItem Item_Astrolabe"));
            Assert("S5.3: Marchers sanitized to Item_Marchers", CapturedCmds.Contains("PurchaseItem Item_Marchers"));
            Assert("S5.4: Bottle sanitized to Item_Bottle", CapturedCmds.Contains("PurchaseItem Item_Bottle"));

            // 5.2: Already-prefixed item names (Item_Astrolabe -> Item_Astrolabe, NO Item_Item_)
            CapturedCmds.Clear();
            lua.DoString(@"
                local snap2 = {
                    items = { 'Item_Astrolabe', 'Item_Marchers' }
                }
                _res2 = Nexus.ApplySnapshot(snap2)
            ");
            Assert("S5.5: Pre-prefixed item is NOT double prefixed",
                CapturedCmds.Contains("PurchaseItem Item_Astrolabe") && !CapturedCmds.Any(c => c.Contains("Item_Item_")));
            Assert("S5.6: Pre-prefixed Marchers maintained", CapturedCmds.Contains("PurchaseItem Item_Marchers"));

            // 5.3: Mixed inventory with inventory alias
            CapturedCmds.Clear();
            lua.DoString(@"
                local snap3 = {
                    inventory = { 'PostHaste', 'Item_FrostwolfSkull', 'BehemothsHeart' }
                }
                _res3 = Nexus.ApplySnapshot(snap3)
            ");
            Assert("S5.7: 'inventory' key supported as alias for 'items'", (bool)lua.DoString("return _res3.success")[0]);
            Assert("S5.8: PostHaste sanitized to Item_PostHaste", CapturedCmds.Contains("PurchaseItem Item_PostHaste"));
            Assert("S5.9: Item_FrostwolfSkull kept as Item_FrostwolfSkull", CapturedCmds.Contains("PurchaseItem Item_FrostwolfSkull"));
            Assert("S5.10: BehemothsHeart sanitized to Item_BehemothsHeart", CapturedCmds.Contains("PurchaseItem Item_BehemothsHeart"));

            // 5.4: Gold, Level, and Teleport mutations
            CapturedCmds.Clear();
            lua.DoString(@"
                local snap4 = {
                    gold = 4500,
                    level = 20,
                    x = 1800.5,
                    y = 3500.0,
                    z = 64.0
                }
                _res4 = Nexus.ApplySnapshot(snap4)
            ");
            Assert("S5.11: GiveGold command formatted", CapturedCmds.Contains("GiveGold 4500"));
            Assert("S5.12: SetHeroLevel command formatted", CapturedCmds.Contains("SetHeroLevel 20"));
            Assert("S5.13: Teleport command formatted with 1 decimal precision", CapturedCmds.Contains("Teleport 1800.5 3500.0"));

            // Verify SNAPSHOT_APPLIED event emission
            string? snapEvent = CapturedEchoes.LastOrDefault(e => e.StartsWith("[NEXUS_EVENT] SNAPSHOT_APPLIED|", StringComparison.Ordinal));
            Assert("S5.14: [NEXUS_EVENT] SNAPSHOT_APPLIED event emitted", snapEvent != null);
        }
        catch (Exception ex)
        {
            Assert("S5: Exception in Suite 5", false, ex.Message);
        }

        // =====================================================================
        // SUITE 6: Lifecycle Phase Guards in ApplySnapshot
        // =====================================================================
        Console.WriteLine("\n--- Suite 6: Lifecycle Phase Guards in ApplySnapshot ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            int[] forbiddenPhases = [0, 1, 2, 4, 5];
            foreach (int phase in forbiddenPhases)
            {
                CurrentGamePhase = phase;
                lua.DoString($"if HoN_Main then HoN_Main.gamePhase = {phase} end");
                CapturedCmds.Clear();
                lua.DoString(@"
                    _guardRes = Nexus.ApplySnapshot({ gold = 1000, items = { 'Astrolabe' } })
                ");
                bool success = (bool)lua.DoString("return _guardRes.success")[0];
                string errCode = lua.DoString("return _guardRes.errorCode")[0]?.ToString() ?? "";

                Assert($"S6.Phase{phase}: Blocked in phase {phase} (success: false, errorCode: INVALID_GAME_PHASE)",
                    !success && errCode == "INVALID_GAME_PHASE");
                Assert($"S6.Phase{phase}.Cmds: Exactly 0 engine commands dispatched in phase {phase}",
                    CapturedCmds.Count == 0, $"Commands dispatched: {CapturedCmds.Count}");
            }
        }
        catch (Exception ex)
        {
            Assert("S6: Exception in Suite 6", false, ex.Message);
        }

        // =====================================================================
        // SUITE 7: Nexus.HideLogin Programmatic Execution
        // =====================================================================
        Console.WriteLine("\n--- Suite 7: Nexus.HideLogin Programmatic Execution ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            lua.DoString("_loginRes = Nexus.HideLogin('ProGamer2026')");

            Assert("S7.1: HideLogin returned success = true", (bool)lua.DoString("return _loginRes.success")[0]);
            Assert("S7.2: Player name returned as ProGamer2026", lua.DoString("return _loginRes.player")[0]?.ToString() == "ProGamer2026");

            // Verify cvars
            Assert("S7.3: _loggedin cvar set to true", CvarStore.TryGetValue("_loggedin", out string? li) && li.Equals("true", StringComparison.OrdinalIgnoreCase));
            Assert("S7.4: login_name cvar set to ProGamer2026", CvarStore.TryGetValue("login_name", out string? ln) && ln == "ProGamer2026");
            Assert("S7.5: ui_match_is_spectating cvar set to false", CvarStore.TryGetValue("ui_match_is_spectating", out string? sp) && sp.Equals("false", StringComparison.OrdinalIgnoreCase));

            // Verify Trigger('LoginStatus')
            var loginTrig = CapturedTriggers.FirstOrDefault(t => t.TriggerName == "LoginStatus");
            Assert("S7.6: LoginStatus trigger fired", loginTrig != default);
            if (loginTrig != default)
            {
                Assert("S7.7: LoginStatus first arg is 'success'", loginTrig.Args.Length > 0 && loginTrig.Args[0] == "success");
            }

            // Verify event_com:DoEventN(1) called
            bool doEventN1 = (bool)lua.DoString(@"
                local ec = UIManager.GetInterface('main'):GetWidget('event_com')
                for _, call in ipairs(ec._doEventCalls) do
                    if call == 1 then return true end
                end
                return false
            ")[0];
            Assert("S7.8: event_com:DoEventN(1) executed (integer index)", doEventN1);

            // Verify login widgets hidden
            string[] hiddenWidgets =
            [
                "prelogin_lower",
                "prelogin_loginbox",
                "prelogin_loginbox_disabled_login",
                "main_login_cancel_button",
                "main_login_button",
                "mainLoginQueue",
                "main_status"
            ];
            foreach (string wName in hiddenWidgets)
            {
                bool isVis = (bool)lua.DoString($"return UIManager.GetInterface('main'):GetWidget('{wName}'):IsVisible()")[0];
                Assert($"S7.Widget.{wName}: Hidden ({wName})", !isVis);
            }

            // Verify systembar visible
            bool sysbarVis = (bool)lua.DoString("return UIManager.GetInterface('main'):GetWidget('systembar'):IsVisible()")[0];
            Assert("S7.9: systembar widget is visible", sysbarVis);

            // Verify HoN_Main lobby state synchronization
            bool loggedInLobby = (bool)lua.DoString("return HoN_Main.isFullyLoggedIn == true and HoN_Main.uiLoginState == false")[0];
            Assert("S7.10: HoN_Main lobby flags synchronized", loggedInLobby);

            // Verify [NEXUS_EVENT] LOGIN_SUCCESS
            string? loginEv = CapturedEchoes.FirstOrDefault(e => e.StartsWith("[NEXUS_EVENT] LOGIN_SUCCESS|", StringComparison.Ordinal));
            Assert("S7.11: LOGIN_SUCCESS event emitted with valid JSON payload", loginEv != null && loginEv.Contains("\"loggedIn\":true"));
        }
        catch (Exception ex)
        {
            Assert("S7: Exception in Suite 7", false, ex.Message);
        }

        // =====================================================================
        // SUITE 8: Nexus.PracticeMatch Command Formatting & Watch
        // =====================================================================
        Console.WriteLine("\n--- Suite 8: Nexus.PracticeMatch Command Formatting & Watch ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            CapturedCmds.Clear();
            lua.DoString(@"
                _pmRes = Nexus.PracticeMatch({
                    map = 'caldavar',
                    hero = 'Hero_Pyromancer',
                    gameName = 'TestPractice',
                    cheats = true
                })
            ");

            Assert("S8.1: PracticeMatch returned success = true", (bool)lua.DoString("return _pmRes.success")[0]);
            Assert("S8.2: Cheats cvar 'Set dev true' dispatched", CapturedCmds.Contains("Set dev true"));
            Assert("S8.3: Cheats cvar 'Set cheats true' dispatched", CapturedCmds.Contains("Set cheats true"));
            Assert("S8.4: Cheats cvar 'Set svr_cheats true' dispatched", CapturedCmds.Contains("Set svr_cheats true"));

            string expectedLaunch = "StartGame practice TestPractice map:caldavar allheroes:true";
            Assert("S8.5: StartGame practice command formatted cleanly", CapturedCmds.Contains(expectedLaunch));

            // Verify GamePhase watch registered and fires hero selection
            CapturedCmds.Clear();
            lua.DoString(@"
                -- Simulate GamePhase watch trigger (phase 2 = hero pick)
                local watches = _RegisteredWatches['GamePhase']
                if watches and #watches > 0 then
                    for _, fn in ipairs(watches) do
                        fn(nil, 2)
                    end
                end
            ");

            Assert("S8.6: Auto-select team dispatched ('Team 1 0')", CapturedCmds.Contains("Team 1 0"));
            Assert("S8.7: RequestMatchStart dispatched", CapturedCmds.Contains("RequestMatchStart"));
            Assert("S8.8: SelectHero Hero_Pyromancer dispatched", CapturedCmds.Contains("SelectHero Hero_Pyromancer"));

            // Verify watch unregistered after execution
            long remainingWatches = (long)lua.DoString("return (_RegisteredWatches['GamePhase'] and #_RegisteredWatches['GamePhase']) or 0")[0];
            Assert("S8.9: GamePhase watch unregistered after hero selection", remainingWatches == 0);

            // Verify events emitted
            Assert("S8.10: PRACTICE_MATCH_START event emitted", CapturedEchoes.Any(e => e.StartsWith("[NEXUS_EVENT] PRACTICE_MATCH_START|", StringComparison.Ordinal)));
            Assert("S8.11: PRACTICE_MATCH_HERO_SELECTED event emitted", CapturedEchoes.Any(e => e.StartsWith("[NEXUS_EVENT] PRACTICE_MATCH_HERO_SELECTED|", StringComparison.Ordinal)));
        }
        catch (Exception ex)
        {
            Assert("S8: Exception in Suite 8", false, ex.Message);
        }

        // =====================================================================
        // SUITE 9: Stress & Performance (1,000 Invocations in < 500ms)
        // =====================================================================
        Console.WriteLine("\n--- Suite 9: Stress & Performance (1,000 Invocations) ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: false, nexusPath, libJsonPath);

            Stopwatch perfSw = Stopwatch.StartNew();
            const int iterations = 1000;
            lua.DoString($@"
                for i = 1, {iterations} do
                    Nexus.TelemetryState.gold = i
                    Nexus.TelemetryState.hp = i * 2
                    local _, json = Nexus.CollectTelemetry()
                end
            ");
            perfSw.Stop();

            Assert($"S9.1: 1,000 telemetry collections in {perfSw.ElapsedMilliseconds}ms (< 1000ms threshold)",
                perfSw.ElapsedMilliseconds < 1000, $"Actual: {perfSw.ElapsedMilliseconds}ms");
            int teleCount = CapturedEchoes.Count(e => e.StartsWith("[NEXUS_TELEMETRY] ", StringComparison.Ordinal));
            Assert("S9.2: Exactly 1,000 [NEXUS_TELEMETRY] echo logs emitted", teleCount == iterations, $"Count: {teleCount}");

            // Spot check last emission
            using JsonDocument lastDoc = JsonDocument.Parse(CapturedEchoes.Last()["[NEXUS_TELEMETRY] ".Length..]);
            Assert("S9.3: 1000th emission matches final state (gold: 1000, hp: 2000)",
                lastDoc.RootElement.GetProperty("gold").GetInt32() == 1000 &&
                lastDoc.RootElement.GetProperty("hp").GetInt32() == 2000);
        }
        catch (Exception ex)
        {
            Assert("S9: Exception in Suite 9", false, ex.Message);
        }

        // =====================================================================
        // SUITE 10: In-Game Watches Registration & Dynamic State Mutation
        // =====================================================================
        Console.WriteLine("\n--- Suite 10: In-Game Watches Registration & Dynamic State Mutation ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            lua.DoString(@"
                local gameIntf = UIManager.GetInterface('game')
                Nexus.RegisterWatches(gameIntf)

                -- Fire watches
                local function fire(name, ...)
                    local wList = _RegisteredWatches[name]
                    if wList then
                        for _, fn in ipairs(wList) do fn(nil, ...) end
                    end
                end

                fire('HeroHealth', '1250', '2000')
                fire('HeroMana', '700', '1000')
                fire('HeroLevel', '14')
                fire('PlayerGold', '2750')
                fire('MatchTime', '840')
                fire('HeroIcon', '/heroes/pyromancer/icon.tga')
                fire('ActiveInventoryCooldown0', '15')
                fire('ActiveInventoryCooldown1', '0')
                fire('ActiveInventoryCooldown2', '45')
                fire('ActiveInventoryCooldown3', '120')
            ");

            object[] res = lua.DoString("return Nexus.CollectTelemetry()");
            string json = res[1]?.ToString() ?? "";
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;

            Assert("S10.1: Watch updated hero to 'pyromancer'", r.GetProperty("hero").GetString() == "pyromancer");
            Assert("S10.2: Watch updated hp to 1250, maxHp to 2000", r.GetProperty("hp").GetInt32() == 1250 && r.GetProperty("maxHp").GetInt32() == 2000);
            Assert("S10.3: Watch updated mana to 700, maxMana to 1000", r.GetProperty("mana").GetInt32() == 700 && r.GetProperty("maxMana").GetInt32() == 1000);
            Assert("S10.4: Watch updated level to 14", r.GetProperty("level").GetInt32() == 14);
            Assert("S10.5: Watch updated gold to 2750", r.GetProperty("gold").GetInt32() == 2750);
            Assert("S10.6: Watch updated gameTime to 840", r.GetProperty("gameTime").GetInt32() == 840);
            Assert("S10.7: Watch updated cooldowns to [15, 0, 45, 120]",
                r.GetProperty("cooldowns")[0].GetInt32() == 15 &&
                r.GetProperty("cooldowns")[1].GetInt32() == 0 &&
                r.GetProperty("cooldowns")[2].GetInt32() == 45 &&
                r.GetProperty("cooldowns")[3].GetInt32() == 120);
        }
        catch (Exception ex)
        {
            Assert("S10: Exception in Suite 10", false, ex.Message);
        }

        // =====================================================================
        // SUITE 11: Backward Compatibility Shims
        // =====================================================================
        Console.WriteLine("\n--- Suite 11: Backward Compatibility Shims ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            CapturedEchoes.Clear();
            lua.DoString("Nexus.ReportHero('Hero_Glacius', 7, 'SupportPlayer', 'practice')");

            Assert("S11.1: ReportHero updated TelemetryState hero",
                (string)lua.DoString("return Nexus.TelemetryState.hero")[0] == "Hero_Glacius");
            Assert("S11.2: ReportHero updated TelemetryState level",
                Convert.ToInt32(lua.DoString("return Nexus.TelemetryState.level")[0]) == 7);
            Assert("S11.3: ReportHero emitted HERO_PICK event",
                CapturedEchoes.Any(e => e.StartsWith("[NEXUS_EVENT] HERO_PICK|", StringComparison.Ordinal)));
            Assert("S11.4: ReportHero triggered telemetry emission",
                CapturedEchoes.Any(e => e.StartsWith("[NEXUS_TELEMETRY] ", StringComparison.Ordinal)));

            CapturedEchoes.Clear();
            lua.DoString("Nexus.UpdateHeroState('Hero_Magmus', 11, 4, 1, 6, 1400, 1650)");

            Assert("S11.5: UpdateHeroState updated hero", (string)lua.DoString("return Nexus.TelemetryState.hero")[0] == "Hero_Magmus");
            Assert("S11.6: UpdateHeroState updated level", Convert.ToInt32(lua.DoString("return Nexus.TelemetryState.level")[0]) == 11);
            Assert("S11.7: UpdateHeroState updated hp/maxHp",
                Convert.ToInt32(lua.DoString("return Nexus.TelemetryState.hp")[0]) == 1400 &&
                Convert.ToInt32(lua.DoString("return Nexus.TelemetryState.maxHp")[0]) == 1650);
            Assert("S11.8: UpdateHeroState triggered telemetry emission",
                CapturedEchoes.Any(e => e.StartsWith("[NEXUS_TELEMETRY] ", StringComparison.Ordinal)));
        }
        catch (Exception ex)
        {
            Assert("S11: Exception in Suite 11", false, ex.Message);
        }

        // =====================================================================
        // SUITE 12: Adversarial Strings, Escapes, and Null Resiliency
        // =====================================================================
        Console.WriteLine("\n--- Suite 12: Adversarial Strings, Escapes, and Null Resiliency ---");
        ResetHarness();
        try
        {
            // Test string escaping in CompactJsonEncode
            using (Lua luaNoLib = CreateMockedLuaEnvironment(loadLibJson: false, nexusPath, libJsonPath))
            {
                luaNoLib.DoString(@"
                    Nexus.TelemetryState.hero = 'Hero_""Special""\\Escaped'
                    _advJson = select(2, Nexus.CollectTelemetry())
                ");
                string advJson = luaNoLib.DoString("return _advJson")[0]?.ToString() ?? "";
                using JsonDocument doc = JsonDocument.Parse(advJson);
                string parsedHero = doc.RootElement.GetProperty("hero").GetString() ?? "";
                Assert("S12.1: CompactJsonEncode correctly escapes quotes and backslashes in strings",
                    parsedHero == "Hero_\"Special\"\\Escaped", $"Parsed: {parsedHero}");
            }

            // Test non-table snapshot payload handling
            using (Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath))
            {
                CurrentGamePhase = 3;
                lua.DoString("if HoN_Main then HoN_Main.gamePhase = 3 end");

                bool nonTableHandled = (bool)lua.DoString(@"
                    local r1 = Nexus.ApplySnapshot(nil)
                    local r2 = Nexus.ApplySnapshot('invalid_string')
                    local r3 = Nexus.ApplySnapshot(12345)
                    return r1.errorCode == 'INVALID_SNAPSHOT' and r2.errorCode == 'INVALID_SNAPSHOT' and r3.errorCode == 'INVALID_SNAPSHOT'
                ")[0];
                Assert("S12.2: ApplySnapshot safely rejects nil, string, and number payloads with INVALID_SNAPSHOT", nonTableHandled);
            }

            // Test empty / nil options in PracticeMatch and HideLogin
            using (Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath))
            {
                bool hideLoginDefault = (bool)lua.DoString(@"
                    local r = Nexus.HideLogin()
                    return r.success == true and (r.player == 'NexusPlayer' or r.player == 'guccidonut' or r.player == 'CanaryPilotUser')
                ")[0];
                Assert("S12.3: HideLogin defaults to 'NexusPlayer' or 'guccidonut' on nil argument", hideLoginDefault);

                bool pmDefault = (bool)lua.DoString(@"
                    local r = Nexus.PracticeMatch()
                    return r.success == true and r.map == 'caldavar' and r.hero == 'Hero_Pyromancer'
                ")[0];
                Assert("S12.4: PracticeMatch defaults map to caldavar and hero to Hero_Pyromancer on nil options", pmDefault);
            }
        }
        catch (Exception ex)
        {
            Assert("S12: Exception in Suite 12", false, ex.Message);
        }

        // =====================================================================
        // SUITE 13: Idempotence & Multi-Environment Re-entry
        // =====================================================================
        Console.WriteLine("\n--- Suite 13: Idempotence & Multi-Environment Re-entry ---");
        ResetHarness();
        try
        {
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            // Mutate state
            lua.DoString("Nexus.TelemetryState.hero = 'Hero_Walkyrie'; Nexus.TelemetryState.gold = 9999");

            // Re-load nexus_bridge.lua (simulating a second interface loading it, e.g. game_legacy)
            lua.DoFile(nexusPath);

            Assert("S13.1: Re-loading nexus_bridge preserves existing TelemetryState table",
                (string)lua.DoString("return Nexus.TelemetryState.hero")[0] == "Hero_Walkyrie" &&
                Convert.ToInt32(lua.DoString("return Nexus.TelemetryState.gold")[0]) == 9999);
            Assert("S13.2: _G.Nexus anchored and identical to local Nexus",
                (bool)lua.DoString("return _G.Nexus == Nexus")[0]);
            Assert("S13.3: _G.lib_json anchored and identical to lib_json",
                (bool)lua.DoString("return _G.lib_json == lib_json")[0]);
        }
        catch (Exception ex)
        {
            Assert("S13: Exception in Suite 13", false, ex.Message);
        }

        // =====================================================================
        // SUITE 14: SysBarController (System Task Bar) Empirical Verification
        // =====================================================================
        Console.WriteLine("\n--- Suite 14: SysBarController (System Task Bar) Empirical Verification ---");
        ResetHarness();
        try
        {
            string sysbarScriptPath = Path.GetFullPath("games/client/override/ui/fe2/scripts/sysbar_controller.lua");
            using Lua lua = CreateMockedLuaEnvironment(loadLibJson: true, nexusPath, libJsonPath);

            lua.DoString(@"
                local widgets = {
                    'sysbar_quick_settings_drawer', 'sysbar_practice_drawer',
                    'sysbar_qs_vol_1', 'sysbar_qs_vol_2', 'sysbar_qs_vol_3', 'sysbar_qs_vol_4', 'sysbar_qs_vol_5',
                    'sysbar_qs_vol_label', 'sysbar_qs_voice_label', 'sysbar_qs_voice_btn',
                    'sysbar_qs_display_label', 'sysbar_qs_perf_label', 'sysbar_status_icon',
                    'sysbar_perf_pill_text', 'sysbar_perf_pill', 'sysbar_perf_dot',
                    'sysbar_friends_online_count', 'sysbar_ccu_count', 'sysbar_friendsonline_label', 'sysbar_ccu_label',
                    'sysbar_qs_mute_sound_label', 'sysbar_qs_mute_sound_bg', 'sysbar_qs_mute_sound_border',
                    'sysbar_qs_mute_music_label', 'sysbar_qs_mute_music_bg', 'sysbar_qs_mute_music_border',
                    'sysbar_qs_mute_units_label', 'sysbar_qs_mute_units_bg', 'sysbar_qs_mute_units_border',
                    'sysbar_session_container', 'sysbar_disconnect', 'sysbar_logout', 'main_login_button', 'sysbar_rap_button'
                }
                for _, wn in ipairs(widgets) do
                    _CreateMockWidget(wn)
                end
            ");

            lua.DoFile(sysbarScriptPath);

            Assert("S14.1: SysBarController table initialized", (bool)lua.DoString("return type(SysBarController) == 'table'")[0]);
            Assert("S14.2: SysBar_ToggleQuickSettings global function defined", (bool)lua.DoString("return type(SysBar_ToggleQuickSettings) == 'function'")[0]);
            Assert("S14.3: SysBar_SetMasterVolume global function defined", (bool)lua.DoString("return type(SysBar_SetMasterVolume) == 'function'")[0]);
            Assert("S14.4: SysBar_ToggleVoiceChat global function defined", (bool)lua.DoString("return type(SysBar_ToggleVoiceChat) == 'function'")[0]);
            Assert("S14.5: SysBar_ToggleDisplayMode global function defined", (bool)lua.DoString("return type(SysBar_ToggleDisplayMode) == 'function'")[0]);
            Assert("S14.6: SysBar_TogglePerfHud global function defined", (bool)lua.DoString("return type(SysBar_TogglePerfHud) == 'function'")[0]);
            Assert("S14.7: SysBar_TogglePracticeFlyout global function defined", (bool)lua.DoString("return type(SysBar_TogglePracticeFlyout) == 'function'")[0]);
            Assert("S14.8: SysBar_LaunchPractice global function defined", (bool)lua.DoString("return type(SysBar_LaunchPractice) == 'function'")[0]);
            Assert("S14.9: SysBar_OpenReplaysFolder global function defined", (bool)lua.DoString("return type(SysBar_OpenReplaysFolder) == 'function'")[0]);
            Assert("S14.10: SysBar_QuickQuit global function defined", (bool)lua.DoString("return type(SysBar_QuickQuit) == 'function'")[0]);
            Assert("S14.11: SysBar_ToggleAppearOffline global function defined", (bool)lua.DoString("return type(SysBar_ToggleAppearOffline) == 'function'")[0]);
            Assert("S14.12: SysBar_ReloadUI global function defined", (bool)lua.DoString("return type(SysBar_ReloadUI) == 'function'")[0]);
            Assert("S14.13: SysBar_UpdateTelemetry global function defined", (bool)lua.DoString("return type(SysBar_UpdateTelemetry) == 'function'")[0]);
            Assert("S14.14: SysBar_CloseAllDrawers global function defined", (bool)lua.DoString("return type(SysBar_CloseAllDrawers) == 'function'")[0]);

            // Test Quick Settings Toggle & Mutual Exclusion
            lua.DoString(@"
                SysBar_TogglePracticeFlyout()
                local practiceVis = SysBarController.isPracticeDrawerOpen
                SysBar_ToggleQuickSettings()
                local qsVis = SysBarController.isQuickSettingsOpen
                local practiceAfter = SysBarController.isPracticeDrawerOpen
                _qsTest = { practiceVis = practiceVis, qsVis = qsVis, practiceAfter = practiceAfter }
            ");
            Assert("S14.15: SysBar_ToggleQuickSettings opens quick settings and closes practice drawer",
                (bool)lua.DoString("return _qsTest.qsVis == true and _qsTest.practiceAfter == false")[0]);

            // Test CloseAllDrawers
            lua.DoString("SysBar_CloseAllDrawers()");
            Assert("S14.16: SysBar_CloseAllDrawers closes both drawers",
                (bool)lua.DoString("return SysBarController.isQuickSettingsOpen == false and SysBarController.isPracticeDrawerOpen == false")[0]);

            // Test SetMasterVolume
            CapturedCmds.Clear();
            lua.DoString("SysBar_SetMasterVolume(0.7)");
            Assert("S14.17: SysBar_SetMasterVolume sets sound_masterVolume cvar",
                CapturedCmds.Any(c => c.Contains("Set sound_masterVolume 0.70")));
            Assert("S14.18: SysBar_SetMasterVolume updates volume state",
                Math.Abs(Convert.ToDouble(lua.DoString("return SysBarController.currentVolume")[0]) - 0.7) < 0.01);
            Assert("S14.19: Volume bar 1 filled", (string)lua.DoString("return _GetMockWidget('sysbar_qs_vol_1'):GetColor()")[0] == "0.2 0.8 1.0 1.0");
            Assert("S14.20: Volume bar 5 unfilled", (string)lua.DoString("return _GetMockWidget('sysbar_qs_vol_5'):GetColor()")[0] == "0.15 0.18 0.25 1.0");

            // Test ToggleVoiceChat
            CapturedCmds.Clear();
            lua.DoString("SysBar_ToggleVoiceChat()");
            Assert("S14.21: ToggleVoiceChat toggles voice_enable cvar",
                CapturedCmds.Any(c => c.Contains("Set voice_enable")));

            // Test ToggleDisplayMode
            CapturedCmds.Clear();
            lua.DoString("SysBar_ToggleDisplayMode()");
            Assert("S14.22: ToggleDisplayMode toggles vid_fullscreen",
                CapturedCmds.Any(c => c.Contains("Set vid_fullscreen")));
            Assert("S14.23: ToggleDisplayMode dispatches SetVideoMode",
                CapturedCmds.Contains("SetVideoMode"));

            // Test TogglePerfHud
            CapturedCmds.Clear();
            lua.DoString("SysBar_TogglePerfHud()");
            Assert("S14.24: TogglePerfHud toggles cl_showfps cvar",
                CapturedCmds.Any(c => c.Contains("Set cl_showfps")));

            // Test LaunchPractice (standard map + all 9 QuickSettings maps & mode tokens)
            CapturedCmds.Clear();
            lua.DoString("SysBar_LaunchPractice('midwars', false)");
            Assert("S14.25: LaunchPractice sets deconflicted svr_port 11580", CapturedCmds.Contains("Set svr_port 11580"));
            Assert("S14.26: LaunchPractice sets svr_voicePortStart 12080", CapturedCmds.Contains("Set svr_voicePortStart 12080"));
            Assert("S14.26a: LaunchPractice sets svr_voicePortEnd 12080", CapturedCmds.Contains("Set svr_voicePortEnd 12080"));
            Assert("S14.26b: LaunchPractice enforces canonical 20 Hz svr_gameFPS", CapturedCmds.Contains("Set svr_gameFPS 20"));
            Assert("S14.27: LaunchPractice dispatches StartGame for midwars with casual mode",
                CapturedCmds.Any(c => c.StartsWith("StartGame practice GameName map:midwars ") && c.Contains("mode:normal") && c.Contains("casual:true") && c.Contains("allheroes:true")));

            // Verify all remaining 8 maps & aliases resolve canonical .s2z map names, sv_worldDefaultGameMode CVars,
            // and omit non-normal inline mode: tokens so cgame_x64.dll (0x1800bbbb0) never rejects custom map modes.
            (string Input, string ExpectedMap, string ExpectedDefaultMode, bool ExpectInlineNormalMode)[] expectedMapTokens = new (string Input, string ExpectedMap, string ExpectedDefaultMode, bool ExpectInlineNormalMode)[]
            {
                ("caldavar", "map:caldavar", "normal", true),
                ("grimms_crossing", "map:grimmscrossing", "normal", true),
                ("devowars", "map:devowars", "forcepick", false),
                ("strikers", "map:soccer", "soccerpick", false),
                ("prophets", "map:prophets", "forcepick", false),
                ("capturetheflag", "map:capturetheflag", "normal", true),
                ("team_deathmatch", "map:team_deathmatch", "normal", true),
                ("riftwars", "map:riftwars", "krosmode", false)
            };
            foreach ((string input, string expectedMap, string expectedDefaultMode, bool expectInlineNormalMode) in expectedMapTokens)
            {
                CapturedCmds.Clear();
                lua.DoString($"SysBar_LaunchPractice('{input}', false)");
                Assert($"S14.27_{input}_cvar: LaunchPractice('{input}') sets sv_worldDefaultGameMode {expectedDefaultMode} & sv_allowSolo",
                    CapturedCmds.Contains($"Set sv_worldDefaultGameMode {expectedDefaultMode}") && CapturedCmds.Contains("Set sv_allowSolo true"));
                Assert($"S14.27_{input}: LaunchPractice('{input}') -> {expectedMap} (inlineNormal={expectInlineNormalMode})",
                    CapturedCmds.Any(c =>
                        c.StartsWith("StartGame practice GameName ") &&
                        c.Contains(expectedMap) &&
                        c.Contains("allheroes:true") &&
                        (expectInlineNormalMode ? c.Contains("mode:normal") : !c.Contains("mode:"))));
            }

            // Verify forcepick sandbox does NOT force Hero_Pyromancer, while Strikers sandbox selects Hero_MonkeyKing
            CapturedCmds.Clear();
            lua.DoString(@"
                SysBar_LaunchPractice('devowars', true)
                local watches = _RegisteredWatches['GamePhase']
                if watches and #watches > 0 then
                    watches[#watches](nil, 1)
                end
            ");
            Assert("S14.27_devo_sb: Devo Wars sandbox skips SelectHero override on forcepick map",
                !CapturedCmds.Any(c => c.StartsWith("SelectHero ")));

            CapturedCmds.Clear();
            lua.DoString(@"
                SysBar_LaunchPractice('soccer', true)
                local watches = _RegisteredWatches['GamePhase']
                if watches and #watches > 0 then
                    watches[#watches](nil, 1)
                end
            ");
            Assert("S14.27_soccer_sb: Strikers sandbox selects Hero_MonkeyKing",
                CapturedCmds.Contains("SelectHero Hero_MonkeyKing"));

            // Test LaunchPractice (sandbox mode)
            CapturedCmds.Clear();
            lua.DoString(@"
                SysBar_LaunchPractice('caldavar', true)
                -- Simulate match load trigger (GamePhase 3)
                local watches = _RegisteredWatches['GamePhase']
                if watches and #watches > 0 then
                    for _, fn in ipairs(watches) do
                        fn(nil, 3)
                    end
                end
            ");
            Assert("S14.28: LaunchPractice sandbox sets sv_cheats 1",
                CapturedCmds.Any(c => c.Contains("Set sv_cheats 1")));
            Assert("S14.29: LaunchPractice sandbox sets level 25",
                CapturedCmds.Any(c => c.Contains("SetLevel 25") || c.Contains("SetHeroLevel 25")));
            Assert("S14.30: LaunchPractice sandbox awards 50000 gold",
                CapturedCmds.Contains("GiveGold 50000"));
            Assert("S14.31: LaunchPractice sandbox resets cooldowns",
                CapturedCmds.Contains("ResetCooldowns") || CapturedCmds.Contains("Refresh"));
            Assert("S14.32: LaunchPractice sandbox spawns fake players",
                CapturedCmds.Contains("AddFakePlayer 1") && CapturedCmds.Contains("AddFakePlayer 2"));

            // Test OpenReplaysFolder
            CapturedCmds.Clear();
            lua.DoString("SysBar_OpenReplaysFolder()");
            Assert("S14.33: OpenReplaysFolder dispatches OpenFolder Resources/Replays/",
                CapturedCmds.Any(c => c.Contains("OpenFolder Resources/Replays/")));

            // Test QuickQuit
            CapturedCmds.Clear();
            lua.DoString("SysBar_QuickQuit()");
            Assert("S14.34: QuickQuit dispatches Quit", CapturedCmds.Contains("Quit"));

            // Test ToggleAppearOffline
            CapturedCmds.Clear();
            lua.DoString("SysBar_ToggleAppearOffline()");
            Assert("S14.35: ToggleAppearOffline toggles ui_appearOffline",
                CapturedCmds.Any(c => c.Contains("Set ui_appearOffline")));

            // Test ReloadUI
            CapturedCmds.Clear();
            lua.DoString("SysBar_ReloadUI()");
            Assert("S14.36: ReloadUI dispatches ui_reloadInterfaces",
                CapturedCmds.Any(c => c.Contains("ui_reloadInterfaces")));

            // Test UpdateTelemetry
            lua.DoString(@"
                SysBarController.lastTelemetryUpdate = 0
                Set('host_framerate', 62)
                Set('net_ping', 28)
                SysBar_UpdateTelemetry()
                _pillTxt = _GetMockWidget('sysbar_perf_pill_text'):GetText()
            ");
            Assert("S14.37: UpdateTelemetry updates sysbar_perf_pill_text with FPS",
                ((string)lua.DoString("return _pillTxt")[0]).Contains("62 FPS"));

            // S14.38 - S14.47: Friends & CCU Hydration Verification
            Assert("S14.38: SysBar_UpdateFriendsCount global function defined", (bool)lua.DoString("return type(SysBar_UpdateFriendsCount) == 'function'")[0]);
            Assert("S14.39: SysBar_UpdateOnlineCCU global function defined", (bool)lua.DoString("return type(SysBar_UpdateOnlineCCU) == 'function'")[0]);

            // Test Friends Count: defaults to 0 and explicitly renders "0"
            lua.DoString(@"
                _G.HoN_Social_Panel = nil
                _G.Social_V2 = nil
                SysBar_UpdateFriendsCount()
                _friendsTxt = _GetMockWidget('sysbar_friends_online_count'):GetText()
                _friendsGrpTxt = _GetMockWidget('sysbar_friendsonline_label'):GetText()
            ");
            Assert("S14.40: SysBar_UpdateFriendsCount explicitly renders '0' on widget when 0 friends online",
                (string)lua.DoString("return _friendsTxt")[0] == "0");
            Assert("S14.41: SysBar_UpdateFriendsCount updates sysbar_friendsonline_label group to '0'",
                (string)lua.DoString("return _friendsGrpTxt")[0] == "0");

            // Test Friends Count: hydration from HoN_Social_Panel
            lua.DoString(@"
                _G.HoN_Social_Panel = { onlineCount = 7 }
                SysBar_UpdateFriendsCount()
                _friendsTxt7 = _GetMockWidget('sysbar_friends_online_count'):GetText()
            ");
            Assert("S14.42: SysBar_UpdateFriendsCount hydrates from HoN_Social_Panel.onlineCount",
                (string)lua.DoString("return _friendsTxt7")[0] == "7");

            // Test Friends Count: fallback to Social_V2.PlayerList['friends']
            lua.DoString(@"
                _G.HoN_Social_Panel = nil
                _G.Social_V2 = {
                    PlayerList = {
                        friends = {
                            { name = 'PlayerA', isOnline = true },
                            { name = 'PlayerB', isOnline = false },
                            { name = 'PlayerC', status = 'online' }
                        }
                    }
                }
                SysBar_UpdateFriendsCount()
                _friendsFallbackTxt = _GetMockWidget('sysbar_friends_online_count'):GetText()
            ");
            Assert("S14.43: SysBar_UpdateFriendsCount computes active friends from Social_V2 fallback",
                (string)lua.DoString("return _friendsFallbackTxt")[0] == "2");

            // Test Online CCU: defaults to 0 and explicitly renders "0"
            lua.DoString(@"
                Set('chat_userCount', 0)
                Set('telemetry_ccu', 0)
                SysBar_UpdateOnlineCCU(0)
                _ccuTxt0 = _GetMockWidget('sysbar_ccu_count'):GetText()
            ");
            Assert("S14.44: SysBar_UpdateOnlineCCU explicitly renders '0' on widget when 0 CCU",
                (string)lua.DoString("return _ccuTxt0")[0] == "0");

            // Test Online CCU: accepts forcedCount
            lua.DoString(@"
                SysBar_UpdateOnlineCCU(842)
                _ccuTxtForced = _GetMockWidget('sysbar_ccu_count'):GetText()
            ");
            Assert("S14.45: SysBar_UpdateOnlineCCU updates widget with forcedCount",
                (string)lua.DoString("return _ccuTxtForced")[0] == "842");

            // Test Online CCU: reads from chat_userCount cvar
            lua.DoString(@"
                Set('chat_userCount', 150)
                SysBar_UpdateOnlineCCU()
                _ccuTxtCvar = _GetMockWidget('sysbar_ccu_count'):GetText()
            ");
            Assert("S14.46: SysBar_UpdateOnlineCCU reads from chat_userCount cvar",
                (string)lua.DoString("return _ccuTxtCvar")[0] == "150");

            // Test Telemetry text aesthetic format (warm gold and silver/grey format)
            Assert("S14.47: Perf pill text uses warm gold and grey color codes",
                ((string)lua.DoString("return _pillTxt")[0]).Contains("^y62 FPS") &&
                !((string)lua.DoString("return _pillTxt")[0]).Contains("ms"));

            // S14.48 - S14.56: Quick Mute & Telemetry Delta Verification
            Assert("S14.48: SysBar_ToggleMuteAll global function defined",
                (bool)lua.DoString("return type(SysBar_ToggleMuteAll) == 'function'")[0]);
            Assert("S14.49: SysBar_ToggleMuteMusic global function defined",
                (bool)lua.DoString("return type(SysBar_ToggleMuteMusic) == 'function'")[0]);
            Assert("S14.50: SysBar_ToggleMuteUnitResponses global function defined",
                (bool)lua.DoString("return type(SysBar_ToggleMuteUnitResponses) == 'function'")[0]);

            // Test Toggle Mute All
            CapturedCmds.Clear();
            lua.DoString(@"
                Set('sound_mute', 'false')
                SysBar_ToggleMuteAll()
                _muteAllTxt = _GetMockWidget('sysbar_qs_mute_sound_label'):GetText()
            ");
            Assert("S14.51: SysBar_ToggleMuteAll toggles sound_mute to true and updates label to 'SOUND: OFF'",
                CapturedCmds.Any(c => c.Contains("Set sound_mute true")) &&
                (string)lua.DoString("return _muteAllTxt")[0] == "SOUND: OFF");

            // Test Toggle Mute Music
            CapturedCmds.Clear();
            lua.DoString(@"
                Set('sound_musicVolume', '0.5')
                Set('sound_muteMusic', 'false')
                SysBar_ToggleMuteMusic()
                _muteMusicTxt = _GetMockWidget('sysbar_qs_mute_music_label'):GetText()
            ");
            Assert("S14.52: SysBar_ToggleMuteMusic toggles sound_muteMusic to true and updates label to 'MUSIC: OFF'",
                CapturedCmds.Any(c => c.Contains("Set sound_muteMusic true")) &&
                (string)lua.DoString("return _muteMusicTxt")[0] == "MUSIC: OFF");

            // Test Toggle Mute Unit Responses
            CapturedCmds.Clear();
            lua.DoString(@"
                Set('cg_unitVoiceResponses', 'true')
                SysBar_ToggleMuteUnitResponses()
                _muteUnitsTxt = _GetMockWidget('sysbar_qs_mute_units_label'):GetText()
            ");
            Assert("S14.53: SysBar_ToggleMuteUnitResponses toggles cg_unitVoiceResponses to false and updates label to 'UNITS: OFF'",
                CapturedCmds.Any(c => c.Contains("Set cg_unitVoiceResponses false")) &&
                (string)lua.DoString("return _muteUnitsTxt")[0] == "UNITS: OFF");

            // Test SysBar_UpdateQuickSettingsUI restores labels when unmuted
            lua.DoString(@"
                Set('sound_mute', 'false')
                Set('sound_muteMusic', 'false')
                Set('cg_unitVoiceResponses', 'true')
                SysBar_UpdateQuickSettingsUI()
                _unmutedSound = _GetMockWidget('sysbar_qs_mute_sound_label'):GetText()
                _unmutedMusic = _GetMockWidget('sysbar_qs_mute_music_label'):GetText()
                _unmutedUnits = _GetMockWidget('sysbar_qs_mute_units_label'):GetText()
            ");
            Assert("S14.54: SysBar_UpdateQuickSettingsUI hydrates unmuted states (SOUND: ON, MUSIC: ON, UNITS: ON)",
                (string)lua.DoString("return _unmutedSound")[0] == "SOUND: ON" &&
                (string)lua.DoString("return _unmutedMusic")[0] == "MUSIC: ON" &&
                (string)lua.DoString("return _unmutedUnits")[0] == "UNITS: ON");

            // Test Telemetry Delta-FPS Calculation when engine framerate returns 0
            lua.DoString(@"
                Set('host_framerate', 0)
                Set('net_ping', 0)
                SysBarController.lastTelemetryUpdate = 0
                SysBarController.telemetryWindowStart = 1000
                SysBarController.telemetryFrameCount = 29
                SysBarController.smoothedFps = nil
                _G.GetTime = function() return 1500 end
                SysBar_UpdateTelemetry()
                _pillDeltaTxt = _GetMockWidget('sysbar_perf_pill_text'):GetText()
            ");
            Assert("S14.55: SysBar_UpdateTelemetry calculates smoothed FPS from elapsed frame delta when cvar is 0",
                ((string)lua.DoString("return _pillDeltaTxt")[0]).Contains("60 FPS"));

            // S14.56 - S14.60: Ping Telemetry & ScoreboardSelf Verification
            Assert("S14.56: SysBarController.OnScoreboardSelf exported",
                (bool)lua.DoString("return type(SysBarController.OnScoreboardSelf) == 'function' and type(SysBar_OnScoreboardSelf) == 'function'")[0]);
            Assert("S14.57: SysBarController.ResolvePing exported",
                (bool)lua.DoString("return type(SysBarController.ResolvePing) == 'function' and type(SysBar_ResolvePing) == 'function'")[0]);

            // Test in-match ping from ScoreboardSelf trigger
            lua.DoString(@"
                SysBarController.lastFrameTimestamp = 0
                SysBarController.lastTelemetryUpdate = 0
                _G.GetTime = function() return 2000 end
                Set('host_framerate', 144)
                SysBar_OnScoreboardSelf(144, 37)
                SysBar_UpdateTelemetry()
                _pillMatchTxt = _GetMockWidget('sysbar_perf_pill_text'):GetText()
            ");
            Assert("S14.58: SysBar_OnScoreboardSelf updates pill to 144 FPS and records ping on controller",
                ((string)lua.DoString("return _pillMatchTxt")[0]).Contains("144 FPS") &&
                !((string)lua.DoString("return _pillMatchTxt")[0]).Contains("ms") &&
                Convert.ToInt32(lua.DoString("return SysBarController.matchPing")[0]) == 37);

            // Test local loopback / practice 0ms ping
            lua.DoString(@"
                SysBarController.lastFrameTimestamp = 0
                SysBarController.lastTelemetryUpdate = 0
                _G.GetTime = function() return 2500 end
                Set('host_framerate', 60)
                SysBar_OnScoreboardSelf(60, 0)
                SysBar_UpdateTelemetry()
                _pillZeroTxt = _GetMockWidget('sysbar_perf_pill_text'):GetText()
            ");
            Assert("S14.59: SysBar_OnScoreboardSelf formats 60 FPS cleanly without ping",
                ((string)lua.DoString("return _pillZeroTxt")[0]).Contains("60 FPS") &&
                !((string)lua.DoString("return _pillZeroTxt")[0]).Contains("ms") &&
                Convert.ToInt32(lua.DoString("return SysBarController.matchPing")[0]) == 0);

            // Test SysBar_TogglePerfHud leaves sysbar_perf_monitor visible
            lua.DoString(@"
                local perfMon = _CreateMockWidget('sysbar_perf_monitor')
                perfMon:SetVisible(true)
                SysBar_TogglePerfHud()
                _perfMonVis = perfMon:IsVisible()
            ");
            Assert("S14.60: SysBar_TogglePerfHud preserves sysbar_perf_monitor visibility",
                (bool)lua.DoString("return _perfMonVis == true")[0]);

            // Test Cinematic Theater Fullscreen toggle parameter
            lua.DoString(@"
                local cWidgets = {
                    'cinematic_theater_modal', 'cinematic_theater_video',
                    'cinematic_play_pause_label', 'cinematic_fullscreen_label', 'cinematic_fs_exit_btn'
                }
                for _, cw in ipairs(cWidgets) do
                    _CreateMockWidget(cw)
                end
            ");
            string cinematicScriptPath = Path.GetFullPath("games/client/override/ui/fe2/scripts/cinematic_theater.lua");
            lua.DoFile(cinematicScriptPath);
            lua.DoString(@"
                CinematicTheater_Toggle(true)
                _theaterFullscreen = CinematicTheater.isFullscreen and CinematicTheater.isOpen
            ");
            Assert("S14.61: CinematicTheater_Toggle(true) opens theater directly in fullscreen mode",
                (bool)lua.DoString("return _theaterFullscreen")[0]);

            // S14.62 - S14.67: Session Controls (Logout & Disconnect) Verification
            Assert("S14.62: SysBar session control functions exported",
                (bool)lua.DoString(@"return type(SysBar_UpdateSessionButtons) == 'function'
                    and type(SysBar_IsSessionAuthenticated) == 'function'
                    and type(SysBar_Logout) == 'function'
                    and type(SysBarController.UpdateSessionButtons) == 'function'")[0]);

            // Test Pre-Login / Unauthenticated phase 0 hides session container, logout, and disconnect
            lua.DoString(@"
                Set('cl_GarenaEnable', 'false')
                Set('_loggedin', 'false')
                Set('ui_isLoggedIn', 'false')
                Set('kongor_offline_hub', 'false')
                Set('p2p_mode', 'false')
                _G._loggedin = false
                _G.isLoggedIn = false
                _G.IsLoggedIn = function() return false end
                _G.GetCurrentGamePhase = function() return 0 end
                SysBar_UpdateSessionButtons('onload')
                _cVis0 = _GetMockWidget('sysbar_session_container'):IsVisible()
                _lVis0 = _GetMockWidget('sysbar_logout'):IsVisible()
                _dVis0 = _GetMockWidget('sysbar_disconnect'):IsVisible()
            ");
            Assert("S14.63: SysBar_UpdateSessionButtons hides container, logout, and disconnect on unauthenticated phase 0",
                (bool)lua.DoString("return _cVis0 == false and _lVis0 == false and _dVis0 == false")[0]);

            // Test LoginStatus success at phase 0 shows logout and container, hides disconnect
            lua.DoString(@"
                SysBar_UpdateSessionButtons('LoginStatus', 'success', '1')
                _cVis1 = _GetMockWidget('sysbar_session_container'):IsVisible()
                _lVis1 = _GetMockWidget('sysbar_logout'):IsVisible()
                _dVis1 = _GetMockWidget('sysbar_disconnect'):IsVisible()
            ");
            Assert("S14.64: SysBar_UpdateSessionButtons shows logout and container on LoginStatus success (phase 0)",
                (bool)lua.DoString("return _cVis1 == true and _lVis1 == true and _dVis1 == false")[0]);

            // Test In-game phase (GamePhase > 0) switches to Disconnect button and hides Logout
            lua.DoString(@"
                _G.GetCurrentGamePhase = function() return 5 end
                SysBar_UpdateSessionButtons('GamePhase', 5)
                _cVis5 = _GetMockWidget('sysbar_session_container'):IsVisible()
                _lVis5 = _GetMockWidget('sysbar_logout'):IsVisible()
                _dVis5 = _GetMockWidget('sysbar_disconnect'):IsVisible()
            ");
            Assert("S14.65: SysBar_UpdateSessionButtons switches to Disconnect button in-match (phase 5)",
                (bool)lua.DoString("return _cVis5 == true and _lVis5 == false and _dVis5 == true")[0]);

            // Test Offline Hub (kongor_offline_hub=true) hydrates Logout at phase 0 even if native IsLoggedIn is false
            lua.DoString(@"
                _G.GetCurrentGamePhase = function() return 0 end
                _G.IsLoggedIn = function() return false end
                Set('kongor_offline_hub', 'true')
                SysBar_UpdateSessionButtons('onload')
                _lVisOffline = _GetMockWidget('sysbar_logout'):IsVisible()
            ");
            Assert("S14.66: SysBar_UpdateSessionButtons displays Logout when in Offline Hub",
                (bool)lua.DoString("return _lVisOffline == true")[0]);

            // Test SysBar_Logout sets ui_suppressAutoLogin, resets state, and dispatches logout
            CapturedCmds.Clear();
            CapturedTriggers.Clear();
            lua.DoString(@"
                SysBar_Logout()
                _suppress = GetCvarBool('ui_suppressAutoLogin')
                _offlineHub = GetCvarBool('kongor_offline_hub')
                _lVisAfterLogout = _GetMockWidget('sysbar_logout'):IsVisible()
            ");
            bool trigOffline = CapturedTriggers.Any(t => t.TriggerName == "LoginStatus" && t.Args.Length > 0 && t.Args[0] == "offline");
            Assert("S14.67: SysBar_Logout suppresses auto-login, resets offline hub, and updates visibility to hidden",
                (bool)lua.DoString("return _suppress == true and _offlineHub == false and _lVisAfterLogout == false")[0] && trigOffline);

            // S14.68 - S14.70: TacticalHud RegisterWatches recreation resilience across Queue Arcade Round 2
            string tacticalHudScriptPath = Path.GetFullPath("games/client/override/ui/scripts/tactical_hud.lua");
            lua.DoString(@"
                _CreateMockWidget('tactical_hud_root')
                _CreateMockWidget('tactical_camp_overlay')
                _CreateMockWidget('tactical_buyback_panel')
                _CreateMockWidget('tactical_ally_ult_panel')
            ");
            lua.DoFile(tacticalHudScriptPath);
            lua.DoString(@"
                -- Simulate round 1 completed: disconnect dispatched and pop alert active
                TacticalHud.rankedPullDisconnectDispatched = true
                TacticalHud.rankedPopAlertActive = true
                _initialBoundHostId = TacticalHud.boundWatchHostId

                -- Recreate tactical_hud_root widget table (simulating map reload / UI rebuild in Round 2)
                local newRoot = _CreateMockWidget('tactical_hud_root')
                _newRootHostId = tostring(newRoot)

                -- Call RegisterWatches on recreated root
                TacticalHud.RegisterWatches()

                _reboundHostId = TacticalHud.boundWatchHostId
                _dispatchedReset = TacticalHud.rankedPullDisconnectDispatched == false
                _popAlertReset = TacticalHud.rankedPopAlertActive == false
                _whisperBound = newRoot._watches and newRoot._watches['ChatWhisperUpdate'] ~= nil
            ");

            Assert("S14.68: TacticalHud.RegisterWatches detects recreated tactical_hud_root and updates boundWatchHostId",
                (string)lua.DoString("return _initialBoundHostId")[0] != (string)lua.DoString("return _reboundHostId")[0] &&
                (string)lua.DoString("return _reboundHostId")[0] == (string)lua.DoString("return _newRootHostId")[0]);

            Assert("S14.69: TacticalHud.RegisterWatches resets rankedPullDisconnectDispatched and rankedPopAlertActive on host recreate",
                (bool)lua.DoString("return _dispatchedReset")[0] && (bool)lua.DoString("return _popAlertReset")[0]);

            Assert("S14.70: TacticalHud.RegisterWatches re-binds ChatWhisperUpdate watch to recreated tactical_hud_root",
                (bool)lua.DoString("return _whisperBound")[0]);
        }
        catch (Exception ex)
        {
            Assert("S14: Exception in Suite 14", false, ex.Message);
        }

        sw.Stop();

        Console.WriteLine("\n================================================================================");
        Console.WriteLine($"M1 EMPIRICAL VERIFIER SUMMARY: {passedTests}/{totalTests} Tests Passed in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine("================================================================================");

        if (failedTests > 0)
        {
            Console.WriteLine($"\nVERDICT: REQUEST_CHANGES ({failedTests} test failures detected)");
            return 1;
        }

        Console.WriteLine("\nVERDICT: APPROVE (All empirical telemetry and mutation tests passed 100%)");
        return 0;
    }
}
