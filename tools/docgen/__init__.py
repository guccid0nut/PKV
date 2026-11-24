# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
ProjectKongor Docgen Tools Package
Provides diagram and architecture SVG generators for repository documentation.
"""

from .generate_charts import (
    create_decision_funnel_svg,
    create_ecosystem_architecture_svg,
)
from .generate_sub_charts import (
    create_master_server_architecture_svg,
    create_game_loop_architecture_svg,
    create_mod_pipeline_architecture_svg,
    create_replay_renderer_architecture_svg,
    create_combat_juice_architecture_svg,
)

__all__ = [
    "create_decision_funnel_svg",
    "create_ecosystem_architecture_svg",
    "create_master_server_architecture_svg",
    "create_game_loop_architecture_svg",
    "create_mod_pipeline_architecture_svg",
    "create_replay_renderer_architecture_svg",
    "create_combat_juice_architecture_svg",
]
