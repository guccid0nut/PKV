<!-- SPDX-License-Identifier: CC-BY-NC-SA-4.0 -->
<!-- SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors -->

# 🤖 Gratitude & Educational Notes: AI-Assisted Engineering on Project KONGOR V

<div align="center">

*Recognizing the AI models, research teams, and agentic tools that helped accelerate Project KONGOR V—and sharing practical lessons for students, hobbyists, and engineers.*

</div>

---

## 🙏 1. Why We Share This Openly

Reviving, modernizing, and documenting a multi-language preservation stack—spanning `.NET 11` backend services, Avalonia desktop tools, x64 C++ engine shims, Vulkan/DXVK interop, K2 XML schemas, and hundreds of Lua scripts—as an independent, zero-budget hobbyist effort (`guccidonut`) would have taken many years without modern AI coding collaborators.

We share this document for two reasons:
1. **Gratitude & Transparency:** To openly recognize the AI models, research labs, and developer toolmakers whose systems helped draft, debug, review, and test parts of **Project KONGOR V (`PKV`)**.
2. **Educational Value:** So other solo maintainers, students, and open-source preservationists can see candidly *which* models were used, *where* each shone, *where* AI assistance falls short without guardrails, and *how* human direction + deterministic verification kept the codebase grounded.

---

## 🌟 2. Gratitude to the AI Models & Platforms We Worked With

### 🧠 Core Engineering, Reasoning & Coding Collaborators

- **Google / Google DeepMind — `Gemini` (Thinking & Flash Models) & `Google Antigravity`**
  - **Where it helped:** Upfront systems planning, multi-file `.NET 11` / C# service and CLI architecture, native C++ (`NativeGuard` / Vulkan interop) debugging, K2 Lua/XML UI repairs, cross-platform Linux/macOS packaging audits, and rapid offscreen runtime verification via the **TAO (`Together As One`) MCP** server.
  - **Educational takeaway:** Pairing deeper reasoning ("Thinking") passes for architecture and edge-case planning with fast ("Flash") passes for surgical edits, log triage, and UI inspection gave a strong balance of depth and iteration speed.

- **Anthropic — `Claude` (`Opus` & `Sonnet` Models), `Claude Code` / `Claude Desktop`, & the `Model Context Protocol (MCP)`**
  - **Where it helped:** Deep architectural and adversarial code reviews, subtle concurrency and memory-lifetime bug hunting across C# and C++, cleanroom/licensing governance audits, and multi-step terminal debugging in `Claude Code`.
  - **Special gratitude for MCP:** Anthropic's decision to publish the **Model Context Protocol (MCP)** as an open standard directly inspired and enabled Project KONGOR V's built-in [`TAO` MCP server](tao-mcp/overview.md), allowing coding agents to inspect VFS archives, evaluate Lua, and verify fixes against real engine state instead of guessing.

- **JetBrains — `Junie` Autonomous Coding Agent, `JetBrains AI`, `PyCharm`, & `Rider`**
  - **Where it helped:** Early and ongoing monorepo exploration, structured codebase health scorecards, C#/.NET and Python refactoring inside **Rider** and **PyCharm**, and synthesizing early phased modernization plans (`junies_recommendation_roadmap.md`).
  - **Educational takeaway:** Having an agent deeply integrated with IDE project indexes and static inspections helped surface structural cleanup opportunities early in the monorepo consolidation.

### 🔄 Additional Coding Models & Multi-Agent Environments

- **OpenAI — `GPT` & `Codex` Models / CLI**
  - **Where it helped:** Independent second-opinion plan critiques, protocol/serialization sanity checks, and cross-examining assumptions made by other models (`kongor mcp` exports native `~/.codex/config.toml` configuration via `IdeConfigExporter`).
- **AI-Native Editors & IDE Hosts (`Cursor`, `VS Code` / `GitHub Copilot`, `Windsurf`, `Zed`)**
  - **Where they helped:** Testing and supporting contributor workflows across different MCP-capable editors so developers are never locked into a single tool.

### 🎨 Specialized Audio, Voice & Creative ML Tools *(Experimental / Opt-In Creator Workflows)*

- **`RNNoise` (Jean-Marc Valin / Xiph.Org — `BSD-3-Clause`):** Compact recurrent neural network explored for experimental real-time voice chat noise suppression.
- **ElevenLabs & `Voicebox` (Local TTS Studio):** Optional voice synthesis tools explored in experimental creator pipelines ([`announcer-forge.md`](art-and-assets/announcer-forge.md)) for prototyping custom announcer cues.
- **ComfyUI, Stable Diffusion (`SDXL` / `SD 1.5`), ControlNet & 3D Prototyping Tools (`Meshy`):** Optional local/creative workflows explored for concept art, optional `.honmod` visual map themes, and 3D asset experiments.

---

## 🎓 3. Educational Notes: What Actually Worked (and What Didn't)

