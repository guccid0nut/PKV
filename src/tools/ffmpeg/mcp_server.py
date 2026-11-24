#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
FFmpeg Media Pipeline MCP Server for ProjectKongor.
Bridges MCP tools to ffmpeg/ffprobe for media inspection, clip slicing,
audio transcoding, and frame extraction.
"""

import argparse
import asyncio
import json
import os
import pathlib
import shutil
import subprocess
import sys
from typing import Any, Dict, List, Optional

_TOOLS_DIR = str(pathlib.Path(__file__).resolve().parents[1])
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

from _common.mcp_telemetry import (
    create_fastmcp_server,
    resolve_tool_binary,
    run_fastmcp_stdio,
    shield_stdio_stdout,
)

shield_stdio_stdout()

DEFAULT_FFMPEG = pathlib.Path(r"C:\ProgramData\chocolatey\bin\ffmpeg.exe")
DEFAULT_FFPROBE = pathlib.Path(r"C:\ProgramData\chocolatey\bin\ffprobe.exe")

FFMPEG_EXE = resolve_tool_binary(
    ("ffmpeg.exe", "ffmpeg"),
    env_var="FFMPEG_PATH",
    subdir_hints=("ffmpeg",),
    extra_candidates=(DEFAULT_FFMPEG,),
)
FFPROBE_EXE = resolve_tool_binary(
    ("ffprobe.exe", "ffprobe"),
    env_var="FFPROBE_PATH",
    subdir_hints=("ffmpeg",),
    extra_candidates=(DEFAULT_FFPROBE,),
)

server = create_fastmcp_server("ffmpeg-pipeline")

def _run_cmd(cmd: List[str], timeout: float = 60.0) -> subprocess.CompletedProcess[str]:
    """Run an external CLI command synchronously with timeout."""
    return subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False
    )

@server.tool()
async def ffmpeg_probe_media(file_path: str) -> Dict[str, Any]:
    """
    Inspect a media container (video or audio) using ffprobe.
    Returns streams, codecs, duration, bitrate, resolution, framerate, and audio channels.
    """
    abs_path = os.path.abspath(file_path)
    if not os.path.isfile(abs_path):
        return {"error": f"File not found: {abs_path}"}

    cmd = [
        FFPROBE_EXE,
        "-v", "quiet",
        "-print_format", "json",
        "-show_format",
        "-show_streams",
        abs_path
    ]

    proc = await asyncio.to_thread(_run_cmd, cmd, 30.0)
    if proc.returncode != 0:
        return {"error": f"ffprobe failed with code {proc.returncode}: {proc.stderr.strip()}"}

    try:
        data = json.loads(proc.stdout)
    except Exception as ex:
        return {"error": f"Failed to parse ffprobe output: {ex}", "raw": proc.stdout}

    fmt = data.get("format", {})
    streams = data.get("streams", [])

    video_streams = []
    audio_streams = []
    for s in streams:
        codec_type = s.get("codec_type")
        if codec_type == "video":
            video_streams.append({
                "index": s.get("index"),
                "codec_name": s.get("codec_name"),
                "width": s.get("width"),
                "height": s.get("height"),
                "fps": s.get("r_frame_rate"),
                "pixel_format": s.get("pix_fmt"),
                "duration": s.get("duration"),
                "bit_rate": s.get("bit_rate")
            })
        elif codec_type == "audio":
            audio_streams.append({
                "index": s.get("index"),
                "codec_name": s.get("codec_name"),
                "sample_rate": s.get("sample_rate"),
                "channels": s.get("channels"),
                "channel_layout": s.get("channel_layout"),
                "duration": s.get("duration"),
                "bit_rate": s.get("bit_rate")
            })

    return {
        "file": abs_path,
        "format_name": fmt.get("format_name"),
        "duration_seconds": float(fmt.get("duration", 0.0)) if fmt.get("duration") else None,
        "size_bytes": int(fmt.get("size", 0)) if fmt.get("size") else os.path.getsize(abs_path),
        "bit_rate": int(fmt.get("bit_rate", 0)) if fmt.get("bit_rate") else None,
        "video_streams": video_streams,
        "audio_streams": audio_streams
    }

@server.tool()
async def ffmpeg_slice_clip(
    input_path: str,
    output_path: str,
    start_time: str,
    duration: Optional[str] = None,
    end_time: Optional[str] = None,
    reencode: bool = False
) -> Dict[str, Any]:
    """
    Slice a media clip starting at start_time (e.g. '00:01:23.5' or seconds '83.5').
    If reencode is False (default), performs lossless copy stream slicing (-c copy).
    """
    abs_in = os.path.abspath(input_path)
    abs_out = os.path.abspath(output_path)
    if not os.path.isfile(abs_in):
        return {"error": f"Input file not found: {abs_in}"}

    os.makedirs(os.path.dirname(abs_out), exist_ok=True)

    cmd = [FFMPEG_EXE, "-y", "-ss", start_time, "-i", abs_in]

    if duration:
        cmd.extend(["-t", duration])
    elif end_time:
        cmd.extend(["-to", end_time])

    if not reencode:
        cmd.extend(["-c", "copy"])

    cmd.append(abs_out)

    proc = await asyncio.to_thread(_run_cmd, cmd, 120.0)
    if proc.returncode != 0:
        return {"error": f"ffmpeg slice failed: {proc.stderr.strip()}", "cmd": cmd}

    return {
        "success": True,
        "output_file": abs_out,
        "output_size_bytes": os.path.getsize(abs_out) if os.path.isfile(abs_out) else 0
    }

@server.tool()
async def ffmpeg_transcode_audio(
    input_path: str,
    output_path: str,
    codec: str = "libmp3lame",
    bitrate: str = "192k",
    sample_rate: Optional[int] = 44100,
    channels: Optional[int] = None
) -> Dict[str, Any]:
    """
    Transcode an audio file or extract audio from video (e.g. to mp3, wav, ogg, flac).
    channels: 1 for Mono (in-world 3D spatial), 2 for Stereo (2D UI/Announcers).
    """
    abs_in = os.path.abspath(input_path)
    abs_out = os.path.abspath(output_path)
    if not os.path.isfile(abs_in):
        return {"error": f"Input file not found: {abs_in}"}

    os.makedirs(os.path.dirname(abs_out), exist_ok=True)

    cmd = [FFMPEG_EXE, "-y", "-i", abs_in, "-vn"]

    # Select audio codec based on output extension if default
    ext = pathlib.Path(abs_out).suffix.lower()
    if codec == "libmp3lame" or codec == "mp3":
        cmd.extend(["-c:a", "libmp3lame", "-b:a", bitrate])
    elif codec in ("pcm_s16le", "wav") or ext == ".wav":
        cmd.extend(["-c:a", "pcm_s16le"])
    elif codec in ("libvorbis", "ogg") or ext == ".ogg":
        cmd.extend(["-c:a", "libvorbis", "-b:a", bitrate])
    elif codec in ("flac") or ext == ".flac":
        cmd.extend(["-c:a", "flac"])
    elif codec in ("aac") or ext == ".m4a":
        cmd.extend(["-c:a", "aac", "-b:a", bitrate])
    else:
        cmd.extend(["-c:a", codec])

    if sample_rate:
        cmd.extend(["-ar", str(sample_rate)])

    if channels:
        cmd.extend(["-ac", str(channels)])

    cmd.append(abs_out)

    proc = await asyncio.to_thread(_run_cmd, cmd, 120.0)
    if proc.returncode != 0:
        return {"error": f"ffmpeg audio transcode failed: {proc.stderr.strip()}", "cmd": cmd}

    return {
        "success": True,
        "output_file": abs_out,
        "output_size_bytes": os.path.getsize(abs_out) if os.path.isfile(abs_out) else 0
    }

@server.tool()
async def ffmpeg_generate_thumbnail(
    input_path: str,
    output_path: str,
    timestamp: str = "00:00:01",
    width: Optional[int] = None
) -> Dict[str, Any]:
    """
    Extract a single frame thumbnail image (PNG or JPG) from a video at timestamp.
    """
    abs_in = os.path.abspath(input_path)
    abs_out = os.path.abspath(output_path)
    if not os.path.isfile(abs_in):
        return {"error": f"Input file not found: {abs_in}"}

    os.makedirs(os.path.dirname(abs_out), exist_ok=True)

    cmd = [FFMPEG_EXE, "-y", "-ss", timestamp, "-i", abs_in, "-frames:v", "1"]

    if width:
        cmd.extend(["-vf", f"scale={width}:-1"])

    cmd.append(abs_out)

    proc = await asyncio.to_thread(_run_cmd, cmd, 30.0)
    if proc.returncode != 0:
        return {"error": f"ffmpeg thumbnail failed: {proc.stderr.strip()}", "cmd": cmd}

    return {
        "success": True,
        "output_file": abs_out,
        "output_size_bytes": os.path.getsize(abs_out) if os.path.isfile(abs_out) else 0
    }

@server.tool()
async def ffmpeg_extract_frames(
    input_path: str,
    output_dir: str,
    filename_prefix: str = "frame",
    fps: float = 1.0,
    max_frames: int = 30
) -> Dict[str, Any]:
    """
    Extract a sequence of video frames into images at a specified frame rate (fps),
    capped at max_frames to avoid flooding storage.
    """
    abs_in = os.path.abspath(input_path)
    abs_dir = os.path.abspath(output_dir)
    if not os.path.isfile(abs_in):
        return {"error": f"Input file not found: {abs_in}"}

    os.makedirs(abs_dir, exist_ok=True)
    out_pattern = os.path.join(abs_dir, f"{filename_prefix}_%04d.png")

    cmd = [
        FFMPEG_EXE, "-y",
        "-i", abs_in,
        "-vf", f"fps={fps}",
        "-frames:v", str(max_frames),
        out_pattern
    ]

    proc = await asyncio.to_thread(_run_cmd, cmd, 60.0)
    if proc.returncode != 0:
        return {"error": f"ffmpeg extract frames failed: {proc.stderr.strip()}", "cmd": cmd}

    extracted_files = sorted([
        os.path.join(abs_dir, f)
        for f in os.listdir(abs_dir)
        if f.startswith(filename_prefix) and f.endswith(".png")
    ])

    return {
        "success": True,
        "extracted_count": len(extracted_files),
        "files": extracted_files
    }

def main():
    run_fastmcp_stdio(server, "ffmpeg")

if __name__ == "__main__":
    main()
