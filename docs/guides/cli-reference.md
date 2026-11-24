# 💻 Project Kongor V Unified CLI Reference Guide

The `kongor` command-line tool provides a clean, unified entry point for all applications, microservices, mod management tools, entity prebakers, 3D asset pipelines, developer diagnostics, and natural language Q&A.

---

## 📦 CLI Installation & Execution Options

We provide multiple zero-friction ways to install and run `kongor`:

### 🌟 Option 1: One-Click Automated Installer (Recommended)
Run the automated installation script from the repository root in Windows PowerShell. It automatically resolves file locks, packages the CLI, and registers it globally:
```powershell
.\scripts\install-cli.ps1
```

### ⚡ Option 2: Zero-Install Direct Execution
Run any command immediately without global registration:
```powershell
# Windows PowerShell
.\kongor.ps1 <command>

# Windows Command Prompt
.\kongor.cmd <command>

# Direct dotnet execution
dotnet run --project src/tools/kongor-cli/Kongor.Cli.csproj -- <command>
```

### 🔄 Option 3: Instant Global Self-Update
Once installed globally, update your installation at any time with a single command:
```powershell
kongor refresh
```

### 🛠️ Option 4: Manual Global Installation
```powershell
# 1. Package the CLI tool into a NuGet package
dotnet pack src/tools/kongor-cli/Kongor.Cli.csproj -c Release -o src/tools/kongor-cli/nupkg

# 2. Install globally as a dotnet tool
dotnet tool install -g --add-source src/tools/kongor-cli/nupkg Kongor.Cli
```

---

## 🏛️ Architecture & Modernization

* **Powered by `Spectre.Console.Cli`**: The CLI is built on a modular command tree pattern (`src/tools/kongor-cli/Commands/`), replacing legacy `System.CommandLine` beta packages with high-performance, strongly-typed command routing and auto-generated ANSI terminal help.
* **Decoupled Headless UI Renderer (`Kongor.HeadlessRenderer`)**: Heavy desktop GUI packages (`Avalonia.Headless`, `Avalonia.Skia`, `FluentTheme`) have been decoupled into a dedicated standalone rendering tool (`src/tools/headless-renderer/`). This slims the `kongor` CLI tool package by over **85%** (from ~180 MB to ~25 MB) for instant cold starts and minimal disk footprints.

---

## 📋 Complete Commands Matrix


### 🎮 1. Core Applications & Microservices
```bash
# Launch game client (WILLOWMAKER with Vulkan 1.3, 1000 FPS, CDN sync)
kongor play
kongor launcher
kongor willowmaker

# Host dedicated server fleet (COMPEL with Win32 Job Object sandbox)
kongor host
kongor compel
kongor server

# Launch .NET Aspire local cluster (4 core backend services + Garnet/Redis & optional sidecars; Dashboard: https://localhost:5550)
kongor start
kongor aspire
kongor cluster

# Turnkey Docker Compose Stack (Cluster + Dedicated Fleet :11334 + Queue Arcade :11484 + Optional Host Client)
kongor docker init-secrets
kongor docker check
kongor docker up --perf Balanced --client
kongor docker up --perf Eco --local-postgres
kongor docker up --perf High --stop-host-conflict --launcher
kongor docker status
kongor docker logs -s master-server -f
kongor docker down

# Launch NEXUS desktop administration GUI (Avalonia 11 / 3D viewport / 🐳 Docker Stack controls)
kongor gui
kongor control-panel
kongor nexus

# Build all projects across Debug and Release configurations
kongor build
kongor build -c Release
kongor build apps -c Release
kongor build services -c Release
kongor build libs -c Release
kongor build tools -c Release
kongor build willowmaker -c Release
kongor build --clean --restore

# Run individual backend services standalone
kongor master-server
kongor chat-server
kongor web-api
kongor web-ui
```

---

### 📦 2. Modding & Package Management (`kongor mod`)
The `kongor mod` command manages `.honmod` and `.s2z` mod archives, DAG dependency ordering, conflict resolution, rollback journals, and AST repairs:

