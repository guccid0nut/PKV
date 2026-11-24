#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Launcher for Project Kongor ComfyUI Server.
Runs ComfyUI in the background or foreground on port 8188 with hardware acceleration.
"""

from __future__ import annotations

import argparse
import logging
import os
import platform
import subprocess
import sys
import time
import urllib.request
import webbrowser

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [ComfyUI-Launcher] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger("launch_comfyui")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMFYUI_DIR = os.path.join(SCRIPT_DIR, "ComfyUI")
VENV_DIR = os.path.join(SCRIPT_DIR, ".venv")


def get_python_exe() -> str:
    """Find python executable (venv or system)."""
    env_py = os.environ.get("COMFYUI_PYTHON")
    if env_py and os.path.exists(env_py):
        return env_py

    candidates = []
    if platform.system() == "Windows":
        candidates.append(os.path.join(VENV_DIR, "Scripts", "python.exe"))
    else:
        candidates.append(os.path.join(VENV_DIR, "bin", "python"))
    
    for c in candidates:
        if os.path.exists(c):
            return c
    return sys.executable


def is_comfyui_running(host: str = "127.0.0.1", port: int = 8188) -> bool:
    """Check if ComfyUI server is already responding."""
    try:
        url = f"http://{host}:{port}/system_stats"
        with urllib.request.urlopen(url, timeout=2) as resp:
            return resp.status == 200
    except Exception:
        return False


def stop_comfyui(host: str = "127.0.0.1", port: int = 8188) -> bool:
    """Find and terminate running ComfyUI processes."""
    logger.info("Searching for running ComfyUI instances on port %d...", port)
    stopped_any = False

    if platform.system() == "Windows":
        # 1. Try finding PID by port via netstat or PowerShell
        try:
            cmd = ["powershell", "-NoProfile", "-Command",
                   f"(Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue).OwningProcess"]
            res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            pids = [int(p.strip()) for p in res.stdout.strip().split() if p.strip().isdigit()]
            for pid in pids:
                logger.info("Found process %d listening on port %d. Terminating...", pid, port)
                subprocess.run(["taskkill", "/PID", str(pid), "/F"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                logger.info("Successfully terminated process %d.", pid)
                stopped_any = True
        except Exception as ex:
            logger.warning("Error querying port connection on Windows: %s", ex)

        # 2. Also search for any orphaned ComfyUI main.py processes
        try:
            ps_cmd = ["powershell", "-NoProfile", "-Command",
                      "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'ComfyUI[\\\\/]main\\.py' } | Select-Object -ExpandProperty ProcessId"]
            res = subprocess.run(ps_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            pids = [int(p.strip()) for p in res.stdout.strip().split() if p.strip().isdigit()]
            for pid in pids:
                logger.info("Found ComfyUI process %d via CommandLine match. Terminating...", pid)
                subprocess.run(["taskkill", "/PID", str(pid), "/F"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                logger.info("Successfully terminated ComfyUI process %d.", pid)
                stopped_any = True
        except Exception as ex:
            logger.warning("Error querying Win32_Process on Windows: %s", ex)

        # 3. Terminate any ComfyUI MCP servers (mcp_server.py)
        try:
            ps_cmd = ["powershell", "-NoProfile", "-Command",
                      "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'mcp_server\\.py' } | Select-Object -ExpandProperty ProcessId"]
            res = subprocess.run(ps_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            pids = [int(p.strip()) for p in res.stdout.strip().split() if p.strip().isdigit()]
            for pid in pids:
                logger.info("Found ComfyUI MCP server process %d. Terminating...", pid)
                subprocess.run(["taskkill", "/PID", str(pid), "/F"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                logger.info("Successfully terminated ComfyUI MCP server process %d.", pid)
                stopped_any = True
        except Exception as ex:
            logger.warning("Error querying MCP server processes on Windows: %s", ex)
    else:
        # Unix/Linux/macOS fallback (fuser / lsof / pkill)
        try:
            subprocess.run(["fuser", "-k", f"{port}/tcp"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            stopped_any = True
        except Exception:
            pass
        try:
            subprocess.run(["pkill", "-f", "mcp_server.py"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            stopped_any = True
        except Exception:
            pass

    if is_comfyui_running(host, port):
        logger.warning("ComfyUI still appears to be responding at http://%s:%d", host, port)
        return False

    if stopped_any:
        logger.info("ComfyUI server has been stopped.")
    else:
        logger.info("No running ComfyUI instance found on port %d.", port)
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Launch or Stop ComfyUI server for Project Kongor")
    parser.add_argument("--port", type=int, default=8188, help="Port to run/stop ComfyUI on (default: 8188)")
    parser.add_argument("--listen", type=str, default="127.0.0.1", help="IP to listen on")
    parser.add_argument("--stop", action="store_true", help="Stop running ComfyUI server")
    parser.add_argument("--status", action="store_true", help="Check if ComfyUI is currently running")
    parser.add_argument("--no-browser", action="store_true", help="Do not open browser on startup")
    parser.add_argument("--lowvram", action="store_true", help="Enable low VRAM mode for GPUs < 6GB")
    args = parser.parse_args()

    if args.status:
        online = is_comfyui_running(args.listen, args.port)
        if online:
            logger.info("STATUS: ComfyUI is ONLINE at http://%s:%d", args.listen, args.port)
            return 0
        else:
            logger.info("STATUS: ComfyUI is OFFLINE on http://%s:%d", args.listen, args.port)
            return 1

    if args.stop:
        success = stop_comfyui(args.listen, args.port)
        return 0 if success else 1

    if is_comfyui_running(args.listen, args.port):
        logger.info("ComfyUI is already running at http://%s:%d", args.listen, args.port)
        if not args.no_browser:
            webbrowser.open(f"http://{args.listen}:{args.port}")
        return 0

    main_py = os.path.join(COMFYUI_DIR, "main.py")
    if not os.path.exists(main_py):
        logger.error("ComfyUI not found at %s. Please run 'python install_comfyui.py' first.", main_py)
        return 1

    py_exe = get_python_exe()
    cmd = [
        py_exe, main_py,
        "--listen", args.listen,
        "--port", str(args.port),
        "--preview-method", "auto",
        "--enable-assets",
        "--reserve-vram", "2.0",
        "--lowvram",
        "--disable-smart-memory"
    ]

    logger.info("Starting ComfyUI server...")
    logger.info("Command: %s", " ".join(cmd))
    logger.info("URL: http://%s:%d", args.listen, args.port)

    if not args.no_browser:
        def open_tab():
            for _ in range(15):
                time.sleep(1)
                if is_comfyui_running(args.listen, args.port):
                    webbrowser.open(f"http://{args.listen}:{args.port}")
                    break
        import threading
        threading.Thread(target=open_tab, daemon=True).start()

    try:
        proc = subprocess.run(cmd, cwd=COMFYUI_DIR)
        return proc.returncode
    except KeyboardInterrupt:
        logger.info("ComfyUI server stopped.")
        return 0


if __name__ == "__main__":
    sys.exit(main())

