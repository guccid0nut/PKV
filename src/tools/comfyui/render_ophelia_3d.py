# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

import bpy
import os
import math

bpy.ops.wm.read_factory_settings(use_empty=True)

glb_path = os.path.abspath(r"src\tools\comfyui\ComfyUI\input\3d\ophelia.glb")
out_img = os.path.abspath(r"src\tools\comfyui\ComfyUI\input\ophelia_3d_render.png")
out_front = os.path.abspath(r"src\tools\comfyui\ComfyUI\input\ophelia_3d_front.png")
out_profile = os.path.abspath(r"src\tools\comfyui\ComfyUI\input\ophelia_3d_profile.png")

print(f"Loading 3D model: {glb_path}")
bpy.ops.import_scene.gltf(filepath=glb_path)

objs = [o for o in bpy.context.scene.objects if o.type == 'MESH']
print(f"Loaded {len(objs)} mesh objects.")

cam_data = bpy.data.cameras.new("RenderCam")
cam_data.lens = 50
cam_obj = bpy.data.objects.new("RenderCam", cam_data)
bpy.context.scene.collection.objects.link(cam_obj)
bpy.context.scene.camera = cam_obj

# Workbench or Cycles
bpy.context.scene.render.engine = 'CYCLES'
bpy.context.scene.cycles.samples = 32
bpy.context.scene.cycles.device = 'CPU'

world = bpy.data.worlds.new("StudioWorld")
bpy.context.scene.world = world
world.use_nodes = True
bg = world.node_tree.nodes.get("Background")
if bg:
    bg.inputs[0].default_value = (0.05, 0.07, 0.1, 1.0)

def add_light(name, pos, energy, color=(1,1,1)):
    light_data = bpy.data.lights.new(name=name, type='POINT')
    light_data.energy = energy
    light_data.color = color
    light_obj = bpy.data.objects.new(name=name, object_data=light_data)
    light_obj.location = pos
    bpy.context.scene.collection.objects.link(light_obj)

add_light("KeyLight", (4, -5, 8), 1500, (1.0, 0.95, 0.85))
add_light("FillLight", (-5, -4, 5), 800, (0.7, 0.85, 1.0))
add_light("RimLight", (0, 6, 8), 2000, (0.4, 0.9, 0.6))

bpy.context.scene.render.resolution_x = 1024
bpy.context.scene.render.resolution_y = 1024

cam_obj.location = (2.0, -3.8, 2.2)
cam_obj.rotation_euler = (math.radians(70), 0, math.radians(25))
bpy.context.scene.render.filepath = out_img
bpy.ops.render.render(write_still=True)
print(f"[SUCCESS] Rendered 3/4 Perspective -> {out_img}")

cam_obj.location = (0, -4.0, 2.0)
cam_obj.rotation_euler = (math.radians(80), 0, 0)
bpy.context.scene.render.filepath = out_front
bpy.ops.render.render(write_still=True)
print(f"[SUCCESS] Rendered Front View -> {out_front}")

cam_obj.location = (4.0, 0, 2.0)
cam_obj.rotation_euler = (math.radians(80), 0, math.radians(90))
bpy.context.scene.render.filepath = out_profile
bpy.ops.render.render(write_still=True)
print(f"[SUCCESS] Rendered Profile View -> {out_profile}")
