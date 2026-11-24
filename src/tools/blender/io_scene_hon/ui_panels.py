# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Blender 4.2+ N-Panel UI & Operators for Heroes of Newerth Pipeline.
Provides comprehensive View3D sidebar panels for importing/exporting HoN models, skeletons,
playing clips, managing Principled BSDF v2 materials, armature viewport styling, and glTF 2.0 conversion.

Acknowledgments:
    Viewport header bone/X-ray/sizing quick-toggle workflow informed by
    KRAT0Sz / why2k (https://github.com/KRAT0Sz/KONGOR-TOOL) and RedBear.
"""

from __future__ import annotations

import logging
import os
from typing import List, Optional

logger = logging.getLogger("io_scene_hon.ui_panels")

try:
    import bpy
    from bpy.props import BoolProperty, EnumProperty, FloatProperty, FloatVectorProperty, IntProperty, PointerProperty, StringProperty
    from bpy.types import Operator, Panel, PropertyGroup
    from bpy_extras.io_utils import ExportHelper, ImportHelper
    BPY_AVAILABLE = True
except ImportError:
    bpy = None
    BPY_AVAILABLE = False


if BPY_AVAILABLE:

    # ============================================================================
    # Property Groups
    # ============================================================================

    class K2ImportSettings(PropertyGroup):
        flip_uv: BoolProperty(
            name="Flip UV",
            description="Flip vertical UV coordinates for DirectX/OpenGL compatibility",
            default=True,
        )
        tex_color: BoolProperty(
            name="Color",
            description="Import color / diffuse / albedo texture",
            default=True,
        )
        tex_color2: BoolProperty(
            name="Color 2",
            description="Import secondary color texture (team color / tint)",
            default=False,
        )
        tex_color3: BoolProperty(
            name="Color 3",
            description="Import tertiary color texture (detail / specular)",
            default=False,
        )
        tex_normal: BoolProperty(
            name="Normal",
            description="Import normal map texture (supporting RxGB decoding)",
            default=True,
        )
        tex_mrao: BoolProperty(
            name="MRAO",
            description="Import MRAO (Metallic/Roughness/AO) texture",
            default=True,
        )
        tex_emissive: BoolProperty(
            name="Emissive",
            description="Import emissive / glow texture",
            default=True,
        )

    class K2ExportSettings(PropertyGroup):
        export_mode: EnumProperty(
            name="Export Mode",
            items=[
                ("STATIC", "Static Mesh", "Export as static mesh without skeleton/animation"),
                ("ANIMATED", "Animated Mesh", "Export with skeleton and animation skinning"),
            ],
            default="STATIC",
        )
        force_static: BoolProperty(
            name="Force Static",
            description="Export without skeleton / animation skinning",
            default=False,
        )
        remove_hierarchy: BoolProperty(
            name="Remove Hierarchy",
            description="Flatten bone hierarchy on export (all bones parented to root)",
            default=False,
        )
        copy_textures: BoolProperty(
            name="Copy Textures",
            description="Copy texture files alongside exported model into textures/ subfolder",
            default=True,
        )
        export_geometry: BoolProperty(
            name="Geometry",
            description="Export mesh geometry data",
            default=True,
        )
        export_model_def: BoolProperty(
            name="Model Definition (.mdf)",
            description="Export companion .mdf XML model definition",
            default=True,
        )
        export_materials: BoolProperty(
            name="Materials (.material)",
            description="Export companion .material XML shader definition",
            default=True,
        )
        export_animation: BoolProperty(
            name="Animation (.clip)",
            description="Bake and export animation Action keyframes",
            default=False,
        )
        apply_modifiers: BoolProperty(
            name="Apply Modifiers",
            description="Evaluate depsgraph modifiers before exporting",
            default=True,
        )
        frame_start: IntProperty(
            name="Start Frame",
            description="Starting animation frame for export",
            default=0,
        )
        frame_end: IntProperty(
            name="End Frame",
            description="Ending animation frame for export",
            default=250,
        )

    class K2MeshSettings(PropertyGroup):
        mesh_type: EnumProperty(
            name="Mesh Type",
            items=[
                ("NORMAL", "Normal Mesh", "Standard renderable mesh"),
                ("COLLISION", "Collision Surface", "Physics collision surface hull (surf)"),
                ("REFBONE", "Reference Bone", "Reference bone attachment marker point"),
                ("SPRITE", "Sprite", "Billboard sprite mesh (mode 3)"),
                ("GROUND", "Ground Plane", "Ground plane decal mesh (mode 4)"),
            ],
            default="NORMAL",
        )
        exclude_normals: BoolProperty(
            name="Exclude Normals",
            description="Do not export vertex normals for this mesh",
            default=False,
        )
        roof: BoolProperty(
            name="Roof Surface",
            description="Mark mesh as a roof surface for visibility occlusion",
            default=False,
        )

    class K2WeaponPairSettings(PropertyGroup):
        weapon_object: PointerProperty(
            name="Weapon Mesh",
            type=bpy.types.Object,
            description="Weapon mesh object to snap and pair with character hand"
        )
        armature_object: PointerProperty(
            name="Hero Armature",
            type=bpy.types.Object,
            description="Character hero armature containing the hand socket bone"
        )
        grip_source: EnumProperty(
            name="Grip Source",
            items=[
                ("CURSOR", "3D Cursor", "Use Blender 3D cursor position as weapon grip point"),
                ("VERTEX", "Selected Vertex", "Use active or selected vertex/vertices on weapon handle"),
                ("ORIGIN", "Object Origin", "Use current weapon object origin/pivot as grip point"),
                ("LOCATOR", "Grip Locator", "Use designated Empty object/locator (grip_locator)"),
                ("CUSTOM", "Custom XYZ", "Specify manual XYZ world coordinates"),
            ],
            default="CURSOR",
            description="Method to define the weapon grip/pivot point"
        )
        custom_grip_pos: FloatVectorProperty(
            name="Custom XYZ",
            description="Explicit world coordinate for grip point",
            default=(0.0, 0.0, 0.0)
        )
        locator_name: StringProperty(
            name="Locator Name",
            description="Name of Empty / locator object defining grip",
            default="grip_locator"
        )
        socket_bone: EnumProperty(
            name="Hand Socket",
            items=[
                ("_bone_weapon", "_bone_weapon (Dominant Hand)", "Standard HoN primary weapon socket bone"),
                ("_bone_weaponattach", "_bone_weaponattach", "Accessory & grip anchor bone"),
                ("_bone_weapon1", "_bone_weapon1", "Dynamic chain / secondary weapon bone 1"),
                ("Bip01 R Hand", "Bip01 R Hand (Right Wrist)", "Right hand wrist joint"),
                ("Bip01 L Hand", "Bip01 L Hand (Left Wrist)", "Left hand wrist joint"),
                ("CUSTOM", "Custom Bone Name...", "Specify custom bone name manually"),
            ],
            default="_bone_weapon",
            description="Target bone on hero armature to lock weapon onto"
        )
        custom_socket_bone: StringProperty(
            name="Custom Bone",
            description="Custom target bone name",
            default="_bone_weapon"
        )
        align_rotation: BoolProperty(
            name="Align Rotation",
            description="Align weapon rotation to match the hand socket bone orientation",
            default=True
        )
        set_origin_to_grip: BoolProperty(
            name="Set Origin to Grip",
            description="Move weapon object origin to grip point so future rotations pivot around the handle",
            default=True
        )
        parent_mode: EnumProperty(
            name="Parenting",
            items=[
                ("BONE", "Parent to Bone", "Standard bone parenting (weapon moves with hand animation)"),
                ("CHILD_OF", "Child Of Constraint", "Non-destructive Child Of constraint"),
                ("VERTEX_GROUP", "Skin Weight 1.0", "Bind via vertex group and Armature modifier"),
                ("DUAL_VERTEX_GROUP", "Dual Grip Gradient", "Blend skin weights across dual sockets (flexible chains)"),
                ("NONE", "Snap Only", "Snap transform without creating parent relationship"),
            ],
            default="BONE",
            description="Method to lock weapon to hand bone"
        )
        use_dual_grip: BoolProperty(
            name="Dual Grip (Two Hands)",
            description="Hold front and back of weapon/chain simultaneously with both hands",
            default=False
        )
        secondary_grip_source: EnumProperty(
            name="Secondary Grip Source",
            items=[
                ("CURSOR", "3D Cursor", "Use Blender 3D cursor position as secondary grip point"),
                ("VERTEX", "Selected Vertex", "Use active or selected vertex on rear chain/handle"),
                ("ORIGIN", "Object Origin", "Use weapon object origin"),
                ("LOCATOR", "Secondary Locator", "Use designated Empty object/locator (grip_locator_sec)"),
                ("CUSTOM", "Custom XYZ", "Specify manual XYZ world coordinates"),
            ],
            default="CURSOR",
            description="Method to define the secondary grip point (e.g. rear chain)"
        )
        secondary_custom_grip_pos: FloatVectorProperty(
            name="Secondary Custom XYZ",
            description="Explicit world coordinate for secondary grip point",
            default=(0.0, 0.0, 0.0)
        )
        secondary_locator_name: StringProperty(
            name="Secondary Locator",
            description="Name of Empty / locator object defining secondary grip",
            default="grip_locator_sec"
        )
        secondary_socket_bone: EnumProperty(
            name="Secondary Socket",
            items=[
                ("_bone_fx_l_hand", "_bone_fx_l_hand (Left Hand Chain)", "Left hand chain socket"),
                ("Bip01 L Hand", "Bip01 L Hand (Left Wrist)", "Left hand wrist joint"),
                ("_bone_weapon1", "_bone_weapon1 (Chain Link 1)", "Dynamic chain link 1"),
                ("CUSTOM", "Custom Bone Name...", "Specify custom bone name manually"),
            ],
            default="_bone_fx_l_hand",
            description="Target bone on hero armature for secondary grip (e.g. left hand)"
        )
        custom_secondary_socket: StringProperty(
            name="Custom Secondary Bone",
            description="Custom target bone name for secondary grip",
            default="_bone_fx_l_hand"
        )
        align_to_hand_vector: BoolProperty(
            name="Align Along Hand Vector",
            description="Orient weapon along vector connecting both hand sockets",
            default=True
        )

    def _get_default_dialog_path(ext: str, default_filename: str = "") -> str:
        """Resolve smart default directories for HoN asset dialogs."""
        repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
        gwyn_dir = os.path.join(repo_root, "games", "client", "override", "heroes", "gwynplaine")
        clips_dir = os.path.join(gwyn_dir, "clips")
        output_dir = os.path.join(repo_root, "output")

        if ext == ".clip":
            target_dir = clips_dir if os.path.isdir(clips_dir) else (gwyn_dir if os.path.isdir(gwyn_dir) else repo_root)
        elif ext in (".model", ".mdf", ".material", "textures"):
            target_dir = gwyn_dir if os.path.isdir(gwyn_dir) else repo_root
        elif ext in (".glb", ".gltf"):
            target_dir = output_dir if os.path.isdir(output_dir) else repo_root
        else:
            target_dir = gwyn_dir if os.path.isdir(gwyn_dir) else repo_root

        if default_filename:
            return os.path.join(target_dir, default_filename)
        return os.path.join(target_dir, "")

    # ============================================================================
    # Import Operators
    # ============================================================================

    class HON_OT_import_model(Operator, ImportHelper):
        """Import a Heroes of Newerth Model (.model / .k2model) into Blender"""
        bl_idname = "hon.import_model"
        bl_label = "Import HoN Model"
        bl_options = {"REGISTER", "UNDO"}

        filter_glob: StringProperty(
            default="*.model;*.k2model",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            self.filepath = _get_default_dialog_path(".model")
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        import_skeleton: BoolProperty(
            name="Import Skeleton",
            description="Create armature and bone hierarchy",
            default=True
        )

        import_skinning: BoolProperty(
            name="Import Skinning",
            description="Bind vertices to armature with vertex groups and weights",
            default=True
        )

        def execute(self, context):
            from .scene_importer import import_hon_model
            settings = getattr(context.scene, "k2_import_settings", None)
            flip_uv = getattr(settings, "flip_uv", True) if settings else True
            tex_flags = {
                "color": getattr(settings, "tex_color", True) if settings else True,
                "color2": getattr(settings, "tex_color2", False) if settings else False,
                "color3": getattr(settings, "tex_color3", False) if settings else False,
                "normal": getattr(settings, "tex_normal", True) if settings else True,
                "mrao": getattr(settings, "tex_mrao", True) if settings else True,
                "emissive": getattr(settings, "tex_emissive", True) if settings else True,
            }
            try:
                mesh_obj, rig = import_hon_model(
                    self.filepath,
                    flip_uv=flip_uv,
                    tex_flags=tex_flags,
                    import_skeleton=self.import_skeleton,
                    import_skinning=self.import_skinning
                )
                self.report({"INFO"}, f"Successfully imported HoN model: {os.path.basename(self.filepath)}")
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to import HoN model: %s", e)
                self.report({"ERROR"}, f"Failed to import HoN model: {e}")
                return {"CANCELLED"}

    class HON_OT_import_clip(Operator, ImportHelper):
        """Import a Heroes of Newerth Animation Clip (.clip) onto active armature"""
        bl_idname = "hon.import_clip"
        bl_label = "Import HoN Clip"
        bl_options = {"REGISTER", "UNDO"}

        filter_glob: StringProperty(
            default="*.clip",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            self.filepath = _get_default_dialog_path(".clip")
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        def execute(self, context):
            from .scene_importer import import_hon_clip
            try:
                action = import_hon_clip(self.filepath)
                if action:
                    self.report({"INFO"}, f"Successfully imported HoN clip: {action.name}")
                    return {"FINISHED"}
                else:
                    self.report({"WARNING"}, "No active armature found to apply clip animation")
                    return {"CANCELLED"}
            except Exception as e:
                logger.exception("Failed to import HoN clip: %s", e)
                self.report({"ERROR"}, f"Failed to import HoN clip: {e}")
                return {"CANCELLED"}

    class HON_OT_import_mdf(Operator, ImportHelper):
        """Import a Heroes of Newerth Entity Definition (.mdf) and Companion Clips"""
        bl_idname = "hon.import_mdf"
        bl_label = "Import HoN MDF Entity"
        bl_options = {"REGISTER", "UNDO"}

        filter_glob: StringProperty(
            default="*.mdf",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            self.filepath = _get_default_dialog_path(".mdf")
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        def execute(self, context):
            from .mdf_parser import resolve_mdf_hierarchy
            from .scene_importer import import_hon_model
            try:
                info = resolve_mdf_hierarchy(self.filepath)
                if info["model_path"]:
                    import_hon_model(info["model_path"])
                self.report({"INFO"}, f"Resolved MDF entity '{info['mdf'].name}': {len(info['clip_paths'])} clips found")
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to import MDF: %s", e)
                self.report({"ERROR"}, f"Failed to import MDF: {e}")
                return {"CANCELLED"}

    class HON_OT_import_textures(Operator, ImportHelper):
        """Batch scan and assign textures from a selected directory to mesh materials"""
        bl_idname = "k2.import_textures"
        bl_label = "Import Textures"

        filter_glob: StringProperty(
            default="*.tga;*.png;*.dds;*.jpg;*.jpeg;*.bmp;*.tif",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            self.filepath = _get_default_dialog_path("textures")
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        def execute(self, context):
            from .texture_manager import assign_textures_to_objects
            tex_dir = os.path.dirname(os.path.abspath(self.filepath))
            settings = getattr(context.scene, "k2_import_settings", None)
            tex_flags = {
                "color": getattr(settings, "tex_color", True) if settings else True,
                "color2": getattr(settings, "tex_color2", False) if settings else False,
                "color3": getattr(settings, "tex_color3", False) if settings else False,
                "normal": getattr(settings, "tex_normal", True) if settings else True,
                "mrao": getattr(settings, "tex_mrao", True) if settings else True,
                "emissive": getattr(settings, "tex_emissive", True) if settings else True,
            }
            objects = [o for o in context.selected_objects if o.type == "MESH"]
            if not objects:
                objects = [o for o in context.scene.objects if o.type == "MESH"]
            count = assign_textures_to_objects(objects, tex_dir, tex_flags)
            if count:
                self.report({"INFO"}, f"Loaded textures for {count} material(s) from {tex_dir}")
            else:
                self.report({"WARNING"}, f"No matching textures found in {tex_dir}")
            return {"FINISHED"}

    # ============================================================================
    # Export Operators
    # ============================================================================

    class HON_OT_export_model(Operator, ExportHelper):
        """Export active objects to Heroes of Newerth Model (.model)"""
        bl_idname = "hon.export_model"
        bl_label = "Export HoN Model"
        bl_options = {"REGISTER", "UNDO"}

        filename_ext = ".model"
        filter_glob: StringProperty(
            default="*.model",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            self.filepath = _get_default_dialog_path(".model", "high.model")
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        def execute(self, context):
            from .scene_exporter import export_hon_model, ExportOptions
            try:
                opts = ExportOptions.from_scene(context.scene)
                export_hon_model(self.filepath, opts)
                self.report({"INFO"}, f"Successfully exported HoN model to {self.filepath}")
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to export HoN model: %s", e)
                self.report({"ERROR"}, f"Failed to export HoN model: {e}")
                return {"CANCELLED"}

    class HON_OT_export_clip(Operator, ExportHelper):
        """Export active Action to Heroes of Newerth Clip (.clip)"""
        bl_idname = "hon.export_clip"
        bl_label = "Export HoN Clip"
        bl_options = {"REGISTER", "UNDO"}

        filename_ext = ".clip"
        filter_glob: StringProperty(
            default="*.clip",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            act = None
            for obj in context.selected_objects:
                if obj.type == "ARMATURE" and obj.animation_data:
                    act = obj.animation_data.action
                    break
            clip_name = f"{act.name}.clip" if act else "walk_1.clip"
            self.filepath = _get_default_dialog_path(".clip", clip_name)
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        def execute(self, context):
            from .scene_exporter import export_hon_clip, ExportOptions
            try:
                opts = ExportOptions.from_scene(context.scene)
                export_hon_clip(self.filepath, opts)
                self.report({"INFO"}, f"Successfully exported HoN clip to {self.filepath}")
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to export HoN clip: %s", e)
                self.report({"ERROR"}, f"Failed to export HoN clip: {e}")
                return {"CANCELLED"}

    class HON_OT_export_gltf(Operator, ExportHelper):
        """Export HoN Model & Animations to standard glTF / GLB 2.0"""
        bl_idname = "hon.export_gltf"
        bl_label = "Export to glTF / GLB"
        bl_options = {"REGISTER", "UNDO"}

        filename_ext = ".glb"
        filter_glob: StringProperty(
            default="*.glb;*.gltf",
            options={"HIDDEN"}
        )

        def invoke(self, context, event):
            self.filepath = _get_default_dialog_path(".glb", "gwynplaine.glb")
            context.window_manager.fileselect_add(self)
            return {"RUNNING_MODAL"}

        bake_animations: BoolProperty(
            name="Bake Animations",
            description="Include all loaded animation clips in the glTF export",
            default=True
        )

        fps: IntProperty(
            name="Framerate",
            description="Animation sampling framerate",
            default=30,
            min=1,
            max=120
        )

        def execute(self, context):
            from .gltf_exporter import export_hon_to_gltf
            from .smdl_parser import HonModelData
            try:
                model = HonModelData()
                export_hon_to_gltf(model, output_path=self.filepath, fps=self.fps, bake_animations=self.bake_animations)
                self.report({"INFO"}, f"Successfully exported glTF to {self.filepath}")
                return {"FINISHED"}
            except Exception as e:
                self.report({"ERROR"}, f"Failed to export glTF: {e}")
                return {"CANCELLED"}

    # ============================================================================
    # Armature & Viewport Tools
    # ============================================================================

    def _get_armature_objects() -> List[Any]:
        return [o for o in bpy.data.objects if o.type == "ARMATURE"]

    class HON_OT_toggle_bone_names(Operator):
        """Toggle bone name display for all armatures in the scene"""
        bl_idname = "k2.toggle_bone_names"
        bl_label = "Toggle Bone Names"

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}
            new_state = not arms[0].data.show_names
            for obj in arms:
                obj.data.show_names = new_state
            self.report({"INFO"}, f"Bone names: {'ON' if new_state else 'OFF'}")
            return {"FINISHED"}

    class HON_OT_toggle_bones(Operator):
        """Show or hide armature skeleton bones in the 3D Viewport"""
        bl_idname = "k2.toggle_bones"
        bl_label = "Toggle Bones"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}
            any_hidden = any(arm.hide_get() or arm.hide_viewport for arm in arms)
            if any_hidden:
                for arm in arms:
                    arm.hide_viewport = False
                    arm.hide_set(False)
                if getattr(context, "space_data", None) and getattr(context.space_data, "type", None) == "VIEW_3D":
                    if hasattr(context.space_data, "overlay") and hasattr(context.space_data.overlay, "show_bones"):
                        context.space_data.overlay.show_bones = True
                self.report({"INFO"}, f"Bones: Shown ({len(arms)} armature(s))")
            else:
                for arm in arms:
                    arm.hide_viewport = True
                    arm.hide_set(True)
                self.report({"INFO"}, f"Bones: Hidden ({len(arms)} armature(s))")
            return {"FINISHED"}

    class HON_OT_toggle_bone_xray(Operator):
        """Toggle In Front (X-Ray) display for armature skeletons"""
        bl_idname = "k2.toggle_bone_xray"
        bl_label = "Toggle Bones In Front"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}
            new_state = not all(arm.show_in_front for arm in arms)
            for arm in arms:
                arm.show_in_front = new_state
            self.report({"INFO"}, f"Bones In Front (X-Ray): {'ON' if new_state else 'OFF'}")
            return {"FINISHED"}

    class HON_OT_fit_bone_size(Operator):
        """Automatically resize armature bone lengths to match character joint proportions"""
        bl_idname = "k2.fit_bone_size"
        bl_label = "Auto-Fit Bone Sizes"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}

            orig_active = context.view_layer.objects.active
            orig_mode = orig_active.mode if orig_active else "OBJECT"
            resized_count = 0

            try:
                for arm_obj in arms:
                    if not arm_obj.data:
                        continue
                    dim = max(arm_obj.dimensions) if any(arm_obj.dimensions) else 0.0
                    if dim < 1.0:
                        child_meshes = [o for o in context.scene.objects if o.type == "MESH" and o.parent == arm_obj]
                        if child_meshes:
                            dim = max(max(m.dimensions) for m in child_meshes)
                    default_len = max(dim * 0.05, 2.0) if dim > 1.0 else 5.0

                    was_hidden = arm_obj.hide_get()
                    was_hide_vp = arm_obj.hide_viewport
                    arm_obj.hide_viewport = False
                    arm_obj.hide_set(False)

                    context.view_layer.objects.active = arm_obj
                    bpy.ops.object.mode_set(mode="EDIT")
                    for eb in arm_obj.data.edit_bones:
                        child_dists = [
                            (c.head - eb.head).length
                            for c in eb.children
                            if (c.head - eb.head).length > 0.05
                        ]
                        if child_dists:
                            target_len = min(child_dists)
                        elif eb.parent:
                            p_dist = (eb.head - eb.parent.head).length
                            target_len = max(p_dist * 0.7, default_len * 0.5) if p_dist > 0.05 else default_len
                        else:
                            target_len = default_len
                        eb.length = max(target_len, 0.5)
                        resized_count += 1
                    bpy.ops.object.mode_set(mode="OBJECT")

                    arm_obj.hide_set(was_hidden)
                    arm_obj.hide_viewport = was_hide_vp
            finally:
                if orig_active:
                    context.view_layer.objects.active = orig_active
                    if orig_mode != "OBJECT":
                        try:
                            bpy.ops.object.mode_set(mode=orig_mode)
                        except Exception:
                            pass

            self.report({"INFO"}, f"Auto-fitted {resized_count} bone(s) across {len(arms)} armature(s)")
            return {"FINISHED"}

    class HON_OT_fix_bone_display(Operator):
        """Set bone display to high-contrast visible colors (cyan/yellow/green) and STICK mode"""
        bl_idname = "k2.fix_bone_display"
        bl_label = "Fix Bone Colors"

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}
            count = 0
            for obj in arms:
                arm = obj.data
                arm.display_type = "STICK"
                for bone in arm.bones:
                    bone.color.palette = "CUSTOM"
                    bone.color.custom.normal = (0.0, 0.75, 1.0)
                    bone.color.custom.select = (1.0, 0.85, 0.0)
                    bone.color.custom.active = (0.2, 1.0, 0.4)
                    count += 1
            self.report({"INFO"}, f"Fixed {count} bones across {len(arms)} armature(s)")
            return {"FINISHED"}

    class HON_OT_reset_bone_display(Operator):
        """Reset bone display back to Blender defaults"""
        bl_idname = "k2.reset_bone_display"
        bl_label = "Reset Bone Colors"

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}
            for obj in arms:
                arm = obj.data
                arm.display_type = "OCTAHEDRAL"
                for bone in arm.bones:
                    bone.color.palette = "DEFAULT"
            self.report({"INFO"}, "Bone display reset to defaults")
            return {"FINISHED"}

    class HON_OT_cycle_bone_display(Operator):
        """Cycle armature bone display style"""
        bl_idname = "k2.cycle_bone_display"
        bl_label = "Cycle Display Type"

        _types = ["OCTAHEDRAL", "STICK", "BBONE", "ENVELOPE", "WIRE"]

        def execute(self, context):
            arms = _get_armature_objects()
            if not arms:
                self.report({"WARNING"}, "No armatures in scene")
                return {"CANCELLED"}
            cur = arms[0].data.display_type
            idx = (self._types.index(cur) + 1) % len(self._types) if cur in self._types else 0
            new_type = self._types[idx]
            for obj in arms:
                obj.data.display_type = new_type
            self.report({"INFO"}, f"Bone display: {new_type}")
            return {"FINISHED"}

    class HON_OT_scene_info(Operator):
        """Display HoN scene statistics (vertices, faces, meshes, bones, surfs, sprites)"""
        bl_idname = "k2.scene_info"
        bl_label = "Scene Info"

        def execute(self, context):
            total_verts = 0
            total_faces = 0
            num_meshes = 0
            num_surfs = 0
            num_sprites = 0
            num_bones = 0
            num_skinned = 0
            num_static = 0

            for obj in bpy.data.objects:
                if obj.type == "MESH":
                    mt = "NORMAL"
                    if hasattr(obj, "k2_mesh_settings"):
                        mt = obj.k2_mesh_settings.mesh_type
                    if mt == "COLLISION":
                        num_surfs += 1
                    elif mt in ("SPRITE", "GROUND"):
                        num_sprites += 1
                    else:
                        num_meshes += 1
                        total_verts += len(obj.data.vertices)
                        total_faces += len(obj.data.polygons)
                        has_armature = any(m.type == "ARMATURE" for m in obj.modifiers)
                        if has_armature:
                            num_skinned += 1
                        else:
                            num_static += 1
                elif obj.type == "ARMATURE":
                    num_bones += len(obj.data.bones)

            lines = [
                f"{total_verts} verts, {total_faces} faces",
                f"{num_meshes} meshes, {num_bones} bones",
                f"{num_surfs} surfs, {num_sprites} sprites",
                f"Skinned: {num_skinned}, Static: {num_static}"
            ]
            self.report({"INFO"}, " | ".join(lines))
            return {"FINISHED"}

    # ============================================================================
    # Sidebar UI Panels
    # ============================================================================

    class HON_PT_main_panel(Panel):
        """Main N-Panel UI for HoN Pipeline"""
        bl_label = "Heroes of Newerth Pipeline"
        bl_idname = "HON_PT_main_panel"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "HoN"

        def draw(self, context):
            layout = self.layout
            layout.label(text="HoN 3D Asset & Animation Pipeline", icon="SCENE_DATA")

            # Import Box
            box = layout.box()
            box.label(text="Import Assets", icon="IMPORT")
            col = box.column(align=True)
            col.operator("hon.import_model", text="Import Model (.model)", icon="MESH_DATA")
            col.operator("hon.import_clip", text="Import Clip (.clip)", icon="ANIM_DATA")
            col.operator("hon.import_mdf", text="Import MDF Entity (.mdf)", icon="OBJECT_DATAMODE")
            col.operator("k2.import_textures", text="Import Textures Folder", icon="TEXTURE")

            # Export Box
            box_exp = layout.box()
            box_exp.label(text="Export & Conversion", icon="EXPORT")
            col_exp = box_exp.column(align=True)
            col_exp.operator("hon.export_gltf", text="Export glTF / GLB 2.0", icon="FILE_3D")
            col_exp.operator("hon.export_model", text="Export HoN Model (.model)", icon="MESH_DATA")
            col_exp.operator("hon.export_clip", text="Export HoN Clip (.clip)", icon="ANIM_DATA")

            layout.separator()
            row = layout.row(align=True)
            row.operator("k2.scene_info", text="Scene Info", icon="INFO")

    class HON_PT_armature_panel(Panel):
        """Armature viewport display and styling tools"""
        bl_label = "Armature Display"
        bl_idname = "HON_PT_armature_panel"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "HoN"

        @classmethod
        def poll(cls, context):
            return any(o.type == "ARMATURE" for o in bpy.data.objects)

        def draw(self, context):
            layout = self.layout
            arms = _get_armature_objects()
            if not arms:
                return

            arm = arms[0].data
            show_names = arm.show_names
            bones_vis = any(not a.hide_get() and not a.hide_viewport for a in arms)
            xray_vis = any(a.show_in_front for a in arms)

            box = layout.box()
            box.label(text="Bone Visualization")
            row0 = box.row(align=True)
            row0.operator("k2.toggle_bones", text="Bones", icon="BONE_DATA", depress=bones_vis)
            row0.operator("k2.toggle_bone_names", text="Names", icon="HIDE_OFF" if show_names else "HIDE_ON", depress=show_names)
            row0.operator("k2.toggle_bone_xray", text="X-Ray", icon="XRAY", depress=xray_vis)

            row = box.row(align=True)
            row.operator("k2.fit_bone_size", text="Auto-Fit Sizes", icon="ARROW_LEFTRIGHT")
            row.operator("k2.fix_bone_display", text="Fix Colors", icon="COLORSET_09_VEC")

            row2 = box.row(align=True)
            row2.operator("k2.cycle_bone_display", text=f"Style: {arm.display_type}", icon="BONE_DATA")
            row2.operator("k2.reset_bone_display", text="Reset", icon="LOOP_BACK")

    def draw_viewport_header_armature_toggles(self, context):
        """Compact 3D Viewport header quick-toggles (auto-hidden when no Armature exists in scene)."""
        arms = _get_armature_objects()
        if not arms:
            return
        bones_vis = any(not a.hide_get() and not a.hide_viewport for a in arms)
        names_vis = any(a.data.show_names for a in arms if a.data)
        xray_vis = any(a.show_in_front for a in arms)

        row = self.layout.row(align=True)
        row.operator("k2.toggle_bones", text="", icon="BONE_DATA", depress=bones_vis)
        row.operator("k2.toggle_bone_names", text="", icon="SYNTAX_ON", depress=names_vis)
        row.operator("k2.toggle_bone_xray", text="", icon="XRAY", depress=xray_vis)


    # ============================================================================
    # Weapon Pairing Operators & Panel
    # ============================================================================

    class HON_OT_pair_weapon(Operator):
        """Define weapon grip point and lock directly to hero hand socket bone (_bone_weapon)"""
        bl_idname = "hon.pair_weapon"
        bl_label = "Pair Weapon to Hand Socket"
        bl_options = {"REGISTER", "UNDO"}

        grip_source: EnumProperty(
            name="Grip Point On Weapon",
            items=[
                ("CURSOR", "3D Cursor (Click Handle in 3D View)", "Use 3D Cursor position as weapon grip point"),
                ("VERTEX", "Selected Vertex (Mesh Handle)", "Use active or selected vertex on weapon handle"),
                ("ORIGIN", "Object Origin (Current Pivot)", "Use weapon object's current origin"),
                ("LOCATOR", "Grip Locator (Empty Object)", "Use designated Empty object or locator"),
                ("CUSTOM", "Custom Coordinates (X, Y, Z)", "Enter exact XYZ world coordinates"),
            ],
            default="CURSOR",
            description="Point on the weapon to lock directly to the hero's hand"
        )
        custom_grip_pos: FloatVectorProperty(
            name="Custom XYZ",
            default=(0.0, 0.0, 0.0)
        )
        locator_name: StringProperty(
            name="Locator Name",
            default="grip_locator"
        )
        socket_bone: EnumProperty(
            name="Target Hand Socket",
            items=[
                ("_bone_weapon", "_bone_weapon (Dominant Hand)", "Standard HoN weapon socket"),
                ("_bone_weaponattach", "_bone_weaponattach", "Grip & chain attachment socket"),
                ("_bone_weapon1", "_bone_weapon1", "Dynamic weapon segment 1"),
                ("Bip01 R Hand", "Bip01 R Hand (Right Wrist)", "Right hand wrist joint"),
                ("Bip01 L Hand", "Bip01 L Hand (Left Wrist)", "Left hand wrist joint"),
                ("CUSTOM", "Custom Bone Name...", "Specify custom bone"),
            ],
            default="_bone_weapon"
        )
        custom_socket_bone: StringProperty(
            name="Custom Bone",
            default="_bone_weapon"
        )
        align_rotation: BoolProperty(
            name="Align to Hand Orientation",
            description="Align weapon rotation with hand socket bone",
            default=True
        )
        set_origin_to_grip: BoolProperty(
            name="Set Weapon Origin to Grip",
            description="Re-center weapon origin to the grip point so rotations pivot around the handle",
            default=True
        )
        parent_mode: EnumProperty(
            name="Locking Mode",
            items=[
                ("BONE", "Parent to Bone (Standard)", "Direct bone parenting for animation"),
                ("CHILD_OF", "Child Of Constraint", "Non-destructive constraint"),
                ("VERTEX_GROUP", "Skin Weight 1.0", "Weight entire mesh to bone"),
                ("NONE", "Snap Only", "Snap transform without parenting"),
            ],
            default="BONE"
        )

        def invoke(self, context, event):
            settings = getattr(context.scene, "k2_weapon_settings", None)
            if settings:
                if not settings.weapon_object:
                    if context.active_object and context.active_object.type == "MESH":
                        settings.weapon_object = context.active_object
                    elif context.selected_objects:
                        meshes = [o for o in context.selected_objects if o.type == "MESH"]
                        if meshes:
                            settings.weapon_object = meshes[0]
                if not settings.armature_object:
                    if context.selected_objects:
                        arms = [o for o in context.selected_objects if o.type == "ARMATURE" and o != settings.weapon_object]
                        if arms:
                            settings.armature_object = arms[0]
                    if not settings.armature_object:
                        from .weapon_pairing import find_hero_armature
                        settings.armature_object = find_hero_armature(scene=context.scene, exclude_obj=settings.weapon_object, context=context)

                self.grip_source = settings.grip_source
                self.socket_bone = settings.socket_bone
                self.align_rotation = settings.align_rotation
                self.set_origin_to_grip = settings.set_origin_to_grip
                self.parent_mode = settings.parent_mode
                self.custom_socket_bone = settings.custom_socket_bone
                self.custom_grip_pos = settings.custom_grip_pos
                self.locator_name = settings.locator_name
            return context.window_manager.invoke_props_dialog(self, width=440)

        def draw(self, context):
            layout = self.layout
            layout.label(text="Define Weapon Grip to Lock to Hand:", icon="BONE_DATA")

            settings = getattr(context.scene, "k2_weapon_settings", None)
            if settings:
                box_targets = layout.box()
                box_targets.label(text="Pairing Targets:", icon="OBJECT_DATA")
                box_targets.prop(settings, "weapon_object", text="Weapon")
                box_targets.prop(settings, "armature_object", text="Armature")

            box_grip = layout.box()
            box_grip.label(text="1. Grip / Pivot Point on Weapon:", icon="PIVOT_CURSOR")
            box_grip.prop(self, "grip_source", text="")

            if self.grip_source == "CURSOR":
                c = context.scene.cursor.location
                col = box_grip.column(align=True)
                col.label(text=f"Current 3D Cursor: ({c.x:.2f}, {c.y:.2f}, {c.z:.2f})", icon="CURSOR")
                col.label(text="Place cursor on handle (Shift+Right Click)", icon="INFO")
            elif self.grip_source == "VERTEX":
                obj = (settings.weapon_object if settings and settings.weapon_object else context.active_object)
                if obj and obj.type == "MESH":
                    if getattr(obj, "mode", "OBJECT") == "EDIT":
                        try:
                            import bmesh
                            bm = bmesh.from_edit_mesh(obj.data)
                            sel_count = sum(1 for v in bm.verts if v.select)
                        except Exception:
                            sel_count = 0
                    else:
                        sel_count = sum(1 for v in obj.data.vertices if v.select) if hasattr(obj.data, "vertices") else 0
                    box_grip.label(text=f"Selected Vertices on '{obj.name}': {sel_count}", icon="VERTEXSEL")
                    box_grip.label(text="Uses active or average selected vertex position", icon="INFO")
                else:
                    box_grip.label(text="Select weapon mesh in Edit/Object mode", icon="ERROR")
            elif self.grip_source == "LOCATOR":
                box_grip.prop(self, "locator_name", text="Empty/Locator")
            elif self.grip_source == "CUSTOM":
                box_grip.prop(self, "custom_grip_pos", text="")
            elif self.grip_source == "ORIGIN":
                box_grip.label(text="Uses weapon object's current origin (pivot point)", icon="INFO")

            box_socket = layout.box()
            box_socket.label(text="2. Target Hand Socket Bone:", icon="CONSTRAINT_BONE")
            box_socket.prop(self, "socket_bone", text="")
            if self.socket_bone == "CUSTOM":
                box_socket.prop(self, "custom_socket_bone", text="Bone Name")

            box_opts = layout.box()
            box_opts.label(text="3. Snapping & Locking Options:", icon="TOOL_SETTINGS")
            box_opts.prop(self, "align_rotation")
            box_opts.prop(self, "set_origin_to_grip")
            box_opts.prop(self, "parent_mode", text="Mode")

        def execute(self, context):
            from .weapon_pairing import pair_weapon_to_socket, find_hero_armature

            settings = getattr(context.scene, "k2_weapon_settings", None)
            weapon_obj = None
            arm_obj = None

            if settings:
                weapon_obj = settings.weapon_object
                arm_obj = settings.armature_object

            if not weapon_obj:
                if context.active_object and context.active_object.type == "MESH":
                    weapon_obj = context.active_object
                elif context.selected_objects:
                    meshes = [o for o in context.selected_objects if o.type == "MESH"]
                    if meshes:
                        weapon_obj = meshes[0]

            if not weapon_obj:
                self.report({"ERROR"}, "No weapon mesh object selected or assigned.")
                return {"CANCELLED"}

            if not arm_obj:
                arm_obj = find_hero_armature(scene=context.scene, exclude_obj=weapon_obj, context=context)

            if not arm_obj and self.parent_mode != "NONE":
                self.report({"ERROR"}, "No character armature found in scene. Please import or select a hero model.")
                return {"CANCELLED"}

            target_bone = self.custom_socket_bone if self.socket_bone == "CUSTOM" else self.socket_bone

            try:
                res = pair_weapon_to_socket(
                    weapon_obj=weapon_obj,
                    armature_obj=arm_obj,
                    socket_bone=target_bone,
                    grip_source=self.grip_source,
                    custom_grip=tuple(self.custom_grip_pos) if self.grip_source == "CUSTOM" else None,
                    locator_name=self.locator_name if self.grip_source == "LOCATOR" else None,
                    align_rotation=self.align_rotation,
                    set_origin_to_grip=self.set_origin_to_grip,
                    parent_mode=self.parent_mode,
                    context=context
                )
                grip_pt = res["grip_point_world"]
                self.report(
                    {"INFO"},
                    f"Locked '{weapon_obj.name}' grip ({grip_pt[0]:.2f}, {grip_pt[1]:.2f}, {grip_pt[2]:.2f}) "
                    f"to socket '{res['socket_bone']}' on '{res['armature']}'"
                )
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to pair weapon: %s", e)
                self.report({"ERROR"}, f"Failed to pair weapon: {e}")
                return {"CANCELLED"}

    class HON_OT_quick_snap_weapon_cursor(Operator):
        """1-Click: Snap weapon so 3D Cursor locks directly to hand socket (_bone_weapon)"""
        bl_idname = "hon.quick_snap_weapon_cursor"
        bl_label = "Snap 3D Cursor to Hand"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            from .weapon_pairing import pair_weapon_to_socket, find_hero_armature, GripSource
            settings = getattr(context.scene, "k2_weapon_settings", None)
            weapon_obj = context.active_object if (context.active_object and context.active_object.type == "MESH") else (settings.weapon_object if settings else None)
            if not weapon_obj or weapon_obj.type != "MESH":
                if context.selected_objects:
                    meshes = [o for o in context.selected_objects if o.type == "MESH"]
                    if meshes:
                        weapon_obj = meshes[0]
            if not weapon_obj or weapon_obj.type != "MESH":
                self.report({"ERROR"}, "Select a weapon mesh object first.")
                return {"CANCELLED"}
            arm_obj = settings.armature_object if settings and settings.armature_object else None
            if not arm_obj:
                arm_obj = find_hero_armature(context.scene, exclude_obj=weapon_obj, context=context)
            try:
                res = pair_weapon_to_socket(
                    weapon_obj=weapon_obj,
                    armature_obj=arm_obj,
                    socket_bone="_bone_weapon",
                    grip_source=GripSource.CURSOR,
                    align_rotation=True,
                    set_origin_to_grip=True,
                    parent_mode="BONE",
                    context=context
                )
                self.report({"INFO"}, f"1-Click Snapped '{weapon_obj.name}' to {res['socket_bone']} via 3D Cursor")
                return {"FINISHED"}
            except Exception as e:
                self.report({"ERROR"}, f"Snap failed: {e}")
                return {"CANCELLED"}

    class HON_OT_quick_snap_weapon_vertex(Operator):
        """1-Click: Snap weapon so selected handle vertex locks directly to hand socket (_bone_weapon)"""
        bl_idname = "hon.quick_snap_weapon_vertex"
        bl_label = "Snap Selected Vertex to Hand"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            from .weapon_pairing import pair_weapon_to_socket, find_hero_armature, GripSource
            settings = getattr(context.scene, "k2_weapon_settings", None)
            weapon_obj = context.active_object if (context.active_object and context.active_object.type == "MESH") else (settings.weapon_object if settings else None)
            if not weapon_obj or weapon_obj.type != "MESH":
                if context.selected_objects:
                    meshes = [o for o in context.selected_objects if o.type == "MESH"]
                    if meshes:
                        weapon_obj = meshes[0]
            if not weapon_obj or weapon_obj.type != "MESH":
                self.report({"ERROR"}, "Select a weapon mesh object with selected handle vertices.")
                return {"CANCELLED"}
            arm_obj = settings.armature_object if settings and settings.armature_object else None
            if not arm_obj:
                arm_obj = find_hero_armature(context.scene, exclude_obj=weapon_obj, context=context)
            try:
                res = pair_weapon_to_socket(
                    weapon_obj=weapon_obj,
                    armature_obj=arm_obj,
                    socket_bone="_bone_weapon",
                    grip_source=GripSource.VERTEX,
                    align_rotation=True,
                    set_origin_to_grip=True,
                    parent_mode="BONE",
                    context=context
                )
                self.report({"INFO"}, f"1-Click Snapped '{weapon_obj.name}' to {res['socket_bone']} via Selected Vertex")
                return {"FINISHED"}
            except Exception as e:
                self.report({"ERROR"}, f"Snap failed: {e}")
                return {"CANCELLED"}

    class HON_OT_set_weapon_grip_origin(Operator):
        """Move weapon object origin to 3D Cursor position without shifting geometry"""
        bl_idname = "hon.set_weapon_grip_origin"
        bl_label = "Set Origin to 3D Cursor"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            from .weapon_pairing import set_object_origin_to_world_point
            weapon_obj = context.active_object
            if not weapon_obj or weapon_obj.type != "MESH":
                self.report({"ERROR"}, "Select a weapon mesh object.")
                return {"CANCELLED"}
            c = context.scene.cursor.location
            set_object_origin_to_world_point(weapon_obj, (c.x, c.y, c.z))
            self.report({"INFO"}, f"Origin of '{weapon_obj.name}' re-centered to ({c.x:.2f}, {c.y:.2f}, {c.z:.2f})")
            return {"FINISHED"}

    class HON_OT_unpair_weapon(Operator):
        """Detach weapon from character armature while preserving current world position"""
        bl_idname = "hon.unpair_weapon"
        bl_label = "Detach Weapon"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            from .weapon_pairing import unpair_weapon
            weapon_obj = context.active_object
            if not weapon_obj:
                self.report({"ERROR"}, "Select an object to detach.")
                return {"CANCELLED"}
            res = unpair_weapon(weapon_obj, clear_parent=True, keep_transform=True)
            self.report({"INFO"}, f"Detached '{weapon_obj.name}' from armature.")
            return {"FINISHED"}

    class HON_OT_pair_dual_weapon(Operator):
        """Snap and lock weapon across two hand sockets (holding front and back simultaneously)"""
        bl_idname = "hon.pair_dual_weapon"
        bl_label = "Pair Dual Weapon Grips (Both Hands)"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            from .weapon_pairing import pair_dual_weapon_grips, find_hero_armature

            settings = getattr(context.scene, "k2_weapon_settings", None)
            weapon_obj = None
            arm_obj = None

            if settings:
                weapon_obj = settings.weapon_object
                arm_obj = settings.armature_object

            if not weapon_obj:
                if context.active_object and context.active_object.type == "MESH":
                    weapon_obj = context.active_object
                elif context.selected_objects:
                    meshes = [o for o in context.selected_objects if o.type == "MESH"]
                    if meshes:
                        weapon_obj = meshes[0]

            if not weapon_obj:
                self.report({"ERROR"}, "No weapon mesh object selected or assigned.")
                return {"CANCELLED"}

            if not arm_obj:
                arm_obj = find_hero_armature(scene=context.scene, exclude_obj=weapon_obj, context=context)

            p_bone = settings.custom_socket_bone if (settings and settings.socket_bone == "CUSTOM") else (settings.socket_bone if settings else "_bone_weapon")
            s_bone = settings.custom_secondary_socket if (settings and settings.secondary_socket_bone == "CUSTOM") else (settings.secondary_socket_bone if settings else "_bone_fx_l_hand")

            p_source = settings.grip_source if settings else "VERTEX"
            s_source = settings.secondary_grip_source if settings else "CURSOR"
            align_vec = settings.align_to_hand_vector if settings else True
            set_origin = settings.set_origin_to_grip if settings else True
            p_mode = settings.parent_mode if settings else "BONE"

            try:
                res = pair_dual_weapon_grips(
                    weapon_obj=weapon_obj,
                    armature_obj=arm_obj,
                    primary_socket_bone=p_bone,
                    secondary_socket_bone=s_bone,
                    primary_grip_source=p_source,
                    secondary_grip_source=s_source,
                    primary_custom_grip=tuple(settings.custom_grip_pos) if (settings and p_source == "CUSTOM") else None,
                    secondary_custom_grip=tuple(settings.secondary_custom_grip_pos) if (settings and s_source == "CUSTOM") else None,
                    primary_locator_name=settings.locator_name if (settings and p_source == "LOCATOR") else None,
                    secondary_locator_name=settings.secondary_locator_name if (settings and s_source == "LOCATOR") else None,
                    align_to_hand_vector=align_vec,
                    set_origin_to_primary=set_origin,
                    parent_mode=p_mode,
                    context=context,
                )
                self.report({"INFO"}, f"Dual-paired '{weapon_obj.name}' to {res['primary_socket']} and {res['secondary_socket']}")
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to dual-pair weapon: %s", e)
                self.report({"ERROR"}, f"Failed to dual-pair weapon: {e}")
                return {"CANCELLED"}

    class HON_OT_skin_multi_point_chain(Operator):
        """1-Click Skin flexible weapon chain across front handle (_bone_weapon), chest loop (_bone_fx_chest), and rear hand (_bone_fx_l_hand)"""
        bl_idname = "hon.skin_multi_point_chain"
        bl_label = "Skin Flexible Multi-Point Chain"
        bl_options = {"REGISTER", "UNDO"}

        def execute(self, context):
            from .weapon_pairing import skin_multi_point_weapon, find_hero_armature

            settings = getattr(context.scene, "k2_weapon_settings", None)
            weapon_obj = None
            arm_obj = None

            if settings:
                weapon_obj = settings.weapon_object
                arm_obj = settings.armature_object

            if not weapon_obj:
                if context.active_object and context.active_object.type == "MESH":
                    weapon_obj = context.active_object
                elif context.selected_objects:
                    meshes = [o for o in context.selected_objects if o.type == "MESH"]
                    if meshes:
                        weapon_obj = meshes[0]

            if not weapon_obj or weapon_obj.type != "MESH":
                self.report({"ERROR"}, "Select a weapon mesh object to skin.")
                return {"CANCELLED"}

            if not arm_obj:
                arm_obj = find_hero_armature(scene=context.scene, exclude_obj=weapon_obj, context=context)

            if not arm_obj:
                self.report({"ERROR"}, "Hero armature not found.")
                return {"CANCELLED"}

            p_bone = settings.custom_socket_bone if (settings and settings.socket_bone == "CUSTOM") else (settings.socket_bone if settings else "_bone_weapon")
            s_bone = settings.custom_secondary_socket if (settings and settings.secondary_socket_bone == "CUSTOM") else (settings.secondary_socket_bone if settings else "_bone_fx_l_hand")

            try:
                res = skin_multi_point_weapon(
                    weapon_obj=weapon_obj,
                    armature_obj=arm_obj,
                    primary_socket=p_bone,
                    secondary_socket=s_bone,
                    mid_socket="_bone_fx_chest",
                    blend_power=1.0,
                    context=context
                )
                self.report({"INFO"}, f"Successfully skinned '{weapon_obj.name}' across {p_bone}, _bone_fx_chest, and {s_bone}")
                return {"FINISHED"}
            except Exception as e:
                logger.exception("Failed to skin chain weapon: %s", e)
                self.report({"ERROR"}, f"Failed to skin chain weapon: {e}")
                return {"CANCELLED"}

    class HON_PT_weapon_panel(Panel):
        """Weapon Pairing & Grip Point Locking N-Panel"""
        bl_label = "Weapon Pairing & Grip"
        bl_idname = "HON_PT_weapon_panel"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "HoN"

        def draw(self, context):
            layout = self.layout
            settings = getattr(context.scene, "k2_weapon_settings", None)
            if not settings:
                return

            # Target Object Selectors
            box_objs = layout.box()
            box_objs.label(text="Pairing Targets", icon="OBJECT_DATA")
            box_objs.prop(settings, "weapon_object", text="Weapon")
            box_objs.prop(settings, "armature_object", text="Armature")

            # Dual Grip Mode Toggle
            layout.prop(settings, "use_dual_grip", text="Dual Grip (Both Hands)", toggle=True, icon="HAND")

            # Primary Grip Point Selection Method
            box_grip = layout.box()
            box_grip.label(text="Primary Grip / Front Handle", icon="PIVOT_CURSOR")
            box_grip.prop(settings, "grip_source", text="Source")

            if settings.grip_source == "CURSOR":
                c = context.scene.cursor.location
                col = box_grip.column(align=True)
                col.label(text=f"Cursor: ({c.x:.1f}, {c.y:.1f}, {c.z:.1f})", icon="CURSOR")
                col.label(text="Place on grip: Shift+Right Click", icon="INFO")
            elif settings.grip_source == "VERTEX":
                box_grip.label(text="Select handle vertex in Edit Mode", icon="VERTEXSEL")
            elif settings.grip_source == "LOCATOR":
                box_grip.prop(settings, "locator_name", text="Locator")
            elif settings.grip_source == "CUSTOM":
                box_grip.prop(settings, "custom_grip_pos", text="")

            # Primary Hand Socket Bone
            box_bone = layout.box()
            box_bone.label(text="Primary Socket (Dominant Hand)", icon="BONE_DATA")
            box_bone.prop(settings, "socket_bone", text="Socket")
            if settings.socket_bone == "CUSTOM":
                box_bone.prop(settings, "custom_socket_bone", text="Bone")

            # Secondary Grip & Socket (if Dual Grip enabled)
            if settings.use_dual_grip:
                box_sec = layout.box()
                box_sec.label(text="Secondary Grip / Rear Chain", icon="SNAP_NORMAL")
                box_sec.prop(settings, "secondary_grip_source", text="Source")
                if settings.secondary_grip_source == "CUSTOM":
                    box_sec.prop(settings, "secondary_custom_grip_pos", text="")
                elif settings.secondary_grip_source == "LOCATOR":
                    box_sec.prop(settings, "secondary_locator_name", text="Locator")

                box_sec.label(text="Secondary Socket (Off Hand)", icon="BONE_DATA")
                box_sec.prop(settings, "secondary_socket_bone", text="Socket")
                if settings.secondary_socket_bone == "CUSTOM":
                    box_sec.prop(settings, "custom_secondary_socket", text="Bone")
                box_sec.prop(settings, "align_to_hand_vector", text="Align Along Hand Vector")

            # Main Action Button: Interactive Pairing Dialog
            layout.separator()
            row = layout.row(align=True)
            row.scale_y = 1.4
            if settings.use_dual_grip:
                row.operator("hon.pair_dual_weapon", text="Pair Dual Grips (Both Hands)", icon="CONSTRAINT_BONE")
            else:
                row.operator("hon.pair_weapon", text="Pair Weapon (Interactive Dialog...)", icon="CONSTRAINT_BONE")

            # 1-Click Fast Actions
            box_quick = layout.box()
            box_quick.label(text="1-Click Fast Snapping & Skinning", icon="SNAP_ON")
            col_q = box_quick.column(align=True)
            col_q.operator("hon.quick_snap_weapon_cursor", text="Snap (3D Cursor -> Hand)", icon="SNAP_GRID")
            col_q.operator("hon.quick_snap_weapon_vertex", text="Snap (Selected Vertex -> Hand)", icon="SNAP_VERTEX")
            col_q.operator("hon.set_weapon_grip_origin", text="Center Origin to Cursor", icon="ORIENTATION_CURSOR")
            col_q.operator("hon.skin_multi_point_chain", text="Skin Flexible Chain (Multi-Point)", icon="MOD_SKIN")
            col_q.operator("hon.unpair_weapon", text="Detach Weapon", icon="UNLINKED")


CLASSES = [
    K2ImportSettings,
    K2ExportSettings,
    K2MeshSettings,
    K2WeaponPairSettings,
    HON_OT_import_model,
    HON_OT_export_model,
    HON_OT_import_clip,
    HON_OT_export_clip,
    HON_OT_import_mdf,
    HON_OT_import_textures,
    HON_OT_export_gltf,
    HON_OT_toggle_bone_names,
    HON_OT_toggle_bones,
    HON_OT_toggle_bone_xray,
    HON_OT_fit_bone_size,
    HON_OT_fix_bone_display,
    HON_OT_reset_bone_display,
    HON_OT_cycle_bone_display,
    HON_OT_scene_info,
    HON_OT_pair_weapon,
    HON_OT_pair_dual_weapon,
    HON_OT_skin_multi_point_chain,
    HON_OT_quick_snap_weapon_cursor,
    HON_OT_quick_snap_weapon_vertex,
    HON_OT_set_weapon_grip_origin,
    HON_OT_unpair_weapon,
    HON_PT_main_panel,
    HON_PT_armature_panel,
    HON_PT_weapon_panel,
] if BPY_AVAILABLE else []


def register():
    if BPY_AVAILABLE:
        for cls in CLASSES:
            bpy.utils.register_class(cls)
        bpy.types.Scene.k2_import_settings = PointerProperty(type=K2ImportSettings)
        bpy.types.Scene.k2_export_settings = PointerProperty(type=K2ExportSettings)
        bpy.types.Scene.k2_weapon_settings = PointerProperty(type=K2WeaponPairSettings)
        bpy.types.Object.k2_mesh_settings = PointerProperty(type=K2MeshSettings)
        bpy.types.VIEW3D_MT_editor_menus.append(draw_viewport_header_armature_toggles)
        logger.info("Registered HoN Blender 4.2+/5.x UI panels, header toggles, and operators.")


def unregister():
    if BPY_AVAILABLE:
        try:
            bpy.types.VIEW3D_MT_editor_menus.remove(draw_viewport_header_armature_toggles)
        except ValueError:
            pass
        if hasattr(bpy.types.Object, "k2_mesh_settings"):
            del bpy.types.Object.k2_mesh_settings
        if hasattr(bpy.types.Scene, "k2_weapon_settings"):
            del bpy.types.Scene.k2_weapon_settings
        if hasattr(bpy.types.Scene, "k2_export_settings"):
            del bpy.types.Scene.k2_export_settings
        if hasattr(bpy.types.Scene, "k2_import_settings"):
            del bpy.types.Scene.k2_import_settings
        for cls in reversed(CLASSES):
            bpy.utils.unregister_class(cls)
        logger.info("Unregistered HoN Blender 4.2+/5.x UI panels, header toggles, and operators.")
