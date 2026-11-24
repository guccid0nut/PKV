# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth MDF Entity Definition Parser.
Parser for HoN .mdf XML files specifying model LODs, attachments, animation clips, and frame events.
"""

from __future__ import annotations

import io
import logging
import os
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from typing import Dict, List, Optional, TextIO, Union

logger = logging.getLogger("io_scene_hon.mdf_parser")


@dataclass
class HonMdfFrameEvent:
    """Event triggered at a specific animation frame (audio, particle effect, gameplay trigger)."""
    frame: int = 0
    cmd: str = ""
    sound_path: Optional[str] = None
    effect_path: Optional[str] = None


@dataclass
class HonMdfAnim:
    """Animation track mapping in MDF entity definition."""
    name: str = ""
    clip_path: str = ""
    fps: int = 30
    loop: bool = True
    loopback_frame: int = 1
    blend_time: int = 100
    num_frames: int = 0
    frame_events: List[HonMdfFrameEvent] = field(default_factory=list)


@dataclass
class HonMdfAttachment:
    """Attached sub-model/accessory bound to a specific bone."""
    bone: str = ""
    model_path: str = ""


@dataclass
class HonMdfData:
    """Root container representing a complete HoN entity model definition (.mdf)."""
    name: str = ""
    model_file: str = ""
    high_model: str = ""
    med_model: str = ""
    low_model: str = ""
    attachments: List[HonMdfAttachment] = field(default_factory=list)
    animations: List[HonMdfAnim] = field(default_factory=list)


def read_mdf(source: Union[str, bytes, TextIO]) -> HonMdfData:
    """
    Parse a HoN .mdf XML entity definition file.
    
    Args:
        source: File path (str), raw XML bytes/string, or text stream.
        
    Returns:
        HonMdfData containing model filenames, attachments, and animation clip references.
    """
    if isinstance(source, str):
        if os.path.exists(source):
            with open(source, "r", encoding="utf-8", errors="replace") as f:
                content = f.read()
        else:
            content = source
    elif isinstance(source, bytes):
        content = source.decode("utf-8", errors="replace")
    elif hasattr(source, "read"):
        content = source.read()
        if isinstance(content, bytes):
            content = content.decode("utf-8", errors="replace")
    else:
        raise TypeError(f"Unsupported source type for MDF parser: {type(source)}")

    root = ET.fromstring(content)
    mdf = HonMdfData()

    mdf.name = root.attrib.get("name", "")
    mdf.model_file = root.attrib.get("high") or root.attrib.get("file") or root.attrib.get("med") or root.attrib.get("low") or ""
    mdf.high_model = root.attrib.get("high", "")
    mdf.med_model = root.attrib.get("med", "")
    mdf.low_model = root.attrib.get("low", "")

    # Parse attachments
    for attach_el in root.findall("attachment"):
        bone = attach_el.attrib.get("bone", "")
        model_path = attach_el.attrib.get("model") or attach_el.attrib.get("file") or ""
        if bone and model_path:
            mdf.attachments.append(HonMdfAttachment(bone=bone, model_path=model_path))

    # Parse animation definitions
    for anim_el in root.findall("anim"):
        anim_name = anim_el.attrib.get("name", "")
        clip_path = anim_el.attrib.get("clip", "")
        if not anim_name or not clip_path:
            continue

        fps = 30
        fps_attr = anim_el.attrib.get("fps")
        if fps_attr:
            try:
                fps = int(fps_attr)
            except ValueError:
                pass

        loop = True
        loop_attr = anim_el.attrib.get("loop")
        if loop_attr:
            loop = loop_attr.lower() in ("true", "1")

        loopback_frame = 1
        lbf_attr = anim_el.attrib.get("loopbackframe")
        if lbf_attr:
            try:
                loopback_frame = int(lbf_attr)
            except ValueError:
                pass

        blend_time = 100
        bt_attr = anim_el.attrib.get("blendtime")
        if bt_attr:
            try:
                blend_time = int(bt_attr)
            except ValueError:
                pass

        anim = HonMdfAnim(
            name=anim_name,
            clip_path=clip_path,
            fps=fps,
            loop=loop,
            loopback_frame=loopback_frame,
            blend_time=blend_time
        )

        # Parse frame events inside anim
        for fe_el in anim_el.findall("frameevent"):
            frame = 0
            f_attr = fe_el.attrib.get("frame")
            if f_attr:
                try:
                    frame = int(f_attr)
                except ValueError:
                    pass

            cmd = fe_el.attrib.get("cmd", "")
            sound_path = None
            effect_path = None

            if cmd.startswith("PlaySound") or cmd.startswith("PlaySoundLinear"):
                parts = cmd.split()
                if len(parts) > 1:
                    sound_path = parts[1].replace("%", "1")
            elif cmd.startswith("StartEffect"):
                parts = cmd.split()
                if len(parts) > 1:
                    effect_path = parts[1]

            anim.frame_events.append(HonMdfFrameEvent(
                frame=frame,
                cmd=cmd,
                sound_path=sound_path,
                effect_path=effect_path
            ))

        mdf.animations.append(anim)

    logger.info("Parsed MDF entity '%s': model='%s', %d animations, %d attachments",
                mdf.name, mdf.model_file, len(mdf.animations), len(mdf.attachments))
    return mdf


def resolve_mdf_hierarchy(mdf_file_path: str) -> Dict[str, any]:
    """
    Given a path to a .mdf file, resolve the absolute paths to its high/med/low .model
    and all associated .clip animation files.
    """
    mdf_dir = os.path.dirname(os.path.abspath(mdf_file_path))
    mdf = read_mdf(mdf_file_path)

    resolved_model = None
    candidates = [mdf.model_file, mdf.high_model, mdf.med_model, mdf.low_model, "high.model", "model.model"]
    for c in candidates:
        if c:
            candidate_path = os.path.normpath(os.path.join(mdf_dir, c))
            if os.path.exists(candidate_path):
                resolved_model = candidate_path
                break

    resolved_anims: Dict[str, str] = {}
    for anim in mdf.animations:
        clip_rel = anim.clip_path.replace("\\", "/")
        clip_path = os.path.normpath(os.path.join(mdf_dir, clip_rel))
        if os.path.exists(clip_path):
            resolved_anims[anim.name] = clip_path

    return {
        "mdf": mdf,
        "mdf_dir": mdf_dir,
        "model_path": resolved_model,
        "clip_paths": resolved_anims
    }
