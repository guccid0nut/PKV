#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Value-Weighted Testing Gate for WinDbg & Texconv Python MCP Pre-Flight Guards (H2-1d):
- protects: Zero-byte/corrupt minidump pre-flight rejection (MCP-172), offline symbol sanitization (MCP-129/131),
  PascalCase/camelCase/alias parameter normalization across windbg (MCP-166) and texconv (MCP-127/128/130/154/167),
  and unified NDJSON outcome logging.
- fails_when: 0-byte or non-MDMP dump reaches cdb.exe, remote msdl URLs leak into offline symbol path,
  PascalCase arguments fail normalization, or truncated DDS files pass pre-flight.
- seam: Tracked src/tools/windbg/mcp_server.py and src/tools/texconv/mcp_server.py modules against temp files.
"""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import types
import sys


REPO_ROOT = Path(__file__).resolve().parents[2]


def _load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, str(path))
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _ensure_stub_mcp_if_missing() -> None:
    """Allow importing texconv/mcp_server.py even on Python runners without 'mcp' installed."""
    try:
        import mcp  # noqa: F401
        return
    except ImportError:
        pass

    mcp_pkg = types.ModuleType("mcp")
    mcp_server_pkg = types.ModuleType("mcp.server")
    fastmcp_mod = types.ModuleType("mcp.server.fastmcp")

    class _StubFastMCP:
        def __init__(self, _name: str) -> None:
            self._mcp_server = None

        def tool(self):
            def _decorator(fn):
                return fn

            return _decorator

        def run(self, **_kwargs) -> None:
            return None

    fastmcp_mod.FastMCP = _StubFastMCP  # type: ignore[attr-defined]
    sys.modules.setdefault("mcp", mcp_pkg)
    sys.modules.setdefault("mcp.server", mcp_server_pkg)
    sys.modules.setdefault("mcp.server.fastmcp", fastmcp_mod)


def test_windbg_argument_normalization_and_minidump_preflight(tmp_path: Path) -> None:
    windbg = _load_module("kongor_windbg_mcp_test", REPO_ROOT / "src" / "tools" / "windbg" / "mcp_server.py")

    # 1. MCP-166 (formerly MCP-103): PascalCase / camelCase / alias normalization
    norm_cmd = windbg.normalize_windbg_arguments(
        "run_cdb_command",
        {"SessionId": "cdb-123", "Command": "!analyze -v", "TimeoutSeconds": 15},
    )
    assert norm_cmd == {"session_id": "cdb-123", "command": "!analyze -v", "timeout_seconds": 15}

    norm_open = windbg.normalize_windbg_arguments(
        "open_cdb_dump",
        {"FilePath": "C:/dumps/crash.dmp", "IncludeStackTrace": True},
    )
    assert norm_open == {"dump_path": "C:/dumps/crash.dmp", "include_stack_trace": True}

    norm_list = windbg.normalize_windbg_arguments("list_dumps", {"path": "C:/dumps"})
    assert norm_list == {"directory_path": "C:/dumps"}

    # 2. MCP-172 (formerly MCP-109): 0-byte & corrupt header rejection vs valid MDMP acceptance
    zero_dmp = tmp_path / "zero.dmp"
    zero_dmp.write_bytes(b"")
    ok, issue, err = windbg.validate_minidump_header(str(zero_dmp))
    assert not ok
    assert issue == "MCP-172"
    assert err is not None and "0 bytes" in err

    bad_magic = tmp_path / "bad_magic.dmp"
    bad_magic.write_bytes(b"NOT_MDMP" + b"\x00" * 128)
    ok2, issue2, err2 = windbg.validate_minidump_header(str(bad_magic))
    assert not ok2
    assert issue2 == "MCP-172"
    assert err2 is not None and "MDMP" in err2

    valid_dmp = tmp_path / "valid.dmp"
    valid_dmp.write_bytes(b"MDMP" + b"\x93\xa7\x00\x00" + b"\x00" * 128)
    ok3, issue3, err3 = windbg.validate_minidump_header(str(valid_dmp))
    assert ok3
    assert issue3 is None
    assert err3 is None

    # 3. MCP-129 / MCP-131: Offline symbol sanitization strips msdl URLs unless explicitly requested
    sanitized = windbg.sanitize_offline_symbols(
        None,
        "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols;D:\\LocalPdb",
    )
    assert "https://" not in sanitized
    assert "C:\\Symbols" in sanitized
    assert "D:\\LocalPdb" in sanitized

    explicit_online = windbg.sanitize_offline_symbols("SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols", None)
    assert "https://msdl.microsoft.com" in explicit_online

    # 4. Unified NDJSON outcome telemetry
    outcome_file = tmp_path / "outcomes.ndjson"
    windbg.record_mcp_outcome("open_cdb_dump", True, 4.25, "MCP-172", "0-byte dump", str(outcome_file))
    lines = outcome_file.read_text(encoding="utf-8").strip().splitlines()
    assert len(lines) == 1
    parsed = json.loads(lines[0])
    assert parsed["server"] == "windbg"
    assert parsed["tool"] == "open_cdb_dump"
    assert parsed["matchedIssueId"] == "MCP-172"


def test_texconv_argument_normalization_and_dds_preflight(tmp_path: Path) -> None:
    _ensure_stub_mcp_if_missing()
    texconv = _load_module("kongor_texconv_mcp_test", REPO_ROOT / "src" / "tools" / "texconv" / "mcp_server.py")

    # 1. MCP-127 / MCP-130 / MCP-167: dds_inspect_texture normalizes FilePath / input_path / path -> file_path
    norm_inspect = texconv.normalize_texconv_arguments(
        "dds_inspect_texture",
        {"FilePath": "C:/tex/color.dds"},
    )
    assert norm_inspect == {"file_path": "C:/tex/color.dds"}

    # 2. MCP-128 / MCP-154: dds_export_to_image normalizes file_path -> input_path and output_path -> output_dir
    out_png = tmp_path / "exports" / "preview.png"
    norm_export = texconv.normalize_texconv_arguments(
        "dds_export_to_image",
        {"file_path": "C:/tex/color.dds", "output_path": str(out_png), "Format": "png"},
    )
    assert norm_export["input_path"] == "C:/tex/color.dds"
    assert norm_export["output_dir"] == str(out_png.parent)
    assert norm_export["format"] == "png"

    # 3. DDS header pre-flight guard
    truncated_dds = tmp_path / "trunc.dds"
    truncated_dds.write_bytes(b"DDS \x00\x00")
    ok, _, issue, err = texconv.validate_texture_preflight(str(truncated_dds))
    assert not ok
    assert issue == "MCP-127"
    assert err is not None and "truncated" in err

    valid_dds = tmp_path / "valid.dds"
    valid_dds.write_bytes(b"DDS " + b"\x7c\x00\x00\x00" + b"\x00" * 160)
    ok2, abs_p, issue2, err2 = texconv.validate_texture_preflight(str(valid_dds))
    assert ok2
    assert abs_p == str(valid_dds)
    assert issue2 is None
    assert err2 is None
