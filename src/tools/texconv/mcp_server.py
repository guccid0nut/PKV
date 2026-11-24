#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
DirectX DDS Texture Pipeline MCP Server for ProjectKongor.
Bridges MCP tools to Microsoft DirectXTex texconv.exe and texdiag.exe
for DDS compression, inspection, mipmap generation, and format conversion.

Hardened with:
- PascalCase / camelCase / file_path <-> input_path / output_path <-> output_dir normalization (MCP-127, MCP-128, MCP-130, MCP-154, MCP-167)
- Pre-flight non-zero file & DDS magic validation with actionable remediation messages
- Unified NDJSON outcome telemetry logging
"""

import asyncio
from datetime import datetime, timezone
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time
from typing import Any, Dict, List, Optional, Tuple

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent.parent
_TOOLS_DIR = str(SCRIPT_DIR.parent)
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

from _common.mcp_telemetry import (
    create_fastmcp_server,
    record_mcp_outcome as _shared_record_mcp_outcome,
    resolve_tool_binary,
    run_fastmcp_stdio,
    shield_stdio_stdout,
)

shield_stdio_stdout()

LOG_DIR = REPO_ROOT / "logs"
DEFAULT_OUTCOME_LOG = str(LOG_DIR / "mcp_python_outcomes.ndjson")

def _resolve_directxtex_tool(exe_name: str, env_var: str) -> str:
    return resolve_tool_binary(
        exe_name,
        env_var=env_var,
        subdir_hints=("texconv",),
        extra_candidates=(
            REPO_ROOT / "artifacts" / "native-binaries" / "windows-x64" / exe_name,
            REPO_ROOT / ".tools" / "bin" / "texconv" / exe_name,
            SCRIPT_DIR / exe_name,
        ),
    )


DEFAULT_TEXCONV = REPO_ROOT / ".tools" / "bin" / "texconv" / "texconv.exe"
DEFAULT_TEXDIAG = REPO_ROOT / ".tools" / "bin" / "texconv" / "texdiag.exe"

TEXCONV_EXE = _resolve_directxtex_tool("texconv.exe", "TEXCONV_PATH")
TEXDIAG_EXE = _resolve_directxtex_tool("texdiag.exe", "TEXDIAG_PATH")

server = create_fastmcp_server("texconv-pipeline")


def record_mcp_outcome(
    tool_name: str,
    is_error: bool,
    duration_ms: float,
    issue_id: Optional[str] = None,
    error_message: Optional[str] = None,
    outcome_log_path: Optional[str] = None,
) -> None:
    """Append unified NDJSON telemetry entry for Texconv MCP invocations via shared _common/mcp_telemetry."""
    _shared_record_mcp_outcome(
        server_name="texconv",
        tool_name=tool_name,
        is_error=is_error,
        duration_ms=duration_ms,
        issue_id=issue_id,
        error_message=error_message,
        outcome_log_path=outcome_log_path,
    )


def normalize_texconv_arguments(tool_name: str, arguments: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    """
    Normalize PascalCase, camelCase, and cross-tool path aliases across texconv MCP tools
    (MCP-127, MCP-128, MCP-130, MCP-154, MCP-167).
    """
    if not arguments or not isinstance(arguments, dict):
        return {}
    norm: Dict[str, Any] = dict(arguments)

    target_input_key = "file_path" if tool_name == "dds_inspect_texture" else "input_path"
    input_aliases = (
        "file_path",
        "FilePath",
        "filePath",
        "input_path",
        "InputPath",
        "inputPath",
        "path",
        "Path",
        "texture_path",
        "texturePath",
    )
    for alias in input_aliases:
        if alias in norm and target_input_key not in norm:
            norm[target_input_key] = norm.pop(alias)
        elif alias in norm and alias != target_input_key:
            del norm[alias]

    output_aliases = ("OutputDir", "outputDir", "output_directory", "outputDirectory", "out_dir")
    for alias in output_aliases:
        if alias in norm and "output_dir" not in norm:
            norm["output_dir"] = norm.pop(alias)
        elif alias in norm and alias != "output_dir":
            del norm[alias]

    # MCP-154: If caller passed 'output_path' / 'OutputPath' pointing to a file or directory, derive output_dir
    for out_file_alias in ("output_path", "OutputPath", "outputPath"):
        if out_file_alias in norm:
            raw_out = norm.pop(out_file_alias)
            if isinstance(raw_out, str) and raw_out.strip() and "output_dir" not in norm:
                p = pathlib.Path(raw_out)
                norm["output_dir"] = str(p.parent) if p.suffix else str(p)

    scalar_aliases = {
        "Format": "format",
        "GenerateMips": "generate_mips",
        "generateMips": "generate_mips",
        "Srgb": "srgb",
        "Overwrite": "overwrite",
        "Dx9Compat": "dx9_compat",
        "dx9Compat": "dx9_compat",
        "IsHeightmap": "is_heightmap",
        "isHeightmap": "is_heightmap",
        "InvertGreen": "invert_green",
        "invertGreen": "invert_green",
        "ReconstructZ": "reconstruct_z",
        "reconstructZ": "reconstruct_z",
    }
    for src, dst in scalar_aliases.items():
        if src in norm and dst not in norm:
            norm[dst] = norm.pop(src)
        elif src in norm and src != dst:
            del norm[src]

    return norm


def validate_texture_preflight(raw_path: Optional[str], param_name: str = "file_path") -> Tuple[bool, str, Optional[str], Optional[str]]:
    """
    Validate texture path existence, non-zero byte length, and DDS magic header (when .dds).
    Returns (is_valid, abs_path, issue_id, actionable_error).
    """
    if not raw_path or not isinstance(raw_path, str) or not raw_path.strip():
        return (
            False,
            "",
            "MCP-127",
            f"Missing required parameter '{param_name}'. Pass {{'{param_name}': '<path-to-texture>'}}.",
        )

    abs_path = os.path.abspath(raw_path.strip())
    if not os.path.isfile(abs_path):
        return (
            False,
            abs_path,
            "MCP-127",
            f"Texture file not found: '{abs_path}'. Locate physical VFS files first via kongor_vfs_locate.",
        )

    size = os.path.getsize(abs_path)
    if size == 0:
        return (
            False,
            abs_path,
            "MCP-127",
            f"Pre-Flight Guard: Texture file '{abs_path}' is 0 bytes.",
        )

    if abs_path.lower().endswith(".dds"):
        if size < 128:
            return (
                False,
                abs_path,
                "MCP-127",
                f"Pre-Flight Guard: DDS file '{abs_path}' is truncated ({size} bytes < 128-byte DDS header).",
            )
        with open(abs_path, "rb") as fh:
            magic = fh.read(4)
        if magic != b"DDS ":
            return (
                False,
                abs_path,
                "MCP-127",
                f"Pre-Flight Guard: File '{abs_path}' has .dds extension but invalid magic header {magic!r} (expected b'DDS ').",
            )

    return True, abs_path, None, None


def _resolve_input_arg(*candidates: Optional[str]) -> Optional[str]:
    for c in candidates:
        if isinstance(c, str) and c.strip():
            return c.strip()
    return None


def _resolve_output_dir(output_dir: Optional[str], output_path: Optional[str], abs_in: str) -> str:
    if isinstance(output_dir, str) and output_dir.strip():
        return os.path.abspath(output_dir.strip())
    if isinstance(output_path, str) and output_path.strip():
        p = pathlib.Path(output_path.strip())
        return os.path.abspath(str(p.parent) if p.suffix else str(p))
    return os.path.dirname(abs_in)


def _run_cmd(cmd: List[str], timeout: float = 60.0) -> subprocess.CompletedProcess[str]:
    """Run external command synchronously with timeout."""
    return subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )


def _parse_texdiag_info(stdout: str) -> Dict[str, Any]:
    """Parse key = value pairs from texdiag info stdout."""
    res: Dict[str, Any] = {}
    for line in stdout.splitlines():
        line = line.strip()
        if "=" in line:
            parts = line.split("=", 1)
            k = parts[0].strip()
            v = parts[1].strip()
            key_clean = k.lower().replace(" ", "_")
            if v.isdigit():
                res[key_clean] = int(v)
            else:
                res[key_clean] = v
    return res


@server.tool()
async def dds_inspect_texture(
    file_path: Optional[str] = None,
    input_path: Optional[str] = None,
    path: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Inspect a DDS texture or image file metadata using texdiag info.
    Accepts 'file_path' (canonical) as well as 'input_path' or 'path' aliases.
    Returns dimensions (width/height/depth), format, mipLevels, dimension (2D/3D/Cube), and alpha mode.
    """
    t0 = time.perf_counter()
    chosen = _resolve_input_arg(file_path, input_path, path)
    ok, abs_path, issue_id, err = validate_texture_preflight(chosen, "file_path")
    if not ok:
        record_mcp_outcome("dds_inspect_texture", True, (time.perf_counter() - t0) * 1000.0, issue_id, err)
        return {"error": err}

    cmd = [TEXDIAG_EXE, "info", "-nologo", abs_path]
    proc = await asyncio.to_thread(_run_cmd, cmd, 30.0)
    if proc.returncode != 0:
        msg = f"texdiag info failed: {proc.stderr.strip()}"
        record_mcp_outcome("dds_inspect_texture", True, (time.perf_counter() - t0) * 1000.0, None, msg)
        return {"error": msg, "raw": proc.stdout}

    parsed = _parse_texdiag_info(proc.stdout)
    parsed["file"] = abs_path
    parsed["file_size_bytes"] = os.path.getsize(abs_path)
    record_mcp_outcome("dds_inspect_texture", False, (time.perf_counter() - t0) * 1000.0)
    return parsed


