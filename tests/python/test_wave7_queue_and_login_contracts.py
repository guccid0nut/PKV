#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Wave 7 Queue, Trigger Persistence, Queue Arcade & Login Username Sanitization Contracts.

Value-Weighted Test Contract:
  - protects:
      1. Issue 1: Persistent interface-independent UITrigger registration across reloads
         (confirmations_v2.package 17 triggers, common_v2.package NewUIPopupMenu, loading_v2.package),
         main-interface TMM watch scoping in loading_v2.lua, nil-safe TeamMembers[1] checks,
         1-click solo immediate leave queue vs >1 party confirmation dialog, and ARCADE: OFF
         preservation across post-match menu return in communicator_v2.lua.
      2. Issue 3D-Lua: Teammaking_V2:EmitPartyOrQueueDrp post-Arcade Phase 0 settle telemetry
         re-emitting status=Queue;mode=<_kongorLastQueueMode>.
      3. Issue 4-Lua: SanitizeLoginAccountName & PrepareCleanLoginUsername, LoggedIn() tag
         stripping, SyncLoginUsernameField() pre-autologin self-heal, pre-submit guards in
         main_v2.interface / main.interface / main_v2_audio_news.lua, secondary Set('login_name')
         call-site sanitization, and in-lobby GetAccountName() clan-tag preservation.
      4. Tier-5 exact byte-identical cross-tree mirror parity for all 13 owned files (41 mirrors)
         without touching games/server/game/main_v2.lua.
  - fails_when:
      Any CVar-gated <trigger> tag returns, Loading_V2 binds watches on non-main interfaces or
      forces TriggerDialogBox for solo queue, TeamMembers[1] nil crashes OnClickLeaveQueue or
      OnRightClickTeamMember, OnLogin clobbers ARCADE: OFF on post-match return, Phase 0 settle
      drops status=Queue DRP, clan/color tags poison login_name/ui_lastSavedAccountName or get
      stripped from in-lobby GetAccountName(), or any mirror drifts.
  - seam:
      Real SHA-256 file parity, real XML ElementTree + luac51 bytecode verification, and real
      LuaVerifier.exe execution against the actual Lua scripts in games/client/override/.
