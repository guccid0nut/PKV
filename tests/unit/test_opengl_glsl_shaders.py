# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
protects: OpenGL GLSL shader compilation, linker varying contracts, and FILE_NOARCHIVES loose-disk parity across Linux/macOS (vid_gl2).
fails_when: Any OpenGL shader in override/ or ClientPackageFiles/ retains HLSL tokens (In.Normal, fmod, .0f, clip, FogD, gl_mWorldViewProj), fails glslangValidator across LIGHTING/AMBIENT_OCCLUSION/LIGHTING_QUALITY permutations, or drifts between override/ and ClientPackageFiles/.
seam: Real core/shaders/{ps_glsl,vs_glsl} files on disk + Khronos glslangValidator compiler/linker when present.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
GAME_SHADERS = REPO_ROOT / "games" / "client" / "game" / "core" / "shaders"
OVERRIDE_SHADERS = REPO_ROOT / "games" / "client" / "override" / "core" / "shaders"
CPF_SHADERS = REPO_ROOT / "games" / "client" / "ClientPackageFiles" / "core" / "shaders"

EXPECTED_PS_OVERRIDES = [
    "effect_energy.psh",
    "effect_fire_gradient_trail.psh",
    "effect_overlay_fire_overlaymask.psh",
    "effect_refract_reveal.psh",
    "effect_water2.psh",
    "mesh_color_enviro_reflect.psh",
    "mesh_color_enviro_unit_fresnel_gradient.psh",
    "mesh_color_enviro_unit_lightmap_spec_reflectmask4_uvanim_maskblend.psh",
    "mesh_color_enviro_unit_scroll_reflect.psh",
    "mesh_color_enviro_unit_team_spec_reflectmask4_glow.psh",
    "mesh_color_ice3.psh",
    "mesh_color_ice4.psh",
    "mesh_color_lightmap.psh",
    "mesh_color_trophy_mask.psh",
    "mesh_color_unit_diffuse_scroll.psh",
    "mesh_color_unit_flat.psh",
    "mesh_color_unit_lightmap.psh",
    "mesh_color_unit_reflect_to_diffuse.psh",
    "mesh_color_unit_reveal.psh",
    "mesh_color_unit_team.psh",
    "mesh_color_unit_team_dissolveup.psh",
    "mesh_color_unit_team_lightmap.psh",
    "mesh_color_unit_team_lightmap_iceblend.psh",
    "mesh_color_water5.psh",
    "mesh_color_water7.psh",
    "mesh_refract.psh",
    "mesh_reveal_gradient.psh",
    "mesh_reveal_gradient_slimebubble.psh",
    "mesh_unlit_lightning.psh",
    "terrain_water.psh",
    "terrain_water_directional.psh",
]

EXPECTED_VS_OVERRIDES = [
    "mesh_color_orb.vsh",
    "mesh_color_unit_flat.vsh",
    "mesh_color_unit_liquid_to_solid.vsh",
    "mesh_color_water.vsh",
    "mesh_color_water5_low.vsh",
    "mesh_refract_scroll.vsh",
]

FORBIDDEN_HLSL_PATTERNS = [
    (re.compile(r"\bIn\.\w+"), "HLSL struct input 'In.<field>'"),
    (re.compile(r"\bOut\.\w+"), "HLSL struct output 'Out.<field>'"),
    (re.compile(r"\bfmod\s*\("), "HLSL 'fmod(' instead of GLSL 'mod('"),
    (re.compile(r"\bclip\s*\("), "HLSL 'clip(' instead of GLSL 'discard'"),
    (re.compile(r"\bFogD?\s*\("), "HLSL 'Fog(' / 'FogD(' helper"),
    (re.compile(r"\bgl_mWorldViewProj\b"), "Typo 'gl_mWorldViewProj' instead of 'mWorldViewProj'"),
    (re.compile(r"\b(?:float2|float3|float4|float3x3|float4x4|half3|half4|tex2D|texCUBE|lerp|saturate)\b"), "HLSL intrinsic/type"),
    (re.compile(r"\d+\.\d+f\b"), "HLSL float suffix '.0f' (invalid in GLSL #version 120)"),
]


def _strip_comments(src: str) -> str:
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


def _find_glslang_validator() -> str | None:
    on_path = shutil.which("glslangValidator")
    if on_path:
        return on_path
    candidates = [
        r"C:\Program Files\RenderDoc\plugins\spirv\glslangValidator.exe",
    ]
    for candidate in candidates:
        if os.path.isfile(candidate):
            return candidate
    return None


def _resolve_shader(subdir: str, filename: str) -> Path:
    override_path = OVERRIDE_SHADERS / subdir / filename
    if override_path.is_file():
        return override_path
    return GAME_SHADERS / subdir / filename


