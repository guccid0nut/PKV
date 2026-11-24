#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Model Downloader for Project Kongor ComfyUI.
Quickly downloads popular Comic, SDXL, and ControlNet model checkpoints directly into ComfyUI.
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import urllib.request

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [ModelDownloader] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger("download_models")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMFYUI_DIR = os.path.join(SCRIPT_DIR, "ComfyUI")
CHECKPOINTS_DIR = os.path.join(COMFYUI_DIR, "models", "checkpoints")
LORAS_DIR = os.path.join(COMFYUI_DIR, "models", "loras")
CONTROLNET_DIR = os.path.join(COMFYUI_DIR, "models", "controlnet")
VAE_DIR = os.path.join(COMFYUI_DIR, "models", "vae")

MODEL_REGISTRY = {
    "sdxl_turbo": {
        "dir": CHECKPOINTS_DIR,
        "filename": "sd_xl_turbo_1.0_fp16.safetensors",
        "url": "https://huggingface.co/stabilityai/sdxl-turbo/resolve/main/sd_xl_turbo_1.0_fp16.safetensors",
        "desc": "Ultra-fast SDXL Turbo (1-4 step 1024x1024 generations)"
    },
    "dreamshaper_xl": {
        "dir": CHECKPOINTS_DIR,
        "filename": "dreamshaperXL_v21TurboDPMSDE.safetensors",
        "url": "https://huggingface.co/Lykon/dreamshaper-xl-v2-turbo/resolve/main/DreamShaperXL_Turbo_v2.1.safetensors",
        "desc": "High-fidelity artistic and fantasy graphic novel checkpoint"
    },
    "controlnet_depth": {
        "dir": CONTROLNET_DIR,
        "filename": "controlnet_depth_sdxl.safetensors",
        "url": "https://huggingface.co/diffusers/controlnet-depth-sdxl-1.0/resolve/main/diffusion_pytorch_model.safetensors",
        "desc": "ControlNet Depth model for 3D Blender mesh pose guidance"
    }
}


def download_with_progress(url: str, dest_path: str) -> bool:
    os.makedirs(os.path.dirname(dest_path), exist_ok=True)
    logger.info("Downloading %s to %s...", url, dest_path)
    try:
        def _reporthook(block_num: int, block_size: int, total_size: int) -> None:
            downloaded = block_num * block_size
            if total_size > 0:
                pct = min(100.0, downloaded * 100.0 / total_size)
                mb_dl = downloaded / (1024 * 1024)
                mb_tot = total_size / (1024 * 1024)
                if block_num % 1000 == 0:
                    sys.stdout.write(f"\r  Progress: {pct:.1f}% ({mb_dl:.1f} MB / {mb_tot:.1f} MB)")
                    sys.stdout.flush()

        urllib.request.urlretrieve(url, dest_path, reporthook=_reporthook)
        print()
        logger.info("Download completed: %s", dest_path)
        return True
    except Exception as ex:
        logger.error("Failed to download %s: %s", url, ex)
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description="Download ComfyUI checkpoints & ControlNet models")
    parser.add_argument("--model", type=str, choices=list(MODEL_REGISTRY.keys()) + ["all"], default="sdxl_turbo")
    args = parser.parse_args()

    targets = list(MODEL_REGISTRY.keys()) if args.model == "all" else [args.model]
    for target in targets:
        info = MODEL_REGISTRY[target]
        dest = os.path.join(info["dir"], info["filename"])
        if os.path.exists(dest):
            logger.info("Model '%s' already exists at %s", target, dest)
            continue
        logger.info("Installing model '%s': %s", target, info["desc"])
        download_with_progress(info["url"], dest)
    return 0


if __name__ == "__main__":
    sys.exit(main())
