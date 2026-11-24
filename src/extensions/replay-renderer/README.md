<div align="center">

# 🎬 Replay Video Renderer (`NEXUS.ReplayRenderer`)
### *Headless Vulkan to 60FPS MP4 Match Highlight Renderer*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![NVENC](https://img.shields.io/badge/NVENC-Hardware%20Accelerated-76B900?style=for-the-badge&logo=nvidia&logoColor=white)](https://ffmpeg.org/)
[![Vulkan Headless](https://img.shields.io/badge/Vulkan-Offscreen%20Capture-E74C3C?style=for-the-badge&logo=vulkan&logoColor=white)](https://www.vulkan.org/)

**Replay Video Renderer is the automated highlight extraction and 60FPS MP4 video rendering engine for ProjectKongor match replays (`.honreplay`).**

[⚡ Quickstart](#-quickstart-render-highlights) •
[🌟 Key Features](#-key-features) •
[🗺️ Replay Pipeline Map](#-replay-renderer--combat-juice-architecture)

---

### ⚡ Quickstart: Render Highlights
Automatically detect teamfights and render an MP4 highlight reel in **Windows PowerShell**:

```powershell
kongor highlight <path/to/match.honreplay>
```
*(Or render the full 60FPS match: `kongor replay render <match.honreplay>`)*

---

</div>

## 🌟 Key Features

* **🎥 Automated Teamfight Detection**: Analyzes combat juice events, rapid hero kills, and high damage spikes to generate highlight clip timestamps.
* **🌋 Headless Vulkan Frame Capture**: Captures 1080p/4K 60FPS offscreen game frames via Vulkan swapchain intercept.
* **🎞️ FFmpeg & NVENC Video Encoding**: Direct hardware-accelerated H.264 / HEVC video encoding for instant replay sharing.

---

## 🗺️ Replay Renderer & Combat Juice Architecture

<div align="center">
  <img src="../../../docs/images/replay_renderer_architecture.svg" alt="Replay Renderer Architecture" width="100%" />
</div>

<div align="center">
  <img src="../../../docs/images/combat_juice_architecture.svg" alt="Combat Juice Architecture" width="100%" />
</div>