def test_loose_shader_parity_between_override_and_client_package_files() -> None:
    for name in EXPECTED_PS_OVERRIDES:
        ov = OVERRIDE_SHADERS / "ps_glsl" / name
        cpf = CPF_SHADERS / "ps_glsl" / name
        assert ov.is_file(), f"Missing loose PS shader {ov}; run Copy-Item to games/client/override/core/shaders/ps_glsl/{name}"
        assert cpf.is_file(), f"Missing mirrored PS shader {cpf}; mirror override/core/shaders/ps_glsl/{name} to ClientPackageFiles/"
        assert ov.read_bytes() == cpf.read_bytes(), f"Drift between {ov} and {cpf}"

    for name in EXPECTED_VS_OVERRIDES:
        ov = OVERRIDE_SHADERS / "vs_glsl" / name
        cpf = CPF_SHADERS / "vs_glsl" / name
        assert ov.is_file(), f"Missing loose VS shader {ov}; run Copy-Item to games/client/override/core/shaders/vs_glsl/{name}"
        assert cpf.is_file(), f"Missing mirrored VS shader {cpf}; mirror override/core/shaders/vs_glsl/{name} to ClientPackageFiles/"
        assert ov.read_bytes() == cpf.read_bytes(), f"Drift between {ov} and {cpf}"


def test_no_hlsl_tokens_in_resolved_opengl_shaders() -> None:
    offenders: list[str] = []
    for subdir, ext in (("ps_glsl", ".psh"), ("vs_glsl", ".vsh")):
        names = {p.name for p in (GAME_SHADERS / subdir).glob(f"*{ext}")} | {
            p.name for p in (OVERRIDE_SHADERS / subdir).glob(f"*{ext}")
        }
        for name in sorted(names):
            shader_path = _resolve_shader(subdir, name)
            cleaned = _strip_comments(shader_path.read_text(encoding="utf-8", errors="replace"))
            for regex, desc in FORBIDDEN_HLSL_PATTERNS:
                match = regex.search(cleaned)
                if match:
                    offenders.append(f"{subdir}/{name}: {desc} ({match.group(0)!r}) in {shader_path}")
    assert not offenders, "HLSL tokens detected in resolved OpenGL GLSL shaders:\n" + "\n".join(offenders)


