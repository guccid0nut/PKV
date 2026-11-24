#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Automated Hand-Drawn Comic Generator using ComfyUI MCP & 3D Hero Reference Styling.
Generates 4K graphic novel comic panels, composites dialogue balloons & captions,
and saves the final issues for the Dawnbringer Lore reader.
"""

from __future__ import annotations

import argparse
import io
import json
import logging
import os
import sys
import time
import urllib.request
from typing import Any, Dict, List, Optional, Tuple

from PIL import Image, ImageDraw, ImageFont

def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

PROJECT_ROOT = _find_repo_root()
WWWROOT_LORE = os.path.join(PROJECT_ROOT, "src", "services", "web-ui", "wwwroot", "images", "lore")

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [ComfyComic] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger("ComfyComic")

COMIC_STYLE_POSITIVE = (
    "masterpiece, best quality, vintage graphic novel style, hand-drawn comic book illustration, "
    "detailed ink lineart, crisp cross-hatching, rich watercolor texture, cinematic lighting, dynamic composition"
)

COMIC_STYLE_NEGATIVE = (
    "photorealistic, 3d render, CGI, plastic, blurry, low quality, bad anatomy, deformed limbs, extra fingers, text, watermark"
)


def get_font(size: int, bold: bool = False) -> ImageFont.ImageFont:
    try:
        font_names = ["arialbd.ttf" if bold else "arial.ttf", "segoeuib.ttf" if bold else "segoeui.ttf", "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"]
        for fn in font_names:
            try:
                return ImageFont.truetype(fn, size)
            except Exception:
                continue
    except Exception:
        pass
    return ImageFont.load_default()


def draw_speech_bubble(
    draw: ImageDraw.ImageDraw,
    speaker: str,
    text: str,
    x: int,
    y: int,
    max_width: int = 650,
    is_narration: bool = False,
    is_shout: bool = False
) -> None:
    font_speaker = get_font(26, bold=True)
    font_text = get_font(24, bold=False)
    
    words = text.split()
    lines = []
    curr = []
    for w in words:
        curr.append(w)
        test_line = " ".join(curr)
        bbox = draw.textbbox((0, 0), test_line, font=font_text)
        if (bbox[2] - bbox[0]) > max_width and len(curr) > 1:
            curr.pop()
            lines.append(" ".join(curr))
            curr = [w]
    if curr:
        lines.append(" ".join(curr))

    line_height = 34
    content_h = len(lines) * line_height + (35 if speaker else 15)
    box_w = max_width + 40
    box_h = content_h + 25

    if is_narration:
        bg_color = (25, 20, 35, 235)
        border_color = (217, 119, 6)
        draw.rectangle([x, y, x + box_w, y + box_h], fill=bg_color, outline=border_color, width=3)
        if speaker:
            draw.text((x + 18, y + 12), speaker.upper(), fill=(251, 191, 36), font=font_speaker)
            ty = y + 44
        else:
            ty = y + 14
        for l in lines:
            draw.text((x + 18, ty), l, fill=(243, 244, 246), font=font_text)
            ty += line_height
    elif is_shout:
        bg_color = (220, 38, 38, 240)
        border_color = (255, 255, 255)
        draw.rectangle([x, y, x + box_w, y + box_h], fill=bg_color, outline=border_color, width=4)
        if speaker:
            draw.text((x + 20, y + 12), speaker.upper(), fill=(255, 255, 255), font=font_speaker)
            ty = y + 46
        else:
            ty = y + 14
        for l in lines:
            draw.text((x + 20, ty), l, fill=(255, 255, 255), font=get_font(26, bold=True))
            ty += line_height + 4
    else:
        bg_color = (15, 23, 42, 235)
        border_color = (168, 85, 247)
        draw.rounded_rectangle([x, y, x + box_w, y + box_h], radius=16, fill=bg_color, outline=border_color, width=3)
        if speaker:
            draw.text((x + 20, y + 12), speaker, fill=(192, 132, 252), font=font_speaker)
            ty = y + 44
        else:
            ty = y + 14
        for l in lines:
            draw.text((x + 20, ty), l, fill=(241, 245, 249), font=font_text)
            ty += line_height


def generate_panel_with_comfyui(
    client: Any,
    character_prompt: str,
    scene_prompt: str,
    dialogue_elements: List[Dict[str, Any]],
    width: int = 1216,
    height: int = 832,
    seed: int = 42
) -> Image.Image:
    """Generate image via ComfyUI MCP client and composite lettering overlay."""
    full_prompt = f"{COMIC_STYLE_POSITIVE}, Project Kongor universe, {character_prompt}, {scene_prompt}"
    logger.info("Queuing ComfyUI panel render (Seed: %d)...", seed)
    logger.info("Prompt: %s", full_prompt[:120] + "...")

    res = client.generate_image(
        prompt=full_prompt,
        negative_prompt=COMIC_STYLE_NEGATIVE,
        width=width,
        height=height,
        steps=6, # 4-8 steps for Lightning / Turbo
        cfg=2.0, # Low CFG for Lightning/Turbo
        seed=seed
    )

    images = res.get("images", [])
    if not images:
        logger.warning("No images returned from ComfyUI, creating placeholder canvas")
        canvas = Image.new("RGB", (width, height), (20, 25, 40))
    else:
        filename = images[0]["filename"]
        subfolder = images[0].get("subfolder", "")
        img_type = images[0].get("type", "output")
        view_url = f"http://127.0.0.1:8188/view?filename={filename}&subfolder={subfolder}&type={img_type}"
        logger.info("Fetching generated image from: %s", view_url)
        with urllib.request.urlopen(view_url, timeout=15) as r:
            canvas = Image.open(io.BytesIO(r.read())).convert("RGBA")

    # Resize to target if needed
    if canvas.size != (width, height):
        canvas = canvas.resize((width, height), Image.Resampling.LANCZOS)

    # Composite dialogue & narration bubbles
    draw = ImageDraw.Draw(canvas, "RGBA")
    for elem in dialogue_elements:
        draw_speech_bubble(
            draw,
            speaker=elem.get("speaker", ""),
            text=elem.get("text", ""),
            x=elem.get("x", 80),
            y=elem.get("y", 80),
            max_width=elem.get("width", 650),
            is_narration=elem.get("is_narration", False),
            is_shout=elem.get("is_shout", False)
        )

    # Clean panel frame border
    draw.rectangle([0, 0, width - 1, height - 1], outline=(30, 41, 59), width=8)
    return canvas.convert("RGB")


def assemble_page(
    page_number: int,
    issue_title: str,
    panels: List[Image.Image],
    output_path_webp: str
) -> None:
    gutter = 40
    header_h = 160
    footer_h = 100
    total_w = 2400
    
    # Scale panels to fit width
    scaled_panels = []
    for p in panels:
        ratio = total_w / float(p.width)
        nh = int(p.height * ratio)
        scaled_panels.append(p.resize((total_w, nh), Image.Resampling.LANCZOS))

    total_h = header_h + sum(p.height for p in scaled_panels) + (len(scaled_panels) - 1) * gutter + footer_h
    page_canvas = Image.new("RGB", (total_w, total_h), (8, 12, 20))
    draw = ImageDraw.Draw(page_canvas)

    # Page Header
    font_header_title = get_font(44, bold=True)
    font_header_sub = get_font(26, bold=False)
    draw.text((80, 50), "PROJECT KONGOR CHRONICLES", fill=(192, 132, 252), font=font_header_sub)
    draw.text((80, 85), issue_title.upper(), fill=(255, 255, 255), font=font_header_title)

    # Paste panels
    curr_y = header_h
    for p in scaled_panels:
        page_canvas.paste(p, (0, curr_y))
        curr_y += p.height + gutter

    # Page Footer
    font_footer = get_font(26, bold=True)
    draw.text((total_w // 2 - 80, total_h - 60), f"PAGE {page_number}", fill=(148, 163, 184), font=font_footer)

    os.makedirs(os.path.dirname(output_path_webp), exist_ok=True)
    page_canvas.save(output_path_webp, "WEBP", quality=92, method=6)
    logger.info("Saved 4K Comic Page: %s (%dx%d)", output_path_webp, total_w, total_h)


def generate_issue_1() -> None:
    sys.path.insert(0, os.path.join(PROJECT_ROOT, "src", "tools", "comfyui"))
    from mcp_client import ComfyUIMCPClient
    client = ComfyUIMCPClient()

    if not client.ping():
        logger.error("ComfyUI is not online at http://127.0.0.1:8188! Please launch it first.")
        return

    logger.info("=== Generating Hand-Drawn 4K Comic: Issue #1 (Whispers in the Ironwood) ===")
    out_dir = os.path.join(WWWROOT_LORE, "comics", "issue-1")

    # Cover
    cover_prompt_char = "giant silverback gorilla guardian Kongor with thick dark fur, sitting atop towering ancient Ironwood bough"
    cover_prompt_scene = "golden sunbeams piercing through misty primeval canopy foliage, majestic fantasy jungle landscape, hand-drawn comic book cover art"
    cover_panel = generate_panel_with_comfyui(client, cover_prompt_char, cover_prompt_scene, [], width=832, height=1216, seed=101)
    cover_panel = cover_panel.resize((2400, 3200), Image.Resampling.LANCZOS)
    
    # Add Cover branding
    draw_cover = ImageDraw.Draw(cover_panel, "RGBA")
    draw_cover.rectangle([80, 80, 2320, 3120], outline=(168, 85, 247), width=6)
    draw_cover.text((140, 140), "PROJECT KONGOR COMICS", fill=(251, 191, 36), font=get_font(56, bold=True))
    draw_cover.text((140, 210), "SAGA: THE PRIMEVAL CANOPY", fill=(192, 132, 252), font=get_font(38, bold=True))
    
    draw_cover.rectangle([120, 2600, 2280, 3050], fill=(10, 15, 26, 230), outline=(168, 85, 247), width=3)
    draw_cover.text((180, 2650), "WHISPERS IN THE IRONWOOD", fill=(255, 255, 255), font=get_font(84, bold=True))
    draw_cover.text((180, 2770), "Issue #1 • The Untold Bond of Kongor & Young Ophelia", fill=(203, 213, 225), font=get_font(36, bold=False))
    cover_panel.save(os.path.join(out_dir, "cover.webp"), "WEBP", quality=92)

    # Page 1: Kongor in the canopy & Ophelia sneaking out
    p1_1 = generate_panel_with_comfyui(
        client,
        "massive gentle silverback gorilla guardian Kongor resting peacefully on colossal tree branch",
        "misty emerald canopy, morning sunrays filtering through leaves, peaceful wild sanctuary",
        [{"speaker": "Narrator", "text": "In the ancient era before the cosmic convergence, the Primeval Canopy was a sanctuary of unbroken peace.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=201
    )
    p1_2 = generate_panel_with_comfyui(
        client,
        "young teenage girl summoner Ophelia with auburn hair in emerald robes sneaking through giant roots",
        "stone sun-enclave fortress walls in distant background, deep ancient forest foreground",
        [{"speaker": "Narrator", "text": "Far beyond the strict sanctuary walls of her people, young Ophelia wandered where the elders strictly forbade.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=202
    )
    assemble_page(1, "Whispers in the Ironwood", [p1_1, p1_2], os.path.join(out_dir, "page-1.webp"))

    # Page 2: First encounter & fruit sharing
    p2_1 = generate_panel_with_comfyui(
        client,
        "young female summoner Ophelia in green robes standing in sacred glade looking up with warm gentle smile",
        "crystal clear forest pool, ancient giant trees, soft dappled light",
        [{"speaker": "Ophelia", "text": "The elders warned of terrifying beasts in the dark grove... but you have gentle eyes.", "x": 100, "y": 80, "width": 750}],
        seed=301
    )
    p2_2 = generate_panel_with_comfyui(
        client,
        "giant gorilla guardian Kongor extending huge gentle hand offering a glowing golden sun-fig to young girl",
        "mossy stones, serene jungle clearing, warm magical atmosphere",
        [{"speaker": "Narrator", "text": "Kongor reached down with astonishing gentleness, offering a ripe sun-fig from the high boughs.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=302
    )
    assemble_page(2, "Whispers in the Ironwood", [p2_1, p2_2], os.path.join(out_dir, "page-2.webp"))

    # Page 3: Companionship at the pool
    p3_1 = generate_panel_with_comfyui(
        client,
        "giant silverback gorilla Kongor and young girl Ophelia sitting side by side by a tranquil forest pool",
        "golden sunset reflection in water, ancient towering trees, fireflies floating in air",
        [{"speaker": "Narrator", "text": "A silent bond of trust was forged between the forest guardian and the daughter of the enclave.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=401
    )
    assemble_page(3, "Whispers in the Ironwood", [p3_1], os.path.join(out_dir, "page-3.webp"))

    # Page 4: Foreshadowing & Ominous Echo
    p4_1 = generate_panel_with_comfyui(
        client,
        "deep dark jungle shadows at twilight, eerie glowing red runic energy pulse in the distant undergrowth",
        "silhouette of hooded ranger Scout and mysterious claw marks on ancient stone",
        [
            {"speaker": "Narrator", "text": "As night fell over the canopy, neither knew that dark forces were already tracking the primordial resonance...", "x": 100, "y": 80, "width": 950, "is_narration": True},
            {"speaker": "Scout", "text": "The boundary wards are failing... something mechanical is stalking the grove.", "x": 100, "y": 600, "width": 800}
        ],
        seed=501
    )
    assemble_page(4, "Whispers in the Ironwood", [p4_1], os.path.join(out_dir, "page-4.webp"))

    logger.info("=== Successfully Generated Issue #1 with ComfyUI Hand-Drawn Art! ===")


def generate_issue_2() -> None:
    sys.path.insert(0, os.path.join(PROJECT_ROOT, "src", "tools", "comfyui"))
    from mcp_client import ComfyUIMCPClient
    client = ComfyUIMCPClient()

    logger.info("=== Generating Hand-Drawn 4K Comic: Issue #2 (Echoes on the Perimeter) ===")
    out_dir = os.path.join(WWWROOT_LORE, "comics", "issue-2")

    # Cover
    cover_prompt_char = "mysterious cloaked ranger Scout with recurve bow perched on ancient mossy stone branch, dark jungle perimeter"
    cover_prompt_scene = "ominous crimson rune monolith glowing in background, misty deep forest, dynamic angle, hand-drawn comic cover art"
    cover_panel = generate_panel_with_comfyui(client, cover_prompt_char, cover_prompt_scene, [], width=832, height=1216, seed=2101)
    cover_panel = cover_panel.resize((2400, 3200), Image.Resampling.LANCZOS)
    
    draw_cover = ImageDraw.Draw(cover_panel, "RGBA")
    draw_cover.rectangle([80, 80, 2320, 3120], outline=(168, 85, 247), width=6)
    draw_cover.text((140, 140), "PROJECT KONGOR COMICS", fill=(251, 191, 36), font=get_font(56, bold=True))
    draw_cover.text((140, 210), "SAGA: THE PRIMEVAL CANOPY", fill=(192, 132, 252), font=get_font(38, bold=True))
    draw_cover.rectangle([120, 2600, 2280, 3050], fill=(10, 15, 26, 230), outline=(168, 85, 247), width=3)
    draw_cover.text((180, 2650), "ECHOES ON THE PERIMETER", fill=(255, 255, 255), font=get_font(84, bold=True))
    draw_cover.text((180, 2770), "Issue #2 • The Severed Wards & The Bloodstone Monolith", fill=(203, 213, 225), font=get_font(36, bold=False))
    cover_panel.save(os.path.join(out_dir, "cover.webp"), "WEBP", quality=92)

    # Page 1
    p1_1 = generate_panel_with_comfyui(
        client,
        "cloaked ranger Scout inspecting slashed metallic tree trunks and severed magic wards",
        "misty canopy perimeter, dark jungle trail, detective atmosphere",
        [{"speaker": "Scout", "text": "The boundary wards are cut cleanly. Someone with mechanical precision is stalking the outer perimeter.", "x": 100, "y": 80, "width": 850}],
        seed=2201
    )
    p1_2 = generate_panel_with_comfyui(
        client,
        "deep ancient jungle ground trembling, strange crimson shockwave ripples through water pool",
        "lush jungle, red magical energy aura, eerie atmosphere",
        [{"speaker": "Narrator", "text": "An eerie crimson pulse vibrated through the earth, setting the leaves trembling.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=2202
    )
    assemble_page(1, "Echoes on the Perimeter", [p1_1, p1_2], os.path.join(out_dir, "page-1.webp"))

    # Page 2
    p2_1 = generate_panel_with_comfyui(
        client,
        "young summoner Ophelia and massive gorilla Kongor standing before a rising obsidian monolith",
        "red glowing ancient runes engraved on dark stone, water pool swirling with magical mist",
        [{"speaker": "Ophelia", "text": "Kongor, look! The water pool is glowing... the stones are bleeding red light!", "x": 100, "y": 80, "width": 800}],
        seed=2301
    )
    p2_2 = generate_panel_with_comfyui(
        client,
        "giant obsidian monolith pulsing with intense crimson power, ancient alien runes",
        "magical energy sparks, dramatic comic panel framing",
        [{"speaker": "Narrator", "text": "An obsidian monolith had risen from the depths, humming with forgotten cosmic power.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=2302
    )
    assemble_page(2, "Echoes on the Perimeter", [p2_1, p2_2], os.path.join(out_dir, "page-2.webp"))

    # Page 3
    p3_1 = generate_panel_with_comfyui(
        client,
        "giant gorilla guardian Kongor stepping protectively in front of young Ophelia, low defensive rumble",
        "glowing red stone reflections on dark fur, dramatic shadows, protective stance",
        [{"speaker": "Narrator", "text": "Kongor stepped between Ophelia and the monolith, shielding her with his massive frame.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=2401
    )
    p3_2 = generate_panel_with_comfyui(
        client,
        "ranger Scout aiming drawn bow into dark foliage, tense alert expression",
        "shadows of the grove, mechanical optical glint in distant brush",
        [{"speaker": "Scout", "text": "They're not just scouting... they're hunting. Ophelia, get back!", "x": 100, "y": 80, "width": 800}],
        seed=2402
    )
    assemble_page(3, "Echoes on the Perimeter", [p3_1, p3_2], os.path.join(out_dir, "page-3.webp"))

    # Page 4
    p4_1 = generate_panel_with_comfyui(
        client,
        "menacing armored cybernetic beast-hunter Predator emerging from foliage with twin razor wrist blades",
        "glowing yellow optics, predatory stance, dark jungle mist",
        [
            {"speaker": "Narrator", "text": "From the dark underbrush, razor-sharp claws extended as Predator's optics locked onto Kongor.", "x": 100, "y": 80, "width": 950, "is_narration": True},
            {"speaker": "", "text": "SHRRRRKKKK!", "x": 800, "y": 500, "width": 500, "is_shout": True}
        ],
        seed=2501
    )
    assemble_page(4, "Echoes on the Perimeter", [p4_1], os.path.join(out_dir, "page-4.webp"))
    logger.info("=== Successfully Generated Issue #2 with ComfyUI Hand-Drawn Art! ===")


def generate_issue_3() -> None:
    sys.path.insert(0, os.path.join(PROJECT_ROOT, "src", "tools", "comfyui"))
    from mcp_client import ComfyUIMCPClient
    client = ComfyUIMCPClient()

    logger.info("=== Generating Hand-Drawn 4K Comic: Issue #3 (The Shattered Dawn) ===")
    out_dir = os.path.join(WWWROOT_LORE, "comics", "issue-3")

    # Cover
    cover_prompt_char = "epic furious giant gorilla Kongor with burning crimson primordial runes ignited across his body, roaring at the sky"
    cover_prompt_scene = "shattered iron chains, lightning and storm over ancient canopy, dramatic high action graphic novel cover art"
    cover_panel = generate_panel_with_comfyui(client, cover_prompt_char, cover_prompt_scene, [], width=832, height=1216, seed=3101)
    cover_panel = cover_panel.resize((2400, 3200), Image.Resampling.LANCZOS)
    
    draw_cover = ImageDraw.Draw(cover_panel, "RGBA")
    draw_cover.rectangle([80, 80, 2320, 3120], outline=(239, 68, 68), width=6)
    draw_cover.text((140, 140), "PROJECT KONGOR COMICS", fill=(251, 191, 36), font=get_font(56, bold=True))
    draw_cover.text((140, 210), "SAGA: THE PRIMEVAL CANOPY", fill=(248, 113, 113), font=get_font(38, bold=True))
    draw_cover.rectangle([120, 2600, 2280, 3050], fill=(10, 15, 26, 230), outline=(239, 68, 68), width=3)
    draw_cover.text((180, 2650), "THE SHATTERED DAWN", fill=(255, 255, 255), font=get_font(84, bold=True))
    draw_cover.text((180, 2770), "Issue #3 • The Awakening of the Primordial Fury", fill=(203, 213, 225), font=get_font(36, bold=False))
    cover_panel.save(os.path.join(out_dir, "cover.webp"), "WEBP", quality=92)

    # Page 1
    p1_1 = generate_panel_with_comfyui(
        client,
        "electrified metallic capture nets flying through air, trapping sacred jungle trees and clearing",
        "sparks flying, armored corrupt hunters advancing with mechanical weapons",
        [{"speaker": "Narrator", "text": "Electrified iron nets fell like thunder! The beast-hunters ambushed the sacred grove with mechanical fury.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=3201
    )
    p1_2 = generate_panel_with_comfyui(
        client,
        "menacing cybernetic hunter Predator pointing plasma claw toward Kongor and monolith",
        "smoke, electrical discharge, fierce action pose",
        [{"speaker": "Predator", "text": "Contain the prime specimen! Harvest the runic core!", "x": 100, "y": 80, "width": 750, "is_shout": True}],
        seed=3202
    )
    assemble_page(1, "The Shattered Dawn", [p1_1, p1_2], os.path.join(out_dir, "page-1.webp"))

    # Page 2
    p2_1 = generate_panel_with_comfyui(
        client,
        "young Ophelia surrounded by electrified traps reaching out desperately toward Kongor",
        "sparks, dramatic anime / graphic novel emotional expression, dark forest smoke",
        [{"speaker": "Ophelia", "text": "KONGOR! RUN! THEY CAME FOR THE CORE!", "x": 100, "y": 80, "width": 700, "is_shout": True}],
        seed=3301
    )
    p2_2 = generate_panel_with_comfyui(
        client,
        "massive Kongor breaking through iron net, eyes blazing with primordial cosmic crimson light",
        "red magical surge exploding from his chest, shattering shockwave",
        [{"speaker": "Narrator", "text": "As the hunters closed in on Ophelia, something ancient and terrifying stirred within the guardian's soul.", "x": 100, "y": 80, "width": 850, "is_narration": True}],
        seed=3302
    )
    assemble_page(2, "The Shattered Dawn", [p2_1, p2_2], os.path.join(out_dir, "page-2.webp"))

    # Page 3
    p3_1 = generate_panel_with_comfyui(
        client,
        "titanic enraged Kongor unleashing an earth-shattering roar, primordial crimson lightning radiating from body",
        "shattered trees, hunters thrown back by massive shockwave, epic double-page action splash",
        [
            {"speaker": "", "text": "ROOOOOAAAAR!", "x": 600, "y": 200, "width": 600, "is_shout": True},
            {"speaker": "Narrator", "text": "A titanic shockwave shattered the iron nets. Primordial crimson runes ignited across Kongor's fur, surging with unimaginable power!", "x": 100, "y": 550, "width": 950, "is_narration": True}
        ],
        seed=3401
    )
    assemble_page(3, "The Shattered Dawn", [p3_1], os.path.join(out_dir, "page-3.webp"))

    # Page 4
    p4_1 = generate_panel_with_comfyui(
        client,
        "Kongor towering majestically over the morning mist, glowing with faint crimson energy, young Ophelia looking up in awe",
        "sunrise over the broken battlefield, serene yet transformed atmosphere",
        [
            {"speaker": "Narrator", "text": "The hunters scattered into the mist. Kongor stood tall, guardian of the forest, awakened by the primordial spark.", "x": 100, "y": 80, "width": 950, "is_narration": True},
            {"speaker": "Ophelia", "text": "The world will never be the same... but you'll always be my guardian.", "x": 100, "y": 400, "width": 750}
        ],
        seed=3501
    )
    assemble_page(4, "The Shattered Dawn", [p4_1], os.path.join(out_dir, "page-4.webp"))
    logger.info("=== Successfully Generated Issue #3 with ComfyUI Hand-Drawn Art! ===")


if __name__ == "__main__":
    generate_issue_1()
    generate_issue_2()
    generate_issue_3()

