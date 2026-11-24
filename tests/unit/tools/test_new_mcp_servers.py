#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Test script to verify the 4 newly installed MCP servers:
1. Wireshark MCP (tshark, capture & dissection)
2. WinDbg MCP (cdb.exe, crash dump analysis)
3. Memory MCP (knowledge graph persistence)
4. Ghidra MCP (pyghidra, decompiler & binary analysis)
"""

import json
import os
import subprocess
import sys
import time

import pytest

MCP_CONFIG_PATH = os.environ.get("GEMINI_MCP_CONFIG", os.path.expanduser("~/.gemini/antigravity/mcp_config.json"))

def probe_mcp_server_connectivity(name: str, config: dict) -> bool:
    print(f"\n{'='*20} Testing MCP Server: {name} {'='*20}")
    command = config.get("command")
    args = config.get("args", [])
    env = os.environ.copy()
    if "env" in config:
        for k, v in config["env"].items():
            env[k] = v

    full_cmd = [command] + args
    print(f"[CMD] {' '.join(full_cmd[:4])} ...")

    try:
        proc = subprocess.Popen(
            full_cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=env,
            shell=False,
        )
    except (FileNotFoundError, OSError) as ex:
        print(f"[SKIP/FAIL] Could not launch {name}: {ex}")
        return False

    try:
        # 1. Send initialize
        init_req = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "test_runner", "version": "1.0"},
            },
        }
        proc.stdin.write(json.dumps(init_req) + "\n")
        proc.stdin.flush()

        # Read initialize response (skip any non-JSON stdout lines if any)
        init_resp = None
        for _ in range(10):
            line = proc.stdout.readline()
            if not line:
                break
            line_str = line.strip()
            if line_str.startswith("{"):
                try:
                    init_resp = json.loads(line_str)
                    break
                except Exception:
                    pass

        if not init_resp:
            print(f"[FAIL] Did not receive valid JSON initialize response from {name}")
            return False

        server_info = init_resp.get("result", {}).get("serverInfo", {})
        print(f"[OK] Initialized: {server_info.get('name', 'Unknown')} v{server_info.get('version', '')}")

        # 2. Send initialized notification
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        proc.stdin.flush()

        # 3. Send tools/list
        tools_req = {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
        proc.stdin.write(json.dumps(tools_req) + "\n")
        proc.stdin.flush()

        tools_resp = None
        for _ in range(10):
            line = proc.stdout.readline()
            if not line:
                break
            line_str = line.strip()
            if line_str.startswith("{"):
                try:
                    tools_resp = json.loads(line_str)
                    break
                except Exception:
                    pass

        if not tools_resp or "result" not in tools_resp:
            print(f"[FAIL] Could not list tools for {name}")
            return False

        tools = tools_resp.get("result", {}).get("tools", [])
        tool_names = [t.get("name") for t in tools]
        print(f"[OK] Advertised {len(tools)} tools: {', '.join(tool_names[:6])}{' ...' if len(tool_names) > 6 else ''}")
        return True

    except Exception as ex:
        print(f"[ERROR] Exception during test of {name}: {ex}")
        return False
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=2.0)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass

def main():
    with open(MCP_CONFIG_PATH, "r", encoding="utf-8") as f:
        mcp_config = json.load(f)

    servers_to_test = ["wireshark", "windbg", "memory", "ghidra"]
    results = {}

    for server_name in servers_to_test:
        if server_name in mcp_config.get("mcpServers", {}):
            results[server_name] = probe_mcp_server_connectivity(server_name, mcp_config["mcpServers"][server_name])
        else:
            print(f"[MISSING] {server_name} not found in {MCP_CONFIG_PATH}")
            results[server_name] = False

    print("\n" + "="*50)
    print("SUMMARY RESULTS:")
    all_passed = True
    for server_name, passed in results.items():
        status = "PASSED" if passed else "FAILED"
        print(f"  {server_name.ljust(15)}: {status}")
        if not passed:
            all_passed = False

    if all_passed:
        print("\nAll 4 MCP servers verified and operational!")
        sys.exit(0)
    else:
        print("\nSome MCP servers failed verification.")
        sys.exit(1)

def test_probe_mcp_server_connectivity_missing_binary_returns_false():
    assert probe_mcp_server_connectivity("missing-mcp", {"command": "__nonexistent_mcp_binary__", "args": []}) is False

def test_configured_mcp_servers_live_ping():
    if os.environ.get("KONGOR_RUN_MANUAL_TESTS") != "1":
        pytest.skip("Live IDE MCP ping requires KONGOR_RUN_MANUAL_TESTS=1")
    if not os.path.isfile(MCP_CONFIG_PATH):
        pytest.skip(f"MCP config not present at {MCP_CONFIG_PATH}")
    with open(MCP_CONFIG_PATH, "r", encoding="utf-8") as f:
        mcp_config = json.load(f)
    for server_name in ("wireshark", "windbg", "memory", "ghidra"):
        cfg = mcp_config.get("mcpServers", {}).get(server_name)
        if cfg is not None:
            assert probe_mcp_server_connectivity(server_name, cfg), f"MCP server {server_name} failed ping"

if __name__ == "__main__":
    main()

