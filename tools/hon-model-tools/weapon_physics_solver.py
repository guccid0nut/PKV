# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Dynamic Weapon Physics Solver & Cyclic Hermite Smoothing System.
Part of ProjectKongor Model & Animation Toolchain.
"""

from __future__ import annotations

from enum import Enum
import math
from typing import Any, Dict, List, Optional, Sequence, Tuple, Union
import numpy as np


class WeaponArchetype(str, Enum):
    """Canonical 6 weapon archetypes extracted from 130+ HoN heroes."""
    STAFF_POLEARM = "staff_polearm"
    ONE_HAND_BLADE = "one_handed_blade"
    CHAIN_WHIP = "chain_whip"
    DUAL_WIELD = "dual_wield"
    BOW_LAUNCHER = "bow_launcher"
    UNARMED = "unarmed"

    @classmethod
    def from_string(cls, val: Union[str, "WeaponArchetype"]) -> "WeaponArchetype":
        if isinstance(val, cls):
            return val
        s = str(val).lower().strip().replace(" ", "_").replace("/", "_").replace("-", "_")
        aliases = {
            "staff": cls.STAFF_POLEARM, "polearm": cls.STAFF_POLEARM, "staves": cls.STAFF_POLEARM,
            "staves_polearms": cls.STAFF_POLEARM, "staff_polearm": cls.STAFF_POLEARM, "spear": cls.STAFF_POLEARM,
            "blade": cls.ONE_HAND_BLADE, "sword": cls.ONE_HAND_BLADE, "axe": cls.ONE_HAND_BLADE,
            "1h": cls.ONE_HAND_BLADE, "1h_blade": cls.ONE_HAND_BLADE, "1h_blades_axes": cls.ONE_HAND_BLADE,
            "one_handed_blade": cls.ONE_HAND_BLADE, "mace": cls.ONE_HAND_BLADE, "dagger": cls.ONE_HAND_BLADE,
            "chain": cls.CHAIN_WHIP, "whip": cls.CHAIN_WHIP, "flail": cls.CHAIN_WHIP,
            "chains_whips": cls.CHAIN_WHIP, "chain_whip": cls.CHAIN_WHIP, "scythe": cls.CHAIN_WHIP,
            "dual": cls.DUAL_WIELD, "dual_wield": cls.DUAL_WIELD, "dual_wielding": cls.DUAL_WIELD, "paired": cls.DUAL_WIELD,
            "bow": cls.BOW_LAUNCHER, "launcher": cls.BOW_LAUNCHER, "gun": cls.BOW_LAUNCHER,
            "crossbow": cls.BOW_LAUNCHER, "bows_launchers": cls.BOW_LAUNCHER, "bow_launcher": cls.BOW_LAUNCHER, "rifle": cls.BOW_LAUNCHER,
            "unarmed": cls.UNARMED, "fist": cls.UNARMED, "brawler": cls.UNARMED, "none": cls.UNARMED
        }
        if s in aliases:
            return aliases[s]
        for candidate in cls:
            if candidate.value == s or candidate.name.lower() == s:
                return candidate
        raise ValueError(f"Unknown weapon archetype: {val}")


class SocketBinding(tuple):
    def __new__(cls, primary: str, secondary: Optional[str] = None):
        return super(SocketBinding, cls).__new__(cls, (primary, secondary))

    @property
    def primary(self) -> str:
        return self[0]

    @property
    def secondary(self) -> Optional[str]:
        return self[1]

    def to_dict(self) -> Dict[str, Optional[str]]:
        return {"primary": self.primary, "secondary": self.secondary}


def clamp_angle(deg: float, min_deg: float = -90.0, max_deg: float = 90.0) -> float:
    return max(min_deg, min(max_deg, float(deg)))

def simulate_weapon_motion(
    archetype: Union[str, WeaponArchetype],
    motion_type: str,
    frame: int,
    total_frames: int,
    fps: float = 30.0,
    hand: str = "right"
) -> Tuple[float, float, float]:
    """
    Computes procedural rotation offset (rx, ry, rz in degrees) for weapon socket.
    Produces authentic physical inertia, whip dynamics, drawback tension, and settle recoil.
    """
    arch = WeaponArchetype.from_string(archetype)
    mtype = motion_type.lower().strip()
    total_f = max(2, total_frames)
    f = max(0, min(total_f - 1, frame))
    t = f / float(total_f - 1)

    rx, ry, rz = 0.0, 0.0, 0.0

    if arch == WeaponArchetype.STAFF_POLEARM:
        if mtype in ("idle", "portrait", "default_1"):
            rx = math.sin(t * 2.0 * math.pi) * 0.8
            rz = math.cos(t * 2.0 * math.pi) * 0.4
        elif mtype in ("walk", "walk_1", "run"):
            rx = math.sin(t * 4.0 * math.pi) * 8.0
            ry = math.cos(t * 4.0 * math.pi) * 2.5
            rz = math.sin(t * 4.0 * math.pi) * 4.0
        elif mtype in ("attack_1", "swipe_spin", "attack"):
            if t <= 0.38:
                p = t / 0.38
                rx = -35.0 * p
                ry = -10.0 * p
            elif t <= 0.65:
                p = (t - 0.38) / 0.27
                rx = -35.0 + 90.0 * p + math.sin(p * math.pi) * 15.0
                ry = -10.0 + 20.0 * p
                rz = -12.0 * math.sin(p * math.pi)
            else:
                p = (t - 0.65) / 0.35
                recoil = math.exp(-p * 5.0) * math.cos(p * 6.0 * math.pi) * 20.0
                rx = recoil
                ry = recoil * 0.2
        elif mtype in ("attack_2", "swipe_cleave"):
            if t <= 0.35:
                rx = -45.0 * (t / 0.35)
            elif t <= 0.60:
                p = (t - 0.35) / 0.25
                rx = -45.0 + 105.0 * p
            else:
                p = (t - 0.60) / 0.40
                rx = 60.0 * math.exp(-p * 6.0) * math.cos(p * 8.0 * math.pi)
        elif mtype in ("ability_1", "hook_throw"):
            if t <= 0.30:
                rx = -30.0 * (t / 0.30)
            elif t <= 0.55:
                p = (t - 0.30) / 0.25
                rx = -30.0 + 95.0 * p
            else:
                p = (t - 0.55) / 0.45
                rx = 65.0 * math.exp(-p * 5.0) * math.cos(p * 5.0 * math.pi)
        elif mtype in ("ability_2", "crouch_dash"):
            phase = math.sin(t * 2.0 * math.pi)
            rx = -15.0 + phase * 4.0
            rz = phase * 3.0
        elif mtype in ("ability_3", "cast_empower"):
            if t <= 0.33:
                rx = -40.0 * (t / 0.33)
            elif t <= 0.66:
                rx = -40.0 + math.sin(t * 12.0 * math.pi) * 6.0
            else:
                rx = -40.0 * (1.0 - (t - 0.66) / 0.34)
        elif mtype in ("ability_4", "jump_slam"):
            if t <= 0.40:
                rx = -50.0 * (t / 0.40)
            elif t <= 0.65:
                rx = -50.0 + 125.0 * ((t - 0.40) / 0.25)
            else:
                p = (t - 0.65) / 0.35
                rx = 75.0 * math.exp(-p * 6.0) * math.cos(p * 6.0 * math.pi)
        elif mtype == "death":
            rx = min(1.0, t * 2.0) * 50.0
            rz = min(1.0, t * 2.0) * 20.0
        elif mtype in ("bored", "bored_1", "twitch"):
            tw = math.sin(t * 8.0 * math.pi) * 6.0
            rx, ry = tw * 0.5, tw
        elif mtype in ("taunt", "taunt_1", "bow"):
            rx = (t / 0.40) * -35.0 if t <= 0.40 else (-35.0 if t <= 0.70 else (1.0 - (t - 0.70) / 0.30) * -35.0)

    elif arch == WeaponArchetype.ONE_HAND_BLADE:
        if mtype in ("idle", "portrait", "default_1"):
            rx, ry, rz = 0.0, 0.0, 0.0
        elif mtype in ("walk", "walk_1", "run"):
            rx = math.sin(t * 4.0 * math.pi) * 3.5
            ry = math.cos(t * 4.0 * math.pi) * 1.5
        elif mtype in ("attack_1", "swipe_spin", "attack"):
            if t <= 0.35:
                rx = -25.0 * (t / 0.35)
                ry = 5.0 * (t / 0.35)
            elif t <= 0.58:
                p = (t - 0.35) / 0.23
                rx = -25.0 + 70.0 * p
                rz = -15.0 * math.sin(p * math.pi)
            else:
                p = (t - 0.58) / 0.42
                rx = math.exp(-p * 7.0) * math.cos(p * 10.0 * math.pi) * 16.0
        elif mtype in ("attack_2", "swipe_cleave"):
            if t <= 0.32:
                rx = -30.0 * (t / 0.32)
                ry = -15.0 * (t / 0.32)
            elif t <= 0.55:
                p = (t - 0.32) / 0.23
                rx = -30.0 + 80.0 * p
                ry = -15.0 + 25.0 * p
            else:
                p = (t - 0.55) / 0.45
                rx = 50.0 * math.exp(-p * 6.0) * math.cos(p * 8.0 * math.pi)
        elif mtype in ("ability_1", "hook_throw"):
            if t <= 0.30:
                rx = -20.0 * (t / 0.30)
            elif t <= 0.50:
                rx = -20.0 + 75.0 * ((t - 0.30) / 0.20)
            else:
                p = (t - 0.50) / 0.50
                rx = 55.0 * math.exp(-p * 6.0) * math.cos(p * 6.0 * math.pi)
        elif mtype in ("ability_2", "crouch_dash"):
            rx = -10.0 + math.sin(t * 2.0 * math.pi) * 3.0
        elif mtype in ("ability_3", "cast_empower"):
            rx = -35.0 * (t / 0.35) if t <= 0.35 else (-35.0 + math.sin(t * 10.0 * math.pi) * 4.0 if t <= 0.70 else -35.0 * (1.0 - (t - 0.70) / 0.30))
        elif mtype in ("ability_4", "jump_slam"):
            if t <= 0.40:
                rx = -45.0 * (t / 0.40)
            elif t <= 0.60:
                rx = -45.0 + 110.0 * ((t - 0.40) / 0.20)
            else:
                p = (t - 0.60) / 0.40
                rx = 65.0 * math.exp(-p * 7.0) * math.cos(p * 6.0 * math.pi)
        elif mtype == "death":
            rx = min(1.0, t * 1.5) * 60.0
        elif mtype in ("bored", "bored_1", "twitch"):
            rx = math.sin(t * 6.0 * math.pi) * 4.0
        elif mtype in ("taunt", "taunt_1", "bow"):
            rx = (t / 0.4) * -25.0 if t <= 0.4 else -25.0 * (1.0 - min(1.0, (t - 0.7) / 0.3))

    elif arch == WeaponArchetype.CHAIN_WHIP:
        if mtype in ("idle", "portrait", "default_1"):
            rx = math.sin(t * 2.0 * math.pi) * 2.5
            rz = math.cos(t * 2.0 * math.pi) * 1.5
        elif mtype in ("walk", "walk_1", "run"):
            rx = math.sin(t * 4.0 * math.pi - 0.5) * 12.0
            rz = math.cos(t * 4.0 * math.pi - 0.5) * 8.0
        elif mtype in ("attack_1", "swipe_spin", "attack"):
            if t <= 0.32:
                rx = -45.0 * (t / 0.32)
                ry = -15.0 * (t / 0.32)
            elif t <= 0.56:
                p = (t - 0.32) / 0.24
                rx = -45.0 + 115.0 * p
                rz = -20.0 * math.sin(p * math.pi)
            else:
                p = (t - 0.56) / 0.44
                recoil = math.exp(-p * 4.0) * (math.cos(p * 6.0 * math.pi) * 22.0 + math.cos(p * 14.0 * math.pi) * 6.0)
                rx = recoil
        elif mtype in ("attack_2", "swipe_cleave"):
            if t <= 0.30:
                rx = -50.0 * (t / 0.30)
            elif t <= 0.52:
                rx = -50.0 + 120.0 * ((t - 0.30) / 0.22)
            else:
                p = (t - 0.52) / 0.48
                rx = 70.0 * math.exp(-p * 4.5) * math.cos(p * 8.0 * math.pi)
        elif mtype in ("ability_1", "hook_throw"):
            if t <= 0.35:
                rx = -55.0 * (t / 0.35)
            elif t <= 0.55:
                rx = -55.0 + 140.0 * ((t - 0.35) / 0.20)
            else:
                p = (t - 0.55) / 0.45
                rx = 85.0 * math.exp(-p * 4.0) * math.cos(p * 5.0 * math.pi)
        elif mtype in ("ability_2", "crouch_dash"):
            phase = math.sin(t * 2.0 * math.pi)
            rx = -18.0 + phase * 5.0
            rz = phase * 4.0
        elif mtype in ("ability_3", "cast_empower"):
            rx = -45.0 * (t / 0.35) if t <= 0.35 else (-45.0 + math.sin(t * 14.0 * math.pi) * 9.0 if t <= 0.68 else -45.0 * (1.0 - (t - 0.68) / 0.32))
        elif mtype in ("ability_4", "jump_slam"):
            if t <= 0.40:
                rx = -60.0 * (t / 0.40)
            elif t <= 0.62:
                rx = -60.0 + 145.0 * ((t - 0.40) / 0.22)
            else:
                p = (t - 0.62) / 0.38
                rx = 85.0 * math.exp(-p * 5.0) * math.cos(p * 6.0 * math.pi)
        elif mtype == "death":
            rx = min(1.0, t * 1.8) * 65.0
            rz = min(1.0, t * 1.8) * 30.0
        elif mtype in ("bored", "bored_1", "twitch"):
            tw = math.sin(t * 8.0 * math.pi) * 8.0
            rx, ry = tw * 0.7, tw
        elif mtype in ("taunt", "taunt_1", "bow"):
            rx = (t / 0.4) * -40.0 if t <= 0.4 else -40.0 * (1.0 - min(1.0, (t - 0.7) / 0.3))

    elif arch == WeaponArchetype.DUAL_WIELD:
        is_left = (str(hand).lower() == "left")
        phase_offset = math.pi if is_left else 0.0

        if mtype in ("idle", "portrait", "default_1"):
            rx = math.sin(t * 2.0 * math.pi + phase_offset) * 0.5
            rz = -10.0 if is_left else 10.0
        elif mtype in ("walk", "walk_1", "run"):
            rx = math.sin(t * 4.0 * math.pi + phase_offset) * 5.0
            ry = math.cos(t * 4.0 * math.pi + phase_offset) * 2.0
            rz = -8.0 if is_left else 8.0
        elif mtype in ("attack_1", "swipe_spin", "attack"):
            if not is_left:
                if t <= 0.20:
                    rx = -25.0 * (t / 0.20)
                elif t <= 0.45:
                    rx = -25.0 + 65.0 * ((t - 0.20) / 0.25)
                else:
                    p = (t - 0.45) / 0.55
                    rx = 40.0 * math.exp(-p * 6.0) * math.cos(p * 8.0 * math.pi)
            else:
                if t <= 0.45:
                    rx = -25.0 * (t / 0.45)
                    rz = -12.0
                elif t <= 0.70:
                    p = (t - 0.45) / 0.25
                    rx = -25.0 + 65.0 * p
                    rz = -12.0 + 20.0 * p
                else:
                    p = (t - 0.70) / 0.30
                    rx = 40.0 * math.exp(-p * 6.0) * math.cos(p * 8.0 * math.pi)
                    rz = 8.0
        elif mtype in ("attack_2", "swipe_cleave"):
            if t <= 0.35:
                rx = -35.0 * (t / 0.35)
                rz = -15.0 if is_left else 15.0
            elif t <= 0.58:
                p = (t - 0.35) / 0.23
                rx = -35.0 + 80.0 * p
                rz = (-15.0 + 35.0 * p) if is_left else (15.0 - 35.0 * p)
            else:
                p = (t - 0.58) / 0.42
                rx = 45.0 * math.exp(-p * 6.0) * math.cos(p * 8.0 * math.pi)
        elif mtype in ("ability_1", "hook_throw"):
            rx = -30.0 * (t / 0.3) if t <= 0.3 else (-30.0 + 85.0 * ((t - 0.3) / 0.25) if t <= 0.55 else 55.0 * math.exp(-((t - 0.55) / 0.45) * 5.0))
        elif mtype in ("ability_2", "crouch_dash"):
            rx = -15.0 + math.sin(t * 2.0 * math.pi + phase_offset) * 3.0
        elif mtype in ("ability_3", "cast_empower"):
            rx = -30.0 * (t / 0.35) if t <= 0.35 else (-30.0 * (1.0 - (t - 0.7) / 0.3) if t >= 0.7 else -30.0)
        elif mtype in ("ability_4", "jump_slam"):
            rx = -45.0 * (t / 0.4) if t <= 0.4 else (-45.0 + 115.0 * ((t - 0.4) / 0.2) if t <= 0.6 else 70.0 * math.exp(-((t - 0.6) / 0.4) * 6.0))
        elif mtype == "death":
            rx = min(1.0, t * 1.5) * 55.0
            rz = (-20.0 if is_left else 20.0) * min(1.0, t * 1.5)
        elif mtype in ("bored", "bored_1", "twitch"):
            rx = math.sin(t * 6.0 * math.pi + phase_offset) * 4.0
        elif mtype in ("taunt", "taunt_1", "bow"):
            rx = (t / 0.4) * -25.0 if t <= 0.4 else -25.0 * (1.0 - min(1.0, (t - 0.7) / 0.3))

    elif arch == WeaponArchetype.BOW_LAUNCHER:
        if mtype in ("idle", "portrait", "default_1"):
            rx, ry, rz = 0.0, 0.0, 0.0
        elif mtype in ("walk", "walk_1", "run"):
            rx = math.sin(t * 4.0 * math.pi) * 2.0
            ry = math.cos(t * 4.0 * math.pi) * 1.0
        elif mtype in ("attack_1", "attack_2", "swipe_spin", "attack"):
            if t <= 0.42:
                p = t / 0.42
                rx = -24.0 * p
                ry = 4.0 * p
            elif t <= 0.48:
                p = (t - 0.42) / 0.06
                rx = -24.0 + 32.0 * p
                ry = 4.0 * (1.0 - p)
            else:
                p = (t - 0.48) / 0.52
                vib = math.exp(-p * 8.0) * math.cos(p * 18.0 * math.pi) * 8.0
                rx = vib
                ry = vib * 0.2
        elif mtype in ("ability_1", "hook_throw"):
            if t <= 0.50:
                rx = -35.0 * (t / 0.50)
            elif t <= 0.56:
                rx = -35.0 + 45.0 * ((t - 0.50) / 0.06)
            else:
                p = (t - 0.56) / 0.44
                rx = 10.0 * math.exp(-p * 7.0) * math.cos(p * 16.0 * math.pi)
        elif mtype in ("ability_2", "crouch_dash"):
            rx = -10.0 + math.sin(t * 2.0 * math.pi) * 2.0
        elif mtype in ("ability_3", "cast_empower"):
            rx = -25.0 * (t / 0.35) if t <= 0.35 else (-25.0 * (1.0 - (t - 0.7) / 0.3) if t >= 0.7 else -25.0)
        elif mtype in ("ability_4", "jump_slam"):
            if t <= 0.45:
                rx = -65.0 * (t / 0.45)
            elif t <= 0.52:
                rx = -65.0 + 80.0 * ((t - 0.45) / 0.07)
            else:
                p = (t - 0.52) / 0.48
                rx = 15.0 * math.exp(-p * 6.0) * math.cos(p * 14.0 * math.pi)
        elif mtype == "death":
            rx = min(1.0, t * 1.5) * 45.0
        elif mtype in ("bored", "bored_1", "twitch"):
            rx = math.sin(t * 4.0 * math.pi) * 3.0
        elif mtype in ("taunt", "taunt_1", "bow"):
            rx = (t / 0.4) * -20.0 if t <= 0.4 else -20.0 * (1.0 - min(1.0, (t - 0.7) / 0.3))

    elif arch == WeaponArchetype.UNARMED:
        if mtype in ("idle", "portrait", "default_1", "walk", "walk_1", "run"):
            rx, ry, rz = 0.0, 0.0, 0.0
        elif mtype in ("attack_1", "swipe_spin", "attack"):
            if t <= 0.35:
                rx = -12.0 * (t / 0.35)
            elif t <= 0.50:
                rx = -12.0 + 27.0 * ((t - 0.35) / 0.15)
            else:
                p = (t - 0.50) / 0.50
                rx = 15.0 * math.exp(-p * 8.0) * math.cos(p * 8.0 * math.pi)
        elif mtype in ("attack_2", "swipe_cleave"):
            if t <= 0.38:
                rx = -18.0 * (t / 0.38)
                ry = -10.0 * (t / 0.38)
            elif t <= 0.58:
                p = (t - 0.38) / 0.20
                rx = -18.0 + 43.0 * p
                ry = -10.0 + 20.0 * p
            else:
                p = (t - 0.58) / 0.42
                rx = 25.0 * math.exp(-p * 7.0) * math.cos(p * 8.0 * math.pi)
        elif mtype in ("ability_1", "hook_throw"):
            rx = -15.0 * (t / 0.3) if t <= 0.3 else (-15.0 + 35.0 * ((t - 0.3) / 0.2) if t <= 0.5 else 20.0 * math.exp(-((t - 0.5) / 0.5) * 6.0))
        elif mtype in ("ability_2", "crouch_dash"):
            rx = -8.0 + math.sin(t * 2.0 * math.pi) * 2.0
        elif mtype in ("ability_3", "cast_empower"):
            rx = -20.0 * (t / 0.35) if t <= 0.35 else (-20.0 * (1.0 - (t - 0.7) / 0.3) if t >= 0.7 else -20.0)
        elif mtype in ("ability_4", "jump_slam"):
            rx = -30.0 * (t / 0.4) if t <= 0.4 else (-30.0 + 70.0 * ((t - 0.4) / 0.2) if t <= 0.6 else 40.0 * math.exp(-((t - 0.6) / 0.4) * 6.0))
        elif mtype == "death":
            rx = min(1.0, t * 1.5) * 30.0
        elif mtype in ("bored", "bored_1", "twitch"):
            rx = math.sin(t * 6.0 * math.pi) * 3.0
        elif mtype in ("taunt", "taunt_1", "bow"):
            rx = (t / 0.4) * -20.0 if t <= 0.4 else -20.0 * (1.0 - min(1.0, (t - 0.7) / 0.3))

    return (clamp_angle(rx), clamp_angle(ry), clamp_angle(rz))

def enforce_cyclic_boundary(
    track_values: Sequence[float],
    blend_window: int = 6,
    is_angular: bool = False,
    target_mode: str = "wrap"
) -> List[float]:
    """
    Applies continuous C1 cubic Hermite cyclic boundary smoothing over final blend_window frames.
    Guarantees seamless loop closure into frame 0 with ΔR < 0.01° and velocity continuity.
    """
    track = list(float(x) for x in track_values)
    n = len(track)
    if n < 2:
        return track

    k = max(2, min(blend_window, n - 1))
    f0 = n - k
    y0 = track[f0]

    if f0 > 0:
        v0 = (track[f0 + 1] - track[f0 - 1]) / 2.0
    else:
        v0 = track[1] - track[0]

    y1 = track[0]
    v1 = (track[1] - track[0]) if n > 2 else 0.0

    y1_unwrapped = y1
    if is_angular:
        diff = y1 - y0
        rev = round(diff / 360.0)
        y1_unwrapped = y1 - rev * 360.0

    out = list(track)

    if target_mode == "last_frame":
        denom = float(k - 1)
        for i in range(k):
            s = i / denom
            h00 = 2.0 * s**3 - 3.0 * s**2 + 1.0
            h10 = s**3 - 2.0 * s**2 + s
            h01 = -2.0 * s**3 + 3.0 * s**2
            h11 = s**3 - s**2
            val = h00 * y0 + h10 * (denom * v0) + h01 * y1_unwrapped + h11 * (denom * v1)
            if is_angular:
                val = (val + 180.0) % 360.0 - 180.0
            out[f0 + i] = val
        out[-1] = y1
    else:
        y_target_last = y1_unwrapped - v1
        denom = float(k - 1)
        for i in range(k):
            s = i / denom
            h00 = 2.0 * s**3 - 3.0 * s**2 + 1.0
            h10 = s**3 - 2.0 * s**2 + s
            h01 = -2.0 * s**3 + 3.0 * s**2
            h11 = s**3 - s**2
            val = h00 * y0 + h10 * (denom * v0) + h01 * y_target_last + h11 * (denom * v1)
            if is_angular:
                val = (val + 180.0) % 360.0 - 180.0
            out[f0 + i] = val
        out[-1] = (y_target_last + 180.0) % 360.0 - 180.0 if is_angular else y_target_last

    return out


def evaluate_loop_closure_error(
    track_values: Sequence[float],
    blend_window: int = 6,
    is_angular: bool = True
) -> Tuple[float, float]:
    """
    Evaluates continuous C1 loop closure metrics (ΔR and velocity delta).
    Returns (delta_R, velocity_delta).
    """
    n = len(track_values)
    if n < 2:
        return (0.0, 0.0)

    y_first = track_values[0]
    y_last = track_values[-1]
    v0 = (track_values[1] - track_values[0]) if n > 2 else 0.0
    v_seam = y_first - y_last
    if is_angular:
        v_seam = (v_seam + 180.0) % 360.0 - 180.0

    def _ang_wrap(diff: float) -> float:
        return (diff + 180.0) % 360.0 - 180.0

    # Measure seam position error:
    # 1. Direct boundary seam difference (for inclusive loops where track[-1] closes into track[0]):
    delta_direct = abs(y_first - y_last) if not is_angular else abs(_ang_wrap(y_first - y_last))

    # 2. Extrapolated wrap seam difference (for non-inclusive loops where track[-1] advances by v0 into track[0]):
    val_at_wrap = y_last + v0
    delta_wrap = abs(y_first - val_at_wrap) if not is_angular else abs(_ang_wrap(y_first - val_at_wrap))

    delta_R = min(delta_direct, delta_wrap)

    # Velocity step:
    if delta_direct < 0.01 and n > 2:
        v_last = track_values[-1] - track_values[-2]
        if is_angular:
            v_last = _ang_wrap(v_last)
        v_diff = abs(v_last - v0) if not is_angular else abs(_ang_wrap(v_last - v0))
    else:
        v_diff = abs(v_seam - v0) if not is_angular else abs(_ang_wrap(v_seam - v0))

    return (float(delta_R), float(v_diff))


def enforce_cyclic_clip(
    clip_data: Any,
    blend_window: int = 6
) -> Any:
    """
    Smooths all looping tracks in clip_data using continuous C1 cubic Hermite blending.
    """
    if isinstance(clip_data, dict):
        target_dict = clip_data["bones"] if "bones" in clip_data else clip_data
        for bone, tracks in target_dict.items():
            if isinstance(tracks, dict):
                if "rotations" in tracks:
                    rots = np.array(tracks["rotations"], dtype=float)
                    if rots.ndim == 2 and rots.shape[1] >= 3:
                        for col in range(3):
                            smoothed = enforce_cyclic_boundary(rots[:, col], blend_window, is_angular=True)
                            rots[:, col] = smoothed
                        tracks["rotations"] = rots.tolist()
                if "positions" in tracks:
                    pos = np.array(tracks["positions"], dtype=float)
                    if pos.ndim == 2 and pos.shape[1] >= 3:
                        for col in range(3):
                            smoothed = enforce_cyclic_boundary(pos[:, col], blend_window, is_angular=False)
                            pos[:, col] = smoothed
                        tracks["positions"] = pos.tolist()
            elif isinstance(tracks, (list, np.ndarray)):
                arr = np.array(tracks, dtype=float)
                if arr.ndim == 2:
                    channels = arr.shape[1]
                    for col in range(channels):
                        is_ang = (col >= 3)
                        arr[:, col] = enforce_cyclic_boundary(arr[:, col], blend_window, is_angular=is_ang)
                    target_dict[bone] = arr.tolist() if isinstance(tracks, list) else arr

    return clip_data


def resolve_weapon_socket(
    archetype: Union[str, WeaponArchetype],
    armature_bones: Sequence[str]
) -> SocketBinding:
    """
    Resolves canonical primary and secondary weapon socket attachments from armature bone candidates.
    """
    arch = WeaponArchetype.from_string(archetype)
    bones_set = set(armature_bones)

    primary_candidates = [
        "_bone_weapon",
        "Bip01 R Hand",
        "RightHand",
        "Bip001 R Hand",
        "Bip01 R Hand_end",
    ]

    secondary_candidates = [
        "_bone_weaponattach",
        "_bone_fx_l_hand",
        "Bip01 L Hand",
        "LeftHand",
        "Bip001 L Hand",
    ]

    primary = "_bone_weapon"
    for cand in primary_candidates:
        if cand in bones_set:
            primary = cand
            break

    secondary = None
    if arch == WeaponArchetype.DUAL_WIELD:
        secondary = "_bone_fx_l_hand"
        for cand in secondary_candidates:
            if cand in bones_set:
                secondary = cand
                break
    else:
        for cand in ["_bone_weaponattach", "_bone_fx_l_hand"]:
            if cand in bones_set:
                secondary = cand
                break

    return SocketBinding(primary, secondary)


def generate_mdf_trail_events(
    attack_action_ratio: float = 0.42,
    total_frames: int = 45,
    effect_path: str = "effects/weapon_trail.effect",
    event_id: int = 1
) -> List[str]:
    """
    Generates MDF XML frameevent commands for weapon trail activation during attack swings.
    """
    total_f = max(10, int(total_frames))
    f_start = max(1, int(round(total_f * 0.22)))
    f_stop = min(total_f - 1, max(f_start + 2, int(round(total_f * 0.60))))

    return [
        f'<frameevent frame="{f_start}" cmd="StartEffect {effect_path} {event_id}"/>',
        f'<frameevent frame="{f_stop}" cmd="StopEffect {event_id}"/>'
    ]


class WeaponPhysicsSolver:
    """
    Unified class interface for weapon physics simulation and cyclic Hermite loop closure.
    """
    Archetype = WeaponArchetype

    @classmethod
    def simulate_motion(
        cls,
        archetype: Union[str, WeaponArchetype],
        motion_type: str,
        frame: int,
        total_frames: int,
        fps: float = 30.0,
        hand: str = "right"
    ) -> Tuple[float, float, float]:
        return simulate_weapon_motion(archetype, motion_type, frame, total_frames, fps, hand)

    @classmethod
    def smooth_boundary(
        cls,
        track_values: Sequence[float],
        blend_window: int = 6,
        is_angular: bool = False,
        target_mode: str = "wrap"
    ) -> List[float]:
        return enforce_cyclic_boundary(track_values, blend_window, is_angular, target_mode)

    @classmethod
    def resolve_sockets(
        cls,
        archetype: Union[str, WeaponArchetype],
        armature_bones: Sequence[str]
    ) -> SocketBinding:
        return resolve_weapon_socket(archetype, armature_bones)

    @classmethod
    def generate_trail_events(
        cls,
        attack_action_ratio: float = 0.42,
        total_frames: int = 45,
        effect_path: str = "effects/weapon_trail.effect",
        event_id: int = 1
    ) -> List[str]:
        return generate_mdf_trail_events(attack_action_ratio, total_frames, effect_path, event_id)

    @classmethod
    def solve_and_blend(
        cls,
        clip_data: Any,
        weapon_archetype: Union[str, WeaponArchetype],
        bone_name: str = "_bone_weapon",
        blend_frames: int = 6,
        is_looping: bool = True
    ) -> Any:
        arch = WeaponArchetype.from_string(weapon_archetype)
        if isinstance(clip_data, dict):
            entry = None
            if bone_name in clip_data:
                entry = clip_data[bone_name]
            elif "bones" in clip_data and bone_name in clip_data["bones"]:
                entry = clip_data["bones"][bone_name]

            if isinstance(entry, dict) and "rotations" in entry:
                rots = entry["rotations"]
                if is_looping and len(rots) > blend_frames:
                    for col in range(3):
                        col_vals = [r[col] for r in rots]
                        smoothed = enforce_cyclic_boundary(col_vals, blend_frames, is_angular=True)
                        for idx, val in enumerate(smoothed):
                            rots[idx][col] = val

        return clip_data
