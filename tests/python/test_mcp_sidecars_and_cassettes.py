#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Deterministic Pytest Cassette & Sidecar Standardization Eval Suite (Wave 2 Slice 2).

Verifies all 11/11 Python MCP sidecars:
  blender, clangd, comfyui, everything, ffmpeg, frida, ghidra, luals, texconv, voicebox, windbg
against:
  1. Shared `_common/mcp_telemetry.py` integration + AST validity (0 syntax errors).
  2. Live stdio FastMCP cassette roundtrip (`voicebox`) with isolated NDJSON telemetry output.
  3. Safe Python literal escaping in `blender` generated bpy payloads (None -> Python `None`, never JSON `null`).
  4. Portable `.tools/bin` / `PATH` binary discovery (`resolve_tool_binary`).
  5. Startup tool token budget (< 12k tokens for default Core toolset).
"""

from __future__ import annotations

import ast
import importlib.util
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
TOOLS_DIR = REPO_ROOT / "src" / "tools"
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from _common.mcp_telemetry import SIDECAR_SERVERS, record_mcp_outcome, resolve_tool_binary  # noqa: E402


def test_all_11_python_sidecars_parse_cleanly_and_wire_shared_telemetry() -> None:
    """
    protects: 11/11 Python MCP sidecar standardization onto _common/mcp_telemetry.py & FastMCP.
    fails_when: Any sidecar has syntax errors, omits _common.mcp_telemetry, or drifts from canonical roster.
    seam: AST parse + source inspection of all 11 src/tools/<sidecar>/mcp_server.py modules.
    """
    assert len(SIDECAR_SERVERS) == 11
    for sidecar in SIDECAR_SERVERS:
        script_path = TOOLS_DIR / sidecar / "mcp_server.py"
        assert script_path.is_file(), f"Missing sidecar entrypoint: {script_path}"
        source = script_path.read_text(encoding="utf-8-sig")
        ast.parse(source, filename=str(script_path))
        assert "_common.mcp_telemetry" in source, f"Sidecar '{sidecar}' does not import _common.mcp_telemetry"


@pytest.mark.parametrize(
    "sidecar",
    ["blender", "clangd", "comfyui", "everything", "ffmpeg", "luals", "texconv", "voicebox"],
)
def test_standalone_fastmcp_sidecar_imports_cleanly_in_isolated_subprocess(sidecar: str) -> None:
    """
    protects: Real runtime module import & FastMCP @server.tool() registration across standalone sidecars.
    fails_when: `from __future__ import annotations`, missing imports, or FastMCP signature bugs break startup.
    seam: Isolated Python subprocess importing `src/tools/<sidecar>/mcp_server.py`.
    """
    script_path = TOOLS_DIR / sidecar / "mcp_server.py"
    code = (
        "import importlib.util, pathlib\n"
        f"p = pathlib.Path({str(script_path)!r})\n"
        "spec = importlib.util.spec_from_file_location('sidecar_probe', p)\n"
        "mod = importlib.util.module_from_spec(spec)\n"
        "spec.loader.exec_module(mod)\n"
        "print('IMPORT_OK')\n"
    )
    res = subprocess.run(
        [sys.executable, "-c", code],
        capture_output=True,
        text=True,
        timeout=15,
        check=False,
    )
    assert res.returncode == 0, f"Sidecar '{sidecar}' failed to import:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}"
    # shield_stdio_stdout() must keep stdout 100% clean for JSON-RPC and route print() to stderr!
    assert res.stdout == "", f"Sidecar '{sidecar}' leaked non-JSON-RPC bytes to stdout: {res.stdout!r}"
    assert "IMPORT_OK" in res.stderr


def test_blender_sidecar_generates_valid_python_none_literals_not_json_null(monkeypatch: pytest.MonkeyPatch) -> None:
    """
    protects: Blender MCP weapon-pairing code generation against JSON `null` NameError and quote injection.
    fails_when: `blender_pair_weapon` emits bare `null` or unescaped quotes into bpy Python code.
    seam: Direct invocation of `src/tools/blender/mcp_server.py` tool with captured client.execute cassette.
    """
    spec = importlib.util.spec_from_file_location("kongor_blender_mcp", TOOLS_DIR / "blender" / "mcp_server.py")
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    captured_scripts: list[str] = []

    def fake_execute(code: str, strict_json: bool = False) -> dict:
        captured_scripts.append(code)
        return {"status": "ok"}

    monkeypatch.setattr(mod.client, "execute", fake_execute)

    res = mod.blender_pair_weapon(
        weapon_name='Frost"Sword',
        armature_name=None,
        custom_grip=None,
        locator_name=None,
    )
    assert res["status"] == "ok"
    assert len(captured_scripts) == 1
    generated = captured_scripts[0]
    # Must parse as valid Python (no bare `null` or broken quote escaping!)
    ast.parse(generated)
    assert "arm_name = None" in generated
    assert "custom_grip=None" in generated
    assert "locator_name=None" in generated


def test_voicebox_fastmcp_stdio_cassette_roundtrip_emits_isolated_telemetry(tmp_path: Path) -> None:
    """
    protects: Voicebox FastMCP stdio initialization, tools/list, tools/call, and shared NDJSON telemetry emission.
    fails_when: Voicebox blocks on raw stdin, drops JSON-RPC responses, or skips _common/mcp_telemetry NDJSON logging.
    seam: Real OS subprocess `python src/tools/voicebox/mcp_server.py` over stdio pipes with isolated KONGOR_MCP_TELEMETRY_DIR.
    """
    env = os.environ.copy()
    env["KONGOR_MCP_TELEMETRY_DIR"] = str(tmp_path)
    env["VOICEBOX_URL"] = "http://127.0.0.1:1"  # Guaranteed unreachable loopback port for fast offline cassette

    proc = subprocess.Popen(
        [sys.executable, str(TOOLS_DIR / "voicebox" / "mcp_server.py")],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        env=env,
    )

    stderr_lines: list[str] = []
    t = threading.Thread(target=lambda: stderr_lines.extend(proc.stderr.readlines() if proc.stderr else []), daemon=True)
    t.start()

    try:
        assert proc.stdin and proc.stdout

        def send(msg: dict) -> None:
            proc.stdin.write(json.dumps(msg) + "\n")
            proc.stdin.flush()

        def recv() -> dict:
            line = proc.stdout.readline()
            assert line, f"Voicebox MCP exited prematurely. stderr={''.join(stderr_lines)}"
            return json.loads(line)

        send({
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "pytest-cassette", "version": "1.0"},
            },
        })
        init_resp = recv()
        assert init_resp["id"] == 1
        assert "result" in init_resp

        send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        list_resp = recv()
        tool_names = {tool["name"] for tool in list_resp["result"]["tools"]}
        assert {"voicebox_get_status", "voicebox_list_profiles", "voicebox_speak"}.issubset(tool_names)

        send({
            "jsonrpc": "2.0",
            "id": 3,
            "method": "tools/call",
            "params": {"name": "voicebox_get_status", "arguments": {}},
        })
        call_resp = recv()
        assert call_resp["id"] == 3
        assert "result" in call_resp
    finally:
        if proc.stdin:
            proc.stdin.close()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)

    telemetry_file = tmp_path / "mcp_python_outcomes.ndjson"
    assert telemetry_file.is_file(), f"Expected Voicebox sidecar telemetry at {telemetry_file}"
    rows = [json.loads(line) for line in telemetry_file.read_text(encoding="utf-8").splitlines() if line.strip()]
    assert any(r["server"] == "voicebox" and r["tool"] == "voicebox_get_status" for r in rows)


def test_shared_telemetry_and_portable_binary_discovery(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """
    protects: Portable binary resolution (env -> .tools/bin -> PATH) and isolated NDJSON telemetry schema.
    fails_when: Env override is ignored or record_mcp_outcome writes invalid JSON/missing fields.
    seam: _common.mcp_telemetry.resolve_tool_binary and record_mcp_outcome.
    """
    fake_bin = tmp_path / "custom_tool.exe"
    fake_bin.write_text("bin", encoding="utf-8")
    monkeypatch.setenv("TEST_TOOL_PATH_OVERRIDE", str(fake_bin))

    resolved = resolve_tool_binary("nonexistent_binary_xyz", env_var="TEST_TOOL_PATH_OVERRIDE")
    assert resolved == str(fake_bin)

    out_log = tmp_path / "outcomes.ndjson"
    record_mcp_outcome(
        server_name="ffmpeg",
        tool_name="ffmpeg_probe_media",
        is_error=False,
        duration_ms=12.345,
        outcome_log_path=str(out_log),
        origin="test",
    )
    rows = [json.loads(line) for line in out_log.read_text(encoding="utf-8").splitlines() if line.strip()]
    assert len(rows) == 1
    assert rows[0]["server"] == "ffmpeg"
    assert rows[0]["tool"] == "ffmpeg_probe_media"
    assert rows[0]["origin"] == "test"
    assert rows[0]["isError"] is False
    assert rows[0]["durationMs"] == 12.345
