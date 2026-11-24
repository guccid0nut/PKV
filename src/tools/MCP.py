#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)
"""
TAO (Together as One) - KONGOR MCP Server & Action Runner.
Named after Empath's signature ultimate ability 'Together as One' (TAO),
uniting the game client, dedicated servers, Aspire cluster, and developer tooling into a cohesive whole.
Follows the same design pattern as Claude, ComfyUI, and Blender MCP servers.

Usage:
  # Stdio MCP Server (used by Antigravity, VS Code, Cursor, and Claude Desktop via tao_mcp.py):
  python -u src/tools/tao_mcp.py

  # Direct CLI action execution (instant testing without MCP client).
  # Exit codes: 0 = success, 1 = tool error / isError result, 2 = timeout or server EOF.
  python src/tools/MCP.py --tool kongor_diagnostics
  python src/tools/MCP.py --tool kongor_game_client_manage --args '{"action":"status"}'

NOTE: This file defines NO tools. All tools live in the canonical C# server
(src/tools/kongor-cli/Mcp). Python is only a launcher / shadow-copy shim
(src/tools/kongor/mcp_server.py) plus this one-shot CLI runner.
"""

import sys
import os
import json
import argparse
import subprocess

# Add kongor directory to sys.path
script_dir = os.path.dirname(os.path.abspath(__file__))
kongor_dir = os.path.join(script_dir, "kongor")
if kongor_dir not in sys.path:
    sys.path.insert(0, kongor_dir)

from mcp_server import (
    cleanup_shadow_runtime,
    configure_dotnet_environment,
    find_mcp_command,
    find_repo_root,
    setup_windows_job_object,
    main as stdio_server_main,
)


import base64
import queue
import threading
import time

