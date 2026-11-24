#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
verify_entity_skills_parity.py — Project Kongor Cross-Mode Entity & Skill Parity Gate

Validates all winning Client & Server .entity definitions across resources*.s2z + override/:
  1. Zero XML syntax/parse errors across all winning .entity files (client & server).
  2. Zero Client <-> Server winning .entity parity drift (CRLF-normalized).
  3. Zero Soccer ally-debuff regressions (e.g., Javaras E stale ally-slow / deleted states).
  4. Zero negative uint32 cooldown wrap patterns (a="0" b="var..." op="sub" feeding startcooldown
     without a positive floor, e.g., Empath R 49.7-day lockout).
  5. Zero Soccer mana-profit refund exploits (unconditional givemana > manacost or missing CD).
  6. Zero missing <modifier key="soccer"> or maxlevel="0" bugs on all Strikers hero slots.
  7. Zero malformed/orphan <else> blocks (consecutive <else>, <else> as first child).
  8. Zero broken entity references (applystate/spawnprojectile/spawnaffector/spawnunit)
     across Strikers heroes, Core heroes, and active Reborn (gated) heroes.

Exit codes:
  0 — All checks passed with 0 errors.
  1 — One or more invariants failed (details printed with file & source attribution).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import struct
import sys
import time
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple


REPO_ROOT = Path(__file__).resolve().parents[2]
CLIENT_GAME_DIR = REPO_ROOT / "games" / "client" / "game"
CLIENT_OVERRIDE_DIR = REPO_ROOT / "games" / "client" / "override"
SERVER_GAME_DIR = REPO_ROOT / "games" / "server" / "game"
SERVER_OVERRIDE_DIR = REPO_ROOT / "games" / "server" / "override"
CLIENT_MAPS_DIR = CLIENT_GAME_DIR / "maps"
SERVER_MAPS_DIR = SERVER_GAME_DIR / "maps"

MAP_REF_ACTION_TAGS = {
    "applystate",
    "spawnprojectile",
    "spawnaffector",
    "spawnunit",
    "spawnline",
    "spawngadget",
    "hasstate",
    "expirestate",
}

# Action tags whose 'name' attribute references another K2 entity definition.
ENTITY_REF_ACTION_TAGS = {
    "applystate",
    "spawnprojectile",
    "spawnaffector",
    "spawnunit",
    "spawnline",
    "spawngadget",
}

# Valid K2 conditional sibling tags that may precede an <else> block.
CONDITIONAL_TAGS = {
    "compare",
    "comparestrings",
    "condition",
    "targettype",
    "cantarget",
    "hasmodifier",
    "hasallmodifiers",
    "hasstate",
    "hasorder",
    "hastrait",
    "hasavatarkey",
    "hasproperty",
    "entitytype",
    "entityexists",
    "damageeffecttype",
    "currentdamageeffecttype",
    "casteffecttype",
    "chance",
    "distance",
    "areaofeffect",
    "isready",
    "isattacking",
    "iswalking",
    "ischanneling",
    "isdisjointed",
    "pushentity",
    "pushentitysearch",
    "test",
    "if",
}

# Untouched vanilla HoN 4.10.1 dead/cosmetic/prototype entity references inside base resources*.s2z.
# Reported as warnings (never silent), while ANY Soccer gameplay ref or new broken ref fails hard.
KNOWN_VANILLA_LEGACY_REFS = {
    ("heroes/chi/state_sound_starter.entity", "state_warchief_ally_spot"),
    ("heroes/sapphire/state_sound_starter.entity", "state_warchief_ally_spot"),
    ("heroes/sapphire/state_sound_starter.entity", "state_warchief_enemy_spot"),
    ("heroes/mimix/hero.entity", "state_hero_mimix_sound_initiator"),
    ("heroes/ebulus/alt/ability_02/gadget/gadget.entity", "state_ebulus_ward_attack"),
    ("heroes/ebulus/alt2/ability_02/gadget/gadget.entity", "state_ebulus_ward_attack"),
    ("heroes/ebulus/alt3/ability_02/gadget/gadget.entity", "state_ebulus_ward_attack"),
    ("heroes/prophet/ability_04/state.entity", "state_prophet_ability4_teleport"),
    ("heroes/prophet/ability_04/state.entity", "state_prophet_ability4_sight"),
    ("heroes/shaman/ability_01/pet.entity", "state_shaman_ability3_pet_speed"),
    ("triggers/custom_maps/soccer/ball/slide.entity", "soccer_sliding"),
    ("triggers/custom_maps/soccer/ball/slide.entity", "soccer_position"),
    ("triggers/custom_maps/soccer/states/goalchecker_logic.entity", "soccer_ball"),
    # Untouched vanilla HoN 4.10.1 retail <expirestate> leftover copied verbatim into colosseum_duel.s2z
    ("resources/heroes/prisoner/ability_01/projectile_return.entity", "state_prisoner_ability1_noweapon"),
}

