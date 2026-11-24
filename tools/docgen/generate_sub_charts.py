#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Sub-Component Architecture SVG Diagram Generator.
Production Tool in tools/docgen/.

Generates detailed sub-component SVG diagrams for individual subsystem READMEs and documentation.
"""

import os
import sys
import logging
import argparse
from typing import Optional

def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

PROJECT_ROOT = _find_repo_root()
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

DEFAULT_OUTPUT_DIR = os.path.join(PROJECT_ROOT, "docs", "images")
logger = logging.getLogger("tools.docgen.generate_sub_charts")


def create_master_server_architecture_svg(filepath: str) -> str:
    """Generates KONGOR Master Server Architecture & Routing SVG."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1150 780" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>
    <marker id="arrowGray" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#8b949e"/>
    </marker>
    <marker id="arrowGreen" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#3fb950"/>
    </marker>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#bc8cff"/>
    </marker>
    <marker id="arrowBlue" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#58a6ff"/>
    </marker>
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- TITLE -->
  <text x="575" y="40" text-anchor="middle" fill="#f0f6fc" font-size="18" font-weight="700">🏛️ KONGOR Master Server Architecture &amp; Routing</text>

  <!-- CLIENTS SUBGRAPH -->
  <g filter="url(#shadow)">
    <rect x="40" y="65" width="1070" height="120" rx="10" fill="#161b22" stroke="#388bfd" stroke-width="1.5"/>
    <rect x="40" y="65" width="1070" height="30" rx="10" fill="#1f6feb" fill-opacity="0.25"/>
    <text x="60" y="86" fill="#58a6ff" font-size="13" font-weight="700">🎮 Players &amp; Dedicated Hosts</text>

    <!-- Client Nodes -->
    <rect x="60" y="105" width="310" height="65" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="215" y="130" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🎮 Game Client (WILLOWMAKER)</text>
    <text x="215" y="152" text-anchor="middle" fill="#79c0ff" font-size="11">HTTP /client_requester.php</text>

    <rect x="420" y="105" width="310" height="65" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="575" y="130" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🛡️ COMPEL Dedicated Host</text>
    <text x="575" y="152" text-anchor="middle" fill="#79c0ff" font-size="11">HTTP /server_requester.php &amp; stats</text>

    <rect x="780" y="105" width="310" height="65" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="935" y="130" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌐 Web Portal &amp; Control Panel</text>
    <text x="935" y="152" text-anchor="middle" fill="#79c0ff" font-size="11">DAWNBRINGER / NEXUS GUI / SignalR</text>
  </g>

  <!-- MASTER SERVER SUBGRAPH -->
  <g filter="url(#shadow)">
    <rect x="40" y="225" width="1070" height="310" rx="10" fill="#161b22" stroke="#2ea043" stroke-width="1.5"/>
    <rect x="40" y="225" width="1070" height="30" rx="10" fill="#2ea043" fill-opacity="0.25"/>
    <text x="60" y="246" fill="#56d364" font-size="13" font-weight="700">☁️ KONGOR Master Server (Port 55555 / ASP.NET Core)</text>

    <!-- Middleware -->
    <rect x="60" y="270" width="480" height="45" rx="6" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="300" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="600">🛡️ Rate Limiting Middleware (Token Bucket / Strict / Relaxed)</text>

    <rect x="580" y="270" width="510" height="45" rx="6" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="835" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="600">🔀 Request Router &amp; Subsystem Dispatcher</text>

    <path d="M 540 292 L 580 292" stroke="#3fb950" stroke-width="1.5" marker-end="url(#arrowGreen)"/>

    <!-- Handlers Grid -->
    <g>
      <rect x="60" y="335" width="155" height="75" rx="6" fill="#1c2128" stroke="#30363d" stroke-width="1"/>
      <text x="137" y="360" text-anchor="middle" fill="#f0f6fc" font-size="11" font-weight="700">🔑 SRP Auth</text>
      <text x="137" y="380" text-anchor="middle" fill="#8b949e" font-size="10">pre_auth / srpauth</text>
      <text x="137" y="396" text-anchor="middle" fill="#7ee787" font-size="9">Session Token Vault</text>

      <rect x="230" y="335" width="155" height="75" rx="6" fill="#1c2128" stroke="#30363d" stroke-width="1"/>
      <text x="307" y="360" text-anchor="middle" fill="#f0f6fc" font-size="11" font-weight="700">🖥️ Server Req</text>
      <text x="307" y="380" text-anchor="middle" fill="#8b949e" font-size="10">new_session / online</text>
      <text x="307" y="396" text-anchor="middle" fill="#7ee787" font-size="9">Slot Heartbeats</text>

      <rect x="400" y="335" width="155" height="75" rx="6" fill="#1c2128" stroke="#30363d" stroke-width="1"/>
      <text x="477" y="360" text-anchor="middle" fill="#f0f6fc" font-size="11" font-weight="700">📊 Stats Req</text>
      <text x="477" y="380" text-anchor="middle" fill="#8b949e" font-size="10">SHA1 HMAC Verify</text>
      <text x="477" y="396" text-anchor="middle" fill="#7ee787" font-size="9">Async Match Queue</text>

      <rect x="570" y="335" width="155" height="75" rx="6" fill="#1c2128" stroke="#30363d" stroke-width="1"/>
      <text x="647" y="360" text-anchor="middle" fill="#f0f6fc" font-size="11" font-weight="700">🛒 Goblin Store</text>
      <text x="647" y="380" text-anchor="middle" fill="#8b949e" font-size="10">Plinko / Vault / Buy</text>
      <text x="647" y="396" text-anchor="middle" fill="#7ee787" font-size="9">Product Catalog</text>

      <rect x="740" y="335" width="155" height="75" rx="6" fill="#1c2128" stroke="#30363d" stroke-width="1"/>
      <text x="817" y="360" text-anchor="middle" fill="#f0f6fc" font-size="11" font-weight="700">🎬 Replay Upload</text>
      <text x="817" y="380" text-anchor="middle" fill="#8b949e" font-size="10">/replays/upload</text>
      <text x="817" y="396" text-anchor="middle" fill="#7ee787" font-size="9">Stream to Cloud S3</text>

      <rect x="910" y="335" width="180" height="75" rx="6" fill="#1c2128" stroke="#30363d" stroke-width="1"/>
      <text x="1000" y="360" text-anchor="middle" fill="#f0f6fc" font-size="11" font-weight="700">📡 SignalR Hubs</text>
      <text x="1000" y="380" text-anchor="middle" fill="#8b949e" font-size="10">/hubs/presence</text>
      <text x="1000" y="396" text-anchor="middle" fill="#7ee787" font-size="9">Real-time Pub/Sub</text>
    </g>

    <!-- Async Match Processing Pipeline -->
    <rect x="60" y="435" width="1030" height="80" rx="6" fill="#14291c" stroke="#2ea043" stroke-width="1"/>
    <text x="80" y="462" fill="#7ee787" font-size="12" font-weight="700">⚡ Async Background Worker Pipeline</text>
    <text x="80" y="484" fill="#c9d1d9" font-size="11">IMatchProcessingChannel (Bounded Channel) ➔ MatchProcessingBackgroundWorker</text>
    <text x="80" y="502" fill="#8b949e" font-size="11">Computes MMR rating deltas, K/D/A aggregates, Hero Mastery XP, and persists structured combat telemetry.</text>
  </g>

  <!-- DATA & CACHE INFRASTRUCTURE SUBGRAPH -->
  <g filter="url(#shadow)">
    <rect x="40" y="570" width="1070" height="175" rx="10" fill="#161b22" stroke="#a371f7" stroke-width="1.5"/>
    <rect x="40" y="570" width="1070" height="30" rx="10" fill="#a371f7" fill-opacity="0.25"/>
    <text x="60" y="591" fill="#bc8cff" font-size="13" font-weight="700">💾 Data &amp; Cache Infrastructure</text>

    <!-- DB Node -->
    <rect x="60" y="615" width="310" height="110" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="215" y="640" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🗄️ MERRICK DatabaseContext</text>
    <text x="215" y="662" text-anchor="middle" fill="#8b949e" font-size="11">EF Core Multi-Provider</text>
    <text x="215" y="682" text-anchor="middle" fill="#d2a8ff" font-size="10">PostgreSQL / Supabase / SQL Server</text>
    <text x="215" y="700" text-anchor="middle" fill="#8b949e" font-size="10">Schemas: core, stat, data, misc</text>

    <!-- Redis Node -->
    <rect x="420" y="615" width="310" height="110" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="575" y="640" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚡ Redis 7+ Distributed Cache</text>
    <text x="575" y="662" text-anchor="middle" fill="#8b949e" font-size="11">Ports 28298 / 28299</text>
    <text x="575" y="682" text-anchor="middle" fill="#d2a8ff" font-size="10">Sessions &amp; SRP Proofs / Salt Cache</text>
    <text x="575" y="700" text-anchor="middle" fill="#8b949e" font-size="10">Matchmaking Queues &amp; Pub/Sub</text>

    <!-- S3 Node -->
    <rect x="780" y="615" width="310" height="110" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="935" y="640" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">📦 Replay &amp; Patch Storage</text>
    <text x="935" y="662" text-anchor="middle" fill="#8b949e" font-size="11">Community / S3 CDN</text>
    <text x="935" y="682" text-anchor="middle" fill="#d2a8ff" font-size="10">LocalDisk / Supabase Storage / AWS S3</text>
    <text x="935" y="700" text-anchor="middle" fill="#8b949e" font-size="10">Full .honreplay Binary Archives</text>
  </g>

  <!-- Connectors -->
  <path d="M 215 170 L 215 270" stroke="#58a6ff" stroke-width="1.5" marker-end="url(#arrowBlue)"/>
  <path d="M 575 170 L 575 270" stroke="#58a6ff" stroke-width="1.5" marker-end="url(#arrowBlue)"/>
  <path d="M 935 170 L 935 270" stroke="#58a6ff" stroke-width="1.5" marker-end="url(#arrowBlue)"/>

  <path d="M 215 535 L 215 615" stroke="#a371f7" stroke-width="1.5" marker-end="url(#arrowPurple)"/>
  <path d="M 575 535 L 575 615" stroke="#a371f7" stroke-width="1.5" marker-end="url(#arrowPurple)"/>
  <path d="M 935 535 L 935 615" stroke="#a371f7" stroke-width="1.5" marker-end="url(#arrowPurple)"/>
