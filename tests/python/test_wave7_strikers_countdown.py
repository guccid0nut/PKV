# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Offline Contract & Headless Lua VM Suite for Wave 7:
  - Issue 5  : Strikers (soccer) 7.0s post-goal reset + server-authoritative 3/2/1
               kickoff countdown + pre-match opening kickoff parity + spectator UI.
  - Issue 3E : 1s Spawn [NEXUS_TELEMETRY] deduplication across game_new.lua &
               alt_info_hero.lua with mixed hero tokens (Hero_Gladiator / The Gladiator / gladiator).

Value-Weighted Testing Gate:
  protects   : Strikers 7.0s reset pacing (3.0s celebration + 4.0s camera/countdown),
               unobstructed noclick=1 2D HUD countdown, audio/visual parity for players
               and spectators, pre-match -3/-2/-1/0 opening kickoff parity, offline
               fallback yield on server countdown_3, 1s [NEXUS_TELEMETRY] spawn dedup,
               and byte-identical cross-tree mirrors.
  fails_when : State_Soccer_Respawn remains 10000ms; State_Soccer_Camera lacks
               impactinterval/maxintervals/accumulator guards or omits countdown_3..go;
               UI embeds <modelpanel> or lacks noclick=1; countdown audio/texture paths
               drift; prematch non-prematch ticks leak; spawn emits >1 telemetry line
               within 1s; or mirrors drift across override/game/ClientPackageFiles/verify_lua/shared.
  seam       : Parses real .entity and .interface XML via xml.etree.ElementTree,
               compiles Lua via native luac51.exe, and executes real game_new.lua,
               alt_info_hero.lua, and game_spectator.interface inline Lua inside LuaVerifier.exe.
