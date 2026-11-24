#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Test Suite Modernization Verification Harness
==============================================
Authoritative Monorepo Naming & Regression Verification Suite for ProjectKongor.

Validates all 4 Modernization Tiers across C# and Python test projects:
  - Tier 1: 0 test methods, classes, or files contain obsolete phase/milestone prefixes.
  - Tier 2: All test methods follow [UnitOfWork]_[Scenario]_[ExpectedBehavior] (or test_* for Python).
  - Tier 3: In-engine domain concepts (GamePhase == 3, Gateway 1-4, etc.) are strictly preserved.
  - Tier 4: Manual test gating guards remain intact (NexusTestEnvironment.IsManualTestEnabled).

Execution Modes:
  CLI Auditor Mode:
    python tests/verify_test_suite_modernization.py [--audit] [--json report.json] [--markdown report.md]
  Unittest Runner Mode:
    python -m unittest tests/verify_test_suite_modernization.py
"""

from __future__ import annotations

import argparse
import ast
from dataclasses import asdict, dataclass, field
import json
import os
from pathlib import Path
import re
import sys
from typing import Any
import unittest

# -----------------------------------------------------------------------------
# Configuration & Domain Invariants
# -----------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
TESTS_ROOT = REPO_ROOT / "tests"

# Directories ignored from test suite naming evaluation
IGNORED_DIRS = {
    "bin",
    "obj",
    ".vs",
    "__pycache__",
    ".git",
    ".agents",
    "userdataKONGOR",
    "output",
    "scratch",
}

# Helper or infrastructure files excluded from test method naming conventions
EXCLUDED_FILES = {
    "Directory.Build.props",
    "xunit.runner.json",
    "tools_schema.json",
    "NexusTestEnvironment.cs",
    "astryon_test_common.py",
    "conftest.py",
    "__init__.py",
    "verify_test_suite_modernization.py",
}

# -----------------------------------------------------------------------------
# Tier 1 & Tier 2: Regular Expressions for Obsolete Markers & Naming Rules
# -----------------------------------------------------------------------------

# C# Obsolete Marker Patterns
CS_FILE_OBSOLETE_PAT = re.compile(
    r"^(?:Phase\d+|Tier\d+|Milestone\d+|M\d{1,2}(?=[A-Z_])|Feature\d{1,2}|F\d{2}|Hotfix\d*|TaoPhase\d+|McpPhase\d+)",
    re.IGNORECASE,
)
CS_CLASS_OBSOLETE_PAT = re.compile(
    r"(?:Phase\d+|Tier\d+|Milestone\d+|^M\d{1,2}[A-Z]|Feature\d{1,2}|F\d{2}|Hotfix\d*|TaoPhase\d+|McpPhase\d+|M\d+Challenger)",
    re.IGNORECASE,
)
CS_METHOD_OBSOLETE_PAT = re.compile(
    r"^(?:Phase\d+_|Tier\d+_|F\d{2}_|Feature\d{1,2}_|M\d{1,2}_|M\d+M\d+_|Milestone\d+_|Hotfix\d*_|Scenario\d{1,2}_|Scenario[A-Z]_|Remediation_Item\d+_|Challenge\d*_|Challenger_)",
    re.IGNORECASE,
)

# Python Obsolete Marker Patterns
PY_FILE_OBSOLETE_PAT = re.compile(
    r"(?:phase\d+|tier\d+|milestone\d+|^m\d+_|_m\d+(?:_|\.)|challenger_m\d+|_p4_)",
    re.IGNORECASE,
)
PY_CLASS_OBSOLETE_PAT = re.compile(
    r"(?:Phase\d+|Tier\d+|Milestone\d+|^TestM\d+|TestFeature\d+)",
    re.IGNORECASE,
)
PY_METHOD_OBSOLETE_PAT = re.compile(
    r"^test_(?:f\d{1,2}_|f\d{2}_\d{2}_|phase\d+_|tier\d+_|m\d+_|milestone\d+_|\d{2}_|scenario\d+_|scenario[a-z]_)",
    re.IGNORECASE,
)

# Genuine in-engine domain concepts and architectural patterns that MUST NOT be flagged as obsolete
DOMAIN_SAFELIST_PAT = re.compile(
    r"(?:GamePhase|DynamicPhase|ExtractGamePhase|ActivePhase3|NonPhase3|Phase3ActiveMatch|"
    r"TwoTierBackupRotation|TieredPortDiscovery|PrebakedVfsResolution|Gateway[1-4]|StagingGateway|"
    r"TwelveSections|twelve_sections)",
    re.IGNORECASE,
)

# C# Method Extraction Regex & Parser
CS_FACT_THEORY_REGEX = re.compile(r"\[\s*(?:Fact|Theory)(?:\([^)]*\))?\s*\]")
CS_METHOD_SIG_REGEX = re.compile(
    r"public\s+(?:(?:async|unsafe|static)\s+)*(?:Task(?:<[^>]+>)?|void)\s+([A-Za-z0-9_]+)\s*\(",
    re.MULTILINE,
)
CS_TEST_METHOD_REGEX = CS_METHOD_SIG_REGEX


def extract_cs_test_methods(content: str) -> list[tuple[str, int, str]]:
    """
    Extracts all test methods (methods decorated with [Fact] or [Theory]) from C# source content.
    Handles:
      - Line comments (//) and block comments (/* */) between attributes and method signatures
      - Brackets and braces inside attribute string arguments (e.g. [InlineData("...[...]...")])
      - Modifiers: async, unsafe, static
      - Multi-line method signatures and multi-attribute stacks
    Returns:
      List of (method_name, line_number, snippet) tuples
    """
    results: list[tuple[str, int, str]] = []
    lines = content.splitlines()

    for match in CS_FACT_THEORY_REGEX.finditer(content):
        start_pos = match.end()
        # Look ahead for method signature within the declaration block
        sub = content[start_pos : start_pos + 16000]
        m = CS_METHOD_SIG_REGEX.search(sub)
        if m:
            method_name = m.group(1)
            abs_pos = start_pos + m.start()
            line_no = content[:abs_pos].count("\n") + 1
            snippet = lines[line_no - 1].strip() if line_no <= len(lines) else ""
            results.append((method_name, line_no, snippet))

    return results


CS_CLASS_REGEX = re.compile(
    r"(?:public\s+(?:sealed\s+)?)class\s+([A-Za-z0-9_]+)",
    re.MULTILINE,
)

# -----------------------------------------------------------------------------
# Data Models
# -----------------------------------------------------------------------------

@dataclass
class Violation:
    tier: int
    category: str  # 'file', 'class', 'method'
    language: str  # 'csharp', 'python'
    file_path: str
    line_number: int
    entity_name: str
    reason: str
    snippet: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class DomainConceptAudit:
    name: str
    description: str
    expected_minimum: int
    found_count: int
    sample_files: list[str] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        return self.found_count >= self.expected_minimum


@dataclass
class GatingGuardAudit:
    metric: str
    description: str
    expected: str
    actual: str
    passed: bool


@dataclass
class ModernizationReport:
    total_files_scanned: int = 0
    total_cs_files: int = 0
    total_py_files: int = 0
    total_cs_methods: int = 0
    total_py_methods: int = 0
    tier1_violations: list[Violation] = field(default_factory=list)
    tier2_violations: list[Violation] = field(default_factory=list)
    tier3_audits: list[DomainConceptAudit] = field(default_factory=list)
    tier4_audits: list[GatingGuardAudit] = field(default_factory=list)

    @property
    def tier1_passed(self) -> bool:
        return len(self.tier1_violations) == 0

    @property
    def tier2_passed(self) -> bool:
        return len(self.tier2_violations) == 0

    @property
    def tier3_passed(self) -> bool:
        return all(a.passed for a in self.tier3_audits)

    @property
    def tier4_passed(self) -> bool:
        return all(a.passed for a in self.tier4_audits)

    @property
    def all_passed(self) -> bool:
        return self.tier1_passed and self.tier2_passed and self.tier3_passed and self.tier4_passed

    def to_dict(self) -> dict[str, Any]:
        return {
            "summary": {
                "all_passed": self.all_passed,
                "tier1_passed": self.tier1_passed,
                "tier2_passed": self.tier2_passed,
                "tier3_passed": self.tier3_passed,
                "tier4_passed": self.tier4_passed,
                "total_files_scanned": self.total_files_scanned,
                "total_cs_files": self.total_cs_files,
                "total_py_files": self.total_py_files,
                "total_cs_methods": self.total_cs_methods,
                "total_py_methods": self.total_py_methods,
                "tier1_violations_count": len(self.tier1_violations),
                "tier2_violations_count": len(self.tier2_violations),
            },
            "tier1_violations": [v.to_dict() for v in self.tier1_violations],
            "tier2_violations": [v.to_dict() for v in self.tier2_violations],
            "tier3_audits": [
                {
                    "name": a.name,
                    "description": a.description,
                    "expected_minimum": a.expected_minimum,
                    "found_count": a.found_count,
                    "sample_files": a.sample_files,
                    "passed": a.passed,
                }
                for a in self.tier3_audits
            ],
            "tier4_audits": [
                {
                    "metric": g.metric,
                    "description": g.description,
                    "expected": g.expected,
                    "actual": g.actual,
                    "passed": g.passed,
                }
                for g in self.tier4_audits
            ],
        }


# -----------------------------------------------------------------------------
# Core Scanner Engine
# -----------------------------------------------------------------------------

class ModernizationScanner:
    """Scans monorepo test directories and evaluates all 4 modernization tiers."""

    def __init__(self, root_dir: Path = TESTS_ROOT):
        self.root_dir = root_dir
        self.report = ModernizationReport()

    def run_full_scan(self) -> ModernizationReport:
        self.report = ModernizationReport()
        cs_files: list[Path] = []
        py_files: list[Path] = []

        # 1. Discover all test files
        for root, dirs, files in os.walk(self.root_dir):
            dirs[:] = [d for d in dirs if d not in IGNORED_DIRS]
            rel_root = os.path.relpath(root, self.root_dir)
            if any(part in IGNORED_DIRS for part in Path(rel_root).parts):
                continue

            for f in files:
                if f in EXCLUDED_FILES or f.startswith("run_"):
                    continue
                full_path = Path(root) / f
                if f.endswith(".cs"):
                    cs_files.append(full_path)
                elif f.endswith(".py"):
                    py_files.append(full_path)

        self.report.total_cs_files = len(cs_files)
        self.report.total_py_files = len(py_files)
        self.report.total_files_scanned = len(cs_files) + len(py_files)

        # 2. Scan C# test files
        for cs_path in cs_files:
            self._scan_cs_file(cs_path)

        # 3. Scan Python test files
        for py_path in py_files:
            self._scan_py_file(py_path)

        # 4. Evaluate Tier 3 (Domain Concepts)
        self._evaluate_tier3_domain_concepts(cs_files, py_files)

        # 5. Evaluate Tier 4 (Manual Test Gating Guards)
        self._evaluate_tier4_gating_guards(cs_files)

        return self.report

    def _scan_cs_file(self, path: Path):
        rel_path = str(path.relative_to(REPO_ROOT)).replace("\\", "/")
        file_stem = path.stem

        # Tier 1 File Name Check
        if CS_FILE_OBSOLETE_PAT.search(file_stem) and not DOMAIN_SAFELIST_PAT.search(file_stem):
            self.report.tier1_violations.append(
                Violation(
                    tier=1,
                    category="file",
                    language="csharp",
                    file_path=rel_path,
                    line_number=1,
                    entity_name=path.name,
                    reason="Filename contains obsolete phase or milestone marker",
                )
            )

        try:
            content = path.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            return

        lines = content.splitlines()

        # Class Name Check
        for match in CS_CLASS_REGEX.finditer(content):
            class_name = match.group(1)
            line_no = content[: match.start()].count("\n") + 1
            if CS_CLASS_OBSOLETE_PAT.search(class_name) and not DOMAIN_SAFELIST_PAT.search(class_name):
                self.report.tier1_violations.append(
                    Violation(
                        tier=1,
                        category="class",
                        language="csharp",
                        file_path=rel_path,
                        line_number=line_no,
                        entity_name=class_name,
                        reason="Class declaration contains obsolete phase or milestone marker",
                    )
                )

        # Method Checks
        for method_name, line_no, snippet in extract_cs_test_methods(content):
            self.report.total_cs_methods += 1

            # Tier 1 Method Check
            is_legacy = bool(CS_METHOD_OBSOLETE_PAT.search(method_name)) and not bool(
                DOMAIN_SAFELIST_PAT.search(method_name)
            )
            if is_legacy:
                self.report.tier1_violations.append(
                    Violation(
                        tier=1,
                        category="method",
                        language="csharp",
                        file_path=rel_path,
                        line_number=line_no,
                        entity_name=method_name,
                        reason="Test method contains obsolete milestone/phase prefix",
                        snippet=snippet,
                    )
                )

            # Tier 2 Naming Convention Conformance
            # Standard: [UnitOfWork]_[Scenario]_[ExpectedBehavior]
            parts = method_name.split("_")
            has_two_underscores = len(parts) >= 3
            has_empty_part = any(not p for p in parts)

            if is_legacy or not has_two_underscores or has_empty_part:
                reason = "Violates [UnitOfWork]_[Scenario]_[ExpectedBehavior]"
                if is_legacy:
                    reason += " (contains legacy milestone prefix)"
                elif len(parts) < 3:
                    reason += f" (insufficient parts: {len(parts)}, requires at least 3 parts separated by underscores)"
                elif has_empty_part:
                    reason += " (contains empty segment / consecutive underscores)"

                self.report.tier2_violations.append(
                    Violation(
                        tier=2,
                        category="method",
                        language="csharp",
                        file_path=rel_path,
                        line_number=line_no,
                        entity_name=method_name,
                        reason=reason,
                        snippet=snippet,
                    )
                )

    def _scan_py_file(self, path: Path):
        rel_path = str(path.relative_to(REPO_ROOT)).replace("\\", "/")
        file_stem = path.stem

        # Tier 1 File Name Check
        if PY_FILE_OBSOLETE_PAT.search(file_stem) and not DOMAIN_SAFELIST_PAT.search(file_stem):
            self.report.tier1_violations.append(
                Violation(
                    tier=1,
                    category="file",
                    language="python",
                    file_path=rel_path,
                    line_number=1,
                    entity_name=path.name,
                    reason="Filename contains obsolete phase or milestone marker",
                )
            )

        try:
            content = path.read_text(encoding="utf-8-sig", errors="ignore")
            tree = ast.parse(content, filename=str(path))
        except Exception:
            return

        lines = content.splitlines()

        for node in ast.walk(tree):
            if isinstance(node, ast.ClassDef):
                class_name = node.name
                line_no = node.lineno
                if PY_CLASS_OBSOLETE_PAT.search(class_name) and not DOMAIN_SAFELIST_PAT.search(class_name):
                    self.report.tier1_violations.append(
                        Violation(
                            tier=1,
                            category="class",
                            language="python",
                            file_path=rel_path,
                            line_number=line_no,
                            entity_name=class_name,
                            reason="Class declaration contains obsolete phase or milestone marker",
                        )
                    )

            elif isinstance(node, ast.FunctionDef) and node.name.startswith("test_"):
                self.report.total_py_methods += 1
                method_name = node.name
                line_no = node.lineno
                snippet = lines[line_no - 1].strip() if line_no <= len(lines) else ""

                # Tier 1 Method Check
                is_legacy = bool(PY_METHOD_OBSOLETE_PAT.search(method_name)) and not bool(
                    DOMAIN_SAFELIST_PAT.search(method_name)
                )
                if is_legacy:
                    self.report.tier1_violations.append(
                        Violation(
                            tier=1,
                            category="method",
                            language="python",
                            file_path=rel_path,
                            line_number=line_no,
                            entity_name=method_name,
                            reason="Test method contains obsolete milestone/phase prefix or sequential index",
                            snippet=snippet,
                        )
                    )

                # Tier 2 Naming Convention Conformance
                # Standard: test_[unit_of_work]_[scenario]_[expected_behavior]
                parts = method_name.split("_")
                # parts[0] is 'test'. We expect at least 3 semantic parts after 'test' => len(parts) >= 4
                has_conforming_parts = len(parts) >= 4
                has_empty_part = any(not p for p in parts)

                if is_legacy or not has_conforming_parts or has_empty_part:
                    reason = "Violates test_[unit_of_work]_[scenario]_[expected_behavior]"
                    if is_legacy:
                        reason += " (contains legacy sprint prefix or opaque index)"
                    elif len(parts) < 4:
                        reason += f" (insufficient parts: {len(parts)-1} after test_, requires at least 3 semantic segments)"
                    elif has_empty_part:
                        reason += " (contains empty segment)"

                    self.report.tier2_violations.append(
                        Violation(
                            tier=2,
                            category="method",
                            language="python",
                            file_path=rel_path,
                            line_number=line_no,
                            entity_name=method_name,
                            reason=reason,
                            snippet=snippet,
                        )
                    )

    def _evaluate_tier3_domain_concepts(self, cs_files: list[Path], py_files: list[Path]):
        """Evaluates preservation of genuine in-engine domain concepts."""
        domain_specs = [
            (
                "GamePhase == 3",
                "K2 active combat match phase (GamePhase == 3 / .Be(3) / = 3)",
                re.compile(r"GamePhase\s*(?:==|\.Should\(\)\.Be\(|=)\s*3"),
                5,
            ),
            (
                "ExtractGamePhaseFromJson",
                "Parser for dynamic game phase telemetry extraction from JSON outputs",
                re.compile(r"ExtractGamePhaseFromJson"),
                1,
            ),
            (
                "DynamicPhase",
                "Dynamic match phase claims and reconciliation mechanism",
                re.compile(r"DynamicPhase"),
                1,
            ),
            (
                "Gateway 1 (XML)",
                "Hero Forge Pre-Commit Staging Gateway 1: XML Schema & MDF Integrity",
                re.compile(r"Gateway\s*1|Gateway1|TestGateway1", re.IGNORECASE),
                2,
            ),
            (
                "Gateway 2 (Audio)",
                "Hero Forge Pre-Commit Staging Gateway 2: 48 kHz Audio & Channel Topology",
                re.compile(r"Gateway\s*2|Gateway2|TestGateway2", re.IGNORECASE),
                2,
            ),
            (
                "Gateway 3 (Armature)",
                "Hero Forge Pre-Commit Staging Gateway 3: Armature & Cyclic Boundary Closure",
                re.compile(r"Gateway\s*3|Gateway3|TestGateway3", re.IGNORECASE),
                2,
            ),
            (
                "Gateway 4 (Normal Map)",
                "Hero Forge Pre-Commit Staging Gateway 4: Tangent RxGB Normal Maps & Inversion",
                re.compile(r"Gateway\s*4|Gateway4|TestGateway4", re.IGNORECASE),
                2,
            ),
            (
                "TieredPortDiscovery",
                "Dedicated server listening port discovery algorithm",
                re.compile(r"TieredPortDiscovery"),
                1,
            ),
            (
                "TwoTierBackupRotation",
                "Database backup rotation mechanism (Hot Hourly / Cold Weekly)",
                re.compile(r"TwoTierBackupRotation"),
                1,
            ),
        ]

        all_files = cs_files + py_files
        file_contents: dict[Path, str] = {}
        for p in all_files:
            try:
                file_contents[p] = p.read_text(encoding="utf-8-sig", errors="ignore")
            except Exception:
                continue

        for name, desc, pat, min_count in domain_specs:
            total_found = 0
            sample_files: list[str] = []
            for p, content in file_contents.items():
                matches = pat.findall(content)
                if matches:
                    total_found += len(matches)
                    rel_p = str(p.relative_to(REPO_ROOT)).replace("\\", "/")
                    if len(sample_files) < 4:
                        sample_files.append(rel_p)

            self.report.tier3_audits.append(
                DomainConceptAudit(
                    name=name,
                    description=desc,
                    expected_minimum=min_count,
                    found_count=total_found,
                    sample_files=sample_files,
                )
            )

    def _evaluate_tier4_gating_guards(self, cs_files: list[Path]):
        """Evaluates preservation of manual test gating invariants."""
        env_cs = REPO_ROOT / "tests" / "shared" / "NexusTestEnvironment.cs"
        env_exists = env_cs.is_file()
        env_content = env_cs.read_text(encoding="utf-8", errors="ignore") if env_exists else ""

        # Check 1: NexusTestEnvironment.cs existence
        self.report.tier4_audits.append(
            GatingGuardAudit(
                metric="NexusTestEnvironment.cs Exists",
                description="Presence of centralized test environment infrastructure file",
                expected="True",
                actual=str(env_exists),
                passed=env_exists,
            )
        )

        # Check 2: NexusTestEnvironment defines IsManualTestEnabled
        has_is_manual_enabled = "public static bool IsManualTestEnabled" in env_content
        self.report.tier4_audits.append(
            GatingGuardAudit(
                metric="NexusTestEnvironment.IsManualTestEnabled Declared",
                description="Global boolean property guarding manual tests",
                expected="True",
                actual=str(has_is_manual_enabled),
                passed=has_is_manual_enabled,
            )
        )

        # Check 3: Standard Category constants declared
        has_categories = (
            'ManualTestCategory = "Manual"' in env_content
            and 'IntegrationTestCategory = "Integration"' in env_content
        )
        self.report.tier4_audits.append(
            GatingGuardAudit(
                metric="Standard Category Constants Declared",
                description="ManualTestCategory and IntegrationTestCategory exposed",
                expected="True",
                actual=str(has_categories),
                passed=has_categories,
            )
        )

        # Check 4: ChatSocialTests.cs contains runtime manual gating guard
        chat_tests_path = (
            REPO_ROOT
            / "tests"
            / "integration"
            / "ASPIRE.Integration.Tests"
            / "TRANSMUTANSTEIN.ChatServer"
            / "Tests"
            / "ChatSocialTests.cs"
        )
        chat_has_guard = False
        if chat_tests_path.is_file():
            chat_content = chat_tests_path.read_text(encoding="utf-8", errors="ignore")
            chat_has_guard = "!NexusTestEnvironment.IsManualTestEnabled" in chat_content

        self.report.tier4_audits.append(
            GatingGuardAudit(
                metric="ChatSocialTests.cs Gating Guard",
                description="ChatSocialTests methods guarded with NexusTestEnvironment.IsManualTestEnabled",
                expected="True",
                actual=str(chat_has_guard),
                passed=chat_has_guard,
            )
        )

        # Check 5: Total manual gating guards across C# test repository
        guard_regex = re.compile(r"if\s*\(\s*!NexusTestEnvironment\.IsManualTestEnabled\s*\)\s*return;")
        total_guards = 0
        for p in cs_files:
            try:
                content = p.read_text(encoding="utf-8", errors="ignore")
                matches = guard_regex.findall(content)
                total_guards += len(matches)
            except Exception:
                continue

        # Baseline is ~352 guards; require at least 300 to prevent accidental stripping
        guards_ok = total_guards >= 300
        self.report.tier4_audits.append(
            GatingGuardAudit(
                metric="Monorepo Test Gating Guard Count",
                description="Total count of 'if (!NexusTestEnvironment.IsManualTestEnabled) return;'",
                expected=">= 300",
                actual=str(total_guards),
                passed=guards_ok,
            )
        )


# -----------------------------------------------------------------------------
# Reporting & Formatter Functions
# -----------------------------------------------------------------------------

def print_audit_report(report: ModernizationReport, verbose: bool = False):
    """Prints a structured, high-visibility modernization status report."""
    border = "=" * 80
    subborder = "-" * 80

    print("\n" + border)
    print(" PROJECTKONGOR TEST SUITE MODERNIZATION AUDIT REPORT")
    print(border)
    print(f" Total Test Files Scanned : {report.total_files_scanned} ({report.total_cs_files} C#, {report.total_py_files} Python)")
    print(f" Total Test Methods Parsed: {report.total_cs_methods + report.total_py_methods} ({report.total_cs_methods} C#, {report.total_py_methods} Python)")
    print(subborder)

    # Status summary
    status = lambda p: "[PASS]" if p else "[FAIL]"
    print(f" Tier 1 - Obsolete Marker Elimination Check      : {status(report.tier1_passed)} ({len(report.tier1_violations)} violations)")
    print(f" Tier 2 - Naming Convention Conformance Check    : {status(report.tier2_passed)} ({len(report.tier2_violations)} violations)")
    print(f" Tier 3 - In-Engine Domain Concept Preservation  : {status(report.tier3_passed)} ({sum(1 for a in report.tier3_audits if a.passed)}/{len(report.tier3_audits)} concepts verified)")
    print(f" Tier 4 - Manual Test Gating Guard Invariants    : {status(report.tier4_passed)} ({sum(1 for a in report.tier4_audits if a.passed)}/{len(report.tier4_audits)} checks verified)")
    print(border)

    # Tier 3 Details
    print("\n[Tier 3 Details: In-Engine Domain Concepts]")
    for a in report.tier3_audits:
        mark = "[PASS]" if a.passed else "[FAIL]"
        samples = f" (e.g. {', '.join([os.path.basename(s) for s in a.sample_files[:2]])})" if a.sample_files else ""
        print(f"  {mark} {a.name:<26}: {a.found_count} found (min: {a.expected_minimum}){samples}")

    # Tier 4 Details
    print("\n[Tier 4 Details: Test Gating Invariants]")
    for g in report.tier4_audits:
        mark = "[PASS]" if g.passed else "[FAIL]"
        print(f"  {mark} {g.metric:<36}: Actual='{g.actual}', Expected='{g.expected}'")

    # Sample Violations
    if report.tier1_violations:
        print(f"\n[Tier 1 Violations Preview (showing up to 10 of {len(report.tier1_violations)})]:")
        for v in report.tier1_violations[:10]:
            print(f"  - [{v.language.upper()}] [{v.category.upper()}] {v.entity_name} in {v.file_path}:{v.line_number}")
            if v.reason:
                print(f"      Reason: {v.reason}")

    if report.tier2_violations:
        print(f"\n[Tier 2 Violations Preview (showing up to 10 of {len(report.tier2_violations)})]:")
        for v in report.tier2_violations[:10]:
            print(f"  - [{v.language.upper()}] {v.entity_name} in {v.file_path}:{v.line_number}")
            if v.reason:
                print(f"      Reason: {v.reason}")

    if verbose and (report.tier1_violations or report.tier2_violations):
        print("\n[Full Violations Manifest]:")
        all_v = report.tier1_violations + report.tier2_violations
        for idx, v in enumerate(all_v, 1):
            print(f"{idx:4d}. [Tier {v.tier}] [{v.category}] {v.file_path}:{v.line_number} -> {v.entity_name} ({v.reason})")

    print("\n" + border)
    if report.all_passed:
        print(" ALL 4 MODERNIZATION TIERS PASSED 100%! ZERO REGRESSIONS DETECTED.")
    else:
        print(" MODERNIZATION INCOMPLETE: Address reported violations above.")
    print(border + "\n")


def generate_markdown_report(report: ModernizationReport) -> str:
    """Generates comprehensive markdown documentation of the audit results."""
    md = []
    md.append("# Test Suite Modernization Verification Report")
    md.append("")
    md.append(f"**Generated**: {os.popen('powershell Get-Date -Format o').read().strip() or '2026-09-15'}")
    md.append(f"**Repository Root**: `{REPO_ROOT}`")
    md.append("")
    md.append("## Executive Summary")
    md.append("")
    md.append("| Metric | Count / Status |")
    md.append("|---|---|")
    md.append(f"| Total Test Files Scanned | {report.total_files_scanned} ({report.total_cs_files} C#, {report.total_py_files} Python) |")
    md.append(f"| Total Test Methods Evaluated | {report.total_cs_methods + report.total_py_methods} ({report.total_cs_methods} C#, {report.total_py_methods} Python) |")
    md.append(f"| **Tier 1 (Obsolete Marker Elimination)** | **{'100% PASS' if report.tier1_passed else f'{len(report.tier1_violations)} Violations'}** |")
    md.append(f"| **Tier 2 (Naming Convention Conformance)** | **{'100% PASS' if report.tier2_passed else f'{len(report.tier2_violations)} Violations'}** |")
    md.append(f"| **Tier 3 (Domain Concepts Preserved)** | **{'100% PASS' if report.tier3_passed else 'FAIL'}** |")
    md.append(f"| **Tier 4 (Manual Test Gating Intact)** | **{'100% PASS' if report.tier4_passed else 'FAIL'}** |")
    md.append(f"| **Overall Modernization Status** | **{'READY' if report.all_passed else 'IN PROGRESS'}** |")
    md.append("")
    md.append("## Tier 3: In-Engine Domain Concept Preservation Matrix")
    md.append("")
    md.append("| Domain Concept | Description | Minimum Required | Found Count | Status | Sample Files |")
    md.append("|---|---|:---:|:---:|:---:|---|")
    for a in report.tier3_audits:
        status_badge = "PASS" if a.passed else "**FAIL**"
        samples = "<br>".join([f"`{os.path.basename(s)}`" for s in a.sample_files[:3]])
        md.append(f"| `{a.name}` | {a.description} | {a.expected_minimum} | {a.found_count} | {status_badge} | {samples} |")
    md.append("")
    md.append("## Tier 4: Manual Test Gating Invariants")
    md.append("")
    md.append("| Invariant Metric | Requirement Description | Expected Value | Actual Value | Status |")
    md.append("|---|---|:---:|:---:|:---:|")
    for g in report.tier4_audits:
        status_badge = "PASS" if g.passed else "**FAIL**"
        md.append(f"| `{g.metric}` | {g.description} | `{g.expected}` | `{g.actual}` | {status_badge} |")
    md.append("")

    if report.tier1_violations:
        md.append(f"## Tier 1 Violations ({len(report.tier1_violations)} Remaining)")
        md.append("")
        md.append("| # | Lang | Type | Entity Name | File Path : Line | Reason |")
        md.append("|---|:---:|:---:|---|---|---|")
        for i, v in enumerate(report.tier1_violations[:50], 1):
            md.append(f"| {i} | {v.language} | {v.category} | `{v.entity_name}` | `{v.file_path}:{v.line_number}` | {v.reason} |")
        if len(report.tier1_violations) > 50:
            md.append(f"| ... | ... | ... | *({len(report.tier1_violations) - 50} more omitted)* | ... | ... |")
        md.append("")

    if report.tier2_violations:
        md.append(f"## Tier 2 Violations ({len(report.tier2_violations)} Remaining)")
        md.append("")
        md.append("| # | Lang | Method Name | File Path : Line | Reason |")
        md.append("|---|:---:|---|---|---|")
        for i, v in enumerate(report.tier2_violations[:50], 1):
            md.append(f"| {i} | {v.language} | `{v.entity_name}` | `{v.file_path}:{v.line_number}` | {v.reason} |")
        if len(report.tier2_violations) > 50:
            md.append(f"| ... | ... | *({len(report.tier2_violations) - 50} more omitted)* | ... | ... |")
        md.append("")

    return "\n".join(md)


# -----------------------------------------------------------------------------
# Unittest Test Suite Integration
# -----------------------------------------------------------------------------

class TestSuiteModernizationTests(unittest.TestCase):
    """
    Standard Python unittest test case for continuous integration & automated verification.
    Executes all 4 modernization tier assertions.
    """

    @classmethod
    def setUpClass(cls):
        scanner = ModernizationScanner(TESTS_ROOT)
        cls.report = scanner.run_full_scan()
        cls.audit_mode = os.environ.get("MODERNIZATION_AUDIT", "0").strip() == "1"

    def test_tier1_obsolete_markers_eliminated(self):
        """Tier 1: 0 test methods, classes, or files contain obsolete phase/milestone prefixes."""
        v_count = len(self.report.tier1_violations)
        if self.audit_mode:
            print(f"[AUDIT] Tier 1 violations: {v_count}")
            return

        msg = (
            f"Found {v_count} legacy phase/milestone markers across test suites. "
            f"First violation: {self.report.tier1_violations[0].entity_name} in {self.report.tier1_violations[0].file_path} "
            f"({self.report.tier1_violations[0].reason})"
            if v_count > 0
            else ""
        )
        self.assertEqual(v_count, 0, msg)

    def test_tier2_naming_convention_conformance(self):
        """Tier 2: All test methods follow [UnitOfWork]_[Scenario]_[ExpectedBehavior]."""
        v_count = len(self.report.tier2_violations)
        if self.audit_mode:
            print(f"[AUDIT] Tier 2 violations: {v_count}")
            return

        msg = (
            f"Found {v_count} non-conforming test method names. "
            f"First violation: {self.report.tier2_violations[0].entity_name} in {self.report.tier2_violations[0].file_path} "
            f"({self.report.tier2_violations[0].reason})"
            if v_count > 0
            else ""
        )
        self.assertEqual(v_count, 0, msg)

    def test_tier3_domain_concepts_preserved(self):
        """Tier 3: In-engine domain concepts (GamePhase == 3, Gateway 1-4, etc.) are strictly preserved."""
        failed = [a for a in self.report.tier3_audits if not a.passed]
        msg = (
            f"Domain concepts damaged or missing: {', '.join([f'{f.name} (found {f.found_count}, min {f.expected_minimum})' for f in failed])}"
            if failed
            else ""
        )
        self.assertEqual(len(failed), 0, msg)

    def test_tier4_manual_test_gating_intact(self):
        """Tier 4: Manual test gating guards remain intact (NexusTestEnvironment.IsManualTestEnabled)."""
        failed = [g for g in self.report.tier4_audits if not g.passed]
        msg = (
            f"Test gating invariants violated: {', '.join([f'{f.metric} (actual={f.actual}, expected={f.expected})' for f in failed])}"
            if failed
            else ""
        )
        self.assertEqual(len(failed), 0, msg)

    def test_csharp_parser_handles_comments_and_brackets_in_attributes(self):
        """Hardened parser handles line comments, attribute strings with brackets, unsafe modifiers, and multiline declarations."""
        sample_code = """
        [Fact] // Explanatory comment
        public void Standard_Fact_Works() {}

        [Theory]
        // Comment before inline data
        [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|[\\"popup_a\\",\\"popup_b\\"]", 2)] // Inline comment
        [InlineData("{\\"action\\": [\\"bypass\\"]}")]
        public void Complex_AttributeArguments_ParsedSuccessfully(string json, int count) {}

        [Fact]
        public unsafe void ViewportBlit_UnsafeModifier_ParsedSuccessfully() {}

        [Fact]
        public async Task
            MultiLineSignature_WithLineBreak_ParsedSuccessfully() {}
        """
        methods = extract_cs_test_methods(sample_code)
        method_names = [m[0] for m in methods]
        self.assertEqual(
            method_names,
            [
                "Standard_Fact_Works",
                "Complex_AttributeArguments_ParsedSuccessfully",
                "ViewportBlit_UnsafeModifier_ParsedSuccessfully",
                "MultiLineSignature_WithLineBreak_ParsedSuccessfully",
            ],
        )


# -----------------------------------------------------------------------------
# CLI Entrypoint
# -----------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Verify ProjectKongor Test Suite Modernization (Tiers 1-4)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--audit",
        action="store_true",
        help="Audit mode: print report and exit 0 even if legacy markers remain",
    )
    parser.add_argument(
        "--verbose",
        "-v",
        action="store_true",
        help="Print verbose list of all violations",
    )
    parser.add_argument(
        "--json",
        metavar="PATH",
        help="Write structured audit report to JSON file",
    )
    parser.add_argument(
        "--markdown",
        metavar="PATH",
        help="Write structured audit report to Markdown file",
    )

    args = parser.parse_args()

    scanner = ModernizationScanner(TESTS_ROOT)
    report = scanner.run_full_scan()

    print_audit_report(report, verbose=args.verbose)

    if args.json:
        out_p = Path(args.json)
        out_p.parent.mkdir(parents=True, exist_ok=True)
        out_p.write_text(json.dumps(report.to_dict(), indent=2), encoding="utf-8")
        print(f"Wrote JSON report to: {out_p}")

    if args.markdown:
        out_p = Path(args.markdown)
        out_p.parent.mkdir(parents=True, exist_ok=True)
        out_p.write_text(generate_markdown_report(report), encoding="utf-8")
        print(f"Wrote Markdown report to: {out_p}")

    if not args.audit and not report.all_passed:
        sys.exit(1)
    else:
        sys.exit(0)


if __name__ == "__main__":
    main()
