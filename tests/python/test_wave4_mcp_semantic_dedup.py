# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Unit & live-corpus tests for Wave 4 Track 4 pgvector MCP-NNN semantic deduplication."""

from __future__ import annotations

import json
import math
import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = REPO_ROOT / "scripts" / "qa" / "mcp_issue_semantic_dedup.py"
SQL_SCHEMA_PATH = REPO_ROOT / "infra" / "postgres" / "pgvector_mcp_issues.sql"
LIVE_ISSUES_DIR = REPO_ROOT / "docs" / "reference" / "mcp-issues"

sys.path.insert(0, str(SCRIPT_PATH.parent))
import mcp_issue_semantic_dedup as dedup  # noqa: E402


def _write_issue(
    directory: Path,
    issue_id: str,
    server: str,
    tool: str,
    category: str,
    status: str,
    title: str,
    prevention_rule: str,
    body: str,
) -> Path:
    slug = issue_id.lower()
    path = directory / f"{issue_id}-{slug}.md"
    content = (
        "---\n"
        f"id: {issue_id}\n"
        f"server: {server}\n"
        f"tool: {tool}\n"
        f"category: {category}\n"
        f'title: "{title}"\n'
        f"status: {status}\n"
        'verified_by: ""\n'
        'superseded_by: ""\n'
        "supersedes: []\n"
        "occurrences: 2\n"
        "date: 2026-11-19\n"
        "last_seen: 2026-11-19\n"
        f"tags: [{server}, {tool}, {category}]\n"
        f'prevention_rule: "{prevention_rule}"\n'
        "---\n\n"
        f"# {issue_id}: {title}\n\n"
        f"{body}\n"
    )
    path.write_text(content, encoding="utf-8")
    return path


def test_pgvector_sql_schema_contains_required_idempotent_objects() -> None:
    sql_text = SQL_SCHEMA_PATH.read_text(encoding="utf-8")
    assert "CREATE EXTENSION IF NOT EXISTS vector;" in sql_text
    assert "CREATE TABLE IF NOT EXISTS mcp_issue_embeddings" in sql_text
    assert "embedding          vector(384) NOT NULL" in sql_text
    assert "USING hnsw (embedding vector_cosine_ops)" in sql_text
    assert "CREATE OR REPLACE FUNCTION find_similar_mcp_issues" in sql_text
    assert "CREATE OR REPLACE FUNCTION find_duplicate_mcp_issue_pairs" in sql_text
    assert "CREATE OR REPLACE FUNCTION upsert_mcp_issue_embedding" in sql_text