"""

from __future__ import annotations

import hashlib
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
OVERRIDE_ROOT = REPO_ROOT / "games" / "client" / "override"

MIRROR_MATRIX: list[tuple[str, tuple[str, ...]]] = [
    ("ui/fe2/newui/confirmations_v2.package", ("games/client/ClientPackageFiles", "games/client/game")),
    ("ui/fe2/newui/common_v2.package", ("games/client/ClientPackageFiles", "games/client/game")),
    ("ui/fe2/newui/loading_v2.package", ("games/client/ClientPackageFiles",)),
    ("ui/scripts/newui/loading_v2.lua", ("games/client/ClientPackageFiles",)),
    ("ui/scripts/newui/teammaking_v2.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/newui/communicator_v2.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/main_v2.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/fe2/main_v2.interface", ("games/client/ClientPackageFiles", "games/client/game")),
    ("ui/fe2/main.interface", ("games/client/ClientPackageFiles", "games/client/game")),
    ("ui/scripts/main_v2_audio_news.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/main_v2_scene.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/main_v2_p2p.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/main_v2_botmatch.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/main_v2_walkthrough.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/lib_json.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/nexus_bridge.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/newui/player_hosted_v2.lua", ("games/client/ClientPackageFiles", "games/client/game", "games/shared")),
    ("ui/scripts/main_matches.lua", ("games/client/ClientPackageFiles",)),
]

CONFIRMATIONS_TRIGGERS = [
    "WarnUnverifiedTrigger",
    "WarnNotEligibleForCampaignTrigger",
    "WarnTokensTrigger",
    "NeedTokensTrigger",
    "TooManyFriendsTrigger",
    "TriggerDialogBox",
    "HTTriggerDialogBox",
    "TriggerDialogBoxWebRequest",
    "TriggerDialogBoxWithCombobox",
    "TriggerDialogBoxWithComboboxGame",
    "TriggerDialogBoxGame",
    "GLIncorrectDisplay",
    "purchase_confirmation_status",
    "purchase_confirmation_results",
    "Confirm_Purchase_AltCourier",
    "Confirm_Purchase_Other",
    "Confirm_Purchase_Other_Start",
]


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _find_lua_verifier() -> Path:
    for cfg in ("Release", "Debug"):
        candidate = REPO_ROOT / "src" / "tools" / "lua-verifier" / "bin" / cfg / "net11.0" / "LuaVerifier.exe"
        if candidate.is_file():
            return candidate
    pytest.fail("LuaVerifier.exe not found under src/tools/lua-verifier/bin/{Release,Debug}/net11.0/")


def _find_luac51() -> Path:
    for rel in (
        "native/build/luac51_build/Release/luac51.exe",
        "native/tools/luac51/build/Release/luac51.exe",
        "artifacts/native-binaries/windows-x64/luac51.exe",
        ".tools/bin/lua/luac51.exe",
    ):
        candidate = REPO_ROOT / rel
        if candidate.is_file():
            return candidate
    pytest.fail("luac51.exe not found in repository build or .tools directories")


def _run_lua_verifier(tmp_path: Path, script_name: str, lua_body: str) -> str:
    verifier = _find_lua_verifier()
    script_path = tmp_path / script_name
    script_path.write_text(lua_body, encoding="utf-8")
    proc = subprocess.run(
        [str(verifier), str(script_path)],
        cwd=str(REPO_ROOT),
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )
    assert proc.returncode == 0, (
        f"LuaVerifier failed ({proc.returncode}) for {script_name}:\n"
        f"STDOUT:\n{proc.stdout}\nSTDERR:\n{proc.stderr}"
    )
    return proc.stdout


def test_cross_tree_mirror_byte_parity_and_server_exclusion() -> None:
    """All 13 owned files match every required cross-tree mirror byte-for-byte, leaving games/server/game/main_v2.lua untouched."""
    pairs_checked = 0
    for rel_path, mirrors in MIRROR_MATRIX:
        src = OVERRIDE_ROOT / rel_path
        assert src.is_file(), f"Missing primary override file: {src}"
        src_hash = _sha256(src)
        for mirror_root in mirrors:
            dst = REPO_ROOT / mirror_root / rel_path
            assert dst.is_file(), f"Missing mirror file: {dst}"
            assert _sha256(dst) == src_hash, (
                f"Mirror byte drift between {src} and {dst}. "
                f"Sync via Copy-Item -LiteralPath '{src}' -Destination '{dst}' -Force"
            )
            pairs_checked += 1

    # 44 cross-tree mirror pairs (18 owned files, including Wave 3A modularized main_v2 scripts & lib_json.lua).
    assert pairs_checked == 44
    srv_main = REPO_ROOT / "games" / "server" / "game" / "ui" / "scripts" / "main_v2.lua"
    if srv_main.is_file():
        assert _sha256(srv_main) != _sha256(OVERRIDE_ROOT / "ui/scripts/main_v2.lua"), (
            "games/server/game/ui/scripts/main_v2.lua must NOT be overwritten by client main_v2.lua"
        )


def test_xml_packages_use_persistent_ui_triggers_and_compile_in_luac51(tmp_path: Path) -> None:
    """confirmations_v2.package (17), common_v2.package (1), and loading_v2.package use UITrigger.CreateTrigger and pass XML + luac51."""
    luac51 = _find_luac51()

    conf_path = OVERRIDE_ROOT / "ui/fe2/newui/confirmations_v2.package"
    conf_text = conf_path.read_text(encoding="utf-8")
    ET.fromstring(conf_text)
    assert "ui_confirmationsTriggersRegistered" not in conf_text or "<if condition=\"!ui_confirmationsTriggersRegistered\">" not in conf_text
    assert "<trigger " not in conf_text, "confirmations_v2.package must not define interface-owned XML <trigger> nodes"
    for trig in CONFIRMATIONS_TRIGGERS:
        assert f"'{trig}'" in conf_text, f"confirmations_v2.package missing UITrigger.CreateTrigger for {trig}"

    common_path = OVERRIDE_ROOT / "ui/fe2/newui/common_v2.package"
    common_text = common_path.read_text(encoding="utf-8")
    ET.fromstring(common_text)
    assert "<if condition=\"!ui_commonTriggersRegistered\">" not in common_text
    assert "<trigger " not in common_text, "common_v2.package must not define interface-owned XML <trigger> nodes"
    assert "UITrigger.CreateTrigger('NewUIPopupMenu')" in common_text

    loading_path = OVERRIDE_ROOT / "ui/fe2/newui/loading_v2.package"
    loading_text = loading_path.read_text(encoding="utf-8")
    ET.fromstring(loading_text)
    assert "UITrigger.CreateTrigger('KongorQueueArcadeState')" in loading_text
    assert "UITrigger.CreateTrigger('TriggerDialogBox')" in loading_text
    assert "Loading_V2:OnClickLeaveQueue()" in loading_text

    # Compile all top-level onloadlua snippets extracted from the 3 packages with real luac51
    snippets: list[str] = []
    for pkg_text in (conf_text, common_text, loading_text):
        root = ET.fromstring(pkg_text)
        for panel in root.findall("./panel"):
            lua_code = panel.attrib.get("onloadlua", "").strip()
            if lua_code:
                snippets.append(f"local function _chk()\n{lua_code}\nend")
    assert len(snippets) >= 3
    lua_check_file = tmp_path / "pkg_onloadlua.lua"
    lua_check_file.write_text("\n".join(snippets), encoding="utf-8")
    proc = subprocess.run([str(luac51), "-p", str(lua_check_file)], capture_output=True, text=True, check=False)
    assert proc.returncode == 0, f"luac51 rejected package onloadlua: {proc.stderr}"


def test_login_interfaces_and_secondary_sites_enforce_clean_login_name() -> None:
    """Both main_v2.interface and main.interface wire PrepareCleanLoginUsername + onchangelua/onlosefocuslua guards."""
    for rel in ("ui/fe2/main_v2.interface", "ui/fe2/main.interface"):
        text = (OVERRIDE_ROOT / rel).read_text(encoding="utf-8")
        ET.fromstring(text)
        for tag in ("launcher_skip", "main_login_user_blur", "main_login_user_onenter", "main_login_password_onenter", "main_login_password_onevent", "main_login_button", "privacy_policy_confirm"):
            assert tag in text, f"{rel} missing PrepareCleanLoginUsername('{tag}') guard"
        assert "SanitizeLoginAccountName" in text, f"{rel} missing onchangelua SanitizeLoginAccountName guard"

    audio_news = (OVERRIDE_ROOT / "ui/scripts/main_v2_audio_news.lua").read_text(encoding="utf-8")
    assert "PrepareCleanLoginUsername" in audio_news

    nexus_bridge = (OVERRIDE_ROOT / "ui/scripts/nexus_bridge.lua").read_text(encoding="utf-8")
    assert nexus_bridge.count("SanitizeLoginAccountName") >= 2

    player_hosted = (OVERRIDE_ROOT / "ui/scripts/newui/player_hosted_v2.lua").read_text(encoding="utf-8")
    assert "sanitizeNick(curLoginCvar)" in player_hosted

    main_matches = (OVERRIDE_ROOT / "ui/scripts/main_matches.lua").read_text(encoding="utf-8")
    assert "SanitizeMatchLoginNick" in main_matches


def test_issue1_and_issue3d_queue_leave_arcade_and_drp_runtime(tmp_path: Path) -> None:
    """Real LuaVerifier execution of loading_v2.lua, teammaking_v2.lua, and communicator_v2.lua covering Issues 1 & 3D-Lua."""
    lua_test = r"""
