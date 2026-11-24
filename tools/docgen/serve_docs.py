#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
ProjectKongor DocFX Developer Documentation Server & Browser Launcher.
Compiles and serves the searchable documentation hub on http://localhost:8080.
"""

from __future__ import annotations

import argparse
import http.server
import json
import logging
import os
import shutil
import socketserver
import subprocess
import sys
import threading
import time
import webbrowser
from typing import Optional, List

logger = logging.getLogger("tools.docgen.serve_docs")


def setup_logging(verbose: bool = False) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    logging.basicConfig(
        level=level,
        format="[%(asctime)s] [%(levelname)s] [DocFX] %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        handlers=[logging.StreamHandler(sys.stdout)],
        force=True
    )


def is_tool_available(name: str) -> bool:
    return shutil.which(name) is not None


def ensure_docfx_built(repo_root: str, force_rebuild: bool = False) -> bool:
    """Introspects the entire codebase, builds component docs, and compiles DocFX / static hub."""
    site_dir = os.path.join(repo_root, "docs", "_site")
    docfx_config = os.path.join(repo_root, "docfx.json")

    # 1. Run live codebase introspection across apps, services, libs, and tools
    try:
        from .build_portal import build_portal
    except ImportError:
        from build_portal import build_portal

    logger.info("Introspecting all apps, microservices, libraries, and tools across repository...")
    build_portal()

    # 2. Run docfx build if available
    has_docfx = is_tool_available("docfx")
    if has_docfx and (force_rebuild or os.path.exists(docfx_config)):
        logger.info("Compiling DocFX documentation with API metadata via 'docfx build'...")
        try:
            res = subprocess.run(["docfx", "build", docfx_config], cwd=repo_root)
            if res.returncode == 0:
                logger.info("DocFX compilation succeeded.")
                return True
        except Exception as e:
            logger.warning("DocFX execution exception: %s", e)

    return True


def serve_docs(repo_root: str, port: int = 8080, open_browser: bool = True) -> None:
    """Serves the documentation site on http://localhost:<port>."""
    site_dir = os.path.join(repo_root, "docs", "_site")

    class DocHandler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=site_dir, **kwargs)

        def do_POST(self):
            if self.path.startswith("/api/ask"):
                content_len = int(self.headers.get("Content-Length", 0))
                post_data = self.rfile.read(content_len) if content_len > 0 else b"{}"
                try:
                    body = json.loads(post_data.decode("utf-8"))
                    q = body.get("query") or body.get("q") or ""
                except Exception:
                    q = ""

                try:
                    try:
                        from query_docs import CodebaseQAService
                    except ImportError:
                        from tools.docgen.query_docs import CodebaseQAService
                    qa_service = CodebaseQAService(repo_root=repo_root)
                    answer = qa_service.answer_question(q)
                    status_code = 200
                except ImportError:
                    answer = "Local Q&A engine (tools/docgen/query_docs.py) is not installed in this checkout."
                    status_code = 501

                resp_payload = json.dumps({"query": q, "answer": answer}).encode("utf-8")
                self.send_response(status_code)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Access-Control-Allow-Origin", "*")
                self.send_header("Content-Length", str(len(resp_payload)))
                self.end_headers()
                self.wfile.write(resp_payload)
                return

            super().do_POST()

        def do_GET(self):
            if self.path.startswith("/api/ask"):
                import urllib.parse
                parsed = urllib.parse.urlparse(self.path)
                params = urllib.parse.parse_qs(parsed.query)
                q = params.get("q", [""])[0] or params.get("query", [""])[0]

                try:
                    try:
                        from query_docs import CodebaseQAService
                    except ImportError:
                        from tools.docgen.query_docs import CodebaseQAService
                    qa_service = CodebaseQAService(repo_root=repo_root)
                    answer = qa_service.answer_question(q)
                    status_code = 200
                except ImportError:
                    answer = "Local Q&A engine (tools/docgen/query_docs.py) is not installed in this checkout."
                    status_code = 501

                resp_payload = json.dumps({"query": q, "answer": answer}).encode("utf-8")
                self.send_response(status_code)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Access-Control-Allow-Origin", "*")
                self.send_header("Content-Length", str(len(resp_payload)))
                self.end_headers()
                self.wfile.write(resp_payload)
                return

            super().do_GET()

        def do_OPTIONS(self):
            self.send_response(204)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.end_headers()

        def log_message(self, format, *args):
            logger.info("[HTTP] %s - " + format, self.address_string(), *args)

    socketserver.TCPServer.allow_reuse_address = True
    try:
        with socketserver.TCPServer(("", port), DocHandler) as httpd:
            url = f"http://localhost:{port}"
            logger.info("=" * 70)
            logger.info(" 📖 PROJECT KONGOR: DEVELOPER DOCUMENTATION HUB ONLINE")
            logger.info("=" * 70)
            logger.info(" Primary URL:      %s", url)
            logger.info(" Site Directory:   %s", site_dir)

            # Also attempt to serve on alternate port (8080 or 55590) so both ports work seamlessly
            alt_port = 8080 if port != 8080 else 55590
            try:
                alt_server = socketserver.TCPServer(("", alt_port), DocHandler)
                threading.Thread(target=alt_server.serve_forever, daemon=True).start()
                logger.info(" Alternate URL:    http://localhost:%d", alt_port)
            except Exception as alt_ex:
                logger.info(" Alternate port %d not available: %s", alt_port, alt_ex)

            logger.info(" Press Ctrl+C to stop the documentation server.")
            logger.info("=" * 70)

            if open_browser:
                threading.Thread(target=lambda: (time.sleep(0.5), webbrowser.open(url)), daemon=True).start()

            httpd.serve_forever()

    except OSError as ex:
        logger.error("Could not bind documentation server on port %d: %s", port, ex)


def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Serve ProjectKongor Developer Documentation Hub")
    parser.add_argument("--port", "-p", type=int, default=55590, help="Port to serve docs on (default: 55590)")
    parser.add_argument("--build", "-b", action="store_true", help="Force rebuild documentation site")
    parser.add_argument("--no-browser", action="store_true", help="Do not open browser automatically")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose logging")
    args = parser.parse_args(argv)

    setup_logging(verbose=args.verbose)

    repo_root = _find_repo_root()

    ensure_docfx_built(repo_root, force_rebuild=args.build)
    serve_docs(repo_root, port=args.port, open_browser=not args.no_browser)
    return 0


if __name__ == "__main__":
    sys.exit(main())
