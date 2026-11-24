-- ============================================================================
-- File: 001_setup_duckdb_fdw.sql
-- System: Supabase PostgreSQL & DuckDB Foreign Data Wrapper Lakehouse
-- Purpose: Mounts Cloudflare R2 / Supabase Storage Parquet match archives
--          into PostgreSQL via duckdb_wrapper to eliminate DB bloat.
-- ============================================================================

-- 1. Enable Wrappers extension
CREATE EXTENSION IF NOT EXISTS wrappers WITH SCHEMA extensions;

-- 2. Enable DuckDB Foreign Data Wrapper handler
CREATE FOREIGN DATA WRAPPER duckdb_wrapper
  HANDLER duckdb_fdw_handler
  VALIDATOR duckdb_fdw_validator;

-- 3. Create foreign server pointing to Supabase Storage S3 / Cloudflare R2
-- Note: Replace vault_key_id and vault_secret with your Vault secret IDs,
-- or specify key_id and secret directly if configuring outside Vault.
CREATE SERVER IF NOT EXISTS duckdb_server
  FOREIGN DATA WRAPPER duckdb_wrapper
  OPTIONS (
    type 's3',
    region 'us-east-2',
    url_style 'path',
    use_ssl 'true',
    endpoint 'ozobzkxiihvpcvokwzbm.supabase.co/storage/v1/s3'
  );

-- 4. Create dedicated schema for foreign analytics tables
CREATE SCHEMA IF NOT EXISTS duckdb;

-- 5. Define foreign table for match combat events
-- Directly maps to COMPEL MatchCombatEventRecord schema with zero PostgreSQL storage overhead
CREATE FOREIGN TABLE IF NOT EXISTS duckdb.match_combat_events (
  match_id BIGINT,
  match_time_ms INTEGER,
  tick INTEGER,
  event_type SMALLINT,
  source_player_index SMALLINT,
  source_hero_id SMALLINT,
  target_player_index SMALLINT,
  target_hero_id SMALLINT,
  ability_or_item_id INTEGER,
  event_value REAL,
  position_x REAL,
  position_y REAL,
  position_z REAL
)
  SERVER duckdb_server
  OPTIONS (
    table '''s3://match-analytics/*.parquet'''
  );

-- 6. Analytical view: Hero combat summary (Kills, Assists, Deaths, Damage) with query pushdown
CREATE OR REPLACE VIEW duckdb.v_hero_combat_summary AS
SELECT 
  source_hero_id AS hero_id,
  COUNT(*) FILTER (WHERE event_type = 0) AS total_kills,
  COUNT(*) FILTER (WHERE event_type = 1) AS total_assists,
  COUNT(*) FILTER (WHERE event_type = 2) AS total_deaths,
  COALESCE(SUM(event_value) FILTER (WHERE event_type IN (0, 3)), 0.0) AS total_damage_dealt,
  COUNT(DISTINCT match_id) AS matches_played
FROM duckdb.match_combat_events
GROUP BY source_hero_id;
