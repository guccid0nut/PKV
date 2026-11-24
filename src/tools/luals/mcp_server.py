#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Lua Language Server MCP for ProjectKongor.
Bridges MCP tools to the local standalone lua-language-server via stdio LSP.
"""

import argparse
import asyncio
import os
import pathlib
import sys
from contextlib import asynccontextmanager
from typing import Any, Dict, List, Optional

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
_TOOLS_DIR = str(SCRIPT_DIR.parent)
for _p in (str(SCRIPT_DIR), _TOOLS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

from _common.mcp_telemetry import (
    resolve_tool_binary,
    run_fastmcp_stdio,
    shield_stdio_stdout,
)

shield_stdio_stdout()

try:
    from mcp.server.fastmcp import FastMCP as MCPServer
except ImportError:
    from mcp.server.mcpserver import MCPServer
from lsp_client import LSPClient, SYMBOL_KINDS, DIAGNOSTIC_SEVERITY, uri_to_path

DEFAULT_LUA_LS_PATH = SCRIPT_DIR / "bin" / "bin" / "lua-language-server.exe"
LUA_LS_EXE = resolve_tool_binary(
    ("lua-language-server.exe", "lua-language-server"),
    env_var="LUA_LS_PATH",
    subdir_hints=("luals", "lua-language-server"),
    extra_candidates=(DEFAULT_LUA_LS_PATH,),
)

lsp: Optional[LSPClient] = None
workspace_path: str = os.getcwd()

def _format_location(loc: Dict[str, Any]) -> str:
    path = uri_to_path(loc.get("uri", ""))
    start = loc.get("range", {}).get("start", {})
    line = start.get("line", 0) + 1
    col = start.get("character", 0) + 1
    return f"{path}:{line}:{col}"

def _source_context(path: str, line_0: int, context: int = 3) -> str:
    try:
        lines = pathlib.Path(path).read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return "(could not read source)"
    lo = max(0, line_0 - context)
    hi = min(len(lines), line_0 + context + 1)
    out = []
    for i in range(lo, hi):
        marker = ">>>" if i == line_0 else "   "
        out.append(f"{marker} {i + 1:4d} | {lines[i]}")
    return "\n".join(out)

@asynccontextmanager
async def lifespan(server_instance: MCPServer):
    global lsp
    luals_cmd = [LUA_LS_EXE]
    lsp = LSPClient()
    await lsp.start(luals_cmd)
    await lsp.initialize(workspace_path)
    try:
        yield
    finally:
        await lsp.shutdown()

server = MCPServer("luals-lua", lifespan=lifespan)

@server.tool()
async def lua_check_diagnostics(file_path: str) -> Dict[str, Any]:
    """
    Run static syntax and type diagnostics on a Lua script.
    Detects syntax errors, undefined globals/variables, missing 'end' blocks, and type mismatches.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
    if not os.path.isfile(abs_path):
        return {"error": f"File not found: {abs_path}", "diagnostics": []}

    raw_diags = await lsp.get_diagnostics(abs_path)
    results = []
    for d in raw_diags:
        start = d.get("range", {}).get("start", {})
        line_0 = start.get("line", 0)
        col_0 = start.get("character", 0)
        severity_code = d.get("severity", 1)
        severity = DIAGNOSTIC_SEVERITY.get(severity_code, "Unknown")
        results.append({
            "severity": severity,
            "line": line_0 + 1,
            "character": col_0 + 1,
            "message": d.get("message", ""),
            "source": d.get("source", "lua-ls"),
            "code": d.get("code", ""),
            "context": _source_context(abs_path, line_0, context=1)
        })

    return {
        "file": abs_path,
        "diagnostics_count": len(results),
        "has_errors": any(d["severity"] == "Error" for d in results),
        "diagnostics": results
    }