</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated master server architecture SVG at: {filepath}")
    return filepath


def create_aspire_orchestration_svg(filepath: str) -> str:
    """Generates .NET Aspire Local Cluster Orchestration SVG."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1150 780" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>
    <marker id="arrowIndigo" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#818cf8"/>
    </marker>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#bc8cff"/>
    </marker>
    <marker id="arrowGreen" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#3fb950"/>
    </marker>
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- TITLE -->
  <text x="575" y="40" text-anchor="middle" fill="#f0f6fc" font-size="18" font-weight="700">📊 .NET Aspire Local Cluster Orchestration</text>

  <!-- ASPIRE HOST SUBGRAPH -->
  <g filter="url(#shadow)">
    <rect x="40" y="65" width="1070" height="120" rx="10" fill="#161b22" stroke="#6366f1" stroke-width="1.5"/>
    <rect x="40" y="65" width="1070" height="30" rx="10" fill="#6366f1" fill-opacity="0.25"/>
    <text x="60" y="86" fill="#a5b4fc" font-size="13" font-weight="700">⚙️ .NET Aspire Application Host (AppHost)</text>

    <rect x="60" y="105" width="480" height="65" rx="8" fill="#21262d" stroke="#6366f1" stroke-width="1.2"/>
    <text x="300" y="130" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📊 Aspire Web Dashboard</text>
    <text x="300" y="152" text-anchor="middle" fill="#a5b4fc" font-size="11">https://localhost:5550 • Unified Structured Logs &amp; OTLP</text>

    <rect x="610" y="105" width="480" height="65" rx="8" fill="#21262d" stroke="#6366f1" stroke-width="1.2"/>
    <text x="850" y="130" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🔥 Grafana Pyroscope Profiler</text>
    <text x="850" y="152" text-anchor="middle" fill="#a5b4fc" font-size="11">http://localhost:4040 • Continuous CPU &amp; Allocation Profiling</text>
  </g>

  <!-- MICROSERVICES SUBGRAPH -->
  <g filter="url(#shadow)">
    <rect x="40" y="225" width="1070" height="270" rx="10" fill="#161b22" stroke="#2ea043" stroke-width="1.5"/>
    <rect x="40" y="225" width="1070" height="30" rx="10" fill="#2ea043" fill-opacity="0.25"/>
    <text x="60" y="246" fill="#56d364" font-size="13" font-weight="700">💻 .NET 10 Microservices Stack</text>

    <g>
      <!-- DB Context -->
      <rect x="60" y="270" width="190" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
      <text x="155" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🗄️ database-context</text>
      <text x="155" y="320" text-anchor="middle" fill="#8b949e" font-size="11">Port 5553 (MERRICK)</text>
      <text x="155" y="340" text-anchor="middle" fill="#7ee787" font-size="10">Schema Migration / Seed</text>

      <!-- Master Server -->
      <rect x="275" y="270" width="190" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
      <text x="370" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🏛️ master-server</text>
      <text x="370" y="320" text-anchor="middle" fill="#8b949e" font-size="11">Port 55555 (KONGOR)</text>
      <text x="370" y="340" text-anchor="middle" fill="#7ee787" font-size="10">SRP Auth &amp; Stats API</text>

      <!-- Chat Server -->
      <rect x="490" y="270" width="190" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
      <text x="585" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">💬 chat-server</text>
      <text x="585" y="320" text-anchor="middle" fill="#8b949e" font-size="11">Ports 5554 / 11031+</text>
      <text x="585" y="340" text-anchor="middle" fill="#7ee787" font-size="10">TRANSMUTANSTEIN</text>

      <!-- Web API -->
      <rect x="705" y="270" width="190" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
      <text x="800" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🔌 web-portal-api</text>
      <text x="800" y="320" text-anchor="middle" fill="#8b949e" font-size="11">Ports 5200 / 5201</text>
      <text x="800" y="340" text-anchor="middle" fill="#7ee787" font-size="10">ZORGATH REST API</text>

      <!-- Web UI -->
      <rect x="920" y="270" width="170" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
      <text x="1005" y="298" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌐 web-portal-ui</text>
      <text x="1005" y="320" text-anchor="middle" fill="#8b949e" font-size="11">Ports 5266 / 5557</text>
      <text x="1005" y="340" text-anchor="middle" fill="#7ee787" font-size="10">DAWNBRINGER Portal</text>
    </g>

    <!-- Optional Fleet row -->
    <rect x="60" y="385" width="1030" height="85" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="80" y="415" fill="#f0883e" font-size="13" font-weight="700">🎮 Dedicated Server Fleet (Optional Node: COMPEL)</text>
    <text x="80" y="438" fill="#c9d1d9" font-size="11">COMPEL Dedicated Server Manager: Ports 11235+ / 21235+ (Win32 Job Object Sandbox &amp; UDP Rate Limiting)</text>
    <text x="80" y="456" fill="#8b949e" font-size="11">Registers server slots with KONGOR master-server and reports heartbeats via REST.</text>
  </g>

  <!-- CONTAINERS SUBGRAPH -->
  <g filter="url(#shadow)">
    <rect x="40" y="535" width="1070" height="210" rx="10" fill="#161b22" stroke="#a371f7" stroke-width="1.5"/>
    <rect x="40" y="535" width="1070" height="30" rx="10" fill="#a371f7" fill-opacity="0.25"/>
    <text x="60" y="556" fill="#bc8cff" font-size="13" font-weight="700">📦 Containerized Infrastructure &amp; Databases</text>

    <!-- Redis -->
    <rect x="60" y="585" width="310" height="135" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="215" y="615" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚡ distributed-cache (Redis 7)</text>
    <text x="215" y="638" text-anchor="middle" fill="#8b949e" font-size="11">Port 6379 (Container)</text>
    <text x="215" y="660" text-anchor="middle" fill="#d2a8ff" font-size="10">Session Vault &amp; Proofs</text>
    <text x="215" y="680" text-anchor="middle" fill="#8b949e" font-size="10">RedisInsight: Port 5540</text>

    <!-- SQL Server -->
    <rect x="420" y="585" width="310" height="135" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="575" y="615" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🗄️ database-server (SQL Server)</text>
    <text x="575" y="638" text-anchor="middle" fill="#8b949e" font-size="11">Port 1433 (Container)</text>
    <text x="575" y="660" text-anchor="middle" fill="#d2a8ff" font-size="10">Local Development Database</text>
    <text x="575" y="680" text-anchor="middle" fill="#8b949e" font-size="10">Auto-created by Aspire</text>

    <!-- Supabase / External -->
    <rect x="780" y="585" width="310" height="135" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="935" y="615" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌐 Supabase / PostgreSQL</text>
    <text x="935" y="638" text-anchor="middle" fill="#8b949e" font-size="11">Cloud Production DB</text>
    <text x="935" y="660" text-anchor="middle" fill="#d2a8ff" font-size="10">DATABASE_PROVIDER=PostgreSQL</text>
    <text x="935" y="680" text-anchor="middle" fill="#8b949e" font-size="10">Zero-Config Direct Handoff</text>
  </g>

  <!-- Connectors -->
  <path d="M 300 170 L 300 225" stroke="#818cf8" stroke-width="1.5" stroke-dasharray="4" marker-end="url(#arrowIndigo)"/>
  <path d="M 850 170 L 850 225" stroke="#818cf8" stroke-width="1.5" stroke-dasharray="4" marker-end="url(#arrowIndigo)"/>
  <path d="M 370 360 L 215 585" stroke="#a371f7" stroke-width="1.5" marker-end="url(#arrowPurple)"/>
  <path d="M 155 360 L 575 585" stroke="#a371f7" stroke-width="1.5" marker-end="url(#arrowPurple)"/>
</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated Aspire orchestration SVG at: {filepath}")
    return filepath


def create_game_loop_architecture_svg(filepath: str) -> str:
    """Generates Game Loop Architecture SVG."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1150 780" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>
    <marker id="arrowBlue" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#58a6ff"/>
    </marker>
    <marker id="arrowGreen" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#3fb950"/>
    </marker>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#bc8cff"/>
    </marker>
    <marker id="arrowOrange" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#d29922"/>
    </marker>
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- TITLE -->
  <text x="575" y="40" text-anchor="middle" fill="#f0f6fc" font-size="18" font-weight="700">🎮 HoN High-Tickrate Game Loop &amp; Frame Pacing Architecture</text>

  <!-- SUBGRAPH 1: ENGINE PACING & INVARIANT TIMER -->
  <g filter="url(#shadow)">
    <rect x="40" y="65" width="1070" height="205" rx="10" fill="#161b22" stroke="#388bfd" stroke-width="1.5"/>
    <rect x="40" y="65" width="1070" height="30" rx="10" fill="#1f6feb" fill-opacity="0.25"/>
    <text x="60" y="86" fill="#58a6ff" font-size="13" font-weight="700">⏱️ Engine Pacing &amp; Invariant Timer (1000 FPS Dynamic Loop)</text>

    <!-- Node 1: TSC -->
    <rect x="60" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="215" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">⏱️ Hardware TSC Calibrator</text>
    <text x="215" y="160" text-anchor="middle" fill="#8b949e" font-size="11">RDTSC / QPC Sub-Microsecond Sync</text>
    <text x="215" y="180" text-anchor="middle" fill="#79c0ff" font-size="10">1.0ms Invariant Timer Calibration</text>

    <!-- Node 2: 1000 FPS Pacer -->
    <rect x="420" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="575" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🏎️ Dynamic Engine Patcher</text>
    <text x="575" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Replaces Legacy 250 FPS Sleep(1) Cap</text>
    <text x="575" y="180" text-anchor="middle" fill="#79c0ff" font-size="10">High-Precision Spin-Wait &amp; Yield</text>

    <!-- Node 3: Simulation Decoupling -->
    <rect x="780" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="935" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">⚖️ Tick &amp; Render Decoupling</text>
    <text x="935" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Server Simulation: 20Hz / 50Hz Fixed</text>
    <text x="935" y="180" text-anchor="middle" fill="#79c0ff" font-size="10">Smooth Hermite Frame Interpolation</text>

    <text x="60" y="222" fill="#c9d1d9" font-size="11">• Eliminates Windows OS timer jitter using monotonic QPC invariant calibration</text>
    <text x="60" y="244" fill="#c9d1d9" font-size="11">• Unlocks silky-smooth 144Hz–360Hz+ high-refresh-rate displays with sub-millisecond click-to-photon latency</text>
  </g>

  <!-- SUBGRAPH 2: RENDER PRESENTATION TRANSLATION (DX9 -> VULKAN 1.3) -->
  <g filter="url(#shadow)">
    <rect x="40" y="295" width="1070" height="235" rx="10" fill="#161b22" stroke="#2ea043" stroke-width="1.5"/>
    <rect x="40" y="295" width="1070" height="30" rx="10" fill="#2ea043" fill-opacity="0.25"/>
    <text x="60" y="316" fill="#56d364" font-size="13" font-weight="700">🌋 Modern Render Translation (DirectX 9 ➔ DXVK Vulkan 1.3 &amp; RTX Remix)</text>

    <!-- Node 1: D3D9 Intercept -->
    <rect x="60" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="175" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🎯 D3D9 Call Intercept</text>
    <text x="175" y="390" text-anchor="middle" fill="#8b949e" font-size="11">d3d9.dll Proxy Wrapper</text>
    <text x="175" y="410" text-anchor="middle" fill="#7ee787" font-size="10">Translates Legacy Draw Calls</text>

    <!-- Node 2: DXVK Vulkan -->
    <rect x="320" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="435" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌋 DXVK 2.3+ Runtime</text>
    <text x="435" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Vulkan 1.3 Command Buffers</text>
    <text x="435" y="410" text-anchor="middle" fill="#7ee787" font-size="10">Async Pipeline Caching (Zero Stutter)</text>

    <!-- Node 3: Swapchain Mode -->
    <rect x="580" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="695" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚡ Mailbox Presentation</text>
    <text x="695" y="390" text-anchor="middle" fill="#8b949e" font-size="11">VK_PRESENT_MODE_MAILBOX</text>
    <text x="695" y="410" text-anchor="middle" fill="#7ee787" font-size="10">Tear-Free Minimum Input Latency</text>

    <!-- Node 4: RTX Remix -->
    <rect x="840" y="340" width="250" height="90" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="965" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">✨ RTX Remix Path Tracing</text>
    <text x="965" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Full Real-Time Ray Tracing</text>
    <text x="965" y="410" text-anchor="middle" fill="#7ee787" font-size="10">PBR Materials &amp; Volumetric Lighting</text>

    <text x="60" y="460" fill="#c9d1d9" font-size="11">• Eliminates driver-level D3D9 overhead and enables full multi-threaded Vulkan command recording</text>
    <text x="60" y="482" fill="#c9d1d9" font-size="11">• State Cache (`dxvk.conf`) stores compiled shader blobs for 0ms initial ability cast lag</text>
    <text x="60" y="504" fill="#c9d1d9" font-size="11">• Direct hardware bridge to NVIDIA RTX Remix for modern photorealistic lighting overhaul</text>
  </g>

  <!-- SUBGRAPH 3: INPUT & MAILBOX IPC -->
  <g filter="url(#shadow)">
    <rect x="40" y="555" width="1070" height="195" rx="10" fill="#161b22" stroke="#a371f7" stroke-width="1.5"/>
    <rect x="40" y="555" width="1070" height="30" rx="10" fill="#a371f7" fill-opacity="0.25"/>
    <text x="60" y="576" fill="#bc8cff" font-size="13" font-weight="700">⚡ Low-Latency Input Polling &amp; Mailbox IPC Control</text>

    <!-- Node 1: Raw Input -->
    <rect x="60" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="215" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🖱️ Win32 Raw Input (WM_INPUT)</text>
    <text x="215" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Bypasses Standard Window Queue</text>
    <text x="215" y="670" text-anchor="middle" fill="#d2a8ff" font-size="10">1000Hz+ Gaming Mouse Polling</text>

    <!-- Node 2: Named Pipe IPC -->
    <rect x="420" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="575" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">📬 Named Pipe Mailbox IPC</text>
    <text x="575" y="650" text-anchor="middle" fill="#8b949e" font-size="11">\\\\.\\pipe\\willowmaker-ipc</text>
    <text x="575" y="670" text-anchor="middle" fill="#d2a8ff" font-size="10">Async CVar, Lua &amp; Command Injection</text>

    <!-- Node 3: Headless Sandbox -->
    <rect x="780" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="935" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">👻 Headless Offscreen Sandbox</text>
    <text x="935" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Parked at vid_pos -32000 -32000</text>
    <text x="935" y="670" text-anchor="middle" fill="#d2a8ff" font-size="10">0ms Dwell Direct Frame Capture</text>

    <text x="60" y="715" fill="#c9d1d9" font-size="11">• Decoupled input polling guarantees zero skipped frames during intense 5v5 teamfight particle explosions</text>
    <text x="60" y="735" fill="#c9d1d9" font-size="11">• Enables high-throughput automated CI/CD sweeps and AI bot testing without stealing desktop focus</text>
  </g>
