<div align="center">

# 🔨 HoN Model Tools (`hon-model-tools`)
### *Standalone Batch 3D Asset Converter & glTF/GLB Compiler*

[![Python 3.11+](https://img.shields.io/badge/Python-3.11%2B-3776AB?style=for-the-badge&logo=python&logoColor=white)](https://www.python.org/)
[![glTF 2.0](https://img.shields.io/badge/glTF-2.0%20GLB-green?style=for-the-badge)](#)
[![Headless CLI](https://img.shields.io/badge/CLI-Batch%20Processing-blue?style=for-the-badge)](#)

**Standalone command-line interface for batch converting Heroes of Newerth 3D assets (`.model`, `.k2model`, `.mdf`, `.clip`, `.material`, `.dds`) to standard glTF / GLB 2.0 with skinned armatures, animations, and PBR materials.**

[⚡ Quickstart](#-quick-start) •
[📋 Arguments](#-command-line-arguments)

---

### ⚡ Quickstart: Export Hero Model to GLB
Export any hero model with all skeletal animations baked in **Windows PowerShell**:

```powershell
kongor model export "thriller rhapsody"
```

---

</div>

## 🚀 Quick Start

```powershell
# List all discoverable heroes, buildings, units & models available for export
kongor model list

# Search discoverable assets by keyword (e.g. empath, devourer, rhapsody)
kongor model list rhapsody

# Export specific hero skin with animations baked
kongor model export "thriller rhapsody"

# Export all Empath models, skeletons, and animation clips directly to output/
kongor model export empath

# Install portable Blender 4.2+ LTS and configure the io_scene_hon addon cross-platform
kongor blender install

# Open exported model in Blender
kongor blender output/models/empath.glb

# Convert an MDF with animations baked via standalone Python script
python tools/hon-model-tools/hon_model_converter.py -i games/shared/buildings/conq01/attack_tower/tower_1/model.mdf -o output/tower_1.glb --bake-animations --fps 30
```

---

## 📋 Command Line Arguments

| Argument | Short | Default | Description |
|---|---|---|---|
| `--input` | `-i` | *(required)* | Path to input `.model`, `.mdf`, `.clip`, or directory. |
| `--output` | `-o` | `""` | Path to destination `.glb`, `.gltf`, or output directory. |
| `--format` | `-f` | `"glb"` | Output format: `"glb"` (single binary) or `"gltf"` (JSON + `.bin`). |
| `--bake-animations` | | `True` | Bake companion animation clips into the exported glTF file. |
| `--no-bake-animations` | | | Disable animation baking. |
| `--fps` | | `30` | Target animation framerate for keyframe sampling. |
| `--export-textures` | | `False` | Extract unpacked textures as PNG files. |
| `--verbose` | `-v` | `False` | Enable debug-level structured logging. |
| `--log-file` | | `""` | File path for structured log output. |
