# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Project Kongor - Wave 3A Nil-Safe Lua 5.1 GetWidget():Method() Codemod

Rewrites standalone statement-level K2 Lua calls of the form:
    <indent><receiver>GetWidget(<args>):<method>(<method_args>)
into zero-allocation, Lua 5.1 register-safe nil-guarded blocks:
    <indent>do local _kw = <receiver>GetWidget(<args>); if _kw then _kw:<method>(<method_args>) end end

Why `do local _kw = ...; if _kw then ... end end`?
- Lua 5.1 enforces a hard 200-local-variable limit per function. Wrapping `_kw`
  in a lexical `do ... end` block releases the register immediately at `end`.
- Zero table or closure allocations (frame-budget safe for <= 1.0 ms HUD loops).
- Strictly preserves comments, long-bracket strings/comments (`--[[...]]`,
  `[=[...]=]`), multi-line expressions, assignments, returns, and chained
  method calls unless explicitly standalone single-method statements.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable, List, Optional, Tuple

REPO_ROOT = Path(__file__).resolve().parents[2]

_RECEIVER_RE = re.compile(
    r"^(?P<indent>[ \t]*)"
    r"(?P<call>(?:[A-Za-z_][A-Za-z0-9_]*[.:])?GetWidget)\s*\("
)

_METHOD_RE = re.compile(r"^:(?P<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(")

_PREV_CONTINUATION_RE = re.compile(
    r"(?:\b(?:return|and|or|not|local|elseif|if|while|until)|[=,(\[{+\-*/%^#<>;]|(?:\.\.))\s*$"
)
_NEXT_CONTINUATION_RE = re.compile(
    r"^[ \t]*(?:[.:(\[{},)=<>~+\-*/%^]|(?:\.\.)|\b(?:and|or|then|else|elseif)\b)"
)
_LONG_OPEN_RE = re.compile(r"\[(=*)\[")


@dataclass
class FileCodemodResult:
    path: str
    candidates_found: int
    rewritten: int
    skipped_multiline_or_chain: int
    luac51_ok: Optional[bool]
    elapsed_ms: float
    sample_rewrites: List[str]


def find_luac51(repo_root: Path = REPO_ROOT) -> Optional[Path]:
    for candidate in (
        repo_root / "native" / "build" / "luac51_build" / "Release" / "luac51.exe",
        repo_root / "native" / "tools" / "luac51" / "build" / "Release" / "luac51.exe",
        repo_root / "artifacts" / "native-binaries" / "windows-x64" / "luac51.exe",
        repo_root / ".tools" / "bin" / "lua" / "luac51.exe",
    ):
        if candidate.is_file():
            return candidate
    return None


def _strip_line_comment_preserving_strings(line: str) -> Tuple[str, str]:
    """Splits a single Lua code line (outside long brackets) into (code, trailing_comment)."""
    in_quote: Optional[str] = None
    i = 0
    n = len(line)
    while i < n:
        ch = line[i]
        if in_quote is not None:
            if ch == "\\" and i + 1 < n:
                i += 2
                continue
            if ch == in_quote:
                in_quote = None
            i += 1
            continue
        if ch in ("'", '"'):
            in_quote = ch
            i += 1
            continue
        if ch == "-" and i + 1 < n and line[i + 1] == "-":
            return line[:i], line[i:]
        i += 1
    return line, ""


def _find_matching_paren(text: str, open_idx: int) -> int:
    """Given index of '(', returns index of matching ')' or -1 if unmatched on this line."""
    depth = 0
    in_quote: Optional[str] = None
    i = open_idx
    n = len(text)
    while i < n:
        ch = text[i]
        if in_quote is not None:
            if ch == "\\" and i + 1 < n:
                i += 2
                continue
            if ch == in_quote:
                in_quote = None
            i += 1
            continue
        if ch in ("'", '"'):
            in_quote = ch
            i += 1
            continue
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _update_long_bracket_state(line: str, active_eq_len: Optional[int]) -> Tuple[Optional[int], bool]:
    """
    Tracks Lua long-bracket blocks (`[[...]]`, `--[[...]]`, `[=[...]=]`).
    Returns (new_active_eq_len, line_touches_long_bracket).
    """
    i = 0
    n = len(line)
    touched = active_eq_len is not None
    in_quote: Optional[str] = None

    while i < n:
        if active_eq_len is not None:
            close_token = "]" + ("=" * active_eq_len) + "]"
            pos = line.find(close_token, i)
            if pos == -1:
                return active_eq_len, True
            i = pos + len(close_token)
            active_eq_len = None
            touched = True
            continue

        ch = line[i]
        if in_quote is not None:
            if ch == "\\" and i + 1 < n:
                i += 2
                continue
            if ch == in_quote:
                in_quote = None
            i += 1
            continue

        if ch in ("'", '"'):
            in_quote = ch
            i += 1
            continue

        if ch == "-" and i + 1 < n and line[i + 1] == "-":
            m = _LONG_OPEN_RE.match(line, i + 2)
            if m:
                active_eq_len = len(m.group(1))
                touched = True
                i = m.end()
                continue
            # Regular single-line comment ends scanning for long-bracket openers.
            break

        if ch == "[":
            m = _LONG_OPEN_RE.match(line, i)
            if m:
                active_eq_len = len(m.group(1))
                touched = True
                i = m.end()
                continue

        i += 1

    return active_eq_len, touched


