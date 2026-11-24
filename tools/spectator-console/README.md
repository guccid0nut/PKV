# 📺 Live Spectator Console (`tools/spectator-console`)

> **Async `aiohttp` WebSocket + HTTP relay streaming Vulkan `HonCap` frames and Seqlock telemetry to browsers.**

`tools/spectator-console` connects to the `VkLayer_hon_replay_capture` shared-memory frame ring (`HonCapReader`) and `TelemetryReader` Seqlock stream, serving a live browser spectator dashboard over HTTP and WebSockets (`/ws/stream`).

---

## ⚡ Quick Start

```powershell
# Start the WebSocket & static spectator console server (default port 8088)
python tools/spectator-console/streamer.py --host 0.0.0.0 --port 8088
```

Then open `http://localhost:8088/` in your browser.

---

## 🏗️ Architecture & Endpoints

- **`streamer.py` (`ViewportStreamer`):** Async `aiohttp` server exposing:
  - `GET /ws/stream` — Real-time binary JPEG frame & telemetry WebSocket stream
  - `GET /api/status` — Streamer FPS, active WebSocket clients, and Vulkan/Seqlock connection state
  - `GET /api/telemetry` — Latest JSON telemetry snapshot
  - `GET /api/snapshot.jpg` — Single JPEG frame (synthesizes a fallback frame when game client is idle)
- **`honcap_reader.py` & `telemetry_reader.py`:** Shared-memory Vulkan capture & Seqlock state readers.
- **`index.html`, `app.js`, `style.css`:** Browser spectator console frontend.
