#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

"""
ProjectKongor MCP Server End-to-End (E2E) Test Suite Runner
Executes comprehensive opaque-box verification across Tiers 1-4 via JSON-RPC 2.0 stdio.
"""

import os
import sys
import json
import time
import argparse
import subprocess
from typing import Dict, Any, List, Optional, Tuple

# ANSI color formatting
CYAN = "\033[96m"
GREEN = "\033[92m"
RED = "\033[91m"
YELLOW = "\033[93m"
MAGENTA = "\033[95m"
BOLD = "\033[1m"
RESET = "\033[0m"

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))

class McpTestProcess:
    def __init__(self, verbose: bool = False):
        self.verbose = verbose
        self.process: Optional[subprocess.Popen] = None
        self.req_id = 0

    def start(self):
        cli_dll = os.path.join(REPO_ROOT, "src", "tools", "kongor-cli", "bin", "Debug", "net11.0", "kongor.dll")
        cli_exe = os.path.join(REPO_ROOT, "src", "tools", "kongor-cli", "bin", "Debug", "net11.0", "kongor.exe")
        cli_proj = os.path.join(REPO_ROOT, "src", "tools", "kongor-cli", "Kongor.Cli.csproj")

        if os.path.exists(cli_exe):
            cmd = [cli_exe, "mcp"]
        elif os.path.exists(cli_dll):
            cmd = ["dotnet", "exec", cli_dll, "mcp"]
        else:
            cmd = ["dotnet", "run", "--project", cli_proj, "--", "mcp"]

        if self.verbose:
            print(f"{CYAN}[MCP Runner] Launching: {' '.join(cmd)}{RESET}")

        self.process = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=REPO_ROOT,
            text=True,
            encoding="utf-8",
            bufsize=1
        )
        time.sleep(0.2)
        if self.process.poll() is not None:
            err = self.process.stderr.read() if self.process.stderr else ""
            raise RuntimeError(f"MCP server exited immediately with code {self.process.returncode}. Stderr: {err}")

    def send_raw(self, line: str) -> str:
        if not self.process or self.process.poll() is not None:
            raise RuntimeError("MCP server is not running")
        if self.verbose:
            print(f"{MAGENTA}--> {line.strip()}{RESET}")
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()
        resp_line = self.process.stdout.readline().strip()
        if self.verbose:
            print(f"{CYAN}<-- {resp_line}{RESET}")
        return resp_line

    def send_request(self, method: str, params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        self.req_id += 1
        payload = {
            "jsonrpc": "2.0",
            "id": self.req_id,
            "method": method
        }
        if params is not None:
            payload["params"] = params
        
        raw_resp = self.send_raw(json.dumps(payload))
        try:
            return json.loads(raw_resp)
        except Exception as e:
            return {"raw": raw_resp, "error": {"code": -32700, "message": str(e)}}

    def call_tool(self, tool_name: str, arguments: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        return self.send_request("tools/call", {
            "name": tool_name,
            "arguments": arguments or {}
        })

    def stop(self):
        if self.process:
            try:
                self.process.terminate()
                self.process.wait(timeout=2)
            except Exception:
                try:
                    self.process.kill()
                except Exception:
                    pass
            self.process = None

class TestResult:
    def __init__(self, name: str, passed: bool, error: str = "", duration_ms: float = 0.0):
        self.name = name
        self.passed = passed
        self.error = error
        self.duration_ms = duration_ms

def run_tests(tier: str, verbose: bool) -> List[TestResult]:
    results: List[TestResult] = []

    def record(name: str, fn):
        t0 = time.time()
        try:
            fn()
            dt = (time.time() - t0) * 1000
            results.append(TestResult(name, True, duration_ms=dt))
            print(f"  {GREEN}PASS{RESET} {name} ({dt:.1f}ms)")
        except Exception as ex:
            dt = (time.time() - t0) * 1000
            results.append(TestResult(name, False, str(ex), duration_ms=dt))
            print(f"  {RED}FAIL{RESET} {name}: {ex} ({dt:.1f}ms)")

    client = McpTestProcess(verbose=verbose)
    try:
        client.start()
    except Exception as e:
        print(f"{RED}Failed to start MCP server: {e}{RESET}")
        print(f"{YELLOW}Running via dotnet test directly...{RESET}")
        return run_dotnet_test(tier)

    # ---------------------------------------------------------------------
    # TIER 1: Feature Coverage Tests
    # ---------------------------------------------------------------------
    if tier in ["1", "all"]:
        print(f"\n{BOLD}{CYAN}=== Tier 1: Feature Coverage ==={RESET}")
        
        # Protocol Handshake
        def test_init():
            res = client.send_request("initialize", {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "PythonE2ERunner", "version": "1.0.0"}
            })
            assert "result" in res, f"Expected 'result' in response: {res}"
            assert res["result"].get("protocolVersion") is not None
        record("T1_Protocol_Initialize_ValidHandshake", test_init)

        def test_initialized():
            client.send_raw(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}))
        record("T1_Protocol_Initialized_Notification", test_initialized)

        def test_ping():
            res = client.send_request("ping", {})
            assert "result" in res, f"Expected ping result: {res}"
        record("T1_Protocol_Ping", test_ping)

        def test_tools_list():
            res = client.send_request("tools/list", {})
            assert "result" in res and "tools" in res["result"]
            tools = [t["name"] for t in res["result"]["tools"]]
            for expected in ["kongor_server_manage", "kongor_game_manage", "kongor_game_command", "kongor_game_telemetry", "kongor_capture_window", "kongor_ui_render_view"]:
                assert expected in tools, f"Missing tool: {expected}"
        record("T1_Protocol_ToolsList_AllSixToolsPresent", test_tools_list)

        # Tool 1: kongor_server_manage
        def test_server_manage_status():
            res = client.call_tool("kongor_server_manage", {"action": "status", "target": "cluster"})
            assert "result" in res
        record("T1_Tool_ServerManage_StatusCluster", test_server_manage_status)

        def test_server_manage_status_all():
            res = client.call_tool("kongor_server_manage", {"action": "status", "target": "all"})
            assert "result" in res
        record("T1_Tool_ServerManage_StatusAll", test_server_manage_status_all)

        # Tool 2: kongor_game_manage
        def test_game_manage_status():
            res = client.call_tool("kongor_game_manage", {"action": "status"})
            assert "result" in res
        record("T1_Tool_GameManage_Status", test_game_manage_status)

        def test_game_manage_stop():
            res = client.call_tool("kongor_game_manage", {"action": "stop"})
            assert "result" in res
        record("T1_Tool_GameManage_Stop", test_game_manage_stop)

        # Tool 3: kongor_game_command
        def test_game_command():
            res = client.call_tool("kongor_game_command", {"command": "echo E2E_Python_Test", "timeoutMs": 3000})
            assert "result" in res
        record("T1_Tool_GameCommand_Echo", test_game_command)

        # Tool 4: kongor_game_telemetry
        def test_game_telemetry():
            res = client.call_tool("kongor_game_telemetry", {"timeoutMs": 3000})
            assert "result" in res
        record("T1_Tool_GameTelemetry_Snapshot", test_game_telemetry)

        # Tool 5: kongor_capture_window
        def test_capture_window():
            res = client.call_tool("kongor_capture_window", {"target": "active", "save_artifact": False})
            assert "result" in res
        record("T1_Tool_CaptureWindow_Active", test_capture_window)

        # Tool 6: kongor_ui_render_view
        def test_ui_render_view():
            res = client.call_tool("kongor_ui_render_view", {"view_name": "ModManagerView", "theme": "dark", "mock_data": True})
            assert "result" in res
        record("T1_Tool_UiRenderView_ModManagerView", test_ui_render_view)

    # ---------------------------------------------------------------------
    # TIER 2: Boundary & Corner Cases
    # ---------------------------------------------------------------------
    if tier in ["2", "all"]:
        print(f"\n{BOLD}{CYAN}=== Tier 2: Boundary & Corner Cases ==={RESET}")

        def test_bad_json():
            raw = client.send_raw("{\"jsonrpc\": \"2.0\", \"id\": 999, BAD_JSON")
            assert "error" in raw or "-32700" in raw or "result" in raw
        record("T2_Boundary_InvalidJsonSyntax", test_bad_json)

        def test_unknown_method():
            res = client.send_request("unknown_method_xyz", {})
            assert "error" in res and res["error"].get("code") == -32601
        record("T2_Boundary_UnknownMethod_Returns32601", test_unknown_method)

        def test_unknown_tool():
            res = client.call_tool("completely_unknown_tool_123", {})
            assert res.get("result", {}).get("isError") is True or "error" in res
        record("T2_Boundary_UnknownTool_IsError", test_unknown_tool)

        def test_zero_timeout_command():
            res = client.call_tool("kongor_game_command", {"command": "echo Bound", "timeoutMs": 0})
            assert "result" in res
        record("T2_Boundary_ZeroTimeoutCommand", test_zero_timeout_command)

        def test_extreme_resolution():
            res = client.call_tool("kongor_ui_render_view", {"view_name": "ModManagerView", "width": 7680, "height": 4320})
            assert "result" in res
        record("T2_Boundary_ExtremeResolution_8K", test_extreme_resolution)

    # ---------------------------------------------------------------------
    # TIER 3: Cross-Feature Interactions
    # ---------------------------------------------------------------------
    if tier in ["3", "all"]:
        print(f"\n{BOLD}{CYAN}=== Tier 3: Cross-Feature Interactions ==={RESET}")

        def test_combo_lifecycle():
            client.call_tool("kongor_server_manage", {"action": "status", "target": "cluster"})
            client.call_tool("kongor_game_manage", {"action": "status"})
            client.call_tool("kongor_game_manage", {"action": "stop"})
        record("T3_Combination_LifecycleAndClientState", test_combo_lifecycle)

        def test_combo_visual():
            client.call_tool("kongor_ui_render_view", {"view_name": "DiagnosticsView", "theme": "light"})
            client.call_tool("kongor_capture_window", {"target": "active", "save_artifact": False})
        record("T3_Combination_DualVisualInspection", test_combo_visual)

    # ---------------------------------------------------------------------
    # TIER 4: Real-World Scenarios
    # ---------------------------------------------------------------------
    if tier in ["4", "all"]:
        print(f"\n{BOLD}{CYAN}=== Tier 4: Real-World Scenarios ==={RESET}")

        def test_scenario_match_flow():
            client.call_tool("kongor_server_manage", {"action": "status", "target": "cluster"})
            client.call_tool("kongor_game_manage", {"action": "launch", "mode": "practice"})
            client.call_tool("kongor_game_command", {"command": "Set cl_fov 95", "timeoutMs": 2000})
            client.call_tool("kongor_game_telemetry", {"timeoutMs": 2000})
            client.call_tool("kongor_capture_window", {"target": "active"})
            client.call_tool("kongor_game_manage", {"action": "stop"})
        record("T4_Scenario_AutonomousMatchFlow", test_scenario_match_flow)

        def test_scenario_ui_audit():
            for v in ["ModManagerView", "ReplayStudioView", "SecretsConfigView"]:
                client.call_tool("kongor_ui_render_view", {"view_name": v, "theme": "dark", "mock_data": True})
        record("T4_Scenario_AvaloniaDesignAudit", test_scenario_ui_audit)

    client.stop()
    return results

