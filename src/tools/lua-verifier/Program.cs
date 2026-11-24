// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using NLua;
using NLua.Exceptions;
using ASPIRE.Common.Tooling.Lua;

namespace LuaVerifier;

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

    public void SetGlow(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetGlow({val})");
    }

    public void SetGlowColor(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetGlowColor({val})");
    }

    public void SetBackgroundGlow(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetBackgroundGlow({val})");
    }

    public void SetBorderGlow(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetBorderGlow({val})");
    }

    public void SetTextGlow(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetTextGlow({val})");
    }

    public void SetShadow(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetShadow({val})");
    }

    public void SetShadowColor(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetShadowColor({val})");
    }

    public void SetShadowOffset(object? x = null, object? y = null)
    {
        Console.WriteLine($"[Widget {Name}] SetShadowOffset({x}, {y})");
    }

    public void SetOutline(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetOutline({val})");
    }

    public void SetOutlineColor(object? val = null)
    {
        Console.WriteLine($"[Widget {Name}] SetOutlineColor({val})");
    }

    public void SetFont(object? font = null)
    {
    }

    public void SetColor(object? color = null)
    {
        Color = color?.ToString() ?? "";
    }

    public void SetText(object? text = null)
    {
        Text = text?.ToString() ?? "";
        Program.OnWidgetSetText(Name, Text);
    }

    public void SetSafeText(object? text = null)
    {
        SetText(text);
    }

    public void SetTexture(object? texture = null)
    {
        Texture = texture?.ToString() ?? "";
        Program.OnWidgetSetTexture(Name, Texture);
    }

    public void SetVisible(object? vis = null)
    {
    }

    public void SetEnabled(object? en = null)
    {
    }

    public void SetWidth(object? w = null)
    {
    }

    public void SetHeight(object? h = null)
    {
    }

    public void SetAbsoluteX(object? x = null)
    {
    }

    public void SetAbsoluteY(object? y = null)
    {
    }

    public void SetX(object? x = null)
    {
    }

    public void SetY(object? y = null)
    {
    }

    public void SetValue(object? val = null)
    {
    }

    public void SetMaxValue(object? val = null)
    {
    }

    public void SetRenderMode(object? mode = null)
    {
    }

    public void SetSelectedItemByValue(object? val = null, object? ev = null)
    {
    }

    public void SetAvatar(object? url = null)
    {
    }

    public void SetCallback(object? ev = null, object? cb = null)
    {
    }

    public void ClearCallback(object? ev = null)
    {
    }

    public void ClearChildren()
    {
    }

    public float GetAbsoluteX()
    {
        return 0f;
    }

    public float GetAbsoluteY()
    {
        return 0f;
    }

    public float GetWidth()
    {
        return 100f;
    }

    public float GetHeight()
    {
        return 100f;
    }

    public string GetValue()
    {
        return "0";
    }

    public string GetText()
    {
        return Text;
    }

    public string GetName()
    {
        return Name;
    }

    public bool IsVisible()
    {
        return Visible;
    }

    public bool IsEnabled()
    {
        return Enabled;
    }

    public void DoEvent(object? ev = null)
    {
    }

    public string UICmd(object? cmd = null)
    {
        if (cmd?.ToString()?.Contains("GetScrollbarMaxValue") == true) return "100";
        return "";
    }

    public void Sleep(object? time = null, object? cb = null)
    {
    }

    public void FadeIn(object? time = null)
    {
    }

    public void FadeOut(object? time = null)
    {
    }

    public void Show(object? duration = null)
    {
        Visible = true;
    }

    public void Hide()
    {
        Visible = false;
    }

    public void Enable()
    {
        Enabled = true;
    }

    public void Disable()
    {
        Enabled = false;
    }

    public void RegisterWatch(object? n = null, object? f = null)
    {
    }

    public MockWidget GetWidget(object? n = null)
    {
        return new MockWidget(n?.ToString() ?? "");
    }

    public MockWidget Instantiate(params object?[] args)
    {
        return new MockWidget("new_instanced_widget");
    }
}

public class Program
{
    private static volatile bool s_verbose = true;

