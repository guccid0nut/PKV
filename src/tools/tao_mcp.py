#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)
"""
TAO (Together as One) - KONGOR MCP Server & Action Runner.
Named after Empath's signature ultimate ability 'Together as One' (TAO),
uniting the game client, dedicated servers, Aspire cluster, and developer tooling into a cohesive whole.

This file serves as a canonical, descriptive entry point for external AI assistants
(Anthropic Claude, GitHub Copilot, Cursor, etc.) to launch the TAO MCP Server.

Usage:
  # Stdio MCP Server (Claude Desktop / Cursor / Copilot):
  python src/tools/tao_mcp.py

  # Direct CLI action test (instant smoke test):
  python src/tools/tao_mcp.py --tool kongor_diagnostics
"""

import os
import sys

# Ensure sibling scripts can be imported cleanly
script_dir = os.path.dirname(os.path.abspath(__file__))
if script_dir not in sys.path:
    sys.path.insert(0, script_dir)
kongor_dir = os.path.join(script_dir, "kongor")
if kongor_dir not in sys.path:
    sys.path.insert(0, kongor_dir)

os.environ.setdefault("PYTHONUNBUFFERED", "1")

from MCP import run_tool_cli, stdio_server_main
from mcp_server import configure_dotnet_environment, discover_dotnet_root

if __name__ == "__main__":
    if "--tool" in sys.argv:
        # CLI action runner mode
        import argparse
        parser = argparse.ArgumentParser(description="TAO (Together as One) MCP CLI Tool Invoker")
        parser.add_argument("--tool", required=True, help="Name of the MCP tool to execute (e.g. kongor_diagnostics)")
        parser.add_argument("--args", help="Tool arguments as JSON string")
        parser.add_argument("--args-b64", help="Tool arguments as base64-encoded JSON string")
        parser.add_argument("--timeout", type=float, default=30.0, help="Execution timeout in seconds")
        cli_args = parser.parse_args()
        sys.exit(run_tool_cli(cli_args.tool, cli_args.args, cli_args.args_b64, cli_args.timeout))
    else:
        # Standard MCP stdio server mode
        sys.exit(stdio_server_main())