</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated game loop architecture SVG at: {filepath}")
    return filepath


def create_mod_pipeline_architecture_svg(filepath: str) -> str:
    """Generates Mod Pipeline Architecture SVG."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1150 780" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>
    <marker id="arrowGreen" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#3fb950"/>
    </marker>
    <marker id="arrowOrange" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#d29922"/>
    </marker>
    <marker id="arrowBlue" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#58a6ff"/>
    </marker>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#bc8cff"/>
    </marker>
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- TITLE -->
  <text x="575" y="40" text-anchor="middle" fill="#f0f6fc" font-size="18" font-weight="700">📦 KONGOR S2Z Modding Engine, AST Normalizer &amp; VFS Architecture</text>

  <!-- SUBGRAPH 1: DISCOVERY & INGESTION -->
  <g filter="url(#shadow)">
    <rect x="40" y="65" width="1070" height="205" rx="10" fill="#161b22" stroke="#3fb950" stroke-width="1.5"/>
    <rect x="40" y="65" width="1070" height="30" rx="10" fill="#2ea043" fill-opacity="0.25"/>
    <text x="60" y="86" fill="#56d364" font-size="13" font-weight="700">🔍 Mod Package Discovery, Ingestion &amp; Manifest Parsing</text>

    <!-- Node 1 -->
    <rect x="60" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="215" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📦 Package Scanner</text>
    <text x="215" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Scans mods/ &amp; game/ Directory</text>
    <text x="215" y="180" text-anchor="middle" fill="#7ee787" font-size="10">.honmod, .s2z &amp; Loose Folders</text>

    <!-- Node 2 -->
    <rect x="420" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="575" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📜 Manifest &amp; Schema Validator</text>
    <text x="575" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Parses mod.xml &amp; Metadata</text>
    <text x="575" y="180" text-anchor="middle" fill="#7ee787" font-size="10">Target Engine Bounds &amp; Author Info</text>

    <!-- Node 3 -->
    <rect x="780" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="935" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🔒 Archive Header Parser</text>
    <text x="935" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Central Directory &amp; Checksums</text>
    <text x="935" y="180" text-anchor="middle" fill="#7ee787" font-size="10">CRC32 &amp; Uncompressed Size Mapping</text>

    <text x="60" y="222" fill="#c9d1d9" font-size="11">• Automatically indexes community mods, official balance patches, and custom UI skin packs</text>
    <text x="60" y="244" fill="#c9d1d9" font-size="11">• Validates compatibility matrices against K2 Engine v5.0+ binary structures before applying</text>
  </g>

  <!-- SUBGRAPH 2: DAG RESOLUTION & AST MERGE -->
  <g filter="url(#shadow)">
    <rect x="40" y="295" width="1070" height="235" rx="10" fill="#161b22" stroke="#d29922" stroke-width="1.5"/>
    <rect x="40" y="295" width="1070" height="30" rx="10" fill="#d29922" fill-opacity="0.25"/>
    <text x="60" y="316" fill="#f0883e" font-size="13" font-weight="700">🔀 Topological DAG Resolution &amp; In-Memory AST Entity Merging</text>

    <!-- Node 1 -->
    <rect x="60" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="175" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">📊 DAG Dependency Resolver</text>
    <text x="175" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Topological Sort Ordering</text>
    <text x="175" y="410" text-anchor="middle" fill="#ffab70" font-size="10">Prerequisite &amp; Cycle Resolution</text>

    <!-- Node 2 -->
    <rect x="320" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="435" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚠️ Conflict Detection Guard</text>
    <text x="435" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Collision Analysis</text>
    <text x="435" y="410" text-anchor="middle" fill="#ffab70" font-size="10">Hero Entity &amp; String Clashes</text>

    <!-- Node 3 -->
    <rect x="580" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="695" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🧬 AST XML Entity Merger</text>
    <text x="695" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Surgical In-Memory Splice</text>
    <text x="695" y="410" text-anchor="middle" fill="#ffab70" font-size="10">Heroes, Abilities &amp; String Tables</text>

    <!-- Node 4 -->
    <rect x="840" y="340" width="250" height="90" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="965" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🎨 Asset &amp; Texture Normalizer</text>
    <text x="965" y="390" text-anchor="middle" fill="#8b949e" font-size="11">DDS / TGA Texture Conversion</text>
    <text x="965" y="410" text-anchor="middle" fill="#ffab70" font-size="10">DXT5 Mipmaps &amp; Shader Descriptors</text>

    <text x="60" y="460" fill="#c9d1d9" font-size="11">• In-memory AST surgery resolves entity attribute overrides without corrupting master XML databases</text>
    <text x="60" y="482" fill="#c9d1d9" font-size="11">• Topological sorting guarantees child sub-mods load strictly after parent frameworks</text>
    <text x="60" y="504" fill="#c9d1d9" font-size="11">• Automatic texture conversion standardizes legacy DDS formats into DXVK-compatible textures</text>
  </g>

  <!-- SUBGRAPH 3: VFS & PACKING -->
  <g filter="url(#shadow)">
    <rect x="40" y="555" width="1070" height="195" rx="10" fill="#161b22" stroke="#58a6ff" stroke-width="1.5"/>
    <rect x="40" y="555" width="1070" height="30" rx="10" fill="#1f6feb" fill-opacity="0.25"/>
    <text x="60" y="576" fill="#79c0ff" font-size="13" font-weight="700">📂 Virtual File System (VFS) Layering &amp; High-Speed S2Z Archiving</text>

    <!-- Node 1 -->
    <rect x="60" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="215" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🏔️ Tiered VFS Priority Stack</text>
    <text x="215" y="650" text-anchor="middle" fill="#8b949e" font-size="11">game/override/ ➔ mods/ ➔ resources0.s2z</text>
    <text x="215" y="670" text-anchor="middle" fill="#79c0ff" font-size="10">Zero-File-Modification Overrides</text>

    <!-- Node 2 -->
    <rect x="420" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="575" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🔗 NTFS Junction &amp; Symlink Injector</text>
    <text x="575" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Instant Zero-Copy Mod Activation</text>
    <text x="575" y="670" text-anchor="middle" fill="#79c0ff" font-size="10">Atomic Enable / Disable Toggles</text>

    <!-- Node 3 -->
    <rect x="780" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="935" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">📦 High-Speed S2Z Pack &amp; Streamer</text>
    <text x="935" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Native Deflate Archive Compression</text>
    <text x="935" y="670" text-anchor="middle" fill="#79c0ff" font-size="10">Optimized for Ultra-Fast VFS Mounts</text>

    <text x="60" y="715" fill="#c9d1d9" font-size="11">• Loose file priority lets developers edit models and Lua scripts live without repackaging archives</text>
    <text x="60" y="735" fill="#c9d1d9" font-size="11">• Supports single-click mod distribution packages with cryptographic manifest validation</text>
  </g>
</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated mod pipeline architecture SVG at: {filepath}")
    return filepath


def create_replay_renderer_architecture_svg(filepath: str) -> str:
    """Generates Replay Renderer Architecture SVG."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1150 780" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>
    <marker id="arrowOrange" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#f0883e"/>
    </marker>
    <marker id="arrowBlue" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#58a6ff"/>
    </marker>
    <marker id="arrowGreen" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#3fb950"/>
    </marker>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#bc8cff"/>
    </marker>
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- TITLE -->
  <text x="575" y="40" text-anchor="middle" fill="#f0f6fc" font-size="18" font-weight="700">🎬 Deterministic Replay-to-MP4 Render Pipeline &amp; Highlight Clipper</text>

  <!-- SUBGRAPH 1: REPLAY INGESTION & HIGHLIGHT DETECTION -->
  <g filter="url(#shadow)">
    <rect x="40" y="65" width="1070" height="205" rx="10" fill="#161b22" stroke="#f0883e" stroke-width="1.5"/>
    <rect x="40" y="65" width="1070" height="30" rx="10" fill="#f0883e" fill-opacity="0.25"/>
    <text x="60" y="86" fill="#ffab70" font-size="13" font-weight="700">📼 Replay Ingestion, Telemetry Parsing &amp; Teamfight Detection</text>

    <!-- Node 1 -->
    <rect x="60" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#f0883e" stroke-width="1.2"/>
    <text x="215" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📼 Binary Replay Parser</text>
    <text x="215" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Reads .honreplay Wire Stream</text>
    <text x="215" y="180" text-anchor="middle" fill="#ffab70" font-size="10">Entity States, Snapshots &amp; Actions</text>

    <!-- Node 2 -->
    <rect x="420" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#f0883e" stroke-width="1.2"/>
    <text x="575" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">💥 Combat Juice Event Detector</text>
    <text x="575" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Damage Spikes &amp; Killstreaks</text>
    <text x="575" y="180" text-anchor="middle" fill="#ffab70" font-size="10">Automated Highlight Cut Timestamps</text>

    <!-- Node 3 -->
    <rect x="780" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#f0883e" stroke-width="1.2"/>
    <text x="935" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🎥 Camera Choreography Director</text>
    <text x="935" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Cinematic Spline Smoothing</text>
    <text x="935" y="180" text-anchor="middle" fill="#ffab70" font-size="10">Smart Hero Tracking &amp; Zoom Easing</text>

    <text x="60" y="222" fill="#c9d1d9" font-size="11">• Automatically indexes 100% of combat engagements, team wipes, and Roshan/Kongor attempts</text>
    <text x="60" y="244" fill="#c9d1d9" font-size="11">• Generates clip timeline metadata with pre-roll (5s) and post-roll (8s) buffer windows</text>
  </g>

  <!-- SUBGRAPH 2: HEADLESS VULKAN SIMULATION -->
  <g filter="url(#shadow)">
    <rect x="40" y="295" width="1070" height="235" rx="10" fill="#161b22" stroke="#388bfd" stroke-width="1.5"/>
    <rect x="40" y="295" width="1070" height="30" rx="10" fill="#1f6feb" fill-opacity="0.25"/>
    <text x="60" y="316" fill="#58a6ff" font-size="13" font-weight="700">🌋 Headless Vulkan Simulation &amp; Shared Memory Interception</text>

    <!-- Node 1 -->
    <rect x="60" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="175" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">👻 Headless Client Runner</text>
    <text x="175" y="390" text-anchor="middle" fill="#8b949e" font-size="11">hon_x64.exe -replay</text>
    <text x="175" y="410" text-anchor="middle" fill="#79c0ff" font-size="10">Coordinates (-32000, -32000)</text>

    <!-- Node 2 -->
    <rect x="320" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="435" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚙️ Deterministic Tick Stepper</text>
    <text x="435" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Step-Locked Frame Sync</text>
    <text x="435" y="410" text-anchor="middle" fill="#79c0ff" font-size="10">Zero Frame Drops / Glitches</text>

    <!-- Node 3 -->
    <rect x="580" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="695" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌋 VkLayer_hon_replay_capture</text>
    <text x="695" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Vulkan Swapchain Hook</text>
    <text x="695" y="410" text-anchor="middle" fill="#79c0ff" font-size="10">32-bit Raw RGBA Pixel Extraction</text>

    <!-- Node 4 -->
    <rect x="840" y="340" width="250" height="90" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="965" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🔊 WASAPI Loopback Audio</text>
    <text x="965" y="390" text-anchor="middle" fill="#8b949e" font-size="11">High-Definition Audio Stream</text>
    <text x="965" y="410" text-anchor="middle" fill="#79c0ff" font-size="10">Hero VO, Announcer &amp; SFX Tracks</text>

    <text x="60" y="460" fill="#c9d1d9" font-size="11">• Headless playback bypasses desktop window exposure, allowing silent parallel rendering in background</text>
    <text x="60" y="482" fill="#c9d1d9" font-size="11">• Vulkan layer hooks `vkQueuePresentKHR` directly for bit-perfect 1080p / 4K frame buffer extraction</text>
    <text x="60" y="504" fill="#c9d1d9" font-size="11">• Synchronizes WASAPI uncompressed PCM audio streams with video presentation timestamps</text>
  </g>

  <!-- SUBGRAPH 3: ENCODING & DISTRIBUTION -->
  <g filter="url(#shadow)">
    <rect x="40" y="555" width="1070" height="195" rx="10" fill="#161b22" stroke="#2ea043" stroke-width="1.5"/>
    <rect x="40" y="555" width="1070" height="30" rx="10" fill="#2ea043" fill-opacity="0.25"/>
    <text x="60" y="576" fill="#56d364" font-size="13" font-weight="700">🎞️ Hardware-Accelerated Video Encoding &amp; Cloud Distribution</text>

    <!-- Node 1 -->
    <rect x="60" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#2ea043" stroke-width="1.2"/>
    <text x="215" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🔄 Win32 Shared Memory Ring Buffer</text>
    <text x="215" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Local\\HonReplayFrameBuffer_{pid}</text>
    <text x="215" y="670" text-anchor="middle" fill="#7ee787" font-size="10">Zero-Copy 64B Header Event Sync</text>

    <!-- Node 2 -->
    <rect x="420" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#2ea043" stroke-width="1.2"/>
    <text x="575" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚡ FFmpeg NVENC / HEVC Encoder</text>
    <text x="575" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Hardware-Accelerated 60FPS Video</text>
    <text x="575" y="670" text-anchor="middle" fill="#7ee787" font-size="10">High-Bitrate H.264 / HEVC MP4 Mux</text>

    <!-- Node 3 -->
    <rect x="780" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#2ea043" stroke-width="1.2"/>
    <text x="935" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌐 Cloud S3 &amp; DAWNBRINGER Sync</text>
    <text x="935" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Direct Stream to Community / S3 CDN</text>
    <text x="935" y="670" text-anchor="middle" fill="#7ee787" font-size="10">Instant Player Match History Playback</text>

    <text x="60" y="715" fill="#c9d1d9" font-size="11">• Renders 60-minute competitive matches in under 3 minutes on modern NVENC-capable GPUs</text>
    <text x="60" y="735" fill="#c9d1d9" font-size="11">• Immediately publishes playable video clips to player profile match dossiers and Discord webhooks</text>
  </g>
