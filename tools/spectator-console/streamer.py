# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Project Kongor: Viewport Streamer Relay Service
File: tools/spectator-console/streamer.py

Asynchronously streams Vulkan-rendered game frames and Phase 2 Seqlock telemetry
to web browsers (both standalone and embedded in Dawnbringer) over WebSockets and HTTP.
"""

import asyncio
import argparse
import json
import os
import sys
import time
from typing import Set
from aiohttp import web, WSMsgType

from honcap_reader import HonCapReader
from telemetry_reader import TelemetryReader


LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1", "[::1]"}


def _is_loopback_request(request: web.Request) -> bool:
    if os.environ.get("KONGOR_SPECTATOR_ALLOW_REMOTE", "") == "1":
        return True
    raw_host = (request.headers.get("Host") or "").strip()
    if raw_host.startswith("["):
        end = raw_host.find("]")
        host_only = raw_host[: end + 1] if end != -1 else raw_host
    elif ":" in raw_host:
        host_only = raw_host.split(":", 1)[0]
    else:
        host_only = raw_host
    if host_only.lower() not in LOOPBACK_HOSTS:
        return False
    origin = (request.headers.get("Origin") or "").strip()
    if origin:
        from urllib.parse import urlparse
        parsed = urlparse(origin)
        if (parsed.hostname or "").lower() not in {"127.0.0.1", "localhost", "::1"}:
            return False
    return True


class ViewportStreamer:
    def __init__(self, host: str = "127.0.0.1", port: int = 8088, static_dir: str = os.path.dirname(os.path.abspath(__file__))):
        self.host = host
        self.port = port
        self.static_dir = os.path.abspath(static_dir)
        self.honcap = HonCapReader()
        self.telemetry = TelemetryReader()
        self.active_websockets: Set[web.WebSocketResponse] = set()
        self.is_running = False
        self.total_frames_broadcast = 0
        self.latest_jpeg = b""
        self.latest_telemetry = {}
        self.fps_counter = 0
        self.current_fps = 0.0
        self.last_fps_calc = time.time()

    async def init_app(self) -> web.Application:
        app = web.Application()
        app.router.add_get("/ws/stream", self.handle_websocket)
        app.router.add_get("/api/status", self.handle_status)
        app.router.add_get("/api/telemetry", self.handle_telemetry)
        app.router.add_get("/api/snapshot.jpg", self.handle_snapshot)

        # Serve spectator console web files
        if os.path.exists(self.static_dir):
            app.router.add_static("/", path=self.static_dir, show_index=True)
        else:
            print(f"[WARN] Static directory '{self.static_dir}' does not exist yet. Web UI will be unavailable.")

        return app

    async def handle_status(self, request: web.Request) -> web.Response:
        if not _is_loopback_request(request):
            return web.json_response({"error": "Forbidden: loopback host/origin required"}, status=403)
        status_data = {
            "status": "ONLINE",
            "active_clients": len(self.active_websockets),
            "total_frames": self.total_frames_broadcast,
            "fps": round(self.current_fps, 1),
            "vulkan_connected": self.honcap.is_connected,
            "seqlock_connected": self.telemetry.is_connected,
            "static_dir": self.static_dir
        }
        return web.json_response(status_data)

    async def handle_telemetry(self, request: web.Request) -> web.Response:
        if not _is_loopback_request(request):
            return web.json_response({"error": "Forbidden: loopback host/origin required"}, status=403)
        return web.json_response(self.latest_telemetry)

    async def handle_snapshot(self, request: web.Request) -> web.Response:
        if not _is_loopback_request(request):
            return web.Response(text="Forbidden", status=403)
        if not self.latest_jpeg:
            # Generate one synthetic frame if empty
            frame = self.honcap.generate_synthetic_frame()
            self.latest_jpeg = frame.to_jpeg(85)

        return web.Response(body=self.latest_jpeg, content_type="image/jpeg")

    async def handle_websocket(self, request: web.Request) -> web.WebSocketResponse:
        if not _is_loopback_request(request):
            raise web.HTTPForbidden(text="Forbidden: loopback host/origin required")
        ws = web.WebSocketResponse(heartbeat=15.0)
        await ws.prepare(request)

        self.active_websockets.add(ws)
        client_addr = request.remote
        print(f"[STREAMER] Client connected: {client_addr} (Total clients: {len(self.active_websockets)})")

        try:
            # Send initial hello message
            hello = {
                "type": "HELLO",
                "server": "Project Kongor Viewport Streamer",
                "version": "1.0.0",
                "width": 960,
                "height": 540
            }
            await ws.send_str(json.dumps(hello))

            async for msg in ws:
                if msg.type == WSMsgType.TEXT:
                    try:
                        cmd = json.loads(msg.data)
                        if cmd.get("action") == "PING":
                            await ws.send_str(json.dumps({"type": "PONG", "ts": time.time()}))
                    except Exception:
                        pass
                elif msg.type == WSMsgType.ERROR:
                    print(f"[STREAMER] WS error from {client_addr}: {ws.exception()}")
        finally:
            self.active_websockets.remove(ws)
            print(f"[STREAMER] Client disconnected: {client_addr} (Remaining: {len(self.active_websockets)})")

        return ws

    async def frame_broadcast_loop(self):
        """Broadcasts frames at target 60 FPS (16.6ms intervals)."""
        print("[STREAMER] Frame broadcast loop started.")
        self.is_running = True
        target_interval = 1.0 / 60.0

        while self.is_running:
            t_start = time.perf_counter()

            # 1. Read or generate frame
            frame = None
            if self.honcap.is_connected:
                frame = self.honcap.read_latest_frame(timeout_ms=2)

            if not frame:
                # If game is not active or hasn't produced frame, generate synthetic
                frame = self.honcap.generate_synthetic_frame()

            jpeg_bytes = frame.to_jpeg(quality=75)
            self.latest_jpeg = jpeg_bytes

            # 2. Read latest Seqlock telemetry
            telem = self.telemetry.read_latest()
            telem["type"] = "TELEMETRY"
            telem["frame_index"] = frame.frame_index
            self.latest_telemetry = telem

            # 3. Calculate FPS stats
            self.fps_counter += 1
            now = time.time()
            if now - self.last_fps_calc >= 1.0:
                self.current_fps = self.fps_counter / (now - self.last_fps_calc)
                self.fps_counter = 0
                self.last_fps_calc = now

            # 4. Broadcast to all active WebSocket clients
            if self.active_websockets:
                telem_json = json.dumps(telem)
                dead_clients = set()
                for ws in self.active_websockets:
                    if ws.closed:
                        dead_clients.add(ws)
                        continue
                    try:
                        # Send JSON telemetry header then binary JPEG frame
                        await ws.send_str(telem_json)
                        await ws.send_bytes(jpeg_bytes)
                    except Exception:
                        dead_clients.add(ws)

                for dead in dead_clients:
                    self.active_websockets.discard(dead)

            self.total_frames_broadcast += 1

            # Sleep remainder of frame interval
            elapsed = time.perf_counter() - t_start
            sleep_time = max(0.001, target_interval - elapsed)
            await asyncio.sleep(sleep_time)

    def stop(self):
        self.is_running = False
        self.honcap.close()
        self.telemetry.close()


async def main():
    parser = argparse.ArgumentParser(description="Project Kongor Viewport Streamer")
    parser.add_argument("--host", default="127.0.0.1", help="Host address to bind")
    parser.add_argument("--port", type=int, default=8088, help="Port to bind (default: 8088)")
    parser.add_argument("--static", default=os.path.dirname(os.path.abspath(__file__)), help="Path to spectator console web assets")
    args = parser.parse_args()

    streamer = ViewportStreamer(host=args.host, port=args.port, static_dir=args.static)
    app = await streamer.init_app()

    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, streamer.host, streamer.port)
    await site.start()

    print("==========================================================")
    print("  Project Kongor: Viewport Streamer Relay Service")
    print(f"  Live Spectator URL: http://{streamer.host}:{streamer.port}")
    print(f"  WebSocket Stream:   ws://{streamer.host}:{streamer.port}/ws/stream")
    print(f"  Snapshot API:       http://{streamer.host}:{streamer.port}/api/snapshot.jpg")
    print("==========================================================")

    # Launch background broadcast loop
    broadcast_task = asyncio.create_task(streamer.frame_broadcast_loop())

    try:
        await broadcast_task
    except asyncio.CancelledError:
        pass
    finally:
        streamer.stop()
        await runner.cleanup()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[STREAMER] Shutting down cleanly.")
