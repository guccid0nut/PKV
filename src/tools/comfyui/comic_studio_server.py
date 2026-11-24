#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Kongor Chronicles - Interactive Story Architect, ComfyUI Panel Studio & 60 FPS Lettering Server
Serves a responsive Web Studio at http://127.0.0.1:8190 that connects to ComfyUI (127.0.0.1:8188)
and shares the exact custom node engine in `kongor_chronicles_nodes.py`.
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import logging
import os
import re
import sys
import time
import urllib.parse
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, List, Optional, Tuple

from PIL import Image, ImageDraw

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [KongorComicStudio] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
    handlers=[logging.StreamHandler(sys.stdout)],
)
logger = logging.getLogger("kongor_comic_studio")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CUSTOM_NODES_DIR = os.path.join(SCRIPT_DIR, "ComfyUI", "custom_nodes")
if CUSTOM_NODES_DIR not in sys.path:
    sys.path.insert(0, CUSTOM_NODES_DIR)
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from kongor_chronicles_nodes import (
    CHAR_BIBLE_PATH,
    COMFY_OUTPUT_DIR,
    DATA_COMICS_DIR,
    LOC_BIBLE_PATH,
    REPO_ROOT,
    STORIES_DIR,
    WWWROOT_LORE_DIR,
    KongorCharacterRefLoader,
    KongorComicLetterer,
    KongorComicPromptCompiler,
    KongorLocationRefLoader,
    KongorStoryArchitectPrompt,
    KongorStripAssembler,
    build_hero_reference_plate,
    comfy_tensor_to_pil,
    get_comic_font,
    list_all_heroes,
    list_all_locations,
    list_all_stories,
    load_character_bible,
    load_location_bible,
    pil_to_comfy_tensor,
    render_lettered_panel_pil,
)

COMFYUI_BASE_URL = "http://127.0.0.1:8188"


def check_comfyui_online() -> Dict[str, Any]:
    try:
        with urllib.request.urlopen(f"{COMFYUI_BASE_URL}/system_stats", timeout=2) as resp:
            stats = json.loads(resp.read().decode("utf-8"))
            return {"online": True, "url": COMFYUI_BASE_URL, "stats": stats}
    except Exception as ex:
        return {"online": False, "url": COMFYUI_BASE_URL, "error": str(ex)}


def generate_showrunner_pitch(
    seed_idea: str,
    pacing_mode: str,
    primary_hero: str,
    secondary_hero: str,
    location: str,
    panel_count: int,
) -> Dict[str, Any]:
    """
    Generate 3 vivid narrative twists and 4 interactive multiple-choice showrunner questions
    tailored to the selected HoN heroes, abilities, and location.
    """
    char_bible = load_character_bible().get("characters", {})
    loc_bible = load_location_bible().get("locations", {})
    h1 = char_bible.get(primary_hero, {})
    h2 = char_bible.get(secondary_hero, {}) if secondary_hero != "None" else {}
    loc = loc_bible.get(location, {})

    h1_abils = h1.get("signature_abilities", ["Signature Strike", "Ancient Instinct", "Defensive Stance"])
    h2_abils = h2.get("signature_abilities", ["Ambush Strike", "Shadow Step", "Runic Surge"])
    second_label = secondary_hero if secondary_hero != "None" else "an unseen presence in the shadows"

    twists = [
        {
            "id": "twist_a",
            "title": f"Option A: The False Trail (Slow-Burn Sensory Trap)",
            "summary": (
                f"What {primary_hero} believes is a retreating trail in {location} is actually a deliberate hunting circle. "
                f"Every ward or marker {primary_hero} inspects has been subtly tampered with to Herd them toward the center of the glade where {second_label} waits."
            ),
            "tone_tag": "High Suspense / Reversal",
        },
        {
            "id": "twist_b",
            "title": f"Option B: The Ancient Resonance (Mythic Lore Reveal)",
            "summary": (
                f"During the confrontation between {primary_hero} and {second_label} in {location}, the ground fractures to expose a dormant runic relic "
                f"that reacts to {primary_hero}'s {h1_abils[0]}, forcing both warriors to reckon with a much older primordial threat."
            ),
            "tone_tag": "Epic Dark Fantasy / Lore Deep-Dive",
        },
        {
            "id": "twist_c",
            "title": f"Option C: Duel of Split Seconds (Decompressed Tactical Climax)",
            "summary": (
                f"A dead-silent standoff in {location} where we stretch 5 seconds of real time across {panel_count} decompressed panels—"
                f"tracking heartbeats, shifting weight on wet stone, and the exact micro-second {primary_hero}'s {h1_abils[0]} collides with {second_label}'s {h2_abils[0]}."
            ),
            "tone_tag": "Cinematic Action / Show-Don't-Tell",
        },
    ]

    questions = [
        {
            "id": "q_opening",
            "question": f"1. Sensory Opening Hook (Panel 1): How should we open in {location} to pull the reader into the atmosphere?",
            "options": [
                f"Extreme macro close-up on a physical clue (smoking venom on an Ironwood leaf, a cracked rune stone, or {primary_hero}'s hand on the earth) while the forest goes dead silent",
                f"Towering wide silhouette of {location} under dramatic weather (mist, rain, or god-rays) with {primary_hero} dwarfed by the ancient architecture",
                f"In-media-res tension: Tight close-up on {primary_hero}'s eyes narrowing as they sense something wrong in the wind",
            ],
        },
        {
            "id": "q_escalation",
            "question": f"2. The Escalation Beat: How do we stretch the suspense before {second_label} makes their move?",
            "options": [
                f"Show {second_label} hidden in the background/canopy of Panel 2 before {primary_hero} realizes they are there (dramatic irony)",
                f"A sudden environmental shift—water rippling backwards, boundary wards flickering out one by one into darkness",
                f"A tense verbal exchange echoed through the fog where {second_label}'s voice comes from every direction at once",
            ],
        },
        {
            "id": "q_climax",
            "question": f"3. Signature HoN Payoff: What visual centerpiece anchors the climax panel?",
            "options": [
                f"{primary_hero} unleashing '{h1_abils[0]}' in a dramatic low-angle splash panel with high-contrast rim lighting",
                f"A kinetic clash between {primary_hero}'s '{h1_abils[0]}' and {second_label}'s '{h2_abils[0]}' with comic SFX ('SHRRK!', 'KRA-KOOM!')",
                f"An emotional/mythic turning point where {primary_hero} shields an innocent charge or awakens dormant runic power",
            ],
        },
        {
            "id": "q_ending",
            "question": "4. Final Panel Resonance: What lingering beat closes this strip/scene?",
            "options": [
                "A chilling cliffhanger reveal showing a larger threat (like Kongor's glowing runes or the Bloodstone Monolith) waking on the horizon",
                "Quiet atmospheric aftermath—smoke clearing in the moonlight as a single vow is spoken",
                "High-velocity freeze-frame right at the moment of impact ('TO BE CONTINUED...')",
            ],
        },
    ]

    logger.info(
        "[ShowrunnerPitch] Generated 3 twists & 4 interactive Q&A items for '%s' (%s vs %s @ %s)",
        seed_idea[:60],
        primary_hero,
        secondary_hero,
        location,
    )
    return {
        "success": True,
        "seed_idea": seed_idea,
        "pacing_mode": pacing_mode,
        "primary_hero": primary_hero,
        "secondary_hero": secondary_hero,
        "location": location,
        "panel_count": panel_count,
        "twists": twists,
        "questions": questions,
    }