@server.tool()
async def dds_convert_texture(
    input_path: Optional[str] = None,
    file_path: Optional[str] = None,
    path: Optional[str] = None,
    output_dir: Optional[str] = None,
    output_path: Optional[str] = None,
    format: str = "BC3_UNORM",
    generate_mips: bool = True,
    srgb: bool = False,
    overwrite: bool = True,
    dx9_compat: bool = False,
) -> Dict[str, Any]:
    """
    Convert an image (PNG, TGA, JPG, DDS) to target DDS format (e.g. BC1_UNORM, BC3_UNORM, BC5_UNORM, BC7_UNORM).
    Supports mipmap generation and DX9 legacy header compatibility.
    """
    t0 = time.perf_counter()
    chosen = _resolve_input_arg(input_path, file_path, path)
    ok, abs_in, issue_id, err = validate_texture_preflight(chosen, "input_path")
    if not ok:
        record_mcp_outcome("dds_convert_texture", True, (time.perf_counter() - t0) * 1000.0, issue_id, err)
        return {"error": err}

    out_dir = _resolve_output_dir(output_dir, output_path, abs_in)
    os.makedirs(out_dir, exist_ok=True)

    cmd = [TEXCONV_EXE, "-nologo", "-f", format, "-ft", "dds", "-o", out_dir]
    if not generate_mips:
        cmd.extend(["-m", "1"])
    if srgb:
        cmd.append("-srgb")
    if overwrite:
        cmd.append("-y")
    if dx9_compat:
        cmd.append("-dx9")
    cmd.append(abs_in)

    proc = await asyncio.to_thread(_run_cmd, cmd, 60.0)
    if proc.returncode != 0:
        msg = f"texconv convert failed: {proc.stderr.strip()}"
        record_mcp_outcome("dds_convert_texture", True, (time.perf_counter() - t0) * 1000.0, None, msg)
        return {"error": msg, "stdout": proc.stdout}

    base_name = pathlib.Path(abs_in).stem
    expected_out = os.path.join(out_dir, f"{base_name}.dds")

    info: Dict[str, Any] = {}
    if os.path.isfile(expected_out):
        diag_cmd = [TEXDIAG_EXE, "info", "-nologo", expected_out]
        diag_proc = await asyncio.to_thread(_run_cmd, diag_cmd, 15.0)
        if diag_proc.returncode == 0:
            info = _parse_texdiag_info(diag_proc.stdout)

    record_mcp_outcome("dds_convert_texture", False, (time.perf_counter() - t0) * 1000.0)
    return {
        "success": os.path.isfile(expected_out),
        "output_file": expected_out,
        "size_bytes": os.path.getsize(expected_out) if os.path.isfile(expected_out) else 0,
        "metadata": info,
    }


