# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Project Kongor: Viewport Streamer - Vulkan Shared Memory (HONCAP) Frame Reader
File: src/tools/viewport-streamer/honcap_reader.py

Discovers and maps the Windows Named Shared Memory created by VkLayer_hon_replay_capture.dll,
synchronizes with ready/done named events, and unpacks 64-byte HONCAP_FRAME_HEADERs
and raw BGRA/RGBA frame buffers. Also provides synthetic fallback generation for headless CI.
"""

import ctypes
from ctypes import wintypes
import struct
import os
import json
import time
import math
from typing import Optional, Tuple, Dict, Any
from PIL import Image, ImageDraw, ImageFont
import io

# Win32 Constants
FILE_MAP_READ = 0x0004
WAIT_OBJECT_0 = 0x00000000
WAIT_TIMEOUT = 0x00000102
SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002

HON_CAPTURE_MAGIC = 0x484F4E43  # "HONC"
HONCAP_HEADER_FORMAT = "<IIIIIIQQIII12s"
HONCAP_HEADER_SIZE = struct.calcsize(HONCAP_HEADER_FORMAT)  # 64 bytes

kernel32 = ctypes.windll.kernel32

OpenFileMappingA = kernel32.OpenFileMappingA
OpenFileMappingA.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCSTR]
OpenFileMappingA.restype = wintypes.HANDLE

MapViewOfFile = kernel32.MapViewOfFile
MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]
MapViewOfFile.restype = ctypes.c_void_p

UnmapViewOfFile = kernel32.UnmapViewOfFile
UnmapViewOfFile.argtypes = [ctypes.c_void_p]
UnmapViewOfFile.restype = wintypes.BOOL

CloseHandle = kernel32.CloseHandle
CloseHandle.argtypes = [wintypes.HANDLE]
CloseHandle.restype = wintypes.BOOL

OpenEventA = kernel32.OpenEventA
OpenEventA.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCSTR]
OpenEventA.restype = wintypes.HANDLE

WaitForSingleObject = kernel32.WaitForSingleObject
WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
WaitForSingleObject.restype = wintypes.DWORD

SetEvent = kernel32.SetEvent
SetEvent.argtypes = [wintypes.HANDLE]
SetEvent.restype = wintypes.BOOL


class HonCapFrame:
    def __init__(self, header: Dict[str, Any], raw_bytes: bytes):
        self.magic = header["magic"]
        self.version = header["version"]
        self.width = header["width"]
        self.height = header["height"]
        self.format = header["format"]
        self.row_pitch = header["row_pitch"]
        self.frame_index = header["frame_index"]
        self.timestamp_ns = header["timestamp_ns"]
        self.data_size_bytes = header["data_size_bytes"]
        self.slot_index = header["slot_index"]
        self.flags = header["flags"]
        self.raw_bytes = raw_bytes

    def to_jpeg(self, quality: int = 80) -> bytes:
        """Converts raw BGRA pixel bytes to compressed JPEG bytes."""
        if not self.raw_bytes or self.width <= 0 or self.height <= 0:
            return b""
        
        # PIL 'BGRX' or 'BGRA' mode
        img = Image.frombytes("RGBA", (self.width, self.height), self.raw_bytes, "raw", "BGRA")
        out = io.BytesIO()
        img.convert("RGB").save(out, format="JPEG", quality=quality)
        return out.getvalue()


class HonCapReader:
    def __init__(self, session_path: str = "artifacts/captures/honcap_active.json"):
        self.session_path = session_path
        self.h_map: Optional[wintypes.HANDLE] = None
        self.p_view: Optional[int] = None
        self.session_meta: Optional[Dict[str, Any]] = None
        self.ready_events: Dict[int, wintypes.HANDLE] = {}
        self.done_events: Dict[int, wintypes.HANDLE] = {}
        self.is_connected = False
        self.mock_frame_index = 0

    def discover_session(self) -> Optional[Dict[str, Any]]:
        """Reads artifacts/captures/honcap_active.json metadata."""
        if not os.path.exists(self.session_path):
            return None
        try:
            with open(self.session_path, "r", encoding="utf-8") as f:
                meta = json.load(f)
                if "pid" in meta and "map_name" in meta:
                    self.session_meta = meta
                    return meta
        except Exception:
            return None
        return None

    def connect(self) -> bool:
        """Connects to active HONCAP shared memory and opens event handles."""
        meta = self.discover_session()
        if not meta:
            return False

        map_name = meta["map_name"].encode("ascii")
        self.h_map = OpenFileMappingA(FILE_MAP_READ, False, map_name)
        if not self.h_map:
            return False

        total_bytes = meta.get("total_bytes", 0)
        self.p_view = MapViewOfFile(self.h_map, FILE_MAP_READ, 0, 0, total_bytes)
        if not self.p_view:
            CloseHandle(self.h_map)
            self.h_map = None
            return False

        pid = meta["pid"]
        slots = meta.get("slots", 3)
        for s in range(slots):
            ready_name = f"Local\\HonFrameReady_{pid}_{s}".encode("ascii")
            done_name = f"Local\\HonFrameDone_{pid}_{s}".encode("ascii")

            h_ready = OpenEventA(SYNCHRONIZE, False, ready_name)
            h_done = OpenEventA(EVENT_MODIFY_STATE, False, done_name)

            if h_ready:
                self.ready_events[s] = h_ready
            if h_done:
                self.done_events[s] = h_done

        self.is_connected = True
        return True

    def read_latest_frame(self, timeout_ms: int = 16) -> Optional[HonCapFrame]:
        """Polls for the next available frame across the ring buffer slots."""
        if not self.is_connected or not self.p_view or not self.session_meta:
            return None

        slots = self.session_meta.get("slots", 3)
        slot_stride = self.session_meta.get("slot_stride", 0)

        # Check for any signaled ready event
        ready_slot = -1
        for s in range(slots):
            h_ready = self.ready_events.get(s)
            if h_ready:
                res = WaitForSingleObject(h_ready, 0)
                if res == WAIT_OBJECT_0:
                    ready_slot = s
                    break

        if ready_slot < 0:
            if timeout_ms > 0:
                time.sleep(timeout_ms / 1000.0)
            return None

        # Slot is ready: unpack header and pixels
        offset = ready_slot * slot_stride
        header_buf = ctypes.string_at(self.p_view + offset, HONCAP_HEADER_SIZE)
        unpacked = struct.unpack(HONCAP_HEADER_FORMAT, header_buf)

        magic = unpacked[0]
        if magic != HON_CAPTURE_MAGIC:
            return None

        hdr = {
            "magic": magic,
            "version": unpacked[1],
            "width": unpacked[2],
            "height": unpacked[3],
            "format": unpacked[4],
            "row_pitch": unpacked[5],
            "frame_index": unpacked[6],
            "timestamp_ns": unpacked[7],
            "data_size_bytes": unpacked[8],
            "slot_index": unpacked[9],
            "flags": unpacked[10]
        }

        pixel_data = ctypes.string_at(self.p_view + offset + HONCAP_HEADER_SIZE, hdr["data_size_bytes"])

        # Signal done event back to K2 Vulkan layer
        h_done = self.done_events.get(ready_slot)
        if h_done:
            SetEvent(h_done)

        return HonCapFrame(hdr, pixel_data)

    def generate_synthetic_frame(self, width: int = 960, height: int = 540) -> HonCapFrame:
        """Generates a synthetic frame for practice / headless CI testing when game is inactive."""
        self.mock_frame_index += 1
        t = time.time()

        # Create animated synthetic game viewport
        img = Image.new("RGBA", (width, height), (15, 18, 24, 255))
        draw = ImageDraw.Draw(img)

        # Ground grid lines
        grid_spacing = 40
        grid_offset = int((t * 60) % grid_spacing)
        for y in range(grid_offset, height, grid_spacing):
            draw.line([(0, y), (width, y)], fill=(30, 38, 52, 255), width=1)
        for x in range(0, width, grid_spacing):
            draw.line([(x, 0), (x, height)], fill=(30, 38, 52, 255), width=1)

        # Animated hero position indicator (Caliban / Pyromancer)
        cx = int(width / 2 + math.cos(t * 2.0) * 200)
        cy = int(height / 2 + math.sin(t * 2.0) * 120)

        # Glow ring
        draw.ellipse([cx - 35, cy - 35, cx + 35, cy + 35], outline=(0, 200, 255, 180), width=3)
        draw.ellipse([cx - 20, cy - 20, cx + 20, cy + 20], fill=(20, 140, 240, 255))

        # Opponent hero (Hellbourne Devourer)
        ox = int(width / 2 - math.cos(t * 1.5) * 180)
        oy = int(height / 2 - math.sin(t * 1.5) * 100)
        draw.ellipse([ox - 35, oy - 35, ox + 35, oy + 35], outline=(255, 60, 60, 180), width=3)
        draw.ellipse([ox - 20, oy - 20, ox + 20, oy + 20], fill=(220, 30, 40, 255))

        # Telemetry Text
        draw.text((25, 25), "PROJECT KONGOR: HEADLESS VULKAN VIEWPORT", fill=(255, 200, 50, 255))
        draw.text((25, 50), f"Mode: SYNTHETIC STREAM RELAY | Frame: {self.mock_frame_index}", fill=(180, 200, 220, 255))
        draw.text((25, 75), f"Resolution: {width}x{height} | Target: 60 FPS", fill=(100, 220, 120, 255))

        # Convert to BGRA raw bytes
        b, g, r, a = img.split()
        bgra_img = Image.merge("RGBA", (b, g, r, a))
        raw_bytes = bgra_img.tobytes()

        hdr = {
            "magic": HON_CAPTURE_MAGIC,
            "version": 1,
            "width": width,
            "height": height,
            "format": 44,
            "row_pitch": width * 4,
            "frame_index": self.mock_frame_index,
            "timestamp_ns": int(t * 1e9),
            "data_size_bytes": len(raw_bytes),
            "slot_index": self.mock_frame_index % 3,
            "flags": 0x1
        }

        return HonCapFrame(hdr, raw_bytes)

    def close(self):
        """Releases shared memory and event handles."""
        for h in self.ready_events.values():
            CloseHandle(h)
        for h in self.done_events.values():
            CloseHandle(h)
        self.ready_events.clear()
        self.done_events.clear()

        if self.p_view:
            UnmapViewOfFile(self.p_view)
            self.p_view = None
        if self.h_map:
            CloseHandle(self.h_map)
            self.h_map = None

        self.is_connected = False