# Tags whose root element defines a named K2 entity.
ENTITY_ROOT_TAGS = {
    "hero",
    "ability",
    "abilityattribute",
    "item",
    "state",
    "projectile",
    "affector",
    "gadget",
    "pet",
    "creep",
    "neutral",
    "building",
    "critter",
    "unit",
    "order",
    "powerup",
    "shop",
    "trigger",
    "entity",
    "game",
}

# Known non-entity tokens or dynamic expressions that can appear in K2 action 'name' attributes.
DYNAMIC_REF_RE = re.compile(r"[\{\}\$\%]|^(?:none|null|empty|this_.*|target_.*|source_.*)$", re.IGNORECASE)


@dataclass
class VfsFile:
    rel_path: str
    source: str
    raw_bytes: bytes


def _archive_sort_key(path: Path) -> Tuple[int, str]:
    m = re.search(r"(\d+)", path.stem)
    return (int(m.group(1)) if m else -1, path.name.lower())


def load_winning_entities(game_dir: Path, override_dir: Path) -> Dict[str, VfsFile]:
    """Build exact K2 VFS winning map for *.entity files (resources0..N.s2z -> override/)."""
    winning: Dict[str, VfsFile] = {}

    if game_dir.is_dir():
        archives = sorted(
            [p for p in game_dir.iterdir() if p.is_file() and re.match(r"^resources\d+\.s2z$", p.name, re.IGNORECASE)],
            key=_archive_sort_key,
        )
        for arc in archives:
            with zipfile.ZipFile(arc, "r") as zf:
                for info in zf.infolist():
                    if info.is_dir():
                        continue
                    norm = info.filename.replace("\\", "/").lstrip("/").lower()
                    if not norm.endswith(".entity"):
                        continue
                    winning[norm] = VfsFile(
                        rel_path=norm,
                        source=arc.name,
                        raw_bytes=zf.read(info),
                    )

    if override_dir.is_dir():
        for root, _, files in os.walk(override_dir):
            for fn in files:
                if not fn.lower().endswith(".entity"):
                    continue
                full = Path(root) / fn
                rel = full.relative_to(override_dir).as_posix().lower()
                winning[rel] = VfsFile(
                    rel_path=rel,
                    source=f"override/{rel}",
                    raw_bytes=full.read_bytes(),
                )

    return winning


def _normalize_text(raw: bytes) -> str:
    return raw.decode("utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")


def _parse_xml_strict(raw: bytes) -> Tuple[Optional[ET.Element], Optional[str]]:
    text = _normalize_text(raw).lstrip("\ufeff")
    try:
        return ET.fromstring(text), None
    except ET.ParseError as exc:
        return None, str(exc)


def _split_entity_ref_names(attr_val: str) -> List[str]:
    """Split valid K2 per-level comma-separated entity refs (e.g. ',State_Foo_Lv2,State_Foo_Lv3')."""
    out: List[str] = []
    for part in attr_val.split(","):
        token = part.strip()
        if not token or DYNAMIC_REF_RE.search(token):
            continue
        out.append(token)
    return out


def _collect_entity_name_index(parsed_client: Dict[str, ET.Element]) -> Tuple[Set[str], Dict[str, str]]:
    """Collect all defined K2 entity names (lowercase -> canonical) and map name -> path."""
    names: Set[str] = set()
    name_to_path: Dict[str, str] = {}
    for rel_path, root in parsed_client.items():
        nm = root.attrib.get("name", "").strip()
        if nm:
            key = nm.lower()
            names.add(key)
            name_to_path[key] = rel_path
    return names, name_to_path


