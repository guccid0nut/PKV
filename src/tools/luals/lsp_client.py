# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
LSP Client for lua-language-server subprocess communication over framed JSON-RPC 2.0.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import pathlib
import sys
import urllib.parse
from typing import Any, Dict, List, Optional, Set

logger = logging.getLogger("luals_lsp")

SYMBOL_KINDS: Dict[int, str] = {
    1: "File",
    2: "Module",
    3: "Namespace",
    4: "Package",
    5: "Class",
    6: "Method",
    7: "Property",
    8: "Field",
    9: "Constructor",
    10: "Enum",
    11: "Interface",
    12: "Function",
    13: "Variable",
    14: "Constant",
    15: "String",
    16: "Number",
    17: "Boolean",
    18: "Array",
    19: "Object",
    20: "Key",
    21: "Null",
    22: "EnumMember",
    23: "Struct",
    24: "Event",
    25: "Operator",
    26: "TypeParameter",
}

DIAGNOSTIC_SEVERITY: Dict[int, str] = {
    1: "Error",
    2: "Warning",
    3: "Information",
    4: "Hint",
}

def path_to_uri(path: str) -> str:
    """Convert a filesystem path to a file:// URI."""
    return pathlib.Path(path).resolve().as_uri()

def uri_to_path(uri: str) -> str:
    """Convert a file:// URI to a local filesystem path (Windows-safe)."""
    parsed = urllib.parse.urlparse(uri)
    path = urllib.parse.unquote(parsed.path)
    if sys.platform == "win32" and path.startswith("/") and len(path) > 2 and path[2] == ":":
        path = path[1:]
    return os.path.normpath(path)