@server.tool()
async def lua_get_definition(file_path: str, line: int, character: int) -> Dict[str, Any]:
    """
    Jump to the definition of the Lua symbol at the specified file, 1-based line, and 1-based character position.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
    if not os.path.isfile(abs_path):
        return {"error": f"File not found: {abs_path}", "definitions": []}

    await lsp.open_file(abs_path)
    defs = await lsp.definition(abs_path, line - 1, character - 1)
    if not defs:
        return {"found": False, "definitions": []}

    results = []
    for d in defs:
        loc_str = _format_location(d)
        target_path = uri_to_path(d.get("uri", ""))
        start_line_0 = d.get("range", {}).get("start", {}).get("line", 0)
        results.append({
            "location": loc_str,
            "path": target_path,
            "line": start_line_0 + 1,
            "context": _source_context(target_path, start_line_0)
        })
    return {"found": True, "definitions": results}

@server.tool()
async def lua_find_references(file_path: str, line: int, character: int) -> Dict[str, Any]:
    """
    Find all references / usages of the Lua symbol at the given file, 1-based line, and 1-based character position.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
    if not os.path.isfile(abs_path):
        return {"error": f"File not found: {abs_path}", "references": []}

    await lsp.open_file(abs_path)
    refs = await lsp.references(abs_path, line - 1, character - 1)
    results = []
    for r in refs:
        loc_str = _format_location(r)
        target_path = uri_to_path(r.get("uri", ""))
        start_line_0 = r.get("range", {}).get("start", {}).get("line", 0)
        results.append({
            "location": loc_str,
            "path": target_path,
            "line": start_line_0 + 1
        })
    return {"count": len(results), "references": results}

@server.tool()
async def lua_get_type_info(file_path: str, line: int, character: int) -> Dict[str, Any]:
    """
    Get type information, hover documentation, and function signatures for a Lua symbol at 1-based line and character.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
    if not os.path.isfile(abs_path):
        return {"error": f"File not found: {abs_path}"}

    await lsp.open_file(abs_path)
    hover_res = await lsp.hover(abs_path, line - 1, character - 1)
    if not hover_res:
        return {"found": False}

    contents = hover_res.get("contents", "")
    text = ""
    if isinstance(contents, dict):
        text = contents.get("value", "")
    elif isinstance(contents, str):
        text = contents
    elif isinstance(contents, list):
        text = "\n".join(str(c) for c in contents)

    return {"found": True, "type_info": text}

@server.tool()
async def lua_list_file_symbols(file_path: str) -> Dict[str, Any]:
    """
    List all symbols (outline: functions, methods, tables, variables) defined in a Lua script.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
    if not os.path.isfile(abs_path):
        return {"error": f"File not found: {abs_path}", "symbols": []}

    await lsp.open_file(abs_path)
    symbols = await lsp.document_symbol(abs_path)
    results = []
    for sym in symbols:
        name = sym.get("name", "")
        kind = SYMBOL_KINDS.get(sym.get("kind", 0), "Symbol")
        detail = sym.get("detail", "")
        range_data = sym.get("range") or sym.get("location", {}).get("range", {})
        start_line_0 = range_data.get("start", {}).get("line", 0)
        results.append({
            "name": name,
            "kind": kind,
            "detail": detail,
            "line": start_line_0 + 1
        })
    return {"file": abs_path, "count": len(results), "symbols": results}

@server.tool()
async def lua_find_symbol(query: str) -> Dict[str, Any]:
    """
    Search symbols (functions, tables, globals) by name across the Lua workspace.
    """
    assert lsp is not None
    symbols = await lsp.workspace_symbol(query)
    results = []
    for sym in symbols:
        name = sym.get("name", "")
        kind = SYMBOL_KINDS.get(sym.get("kind", 0), "Symbol")
        container = sym.get("containerName", "")
        loc = sym.get("location", {})
        results.append({
            "name": name,
            "kind": kind,
            "container": container,
            "location": _format_location(loc)
        })
    return {"query": query, "count": len(results), "symbols": results}

def main():
    global workspace_path
    parser = argparse.ArgumentParser(description="Lua Language Server MCP")
    parser.add_argument("--workspace-dir", default=os.getcwd(), help="Root directory of Lua workspace")
    args, _ = parser.parse_known_args()
    workspace_path = os.path.abspath(args.workspace_dir)
    run_fastmcp_stdio(server, "luals")

if __name__ == "__main__":
    main()
