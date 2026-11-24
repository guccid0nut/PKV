# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Weapon Pairing & Grip Locking System for Heroes of Newerth Blender Pipeline.
Allows users and AI agents to define or select a grip/pivot point on a weapon
(via 3D Cursor, active/selected vertex, object origin, or designated locator)
and snap/lock it directly to the hero's hand socket (_bone_weapon / _bone_weaponattach).
"""

from __future__ import annotations

import logging
import math
from typing import Any, Dict, List, Optional, Tuple, Union

logger = logging.getLogger("io_scene_hon.weapon_pairing")

try:
    import bpy
    import mathutils
    from mathutils import Matrix, Vector, Euler, Quaternion
    BPY_AVAILABLE = True
except ImportError:
    bpy = None
    mathutils = None
    Matrix = None
    Vector = None
    Euler = None
    Quaternion = None
    BPY_AVAILABLE = False


# ============================================================================
# Pure Python Math Fallback (When running outside Blender)
# ============================================================================

class _Vec3:
    """Lightweight 3D vector for headless / standalone math testing."""
    def __init__(self, x: float = 0.0, y: float = 0.0, z: float = 0.0):
        if isinstance(x, (tuple, list)):
            self.x, self.y, self.z = float(x[0]), float(x[1]), float(x[2])
        else:
            self.x, self.y, self.z = float(x), float(y), float(z)

    def __add__(self, other: Any) -> _Vec3:
        o = _Vec3(other)
        return _Vec3(self.x + o.x, self.y + o.y, self.z + o.z)

    def __sub__(self, other: Any) -> _Vec3:
        o = _Vec3(other)
        return _Vec3(self.x - o.x, self.y - o.y, self.z - o.z)

    def __mul__(self, scalar: float) -> _Vec3:
        return _Vec3(self.x * scalar, self.y * scalar, self.z * scalar)

    def __truediv__(self, scalar: float) -> _Vec3:
        return _Vec3(self.x / scalar, self.y / scalar, self.z / scalar)

    def __getitem__(self, idx: int) -> float:
        return (self.x, self.y, self.z)[idx]

    def length(self) -> float:
        return math.sqrt(self.x * self.x + self.y * self.y + self.z * self.z)

    def normalized(self) -> _Vec3:
        l = self.length()
        return _Vec3(self.x / l, self.y / l, self.z / l) if l > 1e-9 else _Vec3(0, 0, 0)

    def to_tuple(self) -> Tuple[float, float, float]:
        return (round(self.x, 5), round(self.y, 5), round(self.z, 5))

    def dot(self, other: Any) -> float:
        o = _Vec3(other)
        return self.x * o.x + self.y * o.y + self.z * o.z

    def cross(self, other: Any) -> _Vec3:
        o = _Vec3(other)
        return _Vec3(
            self.y * o.z - self.z * o.y,
            self.z * o.x - self.x * o.z,
            self.x * o.y - self.y * o.x,
        )

    def __repr__(self) -> str:
        return f"Vec3({self.x:.3f}, {self.y:.3f}, {self.z:.3f})"


# Use mathutils.Vector if available, else _Vec3
def make_vec3(val: Any) -> Any:
    if BPY_AVAILABLE and Vector is not None:
        if isinstance(val, Vector):
            return val.copy()
        if isinstance(val, (tuple, list)):
            return Vector((float(val[0]), float(val[1]), float(val[2])))
        return Vector(val)
    return _Vec3(val)


# ============================================================================
# Constants & Enums
# ============================================================================

class GripSource:
    CURSOR = "CURSOR"          # Blender 3D cursor position
    VERTEX = "VERTEX"          # Active or selected vertex / vertices centroid on weapon mesh
    ORIGIN = "ORIGIN"          # Weapon object pivot / origin
    LOCATOR = "LOCATOR"        # Designated Empty / locator object (e.g. grip_locator)
    CUSTOM = "CUSTOM"          # Explicit XYZ world coordinates


class ParentMode:
    BONE = "BONE"                  # Direct bone parenting (standard HoN attachment)
    CHILD_OF = "CHILD_OF"          # Non-destructive Child Of constraint
    VERTEX_GROUP = "VERTEX_GROUP"  # Armature modifier + vertex group weighted 1.0
    DUAL_VERTEX_GROUP = "DUAL_VERTEX_GROUP"  # Blended skin weights across dual/multi sockets (chains)
    NONE = "NONE"                  # Snap position/rotation only, no parenting


DEFAULT_SOCKET_CANDIDATES = [
    "_bone_weapon",
    "_bone_weaponattach",
    "_bone_weapon1",
    "_bone_weapon2",
    "_bone_weapon3",
    "Bip01 R Hand",
    "Bip01 L Hand",
    "Bip001 R Hand",
    "Bip001 L Hand",
]


# ============================================================================
# Grip Point Resolution
# ============================================================================

def resolve_grip_point(
    weapon_obj: Any,
    source: str = GripSource.CURSOR,
    custom_coord: Optional[Tuple[float, float, float]] = None,
    locator_name: Optional[str] = None,
    cursor_location: Optional[Tuple[float, float, float]] = None,
    context: Optional[Any] = None,
) -> Tuple[float, float, float]:
    """
    Resolves the 3D world coordinate of the weapon grip/pivot point based on user selection.
    
    Args:
        weapon_obj: Weapon mesh object
        source: GripSource enum string ('CURSOR', 'VERTEX', 'ORIGIN', 'LOCATOR', 'CUSTOM')
        custom_coord: Explicit (x, y, z) if source is 'CUSTOM'
        locator_name: Name of locator/empty if source is 'LOCATOR'
        cursor_location: Optional manual override for 3D cursor position
        context: Active Blender context (optional)

    Returns:
        (x, y, z) tuple in world space coordinates.
    """
    src = source.upper()

    # 1. Custom Coordinate
    if src == GripSource.CUSTOM:
        if custom_coord is None:
            raise ValueError("GripSource 'CUSTOM' requires custom_coord (x, y, z).")
        return (float(custom_coord[0]), float(custom_coord[1]), float(custom_coord[2]))

    # 2. Weapon Object Origin
    if src == GripSource.ORIGIN:
        if hasattr(weapon_obj, "matrix_world"):
            t = weapon_obj.matrix_world.translation
            return (float(t.x), float(t.y), float(t.z))
        if hasattr(weapon_obj, "location"):
            loc = weapon_obj.location
            return (float(loc[0]), float(loc[1]), float(loc[2]))
        raise ValueError(f"Object '{getattr(weapon_obj, 'name', 'weapon')}' has no transform or location.")

    # 3. 3D Cursor
    if src == GripSource.CURSOR:
        if cursor_location is not None:
            return (float(cursor_location[0]), float(cursor_location[1]), float(cursor_location[2]))
        
        ctx = context if context else (bpy.context if BPY_AVAILABLE else None)
        if ctx and hasattr(ctx, "scene") and hasattr(ctx.scene, "cursor"):
            c_loc = ctx.scene.cursor.location
            return (float(c_loc[0]), float(c_loc[1]), float(c_loc[2]))
        
        # Fallback if no context/cursor available
        logger.warning("3D Cursor not accessible; falling back to weapon object origin.")
        return resolve_grip_point(weapon_obj, source=GripSource.ORIGIN)

    # 4. Active or Selected Vertex / Vertices
    if src == GripSource.VERTEX:
        return get_selected_vertex_center(weapon_obj, context=context)

    # 5. Designated Locator / Empty Object
    if src == GripSource.LOCATOR:
        loc_obj = None
        target_name = locator_name if locator_name else "grip_locator"
        
        # Check weapon children first (e.g. Empty child attached to weapon)
        if hasattr(weapon_obj, "children"):
            for child in weapon_obj.children:
                if child.name.lower() in (target_name.lower(), "grip", "handle", "weapon_grip", "grip_point", "locator"):
                    loc_obj = child
                    break

        if not loc_obj and BPY_AVAILABLE and bpy.data and hasattr(bpy.data, "objects"):
            loc_obj = bpy.data.objects.get(target_name)
            if not loc_obj:
                # Search for common locator aliases
                for cand in ("grip", "handle", "weapon_grip", "grip_point", "locator"):
                    loc_obj = bpy.data.objects.get(cand)
                    if loc_obj:
                        break

        if loc_obj and hasattr(loc_obj, "matrix_world"):
            t = loc_obj.matrix_world.translation
            return (float(t.x), float(t.y), float(t.z))

        raise ValueError(
            f"Grip locator object '{target_name}' not found. "
            "Please create an Empty at the grip or choose 'CURSOR' / 'VERTEX'."
        )

    raise ValueError(f"Unknown GripSource: '{source}'. Valid sources: CURSOR, VERTEX, ORIGIN, LOCATOR, CUSTOM.")


def get_selected_vertex_center(
    mesh_obj: Any,
    context: Optional[Any] = None
) -> Tuple[float, float, float]:
    """
    Computes the world-space center of selected vertices or active vertex on a mesh object.
    Works in both Object Mode and Edit Mode.
    """
    if not hasattr(mesh_obj, "data") or not hasattr(mesh_obj.data, "vertices"):
        raise ValueError(f"Object '{getattr(mesh_obj, 'name', 'obj')}' is not a mesh or lacks vertex data.")

    # Check if we are inside Blender and can use BMesh for active Edit Mode
    if BPY_AVAILABLE and getattr(mesh_obj, "mode", "OBJECT") == "EDIT":
        try:
            import bmesh
            bm = bmesh.from_edit_mesh(mesh_obj.data)
            selected_verts = [v.co.copy() for v in bm.verts if v.select]
            if not selected_verts and hasattr(bm, "select_history") and bm.select_history.active:
                act = bm.select_history.active
                if isinstance(act, bmesh.types.BMVert):
                    selected_verts = [act.co.copy()]

            if selected_verts:
                local_center = sum(selected_verts, Vector((0.0, 0.0, 0.0))) / len(selected_verts)
                world_pt = mesh_obj.matrix_world @ local_center
                return (float(world_pt.x), float(world_pt.y), float(world_pt.z))
        except Exception as e:
            logger.debug("BMesh edit mode vertex retrieval failed: %s", e)

    # Object Mode vertex selection
    verts = mesh_obj.data.vertices
    selected_indices = [v.index for v in verts if getattr(v, "select", False)]

    if not selected_indices:
        # If no vertices selected, check if user provided vertex coordinates or active vertex
        if len(verts) > 0:
            logger.warning("No vertices explicitly selected on '%s'; using vertex 0.", mesh_obj.name)
            selected_indices = [0]
        else:
            raise ValueError(f"Mesh '{mesh_obj.name}' has 0 vertices.")

    # Compute average local coordinate
    if BPY_AVAILABLE and Vector is not None:
        local_avg = sum((Vector(verts[i].co) for i in selected_indices), Vector((0.0, 0.0, 0.0))) / len(selected_indices)
        if hasattr(mesh_obj, "matrix_world"):
            world_pt = mesh_obj.matrix_world @ local_avg
            return (float(world_pt.x), float(world_pt.y), float(world_pt.z))
        return (float(local_avg.x), float(local_avg.y), float(local_avg.z))
    else:
        # Standalone python fallback
        sx = sum(verts[i].co[0] for i in selected_indices) / len(selected_indices)
        sy = sum(verts[i].co[1] for i in selected_indices) / len(selected_indices)
        sz = sum(verts[i].co[2] for i in selected_indices) / len(selected_indices)
        if hasattr(mesh_obj, "matrix_world") and hasattr(mesh_obj.matrix_world, "translation"):
            t = mesh_obj.matrix_world.translation
            return (float(sx + t.x), float(sy + t.y), float(sz + t.z))
        return (float(sx), float(sy), float(sz))


# ============================================================================
# Armature & Hand Socket Detection
# ============================================================================

def find_hero_armature(scene: Optional[Any] = None, exclude_obj: Optional[Any] = None, context: Optional[Any] = None) -> Optional[Any]:
    """
    Discovers the hero character armature in the scene.
    Prefers armatures containing HoN character bones (_bone_weapon, Bip01 Pelvis, Scene Root).
    """
    if not BPY_AVAILABLE or not bpy.data:
        return None

    # First check selected objects in context
    ctx = context if context else (bpy.context if BPY_AVAILABLE else None)
    if ctx and hasattr(ctx, "selected_objects"):
        sel_arms = [o for o in ctx.selected_objects if o.type == "ARMATURE" and o != exclude_obj]
        if sel_arms:
            return sel_arms[0]

    armatures = [o for o in bpy.data.objects if o.type == "ARMATURE" and o != exclude_obj]
    if not armatures:
        return None

    # Score armatures based on standard HoN bone signatures
    best_arm = None
    best_score = -1

    for arm in armatures:
        score = 0
        bone_names = [b.name.lower() for b in arm.data.bones] if hasattr(arm, "data") else []
        for cand in DEFAULT_SOCKET_CANDIDATES:
            if cand.lower() in bone_names:
                score += 5
        for bip in ("bip01 pelvis", "bip01 r hand", "scene root", "_bone_origin"):
            if bip in bone_names:
                score += 2
        if score > best_score:
            best_score = score
            best_arm = arm

    return best_arm if best_arm else armatures[0]


def find_socket_bone(armature_obj: Any, requested_bone: Optional[str] = None) -> str:
    """
    Finds the canonical hand socket bone on the armature.
    If requested_bone is provided, matches it (case-insensitive).
    Otherwise returns the best matching candidate from DEFAULT_SOCKET_CANDIDATES.
    """
    if not armature_obj:
        raise ValueError("Armature object is None.")

    available_bones: List[str] = []
    if hasattr(armature_obj, "data") and hasattr(armature_obj.data, "bones"):
        raw_bones = armature_obj.data.bones
        if hasattr(raw_bones, "keys"):
            available_bones = list(raw_bones.keys())
        else:
            available_bones = [b.name if hasattr(b, "name") else str(b) for b in raw_bones]
    elif hasattr(armature_obj, "pose") and hasattr(armature_obj.pose, "bones"):
        raw_bones = armature_obj.pose.bones
        if hasattr(raw_bones, "keys"):
            available_bones = list(raw_bones.keys())
        else:
            available_bones = [b.name if hasattr(b, "name") else str(b) for b in raw_bones]

    if not available_bones:
        return requested_bone if requested_bone else "_bone_weapon"

    lower_map = {b.lower(): b for b in available_bones}

    if requested_bone:
        req_clean = requested_bone.strip()
        if req_clean.lower() in lower_map:
            return lower_map[req_clean.lower()]
        # Partial match
        for k, v in lower_map.items():
            if req_clean.lower() in k:
                return v
        logger.warning("Requested bone '%s' not found in armature; falling back to candidate scan.", requested_bone)

    # Scan default candidate sockets
    for cand in DEFAULT_SOCKET_CANDIDATES:
        if cand.lower() in lower_map:
            return lower_map[cand.lower()]

    # Fallback to any bone containing 'hand'
    for k, v in lower_map.items():
        if "hand" in k:
            return v

    return available_bones[0]


def get_socket_world_transform(armature_obj: Any, bone_name: str) -> Dict[str, Any]:
    """
    Extracts the world position, orientation matrix, head, and tail of a socket bone.
    """
    if not BPY_AVAILABLE:
        # Standalone mock return
        return {
            "name": bone_name,
            "position": (46.0, 3.5, 85.5),
            "tail": (46.0, 3.5, 90.0),
            "matrix": None,
            "direction": (0.0, 0.0, 1.0),
        }

    # In Blender, check pose bones first (takes current animation/pose into account)
    pose_bone = armature_obj.pose.bones.get(bone_name) if hasattr(armature_obj, "pose") else None
    data_bone = armature_obj.data.bones.get(bone_name) if hasattr(armature_obj, "data") else None

    arm_mat = armature_obj.matrix_world

    if pose_bone:
        head_world = arm_mat @ pose_bone.head
        tail_world = arm_mat @ pose_bone.tail
        bone_mat_world = arm_mat @ pose_bone.matrix
        dir_vec = (tail_world - head_world).normalized() if (tail_world - head_world).length > 1e-6 else Vector((0, 0, 1))
        return {
            "name": bone_name,
            "position": (float(head_world.x), float(head_world.y), float(head_world.z)),
            "tail": (float(tail_world.x), float(tail_world.y), float(tail_world.z)),
            "matrix": bone_mat_world,
            "direction": (float(dir_vec.x), float(dir_vec.y), float(dir_vec.z)),
        }
    elif data_bone:
        head_world = arm_mat @ data_bone.head_local
        tail_world = arm_mat @ data_bone.tail_local
        dir_vec = (tail_world - head_world).normalized() if (tail_world - head_world).length > 1e-6 else Vector((0, 0, 1))
        return {
            "name": bone_name,
            "position": (float(head_world.x), float(head_world.y), float(head_world.z)),
            "tail": (float(tail_world.x), float(tail_world.y), float(tail_world.z)),
            "matrix": arm_mat @ data_bone.matrix_local,
            "direction": (float(dir_vec.x), float(dir_vec.y), float(dir_vec.z)),
        }

    # Fallback to armature location
    loc = arm_mat.translation
    return {
        "name": bone_name,
        "position": (float(loc.x), float(loc.y), float(loc.z)),
        "tail": (float(loc.x), float(loc.y), float(loc.z + 5.0)),
        "matrix": arm_mat,
        "direction": (0.0, 0.0, 1.0),
    }


# ============================================================================
# Weapon Origin & Transform Manipulation
# ============================================================================

def set_object_origin_to_world_point(weapon_obj: Any, world_point: Tuple[float, float, float]) -> None:
    """
    Re-centers the weapon object's origin (pivot) to world_point WITHOUT moving the mesh
    geometry in world space.
    This ensures that future rotations, scaling, and bone transforms pivot directly around the grip!
    """
    if not BPY_AVAILABLE or Matrix is None or Vector is None:
        # Standalone mock: if mock object has translation/location, update it
        if hasattr(weapon_obj, "location"):
            weapon_obj.location = list(world_point)
        if hasattr(weapon_obj, "matrix_world") and hasattr(weapon_obj.matrix_world, "translation"):
            weapon_obj.matrix_world.translation.x = world_point[0]
            weapon_obj.matrix_world.translation.y = world_point[1]
            weapon_obj.matrix_world.translation.z = world_point[2]
        return

    # If weapon is currently in Edit mode, ensure we are in Object mode
    # so vertex modifications in mesh.data are preserved.
    orig_mode = getattr(weapon_obj, "mode", "OBJECT")
    if orig_mode == "EDIT":
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception as e:
            logger.debug("Could not switch to OBJECT mode: %s", e)

    world_pt_vec = Vector(world_point)

    # Ensure unparented while resetting origin so parent inverse doesn't interfere
    prev_parent = getattr(weapon_obj, "parent", None)
    prev_parent_type = getattr(weapon_obj, "parent_type", "OBJECT")
    prev_parent_bone = getattr(weapon_obj, "parent_bone", "")
    prev_mat_inverse = weapon_obj.matrix_parent_inverse.copy() if hasattr(weapon_obj, "matrix_parent_inverse") else None

    world_mat = weapon_obj.matrix_world.copy()
    if prev_parent:
        weapon_obj.parent = None
        weapon_obj.matrix_world = world_mat

    inv_mat = world_mat.inverted()
    local_offset = inv_mat @ world_pt_vec

    # 1. Translate all mesh vertices by -local_offset
    if hasattr(weapon_obj, "data") and hasattr(weapon_obj.data, "vertices"):
        mesh = weapon_obj.data
        for v in mesh.vertices:
            v.co -= local_offset
        mesh.update()

    # 2. Update object's world matrix translation to world_point
    world_mat.translation = world_pt_vec
    weapon_obj.matrix_world = world_mat

    # Restore parent if it had one
    if prev_parent:
        weapon_obj.parent = prev_parent
        weapon_obj.parent_type = prev_parent_type
        weapon_obj.parent_bone = prev_parent_bone
        if prev_mat_inverse:
            weapon_obj.matrix_parent_inverse = prev_mat_inverse

    logger.info("Re-centered origin of '%s' to grip point %s", weapon_obj.name, world_point)


# ============================================================================
# Main Weapon Pairing Function
# ============================================================================

def pair_weapon_to_socket(
    weapon_obj: Any,
    armature_obj: Optional[Any] = None,
    socket_bone: Optional[str] = "_bone_weapon",
    grip_source: str = GripSource.CURSOR,
    custom_grip: Optional[Tuple[float, float, float]] = None,
    locator_name: Optional[str] = None,
    align_rotation: bool = True,
    set_origin_to_grip: bool = True,
    parent_mode: str = ParentMode.BONE,
    context: Optional[Any] = None,
) -> Dict[str, Any]:
    """
    1-Click Weapon Pairing Workflow:
    Snaps and locks a weapon to the hero's hand socket bone (_bone_weapon).
    
    Workflow Steps:
    1. Resolves weapon grip point in 3D space (3D Cursor, active/selected vertex, origin, or locator).
    2. Identifies target hero armature and hand socket bone.
    3. (Optional) Re-centers weapon object origin to the designated grip point.
    4. Snaps the weapon so the grip point coincides exactly with the hand socket.
    5. (Optional) Aligns weapon orientation with the socket bone.
    6. Locks weapon to bone via ParentMode (BONE, CHILD_OF, VERTEX_GROUP, or NONE).
    """
    if weapon_obj is None:
        raise ValueError("weapon_obj cannot be None.")

    # If weapon is currently in Edit mode, ensure Object mode so vertex selection is synced
    if BPY_AVAILABLE and getattr(weapon_obj, "mode", "OBJECT") == "EDIT":
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception as e:
            logger.debug("Could not switch to OBJECT mode: %s", e)

    # 1. Discover armature if not explicitly provided
    if armature_obj is None:
        armature_obj = find_hero_armature(scene=(context.scene if context else None), exclude_obj=weapon_obj, context=context)
        if armature_obj is None and parent_mode != ParentMode.NONE:
            raise ValueError("No armature found in scene to pair weapon with.")

    # 2. Resolve socket bone name
    resolved_bone_name = find_socket_bone(armature_obj, requested_bone=socket_bone) if armature_obj else "_bone_weapon"

    # 3. Resolve grip point in world space
    grip_world = resolve_grip_point(
        weapon_obj,
        source=grip_source,
        custom_coord=custom_grip,
        locator_name=locator_name,
        context=context,
    )
    logger.info("Resolved grip point for '%s' (%s): %s", getattr(weapon_obj, "name", "weapon"), grip_source, grip_world)

    # 4. Extract socket world transform
    socket_tf = get_socket_world_transform(armature_obj, resolved_bone_name) if armature_obj else {
        "position": (0.0, 0.0, 0.0),
        "matrix": None,
        "direction": (0.0, 0.0, 1.0)
    }
    socket_pos = socket_tf["position"]

    # 5. Optional origin re-centering
    if set_origin_to_grip:
        set_object_origin_to_world_point(weapon_obj, grip_world)
        grip_world = resolve_grip_point(weapon_obj, source=GripSource.ORIGIN)

    # 6. Apply Snap Translation & Rotation in Blender
    if BPY_AVAILABLE and Matrix is not None and Vector is not None:
        target_pos_vec = Vector(socket_pos)

        # Unparent first to prevent any previous parent inverse or matrix_basis corruption
        if weapon_obj.parent:
            old_mat = weapon_obj.matrix_world.copy()
            weapon_obj.parent = None
            weapon_obj.matrix_world = old_mat

        # Local grip point coordinate in current weapon object space
        inv_world = weapon_obj.matrix_world.inverted()
        local_grip = inv_world @ Vector(grip_world)

        # Target orientation: bone rotation if align_rotation is True, else keep current weapon orientation
        if align_rotation and socket_tf.get("matrix") is not None:
            bone_mat = socket_tf["matrix"]
            target_rot = bone_mat.to_3x3().normalized()
        else:
            target_rot = weapon_obj.matrix_world.to_3x3().normalized()

        # Preserve weapon scale (including non-uniform scale) without skewing
        scale_vec = weapon_obj.matrix_world.to_scale()
        scale_mat = Matrix.Diagonal((scale_vec.x, scale_vec.y, scale_vec.z, 1.0))
        rot_scale_mat = target_rot.to_4x4() @ scale_mat

        # Compute weapon origin translation such that the grip point lands at socket_pos
        v_grip_rot = (rot_scale_mat @ local_grip.to_4d()).to_3d()
        target_origin = target_pos_vec - v_grip_rot

        new_world_mat = Matrix.Translation(target_origin) @ rot_scale_mat
        weapon_obj.matrix_world = new_world_mat

        # 7. Apply Locking / Parenting
        p_mode = parent_mode.upper()
        if armature_obj and p_mode == ParentMode.BONE:
            weapon_obj.parent = armature_obj
            weapon_obj.parent_type = "BONE"
            weapon_obj.parent_bone = resolved_bone_name

            # In Blender, bone parenting child space origin is at the bone's TAIL
            pose_b = armature_obj.pose.bones.get(resolved_bone_name) if hasattr(armature_obj, "pose") else None
            data_b = armature_obj.data.bones.get(resolved_bone_name) if hasattr(armature_obj, "data") else None

            if pose_b:
                bone_mat = pose_b.matrix.copy()
                bone_mat.translation = pose_b.tail
                bone_world = armature_obj.matrix_world @ bone_mat
                weapon_obj.matrix_parent_inverse = bone_world.inverted()
            elif data_b:
                bone_mat = data_b.matrix_local.copy()
                bone_mat.translation = data_b.tail_local
                bone_world = armature_obj.matrix_world @ bone_mat
                weapon_obj.matrix_parent_inverse = bone_world.inverted()

        elif armature_obj and p_mode == ParentMode.CHILD_OF:
            # Remove any existing Child Of constraints to this socket
            for c in list(weapon_obj.constraints):
                if c.type == "CHILD_OF" and c.subtarget == resolved_bone_name:
                    weapon_obj.constraints.remove(c)
            con = weapon_obj.constraints.new(type="CHILD_OF")
            con.name = f"ChildOf_{resolved_bone_name}"
            con.target = armature_obj
            con.subtarget = resolved_bone_name
            # Set inverse matrix relative to pose bone
            pose_b = armature_obj.pose.bones.get(resolved_bone_name) if hasattr(armature_obj, "pose") else None
            if pose_b:
                bone_world = armature_obj.matrix_world @ pose_b.matrix
                con.inverse_matrix = bone_world.inverted()

        elif armature_obj and p_mode == ParentMode.VERTEX_GROUP:
            # Create vertex group weighted 1.0 for all vertices
            vg = weapon_obj.vertex_groups.get(resolved_bone_name) or weapon_obj.vertex_groups.new(name=resolved_bone_name)
            all_v = list(range(len(weapon_obj.data.vertices)))
            vg.add(all_v, 1.0, "REPLACE")
            # Ensure Armature modifier exists
            has_mod = any(m.type == "ARMATURE" and m.object == armature_obj for m in weapon_obj.modifiers)
            if not has_mod:
                mod = weapon_obj.modifiers.new(name="Armature", type="ARMATURE")
                mod.object = armature_obj

        # Sync depsgraph
        if context and hasattr(context, "view_layer") and context.view_layer:
            context.view_layer.update()
        elif hasattr(bpy.context, "view_layer") and bpy.context.view_layer:
            bpy.context.view_layer.update()

    else:
        # Standalone mock fallback
        if hasattr(weapon_obj, "location"):
            weapon_obj.location = list(socket_pos)
        if hasattr(weapon_obj, "matrix_world") and hasattr(weapon_obj.matrix_world, "translation"):
            weapon_obj.matrix_world.translation.x = socket_pos[0]
            weapon_obj.matrix_world.translation.y = socket_pos[1]
            weapon_obj.matrix_world.translation.z = socket_pos[2]
        if armature_obj and parent_mode.upper() == ParentMode.BONE:
            weapon_obj.parent = armature_obj
            weapon_obj.parent_type = "BONE"
            weapon_obj.parent_bone = resolved_bone_name

    result = {
        "status": "ok",
        "weapon": getattr(weapon_obj, "name", "weapon"),
        "armature": getattr(armature_obj, "name", "armature") if armature_obj else None,
        "socket_bone": resolved_bone_name,
        "grip_source": grip_source,
        "grip_point_world": [round(v, 4) for v in grip_world],
        "socket_pos_world": [round(v, 4) for v in socket_pos],
        "parent_mode": parent_mode,
        "origin_recentered": set_origin_to_grip,
        "aligned_rotation": align_rotation,
    }
    logger.info("Successfully paired weapon: %s", result)
    return result


def unpair_weapon(weapon_obj: Any, clear_parent: bool = True, keep_transform: bool = True) -> Dict[str, Any]:
    """
    Detaches/unparents the weapon from any armature or bone while preserving its current world position.
    Also clears Child Of constraints and Armature modifiers targeting character armatures.
    """
    if weapon_obj is None:
        raise ValueError("weapon_obj cannot be None.")

    prev_parent = getattr(weapon_obj, "parent", None)
    prev_bone = getattr(weapon_obj, "parent_bone", "")

    if BPY_AVAILABLE:
        # Preserve world transform
        world_mat = weapon_obj.matrix_world.copy()

        if clear_parent and weapon_obj.parent:
            weapon_obj.parent = None
            weapon_obj.parent_type = "OBJECT"
            weapon_obj.parent_bone = ""

        # Remove Child Of constraints targeting bones
        for c in list(weapon_obj.constraints):
            if c.type == "CHILD_OF":
                weapon_obj.constraints.remove(c)

        # Remove Armature modifiers targeting the previous armature or named Armature
        for m in list(weapon_obj.modifiers):
            if m.type == "ARMATURE" and (prev_parent is None or m.object == prev_parent or m.name == "Armature"):
                weapon_obj.modifiers.remove(m)

        if keep_transform:
            weapon_obj.matrix_world = world_mat

        if hasattr(bpy.context, "view_layer") and bpy.context.view_layer:
            bpy.context.view_layer.update()
    else:
        # Standalone mock fallback
        if clear_parent and hasattr(weapon_obj, "parent"):
            weapon_obj.parent = None
            weapon_obj.parent_type = "OBJECT"
            weapon_obj.parent_bone = ""
        if hasattr(weapon_obj, "modifiers"):
            weapon_obj.modifiers = [m for m in weapon_obj.modifiers if getattr(m, "type", "") != "ARMATURE"]
        if hasattr(weapon_obj, "constraints"):
            weapon_obj.constraints = [c for c in weapon_obj.constraints if getattr(c, "type", "") != "CHILD_OF"]

    return {
        "status": "ok",
        "weapon": getattr(weapon_obj, "name", "weapon"),
        "detached_from_armature": getattr(prev_parent, "name", None) if prev_parent else None,
        "detached_from_bone": prev_bone if prev_bone else None,
    }


# ============================================================================
# Dual-Grip Weapon Pairing Function (Holding Both Front & Back)
# ============================================================================

def pair_dual_weapon_grips(
    weapon_obj: Any,
    armature_obj: Optional[Any] = None,
    primary_socket_bone: Optional[str] = "_bone_weapon",
    secondary_socket_bone: Optional[str] = "_bone_fx_l_hand",
    primary_grip_source: str = GripSource.VERTEX,
    secondary_grip_source: str = GripSource.CURSOR,
    primary_custom_grip: Optional[Tuple[float, float, float]] = None,
    secondary_custom_grip: Optional[Tuple[float, float, float]] = None,
    primary_locator_name: Optional[str] = None,
    secondary_locator_name: Optional[str] = None,
    align_to_hand_vector: bool = True,
    set_origin_to_primary: bool = True,
    parent_mode: str = ParentMode.BONE,
    context: Optional[Any] = None,
) -> Dict[str, Any]:
    """
    Dual-Grip Weapon Pairing Workflow:
    Snaps and locks a weapon across two hand sockets simultaneously (e.g. front handle in right hand,
    rear chain in left hand).
    
    Workflow Steps:
    1. Resolves primary and secondary grip points on weapon geometry.
    2. Identifies primary and secondary hand socket bones on character armature.
    3. (Optional) Re-centers weapon origin to the primary grip point.
    4. Aligns weapon along vector from primary grip to secondary grip toward target hand sockets.
    5. Snaps primary grip to primary socket bone.
    6. Locks weapon to character via ParentMode (BONE, CHILD_OF, VERTEX_GROUP, or NONE).
    """
    if weapon_obj is None:
        raise ValueError("weapon_obj cannot be None.")

    # If weapon is currently in Edit mode, ensure Object mode
    if BPY_AVAILABLE and getattr(weapon_obj, "mode", "OBJECT") == "EDIT":
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception as e:
            logger.debug("Could not switch to OBJECT mode: %s", e)

    # 1. Discover armature if not explicitly provided
    if armature_obj is None:
        armature_obj = find_hero_armature(scene=(context.scene if context else None), exclude_obj=weapon_obj, context=context)
        if armature_obj is None and parent_mode != ParentMode.NONE:
            raise ValueError("No armature found in scene to pair weapon with.")

    # 2. Resolve socket bone names
    primary_bone = find_socket_bone(armature_obj, requested_bone=primary_socket_bone) if armature_obj else (primary_socket_bone or "_bone_weapon")
    secondary_bone = find_socket_bone(armature_obj, requested_bone=secondary_socket_bone) if armature_obj else (secondary_socket_bone or "_bone_fx_l_hand")

    # 3. Resolve grip points in world space
    primary_grip = resolve_grip_point(
        weapon_obj,
        source=primary_grip_source,
        custom_coord=primary_custom_grip,
        locator_name=primary_locator_name,
        context=context,
    )
    secondary_grip = resolve_grip_point(
        weapon_obj,
        source=secondary_grip_source,
        custom_coord=secondary_custom_grip,
        locator_name=secondary_locator_name,
        context=context,
    )
    logger.info("Resolved dual grip points for '%s': primary=%s, secondary=%s", getattr(weapon_obj, "name", "weapon"), primary_grip, secondary_grip)

    # 4. Extract socket world transforms
    p_tf = get_socket_world_transform(armature_obj, primary_bone) if armature_obj else {
        "position": (46.0, 3.5, 85.5),
        "matrix": None,
        "direction": (0.0, 0.0, 1.0)
    }
    s_tf = get_socket_world_transform(armature_obj, secondary_bone) if armature_obj else {
        "position": (-46.0, 3.5, 85.5),
        "matrix": None,
        "direction": (0.0, 0.0, 1.0)
    }
    p_pos = p_tf["position"]
    s_pos = s_tf["position"]

    # 5. Optional primary origin re-centering
    if set_origin_to_primary:
        set_object_origin_to_world_point(weapon_obj, primary_grip)
        primary_grip = resolve_grip_point(weapon_obj, source=GripSource.ORIGIN)
        secondary_grip = resolve_grip_point(
            weapon_obj,
            source=secondary_grip_source,
            custom_coord=secondary_custom_grip,
            locator_name=secondary_locator_name,
            context=context,
        )

    # 6. Apply Alignment and Snap in Blender
    if BPY_AVAILABLE and Matrix is not None and Vector is not None:
        target_pos_vec = Vector(p_pos)

        # Unparent first
        if weapon_obj.parent:
            old_mat = weapon_obj.matrix_world.copy()
            weapon_obj.parent = None
            weapon_obj.matrix_world = old_mat

        # Compute direction vector between grip points and target sockets
        v_grip = Vector(secondary_grip) - Vector(primary_grip)
        v_target = Vector(s_pos) - Vector(p_pos)

        if align_to_hand_vector and v_grip.length > 1e-5 and v_target.length > 1e-5:
            rot_diff = v_grip.normalized().rotation_difference(v_target.normalized())
            target_rot = rot_diff.to_matrix() @ weapon_obj.matrix_world.to_3x3().normalized()
        elif p_tf.get("matrix") is not None:
            target_rot = p_tf["matrix"].to_3x3().normalized()
        else:
            target_rot = weapon_obj.matrix_world.to_3x3().normalized()

        # Preserve weapon scale
        scale_vec = weapon_obj.matrix_world.to_scale()
        scale_mat = Matrix.Diagonal((scale_vec.x, scale_vec.y, scale_vec.z, 1.0))
        rot_scale_mat = target_rot.to_4x4() @ scale_mat

        # Local primary grip point
        inv_world = weapon_obj.matrix_world.inverted()
        local_primary = inv_world @ Vector(primary_grip)
        v_rot = (rot_scale_mat @ local_primary.to_4d()).to_3d()
        target_origin = target_pos_vec - v_rot

        new_world_mat = Matrix.Translation(target_origin) @ rot_scale_mat
        weapon_obj.matrix_world = new_world_mat

        # 7. Apply Parenting
        p_mode = parent_mode.upper()
        if armature_obj and p_mode == ParentMode.BONE:
            weapon_obj.parent = armature_obj
            weapon_obj.parent_type = "BONE"
            weapon_obj.parent_bone = primary_bone

            pose_b = armature_obj.pose.bones.get(primary_bone) if hasattr(armature_obj, "pose") else None
            data_b = armature_obj.data.bones.get(primary_bone) if hasattr(armature_obj, "data") else None

            if pose_b:
                bone_mat = pose_b.matrix.copy()
                bone_mat.translation = pose_b.tail
                bone_world = armature_obj.matrix_world @ bone_mat
                weapon_obj.matrix_parent_inverse = bone_world.inverted()
            elif data_b:
                bone_mat = data_b.matrix_local.copy()
                bone_mat.translation = data_b.tail_local
                bone_world = armature_obj.matrix_world @ bone_mat
                weapon_obj.matrix_parent_inverse = bone_world.inverted()

        elif armature_obj and p_mode == ParentMode.CHILD_OF:
            for c in list(weapon_obj.constraints):
                if c.type == "CHILD_OF" and c.subtarget == primary_bone:
                    weapon_obj.constraints.remove(c)
            con = weapon_obj.constraints.new(type="CHILD_OF")
            con.name = f"ChildOf_{primary_bone}"
            con.target = armature_obj
            con.subtarget = primary_bone
            pose_b = armature_obj.pose.bones.get(primary_bone) if hasattr(armature_obj, "pose") else None
            if pose_b:
                bone_world = armature_obj.matrix_world @ pose_b.matrix
                con.inverse_matrix = bone_world.inverted()

        elif armature_obj and p_mode == ParentMode.DUAL_VERTEX_GROUP:
            skin_multi_point_weapon(
                weapon_obj=weapon_obj,
                armature_obj=armature_obj,
                primary_socket=primary_bone,
                secondary_socket=secondary_bone,
                primary_grip=primary_grip,
                secondary_grip=secondary_grip,
                blend_power=1.0,
                context=context,
            )

        elif armature_obj and p_mode == ParentMode.VERTEX_GROUP:
            vg = weapon_obj.vertex_groups.get(primary_bone) or weapon_obj.vertex_groups.new(name=primary_bone)
            all_v = list(range(len(weapon_obj.data.vertices)))
            vg.add(all_v, 1.0, "REPLACE")
            has_mod = any(m.type == "ARMATURE" and m.object == armature_obj for m in weapon_obj.modifiers)
            if not has_mod:
                mod = weapon_obj.modifiers.new(name="Armature", type="ARMATURE")
                mod.object = armature_obj

        if context and hasattr(context, "view_layer") and context.view_layer:
            context.view_layer.update()
        elif hasattr(bpy.context, "view_layer") and bpy.context.view_layer:
            bpy.context.view_layer.update()

    else:
        # Standalone mock fallback
        if hasattr(weapon_obj, "location"):
            weapon_obj.location = list(p_pos)
        if hasattr(weapon_obj, "matrix_world") and hasattr(weapon_obj.matrix_world, "translation"):
            weapon_obj.matrix_world.translation.x = p_pos[0]
            weapon_obj.matrix_world.translation.y = p_pos[1]
            weapon_obj.matrix_world.translation.z = p_pos[2]
        if armature_obj and parent_mode.upper() == ParentMode.BONE:
            weapon_obj.parent = armature_obj
            weapon_obj.parent_type = "BONE"
            weapon_obj.parent_bone = primary_bone

    result = {
        "status": "ok",
        "weapon": getattr(weapon_obj, "name", "weapon"),
        "armature": getattr(armature_obj, "name", "armature") if armature_obj else None,
        "primary_socket": primary_bone,
        "secondary_socket": secondary_bone,
        "primary_grip_world": [round(v, 4) for v in primary_grip],
        "secondary_grip_world": [round(v, 4) for v in secondary_grip],
        "primary_socket_pos": [round(v, 4) for v in p_pos],
        "secondary_socket_pos": [round(v, 4) for v in s_pos],
        "aligned_to_vector": align_to_hand_vector,
        "parent_mode": parent_mode,
        "origin_recentered": set_origin_to_primary,
    }
    logger.info("Successfully paired dual-grip weapon: %s", result)
    return result


# ============================================================================
# Multi-Point Weapon Skinning (Flexible Chains & Dual Grips)
# ============================================================================

def skin_multi_point_weapon(
    weapon_obj: Any,
    armature_obj: Any,
    primary_socket: str = "_bone_weapon",
    secondary_socket: str = "_bone_fx_l_hand",
    primary_grip: Optional[Tuple[float, float, float]] = None,
    secondary_grip: Optional[Tuple[float, float, float]] = None,
    mid_socket: Optional[str] = None,
    mid_grip: Optional[Tuple[float, float, float]] = None,
    blend_power: float = 1.0,
    context: Optional[Any] = None,
) -> Dict[str, Any]:
    """
    Skin a weapon or chain across multiple hand/torso sockets simultaneously.
    Computes smooth weight gradients along the weapon/chain geometry:
      - Primary socket (_bone_weapon): 100% influence at front handle.
      - Secondary socket (_bone_fx_l_hand): 100% influence at rear chain end.
      - (Optional) Mid socket (_bone_fx_chest / Spine): 100% influence at chain midpoint.
      - Intermediate vertices are linearly/exponentially blended.
    Ensures that when exported to K2 SMDL (mode 1 / lnk1), the weapon/chain dynamically flexes
    between both hands and torso during animations instead of remaining a rigid stick.
    """
    if not weapon_obj or not armature_obj:
        return {"status": "error", "message": "weapon_obj and armature_obj required"}

    if primary_grip is None:
        primary_grip = resolve_grip_point(weapon_obj, source=GripSource.ORIGIN, context=context)
    if secondary_grip is None:
        secondary_grip = resolve_grip_point(weapon_obj, source=GripSource.CURSOR, context=context)

    # In Blender:
    if BPY_AVAILABLE and hasattr(weapon_obj, "data") and hasattr(weapon_obj.data, "vertices"):
        vg_primary = weapon_obj.vertex_groups.get(primary_socket) or weapon_obj.vertex_groups.new(name=primary_socket)
        vg_secondary = weapon_obj.vertex_groups.get(secondary_socket) or weapon_obj.vertex_groups.new(name=secondary_socket)
        vg_mid = None
        if mid_socket and mid_grip:
            vg_mid = weapon_obj.vertex_groups.get(mid_socket) or weapon_obj.vertex_groups.new(name=mid_socket)

        p1 = Vector(primary_grip)
        p2 = Vector(secondary_grip)
        pmid = Vector(mid_grip) if (mid_socket and mid_grip) else None

        v_total = p2 - p1
        total_len_sq = v_total.length_squared
        if total_len_sq < 1e-6:
            total_len_sq = 1.0

        for v in weapon_obj.data.vertices:
            co_world = weapon_obj.matrix_world @ v.co

            if pmid is not None:
                # 3-point segmented chain (Handle -> Chest Loop -> Rear Hand)
                v_seg1 = pmid - p1
                len1_sq = v_seg1.length_squared or 1.0
                t1 = max(0.0, min(1.0, (co_world - p1).dot(v_seg1) / len1_sq))

                v_seg2 = p2 - pmid
                len2_sq = v_seg2.length_squared or 1.0
                t2 = max(0.0, min(1.0, (co_world - pmid).dot(v_seg2) / len2_sq))

                dist_to_seg1 = (co_world - (p1 + v_seg1 * t1)).length
                dist_to_seg2 = (co_world - (pmid + v_seg2 * t2)).length

                if dist_to_seg1 <= dist_to_seg2:
                    w_prim = (1.0 - t1) ** blend_power
                    w_mid = (t1) ** blend_power
                    w_sec = 0.0
                else:
                    w_prim = 0.0
                    w_mid = (1.0 - t2) ** blend_power
                    w_sec = (t2) ** blend_power

                total_w = w_prim + w_mid + w_sec
                if total_w > 1e-6:
                    w_prim /= total_w
                    w_mid /= total_w
                    w_sec /= total_w
                else:
                    w_prim, w_mid, w_sec = 1.0, 0.0, 0.0

                vg_primary.add([v.index], w_prim, "REPLACE")
                vg_mid.add([v.index], w_mid, "REPLACE")
                vg_secondary.add([v.index], w_sec, "REPLACE")

            else:
                # 2-point dual grip gradient (Front Handle -> Rear Chain)
                t = max(0.0, min(1.0, (co_world - p1).dot(v_total) / total_len_sq))
                w_sec = (t) ** blend_power
                w_prim = (1.0 - t) ** blend_power
                total_w = w_prim + w_sec
                if total_w > 1e-6:
                    w_prim /= total_w
                    w_sec /= total_w
                else:
                    w_prim, w_sec = 1.0, 0.0

                vg_primary.add([v.index], w_prim, "REPLACE")
                vg_secondary.add([v.index], w_sec, "REPLACE")

        # Armature modifier
        has_mod = any(m.type == "ARMATURE" and m.object == armature_obj for m in weapon_obj.modifiers)
        if not has_mod:
            mod = weapon_obj.modifiers.new(name="Armature", type="ARMATURE")
            mod.object = armature_obj

        if context and hasattr(context, "view_layer") and context.view_layer:
            context.view_layer.update()
        elif hasattr(bpy.context, "view_layer") and bpy.context.view_layer:
            bpy.context.view_layer.update()

        return {
            "status": "ok",
            "mode": "3-point" if pmid is not None else "dual-gradient",
            "vertex_count": len(weapon_obj.data.vertices),
            "primary_socket": primary_socket,
            "secondary_socket": secondary_socket,
            "mid_socket": mid_socket,
        }

    # Headless / mock fallback
    return {
        "status": "ok",
        "mode": "mock-skinning",
        "primary_socket": primary_socket,
        "secondary_socket": secondary_socket,
        "mid_socket": mid_socket,
    }


