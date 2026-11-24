# 🤖 Optional Agent & Asset Tooling Setup Guide

Project KONGOR V (`PKV`) compiles, runs, and passes all unit and integration tests out of the box from a cleanroom `git clone --recurse-submodules` checkout (**zero** external binary tools or extra submodules required beyond `native/deps/rmlui`).

If you are doing **3D asset modding**, **DDS texture baking**, **C++ symbol navigation**, **DLL proxy scaffolding**, **local TTS voice prototyping**, or **AI-agent-assisted debugging**, you (or your AI coding agent) can install any of the optional developer tools below in minutes.

---

## ⚡ One-Command Bootstrap (Recommended)

Preview what will be installed (non-destructive dry run):

```powershell
.\scripts\Install-OptionalAgentTools.ps1 -WhatIf
```

Install all open-source CLI & reference tools (`clangd`, `texconv`, `everything`, `ffmpeg`, `proximo`, `k2-blender`, `voicebox`):

```powershell
.\scripts\Install-OptionalAgentTools.ps1 -Tool All
```

Or install only the specific tool(s) you need:

```powershell
.\scripts\Install-OptionalAgentTools.ps1 -Tool clangd,texconv,everything
```

---

## 📋 Copy-Paste Prompt for Your AI Coding Agent

If you use an AI coding assistant (Antigravity, Claude Code, Cursor, Windsurf, Copilot, Codex), paste this prompt directly into your agent chat:

```text
Read docs/getting-started/optional-agent-tools.md and run .\scripts\Install-OptionalAgentTools.ps1 -WhatIf first to inspect missing optional tools on my machine. Ask me which optional tools I want enabled, install them via .\scripts\Install-OptionalAgentTools.ps1, and verify each binary/repo path without committing downloaded binaries to Git.
```

---

## 🧰 Optional Tool Catalog & Licenses

None of the directories below are committed to the cleanroom GitHub repository (keeping clones fast, zero-LFS, and binary-free). The bootstrap script places each tool in the exact local path expected by Project KONGOR V scripts and MCP configurations:

| Tool Key (`-Tool`) | Target Local Path | Upstream Source | License | Purpose |
| :--- | :--- | :--- | :--- | :--- |
| `clangd` | `src/tools/clangd/` (or system `PATH`) | `LLVM.LLVM` (`winget`) / [clangd releases](https://github.com/clangd/clangd/releases) | `Apache-2.0 WITH LLVM-exception` | Fast C++20 AST indexing & symbol navigation for `native/` |
| `texconv` | `src/tools/texconv/` | [Microsoft DirectXTex](https://github.com/microsoft/DirectXTex/releases) | `MIT` | DirectX DDS / BC1–BC7 / RXGB normal-map inspection & conversion |
| `everything` | `src/tools/everything/` (or system `PATH`) | `voidtools.Everything` + `voidtools.Everything.Cli` (`es.exe`) | Freeware (`voidtools`) | Sub-millisecond `*.log` & `*.dmp` crash dump discovery via Everything MCP |
| `ffmpeg` | System `PATH` | `Gyan.FFmpeg` (`winget`) | `LGPL-2.1+` / `GPL-2.0+` | Media probing, cinematic capture transcoding & audio slicing |
| `proximo` | `src/tools/proximo/` | [Piotrixek/Proximo](https://github.com/Piotrixek/Proximo) | `GPL-3.0` | Standalone Win32 DLL proxy analyzer & CMake generator MCP server |
| `k2-blender` | `src/tools/blender/reference/K2-Blender-master/` | [KRAT0Sz/K2-Blender-master](https://github.com/KRAT0Sz/K2-Blender-master) | `MIT` | Historical K2 `.model` / `.clip` format reference alongside PKV's built-in `src/tools/blender/io_scene_hon/` |
| `voicebox` | `src/tools/voicebox/voicebox-repo/` | [jamiepine/voicebox](https://github.com/jamiepine/voicebox) | `MIT` | Optional local TTS announcer & hero voice-line prototyping studio |
| `mixamo-mcp` *(opt-in only)* | `src/tools/mixamo-mcp/` | [v1xel/mixamo-mcp](https://github.com/v1xel/mixamo-mcp) | *No upstream license file (All Rights Reserved)* | Optional personal Mixamo animation fetch helper; excluded from `-Tool All` unless explicitly requested via `-Tool mixamo-mcp -AcceptUnlicensedThirdParty` |

---

## 🔍 Manual Verification Commands

After running `.\scripts\Install-OptionalAgentTools.ps1`, run the ecosystem health check to confirm tool detection:

```powershell
.\kongor.ps1 doctor
```

Check individual binaries in PowerShell:

```powershell
Get-Command clangd, ffmpeg, es -ErrorAction SilentlyContinue | Format-Table Name, Source
Test-Path .\src\tools\texconv\texconv.exe
```

> [!IMPORTANT]
> **Git Hygiene:** Downloaded `.exe`/`.dll` binaries and optional tool clones under `src/tools/` stay local on your workstation and are automatically excluded from cleanroom exports by `config/public_export_manifest.json`. Never force-add `.exe` or `.dll` files to Git commits.