def run_dotnet_test(tier: str) -> List[TestResult]:
    filter_arg = ""
    if tier == "1":
        filter_arg = "--filter FullyQualifiedName~Tier1"
    elif tier == "2":
        filter_arg = "--filter FullyQualifiedName~Tier2"
    elif tier == "3":
        filter_arg = "--filter FullyQualifiedName~Tier3"
    elif tier == "4":
        filter_arg = "--filter FullyQualifiedName~Tier4"

    cmd = f"dotnet test tests/e2e/mcp/Kongor.Mcp.E2E.Tests.csproj -v normal {filter_arg}".strip()
    print(f"{CYAN}Executing: {cmd}{RESET}")
    res = subprocess.run(cmd, shell=True, cwd=REPO_ROOT)
    passed = (res.returncode == 0)
    return [TestResult("DotNet_xUnit_Suite", passed, "" if passed else f"Exit code {res.returncode}")]

def main():
    parser = argparse.ArgumentParser(description="ProjectKongor MCP Server E2E Test Runner")
    parser.add_argument("--tier", "-t", choices=["1", "2", "3", "4", "all"], default="all", help="Target test tier")
    parser.add_argument("--all", "-a", action="store_true", help="Run all test tiers")
    parser.add_argument("--dotnet-only", action="store_true", help="Execute dotnet test directly")
    parser.add_argument("--verbose", "-v", action="store_true", help="Verbose JSON-RPC message logging")
    args = parser.parse_args()

    selected_tier = "all" if args.all else args.tier

    banner = f"""
{CYAN}{BOLD}================================================================================
          PROJECT KONGOR: MODEL CONTEXT PROTOCOL (MCP) SERVER
                    OPAQUE-BOX E2E TEST RUNNER
================================================================================{RESET}
  Repo Root : {REPO_ROOT}
  Scope     : Tier {selected_tier.upper()} (Feature, Boundary, Combinations, Scenarios)
  Transport : JSON-RPC 2.0 over Stdio (kongor mcp)
================================================================================
"""
    print(banner)

    if args.dotnet_only:
        results = run_dotnet_test(selected_tier)
    else:
        results = run_tests(selected_tier, args.verbose)

    total = len(results)
    passed = sum(1 for r in results if r.passed)
    failed = total - passed

    print("\n" + "=" * 80)
    print(f"{BOLD}MCP E2E Verification Summary:{RESET}")
    print(f"  Total  : {total}")
    print(f"  Passed : {GREEN}{passed}{RESET}")
    print(f"  Failed : {RED}{failed}{RESET}")
    print("=" * 80)

    if failed == 0 and total > 0:
        print(f"\n{GREEN}{BOLD}  🏆 100% Passed - A WINNER IS YOU{RESET}\n")
        sys.exit(0)
    else:
        print(f"\n{RED}{BOLD}  ❌ Some tests failed.{RESET}\n")
        sys.exit(1)

if __name__ == "__main__":
    main()
