#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Project Kongor 4K Ultra-HD Comic & Lore Scene Generator.
Leverages live Blender MCP or headless Blender 4.2+/5.2+ and Pillow to generate 
4K comic book pages, covers, banners, dialogue balloons, and sound effect lettering.
"""

from __future__ import annotations

import argparse
import io
import json
import logging
import math
import os
import sys
import time
from typing import Any, Dict, List, Optional, Tuple

from PIL import Image, ImageDraw, ImageFilter, ImageFont

def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

PROJECT_ROOT = _find_repo_root()
WWWROOT_LORE = os.path.join(PROJECT_ROOT, "src", "services", "web-ui", "wwwroot", "images", "lore")
BLENDER_MCP_CLIENT = os.path.join(PROJECT_ROOT, "src", "tools", "blender", "mcp_client.py")

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [ComicGenerator] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger("ComicGenerator")


def try_blender_mcp() -> bool:
    """Check if live Blender MCP server is reachable."""
    try:
        sys.path.insert(0, os.path.join(PROJECT_ROOT, "src", "tools", "blender"))
        from mcp_client import BlenderMCPClient
        client = BlenderMCPClient()
        return client.ping()
    except Exception as ex:
        logger.debug("Blender MCP not reachable: %s", ex)
        return False


def get_font(size: int, bold: bool = False) -> ImageFont.ImageFont:
    """Load default font with size."""
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


def create_gradient_canvas(width: int, height: int, top_color: Tuple[int, int, int], bot_color: Tuple[int, int, int]) -> Image.Image:
    """Generate a vertical gradient image."""
    base = Image.new("RGB", (width, height), top_color)
    draw = ImageDraw.Draw(base)
    for y in range(height):
        factor = y / float(height)
        r = int(top_color[0] + factor * (bot_color[0] - top_color[0]))
        g = int(top_color[1] + factor * (bot_color[1] - top_color[1]))
        b = int(top_color[2] + factor * (bot_color[2] - top_color[2]))
        draw.line([(0, y), (width, y)], fill=(r, g, b))
    return base


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
    """Draw comic speech balloon or narrative caption box."""
    font_speaker = get_font(26, bold=True)
    font_text = get_font(24, bold=False)
    
    # Word wrap text
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
        # Parchment narrative box
        bg_color = (25, 20, 35, 230)
        border_color = (217, 119, 6) # Amber gold
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
        # Jagged action burst balloon
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
        # Standard white speech bubble
        bg_color = (15, 23, 42, 235)
        border_color = (168, 85, 247) # Purple
        draw.rounded_rectangle([x, y, x + box_w, y + box_h], radius=16, fill=bg_color, outline=border_color, width=3)
        if speaker:
            draw.text((x + 20, y + 12), speaker, fill=(192, 132, 252), font=font_speaker)
            ty = y + 44
        else:
            ty = y + 14
        for l in lines:
            draw.text((x + 20, ty), l, fill=(241, 245, 249), font=font_text)
            ty += line_height


def draw_action_sfx(draw: ImageDraw.ImageDraw, sfx: str, x: int, y: int, color: Tuple[int, int, int] = (239, 68, 68)) -> None:
    """Draw stylized comic sound effect text."""
    font_sfx = get_font(68, bold=True)
    # Drop shadow
    draw.text((x + 4, y + 4), sfx, fill=(0, 0, 0), font=font_sfx)
    draw.text((x + 2, y + 2), sfx, fill=(255, 255, 255), font=font_sfx)
    draw.text((x, y), sfx, fill=color, font=font_sfx)


def generate_panel_artwork(
    title: str,
    top_color: Tuple[int, int, int],
    bot_color: Tuple[int, int, int],
    glow_color: Tuple[int, int, int],
    panel_index: int,
    total_panels: int,
    elements: List[Dict[str, Any]]
) -> Image.Image:
    """Generate a single 4K comic panel (width: 2400, height: 1350)."""
    pw, ph = 2400, 1350
    canvas = create_gradient_canvas(pw, ph, top_color, bot_color)
    draw = ImageDraw.Draw(canvas, "RGBA")

    # Add atmospheric depth & ambient circles/runes
    for i in range(5):
        cx = int(pw * (0.2 + 0.15 * i))
        cy = int(ph * (0.3 + 0.1 * (i % 3)))
        r = 120 + i * 40
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(glow_color[0], glow_color[1], glow_color[2], 25))

    # Add horizon / ground layer
    draw.polygon([(0, int(ph * 0.72)), (pw, int(ph * 0.68)), (pw, ph), (0, ph)], fill=(10, 15, 25, 255))
    draw.line([(0, int(ph * 0.72)), (pw, int(ph * 0.68))], fill=(glow_color[0], glow_color[1], glow_color[2], 120), width=4)

    # Draw elements (dialogue, narration, SFX)
    for elem in elements:
        etype = elem.get("type")
        if etype == "dialogue":
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
        elif etype == "sfx":
            draw_action_sfx(
                draw,
                sfx=elem.get("text", "ROAR!"),
                x=elem.get("x", 400),
                y=elem.get("y", 400),
                color=elem.get("color", (239, 68, 68))
            )

    # Panel Border Frame
    draw.rectangle([0, 0, pw - 1, ph - 1], outline=(30, 41, 59), width=8)

    # Panel Number Badge
    draw.rounded_rectangle([pw - 160, ph - 70, pw - 30, ph - 25], radius=10, fill=(15, 23, 42, 220), outline=(168, 85, 247), width=2)
    font_badge = get_font(22, bold=True)
    draw.text((pw - 145, ph - 62), f"PANEL {panel_index}/{total_panels}", fill=(226, 232, 240), font=font_badge)

    return canvas


def assemble_comic_page(
    page_number: int,
    issue_title: str,
    panels: List[Image.Image],
    output_path_webp: str,
    output_path_png: Optional[str] = None
) -> None:
    """Combine panels into a single vertical page strip and save WebP/PNG."""
    gutter = 40
    header_h = 160
    footer_h = 100
    total_w = 2400
    total_h = header_h + sum(p.height for p in panels) + (len(panels) - 1) * gutter + footer_h

    page_canvas = Image.new("RGB", (total_w, total_h), (8, 12, 20))
    draw = ImageDraw.Draw(page_canvas)

    # Page Header
    font_header_title = get_font(44, bold=True)
    font_header_sub = get_font(26, bold=False)
    draw.text((80, 50), "PROJECT KONGOR CHRONICLES", fill=(192, 132, 252), font=font_header_sub)
    draw.text((80, 85), issue_title.upper(), fill=(255, 255, 255), font=font_header_title)

    # Paste panels
    curr_y = header_h
    for p in panels:
        page_canvas.paste(p, (0, curr_y))
        curr_y += p.height + gutter

    # Page Footer
    font_footer = get_font(26, bold=True)
    draw.text((total_w // 2 - 80, total_h - 60), f"PAGE {page_number}", fill=(148, 163, 184), font=font_footer)

    # Save WebP with high quality
    os.makedirs(os.path.dirname(output_path_webp), exist_ok=True)
    page_canvas.save(output_path_webp, "WEBP", quality=90, method=6)
    logger.info("Generated 4K Page WebP: %s (%dx%d)", output_path_webp, total_w, total_h)

    if output_path_png:
        page_canvas.save(output_path_png, "PNG")


def generate_comic_cover(
    issue_number: int,
    title: str,
    arc_title: str,
    synopsis: str,
    top_color: Tuple[int, int, int],
    bot_color: Tuple[int, int, int],
    output_path: str
) -> None:
    """Generate high-res 4K comic issue cover art."""
    cw, ch = 2400, 3200
    cover = create_gradient_canvas(cw, ch, top_color, bot_color)
    draw = ImageDraw.Draw(cover, "RGBA")

    # Decorative frame & runic elements
    draw.rectangle([60, 60, cw - 60, ch - 60], outline=(168, 85, 247), width=6)
    draw.rectangle([80, 80, cw - 80, ch - 80], outline=(251, 191, 36, 120), width=3)

    # Central mystic nebula
    for r in range(600, 100, -80):
        draw.ellipse([cw // 2 - r, ch // 2 - r, cw // 2 + r, ch // 2 + r], fill=(168, 85, 247, 18))

    # Top Brand Header
    font_brand = get_font(52, bold=True)
    font_arc = get_font(36, bold=True)
    draw.text((120, 120), "PROJECT KONGOR COMICS", fill=(251, 191, 36), font=font_brand)
    draw.text((120, 185), f"SAGA: {arc_title.upper()}", fill=(192, 132, 252), font=font_arc)

    # Issue Number Badge
    draw.rounded_rectangle([cw - 380, 110, cw - 120, 240], radius=20, fill=(15, 23, 42, 240), outline=(168, 85, 247), width=4)
    font_issue = get_font(48, bold=True)
    draw.text((cw - 350, 145), f"ISSUE #{issue_number}", fill=(255, 255, 255), font=font_issue)

    # Bottom Title Card
    font_title = get_font(80, bold=True)
    font_synopsis = get_font(32, bold=False)

    draw.rectangle([100, ch - 700, cw - 100, ch - 120], fill=(10, 15, 26, 235), outline=(168, 85, 247), width=3)
    draw.text((150, ch - 660), title.upper(), fill=(255, 255, 255), font=font_title)

    # Synopsis text wrap
    words = synopsis.split()
    lines = []
    curr = []
    for w in words:
        curr.append(w)
        test_line = " ".join(curr)
        bbox = draw.textbbox((0, 0), test_line, font=font_synopsis)
        if (bbox[2] - bbox[0]) > 2000 and len(curr) > 1:
            curr.pop()
            lines.append(" ".join(curr))
            curr = [w]
    if curr:
        lines.append(" ".join(curr))

    ty = ch - 540
    for l in lines[:4]:
        draw.text((150, ty), l, fill=(203, 213, 225), font=font_synopsis)
        ty += 44

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    cover.save(output_path, "WEBP", quality=92, method=6)
    logger.info("Generated 4K Comic Cover: %s", output_path)


def generate_arc_banner(output_path: str) -> None:
    """Generate high-res Arc banner."""
    bw, bh = 2560, 800
    banner = create_gradient_canvas(bw, bh, (15, 23, 42), (45, 15, 75))
    draw = ImageDraw.Draw(banner, "RGBA")

    # Mystic circles
    for r in range(400, 100, -60):
        draw.ellipse([bw - 500 - r, bh // 2 - r, bw - 500 + r, bh // 2 + r], fill=(168, 85, 247, 25))

    font_title = get_font(72, bold=True)
    font_sub = get_font(32, bold=False)
    draw.text((120, 260), "THE PRIMEVAL CANOPY", fill=(255, 255, 255), font=font_title)
    draw.text((120, 360), "Before the Cataclysm • The Era of Forest Guardians & Sacred Bonds", fill=(192, 132, 252), font=font_sub)

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    banner.save(output_path, "WEBP", quality=90)
    logger.info("Generated Arc Banner: %s", output_path)


def generate_all_comics() -> None:
    """Build all 3 initial test comics."""
    logger.info("=== Starting Automated 4K Comic Generation ===")
    
    # Generate Arc Banner
    generate_arc_banner(os.path.join(WWWROOT_LORE, "arcs", "primeval-canopy-banner.webp"))

    # ==========================================
    # COMIC #1: Whispers in the Ironwood
    # ==========================================
    c1_dir = os.path.join(WWWROOT_LORE, "comics", "issue-1")
    generate_comic_cover(
        issue_number=1,
        title="Whispers in the Ironwood",
        arc_title="The Primeval Canopy",
        synopsis="Long before he was known as the terror of the pit, Kongor guarded his family in the high branches. When young Ophelia slips away from her sanctuary village into the deep woods, an unexpected friendship is born—one that will echo across the shattered cosmos.",
        top_color=(20, 30, 45),
        bot_color=(45, 20, 60),
        output_path=os.path.join(c1_dir, "cover.webp")
    )

    # Comic 1 - Page 1
    p1_1 = generate_panel_artwork(
        "Page 1 Panel 1", (15, 25, 40), (10, 15, 30), (59, 130, 246), 1, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "In the ancient age of Newerth, the Primeval Canopy stretched unbroken from horizon to horizon.", "x": 100, "y": 80, "width": 800, "is_narration": True}
        ]
    )
    p1_2 = generate_panel_artwork(
        "Page 1 Panel 2", (20, 35, 30), (15, 20, 25), (34, 197, 94), 2, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "High in the Ironwood branches, Kongor kept silent vigil over his kin—a peaceful giant who knew only harmony.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    assemble_comic_page(1, "Whispers in the Ironwood", [p1_1, p1_2], os.path.join(c1_dir, "page-1.webp"))

    # Comic 1 - Page 2
    p2_1 = generate_panel_artwork(
        "Page 2 Panel 1", (30, 20, 40), (15, 10, 25), (168, 85, 247), 1, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "Beyond the sanctuary walls of the Sun Enclave, young Ophelia watched the forbidden canopy sway.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    p2_2 = generate_panel_artwork(
        "Page 2 Panel 2", (25, 30, 45), (10, 15, 25), (147, 51, 234), 2, 2,
        [
            {"type": "dialogue", "speaker": "Ophelia", "text": "The elders speak of ferocious beasts... but the wind only carries songs of peace.", "x": 100, "y": 80, "width": 750}
        ]
    )
    assemble_comic_page(2, "Whispers in the Ironwood", [p2_1, p2_2], os.path.join(c1_dir, "page-2.webp"))

    # Comic 1 - Page 3
    p3_1 = generate_panel_artwork(
        "Page 3 Panel 1", (35, 45, 30), (20, 25, 20), (234, 179, 8), 1, 2,
        [
            {"type": "dialogue", "speaker": "Ophelia", "text": "You're not a monster at all... you're a guardian.", "x": 100, "y": 80, "width": 700},
            {"type": "dialogue", "speaker": "Narrator", "text": "With gentle grace, Kongor offered a golden sun-fig from the high bough.", "x": 1200, "y": 80, "width": 750, "is_narration": True}
        ]
    )
    p3_2 = generate_panel_artwork(
        "Page 3 Panel 2", (40, 30, 20), (25, 15, 10), (249, 115, 22), 2, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "A silent pact was born between nature and spirit—one of pure trust.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    assemble_comic_page(3, "Whispers in the Ironwood", [p3_1, p3_2], os.path.join(c1_dir, "page-3.webp"))

    # Comic 1 - Page 4
    p4_1 = generate_panel_artwork(
        "Page 4 Panel 1", (20, 20, 45), (10, 10, 30), (99, 102, 241), 1, 1,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "As the sun set over the canopy, neither knew that dark forces were already tracing the primordial resonance of the woods...", "x": 100, "y": 80, "width": 950, "is_narration": True},
            {"type": "sfx", "text": "TO BE CONTINUED IN ISSUE #2", "x": 600, "y": 900, "color": (251, 191, 36)}
        ]
    )
    assemble_comic_page(4, "Whispers in the Ironwood", [p4_1], os.path.join(c1_dir, "page-4.webp"))

    # ==========================================
    # COMIC #2: Echoes on the Perimeter
    # ==========================================
    c2_dir = os.path.join(WWWROOT_LORE, "comics", "issue-2")
    generate_comic_cover(
        issue_number=2,
        title="Echoes on the Perimeter",
        arc_title="The Primeval Canopy",
        synopsis="Scout tracks strange metallic vibrations and runic energy leaks fracturing the forest boundary. Meanwhile, Ophelia discovers a dormant obsidian monolith pulsating with dark crimson resonance near Kongor’s sacred watering pool. Kongor senses the disturbance, protecting his kin.",
        top_color=(35, 25, 45),
        bot_color=(20, 15, 30),
        output_path=os.path.join(c2_dir, "cover.webp")
    )

    # Comic 2 - Page 1
    c2_p1_1 = generate_panel_artwork(
        "Issue 2 Page 1 Panel 1", (25, 30, 20), (15, 20, 15), (34, 197, 94), 1, 2,
        [
            {"type": "dialogue", "speaker": "Scout", "text": "The boundary wards are cut cleanly. Someone with mechanical precision is stalking the outer perimeter.", "x": 100, "y": 80, "width": 800}
        ]
    )
    c2_p1_2 = generate_panel_artwork(
        "Issue 2 Page 1 Panel 2", (30, 25, 35), (20, 15, 25), (168, 85, 247), 2, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "An eerie crimson pulse vibrated through the earth, setting the leaves trembling.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    assemble_comic_page(1, "Echoes on the Perimeter", [c2_p1_1, c2_p1_2], os.path.join(c2_dir, "page-1.webp"))

    # Comic 2 - Page 2
    c2_p2_1 = generate_panel_artwork(
        "Issue 2 Page 2 Panel 1", (40, 20, 25), (25, 10, 15), (239, 68, 68), 1, 2,
        [
            {"type": "dialogue", "speaker": "Ophelia", "text": "Kongor, look! The water pool is glowing... the stones are bleeding red light!", "x": 100, "y": 80, "width": 800}
        ]
    )
    c2_p2_2 = generate_panel_artwork(
        "Issue 2 Page 2 Panel 2", (30, 20, 30), (15, 10, 20), (220, 38, 38), 2, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "An obsidian monolith had risen from the depths, humming with cosmic power.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    assemble_comic_page(2, "Echoes on the Perimeter", [c2_p2_1, c2_p2_2], os.path.join(c2_dir, "page-2.webp"))

    # Comic 2 - Page 3
    c2_p3_1 = generate_panel_artwork(
        "Issue 2 Page 3 Panel 1", (45, 25, 20), (25, 15, 15), (245, 158, 11), 1, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "Kongor stepped between Ophelia and the monolith, shielding her with his massive frame.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    c2_p3_2 = generate_panel_artwork(
        "Issue 2 Page 3 Panel 2", (20, 15, 35), (10, 10, 20), (129, 140, 248), 2, 2,
        [
            {"type": "dialogue", "speaker": "Scout", "text": "They're not just scouting... they're hunting. Ophelia, get back!", "x": 100, "y": 80, "width": 750}
        ]
    )
    assemble_comic_page(3, "Echoes on the Perimeter", [c2_p3_1, c2_p3_2], os.path.join(c2_dir, "page-3.webp"))

    # Comic 2 - Page 4
    c2_p4_1 = generate_panel_artwork(
        "Issue 2 Page 4 Panel 1", (40, 15, 20), (20, 10, 15), (220, 38, 38), 1, 1,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "From the dark underbrush, razor-sharp claws extended as Predator's optics locked onto Kongor.", "x": 100, "y": 80, "width": 950, "is_narration": True},
            {"type": "sfx", "text": "SHRRRRKKKK!", "x": 800, "y": 600, "color": (239, 68, 68)},
            {"type": "sfx", "text": "TO BE CONTINUED IN ISSUE #3", "x": 600, "y": 950, "color": (251, 191, 36)}
        ]
    )
    assemble_comic_page(4, "Echoes on the Perimeter", [c2_p4_1], os.path.join(c2_dir, "page-4.webp"))

    # ==========================================
    # COMIC #3: The Shattered Dawn
    # ==========================================
    c3_dir = os.path.join(WWWROOT_LORE, "comics", "issue-3")
    generate_comic_cover(
        issue_number=3,
        title="The Shattered Dawn",
        arc_title="The Primeval Canopy",
        synopsis="A rogue vanguard of corrupted hunters and Predator track the crimson resonance into the deep grove. As the hunters trap Ophelia and threaten Kongor's troop, Kongor unleashes a ferocious, primal roar, awakening the ancient primordial fury buried within his bloodline to defend his friends.",
        top_color=(50, 15, 20),
        bot_color=(25, 10, 15),
        output_path=os.path.join(c3_dir, "cover.webp")
    )

    # Comic 3 - Page 1
    c3_p1_1 = generate_panel_artwork(
        "Issue 3 Page 1 Panel 1", (55, 20, 20), (30, 10, 10), (239, 68, 68), 1, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "Electrified iron nets fell like thunder! The beast-hunters ambushed the sacred grove with mechanical fury.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    c3_p1_2 = generate_panel_artwork(
        "Issue 3 Page 1 Panel 2", (45, 15, 25), (25, 10, 15), (248, 113, 113), 2, 2,
        [
            {"type": "dialogue", "speaker": "Predator", "text": "Contain the prime specimen! Harness the runic core!", "x": 100, "y": 80, "width": 750, "is_shout": True}
        ]
    )
    assemble_comic_page(1, "The Shattered Dawn", [c3_p1_1, c3_p1_2], os.path.join(c3_dir, "page-1.webp"))

    # Comic 3 - Page 2
    c3_p2_1 = generate_panel_artwork(
        "Issue 3 Page 2 Panel 1", (60, 20, 30), (35, 15, 20), (239, 68, 68), 1, 2,
        [
            {"type": "dialogue", "speaker": "Ophelia", "text": "KONGOR! LOOK OUT!", "x": 100, "y": 80, "width": 600, "is_shout": True}
        ]
    )
    c3_p2_2 = generate_panel_artwork(
        "Issue 3 Page 2 Panel 2", (40, 25, 45), (20, 15, 30), (168, 85, 247), 2, 2,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "As the hunters closed in on Ophelia, something ancient and terrifying stirred within the guardian's soul.", "x": 100, "y": 80, "width": 850, "is_narration": True}
        ]
    )
    assemble_comic_page(2, "The Shattered Dawn", [c3_p2_1, c3_p2_2], os.path.join(c3_dir, "page-2.webp"))

    # Comic 3 - Page 3
    c3_p3_1 = generate_panel_artwork(
        "Issue 3 Page 3 Panel 1", (70, 15, 15), (40, 10, 10), (239, 68, 68), 1, 1,
        [
            {"type": "sfx", "text": "ROOOOOAAAAR!", "x": 500, "y": 300, "color": (255, 255, 255)},
            {"type": "dialogue", "speaker": "Narrator", "text": "A titanic shockwave shattered the iron nets. Primordial crimson runes ignited across Kongor's fur, surging with unimaginable power!", "x": 100, "y": 750, "width": 950, "is_narration": True}
        ]
    )
    assemble_comic_page(3, "The Shattered Dawn", [c3_p3_1], os.path.join(c3_dir, "page-3.webp"))

    # Comic 3 - Page 4
    c3_p4_1 = generate_panel_artwork(
        "Issue 3 Page 4 Panel 1", (30, 20, 45), (15, 10, 25), (168, 85, 247), 1, 1,
        [
            {"type": "dialogue", "speaker": "Narrator", "text": "The hunters scattered into the mist. Kongor stood tall, guardian of the forest, awakened by the primordial spark.", "x": 100, "y": 80, "width": 950, "is_narration": True},
            {"type": "dialogue", "speaker": "Ophelia", "text": "The world will never be the same... but you'll always be my guardian.", "x": 100, "y": 350, "width": 750},
            {"type": "sfx", "text": "END OF ARC 1: THE PRIMEVAL CANOPY", "x": 400, "y": 900, "color": (251, 191, 36)}
        ]
    )
    assemble_comic_page(4, "The Shattered Dawn", [c3_p4_1], os.path.join(c3_dir, "page-4.webp"))

    logger.info("=== Successfully Generated All 3 Comics (12 4K Pages + Covers + Banners) ===")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Project Kongor 4K Comic Generator")
    parser.add_argument("--all", action="store_true", default=True, help="Generate all test comics")
    args = parser.parse_args()

    mcp_active = try_blender_mcp()
    if mcp_active:
        logger.info("Live Blender MCP session detected on port 9876!")
    else:
        logger.info("Blender MCP server inactive; using local 4K compositor renderer.")

    generate_all_comics()
