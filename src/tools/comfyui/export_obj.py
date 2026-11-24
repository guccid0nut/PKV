# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

import bpy, os

bpy.ops.wm.read_factory_settings(use_empty=True)
glb_path = os.path.abspath(r"src\tools\comfyui\ComfyUI\input\3d\ophelia.glb")
obj_path = os.path.abspath(r"src\tools\comfyui\ComfyUI\input\3d\ophelia.obj")

bpy.ops.import_scene.gltf(filepath=glb_path)
bpy.ops.wm.obj_export(filepath=obj_path)
print(f"[SUCCESS] Exported Ophelia OBJ -> {obj_path}")