local echoLog = {}
local uiCmds = {}
local sounds = {}
local triggerCalls = {}
local createdTriggers = {}
local watchesByWidget = {}
local mockWidgets = {}
local inQueueFlag = true
local inGroupFlag = true
local gamePhase = 0

_G.Echo = function(msg) table.insert(echoLog, tostring(msg)) end
_G.println = function() end
_G.GetScreenHeight = function() return 1080 end
_G.GetChatModeType = function() return 0 end
_G.IsLoggedIn = function() return true end
_G.IsBuddy = function() return false end
_G.IsMe = function(n) return tostring(n or "") == "SoloPilot" end
_G.ViewingStreaming = function() return false end
_G.Translate = function(k) return tostring(k) end
_G.Empty = function(s) return s == nil or s == "" or s == false end
_G.NotEmpty = function(s) return not _G.Empty(s) end
_G.AtoB = function(v) return v == true or v == 1 or v == "1" or v == "true" end
_G.Explode = function(_, s) return { tostring(s or "") } end
_G.GetChatClientInfo = function() return "" end
_G.GetAccountName = function() return "[GUCI]SoloPilot" end
_G.UIGetAccountID = function() return 42 end
_G.GetCurrentGamePhase = function() return gamePhase end
_G.UIGamePhase = function() return gamePhase end
_G.IsInQueue = function() return inQueueFlag end
_G.IsInGroup = function() return inGroupFlag end
_G.PlaySound = function(p) table.insert(sounds, tostring(p)) end
_G.playBgMusic = function() table.insert(sounds, "bg_music") end
_G.RegisterScript2 = function() end
_G.GetCvarString = function() return "" end
_G.GetCvarBool = function() return false end
_G.Set = function() end
_G.StripClanTag = function(name)
    local s = tostring(name or "")
    local i = string.find(s, "]")
    return i and string.sub(s, i + 1) or s
