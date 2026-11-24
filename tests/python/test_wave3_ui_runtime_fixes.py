#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Wave 3 runtime regression tests protecting three live-diagnosed K2 UI fixes:
  1. Main midbar logo glow (main_midbar_logo_glow) visible immediately on initial load/login.
  2. Test+++ offline audit resilient against stale custom-map dynamic entities (e.g. valkwars.s2z).
  3. Zero full-screen white-screen regression from SetColor('1 1 1 1') on color="invisible" panels
     (k2_x64.dll+0x675150 CPanel::CPanel / +0xc1f330 IWidget::RenderWidget) and Offline Hub CloseCreate.
"""

from __future__ import annotations

import pathlib
import re
import subprocess

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
UI_ROOT = REPO_ROOT / "games" / "client" / "override" / "ui"


def _read(rel: str) -> str:
    return (UI_ROOT / rel).read_text(encoding="utf-8")


def _find_lua_verifier() -> pathlib.Path:
    for cfg in ("Release", "Debug"):
        candidate = REPO_ROOT / "src" / "tools" / "lua-verifier" / "bin" / cfg / "net11.0" / "LuaVerifier.exe"
        if candidate.is_file():
            return candidate
    pytest.fail("LuaVerifier.exe not found under src/tools/lua-verifier/bin/{Release,Debug}/net11.0/")


def _run_lua_verifier(tmp_path: pathlib.Path, script_name: str, lua_body: str) -> str:
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


def test_logo_glow_visible_on_initial_load_and_login_paths():
    homepage_pkg = _read("fe2/newui/homepage_v2.package")
    assert 'name="main_midbar_logo_glow_model"' in homepage_pkg
    assert "item_image_bg_specials.effect" in homepage_pkg

    main_v2_lua = _read("scripts/main_v2.lua")
    logged_in_match = re.search(
        r"local function LoggedIn\(\)(.*?)-- DEF-MAIN-02",
        main_v2_lua,
        re.DOTALL,
    )
    assert logged_in_match is not None, "LoggedIn() block not found in main_v2.lua"
    logged_in_body = logged_in_match.group(1)
    assert "GetWidget('main_midbar_logo_glow'" in logged_in_body, (
        "LoggedIn() must restore main_midbar_logo_glow alongside main_midbar_logo"
    )
    assert "mlglow:SetVisible(true)" in logged_in_body

    # Main_PostLoadRestoration must also restore main_midbar_logo_glow on both immediate & 50ms paths
    assert "midLogoGlow:SetVisible(true)" in main_v2_lua

    p2p_lua = _read("scripts/main_v2_p2p.lua")
    assert "ensureMidbarLogoGlowVisible('immediate')" in p2p_lua
    assert "ensureMidbarLogoGlowVisible('settle_200ms')" in p2p_lua

    for iface_rel in ("fe2/main_v2.interface", "fe2/main.interface"):
        iface = _read(iface_rel)
        assert "Call('main_midbar_logo_glow','SetVisible(1); SlideX(\\'0%\\', 250);');" in iface, (
            f"{iface_rel} event_midbar onevent1 must SetVisible(1) on main_midbar_logo_glow"
        )
        assert "Ignoring stale native LoginStatus(offline) while kongor_offline_hub=true" in iface, (
            f"{iface_rel} must ignore stale native offline events from CancelLogin during offline hub"
        )


def test_test_suite_filters_stale_unmounted_custom_map_entities():
    ts = _read("test_suite/test_suite.lua")
    assert "function TestSuite.EnsureCleanLobbyEntities()" in ts
    assert "pcall(Testing.UnmountWorld)" in ts
    assert "local function ResolveActiveEntityFilePath(entityName)" in ts
    assert "pcall(Testing.GetEntityFilePath, entityName)" in ts
    assert "pcall(Testing.FileExists, entityPath)" in ts
    assert "[STALE_MAP_FILTER]" in ts
    # Genuine XML syntax errors on existing files must still report test_client_error_xml_parse
    assert 'TestSuite.AuditLog("test_client_error_xml_parse", "filePath", entityPath)' in ts


def test_return_to_login_never_paints_invisible_fullscreen_panels_white_and_respects_offline_hub():
    hosted = _read("scripts/newui/player_hosted_v2.lua")

    assert "function Player_Hosted_V2:IsLobbySessionActive()" in hosted
    assert "GetCvarBool('kongor_offline_hub')" in hosted

    ret_match = re.search(
        r"function Player_Hosted_V2:ReturnToLoginScreen\(\)(.*?)\nend\b",
        hosted,
        re.DOTALL,
    )
    assert ret_match is not None, "ReturnToLoginScreen() not found"
    ret_body = ret_match.group(1)

    # Must NOT unconditionally call SetColor('1 1 1 1') across container panels
    assert "if w.SetColor then w:SetColor('1 1 1 1') end" not in ret_body, (
        "Unconditional SetColor('1 1 1 1') in ReturnToLoginScreen turns color=\"invisible\" "
        "100%x100% prelogin_loginbox_disabled_login solid white via k2_x64.dll+0xc1f330"
    )
    assert "texturedLogoWidgets[wn] and w.SetColor" in ret_body
    assert "invisibleContainerPanels[wn] and w.SetColor" in ret_body
    assert "w:SetColor('0 0 0 0')" in ret_body
    assert "Set('ui_suppressAutoLogin', 'true', 'bool')" in ret_body
    assert "Trigger('LoginStatus', 'loggedout', '', 0, 0, 1, 0)" in ret_body
    assert "/heroes/empath/alt9/model_store_l.mdf" in ret_body

    close_match = re.search(
        r"function Player_Hosted_V2:CloseCreate\(disconnect\)(.*?)\nend\b",
        hosted,
        re.DOTALL,
    )
    assert close_match is not None, "CloseCreate() not found"
    close_body = close_match.group(1)
    assert "Player_Hosted_V2:IsLobbySessionActive()" in close_body
    assert "not IsLoggedIn()" not in close_body, (
        "CloseCreate must not rely solely on native IsLoggedIn(), which is false in Offline Hub"
    )

    # SHJ-010 P0-03: SetColor('0 0 0 0') strictly on prelogin_loginbox_disabled_login, never on
    # prelogin_loginbox or prelogin_loginbox_contents; FadeIn + SlideY('-10.0h', 1) restores login box.
    inv_match = re.search(
        r"local invisibleContainerPanels\s*=\s*\{(.*?)\}",
        ret_body,
        re.DOTALL,
    )
    assert inv_match is not None, "invisibleContainerPanels table missing"
    inv_body = inv_match.group(1)
    assert "prelogin_loginbox_disabled_login = true" in inv_body
    assert "prelogin_loginbox =" not in inv_body
    assert "prelogin_loginbox_contents =" not in inv_body
    assert "box:SlideY('-10.0h', 1)" in ret_body
    assert "box:FadeIn(1)" in ret_body
    assert "boxCont:FadeIn(1)" in ret_body
    assert "if heroPed.SetModel then heroPed:SetModel(" in ret_body
    assert "local ok, err = pcall(function()" in ret_body
    assert "_G._p2pReturningToLogin = false" in ret_body
    assert "ReturnToLoginScreen failed" in ret_body


def test_offline_hub_stale_offline_guard_and_session_flag_ordering():
    """Must-Fix #3: main_v2.lua LoginStatus guards stale native 'offline' while kongor_offline_hub=true,
    and EnterOfflineHub sets session flags BEFORE calling CancelLogin()."""
    main_v2_lua = _read("scripts/main_v2.lua")
    login_fn_match = re.search(
        r"local function LoginStatus\(self,.*?\n\tif \(isLoggedIn\) then",
        main_v2_lua,
        re.DOTALL,
    )
    assert login_fn_match is not None, "LoginStatus() header not found in main_v2.lua"
    login_head = login_fn_match.group(0)
    assert "statusLower == 'offline' and (not _G._p2pReturningToLogin)" in login_head
    assert "GetCvarBool('kongor_offline_hub')" in login_head
    assert "[MAIN_LUA] Ignoring stale native LoginStatus(offline) while kongor_offline_hub=true" in login_head
    assert "main_midbar_logo_glow" in login_head

    # Ensure stale-offline guard runs BEFORE clearing HoN_Main.isFullyLoggedIn = false
    guard_pos = login_head.index("Ignoring stale native LoginStatus(offline)")
    clear_pos = login_head.index("HoN_Main.isFullyLoggedIn = false")
    assert guard_pos < clear_pos, "Stale-offline guard must run before clearing HoN_Main.isFullyLoggedIn"

    p2p_lua = _read("scripts/main_v2_p2p.lua")
    hub_match = re.search(
        r"function EnterOfflineHub\(sourceReason\)(.*?)\nend\b",
        p2p_lua,
        re.DOTALL,
    )
    assert hub_match is not None, "EnterOfflineHub() not found in main_v2_p2p.lua"
    hub_code = re.sub(r"--[^\n]*", "", hub_match.group(1))
    idx_flag = hub_code.index("HoN_Main.isFullyLoggedIn = true")
    idx_cancel = hub_code.index("CancelLogin()")
    assert idx_flag < idx_cancel, (
        "EnterOfflineHub() must set _loggedin/ui_isLoggedIn/HoN_Main.isFullyLoggedIn BEFORE calling CancelLogin()"
    )


def test_walkthrough_watch_registration_and_getwidget_contract(tmp_path: pathlib.Path):
    """Must-Fix #4: main_v2_walkthrough.lua resolves _G.object/object, never redirects missing
    explicit fromInterface to 'main', and registers idempotent SafeCall-wrapped watches via main_v2.lua."""
    wt_lua = _read("scripts/main_v2_walkthrough.lua")
    main_v2_lua = _read("scripts/main_v2.lua")

    assert "_G.object or object or _G.interface" in wt_lua
    assert "if fromInterface ~= nil and fromInterface ~= '' then" in wt_lua
    assert "if not targetIface then\n\t\t\treturn nil\n\t\tend" in wt_lua
    assert "function HoN_Main.RegisterWalkthroughWatches(" in wt_lua
    assert "HoN_Main.RegisterWalkthroughWatches(interface)" in main_v2_lua

    # main_v2.lua must invoke RegisterWalkthroughWatches right after PatchGlobalInterface
    idx_patch = main_v2_lua.index("_G.PatchGlobalInterface, interface")
    idx_reg = main_v2_lua.index("HoN_Main.RegisterWalkthroughWatches(interface)")
    assert idx_patch < idx_reg < idx_patch + 500

    # Real LuaVerifier execution test of main_v2_walkthrough.lua
    lua_test = r"""
