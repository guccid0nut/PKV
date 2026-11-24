#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Native Everything Search MCP Server for ProjectKongor.
Uses MCPServer (MCP 2.x) to expose Voidtools Everything file search via es.exe
with automatic self-healing:
- Sort parameter normalization ('date_modified_desc', 'size-desc', etc.) (MCP-117, MCP-120)
- Compound '|' OR-query splitting when 'path' is specified (MCP-108)
- Default query='*' in everything_count_stats (MCP-122)
- Auto-launch of Everything.exe + fast native Python os.scandir fallback when
  Everything IPC is offline or sandboxed (MCP-009, MCP-076, MCP-083, MCP-091, MCP-099, MCP-110, MCP-118, MCP-124)
"""

import builtins
import fnmatch
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from typing import Any, Dict, List, Optional

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent.parent
_TOOLS_DIR = str(SCRIPT_DIR.parent)
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

from _common.mcp_telemetry import (
    create_fastmcp_server,
    resolve_tool_binary,
    run_fastmcp_stdio,
    shield_stdio_stdout,
)

shield_stdio_stdout()


def _resolve_es_path() -> str:
    env_override = os.environ.get("EVERYTHING_ES_PATH", "").strip()
    if env_override:
        return env_override
    for cand in (
        REPO_ROOT / ".tools" / "bin" / "everything" / "es.exe",
        REPO_ROOT / "artifacts" / "native-binaries" / "windows-x64" / "es.exe",
        SCRIPT_DIR / "es.exe",
    ):
        if cand.is_file():
            return str(cand)
    return shutil.which("es") or str(REPO_ROOT / ".tools" / "bin" / "everything" / "es.exe")


DEFAULT_ES_PATH = REPO_ROOT / ".tools" / "bin" / "everything" / "es.exe"
ES_PATH = _resolve_es_path()

EVERYTHING_EXE_CANDIDATES = [
    r"C:\Program Files\Everything\Everything.exe",
    r"C:\Program Files (x86)\Everything\Everything.exe",
    str(REPO_ROOT / ".tools" / "bin" / "everything" / "Everything.exe"),
    str(SCRIPT_DIR / "Everything.exe"),
]

SORT_MAP: Dict[str, str] = {
    "name": "name-ascending",
    "name-asc": "name-ascending",
    "name-ascending": "name-ascending",
    "name-desc": "name-descending",
    "name-descending": "name-descending",
    "path": "path-ascending",
    "path-asc": "path-ascending",
    "path-ascending": "path-ascending",
    "path-desc": "path-descending",
    "path-descending": "path-descending",
    "size": "size-descending",
    "size-desc": "size-descending",
    "size-descending": "size-descending",
    "size-asc": "size-ascending",
    "size-ascending": "size-ascending",
    "date-modified-desc": "date-modified-descending",
    "date-modified-descending": "date-modified-descending",
    "date-modified": "date-modified-ascending",
    "date-modified-asc": "date-modified-ascending",
    "date-modified-ascending": "date-modified-ascending",
    "date-created-desc": "date-created-descending",
    "date-created-descending": "date-created-descending",
    "date-created": "date-created-ascending",
    "date-created-asc": "date-created-ascending",
    "date-created-ascending": "date-created-ascending",
}

_autostart_attempted = False
_es_backoff_until: float = 0.0

server = create_fastmcp_server("everything-search")


def _normalize_sort(sort: Optional[str]) -> str:
    """Normalize sort parameter (underscores -> hyphens) with safe fallback (MCP-117, MCP-120)."""
    if not sort:
        return "date-modified-descending"
    cleaned = str(sort).strip().lower().replace("_", "-")
    return SORT_MAP.get(cleaned, "date-modified-descending")


def _try_autostart_everything() -> bool:
    """Attempt once to start Everything.exe in the background if installed."""
    global _autostart_attempted
    if _autostart_attempted:
        return False
    _autostart_attempted = True
    for exe in EVERYTHING_EXE_CANDIDATES:
        if os.path.isfile(exe):
            try:
                subprocess.Popen(
                    [exe, "-startup"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    stdin=subprocess.DEVNULL,
                    creationflags=getattr(subprocess, "DETACHED_PROCESS", 0x00000008),
                )
                time.sleep(0.4)
                return True
            except Exception:
                pass
    return False


def _run_es(args: List[str], timeout: float = 2.0) -> subprocess.CompletedProcess[str]:
    """Execute es.exe with specified arguments, auto-starting Everything.exe once on Error 8 and circuit-breaking for 30s if IPC is unavailable."""
    global _es_backoff_until
    now = time.monotonic()
    if now < _es_backoff_until:
        return subprocess.CompletedProcess(args=[ES_PATH] + args, returncode=8, stdout="", stderr="Everything IPC circuit-breaker active (using fast Python fallback)")

    cmd = [ES_PATH, "-timeout", "1500"] + args
    try:
        res = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
        )
        if res.returncode == 8 and _try_autostart_everything():
            res = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=timeout,
            )
        if res.returncode != 0:
            _es_backoff_until = time.monotonic() + 30.0
        return res
    except subprocess.TimeoutExpired as ex:
        _es_backoff_until = time.monotonic() + 30.0
        return subprocess.CompletedProcess(args=cmd, returncode=8, stdout="", stderr=f"es.exe timed out after {timeout}s ({ex})")


def _sort_paths(paths: List[str], mapped_sort: str) -> List[str]:
    """Sort file paths in Python matching Everything's sort semantics."""
    if not paths:
        return paths

    def _safe_stat(p: str):
        try:
            return os.stat(p)
        except OSError:
            return None

    if mapped_sort.startswith("date-modified"):
        reverse = mapped_sort.endswith("descending")
        return sorted(
            paths,
            key=lambda p: (st.st_mtime if (st := _safe_stat(p)) else 0.0),
            reverse=reverse,
        )
    if mapped_sort.startswith("date-created"):
        reverse = mapped_sort.endswith("descending")
        return sorted(
            paths,
            key=lambda p: (st.st_ctime if (st := _safe_stat(p)) else 0.0),
            reverse=reverse,
        )
    if mapped_sort.startswith("size"):
        reverse = mapped_sort.endswith("descending")
        return sorted(
            paths,
            key=lambda p: (st.st_size if (st := _safe_stat(p)) else 0),
            reverse=reverse,
        )
    if mapped_sort.startswith("path"):
        reverse = mapped_sort.endswith("descending")
        return sorted(paths, key=lambda p: p.lower(), reverse=reverse)
    # Default: name
    reverse = mapped_sort.endswith("descending")
    return sorted(paths, key=lambda p: os.path.basename(p).lower(), reverse=reverse)


