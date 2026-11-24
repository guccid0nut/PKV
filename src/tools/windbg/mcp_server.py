#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Together as One (TAO) - WinDbg CDB/KD MCP Server Wrapper for ProjectKongor.
Enforces tracked, cleanroom-portable guards in front of mcp_windbg:
- Pre-flight >= 64-byte & 4-byte 'MDMP' magic header validation before spawning cdb.exe (MCP-172 [formerly MCP-109], MCP-129)
- Offline local symbol cache prioritization to prevent 3m0s msdl.microsoft.com hangs (MCP-129, MCP-131, MCP-165, MCP-170)
- PascalCase / camelCase / alias argument normalization (MCP-166 [formerly MCP-103])
- Stdio JSON-RPC print pollution shielding & unified NDJSON outcome telemetry
"""

from __future__ import annotations

import builtins
from datetime import datetime, timezone
import functools
import json
import logging
from logging.handlers import RotatingFileHandler
import os
import sys
import time
from typing import Any, Dict, Optional, Tuple

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "..", ".."))
_TOOLS_DIR = os.path.dirname(SCRIPT_DIR)
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

from _common.mcp_telemetry import (
    record_mcp_outcome as _shared_record_mcp_outcome,
    shield_stdio_stdout,
)

shield_stdio_stdout()
LOG_DIR = os.path.join(REPO_ROOT, "logs")
os.makedirs(LOG_DIR, exist_ok=True)
LOG_FILE = os.path.join(LOG_DIR, "windbg_mcp.log")
DEFAULT_OUTCOME_LOG = os.path.join(LOG_DIR, "mcp_python_outcomes.ndjson")

logger = logging.getLogger("windbg_mcp")
logger.setLevel(logging.INFO)
formatter = logging.Formatter("%(asctime)s [%(levelname)s] [%(name)s] %(message)s")

if not logger.handlers:
    file_handler = RotatingFileHandler(
        LOG_FILE,
        maxBytes=10 * 1024 * 1024,
        backupCount=3,
        encoding="utf-8",
    )
    file_handler.setLevel(logging.INFO)
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)

    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setLevel(logging.INFO)
    stderr_handler.setFormatter(formatter)
    logger.addHandler(stderr_handler)

_orig_builtin_print = builtins.print


def _safe_print(*args: Any, **kwargs: Any) -> Any:
    target_file = kwargs.get("file")
    if target_file is None or target_file is sys.stdout:
        kwargs["file"] = sys.stderr
    return _orig_builtin_print(*args, **kwargs)


def record_mcp_outcome(
    tool_name: str,
    is_error: bool,
    duration_ms: float,
    issue_id: Optional[str] = None,
    error_message: Optional[str] = None,
    outcome_log_path: Optional[str] = None,
) -> None:
    """Append unified NDJSON telemetry entry for WinDbg MCP invocations via shared _common/mcp_telemetry."""
    _shared_record_mcp_outcome(
        server_name="windbg",
        tool_name=tool_name,
        is_error=is_error,
        duration_ms=duration_ms,
        issue_id=issue_id,
        error_message=error_message,
        outcome_log_path=outcome_log_path,
    )


def normalize_windbg_arguments(tool_name: str, arguments: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    """Normalize PascalCase, camelCase, and alias parameter names (MCP-166 [formerly MCP-103])."""
    if not arguments or not isinstance(arguments, dict):
        return {}
    norm: Dict[str, Any] = dict(arguments)
    alias_map = {
        "SessionId": "session_id",
        "sessionId": "session_id",
        "session": "session_id",
        "Command": "command",
        "cmd": "command",
        "DumpPath": "dump_path",
        "dumpPath": "dump_path",
        "file_path": "dump_path",
        "FilePath": "dump_path",
        "DirectoryPath": "directory_path",
        "directoryPath": "directory_path",
        "dir": "directory_path",
        "Recursive": "recursive",
        "SymbolsPath": "symbols_path",
        "symbolsPath": "symbols_path",
        "IncludeStackTrace": "include_stack_trace",
        "includeStackTrace": "include_stack_trace",
        "IncludeModules": "include_modules",
        "includeModules": "include_modules",
        "IncludeThreads": "include_threads",
        "includeThreads": "include_threads",
        "TimeoutSeconds": "timeout_seconds",
        "timeoutSeconds": "timeout_seconds",
        "timeout": "timeout_seconds",
        "ConnectionString": "connection_string",
        "connectionString": "connection_string",
        "Resume": "resume",
    }
    for src, dst in alias_map.items():
        if src in norm and dst not in norm:
            norm[dst] = norm.pop(src)
        elif src in norm and src != dst:
            del norm[src]

    if "path" in norm:
        val = norm.pop("path")
        if tool_name == "list_dumps" and "directory_path" not in norm:
            norm["directory_path"] = val
        elif tool_name == "open_cdb_dump" and "dump_path" not in norm:
            norm["dump_path"] = val

    return norm


def validate_minidump_header(dump_path: str) -> Tuple[bool, Optional[str], Optional[str]]:
    """
    Validate dump file existence, >= 64-byte size, and 4-byte 'MDMP' magic header
    before spawning cdb.exe (MCP-172 [formerly MCP-109], MCP-129).
    Returns (is_valid, issue_id, actionable_error).
    """
    if not dump_path or not isinstance(dump_path, str) or not dump_path.strip():
        return False, "MCP-166", "Missing 'dump_path' parameter. Pass {'dump_path': '<absolute-path-to.dmp>'}."

    abs_path = os.path.abspath(dump_path)
    if not os.path.isfile(abs_path):
        return (
            False,
            "MCP-172",
            f"Dump file not found: '{abs_path}'. Discover valid dumps first with everything_search(query='ext:dmp').",
        )

    try:
        size = os.path.getsize(abs_path)
        if size < 64:
            return (
                False,
                "MCP-172",
                f"MCP-172 Pre-Flight Guard: Rejecting truncated/zero-byte dump '{abs_path}' ({size} bytes < 64 bytes) to prevent cdb.exe hang.",
            )
        with open(abs_path, "rb") as fh:
            magic = fh.read(4)
        if magic != b"MDMP":
            return (
                False,
                "MCP-172",
                f"MCP-172 Pre-Flight Guard: File '{abs_path}' does not start with 'MDMP' minidump header (got {magic!r}).",
            )
    except OSError as exc:
        return False, "MCP-172", f"Cannot read dump file '{abs_path}': {exc}"

    return True, None, None


def sanitize_offline_symbols(
    per_call_symbols: Optional[str],
    server_default_symbols: Optional[str],
) -> str:
    """
    Strip remote HTTP symbol servers by default to prevent 3m0s msdl.microsoft.com hangs
    (MCP-129, MCP-131, MCP-165, MCP-170) unless per_call_symbols explicitly requests http(s)://.
    """
    if per_call_symbols and ("http://" in per_call_symbols.lower() or "https://" in per_call_symbols.lower()):
        combined = [s for s in (per_call_symbols, server_default_symbols) if s]
        return ";".join(combined)

    parts: list[str] = []
    if per_call_symbols:
        parts.append(per_call_symbols)

    if server_default_symbols:
        for token in server_default_symbols.split(";"):
            token = token.strip()
            if not token:
                continue
            if token.upper().startswith("SRV*"):
                srv_chunks = [
                    c
                    for c in token[4:].split("*")
                    if c and not c.lower().startswith("http://") and not c.lower().startswith("https://")
                ]
                for local_dir in srv_chunks:
                    if local_dir not in parts:
                        parts.append(local_dir)
            elif "http://" not in token.lower() and "https://" not in token.lower():
                if token not in parts:
                    parts.append(token)

    for rel_dir in ("games/client", "games/client/windows-x64", "games/server"):
        candidate = os.path.join(REPO_ROOT, rel_dir)
        if os.path.isdir(candidate) and candidate not in parts:
            parts.append(candidate)

    return ";".join(parts) if parts else "C:\\Symbols"


def install_windbg_runtime_guards() -> bool:
    """Attach tracked pre-flight, symbol, and normalization guards onto mcp_windbg when installed."""
    try:
        import mcp_windbg.cdb_session as cdb_mod
        import mcp_windbg.server as srv_mod
    except ImportError:
        logger.warning("mcp_windbg package not installed in active Python environment.")
        return False

    srv_mod._normalize_windbg_arguments = normalize_windbg_arguments
    srv_mod._sanitize_offline_symbols = sanitize_offline_symbols
    if hasattr(srv_mod, "mcp"):
        from _common.mcp_telemetry import instrument_fastmcp_server
        instrument_fastmcp_server(srv_mod.mcp, "windbg")

    orig_init = cdb_mod.CDBSession.__init__
    if not getattr(orig_init, "_kongor_guarded", False):

        @functools.wraps(orig_init)
        def _guarded_cdb_init(self: Any, dump_path: str, *args: Any, **kwargs: Any) -> None:
            t0 = time.perf_counter()
            ok, issue_id, err = validate_minidump_header(dump_path)
            if not ok:
                record_mcp_outcome(
                    "open_cdb_dump",
                    is_error=True,
                    duration_ms=(time.perf_counter() - t0) * 1000.0,
                    issue_id=issue_id,
                    error_message=err,
                )
                raise ValueError(err)

            if "symbols_path" in kwargs:
                kwargs["symbols_path"] = sanitize_offline_symbols(kwargs.get("symbols_path"), os.environ.get("_NT_SYMBOL_PATH"))
            orig_init(self, dump_path, *args, **kwargs)
            record_mcp_outcome(
                "open_cdb_dump",
                is_error=False,
                duration_ms=(time.perf_counter() - t0) * 1000.0,
            )

        _guarded_cdb_init._kongor_guarded = True  # type: ignore[attr-defined]
        cdb_mod.CDBSession.__init__ = _guarded_cdb_init

    return True


def main() -> None:
    builtins.print = _safe_print
    install_windbg_runtime_guards()
    from mcp_windbg.__main__ import main as windbg_main

    logger.info("Starting WinDbg MCP server for ProjectKongor with args: %s", sys.argv[1:])
    windbg_main()


if __name__ == "__main__":
    main()
