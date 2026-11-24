# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Heroes of Newerth Alt Avatar Metadata & Natural Language Name Resolver.
Discovers and maps every Alt Avatar skin (e.g. Thriller Rhapsody, Clown Devourer,
Frost Pyromancer, Bunny Valkyrie) to its exact model directory path.
"""

from __future__ import annotations

import os
import re
import xml.etree.ElementTree as ET
from typing import Any, Dict, List, Optional, Tuple

# Comprehensive curated mapping of iconic HoN alt avatar titles & keywords
CURATED_AVATAR_TITLES: Dict[str, Dict[str, Any]] = {
    # Rhapsody
    "rhapsody/alt": {
        "title": "Thriller Rhapsody",
        "aliases": ["thriller", "thriller rhapsody", "thriller_rhapsody", "rhapsody_alt", "rhapsody alt", "alt1"]
    },
    "rhapsody/alt2": {
        "title": "Disco Diva Rhapsody",
        "aliases": ["disco", "disco rhapsody", "disco diva", "rhapsody_alt2", "rhapsody alt 2", "alt2"]
    },
    "rhapsody/alt3": {
        "title": "Corsair Pirate Rhapsody",
        "aliases": ["corsair", "pirate rhapsody", "corsair rhapsody", "rhapsody_alt3", "rhapsody alt 3", "alt3"]
    },
    "rhapsody/alt4": {
        "title": "Pop Idol Rhapsody",
        "aliases": ["pop idol", "kpop rhapsody", "idol rhapsody", "rhapsody_alt4", "rhapsody alt 4", "alt4"]
    },
    "rhapsody/alt5": {
        "title": "8-Bit Arcade Rhapsody",
        "aliases": ["8bit", "8-bit", "arcade rhapsody", "8bit rhapsody", "rhapsody_alt5", "rhapsody alt 5", "alt5"]
    },
    "rhapsody/alt6": {
        "title": "Siren Rhapsody",
        "aliases": ["siren", "siren rhapsody", "rhapsody_alt6", "rhapsody alt 6", "alt6"]
    },
    "rhapsody/alt7": {
        "title": "Starlight Rhapsody",
        "aliases": ["starlight", "starlight rhapsody", "rhapsody_alt7", "rhapsody alt 7", "alt7"]
    },
    "rhapsody/alt8": {
        "title": "Valentine Rhapsody",
        "aliases": ["valentine", "valentine rhapsody", "rhapsody_alt8", "rhapsody alt 8", "alt8"]
    },
    "rhapsody/alt9": {
        "title": "Cyberpunk Rhapsody",
        "aliases": ["cyberpunk", "cyber rhapsody", "rhapsody_alt9", "rhapsody alt 9", "alt9"]
    },
    "rhapsody/alt10": {
        "title": "Gothic Rhapsody",
        "aliases": ["gothic", "goth rhapsody", "rhapsody_alt10", "rhapsody alt 10", "alt10"]
    },
    "rhapsody/hd_rhapsody": {
        "title": "HD High-Poly Rhapsody",
        "aliases": ["hd rhapsody", "hd_rhapsody", "high poly rhapsody"]
    },

    # Devourer
    "devourer/alt": {
        "title": "Clown Devourer",
        "aliases": ["clown", "clown devourer", "clown_devourer", "devourer_alt", "devourer alt"]
    },
    "devourer/alt2": {
        "title": "Chef Devourer",
        "aliases": ["chef", "chef devourer", "chef_devourer", "devourer_alt2", "devourer alt 2"]
    },
    "devourer/alt3": {
        "title": "Undead Butcher Devourer",
        "aliases": ["undead", "butcher", "pudge", "undead devourer", "devourer_alt3", "devourer alt 3"]
    },
    "devourer/alt4": {
        "title": "Robo Devourer",
        "aliases": ["robo", "mecha devourer", "robot devourer", "devourer_alt4"]
    },

    # Pyromancer
    "pyromancer/alt": {
        "title": "Frost Pyromancer",
        "aliases": ["frost", "frost pyromancer", "frost_pyromancer", "pyromancer_alt", "ice pyromancer"]
    },
    "pyromancer/alt2": {
        "title": "Infernal Dragon Pyromancer",
        "aliases": ["infernal", "infernal pyromancer", "dragon pyromancer", "pyromancer_alt2"]
    },

    # Valkyrie
    "valkyrie/alt": {
        "title": "Battle Valkyrie",
        "aliases": ["battle valkyrie", "valkyrie_alt"]
    },
    "valkyrie/alt2": {
        "title": "Bunny Valkyrie",
        "aliases": ["bunny", "bunny valkyrie", "playboy valkyrie", "valkyrie_alt2"]
    },

    # Empath
    "empath/alt": {
        "title": "Dark Empath",
        "aliases": ["dark empath", "empath_alt", "shadow empath"]
    },
    "empath/alt2": {
        "title": "Cupid Empath",
        "aliases": ["cupid", "cupid empath", "love empath", "empath_alt2"]
    },

    # Calamity
    "calamity/alt": {
        "title": "Black Orchid Calamity",
        "aliases": ["black orchid", "orchid calamity", "calamity_alt"]
    },
    "calamity/alt8": {
        "title": "Ravenous Calamity",
        "aliases": ["ravenous", "ravenous calamity", "calamity_alt8"]
    }
}


def get_avatar_display_name(target_key: str) -> str:
    """Returns the human-readable display name for a given target ID (e.g. rhapsody/alt -> Thriller Rhapsody)."""
    norm = target_key.replace("\\", "/").strip().lower()
    if norm in CURATED_AVATAR_TITLES:
        return CURATED_AVATAR_TITLES[norm]["title"]

    parts = norm.split("/")
    if len(parts) == 2:
        hero, sub = parts
        hero_title = hero.replace("_", " ").title()
        if sub.startswith("alt"):
            num = sub[3:] or "1"
            return f"{hero_title} (Alt Avatar {num})"
        if sub.startswith("hd"):
            return f"HD {hero_title}"
        return f"{hero_title} ({sub.title()})"

    return target_key.replace("_", " ").title()


def resolve_avatar_query(query: str, repo_root: Optional[str] = None) -> Optional[Tuple[str, str, str]]:
    """
    Resolves natural language queries (e.g. 'thriller rhapsody', 'clown devourer', 'rhapsody/alt')
    to (Relative_Directory, Target_Key, Display_Title).
    """
    if not query:
        return None

    q = query.strip().lower().replace("_", " ")

    def _make_rel_path(key: str) -> str:
        if repo_root:
            shared_p = os.path.join(repo_root, "games", "shared", "heroes", key)
            if os.path.exists(shared_p):
                return f"games/shared/heroes/{key}"
            client_p = os.path.join(repo_root, "games", "client", "game", "heroes", key)
            if os.path.exists(client_p):
                return f"games/client/game/heroes/{key}"
        elif os.path.exists(os.path.join("games", "client", "game", "heroes", key)):
            return f"games/client/game/heroes/{key}"
        return f"games/shared/heroes/{key}"

    # 1. Exact match against curated aliases
    for key, data in CURATED_AVATAR_TITLES.items():
        title = data["title"]
        aliases = data["aliases"]
        if q == key or q in aliases or q == title.lower():
            rel_path = _make_rel_path(key)
            return rel_path, key, title

    # 2. Substring matching in aliases
    for key, data in CURATED_AVATAR_TITLES.items():
        for a in data["aliases"]:
            if a in q or q in a:
                rel_path = _make_rel_path(key)
                return rel_path, key, data["title"]

    # 3. Dynamic hero alt pattern (e.g. 'rhapsody alt 2', 'pyromancer alt')
    m = re.search(r'([a-z_]+)\s*(?:alt|avatar|skin)\s*(\d*)', q)
    if m:
        hero = m.group(1).replace(" ", "_")
        num = m.group(2)
        sub = f"alt{num}" if num and int(num) > 1 else "alt"
        key = f"{hero}/{sub}"
        title = get_avatar_display_name(key)
        rel_path = _make_rel_path(key)
        return rel_path, key, title

    return None