def _compile_single_term_matcher(
    term: str,
    match_case: bool = False,
    match_whole_word: bool = False,
    match_regex: bool = False,
):
    """Build a fast filename/path predicate for a single Everything search term."""
    raw = term.strip()
    if not raw or raw == "*":
        return lambda _name, _full: True

    if match_regex:
        flags = 0 if match_case else re.IGNORECASE
        rx = re.compile(raw, flags)
        return lambda name, full: bool(rx.search(name) or rx.search(full))

    # Support space-separated AND tokens within a single branch (e.g. "ext:lua caldavar")
    tokens = [t.strip() for t in raw.split() if t.strip()]
    if len(tokens) > 1:
        sub_matchers = [
            _compile_single_term_matcher(tok, match_case, match_whole_word, False)
            for tok in tokens
        ]
        return lambda name, full: all(m(name, full) for m in sub_matchers)

    tok = tokens[0]
    lower_tok = tok.lower()

    # Handle Everything's ext:log or ext:log;dmp syntax
    if lower_tok.startswith("ext:"):
        ext_list = [
            ("." + e.lstrip(".")).lower()
            for e in lower_tok[4:].replace(",", ";").split(";")
            if e.strip()
        ]
        return lambda name, _full: any(name.lower().endswith(ext) for ext in ext_list)

    # Handle size:>5gb etc.
    if lower_tok.startswith("size:"):
        m = re.match(r"^size:([<>]=?)(\d+(?:\.\d+)?)(kb|mb|gb)?$", lower_tok)
        if m:
            op, num_s, unit = m.groups()
            mult = {"kb": 1024, "mb": 1024**2, "gb": 1024**3}.get(unit or "", 1)
            threshold = float(num_s) * mult

            def _size_match(_name: str, full: str) -> bool:
                try:
                    sz = os.path.getsize(full)
                except OSError:
                    return False
                if op == ">":
                    return sz > threshold
                if op == ">=":
                    return sz >= threshold
                if op == "<":
                    return sz < threshold
                return sz <= threshold

            return _size_match

    # Handle glob wildcards (* or ?)
    if "*" in tok or "?" in tok:
        if match_case:
            return lambda name, _full: fnmatch.fnmatchcase(name, tok)
        pat = tok.lower()
        return lambda name, _full: fnmatch.fnmatchcase(name.lower(), pat)

    # Whole word or substring match
    if match_whole_word:
        flags = 0 if match_case else re.IGNORECASE
        rx = re.compile(rf"\b{re.escape(tok)}\b", flags)
        return lambda name, _full: bool(rx.search(name))

    if match_case:
        return lambda name, full: (tok in name) or (tok in full)
    return lambda name, full: (lower_tok in name.lower()) or (lower_tok in full.lower())


