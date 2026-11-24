# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Project Kongor: Viewport Streamer - Telemetry Reader
File: src/tools/viewport-streamer/telemetry_reader.py

Connects to the Phase 2 Seqlock Fast-Bus Shared Memory (Local\\KongorTelemetryBuffer)
to extract sub-100ns game engine metrics (FPS, CPU/GPU Frame Time, Ping, Server Tick, Entities).
Falls back to synthetic metrics when running standalone or in CI.
"""

import ctypes
from ctypes import wintypes
import struct
import time
import math
from typing import Optional, Dict, Any

FILE_MAP_READ = 0x0004
KONGOR_SHM_NAME = b"Local\\KongorTelemetryBuffer"
KONGOR_MAGIC = 0x4B4F4E47  # 'KONG'

# FrameTelemetryRecord struct format: 64 bytes
# uint64 FrameIndex, int64 TimestampQpc, float CpuFrameTimeMs, float GpuFrameTimeMs, float InputLatencyMs,
# uint32 DrawCallCount, uint32 ActiveEntities, uint32 ParticlesAlive, uint32 PingMicroseconds,
# float PacketLossPercent, uint32 MemoryWorkingSetMb, uint32 AudioVoicesActive, uint32 ServerTick, uint32 ReservedFlags
TELEMETRY_RECORD_FORMAT = "<QqfffIIIIIfIIII"
TELEMETRY_RECORD_SIZE = struct.calcsize(TELEMETRY_RECORD_FORMAT)  # 64 bytes

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


class TelemetryReader:
    def __init__(self):
        self.h_map: Optional[wintypes.HANDLE] = None
        self.p_view: Optional[int] = None
        self.is_connected = False
        self.mock_tick = 0

    def connect(self) -> bool:
        """Attempts to open Local\\KongorTelemetryBuffer."""
        self.h_map = OpenFileMappingA(FILE_MAP_READ, False, KONGOR_SHM_NAME)
        if not self.h_map:
            return False

        # Header V2 size is 192 bytes, ring buffer is 4096 * 64 bytes
        total_size = 192 + (4096 * 64)
        self.p_view = MapViewOfFile(self.h_map, FILE_MAP_READ, 0, 0, total_size)
        if not self.p_view:
            CloseHandle(self.h_map)
            self.h_map = None
            return False

        # Verify Magic
        magic_bytes = ctypes.string_at(self.p_view, 4)
        magic = struct.unpack("<I", magic_bytes)[0]
        if magic != KONGOR_MAGIC:
            self.close()
            return False

        self.is_connected = True
        return True

    def read_latest(self) -> Dict[str, Any]:
        """Reads latest telemetry snapshot using Seqlock consistency, or returns synthetic stats."""
        if not self.is_connected or not self.p_view:
            if not self.connect():
                return self.get_synthetic_telemetry()

        # Cache Line 1 (offset 64..127): Seqlock Sequence is at offset 64
        # Cache Line 2 (offset 128..191): Atomic Latest Snapshot (64 bytes)
        seq_ptr = self.p_view + 64
        snapshot_ptr = self.p_view + 128

        for attempt in range(3):
            seq1 = struct.unpack("<Q", ctypes.string_at(seq_ptr, 8))[0]
            if (seq1 & 1) != 0:
                # Writer is updating snapshot, retry
                continue

            rec_bytes = ctypes.string_at(snapshot_ptr, TELEMETRY_RECORD_SIZE)
            seq2 = struct.unpack("<Q", ctypes.string_at(seq_ptr, 8))[0]

            if seq1 == seq2:
                # Seqlock consistency verified!
                unpacked = struct.unpack(TELEMETRY_RECORD_FORMAT, rec_bytes)
                cpu_ms = unpacked[2]
                gpu_ms = unpacked[3]
                fps = 1000.0 / max(cpu_ms, 0.1) if cpu_ms > 0 else 144.0

                return {
                    "source": "SHM_SEQLOCK",
                    "frame_index": unpacked[0],
                    "cpu_ms": round(cpu_ms, 2),
                    "gpu_ms": round(gpu_ms, 2),
                    "fps": round(fps, 1),
                    "input_latency_ms": round(unpacked[4], 2),
                    "draw_calls": unpacked[5],
                    "active_entities": unpacked[6],
                    "particles_alive": unpacked[7],
                    "ping_ms": round(unpacked[8] / 1000.0, 1),
                    "packet_loss_pct": round(unpacked[9], 2),
                    "memory_mb": unpacked[10],
                    "audio_voices": unpacked[11],
                    "server_tick": unpacked[12],
                    "ptt_active": bool(unpacked[13] & 0x1)
                }

        return self.get_synthetic_telemetry()

    def get_synthetic_telemetry(self) -> Dict[str, Any]:
        """Provides smooth synthetic metrics for standalone viewing / headless CI."""
        self.mock_tick += 1
        t = time.time()
        fps = 144.0 + math.sin(t * 1.5) * 5.0
        frame_ms = 1000.0 / fps

        return {
            "source": "SYNTHETIC_SIMULATOR",
            "frame_index": self.mock_tick,
            "cpu_ms": round(frame_ms * 0.45, 2),
            "gpu_ms": round(frame_ms * 0.55, 2),
            "fps": round(fps, 1),
            "input_latency_ms": round(4.2 + math.sin(t) * 0.5, 2),
            "draw_calls": 342,
            "active_entities": 84,
            "particles_alive": 128,
            "ping_ms": round(24.5 + math.sin(t * 0.5) * 2.0, 1),
            "packet_loss_pct": 0.0,
            "memory_mb": 1420,
            "audio_voices": 18,
            "server_tick": self.mock_tick * 2,
            "ptt_active": False,
            "score_legion": 14,
            "score_hellbourne": 11,
            "game_time": "18:42"
        }

    def close(self):
        if self.p_view:
            UnmapViewOfFile(self.p_view)
            self.p_view = None
        if self.h_map:
            CloseHandle(self.h_map)
            self.h_map = None
        self.is_connected = False
