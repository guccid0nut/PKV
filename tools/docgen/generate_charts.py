#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Architecture and Decision Funnel SVG Diagram Generator.
Production Tool in tools/docgen/.

Generates SVG architecture and decision funnel diagrams for repository documentation.
"""

import os
import sys
import logging
import argparse
from typing import Optional, List

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
logger = logging.getLogger("tools.docgen.generate_charts")


def create_decision_funnel_svg(filepath: str) -> str:
    """Generates the decision funnel SVG diagram."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1100 680" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <!-- Background Gradient -->
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>

    <!-- Card Backgrounds -->
    <linearGradient id="blueCardGrad" x1="0%" y1="0%" x2="0%" y2="100%">
      <stop offset="0%" stop-color="#1b2838"/>
      <stop offset="100%" stop-color="#0e1726"/>
    </linearGradient>
    <linearGradient id="amberCardGrad" x1="0%" y1="0%" x2="0%" y2="100%">
      <stop offset="0%" stop-color="#2d2215"/>
      <stop offset="100%" stop-color="#17120a"/>
    </linearGradient>
    <linearGradient id="greenCardGrad" x1="0%" y1="0%" x2="0%" y2="100%">
      <stop offset="0%" stop-color="#1a1c36"/>
      <stop offset="100%" stop-color="#0e1022"/>
    </linearGradient>

    <!-- Arrow Markers -->
    <marker id="arrowGray" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#8b949e"/>
    </marker>
    <marker id="arrowBlue" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#58a6ff"/>
    </marker>
    <marker id="arrowAmber" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#f0883e"/>
    </marker>
    <marker id="arrowPurple" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M 0 1 L 8 5 L 0 9 z" fill="#a5b4fc"/>
    </marker>

    <!-- Drop Shadow Filter -->
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="6" flood-color="#000000" flood-opacity="0.6"/>
    </filter>
  </defs>

  <!-- Canvas Background -->
  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- Top Title / Start Node -->
  <g filter="url(#shadow)">
    <rect x="260" y="28" width="580" height="52" rx="26" fill="#21262d" stroke="#58a6ff" stroke-width="2"/>
    <text x="550" y="60" text-anchor="middle" fill="#f0f6fc" font-size="16" font-weight="700">👋 Welcome to ProjectKongor! What is your primary goal?</text>
  </g>

  <!-- Flow to Choice -->
  <path d="M 550 80 L 550 112" stroke="#8b949e" stroke-width="2" marker-end="url(#arrowGray)"/>

  <!-- Decision Node -->
  <g filter="url(#shadow)">
    <polygon points="550,115 670,145 550,175 430,145" fill="#161b22" stroke="#8b949e" stroke-width="2"/>
    <text x="550" y="150" text-anchor="middle" fill="#f0f6fc" font-size="14" font-weight="700">Select Your Path</text>
  </g>

  <!-- Connectors from Decision to 3 Columns -->
  <!-- Left: Play -> WILLOWMAKER -->
  <path d="M 430 145 C 230 145, 190 195, 190 225" fill="none" stroke="#58a6ff" stroke-width="2" marker-end="url(#arrowBlue)"/>
  <g filter="url(#shadow)">
    <rect x="75" y="170" width="230" height="30" rx="15" fill="#0d1117" stroke="#388bfd" stroke-width="1.5"/>
    <text x="190" y="190" text-anchor="middle" fill="#79c0ff" font-size="12" font-weight="700">🎮 "I want to PLAY"</text>
  </g>

  <!-- Center: Host -> COMPEL -->
  <path d="M 550 175 L 550 225" fill="none" stroke="#f0883e" stroke-width="2" marker-end="url(#arrowAmber)"/>
  <g filter="url(#shadow)">
    <rect x="435" y="185" width="230" height="30" rx="15" fill="#0d1117" stroke="#d29922" stroke-width="1.5"/>
    <text x="550" y="205" text-anchor="middle" fill="#ffab70" font-size="12" font-weight="700">🖥️ "I want to HOST"</text>
  </g>

  <!-- Right: Run -> CONTROL PANEL GUI -->
  <path d="M 670 145 C 870 145, 910 195, 910 225" fill="none" stroke="#818cf8" stroke-width="2" marker-end="url(#arrowPurple)"/>
  <g filter="url(#shadow)">
    <rect x="795" y="170" width="230" height="30" rx="15" fill="#0d1117" stroke="#6366f1" stroke-width="1.5"/>
    <text x="910" y="190" text-anchor="middle" fill="#c7d2fe" font-size="12" font-weight="700">⚙️ "I want to RUN / MANAGE"</text>
  </g>

  <!-- ==================== COLUMN 1: PLAY -> WILLOWMAKER ==================== -->
  <g filter="url(#shadow)">
    <rect x="40" y="235" width="300" height="415" rx="12" fill="url(#blueCardGrad)" stroke="#388bfd" stroke-width="1.5"/>
    
    <!-- Column Header -->
    <rect x="40" y="235" width="300" height="50" rx="12" fill="#1f6feb" fill-opacity="0.3"/>
    <text x="190" y="267" text-anchor="middle" fill="#58a6ff" font-size="16" font-weight="700">🚀 WILLOWMAKER Launcher</text>

    <!-- Step 1 Card -->
    <rect x="55" y="300" width="270" height="145" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.2"/>
    <text x="70" y="328" fill="#f0f6fc" font-size="14" font-weight="700">1. Launch Game Client</text>
    <text x="70" y="352" fill="#c9d1d9" font-size="12">Automatic CDN sync, 1000 FPS</text>
    <text x="70" y="370" fill="#c9d1d9" font-size="12">patch, DXVK Vulkan &amp; RTX Remix.</text>
    <rect x="70" y="392" width="240" height="32" rx="6" fill="#21262d" stroke="#388bfd" stroke-width="1"/>
    <text x="190" y="413" text-anchor="middle" fill="#79c0ff" font-size="11" font-weight="600">👉 src/apps/willowmaker/README.md</text>

    <!-- Step 2 Card -->
    <rect x="55" y="465" width="270" height="160" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.2"/>
    <text x="70" y="493" fill="#f0f6fc" font-size="14" font-weight="700">2. Mods &amp; Discord RPC</text>
    <text x="70" y="517" fill="#c9d1d9" font-size="12">Install skins, HUD mods, and sync</text>
    <text x="70" y="535" fill="#c9d1d9" font-size="12">real-time hero stats to Discord.</text>
    <rect x="70" y="565" width="240" height="32" rx="6" fill="#21262d" stroke="#388bfd" stroke-width="1"/>
    <text x="190" y="586" text-anchor="middle" fill="#79c0ff" font-size="11" font-weight="600">📖 Get Started as Player</text>
  </g>

  <!-- ==================== COLUMN 2: HOST -> COMPEL ==================== -->
  <g filter="url(#shadow)">
    <rect x="400" y="235" width="300" height="415" rx="12" fill="url(#amberCardGrad)" stroke="#d29922" stroke-width="1.5"/>
    
    <!-- Column Header -->
    <rect x="400" y="235" width="300" height="50" rx="12" fill="#d29922" fill-opacity="0.25"/>
    <text x="550" y="267" text-anchor="middle" fill="#f0883e" font-size="16" font-weight="700">🛡️ COMPEL Server Manager</text>

    <!-- Step 1 Card -->
    <rect x="415" y="300" width="270" height="145" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.2"/>
    <text x="430" y="328" fill="#f0f6fc" font-size="14" font-weight="700">1. Run Dedicated Manager</text>
    <text x="430" y="352" fill="#c9d1d9" font-size="12">Win32 Job Object sandboxing,</text>
    <text x="430" y="370" fill="#c9d1d9" font-size="12">1.0ms timer pacing, &amp; anti-DDoS.</text>
    <rect x="430" y="392" width="240" height="32" rx="6" fill="#21262d" stroke="#d29922" stroke-width="1"/>
    <text x="550" y="413" text-anchor="middle" fill="#ffab70" font-size="11" font-weight="600">👉 src/apps/compel/README.md</text>

    <!-- Step 2 Card -->
    <rect x="415" y="465" width="270" height="160" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.2"/>
    <text x="430" y="493" fill="#f0f6fc" font-size="14" font-weight="700">2. Firewall &amp; Gateway</text>
    <text x="430" y="517" fill="#c9d1d9" font-size="12">Open UDP 21235+ for match slaves</text>
    <text x="430" y="535" fill="#c9d1d9" font-size="12">&amp; register heartbeat with Master.</text>
    <rect x="430" y="565" width="240" height="32" rx="6" fill="#21262d" stroke="#d29922" stroke-width="1"/>
    <text x="550" y="586" text-anchor="middle" fill="#ffab70" font-size="11" font-weight="600">📖 Get Started as Host</text>
  </g>

  <!-- ==================== COLUMN 3: RUN -> CONTROL PANEL GUI ==================== -->
  <g filter="url(#shadow)">
    <rect x="760" y="235" width="300" height="415" rx="12" fill="url(#greenCardGrad)" stroke="#6366f1" stroke-width="1.5"/>
    
    <!-- Column Header -->
    <rect x="760" y="235" width="300" height="50" rx="12" fill="#6366f1" fill-opacity="0.25"/>
    <text x="910" y="267" text-anchor="middle" fill="#a5b4fc" font-size="16" font-weight="700">🖥️ NEXUS Control Panel GUI</text>

    <!-- Step 1 Card -->
    <rect x="775" y="300" width="270" height="145" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.2"/>
    <text x="790" y="328" fill="#f0f6fc" font-size="14" font-weight="700">1. Admin Desktop GUI</text>
    <text x="790" y="352" fill="#c9d1d9" font-size="12">Manage accounts, browse servers,</text>
    <text x="790" y="370" fill="#c9d1d9" font-size="12">User Secrets, &amp; 3D hero models.</text>
    <rect x="790" y="392" width="240" height="32" rx="6" fill="#21262d" stroke="#6366f1" stroke-width="1"/>
    <text x="910" y="413" text-anchor="middle" fill="#c7d2fe" font-size="11" font-weight="600">👉 src/apps/control-panel/README.md</text>

    <!-- Step 2 Card -->
    <rect x="775" y="465" width="270" height="160" rx="8" fill="#161b22" stroke="#30363d" stroke-width="1.2"/>
    <text x="790" y="493" fill="#f0f6fc" font-size="14" font-weight="700">2. Cluster &amp; Fleet Control</text>
    <text x="790" y="517" fill="#c9d1d9" font-size="12">1-command Aspire orchestration</text>
    <text x="790" y="535" fill="#c9d1d9" font-size="12">&amp; KONGOR Master Server stack.</text>
    <rect x="790" y="565" width="240" height="32" rx="6" fill="#21262d" stroke="#6366f1" stroke-width="1"/>
    <text x="910" y="586" text-anchor="middle" fill="#c7d2fe" font-size="11" font-weight="600">📖 Get Started with Admin GUI</text>
  </g>