def _python_fallback_search(
    query: str,
    max_results: int,
    path: Optional[str],
    mapped_sort: str,
    match_case: bool = False,
    match_whole_word: bool = False,
    match_regex: bool = False,
    deadline_sec: float = 8.0,
) -> List[str]:
    """
    Fast bounded native Python filesystem search used when Everything.exe IPC is offline
    (MCP-009, MCP-076, MCP-083, MCP-091, MCP-099, MCP-110, MCP-118, MCP-124).
    """
    root_dir = Path(path) if (path and os.path.exists(path)) else REPO_ROOT
    if not root_dir.exists():
        return []

    if match_regex:
        branch_matchers = [_compile_single_term_matcher(query, match_case, match_whole_word, True)]
    else:
        branches = [b.strip() for b in query.split("|") if b.strip()] or ["*"]
        branch_matchers = [
            _compile_single_term_matcher(b, match_case, match_whole_word, False)
            for b in branches
        ]

    matched: List[str] = []
    deadline = time.monotonic() + deadline_sec
    skip_dirs = {".git", "node_modules", "__pycache__"}

    for dirpath, dirnames, filenames in os.walk(str(root_dir)):
        if time.monotonic() > deadline:
            break
        dirnames[:] = [d for d in dirnames if d not in skip_dirs]
        for fname in filenames:
            full_path = os.path.join(dirpath, fname)
            if any(m(fname, full_path) for m in branch_matchers):
                matched.append(full_path)

    sorted_matches = _sort_paths(matched, mapped_sort)
    return sorted_matches[: max(1, min(max_results, 500))]