```bash
# List all discovered mods with enabled/disabled status and metadata:
kongor mod list

# Enable one or more mods:
kongor mod enable "Hero_Alt_Devourer"
kongor mod enable "CustomUI"

# Disable one or more mods (or 'all' to disable all active mods):
kongor mod disable "Hero_Alt_Devourer"
kongor mod disable all

# Inspect detailed manifest, dependencies, and target files of a mod:
kongor mod inspect "CustomUI"

# Apply all enabled mods with DAG ordering to game client (vfs or archive):
kongor mod apply

# Revert all applied modifications cleanly using rollback journal:
kongor mod rollback

# Run dependency checking, cycle detection, and collision matrix tests:
kongor mod verify

# Harvest community mods and metadata from Trello catalog:
kongor mod harvest

# Repair corrupted XML/Lua AST entities in mods:
kongor mod repair
```

---

### ⚡ 3. Entity XML Prebaking & Binary Serialization (`kongor prebake`)
The `kongor prebake` pipeline harvests game entity XMLs and serializes them into ultra-compact, zero-allocation binary caches:

```bash
# Execute full pre-baking pipeline (parse, serialize, lua cache, s2z bundle, stage):
kongor prebake all

# Parse game entity XMLs and serialize to entity_cache.bin (MemoryPack + Zstd):
kongor prebake entities

# Generate hero_definitions_cache.lua for instant client UI resolution:
kongor prebake lua

# Package pre-baked assets into resources0_prebaked.s2z archive:
kongor prebake bundle
```

---

### 🎨 4. 3D Hero Model & Animation Exporter (`kongor model`)
```bash
# List all discoverable heroes and named alt avatar skins:
kongor model list
kongor model list rhapsody
kongor model list devourer

# Export specific hero or alt avatar to glTF / GLB with full armature & animations:
kongor model export "thriller rhapsody"
kongor model export "clown devourer"
kongor model export rhapsody/alt
kongor model export empath

# Batch export all skins for a hero:
kongor model export rhapsody

# Custom output destination and framerate:
kongor model export empath -o output/models/empath.glb --fps 30
```

---

### 🖌️ 5. Blender 3D & HoN Pipeline (`kongor blender`)
```bash
# Launch Blender 3D with io_scene_hon add-on:
kongor blender

# Open a specific .blend file in Blender:
kongor blender path/to/scene.blend

# Automatically download and install portable Blender 4.2+ LTS and io_scene_hon add-on:
kongor blender install
kongor install_blender

# Stop all running Blender processes:
kongor blender stop

# Cleanly uninstall portable Blender and delete downloaded archives:
kongor blender uninstall
kongor uninstall_blender
```

---

### 🤖 6. ComfyUI Generative AI & MCP Server (`kongor comfy`)
```bash
# Launch ComfyUI generative AI server on port 8188:
kongor comfy
kongor comfyui

# Automatically download, install, and configure ComfyUI, PyTorch CUDA/MPS, and MCP server:
kongor comfy install
kongor install_comfyui

# Stop running ComfyUI server and MCP instances:
kongor comfy stop

# Cleanly uninstall ComfyUI repository, virtual environment, and caches:
kongor comfy uninstall
kongor uninstall_comfyui

# Run ComfyUI Model Context Protocol (MCP) server for AI agent pairing:
kongor comfyui_mcp
```

---

### 🎬 7. Cinematic Replay & Highlight Clipper (`kongor replay`)
```bash
# Render replay locked onto Player 1 POV (1..10) in 60FPS MP4:
kongor replay render games/client/M2060568725.honreplay --player 1 --fps 60

# Auto-detect and render a game-winning teamfight highlight clip:
kongor replay highlight games/client/M2060568725.honreplay
kongor highlight games/client/M2060568725.honreplay

# Inspect detected winning teamfight & MVP stats (dry-run):
kongor replay highlight games/client/M2060568725.honreplay --dry-run
```

---

### 🔬 8. Developer Diagnostics, Verification & Sandbox
```bash
# HoN network client proxy, test supervisor & log tailer:
kongor proxy
kongor proxy --game-path "C:/HoN"

# Extract Lua/XML SubmitForm routes & reconcile API endpoints against Master Server:
kongor audit-endpoints
kongor audit

# Headless NLua sandbox mocking K2 UI widgets & PHP/Base64 serialization:
kongor lua-verify
kongor lua

# Headless K2 engine & cvar simulator (consolidated into lua-verifier):
kongor k2-test
kongor k2

# Sub-router for additional developer tools (docgen, blender, test-e2e):
kongor tools docgen
kongor tools blender
kongor tools test-e2e
```

---