def run_tool_cli(tool_name: str, args_json: str | None = None, args_b64: str | None = None, timeout: float = 30.0) -> int:
    """Execute a single MCP tool call against the canonical .NET MCP server and print result."""
    t_total_start = time.perf_counter()
    parsed_args = {}
    if args_b64:
        try:
            raw = base64.b64decode(args_b64).decode("utf-8")
            parsed_args = json.loads(raw)
        except Exception as ex:
            sys.stderr.write(f"[MCP.py] Error parsing base64 JSON arguments: {ex} (pass valid UTF-8 JSON base64 or use --args)\n")
            return 1
    elif args_json:
        try:
            parsed_args = json.loads(args_json)
        except Exception as ex:
            sys.stderr.write(f"[MCP.py] Error parsing JSON arguments: {ex} (use --args-b64 to avoid shell quote escaping issues)\n")
            return 1

    env = os.environ.copy()
    env["PYTHONUNBUFFERED"] = "1"
    configure_dotnet_environment(env, update_process_env=True)

    repo_root = find_repo_root()
    base_cmd, shadow_runtime_dir = find_mcp_command(repo_root, env=env)

    init_msg = json.dumps({
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "Antigravity-MCP", "version": "1.0"}
        }
    })

    call_msg = json.dumps({
        "jsonrpc": "2.0",
        "id": 2,
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": parsed_args
        }
    })

    proc = subprocess.Popen(
        base_cmd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=str(repo_root),
        env=env,
        text=True,
        encoding="utf-8"
    )

    setup_windows_job_object(proc)

    # Read stdout on a background thread so we can enforce --timeout (readline() alone blocks forever).
    # stderr is drained too, otherwise a chatty server can fill the pipe buffer and deadlock.
    stdout_lines: "queue.Queue[str | None]" = queue.Queue()

    def _pump_stdout() -> None:
        try:
            for raw_line in proc.stdout:
                stdout_lines.put(raw_line)
        finally:
            stdout_lines.put(None)  # EOF sentinel

    def _pump_stderr() -> None:
        for raw_line in proc.stderr:
            sys.stderr.write(f"[kongor-mcp stderr] {raw_line}")

    threading.Thread(target=_pump_stdout, daemon=True).start()
    threading.Thread(target=_pump_stderr, daemon=True).start()

    deadline = time.monotonic() + timeout

    def _await_response(request_id: int) -> dict | None:
        """Returns the JSON-RPC message with the given id, skipping notifications. None on timeout/EOF."""
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                sys.stderr.write(f"[MCP.py] Timed out after {timeout:.1f}s waiting for response id={request_id}.\n")
                return None
            try:
                line = stdout_lines.get(timeout=remaining)
            except queue.Empty:
                continue
            if line is None:
                sys.stderr.write(f"[MCP.py] MCP server closed stdout before responding to id={request_id}.\n")
                return None
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                sys.stderr.write(f"[MCP.py] Ignoring non-JSON stdout line: {line[:200]}\n")
                continue
            if message.get("id") == request_id:
                return message
            # Notifications (e.g. notifications/progress) or unrelated messages.
            sys.stderr.write(f"[MCP.py] Skipping message while awaiting id={request_id}: "
                             f"{message.get('method', 'response id=' + str(message.get('id')))}\n")

    try:
        # 1. Send initialize and wait for its response
        t_init_start = time.perf_counter()
        proc.stdin.write(init_msg + "\n")
        proc.stdin.flush()
        if _await_response(1) is None:
            return 2
        init_ms = (time.perf_counter() - t_init_start) * 1000.0

        # 2. Send initialized notification
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        proc.stdin.flush()

        # 3. Send tools/call
        t_rpc_start = time.perf_counter()
        proc.stdin.write(call_msg + "\n")
        proc.stdin.flush()

        # 4. Read the matching response (progress notifications may arrive first)
        response = _await_response(2)
        if response is None:
            return 2
        rpc_ms = (time.perf_counter() - t_rpc_start) * 1000.0

        if "error" in response:
            print(json.dumps(response["error"], indent=2))
            return 1

        result = response.get("result", {})
        content_items = result.get("content", [])
        if content_items:
            for item in content_items:
                if item.get("type") == "text":
                    try:
                        parsed_text = json.loads(item["text"])
                        print(json.dumps(parsed_text, indent=2))
                    except Exception:
                        print(item["text"])
                elif item.get("type") == "image":
                    print(json.dumps({"type": "image", "mimeType": item.get("mimeType"), "dataLength": len(item.get("data", ""))}, indent=2))
        else:
            print(json.dumps(result, indent=2))

        total_ms = (time.perf_counter() - t_total_start) * 1000.0
        sys.stderr.write(
            f"[MCP.py] Tool '{tool_name}' completed in {total_ms:.1f}ms "
            f"(initialize={init_ms:.1f}ms, rpc={rpc_ms:.1f}ms)\n"
        )
        sys.stderr.flush()

        return 1 if result.get("isError") else 0
    except Exception as ex:
        sys.stderr.write(
            f"[MCP.py] MCP execution failed for tool '{tool_name}': {ex} "
            f"(verify Kongor.Cli build via 'dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release')\n"
        )
        return 1
    finally:
        try:
            if proc.stdin:
                proc.stdin.close()
        except Exception as ex:
            sys.stderr.write(f"[MCP.py] Debug: stdin close notice: {ex}\n")
        try:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=2.0)
        except Exception as ex:
            sys.stderr.write(f"[MCP.py] Debug: child terminate notice: {ex}\n")
            try:
                proc.kill()
            except Exception as kex:
                sys.stderr.write(f"[MCP.py] Debug: child kill notice: {kex}\n")
        cleanup_shadow_runtime(shadow_runtime_dir)


if __name__ == "__main__":
    if "--tool" in sys.argv:
        parser = argparse.ArgumentParser(description="ProjectKongor MCP Action Runner")
        parser.add_argument("--tool", required=True, help="MCP tool name to execute")
        parser.add_argument("--args", default=None, help="JSON arguments string")
        parser.add_argument("--args-b64", default=None, help="Base64-encoded JSON arguments string")
        parser.add_argument("--timeout", type=float, default=30.0, help="Execution timeout in seconds")
        cli_args = parser.parse_args()
        sys.exit(run_tool_cli(cli_args.tool, cli_args.args, cli_args.args_b64, cli_args.timeout))
    else:
        sys.exit(stdio_server_main())

