# 🦍 Project Kongor V Developer Documentation Hub

Welcome to the **Project Kongor V (v5.0.2)** developer documentation and API reference hub.

Project Kongor V is the unified, high-performance Master Server, Dedicated Server Fleet Manager, Vulkan 1.3 Client Launcher, and 3D Asset Tooling ecosystem for Heroes of Newerth.

> [!IMPORTANT]
> ### ⚖️ Preservation, Attribution & Cleanroom Workspace Notice
> Project Kongor V (`PKV`) is an independent, non-profit video game preservation and interoperability initiative maintained by `guccidonut`.
> - **Cleanroom Source & Upstream Attribution**: Builds upon foundational `AGPL-3.0` open-source work by `[K]ONGOR` (`Project-KONGOR-Open-Source`) alongside newly authored PKV platform, native Vulkan/audio/guard (`MPL-2.0`), and TAO MCP (`AGPL-3.0-or-later`) modules. This public repository distributes **zero** retail *Heroes of Newerth* `.s2z` archives, textures, models, or executables (`0` Git LFS dependencies).
> - **Intellectual Property**: *Heroes of Newerth*™ and *Newerth*™ are registered trademarks of **Garena Online Private Limited** (and historically S2 Games and Frostburn Studios). This project is not affiliated with, endorsed by, or sponsored by Garena, S2 Games, Frostburn Studios, Kongor Studios, or `[K]ONGOR`.
> - **Social Contract**: Project Kongor V will **always and forever remain 100% free and open-source**. Read our full [Preservation Charter](charter.md).

---

## 🧭 Documentation Hub (The 4 Diataxis Quadrants)

### 🎓 1. Tutorials *(Learning & Onboarding)*
| Guide | Description | Link |
|---|---|---|
| **🛠️ Developer Setup & Onboarding** | Clone `--recurse-submodules`, configure User Secrets, and build the `.NET 11` monorepo | [Developer Guide](guides/developer-guide.md) |
| **🚀 What's New in 5.0, 5.0.1 & V5.1** | Queue Arcade, custom maps, Snapdragon ARM64, and subsystem capability matrix | [What's New](releases/whats-new-in-v5.md) • [5.0 & 5.0.1 Update Notes](releases/v5-update-notes.md) |
| **🎮 P2P & LAN Hosting Guide** | Step-by-step guide for hosting LAN games, offline bots, and direct connect | [P2P & LAN Guide](guides/p2p-lan-hosting-guide.md) |

### 🛠️ 2. How-To Guides *(Task & Problem Recipes)*
| Guide | Description | Link |
|---|---|---|
| **🤖 Vibe Coding & AI Agent Tips** | Model pairing (Thinking + Flash), offscreen TAO proof, worker locks, KB bumps, and debugging stack | [Vibe Coding Tips](how-to/vibe-coding-and-agent-tips.md) |
| **🎯 Surgical Client Override & VFS Workflow** | Locate assets in $<1\text{ ms}$ (`VfsIndexService`), extract to `override/`, hot-reload with zero focus theft, and bake `.s2z` | [Client VFS Workflow](how-to/client-override-vfs-workflow.md) |
| **🧪 Testing, Quality Gates & UI E2E** | Run the C# test suite, `KONGOR_RUN_MANUAL_TESTS=1`, `gate_check.ps1`, and the UI E2E harness | [Testing & Gates](how-to/testing-and-quality-gates.md) |
| **🎨 3D Art, Hero Forge & Models** | Create heroes, announcer packs, and import/export `.model`/`.mdf`/`.clip` in Blender 4.2+ | [Art & Assets Hub](art-and-assets/README.md) |
| **🐳 Turnkey Docker Stack & VPS Hosting** | Launch the full 7-container stack + Queue Arcade in 1 command (`kongor docker up`), Kubernetes/Agones, and VPS UDP relays | [Container Guide](infrastructure/container-hosting-guide.md) • [VPS Guide](infrastructure/vps-hosting-guide.md) |
| **🔧 Operations & Troubleshooting** | Diagnose port conflicts, crash dumps (`Everything` $\to$ `WinDbg` $\to$ `Ghidra`), and Obligation Ledgers | [Troubleshooting](operations/troubleshooting.md) |