"""

from __future__ import annotations

import hashlib
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
pytestmark = pytest.mark.skipif(
    not (REPO_ROOT / "games" / "client").is_dir(),
    reason="Requires local HoN games/ overlay (excluded from cleanroom PKV export)",
)


def _resolve_luac51() -> Path:
    candidates = [
        REPO_ROOT / "native/build/luac51_build/Release/luac51.exe",
        REPO_ROOT / "native/tools/luac51/build/Release/luac51.exe",
        REPO_ROOT / "artifacts/native-binaries/windows-x64/luac51.exe",
        REPO_ROOT / ".tools/bin/lua/luac51.exe",
    ]
    for cand in candidates:
        if cand.is_file():
            return cand
    pytest.fail(
        "luac51.exe compiler not found. "
        "Fix/diagnose: run powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_client_lua.ps1"
    )


def _resolve_lua_verifier() -> Path:
    candidates = [
        REPO_ROOT / "src/tools/lua-verifier/bin/Release/net11.0/LuaVerifier.exe",
        REPO_ROOT / "src/tools/lua-verifier/bin/Debug/net11.0/LuaVerifier.exe",
    ]
    for cand in candidates:
        if cand.is_file():
            return cand
    pytest.fail(
        "LuaVerifier.exe not found. "
        "Fix/diagnose: run powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 "
        "-Owner H3 -- dotnet build src/tools/lua-verifier/LuaVerifier.csproj -c Release"
    )


def _run_lua_script(lua_code: str) -> str:
    verifier = _resolve_lua_verifier()
    with tempfile.NamedTemporaryFile("w", suffix=".lua", delete=False, encoding="utf-8") as tf:
        tf.write(lua_code)
        tmp_path = Path(tf.name)
    try:
        proc = subprocess.run(
            [str(verifier), str(tmp_path)],
            cwd=str(REPO_ROOT),
            capture_output=True,
            text=True,
            timeout=30,
        )
    finally:
        tmp_path.unlink(missing_ok=True)
    combined = (proc.stdout or "") + "\n" + (proc.stderr or "")
    if proc.returncode != 0:
        pytest.fail(f"LuaVerifier failed (rc={proc.returncode}):\n{combined}")
    return combined


def test_entity_7s_post_goal_timeline_and_camera_countdown() -> None:
    ball_path = REPO_ROOT / "games/server/override/triggers/custom_maps/soccer/ball/ball.entity"
    cam_path = REPO_ROOT / "games/server/override/triggers/custom_maps/soccer/states/camera_resetdelay.entity"
    tp_legion_path = REPO_ROOT / "games/server/override/triggers/custom_maps/soccer/states/teleport_legionwithball.entity"
    tp_hell_path = REPO_ROOT / "games/server/override/triggers/custom_maps/soccer/states/teleport_hellbournewithball.entity"

    ball_root = ET.parse(ball_path).getroot()
    respawn_states = [
        el for el in ball_root.iter("applystate") if el.attrib.get("name") == "State_Soccer_Respawn"
    ]
    assert len(respawn_states) == 4, f"Expected 4 State_Soccer_Respawn entries in ball.entity, got {len(respawn_states)}"
    for st in respawn_states:
        assert st.attrib.get("duration") == "7000", f"Expected 7000ms State_Soccer_Respawn, got {st.attrib}"

    # Verify 10th winning goal branch stays untouched (State_Soccer_Teleport_All duration=9000)
    win_teleports = [
        el for el in ball_root.iter("applystate") if el.attrib.get("name") == "State_Soccer_Teleport_All"
    ]
    assert len(win_teleports) == 2
    for wt in win_teleports:
        assert wt.attrib.get("duration") == "9000"

    # Verify teleport states apply State_Soccer_Camera duration=4000 to this_owner_entity
    for tp_path in (tp_legion_path, tp_hell_path):
        tp_root = ET.parse(tp_path).getroot()
        onexp = tp_root.find("onexpired")
        assert onexp is not None, f"{tp_path.name} missing <onexpired>"
        cam_apps = [
            el for el in onexp.findall("applystate") if el.attrib.get("name") == "State_Soccer_Camera"
        ]
        assert len(cam_apps) == 1, f"{tp_path.name} must apply State_Soccer_Camera once outside <foreachplayer>"
        assert cam_apps[0].attrib.get("duration") == "4000"
        assert cam_apps[0].attrib.get("target") == "this_owner_entity"
        assert cam_apps[0].attrib.get("source") == "this_owner_entity"

    # Verify camera_resetdelay.entity (State_Soccer_Camera)
    cam_root = ET.parse(cam_path).getroot()
    assert cam_root.attrib.get("name") == "State_Soccer_Camera"
    assert cam_root.attrib.get("impactinterval") == "1000"
    assert cam_root.attrib.get("maxintervals") == "3"

    oninf = cam_root.find("oninflict")
    assert oninf is not None
    setacc = oninf.find("setaccumulator")
    assert setacc is not None and setacc.attrib.get("value") == "0"

    onimp = cam_root.find("onimpact")
    assert onimp is not None
    chacc = onimp.find("changeaccumulator")
    assert chacc is not None and chacc.attrib.get("b") == "1" and chacc.attrib.get("op") == "add"

    impact_triggers = []
    for fep in onimp.iter("foreachplayer"):
        assert fep.attrib.get("includespectators") == "true"
        for ct in fep.findall("clientuitrigger"):
            if ct.attrib.get("name") == "EventSoccerGoal":
                impact_triggers.append(ct.attrib.get("param"))
    assert impact_triggers == ["countdown_3", "countdown_2", "countdown_1"], f"Unexpected impact triggers: {impact_triggers}"

    onexp_cam = cam_root.find("onexpired")
    assert onexp_cam is not None
    fep_exp = onexp_cam.find("foreachplayer")
    assert fep_exp is not None and fep_exp.attrib.get("includespectators") == "true"
    setcam = fep_exp.find("setcamera")
    assert setcam is not None and setcam.attrib.get("camera") == "" and setcam.attrib.get("entity") == "target_entity"
    go_trigs = [
        ct.attrib.get("param")
        for ct in fep_exp.findall("clientuitrigger")
        if ct.attrib.get("name") == "EventSoccerGoal"
    ]
    assert go_trigs == ["countdown_go"], f"Expected countdown_go on <onexpired>, got {go_trigs}"

    # Verify team_deathmatch camera_resetdelay.entity was never polluted
    tdm_cam = REPO_ROOT / "games/client/game/triggers/custom_maps/team_deathmatch/camera_resetdelay.entity"
    if tdm_cam.is_file():
        assert "EventSoccerGoal" not in tdm_cam.read_text(encoding="utf-8")


def test_ui_interfaces_countdown_overlay_and_noclick_budget() -> None:
    iface_paths = [
        REPO_ROOT / "games/client/override/ui/game_apex.interface",
        REPO_ROOT / "games/client/override/ui/game_legacy.interface",
        REPO_ROOT / "games/client/override/ui/game_spectator.interface",
    ]
    for path in iface_paths:
        root = ET.parse(path).getroot()
        triggers = [t.attrib.get("name") for t in root.findall("trigger")]
        if path.name == "game_spectator.interface":
            assert "EventSoccerGoal" not in triggers, (
                "game_spectator.interface must NOT redeclare <trigger name='EventSoccerGoal' /> "
                "(causes duplicate-trigger console error when game_apex/game_legacy is loaded); "
                "use idempotent LuaTrigger.CreateCustomTrigger + interface:RegisterWatch instead"
            )
        else:
            assert "EventSoccerGoal" in triggers, f"{path.name} missing <trigger name='EventSoccerGoal' />"

        panels = [p for p in root.iter("panel") if p.attrib.get("name") == "soccer_kickoff_countdown_panel"]
        assert len(panels) == 1, f"{path.name} must have exactly one soccer_kickoff_countdown_panel"
        panel = panels[0]
        assert panel.attrib.get("y") == "14.5h"
        assert panel.attrib.get("visible") == "0"
        assert panel.attrib.get("noclick") == "1"
        assert panel.attrib.get("passivechildren") == "1"
        assert list(panel.iter("modelpanel")) == [], f"{path.name} must NOT use <modelpanel> for countdown overlay"

        for child in panel.iter():
            assert child.attrib.get("noclick") == "1", f"Non-noclick child <{child.tag}> in {path.name}"

        imgs = [i for i in panel.iter("image") if i.attrib.get("name") == "soccer_kickoff_countdown_number"]
        assert len(imgs) == 1
        assert imgs[0].attrib.get("texture") == "/ui/elements/number_large_Soccer_3.tga"

        lbls = [l for l in panel.iter("label") if l.attrib.get("name") == "soccer_kickoff_countdown_label"]
        assert len(lbls) == 1 and lbls[0].attrib.get("content") == "KICKOFF"

        glows = [g for g in panel.iter("frame") if g.attrib.get("name") == "soccer_kickoff_countdown_glow"]
        assert len(glows) == 1

    # Verify game_spectator preserved replay POV autolocker & camera lockers and avoids self.* userdata indexing
    spec_text = (REPO_ROOT / "games/client/override/ui/game_spectator.interface").read_text(encoding="utf-8")
    assert 'name="specui_pov_autolocker"' in spec_text
    assert 'name="specui_camera_locker_0"' in spec_text
    assert 'name="specui_camera_locker"' in spec_text
    assert "SpecSoccerCountdown" in spec_text
    assert "self.ShowStep" not in spec_text and "self.cdSeq" not in spec_text

    # Verify soccer_mode unnamed.material stub exists and is valid XML <material>
    mat_path = REPO_ROOT / "games/client/override/ui/common/models/soccer_mode/unnamed.material"
    assert mat_path.is_file(), "Missing ui/common/models/soccer_mode/unnamed.material stub"
    assert ET.parse(mat_path).getroot().tag == "material"


def test_luac51_bytecode_compilation() -> None:
    luac = _resolve_luac51()
    for rel in (
        "games/client/override/ui/scripts/game_new.lua",
        "games/client/override/ui/scripts/alt_info_hero.lua",
    ):
        proc = subprocess.run([str(luac), "-p", str(REPO_ROOT / rel)], capture_output=True, text=True, timeout=15)
        assert proc.returncode == 0, f"luac51 failed on {rel}: {proc.stderr}"


def test_lua_vm_strikers_countdown_server_prematch_and_fallback() -> None:
    # Also extract inline Lua from game_spectator.interface so we exercise real XML Lua too
    spec_root = ET.parse(REPO_ROOT / "games/client/override/ui/game_spectator.interface").getroot()
    spec_panel = next(p for p in spec_root.iter("panel") if p.attrib.get("name") == "soccer_kickoff_countdown_panel")
    spec_onload = spec_panel.attrib["onloadlua"]
    spec_match = spec_panel.attrib["ontrigger0lua"]

    lua_script = f"""
