# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
ProjectKongor Cinematic Tools Package
Provides modular production tools for audio timeline parsing, skeletal choreography,
cinematography direction, 5-point studio lighting, PBR materials, secondary physics,
and headless rendering.
"""

from .quaternion_math import (
    normalize_quat,
    euler_to_quat,
    quat_to_euler,
    quat_multiply,
    quat_slerp,
    quat_angular_distance_deg,
    hermite_smoothstep,
    cosine_smoothstep,
    lerp_vec,
)
from .k2_clip_decoder import (
    K2Bone,
    K2Model,
    K2ClipChannel,
    K2Clip,
    ClipLibrary,
)
from .stage_trajectories import (
    TrajectoryEngine,
    bezier_cubic,
    bezier_cubic_derivative,
)
from .secondary_physics import (
    SecondaryChain,
    SecondaryDynamicsEngine,
)
from .vocal_timeline_parser import (
    VocalTimelineParser,
    generate_timeline,
    CANONICAL_SECTIONS,
    CANONICAL_SCREAM_TRIGGERS,
)
from .choreography_engine import (
    ChoreographyEngine,
    generate_choreography_dataset,
    MIXAMO_TO_HON_BONE_MAP,
    SECTION_TIMELINE,
)
from .cinematography_director import (
    CinematographyDirector,
    CANONICAL_SHOTS,
    CANONICAL_CUT_FRAMES,
    CANONICAL_CUT_PAIRS,
    SCREAM_DROP_ONSETS,
)
from .lighting_director import (
    LightingDirector,
    LIGHT_RIG_SPECS,
    kelvin_to_linear_rgb,
)
from .pbr_material_setup import (
    setup_all_pbr_materials,
)
from .production_render_pipeline import (
    ProductionRenderPipeline,
    configure_blender_scene,
    verify_rendered_deliverables,
)

__all__ = [
    "normalize_quat",
    "euler_to_quat",
    "quat_to_euler",
    "quat_multiply",
    "quat_slerp",
    "quat_angular_distance_deg",
    "hermite_smoothstep",
    "cosine_smoothstep",
    "lerp_vec",
    "K2Bone",
    "K2Model",
    "K2ClipChannel",
    "K2Clip",
    "ClipLibrary",
    "TrajectoryEngine",
    "bezier_cubic",
    "bezier_cubic_derivative",
    "SecondaryChain",
    "SecondaryDynamicsEngine",
    "VocalTimelineParser",
    "generate_timeline",
    "CANONICAL_SECTIONS",
    "CANONICAL_SCREAM_TRIGGERS",
    "ChoreographyEngine",
    "generate_choreography_dataset",
    "MIXAMO_TO_HON_BONE_MAP",
    "SECTION_TIMELINE",
    "CinematographyDirector",
    "CANONICAL_SHOTS",
    "CANONICAL_CUT_FRAMES",
    "CANONICAL_CUT_PAIRS",
    "SCREAM_DROP_ONSETS",
    "LightingDirector",
    "LIGHT_RIG_SPECS",
    "kelvin_to_linear_rgb",
    "setup_all_pbr_materials",
    "ProductionRenderPipeline",
    "configure_blender_scene",
    "verify_rendered_deliverables",
]
