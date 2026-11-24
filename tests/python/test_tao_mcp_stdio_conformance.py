#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)
"""
End-to-end stdio subprocess conformance suite for TAO MCP (`kongor mcp`) with `NetGuard` active.

Important scope & guarantees:
- Spawns the real compiled `kongor.dll mcp` binary as an isolated child OS process (IsTestExecution == False).
- Isolates KONGOR_MCP_TELEMETRY_DIR and KONGOR_MCP_PAYLOAD_DIR into pytest `tmp_path` so conformance
  runs never pollute production `logs/mcp_tool_outcomes.jsonl` or game SLO 1 (FirstCallSuccessRate).
- Note: `NetGuard` is a 250ms polling TCP/inet egress detector (not a kernel firewall and does not
  intercept single-packet UDP datagrams). Here we pair `NetGuard` with direct `psutil` socket checks
  on the `dotnet kongor.dll mcp` server process to assert zero non-loopback network connections.
"""

from __future__ import annotations

import ipaddress
import json
import os
import sys
from pathlib import Path

import psutil
import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
AUDIT_DIR = REPO_ROOT / "scripts" / "mcp_audit"
if str(AUDIT_DIR) not in sys.path:
    sys.path.insert(0, str(AUDIT_DIR))

from tao_session import NetGuard, Session, resolve_kongor_dll  # noqa: E402


def _is_loopback_or_local(ip: str, local_ips: set[str]) -> bool:
    if ip in local_ips:
        return True
    try:
        addr = ipaddress.ip_address(ip)
        return addr.is_loopback or addr.is_unspecified
    except ValueError:
        return False


def _assert_no_remote_server_sockets(pid: int, local_ips: set[str]) -> None:
    try:
        proc = psutil.Process(pid)
        conns = proc.net_connections(kind="inet")
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return

    remote_peers = [
        f"{c.raddr.ip}:{c.raddr.port} ({c.status})"
        for c in conns
        if c.raddr and not _is_loopback_or_local(c.raddr.ip, local_ips)
    ]
    assert not remote_peers, f"kongor mcp subprocess opened non-local socket(s): {remote_peers}"


def _build_isolated_env(tmp_path: Path) -> tuple[dict[str, str | None], Path, Path]:
    tel_dir = tmp_path / "telemetry"
    payload_dir = tmp_path / "payloads"
    tel_dir.mkdir(parents=True, exist_ok=True)
    payload_dir.mkdir(parents=True, exist_ok=True)

    env: dict[str, str | None] = {
        "KONGOR_MCP_TELEMETRY_DIR": str(tel_dir),
        "KONGOR_MCP_PAYLOAD_DIR": str(payload_dir),
        "KONGOR_MCP_ALLOW_LEGACY_MOCKS": None,
        "KONGOR_MCP_INCLUDE_SIMULATED": None,
        "KONGOR_MCP_ALLOW_WINDOW_EXPOSURE": None,
        "KONGOR_MCP_OUTCOME_LOG_DISABLED": None,
    }
    return env, tel_dir, payload_dir


def _extract_tool_json(resp: dict) -> tuple[bool, dict, dict]:
    assert "error" not in resp, f"Unexpected JSON-RPC error: {resp}"
    result = resp["result"]
    is_error = bool(result.get("isError", False))
    text_items = [c["text"] for c in result.get("content", []) if c.get("type") == "text"]
    assert text_items, f"Expected text content item in {result}"
    return is_error, json.loads(text_items[0]), result


