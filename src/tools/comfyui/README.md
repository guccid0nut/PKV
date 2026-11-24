# 🎨 ProjectKongor ComfyUI Generative AI & MCP Tool Suite

> **Directory:** `src/tools/comfyui` | **Default Port:** `8188` | **MCP Port:** `stdio / 8188`

The **ProjectKongor ComfyUI Tool Suite** provides an automated, cross-platform generative AI environment for Heroes of Newerth asset creation, lore comic generation, 3D character lip-sync animation (Empath & Ophelia), and AI agent integration via the **Model Context Protocol (MCP)**.

---

## 🚀 Quick Start via Kongor CLI

The fastest way to install, launch, monitor, and stop ComfyUI is through the unified `kongor` command-line tool or `kongor.ps1` script:

```powershell
# 1. Automated cross-platform installation (PyTorch CUDA 12.4 / MPS, venv, ComfyUI-Manager, MCP)
kongor comfy install
# or
.\kongor.ps1 comfy install

# 2. Launch ComfyUI generative server (http://127.0.0.1:8188)
kongor comfy
# or
.\kongor.ps1 comfy

# 3. Stop running ComfyUI server and MCP instances
kongor comfy stop
# or
.\kongor.ps1 comfy stop

# 4. Cleanly uninstall ComfyUI repository, virtual environment, and temp caches
kongor comfy uninstall
# or
.\kongor.ps1 comfy uninstall
```

---

## 📋 Features & Architecture

```
                    ┌──────────────────────────────────────────────┐
                    │      Antigravity / Claude / AI Agent         │
                    └──────────────────────┬───────────────────────┘
                                           │ (MCP Stdio / JSON-RPC)
                                           ▼
                    ┌──────────────────────────────────────────────┐
                    │       mcp_server.py (MCP Bridge)             │
                    │   • comfyui_get_status                       │
                    │   • comfyui_list_models                      │
                    │   • comfyui_generate_image                   │
                    │   • comfyui_generate_comic_panel             │
                    │   • comfyui_run_workflow                     │
                    │   • comfyui_interrupt                        │
                    └──────────────────────┬───────────────────────┘
                                           │ (HTTP REST & WebSockets)
                                           ▼
                    ┌──────────────────────────────────────────────┐
                    │   ComfyUI Engine (http://127.0.0.1:8188)     │
                    │   • Stable Diffusion XL / 1.5                │
                    │   • ControlNet & OpenPose                    │
                    │   • AnimateDiff & SVD Video Generation       │
                    │   • ComfyUI-Manager Node Ecosystem           │
                    └──────────────────────────────────────────────┘
```

- **Cross-Platform Acceleration**:
  - **Windows**: PyTorch with NVIDIA CUDA 12.4 acceleration (RTX series optimized).
  - **macOS**: PyTorch with native Apple Silicon Metal Performance Shaders (MPS).
  - **Linux**: PyTorch with CUDA or ROCm GPU acceleration.
- **Isolated Virtual Environment**:
  - All Python packages, PyTorch wheels, and custom node dependencies are installed inside `src/tools/comfyui/.venv/` to prevent conflicting with system packages.
- **Model Context Protocol (MCP) Server**:
  - Exposes standard MCP tools so AI agents can inspect GPU hardware, query available model checkpoints, generate 4K hero illustrations, and create multi-panel comics directly.
- **Cinematic Character Video & Lip-Sync Pipelines**:
  - Automated pipelines for generating audio-reactive singing videos and 3D facial landmark mesh animations for HoN heroes (Empath, Ophelia).
- **100% Optional & Zero-Bloat**:
  - Completely git-ignored (`.gitignore`), only downloaded when explicitly requested via `kongor comfy install`.

---

## 🔌 Model Context Protocol (MCP) Server

The MCP server enables AI assistants (such as Antigravity, Claude, and Gemini) to interact with ComfyUI as a tool provider.

### Running the MCP Server
```powershell
# Run the MCP server over stdio:
kongor comfyui_mcp
# or
python src/tools/comfyui/mcp_server.py
```

