# 🌸 Together As One (TAO) — MCP Documentation Hub

> *"We Are As One"*  
> — **Empath**, *Heroes of Newerth*

Welcome to the **Together As One (TAO) MCP Documentation Hub** for Project Kongor.

TAO unites the game client (`hon_x64.exe`), dedicated servers (`COMPEL`), the .NET Aspire microservices cluster, and AI pair-programming tools under a cohesive, self-healing JSON-RPC 2.0 interface.

---

## 📚 Complete TAO Documentation Suite

| Document | Description | Target Audience |
|---|---|---|
| **[`instructions.md`](./instructions.md)** | **Universal Integration & Installation Guide**: Step-by-step setup for Claude Desktop, Claude Code, GitHub Copilot, Cursor, Windsurf, and Antigravity, plus critical engine guardrails and automated installer details. | AI Agents, Developers |
| **[`architecture.md`](./architecture.md)** | **Full System Architecture Specification**: Deep technical dive into the Empath analogy, zero-lock shadow runtime in `%TEMP%`, Win32 Job Object sandbox guards, Zero-Focus-Theft Mailbox IPC (`CompositeGameIpcBridge`), and Tier 0 Vulkan swapchain frame grabbing. | System Architects, Engineers |
| **[`tools-catalog.md`](./tools-catalog.md)** | **Complete Tool Reference Catalog**: Domain-by-domain catalog of specialized `kongor_*` tools with arguments, behaviors, and examples. | AI Agents, Automation Devs |
| **[`self-healing.md`](./self-healing.md)** | **Autonomous Self-Healing Triangulation Architecture**: The closed-loop Self-Healing Hexad (TAO, Proximo, WinDbg, Ghidra, Willowmaker, Compel) for diagnosing and patching native crashes without manual debuggers. | Core Devs, AI Pair-Programmers |
| **[`../../.github/copilot-instructions.md`](../../.github/copilot-instructions.md)** | **GitHub Copilot Custom Workspace Guidelines**: Tailored prompt guidelines and engine constraints automatically indexed by GitHub Copilot Chat in VS Code and GitHub.com. | GitHub Copilot Chat |
| **[`../roadmaps/mcp-ecosystem-roadmap.md`](../roadmaps/mcp-ecosystem-roadmap.md)** | **Cross-Server Synergy & Ecosystem Roadmap**: The multi-phase roadmap unifying specialized MCP servers into an autonomous developer and modding fabric. | Technical Leads, Contributors |
| **[`../MCP_ISSUES.md`](../MCP_ISSUES.md)** | **MCP Living Issue & Prevention Backlog**: Living record of all MCP tool execution issues, failure analyses, logs, and concrete prevention rules to ensure first-time execution success. | All AI Agents & Devs |

---

## ⚡ 1-Second Quick Install

Run the automated universal installer from **Windows PowerShell**:
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev\install_tao_mcp.ps1
```

### What this automatically configures:
- ✅ **Anthropic Claude Desktop**: `%APPDATA%\Claude\claude_desktop_config.json`
- ✅ **GitHub Copilot / VS Code**: `.vscode/mcp.json`
- ✅ **Cursor IDE**: `.cursor/mcp.json` (workspace) and `~/.cursor/mcp.json` (global)
- ✅ **Google Antigravity**: `~/.gemini/antigravity/mcp/kongor/instructions.md`
- ✅ **Live Pre-Flight Smoke Test**: Validates live connection with `python src/tools/tao_mcp.py --tool kongor_diagnostics`

---

## 🧭 Identity Summary

- **MCP Server Identifier**: `kongor` *(or alias `tao`)*
- **Protocol Server Title**: `Together as One - KONGOR MCP (TAO)`
- **Tool Namespace Prefix**: `kongor_*` (specialized tools with zero collision against generic MCP tools)
- **Protocol Version**: `2024-11-05`
- **Canonical Python Launcher**: `src/tools/tao_mcp.py` (or `src/tools/MCP.py`)
- **Native .NET 11 Host**: `src/tools/kongor-cli/bin/Release/net11.0/kongor.dll mcp`
