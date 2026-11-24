# Project: HoN Replay-to-MP4 Video Rendering Pipeline

## Architecture
The Project Kongor Replay-to-MP4 pipeline delivers high-fidelity, deterministic video rendering from `.honreplay` files directly to hardware-encoded MP4 files locked to any player's perspective.

```
+-----------------------------------------------------------------------------------+
|                              CLI / PowerShell Entry                               |
|                  render-replay.ps1 / ReplayRenderer.Service.exe                   |
+----------------------------------------+------------------------------------------+
                                         |
                                         v
+-----------------------------------------------------------------------------------+
|                           services/replay-renderer                                |
|  - Parse .honreplay XML metadata (clientnum, heroes, match duration)              |
|  - Generate deterministic autoexec.cfg (camera lock, framerate, UI suppression)   |
|  - Launch hon_x64.exe inside Win32 Job Object sandbox                             |
|  - Manage Vulkan Shared Memory & WASAPI Audio Loopback Capture                    |
|  - Pipe BGRA raw frames & 48kHz PCM audio into FFmpeg NVENC (H.264 / HEVC)        |
+--------------------+------------------------------------+-------------------------+
                     |                                    |
                     v (Injects Vulkan Layer)             v (Named Pipes)
+---------------------------------------+   +---------------------------------------+
|          games/client/hon_x64.exe     |   |               ffmpeg.exe              |
|  - DXVK (d3d9.dll -> Vulkan 1.3)      |   |  - NVIDIA NVENC Hardware Encoder      |
|  - VkLayer_hon_replay_capture.dll     |   |  - 1080p 60fps / 25 Mbps VBR          |
|  - Shared Memory Frame Publisher      |   |  - AAC 320 kbps Stereo Audio          |
+---------------------------------------+   |  - FastStart MP4 Container Output     |
                                            +---------------------------------------+
```

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| F1 | Replay Metadata Parsing | Extract player names, slot numbers (`clientnum`), hero IDs, and match duration from `.honreplay` XML | M3 | ORIGINAL_REQUEST §R1, R3 |
| F2 | Deterministic Engine Playback | Automate `hon_x64.exe` startup via `autoexec.cfg` with fixed 60fps clock, disabled CEF, and no master server | M3 | ORIGINAL_REQUEST §R2 |
| F3 | Player Camera Locking | Lock camera onto chosen player perspective (`replay_client`, `cg_lockCamera true`, `cg_fogofwar true`) | M3 | ORIGINAL_REQUEST §R1, R2 |
| F4 | Replay Seeking / Start Offset | Seek to `--start <seconds>` using `ReplaySetFrame (seconds * 20)` and handle `--duration` | M3 | ORIGINAL_REQUEST §R3 |
| F5 | UI / Debug Element Suppression | Suppress debug alerts, DXVK HUD overlay, FPS counters, and login animations | M2, M3 | ORIGINAL_REQUEST §R2 |
| F6 | Vulkan Swapchain DMA Capture | Intercept `vkQueuePresentKHR` in `VkLayer_hon_replay_capture.dll` and publish via Shared Memory | M2 | ORIGINAL_REQUEST §R1, R2 |
| F7 | WASAPI Audio Loopback Sync | Capture 48kHz stereo PCM audio with exact 3,200 bytes/frame sync and silence underrun padding | M3 | ORIGINAL_REQUEST §R1 |
| F8 | Hardware-Accelerated NVENC MP4 | Stream raw video/audio pipes into FFmpeg encoding 1080p60 NVENC H.264/HEVC MP4 | M3 | ORIGINAL_REQUEST §R1 |
| F9 | Comprehensive CLI Flags | Support `--replay`, `--player`, `--output`, `--start`, `--duration`, `--resolution`, `--fps`, `--codec` | M3 | ORIGINAL_REQUEST §R3 |
| F10 | Legacy Prototype Decommissioning | Remove obsolete synthetic GLB renderer (`tools/replay-vulkan-renderer`) from solutions | M1 | ORIGINAL_REQUEST §R4 |
| F11 | PowerShell Runner & Telemetry | Root `render-replay.ps1` script with verbose logging, `Copy-Item`/`Select-String`, and metrics | M4 | ORIGINAL_REQUEST §R5 |
| F12 | Opaque-Box E2E Test Suite | Automated test harness validating Tiers 1-4 test scenarios with `ffprobe` verification | M5 | ORIGINAL_REQUEST §Acceptance Criteria |
| F13 | End-to-End Verification & Hardening | Full rendering of `M2060568725.honreplay` + Tier 5 adversarial stress testing | M6 | ORIGINAL_REQUEST §Acceptance Criteria |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Legacy Codebase Decommissioning | Remove `tools/replay-vulkan-renderer` from solutions, verify clean solution build | none | DONE |
| M2 | Native Vulkan Layer Optimization & Staging | Fix `layer_manifest.h` log path, build CMake Release layer, stage DLLs to `games/client/` | none | DONE |
| M3 | Replay Engine Automation, CLI & Pipeline | Add `--player`, `--resolution`, `--start`, auto-paths, camera lock & UI config generation | M1, M2 | DONE (118/118 unit tests passing) |
| M4 | PowerShell Automation & Verbose Telemetry | Create `render-replay.ps1` complying with PowerShell rules and real-time telemetry | M3 | DONE (Strict cmdlet compliance) |
| M5 | E2E Testing Track & Test Harness | Implement comprehensive opaque-box test suite (Tiers 1-4) and publish `TEST_READY.md` | M2, M3 | DONE (29/29 E2E tests passing) |
| M6 | Final Verification & Adversarial Hardening | Render full replay `M2060568725.honreplay`, verify MP4 stream integrity, run Tier 5 tests | M4, M5 | DONE (Gate passed, Clean audit) |

