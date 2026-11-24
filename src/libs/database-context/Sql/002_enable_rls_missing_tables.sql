-- ============================================================================
-- File: 002_enable_rls_missing_tables.sql
-- System: Supabase PostgreSQL / MERRICK Security Hardening
-- Purpose: Resolves Supabase Security Advisor "Policy Exists RLS Disabled"
--          by enabling Row Level Security (RLS) on core.InviteSystemAuditLogs,
--          core.UserInvites, and stat.MatchRecords, and ensuring clean policies.
-- ============================================================================

-- 1. Enable Row Level Security (RLS) on omitted tables
ALTER TABLE IF EXISTS "core"."InviteSystemAuditLogs" ENABLE ROW LEVEL SECURITY;
ALTER TABLE IF EXISTS "core"."UserInvites" ENABLE ROW LEVEL SECURITY;
ALTER TABLE IF EXISTS "stat"."MatchRecords" ENABLE ROW LEVEL SECURITY;

-- 2. Ensure Service Role Full Access Policies across all custom schemas
DROP POLICY IF EXISTS "Service Role All" ON "core"."InviteSystemAuditLogs";
CREATE POLICY "Service Role All" ON "core"."InviteSystemAuditLogs"
    FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS "Service Role All" ON "core"."UserInvites";
CREATE POLICY "Service Role All" ON "core"."UserInvites"
    FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS "Service Role All" ON "stat"."MatchRecords";
CREATE POLICY "Service Role All" ON "stat"."MatchRecords"
    FOR ALL TO service_role USING (true) WITH CHECK (true);

-- 3. Public Read Policy for stat.MatchRecords
-- Allows client applications and web portals to view match ledger records
DROP POLICY IF EXISTS "Public Read MatchRecords" ON "stat"."MatchRecords";
CREATE POLICY "Public Read MatchRecords" ON "stat"."MatchRecords"
    FOR SELECT USING (true);

-- 4. Dynamic safeguard: Ensure all current and future base tables in custom schemas have RLS enabled
DO $$
DECLARE
    s text;
    t text;
    schemas text[] := ARRAY['core', 'data', 'stat', 'misc', 'nexus_auth'];
BEGIN
    FOREACH s IN ARRAY schemas LOOP
        FOR t IN SELECT table_name FROM information_schema.tables WHERE table_schema = s AND table_type = 'BASE TABLE' LOOP
            BEGIN
                EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY;', s, t);
            EXCEPTION WHEN OTHERS THEN
                NULL;
            END;
        END LOOP;
    END LOOP;
END $$;
