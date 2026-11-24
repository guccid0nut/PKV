#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Clangd MCP Server for ProjectKongor.
Bridges MCP tools to the local standalone LLVM clangd Language Server.
"""

import argparse
import asyncio
import os
import pathlib
import shutil
import sys
from contextlib import asynccontextmanager
from typing import Any, Dict, List, Optional

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent.parent
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
from lsp_client import LSPClient, SYMBOL_KINDS, uri_to_path


def _resolve_clangd_path() -> str:
    return resolve_tool_binary(
        ("clangd.exe", "clangd"),
        env_var="CLANGD_PATH",
        subdir_hints=("clangd", "clangd/clangd_23.1.0"),
        extra_candidates=(
            REPO_ROOT / ".tools" / "bin" / "clangd" / "clangd_23.1.0" / "bin" / "clangd.exe",
            SCRIPT_DIR / "clangd_23.1.0" / "bin" / "clangd.exe",
        ),
    )


DEFAULT_CLANGD_PATH = REPO_ROOT / ".tools" / "bin" / "clangd" / "clangd_23.1.0" / "bin" / "clangd.exe"
CLANGD_EXE = _resolve_clangd_path()

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
    clangd_cmd = [
        CLANGD_EXE,
        "--log=error",
        "--background-index",
        "--pch-storage=disk",
        "--limit-results=100",
        "-j=4",
    ]
    lsp = LSPClient()
    await lsp.start(clangd_cmd)
    await lsp.initialize(workspace_path)
    try:
        yield
    finally:
        await lsp.shutdown()

server = MCPServer("clangd-cplusplus", lifespan=lifespan)

@server.tool()
async def clangd_find_symbol(query: str) -> Dict[str, Any]:
    """
    Search symbols (functions, classes, structs, enums) by name across the C++ workspace.
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

@server.tool()
async def clangd_get_definition(file_path: str, line: int, character: int) -> Dict[str, Any]:
    """
    Jump to the definition of the symbol at the specified file, 1-based line, and 1-based character position.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
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
async def clangd_find_references(file_path: str, line: int, character: int) -> Dict[str, Any]:
    """
    Find all references / usages of the symbol at the given file, 1-based line, and 1-based character position.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
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
async def clangd_get_type_info(file_path: str, line: int, character: int) -> Dict[str, Any]:
    """
    Get type information, hover documentation, and signature for symbol at 1-based line and character.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
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
async def clangd_list_file_symbols(file_path: str) -> Dict[str, Any]:
    """
    List all symbols (outline: classes, structs, functions, enums) in a specific C/C++ source or header file.
    """
    assert lsp is not None
    abs_path = os.path.abspath(file_path)
    await lsp.open_file(abs_path)
    symbols = await lsp.document_symbol(abs_path)
    results = []
    for sym in symbols:
        name = sym.get("name", "")
        kind = SYMBOL_KINDS.get(sym.get("kind", 0), "Symbol")
        detail = sym.get("detail", "")
        start_line_0 = sym.get("range", {}).get("start", {}).get("line", 0)
        results.append({
            "name": name,
            "kind": kind,
            "detail": detail,
            "line": start_line_0 + 1
        })
    return {"file": file_path, "count": len(results), "symbols": results}

def main():
    global workspace_path
    parser = argparse.ArgumentParser(description="Clangd MCP Server")
    parser.add_argument("--workspace-dir", default=os.getcwd(), help="Root directory of C++ workspace")
    args, _ = parser.parse_known_args()
    workspace_path = os.path.abspath(args.workspace_dir)
    run_fastmcp_stdio(server, "clangd")

if __name__ == "__main__":
    main()