## Code Layout
- `services/replay-renderer/src/`: Canonical Replay Renderer .NET 10 service & CLI
  - `Program.cs`: CLI argument parser and entry point
  - `Configuration/RenderJobConfiguration.cs`: Configuration models and parsing
  - `ReplayProcessLauncher.cs`: `hon_x64.exe` process management, VFS, and `autoexec.cfg` generator
  - `Ipc/VulkanMemoryMappedReceiver.cs`: Shared memory frame receiver
  - `Audio/WasapiLoopbackAudioCapture.cs`: WASAPI audio loopback
  - `Encoding/FfmpegArgumentBuilder.cs`: FFmpeg command line builder
  - `TelemetryLogger.cs` & `RenderProgressMonitor.cs`: Real-time telemetry monitoring
- `native/vulkan-layer/`: C++ Vulkan 1.3 Swapchain Capture Layer (`VkLayer_hon_replay_capture.dll`)
  - `layer_manifest.h`, `layer_dispatch.cpp`, `frame_capturer.cpp`, `CMakeLists.txt`
- `games/client/`: HoN game client binaries, DXVK (`d3d9.dll`), configurations, and sample replays
- `render-replay.ps1`: Primary PowerShell runner script complying with Windows PowerShell rules
- `tests/e2e/`: Automated E2E verification test suite (Tiers 1-4)

## Interface Contracts
### CLI ↔ ReplayRenderer.Service
```
Arguments:
  --replay <path>             Path to .honreplay file (required)
  --output <path>             Path to output .mp4 file (required)
  --player <name_or_slot>     Player name or slot index (0..9) for locked camera (default: 0)
  --resolution <WxH>          Output video resolution (default: 1920x1080)
  --fps <fps>                 Output framerate (default: 60)
  --start <seconds>           Start time offset in seconds (default: 0)
  --duration <seconds>        Duration to record in seconds (optional)
  --codec <codec>             Video codec: h264_nvenc, hevc_nvenc, libx264 (default: h264_nvenc)
  --game-dir <path>           Game client directory (default: .\games\client)
  --layer-dir <path>          Vulkan layer directory (default: .\native\vulkan-layer\build\Release)
```

### Shared Memory Frame Protocol (Vulkan Layer ↔ ReplayRenderer)
- Shared Memory Name: `Local\HonReplayFrameBuffer_<PID>`
- Synchronization Events: `Local\HonFrameReady_<PID>_<slot>`, `Local\HonFrameDone_<PID>_<slot>` (slots 0..2)
- Frame Header (64 bytes):
  - `uint32_t magic` (`0x484F4E43`)
  - `uint32_t version` (1)
  - `uint32_t width`, `uint32_t height`, `uint32_t format` (44 = VK_FORMAT_B8G8R8A8_UNORM)
  - `uint32_t rowPitch`, `uint64_t frameIndex`, `uint64_t timestampNs`, `uint64_t dataSizeBytes`, `uint32_t bufferSlotIndex`
