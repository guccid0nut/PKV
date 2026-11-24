#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
===============================================================================
ProjectKongor RAG & Intelligent Q&A Engine — Automated Evaluation Test Suite
===============================================================================
Comprehensive requirement-driven E2E benchmark evaluation test suite verifying
100% precision, zero false-positive guide misdirections, and strict latency
thresholds (< 10ms prebaked, < 500ms live synthesis) across 35 real-world queries.

Domains Covered:
  1. GAMEPLAY_ONBOARDING  : New player onboarding, launching, game modes, controls.
  2. AVATAR_EXPORT        : Alt avatar 3D models, aliases, glTF/GLB, Blender 4.2+.
  3. SERVER_HOSTING       : Dedicated server fleet, COMPEL, Win32 Job Object sandbox.
  4. SYSTEM_DOCTOR        : Diagnostic health checks, LocationGuard, ContentBroker SHA-256.
  5. CODEBASE_SYMBOL      : Deep C# AST symbols, XML docs, engine patch offsets.
  6. EDGE_AND_NEGATIVE    : Adversarial, ambiguous, empty, out-of-domain rejection.
  7. PERFORMANCE_LATENCY  : Latency stress benchmarks (< 10ms prebaked, < 500ms synthesis).

Test Methodology:
  - Category-Partition & Boundary Value Analysis (Tier 1 & Tier 2)
  - Pairwise Cross-Feature Combinations (Tier 3)
  - Real-World Developer / Player Troubleshooting Workloads (Tier 4)
  - Latency & Resource Containment Verification