def _prev_nonempty_code(lines: List[str], idx: int) -> str:
    j = idx - 1
    while j >= 0:
        code, _ = _strip_line_comment_preserving_strings(lines[j])
        stripped = code.strip()
        if stripped:
            return stripped
        j -= 1
    return ""


def _next_nonempty_code(lines: List[str], idx: int) -> str:
    j = idx + 1
    n = len(lines)
    while j < n:
        code, _ = _strip_line_comment_preserving_strings(lines[j])
        stripped = code.strip()
        if stripped:
            return stripped
        j += 1
    return ""


def transform_lua_source(source: str) -> Tuple[str, int, int, int, List[str]]:
    """
    Transforms standalone `GetWidget(...):Method(...)` statements in Lua source.

    Returns: (new_source, candidates_found, rewritten, skipped_multiline_or_chain, sample_rewrites)
    """
    # Preserve exact line endings per line via splitlines(keepends=True)
    raw_lines = source.splitlines(keepends=True)
    bare_lines = [ln.rstrip("\r\n") for ln in raw_lines]
    endings = [ln[len(bare):] for ln, bare in zip(raw_lines, bare_lines)]

    active_eq_len: Optional[int] = None
    out_lines: List[str] = []
    candidates = 0
    rewritten = 0
    skipped = 0
    samples: List[str] = []

    for idx, bare in enumerate(bare_lines):
        ending = endings[idx]
        next_eq_len, touched_long = _update_long_bracket_state(bare, active_eq_len)
        was_in_long = active_eq_len is not None
        active_eq_len = next_eq_len

        if was_in_long or touched_long:
            out_lines.append(bare + ending)
            continue

        code, comment = _strip_line_comment_preserving_strings(bare)
        m = _RECEIVER_RE.match(code)
        if not m:
            out_lines.append(bare + ending)
            continue

        open_paren = m.end() - 1
        close_paren = _find_matching_paren(code, open_paren)
        if close_paren == -1:
            candidates += 1
            skipped += 1
            out_lines.append(bare + ending)
            continue

        after_getwidget = code[close_paren + 1 :]
        mm = _METHOD_RE.match(after_getwidget)
        if not mm:
            out_lines.append(bare + ending)
            continue

        candidates += 1

        method_open = close_paren + 1 + mm.end() - 1
        method_close = _find_matching_paren(code, method_open)
        if method_close == -1:
            skipped += 1
            out_lines.append(bare + ending)
            continue

        tail = code[method_close + 1 :]
        tail_stripped = tail.strip()
        if tail_stripped not in ("", ";"):
            # Chained method call (`:GetWidget():A():B()`), operator, or multi-statement line: skip safely.
            skipped += 1
            out_lines.append(bare + ending)
            continue

        prev_code = _prev_nonempty_code(bare_lines, idx)
        if prev_code and _PREV_CONTINUATION_RE.search(prev_code):
            skipped += 1
            out_lines.append(bare + ending)
            continue

        next_code = _next_nonempty_code(bare_lines, idx)
        if next_code and _NEXT_CONTINUATION_RE.match(next_code):
            skipped += 1
            out_lines.append(bare + ending)
            continue

        indent = m.group("indent")
        getwidget_expr = code[len(indent) : close_paren + 1]
        method_call = code[close_paren + 1 : method_close + 1]

        # Preserve optional trailing whitespace before inline comment cleanly
        comment_suffix = ""
        if comment:
            ws_between = tail[len(tail_stripped) :] if tail_stripped == ";" else tail
            if not ws_between:
                ws_between = " "
            comment_suffix = ws_between + comment

        rewritten_line = (
            f"{indent}do local _kw = {getwidget_expr}; "
            f"if _kw then _kw{method_call} end end{comment_suffix}"
        )
        rewritten += 1
        if len(samples) < 5:
            samples.append(f"L{idx + 1}: {bare.strip()} -> {rewritten_line.strip()}")
        out_lines.append(rewritten_line + ending)

    return "".join(out_lines), candidates, rewritten, skipped, samples


def verify_luac51(source: str, luac51: Path) -> Tuple[bool, str]:
    with tempfile.NamedTemporaryFile("wb", suffix=".lua", delete=False) as tmp:
        tmp.write(source.encode("utf-8", errors="surrogateescape"))
        tmp_path = Path(tmp.name)
    try:
        proc = subprocess.run(
            [str(luac51), "-p", str(tmp_path)],
            capture_output=True,
            text=True,
            errors="replace",
            timeout=15,
            check=False,
        )
        if proc.returncode == 0:
            return True, ""
        return False, (proc.stderr or proc.stdout).strip()
    finally:
        tmp_path.unlink(missing_ok=True)


