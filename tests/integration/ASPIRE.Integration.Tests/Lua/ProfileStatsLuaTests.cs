// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.Lua;

public class ProfileStatsLuaTests : LuaTestBase
{
    private const string SCRIPT_PATH = "newui/player_stats_v2.lua";

    public ProfileStatsLuaTests()
    {
        // Load the script
        string fullPath = Path.Combine(LuaScriptsRoot, SCRIPT_PATH);
        if (!File.Exists(fullPath)) return;

        string content = File.ReadAllText(fullPath);

        // Standard patching
        content = content.Replace("local _G = getfenv(0)", "local _G = _G");
        content = LuaFunctionVarArgsRegex().Replace(content,
            "function $1(...) local arg = { ... }; arg.n = #arg");

        // Inject Backdoor to set current tab
        content += "\n function Player_Stats_V2:SetCurrentTab(val) _currentTab = val end \n";

        // Inject Backdoor to set bHeroMasteryRetrieved (required for Overview update)
        content += "\n function Player_Stats_V2:SetHeroMasteryRetrieved(val) bHeroMasteryRetrieved = val end \n";

        // Polyfill unpack
        LuaState.DoString("unpack = table.unpack");

        LuaState.DoString(content, "@" + SCRIPT_PATH);
        LuaState.DoString("Player_Stats_V2:Init()");
    }
}