@server.tool()
async def dds_bake_normal_map(
    input_path: Optional[str] = None,
    file_path: Optional[str] = None,
    path: Optional[str] = None,
    output_dir: Optional[str] = None,
    output_path: Optional[str] = None,
    format: str = "BC5_UNORM",
    is_heightmap: bool = False,
    invert_green: bool = False,
    reconstruct_z: bool = False,
    overwrite: bool = True,
) -> Dict[str, Any]:
    """
    Bake or compress generic 2-channel normal maps (default BC5_UNORM / ATI2N).
    NOTE: No BC5/ATI2/DX10 FourCC handling found in k2_x64.dll or vid_d3d9_x64.dll;
    use dds_bake_rxgb_normal_map (DXT5 .agb) for K2 engine normal maps.
    """
    t0 = time.perf_counter()
    chosen = _resolve_input_arg(input_path, file_path, path)
    ok, abs_in, issue_id, err = validate_texture_preflight(chosen, "input_path")
    if not ok:
        record_mcp_outcome("dds_bake_normal_map", True, (time.perf_counter() - t0) * 1000.0, issue_id, err)
        return {"error": err}

    out_dir = _resolve_output_dir(output_dir, output_path, abs_in)
    os.makedirs(out_dir, exist_ok=True)

    cmd = [TEXCONV_EXE, "-nologo", "-f", format, "-ft", "dds", "-o", out_dir]
    if is_heightmap:
        cmd.append("-nmap")
    if invert_green:
        cmd.append("--invert-y")
    if reconstruct_z:
        cmd.append("--reconstruct-z")
    if overwrite:
        cmd.append("-y")
    cmd.append(abs_in)

    proc = await asyncio.to_thread(_run_cmd, cmd, 60.0)
    if proc.returncode != 0:
        msg = f"texconv normal map bake failed: {proc.stderr.strip()}"
        record_mcp_outcome("dds_bake_normal_map", True, (time.perf_counter() - t0) * 1000.0, None, msg)
        return {"error": msg, "stdout": proc.stdout}

    base_name = pathlib.Path(abs_in).stem
    expected_out = os.path.join(out_dir, f"{base_name}.dds")
    record_mcp_outcome("dds_bake_normal_map", False, (time.perf_counter() - t0) * 1000.0)
    return {
        "success": os.path.isfile(expected_out),
        "output_file": expected_out,
        "format": format,
        "size_bytes": os.path.getsize(expected_out) if os.path.isfile(expected_out) else 0,
    }