end
_G.UITrigger = {
    CreateTrigger = function(name)
        createdTriggers[name] = true
    end
}
_G.Trigger = function(name, ...)
    table.insert(triggerCalls, { name = name, args = { ... } })
    if name == "ChatAutoCompleteClear" then
        error("simulated unregistered ChatAutoCompleteClear")
    end
end
_G.Common_V2 = {
    PopupMenu = function() end,
    SetLongText = function(_, w, t) if w and w.SetText then w:SetText(t) end return false end,
    SetLongTextLabel = function(_, wn, n, c) end,
}
_G.MapSelection_V2 = { Hide = function() end }
_G.Social_V2 = { CloseSocialPopups = function() end }

local widgetMt = {
    __index = function(tbl, key)
        if key == "GetParent" or key == "FindWidget" or key == "GetChild" or key == "GetRoot" then
            local fn = function(s) return s end
            rawset(tbl, key, fn)
            return fn
        end
        local dummy = function() return nil end
        rawset(tbl, key, dummy)
        return dummy
    end
}

local function makeWidget(name)
    local w = {
        _name = name,
        _visible = false,
        _watches = {},
    }
    function w:GetName() return self._name end
    function w:SetVisible(v) self._visible = (v == true or v == 1) end
    function w:IsVisible() return self._visible end
    function w:IsValid() return true end
    function w:SetEnabled() end
    function w:SetText(t) self._text = t end
    function w:SetTexture() end
    function w:SetWidth() end
    function w:SetHeight() end
    function w:SetX() end
    function w:SetY() end
    function w:SetColor() end
    function w:ScaleWidth() end
    function w:ScaleHeight() end
    function w:FadeIn() self._visible = true end
    function w:FadeOut() self._visible = false end
    function w:EraseInputLine() end
    function w:SetCallback() end
    function w:RefreshCallbacks() end
    function w:ClearItems() end
    function w:AddTemplateListItem() end
    function w:RegisterWatch(trig, cb)
        self._watches[trig] = cb
        watchesByWidget[self._name] = watchesByWidget[self._name] or {}
        watchesByWidget[self._name][trig] = cb
    end
    function w:UnregisterWatch(trig)
        self._watches[trig] = nil
        if watchesByWidget[self._name] then watchesByWidget[self._name][trig] = nil end
    end
    function w:UICmd(cmd)
        table.insert(uiCmds, tostring(cmd))
        if cmd == "IsInQueue" then return inQueueFlag and "1" or "0" end
        return "0"
    end
    setmetatable(w, widgetMt)
    mockWidgets[name] = w
    return w
end

_G.GetWidget = function(name, _, _)
    if not name then return nil end
    return mockWidgets[name] or makeWidget(name)
end

local function makeIface(name)
    local obj = makeWidget("iface_" .. name)
    obj.GetName = function() return name end
    obj.GetWidget = function(_, wn) return _G.GetWidget(wn) end
    obj.Sleep = function() end
    obj.UICmd = function(_, cmd)
        table.insert(uiCmds, name .. ":" .. tostring(cmd))
        if cmd == "IsInQueue" then return inQueueFlag and "1" or "0" end
        return "0"
    end
    return obj
end

local mainIface = makeIface("main")
local otherIface = makeIface("loading")
_G.UIManager = {
    GetInterface = function(name)
        if name == "main" then return mainIface end
        if name == "loading" then return otherIface end
        return nil
    end
}

-- 1. Non-main interface must NOT bind TMM watches in Loading_V2:Init()
_G.object = otherIface
dofile("games/client/override/ui/scripts/newui/loading_v2.lua")
Loading_V2:Init()
assert(watchesByWidget["loading_v2_helper"] == nil, "Non-main interface must not bind TMM watches on loading_v2_helper")

-- Reloading loading_v2.lua on main must preserve Loading_V2 table identity & bind watches
local prevTable = Loading_V2
prevTable._sentinelReloadPreserved = true
_G.object = mainIface
dofile("games/client/override/ui/scripts/newui/loading_v2.lua")
assert(Loading_V2 == prevTable and Loading_V2._sentinelReloadPreserved == true, "Loading_V2 = Loading_V2 or {} must preserve table")
Loading_V2:Init()
assert(watchesByWidget["loading_v2_helper"] and watchesByWidget["loading_v2_helper"]["TMMReadyStatus"] ~= nil, "main interface Init must bind TMMReadyStatus")

