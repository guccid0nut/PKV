-- SPDX-License-Identifier: AGPL-3.0-or-later
-- SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
-- Project KONGOR V (https://github.com/guccidonut/PKV)
-- ============================================================================
-- Project Kongor Wave 4 Track 4: pgvector Semantic Deduplication for MCP-NNN
--
-- Purpose:
--   Stores deterministic 384-dimensional L2-normalized semantic embeddings for
--   every curated MCP issue ledger document (docs/reference/mcp-issues/MCP-*.md)
--   with an HNSW cosine-distance index for sub-millisecond near-duplicate
--   detection and pre-creation similarity search.
--
-- Compatibility:
--   * PostgreSQL 16+ / 17+ with pgvector >= 0.5.0 (HNSW vector_cosine_ops)
--   * Fully idempotent (safe to mount in /docker-entrypoint-initdb.d/ or re-run)
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS mcp_issue_embeddings (
    issue_id           TEXT PRIMARY KEY,
    file_name          TEXT NOT NULL,
    server             TEXT NOT NULL DEFAULT '',
    tool               TEXT NOT NULL DEFAULT '',
    category           TEXT NOT NULL DEFAULT '',
    status             TEXT NOT NULL DEFAULT 'open',
    title              TEXT NOT NULL,
    prevention_rule    TEXT NOT NULL DEFAULT '',
    content_sha256     CHAR(64) NOT NULL,
    vectorizer_version TEXT NOT NULL DEFAULT 'kongor-tfidf-ngram-384-v1',
    embedding          vector(384) NOT NULL,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- HNSW cosine index for fast approximate nearest-neighbor lookup over 384-d unit vectors
CREATE INDEX IF NOT EXISTS ix_mcp_issue_embeddings_hnsw_cosine
    ON mcp_issue_embeddings
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- Relational filter index for scoped server/tool/status queries
CREATE INDEX IF NOT EXISTS ix_mcp_issue_embeddings_server_tool_status
    ON mcp_issue_embeddings (server, tool, status);

-- Idempotent upsert helper for scripts/qa/mcp_issue_semantic_dedup.py --pgvector-dsn
CREATE OR REPLACE FUNCTION upsert_mcp_issue_embedding(
    p_issue_id           TEXT,
    p_file_name          TEXT,
    p_server             TEXT,
    p_tool               TEXT,
    p_category           TEXT,
    p_status             TEXT,
    p_title              TEXT,
    p_prevention_rule    TEXT,
    p_content_sha256     CHAR(64),
    p_vectorizer_version TEXT,
    p_embedding          vector(384)
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO mcp_issue_embeddings (
        issue_id,
        file_name,
        server,
        tool,
        category,
        status,
        title,
        prevention_rule,
        content_sha256,
        vectorizer_version,
        embedding,
        updated_at
    )
    VALUES (
        p_issue_id,
        p_file_name,
        COALESCE(p_server, ''),
        COALESCE(p_tool, ''),
        COALESCE(p_category, ''),
        COALESCE(p_status, 'open'),
        p_title,
        COALESCE(p_prevention_rule, ''),
        p_content_sha256,
        COALESCE(p_vectorizer_version, 'kongor-tfidf-ngram-384-v1'),
        p_embedding,
        NOW()
    )
    ON CONFLICT (issue_id) DO UPDATE
    SET file_name          = EXCLUDED.file_name,
        server             = EXCLUDED.server,
        tool               = EXCLUDED.tool,
        category           = EXCLUDED.category,
        status             = EXCLUDED.status,
        title              = EXCLUDED.title,
        prevention_rule    = EXCLUDED.prevention_rule,
        content_sha256     = EXCLUDED.content_sha256,
        vectorizer_version = EXCLUDED.vectorizer_version,
        embedding          = EXCLUDED.embedding,
        updated_at         = NOW();
END
$$;

-- Top-K cosine similarity search helper (cosine_similarity = 1.0 - cosine_distance)
CREATE OR REPLACE FUNCTION find_similar_mcp_issues(
    p_query_embedding    vector(384),
    p_exclude_issue_id   TEXT DEFAULT NULL,
    p_min_similarity     DOUBLE PRECISION DEFAULT 0.72,
    p_limit              INTEGER DEFAULT 10,
    p_include_superseded BOOLEAN DEFAULT FALSE
)
RETURNS TABLE (
    issue_id          TEXT,
    file_name         TEXT,
    server            TEXT,
    tool              TEXT,
    category          TEXT,
    status            TEXT,
    title             TEXT,
    prevention_rule   TEXT,
    cosine_similarity DOUBLE PRECISION
)
LANGUAGE sql
STABLE
AS $$
    SELECT
        e.issue_id,
        e.file_name,
        e.server,
        e.tool,
        e.category,
        e.status,
        e.title,
        e.prevention_rule,
        (1.0 - (e.embedding <=> p_query_embedding))::DOUBLE PRECISION AS cosine_similarity
    FROM mcp_issue_embeddings AS e
    WHERE (p_exclude_issue_id IS NULL OR e.issue_id <> p_exclude_issue_id)
      AND (p_include_superseded OR e.status <> 'superseded')
      AND (1.0 - (e.embedding <=> p_query_embedding)) >= p_min_similarity
    ORDER BY e.embedding <=> p_query_embedding ASC
    LIMIT GREATEST(p_limit, 1);
$$;

-- All-pairs semantic near-duplicate finder for periodic MCP-NNN KB hygiene sweeps
CREATE OR REPLACE FUNCTION find_duplicate_mcp_issue_pairs(
    p_min_similarity     DOUBLE PRECISION DEFAULT 0.78,
    p_limit              INTEGER DEFAULT 25,
    p_include_superseded BOOLEAN DEFAULT FALSE
)
RETURNS TABLE (
    issue_id_a        TEXT,
    issue_id_b        TEXT,
    server_a          TEXT,
    tool_a            TEXT,
    title_a           TEXT,
    title_b           TEXT,
    cosine_similarity DOUBLE PRECISION
)
LANGUAGE sql
STABLE
AS $$
    SELECT
        a.issue_id AS issue_id_a,
        b.issue_id AS issue_id_b,
        a.server   AS server_a,
        a.tool     AS tool_a,
        a.title    AS title_a,
        b.title    AS title_b,
        (1.0 - (a.embedding <=> b.embedding))::DOUBLE PRECISION AS cosine_similarity
    FROM mcp_issue_embeddings AS a
    JOIN mcp_issue_embeddings AS b
      ON a.issue_id < b.issue_id
    WHERE (p_include_superseded OR (a.status <> 'superseded' AND b.status <> 'superseded'))
      AND (1.0 - (a.embedding <=> b.embedding)) >= p_min_similarity
    ORDER BY cosine_similarity DESC, a.issue_id ASC, b.issue_id ASC
    LIMIT GREATEST(p_limit, 1);
$$;