def _find_soccer_modifier(root: ET.Element) -> Optional[ET.Element]:
    for mod in root.findall(".//modifier"):
        if mod.attrib.get("key", "").strip().lower() == "soccer":
            return mod
    return None


def _check_orphan_else(root: ET.Element, rel_path: str, source: str, errors: List[str]) -> None:
    """Detect malformed <else> placement (first child of parent, consecutive <else>, or inside non-branching block)."""
    for parent in root.iter():
        children = list(parent)
        for idx, child in enumerate(children):
            if child.tag.lower() != "else":
                continue
            if idx == 0:
                errors.append(
                    f"[ORPHAN_ELSE] {rel_path} ({source}): <else> is first child of <{parent.tag}> with no preceding condition."
                )
                continue
            prev_tag = children[idx - 1].tag.lower()
            if prev_tag == "else":
                errors.append(
                    f"[DOUBLE_ELSE] {rel_path} ({source}): consecutive <else> sibling inside <{parent.tag}> (second <else> is dead/invalid)."
                )
            elif prev_tag not in CONDITIONAL_TAGS and parent.tag.lower() == "cantarget":
                errors.append(
                    f"[ORPHAN_ELSE] {rel_path} ({source}): <else> inside <cantarget> preceded by non-conditional <{prev_tag}>."
                )


def _check_negative_cooldown_wrap(root: ET.Element, rel_path: str, source: str, errors: List[str]) -> None:
    """Detect negative subtraction feeding <startcooldown> without a positive max floor (uint32 49.7d wrap)."""
    for parent in root.iter():
        children = list(parent)
        saw_zero_sub_var0 = False
        for child in children:
            tag = child.tag.lower()
            if tag == "evaluate":
                a = child.attrib.get("a", "").strip()
                b = child.attrib.get("b", "").strip()
                op = child.attrib.get("op", "").strip().lower()
                if a == "0" and op == "sub" and b:
                    saw_zero_sub_var0 = True
            elif tag in ("setvar0", "setvar1", "setvar2", "setvar3"):
                a = child.attrib.get("a", "").strip()
                b = child.attrib.get("b", "").strip()
                op = child.attrib.get("op", "").strip().lower()
                if a == "0" and op == "sub" and b:
                    saw_zero_sub_var0 = True
            elif tag == "startcooldown" and saw_zero_sub_var0:
                dur = child.attrib.get("duration", "").strip().lower()
                dur_op = child.attrib.get("durationop", "").strip().lower()
                if dur in ("result", "var0", "var1") and dur_op != "max":
                    errors.append(
                        f"[NEGATIVE_CD_WRAP] {rel_path} ({source}): <startcooldown duration=\"{dur}\"> follows 0-sub without positive durationop=\"max\" floor (uint32 49.7d lockout risk)."
                    )


def _parse_first_float(val: Optional[str]) -> Optional[float]:
    if not val:
        return None
    token = val.split(",")[0].strip()
    try:
        return float(token)
    except ValueError:
        return None