def test_glslang_validator_compiles_and_links_all_override_shaders() -> None:
    validator = _find_glslang_validator()
    if not validator:
        pytest.skip("glslangValidator not installed on this runner")

    ps_configs = [
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 0", "#define SHADOWS 1", "#define SHADOWMAP_TYPE 0", "#define SHADOWMAP_FILTER_WIDTH 1", "#define FOG_TYPE 1", "#define FOG_QUALITY 0", "#define FALLOFF_QUALITY 0", "#define FOG_OF_WAR 1", "#define AMBIENT_OCCLUSION 1", "#define NUM_POINT_LIGHTS 1", "#define TEXKILL 1", "#define RXGB_NORMALMAP 1", "#define SPECULAR_LOOKUP 1", "#define CLOUDS 1"],
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 0", "#define SHADOWS 1", "#define SHADOWMAP_TYPE 1", "#define SHADOWMAP_FILTER_WIDTH 0", "#define FOG_TYPE 1", "#define FOG_QUALITY 0", "#define FALLOFF_QUALITY 0", "#define FOG_OF_WAR 0", "#define NUM_POINT_LIGHTS 0", "#define TEXKILL 1"],
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 1", "#define SHADOWS 0", "#define FOG_TYPE 1", "#define FOG_QUALITY 1", "#define FALLOFF_QUALITY 1", "#define FOG_OF_WAR 1", "#define AMBIENT_OCCLUSION 1", "#define NUM_POINT_LIGHTS 0", "#define TEXKILL 0"],
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 2", "#define SHADOWS 1", "#define SHADOWMAP_TYPE 0", "#define SHADOWMAP_FILTER_WIDTH 1", "#define FOG_TYPE 1", "#define FOG_QUALITY 1", "#define FALLOFF_QUALITY 1", "#define FOG_OF_WAR 1", "#define AMBIENT_OCCLUSION 1", "#define NUM_POINT_LIGHTS 0", "#define GROUND_AMBIENT 1"],
        ["#version 120", "#define LIGHTING 0", "#define LIGHTING_QUALITY 0", "#define SHADOWS 0", "#define FOG_TYPE 0", "#define FOG_QUALITY 0", "#define FALLOFF_QUALITY 0", "#define FOG_OF_WAR 0", "#define NUM_POINT_LIGHTS 0"],
    ]
    vs_configs = [
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 0", "#define SHADOWS 1", "#define FOG_TYPE 1", "#define FOG_QUALITY 0", "#define FALLOFF_QUALITY 0", "#define FOG_OF_WAR 1", "#define WATER_QUALITY 0", "#define NUM_BONES 4", "#define NUM_BONE_WEIGHTS 4", "#define CLOUDS 1", "#define TEXCOORDS 1"],
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 2", "#define SHADOWS 0", "#define FOG_TYPE 1", "#define FOG_QUALITY 1", "#define FALLOFF_QUALITY 1", "#define FOG_OF_WAR 0", "#define WATER_QUALITY 3", "#define NUM_BONES 0", "#define NUM_BONE_WEIGHTS 0", "#define GROUND_AMBIENT 1", "#define TEXCOORDS 1"],
    ]
    link_pairs = [
        ("meshc_color_water5.vsh", "mesh_color_water7.psh"),
        ("mesh_color_water5_low.vsh", "mesh_color_water7.psh"),
        ("meshc_color_water5.vsh", "mesh_color_water5.psh"),
        ("mesh_color_unit_scene.vsh", "mesh_color_unit_team_lightmap_iceblend.psh"),
        ("effect_refract.vsh", "effect_refract_reveal.psh"),
        ("mesh_refract_scroll.vsh", "mesh_refract.psh"),
        ("mesh_color_unit_flat.vsh", "mesh_color_unit_flat.psh"),
        ("mesh_color_orb.vsh", "mesh_color_orb.psh"),
        ("mesh_color_unit_liquid_to_solid.vsh", "mesh_color_unit_reflect_to_diffuse.psh"),
        ("mesh_color_water.vsh", "mesh_color_water.psh"),
    ]
    link_configs = [
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 0", "#define SHADOWS 1", "#define SHADOWMAP_TYPE 0", "#define SHADOWMAP_FILTER_WIDTH 1", "#define FOG_TYPE 1", "#define FOG_QUALITY 0", "#define FALLOFF_QUALITY 0", "#define FOG_OF_WAR 1", "#define WATER_QUALITY 0", "#define AMBIENT_OCCLUSION 1", "#define NUM_POINT_LIGHTS 1", "#define NUM_BONES 4", "#define NUM_BONE_WEIGHTS 4"],
        ["#version 120", "#define LIGHTING 1", "#define LIGHTING_QUALITY 2", "#define SHADOWS 0", "#define FOG_TYPE 1", "#define FOG_QUALITY 1", "#define FALLOFF_QUALITY 1", "#define FOG_OF_WAR 0", "#define WATER_QUALITY 3", "#define NUM_POINT_LIGHTS 0", "#define NUM_BONES 0", "#define NUM_BONE_WEIGHTS 0"],
    ]

    failures: list[str] = []
    with tempfile.TemporaryDirectory() as td:
        td_path = Path(td)
        for name in EXPECTED_PS_OVERRIDES:
            body = _resolve_shader("ps_glsl", name).read_text(encoding="utf-8")
            for idx, cfg in enumerate(ps_configs):
                frag = td_path / "test.frag"
                frag.write_text("\n".join(cfg) + "\n#line 1\n" + body, encoding="utf-8")
                res = subprocess.run([validator, str(frag)], capture_output=True, text=True, check=False)
                if res.returncode != 0:
                    failures.append(f"PS {name} cfg#{idx}: {res.stdout.strip()}")
        for name in EXPECTED_VS_OVERRIDES:
            body = _resolve_shader("vs_glsl", name).read_text(encoding="utf-8")
            for idx, cfg in enumerate(vs_configs):
                vert = td_path / "test.vert"
                vert.write_text("\n".join(cfg) + "\n#line 1\n" + body, encoding="utf-8")
                res = subprocess.run([validator, str(vert)], capture_output=True, text=True, check=False)
                if res.returncode != 0:
                    failures.append(f"VS {name} cfg#{idx}: {res.stdout.strip()}")
        for vs_name, ps_name in link_pairs:
            vs_body = _resolve_shader("vs_glsl", vs_name).read_text(encoding="utf-8")
            ps_body = _resolve_shader("ps_glsl", ps_name).read_text(encoding="utf-8")
            for idx, cfg in enumerate(link_configs):
                vert = td_path / "link.vert"
                frag = td_path / "link.frag"
                vert.write_text("\n".join(cfg) + "\n#line 1\n" + vs_body, encoding="utf-8")
                frag.write_text("\n".join(cfg) + "\n#line 1\n" + ps_body, encoding="utf-8")
                res = subprocess.run([validator, "-l", str(vert), str(frag)], capture_output=True, text=True, check=False)
                if res.returncode != 0:
                    failures.append(f"LINK {vs_name}+{ps_name} cfg#{idx}: {(res.stdout + ' ' + res.stderr).strip()}")

    assert not failures, "glslangValidator failures (fix in games/client/override/core/shaders/):\n" + "\n".join(failures)
