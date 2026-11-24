#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Deterministic 384-d TF-IDF/n-gram semantic deduplicator for Project Kongor MCP-NNN issues.

Zero external API or ML-runtime dependencies: computes reproducible 384-dimensional
L2-normalized embeddings across docs/reference/mcp-issues/MCP-*.md using signed
Blake2b feature hashing over word unigrams, word bigrams, and character 3..5-grams
weighted by corpus IDF.

Supports:
  - Pairwise cosine similarity duplicate pair detection across active/all MCP-NNN files
  - Ad-hoc --query similarity lookup (pre-creation anti-duplicate semantic check)
  - Optional PostgreSQL pgvector upsert & HNSW query mode (--pgvector-dsn)
  - Offline SQL export (--emit-sql) compatible with infra/postgres/pgvector_mcp_issues.sql
"""

from __future__ import annotations

import argparse
import dataclasses
import hashlib
import importlib
import json
import math
import re
import struct
import sys
import time
from collections import Counter
from pathlib import Path
from typing import Any, Iterable, Sequence

EMBEDDING_DIM = 384
VECTORIZER_VERSION = "kongor-tfidf-ngram-384-v1"

_FRONTMATTER_RE = re.compile(r"^---\n(.*?)\n---\n(.*)$", re.DOTALL)
_TOKEN_RE = re.compile(r"[a-z0-9][a-z0-9_./:-]{1,}")
_STOPWORDS = frozenset(
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "for",
        "from",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "this",
        "to",
        "with",
        "when",
        "not",
        "into",
        "via",
        "use",
        "before",
        "after",
    }
)


@dataclasses.dataclass(frozen=True)
class McpIssueDocument:
    issue_id: str
    file_name: str
    server: str
    tool: str
    category: str
    status: str
    title: str
    prevention_rule: str
    tags: tuple[str, ...]
    body_excerpt: str
    content_sha256: str


@dataclasses.dataclass(frozen=True)
class DuplicatePair:
    issue_id_a: str
    issue_id_b: str
    file_a: str
    file_b: str
    server_a: str
    tool_a: str
    server_b: str
    tool_b: str
    status_a: str
    status_b: str
    title_a: str
    title_b: str
    cosine_similarity: float


@dataclasses.dataclass(frozen=True)
class QueryMatch:
    issue_id: str
    file_name: str
    server: str
    tool: str
    category: str
    status: str
    title: str
    prevention_rule: str
    cosine_similarity: float


def _parse_inline_yaml_array(raw_value: str) -> tuple[str, ...]:
    trimmed = raw_value.strip()
    if trimmed.startswith("[") and trimmed.endswith("]"):
        inner = trimmed[1:-1].strip()
        if not inner:
            return ()
        parts = [p.strip().strip("\"'") for p in inner.split(",")]
        return tuple(p for p in parts if p)
    cleaned = trimmed.strip("\"'")
    return (cleaned,) if cleaned else ()


def _strip_markdown_noise(body: str) -> str:
    # Remove fenced verification command blocks that are boilerplate across every MCP-NNN doc
    cleaned = re.sub(
        r"##\s+Verification\s*/\s*Pre-Flight\s+Oracle.*?(?=\n##\s+|\Z)",
        " ",
        body,
        flags=re.IGNORECASE | re.DOTALL,
    )
    cleaned = re.sub(r"```.*?```", " ", cleaned, flags=re.DOTALL)
    cleaned = re.sub(r"[|`*_#>\[\]()]+", " ", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    return cleaned[:1600]


def parse_mcp_issue_file(file_path: Path) -> McpIssueDocument:
    raw = file_path.read_text(encoding="utf-8")
    normalized = raw.replace("\r\n", "\n")
    content_sha256 = hashlib.sha256(normalized.encode("utf-8")).hexdigest()

    match = _FRONTMATTER_RE.match(normalized)
    if not match:
        raise ValueError(f"Missing YAML frontmatter block in '{file_path.name}'.")

    yaml_block, body = match.group(1), match.group(2)
    meta: dict[str, Any] = {}
    for line in yaml_block.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        kv_match = re.match(r"^([a-zA-Z0-9_]+)\s*:\s*(.*)$", stripped)
        if not kv_match:
            continue
        key = kv_match.group(1).strip()
        val = kv_match.group(2).strip()
        if key in ("tags", "supersedes"):
            meta[key] = _parse_inline_yaml_array(val)
        elif key == "status":
            val_no_comment = re.sub(r"#.*$", "", val).strip().strip("\"'")
            meta[key] = val_no_comment
        else:
            # Strip trailing inline YAML comment when not quoted
            if val.startswith('"') and val.endswith('"') and len(val) >= 2:
                meta[key] = val[1:-1]
            elif val.startswith("'") and val.endswith("'") and len(val) >= 2:
                meta[key] = val[1:-1]
            else:
                meta[key] = re.sub(r"\s+#.*$", "", val).strip().strip("\"'")

    issue_id = str(meta.get("id", file_path.stem.split("-", 2)[0])).strip()
    return McpIssueDocument(
        issue_id=issue_id,
        file_name=file_path.name,
        server=str(meta.get("server", "")).strip(),
        tool=str(meta.get("tool", "")).strip(),
        category=str(meta.get("category", "")).strip(),
        status=str(meta.get("status", "open")).strip().lower(),
        title=str(meta.get("title", file_path.stem)).strip(),
        prevention_rule=str(meta.get("prevention_rule", "")).strip(),
        tags=tuple(meta.get("tags", ())),
        body_excerpt=_strip_markdown_noise(body),
        content_sha256=content_sha256,
    )


def load_mcp_issues(issues_dir: Path) -> list[McpIssueDocument]:
    if not issues_dir.is_dir():
        raise FileNotFoundError(
            f"MCP issues directory not found: '{issues_dir}'. "
            "Pass --issues-dir pointing to docs/reference/mcp-issues."
        )

    files = sorted(issues_dir.glob("MCP-*.md"), key=lambda p: p.name)
    docs: list[McpIssueDocument] = []
    for path in files:
        docs.append(parse_mcp_issue_file(path))
    return docs


def _extract_weighted_features(text: str, field_weight: float) -> Counter[str]:
    lowered = text.lower()
    raw_words = [w for w in _TOKEN_RE.findall(lowered) if w not in _STOPWORDS]
    features: Counter[str] = Counter()

    for idx, word in enumerate(raw_words):
        features[f"w:{word}"] += field_weight
        # Sub-word split on snake_case / kebab-case / dot paths for cross-style recall
        parts = [p for p in re.split(r"[_./:-]+", word) if len(p) >= 2 and p not in _STOPWORDS]
        if len(parts) > 1:
            for part in parts:
                features[f"sw:{part}"] += field_weight * 0.7

        if idx + 1 < len(raw_words):
            features[f"bg:{word}|{raw_words[idx + 1]}"] += field_weight * 0.85

        # Character 3..5-grams capture morphological variants and minor typos
        padded = f"<{word}>"
        if len(word) >= 4:
            for n in (3, 4, 5):
                if len(padded) >= n:
                    for pos in range(0, len(padded) - n + 1):
                        features[f"c{n}:{padded[pos : pos + n]}"] += field_weight * 0.25

    return features


def extract_document_features(doc: McpIssueDocument) -> Counter[str]:
    combined: Counter[str] = Counter()
    weighted_fields: Sequence[tuple[str, float]] = (
        (doc.title, 3.0),
        (f"{doc.server} {doc.tool} {doc.category}", 2.2),
        (" ".join(doc.tags), 1.6),
        (doc.prevention_rule, 2.0),
        (doc.body_excerpt, 1.0),
    )
    for field_text, weight in weighted_fields:
        if field_text:
            for feat, val in _extract_weighted_features(field_text, weight).items():
                combined[feat] += val
    return combined


def _hash_feature_bucket(feature: str, dim: int = EMBEDDING_DIM) -> tuple[int, float]:
    digest = hashlib.blake2b(feature.encode("utf-8"), digest_size=8).digest()
    bucket_u32, sign_u32 = struct.unpack("<II", digest)
    idx = bucket_u32 % dim
    sign = 1.0 if (sign_u32 & 1) == 0 else -1.0
    return idx, sign


class DeterministicTfIdfHasher:
    """Deterministic 384-d L2-normalized TF-IDF + char/word n-gram feature hasher."""

    def __init__(self, dim: int = EMBEDDING_DIM) -> None:
        self.dim = dim
        self.idf_by_feature: dict[str, float] = {}
        self.doc_count: int = 0

    def fit(self, feature_counters: Sequence[Counter[str]]) -> "DeterministicTfIdfHasher":
        self.doc_count = len(feature_counters)
        df: Counter[str] = Counter()
        for fc in feature_counters:
            for feat in fc.keys():
                df[feat] += 1

        n_docs = max(self.doc_count, 1)
        self.idf_by_feature = {
            feat: math.log((1.0 + n_docs) / (1.0 + count)) + 1.0
            for feat, count in df.items()
        }
        return self

    def transform_features(self, features: Counter[str]) -> list[float]:
        vec = [0.0] * self.dim
        default_idf = math.log((1.0 + max(self.doc_count, 1)) / 1.0) + 1.0
        for feat, raw_tf in features.items():
            if raw_tf <= 0.0:
                continue
            tf_weight = 1.0 + math.log(raw_tf)
            idf_weight = self.idf_by_feature.get(feat, default_idf)
            idx, sign = _hash_feature_bucket(feat, self.dim)
            vec[idx] += sign * tf_weight * idf_weight

        norm = math.sqrt(sum(v * v for v in vec))
        if norm > 0.0:
            inv_norm = 1.0 / norm
            vec = [v * inv_norm for v in vec]
        return vec

    def transform_query_text(self, query_text: str) -> list[float]:
        features = _extract_weighted_features(query_text, 2.0)
        return self.transform_features(features)


def cosine_similarity(vec_a: Sequence[float], vec_b: Sequence[float]) -> float:
    return float(sum(a * b for a, b in zip(vec_a, vec_b)))


def format_pgvector_literal(vec: Sequence[float]) -> str:
    return "[" + ",".join(f"{v:.6f}" for v in vec) + "]"


def find_duplicate_pairs(
    docs: Sequence[McpIssueDocument],
    embeddings: Sequence[Sequence[float]],
    threshold: float = 0.72,
    top_k: int = 25,
    include_superseded: bool = False,
) -> list[DuplicatePair]:
    active_indices = [
        idx
        for idx, doc in enumerate(docs)
        if include_superseded or doc.status != "superseded"
    ]

    pairs: list[DuplicatePair] = []
    n_active = len(active_indices)
    for i_pos in range(n_active):
        idx_a = active_indices[i_pos]
        vec_a = embeddings[idx_a]
        doc_a = docs[idx_a]
        for j_pos in range(i_pos + 1, n_active):
            idx_b = active_indices[j_pos]
            sim = cosine_similarity(vec_a, embeddings[idx_b])
            if sim >= threshold:
                doc_b = docs[idx_b]
                pairs.append(
                    DuplicatePair(
                        issue_id_a=doc_a.issue_id,
                        issue_id_b=doc_b.issue_id,
                        file_a=doc_a.file_name,
                        file_b=doc_b.file_name,
                        server_a=doc_a.server,
                        tool_a=doc_a.tool,
                        server_b=doc_b.server,
                        tool_b=doc_b.tool,
                        status_a=doc_a.status,
                        status_b=doc_b.status,
                        title_a=doc_a.title,
                        title_b=doc_b.title,
                        cosine_similarity=round(sim, 6),
                    )
                )

    pairs.sort(key=lambda p: (-p.cosine_similarity, p.issue_id_a, p.issue_id_b))
    return pairs[: max(top_k, 1)]


def search_similar_issues(
    query_vec: Sequence[float],
    docs: Sequence[McpIssueDocument],
    embeddings: Sequence[Sequence[float]],
    threshold: float = 0.20,
    top_k: int = 10,
    include_superseded: bool = False,
) -> list[QueryMatch]:
    matches: list[QueryMatch] = []
    for doc, vec in zip(docs, embeddings):
        if not include_superseded and doc.status == "superseded":
            continue
        sim = cosine_similarity(query_vec, vec)
        if sim >= threshold:
            matches.append(
                QueryMatch(
                    issue_id=doc.issue_id,
                    file_name=doc.file_name,
                    server=doc.server,
                    tool=doc.tool,
                    category=doc.category,
                    status=doc.status,
                    title=doc.title,
                    prevention_rule=doc.prevention_rule,
                    cosine_similarity=round(sim, 6),
                )
            )
    matches.sort(key=lambda m: (-m.cosine_similarity, m.issue_id))
    return matches[: max(top_k, 1)]


def _sql_quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def emit_upsert_sql(
    docs: Sequence[McpIssueDocument],
    embeddings: Sequence[Sequence[float]],
) -> str:
    lines = [
        "-- Auto-generated by scripts/qa/mcp_issue_semantic_dedup.py",
        f"-- Vectorizer: {VECTORIZER_VERSION} (dim={EMBEDDING_DIM}, docs={len(docs)})",
        "BEGIN;",
    ]
    for doc, vec in zip(docs, embeddings):
        vec_lit = format_pgvector_literal(vec)
        lines.append(
            "SELECT upsert_mcp_issue_embedding("
            f"{_sql_quote(doc.issue_id)}, "
            f"{_sql_quote(doc.file_name)}, "
            f"{_sql_quote(doc.server)}, "
            f"{_sql_quote(doc.tool)}, "
            f"{_sql_quote(doc.category)}, "
            f"{_sql_quote(doc.status)}, "
            f"{_sql_quote(doc.title)}, "
            f"{_sql_quote(doc.prevention_rule)}, "
            f"{_sql_quote(doc.content_sha256)}, "
            f"{_sql_quote(VECTORIZER_VERSION)}, "
            f"{_sql_quote(vec_lit)}::vector(384));"
        )
    lines.append("COMMIT;")
    return "\n".join(lines) + "\n"


def _connect_pgvector(dsn: str) -> Any:
    for mod_name in ("psycopg", "psycopg2"):
        try:
            mod = importlib.import_module(mod_name)
            return mod.connect(dsn)
        except ImportError:
            continue
    raise RuntimeError(
        "PostgreSQL pgvector mode requested (--pgvector-dsn) but neither 'psycopg' nor 'psycopg2' is installed. "
        "Install with: python -m pip install 'psycopg[binary]'"
    )


def sync_to_pgvector(
    dsn: str,
    docs: Sequence[McpIssueDocument],
    embeddings: Sequence[Sequence[float]],
    query_vec: Sequence[float] | None,
    threshold: float,
    top_k: int,
    include_superseded: bool,
) -> dict[str, Any]:
    conn = _connect_pgvector(dsn)
    try:
        with conn:
            with conn.cursor() as cur:
                for doc, vec in zip(docs, embeddings):
                    cur.execute(
                        "SELECT upsert_mcp_issue_embedding(%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::vector(384))",
                        (
                            doc.issue_id,
                            doc.file_name,
                            doc.server,
                            doc.tool,
                            doc.category,
                            doc.status,
                            doc.title,
                            doc.prevention_rule,
                            doc.content_sha256,
                            VECTORIZER_VERSION,
                            format_pgvector_literal(vec),
                        ),
                    )
                if query_vec is not None:
                    cur.execute(
                        "SELECT issue_id, file_name, server, tool, category, status, title, prevention_rule, cosine_similarity "
                        "FROM find_similar_mcp_issues(%s::vector(384), NULL, %s, %s, %s)",
                        (format_pgvector_literal(query_vec), threshold, top_k, include_superseded),
                    )
                    rows = cur.fetchall()
                    return {
                        "upserted": len(docs),
                        "pgvector_query_hits": [
                            {
                                "issue_id": r[0],
                                "file_name": r[1],
                                "server": r[2],
                                "tool": r[3],
                                "category": r[4],
                                "status": r[5],
                                "title": r[6],
                                "prevention_rule": r[7],
                                "cosine_similarity": round(float(r[8]), 6),
                            }
                            for r in rows
                        ],
                    }

                cur.execute(
                    "SELECT issue_id_a, issue_id_b, server_a, tool_a, title_a, title_b, cosine_similarity "
                    "FROM find_duplicate_mcp_issue_pairs(%s, %s, %s)",
                    (threshold, top_k, include_superseded),
                )
                rows = cur.fetchall()
                return {
                    "upserted": len(docs),
                    "pgvector_duplicate_pairs": [
                        {
                            "issue_id_a": r[0],
                            "issue_id_b": r[1],
                            "server_a": r[2],
                            "tool_a": r[3],
                            "title_a": r[4],
                            "title_b": r[5],
                            "cosine_similarity": round(float(r[6]), 6),
                        }
                        for r in rows
                    ],
                }
    finally:
        conn.close()


def run_dedup_pipeline(
    issues_dir: Path,
    threshold: float = 0.72,
    top_k: int = 25,
    include_superseded: bool = False,
    query: str = "",
    pgvector_dsn: str = "",
    emit_sql_path: Path | None = None,
) -> dict[str, Any]:
    t0 = time.perf_counter()
    docs = load_mcp_issues(issues_dir)
    feature_counters = [extract_document_features(doc) for doc in docs]
    vectorizer = DeterministicTfIdfHasher(dim=EMBEDDING_DIM).fit(feature_counters)
    embeddings = [vectorizer.transform_features(fc) for fc in feature_counters]

    active_count = sum(1 for d in docs if d.status != "superseded")
    superseded_count = len(docs) - active_count
    avg_norm = (
        sum(math.sqrt(sum(v * v for v in emb)) for emb in embeddings) / len(embeddings)
        if embeddings
        else 0.0
    )

    if emit_sql_path is not None:
        sql_text = emit_upsert_sql(docs, embeddings)
        emit_sql_path.parent.mkdir(parents=True, exist_ok=True)
        emit_sql_path.write_text(sql_text, encoding="utf-8")

    query_vec: list[float] | None = None
    query_matches: list[QueryMatch] = []
    duplicate_pairs: list[DuplicatePair] = []

    if query.strip():
        query_vec = vectorizer.transform_query_text(query.strip())
        query_matches = search_similar_issues(
            query_vec=query_vec,
            docs=docs,
            embeddings=embeddings,
            threshold=threshold,
            top_k=top_k,
            include_superseded=include_superseded,
        )
    else:
        duplicate_pairs = find_duplicate_pairs(
            docs=docs,
            embeddings=embeddings,
            threshold=threshold,
            top_k=top_k,
            include_superseded=include_superseded,
        )

    pgvector_result: dict[str, Any] | None = None
    if pgvector_dsn.strip():
        pgvector_result = sync_to_pgvector(
            dsn=pgvector_dsn.strip(),
            docs=docs,
            embeddings=embeddings,
            query_vec=query_vec,
            threshold=threshold,
            top_k=top_k,
            include_superseded=include_superseded,
        )

    elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 2)
    return {
        "vectorizer_version": VECTORIZER_VERSION,
        "embedding_dim": EMBEDDING_DIM,
        "issues_dir": str(issues_dir),
        "total_issues": len(docs),
        "active_issues": active_count,
        "superseded_issues": superseded_count,
        "include_superseded": include_superseded,
        "threshold": threshold,
        "top_k": top_k,
        "avg_l2_norm": round(avg_norm, 6),
        "elapsed_ms": elapsed_ms,
        "query": query.strip() if query.strip() else None,
        "duplicate_pair_count": len(duplicate_pairs),
        "duplicate_pairs": [dataclasses.asdict(p) for p in duplicate_pairs],
        "query_match_count": len(query_matches),
        "query_matches": [dataclasses.asdict(m) for m in query_matches],
        "pgvector_sync": pgvector_result,
    }


def _build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Deterministic 384-d TF-IDF/n-gram semantic deduplicator for Kongor MCP-NNN issues."
    )
    parser.add_argument(
        "--issues-dir",
        type=Path,
        default=Path("docs/reference/mcp-issues"),
        help="Path to MCP-*.md issue directory (default: docs/reference/mcp-issues).",
    )
    parser.add_argument(
        "--threshold",
        type=float,
        default=0.55,
        help="Minimum cosine similarity threshold (default: 0.55 for pair mode; pass lower e.g. 0.25 for ad-hoc --query).",
    )
    parser.add_argument(
        "--top-k",
        type=int,
        default=25,
        help="Maximum number of duplicate pairs or query matches to return (default: 25).",
    )
    parser.add_argument(
        "--query",
        type=str,
        default="",
        help="Optional ad-hoc issue description/error text to search for nearest existing MCP-NNN issues.",
    )
    parser.add_argument(
        "--include-superseded",
        action="store_true",
        help="Include issues with status=superseded in pair/query comparisons.",
    )
    parser.add_argument(
        "--pgvector-dsn",
        type=str,
        default="",
        help="Optional PostgreSQL DSN to upsert 384-d embeddings and run pgvector HNSW similarity search.",
    )
    parser.add_argument(
        "--emit-sql",
        type=Path,
        default=None,
        help="Optional file path to export idempotent pgvector upsert SQL statements.",
    )
    parser.add_argument(
        "--json-out",
        type=Path,
        default=None,
        help="Optional path to write full JSON summary output.",
    )
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="Suppress human-readable stdout summary table.",
    )
    return parser


def main(argv: Iterable[str] | None = None) -> int:
    parser = _build_arg_parser()
    args = parser.parse_args(list(argv) if argv is not None else None)

    summary = run_dedup_pipeline(
        issues_dir=args.issues_dir,
        threshold=args.threshold,
        top_k=args.top_k,
        include_superseded=args.include_superseded,
        query=args.query,
        pgvector_dsn=args.pgvector_dsn,
        emit_sql_path=args.emit_sql,
    )

    if args.json_out is not None:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    if not args.quiet:
        print(
            f"[MCP-SEMANTIC-DEDUP] Vectorized {summary['total_issues']} MCP issue(s) "
            f"({summary['active_issues']} active, {summary['superseded_issues']} superseded) "
            f"into {summary['embedding_dim']}-d L2 vectors (avg_norm={summary['avg_l2_norm']}) "
            f"in {summary['elapsed_ms']}ms."
        )
        if summary["query"]:
            print(
                f"  Query '{summary['query']}' -> {summary['query_match_count']} match(es) "
                f"(threshold >= {summary['threshold']:.2f}):"
            )
            for hit in summary["query_matches"]:
                print(
                    f"    [{hit['cosine_similarity']:.3f}] {hit['issue_id']} "
                    f"({hit['server']}/{hit['tool']} | {hit['status']}) {hit['title']}"
                )
        else:
            print(
                f"  Top {summary['duplicate_pair_count']} semantic near-duplicate pair(s) "
                f"(cosine >= {summary['threshold']:.2f}, include_superseded={summary['include_superseded']}):"
            )
            for pair in summary["duplicate_pairs"]:
                print(
                    f"    [{pair['cosine_similarity']:.3f}] {pair['issue_id_a']} ({pair['server_a']}/{pair['tool_a']}) "
                    f"<-> {pair['issue_id_b']} ({pair['server_b']}/{pair['tool_b']})"
                )
                print(f"       A: {pair['title_a']}")
                print(f"       B: {pair['title_b']}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