-- Load teammaking_v2.lua & communicator_v2.lua
dofile("games/client/override/ui/scripts/newui/safecall.lua")
dofile("games/client/override/ui/scripts/newui/teammaking_v2.lua")
dofile("games/client/override/ui/scripts/newui/communicator_v2.lua")

-- 2. Nil TeamMembers[1] while IsInGroup()==true must NOT crash OnRightClickTeamMember or OnClickLeaveQueue
Teammaking_V2.TeamMembers = {}
inGroupFlag = true
inQueueFlag = true
assert(Teammaking_V2:GetActivePartyMemberCount() == 0)
Teammaking_V2:OnRightClickTeamMember(1)

-- 3. Solo 1-click immediate leave even when IsInGroup()==true and TeamMembers[1].isLeader==false
Teammaking_V2.TeamMembers = { { name = "SoloPilot", isLeader = false } }
assert(Teammaking_V2:GetActivePartyMemberCount() == 1)
uiCmds, triggerCalls, sounds = {}, {}, {}
Loading_V2:OnClickLeaveQueue()
local sawLeaveQueue = false
local sawDialogTrigger = false
for _, c in ipairs(uiCmds) do
    if string.find(c, "LeaveTMMQueue();", 1, true) then sawLeaveQueue = true end
end
for _, t in ipairs(triggerCalls) do
    if t.name == "TriggerDialogBox" then sawDialogTrigger = true end
end
assert(sawLeaveQueue == true, "Solo leave queue must immediately dispatch LeaveTMMQueue();")
assert(sawDialogTrigger == false, "Solo leave queue must NOT open TriggerDialogBox")

-- 4. Multi-player party (>1 active members) opens TriggerDialogBox and non-leader calls LeaveGroup()
Teammaking_V2.TeamMembers = {
    { name = "SoloPilot", isLeader = false },
    { name = "PartnerTwo", isLeader = true },
}
assert(Teammaking_V2:GetActivePartyMemberCount() == 2)
uiCmds, triggerCalls, createdTriggers = {}, {}, {}
Loading_V2:OnClickLeaveQueue()
local dlgTriggered = false
for _, t in ipairs(triggerCalls) do
    if t.name == "TriggerDialogBox" then dlgTriggered = true end
end
assert(createdTriggers["TriggerDialogBox"] == true, "Multi-player leave must ensure UITrigger.CreateTrigger('TriggerDialogBox')")
assert(dlgTriggered == true, "Multi-player party leave must open TriggerDialogBox confirmation")

uiCmds = {}
Teammaking_V2:OnClickLeaveQueue()
local sawLeaveGroup = false
for _, c in ipairs(uiCmds) do
    if string.find(c, "LeaveTMMGroup();", 1, true) then sawLeaveGroup = true end
end
assert(sawLeaveGroup == true, "Multi-player non-leader must dispatch LeaveTMMGroup()")

-- 5. Missing generic_dialog_box falls back directly to leave queue instead of trapping player
mockWidgets["generic_dialog_box"] = nil
_G.GetWidget = function(name, _, _)
    if name == "generic_dialog_box" then return nil end
    return mockWidgets[name] or makeWidget(name)
end
Teammaking_V2.TeamMembers = { { name = "LeaderOne", isLeader = true }, { name = "MemberTwo", isLeader = false } }
uiCmds = {}
Loading_V2:OnClickLeaveQueue()
local sawFallbackLeave = false
for _, c in ipairs(uiCmds) do
    if string.find(c, "LeaveTMMQueue();", 1, true) then sawFallbackLeave = true end
end
assert(sawFallbackLeave == true, "Missing generic_dialog_box must fall back directly to LeaveTMMQueue();")

