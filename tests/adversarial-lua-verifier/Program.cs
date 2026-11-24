// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using NLua;
using NLua.Exceptions;

namespace AdversarialLuaVerifier;

public class MockWidget(string name)
{
    public string Name { get; set; } = name;
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public string Text { get; set; } = string.Empty;
    public string Texture { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 100f;
    public float Height { get; set; } = 100f;

    public void SetGlow(object? val = null) { }
    public void SetGlowColor(object? val = null) { }
    public void SetBackgroundGlow(object? val = null) { }
    public void SetBorderGlow(object? val = null) { }
    public void SetTextGlow(object? val = null) { }
    public void SetShadow(object? val = null) { }
    public void SetShadowColor(object? val = null) { }
    public void SetShadowOffset(object? x = null, object? y = null) { }
    public void SetOutline(object? val = null) { }
    public void SetOutlineColor(object? val = null) { }
    public void SetFont(object? font = null) { }
    public void SetColor(object? color = null) => Color = color?.ToString() ?? "";

    public void SetText(object? text = null)
    {
        Text = text?.ToString() ?? "";
        Program.RecordWidgetText(Name, Text);
    }

    public void SetSafeText(object? text = null) => SetText(text);

    public void SetTexture(object? texture = null)
    {
        Texture = texture?.ToString() ?? "";
        Program.RecordWidgetTexture(Name, Texture);
    }

    public void SetVisible(object? vis = null)
    {
        Visible = vis is bool b ? b : (vis?.ToString() == "1" || vis?.ToString() == "true");
        Program.RecordWidgetVisibility(Name, Visible);
    }

    public void SetEnabled(object? en = null)
    {
        Enabled = en is bool b ? b : (en?.ToString() == "1" || en?.ToString() == "true");
    }

    public void SetWidth(object? w = null) { }
    public void SetHeight(object? h = null) { }
    public void SetAbsoluteX(object? x = null) { }
    public void SetAbsoluteY(object? y = null) { }
    public void SetX(object? x = null) { }
    public void SetY(object? y = null) { }
    public void SetValue(object? val = null) { }
    public void SetMaxValue(object? val = null) { }
    public void SetRenderMode(object? mode = null) { }
    public void SetSelectedItemByValue(object? val = null, object? ev = null) { }
    public void SetAvatar(object? url = null) { }
    public void SetCallback(object? ev = null, object? cb = null) { }
    public void ClearCallback(object? ev = null) { }
    public void ClearChildren() => Program.RecordClearChildren(Name);
    public float GetAbsoluteX() => 0f;
    public float GetAbsoluteY() => 0f;
    public float GetWidth() => 100f;
    public float GetHeight() => 100f;
    public string GetValue() => "0";
    public string GetText() => Text;
    public string GetName() => Name;
    public bool IsVisible() => Visible;
    public bool IsEnabled() => Enabled;
    public void DoEvent(object? ev = null) { }
    public string UICmd(object? cmd = null) => cmd?.ToString()?.Contains("GetScrollbarMaxValue") == true ? "100" : "";
    public void Sleep(object? time = null, object? cb = null) { }
    public void FadeIn(object? time = null) { }
    public void FadeOut(object? time = null) { }
    public void Show(object? duration = null) => Visible = true;
    public void Hide() => Visible = false;
    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;
    public void RegisterWatch(object? n = null, object? f = null) { }
    public MockWidget GetWidget(object? n = null) => Program.GetOrCreateWidget(n?.ToString() ?? "");
    public MockWidget Instantiate(params object?[] args)
    {
        Program.RecordInstantiation(Name);
        return new MockWidget("new_instanced_widget");
    }
}

public class Program
{
    private static readonly Dictionary<string, MockWidget> Widgets = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<string> Instantiations = [];
    public static readonly List<string> ClearChildrenCalls = [];
    public static readonly Dictionary<string, string> WidgetTexts = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, string> WidgetTextures = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, bool> WidgetVisibilities = new(StringComparer.OrdinalIgnoreCase);

    public static MockWidget GetOrCreateWidget(string name)
    {
        if (!Widgets.TryGetValue(name, out MockWidget? w))
        {
            w = new MockWidget(name);
            Widgets[name] = w;
        }
        return w;
    }

    public static void RecordWidgetText(object name, object? text) => WidgetTexts[name?.ToString() ?? ""] = text?.ToString() ?? "";
    public static void RecordWidgetTexture(object name, object? texture) => WidgetTextures[name?.ToString() ?? ""] = texture?.ToString() ?? "";
    public static void RecordWidgetVisibility(object name, object? vis)
    {
        bool visible = vis is bool b ? b : (vis?.ToString() == "1" || vis?.ToString() == "true");
        WidgetVisibilities[name?.ToString() ?? ""] = visible;
    }
    public static void RecordClearChildren(object name) => ClearChildrenCalls.Add(name?.ToString() ?? "");
    public static void RecordInstantiation(object parent)
    {
        Instantiations.Add($"{parent}:Instantiate(playerstats_match_row_template)");
    }