</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated decision funnel SVG at: {filepath}")
    return filepath


def create_ecosystem_architecture_svg(filepath: str) -> str:
    """Generates the comprehensive ecosystem architecture SVG diagram."""
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 900" width="100%" height="100%" style="background-color: #0d1117; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
  <defs>
    <!-- Background Gradient -->
    <linearGradient id="bgGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161b22"/>
      <stop offset="100%" stop-color="#0d1117"/>
    </linearGradient>

    <!-- Shadow filter -->
    <filter id="shadow" x="-5%" y="-5%" width="110%" height="110%">
      <feDropShadow dx="0" dy="4" stdDeviation="5" flood-color="#000000" flood-opacity="0.5"/>
    </filter>
  </defs>

  <!-- Canvas Background -->
  <rect width="100%" height="100%" fill="url(#bgGrad)" rx="12"/>
  <rect width="100%" height="100%" fill="none" stroke="#30363d" stroke-width="1.5" rx="12"/>

  <!-- ==================== SUBGRAPH 1: CLIENTS (TOP LEFT) ==================== -->
  <g filter="url(#shadow)">
    <rect x="40" y="30" width="530" height="235" rx="10" fill="#161b22" stroke="#388bfd" stroke-width="1.5"/>
    <rect x="40" y="30" width="530" height="34" rx="10" fill="#1f6feb" fill-opacity="0.25"/>
    <text x="60" y="53" fill="#58a6ff" font-size="14" font-weight="700">👤 Player &amp; Administrative Clients</text>

    <!-- Node CP -->
    <rect x="60" y="80" width="145" height="75" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="132" y="105" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🖥️ NEXUS GUI</text>
    <text x="132" y="125" text-anchor="middle" fill="#8b949e" font-size="10">Avalonia 11 / .NET 10-11</text>
    <text x="132" y="142" text-anchor="middle" fill="#79c0ff" font-size="9" font-weight="600">Admin &amp; Control</text>

    <!-- Node WM -->
    <rect x="225" y="80" width="160" height="75" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="305" y="105" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🚀 WILLOWMAKER</text>
    <text x="305" y="125" text-anchor="middle" fill="#8b949e" font-size="10">Game Client Launcher</text>
    <text x="305" y="142" text-anchor="middle" fill="#79c0ff" font-size="9" font-weight="600">1000 FPS / CDN Sync</text>

    <!-- Node HON -->
    <rect x="405" y="80" width="145" height="75" rx="8" fill="#21262d" stroke="#58a6ff" stroke-width="1.2"/>
    <text x="477" y="105" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🎮 HoN Game</text>
    <text x="477" y="125" text-anchor="middle" fill="#8b949e" font-size="10">hon_x64.exe</text>
    <text x="477" y="142" text-anchor="middle" fill="#79c0ff" font-size="9" font-weight="600">K2 Engine / DXVK</text>

    <text x="60" y="180" fill="#c9d1d9" font-size="11">• NEXUS Control Panel: Secrets Vault, 3D Hero Viewer &amp; Fleet Management</text>
    <text x="60" y="202" fill="#c9d1d9" font-size="11">• WILLOWMAKER: Automatic CDN sync, 1000 FPS patcher, DXVK Vulkan &amp; RTX Remix</text>
    <text x="60" y="224" fill="#c9d1d9" font-size="11">• HoN Client: SRP authentication, binary chat stream &amp; low-latency UDP matches</text>
  </g>

  <!-- ==================== SUBGRAPH 2: HOSTING (TOP RIGHT) ==================== -->
  <g filter="url(#shadow)">
    <rect x="630" y="30" width="530" height="235" rx="10" fill="#161b22" stroke="#d29922" stroke-width="1.5"/>
    <rect x="630" y="30" width="530" height="34" rx="10" fill="#d29922" fill-opacity="0.25"/>
    <text x="650" y="53" fill="#f0883e" font-size="14" font-weight="700">🖥️ Dedicated Game Server Fleet</text>

    <!-- Node COMPEL -->
    <rect x="650" y="80" width="150" height="75" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="725" y="105" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">🛡️ COMPEL Manager</text>
    <text x="725" y="125" text-anchor="middle" fill="#8b949e" font-size="10">Win32 Job Sandbox</text>
    <text x="725" y="142" text-anchor="middle" fill="#ffab70" font-size="9" font-weight="600">1.0ms Timer Pacer</text>

    <!-- Node SLAVES -->
    <rect x="820" y="80" width="160" height="75" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="900" y="105" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚔️ HoN Match Slaves</text>
    <text x="900" y="125" text-anchor="middle" fill="#8b949e" font-size="10">UDP Ports 21235+</text>
    <text x="900" y="142" text-anchor="middle" fill="#ffab70" font-size="9" font-weight="600">Isolated Instances</text>

    <!-- Node PROXY -->
    <rect x="1000" y="80" width="140" height="75" rx="8" fill="#21262d" stroke="#d29922" stroke-width="1.2"/>
    <text x="1070" y="105" text-anchor="middle" fill="#f0f6fc" font-size="12" font-weight="700">⚡ UDP Proxy</text>
    <text x="1070" y="125" text-anchor="middle" fill="#8b949e" font-size="10">Anti-DDoS Shield</text>
    <text x="1070" y="142" text-anchor="middle" fill="#ffab70" font-size="9" font-weight="600">Challenge-Response</text>

    <text x="650" y="180" fill="#c9d1d9" font-size="11">• COMPEL: Spawns slaves inside memory-capped Win32 Job Object sandboxes</text>
    <text x="650" y="202" fill="#c9d1d9" font-size="11">• UDP Proxy: Challenge-response cookie validation &amp; packet rate limiting</text>
    <text x="650" y="224" fill="#c9d1d9" font-size="11">• Match Slaves: High-tickrate dedicated instances streaming stats to Master Server</text>
  </g>

  <!-- ==================== SUBGRAPH 3: MASTER SERVER STACK (MIDDLE) ==================== -->
  <g filter="url(#shadow)">
    <rect x="40" y="295" width="1120" height="240" rx="10" fill="#161b22" stroke="#2ea043" stroke-width="1.5"/>
    <rect x="40" y="295" width="1120" height="34" rx="10" fill="#2ea043" fill-opacity="0.25"/>
    <text x="60" y="318" fill="#56d364" font-size="14" font-weight="700">☁️ Master Server Microservice Stack (ASP.NET Core / .NET 10-11)</text>

    <!-- Node MS -->
    <rect x="60" y="345" width="200" height="100" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.5"/>
    <text x="160" y="373" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🏛️ KONGOR Master</text>
    <text x="160" y="393" text-anchor="middle" fill="#8b949e" font-size="11">Port 55555 (HTTP/REST)</text>
    <text x="160" y="413" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">SRP Auth &amp; Session Vault</text>
    <text x="160" y="431" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">Store, MMR &amp; Stats Engine</text>

    <!-- Node CHAT -->
    <rect x="280" y="345" width="190" height="100" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="375" y="373" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">💬 TRANSMUTANSTEIN</text>
    <text x="375" y="393" text-anchor="middle" fill="#8b949e" font-size="11">TCP 11031-11033</text>
    <text x="375" y="413" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">Binary HoN Protocol</text>
    <text x="375" y="431" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">Channels, Clans &amp; Whispers</text>

    <!-- Node API -->
    <rect x="490" y="345" width="190" height="100" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="585" y="373" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🔌 ZORGATH Web API</text>
    <text x="585" y="393" text-anchor="middle" fill="#8b949e" font-size="11">Ports 5200 / 5201</text>
    <text x="585" y="413" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">REST API / JWT Auth</text>
    <text x="585" y="431" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">Discord OAuth2 Bridge</text>

    <!-- Node UI -->
    <rect x="700" y="345" width="195" height="100" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="797" y="373" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🌐 DAWNBRINGER UI</text>
    <text x="797" y="393" text-anchor="middle" fill="#8b949e" font-size="11">Ports 5266 / 5557</text>
    <text x="797" y="413" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">ASP.NET MVC Web Portal</text>
    <text x="797" y="431" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">Player Profile &amp; Replays</text>

    <!-- Node RENDER -->
    <rect x="915" y="345" width="225" height="100" rx="8" fill="#21262d" stroke="#3fb950" stroke-width="1.2"/>
    <text x="1027" y="373" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🎬 Replay Video Renderer</text>
    <text x="1027" y="393" text-anchor="middle" fill="#8b949e" font-size="11">Headless Vulkan Renderer</text>
    <text x="1027" y="413" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">NVENC MP4 Transcoder</text>
    <text x="1027" y="431" text-anchor="middle" fill="#7ee787" font-size="10" font-weight="600">Automated Highlight Clips</text>

    <text x="60" y="480" fill="#c9d1d9" font-size="11">Core Responsibilities: High-throughput SRP authentication, legacy PHP serialization parser, asynchronous match stats ingestion,</text>
    <text x="60" y="502" fill="#c9d1d9" font-size="11">dynamic inventory sync (avatars/taunts), continuous leaderboards, and automated cloud replay storage pipeline.</text>
  </g>

  <!-- ==================== SUBGRAPH 4: STORAGE & PERSISTENCE (BOTTOM LEFT) ==================== -->
  <g filter="url(#shadow)">
    <rect x="40" y="565" width="620" height="305" rx="10" fill="#161b22" stroke="#a371f7" stroke-width="1.5"/>
    <rect x="40" y="565" width="620" height="34" rx="10" fill="#a371f7" fill-opacity="0.25"/>
    <text x="60" y="588" fill="#bc8cff" font-size="14" font-weight="700">💾 Storage &amp; Cache Layer</text>

    <!-- DB -->
    <rect x="60" y="615" width="180" height="115" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="150" y="643" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🗄️ MERRICK Database</text>
    <text x="150" y="665" text-anchor="middle" fill="#8b949e" font-size="11">EF Core Multi-Provider</text>
    <text x="150" y="688" text-anchor="middle" fill="#d2a8ff" font-size="10" font-weight="600">PostgreSQL / Supabase</text>
    <text x="150" y="708" text-anchor="middle" fill="#d2a8ff" font-size="10" font-weight="600">Microsoft SQL Server</text>

    <!-- REDIS -->
    <rect x="260" y="615" width="180" height="115" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="350" y="643" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">⚡ Garnet / Redis Cache</text>
    <text x="350" y="665" text-anchor="middle" fill="#8b949e" font-size="11">Port 28298 (Local/Cloud)</text>
    <text x="350" y="688" text-anchor="middle" fill="#d2a8ff" font-size="10" font-weight="600">Session State &amp; SRP Salt</text>
    <text x="350" y="708" text-anchor="middle" fill="#d2a8ff" font-size="10" font-weight="600">Pub/Sub Presence Hub</text>

    <!-- S3 -->
    <rect x="460" y="615" width="180" height="115" rx="8" fill="#21262d" stroke="#a371f7" stroke-width="1.2"/>
    <text x="550" y="643" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📦 S3 / Cloud Storage</text>
    <text x="550" y="665" text-anchor="middle" fill="#8b949e" font-size="11">Community / S3 CDN</text>
    <text x="550" y="688" text-anchor="middle" fill="#d2a8ff" font-size="10" font-weight="600">Client Patches &amp; Assets</text>
    <text x="550" y="708" text-anchor="middle" fill="#d2a8ff" font-size="10" font-weight="600">Match Replay Archive</text>

    <text x="60" y="765" fill="#c9d1d9" font-size="11">• MERRICK: Relational storage for accounts, match summaries, MMR rankings &amp; stats</text>
    <text x="60" y="790" fill="#c9d1d9" font-size="11">• Redis: Sub-millisecond session tokens, SRP verification, &amp; presence hubs</text>
    <text x="60" y="815" fill="#c9d1d9" font-size="11">• S3: Object storage for full .honreplay archives, CDN manifests &amp; mod skins</text>
  </g>

  <!-- ==================== SUBGRAPH 5: OBSERVABILITY & TELEMETRY (BOTTOM RIGHT) ==================== -->
  <g filter="url(#shadow)">
    <rect x="700" y="565" width="460" height="305" rx="10" fill="#161b22" stroke="#6366f1" stroke-width="1.5"/>
    <rect x="700" y="565" width="460" height="34" rx="10" fill="#6366f1" fill-opacity="0.25"/>
    <text x="720" y="588" fill="#a5b4fc" font-size="14" font-weight="700">⚙️ Orchestration &amp; Telemetry</text>

    <!-- ASPIRE -->
    <rect x="720" y="615" width="200" height="115" rx="8" fill="#21262d" stroke="#6366f1" stroke-width="1.2"/>
    <text x="820" y="643" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">📊 .NET Aspire Host</text>
    <text x="820" y="665" text-anchor="middle" fill="#8b949e" font-size="11">Dashboard: :5550</text>
    <text x="820" y="688" text-anchor="middle" fill="#c7d2fe" font-size="10" font-weight="600">Unified OTLP Metrics</text>
    <text x="820" y="708" text-anchor="middle" fill="#c7d2fe" font-size="10" font-weight="600">Distributed Trace Tree</text>

    <!-- PYROSCOPE -->
    <rect x="940" y="615" width="200" height="115" rx="8" fill="#21262d" stroke="#6366f1" stroke-width="1.2"/>
    <text x="1040" y="643" text-anchor="middle" fill="#f0f6fc" font-size="13" font-weight="700">🔥 Pyroscope Profiler</text>
    <text x="1040" y="665" text-anchor="middle" fill="#8b949e" font-size="11">Port 4040</text>
    <text x="1040" y="688" text-anchor="middle" fill="#c7d2fe" font-size="10" font-weight="600">Continuous CPU Profiler</text>
    <text x="1040" y="708" text-anchor="middle" fill="#c7d2fe" font-size="10" font-weight="600">Flamegraph Diagnostics</text>

    <text x="720" y="765" fill="#c9d1d9" font-size="11">• Aspire: Orchestrates all 9 backend microservices in a single dev command</text>
    <text x="720" y="790" fill="#c9d1d9" font-size="11">• OpenTelemetry: Collects real-time structured logs &amp; distributed traces</text>
    <text x="720" y="815" fill="#c9d1d9" font-size="11">• Pyroscope: Continuous CPU hot-path and memory allocation profiling</text>
  </g>

</svg>
"""
    os.makedirs(os.path.dirname(os.path.abspath(filepath)), exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(svg)
    logger.info(f"Generated ecosystem architecture SVG at: {filepath}")
    return filepath


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate Architecture and Decision Funnel SVG Charts")
    parser.add_argument("--output-dir", "-o", type=str, default=DEFAULT_OUTPUT_DIR, help="Destination directory for SVGs")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info(f"Generating charts into: {args.output_dir}")

    f1 = os.path.join(args.output_dir, "decision_funnel.svg")
    f2 = os.path.join(args.output_dir, "ecosystem_architecture.svg")

    create_decision_funnel_svg(f1)
    create_ecosystem_architecture_svg(f2)
    logger.info("All documentation charts generated successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