def test_deterministic_384d_unit_embeddings_and_duplicate_detection(tmp_path: Path) -> None:
    _write_issue(
        tmp_path,
        "MCP-901",
        "kongor",
        "kongor_client_eval_lua",
        "ipc-timeout",
        "open",
        "Named pipe timeout while evaluating K2 Lua widget visibility",
        "Check kongor_game_client_manage status before dispatching eval_lua over named pipe.",
        "Dispatching kongor_client_eval_lua timed out after 5000ms waiting on kongor-native-client named pipe.",
    )
    _write_issue(
        tmp_path,
        "MCP-902",
        "kongor",
        "kongor_client_eval_lua",
        "ipc-timeout",
        "open",
        "Named pipe timeout when evaluating K2 Lua widget visibility state",
        "Verify kongor_game_client_manage status prior to eval_lua named pipe dispatch.",
        "Calling kongor_client_eval_lua hit a 5000ms timeout waiting for kongor-native-client named pipe reply.",
    )
    _write_issue(
        tmp_path,
        "MCP-903",
        "ghidra",
        "decompile_function",
        "decompiler-timeout",
        "documented",
        "Ghidra decompiler hangs on CRT static initializer thunk",
        "Use disassemble instead of decompile_function on CRT startup routines.",
        "Decompiling __scrt_common_main_seh exceeded 120s JVM heap limit inside Ghidra headless bridge.",
    )
    _write_issue(
        tmp_path,
        "MCP-904",
        "kongor",
        "kongor_client_eval_lua",
        "ipc-timeout",
        "superseded",
        "Named pipe timeout while evaluating K2 Lua widget visibility (legacy duplicate)",
        "Check kongor_game_client_manage status before dispatching eval_lua.",
        "Duplicate legacy record for kongor_client_eval_lua named pipe timeout.",
    )

    docs = dedup.load_mcp_issues(tmp_path)
    assert len(docs) == 4

    fc = [dedup.extract_document_features(d) for d in docs]
    vec_a = dedup.DeterministicTfIdfHasher().fit(fc)
    emb_a = [vec_a.transform_features(f) for f in fc]
    emb_b = [dedup.DeterministicTfIdfHasher().fit(fc).transform_features(f) for f in fc]

    # Exact determinism & 384-d unit L2 norm
    assert emb_a == emb_b
    for emb in emb_a:
        assert len(emb) == 384
        assert math.isclose(math.sqrt(sum(v * v for v in emb)), 1.0, rel_tol=1e-6)

    # Near-duplicate pair (MCP-901 <-> MCP-902) scores high; unrelated Ghidra (MCP-903) scores low
    sim_dup = dedup.cosine_similarity(emb_a[0], emb_a[1])
    sim_unrelated = dedup.cosine_similarity(emb_a[0], emb_a[2])
    assert sim_dup > 0.65
    assert sim_unrelated < 0.30
    assert sim_dup > sim_unrelated + 0.35

    # Active-only excludes MCP-904 (superseded)
    res_active = dedup.run_dedup_pipeline(tmp_path, threshold=0.60, top_k=10, include_superseded=False)
    assert res_active["active_issues"] == 3
    assert res_active["superseded_issues"] == 1
    assert res_active["duplicate_pair_count"] == 1
    pair = res_active["duplicate_pairs"][0]
    assert (pair["issue_id_a"], pair["issue_id_b"]) == ("MCP-901", "MCP-902")

    # include_superseded=True surfaces MCP-904 pairs as well
    res_all = dedup.run_dedup_pipeline(tmp_path, threshold=0.60, top_k=10, include_superseded=True)
    assert res_all["duplicate_pair_count"] >= 2

    # Ad-hoc --query ranks Ghidra issue #1
    query_res = dedup.run_dedup_pipeline(
        tmp_path,
        threshold=0.10,
        top_k=3,
        query="ghidra decompile_function CRT startup initializer hang",
    )
    assert query_res["query_match_count"] >= 1
    assert query_res["query_matches"][0]["issue_id"] == "MCP-903"


def test_emit_sql_produces_valid_384d_pgvector_upserts(tmp_path: Path) -> None:
    _write_issue(
        tmp_path,
        "MCP-910",
        "kongor",
        "kongor_vfs_resolve",
        "vfs-precedence",
        "patched",
        "VFS archive precedence shadows loose override texture",
        "Run kongor_vfs_resolve before editing textures.",
        "Loose override file was shadowed by textures.s2z stem entry.",
    )
    sql_out = tmp_path / "upserts.sql"
    summary = dedup.run_dedup_pipeline(tmp_path, emit_sql_path=sql_out)
    assert summary["total_issues"] == 1
    sql_body = sql_out.read_text(encoding="utf-8")
    assert "SELECT upsert_mcp_issue_embedding('MCP-910'" in sql_body
    assert "::vector(384));" in sql_body
    # Count exact comma-separated coordinates inside the vector literal
    vec_start = sql_body.index("'[") + 2
    vec_end = sql_body.index("]'::vector(384)")
    coords = sql_body[vec_start:vec_end].split(",")
    assert len(coords) == 384


def test_live_mcp_issues_corpus_vectorizes_cleanly_via_cli(tmp_path: Path) -> None:
    assert LIVE_ISSUES_DIR.is_dir()
    json_out = tmp_path / "live_dedup.json"
    proc = subprocess.run(
        [
            sys.executable,
            str(SCRIPT_PATH),
            "--issues-dir",
            str(LIVE_ISSUES_DIR),
            "--threshold",
            "0.55",
            "--top-k",
            "15",
            "--json-out",
            str(json_out),
            "--quiet",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc.returncode == 0, f"stderr={proc.stderr}\nstdout={proc.stdout}"
    payload = json.loads(json_out.read_text(encoding="utf-8"))
    assert payload["total_issues"] >= 290
    assert payload["embedding_dim"] == 384
    assert math.isclose(payload["avg_l2_norm"], 1.0, abs_tol=1e-4)
    assert payload["elapsed_ms"] < 15000.0
    assert payload["duplicate_pair_count"] >= 2
    for pair in payload["duplicate_pairs"]:
        assert pair["cosine_similarity"] >= 0.55
        assert pair["status_a"] != "superseded"
        assert pair["status_b"] != "superseded"
