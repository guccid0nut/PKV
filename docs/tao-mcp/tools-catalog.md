# 🧰 TAO MCP Server — Complete Tool Reference Catalog

> *"We Are As One"*  
> — **Empath**, *Heroes of Newerth*

This document provides a domain-by-domain reference of the **specialized MCP tools** exposed by the **Together As One (TAO)** MCP server (`kongor`).

All tools use the strict namespace prefix `kongor_*` (with transparent canonical short aliases) to guarantee zero collisions with generic MCP servers.

---

## 📑 Table of Contents

1. [🎮 Client Lifecycle & Runtime Management](#1--client-lifecycle--runtime-management)
2. [🛡️ Dedicated Server Fleet & Match Orchestration](#2--dedicated-server-fleet--match-orchestration)
3. [🖼️ UI Automation, Navigation & Headless Rendering](#3--ui-automation-navigation--headless-rendering)
4. [📦 Virtual Filesystem (VFS), Modding & Asset Pipeline](#4--virtual-filesystem-vfs-modding--asset-pipeline)
5. [🧠 Engine Introspection, Dynamic Memory & Hot-Reload](#5--engine-introspection-dynamic-memory--hot-reload)
6. [🔬 Diagnostics, Memory Leaks & Crash Dump Triage](#6--diagnostics-memory-leaks--crash-dump-triage)
7. [🗄️ Database, Analytics, Replays & Multi-Instance Desync Analysis](#7--database-analytics-replays--multi-instance-desync-analysis)
8. [⚡ Resilience, Chaos Engineering & Bot Choreography](#8--resilience-chaos-engineering--bot-choreography)
9. [🩺 Autonomous Self-Healing, Profiling & AI Vector Search](#9--autonomous-self-healing-profiling--ai-vector-search)
10. [🎯 Client Dev Velocity, Mailbox IPC & Store/Model Verification](#10--client-dev-velocity-mailbox-ipc--storemodel-verification)

---

## 1. 🎮 Client Lifecycle & Runtime Management

### `kongor_game_client_manage`
- **Aliases**: `kongor_game_client_manager`
- **Purpose**: Controls the player game client binary (`hon_x64.exe`). Manages process lifecycle with full VFS mod mounts, DXVK Vulkan settings, and low-latency audio.
- **Key Arguments**:
  - `action` (string, required): `"launch"` | `"status"` | `"stop"` | `"restart"` | `"errors"`
  - `mode` (string, optional): `"practice"` | `"bot_match"` | `"connect"`
  - `resolution` (string, optional): e.g. `"1920x1080"`
  - `connectAddress` (string, optional): IP:Port string (e.g. `"127.0.0.1:11235"`)
- **Example**:
  ```json
  { "name": "kongor_game_client_manage", "arguments": { "action": "launch", "mode": "practice" } }
  ```

### `kongor_game_command`
- **Purpose**: Unified remote console command router and multiplexer. Routes commands to running client (`\\.\pipe\willowmaker-ipc`), dedicated servers (`\\.\pipe\hon-server-{port}` / `stdin`), or both simultaneously with automatic self-healing.
- **Key Arguments**:
  - `command` (string, required): Console command string (e.g. `"Set vid_maxFPS 1000"`)
  - `target` (string, optional): `"client"` | `"server"` | `"both"` (default: `"client"`)
  - `port` (int, optional): Dedicated server listen port (required if `target="server"`)
  - `autoLaunch` (bool, optional): Automatically launches target client/server if not active
- **Example**:
  ```json
  { "name": "kongor_game_command", "arguments": { "command": "Set dev true" } }
  ```

### `kongor_game_client_command`
- **Purpose**: Dedicated execution channel targeting the player game client console via IPC without risk of routing to dedicated servers.
- **Key Arguments**:
  - `command` (string, required): Console command to execute in the player client
  - `autoLaunch` (bool, optional): Auto-launch client if offline (default: `true`)
  - `timeoutMs` (int, optional): Execution timeout in milliseconds (default: `5000`)
- **Example**:
  ```json
  { "name": "kongor_game_client_command", "arguments": { "command": "Echo Hello Newerth" } }
  ```

### `kongor_game_lua`
- **Aliases**: `kongor_lua`, `kongor_game_lua_eval`
- **Purpose**: Evaluates or executes Lua scripts within the running K2 engine interface without mouse simulation. Features pre-execution sandbox guardrails blocking forbidden `io.*`/`os.*` operations, automated colon method check rewriting (`widget:Method and` $\to$ `widget.Method and`, `[MCP-078]`), and `CInterface:GetRootWidget()` remediation (`[MCP-079]`).
- **Key Arguments**:
  - `code` / `script` (string, required): Lua expression or script block
  - `action` (string, optional): `"eval"` | `"exec"` | `"code"` (default: `"eval"`)
  - `target` (string, optional): `"client"` | `"server"` | `"both"` | `"pid:<id>"` (default: `"client"`)
  - `port` (int, optional): Server port (if targeting server)
- **Example**:
  ```json
  { "name": "kongor_game_lua", "arguments": { "code": "UIManager.GetInterface('main'):GetWidget('sysbar_quickStart'):DoEvent()" } }
  ```

### `kongor_game_manage`
- **Purpose**: High-level lifecycle router automatically redirecting to `kongor_game_client_manage` or `kongor_willowmaker_manage`.
- **Key Arguments**:
  - `action` (string, required): `"start"` | `"stop"` | `"restart"` | `"status"`
- **Example**:
  ```json
  { "name": "kongor_game_manage", "arguments": { "action": "status" } }
  ```

### `kongor_game_telemetry`
- **Purpose**: Reads instantaneous engine telemetry: FPS, frame pacing percentiles, working set memory, active map, and player counts.
- **Key Arguments**:
  - `timeoutMs` (int, optional): Read timeout in milliseconds (default: `2000`)
- **Example**:
  ```json
  { "name": "kongor_game_telemetry", "arguments": { "timeoutMs": 2000 } }
  ```

### `kongor_game_action`
- **Purpose**: Low-overhead health ping and state probe against the client IPC bridge.
- **Key Arguments**:
  - `action` (string, required): `"ping"` | `"state"`
- **Example**:
  ```json
  { "name": "kongor_game_action", "arguments": { "action": "ping" } }
  ```

### `kongor_willowmaker_manage`
- **Aliases**: `kongor_willowmaker_manager`
- **Purpose**: Lifecycle manager and status inspector for the WILLOWMAKER game launcher, DXVK Vulkan injection, 1000 FPS pacer, and CDN delta manifests.
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"apply_patches"` | `"sync_cdn"`
- **Example**:
  ```json
  { "name": "kongor_willowmaker_manage", "arguments": { "action": "status" } }
  ```

---

## 2. 🛡️ Dedicated Server Fleet & Match Orchestration

### `kongor_game_server_manage`
- **Aliases**: `kongor_game_server_manager`
- **Purpose**: Supervises host and Docker (`kongor-stack`) COMPEL dedicated server worker instances across host (`:11234`), Docker Tier D (`:11334`, ports `11335+`), and Queue Arcade Tier Q (`:11484`, ports `11485+`).
- **Key Arguments**:
  - `action` (string, required): `"start"` | `"stop"` | `"restart"` | `"status"` | `"stop_all"`
  - `port` (int, optional): Dedicated server port (default: `11235`; `status` inspects all active host & Docker fleets)
  - `map` (string, optional): Map to load (e.g. `"caldavar"`)
- **Example**:
  ```json
  { "name": "kongor_game_server_manage", "arguments": { "action": "status", "port": 11335 } }
  ```

### `kongor_game_server_command`
- **Purpose**: Dedicated console command executor strictly targeting dedicated server instances by port. Commands route via redirected `stdin`, COMPEL HTTP control plane, or named pipe `\\.\pipe\hon-server-{port}`.
- **Key Arguments**:
  - `port` (int, required): Target dedicated server listen port (e.g. `11235` or `11335`)
  - `command` (string, required): Authoritative command (e.g. `"AddFakePlayer 1"`, `"Kick 2"`)
  - `timeoutMs` (int, optional): Execution timeout (default: `5000`)
- **Example**:
  ```json
  { "name": "kongor_game_server_command", "arguments": { "port": 11335, "command": "AddFakePlayer 2" } }
  ```

### `kongor_server_manage`
- **Purpose**: Manages lifecycle (`start`, `stop`, `restart`, `status`, `logs`) of the .NET Aspire microservices cluster or turnkey `kongor-stack` Docker Compose deployment (`DockerStackProbe`).
- **Key Arguments**:
  - `action` (string, required): `"start"` | `"stop"` | `"restart"` | `"status"`
  - `service` (string, optional): `"aspire"` | `"docker"` | `"compel"` | `"all"` (default: `"all"`)
- **Example**:
  ```json
  { "name": "kongor_server_manage", "arguments": { "action": "status", "service": "all" } }
  ```

### `kongor_server_perf_stream`
- **Purpose**: Sub-second telemetry streaming for server tickrates (50Hz/100Hz), packet loss, and CPU load.
- **Key Arguments**:
  - `action` (string, required): `"snapshot"` | `"stream"`
  - `port` (int, optional): Target listen port (default: `11235`)
- **Example**:
  ```json
  { "name": "kongor_server_perf_stream", "arguments": { "action": "snapshot", "port": 11235 } }
  ```

### `kongor_compel_manage`
- **Aliases**: `kongor_compel_manager`
- **Purpose**: Controls and inspects the COMPEL dedicated server supervisor & REST API across host (`:11234`), Docker Tier D (`:11334`), and Docker Queue Arcade Tier Q (`:11484`). Adjusts worker pool capacity and multimedia timer pacing (1.0ms precision).
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"scale"` | `"restart"`
  - `targetInstances` (int, optional): Desired worker pool count
- **Example**:
  ```json
  { "name": "kongor_compel_manage", "arguments": { "action": "status" } }
  ```

### `kongor_match_launch`
- **Purpose**: Direct match launcher bypassing complex UI menu button clicks.
- **Key Arguments**:
  - `mode` (string, required): `"practice"` | `"bot"` | `"public"`
  - `map` (string, optional): `"caldavar"` | `"midwars"` (default: `"caldavar"`)
  - `hero` (string, optional): Hero entity name (e.g. `"Hero_Pyromancer"`)
  - `allheroes` (bool, optional): Unlock all heroes in practice (default: `true`)
- **Example**:
  ```json
  { "name": "kongor_match_launch", "arguments": { "mode": "practice", "map": "caldavar", "hero": "Hero_Pyromancer" } }
  ```

### `kongor_client_practice`
- **Aliases**: `client_practice`, `practice_match`
- **Purpose**: Launches an automated practice match session with instant hero spawning in headless offscreen mode.
- **Key Arguments**:
  - `hero` (string, optional): Hero to spawn (default: `"Hero_Pyromancer"`)
  - `map` (string, optional): Map identifier (default: `"caldavar"`)
  - `window_mode` (string, optional): `"offscreen"` | `"windowed"` (default: `"offscreen"`)
- **Example**:
  ```json
  { "name": "kongor_client_practice", "arguments": { "hero": "Hero_Valkyrie", "window_mode": "offscreen" } }
  ```

### `kongor_scrimmage_launch`
- **Purpose**: Orchestrates 5v5 bot scrimmages for balance and performance profiling.
- **Key Arguments**:
  - `map` (string, optional): Map to use (default: `"caldavar"`)
  - `team1Heroes` (array, optional): List of Legion hero entity names
  - `team2Heroes` (array, optional): List of Hellbourne hero entity names
- **Example**:
  ```json
  { "name": "kongor_scrimmage_launch", "arguments": { "map": "caldavar", "team1Heroes": ["Hero_Pyromancer"], "team2Heroes": ["Hero_Magmus"] } }
  ```

### `kongor_matchmaking_simulate`
- **Purpose**: Simulates high-load matchmaking queues to verify PSR/MMR Elo algorithms and lobby assignment.
- **Key Arguments**:
  - `playerCount` (int, required): Number of simulated queue entries
  - `skillSpread` (string, optional): `"narrow"` | `"wide"` | `"uniform"`
- **Example**:
  ```json
  { "name": "kongor_matchmaking_simulate", "arguments": { "playerCount": 100, "skillSpread": "narrow" } }
  ```

### `kongor_match_state`
- **Purpose**: Queries authoritative match state: game time, towers remaining, team gold, and player inventories.
- **Key Arguments**:
  - `port` (int, required): Dedicated server listen port (e.g. `11235`)
- **Example**:
  ```json
  { "name": "kongor_match_state", "arguments": { "port": 11235 } }
  ```

### `kongor_match_simulate_e2e`
- **Purpose**: Fast-forward simulation verifying game-ending conditions and win/loss resolution at uncapped tick rate.
- **Key Arguments**:
  - `port` (int, required): Target server port
  - `fastSimulation` (bool, optional): Uncap tick rate (default: `true`)
  - `durationSeconds` (int, optional): Simulation limit
- **Example**:
  ```json
  { "name": "kongor_match_simulate_e2e", "arguments": { "port": 11235, "fastSimulation": true, "durationSeconds": 60 } }
  ```

### `kongor_match_spectator`
- **Aliases**: `match_spectator`
- **Purpose**: Attaches an authoritative spectator bridge to stream match tick events, player stats, and objective statuses.
- **Key Arguments**:
  - `port` (int, required): Target server port
  - `action` (string, required): `"attach"` | `"detach"` | `"telemetry"`
- **Example**:
  ```json
  { "name": "kongor_match_spectator", "arguments": { "port": 11235, "action": "telemetry" } }
  ```

---

## 3. 🖼️ UI Automation, Navigation & Headless Rendering

### `kongor_k2_ui_inspect`
- **Aliases**: `kongor_ui_inspect`
- **Purpose**: Dumps the live XML/Lua widget hierarchy, resolving widget names, screen coordinates, and visibility flags.
- **Key Arguments**:
  - `path` (string, optional): Interface name (e.g. `"main"`, `"store"`, `"hero_select"`)
- **Example**:
  ```json
  { "name": "kongor_k2_ui_inspect", "arguments": { "path": "main" } }
  ```

### `kongor_client_navigate`
- **Aliases**: `kongor_navigate`, `kongor_nav`
- **Purpose**: Programmatically navigates game interfaces via clean engine Lua triggers without mouse cursor simulation.
- **Key Arguments**:
  - `destination` (string, required): `"main_menu"` | `"store"` | `"hero_select"` | `"options"` | `"practice"`
- **Example**:
  ```json
  { "name": "kongor_client_navigate", "arguments": { "destination": "store" } }
  ```

### `kongor_ui_control`
- **Aliases**: `kongor_ui_controller`
- **Purpose**: Deterministic UI automation and interactive control for Project Kongor windows (`hon_x64`, WILLOWMAKER, NEXUS Control Panel). Inspects UI element trees, performs coordinate/element clicks with 100-150ms actuation delays, invokes UIA patterns, types text characters, dispatches virtual keys, and executes DOM events on K2 widgets with anti-chat spill guardrails and stale PID auto-migration.
- **Key Arguments**:
  - `action` (string, required): `"inspect"` | `"click"` | `"invoke"` | `"type"` | `"key"` | `"dispatch_event"` | `"dismiss_console"`
  - `target` (string, optional): Window identifier: `"hon_x64"` (default), window title (`"*Heroes of Newerth*"`), HWND hex (`"0x000E073C"`), or `"pid:<id>"`. If an explicit PID is dead or windowless, automatically migrates to the active client window (`[MCP-069]`, `[MCP-074]`).
  - `widget` (string, optional): Target K2 widget identifier (e.g. `"sysbar_quickStart"`, `"btn_play"`)
  - `event` (string, optional): Event name to trigger on widget (default: `"click"`)
  - `interface` (string, optional): K2 interface containing widget (defaults to `"main"` or active interface)
  - `element_name` (string, optional): UI Automation element name or AutomationId to target
  - `text` (string, optional): Text to type (when `action="type"`)
  - `key` (string, optional): Virtual key name (e.g. `"Enter"`, `"Tab"`, `"Escape"`, `"F8"`, `"Space"`)
  - `x`, `y` (int, optional): Window-relative coordinates to click
- **Example**:
  ```json
  { "name": "kongor_ui_control", "arguments": { "action": "inspect", "target": "hon_x64" } }
  ```

### `kongor_ui_flow`
- **Purpose**: Sequences multi-step UI flows (e.g. Main Menu -> Goblin Store -> Avatar Preview).
- **Key Arguments**:
  - `action` (string, required): `"record"` | `"play"` | `"history"`
  - `flowName` (string, optional): Name of recorded flow
- **Example**:
  ```json
  { "name": "kongor_ui_flow", "arguments": { "action": "play", "flowName": "open_store" } }
  ```

### `kongor_ui_observer`
- **Purpose**: Reactive K2 UI observer that waits asynchronously for widget visibility without CPU spinning.
- **Key Arguments**:
  - `action` (string, required): `"wait_visible"` | `"wait_hidden"`
  - `widget` (string, required): Target widget name
  - `timeout_ms` (int, optional): Maximum wait time (default: `5000`)
- **Example**:
  ```json
  { "name": "kongor_ui_observer", "arguments": { "action": "wait_visible", "widget": "store_container" } }
  ```

### `kongor_ui_render_view`
- **Purpose**: Headless offscreen Avalonia XAML view rendering with SkiaSharp for visual layout and theme verification.
- **Key Arguments**:
  - `view_name` (string, required): Name of XAML view to render
  - `save_artifact` (bool, optional): Save rendered output to artifacts directory
- **Example**:
  ```json
  { "name": "kongor_ui_render_view", "arguments": { "view_name": "HeroSelectionView", "save_artifact": true } }
  ```

### `kongor_ui_visual_diff`
- **Purpose**: Calculates SSIM visual similarity and renders color-coded pixel diff artifacts between baseline and candidate screenshots.
- **Key Arguments**:
  - `baselineImage` (string, required): Absolute path to baseline PNG
  - `candidateImage` (string, required): Absolute path to candidate PNG
  - `threshold` (float, optional): Acceptance threshold (default: `0.95`)
- **Example**:
  ```json
  { "name": "kongor_ui_visual_diff", "arguments": { "baselineImage": "baseline.png", "candidateImage": "candidate.png" } }
  ```

### `kongor_assert_visual`
- **Purpose**: Asserts UI visual rendering conditions against headless frame captures with automated pass/fail reporting.
- **Key Arguments**:
  - `expectedPattern` (string, required): Pattern or visual target identifier
  - `region` (string, optional): Sub-region coordinates (e.g. `"0,0,300,100"`)
  - `threshold` (float, optional): Match threshold (default: `0.90`)
- **Example**:
  ```json
  { "name": "kongor_assert_visual", "arguments": { "expectedPattern": "minimap_gold_lead" } }
  ```

### `kongor_capture_window`
- **Purpose**: Window screenshot acquisition with **Tier 0 Vulkan swapchain shared memory acceleration** (0ms dwell, 0% window exposure), cascading gracefully to PrintWindow, Window DC, and Desktop DC.
- **Key Arguments**:
  - `target` (string, optional): Process or window name (default: `"hon_x64"`)
  - `capture_method` (string, optional): `"auto"` | `"vulkan_offscreen"` | `"print_window"` | `"window_dc"` | `"transient"`
  - `save_artifact` (bool, optional): Save captured frame as PNG artifact (default: `true`)
- **Example**:
  ```json
  { "name": "kongor_capture_window", "arguments": { "target": "hon_x64", "capture_method": "auto", "save_artifact": true } }
  ```

### `kongor_transient_capture`
- **Aliases**: `kongor_capture_transient`
- **Purpose**: Momentary capture for offscreen/hidden windows with zero operator disruption. Automatically intercepted by Tier 0 Vulkan shared memory when available.
- **Key Arguments**:
  - `target` (string, optional): Target window name (default: `"hon_x64"`)
  - `dwell_ms` (int, optional): Exposition dwell time in ms (default: `350`)
  - `save_artifact` (bool, optional): Persist capture as PNG
- **Example**:
  ```json
  { "name": "kongor_transient_capture", "arguments": { "target": "hon_x64", "dwell_ms": 300 } }
  ```

---

## 4. 📦 Virtual Filesystem (VFS), Modding & Asset Pipeline

### `kongor_vfs_resolve`
- **Purpose**: Resolves virtual asset paths to physical files across stacked `.s2z` archives.
- **Key Arguments**:
  - `virtualPath` (string, required): Virtual path (e.g. `"/ui/main.interface"`)
- **Example**:
  ```json
  { "name": "kongor_vfs_resolve", "arguments": { "virtualPath": "/ui/main.interface" } }
  ```

### `kongor_vfs_locate`
- **Aliases**: `vfs_locate`
- **Purpose**: Sub-millisecond disk asset locator bridging K2 mount layers with Voidtools Everything NTFS MFT indexer.
- **Key Arguments**:
  - `query` (string, required): Search query pattern
  - `extension` (string, optional): Filter by file extension (e.g. `"material"`, `"dds"`, `"clip"`)
- **Example**:
  ```json
  { "name": "kongor_vfs_locate", "arguments": { "query": "Hero_Pyromancer", "extension": "mdf" } }
  ```

### `kongor_vfs_mount_graph`
- **Purpose**: Analyzes mount precedence, shadowing order, and overlay conflicts in the K2 Virtual File System.
- **Key Arguments**:
  - `action` (string, required): `"inspect_mounts"` | `"verify_precedence"`
- **Example**:
  ```json
  { "name": "kongor_vfs_mount_graph", "arguments": { "action": "inspect_mounts" } }
  ```

### `kongor_asset_inspect`
- **Purpose**: Inspects `.s2z` ZIP central directory headers, compression ratios, and CRC32 checksums.
- **Key Arguments**:
  - `path` (string, required): Path to `.s2z` archive
- **Example**:
  ```json
  { "name": "kongor_asset_inspect", "arguments": { "path": "game/resources0.s2z" } }
  ```

### `kongor_asset_bake_pipeline`
- **Aliases**: `asset_bake_pipeline`
- **Purpose**: Generative 2D/3D Asset Baking Pipeline bridging ComfyUI/Meshy outputs to BC7/BC5 textures and `.material` XML.
- **Key Arguments**:
  - `action` (string, required): `"bake"` | `"clean"` | `"status"`
  - `targetPath` (string, required): Source asset directory or file path
- **Example**:
  ```json
  { "name": "kongor_asset_bake_pipeline", "arguments": { "action": "bake", "targetPath": "heroes/custom_hero/" } }
  ```

### `kongor_audio_studio`
- **Aliases**: `audio_studio`
- **Purpose**: Audio Voice & SFX Studio Pipeline with EBU R128 loudness normalization and ElevenLabs voice handoff.
- **Key Arguments**:
  - `action` (string, required): `"preview"` | `"synthesize"` | `"normalize"`
  - `hero` (string, optional): Hero identifier
  - `cue` (string, optional): Audio cue trigger name
- **Example**:
  ```json
  { "name": "kongor_audio_studio", "arguments": { "action": "preview", "hero": "Hero_Pyromancer", "cue": "attack_1" } }
  ```

### `kongor_engine_cache_purge`
- **Purpose**: Flushes MemoryPack and Zstd prebaked binary caches.
- **Key Arguments**:
  - `target` (string, required): `"entities"` | `"lua"` | `"all"`
- **Example**:
  ```json
  { "name": "kongor_engine_cache_purge", "arguments": { "target": "all" } }
  ```

### `kongor_cache_coherence_verify`
- **Purpose**: Validates binary cache checksums against raw XML entity definitions to detect drift. Provides safe non-UTF8 struct decoding for binary Redis hashes.
- **Key Arguments**:
  - `action` (string, required): `"verify"` | `"inspect"` | `"hgetall"`
  - `key` (string, optional): Cache key name (e.g. `"MATCH-SERVERS"`)
- **Example**:
  ```json
  { "name": "kongor_cache_coherence_verify", "arguments": { "action": "verify" } }
  ```

### `kongor_mod_build`
- **Purpose**: Compiles loose assets and mod scripts into production `.honmod` or `.s2z` packages.
- **Key Arguments**:
  - `sourceDirectory` (string, required): Path to unpacked mod files
  - `outputArchive` (string, required): Target `.s2z` or `.honmod` path
- **Example**:
  ```json
  { "name": "kongor_mod_build", "arguments": { "sourceDirectory": "mods/custom_hud", "outputArchive": "game/custom_hud.s2z" } }
  ```

### `kongor_material_inspect`
- **Purpose**: Parses K2 material shaders, texture samplers, blend modes, and culling states.
- **Key Arguments**:
  - `path` (string, required): Path to `.material` definition file
- **Example**:
  ```json
  { "name": "kongor_material_inspect", "arguments": { "path": "heroes/pyromancer/hero.material" } }
  ```

### `kongor_texture_preview`
- **Purpose**: Decodes DirectX DDS/TGA textures into web-compatible PNG/JPEG artifacts for visual review.
- **Key Arguments**:
  - `path` (string, required): Path to `.dds` or `.tga` texture
  - `save_artifact` (bool, optional): Persist as artifact (default: `true`)
- **Example**:
  ```json
  { "name": "kongor_texture_preview", "arguments": { "path": "heroes/pyromancer/color.dds", "save_artifact": true } }
  ```

### `kongor_texture_in_game_inspect`
- **Purpose**: Verifies texture binding and mipmap resolution directly on in-game hero models.
- **Key Arguments**:
  - `hero` (string, required): Hero entity identifier
- **Example**:
  ```json
  { "name": "kongor_texture_in_game_inspect", "arguments": { "hero": "Hero_Pyromancer" } }
  ```

### `kongor_texture_diff`
- **Purpose**: Computes pixel-level difference maps to detect texture compression artifacts.
- **Key Arguments**:
  - `baseline` (string, required): Reference texture path
  - `candidate` (string, required): Comparison texture path
  - `threshold` (float, optional): Tolerance threshold (default: `0.05`)
- **Example**:
  ```json
  { "name": "kongor_texture_diff", "arguments": { "baseline": "color_orig.dds", "candidate": "color_bc7.dds" } }
  ```

### `kongor_audio_preview`
- **Purpose**: Validates K2 sound definitions, sample rates, spatial rolloff, and event hooks.
- **Key Arguments**:
  - `soundPath` (string, required): Virtual path to sound file
  - `action` (string, optional): `"status"` | `"validate"`
- **Example**:
  ```json
  { "name": "kongor_audio_preview", "arguments": { "soundPath": "/sounds/spells/pyro_blast.wav" } }
  ```

---

## 5. 🧠 Engine Introspection, Dynamic Memory & Hot-Reload

### `kongor_engine_inspect`
- **Purpose**: Searches and inspects engine cvars, default values, and command symbol tables.
- **Key Arguments**:
  - `target` (string, required): `"cvar"` | `"cmd"`
  - `query` (string, required): Search pattern
- **Example**:
  ```json
  { "name": "kongor_engine_inspect", "arguments": { "target": "cvar", "query": "vid_" } }
  ```

### `kongor_app_eval`
- **Purpose**: Executes dynamic C# scripts against live ecosystem services via Roslyn scripting.
- **Key Arguments**:
  - `script` (string, required): C# script expression
- **Example**:
  ```json
  { "name": "kongor_app_eval", "arguments": { "script": "return DateTime.UtcNow.ToString();" } }
  ```

### `kongor_hot_reload`
- **Purpose**: Triggers live hot-reload of in-memory K2 interfaces and entity XML scripts without engine restart.
- **Key Arguments**:
  - `action` (string, required): `"reload"`
  - `target` (string, required): `"ui"` | `"entity"` | `"all"`
- **Example**:
  ```json
  { "name": "kongor_hot_reload", "arguments": { "action": "reload", "target": "ui" } }
  ```

### `kongor_native_manage`
- **Purpose**: Audits native C++ engine dependencies, MSVC CRT versions, and DLL imports.
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"verify"`
- **Example**:
  ```json
  { "name": "kongor_native_manage", "arguments": { "action": "verify" } }
  ```

### `kongor_vulkan`
- **Purpose**: Interrogates DXVK Vulkan 1.3 physical device capabilities, memory heaps, and driver queues.
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"extensions"` | `"heaps"`
- **Example**:
  ```json
  { "name": "kongor_vulkan", "arguments": { "action": "status" } }
  ```

### `kongor_camera_control`
- **Purpose**: Controls in-game camera coordinates, pitch, yaw, and FOV for cinematic capture.
- **Key Arguments**:
  - `action` (string, required): `"position"` | `"fov"` | `"track"`
  - `x` (float, optional), `y` (float, optional), `z` (float, optional)
- **Example**:
  ```json
  { "name": "kongor_camera_control", "arguments": { "action": "position", "x": 1500.0, "y": 2000.0, "z": 800.0 } }
  ```

### `kongor_memory_sync`
- **Aliases**: `memory_sync`
- **Purpose**: Project Knowledge Graph & Persistent Memory Bridge conforming to `@modelcontextprotocol/server-memory`. Synchronizes shared memory states and inspects IPC memory mappings.
- **Key Arguments**:
  - `action` (string, required): `"sync"` | `"status"` | `"read_graph"`
- **Example**:
  ```json
  { "name": "kongor_memory_sync", "arguments": { "action": "status" } }
  ```

---

## 6. 🔬 Diagnostics, Memory Leaks & Crash Dump Triage

### `kongor_diagnostics`
- **Purpose**: Audits TCP/UDP port bindings, running service PIDs, and ASP.NET Core health endpoints across the entire ecosystem.
- **Key Arguments**:
  - `scope` (string, optional): `"ports"` | `"services"` | `"all"` (default: `"all"`)
- **Example**:
  ```json
  { "name": "kongor_diagnostics", "arguments": { "scope": "all" } }
  ```

### `kongor_cluster_diagnostics`
- **Purpose**: Comprehensive health sweep covering Aspire, Redis, PostgreSQL, and dedicated servers.
- **Key Arguments**:
  - `scope` (string, optional): `"health"` | `"dependencies"` (default: `"health"`)
- **Example**:
  ```json
  { "name": "kongor_cluster_diagnostics", "arguments": { "scope": "health" } }
  ```

### `kongor_memory_leak_detect`
- **Purpose**: Tracks process working set growth and detects unmanaged handle leaks over prolonged sessions.
- **Key Arguments**:
  - `action` (string, required): `"snapshot"` | `"diff"`
- **Example**:
  ```json
  { "name": "kongor_memory_leak_detect", "arguments": { "action": "snapshot" } }
  ```

### `kongor_crash_dump_analyze`
- **Purpose**: Parses Win32 crash dumps (`diag_*.txt` / `.dmp`), extracting faulting instruction pointers and call stacks.
- **Key Arguments**:
  - `action` (string, required): `"list"` | `"inspect"`
  - `dumpFile` (string, optional): Path to dump file
- **Example**:
  ```json
  { "name": "kongor_crash_dump_analyze", "arguments": { "action": "list" } }
  ```

### `kongor_crash_triage`
- **Aliases**: `crash_triage`
- **Purpose**: Triangulates faulting crash offsets with Ghidra decompilation and native C++ symbol trees.
- **Key Arguments**:
  - `dump_path` (string, required): Path to `.dmp` or `diag_*.txt`
  - `binary_name` (string, optional): Target binary (e.g. `"k2_x64.dll"`)
- **Example**:
  ```json
  { "name": "kongor_crash_triage", "arguments": { "dump_path": "logs/crash/minidump.dmp", "binary_name": "k2_x64.dll" } }
  ```

### `kongor_crash_patch`
- **Aliases**: `crash_patch`
- **Purpose**: Synthesizes and applies reversible on-disk x64 binary patches (null-checks, zero-divisor guards) with automatic `.bak` backup creation and `dry_run` verification.
- **Key Arguments**:
  - `target_dll` (string, required): DLL name (e.g. `"cgame_x64.dll"`)
  - `offset` (string, required): Hex offset (e.g. `"0x2F140"`)
  - `patch_bytes` (string, required): Hex byte sequence
  - `dry_run` (bool, optional): Preview patch bytes and target offset without writing to disk (default: `true`)
- **Example**:
  ```json
  { "name": "kongor_crash_patch", "arguments": { "target_dll": "cgame_x64.dll", "offset": "0x2F140", "patch_bytes": "4885C97405", "dry_run": true } }
  ```

### `kongor_triage`
- **Purpose**: Automated crash fingerprint matching against the Project Kongor known-issue database.
- **Key Arguments**:
  - `error_signature` (string, required): Crash signature or call stack snippet
- **Example**:
  ```json
  { "name": "kongor_triage", "arguments": { "error_signature": "cgame_x64.dll+0x2F140" } }
  ```

### `kongor_k2_lint`
- **Purpose**: Lints K2 XML and Lua files for syntax errors, unclosed tags, and deprecated functions.
- **Key Arguments**:
  - `path` (string, required): Directory or file path to lint
- **Example**:
  ```json
  { "name": "kongor_k2_lint", "arguments": { "path": "src/ui" } }
  ```

---

## 7. 🗄️ Database, Analytics, Replays & Multi-Instance Desync Analysis

### `kongor_db_fixture`
- **Aliases**: `game_fixture`
- **Purpose**: Seeds and resets development databases with reproducible player accounts, matches, and shop inventories.
- **Key Arguments**:
  - `action` (string, required): `"seed"` | `"reset"`
  - `fixtureName` (string, optional): Specific fixture template (default: `"standard_dev"`)
- **Example**:
  ```json
  { "name": "kongor_db_fixture", "arguments": { "action": "seed", "fixtureName": "standard_dev" } }
  ```

### `kongor_db_migration_test`
- **Purpose**: Verifies EF Core migrations across PostgreSQL, Supabase, and SQL Server databases.
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"migrate"` | `"rollback"`
- **Example**:
  ```json
  { "name": "kongor_db_migration_test", "arguments": { "action": "status" } }
  ```

### `kongor_db_pool_diagnostics`
- **Purpose**: Monitors Npgsql / SqlClient connection pools to detect connection starvation and long-running locks.
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"inspect"`
- **Example**:
  ```json
  { "name": "kongor_db_pool_diagnostics", "arguments": { "action": "status" } }
  ```

### `kongor_match_analytics`
- **Aliases**: `match_analytics`
- **Purpose**: Executes high-speed DuckDB analytics queries over compressed parquet match telemetry.
- **Key Arguments**:
  - `match_id` (string, optional): Match identifier
  - `query` (string, required): SQL query to run against telemetry data
- **Example**:
  ```json
  { "name": "kongor_match_analytics", "arguments": { "query": "SELECT hero, count(*) FROM matches GROUP BY hero" } }
  ```

### `kongor_replay_inspect`
- **Purpose**: Parses binary replay headers, player actions, game chat, and team statistics.
- **Key Arguments**:
  - `path` (string, required): Path to `.honreplay` file
- **Example**:
  ```json
  { "name": "kongor_replay_inspect", "arguments": { "path": "replays/M2060568725.honreplay" } }
  ```

### `kongor_replay_playback`
- **Purpose**: Controls in-engine replay playback and frame stepping.
- **Key Arguments**:
  - `action` (string, required): `"play"` | `"pause"` | `"seek"` | `"step"`
  - `frame` (int, optional): Target frame number
- **Example**:
  ```json
  { "name": "kongor_replay_playback", "arguments": { "action": "seek", "frame": 1200 } }
  ```

### `kongor_replay_diff`
- **Purpose**: Compares client and server replays to pinpoint synchronization drift and packet loss anomalies.
- **Key Arguments**:
  - `action` (string, required): `"diff_metadata"` | `"diff_ticks"`
  - `clientReplay` (string, required): Client replay path
  - `serverReplay` (string, required): Server replay path
- **Example**:
  ```json
  { "name": "kongor_replay_diff", "arguments": { "action": "diff_ticks", "clientReplay": "client.honreplay", "serverReplay": "server.honreplay" } }
  ```

---

## 8. ⚡ Resilience, Chaos Engineering & Bot Choreography

### `kongor_network_chaos`
- **Purpose**: Configures K2's built-in network impairment CVars (`net_lag`, `net_drop`, `net_jitter`) with an auto-expiring lease timer (`ChaosLeaseDurationSeconds`).
- **Key Arguments**:
  - `action` (string, required): `"inject"` | `"clear"` | `"status"`
  - `packetLossPercent` (float, optional): Packet drop percentage (0–100, e.g. `5.0`)
  - `latencyMs` (int, optional): Simulated latency in milliseconds (`net_lag`)
  - `jitterMs` (int, optional): Latency jitter in milliseconds (`net_jitter`)
- **Example**:
  ```json
  { "name": "kongor_network_chaos", "arguments": { "action": "inject", "packetLossPercent": 5.0, "latencyMs": 80, "jitterMs": 20 } }
  ```

### `kongor_network_impair_simulate`
- **Purpose**: Simulates realistic WAN network degradation profiles.
- **Key Arguments**:
  - `action` (string, required): `"simulate"` | `"stop"`
  - `profile` (string, required): `"wan"` | `"3g"` | `"lossy_satellite"`
- **Example**:
  ```json
  { "name": "kongor_network_impair_simulate", "arguments": { "action": "simulate", "profile": "wan" } }
  ```

### `kongor_net_packet_fuzz`
- **Aliases**: `packet_fuzz`, `net_fuzz`
- **Purpose**: Fuzz-tests dedicated server UDP protocol handling to discover packet parsing vulnerabilities.
- **Key Arguments**:
  - `target_port` (int, required): Server port (e.g. `11235`)
  - `duration_sec` (int, optional): Fuzz duration in seconds (default: `10`)
  - `rate_pps` (int, optional): Packets per second (default: `100`)
- **Example**:
  ```json
  { "name": "kongor_net_packet_fuzz", "arguments": { "target_port": 11235, "duration_sec": 5, "rate_pps": 50 } }
  ```

### `kongor_bot_choreography`
- **Purpose**: Programmatically directs bot heroes to execute coordinated maneuvers, spells, and teamfights.
- **Key Arguments**:
  - `action` (string, required): `"spawn"` | `"move"` | `"cast"` | `"stop"`
  - `botId` (int, required): Bot entity index
  - `targetX` (float, optional), `targetY` (float, optional)
  - `abilitySlot` (int, optional): Spell slot index
- **Example**:
  ```json
  { "name": "kongor_bot_choreography", "arguments": { "action": "cast", "botId": 1, "abilitySlot": 0 } }
  ```

### `kongor_fixture_inject`
- **Aliases**: `fixture_inject`
- **Purpose**: Injects dynamic game entities or test scenarios into live running matches.
- **Key Arguments**:
  - `entity_name` (string, required): Entity XML definition name
  - `target` (string, optional): `"client"` | `"server"` (default: `"server"`)
- **Example**:
  ```json
  { "name": "kongor_fixture_inject", "arguments": { "entity_name": "Creep_LegionMelee", "target": "server" } }
  ```

### `kongor_game_step`
- **Purpose**: Advances the authoritative game simulation by an exact number of deterministic ticks.
- **Key Arguments**:
  - `ticks` (int, required): Number of ticks to simulate (e.g. `20` = 1 second at 20Hz)
  - `timeout_ms` (int, optional): Execution timeout (default: `5000`)
- **Example**:
  ```json
  { "name": "kongor_game_step", "arguments": { "ticks": 20 } }
  ```

### `kongor_game_snapshot`
- **Purpose**: Saves and restores hero state checkpoints (`gold`, `level`, `x/y/z` position, `items`) via `Nexus.ApplySnapshot` or seeks replay frames.
- **Key Arguments**:
  - `action` (string, required): `"save"` | `"restore"` | `"list"` | `"seek"`
  - `snapshot_id` (string, optional): Checkpoint identifier
- **Example**:
  ```json
  { "name": "kongor_game_snapshot", "arguments": { "action": "save", "snapshot_id": "pre_teamfight" } }
  ```

### `kongor_state_snapshot`
- **Purpose**: High-level game state persistence and replay checkpointing.
- **Key Arguments**:
  - `action` (string, required): `"create"` | `"rollback"`
  - `snapshotId` (string, optional): Snapshot identifier
- **Example**:
  ```json
  { "name": "kongor_state_snapshot", "arguments": { "action": "create", "snapshotId": "before_roshan" } }
  ```

---

## 9. 🩺 Autonomous Self-Healing, Profiling & AI Vector Search

### `kongor_self_heal`
- **Aliases**: `self_heal`
- **Purpose**: Autonomous Self-Healing Pipeline. Ingests runtime engine logs across game client and dedicated servers, deduplicates errors with SHA-256 fingerprints, and executes the Dual-State Reflex loop (Record First -> Immediate Reflex Healing -> Auto-Resolve with Receipt).
- **Key Arguments**:
  - `target` (string, optional): `"all"` | `"ui"` | `"network"` | `"cache"` (default: `"all"`)
  - `action` (string, optional): `"scan_and_ingest"` | `"status"`
  - `log_path` (string, optional): Specific log file path
- **Example**:
  ```json
  { "name": "kongor_self_heal", "arguments": { "action": "scan_and_ingest", "target": "all" } }
  ```

### `kongor_profile_trace`
- **Aliases**: `profile_trace`, `flamegraph`
- **Purpose**: Generates a Chrome Tracing (`chrome://tracing`) frame-budget reference profile modeling K2's 16.6ms (60 FPS) or 6.9ms (144 FPS) main-thread budget (for live runtime FPS/frame-time sampling, use `kongor_perf_window` or Pyroscope `:4040`).
- **Key Arguments**:
  - `action` (string, required): `"capture"` | `"analyze"`
  - `targetFps` (int, optional): Target framerate budget (default: `60`)
- **Example**:
  ```json
  { "name": "kongor_profile_trace", "arguments": { "action": "capture", "targetFps": 144 } }
  ```

### `kongor_audit_stats`
- **Aliases**: `audit_stats`
- **Purpose**: Audits and asserts combat math, DPS curves, and hero stat balance formulas across all hero definitions.
- **Key Arguments**:
  - `action` (string, required): `"audit"` | `"verify"`
- **Example**:
  ```json
  { "name": "kongor_audit_stats", "arguments": { "action": "audit" } }
  ```

### `kongor_e2e_match_audit`
- **Aliases**: `e2e_match_audit`
- **Purpose**: Executes an end-to-end autonomous practice match audit with automated health reporting, tickrate validation, and memory leak checks.
- **Key Arguments**:
  - `duration_minutes` (int, optional): Audit duration (default: `1`)
  - `hero` (string, optional): Hero to test (default: `"Hero_Pyromancer"`)
- **Example**:
  ```json
  { "name": "kongor_e2e_match_audit", "arguments": { "duration_minutes": 1, "hero": "Hero_Pyromancer" } }
  ```

### `kongor_vector_search`
- **Aliases**: `kongor_vector`
- **Purpose**: Low-level Garnet Vector Set (`VADD`/`VSIM`) and in-memory cosine similarity client over pre-computed `float[]` embeddings (for natural-language search across documentation and issues, use `kongor ask` or `scripts/query_docs.py`).
- **Key Arguments**:
  - `action` (string, optional): `"vsim"` | `"vadd"` | `"status"` (default: `"vsim"`)
  - `index` (string, optional): Vector set key name (default: `"kongor:vectors:default"`)
  - `vector` (array of float, optional): Pre-computed query or insertion embedding vector
  - `topK` (int, optional): Maximum results (default: `5`)
- **Example**:
  ```json
  { "name": "kongor_vector_search", "arguments": { "action": "vsim", "index": "kongor:vectors:default", "vector": [0.12, -0.34, 0.56, 0.78], "topK": 3 } }
  ```

### `kongor_realtime_stream`
- **Purpose**: Server-Sent Events (SSE) and metric streams for external dashboards.
- **Key Arguments**:
  - `action` (string, required): `"metrics"` | `"status"`
- **Example**:
  ```json
  { "name": "kongor_realtime_stream", "arguments": { "action": "metrics" } }
  ```

### `kongor_telemetry_stream`
- **Purpose**: Real-time event streams capturing kills, deaths, item purchases, and objective captures.
- **Key Arguments**:
  - `action` (string, required): `"status"` | `"subscribe"`
- **Example**:
  ```json
  { "name": "kongor_telemetry_stream", "arguments": { "action": "status" } }
  ```

---

## 10. 🎯 Client Dev Velocity, Mailbox IPC & Store/Model Verification

| Tool Name | Key Arguments | Capabilities |
|---|---|---|
| `kongor_client_eval_lua` | `code`, `timeoutMs` | Evaluates Lua expressions directly in the running game client over Zero-Focus-Theft Mailbox IPC (`NexusMailboxIpcBridge` + `NativeClientIpcBridge`). |
| `kongor_reload_and_verify` | `target`, `verifyLogs`, `captureScreenshot` | Atomically triggers `ReloadInterfaces`, checks `console.log` / `client_lua_debug.log` for new errors, and captures a verification frame. |
| `kongor_errors_since` | `sinceTimestamp`, `logType` | Queries `console.log` and `client_lua_debug.log` for any `ERR:`, `WARN:`, or Lua stack traces emitted after a specific timestamp. |
| `kongor_get_client_logs` | `lines`, `filter`, `logType` | Tails the latest lines from `console.log`, `client_lua_debug.log`, or `WILLOWMAKER.log` with regex/substring filtering. |
| `kongor_vfs_clean_cache` | `dryRun`, `target` | Purges stale loose or cached VFS artifacts while preserving tracked `games/client/override/` modifications. |
| `kongor_vfs_completeness_audit` | `scope`, `checkTextures` | Audits `.interface`, `.package`, and `.mdf` files against `VfsIndexService` to detect missing texture, model, or effect references. |
| `kongor_sync_mirrors` | `direction`, `dryRun` | Synchronizes and verifies 100% byte-identical parity between `games/client/override/` and `games/client/ClientPackageFiles/`. |
| `kongor_inspect_model_mdf` | `mdfPath`, `checkClips` | Parses K2 `.mdf` model definitions and verifies `.model` geometry, `.clip` animations, and material paths in the VFS. |
| `kongor_inspect_hero_pool` | `filter`, `includeAlts` | Inspects registered hero entities and alt avatars, verifying icons, preview models, and store metadata. |
| `kongor_validate_previews` | `hero`, `category` | Validates store 3D pedestal and 2D icon previews across avatars, couriers, taunts, and wards. |
| `kongor_set_store_mode` | `mode`, `category` | Programmatically switches the in-game Goblin Store (`store2`) mode and active tab for automated testing. |
| `kongor_client_capture_preview` | `targetWidget`, `saveArtifact` | Captures a cropped preview screenshot of a specific K2 UI widget or `<modelpanel>`. |
| `kongor_assert_ui` | `interfaceName`, `widgetName`, `expectedVisible` | Asserts widget existence, visibility, dimensions, and text state in the live K2 UI hierarchy. |
| `kongor_wait_for` | `condition`, `target`, `timeoutMs` | Waits asynchronously for an engine CVar, log pattern, UI state, or process readiness condition without busy-polling. |
| `kongor_log_correlate` | `timeWindowSeconds` | Correlates timestamps and events across `console.log`, `WILLOWMAKER.log`, `COMPEL`, and Master Server logs. |
| `kongor_health_check` | `includeClient`, `includeCluster` | Fast liveness check across client IPC, COMPEL fleet, and Aspire microservices. |
| `kongor_tao_capabilities` | `domain`, `filter` | Returns the machine-readable capability manifest and schema summary of all registered TAO MCP tools. |
| `kongor_state_snapshot` | `action: "capture" \| "compare"` | Captures and diffs complete client + server state snapshots (CVars, loaded interfaces, memory metrics). |
| `kongor_fixture_inject` | `fixtureName`, `target` | Injects deterministic mock responses or test fixtures into the client/server test harness. |
| `kongor_ci_trigger` | `pipeline`, `mode` | Triggers local pre-push or inner-loop verification gates (`gate_check.ps1`, `test_metagen_parity.ps1`). |

---

## 🌸 Summary

Every tool in the TAO catalog adheres to our core engine guardrails:
1. **Target Specific Server Ports**: Never broadcast dedicated server commands to generic unnamed pipes (`port: 11235` required).
2. **Global UI Manager is `UIManager`**: In K2 Lua, `Windows.main` does not exist; always use `UIManager.GetInterface('main')`.
3. **`DoEvent()` Signature**: Takes zero arguments; never pass `'onclick'` strings.
4. **Zero-Focus-Theft Mailbox IPC**: Client Lua and console commands execute via `CompositeGameIpcBridge` (`NexusMailboxIpcBridge` + `NativeClientIpcBridge` over `version.dll` NativeGuard) without stealing OS foreground focus.
5. **Tier 0 Vulkan Swapchain Memory**: Headless client captures execute with 0ms dwell and 0% window exposure.