local _hostClock = os and os.clock
rawset(_G, "os", nil)
rawset(_G, "io", nil)
rawset(_G, "package", nil)

local echoes = {{}}
local sounds = {{}}
local watches = {{}}
local pendingSleeps = {{}}
local nowMs = 10000
local activeMap = "soccer"
local cvars = {{}}

rawset(_G, "Echo", function(msg) table.insert(echoes, tostring(msg)) end)
rawset(_G, "println", function(msg) table.insert(echoes, tostring(msg)) end)
local _origPrint = print
rawset(_G, "PlaySound", function(path) table.insert(sounds, tostring(path)) end)
rawset(_G, "HostTime", function() return nowMs end)
rawset(_G, "GetTime", function() return nowMs end)
rawset(_G, "GetMap", function() return activeMap end)
rawset(_G, "GetCurrentMapName", function() return activeMap end)
rawset(_G, "GetAccountName", function() return "TestStriker" end)
rawset(_G, "GetSelectedHero", function() return "Hero_Gladiator" end)
rawset(_G, "GetViewHeroName", function() return "Hero_Gladiator" end)
rawset(_G, "GetCvarString", function(k) return cvars[k] or "" end)
rawset(_G, "GetCvarBool", function(k) return cvars[k] == true or cvars[k] == "true" or cvars[k] == "1" end)
rawset(_G, "GetCvarBoolMem", function(k) return cvars[k] == true end)
rawset(_G, "GetCvarInt", function(k) return tonumber(cvars[k]) or 0 end)
rawset(_G, "GetCvarNumber", function(k) return tonumber(cvars[k]) end)
rawset(_G, "Set", function(k, v) cvars[k] = v end)
rawset(_G, "SetSave", function(k, v) cvars[k] = v end)
rawset(_G, "Cmd", function() end)
rawset(_G, "Trigger", function() end)
rawset(_G, "Action", function() end)
rawset(_G, "Translate", function(k) return k end)
rawset(_G, "AtoB", function(v) return v == true or v == "true" or v == "1" or v == 1 end)
rawset(_G, "AtoN", function(v) return tonumber(v) or 0 end)
rawset(_G, "FtoP", function(v) return tostring((tonumber(v) or 0) * 100) .. "%" end)
rawset(_G, "ToPercent", function(v) return tostring((tonumber(v) or 0) * 100) .. "%" end)
rawset(_G, "NotEmpty", function(v) return v ~= nil and v ~= "" end)
rawset(_G, "round", function(v) return math.floor((tonumber(v) or 0) + 0.5) end)
rawset(_G, "RegisterScript2", function() end)
rawset(_G, "GetTimeOfDawn", function() return 0 end)
rawset(_G, "ViewingReplay", function() return false end)
rawset(_G, "ViewingStreaming", function() return false end)
rawset(_G, "IsInQueue", function() return false end)
rawset(_G, "SetCancelCallback", function() end)
rawset(_G, "Cvar", {{
    GetCvar = function(name)
        if cvars[name] == nil then return nil end
        return {{
            GetString = function() return tostring(cvars[name]) end,
            GetNumber = function() return tonumber(cvars[name]) or 0 end,
            GetBoolean = function() return cvars[name] == true or cvars[name] == "true" or cvars[name] == "1" end,
            Set = function(_, val) cvars[name] = val end,
        }}
    end,
    CreateCvar = function(name, _, def)
        if cvars[name] == nil then cvars[name] = def end
        return _G.Cvar.GetCvar(name)
    end
}})

