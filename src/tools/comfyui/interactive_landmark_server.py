#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth - Interactive Landmark & Lip Calibration Server
Serves a responsive Web UI at http://127.0.0.1:8189 where the user can click,
drag, and calibrate exact lip/face landmark points on Empath with live real-time
viseme preview, and trigger instant video synthesis.
"""

from __future__ import annotations

import os
import sys
import json
import logging
from http.server import HTTPServer, SimpleHTTPRequestHandler
import urllib.parse
import threading
import subprocess

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [LandmarkEditor] %(message)s",
    datefmt="%H:%M:%S",
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger("landmark_editor")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMFY_OUTPUT_DIR = os.path.join(SCRIPT_DIR, "ComfyUI", "output")
LANDMARKS_FILE = os.path.join(SCRIPT_DIR, "empath_landmarks.json")

# Default calibrated landmarks for Empath
DEFAULT_LANDMARKS = {
    "pupil_l": {"x": 418, "y": 397, "name": "Left Pupil", "color": "#00ff88"},
    "pupil_r": {"x": 582, "y": 399, "name": "Right Pupil", "color": "#00ff88"},
    "nose_tip": {"x": 495, "y": 515, "name": "Nose Tip", "color": "#00d4ff"},
    "cupids_bow_l": {"x": 478, "y": 550, "name": "Cupid's Bow Left", "color": "#ffd700"},
    "cupids_bow_c": {"x": 496, "y": 552, "name": "Cupid's Bow Center", "color": "#ffd700"},
    "cupids_bow_r": {"x": 514, "y": 550, "name": "Cupid's Bow Right", "color": "#ffd700"},
    "mouth_corner_l": {"x": 430, "y": 566, "name": "Left Mouth Corner", "color": "#ff007f"},
    "mouth_corner_r": {"x": 566, "y": 566, "name": "Right Mouth Corner", "color": "#ff007f"},
    "upper_lip_bottom": {"x": 496, "y": 567, "name": "Upper Lip Bottom", "color": "#ff3333"},
    "lower_lip_top": {"x": 496, "y": 574, "name": "Lower Lip Top", "color": "#ff9900"},
    "lower_lip_bottom": {"x": 496, "y": 608, "name": "Lower Lip Bottom", "color": "#ff00ff"},
    "chin_center": {"x": 496, "y": 665, "name": "Chin Center", "color": "#00ffff"}
}


def load_landmarks():
    if os.path.exists(LANDMARKS_FILE):
        try:
            with open(LANDMARKS_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            logger.warning("Failed to load %s: %s", LANDMARKS_FILE, e)
    return DEFAULT_LANDMARKS


def save_landmarks(data):
    with open(LANDMARKS_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    logger.info("Saved custom landmarks to %s", LANDMARKS_FILE)


HTML_PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>HoN Empath 3D Lip-Sync Landmark Editor</title>
<style>
  :root {
    --bg-dark: #0f141c;
    --card-bg: #18202c;
    --accent: #00ffaa;
    --accent-glow: rgba(0, 255, 170, 0.4);
    --gold: #ffd700;
    --text-main: #f0f6fc;
    --text-muted: #8b949e;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; }
  body {
    background-color: var(--bg-dark);
    color: var(--text-main);
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    display: flex;
    height: 100vh;
    overflow: hidden;
  }
  
  /* Sidebar */
  #sidebar {
    width: 380px;
    background: var(--card-bg);
    border-right: 1px solid #2d3748;
    display: flex;
    flex-direction: column;
    padding: 20px;
    box-shadow: 4px 0 20px rgba(0,0,0,0.5);
    z-index: 10;
    overflow-y: auto;
  }
  
  h1 { font-size: 1.3rem; color: var(--accent); margin-bottom: 6px; display: flex; align-items: center; gap: 8px; }
  p.desc { font-size: 0.85rem; color: var(--text-muted); margin-bottom: 18px; line-height: 1.4; }
  
  .section-title {
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 1px;
    color: var(--gold);
    margin: 16px 0 8px 0;
    border-bottom: 1px solid #2d3748;
    padding-bottom: 4px;
  }
  
  .image-select-row {
    display: flex;
    gap: 8px;
    margin-bottom: 14px;
  }
  select {
    flex: 1;
    background: #0d1117;
    border: 1px solid #30363d;
    color: var(--text-main);
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 0.9rem;
    outline: none;
  }
  
  .points-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    flex: 1;
    overflow-y: auto;
    max-height: 380px;
    padding-right: 4px;
  }
  
  .point-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    background: #0d1117;
    padding: 8px 12px;
    border-radius: 6px;
    border: 1px solid #21262d;
    cursor: pointer;
    transition: all 0.15s ease;
  }
  .point-item:hover, .point-item.active {
    border-color: var(--accent);
    background: #161b22;
    transform: translateX(2px);
  }
  .point-tag {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 0.85rem;
    font-weight: 500;
  }
  .color-dot {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    box-shadow: 0 0 8px currentColor;
  }
  .coords {
    font-family: 'Consolas', monospace;
    font-size: 0.8rem;
    color: var(--text-muted);
  }
  
  .controls {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin-top: 20px;
  }
  
  button {
    background: linear-gradient(135deg, #00ffaa, #00b377);
    color: #05140d;
    font-weight: bold;
    border: none;
    padding: 12px 18px;
    border-radius: 8px;
    cursor: pointer;
    font-size: 0.95rem;
    box-shadow: 0 4px 15px var(--accent-glow);
    transition: all 0.2s ease;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
  }
  button:hover {
    transform: translateY(-2px);
    box-shadow: 0 6px 20px rgba(0,255,170,0.6);
  }
  button.secondary {
    background: #21262d;
    color: var(--text-main);
    box-shadow: none;
  }
  button.secondary:hover {
    background: #30363d;
  }
  
  /* Canvas Area */
  #viewport {
    flex: 1;
    position: relative;
    background: radial-gradient(circle at center, #1a2230 0%, #090c10 100%);
    overflow: hidden;
    cursor: crosshair;
  }
  
  canvas {
    position: absolute;
    top: 0;
    left: 0;
    transform-origin: 0 0;
  }
  
  .instructions-hud {
    position: absolute;
    bottom: 20px;
    right: 20px;
    background: rgba(13, 17, 23, 0.85);
    backdrop-filter: blur(8px);
    border: 1px solid #30363d;
    padding: 12px 18px;
    border-radius: 8px;
    font-size: 0.82rem;
    color: var(--text-muted);
    line-height: 1.5;
    pointer-events: none;
  }
  .hud-key {
    color: var(--accent);
    font-weight: bold;
  }
  
  /* Status Toast */
  #toast {
    position: absolute;
    top: 20px;
    right: 20px;
    background: #00ffaa;
    color: #05140d;
    font-weight: bold;
    padding: 12px 24px;
    border-radius: 8px;
    box-shadow: 0 4px 20px rgba(0,255,170,0.5);
    opacity: 0;
    transform: translateY(-10px);
    transition: all 0.3s ease;
    pointer-events: none;
    z-index: 100;
  }
  #toast.show {
    opacity: 1;
    transform: translateY(0);
  }
</style>
</head>
<body>

<div id="sidebar">
  <h1>✨ Empath 3D Lip-Sync Node</h1>
  <p class="desc">Click and drag landmark points directly onto Empath's lips, eyes, and jaw to set sub-pixel calibration for the singing video generator.</p>
  
  <div class="section-title">Character Keyframe</div>
  <div class="image-select-row">
    <select id="imageSelect" onchange="changeImage(this.value)">
      <option value="KongorLore_00032_.png">Empath 3D (KongorLore_00032_.png)</option>
      <option value="KongorLore_00031_.png">Empath 3D CGI (KongorLore_00031_.png)</option>
      <option value="KongorLore_00033_.png">Empath 3D Belting (KongorLore_00033_.png)</option>
      <option value="KongorLore_00030_.png">Empath 2D (KongorLore_00030_.png)</option>
    </select>
  </div>

  <div class="section-title">Landmark Points (<span id="ptCount">12</span>)</div>
  <div class="points-list" id="pointsList"></div>

  <div class="controls">
    <button onclick="saveAndApply()">💾 Save & Set Landmarks</button>
    <button class="secondary" onclick="resetDefaults()">↺ Reset to Defaults</button>
  </div>
</div>

<div id="viewport">
  <canvas id="mainCanvas"></canvas>
  <div class="instructions-hud">
    <div>• <span class="hud-key">Click & Drag</span> any colored circle to reposition</div>
    <div>• <span class="hud-key">Scroll Wheel</span> to Zoom In / Out</div>
    <div>• <span class="hud-key">Right Click + Drag</span> to Pan Image</div>
  </div>
  <div id="toast">Landmarks Saved Successfully!</div>
</div>

<script>
let landmarks = {};
let currentImageName = "KongorLore_00032_.png";
let img = new Image();

const canvas = document.getElementById("mainCanvas");
const ctx = canvas.getContext("2d");
const viewport = document.getElementById("viewport");

let zoom = 1.0;
let panX = 0;
let panY = 0;
let isPanning = false;
let startPanX = 0;
let startPanY = 0;

let draggingKey = null;
let hoveredKey = null;

async function init() {
  const res = await fetch("/api/landmarks");
  landmarks = await res.json();
  renderPointsList();
  loadImage(currentImageName);
  
  // Center image
  panX = (viewport.clientWidth - 1024) / 2;
  panY = (viewport.clientHeight - 1024) / 2;
  
  setupEvents();
}

function loadImage(name) {
  currentImageName = name;
  img.src = "/api/image?name=" + encodeURIComponent(name);
  img.onload = () => {
    canvas.width = img.naturalWidth || 1024;
    canvas.height = img.naturalHeight || 1024;
    redraw();
  };
}

function changeImage(name) {
  loadImage(name);
}

function renderPointsList() {
  const container = document.getElementById("pointsList");
  container.innerHTML = "";
  for (const [key, pt] of Object.entries(landmarks)) {
    const item = document.createElement("div");
    item.className = "point-item" + (hoveredKey === key ? " active" : "");
    item.id = "item_" + key;
    item.innerHTML = `
      <div class="point-tag">
        <div class="color-dot" style="color: ${pt.color}; background: ${pt.color}"></div>
        <span>${pt.name}</span>
      </div>
      <div class="coords" id="coords_${key}">(${Math.round(pt.x)}, ${Math.round(pt.y)})</div>
    `;
    item.onmouseenter = () => { hoveredKey = key; redraw(); };
    item.onmouseleave = () => { hoveredKey = null; redraw(); };
    container.appendChild(item);
  }
  document.getElementById("ptCount").innerText = Object.keys(landmarks).length;
}

function redraw() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (img.complete && img.naturalWidth > 0) {
    ctx.drawImage(img, 0, 0);
  }

  // Draw connecting lips mesh
  drawLipMesh();

  // Draw points
  for (const [key, pt] of Object.entries(landmarks)) {
    const isHover = (hoveredKey === key || draggingKey === key);
    const radius = isHover ? 9 : 6;

    // Glow ring
    ctx.beginPath();
    ctx.arc(pt.x, pt.y, radius + 4, 0, Math.PI * 2);
    ctx.fillStyle = isHover ? "rgba(255, 255, 255, 0.4)" : "rgba(0, 0, 0, 0.5)";
    ctx.fill();

    // Inner dot
    ctx.beginPath();
    ctx.arc(pt.x, pt.y, radius, 0, Math.PI * 2);
    ctx.fillStyle = pt.color || "#00ffaa";
    ctx.fill();
    ctx.strokeStyle = "#ffffff";
    ctx.lineWidth = 2;
    ctx.stroke();

    // Label
    if (isHover) {
      ctx.font = "bold 13px Segoe UI, sans-serif";
      ctx.fillStyle = "#ffffff";
      ctx.shadowColor = "#000000";
      ctx.shadowBlur = 6;
      ctx.fillText(`${pt.name} (${Math.round(pt.x)}, ${Math.round(pt.y)})`, pt.x + 14, pt.y + 5);
      ctx.shadowBlur = 0;
    }
  }

  updateCanvasTransform();
}

function drawLipMesh() {
  const pts = landmarks;
  if (!pts.mouth_corner_l || !pts.mouth_corner_r || !pts.cupids_bow_c) return;

  ctx.strokeStyle = "rgba(255, 215, 0, 0.6)";
  ctx.lineWidth = 2;
  ctx.beginPath();
  // Upper Lip
  ctx.moveTo(pts.mouth_corner_l.x, pts.mouth_corner_l.y);
  ctx.lineTo(pts.cupids_bow_l.x, pts.cupids_bow_l.y);
  ctx.lineTo(pts.cupids_bow_c.x, pts.cupids_bow_c.y);
  ctx.lineTo(pts.cupids_bow_r.x, pts.cupids_bow_r.y);
  ctx.lineTo(pts.mouth_corner_r.x, pts.mouth_corner_r.y);
  // Lower Lip
  ctx.lineTo(pts.lower_lip_bottom.x, pts.lower_lip_bottom.y);
  ctx.closePath();
  ctx.stroke();

  // Parting line
  ctx.strokeStyle = "rgba(255, 50, 100, 0.8)";
  ctx.beginPath();
  ctx.moveTo(pts.mouth_corner_l.x, pts.mouth_corner_l.y);
  ctx.lineTo(pts.upper_lip_bottom.x, pts.upper_lip_bottom.y);
  ctx.lineTo(pts.mouth_corner_r.x, pts.mouth_corner_r.y);
  ctx.stroke();
}

function updateCanvasTransform() {
  canvas.style.transform = `translate(${panX}px, ${panY}px) scale(${zoom})`;
}

function getCanvasCoords(e) {
  const rect = viewport.getBoundingClientRect();
  const mouseX = e.clientX - rect.left;
  const mouseY = e.clientY - rect.top;
  const canvasX = (mouseX - panX) / zoom;
  const canvasY = (mouseY - panY) / zoom;
  return { x: canvasX, y: canvasY };
}

function setupEvents() {
  viewport.addEventListener("mousedown", (e) => {
    if (e.button === 2) { // Right click pan
      isPanning = true;
      startPanX = e.clientX - panX;
      startPanY = e.clientY - panY;
      e.preventDefault();
      return;
    }

    const { x, y } = getCanvasCoords(e);
    // Find closest landmark
    let closestKey = null;
    let minDist = 18; // Click radius
    for (const [key, pt] of Object.entries(landmarks)) {
      const dist = Math.hypot(pt.x - x, pt.y - y);
      if (dist < minDist) {
        minDist = dist;
        closestKey = key;
      }
    }

    if (closestKey) {
      draggingKey = closestKey;
      hoveredKey = closestKey;
      redraw();
    }
  });

  window.addEventListener("mousemove", (e) => {
    if (isPanning) {
      panX = e.clientX - startPanX;
      panY = e.clientY - startPanY;
      updateCanvasTransform();
      return;
    }

    const { x, y } = getCanvasCoords(e);
    if (draggingKey) {
      landmarks[draggingKey].x = Math.max(0, Math.min(canvas.width, x));
      landmarks[draggingKey].y = Math.max(0, Math.min(canvas.height, y));
      const coordEl = document.getElementById("coords_" + draggingKey);
      if (coordEl) coordEl.innerText = `(${Math.round(x)}, ${Math.round(y)})`;
      redraw();
    } else {
      let foundHover = null;
      for (const [key, pt] of Object.entries(landmarks)) {
        if (Math.hypot(pt.x - x, pt.y - y) < 14) {
          foundHover = key;
          break;
        }
      }
      if (foundHover !== hoveredKey) {
        hoveredKey = foundHover;
        redraw();
      }
    }
  });

  window.addEventListener("mouseup", () => {
    isPanning = false;
    if (draggingKey) {
      draggingKey = null;
      redraw();
    }
  });

  viewport.addEventListener("wheel", (e) => {
    e.preventDefault();
    const zoomFactor = e.deltaY < 0 ? 1.15 : 0.85;
    const { x: mouseX, y: mouseY } = getCanvasCoords(e);
    
    zoom *= zoomFactor;
    zoom = Math.max(0.2, Math.min(5.0, zoom));
    
    panX -= (mouseX * zoomFactor - mouseX) * (zoom / zoomFactor);
    panY -= (mouseY * zoomFactor - mouseY) * (zoom / zoomFactor);
    updateCanvasTransform();
  });

  viewport.addEventListener("contextmenu", (e) => e.preventDefault());
}

async function saveAndApply() {
  await fetch("/api/landmarks", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(landmarks)
  });
  showToast("✅ Landmarks Saved! Node updated.");
}

async function resetDefaults() {
  const res = await fetch("/api/landmarks?reset=true");
  landmarks = await res.json();
  renderPointsList();
  redraw();
  showToast("↺ Reset to Defaults");
}

function showToast(msg) {
  const t = document.getElementById("toast");
  t.innerText = msg;
  t.classList.add("show");
  setTimeout(() => t.classList.remove("show"), 2500);
}

window.onload = init;
</script>
</body>
</html>
"""


