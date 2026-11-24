# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

import re
import unittest
from pathlib import Path

class TestDatabasePersistenceAdversarial(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.project_root = Path(__file__).resolve().parents[2]
        cls.db_init_path = cls.project_root / "src" / "libs" / "database-context" / "Services" / "DatabaseInitializer.cs"
        cls.merrick_context_path = cls.project_root / "src" / "libs" / "database-context" / "Persistence" / "MerrickContext.cs"
        cls.store_item_entity_path = cls.project_root / "src" / "libs" / "database-context" / "Entities" / "Store" / "StoreItem.cs"
        cls.seed_data_path = cls.project_root / "src" / "libs" / "database-context" / "Handlers" / "SeedDataHandlers.cs"

    def test_database_initializer_syntax_error_vulnerability(self):
        """
        Adversarial test: Verifies that 'ID prisoners' typo is completely eliminated
        and StoreItems / EconomyTransactions DDL are cleanly executed.
        """
        self.assertTrue(self.db_init_path.exists(), f"DatabaseInitializer.cs must exist at {self.db_init_path}")
        code = self.db_init_path.read_text(encoding="utf-8")

        # 1. Verify absence of corrupted token
        self.assertNotIn("ID prisoners", code, "DatabaseInitializer.cs must not contain 'ID prisoners' syntax error")
        self.assertNotIn("Fallback without invalid typo", code, "DatabaseInitializer.cs must not rely on flawed catch blocks")

        # 2. Verify presence of clean table DDL
        self.assertIn('""data"".""StoreItems""', code, "DatabaseInitializer.cs must define StoreItems table DDL")
        self.assertIn('""core"".""EconomyTransactions""', code, "DatabaseInitializer.cs must define EconomyTransactions table DDL")
        self.assertIn('""UserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE CASCADE', code)

    def test_rls_table_list_completeness(self):
        """
        Adversarial test: Verifies that all 32 custom tables are listed in tables_to_rls.
        """
        code = self.db_init_path.read_text(encoding="utf-8")
        rls_match = re.search(r'tables_to_rls text\[\]\[\] := ARRAY\[(.*?)\];', code, re.DOTALL)
        self.assertIsNotNone(rls_match, "Must find tables_to_rls array in DatabaseInitializer.cs")
        rls_content = rls_match.group(1)

        expected_tables = [
            ('core', 'Accounts'), ('core', 'Clans'), ('core', 'HostApplications'),
            ('core', 'StreamerApplications'), ('core', 'SystemTelemetryMetrics'),
            ('core', 'Users'), ('core', 'EconomyTransactions'), ('core', 'Friendships'),
            ('core', 'UserInventory'), ('core', 'AccountSessions'),
            ('data', 'AccountStatistics'), ('data', 'MatchmakingQueueWaitEntries'),
            ('data', 'StoreItems'),
            ('stat', 'Masteries'), ('stat', 'MatchStatistics'), ('stat', 'PlayerStatistics'),
            ('misc', 'HeroDetails'), ('misc', 'HeroDifficultyVotes'), ('misc', 'HeroGuides'),
            ('misc', 'HeroItems'), ('misc', 'MatchEvidence'), ('misc', 'Notifications'),
            ('misc', 'PlayerReports'), ('misc', 'RedeemableCodes'), ('misc', 'UserWebRewards'),
            ('misc', 'LoreArcs'), ('misc', 'LoreComics'), ('misc', 'LorePages'),
            ('misc', 'LoreCredits'), ('misc', 'LoreRatings'),
            ('nexus_auth', 'Roles'), ('nexus_auth', 'Tokens')
        ]

        self.assertEqual(len(expected_tables), 32)
        for schema, table in expected_tables:
            entry = f"['{schema}', '{table}']"
            self.assertIn(entry, rls_content, f"tables_to_rls must contain {entry}")

    def test_all_32_tables_in_merrick_context(self):
        """
        Adversarial test: Verifies that MerrickContext.cs configures schemas for all 32 custom tables.
        """
        code = self.merrick_context_path.read_text(encoding="utf-8")
        expected_entities = [
            "Account", "Clan", "HostApplication", "StreamerApplication", "SystemTelemetryMetrics",
            "User", "EconomyTransaction", "Friendship", "UserInventory", "AccountSession",
            "AccountStatistics", "MatchmakingQueueWaitEntry", "StoreItem",
            "Mastery", "MatchStatistics", "PlayerStatistics",
            "HeroDetail", "HeroDifficultyVote", "HeroGuide", "HeroItem", "MatchEvidence",
            "Notification", "PlayerReport", "RedeemableCode", "UserWebReward",
            "LoreArc", "LoreComic", "LorePage", "LoreCredit", "LoreRating",
            "Role", "Token"
        ]
        self.assertEqual(len(expected_entities), 32)
        for entity in expected_entities:
            pattern = rf'builder\.Entity<{entity}>\(\)\.ToTable\('
            self.assertIsNotNone(re.search(pattern, code), f"MerrickContext must configure ToTable for {entity}")

if __name__ == '__main__':
    unittest.main()