===============================================================================
"""

from __future__ import annotations

import dataclasses
import json
import math
import os
import re
import sys
import time
import unittest
from typing import Any, Dict, List, Optional, Tuple

# Ensure repository root and docgen tools are in sys.path
def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

REPO_ROOT = _find_repo_root()
DOCGEN_DIR = os.path.dirname(os.path.abspath(__file__))
SITE_DIR = os.path.join(REPO_ROOT, "docs", "_site")

if REPO_ROOT not in sys.path:
    sys.path.insert(0, REPO_ROOT)
if DOCGEN_DIR not in sys.path:
    sys.path.insert(0, DOCGEN_DIR)

# Import Core RAG Services (optional when query_docs.py is absent)
try:
    from query_docs import CodebaseQAService
except ImportError:
    try:
        from tools.docgen.query_docs import CodebaseQAService
    except ImportError:
        CodebaseQAService = None  # type: ignore[assignment,misc]


# =============================================================================
# Data Structures & Benchmark Definitions
# =============================================================================

@dataclasses.dataclass
class BenchmarkCase:
    """Definition of an individual evaluation test case."""
    case_id: str
    name: str
    query: str
    domain: str
    expected_intent: str
    tier: str
    required_commands: List[str] = dataclasses.field(default_factory=list)
    required_terms: List[str] = dataclasses.field(default_factory=list)
    forbidden_terms: List[str] = dataclasses.field(default_factory=list)
    expected_files: List[str] = dataclasses.field(default_factory=list)
    max_latency_ms: float = 500.0
    description: str = ""


@dataclasses.dataclass
class BenchmarkResult:
    """Outcome and telemetry from evaluating a single benchmark case."""
    case_id: str
    name: str
    query: str
    domain: str
    expected_intent: str
    detected_intent: str
    passed: bool
    latency_ms: float
    command_matched: bool
    terms_matched: bool
    forbidden_matched: bool
    files_matched: bool
    latency_passed: bool
    response_snippet: str
    failure_reasons: List[str] = dataclasses.field(default_factory=list)


# =============================================================================
# Benchmark Intent Classifier & Helper Evaluators
# =============================================================================

def classify_response_intent(query: str, response: str) -> str:
    """
    Infers the detected intent from the query and generated response text.
    Used for strict intent classification verification.
    """
    q_lower = query.strip().lower()
    r_lower = response.lower()

    if not q_lower:
        return "EMPTY"

    if "how to play heroes of newerth" in r_lower or "kongor play" in r_lower:
        return "GAMEPLAY_ONBOARDING"

    if "how to export" in r_lower and "blender" in r_lower:
        return "AVATAR_EXPORT"

    if "how to host a dedicated game server" in r_lower or "kongor host" in r_lower or "compel" in r_lower:
        if "locationguard" in q_lower or "corrupt" in q_lower:
            return "SYSTEM_DOCTOR"
        return "SERVER_HOSTING"

    if "1000 fps engine patch" in r_lower or "0xfb416c" in r_lower or "enginepatchservice" in r_lower:
        return "ENGINE_PATCH"

    if "kongor doctor" in r_lower or "locationguard" in r_lower or "sha-256" in r_lower or "corruption" in r_lower:
        return "SYSTEM_DOCTOR"

    if "srp-6a" in r_lower or "srp" in r_lower or "55555" in r_lower or "rfc 2945" in r_lower:
        return "SRP_AUTH"

    if "codebase symbol" in r_lower or "codebase intelligence" in r_lower or "artefactslocator" in r_lower or "processtreesandbox" in r_lower:
        return "CODEBASE_SYMBOL"

    if "replay-to-mp4 renderer" in r_lower or "kongor replay" in r_lower:
        return "CINEMATICS"

    if "kongor codebase assistant" in r_lower or "please provide a question" in r_lower:
        return "FALLBACK"

    return "UNKNOWN"


def strip_ansi(text: str) -> str:
    """Removes ANSI color escape codes for clean text assertions."""
    ansi_escape = re.compile(r'\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])')
    return ansi_escape.sub('', text)


# =============================================================================
# Benchmark Suite Inventory (35 Comprehensive Test Cases)
# =============================================================================

BENCHMARK_CASES: List[BenchmarkCase] = [
    # -------------------------------------------------------------------------
    # Domain 1: GAMEPLAY_ONBOARDING (Tier 1 & Tier 4)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-01",
        name="New Player How to Play Quickstart",
        query="how do i play?",
        domain="GAMEPLAY_ONBOARDING",
        expected_intent="GAMEPLAY_ONBOARDING",
        tier="Tier 1",
        required_commands=["kongor play"],
        required_terms=["WILLOWMAKER", "Vulkan 1.3", "1000 FPS", "Mid Wars"],
        forbidden_terms=["how to host a dedicated game server", "export to blender"],
        max_latency_ms=10.0,
        description="Verify new player guidance with kongor play launcher command."
    ),
    BenchmarkCase(
        case_id="TC-02",
        name="How Can I Start Playing Launch Workflow",
        query="how can i start playing the game?",
        domain="GAMEPLAY_ONBOARDING",
        expected_intent="GAMEPLAY_ONBOARDING",
        tier="Tier 1",
        required_commands=["kongor play"],
        required_terms=["WILLOWMAKER", "1000 FPS", "SRP account"],
        forbidden_terms=["kongor host"],
        max_latency_ms=10.0,
        description="Verify game start query returns play instructions."
    ),
    BenchmarkCase(
        case_id="TC-03",
        name="Essential Controls and Hotkeys",
        query="what are the essential controls for a new player in Newerth?",
        domain="GAMEPLAY_ONBOARDING",
        expected_intent="GAMEPLAY_ONBOARDING",
        tier="Tier 1",
        required_terms=["Q / W / E / R", "B:", "F1:"],
        forbidden_terms=["0xFB416C", "Job Object"],
        max_latency_ms=10.0,
        description="Verify essential gameplay keyboard shortcuts and hotkeys."
    ),
    BenchmarkCase(
        case_id="TC-04",
        name="Custom Master Server Port and Connect",
        query="how do i connect to a custom master server?",
        domain="GAMEPLAY_ONBOARDING",
        expected_intent="GAMEPLAY_ONBOARDING",
        tier="Tier 4",
        required_terms=["55555", "Master Server"],
        forbidden_terms=["Blender 4.2+"],
        max_latency_ms=10.0,
        description="Verify custom master server port 55555 configuration instructions."
    ),
    BenchmarkCase(
        case_id="TC-05",
        name="Available Game Modes Overview",
        query="what game modes are available in ProjectKongor?",
        domain="GAMEPLAY_ONBOARDING",
        expected_intent="GAMEPLAY_ONBOARDING",
        tier="Tier 1",
        required_terms=["Practice Mode", "Mid Wars", "Forests of Caldavar"],
        forbidden_terms=["export", "Blender"],
        max_latency_ms=10.0,
        description="Verify enumeration of Practice, Mid Wars, and Caldavar modes."
    ),

    # -------------------------------------------------------------------------
    # Domain 2: AVATAR_EXPORT (Tier 1, Tier 2, Tier 4)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-06",
        name="Export Thriller Rhapsody Alt Avatar Skin",
        query="how can i export the thriller rhapsody skin?",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 1",
        required_commands=['kongor model export "thriller rhapsody"'],
        required_terms=["output/models/rhapsody/alt.glb", "Blender 4.2+", "glTF 2.0 (.glb)"],
        forbidden_terms=["how to play heroes of newerth", "kongor host"],
        max_latency_ms=10.0,
        description="Verify exact model export command and Blender import workflow for Thriller Rhapsody."
    ),
    BenchmarkCase(
        case_id="TC-07",
        name="Export Clown Devourer to Blender",
        query="how do i export clown devourer to blender?",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 1",
        required_commands=['kongor model export "clown devourer"'],
        required_terms=["output/models/devourer/alt.glb", "Blender 4.2+", "kongor model list devourer"],
        forbidden_terms=["how to host a dedicated game server"],
        max_latency_ms=10.0,
        description="Verify Clown Devourer alias resolves to devourer/alt and glTF export."
    ),
    BenchmarkCase(
        case_id="TC-08",
        name="Export Frost Pyromancer Skin",
        query="how do i export frost pyromancer?",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 1",
        required_commands=['kongor model export "frost pyromancer"'],
        required_terms=["output/models/pyromancer/alt.glb", "Blender 4.2+"],
        forbidden_terms=["kongor play"],
        max_latency_ms=10.0,
        description="Verify Frost Pyromancer alias maps to pyromancer/alt."
    ),
    BenchmarkCase(
        case_id="TC-09",
        name="Export Bunny Valkyrie 3D Model",
        query="can i export bunny valkyrie 3d model?",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 1",
        required_commands=['kongor model export "bunny valkyrie"'],
        required_terms=["output/models/valkyrie/alt2.glb", "Blender"],
        forbidden_terms=["Job Object"],
        max_latency_ms=10.0,
        description="Verify Bunny Valkyrie skin resolves to valkyrie/alt2."
    ),
    BenchmarkCase(
        case_id="TC-10",
        name="List All Named Skins for Hero",
        query="how do i list all available skins for rhapsody?",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 2",
        required_commands=["kongor model list rhapsody"],
        required_terms=["rhapsody"],
        forbidden_terms=["kongor host"],
        max_latency_ms=10.0,
        description="Verify model list command suggestion for hero skins."
    ),
    BenchmarkCase(
        case_id="TC-11",
        name="Batch Export All Skins for a Hero",
        query="how can i batch export all skins for a hero?",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 2",
        required_commands=["kongor model export"],
        required_terms=["export", "skins"],
        forbidden_terms=["0xFB416C"],
        max_latency_ms=10.0,
        description="Verify batch hero export syntax explanation."
    ),

    # -------------------------------------------------------------------------
    # Domain 3: SERVER_HOSTING (Tier 1 & Tier 4)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-12",
        name="Host Dedicated Server with COMPEL",
        query="how do i host a dedicated server?",
        domain="SERVER_HOSTING",
        expected_intent="SERVER_HOSTING",
        tier="Tier 1",
        required_commands=["kongor host"],
        required_terms=["COMPEL", "Win32 Job Object", "1.0ms timer pacing", "21235", "artifacts/compel/"],
        forbidden_terms=["how to play heroes of newerth", "export to blender"],
        max_latency_ms=10.0,
        description="Verify COMPEL hosting quickstart guide and port requirements."
    ),
    BenchmarkCase(
        case_id="TC-13",
        name="Launch COMPEL Server Manager",
        query="how can i launch compel?",
        domain="SERVER_HOSTING",
        expected_intent="SERVER_HOSTING",
        tier="Tier 1",
        required_commands=["kongor host"],
        required_terms=["COMPEL", "Win32 Job Object", "artifacts/compel/"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=10.0,
        description="Verify explicit COMPEL query returns server manager hosting instructions."
    ),
    BenchmarkCase(
        case_id="TC-14",
        name="COMPEL Win32 Job Object Sandbox Memory Limits",
        query="what are the memory limits and process sandboxing in COMPEL?",
        domain="SERVER_HOSTING",
        expected_intent="SERVER_HOSTING",
        tier="Tier 4",
        required_terms=["Win32 Job Object", "4", "16"],
        forbidden_terms=["Blender 4.2+"],
        max_latency_ms=10.0,
        description="Verify memory limit documentation: 4GB per-process commit, 16GB aggregate job cap."
    ),
    BenchmarkCase(
        case_id="TC-15",
        name="High-Resolution 1.0ms Timer Pacing",
        query="how does COMPEL guarantee high-resolution timer pacing?",
        domain="SERVER_HOSTING",
        expected_intent="SERVER_HOSTING",
        tier="Tier 4",
        required_terms=["1.0ms timer pacing", "COMPEL"],
        forbidden_terms=["export to blender"],
        max_latency_ms=10.0,
        description="Verify explanation of 1.0ms timer pacing via timeBeginPeriod(1)."
    ),
    BenchmarkCase(
        case_id="TC-16",
        name="Server Runtime Artefacts Storage Containment",
        query="where does COMPEL store server replays, logs, and crash dumps?",
        domain="SERVER_HOSTING",
        expected_intent="SERVER_HOSTING",
        tier="Tier 4",
        required_terms=["artifacts/compel/"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=10.0,
        description="Verify project-isolated storage in artifacts/compel/."
    ),
    BenchmarkCase(
        case_id="TC-17",
        name="Public Server UDP Match Ports Forwarding",
        query="what UDP ports need to be forwarded for hosting a public HoN server?",
        domain="SERVER_HOSTING",
        expected_intent="SERVER_HOSTING",
        tier="Tier 4",
        required_terms=["21235", "55555"],
        forbidden_terms=["Blender"],
        max_latency_ms=10.0,
        description="Verify UDP port range 21235-21250 and master server auth TCP port 55555."
    ),

    # -------------------------------------------------------------------------
    # Domain 4: SYSTEM_DOCTOR (Tier 1, Tier 3, Tier 4)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-18",
        name="File Corruption Integrity Diagnostics",
        query="how do i do a check that all our files are not corrupt",
        domain="SYSTEM_DOCTOR",
        expected_intent="SYSTEM_DOCTOR",
        tier="Tier 3",
        required_terms=["doctor", "diagnostics", "check"],
        forbidden_terms=["🎮 HOW TO PLAY", "Step 1: Launch the Game Client", "Welcome to ProjectKongor! Here is how to get into the action"],
        max_latency_ms=15.0,
        description="CRITICAL TEST: Verify file corruption check NEVER falsely triggers new player onboarding guide."
    ),
    BenchmarkCase(
        case_id="TC-19",
        name="LocationGuard Accidental Data Loss Prevention",
        query="how does LocationGuard prevent accidental data loss?",
        domain="SYSTEM_DOCTOR",
        expected_intent="SYSTEM_DOCTOR",
        tier="Tier 4",
        required_terms=["LocationGuard", "Downloads", "Desktop", "hon_x64.exe"],
        forbidden_terms=["export to blender"],
        max_latency_ms=500.0,
        description="Verify LocationGuard safety rules protecting Desktop/Downloads from accidental cleanup."
    ),
    BenchmarkCase(
        case_id="TC-20",
        name="ContentBroker SHA-256 Manifest Verification",
        query="how does ContentBroker verify game client file integrity?",
        domain="SYSTEM_DOCTOR",
        expected_intent="SYSTEM_DOCTOR",
        tier="Tier 4",
        required_terms=["SHA-256", "manifest.json", "CRC32"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=500.0,
        description="Verify differential CDN synchronization and SHA-256 streaming verification."
    ),
    BenchmarkCase(
        case_id="TC-21",
        name="Diagnostic Health Checks on Ports and Dependencies",
        query="how do i run diagnostic health checks on ports and dependencies?",
        domain="SYSTEM_DOCTOR",
        expected_intent="SYSTEM_DOCTOR",
        tier="Tier 1",
        required_commands=["kongor doctor"],
        required_terms=["doctor", "ports"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=10.0,
        description="Verify recommendation of kongor doctor for ecosystem health checks."
    ),

    # -------------------------------------------------------------------------
    # Domain 5: CODEBASE_SYMBOL & ENGINE_PATCH (Tier 1 & Tier 4)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-22",
        name="1000 FPS Float Binary Patch Offset",
        query="how does the 1000 fps patch work in EnginePatchService?",
        domain="ENGINE_PATCH",
        expected_intent="ENGINE_PATCH",
        tier="Tier 1",
        required_terms=["k2_x64.dll", "0xFB416C", "1000 FPS", "EnginePatchService"],
        forbidden_terms=["export to blender"],
        max_latency_ms=10.0,
        description="Verify citation of k2_x64.dll offset 0xFB416C for 1000 FPS unlock."
    ),
    BenchmarkCase(
        case_id="TC-23",
        name="CHost::Execute Host Throttle Jump Suppression",
        query="what is the patch offset for host throttle jump suppression in CHost::Execute?",
        domain="ENGINE_PATCH",
        expected_intent="ENGINE_PATCH",
        tier="Tier 4",
        required_terms=["0x8B72A8", "CHost::Execute", "NOP"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=500.0,
        description="Verify binary patch offset 0x8B72A8 (7C 96 -> 90 90) preventing frame throttle."
    ),
    BenchmarkCase(
        case_id="TC-24",
        name="ArtefactsLocator C# Class and Profile Redirection",
        query="what does ArtefactsLocator do?",
        domain="CODEBASE_SYMBOL",
        expected_intent="CODEBASE_SYMBOL",
        tier="Tier 4",
        expected_files=["apps/compel/src/Services/Supervision/ArtefactsLocator.cs"],
        required_terms=["ArtefactsLocator", "Documents/Heroes of Newerth x64", "artifacts/compel"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=500.0,
        description="Verify retrieval of ArtefactsLocator.cs symbol and runtime path logic."
    ),
    BenchmarkCase(
        case_id="TC-25",
        name="ProcessTreeSandbox Job Object Supervision",
        query="how does ProcessTreeSandbox manage child processes in COMPEL?",
        domain="CODEBASE_SYMBOL",
        expected_intent="CODEBASE_SYMBOL",
        tier="Tier 4",
        expected_files=["apps/compel/src/Internals/ProcessTreeSandbox.cs"],
        required_terms=["ProcessTreeSandbox", "JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=500.0,
        description="Verify retrieval of ProcessTreeSandbox.cs symbol and kernel cleanup flag."
    ),
    BenchmarkCase(
        case_id="TC-26",
        name="SRP-6a Cryptographic Authentication on Master Server",
        query="how does SRP-6a authentication work on the KONGOR Master Server?",
        domain="SRP_AUTH",
        expected_intent="SRP_AUTH",
        tier="Tier 4",
        required_terms=["SRP", "RFC 2945", "55555", "password", "salt"],
        forbidden_terms=["export to blender"],
        max_latency_ms=500.0,
        description="Verify SRP zero-knowledge authentication details on port 55555."
    ),
    BenchmarkCase(
        case_id="TC-27",
        name="FastCombatLogParser Zero Allocation Span Slicing",
        query="how does FastCombatLogParser achieve zero allocations?",
        domain="CODEBASE_SYMBOL",
        expected_intent="CODEBASE_SYMBOL",
        tier="Tier 4",
        required_terms=["ReadOnlySpan", "zero-allocation", "combat log"],
        forbidden_terms=["how to play heroes of newerth"],
        max_latency_ms=500.0,
        description="Verify telemetry streaming parser using ReadOnlySpan and SIMD SearchValues."
    ),

    # -------------------------------------------------------------------------
    # Domain 6: EDGE_AND_NEGATIVE (Tier 2 Boundary & Adversarial)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-28",
        name="Out of Domain Negative Baking Cake Query",
        query="how do i cook a chocolate cake?",
        domain="EDGE_AND_NEGATIVE",
        expected_intent="FALLBACK",
        tier="Tier 2",
        forbidden_terms=[
            "HOW TO PLAY HEROES OF NEWERTH",
            "HOW TO HOST A DEDICATED GAME SERVER",
            "1000 FPS ENGINE PATCH",
            "HOW TO EXPORT"
        ],
        max_latency_ms=500.0,
        description="Verify completely unrelated query falls back gracefully without triggering false guides."
    ),
    BenchmarkCase(
        case_id="TC-29",
        name="Boundary Empty Query String",
        query="",
        domain="EDGE_AND_NEGATIVE",
        expected_intent="EMPTY",
        tier="Tier 2",
        required_terms=["Please provide a question"],
        forbidden_terms=["HOW TO PLAY HEROES OF NEWERTH", "HOW TO HOST"],
        max_latency_ms=10.0,
        description="Verify empty input returns prompt without exceptions."
    ),
    BenchmarkCase(
        case_id="TC-30",
        name="Boundary Special Characters Only",
        query="!@#$%^&*()_+=-~`<>?/.,;:'\"[{}]|\\",
        domain="EDGE_AND_NEGATIVE",
        expected_intent="FALLBACK",
        tier="Tier 2",
        forbidden_terms=["HOW TO PLAY HEROES OF NEWERTH", "HOW TO HOST"],
        max_latency_ms=10.0,
        description="Verify input with pure symbols/punctuation does not crash regex or trigger guides."
    ),
    BenchmarkCase(
        case_id="TC-31",
        name="Ambiguous Single Word Query Check",
        query="check",
        domain="EDGE_AND_NEGATIVE",
        expected_intent="FALLBACK",
        tier="Tier 2",
        forbidden_terms=["HOW TO PLAY HEROES OF NEWERTH", "Welcome to ProjectKongor!"],
        max_latency_ms=500.0,
        description="Verify ambiguous single word 'check' does not trigger new player onboarding."
    ),
    BenchmarkCase(
        case_id="TC-32",
        name="Ambiguous Single Word Query Export",
        query="export",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 2",
        required_commands=["kongor model export"],
        forbidden_terms=["HOW TO HOST A DEDICATED GAME SERVER"],
        max_latency_ms=10.0,
        description="Verify 'export' keyword routes to model export syntax cleanly."
    ),
    BenchmarkCase(
        case_id="TC-33",
        name="Single Hero Name Query Rhapsody",
        query="rhapsody",
        domain="AVATAR_EXPORT",
        expected_intent="AVATAR_EXPORT",
        tier="Tier 2",
        required_terms=["rhapsody", "kongor model"],
        forbidden_terms=["HOW TO HOST A DEDICATED GAME SERVER", "0xFB416C"],
        max_latency_ms=10.0,
        description="Verify single hero name returns model information for that hero."
    ),

    # -------------------------------------------------------------------------
    # Domain 7: PERFORMANCE_LATENCY (Tier 1 & Tier 4)
    # -------------------------------------------------------------------------
    BenchmarkCase(
        case_id="TC-34",
        name="High-Throughput Batch Prebaked Latency Benchmark",
        query="how do i play?",
        domain="PERFORMANCE_LATENCY",
        expected_intent="GAMEPLAY_ONBOARDING",
        tier="Tier 1",
        required_commands=["kongor play"],
        max_latency_ms=10.0,
        description="Verify 100 consecutive prebaked query executions maintain mean latency < 10ms."
    ),
    BenchmarkCase(
        case_id="TC-35",
        name="Live Codebase Dynamic RAG Scan Latency",
        query="what does ArtefactsLocator do?",
        domain="PERFORMANCE_LATENCY",
        expected_intent="CODEBASE_SYMBOL",
        tier="Tier 4",
        required_terms=["ArtefactsLocator"],
        max_latency_ms=500.0,
        description="Verify live codebase symbol and snippet scan returns in under 500ms."
    )
]


# =============================================================================
# Automated Evaluator Core
# =============================================================================

class RagEvaluator:
    """Automated evaluation test runner for ProjectKongor RAG Engine."""

    def __init__(self):
        self.qa_service = CodebaseQAService()
        # Warm up caches
        self.qa_service.get_knowledge_base()
        self.qa_service.get_symbols()

    def evaluate_case(self, case: BenchmarkCase) -> BenchmarkResult:
        """Executes a single benchmark case and evaluates all assertions."""
        t_start = time.perf_counter()
        raw_response = self.qa_service.answer_question(case.query)
        latency_ms = (time.perf_counter() - t_start) * 1000.0

        clean_response = strip_ansi(raw_response)
        detected_intent = classify_response_intent(case.query, clean_response)

        failure_reasons: List[str] = []

        # 1. Assert Intent
        if case.expected_intent and case.expected_intent != "FALLBACK" and case.expected_intent != "UNKNOWN":
            if detected_intent != case.expected_intent:
                failure_reasons.append(f"Intent mismatch: Expected '{case.expected_intent}', got '{detected_intent}'")

        # 2. Assert Exact Commands
        command_matched = True
        for cmd in case.required_commands:
            if cmd.lower() not in clean_response.lower():
                command_matched = False
                failure_reasons.append(f"Missing required command: '{cmd}'")

        # 3. Assert Required Terms
        terms_matched = True
        for term in case.required_terms:
            if term.lower() not in clean_response.lower():
                terms_matched = False
                failure_reasons.append(f"Missing required term: '{term}'")

        # 4. Assert Forbidden Terms (Zero Misdirection)
        forbidden_matched = False
        for fterm in case.forbidden_terms:
            if fterm.lower() in clean_response.lower():
                forbidden_matched = True
                failure_reasons.append(f"False-positive misdirection triggered: contains forbidden phrase '{fterm}'")

        # 5. Assert File Citations
        files_matched = True
        for fpath in case.expected_files:
            # Check relative filename or full path
            fname = os.path.basename(fpath)
            if fname.lower() not in clean_response.lower():
                files_matched = False
                failure_reasons.append(f"Missing expected source file citation: '{fpath}'")

        # 6. Assert Latency Threshold
        latency_passed = latency_ms <= case.max_latency_ms
        if not latency_passed:
            failure_reasons.append(f"Latency exceeded: {latency_ms:.2f}ms > limit {case.max_latency_ms:.2f}ms")

        passed = (len(failure_reasons) == 0)

        # Snippet for logging
        snippet = clean_response[:120].replace('\n', ' ')

        return BenchmarkResult(
            case_id=case.case_id,
            name=case.name,
            query=case.query,
            domain=case.domain,
            expected_intent=case.expected_intent,
            detected_intent=detected_intent,
            passed=passed,
            latency_ms=latency_ms,
            command_matched=command_matched,
            terms_matched=terms_matched,
            forbidden_matched=forbidden_matched,
            files_matched=files_matched,
            latency_passed=latency_passed,
            response_snippet=snippet,
            failure_reasons=failure_reasons
        )

    def run_all_benchmarks(self) -> Tuple[List[BenchmarkResult], Dict[str, Any]]:
        """Runs all benchmark cases and computes aggregated statistics."""
        results: List[BenchmarkResult] = []
        latencies: List[float] = []

        print("\n" + "=" * 80)
        print("🚀 EXECUTING PROJECTKONGOR RAG AUTOMATED EVALUATION SUITE (35 BENCHMARKS)")
        print("=" * 80)

        for case in BENCHMARK_CASES:
            res = self.evaluate_case(case)
            results.append(res)
            latencies.append(res.latency_ms)

            status_glyph = "\033[1;32m[PASS]\033[0m" if res.passed else "\033[1;31m[FAIL]\033[0m"
            print(f"{status_glyph} {res.case_id:5s} | {res.domain:20s} | {res.latency_ms:6.2f}ms | {res.name}")
            if not res.passed:
                for r in res.failure_reasons:
                    print(f"       \033[31m└── Error:\033[0m {r}")

        total_cases = len(results)
        passed_cases = sum(1 for r in results if r.passed)
        failed_cases = total_cases - passed_cases
        pass_rate = (passed_cases / total_cases) * 100.0

        latencies_sorted = sorted(latencies)
        min_lat = latencies_sorted[0] if latencies_sorted else 0.0
        max_lat = latencies_sorted[-1] if latencies_sorted else 0.0
        mean_lat = sum(latencies_sorted) / max(1, len(latencies_sorted))
        p50_lat = latencies_sorted[len(latencies_sorted) // 2] if latencies_sorted else 0.0
        p95_idx = min(len(latencies_sorted) - 1, int(len(latencies_sorted) * 0.95))
        p95_lat = latencies_sorted[p95_idx] if latencies_sorted else 0.0

        summary = {
            "total_benchmarks": total_cases,
            "passed": passed_cases,
            "failed": failed_cases,
            "pass_rate_pct": pass_rate,
            "latency_stats_ms": {
                "min": min_lat,
                "mean": mean_lat,
                "median_p50": p50_lat,
                "p95": p95_lat,
                "max": max_lat
            },
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        }

        print("\n" + "=" * 80)
        print(f"📊 EVALUATION SUMMARY: {passed_cases}/{total_cases} PASSED ({pass_rate:.1f}%)")
        print(f"⏱️  LATENCY: min={min_lat:.2f}ms | mean={mean_lat:.2f}ms | p50={p50_lat:.2f}ms | p95={p95_lat:.2f}ms | max={max_lat:.2f}ms")
        print("=" * 80 + "\n")

        return results, summary


# =============================================================================
# Python unittest Standard Test Classes
# =============================================================================

class TestRagSuite(unittest.TestCase):
    """Base class for all domain test cases."""
    evaluator: RagEvaluator

    @classmethod
    def setUpClass(cls):
        if CodebaseQAService is None:
            raise unittest.SkipTest("tools/docgen/query_docs.py not present")
        cls.evaluator = RagEvaluator()

    def _verify_case_by_id(self, case_id: str):
        case = next((c for c in BENCHMARK_CASES if c.case_id == case_id), None)
        self.assertIsNotNone(case, f"Benchmark case {case_id} not found in inventory")
        result = self.evaluator.evaluate_case(case)
        if not result.passed:
            self.fail(f"Benchmark {case_id} failed: {'; '.join(result.failure_reasons)}")


class TestGameplayOnboarding(TestRagSuite):
    """Domain 1: New Player Onboarding & Launching Tests"""

    def test_tc01_how_do_i_play(self):
        self._verify_case_by_id("TC-01")

    def test_tc02_how_can_i_start_playing(self):
        self._verify_case_by_id("TC-02")

    def test_tc03_essential_controls(self):
        self._verify_case_by_id("TC-03")

    def test_tc04_custom_master_server(self):
        self._verify_case_by_id("TC-04")

    def test_tc05_available_game_modes(self):
        self._verify_case_by_id("TC-05")


class TestAvatarModelExport(TestRagSuite):
    """Domain 2: Alt Avatar 3D Model Exporting & Blender Tests"""

    def test_tc06_thriller_rhapsody_export(self):
        self._verify_case_by_id("TC-06")

    def test_tc07_clown_devourer_export(self):
        self._verify_case_by_id("TC-07")

    def test_tc08_frost_pyromancer_export(self):
        self._verify_case_by_id("TC-08")

    def test_tc09_bunny_valkyrie_export(self):
        self._verify_case_by_id("TC-09")

    def test_tc10_list_skins_for_hero(self):
        self._verify_case_by_id("TC-10")

    def test_tc11_batch_export_hero(self):
        self._verify_case_by_id("TC-11")


class TestServerHostingAndSandbox(TestRagSuite):
    """Domain 3: Dedicated Server Hosting & Win32 Job Object Supervision Tests"""

    def test_tc12_host_dedicated_server(self):
        self._verify_case_by_id("TC-12")

    def test_tc13_launch_compel(self):
        self._verify_case_by_id("TC-13")

    def test_tc14_sandbox_memory_limits(self):
        self._verify_case_by_id("TC-14")

    def test_tc15_timer_pacing_high_res(self):
        self._verify_case_by_id("TC-15")

    def test_tc16_runtime_artefacts_storage(self):
        self._verify_case_by_id("TC-16")

    def test_tc17_udp_match_ports_forwarding(self):
        self._verify_case_by_id("TC-17")


class TestSystemDoctorAndIntegrity(TestRagSuite):
    """Domain 4: Diagnostics, LocationGuard, and File Integrity Tests"""

    def test_tc18_file_corruption_check_no_misdirection(self):
        self._verify_case_by_id("TC-18")

    def test_tc19_location_guard_safety(self):
        self._verify_case_by_id("TC-19")

    def test_tc20_content_broker_sha256(self):
        self._verify_case_by_id("TC-20")

    def test_tc21_diagnostic_health_checks(self):
        self._verify_case_by_id("TC-21")


class TestCodebaseSymbolsAndPatches(TestRagSuite):
    """Domain 5: Codebase Symbols & Engine Patches Tests"""

    def test_tc22_1000_fps_patch_offset(self):
        self._verify_case_by_id("TC-22")

    def test_tc23_host_throttle_jump_offset(self):
        self._verify_case_by_id("TC-23")

    def test_tc24_artefacts_locator_symbol(self):
        self._verify_case_by_id("TC-24")

    def test_tc25_process_tree_sandbox_symbol(self):
        self._verify_case_by_id("TC-25")

    def test_tc26_srp_auth_master_server(self):
        self._verify_case_by_id("TC-26")

    def test_tc27_fast_combat_log_parser(self):
        self._verify_case_by_id("TC-27")


class TestEdgeCasesAndNegativeQueries(TestRagSuite):
    """Domain 6: Boundary, Ambiguous, and Out-of-Domain Rejection Tests"""

    def test_tc28_out_of_domain_chocolate_cake(self):
        self._verify_case_by_id("TC-28")

    def test_tc29_boundary_empty_string(self):
        self._verify_case_by_id("TC-29")

    def test_tc30_boundary_special_characters(self):
        self._verify_case_by_id("TC-30")

    def test_tc31_ambiguous_single_word_check(self):
        self._verify_case_by_id("TC-31")

    def test_tc32_ambiguous_single_word_export(self):
        self._verify_case_by_id("TC-32")

    def test_tc33_single_hero_name_rhapsody(self):
        self._verify_case_by_id("TC-33")


class TestPerformanceAndLatency(TestRagSuite):
    """Domain 7: Latency Stress & Scale Benchmarks"""

    def test_tc34_batch_prebaked_latency(self):
        # Run 100 iterations of prebaked query and assert latency < 10ms mean
        latencies = []
        for _ in range(100):
            t0 = time.perf_counter()
            _ = self.evaluator.qa_service.answer_question("how do i play?")
            latencies.append((time.perf_counter() - t0) * 1000.0)
        mean_lat = sum(latencies) / len(latencies)
        self.assertLess(mean_lat, 10.0, f"Mean prebaked latency exceeded 10ms: {mean_lat:.2f}ms")

    def test_tc35_codebase_rag_scan_latency(self):
        self._verify_case_by_id("TC-35")


class TestDocgenPortalBuild(unittest.TestCase):
    """Domain 8: Documentation Portal & DocFX Hub Verification"""

    def test_tc36_build_portal_execution_and_index_generation(self):
        try:
            from build_portal import build_portal
        except ImportError:
            from tools.docgen.build_portal import build_portal

        # Verify build_portal executes without KeyError or SyntaxWarning
        build_portal()
        index_html = os.path.join(SITE_DIR, "index.html")
        self.assertTrue(os.path.isfile(index_html), f"Expected generated index.html at {index_html}")
        self.assertGreater(os.path.getsize(index_html), 10000, "Generated index.html is unexpectedly small or empty")

    def test_tc37_serve_docs_builder(self):
        try:
            from serve_docs import ensure_docfx_built
        except ImportError:
            from tools.docgen.serve_docs import ensure_docfx_built

        ensure_docfx_built(REPO_ROOT, force_rebuild=False)
        index_html = os.path.join(SITE_DIR, "index.html")
        self.assertTrue(os.path.isfile(index_html))

    def test_tc38_discord_rich_presence_summary(self):
        if CodebaseQAService is None:
            self.skipTest("tools/docgen/query_docs.py not present")

        qa = CodebaseQAService(repo_root=REPO_ROOT)
        ans = qa.answer_question("can i have a summary of how willowmaker sends data to discord rich presence pls")
        self.assertIn("DiscordPresenceService", ans)
        self.assertIn("1003760104199422052", ans)
        self.assertIn("ResolveHero", ans)
        self.assertNotIn("I found no matching records", ans)

    def test_tc39_live_codebase_research(self):
        if CodebaseQAService is None:
            self.skipTest("tools/docgen/query_docs.py not present")

        qa = CodebaseQAService(repo_root=REPO_ROOT)
        ans = qa.answer_question("how does the fast combat log parser achieve zero allocation?")
        self.assertIn("FastCombatLogParser", ans)
        self.assertIn("ReadOnlySpan", ans)
        self.assertNotIn("I found no matching records", ans)

    def test_tc40_tell_me_about_this_app(self):
        if CodebaseQAService is None:
            self.skipTest("tools/docgen/query_docs.py not present")

        qa = CodebaseQAService(repo_root=REPO_ROOT)
        ans = qa.answer_question("tell me about this app")
        self.assertIn("WILLOWMAKER", ans)
        self.assertIn("COMPEL", ans)
        self.assertIn("TRANSMUTANSTEIN", ans)
        self.assertNotIn("I am ready to assist you with ProjectKongor! Please provide a question", ans)
        self.assertNotIn("Things You Can Ask:", ans)

    def test_tc41_build_command(self):
        if CodebaseQAService is None:
            self.skipTest("tools/docgen/query_docs.py not present")

        qa = CodebaseQAService(repo_root=REPO_ROOT)
        ans = qa.answer_question("is there a build command to compile debug and release?")
        self.assertIn("kongor build", ans)
        self.assertIn("Release", ans)
        self.assertIn("Debug", ans)


# =============================================================================
# CLI Main Entry Point
# =============================================================================

def main() -> int:
    """Executes the full evaluation suite and exports JSON metrics."""
    evaluator = RagEvaluator()
    results, summary = evaluator.run_all_benchmarks()

    # Save evaluation report to docs/_site/rag_eval_results.json
    try:
        os.makedirs(SITE_DIR, exist_ok=True)
        report_path = os.path.join(SITE_DIR, "rag_eval_results.json")
        with open(report_path, "w", encoding="utf-8") as f:
            json.dump({
                "summary": summary,
                "benchmarks": [dataclasses.asdict(r) for r in results]
            }, f, indent=2)
        print(f"📁 Evaluation results exported to: {os.path.relpath(report_path, REPO_ROOT)}")
    except Exception as e:
        print(f"⚠️  Failed to save JSON evaluation report: {e}")

    return 0 if summary["failed"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