def test_tao_mcp_stdio_conformance_end_to_end(tmp_path: Path) -> None:
    """
    protects: Real out-of-process stdio framing, SimulatedTestDouble rejection, unknown_argument rejection, 8KB response cap, and zero remote egress (MCP-283, MCP-293, MCP-295, MCP-297, MCP-298).
    fails_when: kongor.dll is missing, IsTestExecution hides a production bug, SimulatedTestDouble leaks through, unknown args are accepted, >8KB output isn't capped, or non-loopback sockets open.
    seam: Real OS subprocess `dotnet kongor.dll mcp` over stdio via `scripts/mcp_audit/tao_session.py` + `NetGuard`.
    """
    dll_path = resolve_kongor_dll()
    if not os.path.isfile(dll_path):
        pytest.fail(
            f"Compiled kongor.dll not found at '{dll_path}'. "
            "Run: ./scripts/Invoke-KongorTestLock.ps1 -Owner 'build' -- dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release"
        )

    env, tel_dir, payload_dir = _build_isolated_env(tmp_path)
    guard = NetGuard()
    session = Session(protocol_version="2024-11-05", extra_env=env)
    try:
        # 1. Protocol 2024-11-05 handshake & core tools/list budget
        assert session.init_response["result"]["protocolVersion"] == "2024-11-05"
        tools_msg = session.request("tools/list", {}, 30)
        tools = tools_msg["result"]["tools"]
        tool_names = {t["name"] for t in tools}
        assert len(tools) == 18, f"Expected 18 Core tools by default, got {len(tools)}"
        assert "kongor_call_tool" in tool_names

        # 2. Under 2024-11-05, structuredContent must be omitted on wire
        vfs_resp = session.request(
            "tools/call",
            {"name": "kongor_vfs_locate", "arguments": {"path": "ui/fe2/main.interface"}},
            30,
        )
        is_err, vfs_json, raw_result = _extract_tool_json(vfs_resp)
        assert not is_err, f"Unexpected error on kongor_vfs_locate: {vfs_json}"
        assert "structuredContent" not in raw_result

        # 3. kongor_call_tool describe works without false-positive SimulatedTestDouble substring block
        desc_resp = session.request(
            "tools/call",
            {
                "name": "kongor_call_tool",
                "arguments": {"action": "describe", "tool_name": "kongor_vfs_mount_graph"},
            },
            30,
        )
        desc_err, desc_json, _ = _extract_tool_json(desc_resp)
        assert not desc_err, f"Unexpected describe error: {desc_json}"
        assert "kongor_vfs_mount_graph" in json.dumps(desc_json)

        # 4. MCP-283: Live repo VFS scan returns StaticOfflineAnalysis (never resources999.s2z),
        #    and missing directory returns directory_not_found
        live_vfs_resp = session.request(
            "tools/call",
            {
                "name": "kongor_call_tool",
                "arguments": {
                    "action": "call",
                    "tool_name": "kongor_vfs_mount_graph",
                    "arguments": {"action": "inspect_mounts"},
                },
            },
            30,
        )
        lv_err, lv_json, _ = _extract_tool_json(live_vfs_resp)
        assert not lv_err
        assert lv_json["executionFidelity"] == "StaticOfflineAnalysis"
        assert "resources999.s2z" not in json.dumps(lv_json)

        missing_dir = str(tmp_path / "does_not_exist_vfs")
        miss_vfs_resp = session.request(
            "tools/call",
            {
                "name": "kongor_call_tool",
                "arguments": {
                    "action": "call",
                    "tool_name": "kongor_vfs_mount_graph",
                    "arguments": {"action": "inspect_mounts", "game_dir": missing_dir},
                },
            },
            30,
        )
        mv_err, mv_json, _ = _extract_tool_json(miss_vfs_resp)
        assert mv_err
        assert mv_json["errorCode"] == "directory_not_found"

        # 5. MCP-283: SimulatedTestDouble output rejected without allowSimulation=true, permitted with allowSimulation=true
        sim_blocked_resp = session.request(
            "tools/call",
            {
                "name": "kongor_call_tool",
                "arguments": {
                    "action": "call",
                    "tool_name": "kongor_audit_stats",
                    "arguments": {"synthetic": True},
                },
            },
            30,
        )
        sb_err, sb_json, _ = _extract_tool_json(sim_blocked_resp)
        assert sb_err
        assert sb_json["errorCode"] == "simulation_not_allowed"

        sim_ok_resp = session.request(
            "tools/call",
            {
                "name": "kongor_call_tool",
                "arguments": {
                    "action": "call",
                    "tool_name": "kongor_audit_stats",
                    "arguments": {"synthetic": True, "allowSimulation": True},
                },
            },
            30,
        )
        so_err, so_json, _ = _extract_tool_json(sim_ok_resp)
        assert not so_err, f"Expected allowSimulation=True to succeed: {so_json}"
        assert so_json["executionFidelity"] == "SimulatedTestDouble"

        # 6. MCP-293: Unknown argument rejected with didYouMean suggestion
        typo_resp = session.request(
            "tools/call",
            {
                "name": "kongor_k2_ui_inspect",
                "arguments": {"mode": "static", "widgt": "main_top_bar"},
            },
            30,
        )
        ty_err, ty_json, _ = _extract_tool_json(typo_resp)
        assert ty_err
        assert ty_json["errorCode"] == "unknown_argument"
        assert ty_json["unknownArgument"] == "widgt"
        assert ty_json["didYouMean"] in ("widget", "widget_name")

        # 7. MCP-295: >8KB response capped by default into KONGOR_MCP_PAYLOAD_DIR; detail="full" returns complete payload
        ui_xml_path = str(REPO_ROOT / "games" / "client" / "override" / "ui" / "fe2" / "main.interface")
        capped_resp = session.request(
            "tools/call",
            {
                "name": "kongor_k2_ui_inspect",
                "arguments": {"mode": "static", "path": ui_xml_path, "depth": 6},
            },
            30,
        )
        cap_text = capped_resp["result"]["content"][0]["text"]
        assert len(cap_text.encode("utf-8")) <= 8192
        cap_json = json.loads(cap_text)
        assert cap_json.get("truncated") is True
        saved_path = Path(cap_json["fullOutputPath"])
        assert saved_path.is_file()
        assert payload_dir in saved_path.parents

        full_resp = session.request(
            "tools/call",
            {
                "name": "kongor_k2_ui_inspect",
                "arguments": {"mode": "static", "path": ui_xml_path, "depth": 6, "detail": "full"},
            },
            30,
        )
        full_text = full_resp["result"]["content"][0]["text"]
        assert len(full_text.encode("utf-8")) > 8192
        full_json = json.loads(full_text)
        assert "fullOutputPath" not in full_json
        assert "originalBytes" not in full_json

        # 8. Isolated telemetry log written inside tmp_path & zero remote network egress
        _assert_no_remote_server_sockets(session.proc.pid, guard.local)
        assert guard.violation is None, f"NetGuard detected external connection: {guard.violation}"
    finally:
        session.close()
        guard.stop = True

    outcomes_file = tel_dir / "mcp_tool_outcomes.jsonl"
    assert outcomes_file.is_file(), "Expected isolated mcp_tool_outcomes.jsonl in KONGOR_MCP_TELEMETRY_DIR"
    rows = [json.loads(line) for line in outcomes_file.read_text(encoding="utf-8").splitlines() if line.strip()]
    assert len(rows) >= 6


def test_tao_mcp_stdio_negotiated_2025_06_18_emits_structured_content(tmp_path: Path) -> None:
    """
    protects: Protocol-negotiated structuredContent emission over real stdio when client requests MCP 2025-06-18.
    fails_when: Negotiating 2025-06-18 fails to include structuredContent alongside content[0].text.
    seam: Real OS subprocess `dotnet kongor.dll mcp` stdio initialize + tools/call.
    """
    env, _, _ = _build_isolated_env(tmp_path)
    session = Session(protocol_version="2025-06-18", extra_env=env)
    try:
        assert session.init_response["result"]["protocolVersion"] == "2025-06-18"
        resp = session.request(
            "tools/call",
            {"name": "kongor_vfs_locate", "arguments": {"path": "ui/fe2/main.interface"}},
            30,
        )
        is_err, parsed_text, raw_result = _extract_tool_json(resp)
        assert not is_err, f"Unexpected error on kongor_vfs_locate: {parsed_text}"
        assert "structuredContent" in raw_result
        assert raw_result["structuredContent"]["status"] == parsed_text["status"]
    finally:
        session.close()