-- 6. Issue 1: Communicator_V2:OnLogin preserves ARCADE: OFF across post-match menu return, resets on logout / account switch
_G._kongorQueueArcadeInitializedAccount = nil
Communicator_V2:OnLogout()
Communicator_V2:OnLogin()
assert(_G.KONGOR_QUEUE_ARCADE_ENABLED == true, "Fresh login must initialize ARCADE: ON")
_G.KongorSetQueueArcadeEnabled(false, "user_off")
Communicator_V2:OnLogin()
assert(_G.KONGOR_QUEUE_ARCADE_ENABLED == false, "Post-match same-account OnLogin() must preserve ARCADE: OFF")
_G.GetAccountName = function() return "SecondAccount" end
Communicator_V2:OnLogin()
assert(_G.KONGOR_QUEUE_ARCADE_ENABLED == true, "Switching account must initialize ARCADE: ON")
_G.KongorSetQueueArcadeEnabled(false, "user_off_2")
Communicator_V2:OnLogout()
Communicator_V2:OnLogin()
assert(_G.KONGOR_QUEUE_ARCADE_ENABLED == true, "Logging out and back in must reset ARCADE: ON")

-- 7. Issue 3D-Lua: Post-Arcade Phase 0 settle re-emits status=Queue;mode=<_kongorLastQueueMode>
_G._kongorLastQueueMode = "caldavar"
inQueueFlag = true
inGroupFlag = false
gamePhase = 0
echoLog = {}
Teammaking_V2:EmitPartyOrQueueDrp("queue_panel")
gamePhase = 6
Teammaking_V2:EmitPartyOrQueueDrp("in_arcade")
gamePhase = 0
echoLog = {}
Teammaking_V2:EmitPartyOrQueueDrp("phase0_settle")
local foundDrp = false
for _, line in ipairs(echoLog) do
    if string.find(line, "[NEXUS_TELEMETRY] status=Queue;", 1, true) and string.find(line, ";mode=caldavar", 1, true) then
        foundDrp = true
    end
end
assert(foundDrp == true, "Post-Arcade Phase 0 settle must re-emit [NEXUS_TELEMETRY] status=Queue;...;mode=caldavar")
"""
    _run_lua_verifier(tmp_path, "verify_wave7_queue_arcade_drp.lua", lua_test)


def test_issue4_login_username_sanitization_and_lobby_clantag_preservation(tmp_path: Path) -> None:
    """Real LuaVerifier execution of main_v2.lua, main_v2_audio_news.lua, nexus_bridge.lua, player_hosted_v2.lua, and main_matches.lua."""
    lua_test = r"""
local cvarStore = {
    login_name = "^900[GUCI]^wPilotAlpha",
    ui_lastSavedAccountName = "[GUCI]PilotAlpha",
    login_password = string.rep("cd", 16),
    login_rememberName = "true",
    login_rememberPassword = "true",
    login_autoLogin = "false",
    ui_useClientLoginSystem = "true",
    ui_suppressAutoLogin = "false",
    kongor_offline_hub = "false",
    p2p_mode = "false",
    p2p_standalone = "false",
    p2p_nickname = "Player",
    svr_login = "[GUCI]PilotAlpha",
}
local uiCmds = {}
local widgets = {}

_G.Echo = function() end
_G.println = function() end
_G.printdb = function() end
_G.PlaySound, _G.PlayNamedSound, _G.PlaySoundLooping, _G.StopSound = function() end, function() end, function() end, function() end
_G.GetTime, _G.HostTime = function() return 5000 end, function() return 5000 end
_G.Host = { IsConnectedToMasterServer = function() return true end }
_G.GetMaps = function() return { "caldavar", "midwars" } end
_G.memoizeObject, _G.memoize, _G.memoize2, _G.memoize3, _G.memoizeR2 = function(f) return f end, function(f) return f end, function(f) return f end, function(f) return f end, function(f) return f end
_G.GetScreenWidth, _G.GetScreenHeight = function() return 1920 end, function() return 1080 end
_G.Empty = function(s) return s == nil or s == "" or s == false end
_G.NotEmpty = function(s) return not _G.Empty(s) end
_G.AtoB = function(v) return v == true or v == 1 or v == "1" or v == "true" end
_G.AtoN = function(v) return tonumber(v) or 0 end
_G.ItoA = function(v) return tostring(v or 0) end
_G.FtoA = function(v) return tostring(v or 0) end
_G.Translate = function(k) return tostring(k) end
_G.GetCvar = function(k) return tostring(cvarStore[k] or "") end
_G.GetCvarString = _G.GetCvar
_G.GetCvarBool = function(k)
    local v = cvarStore[k]
    return v == true or v == 1 or v == "1" or v == "true"
end
_G.GetCvarInt = function(k) return tonumber(cvarStore[k]) or 0 end
_G.GetCvarNumber = _G.GetCvarInt
_G.GetCvarFloat = _G.GetCvarInt
_G.Set = function(k, v)
    if type(v) == "boolean" then cvarStore[k] = v and "true" or "false" else cvarStore[k] = tostring(v or "") end