def _check_soccer_mana_exploits(root: ET.Element, rel_path: str, source: str, errors: List[str]) -> None:
    """Ensure Soccer modifiers do not refund more Energy than manacost or wipe cooldown unconditionally."""
    soccer_mod = _find_soccer_modifier(root)
    if soccer_mod is None:
        return

    manacost = _parse_first_float(soccer_mod.attrib.get("manacost") or root.attrib.get("manacost"))
    if manacost is not None and manacost > 0:
        for gm in soccer_mod.findall(".//givemana"):
            target = gm.attrib.get("target", "").strip().lower()
            amt = _parse_first_float(gm.attrib.get("amount"))
            # Self-refund greater than ability manacost = infinite Energy exploit (exclude dynamic stolen-mana var0)
            if target in ("source_entity", "this_owner_entity") and amt is not None and amt > manacost:
                errors.append(
                    f"[SOCCER_MANA_EXPLOIT] {rel_path} ({source}): <modifier key=\"soccer\"> refunds {amt:g} Energy > manacost {manacost:g} (net positive Energy exploit)."
                )

    # Specific regression guard for Javaras R (Option B-1): must have 12000ms CD, 30 manacost, and zero resetcooldown
    if rel_path == "heroes/javaras/ability_04/ability.entity":
        cd = _parse_first_float(soccer_mod.attrib.get("cooldowntime"))
        if manacost != 30 or cd != 12000:
            errors.append(
                f"[SOCCER_JAVARAS_R] {rel_path} ({source}): expected manacost=30 & cooldowntime=12000 in soccer modifier, got manacost={manacost}, cooldowntime={cd}."
            )
        if soccer_mod.find(".//resetcooldown") is not None:
            errors.append(
                f"[SOCCER_JAVARAS_R] {rel_path} ({source}): unexpected <resetcooldown> in soccer modifier."
            )

    # Specific regression guard for Javaras E (Flash): must not apply State_Javaras_Ability2_Self or ally slow
    if rel_path == "heroes/javaras/ability_02/ability.entity":
        for ap in soccer_mod.findall(".//applystate"):
            nm = ap.attrib.get("name", "").strip().lower()
            if nm in ("state_javaras_ability2_self", "state_javaras_ability2"):
                errors.append(
                    f"[SOCCER_JAVARAS_E] {rel_path} ({source}): stale state '{nm}' still applied in soccer modifier."
                )

    # Specific regression guard for Flux R: Fluxpush alt blocks must guard soccer refund at <= 20
    if rel_path == "heroes/flux/ability_02/ability.entity":
        for mod in root.findall(".//modifier"):
            if mod.attrib.get("key", "").strip().lower() == "fluxpush":
                for gm in mod.findall(".//givemana"):
                    amt = _parse_first_float(gm.attrib.get("amount"))
                    if amt is not None and amt > 20:
                        # Only valid inside an <else> branch of <hasmodifier name="soccer">
                        pass
                has_soccer_guard = any(
                    hm.attrib.get("name", "").strip().lower() == "soccer"
                    for hm in mod.findall(".//hasmodifier")
                )
                if not has_soccer_guard:
                    errors.append(
                        f"[SOCCER_FLUX_R] {rel_path} ({source}): Fluxpush modifier missing <hasmodifier name=\"soccer\"> guard for 20-Energy refund."
                    )

    # Specific regression guard for Empath R: must charge 40 mana & 30000ms CD in soccer
    if rel_path == "heroes/empath/ability_04/ability.entity":
        cd = _parse_first_float(soccer_mod.attrib.get("cooldowntime"))
        if manacost != 40 or cd != 30000:
            errors.append(
                f"[SOCCER_EMPATH_R] {rel_path} ({source}): expected manacost=40 & cooldowntime=30000 in soccer modifier, got manacost={manacost}, cooldowntime={cd}."
            )


def _check_entity_refs_for_tree(
    root: ET.Element,
    rel_path: str,
    source: str,
    defined_names: Set[str],
    errors: List[str],
    warnings: List[str],
    scope_label: str,
) -> None:
    """Verify every applystate / spawnprojectile / spawnaffector / spawnunit target exists in VFS."""
    for elem in root.iter():
        tag = elem.tag.lower()
        if tag not in ENTITY_REF_ACTION_TAGS:
            continue
        raw_name = elem.attrib.get("name", "").strip()
        if not raw_name:
            continue
        for ref in _split_entity_ref_names(raw_name):
            if ref.lower() not in defined_names:
                if (rel_path, ref.lower()) in KNOWN_VANILLA_LEGACY_REFS:
                    warnings.append(
                        f"[LEGACY_VANILLA_REF:{scope_label}] {rel_path} ({source}): <{tag} name=\"{ref}\"> (known untouched HoN 4.10 ref)."
                    )
                else:
                    errors.append(
                        f"[MISSING_REF:{scope_label}] {rel_path} ({source}): <{tag} name=\"{ref}\"> target entity does not exist in winning VFS."
                    )


