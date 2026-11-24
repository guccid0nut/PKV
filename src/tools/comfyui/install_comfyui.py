#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Cross-Platform ComfyUI Automated Installer & MCP Environment Configurator.
Automatically clones ComfyUI, configures dedicated CUDA/PyTorch virtual environment,
installs ComfyUI-Manager and prepares Model Context Protocol (MCP) server endpoints.
"""

from __future__ import annotations

import argparse
import logging
import os
import platform
import shutil
import subprocess
import sys
import urllib.request
from typing import Optional, Tuple

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [%(levelname)s] [ComfyUI-Installer] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger("install_comfyui")

COMFYUI_REPO_URL = "https://github.com/comfyanonymous/ComfyUI.git"
COMFYUI_MANAGER_URL = "https://github.com/ltdrdata/ComfyUI-Manager.git"

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMFYUI_DIR = os.path.join(SCRIPT_DIR, "ComfyUI")
VENV_DIR = os.path.join(SCRIPT_DIR, ".venv")


def get_venv_python() -> str:
    """Return path to python binary inside venv."""
    if platform.system() == "Windows":
        return os.path.join(VENV_DIR, "Scripts", "python.exe")
    return os.path.join(VENV_DIR, "bin", "python")


def get_venv_pip() -> str:
    """Return path to pip binary inside venv."""
    if platform.system() == "Windows":
        return os.path.join(VENV_DIR, "Scripts", "pip.exe")
    return os.path.join(VENV_DIR, "bin", "pip")


def check_git() -> bool:
    """Verify git is installed and available."""
    try:
        res = subprocess.run(["git", "--version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        return res.returncode == 0
    except Exception:
        return False


def clone_or_update_repo(repo_url: str, target_dir: str) -> bool:
    """Clone git repository if missing, or fetch/pull if present."""
    if not os.path.exists(target_dir):
        logger.info("Cloning %s into %s...", repo_url, target_dir)
        res = subprocess.run(["git", "clone", "--depth", "1", repo_url, target_dir])
        return res.returncode == 0
    else:
        logger.info("Repository %s already exists. Checking for updates...", target_dir)
        try:
            subprocess.run(["git", "pull"], cwd=target_dir, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        except Exception as ex:
            logger.warning("Could not pull latest changes: %s", ex)
        return True


def create_virtual_environment() -> bool:
    """Create isolated virtual environment for ComfyUI."""
    if os.path.exists(get_venv_python()):
        logger.info("Virtual environment already exists at %s", VENV_DIR)
        return True

    logger.info("Creating Python virtual environment at %s...", VENV_DIR)
    res = subprocess.run([sys.executable, "-m", "venv", VENV_DIR])
    if res.returncode != 0:
        logger.error("Failed to create virtual environment.")
        return False
    logger.info("Virtual environment created successfully.")
    return True


def detect_cuda_support() -> bool:
    """Detect if Nvidia GPU with CUDA is available."""
    try:
        res = subprocess.run(["nvidia-smi"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if res.returncode == 0 and "NVIDIA" in res.stdout:
            logger.info("NVIDIA GPU detected via nvidia-smi.")
            return True
    except Exception:
        pass
    return False


def install_dependencies(force_cuda: bool = False) -> bool:
    """Install PyTorch with hardware acceleration (CUDA/MPS/CPU) + ComfyUI and MCP dependencies."""
    pip_bin = get_venv_pip()
    py_bin = get_venv_python()

    logger.info("Upgrading pip and packaging tools...")
    subprocess.run([py_bin, "-m", "pip", "install", "--upgrade", "pip", "setuptools", "wheel"])

    system = platform.system()
    has_cuda = force_cuda or detect_cuda_support()

    if system == "Darwin":
        # macOS Apple Silicon with Metal Performance Shaders (MPS)
        logger.info("macOS detected: Installing PyTorch with native Metal Performance Shaders (MPS) support...")
        subprocess.run([pip_bin, "install", "torch", "torchvision", "torchaudio"])
    elif has_cuda:
        logger.info("CUDA GPU detected: Installing PyTorch with CUDA 12.4 support (Optimized for RTX / modern GPUs)...")
        res = subprocess.run([
            pip_bin, "install", "torch", "torchvision", "torchaudio",
            "--index-url", "https://download.pytorch.org/whl/cu124"
        ])
        if res.returncode != 0:
            logger.warning("CUDA 12.4 index installation had an issue, falling back to standard PyTorch...")
            subprocess.run([pip_bin, "install", "torch", "torchvision", "torchaudio"])
    else:
        logger.info("No dedicated Nvidia CUDA GPU detected: Installing standard PyTorch (CPU / ROCm)...")
        subprocess.run([pip_bin, "install", "torch", "torchvision", "torchaudio"])

    # Install ComfyUI requirements
    req_file = os.path.join(COMFYUI_DIR, "requirements.txt")
    if os.path.exists(req_file):
        logger.info("Installing ComfyUI requirements from %s...", req_file)
        subprocess.run([pip_bin, "install", "-r", req_file])

    # Install MCP & comic generation dependencies
    extra_packages = [
        "mcp", "aiohttp", "pillow", "safetensors", "transformers", "accelerate", "requests", "websocket-client"
    ]
    logger.info("Installing Model Context Protocol (MCP) and generation dependencies: %s", extra_packages)
    subprocess.run([pip_bin, "install"] + extra_packages)

    return True


def install_custom_nodes() -> None:
    """Install ComfyUI-Manager and essential comic workflow nodes."""
    custom_nodes_dir = os.path.join(COMFYUI_DIR, "custom_nodes")
    os.makedirs(custom_nodes_dir, exist_ok=True)

    manager_dir = os.path.join(custom_nodes_dir, "ComfyUI-Manager")
    clone_or_update_repo(COMFYUI_MANAGER_URL, manager_dir)


def uninstall_comfyui() -> bool:
    """Cleanly stop and remove ComfyUI application and isolated virtual environment."""
    logger.info("Uninstalling ComfyUI and removing dedicated virtual environment...")

    # Stop active servers first if launch_comfyui is available
    try:
        from launch_comfyui import stop_comfyui
        stop_comfyui()
    except Exception as ex:
        logger.debug("Could not run stop_comfyui prior to removal: %s", ex)

    removed_any = False

    # 1. Remove ComfyUI repo directory
    if os.path.exists(COMFYUI_DIR):
        logger.info("Removing ComfyUI repository folder: %s", COMFYUI_DIR)
        try:
            shutil.rmtree(COMFYUI_DIR)
            removed_any = True
        except Exception as ex:
            logger.warning("Could not delete %s: %s", COMFYUI_DIR, ex)

    # 2. Remove virtual environment
    if os.path.exists(VENV_DIR):
        logger.info("Removing Python virtual environment: %s", VENV_DIR)
        try:
            shutil.rmtree(VENV_DIR)
            removed_any = True
        except Exception as ex:
            logger.warning("Could not delete %s: %s", VENV_DIR, ex)

    if removed_any:
        logger.info("SUCCESS: ComfyUI and MCP environment uninstalled cleanly.")
    else:
        logger.info("No existing ComfyUI installation found at %s.", COMFYUI_DIR)
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Install or Uninstall ComfyUI & MCP server for Project Kongor")
    parser.add_argument("action", nargs="?", default="install", choices=["install", "uninstall", "remove"],
                        help="Action to perform (install [default], uninstall)")
    parser.add_argument("--uninstall", action="store_true", help="Uninstall ComfyUI and remove virtual environment")
    parser.add_argument("--skip-venv", action="store_true", help="Use current Python environment instead of creating .venv")
    parser.add_argument("--force", action="store_true", help="Force reinstall dependencies")
    parser.add_argument("--cuda", action="store_true", help="Force CUDA installation")
    args = parser.parse_args()

    if args.uninstall or args.action in ("uninstall", "remove"):
        success = uninstall_comfyui()
        return 0 if success else 1

    logger.info("=== Starting Project Kongor ComfyUI & MCP Setup ===")

    if not check_git():
        logger.error("Git is not found on PATH. Please install Git.")
        return 1

    # 1. Clone ComfyUI
    if not clone_or_update_repo(COMFYUI_REPO_URL, COMFYUI_DIR):
        logger.error("Failed to clone ComfyUI repository.")
        return 1

    # 2. Setup venv
    if not args.skip_venv:
        if not create_virtual_environment():
            return 1

    # 3. Install dependencies
    if not install_dependencies(force_cuda=args.cuda):
        logger.error("Failed to install dependencies.")
        return 1

    # 4. Custom nodes
    install_custom_nodes()

    logger.info("=== ComfyUI Installation & MCP Configuration Complete! ===")
    logger.info("ComfyUI Path: %s", COMFYUI_DIR)
    logger.info("Python Environment: %s", get_venv_python())
    logger.info("To launch ComfyUI: 'python src/tools/comfyui/launch_comfyui.py' or 'kongor comfy'")
    logger.info("To launch MCP Server: 'python src/tools/comfyui/mcp_server.py'")
    return 0


if __name__ == "__main__":
    sys.exit(main())