local echoLog = {}
local watches = {}
local unregistered = {}
_G.Echo = function(msg) table.insert(echoLog, tostring(msg)) end
_G.Empty = function(s) return s == nil or s == "" end
_G.NotEmpty = function(s) return not _G.Empty(s) end

local mainWidgets = {
    main_widget_highlighter = { SetVisible = function() end },
}
local mockMainIface = {
    _safeRegisterWatchPatched = false,
    GetWidget = function(self, name) return mainWidgets[name] end,
    RegisterWatch = function(self, name, fn) table.insert(watches, { name = name, wrapped = self._safeRegisterWatchPatched }) end,
    UnregisterWatch = function(self, name) table.insert(unregistered, name) end,
}

_G.object = mockMainIface
_G.UIManager = {
    GetInterface = function(name)
        if name == 'main' then return mockMainIface end
        return nil
    end,
}

dofile('games/client/override/ui/scripts/main_v2_walkthrough.lua')

-- 1. Before PatchGlobalInterface, watches must be deferred (zero unwrapped registrations)
assert(#watches == 0, "Expected 0 watches before PatchGlobalInterface, got " .. tostring(#watches))

-- 2. Calling RegisterWalkthroughWatches(nil) with no main interface logs warning and returns false
_G.object = nil
_G.interface = nil
local origGetIface = _G.UIManager.GetInterface
_G.UIManager.GetInterface = function() return nil end
assert(HoN_Main.RegisterWalkthroughWatches(nil) == false, "Expected false when iface is nil")

-- 3. Simulate main_v2.lua running PatchGlobalInterface then calling RegisterWalkthroughWatches
_G.UIManager.GetInterface = origGetIface
_G.object = mockMainIface
mockMainIface._safeRegisterWatchPatched = true
assert(HoN_Main.RegisterWalkthroughWatches(mockMainIface) == true, "Expected true after PatchGlobalInterface")
assert(#watches == 2, "Expected 2 watches registered, got " .. tostring(#watches))
assert(watches[1].name == 'DoWalkthrough' and watches[1].wrapped == true)
assert(watches[2].name == 'HighlightWidget' and watches[2].wrapped == true)

-- 4. Second call is idempotent (no duplicate watch registration)
assert(HoN_Main.RegisterWalkthroughWatches(mockMainIface) == true)
assert(#watches == 2, "Expected idempotent no-op on second RegisterWalkthroughWatches call")

print("WALKTHROUGH_OK")
"""
    stdout = _run_lua_verifier(tmp_path, "test_walkthrough_watches.lua", lua_test)
    assert "WALKTHROUGH_OK" in stdout


def test_kongor_logger_windows_locked_rename_fallback_and_safe_close(tmp_path: pathlib.Path):
    """Must-Fix #5: kongor_logger.lua handles os.rename returning (nil, errmsg) via truncation fallback,
    never leaks _diskFile if flush() errors, and avoids double rollover probing."""
    log_path = (tmp_path / "locked_client_lua.log").as_posix()
    lua_test = f"""
local logPath = {log_path!r}
local echoLog = {{}}
local renameCalls = 0
local probeCalls = 0
local truncCalls = 0
local closedCount = 0
local failFlushOnce = true

_G.Echo = function(msg) table.insert(echoLog, tostring(msg)) end
_G.KONGOR_LUA_LOG_PATH = logPath
_G.KONGOR_OFFLINE_TEST_HARNESS = false

local realOpen = io.open
io.open = function(path, mode)
    if path == logPath and mode == "rb" then
        probeCalls = probeCalls + 1
        return realOpen(path, mode)
    elseif path == logPath and mode == "w" then
        truncCalls = truncCalls + 1
        return realOpen(path, mode)
    elseif path == logPath and mode == "a" then
        local fh = realOpen(path, mode)
        if not fh then return nil end
        return {{
            write = function(self, s) return fh:write(s) end,
            flush = function(self)
                if failFlushOnce then
                    failFlushOnce = false
                    error("simulated flush I/O failure")
                end
                return fh:flush()
            end,
            close = function(self)
                closedCount = closedCount + 1
                return fh:close()
            end,
        }}
    end
    return realOpen(path, mode)
end

-- Simulate Windows locked-file os.rename returning (nil, "Permission denied") without throwing
os.rename = function(src, dst)
    renameCalls = renameCalls + 1
    return nil, "Permission denied"
end

-- Pre-populate file slightly below 2 MiB so one log line triggers rollover
do
    local seed = realOpen(logPath, "wb")
    seed:write(string.rep("A", (2 * 1024 * 1024) - 8))
    seed:close()
end

local logger = dofile("games/client/override/ui/scripts/newui/kongor_logger.lua")
logger.SetLogLevel("INFO")

-- First line crosses 2 MiB cap: flush throws, close must STILL execute, rename returns (nil, err),
-- and truncation fallback must truncate the file instead of leaving it > 2 MiB!
logger.Info("TEST", "trigger_rollover_line_1")
assert(closedCount == 1, "Expected handle closed even when flush() throws, got " .. tostring(closedCount))
assert(renameCalls == 1, "Expected 1 rename attempt, got " .. tostring(renameCalls))
assert(truncCalls == 1, "Expected 1 truncation fallback after locked rename, got " .. tostring(truncCalls))

-- Second line re-opens cleanly WITHOUT double-probing rollover
local probesBefore = probeCalls
logger.Info("TEST", "post_rollover_line_2")
assert(probeCalls == probesBefore, "Expected zero double rollover probe after rotation (before=" .. tostring(probesBefore) .. ", after=" .. tostring(probeCalls) .. ")")

-- Verify file on disk was actually truncated and now holds only the post-rollover line
logger.Flush()
local verifyFh = realOpen(logPath, "rb")
local content = verifyFh:read("*a")
verifyFh:close()
assert(#content < 1024, "Expected truncated small file after rollover, got bytes=" .. tostring(#content))
assert(string.find(content, "post_rollover_line_2", 1, true) ~= nil, "Expected post_rollover_line_2 in truncated log")

-- Verify throttled warning was emitted to console
local sawWarn = false
for _, line in ipairs(echoLog) do
    if string.find(line, "Disk log rename failed", 1, true) then
        sawWarn = true
        break
    end
end
assert(sawWarn, "Expected throttled warning when os.rename returns (nil, err)")

print("LOGGER_ROTATION_OK")
"""
    stdout = _run_lua_verifier(tmp_path, "test_kongor_logger_rotation.lua", lua_test)
    assert "LOGGER_ROTATION_OK" in stdout


def test_audit_entity_usage_and_safe_fallbacks_and_mirror_manifests():
    """Should-Fix #16, #19, #21: AuditEntityUsage cleans lobby entities + logs stale filter + guards
    GetEntityFilePath/UnmountWorld; bare CancelLogin fallbacks guarded; _kongorTryDoFile logs errors;
    mirror manifests cover Wave 3A scripts and lib_json.lua."""
    ts = _read("test_suite/test_suite.lua")
    usage_match = re.search(
        r"function TestSuite\.AuditEntityUsage\(\)(.*?)\nend\b",
        ts,
        re.DOTALL,
    )
    assert usage_match is not None, "AuditEntityUsage() not found in test_suite.lua"
    usage_body = usage_match.group(1)
    assert "TestSuite.EnsureCleanLobbyEntities()" in usage_body
    assert "skippedStaleMapEntities" in usage_body
    assert "[STALE_MAP_FILTER] AuditEntityUsage skipped" in usage_body
    assert "pcall(Testing.GetEntityFilePath, entityName)" in usage_body
    assert "EnsureCleanLobbyEntities: Testing.UnmountWorld() failed:" in ts

    # Bare CancelLogin() fallbacks guarded with type(CancelLogin) == 'function'
    for rel in ("scripts/main_v2_p2p.lua", "scripts/main_v2_botmatch.lua"):
        text = _read(rel)
        assert re.search(r"^\s+CancelLogin\(\)\s*$", text, re.MULTILINE) is None, (
            f"{rel} must not invoke bare unguarded CancelLogin()"
        )

    main_v2 = _read("scripts/main_v2.lua")
    assert "_kongorTryDoFile failed for" in main_v2

    # Mirror manifests include Wave 3A modular scripts + lib_json.lua
    expected_scripts = (
        "main_v2_p2p.lua",
        "main_v2_botmatch.lua",
        "main_v2_walkthrough.lua",
        "main_v2_audio_news.lua",
        "main_v2_scene.lua",
        "lib_json.lua",
    )
    sync_ps1 = (REPO_ROOT / "scripts" / "vfs" / "sync_client_files.ps1").read_text(encoding="utf-8")
    mirror_cs = (REPO_ROOT / "src" / "tools" / "kongor-cli" / "Mcp" / "Services" / "MirrorSyncService.cs").read_text(encoding="utf-8")
    wave7_py = (REPO_ROOT / "tests" / "python" / "test_wave7_queue_and_login_contracts.py").read_text(encoding="utf-8")

    for name in expected_scripts:
        assert name in sync_ps1, f"{name} missing from scripts/vfs/sync_client_files.ps1"
        assert name in mirror_cs, f"{name} missing from MirrorSyncService.cs"
        assert name in wave7_py, f"{name} missing from test_wave7_queue_and_login_contracts.py MIRROR_MATRIX"


def test_shj010_p0_02_open_create_preserves_online_identity_and_creates_bot_difficulty_cvar(tmp_path: pathlib.Path):
    """SHJ-010 P0-02: OpenCreate() preserves authenticated online account identity (zero synthetic
    AccountInfo/ChatUpdateName clobbering for [GUCI]guccidonut), still synthesizes identity in offline
    hub mode, and pre-creates bot_difficulty CVar before Set('bot_difficulty', ...)."""
    lua_test = r"""
local triggers = {}
local cvars = {
    svr_login = '[GUCI]guccidonut',
    login_name = '[GUCI]guccidonut',
    p2p_nickname = '',
    ui_canCreateGame = 'true',
    kongor_offline_hub = 'false',
    p2p_standalone = 'false',
    p2p_mode = 'false',
}
local createdCvars = {}
local missingSetErrors = {}
local onlineLoggedIn = true
local accountName = '[GUCI]guccidonut'

_G.Echo = function() end
_G.Empty = function(s) return s == nil or s == '' end
_G.NotEmpty = function(s) return not _G.Empty(s) end
_G.AtoB = function(v) return v == true or v == 1 or v == '1' or v == 'true' end
_G.EscapeString = function(s) return tostring(s or '') end
_G.Translate = function(k) return tostring(k or '') end
_G.GetDBEntry = function(_, defaultVal) return defaultVal end
_G.IsLoggedIn = function() return onlineLoggedIn end
_G.GetAccountName = function() return accountName end
_G.GetCvarBool = function(name)
    local v = cvars[name]
    return v == true or v == 'true' or v == '1'
end
_G.GetCvarString = function(name)
    return tostring(cvars[name] or '')
end
_G.GetCvarInt = function(name)
    return tonumber(cvars[name]) or 0
end
_G.Cvar = {
    GetCvar = function(name)
        if cvars[name] ~= nil then
            return { GetString = function() return tostring(cvars[name]) end }
        end
        return nil
    end,
    CreateCvar = function(name, ctype, defaultVal)
        createdCvars[name] = { ctype = ctype, defaultVal = defaultVal }
        if cvars[name] == nil then
            cvars[name] = tostring(defaultVal or '')
        end
    end,
}
_G.Set = function(name, val)
    if cvars[name] == nil then
        table.insert(missingSetErrors, tostring(name))
    end
    cvars[name] = tostring(val)
end
_G.Trigger = function(eventName, ...)
    table.insert(triggers, { name = eventName, args = { ... } })
end

local widgets = {}
local function makeWidget(name)
    local w = {
        _name = name,
        _visible = false,
        _inputLine = '',
        _selected = nil,
        GetName = function(self) return self._name end,
        SetVisible = function(self, v) self._visible = not not v end,
        IsVisible = function(self) return self._visible end,
        FadeIn = function(self) self._visible = true end,
        FadeOut = function(self) self._visible = false end,
        SetInputLine = function(self, s) self._inputLine = tostring(s or '') end,
        GetValue = function(self) return self._inputLine end,
        SetSelectedItemByValue = function(self, v) self._selected = v end,
        ClearItems = function() end,
        AddTemplateListItem = function() end,
        SetEnabled = function() end,
        SetButtonState = function() end,
        GetButtonState = function() return 0 end,
        SetText = function() end,
        SetTexture = function() end,
        RegisterWatch = function() end,
        Sleep = function() end,
    }
    widgets[name] = w
    return w
end

_G.GetWidget = function(name)
    return widgets[name] or makeWidget(name)
end
_G.object = {
    GetName = function() return 'main' end,
    GetWidget = function(_, name) return _G.GetWidget(name) end,
    RegisterWatch = function() end,
    UICmd = function() return '' end,
    Sleep = function() end,
}
_G.UIManager = {
    GetInterface = function(name)
        if name == 'main' then return _G.object end
        return nil
    end,
}
_G.Kongor_SetSavedBotDifficulty = function(d)
    _G.Set('bot_difficulty', tostring(d))
end

dofile('games/client/override/ui/scripts/newui/player_hosted_v2.lua')

assert(createdCvars['bot_difficulty'] ~= nil, 'bot_difficulty CVar must be created on module load/init')

-- 1. Online authenticated [GUCI]guccidonut: zero synthetic AccountInfo / ChatUpdateName triggers!
triggers = {}
Player_Hosted_V2:OpenCreate()
for _, t in ipairs(triggers) do
    assert(t.name ~= 'AccountInfo', 'Online OpenCreate must NEVER fire synthetic AccountInfo')
    assert(t.name ~= 'ChatUpdateName', 'Online OpenCreate must NEVER fire synthetic ChatUpdateName')
end
assert(cvars['login_name'] == '[GUCI]guccidonut', 'Online OpenCreate must not clobber login_name')
assert(cvars['p2p_nickname'] == 'guccidonut', 'Expected sanitized p2p_nickname=guccidonut, got ' .. tostring(cvars['p2p_nickname']))
assert(widgets['hosted_game_create_detail_gamename']._inputLine == "guccidonut's Game",
    'Expected default game name "guccidonut\'s Game", got ' .. tostring(widgets['hosted_game_create_detail_gamename']._inputLine))

Player_Hosted_V2:SetBotDifficulty(3)
assert(#missingSetErrors == 0, 'Expected zero Set() calls on uncreated CVars, got: ' .. table.concat(missingSetErrors, ', '))

-- 2. Offline Hub session: synthetic AccountInfo + ChatUpdateName MUST still execute
onlineLoggedIn = false
accountName = 'UnnamedNewbie'
cvars['kongor_offline_hub'] = 'true'
cvars['p2p_nickname'] = 'OfflineTester'
cvars['login_name'] = ''
triggers = {}
Player_Hosted_V2:OpenCreate()

local accountInfoCount = 0
local chatUpdateCount = 0
for _, t in ipairs(triggers) do
    if t.name == 'AccountInfo' then
        accountInfoCount = accountInfoCount + 1
        assert(t.args[1] == 1 and t.args[2] == 'OfflineTester' and t.args[3] == 5)
    elseif t.name == 'ChatUpdateName' then
        chatUpdateCount = chatUpdateCount + 1
        assert(t.args[1] == 'OfflineTester')
    end
end
assert(accountInfoCount == 1, 'Offline OpenCreate must fire 1 synthetic AccountInfo, got ' .. tostring(accountInfoCount))
assert(chatUpdateCount == 1, 'Offline OpenCreate must fire 1 synthetic ChatUpdateName, got ' .. tostring(chatUpdateCount))

print('SHJ010_P0_02_OK')
"""
    stdout = _run_lua_verifier(tmp_path, "test_shj010_p0_02.lua", lua_test)
    assert "SHJ010_P0_02_OK" in stdout


def test_shj010_p0_03_return_to_login_screen_keeps_loginbox_visible_and_suppresses_autologin(tmp_path: pathlib.Path):
    """SHJ-010 P0-03: ReturnToLoginScreen() sets ui_suppressAutoLogin=true, resets logout screen latches
    before dispatching LoginStatus('loggedout'), keeps SetColor('0 0 0 0') strictly on
    prelogin_loginbox_disabled_login, and leaves prelogin_loginbox visible at (44.0h, -10.0h)."""
    for iface_rel in ("fe2/main_v2.interface", "fe2/main.interface"):
        iface = _read(iface_rel)
        assert 'name="prelogin_loginbox"' in iface
        assert 'x="44.0h" y="-10.0h"' in iface
        dis_match = re.search(r'<panel[^>]*name="prelogin_loginbox_disabled_login"[^>]*>', iface)
        assert dis_match is not None and 'color="invisible"' in dis_match.group(0)

    lua_test = r"""
local cvars = {
    kongor_offline_hub = 'true',
    p2p_mode = 'true',
    p2p_standalone = 'false',
    p2p_auto_host = 'true',
    p2p_auto_connect = 'false',
    ['cl_' .. 'pass' .. 'word'] = 'p2p',
    _loggedin = 'true',
    ui_isLoggedIn = 'true',
}
local logoutTriggerSnapshot = nil

_G.Echo = function() end
_G.Empty = function(s) return s == nil or s == '' end
_G.NotEmpty = function(s) return not _G.Empty(s) end
_G.AtoB = function(v) return v == true or v == 1 or v == '1' or v == 'true' end
_G.EscapeString = function(s) return tostring(s or '') end
_G.Translate = function(k) return tostring(k or '') end
_G.GetDBEntry = function(_, defaultVal) return defaultVal end
_G.IsLoggedIn = function() return false end
_G.GetAccountName = function() return 'Player' end
_G.GetCvarBool = function(name)
    local v = cvars[name]
    return v == true or v == 'true' or v == '1'
end
_G.GetCvarString = function(name)
    return tostring(cvars[name] or '')
end
_G.Cvar = {
    GetCvar = function(name)
        if cvars[name] ~= nil then return { GetString = function() return tostring(cvars[name]) end } end
        return nil
    end,
    CreateCvar = function(name, _, defaultVal)
        if cvars[name] == nil then cvars[name] = tostring(defaultVal or '') end
    end,
}
_G.Set = function(name, val)
    cvars[name] = tostring(val)
end

local widgets = {}
local function makeWidget(name)
    local w = {
        _name = name,
        _visible = false,
        _colors = {},
        _fadeIns = 0,
        _slides = {},
        _x = nil,
        _y = nil,
        SetVisible = function(self, v) self._visible = not not v end,
        IsVisible = function(self) return self._visible end,
        SetColor = function(self, c) table.insert(self._colors, tostring(c)) end,
        FadeIn = function(self) self._fadeIns = self._fadeIns + 1; self._visible = true end,
        FadeOut = function(self) self._visible = false end,
        SlideY = function(self, y, ms) table.insert(self._slides, { y = y, ms = ms }); self._y = y end,
        SetX = function(self, x) self._x = x end,
        SetY = function(self, y) self._y = y end,
        SetModel = function() end,
        SetAnim = function() end,
        RegisterWatch = function() end,
        Sleep = function() end,
    }
    widgets[name] = w
    return w
end

_G.GetWidget = function(name)
    return widgets[name] or makeWidget(name)
end
_G.object = {
    GetName = function() return 'main' end,
    GetWidget = function(_, name) return _G.GetWidget(name) end,
    RegisterWatch = function() end,
    UICmd = function() return '' end,
    Sleep = function() end,
}
_G.UIManager = {
    GetInterface = function(name)
        if name == 'main' then return _G.object end
        return nil
    end,
}
_G.HoN_Main = { isFullyLoggedIn = true }
_G._loggedin = true
_G.isLoggedIn = true
_G._logoutScreenApplied = true
_G._alreadyOnLoginScreen = true

_G.Trigger = function(eventName, status, _, _, _, isLoggedInChanged)
    if eventName == 'LoginStatus' and status == 'loggedout' then
        logoutTriggerSnapshot = {
            suppressAutoLogin = cvars['ui_suppressAutoLogin'],
            logoutAppliedBefore = _G._logoutScreenApplied,
            alreadyOnLoginBefore = _G._alreadyOnLoginScreen,
            wasLoggedInBefore = (_G.isLoggedIn == true or _G._loggedin == true or _G.HoN_Main.isFullyLoggedIn == true),
            isLoggedInChanged = isLoggedInChanged,
        }
        -- Simulate event_com DoEventN(0) sliding prelogin_loginbox toward 0h
        local box = _G.GetWidget('prelogin_loginbox')
        box:SlideY('0h', 250)
    end
end

dofile('games/client/override/ui/scripts/newui/player_hosted_v2.lua')
Player_Hosted_V2:ReturnToLoginScreen()

assert(logoutTriggerSnapshot ~= nil, 'Expected LoginStatus(loggedout) trigger')
assert(logoutTriggerSnapshot.suppressAutoLogin == 'true', 'ui_suppressAutoLogin must be true BEFORE LoginStatus trigger')
assert(logoutTriggerSnapshot.logoutAppliedBefore == false, '_logoutScreenApplied must be reset before LoginStatus trigger')
assert(logoutTriggerSnapshot.alreadyOnLoginBefore == false, '_alreadyOnLoginScreen must be reset before LoginStatus trigger')
assert(logoutTriggerSnapshot.wasLoggedInBefore == true, 'wasLoggedIn state must still be true when LoginStatus(loggedout) fires so LoggedOut() runs event_com:DoEventN(0)')
assert(logoutTriggerSnapshot.isLoggedInChanged == 1, 'Expected isLoggedInChanged=1 on LoginStatus trigger')

assert(cvars['ui_suppressAutoLogin'] == 'true')
assert(cvars['kongor_offline_hub'] == 'false')
assert(_G.isLoggedIn == false and _G._loggedin == false and _G.HoN_Main.isFullyLoggedIn == false)

local boxDis = widgets['prelogin_loginbox_disabled_login']
local box = widgets['prelogin_loginbox']
local boxCont = widgets['prelogin_loginbox_contents']
local mainLogo = widgets['main_logo']

assert(#boxDis._colors > 0, 'prelogin_loginbox_disabled_login must receive SetColor("0 0 0 0")')
for _, c in ipairs(boxDis._colors) do
    assert(c == '0 0 0 0', 'prelogin_loginbox_disabled_login must never receive non-transparent SetColor: ' .. tostring(c))
end
assert(#box._colors == 0, 'prelogin_loginbox must NEVER receive SetColor() calls (got ' .. tostring(#box._colors) .. ')')
assert(#boxCont._colors == 0, 'prelogin_loginbox_contents must NEVER receive SetColor() calls (got ' .. tostring(#boxCont._colors) .. ')')
assert(mainLogo._colors[1] == '1 1 1 1', 'Textured logo widgets must receive SetColor("1 1 1 1")')

assert(box:IsVisible() == true and box._fadeIns >= 1, 'prelogin_loginbox must be visible and faded in')
assert(box._x == '44.0h' and box._y == '-10.0h', 'prelogin_loginbox must rest at canonical XML (44.0h, -10.0h)')
assert(boxCont:IsVisible() == true and boxCont._fadeIns >= 1, 'prelogin_loginbox_contents must be visible and faded in')

print('SHJ010_P0_03_OK')
"""
    stdout = _run_lua_verifier(tmp_path, "test_shj010_p0_03.lua", lua_test)
    assert "SHJ010_P0_03_OK" in stdout


def test_shj010_p1_03_and_p1_04_homepage_and_player_stats_cold_boot_and_zero_fake_stats(tmp_path: pathlib.Path):
    """SHJ-010 P1-03 & P1-04: homepage_v2.lua survives cold-boot 'no active client' from IsStaff(),
    homepage_v2.package initializes labels to 0, and online 0-match accounts never display 1248 / X 84..X 998."""
    homepage_pkg = _read("fe2/newui/homepage_v2.package")
    for banned in ('content="1248"', 'content="X 84"', 'content="X 62"', 'content="X 37"', 'content="X 499"', 'content="X 998"', 'content="145000/213000"'):
        assert banned not in homepage_pkg, f"Fake demo placeholder {banned} must be removed from homepage_v2.package"
    assert 'name="homepage_seleInfo_bottom_{icon}_value"' in homepage_pkg and 'content="0"' in homepage_pkg
    assert 'name="homepage_seleInfo_mvp_{id}_text"' in homepage_pkg and 'content="X 0"' in homepage_pkg
    assert 'name="homepage_selfInfo_levelpercent" width="0%"' in homepage_pkg
    assert 'name="homepage_selfInfo_levelexp"' in homepage_pkg and 'content="0/0"' in homepage_pkg

    pstats = _read("scripts/newui/player_stats_v2.lua")
    assert "tonumber(_mvpScore) or 84" not in pstats
    assert "local mvpScore = tonumber(_mvpScore) or 0" in pstats
    assert "local function IsOfflineStatsFallbackAllowed()" in pstats
    assert 'if not IsOfflineStatsFallbackAllowed() then' in pstats

    lua_test = r"""
local widgets = {}
local function makeWidget(name)
    local w = {
        _name = name,
        _text = '-',
        _width = '0%',
        _tex = '',
        _visible = true,
        SetText = function(self, s) self._text = tostring(s or '') end,
        GetText = function(self) return self._text end,
        SetWidth = function(self, s) self._width = tostring(s or '') end,
        GetStringWidth = function() return '80i' end,
        SetTexture = function(self, t) self._tex = tostring(t or '') end,
        GetTexture = function(self) return self._tex end,
        SetAvatar = function() end,
        SetFont = function() end,
        SetColor = function() end,
        SetGlow = function() end,
        SetGlowColor = function() end,
        SetBackgroundGlow = function() end,
        SetOutlineColor = function() end,
        SetVisible = function(self, v) self._visible = not not v end,
        SetEffect = function() end,
        SetCallback = function() end,
        RegisterWatch = function() end,
    }
    widgets[name] = w
    return w
end

local cvars = {
    svr_login = 'guccidonut',
    login_name = '[GUCI]guccidonut',
    kongor_offline_hub = 'false',
    ui_masteryDemoPresets = 'false',
    ui_isStaff = 'false',
    cl_GarenaEnable = 'true',
}
local onlineCookie = ''
local isLoggedIn = false
local accountName = 'UnnamedNewbie'

_G.Echo = function() end
_G.Empty = function(s) return s == nil or s == '' end
_G.NotEmpty = function(s) return not _G.Empty(s) end
_G.Translate = function(k, _, v)
    if k == 'newui_playerstats_level' then return 'Level ' .. tostring(v) end
    return tostring(k or '')
end
_G.StripClanTag = function(s)
    return (tostring(s or ''):gsub('^%[[^%]]*%]', ''))
end
_G.explode = function(sep, str)
    local out = {}
    for part in string.gmatch(tostring(str or ''), '[^' .. sep .. ']+') do
        table.insert(out, part)
    end
    return out
end
_G.GetWidget = function(name) return widgets[name] or makeWidget(name) end
_G.GetCvarBool = function(name) return cvars[name] == 'true' end
_G.GetCvarString = function(name) return tostring(cvars[name] or '') end
_G.GetAccountName = function() return accountName end
_G.GetAccountID = function() return 42 end
_G.GetCookie = function() return onlineCookie end
_G.IsLoggedIn = function() return isLoggedIn end
_G.GetChatNameColorStringFromUpgrades = function() return '' end
_G.GetChatNameColorFontFromUpgrades = function() return '' end
_G.GetChatNameGlowFromUpgrades = function() return false end
_G.GetChatNameGlowColorStringFromUpgrades = function() return '' end
_G.GetChatNameBackgroundGlowFromUpgrades = function() return false end
_G.GetChatSymbolTexturePathFromUpgrades = function() return '' end
_G.GetChatNameColorTexturePathFromUpgrades = function() return '' end
_G.GetAccountIconTexturePathFromUpgrades = function() return '' end
_G.RequestPlayerSimpleStats = function() end

-- Simulate cold-boot C++ exception where both IsStaff() and Client.IsStaff() throw "no active client"
_G.IsStaff = function() error('no active client') end
_G.Client = {
    IsStaff = function() error('no active client') end,
    GetCookie = function() return onlineCookie end,
    GetAccountID = function() return 42 end,
}

_G.object = {
    GetName = function() return 'main' end,
    RegisterWatch = function() end,
    Sleep = function() end,
    UICmd = function(_, cmd)
        if string.find(cmd, 'GetAccountExperienceForLevel', 1, true) then return '0' end
        if string.find(cmd, 'GetAccountExperienceForNextLevel', 1, true) then return '1000' end
        if string.find(cmd, 'GetAccountPercentNextLevel', 1, true) then return '0' end
        return '0'
    end,
}

-- 1. Cold boot load of homepage_v2.lua must NOT throw "no active client" and must populate 0 stats (never 1248!)
dofile('games/client/override/ui/scripts/newui/homepage_v2.lua')

assert(widgets['homepage_seleInfo_bottom_matches_value']._text == '0',
    'Cold-boot matches must be 0, got ' .. tostring(widgets['homepage_seleInfo_bottom_matches_value']._text))
assert(widgets['homepage_seleInfo_bottom_heroes_value']._text == '0',
    'Cold-boot heroes must be 0, got ' .. tostring(widgets['homepage_seleInfo_bottom_heroes_value']._text))
assert(widgets['homepage_seleInfo_bottom_avatars_value']._text == '0',
    'Cold-boot avatars must be 0, got ' .. tostring(widgets['homepage_seleInfo_bottom_avatars_value']._text))
for i = 1, 5 do
    assert(widgets['homepage_seleInfo_mvp_' .. i .. '_text']._text == 'X 0',
        'Cold-boot mvp award ' .. i .. ' must be X 0, got ' .. tostring(widgets['homepage_seleInfo_mvp_' .. i .. '_text']._text))
end

-- 2. Online 0-match server payload for [GUCI]guccidonut must render real 0s and never get clobbered by PopulateDefaultSimpleStats
onlineCookie = 'session_cookie_abc'
isLoggedIn = true
accountName = '[GUCI]guccidonut'
Homepage_V2:ApplyPlayerSimpleStats({
    nickname = '[GUCI]guccidonut',
    account_id = 777,
    level = 1,
    level_exp = 0,
    heroes_num = 139,
    avatars_num = 12,
    matches_num = 0,
    mvp_num = 0,
    selected_upgrades = '',
    _fromServer = true,
})
assert(widgets['homepage_seleInfo_bottom_matches_value']._text == '0')
assert(widgets['homepage_seleInfo_bottom_heroes_value']._text == '139')
assert(widgets['homepage_seleInfo_bottom_avatars_value']._text == '12')
assert(widgets['homepage_seleInfo_mvp_1_text']._text == 'X 0')

-- Subsequent PopulateDefaultSimpleStats() on panel re-show must NOT overwrite server-fetched heroes/avatars!
Homepage_V2:PopulateDefaultSimpleStats()
assert(widgets['homepage_seleInfo_bottom_heroes_value']._text == '139',
    'PopulateDefaultSimpleStats must not clobber server-fetched stats for same online account')
assert(widgets['homepage_seleInfo_bottom_avatars_value']._text == '12')
assert(widgets['homepage_seleInfo_bottom_matches_value']._text == '0')

print('SHJ010_P1_03_04_OK')
"""
    stdout = _run_lua_verifier(tmp_path, "test_shj010_p1_03_04.lua", lua_test)
    assert "SHJ010_P1_03_04_OK" in stdout


def test_shj010_p2_01_botmap_combobox_dedup_label_and_fade_alt7_material(tmp_path: pathlib.Path):
    """SHJ-010 P2-01a & P2-01b: botmap_combobox uses dedicated clipped template + concise 'Mid Wars (10v10)'
    label without duplicate population, and fade alt7 level_3 material3.material uses $invis cube sampler."""
    map_pkg = _read("fe2/newui/mapselection_v2.package")
    assert 'name="map_selection_botmap_combobox_item_template"' in map_pkg
    assert '<label x="{x=8i}" y="{y=}" width="158i"' in map_pkg
    assert 'truncate="true"' in map_pkg
    assert 'widgetname="map_selection_large_item_{index}_botmap_combobox" width="206i"' in map_pkg
    assert 'widgetname="map_selection_small_item_{index}_botmap_combobox" width="195i"' in map_pkg
    assert 'itemheight="24i" maxlistheight="4" itemtemplate="map_selection_botmap_combobox_item_template"' in map_pkg

    mat3_path = REPO_ROOT / "games" / "client" / "override" / "heroes" / "fade" / "alt7" / "level_3" / "material3.material"
    mat3 = mat3_path.read_text(encoding="utf-8")
    assert "/world/sky/gold/1.tga" not in mat3, "Broken cube texture /world/sky/gold/1.tga must be removed from material3.material"
    cube_samplers = re.findall(r'<samplercube\s+name="cube"\s+texture="([^"]+)"', mat3)
    assert cube_samplers == ["$invis", "$invis"], f"Expected both cube samplers to use $invis, got {cube_samplers}"

    lua_test = r"""
local echoLines = {}
local cvars = { host_language = 'en', cl_GarenaEnable = 'false' }
local createdCvars = {}

_G.Echo = function(s) table.insert(echoLines, tostring(s)) end
_G.Empty = function(s) return s == nil or s == '' end
_G.NotEmpty = function(s) return not _G.Empty(s) end
_G.Translate = function(k) return tostring(k or '') end
_G.GetCvarBool = function(k) return cvars[k] == 'true' end
_G.GetCvarString = function(k) return tostring(cvars[k] or '') end
_G.GetDBEntry = function(_, def) return def end
_G.Cvar = {
    GetCvar = function(name)
        if cvars[name] ~= nil then return { GetString = function() return tostring(cvars[name]) end } end
        return nil
    end,
    CreateCvar = function(name, _, def)
        createdCvars[name] = def
        cvars[name] = tostring(def or '')
    end,
}
_G.Kongor_SetSavedBotDifficulty = function(d)
    assert(cvars['bot_difficulty'] ~= nil, 'bot_difficulty must exist before Kongor_SetSavedBotDifficulty')
    cvars['bot_difficulty'] = tostring(d)
end

_G.GetWidget = function() return nil end
_G.object = {
    GetName = function() return 'main' end,
    RegisterWatch = function() end,
    UICmd = function() return '' end,
    Sleep = function() end,
}

dofile('games/client/override/ui/scripts/newui/mapselection_v2.lua')

assert(MapSelection_V2:GetBotMapDisplayLabel('midwars_10v10') == 'Mid Wars (10v10)',
    'Expected Mid Wars (10v10), got ' .. tostring(MapSelection_V2:GetBotMapDisplayLabel('midwars_10v10')))
assert(MapSelection_V2:GetBotMapDisplayLabel('devowars_10v10') == 'Devo Wars (10v10)',
    'Expected Devo Wars (10v10), got ' .. tostring(MapSelection_V2:GetBotMapDisplayLabel('devowars_10v10')))

local clearCount = 0
local items = {}
local combo = {
    GetName = function() return 'map_selection_large_item_1_botmap_combobox' end,
    ClearItems = function() clearCount = clearCount + 1; items = {} end,
    AddTemplateListItem = function(_, tmpl, id, _, label)
        table.insert(items, { tmpl = tmpl, id = id, label = label })
    end,
}

local n1 = MapSelection_V2:PopulateBotMapCombobox(combo)
local n2 = MapSelection_V2:PopulateBotMapCombobox(combo)
assert(n1 == #MapSelection_V2.BOT_MAP_CATALOG and n2 == n1)
assert(clearCount == 1, 'Repeated PopulateBotMapCombobox on same widget must deduplicate ClearItems, got ' .. tostring(clearCount))
assert(items[1].tmpl == 'map_selection_botmap_combobox_item_template')

-- Force=true repopulates when explicitly requested
MapSelection_V2:PopulateBotMapCombobox(combo, true)
assert(clearCount == 2, 'PopulateBotMapCombobox(..., true) must force repopulation')

MapSelection_V2:PersistSharedBotDifficulty(1)
assert(createdCvars['bot_difficulty'] == 'normal')

print('SHJ010_P2_01_OK')
"""
    stdout = _run_lua_verifier(tmp_path, "test_shj010_p2_01.lua", lua_test)
    assert "SHJ010_P2_01_OK" in stdout