local widgets = {{}}
local function makeWidget(name)
    local w = {{
        _name = name,
        _visible = false,
        _alpha = 1.0,
        _texture = "",
        _text = "",
        _width = "",
        _height = "",
        _scales = {{}},
        _fadeIns = 0,
        _fadeOuts = 0,
    }}
    function w:GetName() return self._name end
    function w:IsValid() return true end
    function w:SetVisible(v) self._visible = (v == true or v == 1) end
    function w:IsVisible() return self._visible end
    function w:SetAlpha(a) self._alpha = a end
    function w:SetTexture(t) self._texture = t end
    function w:SetText(t) self._text = tostring(t) end
    function w:GetText() return self._text end
    function w:SetWidth(val) self._width = val end
    function w:SetHeight(val) self._height = val end
    function w:GetHeight() return 100 end
    function w:GetWidth() return 100 end
    function w:SetColor() end
    function w:SetBorderColor() end
    function w:SetRotation() end
    function w:SetY() end
    function w:Scale(tw, th, ms) table.insert(self._scales, {{tw, th, ms}}) end
    function w:FadeIn() self._fadeIns = self._fadeIns + 1 end
    function w:FadeOut() self._fadeOuts = self._fadeOuts + 1 end
    function w:UICmd() end
    function w:SetCallback() end
    function w:RegisterWatch(n, fn) watches[n] = watches[n] or {{}}; table.insert(watches[n], fn) end
    function w:UnregisterWatch() end
    function w:Sleep(ms, fn) table.insert(pendingSleeps, {{ due = nowMs + ms, fn = fn, host = self._name }}) end
    widgets[name] = w
    return w
end

for _, wname in ipairs({{
    "soccer_kickoff_countdown_panel",
    "soccer_kickoff_countdown_number",
    "soccer_kickoff_countdown_label",
    "soccer_kickoff_countdown_glow",
    "game_top_soccer",
    "lod_spec",
    "game_arcade_model_4",
    "game_arcade_model_2",
    "game_center_portrait_button",
    "game_center_portrait_icon",
    "game_scoreboard_popup",
    "soccer_time",
    "game_match_time_label",
    "game_clock_face",
    "game_team_score_1",
    "game_team_score_2",
    "soccer_team_score1",
    "soccer_team_score2",
    "KillBoard",
    "game_top_left_hero_icon",
    "game_top_left_hero_icon_bg",
    "game_top_left_respawn_timer",
    "game_top_left_health_lerp",
    "game_top_left_health_bar",
    "game_top_left_health_label",
    "game_top_left_mana_lerp",
    "game_top_left_mana_bar",
    "game_top_left_mana_label",
    "game_top_left_hero_lvlup",
    "game_top_left_hero_level_label",
    "game_hero_tip_parent",
    "game_hero_tip_info_parent",
}}) do
    makeWidget(wname)
end

local interfaceObj = {{
    GetName = function() return "game" end,
    GetWidget = function(_, n) return widgets[n] or makeWidget(n) end,
    RegisterWatch = function(_, n, fn) watches[n] = watches[n] or {{}}; table.insert(watches[n], fn) end,
    UICmd = function() end,
    Sleep = function(_, ms, fn) table.insert(pendingSleeps, {{ due = nowMs + ms, fn = fn, host = "interface" }}) end,
}}
rawset(_G, "object", interfaceObj)
rawset(_G, "GetWidget", function(n) return widgets[n] or makeWidget(n) end)
rawset(_G, "UIManager", {{ GetInterface = function() return interfaceObj end }})

local function fireWatch(name, ...)
    for _, fn in ipairs(watches[name] or {{}}) do
        fn(interfaceObj, ...)
    end
end

local function advanceTo(targetMs)
    while true do
        local bestIdx = nil
        local bestDue = targetMs + 1
        for i, s in ipairs(pendingSleeps) do
            -- Simulate K2 Engine invariant: hidden widgets/children inside hidden panels do not tick :Sleep()
            local hiddenHost = (s.host == "soccer_kickoff_countdown_label" or s.host == "soccer_kickoff_countdown_panel")
                and not widgets["soccer_kickoff_countdown_panel"]:IsVisible()
            if (not hiddenHost) and s.due <= targetMs and s.due < bestDue then
                bestDue = s.due
                bestIdx = i
            end
        end
        if not bestIdx then
            nowMs = targetMs
            break
        end
        local item = table.remove(pendingSleeps, bestIdx)
        nowMs = item.due
        item.fn()
    end
end

dofile("games/client/override/ui/scripts/game_new.lua")
Game:InitializeGameInterface()

local p = widgets["soccer_kickoff_countdown_panel"]
local img = widgets["soccer_kickoff_countdown_number"]
local lbl = widgets["soccer_kickoff_countdown_label"]