### 📖 3. Reference *(Technical Specifications & Catalogs)*
| Reference | Description | Link |
|---|---|---|
| **🌸 Together As One (TAO) MCP Server** | Complete reference for **`kongor_*` MCP tools**, `CompositeGameIpcBridge`, and resources | [TAO MCP Overview](tao-mcp/overview.md) • [TAO Docs Hub](tao-mcp/README.md) |
| **📦 `kongor` CLI Command Reference** | Complete command matrix for `kongor play`, `host`, `gui`, `docker`, `start`, `mod`, `prebake`, and `mcp` | [CLI Reference](guides/cli-reference.md) |
| **🌐 Network Ports & IPC Matrix** | Authoritative TCP/UDP port allocations, Named Pipes, and shared memory channels | [Network Matrix](infrastructure/network-and-ports.md) |
| **🐉 Ghidra Reverse-Engineering MCP** | Pre-baked `ProjectKongor.rep` (7 core x64 binaries), RVA decompilation, and symbol search | [Ghidra Guide](tools/ghidra.md) |

### 🏛️ 4. Explanation *(Architecture, ADRs & Roadmaps)*
| Document | Description | Link |
|---|---|---|
| **🗺️ 5-Tier Ecosystem Topology** | Architectural blueprint spanning desktop apps, COMPEL fleet, Aspire services, and MERRICK DB | [View Architecture](architecture/ecosystem.md) |
| **🩹 Autonomous Self-Healing Hexad** | Closed-loop crash triage and live remediation across TAO, WinDbg, Ghidra, Frida, and NativeGuard | [Self-Healing Architecture](architecture/self-healing.md) |
| **📐 Architecture Decision Records** | ADR-0000 through ADR-0010 (template, SRP-6a permanence, MediatR contracts, Aspire, Agones, Argon2id, Goose) | [Browse ADRs](adr/README.md) |
| **🗺️ Engineering Roadmaps & Audits** | Strategic modernization horizons, native C++ hardening, and security audit reports | [Roadmaps](roadmaps/README.md) • [Security Audits](audits/README.md) |
| **📜 Charter, Community & AI Credits** | Preservation framework, tribute to HoN creators/community, and AI collaboration & educational notes | [Charter](charter.md) • [Credits](credits-and-gratitude.md) • [AI Gratitude](ai-gratitude-and-methodology.md) |

### 📋 5. Operational Ledgers *(Live Session & Issue Tracking)*
| Ledger | Description | Link |
|---|---|---|
| **🧠 Active Session State** | Durable cross-turn and cross-agent task state, hypotheses, and telemetry findings | [Active Session State](ACTIVE_SESSION_STATE.md) |
| **🩺 Living Issue Backlog** | Active system/engine issue tracker and historical resolved archive | [Issue Backlog](ISSUES.md) |
| **🤖 MCP Issue & Prevention Log** | Root-cause ledger and first-time prevention rules for MCP tool calls | [MCP Issues](MCP_ISSUES.md) |

---

## 🚀 Easy CLI Installation

Get up and running in seconds using our automated one-click installer in Windows PowerShell:

```powershell
# One-click global CLI installation
.\scripts\install-cli.ps1

# Run system health diagnostics
kongor doctor
kongor status
```

You can also run any command directly without global installation:
```powershell
.\kongor.ps1 <command>     # PowerShell
.\kongor.cmd <command>     # Command Prompt
```

---

## 🔍 In-Doc Search
Press **Ctrl + K** or use the top search bar to search across all classes, API endpoints, guides, and tools in real-time.