def generate_decompressed_storyboard(req_data: Dict[str, Any]) -> Dict[str, Any]:
    """
    Build a complete, decompressed Panel-by-Panel Storyboard JSON from the user's seed idea,
    chosen narrative twist, and Q&A answers, and save it to data/comics/stories/.
    """
    seed_idea = str(req_data.get("seed_idea", "A tense encounter in the ancient forest.")).strip()
    title = str(req_data.get("title", "")).strip() or "Chronicles of the Canopy"
    pacing_mode = str(req_data.get("pacing_mode", "Slow-Burn Atmospheric Thriller"))
    primary_hero = str(req_data.get("primary_hero", "Scout"))
    secondary_hero = str(req_data.get("secondary_hero", "Predator"))
    location = str(req_data.get("location", "Caldavar Night Woods"))
    panel_count = max(2, min(12, int(req_data.get("panel_count", 4))))
    chosen_twist = str(req_data.get("chosen_twist", ""))
    answers = req_data.get("answers", {}) or {}

    slug = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-") or f"chronicle-{int(time.time())}"
    loc_bible = load_location_bible().get("locations", {})
    base_color_script = loc_bible.get(location, {}).get("default_color_script", "Deep emerald shadows and amber rim light")

    q_open = str(answers.get("q_opening", "Extreme macro close-up on a physical clue in the silence"))
    q_esc = str(answers.get("q_escalation", "Show the threat looming unseen in the background"))
    q_cli = str(answers.get("q_climax", f"{primary_hero} unleashing their signature ability in a low-angle splash"))
    q_end = str(answers.get("q_ending", "A chilling cliffhanger reveal on the horizon"))

    second_actor = secondary_hero if secondary_hero != "None" else primary_hero

    # Construct decompressed beat templates across N panels
    beat_templates = [
        {
            "beat_type": "Sensory Hook / Establishing",
            "title": "The First Clue",
            "shot_type": "Extreme Macro Close-Up" if "macro" in q_open.lower() else "Extreme Wide Establishing Shot",
            "character_a": primary_hero,
            "character_a_action": f"in {location}: {q_open}. Fokus on tactile detail and weathered gear.",
            "character_b": "None",
            "character_b_action": "",
            "color_script": f"Cool atmospheric opening tone — {base_color_script}",
            "headroom_region": "top-left",
            "chain_previous_panel": False,
            "dialogue": [
                {
                    "speaker": "Narrator",
                    "text": f"In the deep reaches of {location}, the air grew heavy with unspoken dread.",
                    "style": "narration",
                    "x_pct": 5,
                    "y_pct": 6,
                    "width_pct": 52,
                    "tail_x_pct": 0,
                    "tail_y_pct": 0,
                },
                {
                    "speaker": primary_hero,
                    "text": "Something moved through here moments ago. The trail is still warm.",
                    "style": "normal",
                    "x_pct": 46,
                    "y_pct": 68,
                    "width_pct": 46,
                    "tail_x_pct": 44,
                    "tail_y_pct": 52,
                },
            ],
        },
        {
            "beat_type": "Psychological Escalation Beat",
            "title": "Tightening the Net",
            "shot_type": "Tight Over-the-Shoulder Tension Shot",
            "character_a": primary_hero,
            "character_a_action": f"pausing mid-step in {location}, eyes narrowing as {q_esc}",
            "character_b": secondary_hero,
            "character_b_action": (
                f"lurking partially concealed in the upper shadows of {location}, poised to strike"
                if secondary_hero != "None"
                else ""
            ),
            "color_script": "Deepening contrast — shadows lengthening with an ominous secondary glow",
            "headroom_region": "top-left",
            "chain_previous_panel": True,
            "dialogue": [
                {
                    "speaker": primary_hero,
                    "text": "Wait... these marks aren't retreating. They circled right back above me.",
                    "style": "normal",
                    "x_pct": 5,
                    "y_pct": 8,
                    "width_pct": 48,
                    "tail_x_pct": 34,
                    "tail_y_pct": 58,
                },
                {
                    "speaker": "",
                    "text": "KRRR-KLICK...",
                    "style": "sfx",
                    "x_pct": 64,
                    "y_pct": 18,
                    "width_pct": 28,
                    "tail_x_pct": 0,
                    "tail_y_pct": 0,
                },
            ],
        },
        {
            "beat_type": "Climactic Splash Payoff",
            "title": "The Clash Unleashed",
            "shot_type": "Dynamic Worm's-Eye Low Angle Action Shot",
            "character_a": second_actor,
            "character_a_action": f"erupting into motion in {location} — {q_cli}, dynamic ink motion lines and energy sparks",
            "character_b": primary_hero if second_actor != primary_hero else "None",
            "character_b_action": f"bracing and countering with fierce determination amidst flying debris" if second_actor != primary_hero else "",
            "color_script": "High-impact dramatic rim lighting — blazing crimson and electric gold energy clash",
            "headroom_region": "bottom-left",
            "chain_previous_panel": True,
            "dialogue": [
                {
                    "speaker": second_actor,
                    "text": "YOU STEPPED INTO THE WRONG GROVE!",
                    "style": "shout",
                    "x_pct": 6,
                    "y_pct": 8,
                    "width_pct": 48,
                    "tail_x_pct": 48,
                    "tail_y_pct": 42,
                },
                {
                    "speaker": "",
                    "text": "SHRRRRKKKK!",
                    "style": "sfx",
                    "x_pct": 54,
                    "y_pct": 72,
                    "width_pct": 38,
                    "tail_x_pct": 0,
                    "tail_y_pct": 0,
                },
            ],
        },
        {
            "beat_type": "Aftermath & Cliffhanger Resonance",
            "title": "Echoes of the Horizon",
            "shot_type": "Wide Dramatic Split-Depth Horizon Shot",
            "character_a": primary_hero,
            "character_a_action": f"standing amidst the smoking aftermath in {location}, looking toward the distant horizon as {q_end}",
            "character_b": secondary_hero,
            "character_b_action": f"silhouette poised at the edge of the clearing looking toward the same omen" if secondary_hero != "None" else "",
            "color_script": "Stark twilight silhouette rimmed by burning horizon light",
            "headroom_region": "top-left",
            "chain_previous_panel": True,
            "dialogue": [
                {
                    "speaker": "Narrator",
                    "text": f"The clash was only the beginning. Across {location}, an older power had awakened.",
                    "style": "narration",
                    "x_pct": 5,
                    "y_pct": 6,
                    "width_pct": 54,
                    "tail_x_pct": 0,
                    "tail_y_pct": 0,
                },
                {
                    "speaker": primary_hero,
                    "text": "That tremor wasn't us. Something colossal just woke up.",
                    "style": "normal",
                    "x_pct": 46,
                    "y_pct": 68,
                    "width_pct": 48,
                    "tail_x_pct": 36,
                    "tail_y_pct": 54,
                },
            ],
        },
    ]

    panels: List[Dict[str, Any]] = []
    for i in range(panel_count):
        tmpl = dict(beat_templates[min(i, len(beat_templates) - 1)])
        if i >= len(beat_templates):
            tmpl["title"] = f"Extended Beat #{i + 1}"
            tmpl["beat_type"] = "Decompressed Story Beat"
        tmpl["panel_number"] = i + 1
        tmpl["page_number"] = (i // 2) + 1
        tmpl["aspect_ratio"] = "16:9"
        tmpl["location"] = location
        panels.append(tmpl)

    story_doc = {
        "story_id": slug,
        "slug": slug,
        "issue_number": len(list_all_stories()) + 1,
        "arc_title": "Kongor Chronicles",
        "title": title,
        "pacing_mode": pacing_mode,
        "synopsis": f"{seed_idea} ({chosen_twist})".strip(),
        "panels": panels,
    }

    fname = f"custom_{slug.replace('-', '_')}.json"
    os.makedirs(STORIES_DIR, exist_ok=True)
    save_path = os.path.join(STORIES_DIR, fname)
    with open(save_path, "w", encoding="utf-8") as f:
        json.dump(story_doc, f, indent=2, ensure_ascii=False)

    logger.info("[ShowrunnerScript] Saved decompressed %d-panel storyboard to %s", len(panels), save_path)
    return {"success": True, "story_file": fname, "story": story_doc}


def insert_tension_beat_between_panels(story_file: str, after_panel_number: int, beat_style: str) -> Dict[str, Any]:
    """
    Insert a decompressed 'Show, Don't Tell' sensory/reaction panel right after `after_panel_number`
    and renumber subsequent panels.
    """
    story_path = os.path.join(STORIES_DIR, story_file)
    if not os.path.exists(story_path):
        return {"success": False, "error": f"Story file {story_file} not found."}

    with open(story_path, "r", encoding="utf-8") as f:
        story = json.load(f)

    panels: List[Dict[str, Any]] = story.get("panels", [])
    idx = max(0, min(after_panel_number - 1, len(panels) - 1))
    prev_p = panels[idx]
    next_p = panels[min(idx + 1, len(panels) - 1)]

    char_a = prev_p.get("character_a", "Kongor")
    char_b = next_p.get("character_a", prev_p.get("character_b", "None"))
    loc = prev_p.get("location", "Primeval Canopy")

    if beat_style == "macro_sensory":
        new_panel = {
            "panel_number": after_panel_number + 1,
            "page_number": prev_p.get("page_number", 1),
            "beat_type": "Sensory Macro Insert ('Show, Don't Tell')",
            "title": f"Silent Clue in {loc}",
            "aspect_ratio": "16:9",
            "shot_type": "Extreme Macro Close-Up Detail Shot",
            "character_a": char_a,
            "character_a_action": f"extreme close-up on {char_a}'s hand tightening on their weapon as a single drop of rain or glowing ember hits the stone in dead silence",
            "character_b": "None",
            "character_b_action": "",
            "location": loc,
            "color_script": prev_p.get("color_script", "High-contrast atmospheric detail"),
            "headroom_region": "top-left",
            "chain_previous_panel": True,
            "dialogue": [
                {
                    "speaker": "",
                    "text": "DRIP... HSSSS...",
                    "style": "sfx",
                    "x_pct": 58,
                    "y_pct": 68,
                    "width_pct": 32,
                    "tail_x_pct": 0,
                    "tail_y_pct": 0,
                }
            ],
        }
    elif beat_style == "shadow_foreshadow":
        new_panel = {
            "panel_number": after_panel_number + 1,
            "page_number": prev_p.get("page_number", 1),
            "beat_type": "Looming Shadow Beat",
            "title": "The Unseen Watcher",
            "aspect_ratio": "16:9",
            "shot_type": "High-Angle Canopy POV Shot",
            "character_a": char_b if char_b != "None" else char_a,
            "character_a_action": f"dark foreground silhouette watching silently from the high branches while {char_a} moves below through the mist",
            "character_b": char_a,
            "character_b_action": "walking cautiously through the clearing below",
            "location": loc,
            "color_script": "Deep oppressive foreground shadow framing glowing distant mist",
            "headroom_region": "top-left",
            "chain_previous_panel": True,
            "dialogue": [
                {
                    "speaker": "Narrator",
                    "text": "Every step was already being measured from above.",
                    "style": "narration",
                    "x_pct": 5,
                    "y_pct": 6,
                    "width_pct": 48,
                    "tail_x_pct": 0,
                    "tail_y_pct": 0,
                }
            ],
        }
    else:
        new_panel = {
            "panel_number": after_panel_number + 1,
            "page_number": prev_p.get("page_number", 1),
            "beat_type": "Psychological Reaction Close-Up",
            "title": f"{char_a}'s Split-Second Realization",
            "aspect_ratio": "16:9",
            "shot_type": "Extreme Close-Up on Eyes",
            "character_a": char_a,
            "character_a_action": f"extreme tight close-up on {char_a}'s eyes widening as the reflection of incoming danger gleams across their pupils",
            "character_b": "None",
            "character_b_action": "",
            "location": loc,
            "color_script": "Sharp dramatic rim light slicing across the eyes against pitch darkness",
            "headroom_region": "top-left",
            "chain_previous_panel": True,
            "dialogue": [
                {
                    "speaker": char_a,
                    "text": "...Wait.",
                    "style": "normal",
                    "x_pct": 6,
                    "y_pct": 10,
                    "width_pct": 28,
                    "tail_x_pct": 46,
                    "tail_y_pct": 52,
                }
            ],
        }

    panels.insert(idx + 1, new_panel)
    for i, p in enumerate(panels):
        p["panel_number"] = i + 1
        p["page_number"] = (i // 2) + 1

    with open(story_path, "w", encoding="utf-8") as f:
        json.dump(story, f, indent=2, ensure_ascii=False)

    logger.info(
        "[ShowrunnerInsertBeat] Inserted '%s' beat after Panel #%d in %s (Total panels: %d)",
        beat_style,
        after_panel_number,
        story_file,
        len(panels),
    )
    return {"success": True, "story": story, "inserted_panel_number": after_panel_number + 1}


def render_stylized_concept_canvas(panel: Dict[str, Any], width: int = 1280, height: int = 720) -> Image.Image:
    """
    Create a rich graphic-novel concept blockout compositing the actual HoN hero reference portraits
    and location plate when generating a fast local preview or before running the Partner API pass.
    """
    loc_name = panel.get("location", "Primeval Canopy")
    loc_bible = load_location_bible().get("locations", {})
    loc_entry = loc_bible.get(loc_name, {})
    loc_ref_rel = loc_entry.get("reference_image", "")
    loc_ref_abs = os.path.join(REPO_ROOT, loc_ref_rel) if loc_ref_rel else ""

    if loc_ref_abs and os.path.exists(loc_ref_abs):
        try:
            bg = Image.open(loc_ref_abs).convert("RGB").resize((width, height), Image.Resampling.LANCZOS)
            # Darken slightly for dramatic graphic novel atmosphere
            dark_layer = Image.new("RGB", (width, height), (10, 15, 28))
            canvas = Image.blend(bg, dark_layer, 0.35).convert("RGBA")
        except Exception:
            canvas = Image.new("RGBA", (width, height), (15, 23, 42, 255))
    else:
        canvas = Image.new("RGBA", (width, height), (15, 23, 42, 255))

    char_bible = load_character_bible().get("characters", {})
    for slot_idx, char_key in enumerate(["character_a", "character_b"]):
        c_name = str(panel.get(char_key, "None")).strip()
        if not c_name or c_name == "None":
            continue
        c_entry = char_bible.get(c_name, {})
        ref_rel = c_entry.get("reference_image", "")
        ref_abs = os.path.join(REPO_ROOT, ref_rel) if ref_rel else ""
        if not (ref_abs and os.path.exists(ref_abs)):
            slug = c_name.lower().replace(" ", "_")
            cand = os.path.join(REPO_ROOT, "src", "services", "web-ui", "wwwroot", "images", "heroes", f"{slug}.png")
            if os.path.exists(cand):
                ref_abs = cand
        if ref_abs and os.path.exists(ref_abs):
            try:
                h_im = Image.open(ref_abs).convert("RGBA")
                target_h = int(height * (0.62 if slot_idx == 0 else 0.54))
                target_w = int(h_im.width * (target_h / float(h_im.height)))
                h_im = h_im.resize((target_w, target_h), Image.Resampling.LANCZOS)
                px = int(width * 0.14) if slot_idx == 0 else int(width * 0.62)
                py = height - target_h - int(height * 0.04)
                canvas.alpha_composite(h_im, (max(10, min(px, width - target_w - 10)), max(10, py)))
            except Exception as ex:
                logger.debug("Could not composite hero preview for %s: %s", c_name, ex)

    return canvas.convert("RGB")


def execute_panel_generation(
    story_file: str,
    panel_number: int,
    engine_mode: str = "gemini_1k",
    seed: int = 42,
) -> Dict[str, Any]:
    """
    Execute single-panel generation via ComfyUI (`127.0.0.1:8188`) using our custom nodes
    and Partner API (`GeminiImage2Node` / `Flux2ProImageNode`), or instant local composite lettering.
    """
    story_path = os.path.join(STORIES_DIR, story_file)
    if not os.path.exists(story_path):
        return {"success": False, "error": f"Story file not found: {story_file}"}

    with open(story_path, "r", encoding="utf-8") as f:
        story = json.load(f)

    slug = story.get("slug", os.path.splitext(story_file)[0])
    panels = story.get("panels", [])
    idx = max(0, min(panel_number - 1, len(panels) - 1))
    panel = panels[idx]

    # Compile prompt & references using our exact custom nodes
    ref_loader = KongorCharacterRefLoader()
    loc_loader = KongorLocationRefLoader()
    compiler = KongorComicPromptCompiler()

    _, char_a_name, char_a_tags = ref_loader.load_hero_ref(panel.get("character_a", "Kongor"), True, "")
    _, char_b_name, char_b_tags = ref_loader.load_hero_ref(panel.get("character_b", "None"), True, "")
    _, env_tags, _ = loc_loader.load_location_or_continuity(
        panel.get("location", "Primeval Canopy"),
        bool(panel.get("chain_previous_panel", True)),
        slug,
        panel_number,
        "",
    )

    compiled_prompt, sys_prompt = compiler.compile_prompt(
        shot_type=panel.get("shot_type", "Wide Cinematic Shot"),
        character_a_name=char_a_name,
        character_a_tags=char_a_tags,
        character_a_action=panel.get("character_a_action", ""),
        character_b_name=char_b_name,
        character_b_tags=char_b_tags,
        character_b_action=panel.get("character_b_action", ""),
        environment_tags=env_tags,
        color_script=panel.get("color_script", ""),
        headroom_region=panel.get("headroom_region", "top-left"),
        has_layout_guide_image=False,
    )

    raw_dir = os.path.join(COMFY_OUTPUT_DIR, slug, "raw")
    let_dir = os.path.join(COMFY_OUTPUT_DIR, slug, "lettered")
    os.makedirs(raw_dir, exist_ok=True)
    os.makedirs(let_dir, exist_ok=True)
    raw_path = os.path.join(raw_dir, f"panel_{panel_number:02d}.png")
    let_path = os.path.join(let_dir, f"panel_{panel_number:02d}.png")

    used_comfy_api = False
    comfy_error = None

    if engine_mode in ("gemini_1k", "gemini_2k", "flux2_pro") and check_comfyui_online().get("online"):
        try:
            res_tier = "2K" if engine_mode == "gemini_2k" else "1K"
            workflow_prompt: Dict[str, Any] = {
                "1": {
                    "class_type": "KongorCharacterRefLoader",
                    "inputs": {
                        "hero_dropdown": char_a_name if char_a_name in list_all_heroes() else "Kongor",
                        "prefer_turnaround_sheet": True,
                        "hero_name_override": char_a_name,
                    },
                },
                "2": {
                    "class_type": "KongorLocationRefLoader",
                    "inputs": {
                        "location_dropdown": list_all_locations()[0],
                        "chain_previous_panel": bool(panel.get("chain_previous_panel", True)),
                        "story_slug": slug,
                        "current_panel_number": panel_number,
                        "location_id_override": panel.get("location", "Primeval Canopy"),
                    },
                },
                "3": {
                    "class_type": "ImageBatch",
                    "inputs": {
                        "image1": ["1", 0],
                        "image2": ["2", 0],
                    },
                },
            }
            if engine_mode == "flux2_pro":
                workflow_prompt["4"] = {
                    "class_type": "Flux2ProImageNode",
                    "inputs": {
                        "prompt": compiled_prompt,
                        "width": 1344,
                        "height": 768,
                        "seed": seed,
                        "prompt_upsampling": True,
                        "images": ["3", 0],
                    },
                }
            else:
                workflow_prompt["4"] = {
                    "class_type": "GeminiImage2Node",
                    "inputs": {
                        "prompt": compiled_prompt,
                        "model": "Nano Banana 2 (Gemini 3.1 Flash Image)",
                        "seed": seed,
                        "aspect_ratio": panel.get("aspect_ratio", "16:9"),
                        "resolution": res_tier,
                        "response_modalities": "IMAGE",
                        "images": ["3", 0],
                        "system_prompt": sys_prompt,
                    },
                }
            workflow_prompt["5"] = {
                "class_type": "KongorComicLetterer",
                "inputs": {
                    "raw_image": ["4", 0],
                    "story_slug": slug,
                    "panel_number": panel_number,
                    "dialogue_json": json.dumps(panel.get("dialogue", []), ensure_ascii=False),
                    "save_to_disk": True,
                },
            }
            workflow_prompt["6"] = {
                "class_type": "SaveImage",
                "inputs": {
                    "filename_prefix": f"kongor_chronicles/{slug}/panel_{panel_number:02d}",
                    "images": ["5", 0],
                },
            }

            payload = json.dumps({"prompt": workflow_prompt, "client_id": str(uuid.uuid4())}).encode("utf-8")
            req = urllib.request.Request(
                f"{COMFYUI_BASE_URL}/prompt", data=payload, headers={"Content-Type": "application/json"}
            )
            with urllib.request.urlopen(req, timeout=10) as resp:
                q_res = json.loads(resp.read().decode("utf-8"))
                prompt_id = q_res.get("prompt_id")

            if prompt_id:
                logger.info("[ComicStudio] Queued ComfyUI Partner API workflow (prompt_id=%s, mode=%s)", prompt_id, engine_mode)
                t_start = time.time()
                while time.time() - t_start < 90:
                    with urllib.request.urlopen(f"{COMFYUI_BASE_URL}/history/{prompt_id}", timeout=5) as h_resp:
                        hist = json.loads(h_resp.read().decode("utf-8"))
                    if prompt_id in hist:
                        status_obj = hist[prompt_id].get("status", {})
                        if status_obj.get("status_str") == "error":
                            comfy_error = str(status_obj.get("messages", "Partner API execution error"))
                            break
                        if os.path.exists(let_path) and os.path.getmtime(let_path) >= t_start - 1:
                            used_comfy_api = True
                            break
                    time.sleep(1.0)
        except Exception as ex:
            comfy_error = str(ex)
            logger.warning("[ComicStudio] ComfyUI Partner API execution fallback triggered: %s", ex)

    if not used_comfy_api:
        # Either user requested instant local lettering/concept preview, or Partner API needed fallback
        if os.path.exists(raw_path) and engine_mode == "reletter_only":
            raw_pil = Image.open(raw_path).convert("RGB")
        else:
            raw_pil = render_stylized_concept_canvas(panel, width=1280, height=720)
            raw_pil.save(raw_path, "PNG")

        lettered_pil = render_lettered_panel_pil(raw_pil, panel.get("dialogue", []), load_character_bible())
        lettered_pil.save(let_path, "PNG")

    return {
        "success": True,
        "used_comfy_api": used_comfy_api,
        "comfy_note": comfy_error,
        "story_slug": slug,
        "panel_number": panel_number,
        "compiled_prompt": compiled_prompt,
        "raw_url": f"/api/image?path={urllib.parse.quote(raw_path)}&t={int(time.time()*1000)}",
        "lettered_url": f"/api/image?path={urllib.parse.quote(let_path)}&t={int(time.time()*1000)}",
    }


HTML_STUDIO = r"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Kongor Chronicles — AI Showrunner & ComfyUI Panel Studio</title>
<style>
  :root {
    --bg: #0b0f19;
    --panel: #111827;
    --card: #1e293b;
    --border: #334155;
    --accent: #a855f7;
    --accent-hover: #9333ea;
    --gold: #f59e0b;
    --emerald: #10b981;
    --crimson: #ef4444;
    --text: #f8fafc;
    --muted: #94a3b8;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    background: var(--bg);
    color: var(--text);
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    min-height: 100vh;
    display: flex;
    flex-direction: column;
  }
  header {
    background: #0f172a;
    border-bottom: 1px solid var(--border);
    padding: 12px 24px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
  }
  .brand {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .brand h1 {
    font-size: 18px;
    font-weight: 800;
    letter-spacing: 0.5px;
    background: linear-gradient(90deg, #fbbf24, #c084fc);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
  }
  .tabs {
    display: flex;
    gap: 8px;
  }
  .tab-btn {
    background: var(--card);
    color: var(--muted);
    border: 1px solid var(--border);
    padding: 8px 16px;
    border-radius: 10px;
    font-size: 13px;
    font-weight: 700;
    cursor: pointer;
    transition: all 0.15s;
  }
  .tab-btn.active, .tab-btn:hover {
    background: var(--accent);
    color: #fff;
    border-color: #c084fc;
  }
  .status-pill {
    font-size: 12px;
    font-weight: 700;
    padding: 6px 12px;
    border-radius: 999px;
    background: #1e293b;
    border: 1px solid var(--border);
  }
  .main-view {
    display: none;
    padding: 20px 24px;
    flex: 1;
    max-width: 1720px;
    width: 100%;
    margin: 0 auto;
  }
  .main-view.active { display: block; }
  .grid-2 { display: grid; grid-template-columns: 440px 1fr; gap: 20px; }
  .card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 14px;
    padding: 18px;
    margin-bottom: 16px;
  }
  .card h2 {
    font-size: 15px;
    font-weight: 800;
    margin-bottom: 12px;
    color: #e2e8f0;
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  label {
    display: block;
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    color: var(--muted);
    margin-bottom: 5px;
    margin-top: 10px;
  }
  input[type="text"], input[type="number"], select, textarea {
    width: 100%;
    background: #090d16;
    border: 1px solid var(--border);
    color: var(--text);
    padding: 9px 12px;
    border-radius: 8px;
    font-size: 13px;
    font-family: inherit;
  }
  textarea { resize: vertical; min-height: 68px; }
  .row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
  .row-3 { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; }
  .btn {
    background: var(--accent);
    color: #fff;
    border: none;
    padding: 10px 16px;
    border-radius: 10px;
    font-size: 13px;
    font-weight: 700;
    cursor: pointer;
    transition: all 0.15s;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
  }
  .btn:hover { background: var(--accent-hover); }
  .btn-gold { background: #d97706; }
  .btn-gold:hover { background: #b45309; }
  .btn-emerald { background: #059669; }
  .btn-emerald:hover { background: #047857; }
  .btn-slate { background: #334155; }
  .btn-slate:hover { background: #475569; }

  /* Strip ribbon */
  .strip-ribbon {
    display: flex;
    align-items: center;
    gap: 10px;
    overflow-x: auto;
    padding: 12px;
    background: #090d16;
    border: 1px solid var(--border);
    border-radius: 12px;
    margin-bottom: 18px;
  }
  .panel-thumb {
    min-width: 190px;
    background: var(--card);
    border: 2px solid var(--border);
    border-radius: 10px;
    padding: 10px;
    cursor: pointer;
    transition: all 0.15s;
  }
  .panel-thumb.active {
    border-color: var(--gold);
    background: #271f3a;
  }
  .panel-thumb h4 { font-size: 12px; color: var(--gold); margin-bottom: 4px; }
  .panel-thumb p { font-size: 11px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .insert-beat-btn {
    background: #1e293b;
    color: #c084fc;
    border: 1px dashed #a855f7;
    border-radius: 8px;
    padding: 8px 10px;
    font-size: 11px;
    font-weight: 700;
    cursor: pointer;
    white-space: nowrap;
  }
  .insert-beat-btn:hover { background: #3b0764; color: #fff; }

  /* Interactive Q&A cards */
  .twist-card {
    background: #0f172a;
    border: 2px solid var(--border);
    border-radius: 10px;
    padding: 12px;
    margin-bottom: 10px;
    cursor: pointer;
  }
  .twist-card.selected { border-color: var(--gold); background: #1e1b4b; }
  .opt-label {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    background: #090d16;
    border: 1px solid var(--border);
    padding: 8px 12px;
    border-radius: 8px;
    margin-top: 6px;
    cursor: pointer;
    font-size: 12px;
    text-transform: none;
    color: var(--text);
  }
  .opt-label:hover { border-color: var(--accent); }
  #lettering-canvas {
    width: 100%;
    max-width: 1120px;
    border-radius: 10px;
    border: 2px solid var(--border);
    background: #090d16;
    cursor: grab;
    display: block;
  }
</style>
</head>
<body>

<header>
  <div class="brand">
    <span style="font-size:24px;">🦍</span>
    <div>
      <h1>KONGOR CHRONICLES STUDIO</h1>
      <div style="font-size:11px; color:var(--muted);">AI Showrunner • Multi-Ref ComfyUI Panel Generator • 60 FPS Letterer</div>
    </div>
  </div>
  <div class="tabs">
    <button class="tab-btn active" onclick="switchTab('tab-architect', this)">🧠 1. AI Story Architect</button>
    <button class="tab-btn" onclick="switchTab('tab-storyboard', this)">🎬 2. Panel-by-Panel Studio</button>
    <button class="tab-btn" onclick="switchTab('tab-lettering', this)">💬 3. 60 FPS Drag Lettering</button>
    <button class="tab-btn" onclick="switchTab('tab-assemble', this)">📚 4. Assemble Strip & Publish</button>
  </div>
  <div id="comfy-status" class="status-pill">Checking ComfyUI (8188)...</div>
</header>

<!-- TAB 1: AI STORY ARCHITECT -->
<div id="tab-architect" class="main-view active">
  <div class="grid-2">
    <div class="card">
      <h2><span>💡 Step 1: Pitch Your Rough Story Idea</span></h2>
      <label>Episode / Strip Working Title</label>
      <input type="text" id="arch-title" value="Shadows of the Ironwood" />

      <label>Your Rough Story Idea (1-3 Sentences)</label>
      <textarea id="arch-seed" rows="4">Scout is patrolling the midnight perimeter of Caldavar tracking acidic footprints, slowly realizing Predator is stalking him from the canopy above.</textarea>

      <div class="row-2">
        <div>
          <label>Pacing & Tone Dial</label>
          <select id="arch-pacing">
            <option value="Slow-Burn Atmospheric Thriller">Slow-Burn Atmospheric Thriller</option>
            <option value="Dark Fantasy Mythos">Dark Fantasy Mythos</option>
            <option value="High-Octane Action Choreography">High-Octane Action Choreography</option>
            <option value="Character Banter & Adventure">Character Banter & Adventure</option>
          </select>
        </div>
        <div>
          <label>Target Panel Count</label>
          <input type="number" id="arch-panels" value="4" min="2" max="12" />
        </div>
      </div>

      <div class="row-3">
        <div>
          <label>Primary Hero</label>
          <select id="arch-hero1"></select>
        </div>
        <div>
          <label>Secondary Hero</label>
          <select id="arch-hero2"></select>
        </div>
        <div>
          <label>Location Set</label>
          <select id="arch-loc"></select>
        </div>
      </div>

      <button class="btn" style="width:100%; margin-top:16px;" onclick="runShowrunnerPitch()">
        ✨ Pitch 3 Story Twists & Ask Showrunner Questions
      </button>
    </div>

    <div class="card" id="arch-qa-container">
      <h2><span>🎭 Step 2: Interactive Showrunner Twists & Q&A</span></h2>
      <div id="arch-qa-body" style="color:var(--muted); font-size:13px; line-height:1.6;">
        Click <b>"Pitch 3 Story Twists & Ask Showrunner Questions"</b> on the left. The Showrunner will propose 3 vivid narrative twists and ask 4 interactive cinematography & lore questions before generating your decompressed panel-by-panel script.
      </div>
    </div>
  </div>
</div>

<!-- TAB 2: PANEL-BY-PANEL STUDIO -->
<div id="tab-storyboard" class="main-view">
  <div class="card" style="padding:12px 18px;">
    <div style="display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:10px;">
      <div style="display:flex; align-items:center; gap:10px;">
        <label style="margin:0;">Active Storyline JSON:</label>
        <select id="sb-story-select" style="width:320px;" onchange="loadSelectedStory()"></select>
        <button class="btn btn-slate" onclick="saveCurrentStoryJson()">💾 Save Storyline Edits</button>
      </div>
      <div style="font-size:12px; color:var(--muted);">
        Click any panel below to edit & generate one panel at a time, or click <b>+ Slow Down</b> between panels to insert a tension beat.
      </div>
    </div>
    <div id="strip-ribbon" class="strip-ribbon"></div>
  </div>

  <div class="grid-2">
    <div class="card">
      <h2>
        <span id="sb-panel-heading">Panel #1 Inspector</span>
        <span id="sb-beat-badge" style="font-size:11px; background:#3b0764; color:#e9d5ff; padding:3px 8px; border-radius:6px;">Beat</span>
      </h2>

      <div class="row-2">
        <div>
          <label>Panel Title</label>
          <input type="text" id="p-title" oninput="syncPanelFields()" />
        </div>
        <div>
          <label>Camera Shot / Angle</label>
          <input type="text" id="p-shot" oninput="syncPanelFields()" />
        </div>
      </div>

      <div class="row-2">
        <div>
          <label>Character A (Ref Slot 1)</label>
          <select id="p-char-a" onchange="syncPanelFields()"></select>
        </div>
        <div>
          <label>Character B (Ref Slot 2)</label>
          <select id="p-char-b" onchange="syncPanelFields()"></select>
        </div>
      </div>

      <label>Character A Visual Action & Expression</label>
      <textarea id="p-char-a-act" rows="2" oninput="syncPanelFields()"></textarea>

      <label>Character B Visual Action & Expression</label>
      <textarea id="p-char-b-act" rows="2" oninput="syncPanelFields()"></textarea>

      <div class="row-2">
        <div>
          <label>Location Set (Ref Slot 3)</label>
          <select id="p-loc" onchange="syncPanelFields()"></select>
        </div>
        <div>
          <label>Bubble Headroom Region</label>
          <select id="p-headroom" onchange="syncPanelFields()">
            <option value="top-left">top-left</option>
            <option value="top-right">top-right</option>
            <option value="top-band">top-band</option>
            <option value="bottom-left">bottom-left</option>
            <option value="none">none</option>
          </select>
        </div>
      </div>

      <label>Color Script & Lighting Progression</label>
      <input type="text" id="p-color" oninput="syncPanelFields()" />

      <label style="display:flex; align-items:center; gap:8px; margin-top:12px; cursor:pointer;">
        <input type="checkbox" id="p-chain" checked onchange="syncPanelFields()" />
        <span>Chain Raw Continuity Artwork from Panel (N-1)</span>
      </label>

      <div style="display:grid; grid-template-columns:1fr 1fr; gap:8px; margin-top:16px;">
        <button class="btn" onclick="generateActivePanel('gemini_1k')">⚡ Generate 1K Draft (Nano Banana 2)</button>
        <button class="btn btn-gold" onclick="generateActivePanel('gemini_2k')">💎 Render 2K Master (Nano Banana 2)</button>
        <button class="btn btn-emerald" onclick="generateActivePanel('flux2_pro')">🔥 Render via Flux.2 [pro]</button>
        <button class="btn btn-slate" onclick="generateActivePanel('local_concept')">🎨 Instant Concept & Lettering Preview</button>
      </div>
    </div>

    <div class="card">
      <h2>
        <span>🖼️ Single-Panel Output & Multi-Reference Binding</span>
        <button class="btn btn-slate" style="padding:5px 12px; font-size:12px;" onclick="switchTab('tab-lettering', document.querySelectorAll('.tab-btn')[2])">
          💬 Open in 60 FPS Drag Letterer →
        </button>
      </h2>
      <div id="panel-render-status" style="font-size:12px; color:var(--gold); margin-bottom:8px;">Ready to generate panel.</div>
      <img id="sb-preview-img" src="" alt="Panel Preview" style="width:100%; max-height:520px; object-fit:contain; background:#090d16; border-radius:10px; border:1px solid var(--border);" />
      <label style="margin-top:14px;">Compiled Multi-Reference Prompt Sent to ComfyUI Partner Node:</label>
      <textarea id="sb-compiled-prompt" rows="4" readonly style="font-family:monospace; font-size:11px; color:#cbd5e1;"></textarea>
    </div>
  </div>
</div>

<!-- TAB 3: 60 FPS DRAG-AND-DROP LETTERING CANVAS -->
<div id="tab-lettering" class="main-view">
  <div class="grid-2">
    <div class="card">
      <h2>
        <span>💬 Dialogue Bubbles & Pointer Tails</span>
        <button class="btn btn-slate" style="padding:5px 10px; font-size:11px;" onclick="addDialogueBubble()">+ Add Bubble / SFX</button>
      </h2>
      <p style="font-size:12px; color:var(--muted); margin-bottom:10px;">
        Drag any speech bubble box on the canvas to position it, or drag its <b>gold circle handle</b> to point the speech tail directly at the speaking character's mouth!
      </p>
      <div id="bubbles-editor-list"></div>
      <button class="btn btn-emerald" style="width:100%; margin-top:14px;" onclick="commitLetteringToDisk()">
        💾 Commit & Render High-Res Lettered Panel (50ms)
      </button>
    </div>

    <div class="card">
      <h2><span>🎯 Interactive 60 FPS Lettering Canvas (Drag Bubbles & Tails)</span></h2>
      <canvas id="lettering-canvas" width="1280" height="720"></canvas>
    </div>
  </div>
</div>

<!-- TAB 4: ASSEMBLE STRIP & PUBLISH -->
<div id="tab-assemble" class="main-view">
  <div class="grid-2">
    <div class="card">
      <h2><span>📑 Assemble Approved Panels into Strip or 4K Page</span></h2>
      <label>Layout Format</label>
      <select id="asm-format">
        <option value="4K Graphic Novel Page (Vertical Stack)">4K Graphic Novel Page (Vertical Stack)</option>
        <option value="Horizontal Comic Strip (Row)">Horizontal Comic Strip (Row)</option>
        <option value="Vertical Webtoon Scroll">Vertical Webtoon Scroll</option>
        <option value="2x2 Comic Grid Page">2x2 Comic Grid Page</option>
      </select>
      <label>Page Number</label>
      <input type="number" id="asm-page-num" value="1" min="1" max="50" />
      <label style="display:flex; align-items:center; gap:8px; margin-top:12px; cursor:pointer;">
        <input type="checkbox" id="asm-publish" checked />
        <span>Also Publish WebP directly to Dawnbringer Web UI (wwwroot/images/lore/comics/)</span>
      </label>
      <button class="btn btn-gold" style="width:100%; margin-top:16px;" onclick="assembleFullStrip()">
        🚀 Assemble Strip / 4K Page Now
      </button>
      <div id="asm-status" style="margin-top:12px; font-size:12px; color:var(--emerald);"></div>
    </div>

    <div class="card">
      <h2><span>📖 Assembled Strip / Page Preview</span></h2>
      <img id="asm-preview-img" src="" alt="Assembled Page Preview" style="width:100%; max-height:700px; object-fit:contain; background:#090d16; border-radius:10px; border:1px solid var(--border);" />
    </div>
  </div>
</div>

<script>
let STATE = {
  heroes: [],
  locations: [],
  stories: [],
  activeStoryFile: "",
  activeStory: null,
  activePanelIdx: 0,
  selectedTwist: "",
  pitchData: null,
  rawImageObj: new Image(),
  dragTarget: null
};

async function initStudio() {
  const res = await fetch('/api/init');
  const data = await res.json();
  STATE.heroes = data.heroes || [];
  STATE.locations = data.locations || [];
  STATE.stories = data.stories || [];

  const statusEl = document.getElementById('comfy-status');
  if (data.comfy.online) {
    statusEl.textContent = "🟢 ComfyUI Online (127.0.0.1:8188)";
    statusEl.style.borderColor = "#10b981";
  } else {
    statusEl.textContent = "🟡 ComfyUI Offline (Local Preview Active)";
    statusEl.style.borderColor = "#f59e0b";
  }

  populateSelect('arch-hero1', STATE.heroes.filter(h => h !== 'None'), 'Scout');
  populateSelect('arch-hero2', STATE.heroes, 'Predator');
  populateSelect('arch-loc', STATE.locations, 'Caldavar Night Woods');
  populateSelect('p-char-a', STATE.heroes, 'Scout');
  populateSelect('p-char-b', STATE.heroes, 'Predator');
  populateSelect('p-loc', STATE.locations, 'Caldavar Night Woods');
  populateSelect('sb-story-select', STATE.stories, STATE.stories[STATE.stories.length - 1]);

  await loadSelectedStory();
  initLetteringCanvasEvents();
}

function populateSelect(id, items, defaultVal) {
  const sel = document.getElementById(id);
  sel.innerHTML = '';
  items.forEach(item => {
    const opt = document.createElement('option');
    opt.value = item;
    opt.textContent = item;
    if (item === defaultVal) opt.selected = true;
    sel.appendChild(opt);
  });
}

function switchTab(tabId, btnEl) {
  document.querySelectorAll('.main-view').forEach(v => v.classList.remove('active'));
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.getElementById(tabId).classList.add('active');
  if (btnEl) btnEl.classList.add('active');
  if (tabId === 'tab-lettering') {
    renderBubblesEditor();
    drawLetteringCanvas();
  }
}

async function runShowrunnerPitch() {
  const payload = {
    seed_idea: document.getElementById('arch-seed').value,
    pacing_mode: document.getElementById('arch-pacing').value,
    primary_hero: document.getElementById('arch-hero1').value,
    secondary_hero: document.getElementById('arch-hero2').value,
    location: document.getElementById('arch-loc').value,
    panel_count: parseInt(document.getElementById('arch-panels').value, 10)
  };
  const bodyEl = document.getElementById('arch-qa-body');
  bodyEl.innerHTML = '<div style="color:var(--gold);">🧠 Showrunner is analyzing HoN lore & crafting story twists...</div>';

  const res = await fetch('/api/architect/pitch', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify(payload)
  });
  const data = await res.json();
  STATE.pitchData = data;
  STATE.selectedTwist = data.twists[0].summary;

  let html = `<div style="margin-bottom:14px;"><label>Choose a Narrative Twist:</label>`;
  data.twists.forEach((tw, idx) => {
    html += `
      <div class="twist-card ${idx === 0 ? 'selected' : ''}" onclick="selectTwist(this, ${idx})">
        <div style="display:flex; justify-content:space-between; font-weight:800; color:#f8fafc; font-size:13px;">
          <span>${tw.title}</span>
          <span style="color:#c084fc; font-size:11px;">${tw.tone_tag}</span>
        </div>
        <div style="font-size:12px; color:#cbd5e1; margin-top:4px;">${tw.summary}</div>
      </div>`;
  });
  html += `</div>`;

  data.questions.forEach((q, qIdx) => {
    html += `<div style="margin-bottom:12px;">
      <label style="color:#fbbf24;">${q.question}</label>`;
    q.options.forEach((opt, oIdx) => {
      html += `
        <label class="opt-label">
          <input type="radio" name="${q.id}" value="${opt.replace(/"/g, '&quot;')}" ${oIdx === 0 ? 'checked' : ''} />
          <span>${opt}</span>
        </label>`;
    });
    html += `</div>`;
  });

  html += `
    <button class="btn btn-emerald" style="width:100%; margin-top:12px;" onclick="generateDirectorScript()">
      🎬 Generate Decompressed Panel-by-Panel Storyboard & Open Studio →
    </button>`;
  bodyEl.innerHTML = html;
}

function selectTwist(el, idx) {
  document.querySelectorAll('.twist-card').forEach(c => c.classList.remove('selected'));
  el.classList.add('selected');
  if (STATE.pitchData && STATE.pitchData.twists[idx]) {
    STATE.selectedTwist = STATE.pitchData.twists[idx].summary;
  }
}

async function generateDirectorScript() {
  const answers = {};
  if (STATE.pitchData) {
    STATE.pitchData.questions.forEach(q => {
      const checked = document.querySelector(`input[name="${q.id}"]:checked`);
      if (checked) answers[q.id] = checked.value;
    });
  }
  const payload = {
    title: document.getElementById('arch-title').value,
    seed_idea: document.getElementById('arch-seed').value,
    pacing_mode: document.getElementById('arch-pacing').value,
    primary_hero: document.getElementById('arch-hero1').value,
    secondary_hero: document.getElementById('arch-hero2').value,
    location: document.getElementById('arch-loc').value,
    panel_count: parseInt(document.getElementById('arch-panels').value, 10),
    chosen_twist: STATE.selectedTwist,
    answers: answers
  };
  const res = await fetch('/api/architect/generate_script', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify(payload)
  });
  const data = await res.json();
  if (data.success) {
    if (!STATE.stories.includes(data.story_file)) {
      STATE.stories.push(data.story_file);
    }
    populateSelect('sb-story-select', STATE.stories, data.story_file);
    await loadSelectedStory();
    switchTab('tab-storyboard', document.querySelectorAll('.tab-btn')[1]);
  }
}

async function loadSelectedStory() {
  const file = document.getElementById('sb-story-select').value;
  if (!file) return;
  STATE.activeStoryFile = file;
  const res = await fetch(`/api/story?file=${encodeURIComponent(file)}`);
  STATE.activeStory = await res.json();
  STATE.activePanelIdx = 0;
  renderStripRibbon();
  loadPanelIntoInspector(0);
}

function renderStripRibbon() {
  const rib = document.getElementById('strip-ribbon');
  rib.innerHTML = '';
  const panels = (STATE.activeStory && STATE.activeStory.panels) || [];
  panels.forEach((p, idx) => {
    const card = document.createElement('div');
    card.className = `panel-thumb ${idx === STATE.activePanelIdx ? 'active' : ''}`;
    card.onclick = () => {
      STATE.activePanelIdx = idx;
      renderStripRibbon();
      loadPanelIntoInspector(idx);
    };
    card.innerHTML = `
      <h4>PANEL #${p.panel_number} • ${p.character_a}</h4>
      <p style="color:#e2e8f0; font-weight:700;">${p.title || ''}</p>
      <p>${p.shot_type || ''}</p>`;
    rib.appendChild(card);

    if (idx < panels.length - 1) {
      const btn = document.createElement('button');
      btn.className = 'insert-beat-btn';
      btn.textContent = '+ 🎬 Slow Down';
      btn.title = 'Insert a decompressed sensory or reaction tension beat here';
      btn.onclick = () => insertBeatAfter(p.panel_number);
      rib.appendChild(btn);
    }
  });
}

async function insertBeatAfter(panelNumber) {
  const res = await fetch('/api/architect/insert_beat', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({
      story_file: STATE.activeStoryFile,
      after_panel_number: panelNumber,
      beat_style: 'reaction_closeup'
    })
  });
  const data = await res.json();
  if (data.success) {
    STATE.activeStory = data.story;
    STATE.activePanelIdx = data.inserted_panel_number - 1;
    renderStripRibbon();
    loadPanelIntoInspector(STATE.activePanelIdx);
  }
}

function loadPanelIntoInspector(idx) {
  const panels = (STATE.activeStory && STATE.activeStory.panels) || [];
  const p = panels[idx];
  if (!p) return;
  document.getElementById('sb-panel-heading').textContent = `Panel #${p.panel_number} Inspector`;
  document.getElementById('sb-beat-badge').textContent = p.beat_type || 'Story Beat';
  document.getElementById('p-title').value = p.title || '';
  document.getElementById('p-shot').value = p.shot_type || '';
  document.getElementById('p-char-a').value = p.character_a || 'Kongor';
  document.getElementById('p-char-b').value = p.character_b || 'None';
  document.getElementById('p-char-a-act').value = p.character_a_action || '';
  document.getElementById('p-char-b-act').value = p.character_b_action || '';
  document.getElementById('p-loc').value = p.location || 'Primeval Canopy';
  document.getElementById('p-headroom').value = p.headroom_region || 'top-left';
  document.getElementById('p-color').value = p.color_script || '';
  document.getElementById('p-chain').checked = p.chain_previous_panel !== false;
  generateActivePanel('local_concept');
}

function syncPanelFields() {
  const p = STATE.activeStory.panels[STATE.activePanelIdx];
  if (!p) return;
  p.title = document.getElementById('p-title').value;
  p.shot_type = document.getElementById('p-shot').value;
  p.character_a = document.getElementById('p-char-a').value;
  p.character_b = document.getElementById('p-char-b').value;
  p.character_a_action = document.getElementById('p-char-a-act').value;
  p.character_b_action = document.getElementById('p-char-b-act').value;
  p.location = document.getElementById('p-loc').value;
  p.headroom_region = document.getElementById('p-headroom').value;
  p.color_script = document.getElementById('p-color').value;
  p.chain_previous_panel = document.getElementById('p-chain').checked;
  renderStripRibbon();
}

async function saveCurrentStoryJson() {
  await fetch('/api/story/save', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({story_file: STATE.activeStoryFile, story: STATE.activeStory})
  });
  document.getElementById('panel-render-status').textContent = "✅ Storyline JSON saved to disk.";
}

async function generateActivePanel(mode) {
  await saveCurrentStoryJson();
  const p = STATE.activeStory.panels[STATE.activePanelIdx];
  const st = document.getElementById('panel-render-status');
  st.textContent = `⏳ Rendering Panel #${p.panel_number} (${mode})...`;

  const res = await fetch('/api/panel/generate', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({
      story_file: STATE.activeStoryFile,
      panel_number: p.panel_number,
      engine_mode: mode,
      seed: 42 + p.panel_number
    })
  });
  const data = await res.json();
  if (data.success) {
    document.getElementById('sb-preview-img').src = data.lettered_url;
    document.getElementById('sb-compiled-prompt').value = data.compiled_prompt || '';
    STATE.rawImageObj = new Image();
    STATE.rawImageObj.onload = () => drawLetteringCanvas();
    STATE.rawImageObj.src = data.raw_url;
    st.textContent = data.used_comfy_api
      ? `✅ Panel #${p.panel_number} rendered via ComfyUI Partner API!`
      : `✅ Panel #${p.panel_number} rendered (Concept + High-Res Lettering Active).`;
  }
}

/* 60 FPS Drag-and-Drop Lettering Canvas */
function renderBubblesEditor() {
  const listEl = document.getElementById('bubbles-editor-list');
  listEl.innerHTML = '';
  const p = STATE.activeStory.panels[STATE.activePanelIdx];
  const bubbles = p.dialogue || [];
  bubbles.forEach((b, idx) => {
    const div = document.createElement('div');
    div.style.cssText = 'background:#090d16; border:1px solid var(--border); padding:10px; border-radius:8px; margin-bottom:10px;';
    div.innerHTML = `
      <div class="row-2">
        <div>
          <label>Speaker</label>
          <input type="text" value="${b.speaker || ''}" oninput="updateBubbleField(${idx}, 'speaker', this.value)" />
        </div>
        <div>
          <label>Style</label>
          <select onchange="updateBubbleField(${idx}, 'style', this.value)">
            <option value="normal" ${b.style==='normal'?'selected':''}>Speech Bubble</option>
            <option value="narration" ${b.style==='narration'?'selected':''}>Narration Box</option>
            <option value="shout" ${b.style==='shout'?'selected':''}>Shout Burst</option>
            <option value="sfx" ${b.style==='sfx'?'selected':''}>Comic SFX</option>
          </select>
        </div>
      </div>
      <label>Text</label>
      <textarea rows="2" oninput="updateBubbleField(${idx}, 'text', this.value)">${b.text || ''}</textarea>
    `;
    listEl.appendChild(div);
  });
}

function updateBubbleField(idx, field, val) {
  const p = STATE.activeStory.panels[STATE.activePanelIdx];
  p.dialogue[idx][field] = val;
  drawLetteringCanvas();
}

function addDialogueBubble() {
  const p = STATE.activeStory.panels[STATE.activePanelIdx];
  p.dialogue = p.dialogue || [];
  p.dialogue.push({
    speaker: p.character_a || "Scout",
    text: "New dialogue line...",
    style: "normal",
    x_pct: 12,
    y_pct: 14,
    width_pct: 42,
    tail_x_pct: 35,
    tail_y_pct: 55
  });
  renderBubblesEditor();
  drawLetteringCanvas();
}

function drawLetteringCanvas() {
  const canvas = document.getElementById('lettering-canvas');
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  if (STATE.rawImageObj && STATE.rawImageObj.complete && STATE.rawImageObj.naturalWidth > 0) {
    ctx.drawImage(STATE.rawImageObj, 0, 0, canvas.width, canvas.height);
  } else {
    ctx.fillStyle = '#0f172a';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
  }

  const p = STATE.activeStory && STATE.activeStory.panels[STATE.activePanelIdx];
  if (!p || !p.dialogue) return;

  p.dialogue.forEach((b, idx) => {
    const bx = (b.x_pct / 100.0) * canvas.width;
    const by = (b.y_pct / 100.0) * canvas.height;
    const bw = ((b.width_pct || 45) / 100.0) * canvas.width;
    const bh = 110;
    b._rect = {x: bx, y: by, w: bw, h: bh};

    if ((b.style === 'normal' || b.style === 'shout') && (b.tail_x_pct > 0 || b.tail_y_pct > 0)) {
      const tx = (b.tail_x_pct / 100.0) * canvas.width;
      const ty = (b.tail_y_pct / 100.0) * canvas.height;
      b._tail = {x: tx, y: ty};
      ctx.beginPath();
      ctx.moveTo(bx + bw * 0.4, by + bh);
      ctx.lineTo(tx, ty);
      ctx.lineTo(bx + bw * 0.55, by + bh);
      ctx.closePath();
      ctx.fillStyle = 'rgba(15, 23, 42, 0.92)';
      ctx.fill();
      ctx.strokeStyle = '#a855f7';
      ctx.lineWidth = 3;
      ctx.stroke();

      // Tail drag handle
      ctx.beginPath();
      ctx.arc(tx, ty, 9, 0, Math.PI * 2);
      ctx.fillStyle = '#fbbf24';
      ctx.fill();
    }

    ctx.fillStyle = b.style === 'narration' ? 'rgba(25, 20, 35, 0.94)' : b.style === 'shout' ? 'rgba(185, 28, 28, 0.94)' : 'rgba(15, 23, 42, 0.94)';
    ctx.strokeStyle = b.style === 'narration' ? '#f59e0b' : b.style === 'shout' ? '#fef08a' : '#a855f7';
    ctx.lineWidth = 3;
    ctx.fillRect(bx, by, bw, bh);
    ctx.strokeRect(bx, by, bw, bh);

    ctx.fillStyle = '#fbbf24';
    ctx.font = 'bold 16px Segoe UI';
    ctx.fillText(b.speaker || (b.style === 'sfx' ? 'SFX' : ''), bx + 14, by + 26);

    ctx.fillStyle = '#f8fafc';
    ctx.font = '15px Segoe UI';
    ctx.fillText((b.text || '').slice(0, 68), bx + 14, by + 56);
    if ((b.text || '').length > 68) {
      ctx.fillText((b.text || '').slice(68, 136), bx + 14, by + 80);
    }
  });
}

function initLetteringCanvasEvents() {
  const canvas = document.getElementById('lettering-canvas');
  canvas.addEventListener('mousedown', e => {
    const rect = canvas.getBoundingClientRect();
    const mx = (e.clientX - rect.left) * (canvas.width / rect.width);
    const my = (e.clientY - rect.top) * (canvas.height / rect.height);
    const p = STATE.activeStory.panels[STATE.activePanelIdx];
    if (!p || !p.dialogue) return;

    for (let i = p.dialogue.length - 1; i >= 0; i--) {
      const b = p.dialogue[i];
      if (b._tail && Math.hypot(mx - b._tail.x, my - b._tail.y) <= 16) {
        STATE.dragTarget = {type: 'tail', idx: i};
        return;
      }
      if (b._rect && mx >= b._rect.x && mx <= b._rect.x + b._rect.w && my >= b._rect.y && my <= b._rect.y + b._rect.h) {
        STATE.dragTarget = {type: 'box', idx: i, ox: mx - b._rect.x, oy: my - b._rect.y};
        return;
      }
    }
  });

  window.addEventListener('mousemove', e => {
    if (!STATE.dragTarget) return;
    const canvas = document.getElementById('lettering-canvas');
    const rect = canvas.getBoundingClientRect();
    const mx = (e.clientX - rect.left) * (canvas.width / rect.width);
    const my = (e.clientY - rect.top) * (canvas.height / rect.height);
    const b = STATE.activeStory.panels[STATE.activePanelIdx].dialogue[STATE.dragTarget.idx];

    if (STATE.dragTarget.type === 'tail') {
      b.tail_x_pct = Math.round(Math.max(1, Math.min(99, (mx / canvas.width) * 100)));
      b.tail_y_pct = Math.round(Math.max(1, Math.min(99, (my / canvas.height) * 100)));
    } else {
      b.x_pct = Math.round(Math.max(1, Math.min(90, ((mx - STATE.dragTarget.ox) / canvas.width) * 100)));
      b.y_pct = Math.round(Math.max(1, Math.min(88, ((my - STATE.dragTarget.oy) / canvas.height) * 100)));
    }
    drawLetteringCanvas();
  });

  window.addEventListener('mouseup', () => { STATE.dragTarget = null; });
}

async function commitLetteringToDisk() {
  await generateActivePanel('reletter_only');
  switchTab('tab-storyboard', document.querySelectorAll('.tab-btn')[1]);
}

async function assembleFullStrip() {
  const st = document.getElementById('asm-status');
  st.textContent = "⏳ Assembling panels into high-res WebP...";
  const res = await fetch('/api/strip/assemble', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({
      story_slug: STATE.activeStory.slug,
      issue_title: STATE.activeStory.title,
      layout_format: document.getElementById('asm-format').value,
      page_number: parseInt(document.getElementById('asm-page-num').value, 10),
      publish_to_wwwroot: document.getElementById('asm-publish').checked
    })
  });
  const data = await res.json();
  if (data.success) {
    document.getElementById('asm-preview-img').src = data.page_url;
    st.textContent = `✅ Saved assembled page: ${data.output_path}`;
  }
}

window.addEventListener('DOMContentLoaded', initStudio);
</script>
</body>
</html>
"""


class ComicStudioHandler(BaseHTTPRequestHandler):
    def log_message(self, fmt: str, *args: Any) -> None:
        logger.info("%s - %s", self.address_string(), fmt % args)

    def _send_json(self, data: Dict[str, Any], status: int = 200) -> None:
        raw = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/":
            raw = HTML_STUDIO.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)
        elif parsed.path == "/api/init":
            self._send_json(
                {
                    "heroes": list_all_heroes(),
                    "locations": list_all_locations(),
                    "stories": list_all_stories(),
                    "comfy": check_comfyui_online(),
                }
            )
        elif parsed.path == "/api/story":
            qs = urllib.parse.parse_qs(parsed.query)
            fname = qs.get("file", [list_all_stories()[0]])[0]
            fpath = os.path.join(STORIES_DIR, os.path.basename(fname))
            if os.path.exists(fpath):
                with open(fpath, "r", encoding="utf-8") as f:
                    self._send_json(json.load(f))
            else:
                self._send_json({"error": "Not found"}, status=404)
        elif parsed.path == "/api/image":
            qs = urllib.parse.parse_qs(parsed.query)
            img_path = qs.get("path", [""])[0]
            if img_path and os.path.exists(img_path):
                ext = os.path.splitext(img_path)[1].lower()
                mime = "image/webp" if ext == ".webp" else "image/png"
                with open(img_path, "rb") as f:
                    data = f.read()
                self.send_response(200)
                self.send_header("Content-Type", mime)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            else:
                self.send_response(404)
                self.end_headers()
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length).decode("utf-8")) if length > 0 else {}

        if parsed.path == "/api/architect/pitch":
            res = generate_showrunner_pitch(
                seed_idea=body.get("seed_idea", ""),
                pacing_mode=body.get("pacing_mode", "Slow-Burn Atmospheric Thriller"),
                primary_hero=body.get("primary_hero", "Scout"),
                secondary_hero=body.get("secondary_hero", "Predator"),
                location=body.get("location", "Caldavar Night Woods"),
                panel_count=int(body.get("panel_count", 4)),
            )
            self._send_json(res)
        elif parsed.path == "/api/architect/generate_script":
            self._send_json(generate_decompressed_storyboard(body))
        elif parsed.path == "/api/architect/insert_beat":
            self._send_json(
                insert_tension_beat_between_panels(
                    story_file=body.get("story_file", ""),
                    after_panel_number=int(body.get("after_panel_number", 1)),
                    beat_style=body.get("beat_style", "reaction_closeup"),
                )
            )
        elif parsed.path == "/api/story/save":
            fname = os.path.basename(body.get("story_file", "custom_story.json"))
            fpath = os.path.join(STORIES_DIR, fname)
            with open(fpath, "w", encoding="utf-8") as f:
                json.dump(body.get("story", {}), f, indent=2, ensure_ascii=False)
            self._send_json({"success": True, "file": fname})
        elif parsed.path == "/api/panel/generate":
            self._send_json(
                execute_panel_generation(
                    story_file=body.get("story_file", ""),
                    panel_number=int(body.get("panel_number", 1)),
                    engine_mode=body.get("engine_mode", "gemini_1k"),
                    seed=int(body.get("seed", 42)),
                )
            )
        elif parsed.path == "/api/strip/assemble":
            assembler = KongorStripAssembler()
            _, out_webp = assembler.assemble_strip(
                story_slug=body.get("story_slug", "the-hunters-shadow"),
                issue_title=body.get("issue_title", "The Hunter's Shadow"),
                layout_format=body.get("layout_format", "4K Graphic Novel Page (Vertical Stack)"),
                page_number=int(body.get("page_number", 1)),
                publish_to_wwwroot=bool(body.get("publish_to_wwwroot", True)),
            )
            self._send_json(
                {
                    "success": True,
                    "output_path": out_webp,
                    "page_url": f"/api/image?path={urllib.parse.quote(out_webp)}&t={int(time.time()*1000)}",
                }
            )
        else:
            self._send_json({"error": "Unknown endpoint"}, status=404)


def main() -> int:
    global COMFYUI_BASE_URL
    parser = argparse.ArgumentParser(description="Kongor Chronicles Interactive Storyboard & ComfyUI Studio Server")
    parser.add_argument("--host", type=str, default="127.0.0.1", help="Host to bind (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=8190, help="Port to bind (default: 8190)")
    parser.add_argument("--comfy-url", type=str, default="http://127.0.0.1:8188", help="ComfyUI base URL (default: http://127.0.0.1:8188)")
    args = parser.parse_args()
    COMFYUI_BASE_URL = args.comfy_url.rstrip("/")

    server = ThreadingHTTPServer((args.host, args.port), ComicStudioHandler)
    logger.info("=== Kongor Chronicles Storyboard Studio running at http://%s:%d (ComfyUI: %s) ===", args.host, args.port, COMFYUI_BASE_URL)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("Shutting down Kongor Chronicles Storyboard Studio.")
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