    public static void ResetState()
    {
        Widgets.Clear();
        Instantiations.Clear();
        ClearChildrenCalls.Clear();
        WidgetTexts.Clear();
        WidgetTextures.Clear();
        WidgetVisibilities.Clear();
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--m1" || args[0] == "--m1-telemetry"))
        {
            return LuaTelemetryVerifier.Run();
        }

        Console.WriteLine("================================================================================");
        Console.WriteLine("ADVERSARIAL STRESS TEST HARNESS — MILESTONES 2 & 3");
        Console.WriteLine("Targets: Match History CSV Parser, 3D Hero Login Controller & Preservation");
        Console.WriteLine("================================================================================\n");

        string[] scriptPaths =
        [
            Path.GetFullPath("games/client/override/ui/scripts/newui/player_stats_v2.lua"),
            Path.GetFullPath("games/shared/ui/scripts/newui/player_stats_v2.lua")
        ];

        int totalSuites = 0;
        int failedSuites = 0;

        foreach (string scriptPath in scriptPaths)
        {
            totalSuites++;
            Console.WriteLine($"\n>>>>>>>> TARGET: {Path.GetRelativePath(Directory.GetCurrentDirectory(), scriptPath)} <<<<<<<<\n");

            if (!File.Exists(scriptPath))
            {
                if (scriptPath.Contains("shared", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[SKIP] Optional shared target not present on disk: {scriptPath}");
                    continue;
                }
                Console.WriteLine($"[FATAL] Script not found at {scriptPath}");
                failedSuites++;
                continue;
            }

            bool suitePassed = RunFullAdversarialSuite(scriptPath);
            if (!suitePassed)
            {
                failedSuites++;
                Console.WriteLine($"\n[FAIL] Script failed adversarial challenge: {scriptPath}");
            }
            else
            {
                Console.WriteLine($"\n[PASS] All adversarial stress tests passed on: {scriptPath}");
            }
        }

        // Milestone 3 Targets: hero_login_controller.lua
        (string path, bool isSimplified)[] heroTargets =
        [
            (Path.GetFullPath("games/client/override/ui/fe2/scripts/hero_login_controller.lua"), false),
            (Path.GetFullPath("games/client/game/ui/fe2/scripts/hero_login_controller.lua"), false),
            (Path.GetFullPath("games/shared/override/ui/fe2/scripts/hero_login_controller.lua"), true)
        ];

        foreach (var (heroPath, isSimplified) in heroTargets)
        {
            totalSuites++;
            Console.WriteLine($"\n>>>>>>>> TARGET: {Path.GetRelativePath(Directory.GetCurrentDirectory(), heroPath)} <<<<<<<<\n");

            if (!File.Exists(heroPath))
            {
                if (heroPath.Contains("shared", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[SKIP] Optional shared target not present on disk: {heroPath}");
                    continue;
                }
                Console.WriteLine($"[FATAL] Script not found at {heroPath}");
                failedSuites++;
                continue;
            }

            bool heroPassed = RunHeroLoginControllerSuite(heroPath, isSimplified);
            if (!heroPassed)
            {
                failedSuites++;
                Console.WriteLine($"\n[FAIL] HeroLoginController failed adversarial challenge: {heroPath}");
            }
            else
            {
                Console.WriteLine($"\n[PASS] All HeroLoginController adversarial tests passed on: {heroPath}");
            }
        }

        // Milestone 1 Targets: nexus_bridge.lua (Lifecycle Sandboxing & ApplySnapshot Guardrails)
        string nexusBridgePath = Path.GetFullPath("games/shared/ui/scripts/nexus_bridge.lua");
        totalSuites++;
        Console.WriteLine($"\n>>>>>>>> TARGET: {Path.GetRelativePath(Directory.GetCurrentDirectory(), nexusBridgePath)} <<<<<<<<\n");

        if (!File.Exists(nexusBridgePath))
        {
            Console.WriteLine($"[FATAL] Script not found at {nexusBridgePath}");
            failedSuites++;
        }
        else
        {
            bool nexusPassed = RunNexusBridgeLifecycleSuite(nexusBridgePath);
            if (!nexusPassed)
            {
                failedSuites++;
                Console.WriteLine($"\n[FAIL] nexus_bridge.lua failed lifecycle adversarial challenge: {nexusBridgePath}");
            }
            else
            {
                Console.WriteLine($"\n[PASS] All nexus_bridge.lua lifecycle stress tests passed on: {nexusBridgePath}");
            }
        }

        Console.WriteLine("\n================================================================================");
        Console.WriteLine($"FINAL RESULT: {totalSuites - failedSuites}/{totalSuites} Script Targets Passed");
        Console.WriteLine("================================================================================");

        if (failedSuites > 0)
        {
            Console.WriteLine("\nOVERALL VERDICT: REQUEST_CHANGES (Failures Detected)");
            return 1;
        }

        Console.WriteLine("\nOVERALL VERDICT: APPROVE (All Adversarial Stress Tests Passed)");
        return 0;
    }

    private static void CallMatchList(Lua lua, IEnumerable<string> rows)
    {
        string args = string.Join(", ", rows.Select(r => $"[==[{r}]==]"));
        lua.DoString($"Player_Stats_V2:OnPlayerStatsMatchListResult({args})");
    }

    private static bool RunFullAdversarialSuite(string scriptPath)
    {
        int passed = 0;
        int failed = 0;

        void AssertTest(string testName, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  [PASS] {testName} {(string.IsNullOrEmpty(detail) ? "" : $"({detail})")}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {testName} - {detail}");
            }
        }

        Lua lua = InitializeLuaEnvironment(scriptPath);

        // -------------------------------------------------------------------------
        // Test Suite A: 100 Rows With 50 Empty Interleaved (The Primary Adversarial Trigger)
        // -------------------------------------------------------------------------
        Console.WriteLine("\n--- Scenario 1: 100 Rows With 50 Empty Interleaved ---");
        ResetState();
        try
        {
            // Build 100 arguments: even indices empty string, odd indices valid match
            List<string> rows = [];
            for (int i = 1; i <= 100; i++)
            {
                if (i % 2 == 1) // Valid row (50 total)
                {
                    int matchId = 2000 + i;
                    int heroId = (i % 4 == 1) ? 15 : 25; // Aluna (15) or Magmus (25)
                    string heroName = heroId == 15 ? "Hero_Aluna" : "Hero_Magmus";
                    rows.Add($"{matchId},1,1,{i % 15},{i % 5},{i % 20},{heroId},1800,caldavar,2026-09-02,{heroName}");
                }
                else // Empty row (50 total)
                {
                    rows.Add("");
                }
            }

            CallMatchList(lua, rows);

            // Verify state
            long listLen = (long)lua.DoString("return table.getn(_matchList)")[0];
            AssertTest("S1.1: table.getn(_matchList) == 50", listLen == 50, $"Got {listLen}");

            // Verify sequential contiguous keys 1..50 with NO sparse array holes
            bool noHoles = true;
            for (int k = 1; k <= 50; k++)
            {
                object[] res = lua.DoString($"return _matchList[{k}] ~= nil and _matchList[{k}].id or nil");
                if (res == null || res.Length == 0 || res[0] == null)
                {
                    noHoles = false;
                    break;
                }
            }
            AssertTest("S1.2: All keys 1..50 contiguously populated without holes", noHoles);

            // Verify UI: 'playerstats_match_no_history' is hidden (Visible = 0)
            bool noHistoryVisible = WidgetVisibilities.TryGetValue("playerstats_match_no_history", out bool nhv) && nhv;
            AssertTest("S1.3: 'playerstats_match_no_history' widget is hidden", !noHistoryVisible);

            // Verify 50 row instantiations occurred
            int rowInstantiations = Instantiations.Count(inst => inst.Contains("playerstats_match_row_template"));
            AssertTest("S1.4: Exactly 50 match row templates instantiated", rowInstantiations == 50, $"Instantiated: {rowInstantiations}");
        }
        catch (Exception ex)
        {
            AssertTest("S1: Exception during 100 rows execution", false, ex.Message);
        }

        // -------------------------------------------------------------------------
        // Scenario 2: All Empty Rows (20 empty, 100 empty)
        // -------------------------------------------------------------------------
        Console.WriteLine("\n--- Scenario 2: All Empty Rows (20 and 100 empty entries) ---");
        ResetState();
        try
        {
            // 20 empty rows
            List<string> empty20 = Enumerable.Repeat("", 20).ToList();
            CallMatchList(lua, empty20);

            long len20 = (long)lua.DoString("return table.getn(_matchList)")[0];
            AssertTest("S2.1: table.getn(_matchList) == 0 for 20 empty rows", len20 == 0, $"Got {len20}");

            bool nhVisible20 = WidgetVisibilities.TryGetValue("playerstats_match_no_history", out bool v20) && v20;
            AssertTest("S2.2: 'playerstats_match_no_history' displayed for 20 empty rows", nhVisible20);

            // 100 empty rows
            ResetState();
            List<string> empty100 = Enumerable.Repeat("", 100).ToList();
            CallMatchList(lua, empty100);

            long len100 = (long)lua.DoString("return table.getn(_matchList)")[0];
            AssertTest("S2.3: table.getn(_matchList) == 0 for 100 empty rows", len100 == 0, $"Got {len100}");

            bool nhVisible100 = WidgetVisibilities.TryGetValue("playerstats_match_no_history", out bool v100) && v100;
            AssertTest("S2.4: 'playerstats_match_no_history' displayed for 100 empty rows", nhVisible100);

            int instantiationsEmpty = Instantiations.Count(inst => inst.Contains("playerstats_match_row_template"));
            AssertTest("S2.5: Zero row templates instantiated for empty input", instantiationsEmpty == 0, $"Instantiated: {instantiationsEmpty}");
        }
        catch (Exception ex)
        {
            AssertTest("S2: Exception during all-empty rows execution", false, ex.Message);
        }

        // -------------------------------------------------------------------------
        // Scenario 3: Malformed & Corrupted Rows (50 edge cases)
        // -------------------------------------------------------------------------
        Console.WriteLine("\n--- Scenario 3: Malformed & Corrupted Rows (50 adversarial cases) ---");
        ResetState();
        try
        {
            List<string> malformedRows =
            [
                // Truncated columns (< 10 columns)
                "1001",
                "1002,1",
                "1003,1,1",
                "1004,1,1,5",
                "1005,1,1,5,2",
                "1006,1,1,5,2,8",
                "1007,1,1,5,2,8,15",
                "1008,1,1,5,2,8,15,1800",
                "1009,1,1,5,2,8,15,1800,caldavar",
                // Corrupt delimiters (semicolon, tab, pipe, slash)
                "1010;1;1;5;2;8;15;1800;caldavar;2026-09-02;Hero_Aluna",
                "1011\t1\t1\t5\t2\t8\t15\t1800\tcaldavar\t2026-09-02\tHero_Aluna",
                "1012|1|1|5|2|8|15|1800|caldavar|2026-09-02|Hero_Aluna",
                "1013/1/1/5/2/8/15/1800/caldavar/2026-09-02/Hero_Aluna",
                // SQL Injection & script injection in fields
                "1014'; DROP TABLE matches;--,1,1,5,2,8,15,1800,caldavar,2026-09-02,Hero_Aluna",
                "<script>alert('xss')</script>,1,1,5,2,8,15,1800,caldavar,2026-09-02,Hero_Aluna",
                "1016,1,1,5,2,8,15,1800,caldavar,2026-09-02,<img src=x onerror=alert(1)>",
                // Unicode and special characters
                "1017,1,1,5,2,8,15,1800,caldavar,2026-09-02,✨🔥Hero_Special🔥✨",
                "1018,1,1,5,2,8,15,1800,caldavar,2026-09-02,日本語ヒーロー",
                // Whitespace variations
                "   ",
                "\t\t\t",
                ",,,,,,,,,", // 10 empty commas -> 10 empty parts!
                ",,,,,,,,,,", // 11 empty commas
                // Extreme numeric values
                "1023,1,1,-10,-5,-20,15,-1800,caldavar,2026-09-02,Hero_Aluna",
                "99999999999999999,1,1,9999,9999,9999,15,99999999,caldavar,2026-09-02,Hero_Aluna",
                // Non-numeric fields
                "1025,WIN,TEAM_A,TEN,TWO,EIGHT,FIFTEEN,HALF_HOUR,MAP,TODAY,HERO",
                // Single valid row amidst malformed
                "1026,1,1,10,2,8,15,1800,caldavar,2026-09-02,Hero_Aluna"
            ];

            CallMatchList(lua, malformedRows);

            long malformedCount = (long)lua.DoString("return table.getn(_matchList)")[0];
            AssertTest("S3.1: Malformed suite executed without throwing Lua exception", true);

            // Valid entries: the 10-comma entries (parts >= 10), SQL injection (has 11 parts), unicode (has 11 parts),
            // extreme numbers (has 11 parts), non-numeric (has 11 parts), and row 1026.
            // All < 10 parts rows should be cleanly skipped.
            AssertTest("S3.2: Malformed rows skipped without sparse holes", malformedCount > 0, $"Parsed count: {malformedCount}");

            bool contiguous = true;
            for (int k = 1; k <= (int)malformedCount; k++)
            {
                object[] r = lua.DoString($"return _matchList[{k}] ~= nil");
                if (r == null || (bool)r[0] == false)
                {
                    contiguous = false;
                    break;
                }
            }
            AssertTest("S3.3: Parsed elements remain contiguous", contiguous);
        }
        catch (Exception ex)
        {
            AssertTest("S3: Malformed rows threw unhandled exception", false, ex.Message);
        }

        // -------------------------------------------------------------------------
        // Scenario 4: 100 Rows With 50 Empty, 25 Malformed, 25 Valid
        // -------------------------------------------------------------------------
        Console.WriteLine("\n--- Scenario 4: 100 Rows (50 Empty, 25 Malformed, 25 Valid) ---");
        ResetState();
        try
        {
            List<string> mixed100 = [];
            int validGen = 0;
            for (int i = 1; i <= 100; i++)
            {
                if (i % 4 == 0) // 25 Valid rows
                {
                    validGen++;
                    mixed100.Add($"40{validGen:D2},1,1,7,2,5,15,1800,caldavar,2026-09-02,Hero_Aluna");
                }
                else if (i % 4 == 2) // 25 Malformed (< 10 columns)
                {
                    mixed100.Add($"malformed_row_{i},col1,col2");
                }
                else // 50 Empty rows (i % 4 == 1 or 3)
                {
                    mixed100.Add("");
                }
            }

            AssertTest("S4.0: Generated exactly 100 mixed rows", mixed100.Count == 100);

            CallMatchList(lua, mixed100);

            long mixedLen = (long)lua.DoString("return table.getn(_matchList)")[0];
            AssertTest("S4.1: Exactly 25 valid matches parsed into _matchList", mixedLen == 25, $"Got {mixedLen}");

            // Verify all 25 are sequential without holes
            bool mixedContiguous = true;
            for (int k = 1; k <= 25; k++)
            {
                object[] res = lua.DoString($"return _matchList[{k}] ~= nil and _matchList[{k}].id or nil");
                if (res == null || res.Length == 0 || res[0] == null)
                {
                    mixedContiguous = false;
                    break;
                }
            }
            AssertTest("S4.2: Keys 1..25 are 100% contiguous without holes", mixedContiguous);

            int mixedInst = Instantiations.Count(inst => inst.Contains("playerstats_match_row_template"));
            AssertTest("S4.3: Exactly 25 row templates instantiated in UI", mixedInst == 25, $"Instantiated: {mixedInst}");
        }
        catch (Exception ex)
        {
            AssertTest("S4: Mixed 100 rows threw unhandled exception", false, ex.Message);
        }

        // -------------------------------------------------------------------------
        // Scenario 5: Hero ID Map Fallback & Empty Hero Name Variations
        // -------------------------------------------------------------------------
        Console.WriteLine("\n--- Scenario 5: Hero ID Fallback and Legacy 10-Column Handling ---");
        ResetState();
        try
        {
            List<string> fallbackRows =
            [
                "5001,1,1,5,2,8,15,1800,caldavar,2026-09-02,", // Col 11 empty -> ID 15 -> Hero_Aluna
                "5002,1,1,5,2,8,15,1800,caldavar,2026-09-02,0", // Col 11 '0' -> ID 15 -> Hero_Aluna
                "5003,1,1,5,2,8,25,1800,caldavar,2026-09-02,", // Col 11 empty -> ID 25 -> Hero_Magmus
                "5004,1,1,5,2,8,15,1800,caldavar,2026-09-02", // 10-column legacy -> ID 15 -> Hero_Aluna
                "5005,1,1,5,2,8,999,1800,caldavar,2026-09-02,", // ID 999 unknown in HeroIdMap -> empty string
                "5006,1,1,5,2,8,15,1800,caldavar,2026-09-02,Hero_Aluna,trailing_token_12" // 12 columns
            ];

            CallMatchList(lua, fallbackRows);

            long fbLen = (long)lua.DoString("return table.getn(_matchList)")[0];
            AssertTest("S5.1: Exactly 6 rows parsed", fbLen == 6, $"Got {fbLen}");

            string h1 = (string)lua.DoString("return _matchList[1].heroname")[0];
            AssertTest("S5.2: Row 1 col 11 empty resolved to Hero_Aluna", h1 == "Hero_Aluna", $"Got '{h1}'");

            string h2 = (string)lua.DoString("return _matchList[2].heroname")[0];
            AssertTest("S5.3: Row 2 col 11 '0' resolved to Hero_Aluna", h2 == "Hero_Aluna", $"Got '{h2}'");

            string h3 = (string)lua.DoString("return _matchList[3].heroname")[0];
            AssertTest("S5.4: Row 3 col 11 empty resolved to Hero_Magmus", h3 == "Hero_Magmus", $"Got '{h3}'");

            string h4 = (string)lua.DoString("return _matchList[4].heroname")[0];
            AssertTest("S5.5: Row 4 10-column row resolved to Hero_Aluna", h4 == "Hero_Aluna", $"Got '{h4}'");

            string h5 = (string)lua.DoString("return _matchList[5].heroname")[0];
            AssertTest("S5.6: Row 5 unknown hero ID safely resolved to empty string without nil error", h5 == "", $"Got '{h5}'");

            string h6 = (string)lua.DoString("return _matchList[6].heroname")[0];
            AssertTest("S5.7: Row 6 12-column row parsed Hero_Aluna cleanly", h6 == "Hero_Aluna", $"Got '{h6}'");
        }
        catch (Exception ex)
        {
            AssertTest("S5: Fallback rows threw unhandled exception", false, ex.Message);
        }

        lua.Dispose();
        Console.WriteLine($"\nSummary: {passed} passed, {failed} failed.");
        return failed == 0;
    }

    private static Lua InitializeLuaEnvironment(string scriptPath)
    {
        Lua lua = new();
        lua.LoadCLRPackage();

        lua.DoString(@"
            _G = _G or {}
            function GetWidget(name) return _G.MockGetWidget(name) end
            function Translate(key, ...) return key end
            function GetHeroMasteryUpgradeInfo(info) return info end
            function GetMasteryRewardsInfo(info) return info end
            function GetHeroProficiency(info) return {} end
            
            Widget = {}
            Widget.__index = Widget
            function Widget:SetTexture(texture) _G.MockWidgetSetTexture(self.name, texture) end
            function Widget:SetVisible(vis) _G.MockWidgetSetVisible(self.name, vis) end
            function Widget:SetEnabled(en) end
            function Widget:SetText(txt) _G.MockWidgetSetText(self.name, txt) end
            function Widget:SetSafeText(txt) _G.MockWidgetSetText(self.name, txt) end
            function Widget:SetFont(font) end
            function Widget:SetColor(color) end
            function Widget:SetGlow(val) end
            function Widget:SetGlowColor(val) end
            function Widget:SetBackgroundGlow(val) end
            function Widget:SetBorderGlow(val) end
            function Widget:SetTextGlow(val) end
            function Widget:SetShadow(val) end
            function Widget:SetShadowColor(val) end
            function Widget:SetShadowOffset(x, y) end
            function Widget:SetOutline(val) end
            function Widget:SetOutlineColor(color) end
            function Widget:SetWidth(w) end
            function Widget:SetHeight(h) end
            function Widget:SetAbsoluteX(x) end
            function Widget:SetAbsoluteY(y) end
            function Widget:SetX(x) end
            function Widget:SetY(y) end
            function Widget:SetValue(v) end
            function Widget:SetMaxValue(v) end
            function Widget:SetRenderMode(m) end
            function Widget:SetSelectedItemByValue(...) end
            function Widget:SetAvatar(url) end
            function Widget:SetCallback(ev, cb) end
            function Widget:ClearCallback(ev) end
            function Widget:ClearChildren() _G.MockWidgetClearChildren(self.name) end
            function Widget:GetAbsoluteX() return 0 end
            function Widget:GetAbsoluteY() return 0 end
            function Widget:GetWidth() return 100 end
            function Widget:GetHeight() return 100 end
            function Widget:GetValue() return '0' end
            function Widget:GetText() return '' end
            function Widget:GetName() return self.name or '' end
            function Widget:IsVisible() return true end
            function Widget:IsEnabled() return true end
            function Widget:DoEvent(...) end
            function Widget:UICmd(cmd) if cmd and string.find(tostring(cmd), 'GetScrollbarMaxValue') then return '100' end return '' end
            function Widget:Sleep(...) end
            function Widget:FadeIn(...) end
            function Widget:FadeOut(...) end
            function Widget:Show(...) end
            function Widget:Hide(...) end
            function Widget:Enable(...) end
            function Widget:Disable(...) end
            function Widget:RegisterWatch(n, f) end
            function Widget:GetWidget(n) return _G.MockGetWidget(n) end
            function Widget:Instantiate(...) 
                _G.MockWidgetInstantiate(self.name or '')
                return _G.MockGetWidget('new_instanced_widget') 
            end
            
            function _G.MockGetWidget(name)
                local w = { name = name }
                setmetatable(w, Widget)
                return w
            end

            object = {
                GetName = function(self) return 'player_stats_v2' end,
                RegisterWatch = function(self, ...) end,
                GetWidget = function(self, name) return _G.MockGetWidget(name) end,
                UICmd = function(self, cmd) return '' end
            }
            setmetatable(object, Widget)

            Player_Stats_V2 = {}
            Player_Stats_V2.SetMasteryRewardsInfo = function(self, ...) end
            Player_Stats_V2.SetMasteryAllHeroes = function(self, ...) end
            Player_Stats_V2.ShowWaitingMask = function(self, ...) end
            Player_Stats_V2.HideWaitingMask = function(self, ...) end
            
            function SetOverviewSeasonInfo(...) end
            function SetOverviewMasteryInfo(...) end
            function SetPlayerStatsStatsInfo(...) end
            function SetTipMatchesPlayed(...) end
            function SetTipDisconnects(...) end
            function SetMyMasteryExp(...) end
            function GetMasterTypeByLevel(level) return 1 end
            function GetMasteryLevelByExp(exp) return 1 end
            function println(msg) end
            table.getn = function(t) return #t end
            
            -- Upgrades, Chat, and Icon Mocks
            function GetAccountIconTexturePathFromUpgrades(u, id) return '/mock/icon.tga' end
            function GetChatNameColorStringFromUpgrades(u) return 'white' end
            function GetChatNameColorFontFromUpgrades(u) return 'font' end
            function GetChatNameGlowFromUpgrades(u) return 'glow' end
            function GetChatNameGlowColorStringFromUpgrades(u) return 'white' end
            function GetChatNameBackgroundGlowFromUpgrades(u) return 'none' end
            function GetChatSymbolTexturePathFromUpgrades(u) return '' end
            function GetChatNameColorTexturePathFromUpgrades(u) return '' end
            function GetMasterServerAvatarURL(id) return 'http://127.0.0.1:55555/avatar.png' end
            function GetAccountName() return 'TestUser' end
            function IsMe(nick) return true end
            function IsBuddy(nick) return false end
            function GetEntityDisplayName(name) return name end
            function GetHeroIconPath(name) return '/heroes/'..tostring(name)..'/icon.tga' end
            
            -- Cvars and K2 Global Commands
            function GetCvarBool(cvar) return false end
            function GetCvarInt(cvar) return 0 end
            function GetCvarString(cvar) return '' end
            function Set(name, val) end
            function SetSave(name, val, type) end
            function Cmd(cmd) end
            function ShowWidget(w) end
            function HideWidget(w) end
            function SubmitForm(...) end

            find = string.find
            sub = string.sub
            tinsert = table.insert

            function explode(d,p)
                if (d) and (p) and (type(p) == 'string') then
                    local t, ll
                    t={}
                    ll=0
                    if(#p == 1) then return {p} end
                    while true do
                        l=find(p,d,ll,true)
                        if l~=nil then
                            tinsert(t, sub(p,ll,l-1))
                            ll=l+1
                        else
                            tinsert(t, sub(p,ll))
                            break
                        end
                    end
                    return t
                else
                    return {}
                end
            end

            function TranslateOrEmpty(inputString)
                local str = Translate(inputString)
                if (str == inputString) then
                    return ''
                else
                    return str
                end
            end

            HeroDefinitionsCache = {
                Heroes = {},
                HeroIdMap = {
                    [15] = { name = 'Hero_Aluna', displayName = 'Aluna' },
                    [25] = { name = 'Hero_Magmus', displayName = 'Magmus' }
                },
                GetHeroDisplayName = function(name) return name end,
                GetHeroIcon = function(name) return '/heroes/'..tostring(name)..'/icon.tga' end,
                ParseMasteryInfo = function(info) return {} end
            }

            Client = {
                GetCookie = function() return 'mock_cookie' end
            }
            
            function NotEmpty(str) return str ~= nil and str ~= '' end
            function Empty(str) return str == nil or str == '' end
            function AtoB(val) return val == 'true' or val == '1' or val == 1 or val == true end
            function AtoN(val) return tonumber(val) or 0 end
            function StripClanTag(name) return name end
        ");

        lua.RegisterFunction("MockWidgetSetText", typeof(Program).GetMethod("RecordWidgetText"));
        lua.RegisterFunction("MockWidgetSetTexture", typeof(Program).GetMethod("RecordWidgetTexture"));
        lua.RegisterFunction("MockWidgetSetVisible", typeof(Program).GetMethod("RecordWidgetVisibility"));
        lua.RegisterFunction("MockWidgetClearChildren", typeof(Program).GetMethod("RecordClearChildren"));
        lua.RegisterFunction("MockWidgetInstantiate", typeof(Program).GetMethod("RecordInstantiation"));

        string content = File.ReadAllText(scriptPath);
        content = content.Replace("local _G = getfenv(0)", "local _G = _G");
        content = content.Replace("local _seasonStatsNormal = {}", "_seasonStatsNormal = {}");
        content = content.Replace("local _seasonStatsCasual = {}", "_seasonStatsCasual = {}");
        content = content.Replace("local _matchList = {}", "_matchList = {}");
        content = Regex.Replace(content, @"function\s+([a-zA-Z0-9_:\.]+)\s*\(\.\.\.\)",
            "function $1(...) local arg = { ... }; arg.n = #arg");

        lua.DoString("unpack = table.unpack");
        lua.DoString(content);
        lua.DoString("Player_Stats_V2:Init()");

        return lua;
    }

    private static bool RunHeroLoginControllerSuite(string scriptPath, bool isSimplified = false)
    {
        int passed = 0;
        int failed = 0;

        void AssertTest(string testName, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  [PASS] {testName} {(string.IsNullOrEmpty(detail) ? "" : $"({detail})")}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {testName} - {detail}");
            }
        }

        using Lua lua = new();
        lua.LoadCLRPackage();

        lua.DoString(@"
            _G = _G or {}
            HostTime = 1000
            _frameTime = 16.66
            _screenWidth = 1920
            _screenHeight = 1080
            _cursorX = 960
            _cursorY = 540
            _lastSound = ''

            function GetFrameTime() return _frameTime end
            function GetScreenWidth() return _screenWidth end
            function GetScreenHeight() return _screenHeight end
            function PlaySound(path) _lastSound = path end

            Input = {
                GetCursorPosX = function() return _cursorX end,
                GetCursorPosY = function() return _cursorY end
            }

            MockModelPanel = {
                model = '',
                anim = '',
                scale = 1.0,
                camX = 0, camY = 0, camZ = 0,
                camP = 0, camYw = 0, camR = 0,
                camFOV = 40,
                effect = '',
                pitch = 0, roll = 0, yaw = 0,
                pos = '',
                callbacks = {}
            }
            MockModelPanel.__index = MockModelPanel

            function MockModelPanel:new()
                local o = { callbacks = {} }
                setmetatable(o, MockModelPanel)
                return o
            end

            function MockModelPanel:SetModel(m) self.model = m end
            function MockModelPanel:SetAnim(a) self.anim = a end
            function MockModelPanel:SetModelScale(s) self.scale = s end
            function MockModelPanel:SetModelPos(p) self.pos = p end
            function MockModelPanel:SetCameraPos(x, y, z) self.camX = x; self.camY = y; self.camZ = z end
            function MockModelPanel:SetCameraAngles(p, y, r) self.camP = p; self.camYw = y; self.camR = r end
            function MockModelPanel:SetCameraFOV(fov) self.camFOV = fov end
            function MockModelPanel:SetEffect(e) self.effect = e end
            function MockModelPanel:ClearEffect() self.effect = '' end
            function MockModelPanel:SetModelAngles(p, r, y) self.pitch = p; self.roll = r; self.yaw = y end
            function MockModelPanel:SetCallback(ev, cb) self.callbacks[ev] = cb end
            function MockModelPanel:RefreshCallbacks() self.refreshed = true end
        ");

        try
        {
            string content = File.ReadAllText(scriptPath);
            lua.DoString(content);
            AssertTest("H1.0: Lua script parsed and compiled with zero syntax errors", true);

            if (isSimplified)
            {
                // Simplified 25-line script verification
                lua.DoString(@"
                    widget = MockModelPanel:new()
                    HeroLoginController:Init(widget)
                ");

                string model = (string)lua.DoString("return widget.model")[0];
                AssertTest("H1.1: Simplified HeroLoginController:Init sets model /heroes/empath/model.mdf", model == "/heroes/empath/model.mdf", $"Got '{model}'");

                string anim = (string)lua.DoString("return widget.anim")[0];
                AssertTest("H1.2: Simplified HeroLoginController:Init sets anim idle", anim == "idle", $"Got '{anim}'");

                double scale = Convert.ToDouble(lua.DoString("return widget.scale")[0]);
                AssertTest("H1.3: Simplified HeroLoginController:Init sets scale 1.18", Math.Abs(scale - 1.18) < 0.001, $"Got {scale}");
            }
            else
            {
                // Full 243-line controller verification
                // 1. Verify table structure and character presets
                lua.DoString(@"
                    assert(HeroLoginController ~= nil, 'HeroLoginController table is nil')
                    assert(HeroLoginController.heroes ~= nil, 'HeroLoginController.heroes is nil')
                    assert(HeroLoginController.heroes.empath ~= nil, 'empath preset is nil')
                    assert(HeroLoginController.heroes.hd_empath ~= nil, 'hd_empath preset is nil')
                    assert(HeroLoginController.heroes.kongor ~= nil, 'kongor preset is nil')
                ");
                AssertTest("H1.1: HeroLoginController table and presets (empath, hd_empath, kongor) defined", true);

                string empathModel = (string)lua.DoString("return HeroLoginController.heroes.empath.model")[0];
                AssertTest("H1.2: Empath preset points to valid Empath model.mdf", empathModel is "/heroes/empath/alt9/model.mdf" or "/heroes/empath/model.mdf", $"Got '{empathModel}'");

                string hdEmpathModel = (string)lua.DoString("return HeroLoginController.heroes.hd_empath.model")[0];
                AssertTest("H1.3: HD Empath preset points to valid Empath model.mdf", hdEmpathModel is "/heroes/empath/alt9/model.mdf" or "/heroes/empath/hd_empath/model.mdf", $"Got '{hdEmpathModel}'");

                string kongorModel = (string)lua.DoString("return HeroLoginController.heroes.kongor.model")[0];
                AssertTest("H1.4: Kongor preset points to /npcs/kongor/model.mdf", kongorModel == "/npcs/kongor/model.mdf", $"Got '{kongorModel}'");

                // 2. Initialize with MockModelPanel
                lua.DoString(@"
                    widget = MockModelPanel:new()
                    HeroLoginController:OnInit(widget)
                ");

                string initialModel = (string)lua.DoString("return widget.model")[0];
                AssertTest("H2.1: OnInit initializes model to active hero", initialModel == empathModel, $"Got '{initialModel}'");

                string initialAnim = (string)lua.DoString("return widget.anim")[0];
                AssertTest("H2.2: OnInit initializes idle animation", initialAnim == "idle", $"Got '{initialAnim}'");

                bool hasOnFrame = (bool)lua.DoString("return widget.callbacks['onframe'] ~= nil")[0];
                AssertTest("H2.3: OnInit registers onframe callback", hasOnFrame);

                // 3. SwitchHero tests
                lua.DoString(@"HeroLoginController:SwitchHero('hd_empath')");
                string switchedModel = (string)lua.DoString("return widget.model")[0];
                AssertTest("H3.1: SwitchHero('hd_empath') updates model to hd_empath preset", switchedModel == hdEmpathModel, $"Got '{switchedModel}'");

                lua.DoString(@"HeroLoginController:SwitchHero('kongor')");
                string kongorSwitchedModel = (string)lua.DoString("return widget.model")[0];
                AssertTest("H3.2: SwitchHero('kongor') updates model to /npcs/kongor/model.mdf", kongorSwitchedModel == "/npcs/kongor/model.mdf", $"Got '{kongorSwitchedModel}'");

                // Switch back to empath for physics tests
                lua.DoString(@"HeroLoginController:SwitchHero('empath')");

                // 4. OnFrameUpdate physics test: Center cursor (no deflection)
                lua.DoString(@"
                    _cursorX = 960
                    _cursorY = 540
                    _frameTime = 16.66
                    HeroLoginController:OnFrameUpdate()
                ");
                double yawCenter = Convert.ToDouble(lua.DoString("return HeroLoginController.state.currentYaw")[0]);
                AssertTest("H4.1: Physics integration at center cursor remains stable", Math.Abs(yawCenter - 180.0) < 1.0, $"Yaw: {yawCenter}");

                // Deflect cursor to top right: cursorX = 1920, cursorY = 0 -> normX=1, normY=1
                // TargetYaw = 180 + 24 = 204, TargetPitch = 0 - 12 = -12
                lua.DoString(@"
                    _cursorX = 1920
                    _cursorY = 0
                    for i = 1, 30 do
                        HeroLoginController:OnFrameUpdate()
                    end
                ");
                double yawDeflected = Convert.ToDouble(lua.DoString("return HeroLoginController.state.currentYaw")[0]);
                double pitchDeflected = Convert.ToDouble(lua.DoString("return HeroLoginController.state.currentPitch")[0]);
                AssertTest("H4.2: Spring-damper head tracking deflects yaw towards target (180 -> 204)", yawDeflected > 185.0 && !double.IsNaN(yawDeflected), $"Yaw: {yawDeflected}");
                AssertTest("H4.3: Spring-damper head tracking deflects pitch towards target (0 -> -12)", pitchDeflected < -2.0 && !double.IsNaN(pitchDeflected), $"Pitch: {pitchDeflected}");

                // 5. High refresh rate (240Hz, dt=4.16ms) and lag spike clamping (dt=200ms -> clamped to 50ms)
                lua.DoString(@"
                    _frameTime = 4.16
                    for i = 1, 60 do
                        HeroLoginController:OnFrameUpdate()
                    end
                ");
                double yaw240 = Convert.ToDouble(lua.DoString("return HeroLoginController.state.currentYaw")[0]);
                AssertTest("H5.1: 240Hz simulation executes smoothly without NaN", !double.IsNaN(yaw240) && !double.IsInfinity(yaw240), $"Yaw: {yaw240}");

                lua.DoString(@"
                    _frameTime = 200.0
                    HeroLoginController:OnFrameUpdate()
                ");
                double yawLag = Convert.ToDouble(lua.DoString("return HeroLoginController.state.currentYaw")[0]);
                AssertTest("H5.2: Lag spike delta-time clamp (<= 50ms) prevents numerical explosion", !double.IsNaN(yawLag) && !double.IsInfinity(yawLag), $"Yaw: {yawLag}");

                // 6. Interactive gestures and click callbacks
                lua.DoString(@"HeroLoginController:OnPrimaryClick(widget)");
                string clickAnim = (string)lua.DoString("return widget.anim")[0];
                bool isActingClick = (bool)lua.DoString("return HeroLoginController.state.isActing")[0];
                AssertTest("H6.1: OnPrimaryClick triggers attack_1 anim and enters acting state", clickAnim == "attack_1" && isActingClick, $"Anim: {clickAnim}, Acting: {isActingClick}");

                // During action, orientation returns to base resting position
                lua.DoString(@"HeroLoginController:OnFrameUpdate()");
                double targetYawActing = Convert.ToDouble(lua.DoString("return HeroLoginController.state.targetYaw")[0]);
                AssertTest("H6.2: While acting, targetYaw returns to base resting yaw (180)", Math.Abs(targetYawActing - 180.0) < 0.001, $"TargetYaw: {targetYawActing}");

                // Advance HostTime past actionEndTime -> state resets to idle
                lua.DoString(@"
                    HostTime = HostTime + 3000
                    HeroLoginController:OnFrameUpdate()
                ");
                bool isActingAfter = (bool)lua.DoString("return HeroLoginController.state.isActing")[0];
                string animAfter = (string)lua.DoString("return widget.anim")[0];
                AssertTest("H6.3: After action duration, isActing resets and anim returns to idle", !isActingAfter && animAfter == "idle", $"Acting: {isActingAfter}, Anim: {animAfter}");

                // Secondary click
                lua.DoString(@"HeroLoginController:OnSecondaryClick(widget)");
                string tauntAnim = (string)lua.DoString("return widget.anim")[0];
                AssertTest("H6.4: OnSecondaryClick triggers taunt_1 anim", tauntAnim == "taunt_1", $"Anim: {tauntAnim}");

                // Mouse hover callbacks
                lua.DoString(@"HeroLoginController:OnMouseOver(widget)");
                bool isHovered = (bool)lua.DoString("return HeroLoginController.state.isHovered")[0];
                AssertTest("H6.5: OnMouseOver sets isHovered to true", isHovered);

                lua.DoString(@"HeroLoginController:OnMouseOut(widget)");
                bool isHoveredOut = (bool)lua.DoString("return HeroLoginController.state.isHovered")[0];
                AssertTest("H6.6: OnMouseOut sets isHovered to false", !isHoveredOut);

                // Idle timeout trigger (14000ms)
                lua.DoString(@"
                    HostTime = HostTime + 20000
                    HeroLoginController.state.isActing = false
                    HeroLoginController:OnFrameUpdate()
                ");
                string boredAnim = (string)lua.DoString("return widget.anim")[0];
                AssertTest("H6.7: Idle bored timeout triggers bored_1 gesture after inactivity", boredAnim == "bored_1", $"Anim: {boredAnim}");

                // Show/Hide lifecycle
                lua.DoString(@"HeroLoginController:OnHide(widget)");
                bool isActingHide = (bool)lua.DoString("return HeroLoginController.state.isActing")[0];
                AssertTest("H6.8: OnHide clears isActing state", !isActingHide);

                lua.DoString(@"HeroLoginController:OnShow(widget)");
                string modelShow = (string)lua.DoString("return widget.model")[0];
                AssertTest("H6.9: OnShow reapplies hero configuration cleanly", modelShow == empathModel, $"Model: {modelShow}");
            }
        }
        catch (Exception ex)
        {
            AssertTest("H: Unhandled exception during HeroLoginController execution", false, ex.Message);
        }

        Console.WriteLine($"\nHeroLoginController Summary: {passed} passed, {failed} failed.");
        return failed == 0;
    }

    private static bool RunNexusBridgeLifecycleSuite(string scriptPath)
    {
        int passed = 0;
        int failed = 0;

        void AssertTest(string testName, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"  [PASS] {testName} {(string.IsNullOrEmpty(detail) ? "" : $"({detail})")}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {testName} - {detail}");
            }
        }

        Console.WriteLine("--- Milestone 1: Sandboxed Lua Lifecycle & ApplySnapshot Stress Suite ---");

        List<string> dispatchedCommands = [];
        List<string> echoedMessages = [];
        int currentPhase = 0;

        Lua lua = new();
        lua.LoadCLRPackage();

        // Setup K2 environment mocks
        lua.DoString(@"
            _G = _G or {}
            table.getn = function(t) return #t end
            dispatchedCommands = {}
            echoedMessages = {}
            function Trigger(...) end
            function Set(...) end
        ");

        // Register functions
        lua["Cmd"] = (Action<string>)(cmd => {
            dispatchedCommands.Add(cmd);
        });

        lua["Echo"] = (Action<string>)(msg => {
            echoedMessages.Add(msg);
        });

        lua["GetCvarInt"] = (Func<string, bool?, object?>)((cvar, fallback) => {
            if (cvar == "game_phase" || cvar == "_game_phase")
                return currentPhase;
            return 0;
        });

        lua["GetCvarBool"] = (Func<string, bool>)(cvar => false);
        lua["GetCvarString"] = (Func<string, string>)(cvar => "");
        lua["UIGamePhase"] = (Func<int>)(() => currentPhase);

        // Mock Cvar table
        lua.DoString(@"
            Cvar = {
                GetCvar = function(name)
                    return {
                        GetNumber = function() return _currentPhase end,
                        GetString = function() return tostring(_currentPhase) end,
                        Set = function() end
                    }
                end,
                CreateCvar = function() end
            }
            UIManager = {
                GetInterface = function(name)
                    return {
                        UICmd = function(self, cmd)
                            if cmd == 'GetCurrentGamePhase()' then
                                return tostring(_currentPhase)
                            end
                            return ''
                        end,
                        GetWidget = function(self, w)
                            return {
                                SetVisible = function() end,
                                DoEventN = function() end
                            }
                        end,
                        RegisterWatch = function() end,
                        UnregisterWatch = function() end
                    }
                end
            }
            HoN_Main = {
                gamePhase = 0
            }
        ");

        // Load lib_json if available
        string libJsonPath = Path.GetFullPath("games/shared/ui/scripts/lib_json.lua");
        if (File.Exists(libJsonPath))
        {
            try
            {
                lua.DoFile(libJsonPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not load lib_json: {ex.Message}");
            }
        }

        // Load nexus_bridge.lua
        try
        {
            lua.DoFile(scriptPath);
            AssertTest("L0.1: nexus_bridge.lua parsed and executed without syntax error", true);
        }
        catch (Exception ex)
        {
            AssertTest("L0.1: nexus_bridge.lua parsed and executed without syntax error", false, ex.Message);
            return false;
        }

        // Verify _G.Nexus global table
        bool nexusExists = (bool)lua.DoString("return _G.Nexus ~= nil and type(_G.Nexus) == 'table'")[0];
        AssertTest("L0.2: _G.Nexus table is globally anchored in root environment", nexusExists);

        // Helper to update phase across all mock hooks
        void SetEnginePhase(int phase)
        {
            currentPhase = phase;
            lua["_currentPhase"] = phase;
            lua.DoString($"HoN_Main.gamePhase = {phase}");
        }

        // ---------------------------------------------------------------------
        // Suite 1: Phase Introspection Across All Phases (-1, 0, 1, 2, 3, 4, 5, 6)
        // ---------------------------------------------------------------------
        Console.WriteLine("\n--- Phase Introspection Across Phases -1, 0, 1, 2, 3, 4, 5, 6 ---");
        int[] allPhases = [-1, 0, 1, 2, 3, 4, 5, 6];

        foreach (int p in allPhases)
        {
            SetEnginePhase(p);
            long observed = (long)lua.DoString("return Nexus.GetCurrentPhase()")[0];
            if (p <= 0)
            {
                AssertTest($"P1.Phase_{p}: GetCurrentPhase() handles phase {p} safely", observed == 0 || observed == p, $"Observed {observed}");
            }
            else
            {
                AssertTest($"P1.Phase_{p}: GetCurrentPhase() strictly detects phase {p}", observed == p, $"Expected {p}, got {observed}");
            }
        }

        // ---------------------------------------------------------------------
        // Suite 2: Nexus.VerifyPhase Matrix Verification
        // ---------------------------------------------------------------------
        Console.WriteLine("\n--- Nexus.VerifyPhase Matrix Stress Testing ---");

        // 2.1: VerifyPhase({3}) (Array with phase 3)
        foreach (int p in allPhases)
        {
            SetEnginePhase(p);
            lua.DoString("res = Nexus.VerifyPhase({3})");
            bool success = (bool)lua.DoString("return res.success")[0];

            if (p == 3)
            {
                AssertTest($"P2.Array_{p}: VerifyPhase({{3}}) allows active gameplay phase 3", success);
                long curP = (long)lua.DoString("return res.currentPhase")[0];
                AssertTest("P2.Array_3_Cur: res.currentPhase is 3", curP == 3);
            }
            else
            {
                AssertTest($"P2.Array_{p}: VerifyPhase({{3}}) strictly blocks phase {p}", !success);
                string errCode = (string)lua.DoString("return res.errorCode or ''")[0];
                AssertTest($"P2.Array_{p}_Err: Returns INVALID_GAME_PHASE for phase {p}", errCode == "INVALID_GAME_PHASE", $"Got '{errCode}'");
            }
        }

        // 2.2: VerifyPhase(3) (Scalar number 3)
        foreach (int p in allPhases)
        {
            SetEnginePhase(p);
            lua.DoString("res = Nexus.VerifyPhase(3)");
            bool success = (bool)lua.DoString("return res.success")[0];

            if (p == 3)
            {
                AssertTest($"P2.Scalar_{p}: VerifyPhase(3) allows active gameplay phase 3", success);
            }
            else
            {
                AssertTest($"P2.Scalar_{p}: VerifyPhase(3) strictly blocks phase {p}", !success);
                string errCode = (string)lua.DoString("return res.errorCode or ''")[0];
                AssertTest($"P2.Scalar_{p}_Err: Returns INVALID_GAME_PHASE for phase {p}", errCode == "INVALID_GAME_PHASE");
            }
        }

        // 2.3: VerifyPhase({3, 5}) (Multi-phase array: gameplay + stats)
        foreach (int p in allPhases)
        {
            SetEnginePhase(p);
            lua.DoString("res = Nexus.VerifyPhase({3, 5})");
            bool success = (bool)lua.DoString("return res.success")[0];

            if (p == 3 || p == 5)
            {
                AssertTest($"P2.Multi_{p}: VerifyPhase({{3, 5}}) allows permitted phase {p}", success);
            }
            else
            {
                AssertTest($"P2.Multi_{p}: VerifyPhase({{3, 5}}) strictly blocks forbidden phase {p}", !success);
                string errCode = (string)lua.DoString("return res.errorCode or ''")[0];
                AssertTest($"P2.Multi_{p}_Err: Returns INVALID_GAME_PHASE for phase {p}", errCode == "INVALID_GAME_PHASE");
            }
        }

        // 2.4: VerifyPhase(nil) -> Default to active match 3
        SetEnginePhase(3);
        bool def3 = (bool)lua.DoString("return Nexus.VerifyPhase().success")[0];
        AssertTest("P2.Default_3: VerifyPhase() with nil param defaults to allowing phase 3", def3);

        SetEnginePhase(0);
        bool def0 = (bool)lua.DoString("return Nexus.VerifyPhase().success")[0];
        AssertTest("P2.Default_0: VerifyPhase() with nil param strictly blocks phase 0", !def0);

        // 2.5: VerifyPhase({}) -> Empty array allows any phase
        bool emptyAllowed = true;
        foreach (int p in allPhases)
        {
            SetEnginePhase(p);
            bool ok = (bool)lua.DoString("return Nexus.VerifyPhase({}).success")[0];
            if (!ok) { emptyAllowed = false; break; }
        }
        AssertTest("P2.EmptyTable: VerifyPhase({}) treats empty array as permissive wildcard across all phases", emptyAllowed);

        // ---------------------------------------------------------------------
        // Suite 3: Nexus.ApplySnapshot Sandboxing & Zero Command Dispatch Guard
        // ---------------------------------------------------------------------
        Console.WriteLine("\n--- Nexus.ApplySnapshot Short-Circuit & Command Dispatch Verification ---");

        string testSnapshotLua = @"{
            gold = 3500,
            level = 15,
            pos = { x = 1200.5, y = 4500.0, z = 128.0 },
            items = { 'FrostfieldPlate', 'Item_PostHaste' }
        }";

        // 3.1: Short-circuiting outside phase 3 (-1, 0, 1, 2, 4, 5, 6)
        int[] nonMatchPhases = [-1, 0, 1, 2, 4, 5, 6];
        foreach (int p in nonMatchPhases)
        {
            SetEnginePhase(p);
            dispatchedCommands.Clear();
            echoedMessages.Clear();

            lua.DoString($"res = Nexus.ApplySnapshot({testSnapshotLua})");
            bool success = (bool)lua.DoString("return res.success")[0];
            string errCode = (string)lua.DoString("return res.errorCode or ''")[0];

            AssertTest($"S3.Phase_{p}_Blocked: ApplySnapshot in phase {p} returns success == false", !success);
            AssertTest($"S3.Phase_{p}_ErrorCode: ApplySnapshot in phase {p} returns errorCode == INVALID_GAME_PHASE", errCode == "INVALID_GAME_PHASE", $"Got '{errCode}'");
            AssertTest($"S3.Phase_{p}_ZeroCmd: ApplySnapshot in phase {p} dispatched ZERO console commands", dispatchedCommands.Count == 0, $"Commands dispatched: {dispatchedCommands.Count}");
        }

        // 3.2: Execution in Phase 3 (Active gameplay)
        SetEnginePhase(3);
        dispatchedCommands.Clear();
        echoedMessages.Clear();

        lua.DoString($"res = Nexus.ApplySnapshot({testSnapshotLua})");
        bool s3Success = (bool)lua.DoString("return res.success")[0];
        AssertTest("S3.Phase_3_Success: ApplySnapshot in phase 3 returns success == true", s3Success);
        AssertTest("S3.Phase_3_DispatchedCount: Dispatched exactly 5 mutation commands", dispatchedCommands.Count == 5, $"Dispatched: {dispatchedCommands.Count}");

        // Validate individual dispatched commands
        AssertTest("S3.Cmd_Gold: Dispatched 'GiveGold 3500'", dispatchedCommands.Contains("GiveGold 3500"));
        AssertTest("S3.Cmd_Level: Dispatched 'SetHeroLevel 15'", dispatchedCommands.Contains("SetHeroLevel 15"));
        AssertTest("S3.Cmd_Teleport: Dispatched Teleport command with coordinates", dispatchedCommands.Any(c => c.StartsWith("Teleport 1200.5")));
        AssertTest("S3.Cmd_Item_AutoPrefix: Dispatched 'PurchaseItem Item_FrostfieldPlate' (normalized prefix)", dispatchedCommands.Contains("PurchaseItem Item_FrostfieldPlate"));
        AssertTest("S3.Cmd_Item_PreservePrefix: Dispatched 'PurchaseItem Item_PostHaste' (preserved prefix)", dispatchedCommands.Contains("PurchaseItem Item_PostHaste"));

        // Validate SNAPSHOT_APPLIED event emission
        bool eventEmitted = echoedMessages.Any(m => m.Contains("[NEXUS_EVENT] SNAPSHOT_APPLIED|"));
        AssertTest("S3.Event_Emitted: Emitted [NEXUS_EVENT] SNAPSHOT_APPLIED", eventEmitted);

        // 3.3: Invalid Snapshot Payloads in Phase 3
        Console.WriteLine("\n--- Snapshot Error Handling & Malformed Payloads ---");

        // nil payload
        dispatchedCommands.Clear();
        lua.DoString("res = Nexus.ApplySnapshot(nil)");
        bool nilOk = (bool)lua.DoString("return res.success")[0];
        string nilErr = (string)lua.DoString("return res.errorCode or ''")[0];
        AssertTest("S3.Payload_Nil: ApplySnapshot(nil) returns success == false", !nilOk);
        AssertTest("S3.Payload_Nil_Err: Returns INVALID_SNAPSHOT", nilErr == "INVALID_SNAPSHOT");
        AssertTest("S3.Payload_Nil_ZeroCmd: 0 commands dispatched on nil payload", dispatchedCommands.Count == 0);

        // string payload instead of table
        dispatchedCommands.Clear();
        lua.DoString("res = Nexus.ApplySnapshot('corrupted_string')");
        bool strOk = (bool)lua.DoString("return res.success")[0];
        string strErr = (string)lua.DoString("return res.errorCode or ''")[0];
        AssertTest("S3.Payload_String: ApplySnapshot('corrupted') returns success == false", !strOk);
        AssertTest("S3.Payload_String_Err: Returns INVALID_SNAPSHOT", strErr == "INVALID_SNAPSHOT");
        AssertTest("S3.Payload_String_ZeroCmd: 0 commands dispatched on string payload", dispatchedCommands.Count == 0);

        // empty table payload
        dispatchedCommands.Clear();
        lua.DoString("res = Nexus.ApplySnapshot({})");
        bool emptyOk = (bool)lua.DoString("return res.success")[0];
        AssertTest("S3.Payload_EmptyTable: ApplySnapshot({}) returns success == true safely", emptyOk);
        AssertTest("S3.Payload_EmptyTable_ZeroCmd: 0 commands dispatched on empty table", dispatchedCommands.Count == 0);

        // ---------------------------------------------------------------------
        // Suite 4: High-Iteration Rapid Fuzz & Stress Testing (1,000 Iterations)
        // ---------------------------------------------------------------------
        Console.WriteLine("\n--- 1,000 Iteration Lifecycle Phase Toggling Stress Test ---");
        bool rapidStressPassed = true;
        dispatchedCommands.Clear();

        for (int i = 0; i < 1000; i++)
        {
            int testPhase = (i % 8) - 1; // Cycles -1, 0, 1, 2, 3, 4, 5, 6
            SetEnginePhase(testPhase);

            lua.DoString("res = Nexus.VerifyPhase({3})");
            bool isSuccess = (bool)lua.DoString("return res.success")[0];

            if (testPhase == 3 && !isSuccess)
            {
                rapidStressPassed = false;
                break;
            }
            if (testPhase != 3 && isSuccess)
            {
                rapidStressPassed = false;
                break;
            }

            // Outside phase 3, ApplySnapshot must never dispatch commands
            if (testPhase != 3)
            {
                int beforeCmdCount = dispatchedCommands.Count;
                lua.DoString($"res = Nexus.ApplySnapshot({testSnapshotLua})");
                if (dispatchedCommands.Count != beforeCmdCount)
                {
                    rapidStressPassed = false;
                    break;
                }
            }
        }
        AssertTest("S4.Rapid_1000_Iterations: 1,000 rapid lifecycle phase transitions verified with zero leaks", rapidStressPassed);

        // ---------------------------------------------------------------------
        // Suite 5: Additional Functional Verification (HideLogin, PracticeMatch)
        // ---------------------------------------------------------------------
        Console.WriteLine("\n--- Functional Verification (HideLogin & PracticeMatch) ---");
        echoedMessages.Clear();
        lua.DoString("loginRes = Nexus.HideLogin('EmpiricalVerifier')");
        bool loginOk = (bool)lua.DoString("return loginRes.success")[0];
        string loginPlayer = (string)lua.DoString("return loginRes.player")[0];
        AssertTest("F5.HideLogin_Success: HideLogin returns success == true", loginOk);
        AssertTest("F5.HideLogin_Player: HideLogin sets correct player name", loginPlayer == "EmpiricalVerifier");
        AssertTest("F5.HideLogin_Event: Emitted [NEXUS_EVENT] LOGIN_SUCCESS", echoedMessages.Any(m => m.Contains("LOGIN_SUCCESS")));

        dispatchedCommands.Clear();
        echoedMessages.Clear();
        lua.DoString("matchRes = Nexus.PracticeMatch({ hero = 'Hero_Pyromancer', map = 'caldavar' })");
        bool matchOk = (bool)lua.DoString("return matchRes.success")[0];
        AssertTest("F5.PracticeMatch_Success: PracticeMatch returns success == true", matchOk);
        AssertTest("F5.PracticeMatch_DevCmd: Dev cvar command dispatched", dispatchedCommands.Contains("Set dev true"));
        AssertTest("F5.PracticeMatch_StartCmd: StartGame practice command dispatched", dispatchedCommands.Any(c => c.Contains("StartGame practice")));
        AssertTest("F5.PracticeMatch_Event: Emitted [NEXUS_EVENT] PRACTICE_MATCH_START", echoedMessages.Any(m => m.Contains("PRACTICE_MATCH_START")));

        Console.WriteLine($"\nNexus Bridge Lifecycle Summary: {passed} passed, {failed} failed.");
        return failed == 0;
    }
}