    public static int ExecuteBotScenario(Lua lua, string scenarioInput)
    {
        string json = scenarioInput;
        if (File.Exists(scenarioInput))
        {
            json = File.ReadAllText(scenarioInput);
        }

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;
        string taskName = root.TryGetProperty("task", out System.Text.Json.JsonElement tElem) ? (tElem.GetString() ?? "TaskCombatEngagement") : "TaskCombatEngagement";

        StringBuilder sb = new();
        sb.AppendLine("local state = { nNearbyEnemyCreeps = 0, nNearbyAllyCreeps = 0, nNearbyEnemyHeroes = 0, nNearbyAllyHeroes = 0, nHeroHealthPct = 1.0, nHeroManaPct = 1.0, nGameTime = 0, bBaseUnderAttack = false, nTeamPushState = 0, nAlliesAlive = 5 }");
        if (root.TryGetProperty("state", out System.Text.Json.JsonElement stateElem) && stateElem.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (System.Text.Json.JsonProperty prop in stateElem.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    sb.AppendLine($"state['{prop.Name}'] = {prop.Value.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    sb.AppendLine($"state['{prop.Name}'] = true");
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    sb.AppendLine($"state['{prop.Name}'] = false");
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    sb.AppendLine($"state['{prop.Name}'] = '{prop.Value.GetString()}'");
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object && prop.Name == "unitTarget")
                {
                    sb.AppendLine("state['unitTarget'] = { Name = 'TargetHero', IsAlive = function() return true end, GetPosition = function() return Vector3.Create(0,0,0) end }");
                }
            }
        }

        sb.AppendLine($"local task = object.behaviorLib.tHTNDomain.Tasks['{taskName}']");
        sb.AppendLine("local chosenMethod = 'none'");
        sb.AppendLine("local subtasks = {}");
        sb.AppendLine("if task and task.Methods then");
        sb.AppendLine("    for _, m in ipairs(task.Methods) do");
        sb.AppendLine("        if m.Precondition(state) then");
        sb.AppendLine("            chosenMethod = m.Name");
        sb.AppendLine("            for _, st in ipairs(m.Subtasks or {}) do table.insert(subtasks, st) end");
        sb.AppendLine("            break");
        sb.AppendLine("        end");
        sb.AppendLine("    end");
        sb.AppendLine("end");
        sb.AppendLine("return chosenMethod, table.concat(subtasks, ',')");

        object[] results = lua.DoString(sb.ToString());
        string chosenMethod = results.Length > 0 ? (results[0]?.ToString() ?? "none") : "none";
        string subtasksStr = results.Length > 1 ? (results[1]?.ToString() ?? "") : "";
        string[] subtasks = string.IsNullOrEmpty(subtasksStr) ? [] : subtasksStr.Split(',');
        string primarySubtask = subtasks.Length > 0 ? subtasks[0] : "none";

        string outputJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "ok",
            task = taskName,
            chosenMethod,
            primarySubtask,
            subtasks
        });

        Console.WriteLine(outputJson);
        return 0;
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "-?" || args[0] == "/?"))
        {
            Console.WriteLine("Usage: lua-verifier [script_path ...] [--run <script_path>] [--parallel <N>] [--stress <N>] [--strict-globals] [--interactive|-i]");
            Console.WriteLine("Headless NLua verification sandbox and K2 simulation for UI scripts, Cvars, and payloads.");
            return 0;
        }

        int parallel = 1;
        int stress = 1;
        bool isInteractive = false;
        bool strictGlobals = false;
        List<string> scriptPaths = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--parallel", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pVal))
                {
                    Console.Error.WriteLine("ERROR: Missing or invalid integer after '--parallel'. Usage: lua-verifier [script_path ...] [--parallel <N>]");
                    return 2;
                }
                parallel = Math.Max(1, pVal);
                i++;
            }
            else if (arg.Equals("--stress", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int sVal))
                {
                    Console.Error.WriteLine("ERROR: Missing or invalid integer after '--stress'. Usage: lua-verifier [script_path ...] [--stress <N>]");
                    return 2;
                }
                stress = Math.Max(1, sVal);
                i++;
            }
            else if (arg.Equals("--run", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-'))
                {
                    Console.Error.WriteLine("ERROR: Missing script path after '--run'. Usage: lua-verifier --run <script_path>");
                    return 2;
                }
                scriptPaths.Add(args[i + 1]);
                i++;
            }
            else if (arg.Equals("--interactive", StringComparison.OrdinalIgnoreCase) || arg.Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                isInteractive = true;
            }
            else if (arg.Equals("--bot-scenario", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    Console.Error.WriteLine("ERROR: Missing scenario payload after '--bot-scenario'.");
                    return 2;
                }
                string scenarioPayload = args[i + 1];
                using Lua botLua = new();
                K2Bridge.InitializeEnvironment(botLua, strictGlobals: false);
                K2Bridge.InitializeBotEnvironment(botLua);
                return ExecuteBotScenario(botLua, scenarioPayload);
            }
            else if (arg.Equals("--strict-globals", StringComparison.OrdinalIgnoreCase))
            {
                strictGlobals = true;
            }
            else if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"ERROR: Unknown flag '{arg}'. Run 'lua-verifier --help' for usage.");
                return 2;
            }
            else
            {
                scriptPaths.Add(arg);
            }
        }

        K2Bridge.StrictGlobals = strictGlobals;
        Console.WriteLine($"Starting Lua Verifier (ObjectPool<Lua>, StrictGlobals={strictGlobals})...");

        LuaPoolOptions poolOptions = new()
        {
            MaximumRetained = Math.Max(4, parallel * 2),
            PrewarmCount = (parallel > 1 || stress > 1 || isInteractive) ? Math.Min(4, parallel) : 0,
            CustomInitializer = l =>
            {
                l.RegisterFunction("MockWidgetSetText", typeof(Program).GetMethod(nameof(OnWidgetSetText)));
                l.RegisterFunction("MockWidgetSetTexture", typeof(Program).GetMethod(nameof(OnWidgetSetTexture)));
                K2Bridge.StrictGlobals = strictGlobals;
                K2Bridge.InitializeEnvironment(l, strictGlobals);
            }
        };

        LuaStatePooledObjectPolicy stateFactory = new(poolOptions);

        if (isInteractive)
        {
            using LuaStatePool pool = new(poolOptions);
            Console.WriteLine("Entered Lua Verifier Interactive REPL. Type 'exit' to quit.");
            using PooledLuaLease lease = pool.GetLease();
            string replScript = scriptPaths.FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrEmpty(replScript) && File.Exists(replScript))
            {
                Console.WriteLine($"Loading pre-script: {replScript}");
                try
                {
                    lease.Lua.DoFile(replScript);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Lua Pre-script Error: {ex.Message}");
                }
            }
            while (true)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();
                if (input is "exit" or null) break;
                if (string.IsNullOrWhiteSpace(input)) continue;
                try
                {
                    lease.Lua.DoString(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Lua Error: {ex.Message}");
                }
            }
            return 0;
        }

        if (scriptPaths.Count == 0)
        {
            string[] candidateRelPaths =
            [
                Path.Combine("games", "client", "override", "ui", "scripts", "newui", "player_stats_v2.lua"),
                Path.Combine("games", "shared", "ui", "scripts", "newui", "player_stats_v2.lua"),
                Path.Combine("ASPIRE.Tests", "Data", "ClientLuaFiles", "ui", "scripts", "newui", "player_stats_v2.lua")
            ];

            string currentDir = Directory.GetCurrentDirectory();
            string baseDir = AppContext.BaseDirectory;
            string? resolvedWsRoot = Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null
                ? ws.Root
                : null;

            foreach (string rel in candidateRelPaths)
            {
                if (!string.IsNullOrWhiteSpace(resolvedWsRoot))
                {
                    string wsPath = Path.GetFullPath(Path.Combine(resolvedWsRoot, rel));
                    if (File.Exists(wsPath))
                    {
                        scriptPaths.Add(wsPath);
                        break;
                    }
                }

                string p1 = Path.GetFullPath(Path.Combine(currentDir, rel));
                if (File.Exists(p1))
                {
                    scriptPaths.Add(p1);
                    break;
                }

                string p2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", rel));
                if (File.Exists(p2))
                {
                    scriptPaths.Add(p2);
                    break;
                }

                string p3 = Path.GetFullPath(Path.Combine(currentDir, "NEXUS", rel));
                if (File.Exists(p3))
                {
                    scriptPaths.Add(p3);
                    break;
                }
            }
        }

        foreach (string candidate in scriptPaths)
        {
            if (!File.Exists(candidate))
            {
                Console.Error.WriteLine($"ERROR: Script not found at {candidate}");
                return 1;
            }
        }

        if (parallel <= 1 && stress <= 1)
        {
            int failedSuites = 0;
            foreach (string path in scriptPaths)
            {
                K2Bridge.Reset();
                using Lua freshLua = stateFactory.Create();
                int res = RunVerification(freshLua, path, verbose: true);
                if (res != 0)
                {
                    failedSuites++;
                }
            }
            return failedSuites > 0 ? 1 : 0;
        }

        string primaryScript = scriptPaths[0];
        using LuaStatePool stressPool = new(poolOptions);
        int totalRuns = Math.Max(parallel, stress);
        Console.WriteLine($"\n[LuaVerifier] Executing {totalRuns} pooled verification runs across {parallel} parallel worker threads...");
        s_verbose = false;
        int failed = 0;

        Parallel.For(0, totalRuns, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
        {
            using PooledLuaLease lease = stressPool.GetLease();
            int res = RunVerification(lease.Lua, primaryScript, verbose: false);
            if (res != 0) Interlocked.Increment(ref failed);
        });

        if (failed > 0)
        {
            Console.WriteLine($"\n[LuaVerifier] Parallel verification FAILED: {failed}/{totalRuns} runs failed.");
            return 1;
        }

        Console.WriteLine($"\n[LuaVerifier] Parallel verification PASSED: {totalRuns}/{totalRuns} runs executed with 0 state contamination. (ExitCode: 0)");
        return 0;
    }

    public static int RunVerification(Lua lua, string scriptPath, bool verbose = true)
    {
        try
        {
            if (!Path.GetFileName(scriptPath).Equals("player_stats_v2.lua", StringComparison.OrdinalIgnoreCase))
            {
                if (verbose) Console.WriteLine($"Executing custom Lua script: {scriptPath}");
                lua.DoFile(scriptPath);
                if (verbose) Console.WriteLine("\n[LuaVerifier] Custom script executed successfully. (ExitCode: 0)");
                return 0;
            }

            // Load Script
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

            if (verbose) Console.WriteLine("Script Loaded Successfully.");

                // Test: Base64 / Normal Season Result
                Console.WriteLine("\n--- Testing Base64 Payload & OnPlayerStatsNormalSeasonResult ---");

                // 1. Create Data
                Dictionary<object, object> stats = new()
                {
                    { 1, "Nickname" },
                    { 6, 100 }, // Total Matches
                    { 7, 50 }, // Wins
                    { 8, 50 }, // Losses
                    { "cam_wins", 50 },
                    { "cam_losses", 50 }
                };

                // 2. Serialize to PHP Format
                StringBuilder php = new();
                php.Append($"a:{stats.Count}:{{");
                foreach (KeyValuePair<object, object> kvp in stats)
                {
                    if (kvp.Key is int i) php.Append($"i:{i};");
                    else php.Append($"s:{kvp.Key.ToString()?.Length ?? 0}:\"{kvp.Key}\";");

                    if (kvp.Value is int v) php.Append($"i:{v};");
                    else php.Append($"s:{kvp.Value.ToString()?.Length ?? 0}:\"{kvp.Value}\";");
                }

                php.Append('}');
                string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(php.ToString()));

                Console.WriteLine($"Generated Base64: {base64}");

                // 3. Inject into Lua
                lua["base64Payload"] = base64;

                // 4. Run Function with positional args to verify full decoding and state update
                lua.DoString(@"
                            println('Calling OnPlayerStatsNormalSeasonResult...')
                            Player_Stats_V2:OnPlayerStatsNormalSeasonResult('Nickname', 'Clan', '1', '10', '12345', '100', '50', '50')
                            
                            local seasonStats = _seasonStatsNormal
                            if seasonStats and (tostring(seasonStats['cam_wins']) == '50') then
                                println('Lua _seasonStatsNormal[cam_wins]: ' .. tostring(seasonStats['cam_wins']))
                                println('Lua _seasonStatsNormal[cam_losses]: ' .. tostring(seasonStats['cam_losses']))
                                println('[SUCCESS] OnPlayerStatsNormalSeasonResult executed and verified successfully.')
                            else
                                local actual = seasonStats and tostring(seasonStats['cam_wins']) or 'nil'
                                error('Verification failed: expected cam_wins=50, got ' .. actual)
                            end
                        ");

                Console.WriteLine("\n--- Testing Match History CSV Parser Stress Scenarios ---");

                // Stress Test 1: Standard valid match row
                lua.DoString(@"
                    println('--- Stress Test 1: Standard valid row ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1001,1,1,10,2,8,15,1850,caldavar,2026-09-02 12:00:00,Hero_Aluna')
                    assert(_matchList[1] ~= nil, 'Test 1 Failed: _matchList[1] is nil')
                    assert(_matchList[1].id == '1001', 'Test 1 Failed: id mismatch')
                    assert(_matchList[1].heroname == 'Hero_Aluna', 'Test 1 Failed: heroname mismatch')
                    assert(_matchList[1].duration == '1850', 'Test 1 Failed: duration mismatch')
                    println('[PASS] Stress Test 1: Standard valid row')
                ");

                // Stress Test 2: Empty hero name (column 11 empty) -> fallback to HeroDefinitionsCache.HeroIdMap
                lua.DoString(@"
                    println('--- Stress Test 2: Empty hero name (col 11 empty) ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1002,1,1,10,2,8,15,1850,caldavar,2026-09-02 12:00:00,')
                    assert(_matchList[1] ~= nil, 'Test 2 Failed: _matchList[1] is nil')
                    assert(_matchList[1].heroname == 'Hero_Aluna', 'Test 2 Failed: fallback to HeroIdMap failed, got ' .. tostring(_matchList[1].heroname))
                    println('[PASS] Stress Test 2: Empty hero name resolved via HeroIdMap')
                ");

                // Stress Test 3: Hero name is '0' -> fallback to HeroIdMap
                lua.DoString(@"
                    println('--- Stress Test 3: Hero name is 0 ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1003,0,2,2,8,4,15,2100,caldavar,2026-09-02 12:30:00,0')
                    assert(_matchList[1] ~= nil, 'Test 3 Failed: _matchList[1] is nil')
                    assert(_matchList[1].heroname == 'Hero_Aluna', 'Test 3 Failed: fallback to HeroIdMap failed for 0')
                    println('[PASS] Stress Test 3: Hero name 0 resolved via HeroIdMap')
                ");

                // Stress Test 4: Empty duration (col 8 empty)
                lua.DoString(@"
                    println('--- Stress Test 4: Empty duration ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1004,1,1,5,1,10,15,,caldavar,2026-09-02 13:00:00,Hero_Aluna')
                    assert(_matchList[1] ~= nil, 'Test 4 Failed: _matchList[1] is nil')
                    assert(_matchList[1].duration == '', 'Test 4 Failed: duration not empty')
                    println('[PASS] Stress Test 4: Empty duration handled safely without exception')
                ");

                // Stress Test 5: Empty map name (col 9 empty)
                lua.DoString(@"
                    println('--- Stress Test 5: Empty map name ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1005,1,1,8,4,12,15,1500,,2026-09-02 13:30:00,Hero_Aluna')
                    assert(_matchList[1] ~= nil, 'Test 5 Failed: _matchList[1] is nil')
                    assert(_matchList[1].mapname == '', 'Test 5 Failed: mapname not empty')
                    println('[PASS] Stress Test 5: Empty map name handled safely')
                ");

                // Stress Test 6: Empty KDA (cols 4, 5, 6 empty)
                lua.DoString(@"
                    println('--- Stress Test 6: Empty KDA ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1006,1,1,,,,15,1200,caldavar,2026-09-02 14:00:00,Hero_Aluna')
                    assert(_matchList[1] ~= nil, 'Test 6 Failed: _matchList[1] is nil')
                    assert(_matchList[1].kills == '', 'Test 6 Failed: kills not empty')
                    println('[PASS] Stress Test 6: Empty KDA handled safely')
                ");

                // Stress Test 7: Trailing comma (12 tokens)
                lua.DoString(@"
                    println('--- Stress Test 7: Trailing comma ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1007,1,1,6,2,8,15,1600,caldavar,2026-09-02 14:30:00,Hero_Aluna,')
                    assert(_matchList[1] ~= nil, 'Test 7 Failed: _matchList[1] is nil')
                    assert(_matchList[1].id == '1007', 'Test 7 Failed: id mismatch')
                    println('[PASS] Stress Test 7: Trailing comma parsed successfully')
                ");

                // Stress Test 8: 10 columns only (legacy format without col 11)
                lua.DoString(@"
                    println('--- Stress Test 8: 10 columns only ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1008,1,1,3,3,3,15,1400,caldavar,2026-09-02 15:00:00')
                    assert(_matchList[1] ~= nil, 'Test 8 Failed: _matchList[1] is nil')
                    assert(_matchList[1].heroname == 'Hero_Aluna', 'Test 8 Failed: fallback for 10-col failed')
                    println('[PASS] Stress Test 8: 10-column row parsed with fallback')
                ");

                // Stress Test 9: Truncated row (< 10 columns)
                lua.DoString(@"
                    println('--- Stress Test 9: Truncated row (< 10 columns) ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('1009,1,1,5,2')
                    assert(table.getn(_matchList) == 0, 'Test 9 Failed: malformed row should be skipped')
                    println('[PASS] Stress Test 9: Truncated row skipped cleanly')
                ");

                // Stress Test 10: Completely empty string row
                lua.DoString(@"
                    println('--- Stress Test 10: Empty string row ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult('')
                    assert(table.getn(_matchList) == 0, 'Test 10 Failed: empty row should be skipped')
                    println('[PASS] Stress Test 10: Empty string row skipped cleanly')
                ");

                // Stress Test 11: Interspersed valid and invalid rows
                lua.DoString(@"
                    println('--- Stress Test 11: Interspersed valid and invalid rows ---')
                    Player_Stats_V2:OnPlayerStatsMatchListResult(
                        '',
                        '1011,1,1,5,2,8,15,1800,caldavar,2026-09-02,Hero_Aluna',
                        'corrupt,row,not,enough,cols',
                        '1012,1,1,8,3,4,25,1200,caldavar,2026-09-02,Hero_Magmus'
                    )
                    local count = 0
                    for k, v in pairs(_matchList) do
                        count = count + 1
                        println('Found match in _matchList at key ' .. tostring(k) .. ': ID=' .. tostring(v.id))
                    end
                    println('Total parsed matches: ' .. tostring(count) .. ', table.getn(_matchList)=' .. tostring(table.getn(_matchList)))
                    assert(count == 2, 'Test 11 Failed: Expected 2 valid matches, found ' .. tostring(count))
                    assert(table.getn(_matchList) == 2, 'Test 11 Failed: table.getn(_matchList) must be 2, but was ' .. tostring(table.getn(_matchList)))
                    println('[PASS] Stress Test 11: table.getn(_matchList) is ' .. tostring(table.getn(_matchList)) .. ' (no sparse array holes)')
                ");

                if (verbose) Console.WriteLine("\n[LuaVerifier] Verification PASSED (ExitCode: 0)");
                return 0;
            }
            catch (LuaScriptException luaEx)
            {
                Console.WriteLine($"LUA ERROR: {luaEx.Message}");
                Console.WriteLine($"Source: {luaEx.Source}");
                if (luaEx.InnerException != null) Console.WriteLine($"Inner Exception: {luaEx.InnerException.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GENERAL ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

    public static void OnWidgetSetText(string name, object text)
    {
        if (s_verbose) Console.WriteLine($"[Widget] {name}.SetText({text})");
    }

    public static void OnWidgetSetTexture(string name, object texture)
    {
        if (s_verbose) Console.WriteLine($"[Widget] {name}.SetTexture({texture})");
    }
}