For anyone studying AI-assisted software engineering on legacy or systems-level codebases, here are the most important lessons we learned the hard way:

### 1. Human Direction First ("Iron Man Suit", Not Autopilot)
AI models are powerful force multipliers, not replacements for engineering judgment or product care. Models do not inherently know what makes *Heroes of Newerth* feel responsive, where scope creep should stop, or when a "clever" abstraction is worse than a 10-line fix. Every architectural boundary, preservation rule, and release decision remained human-directed, with explicit review pauses before destructive or release actions.

### 2. Multi-Model Cross-Examination Beats Single-Model Confidence
Every model family has blind spots and a tendency to sound confident even when an assumption is subtly wrong. One of our highest-leverage habits was **cross-model adversarial review**:
- Have one model draft a concrete implementation plan with explicit invariants.
- Ask a *different* model (or a fresh-context critic pass) to poke holes in concurrency, OS portability, file locking, and rollback safety *before* writing code.
- Ten minutes of cross-examining a plan routinely prevented hours of debugging downstream.

### 3. Give Models Ground-Truth Oracles Instead of Letting Them Guess
When working against compiled x64 game binaries (`k2_x64.dll`, `cgame_x64.dll`, `game_x64.dll`) and custom `.s2z` archives, ungrounded language models will guess struct offsets, invent Lua widget APIs, or misread VFS override precedence. Building deterministic tool bridges changed everything:
- **Live Engine & VFS Inspection (`TAO` MCP):** Resolving exact VFS winning paths (`override/` vs. `.s2z`), hot-reloading UI, and reading real engine logs.
- **Crash & Binary Triangulation:** Pairing sub-millisecond dump lookup (`Everything`) $\to$ stack/register extraction (`WinDbg` / `cdb`) $\to$ dual-oracle decompilation (`Ghidra` + headless `IDA`) $\to$ live hook verification (`Frida`).

### 4. Fast Gates & Institutional Memory ("Bowling Bumpers")
Because AI agents can write code quickly, they can also introduce regressions quickly if left unchecked. Three mechanisms kept velocity from turning into rot:
- **Sub-5-Second Quality Gates (`scripts/gate_check.ps1`):** Checking banned APIs, Lua 5.1 bytecode (`luac51` + `LuaVerifier`), XML schemas, VFS parity, and reachability on every change.
- **Concurrency & Resource Locks:** File-based mutexes (`kongor_worker_lock.ps1` and `Invoke-KongorTestLock.ps1`) so parallel agents never clobber MSBuild outputs or fight over a running game client.
- **Persistent Project Memory:** Recording root causes and dead ends in [`ACTIVE_SESSION_STATE.md`](ACTIVE_SESSION_STATE.md), Field Notes (`FN-NNN`), and [`MCP_ISSUES.md`](MCP_ISSUES.md) so future sessions inherit hard-won lessons instead of repeating mistakes.
- *(For practical day-to-day workflow tips, see [`Tips for Vibe Coders & AI-Assisted Development`](how-to/vibe-coding-and-agent-tips.md).)*

### 5. Candid Realism: AI Code Still Needs Real Testing & Humility
AI assistance does not make software bug-free. Plenty of early drafts failed under real 20-player load, Linux case-sensitive filesystems, Wine/DXVK edge cases, or 720p UI scaling until caught by unit tests, headless harnesses, or human playtesting. Under-promising, marking experimental features honestly (`WIP` / `Experimental` / `Early AI`), and verifying against real runtime behavior matters even more—not less—when AI tools are in the loop.

---

## ⚖️ 4. Authorship, Licensing & Non-Affiliation Clarity

- **Human Stewardship & Open-Source Licensing:** All architectural direction, problem selection, integration, verification, and curation in Project KONGOR V are directed and maintained by `guccidonut` and human contributors, building upon foundational open-source work credited in [`NOTICE`](../NOTICE) and [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md). Repository source code is distributed under its stated open-source licenses (`AGPL-3.0-or-later` and `MPL-2.0`), and documentation under `CC BY-NC-SA 4.0` ([`docs/LICENSE.md`](LICENSE.md)).
- **No Endorsement or Affiliation:** Google, Google DeepMind, Anthropic, JetBrains, OpenAI, Cursor, GitHub, Microsoft, ElevenLabs, Xiph.Org, and all other companies, research labs, and product names mentioned in this document are the trademarks of their respective owners. None of these organizations are affiliated with, sponsor, or endorse Project KONGOR V.

---

<div align="center">

*To the researchers, engineers, and open-standards builders pushing developer tooling forward—thank you for helping small community preservation projects punch above their weight.*

**See also:** [Preservation Charter](charter.md) • [Community & Developer Credits](credits-and-gratitude.md) • [Vibe Coding & Agent Tips](how-to/vibe-coding-and-agent-tips.md)

</div>