### 🤖 9. Natural Language Codebase Q&A (`kongor ask` / `kongor ?`)
```bash
# Ask any codebase, server hosting, gameplay, or asset export question:
kongor ask "how do i play?"
kongor ask "how does the 1000 fps patch work in EnginePatchService?"
kongor ask "how can i export the thriller rhapsody model to blender?"

# Fast shortcut syntax with '?' or 'ai':
kongor ? how do i host a dedicated server?
kongor ai what does ArtefactsLocator do?
```

---

### 🩺 10. Diagnostics & Documentation
```bash
# Show ecosystem topology, running service ports, Docker containers, COMPEL worker fleets, and PIDs:
kongor status
kongor info

# Run system diagnostic health checks on ports, dependencies, and active kongor-stack Docker containers:
kongor doctor
kongor check

# Launch and view the interactive documentation portal (http://localhost:8080):
kongor docs
kongor docfx
```

---

### 📸 10a. Window & Vulkan Swapchain Capture (`kongor capture`)
```bash
# Capture live screenshot of game client, server, or desktop window to PNG:
kongor capture
kongor screenshot
kongor capture-window

# Target a specific process and output path:
kongor capture -t hon_x64 -o artifacts/game_frame.png

# Explicitly select capture method ('auto', 'vulkan_offscreen', 'print_window', 'bitblt', 'transient_expose'):
kongor capture -t hon_x64 -m vulkan_offscreen
```

---

### 🧪 11. Test Suites & Coverage (`kongor test`)
```bash
# Run all test suites across the repository (uses --no-build by default):
kongor test

# Run all unit tests (tests/unit):
kongor test unit

# Run all integration tests (tests/integration):
kongor test integration

# Run a specific test project by name:
kongor test core
kongor test master
kongor test compel

# Run tests with code coverage collection:
kongor test unit --coverage

# Force a project build before testing:
kongor test --build

# Filter tests by class or test name expression:
kongor test --filter "FullyQualifiedName~EnginePatch"
```

---

### 🧹 12. Deep-Clean Pipeline (`kongor clean`)
```bash
# Deep-clean all bin/, obj/, TestResults/, and temporary build artifacts across all projects:
kongor clean

# Also purge virtual environments, temporary video outputs, and docfx caches:
kongor clean --all
```

---

### 🔒 13. Centralized User Secrets Vault (`kongor secret`)
```bash
# List all secrets stored in the canonical vault (00005072-6f6a-6563-7420-4b4f4e474f52):
kongor secret list
kongor secret

# Initialize the vault:
kongor secret init

# Set a configuration value:
kongor secret set "Database:Provider" "PostgreSQL"
kongor secret set "Supabase:Url" "https://my-instance.supabase.co"

# Retrieve a secret:
kongor secret get "Database:Provider"

# Remove a secret key or clear the vault:
kongor secret remove "Database:Provider"
kongor secret clear
```

---

### 📜 14. Microservice Logs & Live Tail (`kongor logs`)
```bash
# View the latest log output across all services:
kongor logs

# Inspect the most recent Master Server log (shows last 50 lines by default):
kongor logs master

# Inspect Chat Server logs:
kongor logs chat

# Live stream/follow log updates in real time (press Ctrl+C to exit):
kongor logs master --tail
kongor logs compel -t

# Show a custom number of lines:
kongor logs master -n 100
```

---

### 🛑 15. Process & Service Lifecycle (`kongor stop` / `kongor kill`)
```bash
# Stop all running ProjectKongor .NET backend services, servers, and apps on host:
kongor stop
kongor stop dotnet

# Tear down the turnkey Docker Compose stack (kongor-stack):
kongor stop docker

# Stop everything (host .NET services + Docker stack + ComfyUI + Blender) in one command:
kongor stop all

# Stop active ComfyUI server and MCP instances:
kongor stop comfy

# Stop any running Blender processes:
kongor stop blender
```

---

### 🔄 16. CLI Self-Update & Refresh (`kongor refresh`)
```bash
# Re-compile, package, and reinstall the global Kongor.Cli tool with latest local changes:
kongor refresh
kongor reinstall
kongor self-update
kongor update-cli
```

---

### 🔄 17. Ecosystem Versioning & Synchronization
```bash
# Display the current ecosystem version table (SemVer 5.0.0, CalVer, Engine Protocol):
kongor version
kongor -v
kongor --version

# Synchronize all projects, assemblies, contracts, and mod manifests to 5.0.0 (or any target version):
kongor sync-version 5.0.0

# Synchronize with custom build date and engine protocol:
kongor sync-version 5.0.0 --build-date 2026.09.04 --engine-protocol 5.0.0 --min-engine-protocol 4.10.1
```