@server.tool()
async def dds_bake_rxgb_normal_map(
    input_path: Optional[str] = None,
    file_path: Optional[str] = None,
    path: Optional[str] = None,
    output_dir: Optional[str] = None,
    output_path: Optional[str] = None,
    invert_green: bool = False,
    overwrite: bool = True,
) -> Dict[str, Any]:
    """
    Bake or convert a tangent-space normal map into native K2 Direct3D 9 RxGB DXT5 DDS format (.agb).
    Channel swizzle (--swizzle 1gbr): Red = 255, Green = Ny, Blue = Nz, Alpha = Nx.
    """
    t0 = time.perf_counter()
    chosen = _resolve_input_arg(input_path, file_path, path)
    ok, abs_in, issue_id, err = validate_texture_preflight(chosen, "input_path")
    if not ok:
        record_mcp_outcome("dds_bake_rxgb_normal_map", True, (time.perf_counter() - t0) * 1000.0, issue_id, err)
        return {"error": err}

    out_dir = _resolve_output_dir(output_dir, output_path, abs_in)
    os.makedirs(out_dir, exist_ok=True)

    cmd = [TEXCONV_EXE, "-nologo", "-f", "DXT5", "-ft", "dds", "--swizzle", "1gbr", "-o", out_dir]
    if invert_green:
        cmd.append("--invert-y")
    if overwrite:
        cmd.append("-y")
    cmd.append(abs_in)

    proc = await asyncio.to_thread(_run_cmd, cmd, 60.0)
    if proc.returncode != 0:
        msg = f"texconv RxGB normal map bake failed: {proc.stderr.strip()}"
        record_mcp_outcome("dds_bake_rxgb_normal_map", True, (time.perf_counter() - t0) * 1000.0, None, msg)
        return {"error": msg, "stdout": proc.stdout}

    base_name = pathlib.Path(abs_in).stem
    expected_out = os.path.join(out_dir, f"{base_name}.dds")

    info: Dict[str, Any] = {}
    if os.path.isfile(expected_out):
        diag_cmd = [TEXDIAG_EXE, "info", "-nologo", expected_out]
        diag_proc = await asyncio.to_thread(_run_cmd, diag_cmd, 15.0)
        if diag_proc.returncode == 0:
            info = _parse_texdiag_info(diag_proc.stdout)

    record_mcp_outcome("dds_bake_rxgb_normal_map", False, (time.perf_counter() - t0) * 1000.0)
    return {
        "success": os.path.isfile(expected_out),
        "output_file": expected_out,
        "format": "DXT5_RxGB",
        "invert_green": invert_green,
        "size_bytes": os.path.getsize(expected_out) if os.path.isfile(expected_out) else 0,
        "metadata": info,
    }