### Exposed MCP Tools

| Tool Name | Parameters | Description |
|---|---|---|
| **`comfyui_get_status`** | — | Checks whether ComfyUI is online, returns GPU VRAM usage, and reports active queue length. |
| **`comfyui_list_models`** | — | Discovers and returns all installed model checkpoints, LoRAs, VAEs, and ControlNet models. |
| **`comfyui_generate_image`** | `prompt`, `negative_prompt`, `width`, `height`, `steps`, `cfg_scale`, `sampler`, `checkpoint` | Submits a text-to-image workflow and returns the generated image as Base64 or local file path. |
| **`comfyui_generate_comic_panel`** | `prompt`, `dialogue`, `panel_number`, `style`, `character` | Generates a styled lore comic panel with customizable artistic presets (e.g. `hon_dark_fantasy`, `gritty_comic`). |
| **`comfyui_run_workflow`** | `workflow_json`, `wait_for_completion` | Executes an arbitrary raw ComfyUI JSON prompt graph. |
| **`comfyui_interrupt`** | — | Immediately cancels any actively executing generation job on the server. |

---

## 🎬 Specialized Animation & Video Pipelines

In addition to standard image generation, this directory contains specialized video generators:

### 1. Empath Lip-Synced Singing Video Generator
```powershell
python src/tools/comfyui/hon_empath_singing_video_generator.py \
    --audio src/tools/comfyui/audio/sample_song.mp3 \
    --output src/tools/comfyui/video_output/empath_singing.mp4 \
    --resolution 1080p
```
- Performs audio beat and pitch extraction to drive lip-syncing and expression morphs on Empath portrait renders.

### 2. 3D Landmark-Guided Character Video Generator
```powershell
python src/tools/comfyui/hon_empath_3d_singing_video_generator.py \
    --mesh-landmarks src/tools/comfyui/empath_landmarks.json \
    --audio src/tools/comfyui/audio/track.wav \
    --output src/tools/comfyui/video_output/empath_3d.mp4
```
- Blends HoN 3D bone skinning vectors with ControlNet OpenPose and Depth maps for temporally stable video generation.

### 3. Interactive Landmark Server
```powershell
python src/tools/comfyui/interactive_landmark_server.py --port 8089
```
- Web-based calibration server for visually aligning 2D facial feature landmarks to HoN 3D head meshes.

---

## 📦 Model Management

To download recommended base models (SDXL, SD 1.5, ControlNet):
```powershell
python src/tools/comfyui/download_models.py
```

Checkpoints and weights are stored in:
- `src/tools/comfyui/ComfyUI/models/checkpoints/`
- `src/tools/comfyui/ComfyUI/models/loras/`
- `src/tools/comfyui/ComfyUI/models/controlnet/`
- `src/tools/comfyui/ComfyUI/models/vae/`

> **Note:** All `.safetensors`, `.ckpt`, `.pt`, and `.onnx` files are automatically excluded by [`.gitignore`](../../../.gitignore).

---

## 🛠️ CLI Options Reference

### `launch_comfyui.py`
```powershell
python src/tools/comfyui/launch_comfyui.py [options]
```
- `--port <port>`: Port to bind ComfyUI (default: `8188`).
- `--listen <ip>`: IP address to listen on (default: `127.0.0.1`).
- `--lowvram`: Enable memory-saving low-VRAM optimizations for GPUs with < 6GB VRAM.
- `--no-browser`: Do not automatically open the browser on startup.
- `--status`: Check if the server is currently running and healthy.
- `--stop`: Terminate running ComfyUI and MCP processes.

### `install_comfyui.py`
```powershell
python src/tools/comfyui/install_comfyui.py [action] [options]
```
- `action`: `install` (default) or `uninstall`.
- `--uninstall`: Remove ComfyUI repository, virtual environment, and caches.
- `--skip-venv`: Use system Python instead of creating `.venv`.
- `--cuda`: Force NVIDIA CUDA wheels installation regardless of auto-detection.
- `--force`: Force re-download and re-installation of dependencies.
