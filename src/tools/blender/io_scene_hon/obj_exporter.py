# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth to Wavefront OBJ / MTL & Adobe Mixamo ZIP Exporter.
Exports HoN 3D models into clean, unrigged Wavefront OBJ + MTL packages
with decoded PNG textures, bundled into a standard Mixamo-ready ZIP archive
for instant automatic humanoid rigging and animation retargeting.
"""

from __future__ import annotations

import logging
import os
import zipfile
from typing import Dict, List, Optional, Tuple

from .smdl_parser import HonModelData, HonMesh
from .material_parser import HonMaterialData

logger = logging.getLogger("io_scene_hon.obj_exporter")


def strip_model_accessories(model: HonModelData) -> HonModelData:
    """
    Removes weapons, floating accessories, and disconnected props from a HoN model
    so that Adobe Mixamo auto-rigger receives a clean humanoid character mesh.
    """
    if not model.meshes or not model.bones:
        return model

    def is_biped_bone(name: str) -> bool:
        n = name.lower()
        return (n.startswith("bip") or 
                n.startswith("_bone_origin") or 
                n.startswith("scene root") or 
                n.startswith("root"))

    # Determine hand X span to detect distant floating accessories
    hand_xs = [abs(b.loc_matrix[12]) for b in model.bones 
               if "hand" in b.name.lower() and "finger" not in b.name.lower()]
    max_hand_x = max(hand_xs) if hand_xs else 30.0
    thresh_x = max(35.0, max_hand_x * 1.15)

    # 1. Identify accessory & weapon bones
    acc_bone_indices = set()
    weapon_keywords = ("weapon", "projectile", "arrow", "shield", "staff", "sword", "bow", "gun", "hammer", "axe", "claw", "guitar")

    for i, b in enumerate(model.bones):
        if is_biped_bone(b.name):
            continue
        bname = b.name.lower()
        if any(k in bname for k in weapon_keywords):
            acc_bone_indices.add(i)
        elif abs(b.loc_matrix[12]) > thresh_x:
            acc_bone_indices.add(i)

    # 2. Add non-biped bones that are parents of weapon bones
    for i, b in enumerate(model.bones):
        if is_biped_bone(b.name):
            continue
        children = [ci for ci, cb in enumerate(model.bones) if cb.parent_index == i]
        if any(ci in acc_bone_indices for ci in children):
            acc_bone_indices.add(i)

    # 3. Add all non-biped descendants of accessory bones
    added = True
    while added:
        added = False
        for i, b in enumerate(model.bones):
            if is_biped_bone(b.name):
                continue
            if b.parent_index in acc_bone_indices and i not in acc_bone_indices:
                acc_bone_indices.add(i)
                added = True

    if not acc_bone_indices:
        return model

    acc_names = [model.bones[i].name for i in acc_bone_indices]
    logger.info("Stripping accessories associated with bones: %s", acc_names)

    # 4. Filter meshes and remove accessory triangles
    new_meshes: List[HonMesh] = []
    total_verts_removed = 0

    for mesh in model.meshes:
        if any(k in mesh.name.lower() for k in weapon_keywords):
            logger.info("Skipping dedicated accessory mesh: %s", mesh.name)
            continue

        acc_verts = set()
        for vi, link in enumerate(mesh.bone_links):
            for bi, w in zip(link.bone_indices, link.weights):
                if bi in acc_bone_indices and w > 0.15:
                    acc_verts.add(vi)
                    break

        if not acc_verts:
            new_meshes.append(mesh)
            continue

        # Keep triangles where NO vertex is an accessory vertex
        kept_tris = []
        for i in range(0, len(mesh.indices), 3):
            i0, i1, i2 = mesh.indices[i], mesh.indices[i+1], mesh.indices[i+2]
            if i0 not in acc_verts and i1 not in acc_verts and i2 not in acc_verts:
                kept_tris.append((i0, i1, i2))

        if not kept_tris:
            logger.info("Mesh '%s' contained only accessories and was stripped entirely", mesh.name)
            continue

        kept_v_indices = sorted(list(set(v for tri in kept_tris for v in tri)))
        old_to_new = {old: new for new, old in enumerate(kept_v_indices)}

        new_mesh = HonMesh(
            index=mesh.index,
            name=mesh.name,
            default_material=mesh.default_material
        )
        new_mesh.vertices = [mesh.vertices[i] for i in kept_v_indices]
        new_mesh.normals = [mesh.normals[i] for i in kept_v_indices] if mesh.normals else []
        new_mesh.texcoords = [mesh.texcoords[i] for i in kept_v_indices] if mesh.texcoords else []
        new_mesh.indices = [old_to_new[v] for tri in kept_tris for v in tri]
        new_mesh.bone_links = [mesh.bone_links[i] for i in kept_v_indices] if mesh.bone_links else []
        new_mesh.num_vertices = len(new_mesh.vertices)
        new_mesh.num_faces = len(new_mesh.indices) // 3

        verts_removed = len(mesh.vertices) - len(new_mesh.vertices)
        total_verts_removed += verts_removed
        logger.info("Mesh '%s': retained %d body vertices (%d accessory vertices stripped)",
                    mesh.name, new_mesh.num_vertices, verts_removed)
        new_meshes.append(new_mesh)

    filtered_model = HonModelData(
        version=model.version,
        num_meshes=len(new_meshes),
        num_bones=len(model.bones),
        num_surfs=len(model.surfaces),
        bounding_min=model.bounding_min,
        bounding_max=model.bounding_max,
        bones=model.bones,
        meshes=new_meshes,
        surfaces=model.surfaces
    )
    return filtered_model


def export_hon_to_obj_and_mtl(
    model: HonModelData,
    base_name: str,
    output_dir: str,
    materials: Optional[List[HonMaterialData]] = None,
    strip_accessories: bool = False
) -> Tuple[str, str, List[str]]:
    """
    Exports a HonModelData instance to an OBJ file, companion MTL file,
    and associated texture PNG files.

    Returns:
        (obj_path, mtl_path, list_of_texture_paths)
    """
    if strip_accessories:
        model = strip_model_accessories(model)

    os.makedirs(output_dir, exist_ok=True)

    obj_filename = f"{base_name}.obj"
    mtl_filename = f"{base_name}.mtl"
    obj_path = os.path.join(output_dir, obj_filename)
    mtl_path = os.path.join(output_dir, mtl_filename)

    texture_paths: List[str] = []
    mat_map: Dict[str, HonMaterialData] = {}
    if materials:
        for m in materials:
            mat_map[m.name] = m

    # 1. Export Textures
    # Save diffuse and normal PNG textures if available
    diffuse_png_filename: Optional[str] = None
    normal_png_filename: Optional[str] = None

    primary_mat = materials[0] if materials else None
    if primary_mat:
        if primary_mat.diffuse_png_bytes:
            diffuse_png_filename = f"{base_name}_diffuse.png"
            diffuse_path = os.path.join(output_dir, diffuse_png_filename)
            with open(diffuse_path, "wb") as f:
                f.write(primary_mat.diffuse_png_bytes)
            texture_paths.append(diffuse_path)
            logger.info("Saved diffuse texture: %s (%d bytes)", diffuse_path, len(primary_mat.diffuse_png_bytes))

        if primary_mat.normal_png_bytes:
            normal_png_filename = f"{base_name}_normal.png"
            normal_path = os.path.join(output_dir, normal_png_filename)
            with open(normal_path, "wb") as f:
                f.write(primary_mat.normal_png_bytes)
            texture_paths.append(normal_path)
            logger.info("Saved normal texture: %s (%d bytes)", normal_path, len(primary_mat.normal_png_bytes))

    # 2. Write MTL file
    mat_name = f"{base_name}_mat"
    with open(mtl_path, "w", encoding="utf-8") as f_mtl:
        f_mtl.write(f"# Material file for {base_name}\n")
        f_mtl.write(f"newmtl {mat_name}\n")
        f_mtl.write("Ka 1.000000 1.000000 1.000000\n")
        f_mtl.write("Kd 1.000000 1.000000 1.000000\n")
        f_mtl.write("Ks 0.200000 0.200000 0.200000\n")
        f_mtl.write("Ns 20.000000\n")
        f_mtl.write("d 1.000000\n")
        f_mtl.write("illum 2\n")
        if diffuse_png_filename:
            f_mtl.write(f"map_Kd {diffuse_png_filename}\n")
        if normal_png_filename:
            f_mtl.write(f"map_Bump -bm 1.0 {normal_png_filename}\n")

    logger.info("Exported MTL material: %s", mtl_path)

    # 3. Write Wavefront OBJ file
    # HoN coordinates: X right, Y forward, Z up.
    # Mixamo / Wavefront OBJ coordinates: X right, Y up, Z forward (right-handed).
    # Transformation: X_obj = X_hon, Y_obj = Z_hon, Z_obj = -Y_hon.
    # UV mapping: U_obj = U_hon, V_obj = 1.0 - V_hon.
    # Normals: NX_obj = NX_hon, NY_obj = NZ_hon, NZ_obj = -NY_hon.
    # Triangle winding: (i0, i2, i1) due to coordinate reflection.
    # Structure matching Adobe Mixamo reference working models:
    #   Header -> o <name> -> all v -> all vt -> all vn -> usemtl -> s 1 -> all f
    with open(obj_path, "w", encoding="utf-8") as f_obj:
        f_obj.write(f"# Mixamo Compatible OBJ Export for {base_name}\n")
        f_obj.write(f"mtllib {mtl_filename}\n\n")
        f_obj.write(f"o {base_name}\n\n")

        # 1. Write all vertices
        total_verts = 0
        for mesh in model.meshes:
            for v in mesh.vertices:
                x_hon, y_hon, z_hon = v[0], v[1], v[2]
                x_obj = x_hon
                y_obj = z_hon
                z_obj = -y_hon
                f_obj.write(f"v {x_obj:.6f} {y_obj:.6f} {z_obj:.6f}\n")
            total_verts += len(mesh.vertices)
        f_obj.write("\n")

        # 2. Write all texture coordinates (UVs)
        for mesh in model.meshes:
            if len(mesh.texcoords) == len(mesh.vertices):
                for uv in mesh.texcoords:
                    u_obj = uv[0]
                    v_obj = 1.0 - uv[1]
                    f_obj.write(f"vt {u_obj:.6f} {v_obj:.6f}\n")
            else:
                for _ in mesh.vertices:
                    f_obj.write("vt 0.000000 0.000000\n")
        f_obj.write("\n")

        # 3. Write all normals
        for mesh in model.meshes:
            if len(mesh.normals) == len(mesh.vertices):
                for n in mesh.normals:
                    nx_hon, ny_hon, nz_hon = n[0], n[1], n[2]
                    nx_obj = nx_hon
                    ny_obj = nz_hon
                    nz_obj = -ny_hon
                    f_obj.write(f"vn {nx_obj:.6f} {ny_obj:.6f} {nz_obj:.6f}\n")
            else:
                for _ in mesh.vertices:
                    f_obj.write("vn 0.000000 1.000000 0.000000\n")
        f_obj.write("\n")

        # 4. Material and smooth shading
        f_obj.write(f"usemtl {mat_name}\n")
        f_obj.write("s 1\n\n")

        # 5. Write all faces with 1-based indexing and reversed winding (i0, i2, i1)
        v_offset = 0
        total_faces = 0
        for mesh in model.meshes:
            num_indices = len(mesh.indices)
            for i in range(0, num_indices - 2, 3):
                i0 = mesh.indices[i] + 1 + v_offset
                i1 = mesh.indices[i + 1] + 1 + v_offset
                i2 = mesh.indices[i + 2] + 1 + v_offset
                f_obj.write(f"f {i0}/{i0}/{i0} {i2}/{i2}/{i2} {i1}/{i1}/{i1}\n")
                total_faces += 1
            v_offset += len(mesh.vertices)

    logger.info("Exported OBJ mesh: %s (%d vertices, %d faces across %d meshes)",
                obj_path, total_verts, total_faces, len(model.meshes))

    return obj_path, mtl_path, texture_paths


def export_hon_to_mixamo_zip(
    model: HonModelData,
    output_zip_path: str,
    materials: Optional[List[HonMaterialData]] = None,
    base_name: Optional[str] = None,
    strip_accessories: bool = True
) -> str:
    """
    Exports a HoN model to Wavefront OBJ + MTL + PNG textures, and packages
    them directly into a standard Mixamo-ready ZIP archive.

    Args:
        model: Parsed HonModelData containing geometry meshes.
        output_zip_path: Target destination file path for the .zip archive.
        materials: Resolved HonMaterialData with decoded texture PNG bytes.
        base_name: Optional clean identifier name for the character.
        strip_accessories: When True (default), strips weapons and floating props so Mixamo can auto-rig cleanly.

    Returns:
        The absolute path to the generated Mixamo ZIP file.
    """
    output_zip_path = os.path.abspath(output_zip_path)
    zip_dir = os.path.dirname(output_zip_path)
    os.makedirs(zip_dir, exist_ok=True)

    if not base_name:
        zip_filename = os.path.basename(output_zip_path)
        base_name = os.path.splitext(zip_filename)[0]
        # Strip _mixamo_upload or _mixamo suffix if present
        for sfx in ("_mixamo_upload", "_mixamo", "_upload"):
            if base_name.lower().endswith(sfx):
                base_name = base_name[:-len(sfx)]
                break

    # Export OBJ, MTL, and textures into the same folder
    obj_p, mtl_p, tex_ps = export_hon_to_obj_and_mtl(
        model=model,
        base_name=base_name,
        output_dir=zip_dir,
        materials=materials,
        strip_accessories=strip_accessories
    )

    # Package files into ZIP archive (root level, no nested directories)
    with zipfile.ZipFile(output_zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.write(obj_p, arcname=os.path.basename(obj_p))
        zf.write(mtl_p, arcname=os.path.basename(mtl_p))
        for tp in tex_ps:
            zf.write(tp, arcname=os.path.basename(tp))

    logger.info("SUCCESS: Created Mixamo-ready ZIP archive: %s (%d bytes)",
                output_zip_path, os.path.getsize(output_zip_path))
    return output_zip_path