def iter_lua_files(targets: Iterable[str]) -> List[Path]:
    files: List[Path] = []
    seen = set()
    for raw in targets:
        p = Path(raw)
        if not p.is_absolute():
            p = (REPO_ROOT / p).resolve()
        if p.is_dir():
            for child in sorted(p.rglob("*.lua")):
                if child not in seen:
                    seen.add(child)
                    files.append(child)
        elif p.is_file():
            if p not in seen:
                seen.add(p)
                files.append(p)
        else:
            raise FileNotFoundError(f"Target path not found: {raw} (resolved: {p})")
    return files


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Nil-safe Lua 5.1 GetWidget():Method() codemod for K2 UI scripts."
    )
    parser.add_argument("targets", nargs="+", help="Lua files or directories to scan.")
    parser.add_argument("--apply", action="store_true", help="Write transformed files to disk.")
    parser.add_argument("--dry-run", action="store_true", help="Preview changes without writing (default).")
    parser.add_argument(
        "--verify-luac51",
        action="store_true",
        help="Verify transformed Lua via luac51.exe -p (enabled by default when --apply is set).",
    )
    parser.add_argument(
        "--no-verify-luac51",
        action="store_true",
        help="Disable automatic luac51.exe syntax verification when --apply is set (escape hatch).",
    )
    parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON summary.")
    args = parser.parse_args(argv)

    apply_mode = bool(args.apply) and not bool(args.dry_run)
    if args.verify_luac51 and args.no_verify_luac51:
        print(
            "[CODEMOD-ERROR] Cannot pass both --verify-luac51 and --no-verify-luac51.",
            file=sys.stderr,
        )
        return 2

    should_verify = bool(args.verify_luac51) or (apply_mode and not bool(args.no_verify_luac51))
    luac51 = find_luac51() if should_verify else None
    if should_verify and luac51 is None:
        print(
            "[CODEMOD-ERROR] luac51 verification is enabled (default ON for --apply), "
            "but luac51.exe was not found. Build luac51 or pass --no-verify-luac51 explicitly.",
            file=sys.stderr,
        )
        return 2

    files = iter_lua_files(args.targets)
    results: List[FileCodemodResult] = []
    total_candidates = 0
    total_rewritten = 0
    total_skipped = 0
    failures = 0

    for path in files:
        t0 = time.perf_counter()
        raw_bytes = path.read_bytes()
        source = raw_bytes.decode("utf-8", errors="surrogateescape")
        new_source, candidates, rewritten, skipped, samples = transform_lua_source(source)

        luac_ok: Optional[bool] = None
        if luac51 is not None and rewritten > 0:
            ok, err = verify_luac51(new_source, luac51)
            luac_ok = ok
            if not ok:
                failures += 1
                print(
                    f"[CODEMOD-LUAC-FAIL] {path}: luac51 rejected transformed Lua: {err}",
                    file=sys.stderr,
                )
                elapsed_ms = (time.perf_counter() - t0) * 1000.0
                results.append(
                    FileCodemodResult(
                        path=str(path),
                        candidates_found=candidates,
                        rewritten=0,
                        skipped_multiline_or_chain=skipped,
                        luac51_ok=False,
                        elapsed_ms=round(elapsed_ms, 2),
                        sample_rewrites=samples,
                    )
                )
                continue

        if apply_mode and rewritten > 0 and new_source != source:
            path.write_bytes(new_source.encode("utf-8", errors="surrogateescape"))

        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        total_candidates += candidates
        total_rewritten += rewritten
        total_skipped += skipped
        results.append(
            FileCodemodResult(
                path=str(path),
                candidates_found=candidates,
                rewritten=rewritten,
                skipped_multiline_or_chain=skipped,
                luac51_ok=luac_ok,
                elapsed_ms=round(elapsed_ms, 2),
                sample_rewrites=samples,
            )
        )

    payload = {
        "mode": "apply" if apply_mode else "dry-run",
        "files_scanned": len(files),
        "total_candidates": total_candidates,
        "total_rewritten": total_rewritten,
        "total_skipped": total_skipped,
        "luac51_failures": failures,
        "files": [asdict(r) for r in results],
    }

    if args.json:
        print(json.dumps(payload, indent=2))
    else:
        print(
            f"[GETWIDGET-CODEMOD] mode={payload['mode']} files={len(files)} "
            f"candidates={total_candidates} rewritten={total_rewritten} "
            f"skipped={total_skipped} luac51_failures={failures}"
        )
        for r in results:
            if r.rewritten > 0 or r.luac51_ok is False:
                print(
                    f"  - {r.path}: rewritten={r.rewritten} skipped={r.skipped_multiline_or_chain} "
                    f"luac51_ok={r.luac51_ok} ({r.elapsed_ms:.1f}ms)"
                )
                for s in r.sample_rewrites[:2]:
                    print(f"      {s}")

    return 0 if failures == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