def _verify_no_zip64_eocd(path: Path) -> Optional[str]:
    try:
        file_size = path.stat().st_size
    except OSError as exc:
        return f"cannot stat archive: {exc}"
    if file_size < 22:
        return f"archive too small ({file_size} bytes < 22-byte EOCD)"
    if file_size >= 1_500_000_000:
        return f"archive exceeds 1.5 GB K2 32-bit zip offset safety cap ({file_size} bytes)"
    tail_len = min(file_size, 65557)
    with path.open("rb") as fh:
        fh.seek(file_size - tail_len)
        tail = fh.read(tail_len)
    if b"PK\x06\x06" in tail or b"PK\x06\x07" in tail:
        return "ZIP64 EOCD/Locator signature detected (K2 32-bit parser rejects ZIP64)"
    eocd = -1
    for i in range(len(tail) - 22, -1, -1):
        if tail[i:i + 4] == b"PK\x05\x06":
            comment_len = struct.unpack_from("<H", tail, i + 20)[0]
            if (len(tail) - i - 22) == comment_len:
                eocd = i
                break
    if eocd < 0:
        return "missing valid ZIP EOCD (PK\\x05\\x06)"
    cd_size, cd_offset = struct.unpack_from("<II", tail, eocd + 12)
    if cd_size == 0xFFFFFFFF or cd_offset == 0xFFFFFFFF:
        return "ZIP64 sentinel (0xFFFFFFFF) in EOCD"
    return None


def _verify_map_archives(
    defined_names: Set[str],
    errors: List[str],
) -> Tuple[int, int, int]:
    """Audit all maps/*.s2z archives for client/server SHA-256 parity, 32-bit ZIP safety,
    XML syntax, and missing entity references (tags + @projectile/@impactprojectile)."""
    if not CLIENT_MAPS_DIR.is_dir() or not SERVER_MAPS_DIR.is_dir():
        return 0, 0, 0

    client_maps = {p.name.lower(): p for p in sorted(CLIENT_MAPS_DIR.glob("*.s2z"))}
    server_maps = {p.name.lower(): p for p in sorted(SERVER_MAPS_DIR.glob("*.s2z"))}
    all_map_keys = sorted(set(client_maps.keys()) | set(server_maps.keys()))

    maps_checked = 0
    map_entities_checked = 0
    map_parity_diffs = 0

    for key in all_map_keys:
        cp = client_maps.get(key)
        sp = server_maps.get(key)
        if cp is None and sp is not None:
            map_parity_diffs += 1
            errors.append(f"[MAP_PARITY_MISSING_CLIENT] maps/{sp.name} exists on server but missing from client.")
            continue
        if sp is None and cp is not None:
            map_parity_diffs += 1
            errors.append(f"[MAP_PARITY_MISSING_SERVER] maps/{cp.name} exists on client but missing from server.")
            continue
        assert cp is not None and sp is not None
        maps_checked += 1

        # 1. Exact byte-for-byte SHA-256 client <-> server parity
        c_bytes = cp.read_bytes()
        s_bytes = sp.read_bytes()
        if len(c_bytes) != len(s_bytes) or hashlib.sha256(c_bytes).digest() != hashlib.sha256(s_bytes).digest():
            map_parity_diffs += 1
            errors.append(
                f"[MAP_PARITY_DRIFT] maps/{cp.name}: client ({len(c_bytes)}B) != server ({len(s_bytes)}B). "
                "Deploy a single deterministic .s2z copy to both games/server/game/maps/ and games/client/game/maps/."
            )

        # 2. Strict 32-bit ZIP / non-ZIP64 check
        z64_err = _verify_no_zip64_eocd(cp)
        if z64_err:
            errors.append(f"[MAP_ZIP64] maps/{cp.name}: {z64_err}")

        # 3. Parse all embedded .entity files and verify 0 missing references
        map_known: Set[str] = set(defined_names)
        parsed_map: Dict[str, ET.Element] = {}
        try:
            with zipfile.ZipFile(cp, "r") as zf:
                for entry in zf.namelist():
                    if not entry.lower().endswith(".entity"):
                        continue
                    map_entities_checked += 1
                    raw = zf.read(entry)
                    el, perr = _parse_xml_strict(raw)
                    if perr is not None:
                        errors.append(f"[MAP_XML_PARSE] maps/{cp.name}::{entry}: {perr}")
                        continue
                    assert el is not None
                    parsed_map[entry] = el
                    ename = el.attrib.get("name", "").strip().lower()
                    if ename:
                        map_known.add(ename)
        except zipfile.BadZipFile as exc:
            errors.append(f"[MAP_BAD_ZIP] maps/{cp.name}: {exc}")
            continue

        for entry, el in sorted(parsed_map.items()):
            for sub in el.iter():
                tag_l = sub.tag.lower()
                if tag_l in MAP_REF_ACTION_TAGS:
                    val = sub.attrib.get("name", "")
                    for tok in val.split(","):
                        t = tok.strip()
                        tl = t.lower()
                        if (
                            t
                            and not tl.startswith(("this_", "source_", "target_", "proxy_", "stack_"))
                            and "_" in t
                            and tl not in map_known
                        ):
                            if (entry.lower().replace("\\", "/"), tl) in KNOWN_VANILLA_LEGACY_REFS:
                                continue
                            errors.append(
                                f"[MAP_MISSING_REF] maps/{cp.name}::{entry}: <{sub.tag} name=\"{t}\"> does not resolve."
                            )
                for attr in ("projectile", "impactprojectile"):
                    if attr in sub.attrib:
                        for tok in sub.attrib[attr].split(","):
                            t = tok.strip()
                            tl = t.lower()
                            if t and "_" in t and tl not in map_known:
                                errors.append(
                                    f"[MAP_MISSING_REF] maps/{cp.name}::{entry}: @{attr}=\"{t}\" on <{sub.tag}> does not resolve."
                                )

    return maps_checked, map_entities_checked, map_parity_diffs


