# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Offline Contract & VFS Parity Suite for Track 7.3 Bot Lua Fixes (v5.0.2):
  1. Every state name in core.tSubAbilitySkipStates (bots/core.lua) must exist
     in server VFS (.entity files across games/server/override, games/client/game,
     and games/client/game/resources0.s2z), preventing 861x Unit:HasState() warning storms.
  2. bots/botbraincore.lua OpLayeredComboCast must NOT call nonexistent K2 Lua bindings
     (GetTargetType / HasActionFlag) and must dispatch via core.ResolveComboAbilityAction
     backed by core.tVfsAbilityActionCatalog.
  3. Cross-tree mirrors of bots/core.lua and bots/botbraincore.lua must stay byte-identical.
"""

from __future__ import annotations

import hashlib
import re
import zipfile
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
pytestmark = pytest.mark.skipif(
    not (REPO_ROOT / "games" / "client").is_dir(),
    reason="Requires local HoN games/ overlay (excluded from cleanroom PKV export)",
)


def _collect_vfs_state_names() -> set[str]:
    state_re = re.compile(r'<(?:active)?state\b[^>]*\bname="([^"]+)"', re.DOTALL)
    states: set[str] = set()

    for root_rel in ("games/server/override", "games/client/override", "games/client/game"):
        root = REPO_ROOT / root_rel
        if not root.is_dir():
            continue
        for ent in root.rglob("*.entity"):
            try:
                text = ent.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for m in state_re.finditer(text):
                states.add(m.group(1))

    s2z_dir = REPO_ROOT / "games/server/game"
    if not s2z_dir.is_dir():
        s2z_dir = REPO_ROOT / "games/client/game"
    for s2z_path in sorted(s2z_dir.glob("resources*.s2z")):
        with zipfile.ZipFile(s2z_path, "r") as zf:
            for info in zf.infolist():
                if not info.filename.lower().endswith(".entity"):
                    continue
                data = zf.read(info.filename).decode("utf-8", errors="ignore")
                for m in state_re.finditer(data):
                    states.add(m.group(1))

    return states


def test_sub_ability_skip_states_all_exist_in_vfs() -> None:
    core_path = REPO_ROOT / "games/client/override/bots/core.lua"
    core_text = core_path.read_text(encoding="utf-8")

    block_match = re.search(
        r"core\.tSubAbilitySkipStates\s*=\s*\{(.*?)\n\}",
        core_text,
        re.DOTALL,
    )
    assert block_match is not None, "Could not locate core.tSubAbilitySkipStates in bots/core.lua"

    skip_states = sorted(set(re.findall(r'"((?:Reborn_)?State_[^"]+)"', block_match.group(1))))
    assert len(skip_states) >= 15, f"Expected >= 15 skip states in core.tSubAbilitySkipStates, got {len(skip_states)}"

    vfs_states = _collect_vfs_state_names()
    assert len(vfs_states) >= 500, f"Expected >= 500 VFS states discovered, got {len(vfs_states)}"

    missing = sorted(st for st in skip_states if st not in vfs_states)
    assert not missing, (
        f"Nonexistent State_* entries in core.tSubAbilitySkipStates will spam Unit:HasState() warnings: {missing}. "
        "Fix: replace/remove missing state names in games/client/override/bots/core.lua and run scripts/vfs/sync_override_parity.ps1"
    )


def test_combo_cast_uses_vfs_ability_action_catalog_not_nonexistent_bindings() -> None:
    core_text = (REPO_ROOT / "games/client/override/bots/core.lua").read_text(encoding="utf-8")
    brain_text = (REPO_ROOT / "games/client/override/bots/botbraincore.lua").read_text(encoding="utf-8")

    assert "GetTargetType" not in brain_text, (
        "bots/botbraincore.lua must NOT call nonexistent ability:GetTargetType() "
        "(0 bindings in game_shared_x64.dll; causes 939x OrderAbility 'invalid ability' warnings)"
    )
    assert "HasActionFlag" not in brain_text, (
        "bots/botbraincore.lua must NOT call nonexistent ability:HasActionFlag()"
    )
    assert "core.ResolveComboAbilityAction" in brain_text, (
        "OpLayeredComboCast must dispatch via core.ResolveComboAbilityAction"
    )
    assert "core.tVfsAbilityActionCatalog" in core_text, (
        "bots/core.lua must define core.tVfsAbilityActionCatalog"
    )
    catalog_entries = re.findall(r'\b((?:Reborn_)?Ability_\w+)\s*=\s*"([^"]+)"', core_text)
    assert len(catalog_entries) >= 950, (
        f"Expected >= 950 VFS ability action catalog entries in bots/core.lua, got {len(catalog_entries)}"
    )


def test_bot_lua_cross_tree_mirror_parity() -> None:
    for rel_suffix in ("bots/core.lua", "bots/botbraincore.lua"):
        mirrors = [
            REPO_ROOT / "games/client/override" / rel_suffix,
            REPO_ROOT / "games/client/game" / rel_suffix,
            REPO_ROOT / "games/server/override" / rel_suffix,
        ]
        hashes = {}
        for m in mirrors:
            if m.is_file():
                hashes[str(m.relative_to(REPO_ROOT))] = hashlib.sha256(m.read_bytes()).hexdigest()
        assert len(hashes) >= 2
        assert len(set(hashes.values())) == 1, f"Bot Lua mirror drift detected for {rel_suffix}: {hashes}"
