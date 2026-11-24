# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Headless Frida High-Hz Attribution Runner for Project Kongor SHJ perf-canary.

Attaches scripts/qa/frida_high_hz_profiler.js to a running hon_x64.exe PID for an
explicit attribution window (default 10s), captures CUIManager::Frame, CSceneManager::Render,
CResourceManager::Register, main-thread CreateFileW / Sleep stalls, and main-thread CPU %,
then detaches cleanly and emits structured JSON to stdout.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import sys
import threading
import time


def main() -> int:
    parser = argparse.ArgumentParser(description="Run a bounded Frida High-Hz attribution window against hon_x64.exe.")
    parser.add_argument("--pid", type=int, required=True, help="Target hon_x64.exe process ID.")
    parser.add_argument("--duration", type=float, default=10.0, help="Attribution window duration in seconds (default: 10).")
    parser.add_argument("--warmup", type=float, default=0.5, help="Warmup seconds to capture mainTid before resetting window.")
    parser.add_argument("--script", type=str, default="", help="Optional path to frida_high_hz_profiler.js.")
    args = parser.parse_args()

    try:
        import frida  # type: ignore[import-not-found]
    except Exception as exc:  # noqa: BLE001
        print(json.dumps({
            "status": "error",
            "error": f"Failed to import frida Python package: {exc}",
            "actionableRemediation": "Run using src/tools/frida/.venv/Scripts/python.exe (or install frida in the active venv)."
        }))
        return 2

    script_path = pathlib.Path(args.script) if args.script else (pathlib.Path(__file__).resolve().parent / "frida_high_hz_profiler.js")
    if not script_path.is_file():
        print(json.dumps({
            "status": "error",
            "error": f"Frida profiler script not found at '{script_path}'."
        }))
        return 2

    source = script_path.read_text(encoding="utf-8")
    messages: list[dict] = []

    def on_message(message: dict, _data: object) -> None:
        messages.append(message)

    session = None
    script = None
    exit_code = 1
    try:
        session = frida.attach(args.pid)
        script = session.create_script(source)
        script.on("message", on_message)
        script.load()

        # Let at least a few frames run so recordMainTid() latches the main engine thread
        time.sleep(max(0.1, args.warmup))
        script.exports_sync.reset_window()
        time.sleep(max(1.0, args.duration))
        snapshot = script.exports_sync.get_snapshot()

        result = {
            "status": "completed",
            "pid": args.pid,
            "durationRequestedSec": args.duration,
            "snapshot": snapshot,
            "scriptErrors": [m for m in messages if m.get("type") == "error"]
        }
        print(json.dumps(result, indent=2), flush=True)
        exit_code = 0
        return 0
    except Exception as exc:  # noqa: BLE001
        print(json.dumps({
            "status": "error",
            "pid": args.pid,
            "error": str(exc),
            "scriptErrors": [m for m in messages if m.get("type") == "error"]
        }), flush=True)
        exit_code = 1
        return 1
    finally:
        def _cleanup() -> None:
            if script is not None:
                try:
                    script.unload()
                except Exception:
                    pass
            if session is not None:
                try:
                    session.detach()
                except Exception:
                    pass

        cleanup_thread = threading.Thread(target=_cleanup, daemon=True)
        cleanup_thread.start()
        cleanup_thread.join(timeout=8.0)
        if cleanup_thread.is_alive():
            # Avoid hanging the parent SHJ runner if Frida Gum teardown stalls on a hot engine thread
            sys.stdout.flush()
            sys.stderr.flush()
            os._exit(exit_code)


if __name__ == "__main__":
    sys.exit(main())