@server.tool()
async def dds_export_to_image(
    input_path: Optional[str] = None,
    file_path: Optional[str] = None,
    path: Optional[str] = None,
    output_dir: Optional[str] = None,
    output_path: Optional[str] = None,
    format: str = "png",
    overwrite: bool = True,
) -> Dict[str, Any]:
    """
    Export a DDS texture to standard visual image formats (png, tga, jpg, bmp) for visual diffing or web rendering.
    Accepts 'input_path' (canonical) or 'file_path'/'path', and 'output_dir' or 'output_path'.
    """
    t0 = time.perf_counter()
    chosen = _resolve_input_arg(input_path, file_path, path)
    ok, abs_in, issue_id, err = validate_texture_preflight(chosen, "input_path")
    if not ok:
        record_mcp_outcome("dds_export_to_image", True, (time.perf_counter() - t0) * 1000.0, issue_id, err)
        return {"error": err}

    out_dir = _resolve_output_dir(output_dir, output_path, abs_in)
    os.makedirs(out_dir, exist_ok=True)

    cmd = [TEXCONV_EXE, "-nologo", "-ft", format.lower(), "-o", out_dir]
    if overwrite:
        cmd.append("-y")
    cmd.append(abs_in)

    proc = await asyncio.to_thread(_run_cmd, cmd, 60.0)
    if proc.returncode != 0:
        msg = f"texconv export failed: {proc.stderr.strip()}"
        record_mcp_outcome("dds_export_to_image", True, (time.perf_counter() - t0) * 1000.0, None, msg)
        return {"error": msg, "stdout": proc.stdout}

    base_name = pathlib.Path(abs_in).stem
    expected_out = os.path.join(out_dir, f"{base_name}.{format.lower()}")
    record_mcp_outcome("dds_export_to_image", False, (time.perf_counter() - t0) * 1000.0)
    return {
        "success": os.path.isfile(expected_out),
        "output_file": expected_out,
        "size_bytes": os.path.getsize(expected_out) if os.path.isfile(expected_out) else 0,
    }


def _install_fastmcp_arg_normalizer() -> None:
    """Wrap underlying MCPServer call_tool handler if exposed so PascalCase keys normalize prior to validation."""
    low_level = getattr(server, "_mcp_server", None)
    if low_level is None:
        return
    req_handlers = getattr(low_level, "request_handlers", None)
    if not isinstance(req_handlers, dict):
        return
    for req_type, orig_handler in list(req_handlers.items()):
        type_name = getattr(req_type, "__name__", "")
        if type_name == "CallToolRequest" and not getattr(orig_handler, "_kongor_texconv_norm", False):

            async def _wrapped_call_tool(req: Any, _orig: Any = orig_handler) -> Any:
                params = getattr(req, "params", None)
                if params is not None and isinstance(getattr(params, "arguments", None), dict):
                    params.arguments = normalize_texconv_arguments(getattr(params, "name", ""), params.arguments)
                return await _orig(req)

            _wrapped_call_tool._kongor_texconv_norm = True  # type: ignore[attr-defined]
            req_handlers[req_type] = _wrapped_call_tool


def main() -> None:
    _install_fastmcp_arg_normalizer()
    run_fastmcp_stdio(server, "texconv")


if __name__ == "__main__":
    main()