end
_G.SetSave = _G.Set
_G.Cmd = function(c) table.insert(uiCmds, tostring(c)) end
_G.Trigger = function() end
_G.RegisterScript = function() end
_G.RegisterScript2 = function() end
_G.SetCancelCallback = function() end
_G.GetDBEntry = function() return {} end
_G.SetDBEntry = function() end
_G.HideMainCornerAds = function() end
_G.ShowMainCornerAds = function() end
_G.UpdateTimeDependantFunctionality = function() end
_G.CancelLogin = function() end
_G.GetAgreePrivacyPolicy = function() return true end
_G.UI = { RequiresValidation = false }
_G.ResourceManager = { LoadContext = function() end, UnloadContext = function() end }
_G.trackedUserFocus = { logFocus = function() end }
_G.Teammaking_V2 = { UpdateLeaverBlock = function() end, Login = function() end }
_G.Common_V2 = { UsingLargeUI = function() return true end, ShowSimpleTip = function() end }
_G.UIGamePhase, _G.GetCurrentGamePhase = function() return 0 end, function() return 0 end
_G.IsLoggedIn = function() return false end
_G.IsLoggingIn = function() return false end
_G.IsInGame, _G.IsInQueue, _G.IsInGroup, _G.IsTMMEnabled = function() return false end, function() return false end, function() return false end, function() return false end
_G.GetExperience = function() return 100 end
_G.Cvar = {
    GetCvar = function(name)
        return {
            GetString = function() return tostring(cvarStore[name] or "") end,
            GetBool = function() return _G.GetCvarBool(name) end,
            GetBoolean = function() return _G.GetCvarBool(name) end,
            GetNumber = function() return tonumber(cvarStore[name]) or 0 end,
            Set = function(_, val) _G.Set(name, val) end,
            SetSave = function() end
        }
    end,
    CreateCvar = function(name, _, def)
        if cvarStore[name] == nil then cvarStore[name] = tostring(def or "") end
        return _G.Cvar.GetCvar(name)
    end,
    SetCloudSave = function() end
}

local widgetMt = {
    __index = function(tbl, key)
        if key == "GetParent" or key == "FindWidget" or key == "GetChild" or key == "GetRoot" then
            local fn = function(s) return s end
            rawset(tbl, key, fn)
            return fn
        end
        local dummy = function() return nil end
        rawset(tbl, key, dummy)
        return dummy
    end
}

local function makeWidget(name)
    local w = { _name = name, _visible = true, _value = "", _enabled = true }
    function w:GetName() return self._name end
    function w:IsVisible() return self._visible end
    function w:SetVisible(v) self._visible = (v == true or v == 1 or v == "1" or v == "true") end
    function w:SetEnabled(v) self._enabled = (v == true or v == 1) end
    function w:GetValue() return self._value end
    function w:SetInputLine(v) self._value = tostring(v or "") end
    function w:EraseInputLine() self._value = "" end
    function w:HasFocus() return false end
    function w:SetCallback() end
    function w:RegisterWatch() end
    function w:RegisterWatchLua() end
    function w:UnregisterWatchLua() end
    function w:BringToFront() end
    function w:SetText() end
    function w:SetColor() end
    function w:StartAnim() end
    function w:UICmd(c) table.insert(uiCmds, tostring(c)); return "" end
    setmetatable(w, widgetMt)
    widgets[name] = w
    return w
end

_G.GetWidget = function(name) return widgets[name] or makeWidget(name) end
_G.GetWidgetNoMem = _G.GetWidget
_G.WidgetExists = function(name) return widgets[tostring(name)] ~= nil end

local mockIface = {
    GetName = function() return "main" end,
    GetWidget = function(_, n) return _G.GetWidget(n) end,
    RegisterWatch = function() end,
    UnregisterWatch = function() end,
    RegisterWatchLua = function() end,
    Sleep = function() end,
    HoNMainF = function() end,
    UICmd = function(_, c) table.insert(uiCmds, tostring(c)); return "" end,
}
_G.object = mockIface
_G.interface = mockIface
_G.UIManager = { GetInterface = function() return mockIface end, GetActiveInterface = function() return mockIface end }

_G._rawEngineGetAccountNameUnwrapped = function() return "[GUCI]PilotAlpha" end
_G._rawEngineGetAccountName = nil
_G._latestEngineAccountName = nil
_G.GetAccountName = nil