@server.tool()
def everything_search(
    query: str = "*",
    max_results: int = 50,
    path: Optional[str] = None,
    sort: Optional[str] = "date-modified-desc",
    match_case: bool = False,
    match_whole_word: bool = False,
    match_regex: bool = False,
) -> Dict[str, Any]:
    """
    Search Windows filesystem at lightning speed using Voidtools Everything
    with automatic native Python fallback if Everything IPC is offline.

    Args:
        query: Search query (e.g. "*.s2z", "Hero_Pyromancer", "ext:log|ext:dmp").
        max_results: Maximum number of results to return (1-500).
        path: Scope search to a specific directory path.
        sort: Sort field: name, path, size, size-desc, date-modified-desc, date-created-desc.
        match_case: Case-sensitive search.
        match_whole_word: Match whole words only.
        match_regex: Treat query as a regular expression.
    """
    clamped_max = min(max(1, max_results), 500)
    mapped_sort = _normalize_sort(sort)

    base_args = ["-n", str(clamped_max)]
    if match_case:
        base_args.append("-case")
    if match_whole_word:
        base_args.append("-whole-word")
    if match_regex:
        base_args.append("-regex")
    base_args.extend(["-sort", mapped_sort])
    if path:
        base_args.extend(["-path", path])

    # Split compound '|' queries when path is provided so each term stays scoped to path (MCP-108)
    sub_queries = (
        [q.strip() for q in query.split("|") if q.strip()]
        if (not match_regex and "|" in query)
        else [query]
    )

    try:
        collected: List[str] = []
        seen: set[str] = set()
        for sub_q in sub_queries:
            res = _run_es(base_args + [sub_q])
            if res.returncode != 0:
                err_msg = res.stderr.strip() or res.stdout.strip()
                # Self-heal via Python fallback when Everything IPC is offline (Error 8)
                fallback_results = _python_fallback_search(
                    query=query,
                    max_results=clamped_max,
                    path=path,
                    mapped_sort=mapped_sort,
                    match_case=match_case,
                    match_whole_word=match_whole_word,
                    match_regex=match_regex,
                )
                return {
                    "success": True,
                    "engine": "python_fallback",
                    "fallback_reason": err_msg,
                    "query": query,
                    "count": len(fallback_results),
                    "results": fallback_results,
                }
            for line in res.stdout.splitlines():
                stripped = line.strip()
                if stripped and stripped not in seen:
                    seen.add(stripped)
                    collected.append(stripped)

        if len(sub_queries) > 1:
            collected = _sort_paths(collected, mapped_sort)[:clamped_max]

        return {
            "success": True,
            "engine": "everything_es",
            "query": query,
            "count": len(collected),
            "results": collected,
        }
    except Exception as ex:
        fallback_results = _python_fallback_search(
            query=query,
            max_results=clamped_max,
            path=path,
            mapped_sort=mapped_sort,
            match_case=match_case,
            match_whole_word=match_whole_word,
            match_regex=match_regex,
        )
        return {
            "success": True,
            "engine": "python_fallback",
            "fallback_reason": str(ex),
            "query": query,
            "count": len(fallback_results),
            "results": fallback_results,
        }


@server.tool()
def everything_count_stats(query: str = "*", path: Optional[str] = None) -> Dict[str, Any]:
    """
    Quickly count the number of matching files and inspect total matches.
    Defaults query to '*' when omitted (MCP-122).
    """
    effective_query = query.strip() if (query and query.strip()) else "*"
    sub_queries = (
        [q.strip() for q in effective_query.split("|") if q.strip()]
        if "|" in effective_query
        else [effective_query]
    )

    try:
        total = 0
        for sub_q in sub_queries:
            args = ["-get-result-count"]
            if path:
                args.extend(["-path", path])
            args.append(sub_q)
            res = _run_es(args)
            if res.returncode != 0:
                fallback = _python_fallback_search(
                    query=effective_query,
                    max_results=500,
                    path=path,
                    mapped_sort="name-ascending",
                )
                return {
                    "success": True,
                    "engine": "python_fallback",
                    "query": effective_query,
                    "total_matches": len(fallback),
                }
            count_str = res.stdout.strip()
            total += int(count_str) if count_str.isdigit() else 0
        return {
            "success": True,
            "engine": "everything_es",
            "query": effective_query,
            "total_matches": total,
        }
    except Exception as ex:
        fallback = _python_fallback_search(
            query=effective_query,
            max_results=500,
            path=path,
            mapped_sort="name-ascending",
        )
        return {
            "success": True,
            "engine": "python_fallback",
            "fallback_reason": str(ex),
            "query": effective_query,
            "total_matches": len(fallback),
        }


@server.tool()
def everything_file_details(paths: List[str]) -> List[Dict[str, Any]]:
    """
    Retrieve details (size, modified time, exists) for a list of file paths.
    """
    details = []
    for p in paths:
        path_obj = Path(p)
        if not path_obj.exists():
            details.append({"path": p, "exists": False})
            continue

        stat = path_obj.stat()
        details.append({
            "path": p,
            "exists": True,
            "is_dir": path_obj.is_dir(),
            "size_bytes": stat.st_size,
            "modified_time": stat.st_mtime,
        })
    return details


if __name__ == "__main__":
    run_fastmcp_stdio(server, "everything")

