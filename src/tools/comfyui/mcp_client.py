#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Python Client for ComfyUI and ComfyUI MCP Server.
Provides convenient high-level methods for pinging, inspecting models,
and queuing comic generations programmatically.
"""

from __future__ import annotations

import json
import logging
import os
import urllib.parse
import urllib.request
import uuid
from typing import Any, Dict, List, Optional

logger = logging.getLogger("ComfyUIClient")


class ComfyUIMCPClient:
    """Client for interacting with ComfyUI REST & WebSocket API."""

    def __init__(self, host: str = "127.0.0.1", port: int = 8188):
        self.host = host
        self.port = port
        self.base_url = f"http://{host}:{port}"

    def ping(self) -> bool:
        """Check if ComfyUI is running and responding."""
        try:
            url = f"{self.base_url}/system_stats"
            with urllib.request.urlopen(url, timeout=2) as resp:
                return resp.status == 200
        except Exception:
            return False

    def get_stats(self) -> Dict[str, Any]:
        """Fetch system, GPU, and memory metrics."""
        try:
            with urllib.request.urlopen(f"{self.base_url}/system_stats", timeout=5) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as ex:
            return {"error": str(ex), "status": "offline"}

    def list_checkpoints(self) -> List[str]:
        """List all available checkpoints."""
        try:
            with urllib.request.urlopen(f"{self.base_url}/object_info/CheckpointLoaderSimple", timeout=5) as resp:
                data = json.loads(resp.read().decode("utf-8"))
                return data["CheckpointLoaderSimple"]["input"]["required"]["ckpt_name"][0]
        except Exception:
            return []

    def generate_image(
        self,
        prompt: str,
        negative_prompt: str = "blurry, bad anatomy, lowres, watermark",
        checkpoint: Optional[str] = None,
        width: int = 1024,
        height: int = 1024,
        steps: int = 25,
        cfg: float = 7.0,
        seed: int = 0
    ) -> Dict[str, Any]:
        """Generate a single image from prompt."""
        try:
            from mcp_server import build_standard_sdxl_workflow, ComfyUIBridge
        except ImportError:
            from .mcp_server import build_standard_sdxl_workflow, ComfyUIBridge
        bridge = ComfyUIBridge(self.host, self.port)
        if not checkpoint:
            ckpts = self.list_checkpoints()
            checkpoint = ckpts[0] if ckpts else "v1-5-pruned-emaonly.safetensors"

        workflow = build_standard_sdxl_workflow(
            ckpt_name=checkpoint,
            prompt=prompt,
            negative_prompt=negative_prompt,
            width=width,
            height=height,
            steps=steps,
            cfg=cfg,
            seed=seed
        )
        res = bridge.queue_prompt(workflow)
        pid = res.get("prompt_id")
        if not pid:
            return {"error": "Failed queueing prompt", "response": res}
        images = bridge.wait_for_prompt_completion(pid)
        return {
            "prompt_id": pid,
            "images": images,
            "view_urls": [f"{self.base_url}/view?filename={img['filename']}&subfolder={img.get('subfolder','')}&type={img.get('type','output')}" for img in (images or [])]
        }

    def generate_comic_panel(
        self,
        character: str,
        scene_description: str,
        style: str = "graphic novel, crisp ink lineart, vibrant color, dramatic lighting",
        width: int = 1920,
        height: int = 1080,
        seed: int = 0
    ) -> Dict[str, Any]:
        """Generate a high-res graphic novel comic panel."""
        prompt = f"masterpiece, best quality, {style}, Project Kongor universe, {character}, {scene_description}"
        return self.generate_image(
            prompt=prompt,
            width=width,
            height=height,
            seed=seed
        )


if __name__ == "__main__":
    client = ComfyUIMCPClient()
    online = client.ping()
    print(f"ComfyUI Online: {online}")
    if online:
        print("Stats:", client.get_stats())
        print("Checkpoints:", client.list_checkpoints())
