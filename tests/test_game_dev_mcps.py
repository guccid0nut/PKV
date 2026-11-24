#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Comprehensive Integration Test Suite for Game Dev MCP Servers:
- LuaLS (Lua Language Server)
- FFmpeg (Media Pipeline)
- Texconv (DirectX DDS Texture Pipeline)
"""

import json
import os
import pathlib
import queue
import shutil
import subprocess
import sys
import tempfile
import threading

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]
_VENV_PYTHON = REPO_ROOT / "src/tools/comfyui/.venv/Scripts/python.exe"
PYTHON_EXE = str(_VENV_PYTHON) if _VENV_PYTHON.is_file() else sys.executable

def run_jsonrpc_session(cmd, steps, timeout_seconds=15.0):
    p = subprocess.Popen(
        cmd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8"
    )
    out_queue = queue.Queue()

    def _reader():
        try:
            for line in iter(p.stdout.readline, ""):
                out_queue.put(line)
        finally:
            out_queue.put(None)

    reader_thread = threading.Thread(target=_reader, daemon=True)
    reader_thread.start()
    results = []
    try:
        for req, is_notification, wait_response in steps:
            p.stdin.write(json.dumps(req) + "\n")
            p.stdin.flush()
            if wait_response:
                try:
                    line = out_queue.get(timeout=timeout_seconds)
                except queue.Empty:
                    stderr_tail = ""
                    try:
                        p.kill()
                        _, stderr_tail = p.communicate(timeout=2.0)
                    except Exception:
                        pass
                    raise TimeoutError(
                        f"Timed out after {timeout_seconds}s waiting for MCP response to {req.get('method')}. "
                        f"Stderr: {stderr_tail[:500]}"
                    )
                if line:
                    results.append(json.loads(line))
                else:
                    results.append(None)
    finally:
        if p.poll() is None:
            p.terminate()
            try:
                p.wait(timeout=3.0)
            except subprocess.TimeoutExpired:
                p.kill()
                p.wait(timeout=2.0)
    return results

def test_luals_mcp_initialization_succeeds():
    print("=== Testing LuaLS MCP ===")
    luals_exe = pathlib.Path(os.environ.get("LUA_LS_PATH", str(REPO_ROOT / "src/tools/luals/bin/bin/lua-language-server.exe")))
    if not luals_exe.is_file() or not (luals_exe.parent / "main.lua").is_file():
        pytest.skip(f"Local lua-language-server runtime not complete at {luals_exe}")
    behaviorlib = REPO_ROOT / "games/client/game/behaviorlib.lua"
    if not behaviorlib.is_file():
        pytest.skip(f"Cleanroom Lua fixture missing: {behaviorlib}")
    with tempfile.TemporaryDirectory(prefix="kongor_luals_test_") as tmp_dir:
        scoped_lua = pathlib.Path(tmp_dir) / "behaviorlib.lua"
        shutil.copy2(behaviorlib, scoped_lua)
        cmd = [
            PYTHON_EXE,
            str(REPO_ROOT / "src/tools/luals/mcp_server.py"),
            "--workspace-dir",
            tmp_dir,
        ]
        steps = [
            # 1. init
            ({
                "jsonrpc": "2.0", "id": 1, "method": "initialize",
                "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test", "version": "1.0"}}
            }, False, True),
            # 2. initialized notification
            ({"jsonrpc": "2.0", "method": "notifications/initialized"}, True, False),
            # 3. tools/list
            ({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, False, True),
            # 4. lua_list_file_symbols
            ({
                "jsonrpc": "2.0", "id": 3, "method": "tools/call",
                "params": {
                    "name": "lua_list_file_symbols",
                    "arguments": {"file_path": str(scoped_lua)}
                }
            }, False, True),
        ]
        res = run_jsonrpc_session(cmd, steps)
    init_res, list_res, call_res = res[0], res[1], res[2]

    assert "result" in init_res, f"Init failed: {init_res}"
    tools = [t["name"] for t in list_res.get("result", {}).get("tools", [])]
    print(f"LuaLS Tools ({len(tools)}): {tools}")
    assert "lua_list_file_symbols" in tools
    assert "lua_check_diagnostics" in tools

    content_str = call_res.get("result", {}).get("content", [{}])[0].get("text", "{}")
    call_data = json.loads(content_str)
    print(f"Parsed {call_data.get('count', 0)} symbols from behaviorlib.lua")
    assert call_data.get("count", 0) > 0, "No symbols parsed!"
    print(">>> LuaLS MCP PASSED!\n")

def test_ffmpeg_mcp_probe_succeeds():
    print("=== Testing FFmpeg MCP ===")
    ffmpeg_exe = (
        os.environ.get("FFMPEG_PATH")
        or shutil.which("ffmpeg")
        or (r"C:\ProgramData\chocolatey\bin\ffmpeg.exe" if os.path.isfile(r"C:\ProgramData\chocolatey\bin\ffmpeg.exe") else None)
    )
    if not ffmpeg_exe:
        pytest.skip("ffmpeg binary not installed on host")
    test_wav = os.path.abspath("tests/temp_test.wav")
    test_mp3 = os.path.abspath("tests/temp_test.mp3")

    # Generate synthetic tone
    subprocess.run([
        ffmpeg_exe, "-y",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
        test_wav
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)

    cmd = [
        PYTHON_EXE,
        str(REPO_ROOT / "src/tools/ffmpeg/mcp_server.py")
    ]
    steps = [
        ({
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test", "version": "1.0"}}
        }, False, True),
        ({"jsonrpc": "2.0", "method": "notifications/initialized"}, True, False),
        ({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, False, True),
        ({
            "jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": {
                "name": "ffmpeg_probe_media",
                "arguments": {"file_path": test_wav}
            }
        }, False, True),
        ({
            "jsonrpc": "2.0", "id": 4, "method": "tools/call",
            "params": {
                "name": "ffmpeg_transcode_audio",
                "arguments": {"input_path": test_wav, "output_path": test_mp3, "codec": "mp3"}
            }
        }, False, True),
    ]

    try:
        res = run_jsonrpc_session(cmd, steps)
        init_res, list_res, probe_res, transcode_res = res[0], res[1], res[2], res[3]
        tools = [t["name"] for t in list_res.get("result", {}).get("tools", [])]
        print(f"FFmpeg Tools ({len(tools)}): {tools}")
        assert "ffmpeg_probe_media" in tools
        assert "ffmpeg_transcode_audio" in tools

        probe_text = json.loads(probe_res.get("result", {}).get("content", [{}])[0].get("text", "{}"))
        print(f"Probe format: {probe_text.get('format_name')}, duration: {probe_text.get('duration_seconds')}s")
        assert probe_text.get("format_name") == "wav"

        trans_text = json.loads(transcode_res.get("result", {}).get("content", [{}])[0].get("text", "{}"))
        print(f"Transcode result: success={trans_text.get('success')}, size={trans_text.get('output_size_bytes')} bytes")
        assert trans_text.get("success") is True
        print(">>> FFmpeg MCP PASSED!\n")
    finally:
        for f in (test_wav, test_mp3):
            if os.path.exists(f):
                os.remove(f)

def test_texconv_mcp_texture_conversion_succeeds():
    print("=== Testing Texconv DDS MCP ===")
    sample_dds = os.path.abspath("artifacts/test.dds")
    if not os.path.isfile(sample_dds):
        pytest.skip(f"Local sample DDS fixture missing: {sample_dds}")
    out_dir = os.path.abspath("tests/tex_verify_out")
    os.makedirs(out_dir, exist_ok=True)

    cmd = [
        PYTHON_EXE,
        str(REPO_ROOT / "src/tools/texconv/mcp_server.py")
    ]
    steps = [
        ({
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test", "version": "1.0"}}
        }, False, True),
        ({"jsonrpc": "2.0", "method": "notifications/initialized"}, True, False),
        ({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, False, True),
        ({
            "jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": {
                "name": "dds_inspect_texture",
                "arguments": {"file_path": sample_dds}
            }
        }, False, True),
        ({
            "jsonrpc": "2.0", "id": 4, "method": "tools/call",
            "params": {
                "name": "dds_export_to_image",
                "arguments": {"input_path": sample_dds, "output_dir": out_dir, "format": "png"}
            }
        }, False, True),
    ]

    try:
        res = run_jsonrpc_session(cmd, steps)
        init_res, list_res, inspect_res, export_res = res[0], res[1], res[2], res[3]
        tools = [t["name"] for t in list_res.get("result", {}).get("tools", [])]
        print(f"Texconv Tools ({len(tools)}): {tools}")
        assert "dds_inspect_texture" in tools
        assert "dds_convert_texture" in tools
        assert "dds_bake_normal_map" in tools

        inspect_text = json.loads(inspect_res.get("result", {}).get("content", [{}])[0].get("text", "{}"))
        print(f"Inspected DDS: {inspect_text.get('width')}x{inspect_text.get('height')}, format={inspect_text.get('format')}")
        assert inspect_text.get("width") == 16
        assert inspect_text.get("height") == 16

        export_text = json.loads(export_res.get("result", {}).get("content", [{}])[0].get("text", "{}"))
        print(f"Exported to PNG: success={export_text.get('success')}, path={export_text.get('output_file')}")
        assert export_text.get("success") is True
        print(">>> Texconv DDS MCP PASSED!\n")
    finally:
        if os.path.exists(out_dir):
            import shutil
            shutil.rmtree(out_dir, ignore_errors=True)

def main():
    test_luals_mcp_initialization_succeeds()
    test_ffmpeg_mcp_probe_succeeds()
    test_texconv_mcp_texture_conversion_succeeds()
    print("ALL 3 GAME DEV MCP SERVERS VERIFIED AND OPERATIONAL!")

if __name__ == "__main__":
    main()

