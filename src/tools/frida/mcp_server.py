#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Together as One (TAO) - Frida MCP Server for ProjectKongor.
Bridges Frida dynamic instrumentation toolkit into the MCP ecosystem with:
- Automatic device_id="local" resolution on desktop Windows (MCP-082, MCP-094)
- USB device fallback to local device when no mobile target is attached
- Parameter aliasing & process-name-to-PID resolution
- Stdio JSON-RPC print pollution shielding & persistent rolling telemetry logs
"""

from __future__ import annotations

import builtins
import logging
from logging.handlers import RotatingFileHandler
import os
import sys
from typing import Any

# 1. Resolve Project & Log Paths + Shared _common/mcp_telemetry
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "..", ".."))
_TOOLS_DIR = os.path.dirname(SCRIPT_DIR)
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

from _common.mcp_telemetry import (
    instrument_fastmcp_server,
    record_mcp_outcome,
    shield_stdio_stdout,
)

shield_stdio_stdout()
LOG_DIR = os.path.join(REPO_ROOT, "logs")
os.makedirs(LOG_DIR, exist_ok=True)
LOG_FILE = os.path.join(LOG_DIR, "frida_mcp.log")

# 2. Setup Persistent Logging (Strictly Stderr & File Handler)
logger = logging.getLogger("frida_mcp")
logger.setLevel(logging.INFO)
formatter = logging.Formatter("%(asctime)s [%(levelname)s] [%(name)s] %(message)s")

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

# 3. Shield Stdio MCP Transport Against Print Pollution
_orig_builtin_print = builtins.print


def _safe_print(*args: Any, **kwargs: Any) -> Any:
    target_file = kwargs.get("file")
    if target_file is None or target_file is sys.stdout:
        kwargs["file"] = sys.stderr
    return _orig_builtin_print(*args, **kwargs)


builtins.print = _safe_print

# 4. Patch Frida USB Device Fallback to Local Desktop Device (MCP-082, MCP-094)
try:
    import frida

    _orig_get_usb_device = frida.get_usb_device
    _orig_get_device = frida.get_device

    def _resilient_get_usb_device(*args: Any, **kwargs: Any) -> Any:
        try:
            return _orig_get_usb_device(*args, **kwargs)
        except Exception as ex:
            logger.info(
                "No USB device connected (%s); self-healing fallback to frida.get_local_device() (MCP-082/MCP-094).",
                ex,
            )
            return frida.get_local_device()

    def _resilient_get_device(device_id: str | None = None, *args: Any, **kwargs: Any) -> Any:
        if not isinstance(device_id, str):
            device_id = getattr(device_id, "default", "local")
        norm = (device_id or "local").strip().lower() if isinstance(device_id, str) else "local"
        if not norm or norm in ("local", "default", "localhost", "host"):
            return frida.get_local_device()
        if norm == "usb":
            return _resilient_get_usb_device(*args, **kwargs)
        return _orig_get_device(device_id, *args, **kwargs)

    frida.get_usb_device = _resilient_get_usb_device
    frida.get_device = _resilient_get_device
    logger.info("Applied resilient local device fallback hooks to frida module.")
except Exception as e:
    logger.warning("Failed to patch frida device resolution: %s", e)

# 5. Normalize Tool Arguments & Resolve Process Names
DEVICE_SCOPED_TOOLS = {
    "enumerate_processes",
    "get_device",
    "get_process_by_name",
    "attach_to_process",
    "spawn_process",
    "resume_process",
    "kill_process",
    "create_interactive_session",
}


def _resolve_pid_from_name_or_id(target: Any, device_id: str = "local") -> int | Any:
    if isinstance(target, int):
        return target
    s = str(target).strip()
    if s.isdigit():
        return int(s)
    if s.lower().startswith("pid:") and s[4:].strip().isdigit():
        return int(s[4:].strip())
    try:
        dev = frida.get_device(device_id)
        for proc in dev.enumerate_processes():
            if s.lower() == proc.name.lower() or s.lower() in proc.name.lower():
                logger.info("Resolved process target '%s' -> PID %d (%s)", s, proc.pid, proc.name)
                return proc.pid
    except Exception as ex:
        logger.warning("Could not resolve process name '%s' to PID: %s", s, ex)
    return target


def _normalize_frida_arguments(name: str, arguments: dict | None) -> dict:
    if arguments is None or not isinstance(arguments, dict):
        arguments = {}
    else:
        arguments = dict(arguments)

    # Normalize PascalCase / camelCase keys to snake_case
    alias_keys = {
        "DeviceId": "device_id",
        "deviceId": "device_id",
        "ProcessId": "process_id",
        "processId": "process_id",
        "SessionId": "session_id",
        "sessionId": "session_id",
        "JavascriptCode": "javascript_code",
        "javascriptCode": "javascript_code",
        "KeepAlive": "keep_alive",
        "keepAlive": "keep_alive",
    }
    for k_src, k_dst in alias_keys.items():
        if k_src in arguments and k_dst not in arguments:
            arguments[k_dst] = arguments.pop(k_src)
        elif k_src in arguments:
            del arguments[k_src]

    # Default device_id to "local" when omitted or None (MCP-082, MCP-094)
    if name in DEVICE_SCOPED_TOOLS and not arguments.get("device_id"):
        arguments["device_id"] = "local"

    device_id = str(arguments.get("device_id") or "local")

    if name == "get_process_by_name":
        if "name" not in arguments:
            for k in ("process_name", "target", "query", "process"):
                if k in arguments:
                    arguments["name"] = arguments.pop(k)
                    break

    elif name in ("attach_to_process", "resume_process", "kill_process"):
        if "pid" not in arguments:
            for k in ("process_id", "target", "name", "process_name"):
                if k in arguments:
                    arguments["pid"] = arguments.pop(k)
                    break
        if "pid" in arguments:
            arguments["pid"] = _resolve_pid_from_name_or_id(arguments["pid"], device_id)

    elif name == "create_interactive_session":
        if "process_id" not in arguments:
            for k in ("pid", "target", "name", "process_name"):
                if k in arguments:
                    arguments["process_id"] = arguments.pop(k)
                    break
        if "process_id" in arguments:
            arguments["process_id"] = _resolve_pid_from_name_or_id(arguments["process_id"], device_id)

    elif name == "execute_in_session":
        if "javascript_code" not in arguments:
            for k in ("code", "script", "js", "js_code"):
                if k in arguments:
                    arguments["javascript_code"] = arguments.pop(k)
                    break
        if "session_id" not in arguments:
            for k in ("session", "id"):
                if k in arguments:
                    arguments["session_id"] = arguments.pop(k)
                    break

    elif name == "get_session_messages":
        if "session_id" not in arguments:
            for k in ("session", "id"):
                if k in arguments:
                    arguments["session_id"] = arguments.pop(k)
                    break

    return arguments


try:
    from frida_mcp import cli as frida_cli

    # Hook MCPServer / FastMCP call_tool and _tool_manager.call_tool + shared telemetry
    mcp_instance = frida_cli.mcp
    instrument_fastmcp_server(mcp_instance, "frida")
    if hasattr(mcp_instance, "call_tool"):
        _orig_call_tool = mcp_instance.call_tool

        async def _wrapped_call_tool(name: str, arguments: dict | None = None, *args: Any, **kwargs: Any) -> Any:
            import time as _time
            _t0 = _time.perf_counter()
            norm_args = _normalize_frida_arguments(name, arguments)
            logger.info("Frida MCP tool call: %s(%s)", name, list(norm_args.keys()))
            try:
                res = await _orig_call_tool(name, norm_args, *args, **kwargs)
                record_mcp_outcome("frida", name, False, (_time.perf_counter() - _t0) * 1000.0)
                return res
            except Exception as _exc:
                record_mcp_outcome("frida", name, True, (_time.perf_counter() - _t0) * 1000.0, error_message=str(_exc))
                raise

        mcp_instance.call_tool = _wrapped_call_tool

    if hasattr(mcp_instance, "_tool_manager") and hasattr(mcp_instance._tool_manager, "call_tool"):
        _orig_tm_call_tool = mcp_instance._tool_manager.call_tool

        async def _wrapped_tm_call_tool(name: str, arguments: dict | None = None, *args: Any, **kwargs: Any) -> Any:
            norm_args = _normalize_frida_arguments(name, arguments)
            return await _orig_tm_call_tool(name, norm_args, *args, **kwargs)

        mcp_instance._tool_manager.call_tool = _wrapped_tm_call_tool

    logger.info("Applied argument normalization & local device default hooks to Frida MCP server.")
except Exception as e:
    logger.warning("Failed to hook frida_mcp.cli tool manager: %s", e)

from frida_mcp.cli import main

if __name__ == "__main__":
    logger.info("Starting Frida MCP server for ProjectKongor.")
    main()

