#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Model Context Protocol (MCP) Server for ComfyUI.
Provides standard MCP tools (stdio & HTTP) enabling AI agents to programmatically
inspect hardware, list models, and generate 4K images & comic panels via ComfyUI.
"""

import argparse
import asyncio
import io
import json
import logging
import os
import sys
import time
import urllib.parse
import urllib.request
import uuid
from typing import Any, Dict, List, Optional

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [ComfyUI-MCP] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
    handlers=[logging.StreamHandler(sys.stderr)]
)
logger = logging.getLogger("comfyui_mcp")

DEFAULT_COMFYUI_HOST = "127.0.0.1"
DEFAULT_COMFYUI_PORT = 8188


class ComfyUIBridge:
    """HTTP & WebSocket bridge to local ComfyUI instance."""

    def __init__(self, host: str = DEFAULT_COMFYUI_HOST, port: int = DEFAULT_COMFYUI_PORT):
        self.host = host
        self.port = port
        self.base_url = f"http://{host}:{port}"

    def is_online(self) -> bool:
        try:
            with urllib.request.urlopen(f"{self.base_url}/system_stats", timeout=2) as resp:
                return resp.status == 200
        except Exception:
            return False

    def get_system_stats(self) -> Dict[str, Any]:
        try:
            with urllib.request.urlopen(f"{self.base_url}/system_stats", timeout=5) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as ex:
            return {"status": "offline", "error": str(ex), "url": self.base_url}

    def list_models(self) -> Dict[str, List[str]]:
        """Fetch available models (checkpoints, loras, controlnet, vae)."""
        result: Dict[str, List[str]] = {
            "checkpoints": [],
            "loras": [],
            "controlnet": [],
            "vae": []
        }
        for category in result.keys():
            try:
                url = f"{self.base_url}/object_info"
                with urllib.request.urlopen(url, timeout=5) as resp:
                    data = json.loads(resp.read().decode("utf-8"))
                    # Extract model names from node definitions
                    if category == "checkpoints" and "CheckpointLoaderSimple" in data:
                        result["checkpoints"] = data["CheckpointLoaderSimple"]["input"]["required"]["ckpt_name"][0]
                    elif category == "loras" and "LoraLoader" in data:
                        result["loras"] = data["LoraLoader"]["input"]["required"]["lora_name"][0]
                    elif category == "controlnet" and "ControlNetLoader" in data:
                        result["controlnet"] = data["ControlNetLoader"]["input"]["required"]["control_net_name"][0]
                    elif category == "vae" and "VAELoader" in data:
                        result["vae"] = data["VAELoader"]["input"]["required"]["vae_name"][0]
            except Exception as ex:
                logger.debug("Failed listing category %s: %s", category, ex)
        return result

    def queue_prompt(self, workflow: Dict[str, Any]) -> Dict[str, Any]:
        """Dispatch workflow to ComfyUI execution queue."""
        url = f"{self.base_url}/prompt"
        payload = json.dumps({"prompt": workflow, "client_id": str(uuid.uuid4())}).encode("utf-8")
        req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def get_history(self, prompt_id: str) -> Dict[str, Any]:
        """Retrieve execution outputs for a prompt ID."""
        url = f"{self.base_url}/history/{prompt_id}"
        with urllib.request.urlopen(url, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def wait_for_prompt_completion(self, prompt_id: str, timeout_sec: int = 120) -> Optional[List[Dict[str, str]]]:
        """Poll history until prompt execution completes and return output image metadata."""
        start = time.time()
        while time.time() - start < timeout_sec:
            hist = self.get_history(prompt_id)
            if prompt_id in hist:
                outputs = hist[prompt_id].get("outputs", {})
                images = []
                for node_id, node_out in outputs.items():
                    if "images" in node_out:
                        for img_info in node_out["images"]:
                            images.append(img_info)
                if images:
                    return images
            time.sleep(1.0)
        return None

    def interrupt(self) -> bool:
        """Interrupt active execution."""
        try:
            req = urllib.request.Request(f"{self.base_url}/interrupt", data=b"", headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=3) as resp:
                return resp.status == 200
        except Exception:
            return False


def build_standard_sdxl_workflow(
    ckpt_name: str,
    prompt: str,
    negative_prompt: str = "blurry, low quality, distorted, bad anatomy",
    width: int = 1024,
    height: int = 1024,
    steps: int = 6,
    cfg: float = 2.0,
    seed: int = 42,
    sampler_name: str = "dpmpp_sde",
    scheduler: str = "karras"
) -> Dict[str, Any]:
    """Construct a standard txt2img ComfyUI node graph."""
    return {
        "3": {
            "class_type": "KSampler",
            "inputs": {
                "cfg": cfg,
                "denoise": 1,
                "latent_image": ["5", 0],
                "model": ["4", 0],
                "negative": ["7", 0],
                "positive": ["6", 0],
                "sampler_name": sampler_name,
                "scheduler": scheduler,
                "seed": seed,
                "steps": steps
            }
        },
        "4": {
            "class_type": "CheckpointLoaderSimple",
            "inputs": {
                "ckpt_name": ckpt_name
            }
        },
        "5": {
            "class_type": "EmptyLatentImage",
            "inputs": {
                "batch_size": 1,
                "height": height,
                "width": width
            }
        },
        "6": {
            "class_type": "CLIPTextEncode",
            "inputs": {
                "clip": ["4", 1],
                "text": prompt
            }
        },
        "7": {
            "class_type": "CLIPTextEncode",
            "inputs": {
                "clip": ["4", 1],
                "text": negative_prompt
            }
        },
        "8": {
            "class_type": "VAEDecode",
            "inputs": {
                "samples": ["3", 0],
                "vae": ["4", 2]
            }
        },
        "9": {
            "class_type": "SaveImage",
            "inputs": {
                "filename_prefix": "KongorLore",
                "images": ["8", 0]
            }
        }
    }


# ============================================================================
# MCP Standard Server Setup (FastMCP + Shared _common/mcp_telemetry)
# ============================================================================

_TOOLS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

from _common.mcp_telemetry import (
    create_fastmcp_server,
    run_fastmcp_stdio,
    shield_stdio_stdout,
)

shield_stdio_stdout()

bridge = ComfyUIBridge()
server = create_fastmcp_server(
    "comfyui",
    instructions="ComfyUI Model Context Protocol Server for Project Kongor",
)


@server.tool(
    name="comfyui_get_status",
    description="Check if ComfyUI is online, inspect GPU hardware, VRAM, and execution queue."
)
def comfyui_get_status() -> Dict[str, Any]:
    stats = bridge.get_system_stats()
    online = bridge.is_online()
    return {"online": online, "system": stats}


@server.tool(
    name="comfyui_list_models",
    description="List all installed checkpoints, LoRAs, ControlNets, and VAEs in ComfyUI."
)
def comfyui_list_models() -> Dict[str, List[str]]:
    return bridge.list_models()


@server.tool(
    name="comfyui_generate_image",
    description="Generate an image via ComfyUI using standard txt2img workflow."
)
def comfyui_generate_image(
    prompt: str,
    negative_prompt: str = "blurry, low quality, deformed",
    checkpoint: Optional[str] = None,
    width: int = 1024,
    height: int = 1024,
    steps: int = 25,
    cfg: float = 7.0,
    seed: int = 0
) -> Dict[str, Any]:
    if not bridge.is_online():
        return {"error": "ComfyUI is offline. Please launch ComfyUI first using 'kongor comfyui'."}

    models = bridge.list_models()
    checkpoints = models.get("checkpoints", [])
    ckpt = checkpoint
    if not ckpt and checkpoints:
        ckpt = checkpoints[0]
    elif not ckpt:
        ckpt = "v1-5-pruned-emaonly.safetensors"

    s = seed
    if s == 0:
        s = int(time.time() * 1000) % 2147483647

    workflow = build_standard_sdxl_workflow(
        ckpt_name=ckpt,
        prompt=prompt,
        negative_prompt=negative_prompt,
        width=width,
        height=height,
        steps=steps,
        cfg=cfg,
        seed=s
    )

    resp = bridge.queue_prompt(workflow)
    prompt_id = resp.get("prompt_id")
    if not prompt_id:
        return {"error": "Failed to queue prompt in ComfyUI", "response": resp}

    images = bridge.wait_for_prompt_completion(prompt_id)
    return {
        "status": "completed",
        "prompt_id": prompt_id,
        "images": images,
        "view_urls": [
            f"{bridge.base_url}/view?filename={img['filename']}&subfolder={img.get('subfolder','')}&type={img.get('type','output')}"
            for img in (images or [])
        ]
    }


@server.tool(
    name="comfyui_generate_comic_panel",
    description="Generate a stylized Kongor Chronicles graphic novel comic panel using multi-reference character/location binding and optional lettering."
)
def comfyui_generate_comic_panel(
    character: str,
    scene_description: str,
    character_2: str = "None",
    location: str = "Primeval Canopy",
    camera_shot: str = "Cinematic Medium Shot",
    mood: str = "Cinematic Dark Fantasy, rim lighting",
    caption_top: str = "",
    dialogue: str = "",
    sfx_text: str = "",
    engine: str = "gemini-3.1-flash-image-preview",
    aspect_ratio: str = "4:3",
    seed: int = 0
) -> Dict[str, Any]:
    if not bridge.is_online():
        return {"error": "ComfyUI is offline. Please launch ComfyUI first using 'kongor comfyui'."}

    s = seed
    if s == 0:
        s = int(time.time() * 1000) % 2147483647

    logger.info(
        "[KongorChronicles MCP] Generating comic panel: char1=%s char2=%s loc=%s engine=%s seed=%d",
        character, character_2, location, engine, s
    )

    dialogue_items = []
    if caption_top:
        dialogue_items.append({
            "speaker": "Narrator",
            "text": caption_top,
            "style": "narration",
            "x_pct": 5,
            "y_pct": 5,
            "width_pct": 55
        })
    if dialogue:
        dialogue_items.append({
            "speaker": character,
            "text": dialogue,
            "style": "normal",
            "x_pct": 8,
            "y_pct": 18,
            "width_pct": 46,
            "tail_x_pct": 38,
            "tail_y_pct": 48
        })
    if sfx_text:
        dialogue_items.append({
            "speaker": "",
            "text": sfx_text,
            "style": "sfx",
            "x_pct": 62,
            "y_pct": 20,
            "width_pct": 30
        })

    workflow: Dict[str, Any] = {
        "1": {
            "class_type": "KongorCharacterRefLoader",
            "inputs": {
                "hero_dropdown": character,
                "prefer_turnaround_sheet": True,
                "hero_name_override": character
            }
        },
        "2": {
            "class_type": "KongorCharacterRefLoader",
            "inputs": {
                "hero_dropdown": character_2 if (character_2 and character_2 != "None") else "None",
                "prefer_turnaround_sheet": True,
                "hero_name_override": character_2 or "None"
            }
        },
        "3": {
            "class_type": "KongorLocationRefLoader",
            "inputs": {
                "location_dropdown": location,
                "chain_previous_panel": False,
                "story_slug": "mcp-single-panels",
                "current_panel_number": 1,
                "location_id_override": location
            }
        },
        "4": {
            "class_type": "KongorComicPromptCompiler",
            "inputs": {
                "shot_type": camera_shot,
                "character_a_name": ["1", 1],
                "character_a_tags": ["1", 2],
                "character_a_action": scene_description,
                "character_b_name": ["2", 1],
                "character_b_tags": ["2", 2],
                "character_b_action": "",
                "environment_tags": ["3", 1],
                "color_script": mood,
                "headroom_region": "top-left",
                "has_layout_guide_image": False
            }
        },
        "5": {
            "class_type": "ImageBatch",
            "inputs": {
                "image1": ["1", 0],
                "image2": ["3", 0]
            }
        }
    }

    if engine == "flux2_pro":
        workflow["6"] = {
            "class_type": "Flux2ProImageNode",
            "inputs": {
                "prompt": ["4", 0],
                "aspect_ratio": aspect_ratio,
                "resolution": "1 MP",
                "seed": s,
                "input_images": ["5", 0]
            }
        }
    elif engine == "local":
        models = bridge.list_models()
        checkpoints = models.get("checkpoints", [])
        ckpt = checkpoints[0] if checkpoints else "v1-5-pruned-emaonly.safetensors"
        local_wf = build_standard_sdxl_workflow(
            ckpt_name=ckpt,
            prompt=f"Graphic novel panel, heavy black ink lineart, {character}, {scene_description}",
            negative_prompt="blurry, low quality, bad anatomy, deformed, text, watermark",
            width=1024,
            height=1024,
            steps=25,
            cfg=7.0,
            seed=s
        )
        resp = bridge.queue_prompt(local_wf)
        prompt_id = resp.get("prompt_id")
        if not prompt_id:
            return {"error": "Failed to queue local fallback prompt in ComfyUI", "response": resp}
        images = bridge.wait_for_prompt_completion(prompt_id)
        return {
            "status": "completed",
            "prompt_id": prompt_id,
            "images": images,
            "view_urls": [
                f"{bridge.base_url}/view?filename={img['filename']}&subfolder={img.get('subfolder','')}&type={img.get('type','output')}"
                for img in (images or [])
            ]
        }
    else:
        workflow["6"] = {
            "class_type": "GeminiImage2Node",
            "inputs": {
                "prompt": ["4", 0],
                "model": "gemini-3.1-flash-image-preview",
                "seed": s,
                "aspect_ratio": aspect_ratio,
                "resolution": "1K",
                "response_modalities": "IMAGE",
                "images": ["5", 0],
                "system_prompt": ["4", 1]
            }
        }

    workflow["7"] = {
        "class_type": "KongorComicLetterer",
        "inputs": {
            "raw_image": ["6", 0],
            "story_slug": "mcp-single-panels",
            "panel_number": 1,
            "dialogue_json": json.dumps(dialogue_items, ensure_ascii=False),
            "save_to_disk": True
        }
    }
    workflow["8"] = {
        "class_type": "SaveImage",
        "inputs": {
            "images": ["7", 0],
            "filename_prefix": "KongorChronicles/mcp_panel_lettered"
        }
    }

    resp = bridge.queue_prompt(workflow)
    prompt_id = resp.get("prompt_id")
    if not prompt_id:
        return {"error": "Failed to queue prompt in ComfyUI", "response": resp}

    images = bridge.wait_for_prompt_completion(prompt_id)
    return {
        "status": "completed",
        "prompt_id": prompt_id,
        "images": images,
        "view_urls": [
            f"{bridge.base_url}/view?filename={img['filename']}&subfolder={img.get('subfolder','')}&type={img.get('type','output')}"
            for img in (images or [])
        ]
    }


@server.tool(
    name="comfyui_run_workflow",
    description="Execute an arbitrary ComfyUI node graph JSON."
)
def comfyui_run_workflow(workflow: Dict[str, Any]) -> Dict[str, Any]:
    if not bridge.is_online():
        return {"error": "ComfyUI is offline. Please launch ComfyUI first using 'kongor comfyui'."}
    resp = bridge.queue_prompt(workflow)
    prompt_id = resp.get("prompt_id")
    if not prompt_id:
        return {"error": "Failed to queue workflow", "response": resp}
    images = bridge.wait_for_prompt_completion(prompt_id)
    return {"status": "completed", "prompt_id": prompt_id, "images": images}


@server.tool(
    name="comfyui_interrupt",
    description="Interrupt the currently executing prompt generation."
)
def comfyui_interrupt() -> Dict[str, Any]:
    ok = bridge.interrupt()
    return {"success": ok}


def main() -> None:
    parser = argparse.ArgumentParser(description="ComfyUI MCP Server")
    parser.add_argument("--host", type=str, default=DEFAULT_COMFYUI_HOST, help="ComfyUI host")
    parser.add_argument("--port", type=int, default=DEFAULT_COMFYUI_PORT, help="ComfyUI port")
    args = parser.parse_args()

    bridge.host = args.host
    bridge.port = args.port
    bridge.base_url = f"http://{args.host}:{args.port}"

    logger.info("ComfyUI MCP Server starting (Bridge: %s)...", bridge.base_url)
    run_fastmcp_stdio(server, "comfyui")


if __name__ == "__main__":
    main()