-- 1. Server-authoritative post-goal countdown cancels offline fallback
sounds = {{}}
fireWatch("EventSoccerGoal", "1")
assert(not p:IsVisible(), "panel must stay hidden at T=0 goal celebration")
advanceTo(13990) -- T=3990ms (just before offline fallback fires at +4000ms)
assert(not p:IsVisible(), "panel must stay hidden before 4000ms")
sounds = {{}}
fireWatch("EventSoccerGoal", "countdown_3")
assert(p:IsVisible(), "panel must show on countdown_3")
assert(img._texture == "/ui/elements/number_large_Soccer_3.tga", "expected 3.tga, got " .. tostring(img._texture))
assert(lbl._text == "KICKOFF")
assert(#img._scales >= 1 and img._scales[#img._scales][1] == "9.0h", "pop scale animation must run")
assert(#sounds == 2 and sounds[1]:find("announcer/3.wav", 1, true) and sounds[2]:find("button_clock.wav", 1, true))
advanceTo(14050) -- Ensure armed fallback at +4000ms yields cleanly without double-playing audio
assert(#sounds == 2, "yielded fallback must not replay step 3 audio")

advanceTo(15000)
sounds = {{}}
fireWatch("EventSoccerGoal", "countdown_2")
assert(img._texture == "/ui/elements/number_large_Soccer_2.tga")
assert(#sounds == 2 and sounds[1]:find("announcer/2.wav", 1, true))

advanceTo(16000)
sounds = {{}}
fireWatch("EventSoccerGoal", "countdown_1")
assert(img._texture == "/ui/elements/number_large_Soccer_1.tga")
assert(#sounds == 2 and sounds[1]:find("announcer/1.wav", 1, true))

advanceTo(17000)
sounds = {{}}
fireWatch("EventSoccerGoal", "countdown_go")
advanceTo(17200)
assert(not p:IsVisible(), "panel must hide after countdown_go")
assert(#sounds == 0, "no extra sounds on countdown_go or superseded fallback")

-- 2. Simulation-time legacy server fallback runs at +4600/+5600/+6600/+7600ms of MatchTime (NOT wall-clock :Sleep)
--    AND a 3.5s client wall-clock hitch on Goal #1 before server 'reset' NEVER double-triggers 3-2-1!
nowMs = 30000
pendingSleeps = {{}}
sounds = {{}}
Game._strikersServerDrivesCountdown = false
fireWatch("MatchTime", 500000, false) -- soccer clock at 500.0s remaining
fireWatch("EventSoccerGoal", "2")
-- Simulate 3500ms client wall-clock shader/entity hitch while server sim only advanced 2900ms:
advanceTo(35000)
fireWatch("MatchTime", 497100, false)
assert(not p:IsVisible(), "3.5s wall-clock hitch at 2.9s simElapsed must NOT prematurely fire fallback")
-- Now server 'reset' arrives at +3000ms simElapsed -> immediately cancels fallback and locks serverDrives=true
fireWatch("EventSoccerGoal", "reset")
assert(Game._strikersServerDrivesCountdown == true and Game._strikersCountdownFallbackPending == false,
    "server 'reset' must cancel fallback and mark _strikersServerDrivesCountdown=true")
sounds = {{}}
fireWatch("MatchTime", 494000, false)
assert(not p:IsVisible() and #sounds == 0, "cancelled fallback must stay silent after server reset")

-- Now test pure legacy server (no reset / no countdown_3) using simulation-time MatchTime progression:
nowMs = 40000
pendingSleeps = {{}}
sounds = {{}}
Game._strikersServerDrivesCountdown = false
fireWatch("MatchTime", 480000, false)
fireWatch("EventSoccerGoal", "2")
fireWatch("MatchTime", 475500, false) -- +4500ms simElapsed
assert(not p:IsVisible(), "legacy sim-time fallback must not show before +4600ms")
nowMs = 41000
fireWatch("MatchTime", 475400, false) -- +4600ms simElapsed (counting down!)
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_3.tga", "fallback +4600ms simElapsed must show 3")
assert(#sounds == 2)
nowMs = 42000
fireWatch("MatchTime", 474400, false) -- +5600ms simElapsed
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_2.tga", "fallback +5600ms simElapsed must show 2")
nowMs = 43000
fireWatch("MatchTime", 473400, false) -- +6600ms simElapsed
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_1.tga", "fallback +6600ms simElapsed must show 1")
nowMs = 44000
fireWatch("MatchTime", 472400, false) -- +7600ms simElapsed
advanceTo(44200)
assert(not p:IsVisible(), "fallback +7600ms simElapsed must hide panel cleanly")

-- 3. Pre-match opening kickoff parity (-3, -2, -1, 0) & non-prematch guard
nowMs = 50000
pendingSleeps = {{}}
sounds = {{}}
Game.lastMatchTime = -1
fireWatch("MatchTime", 15000, true)
assert(not p:IsVisible(), "15s prematch must not show countdown")
fireWatch("MatchTime", 3000, true)
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_3.tga")
nowMs = 51000
fireWatch("MatchTime", 2000, true)
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_2.tga")
nowMs = 52000
fireWatch("MatchTime", 1000, true)
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_1.tga")
nowMs = 53000
fireWatch("MatchTime", 0, false)
advanceTo(53200)
assert(not p:IsVisible(), "prematch kickoff transition must hide panel")

-- Gameplay clock at 0:01..0:03 (isPreMatch=false) MUST NOT trigger countdown
nowMs = 60000
sounds = {{}}
fireWatch("MatchTime", 3000, false)
assert(not p:IsVisible() and #sounds == 0, "active match 0:03 must NOT trigger prematch countdown")

-- Negative prematch clock (-3s..-1s..0s) & server-late arrival (10ms after fallback)
nowMs = 62000
pendingSleeps = {{}}
sounds = {{}}
Game.lastMatchTime = -999
fireWatch("MatchTime", -3000, true)
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_3.tga", "negative prematch -3000ms must show 3")
nowMs = 63000
fireWatch("MatchTime", -2000, true)
assert(img._texture == "/ui/elements/number_large_Soccer_2.tga")
nowMs = 64000
fireWatch("MatchTime", -1000, true)
assert(img._texture == "/ui/elements/number_large_Soccer_1.tga")
nowMs = 65000
fireWatch("MatchTime", 0, true)
advanceTo(65200)
assert(not p:IsVisible(), "negative prematch 0ms must hide panel")

nowMs = 66000
pendingSleeps = {{}}
sounds = {{}}
Game._strikersServerDrivesCountdown = false
fireWatch("MatchTime", 100000, false)
fireWatch("EventSoccerGoal", "1")
fireWatch("MatchTime", 104600, false) -- fallback +4600ms simElapsed fires first
assert(p:IsVisible() and img._texture == "/ui/elements/number_large_Soccer_3.tga" and #sounds == 2)
nowMs = 66010
fireWatch("EventSoccerGoal", "countdown_3") -- server arrives 10ms later -> cancels +5600/+6600/+7600 and dedups step 3 audio
assert(#sounds == 2, "late server countdown_3 within 450ms must not double-play audio")
nowMs = 67000
fireWatch("MatchTime", 105800, false)
assert(#sounds == 2, "cancelled fallback +5600ms must not play audio")
fireWatch("EventSoccerGoal", "countdown_go")
advanceTo(67400)
assert(not p:IsVisible())

-- 4. Spectator inline XML Lua execution on strict K2 userdata-like widget (forbids self.* writes)
local _compile = loadstring or load
local rawSpecPanel = makeWidget("soccer_kickoff_countdown_panel")
local specPanel = setmetatable({{}}, {{
    __index = function(_, k)
        local v = rawSpecPanel[k]
        if type(v) == "function" then
            return function(_, ...) return v(rawSpecPanel, ...) end
        end
        return v
    end,
    __newindex = function(_, k)
        error("attempt to index local 'self' (a userdata value) key=" .. tostring(k))
    end
}})
local specWatches = {{}}
local specIface = {{
    GetName = function() return "game_spectator" end,
    GetWidget = function(_, n) return n == "soccer_kickoff_countdown_panel" and specPanel or (widgets[n] or makeWidget(n)) end,
    RegisterWatch = function(_, n, fn) specWatches[n] = specWatches[n] or {{}}; table.insert(specWatches[n], fn) end,
    UICmd = function() end,
    Sleep = function(_, ms, fn) table.insert(pendingSleeps, {{ due = nowMs + ms, fn = fn, host = "spec_interface" }}) end,
}}
local function fireSpecGoal(param)
    for _, fn in ipairs(specWatches["EventSoccerGoal"] or {{}}) do
        fn(specIface, param)
    end
end
local specOnloadFn = assert(_compile("local self, interface = ...; " .. {spec_onload!r}))
local specMatchFn = assert(_compile("local self, interface, param0, param1 = ...; " .. {spec_match!r}))

specOnloadFn(specPanel, specIface)
nowMs = 70000
sounds = {{}}
fireSpecGoal("countdown_3")
assert(specPanel:IsVisible() and widgets["soccer_kickoff_countdown_number"]._texture == "/ui/elements/number_large_Soccer_3.tga")
assert(#sounds == 2)
nowMs = 71000
fireSpecGoal("countdown_go")
advanceTo(71200)
assert(not specPanel:IsVisible())

nowMs = 80000
specMatchFn(specPanel, specIface, 2000, true)
assert(specPanel:IsVisible() and widgets["soccer_kickoff_countdown_number"]._texture == "/ui/elements/number_large_Soccer_2.tga")
nowMs = 81000
specMatchFn(specPanel, specIface, 0, false)
advanceTo(81200)
assert(not specPanel:IsVisible())

-- Spectator simulation-time fallback when serverDrives is false
_G.SpecSoccerCountdown.serverDrives = false
nowMs = 90000
pendingSleeps = {{}}
sounds = {{}}
specMatchFn(specPanel, specIface, 300000, false)
fireSpecGoal("2")
specMatchFn(specPanel, specIface, 295400, false) -- +4600ms simElapsed
assert(specPanel:IsVisible() and widgets["soccer_kickoff_countdown_number"]._texture == "/ui/elements/number_large_Soccer_3.tga")
nowMs = 93000
specMatchFn(specPanel, specIface, 292400, false) -- +7600ms simElapsed
advanceTo(93200)
assert(not specPanel:IsVisible())

print("STRIKERS_COUNTDOWN_VM_OK")
"""
    out = _run_lua_script(lua_script)
    assert "STRIKERS_COUNTDOWN_VM_OK" in out


def test_lua_vm_nexus_telemetry_spawn_dedup() -> None:
    lua_script = r"""
rawset(_G, "os", nil)
rawset(_G, "io", nil)
rawset(_G, "package", nil)

local echoes = {}
local watchesGame = {}
local watchesAlt = {}
local nowMs = 100
local cvars = {}
local selHero = "Hero_Gladiator"
local viewHero = "Hero_Gladiator"

rawset(_G, "Echo", function(msg) table.insert(echoes, tostring(msg)) end)
rawset(_G, "println", function() end)
rawset(_G, "PlaySound", function() end)
rawset(_G, "HostTime", function() return nowMs end)
rawset(_G, "GetTime", function() return nowMs end)
rawset(_G, "GetMap", function() return "caldavar" end)
rawset(_G, "GetCurrentMapName", function() return "caldavar" end)
rawset(_G, "GetAccountName", function() return "TestPlayer" end)
rawset(_G, "GetSelectedHero", function() return selHero end)
rawset(_G, "GetViewHeroName", function() return viewHero end)
rawset(_G, "GetCvarString", function(k) return cvars[k] or "" end)
rawset(_G, "GetCvarBool", function(k) return cvars[k] == true end)
rawset(_G, "GetCvarBoolMem", function(k) return cvars[k] == true end)
rawset(_G, "GetCvarInt", function(k) return tonumber(cvars[k]) or 0 end)
rawset(_G, "GetCvarNumber", function(k) return tonumber(cvars[k]) end)
rawset(_G, "IsCvarEnabled", function() return false end)
rawset(_G, "Set", function(k, v) cvars[k] = v end)
rawset(_G, "SetSave", function(k, v) cvars[k] = v end)
rawset(_G, "Cmd", function() end)
rawset(_G, "Trigger", function() end)
rawset(_G, "Action", function() end)
rawset(_G, "Translate", function(k) return k end)
rawset(_G, "AtoB", function(v) return v == true or v == "true" or v == "1" or v == 1 end)
rawset(_G, "AtoN", function(v) return tonumber(v) or 0 end)
rawset(_G, "FtoP", function(v) return tostring((tonumber(v) or 0) * 100) .. "%" end)
rawset(_G, "ToPercent", function(v) return tostring((tonumber(v) or 0) * 100) .. "%" end)
rawset(_G, "NotEmpty", function(v) return v ~= nil and v ~= "" end)
rawset(_G, "round", function(v) return math.floor((tonumber(v) or 0) + 0.5) end)
rawset(_G, "RegisterScript2", function() end)
rawset(_G, "GetScreenHeight", function() return 1080 end)
rawset(_G, "ViewingReplay", function() return false end)
rawset(_G, "ViewingStreaming", function() return false end)
rawset(_G, "IsInQueue", function() return false end)
rawset(_G, "SetCancelCallback", function() end)
rawset(_G, "Cvar", {
    GetCvar = function(name)
        if cvars[name] == nil then return nil end
        return {
            GetString = function() return tostring(cvars[name]) end,
            GetNumber = function() return tonumber(cvars[name]) or 0 end,
            GetBoolean = function() return cvars[name] == true or cvars[name] == "true" or cvars[name] == "1" end,
            Set = function(_, val) cvars[name] = val end,
        }
    end,
    CreateCvar = function(name, _, def)
        if cvars[name] == nil then cvars[name] = def end
        return _G.Cvar.GetCvar(name)
    end
})

local function makeWidget(name)
    local w = {}
    function w:IsValid() return true end
    function w:SetVisible() end
    function w:IsVisible() return false end
    function w:SetAlpha() end
    function w:SetTexture() end
    function w:SetText() return end
    function w:GetText() return "" end
    function w:SetWidth() end
    function w:SetHeight() end
    function w:SetX() end
    function w:SetY() end
    function w:GetWidth() return 100 end
    function w:GetHeight() return 20 end
    function w:SetUScale() end
    function w:RecalculateSize() end
    function w:SetColor() end
    function w:SetBorderColor() end
    function w:SetRotation() end
    function w:UICmd() end
    function w:SetCallback() end
    function w:RegisterWatch() end
    function w:UnregisterWatch() end
    function w:Sleep() end
    return w
end
local sharedW = makeWidget("shared")
local gameIface = {
    GetName = function() return "game" end,
    GetWidget = function() return sharedW end,
    RegisterWatch = function(_, n, fn) watchesGame[n] = watchesGame[n] or {}; table.insert(watchesGame[n], fn) end,
    UICmd = function() end,
    Sleep = function() end,
}
local altIface = {
    GetName = function() return "alt_info_hero" end,
    GetWidget = function() return sharedW end,
    RegisterWatch = function(_, n, fn) watchesAlt[n] = watchesAlt[n] or {}; table.insert(watchesAlt[n], fn) end,
    UICmd = function() end,
}

rawset(_G, "GetWidget", function() return sharedW end)
rawset(_G, "UIManager", { GetInterface = function() return gameIface end })

rawset(_G, "object", gameIface)
dofile("games/client/override/ui/scripts/game_new.lua")
Game:InitializeGameInterface()

rawset(_G, "object", altIface)
dofile("games/client/override/ui/scripts/alt_info_hero.lua")
AltInfoHero.Initialize(0)

local function countTelemetry()
    local c = 0
    for _, line in ipairs(echoes) do
        if line:find("[NEXUS_TELEMETRY]", 1, true) then
            c = c + 1
        end
    end
    return c
end

-- Spawn burst across game_new + alt_info_hero with mixed hero tokens within < 1s
watchesAlt["AltInfo_HeroColor0"][1](sharedW, "1 1 0 1", "1 1 0 1", 1, 2, 0, false)
selHero = ""
viewHero = ""
nowMs = 100
watchesGame["HeroIcon"][1](sharedW, "/heroes/gladiator/icon.tga") -- emits token "gladiator"
nowMs = 120
watchesGame["HeroName"][1](sharedW, "The Gladiator")              -- deduped against "gladiator"
nowMs = 150
selHero = "Hero_Gladiator"
viewHero = "Hero_Gladiator"
watchesGame["PlayerScore"][1](sharedW, 0, 0, 0, 0, 0, 0)         -- deduped against "gladiator"
nowMs = 180
watchesAlt["AltInfo_HeroPlayer0"][1](sharedW, "TestPlayer", "The Gladiator")
watchesAlt["AltInfo_HeroLevel0"][1](sharedW, 1)                  -- deduped via shared CVar
nowMs = 200
watchesAlt["AltInfo_HeroPlayer0"][1](sharedW, "TestPlayer", "The Gladiator") -- deduped

assert(countTelemetry() == 1, "Expected exactly 1 [NEXUS_TELEMETRY] line during 1s spawn burst, got " .. tostring(countTelemetry()))

-- Level up within same second MUST emit
nowMs = 300
watchesAlt["AltInfo_HeroLevel0"][1](sharedW, 2)
assert(countTelemetry() == 2, "Level up to 2 must emit immediately, got " .. tostring(countTelemetry()))

-- Repeat of same level=2 within 1s MUST dedup
nowMs = 400
watchesAlt["AltInfo_HeroPlayer0"][1](sharedW, "TestPlayer", "The Gladiator")
assert(countTelemetry() == 2, "Duplicate level=2 within 1s must dedup, got " .. tostring(countTelemetry()))

-- After >= 1000ms elapses, emission is permitted again
nowMs = 1500
watchesGame["HeroName"][1](sharedW, "The Gladiator")
assert(countTelemetry() == 3, "Emission >= 1000ms later must succeed, got " .. tostring(countTelemetry()))

-- Hero switch within 1s MUST emit
selHero = "Hero_Pyromancer"
viewHero = "Hero_Pyromancer"
nowMs = 1600
watchesGame["HeroName"][1](sharedW, "Pyromancer")
assert(countTelemetry() == 4, "Hero switch must emit immediately, got " .. tostring(countTelemetry()))

-- Also verify HostTime() == 0 start and Cvar==nil per-VM dedup
rawset(_G, "Cvar", nil)
Game._nexusTelemetryDedup = nil
AltInfoHero._nexusTelemetryDedup = nil
nowMs = 0
local baseCount = countTelemetry()
watchesGame["HeroIcon"][1](sharedW, "/heroes/pyromancer/icon.tga")
nowMs = 200
watchesGame["HeroName"][1](sharedW, "Pyromancer")
nowMs = 300
watchesGame["PlayerScore"][1](sharedW, 0, 0, 0, 0, 0, 0)
assert(countTelemetry() == baseCount + 1, "game_new per-VM dedup without Cvar at t=0..300ms must emit 1")

nowMs = 0
AltInfoHero.levelCompare[0] = nil
watchesAlt["AltInfo_HeroPlayer0"][1](sharedW, "TestPlayer", "Pyromancer")
watchesAlt["AltInfo_HeroLevel0"][1](sharedW, 1)
nowMs = 200
watchesAlt["AltInfo_HeroPlayer0"][1](sharedW, "TestPlayer", "Pyromancer")
assert(countTelemetry() == baseCount + 2, "alt_info_hero per-VM dedup without Cvar at t=0..200ms must emit 1")

print("NEXUS_TELEMETRY_DEDUP_VM_OK")
"""
    out = _run_lua_script(lua_script)
    assert "NEXUS_TELEMETRY_DEDUP_VM_OK" in out


def test_exact_cross_tree_mirror_parity() -> None:
    mirror_groups = [
        [
            "games/server/override/triggers/custom_maps/soccer/ball/ball.entity",
            "games/client/override/triggers/custom_maps/soccer/ball/ball.entity",
            "games/client/game/triggers/custom_maps/soccer/ball/ball.entity",
        ],
        [
            "games/server/override/triggers/custom_maps/soccer/states/camera_resetdelay.entity",
            "games/client/override/triggers/custom_maps/soccer/states/camera_resetdelay.entity",
            "games/client/game/triggers/custom_maps/soccer/states/camera_resetdelay.entity",
        ],
        [
            "games/server/override/triggers/custom_maps/soccer/states/teleport_legionwithball.entity",
            "games/client/override/triggers/custom_maps/soccer/states/teleport_legionwithball.entity",
            "games/client/game/triggers/custom_maps/soccer/states/teleport_legionwithball.entity",
        ],
        [
            "games/server/override/triggers/custom_maps/soccer/states/teleport_hellbournewithball.entity",
            "games/client/override/triggers/custom_maps/soccer/states/teleport_hellbournewithball.entity",
            "games/client/game/triggers/custom_maps/soccer/states/teleport_hellbournewithball.entity",
        ],
        [
            "games/client/override/ui/game_apex.interface",
            "games/client/ClientPackageFiles/ui/game_apex.interface",
            "games/client/game/ui/game_apex.interface",
        ],
        [
            "games/client/override/ui/game_legacy.interface",
            "games/client/ClientPackageFiles/ui/game_legacy.interface",
            "games/client/game/ui/game_legacy.interface",
        ],
        [
            "games/client/override/ui/game_spectator.interface",
            "games/client/ClientPackageFiles/ui/game_spectator.interface",
            "games/client/game/ui/game_spectator.interface",
        ],
        [
            "games/client/override/ui/scripts/game_new.lua",
            "games/client/ClientPackageFiles/ui/scripts/game_new.lua",
            "games/client/game/ui/scripts/game_new.lua",
            "games/shared/ui/scripts/game_new.lua",
        ],
        [
            "games/client/override/ui/scripts/alt_info_hero.lua",
            "games/client/ClientPackageFiles/ui/scripts/alt_info_hero.lua",
            "games/client/game/ui/scripts/alt_info_hero.lua",
        ],
    ]
    for group in mirror_groups:
        hashes = {}
        for rel in group:
            p = REPO_ROOT / rel
            assert p.is_file(), f"Missing mirror file: {rel}"
            hashes[rel] = hashlib.sha256(p.read_bytes()).hexdigest()
        unique = set(hashes.values())
        assert len(unique) == 1, f"Cross-tree mirror drift detected: {hashes}"