</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated replay renderer architecture SVG at: {filepath}")
    return filepath


def create_combat_juice_architecture_svg(filepath: str) -> str:
    """Generates Combat Juice Architecture SVG."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1150 780" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#bc8cff"/>
    </marker>
    <marker id="arrowRed" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#f85149"/>
    </marker>
    <marker id="arrowBlue" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#58a6ff"/>
    </marker>
    <marker id="arrowGreen" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#3fb950"/>
    </marker>
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- TITLE -->
  <text x="575" y="40" text-anchor="middle" fill="#f0f6fc" font-size="18" font-weight="700">💥 Combat Feel &amp; Visual Polish Suite (Juice &amp; UMPH Architecture)</text>

  <!-- SUBGRAPH 1: CAMERA TRAUMA & SHAKE -->
  <g filter="url(#shadow)">
    <rect x="40" y="65" width="1070" height="205" rx="10" fill="#161b22" stroke="#bc8cff" stroke-width="1.5"/>
    <rect x="40" y="65" width="1070" height="30" rx="10" fill="#bc8cff" fill-opacity="0.25"/>
    <text x="60" y="86" fill="#d2a8ff" font-size="13" font-weight="700">🎥 Non-Linear Camera Trauma &amp; 6-DOF Shake Engine</text>

    <!-- Node 1 -->
    <rect x="60" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#bc8cff" stroke-width="1.2"/>
    <text x="215" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📉 Non-Linear Trauma Accumulator</text>
    <text x="215" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Decay: Trauma² / Trauma³ Curve</text>
    <text x="215" y="180" text-anchor="middle" fill="#d2a8ff" font-size="10">Heavy Ultimates, Explosions &amp; Crits</text>

    <!-- Node 2 -->
    <rect x="420" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#bc8cff" stroke-width="1.2"/>
    <text x="575" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🌊 Perlin Noise 6-DOF Shake</text>
    <text x="575" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Smooth Translational &amp; Rotational</text>
    <text x="575" y="180" text-anchor="middle" fill="#d2a8ff" font-size="10">Pitch, Yaw, Roll &amp; Z-Displacement</text>

    <!-- Node 3 -->
    <rect x="780" y="110" width="310" height="85" rx="8" fill="#21262d" stroke="#bc8cff" stroke-width="1.2"/>
    <text x="935" y="138" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📏 3D Distance Falloff Evaluator</text>
    <text x="935" y="160" text-anchor="middle" fill="#8b949e" font-size="11">Epicenter-to-Camera Attenuation</text>
    <text x="935" y="180" text-anchor="middle" fill="#d2a8ff" font-size="10">Inverse Square Falloff Modeling</text>

    <text x="60" y="222" fill="#c9d1d9" font-size="11">• Eliminates disorienting linear camera snapping in favor of organic, filmic trauma impulses</text>
    <text x="60" y="244" fill="#c9d1d9" font-size="11">• Automatically clamps maximum rotational deflection to preserve tactical visibility during teamfights</text>
  </g>

  <!-- SUBGRAPH 2: DYNAMIC FLOATERS & KILL BANNERS -->
  <g filter="url(#shadow)">
    <rect x="40" y="295" width="1070" height="235" rx="10" fill="#161b22" stroke="#f85149" stroke-width="1.5"/>
    <rect x="40" y="295" width="1070" height="30" rx="10" fill="#f85149" fill-opacity="0.25"/>
    <text x="60" y="316" fill="#ff7b72" font-size="13" font-weight="700">✨ Dynamic Damage Floaters, Kill Banners &amp; Impact Polish</text>

    <!-- Node 1 -->
    <rect x="60" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#f85149" stroke-width="1.2"/>
    <text x="175" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🔢 Physics Damage Floaters</text>
    <text x="175" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Physical, Magic &amp; True Damage</text>
    <text x="175" y="410" text-anchor="middle" fill="#ff7b72" font-size="10">Velocity Bouncing &amp; Fade Curves</text>

    <!-- Node 2 -->
    <rect x="320" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#f85149" stroke-width="1.2"/>
    <text x="435" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🏆 Multi-Kill Banner System</text>
    <text x="435" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Hat Trick, Bloodbath, ANNIHILATION</text>
    <text x="435" y="410" text-anchor="middle" fill="#ff7b72" font-size="10">Screen-Filling Particle Shockwaves</text>

    <!-- Node 3 -->
    <rect x="580" y="340" width="230" height="90" rx="8" fill="#21262d" stroke="#f85149" stroke-width="1.2"/>
    <text x="695" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚡ Hit Stop &amp; Micro-Freeze</text>
    <text x="695" y="390" text-anchor="middle" fill="#8b949e" font-size="11">30ms–50ms Colossal Hit Pause</text>
    <text x="695" y="410" text-anchor="middle" fill="#ff7b72" font-size="10">Tactile Weight on Heavy Blows</text>

    <!-- Node 4 -->
    <rect x="840" y="340" width="250" height="90" rx="8" fill="#21262d" stroke="#f85149" stroke-width="1.2"/>
    <text x="965" y="368" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌈 Post-Process Impact FX</text>
    <text x="965" y="390" text-anchor="middle" fill="#8b949e" font-size="11">Radial Blur &amp; Vignette Pulse</text>
    <text x="965" y="410" text-anchor="middle" fill="#ff7b72" font-size="10">Subtle Chromatic Aberration</text>

    <text x="60" y="460" fill="#c9d1d9" font-size="11">• Distinctive typography and color-coding for critical strikes, hero heals, and execution thresholds</text>
    <text x="60" y="482" fill="#c9d1d9" font-size="11">• Micro-freeze adds fighting-game punch to game-turning abilities (e.g. Deadwood Willow Punch, Behemoth Fissure)</text>
    <text x="60" y="504" fill="#c9d1d9" font-size="11">• Real-time audio ducking lets announcer callouts and impact thuds cut through ambient battle sounds</text>
  </g>

  <!-- SUBGRAPH 3: TELEMETRY & HIGHLIGHT FEED -->
  <g filter="url(#shadow)">
    <rect x="40" y="555" width="1070" height="195" rx="10" fill="#161b22" stroke="#58a6ff" stroke-width="1.5"/>
    <rect x="40" y="555" width="1070" height="30" rx="10" fill="#1f6feb" fill-opacity="0.25"/>
    <text x="60" y="576" fill="#79c0ff" font-size="13" font-weight="700">📡 Combat Juice Telemetry, Replay Highlights &amp; Web Spectator Feed</text>

    <!-- Node 1 -->
    <rect x="60" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="215" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">📡 Structured Telemetry Dispatcher</text>
    <text x="215" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Named Pipe Event Emission</text>
    <text x="215" y="670" text-anchor="middle" fill="#79c0ff" font-size="10">Low-Latency JSON Event Stream</text>

    <!-- Node 2 -->
    <rect x="420" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="575" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🎬 Automated Highlight Trigger</text>
    <text x="575" y="650" text-anchor="middle" fill="#8b949e" font-size="11">Feeds Replay Video Renderer</text>
    <text x="575" y="670" text-anchor="middle" fill="#79c0ff" font-size="10">Autonomous Clip Extraction</text>

    <!-- Node 3 -->
    <rect x="780" y="600" width="310" height="85" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="935" y="628" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🌐 Live Spectator Hub (SignalR)</text>
    <text x="935" y="650" text-anchor="middle" fill="#8b949e" font-size="11">DAWNBRINGER Live Web Spectator</text>
    <text x="935" y="670" text-anchor="middle" fill="#79c0ff" font-size="10">Real-Time Teamfight Notifications</text>

    <text x="60" y="715" fill="#c9d1d9" font-size="11">• Real-time telemetry allows external broadcasting tools and tournament overlays to trigger effects</text>
    <text x="60" y="735" fill="#c9d1d9" font-size="11">• Bridges gameplay combat feel directly to automated social clip generators and post-game reviews</text>
  </g>
</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated combat juice architecture SVG at: {filepath}")
    return filepath


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate Sub-Component Architecture SVGs")
    parser.add_argument("--output-dir", "-o", type=str, default=DEFAULT_OUTPUT_DIR, help="Destination directory for SVGs")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info(f"Generating sub-charts into: {args.output_dir}")

    create_master_server_architecture_svg(os.path.join(args.output_dir, "master_server_architecture.svg"))
    create_aspire_orchestration_svg(os.path.join(args.output_dir, "aspire_orchestration.svg"))
    create_game_loop_architecture_svg(os.path.join(args.output_dir, "game_loop_architecture.svg"))
    create_mod_pipeline_architecture_svg(os.path.join(args.output_dir, "mod_pipeline_architecture.svg"))
    create_replay_renderer_architecture_svg(os.path.join(args.output_dir, "replay_renderer_architecture.svg"))
    create_combat_juice_architecture_svg(os.path.join(args.output_dir, "combat_juice_architecture.svg"))

    logger.info("All sub-component documentation charts generated successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
