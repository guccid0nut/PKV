// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;

namespace Kongor.Modding.Repair;

/// <summary>
///     Bridges legacy HoN 2.x/3.x Lua APIs and global symbols to the modern ProjectKongor v4.14+ client runtime.
///     Injects error boundaries (pcall) and global namespace fallbacks.
/// </summary>
public static class LuaApiCompatibilityShim
{
    public const string ShimFileName = "kongor_compat_shim.lua";

    public const string StandardShimHeader = @"-- =========================================================
-- [ProjectKongor HoN 3.x/4.x Lua Compatibility Shim]
-- =========================================================
local _G = getfenv(0)

-- Global UI and Client namespace safety fallbacks
_G.mainUI = _G.mainUI or {}
_G.Player_Stats_V2 = _G.Player_Stats_V2 or {}
_G.PlayerStats = _G.PlayerStats or _G.Player_Stats_V2

-- Expose GetHeroIconPath fallback
if not _G.PlayerStats.GetHeroIconPath and _G.Player_Stats_V2 then
    _G.PlayerStats.GetHeroIconPath = function(hero)
        return ""/ui/icons/heroes/"" .. tostring(hero) .. "".tga""
    end
end

-- Client accessors
if not _G.GetLocalClient and _G.client and _G.client.GetLocalClient then
    _G.GetLocalClient = _G.client.GetLocalClient
end
if not _G.GetLocalPlayer and _G.client and _G.client.GetLocalPlayer then
    _G.GetLocalPlayer = _G.client.GetLocalPlayer
end

-- ChatBox fallback
if not _G.ChatBox then
    _G.ChatBox = {
        AddMessage = function(self, msg)
            if _G.Chat and _G.Chat.AddMessage then
                _G.Chat.AddMessage(msg)
            elseif _G.Echo then
                _G.Echo(tostring(msg))
            end
        end,
        Clear = function(self)
            if _G.Chat and _G.Chat.Clear then _G.Chat.Clear() end
        end
    }
end

-- UIManager fallback
if not _G.UIManager then
    _G.UIManager = {
        GetInterface = function(self, name)
            if _G.GetInterface then return _G.GetInterface(name) end
            return nil
        end
    }
end

-- Safe Hook Wrapper (Zero-Crash Error Boundary)
if not _G.KongorSafeHook then
    function _G.KongorSafeHook(eventName, callback)
        return function(...)
            local ok, err = pcall(callback, ...)
            if not ok then
                if _G.Echo then
                    _G.Echo(""^r[MOD_HOOK_ERROR] in "" .. tostring(eventName) .. "": "" .. tostring(err))
                end
            end
        end
    end
end

-- Legacy K2 script event fallbacks
if not _G.CallEvent then
    _G.CallEvent = function(evt, ...)
        if _G.Trigger then _G.Trigger(evt, ...) end
    end
end
if not _G.Cmd then
    _G.Cmd = function(cmd)
        if _G.client and _G.client.Exec then _G.client.Exec(cmd) end
    end
end
-- [End ProjectKongor Compatibility Shim]
";

    private static readonly (string oldCall, string modernCall)[] SymbolMappings =
    [
        ("GetLocalClient()", "client.GetLocalClient()"),
        ("client.GetLocalClient():GetPlayer()", "client.GetLocalPlayer()"),
        ("UIManager:GetInterface(", "GetInterface("),
        ("_G.mainUI.ChatBox", "ChatBox")
    ];

    /// <summary>
    ///     Checks whether the Lua content needs the compatibility shim and applies necessary modifications.
    /// </summary>
    public static string ApplyCompatibilityBridge(string luaContent, out int shimsApplied)
    {
        shimsApplied = 0;
        if (string.IsNullOrWhiteSpace(luaContent)) return luaContent;

        string content = luaContent;

        // Check if script uses legacy functions
        bool needsHeader = content.Contains("GetLocalClient", StringComparison.Ordinal) ||
                           content.Contains("GetLocalPlayer", StringComparison.Ordinal) ||
                           content.Contains("ChatBox", StringComparison.Ordinal) ||
                           content.Contains("UIManager", StringComparison.Ordinal) ||
                           content.Contains("PlayerStats", StringComparison.Ordinal) ||
                           content.Contains("CallEvent", StringComparison.Ordinal);

        if (needsHeader && !content.Contains("ProjectKongor HoN", StringComparison.Ordinal))
        {
            StringBuilder sb = new();
            sb.AppendLine(StandardShimHeader);
            sb.AppendLine();
            sb.Append(content);
            content = sb.ToString();
            shimsApplied++;
        }

        foreach ((string oldCall, string modernCall) in SymbolMappings)
            if (content.Contains(oldCall, StringComparison.Ordinal))
            {
                content = content.Replace(oldCall, modernCall, StringComparison.Ordinal);
                shimsApplied++;
            }

        return content;
    }

    /// <summary>
    ///     Gets the standalone shim file content as UTF-8 bytes.
    /// </summary>
    public static byte[] GetShimFileBytes()
    {
        return Encoding.UTF8.GetBytes(StandardShimHeader);
    }

    /// <summary>
    ///     Gets the standalone shim file text.
    /// </summary>
    public static string GetShimFileContent()
    {
        return StandardShimHeader;
    }
}
