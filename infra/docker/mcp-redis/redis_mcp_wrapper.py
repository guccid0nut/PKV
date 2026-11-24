#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Together as One (TAO) - Redis MCP Server Wrapper for ProjectKongor.
Intercepts Redis MCP calls to provide:
1. Transparent argument normalization ('key' <-> 'name', 'field' <-> 'key').
2. Resilient binary data handling (MemoryPack/MessagePack bytes decoded as hex/json instead of throwing UnicodeDecodeError).
3. Universal error trapping across all Redis commands.
"""

from __future__ import annotations

import base64
import json
import logging
import os
from typing import Any, Sequence

# Set default host and port to local Garnet instance (28298) if not explicitly configured
os.environ.setdefault("REDIS_HOST", "127.0.0.1")
os.environ.setdefault("REDIS_PORT", "28298")

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.tools import ToolManager
from redis.exceptions import RedisError

logger = logging.getLogger("redis_mcp_wrapper")

# 1. Store original tool manager call_tool
_orig_tool_manager_call_tool = ToolManager.call_tool


def _safe_decode_bytes(val: bytes) -> Any:
    """Attempt UTF-8 decode; if invalid bytes, return structured binary preview."""
    try:
        decoded_text = val.decode("utf-8")
        # If it looks like JSON, attempt parse
        if (decoded_text.startswith("{") and decoded_text.endswith("}")) or (
            decoded_text.startswith("[") and decoded_text.endswith("]")
        ):
            try:
                return json.loads(decoded_text)
            except Exception:
                pass
        return decoded_text
    except UnicodeDecodeError:
        return {
            "__type": "binary",
            "length": len(val),
            "hex": val.hex(),
            "preview": f"<binary: {len(val)} bytes, hex: {val[:16].hex()}...>",
            "base64": base64.b64encode(val).decode("ascii"),
        }


async def _hooked_call_tool(
    self,
    name: str,
    arguments: dict[str, Any],
    context: Any = None,
    convert_result: bool = False,
) -> Any:
    if arguments and isinstance(arguments, dict):
        # 1. Parameter Normalization across tools
        # Hash tools: hgetall, hget, hset, hdel, hexists (schema expects 'name' for hash key)
        if name == "hgetall":
            if "name" not in arguments and "key" in arguments:
                arguments["name"] = arguments.pop("key")
        elif name in ("hget", "hdel", "hexists"):
            if "name" not in arguments and "key" in arguments and "field" in arguments:
                arguments["name"] = arguments.pop("key")
                arguments["key"] = arguments.pop("field")
            elif "field" in arguments and "key" not in arguments:
                arguments["key"] = arguments.pop("field")
        elif name == "hset":
            if "name" not in arguments and "key" in arguments and "field" in arguments:
                arguments["name"] = arguments.pop("key")
                arguments["key"] = arguments.pop("field")
            elif "field" in arguments and "key" not in arguments:
                arguments["key"] = arguments.pop("field")
            elif "name" not in arguments and "key" in arguments:
                arguments["name"] = arguments.pop("key")

            # Universal value serialization for hset (MCP-026)
            if "value" in arguments and not isinstance(arguments["value"], str):
                try:
                    arguments["value"] = json.dumps(arguments["value"])
                except Exception:
                    arguments["value"] = str(arguments["value"])

            # Clean and serialize dict mapping if provided
            if "mapping" in arguments and isinstance(arguments["mapping"], dict):
                clean_mapping = {}
                for mk, mv in arguments["mapping"].items():
                    k_str = str(mk)
                    if isinstance(mv, (dict, list)):
                        clean_mapping[k_str] = json.dumps(mv)
                    else:
                        clean_mapping[k_str] = str(mv)
                arguments["mapping"] = clean_mapping

        # Key tools: get, delete, type (schema expects 'key')
        elif name in ("get", "delete", "type"):
            if "key" not in arguments and "name" in arguments:
                arguments["key"] = arguments.pop("name")

        # Key tools: set, hsetnx (schema expects 'key' and string 'value')
        elif name in ("set", "hsetnx"):
            if "key" not in arguments and "name" in arguments:
                arguments["key"] = arguments.pop("name")
            if "value" in arguments and not isinstance(arguments["value"], str):
                try:
                    arguments["value"] = json.dumps(arguments["value"])
                except Exception:
                    arguments["value"] = str(arguments["value"])

        # Key tools: expire (schema expects 'name')
        elif name == "expire":
            if "name" not in arguments and "key" in arguments:
                arguments["name"] = arguments.pop("key")

    def _format_res(raw_result: Any) -> Any:
        tool = self.get_tool(name)
        if convert_result and tool is not None:
            return tool.fn_metadata.convert_result(raw_result)
        return raw_result

    # 2. Binary-Safe Interception for Hash and String operations
    if name == "hgetall":
        try:
            from src.common.connection import RedisConnectionManager

            r = RedisConnectionManager.get_connection(decode_responses=False)
            target_name = arguments.get("name")
            if not target_name:
                return _format_res("Hash name must be provided.")

            hash_data = r.hgetall(target_name)
            if not hash_data:
                return _format_res(f"Hash '{target_name}' is empty or does not exist.")

            res = {}
            for k, v in hash_data.items():
                k_str = k.decode("utf-8", errors="replace") if isinstance(k, bytes) else str(k)
                if isinstance(v, bytes):
                    res[k_str] = _safe_decode_bytes(v)
                else:
                    res[k_str] = v
            return _format_res(res)
        except Exception as ex:
            return _format_res(f"Error getting all fields from hash '{arguments.get('name')}': {str(ex)}")

    elif name == "hget":
        try:
            from src.common.connection import RedisConnectionManager

            r = RedisConnectionManager.get_connection(decode_responses=False)
            target_name = arguments.get("name")
            target_key = arguments.get("key")
            if not target_name or not target_key:
                return _format_res("Hash name and field key must be provided.")

            value = r.hget(target_name, target_key)
            if value is None:
                return _format_res(f"Field '{target_key}' not found in hash '{target_name}'.")

            if isinstance(value, bytes):
                return _format_res(_safe_decode_bytes(value))
            return _format_res(value)
        except Exception as ex:
            return _format_res(f"Error getting field '{arguments.get('key')}' from hash '{arguments.get('name')}': {str(ex)}")

    elif name == "get":
        try:
            from src.common.connection import RedisConnectionManager

            r = RedisConnectionManager.get_connection(decode_responses=False)
            target_key = arguments.get("key")
            if not target_key:
                return _format_res("Key must be provided.")

            value = r.get(target_key)
            if value is None:
                return _format_res(f"Key {target_key} does not exist")

            if isinstance(value, bytes):
                try:
                    return _format_res(value.decode("utf-8"))
                except UnicodeDecodeError:
                    return _format_res(value)
            return _format_res(str(value))
        except Exception as ex:
            return _format_res(f"Error retrieving key {arguments.get('key')}: {str(ex)}")

    elif name == "hset":
        try:
            from src.common.connection import RedisConnectionManager

            r = RedisConnectionManager.get_connection(decode_responses=False)
            target_name = arguments.get("name")
            target_key = arguments.get("key")
            target_val = arguments.get("value")
            target_mapping = arguments.get("mapping")

            if not target_name:
                return _format_res("Hash name must be provided.")

            if target_mapping and isinstance(target_mapping, dict):
                res = r.hset(target_name, mapping=target_mapping)
                return _format_res(f"Successfully set mapping in hash '{target_name}' (result: {res})")

            if not target_key:
                return _format_res("Field key must be provided.")

            if isinstance(target_val, (dict, list)):
                target_val = json.dumps(target_val)
            elif not isinstance(target_val, (str, bytes, int, float)):
                target_val = str(target_val) if target_val is not None else ""

            res = r.hset(target_name, target_key, target_val)
            return _format_res(f"Successfully set field '{target_key}' in hash '{target_name}' (result: {res})")
        except Exception as ex:
            return _format_res(f"Error setting field '{arguments.get('key')}' in hash '{arguments.get('name')}': {str(ex)}")

    elif name == "set":
        try:
            from src.common.connection import RedisConnectionManager

            r = RedisConnectionManager.get_connection(decode_responses=False)
            target_key = arguments.get("key")
            target_val = arguments.get("value")
            if not target_key:
                return _format_res("Key must be provided.")
            if isinstance(target_val, (dict, list)):
                target_val = json.dumps(target_val)
            elif not isinstance(target_val, (str, bytes, int, float)):
                target_val = str(target_val) if target_val is not None else ""

            res = r.set(target_key, target_val)
            return _format_res(f"Successfully set key '{target_key}' (result: {res})")
        except Exception as ex:
            return _format_res(f"Error setting key '{arguments.get('key')}': {str(ex)}")

    # 3. Cascade to standard ToolManager execution for all other tools
    try:
        return await _orig_tool_manager_call_tool(
            self, name, arguments, context=context, convert_result=convert_result
        )
    except UnicodeDecodeError as ex:
        logger.warning("Caught UnicodeDecodeError in Redis tool '%s': %s", name, ex)
        return _format_res({
            "error": "UnicodeDecodeError",
            "message": f"Value contains non-UTF8 binary data: {str(ex)}",
            "tool": name,
        })
    except RedisError as ex:
        logger.warning("Caught RedisError in Redis tool '%s': %s", name, ex)
        return _format_res({
            "error": "RedisError",
            "message": str(ex),
            "tool": name,
        })


# Apply patch
ToolManager.call_tool = _hooked_call_tool

# Start original cli
try:
    from src.main_orig import cli
except ImportError:
    from src.main import cli

# Ensure Click options default to environment variables if present
if hasattr(cli, "params"):
    for param in cli.params:
        if param.name == "host" and os.getenv("REDIS_HOST"):
            param.default = os.getenv("REDIS_HOST")
        elif param.name == "port" and os.getenv("REDIS_PORT"):
            try:
                param.default = int(os.getenv("REDIS_PORT"))
            except ValueError:
                pass

if __name__ == "__main__":
    cli()