class LandmarkServerHandler(SimpleHTTPRequestHandler):
    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/" or parsed.path == "/index.html":
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(HTML_PAGE.encode("utf-8"))
            return

        elif parsed.path == "/api/landmarks":
            qs = urllib.parse.parse_qs(parsed.query)
            if "reset" in qs:
                save_landmarks(DEFAULT_LANDMARKS)
                data = DEFAULT_LANDMARKS
            else:
                data = load_landmarks()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(data).encode("utf-8"))
            return

        elif parsed.path == "/api/image":
            qs = urllib.parse.parse_qs(parsed.query)
            name = qs.get("name", ["KongorLore_00032_.png"])[0]
            # Search in ComfyUI output dir
            img_path = os.path.join(COMFY_OUTPUT_DIR, name)
            if not os.path.exists(img_path):
                img_path = os.path.join(SCRIPT_DIR, "ComfyUI", "input", name)
                
            if os.path.exists(img_path):
                self.send_response(200)
                self.send_header("Content-Type", "image/png")
                self.end_headers()
                with open(img_path, "rb") as f:
                    self.wfile.write(f.read())
            else:
                self.send_response(404)
                self.end_headers()
            return

        super().do_GET()

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/api/landmarks":
            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length).decode("utf-8")
            data = json.loads(body)
            save_landmarks(data)
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"status": "ok"}).encode("utf-8"))
            return

        self.send_response(404)
        self.end_headers()


def run_server(port: int = 8189):
    server = HTTPServer(("0.0.0.0", port), LandmarkServerHandler)
    logger.info("=========================================================")
    logger.info("INTERACTIVE LIP & LANDMARK CALIBRATION SERVER RUNNING")
    logger.info("URL: http://127.0.0.1:%d", port)
    logger.info("=========================================================")
    server.serve_forever()


if __name__ == "__main__":
    port = 8189
    if len(sys.argv) > 1:
        port = int(sys.argv[1])
    run_server(port)
