// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.ObjectPool;
using NLua;

namespace ASPIRE.Common.Tooling.Lua;

/// <summary>
/// Pool policy for creating, sanitizing, and recycling thread-safe isolated Lua states.
/// Guarantees strict sandbox hygiene between script executions.
/// </summary>
public sealed class LuaStatePooledObjectPolicy : IPooledObjectPolicy<NLua.Lua>
{
    private readonly LuaPoolOptions _options;

    public LuaStatePooledObjectPolicy(LuaPoolOptions? options = null)
    {
        _options = options ?? new LuaPoolOptions();
    }

    public NLua.Lua Create()
    {
        NLua.Lua lua = new();
        lua.LoadCLRPackage();

        if (_options.InitializeK2Mocks)
        {
            InitializeK2Mocks(lua);
        }

        _options.CustomInitializer?.Invoke(lua);
        return lua;
    }

    public bool Return(NLua.Lua obj)
    {
        if (obj == null) return false;

        try
        {
            // 1. Health check - verify lua state is alive and responsive
            obj.DoString("local _ = 1");

            // 2. Hygiene & State Sanitization - clean test-polluted global tables
            obj.DoString(@"
                _matchList = {}
                _seasonStatsNormal = {}
                _seasonStatsCasual = {}
                base64Payload = nil
                if Player_Stats_V2 then
                    Player_Stats_V2.mockData = nil
                end
            ");

            if (!string.IsNullOrWhiteSpace(_options.CustomResetScript))
            {
                obj.DoString(_options.CustomResetScript);
            }

            // 3. Force garbage collection to reclaim memory from previous script allocations
            if (_options.AutoCollectGarbageOnReturn)
            {
                obj.DoString("collectgarbage('collect')");
            }

            return true;
        }
        catch
        {
            // State is corrupted or threw unrecoverable error; discard and let pool allocate a fresh instance
            try { obj.Dispose(); } catch { /* ignore */ }
            return false;
        }
    }

    private static void InitializeK2Mocks(NLua.Lua lua)
    {
        lua.DoString(@"
            _G = _G or {}
            function GetWidget(name) return _G.MockGetWidget(name) end
            function Translate(key, ...) return key end
            function TranslateOrEmpty(inputString)
                local str = Translate(inputString)
                if (str == inputString) then return '' else return str end
            end
            function GetHeroMasteryUpgradeInfo(info) return info end
            function GetMasteryRewardsInfo(info) return info end
            function GetHeroProficiency(info) return {} end
            
            Widget = {}
            Widget.__index = Widget
            function Widget:SetTexture(texture) if _G.MockWidgetSetTexture then _G.MockWidgetSetTexture(self.name, texture) end end
            function Widget:SetVisible(vis) self.visible = vis end
            function Widget:SetEnabled(en) self.enabled = en end
            function Widget:SetText(txt) self.text = txt; if _G.MockWidgetSetText then _G.MockWidgetSetText(self.name, txt) end end
            function Widget:SetSafeText(txt) self:SetText(txt) end
            function Widget:SetFont(font) end
            function Widget:SetColor(color) self.color = color end
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
            function Widget:SetWidth(w) self.width = w end
            function Widget:SetHeight(h) self.height = h end
            function Widget:SetAbsoluteX(x) end
            function Widget:SetAbsoluteY(y) end
            function Widget:SetX(x) self.x = x end
            function Widget:SetY(y) self.y = y end
            function Widget:SetValue(v) self.value = v end
            function Widget:SetMaxValue(v) self.maxValue = v end
            function Widget:SetRenderMode(m) end
            function Widget:SetSelectedItemByValue(...) end
            function Widget:SetAvatar(url) end
            function Widget:SetCallback(ev, cb) end
            function Widget:ClearCallback(ev) end
            function Widget:ClearChildren() end
            function Widget:GetAbsoluteX() return 0 end
            function Widget:GetAbsoluteY() return 0 end
            function Widget:GetWidth() return self.width or 100 end
            function Widget:GetHeight() return self.height or 100 end
            function Widget:GetValue() return self.value or '0' end
            function Widget:GetText() return self.text or '' end
            function Widget:GetName() return self.name or '' end
            function Widget:IsVisible() return self.visible ~= false end
            function Widget:IsEnabled() return self.enabled ~= false end
            function Widget:DoEvent(...) end
            function Widget:UICmd(cmd) if cmd and string.find(tostring(cmd), 'GetScrollbarMaxValue') then return '100' end return '' end
            function Widget:Sleep(...) end
            function Widget:FadeIn(...) end
            function Widget:FadeOut(...) end
            function Widget:Show(...) self.visible = true end
            function Widget:Hide(...) self.visible = false end
            function Widget:Enable(...) self.enabled = true end
            function Widget:Disable(...) self.enabled = false end
            function Widget:RegisterWatch(n, f) end
            function Widget:GetWidget(n) return _G.MockGetWidget(n) end
            function Widget:Instantiate(...) return _G.MockGetWidget('new_instanced_widget') end
            
            function _G.MockGetWidget(name)
                local w = { name = name }
                setmetatable(w, Widget)
                return w
            end

            object = {
                GetName = function(self) return 'mock_object' end,
                RegisterWatch = function(self, ...) end,
                GetWidget = function(self, name) return _G.MockGetWidget(name) end,
                UICmd = function(self, cmd) return '' end
            }
            setmetatable(object, Widget)

            Player_Stats_V2 = Player_Stats_V2 or {}
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
            function println(msg) print(msg) end
            table.getn = function(t) return #t end
            
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

            HeroDefinitionsCache = HeroDefinitionsCache or {
                Heroes = {},
                HeroIdMap = {
                    [15] = { name = 'Hero_Aluna', displayName = 'Aluna' },
                    [25] = { name = 'Hero_Magmus', displayName = 'Magmus' }
                },
                GetHeroDisplayName = function(name) return name end,
                GetHeroIcon = function(name) return '/mock/icon.tga' end,
                ParseMasteryInfo = function(info) return {} end
            }

            Client = Client or {
                GetCookie = function() return 'mock_cookie' end
            }
            
            function NotEmpty(str) return str ~= nil and str ~= '' end
            function Empty(str) return str == nil or str == '' end
            function AtoB(val) return val == 'true' or val == '1' or val == 1 or val == true end
            function AtoN(val) return tonumber(val) or 0 end
            function StripClanTag(name) return name end
        ");
    }
}