def run_verification(verbose: bool = False, include_maps: bool = True) -> Dict[str, object]:
    t0 = time.perf_counter()
    errors: List[str] = []
    warnings: List[str] = []

    client_vfs = load_winning_entities(CLIENT_GAME_DIR, CLIENT_OVERRIDE_DIR)
    server_vfs = load_winning_entities(SERVER_GAME_DIR, SERVER_OVERRIDE_DIR)

    # 1. XML parse check across all winning client + server .entity files
    parsed_client: Dict[str, ET.Element] = {}
    parsed_server: Dict[str, ET.Element] = {}

    for rel, vf in client_vfs.items():
        root, err = _parse_xml_strict(vf.raw_bytes)
        if err is not None:
            errors.append(f"[XML_PARSE:client] {rel} ({vf.source}): {err}")
        elif root is not None:
            parsed_client[rel] = root

    for rel, vf in server_vfs.items():
        root, err = _parse_xml_strict(vf.raw_bytes)
        if err is not None:
            errors.append(f"[XML_PARSE:server] {rel} ({vf.source}): {err}")
        elif root is not None:
            parsed_server[rel] = root

    # 2. Client <-> Server winning .entity parity check (CRLF-normalized)
    all_paths = sorted(set(client_vfs.keys()) | set(server_vfs.keys()))
    parity_diffs = 0
    for rel in all_paths:
        cvf = client_vfs.get(rel)
        svf = server_vfs.get(rel)
        if cvf is None:
            parity_diffs += 1
            errors.append(f"[PARITY_MISSING_CLIENT] {rel} exists in server ({svf.source}) but not client.")
            continue
        if svf is None:
            parity_diffs += 1
            errors.append(f"[PARITY_MISSING_SERVER] {rel} exists in client ({cvf.source}) but not server.")
            continue
        if _normalize_text(cvf.raw_bytes) != _normalize_text(svf.raw_bytes):
            parity_diffs += 1
            errors.append(
                f"[PARITY_DRIFT] {rel} differs between client ({cvf.source}) and server ({svf.source}). Run scripts mirror sync."
            )

    # Build entity name index across winning client VFS
    defined_names, name_to_path = _collect_entity_name_index(parsed_client)

    # Identify hero groups (Core heroes, Strikers heroes, Reborn/gated heroes)
    strikers_heroes: List[Tuple[str, ET.Element]] = []
    core_heroes: List[Tuple[str, ET.Element]] = []
    reborn_heroes: List[Tuple[str, ET.Element]] = []

    for rel, root in sorted(parsed_client.items()):
        if root.tag.lower() != "hero":
            continue
        # Only actual hero definitions under heroes/<name>/hero.entity (skip test/dev stubs)
        if not rel.startswith("heroes/") or not rel.endswith("/hero.entity"):
            continue
        hero_dir = rel.split("/")[1]
        if hero_dir in ("base", "dev", "test", "template"):
            continue

        soccer_mod = _find_soccer_modifier(root)
        if soccer_mod is not None:
            strikers_heroes.append((rel, root))

        # Distinguish active core heroes vs gated Reborn heroes
        is_reborn = rel.startswith("heroes/reborn/") or any(
            m.attrib.get("key", "").strip().lower() in ("reborn", "pbt", "gated")
            for m in root.findall(".//modifier")
        ) or ("gated" in root.attrib.get("herolist", "").lower())
        if is_reborn:
            reborn_heroes.append((rel, root))
        else:
            core_heroes.append((rel, root))

    # 3-7. Structural, Cooldown, Mana-Exploit, and Orphan-Else checks on all Soccer/Hero entities
    # Verify Ability_Soccer_SlideKick guarantees slots 2, 3, 4 auto-level on spawn across all 29 Strikers heroes.
    slidekick_path = name_to_path.get("ability_soccer_slidekick")
    if slidekick_path and slidekick_path in parsed_client:
        sk_root = parsed_client[slidekick_path]
        sk_onspawn = sk_root.find("./onspawn")
        leveled_slots = {
            el.attrib.get("slot", "").strip()
            for el in (sk_onspawn.findall(".//levelupability") if sk_onspawn is not None else [])
            if el.attrib.get("force", "").strip().lower() == "true"
        }
        for req_slot in ("2", "3", "4"):
            if req_slot not in leveled_slots:
                errors.append(
                    f"[SOCCER_AUTOLEVEL] {slidekick_path}: missing <onspawn><levelupability slot=\"{req_slot}\" force=\"true\" /> (E/R/passive may stay greyed out on spawn)."
                )

    strikers_ability_paths: Set[str] = set()
    for hero_rel, hero_root in strikers_heroes:
        soccer_mod = _find_soccer_modifier(hero_root)
        assert soccer_mod is not None
        # Check inventory0..inventory4 (Q/W/E/R + innate Passive slot 4)
        for slot_idx in range(5):
            attr = f"inventory{slot_idx}"
            ab_name = (soccer_mod.attrib.get(attr) or hero_root.attrib.get(attr) or "").strip()
            if not ab_name or ab_name.lower() in ("none", "ability_soccer_kick", "ability_soccer_sprint"):
                continue
            ab_path = name_to_path.get(ab_name.lower())
            if not ab_path:
                errors.append(
                    f"[SOCCER_MISSING_ABILITY] {hero_rel}: {attr}=\"{ab_name}\" does not resolve in VFS."
                )
                continue
            strikers_ability_paths.add(ab_path)
            ab_root = parsed_client[ab_path]
            ab_vf = client_vfs[ab_path]
            # Dedicated Soccer-only abilities under triggers/custom_maps/soccer/ do not require a nested <modifier key="soccer">.
            is_dedicated_soccer_ability = ab_path.startswith("triggers/custom_maps/soccer/")
            ab_soccer = _find_soccer_modifier(ab_root)
            if ab_soccer is None and not is_dedicated_soccer_ability:
                errors.append(
                    f"[SOCCER_MISSING_MODIFIER] {ab_path} ({ab_vf.source}): referenced by Strikers hero {hero_rel} ({attr}={ab_name}) but lacks <modifier key=\"soccer\">."
                )
            elif ab_soccer is not None:
                maxlvl = (ab_soccer.attrib.get("maxlevel") or ab_root.attrib.get("maxlevel") or "").strip()
                if maxlvl in ("", "0"):
                    errors.append(
                        f"[SOCCER_MAXLEVEL_INVALID] {ab_path} ({ab_vf.source}): <modifier key=\"soccer\"> has invalid maxlevel=\"{maxlvl}\" (ability unlearnable/unusable)."
                    )
            else:
                maxlvl = (ab_root.attrib.get("maxlevel") or "").strip()
                if maxlvl in ("", "0"):
                    errors.append(
                        f"[SOCCER_MAXLEVEL_INVALID] {ab_path} ({ab_vf.source}): dedicated Soccer ability has invalid maxlevel=\"{maxlvl}\"."
                    )

    # Check all entities under heroes/ and triggers/custom_maps/soccer/
    checked_hero_dirs = {rel.rsplit("/", 1)[0] + "/" for rel, _ in (strikers_heroes + core_heroes + reborn_heroes)}
    strikers_dirs = {rel.rsplit("/", 1)[0] + "/" for rel, _ in strikers_heroes}

    for rel, root in sorted(parsed_client.items()):
        vf = client_vfs[rel]
        is_soccer_trigger = rel.startswith("triggers/custom_maps/soccer/")
        in_strikers = any(rel.startswith(d) for d in strikers_dirs) or rel in strikers_ability_paths
        in_heroes = any(rel.startswith(d) for d in checked_hero_dirs)

        if in_strikers or is_soccer_trigger:
            _check_negative_cooldown_wrap(root, rel, vf.source, errors)
            _check_soccer_mana_exploits(root, rel, vf.source, errors)
            _check_orphan_else(root, rel, vf.source, errors)
            _check_entity_refs_for_tree(root, rel, vf.source, defined_names, errors, warnings, "strikers")
        elif in_heroes:
            _check_negative_cooldown_wrap(root, rel, vf.source, errors)
            _check_orphan_else(root, rel, vf.source, errors)
            _check_entity_refs_for_tree(root, rel, vf.source, defined_names, errors, warnings, "hero")

    maps_checked = 0
    map_entities_checked = 0
    map_parity_diffs = 0
    if include_maps:
        maps_checked, map_entities_checked, map_parity_diffs = _verify_map_archives(defined_names, errors)

    elapsed_ms = (time.perf_counter() - t0) * 1000.0
    summary = {
        "passed": len(errors) == 0,
        "elapsedMs": round(elapsed_ms, 1),
        "clientEntityCount": len(client_vfs),
        "serverEntityCount": len(server_vfs),
        "parityDiffs": parity_diffs,
        "strikersHeroCount": len(strikers_heroes),
        "strikersAbilityCount": len(strikers_ability_paths),
        "coreHeroCount": len(core_heroes),
        "rebornHeroCount": len(reborn_heroes),
        "mapsChecked": maps_checked,
        "mapEntitiesChecked": map_entities_checked,
        "mapParityDiffs": map_parity_diffs,
        "errorCount": len(errors),
        "warningCount": len(warnings),
        "errors": errors,
        "warnings": warnings,
    }
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify Project Kongor cross-mode entity & skill parity.")
    parser.add_argument("--json", action="store_true", help="Emit JSON summary to stdout.")
    parser.add_argument("--verbose", action="store_true", help="Print detailed counts and every check.")
    parser.add_argument("--skip-maps", action="store_true", help="Skip auditing maps/*.s2z archives.")
    args = parser.parse_args()

    result = run_verification(verbose=args.verbose, include_maps=not args.skip_maps)

    if args.json:
        print(json.dumps(result, indent=2))
    else:
        status = "PASS" if result["passed"] else "FAIL"
        print(
            f"[ENTITY_SKILLS_PARITY] {status} in {result['elapsedMs']} ms | "
            f"client={result['clientEntityCount']} server={result['serverEntityCount']} "
            f"parityDiffs={result['parityDiffs']} strikersHeroes={result['strikersHeroCount']} "
            f"coreHeroes={result['coreHeroCount']} rebornHeroes={result['rebornHeroCount']} "
            f"maps={result['mapsChecked']} mapEntities={result['mapEntitiesChecked']} "
            f"mapParityDiffs={result['mapParityDiffs']} errors={result['errorCount']}"
        )
        for err in result["errors"][:100]:
            print(f"  ERROR: {err}")
        if len(result["errors"]) > 100:
            print(f"  ... and {len(result['errors']) - 100} more errors.")

    return 0 if result["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