class LSPClient:
    """Async LSP client managing a lua-language-server background subprocess."""

    def __init__(self) -> None:
        self._process: Optional[asyncio.subprocess.Process] = None
        self._next_id: int = 0
        self._pending: Dict[int, asyncio.Future[Any]] = {}
        self._open_files: Set[str] = set()
        self._diagnostics: Dict[str, List[Dict[str, Any]]] = {}
        self._diag_events: Dict[str, asyncio.Event] = {}
        self._reader_task: Optional[asyncio.Task[None]] = None
        self._stderr_task: Optional[asyncio.Task[None]] = None

    async def start(self, cmd: List[str]) -> None:
        """Start lua-language-server subprocess."""
        logger.info("Starting LuaLS: %s", " ".join(cmd))
        self._process = await asyncio.create_subprocess_exec(
            *cmd,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        self._reader_task = asyncio.create_task(self._read_stdout(), name="luals-reader")
        self._stderr_task = asyncio.create_task(self._log_stderr(), name="luals-stderr")

    async def shutdown(self) -> None:
        """Gracefully shut down LuaLS."""
        if not self._process:
            return
        try:
            await self.request("shutdown")
            await self.notify("exit")
        except Exception as ex:
            logger.warning("Error during LuaLS shutdown: %s", ex)

        if self._reader_task:
            self._reader_task.cancel()
        if self._stderr_task:
            self._stderr_task.cancel()

        if self._process:
            try:
                self._process.terminate()
                await self._process.wait()
            except Exception:
                pass
            self._process = None

    async def _read_stdout(self) -> None:
        """Read framed JSON-RPC messages from stdout."""
        assert self._process and self._process.stdout
        buf = bytearray()
        while True:
            try:
                chunk = await self._process.stdout.read(4096)
                if not chunk:
                    break
                buf.extend(chunk)

                while True:
                    hdr_end = buf.find(b"\r\n\r\n")
                    if hdr_end == -1:
                        break

                    header_text = buf[:hdr_end].decode("ascii", errors="replace")
                    content_length = None
                    for line in header_text.split("\r\n"):
                        if line.lower().startswith("content-length:"):
                            content_length = int(line.split(":", 1)[1].strip())

                    if content_length is None:
                        buf = buf[hdr_end + 4:]
                        continue

                    body_start = hdr_end + 4
                    if len(buf) < body_start + content_length:
                        break

                    body = buf[body_start:body_start + content_length]
                    buf = buf[body_start + content_length:]

                    try:
                        msg = json.loads(body.decode("utf-8"))
                        self._handle_message(msg)
                    except Exception as ex:
                        logger.error("Failed to parse LuaLS JSON: %s", ex)
            except asyncio.CancelledError:
                break
            except Exception as ex:
                logger.error("Error reading LuaLS stdout: %s", ex)
                break

    def _handle_message(self, msg: Dict[str, Any]) -> None:
        """Route message to waiting future or handle notifications."""
        msg_id = msg.get("id")
        if msg_id is not None and msg_id in self._pending:
            fut = self._pending.pop(msg_id)
            if not fut.done():
                if "error" in msg:
                    fut.set_exception(RuntimeError(f"LSP error: {msg['error']}"))
                else:
                    fut.set_result(msg.get("result"))
            return

        method = msg.get("method")
        if method == "textDocument/publishDiagnostics":
            params = msg.get("params", {})
            uri = params.get("uri", "")
            diags = params.get("diagnostics", [])
            self._diagnostics[uri] = diags
            if uri in self._diag_events:
                self._diag_events[uri].set()

    async def _send(self, msg: Dict[str, Any]) -> None:
        """Write framed JSON-RPC message to stdin."""
        if not self._process or not self._process.stdin:
            raise RuntimeError("LuaLS process not running")
        body = json.dumps(msg, separators=(",", ":")).encode("utf-8")
        header = f"Content-Length: {len(body)}\r\n\r\n".encode("utf-8")
        self._process.stdin.write(header + body)
        await self._process.stdin.drain()

    async def _log_stderr(self) -> None:
        assert self._process and self._process.stderr
        while True:
            try:
                chunk = await self._process.stderr.read(4096)
                if not chunk:
                    break
            except Exception:
                break

    async def request(self, method: str, params: Any = None, timeout: float = 20.0) -> Any:
        """Send a request and await response."""
        self._next_id += 1
        req_id = self._next_id
        msg: Dict[str, Any] = {"jsonrpc": "2.0", "id": req_id, "method": method}
        if params is not None:
            msg["params"] = params
        fut: asyncio.Future[Any] = asyncio.get_running_loop().create_future()
        self._pending[req_id] = fut
        await self._send(msg)
        return await asyncio.wait_for(fut, timeout=timeout)

    async def notify(self, method: str, params: Any = None) -> None:
        """Send notification."""
        msg: Dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        await self._send(msg)

    async def initialize(self, root_dir: str) -> Dict[str, Any]:
        """Perform LSP initialize handshake."""
        root_uri = path_to_uri(root_dir)
        res = await self.request("initialize", {
            "processId": os.getpid(),
            "rootUri": root_uri,
            "rootPath": root_dir,
            "capabilities": {
                "textDocument": {
                    "hover": {"contentFormat": ["markdown", "plaintext"]},
                    "definition": {"dynamicRegistration": False},
                    "references": {"dynamicRegistration": False},
                    "documentSymbol": {"hierarchicalDocumentSymbolSupport": True},
                    "publishDiagnostics": {"relatedInformation": True},
                },
                "workspace": {
                    "symbol": {"symbolKind": {"valueSet": list(SYMBOL_KINDS.keys())}}
                }
            }
        })
        await self.notify("initialized", {})
        return res

    async def open_file(self, path: str) -> str:
        """Open a file in LuaLS if not already open, returning URI."""
        p = pathlib.Path(path).resolve()
        uri = path_to_uri(str(p))
        if uri in self._open_files:
            return uri
        try:
            text = p.read_text(encoding="utf-8", errors="replace")
        except OSError:
            return uri

        await self.notify("textDocument/didOpen", {
            "textDocument": {
                "uri": uri,
                "languageId": "lua",
                "version": 1,
                "text": text
            }
        })
        self._open_files.add(uri)
        return uri

    async def get_diagnostics(self, path: str, wait_timeout: float = 1.5) -> List[Dict[str, Any]]:
        """Fetch diagnostics for a file, waiting briefly for publication if newly opened."""
        p = pathlib.Path(path).resolve()
        uri = path_to_uri(str(p))
        if uri not in self._diag_events:
            self._diag_events[uri] = asyncio.Event()

        await self.open_file(str(p))

        if uri not in self._diagnostics:
            try:
                await asyncio.wait_for(self._diag_events[uri].wait(), timeout=wait_timeout)
            except asyncio.TimeoutError:
                pass

        return self._diagnostics.get(uri, [])

    async def workspace_symbol(self, query: str) -> List[Dict[str, Any]]:
        result = await self.request("workspace/symbol", {"query": query})
        return result or []

    async def definition(self, path: str, line: int, character: int) -> List[Dict[str, Any]]:
        result = await self.request("textDocument/definition", {
            "textDocument": {"uri": path_to_uri(path)},
            "position": {"line": line, "character": character}
        })
        if not result:
            return []
        return result if isinstance(result, list) else [result]

    async def references(self, path: str, line: int, character: int) -> List[Dict[str, Any]]:
        result = await self.request("textDocument/references", {
            "textDocument": {"uri": path_to_uri(path)},
            "position": {"line": line, "character": character},
            "context": {"includeDeclaration": True}
        })
        return result or []

    async def hover(self, path: str, line: int, character: int) -> Optional[Dict[str, Any]]:
        return await self.request("textDocument/hover", {
            "textDocument": {"uri": path_to_uri(path)},
            "position": {"line": line, "character": character}
        })

    async def document_symbol(self, path: str) -> List[Dict[str, Any]]:
        result = await self.request("textDocument/documentSymbol", {
            "textDocument": {"uri": path_to_uri(path)}
        })
        return result or []