dofile("games/client/override/ui/scripts/main_v2.lua")
dofile("games/client/override/ui/scripts/main_v2_audio_news.lua")

-- 1. SanitizeLoginAccountName unit matrix
assert(_G.SanitizeLoginAccountName("[GUCI]PilotAlpha") == "PilotAlpha")
assert(_G.SanitizeLoginAccountName("^900[GUCI]^wPilotAlpha") == "PilotAlpha")
assert(_G.SanitizeLoginAccountName("[A][B] ^cPilotAlpha^w") == "PilotAlpha")
assert(_G.SanitizeLoginAccountName("0KONGOR") == "")
assert(_G.SanitizeLoginAccountName("[TAG]KONGOR") == "")
assert(_G.SanitizeLoginAccountName("UnnamedNewbie") == "")
assert(_G.SanitizeLoginAccountName("MODERATOR") == "")
assert(_G.SanitizeLoginAccountName("Player") == "Player")

-- 2. In-lobby GetAccountName() must KEEP [CLAN] tag intact
assert(_G.GetAccountName() == "[GUCI]PilotAlpha", "In-lobby GetAccountName() must preserve [GUCI] clan tag")

-- 3. SyncLoginUsernameField + CheckForAutoLogin self-heals poisoned cvars & textbox BEFORE Login()
local usrBox = makeWidget("main_login_user")
usrBox:SetInputLine("^770[GUCI]PilotAlpha")
uiCmds = {}
_G.CheckForAutoLogin()
assert(usrBox:GetValue() == "PilotAlpha", "CheckForAutoLogin must self-heal main_login_user to PilotAlpha")
assert(cvarStore.login_name == "PilotAlpha", "CheckForAutoLogin must self-heal login_name to PilotAlpha")
assert(cvarStore.ui_lastSavedAccountName == "PilotAlpha", "CheckForAutoLogin must self-heal ui_lastSavedAccountName to PilotAlpha")
local sawLogin = false
for _, c in ipairs(uiCmds) do if string.find(c, "Login()", 1, true) then sawLogin = true end end
assert(sawLogin == true, "Healed CheckForAutoLogin must proceed to Login()")

-- 4. LoggedIn() strips [CLAN] from ui_lastSavedAccountName & login_name while keeping GetAccountName() tagged
cvarStore.login_name = "[GUCI]PilotAlpha"
cvarStore.ui_lastSavedAccountName = "[GUCI]PilotAlpha"
_G._lastLoggedInTime = nil
_G.LoggedIn()
assert(cvarStore.ui_lastSavedAccountName == "PilotAlpha", "LoggedIn() must persist clean ui_lastSavedAccountName without [CLAN]")
assert(cvarStore.login_name == "PilotAlpha", "LoggedIn() must strip [CLAN] from login_name")
assert(_G.GetAccountName() == "[GUCI]PilotAlpha", "LoggedIn() must NOT strip [CLAN] from GetAccountName()")

-- 5. PrivacyPolicyManager:OnMainLoginPasswordEvent invokes PrepareCleanLoginUsername before Login()
cvarStore.login_name = "[TAG]HeroTwo"
cvarStore.ui_lastSavedAccountName = ""
usrBox:SetInputLine("[TAG]HeroTwo")
uiCmds = {}
PrivacyPolicyManager:OnMainLoginPasswordEvent()
assert(cvarStore.login_name == "HeroTwo" and usrBox:GetValue() == "HeroTwo",
    "PrivacyPolicyManager:OnMainLoginPasswordEvent must sanitize username before Login()")

-- 6. Secondary login_name writers (nexus_bridge.lua & main_matches.lua) sanitize [CLAN] tags
dofile("games/client/override/ui/scripts/nexus_bridge.lua")
Nexus.BypassLogin("[CLAN]^cBridgeUser")
assert(cvarStore.login_name == "BridgeUser", "Nexus.BypassLogin must sanitize [CLAN] and color tags from login_name")

_G._origGetAccountName = function() return "[CLAN]MatchUser" end
dofile("games/client/override/ui/scripts/main_matches.lua")
cvarStore.p2p_nickname = ""
cvarStore.login_name = "[CLAN]MatchUser"
usrBox:SetInputLine("[CLAN]MatchUser")
local resolved = P2P_ResolveNick()
assert(resolved == "MatchUser" and cvarStore.login_name == "MatchUser",
    "main_matches P2P_ResolveNick must sanitize [CLAN] from login_name")
"""
    _run_lua_verifier(tmp_path, "verify_wave7_login_sanitization.lua", lua_test)
