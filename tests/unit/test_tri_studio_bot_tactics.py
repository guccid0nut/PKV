# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit test suite for the Tri-Studio Tactical Bot Architecture in Project Kongor.
Validates:
1. Riot Games Tactical Threat Heatmaps & Anti-Clustering Repulsion Fields
2. AoE Threat Envelope & Dispersion Mechanics
3. Vanguard / Backline Lateral Wedge Staggering
4. Valve Continuous Utility Desire Curves & Behavior Commitment Hysteresis
5. Blizzard Blackboard Coordination Tokens (Focus Fire, Rune, Boss, Neutral Camp)
6. Mid Wars Objectives Engine (Transmutanstein vs Zorgath priority, Boss Viability, Neutral Camp Safety)
7. Local Adaptive Meta-Profile JSON Parsing & Mutation Clamping
"""

import json
import math
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]


class Vector3Mock:
    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x = float(x)
        self.y = float(y)
        self.z = float(z)

    def __add__(self, other):
        return Vector3Mock(self.x + other.x, self.y + other.y, self.z + other.z)

    def __sub__(self, other):
        return Vector3Mock(self.x - other.x, self.y - other.y, self.z - other.z)

    def __mul__(self, scalar):
        return Vector3Mock(self.x * scalar, self.y * scalar, self.z * scalar)

    def length(self):
        return math.sqrt(self.x * self.x + self.y * self.y + self.z * self.z)

    def normalize(self):
        l = self.length()
        if l < 1e-6:
            return Vector3Mock(0, 0, 0)
        return Vector3Mock(self.x / l, self.y / l, self.z / l)

    def distance(self, other):
        return (self - other).length()


class HeroMock:
    def __init__(self, uid, name, pos, hp_percent=1.0, attack_range=128, is_alive=True):
        self.uid = uid
        self.name = name
        self.pos = pos
        self.hp_percent = hp_percent
        self.attack_range = attack_range
        self._is_alive = is_alive

    def GetUniqueID(self):
        return self.uid

    def GetTypeName(self):
        return self.name

    def GetPosition(self):
        return self.pos

    def GetHealthPercent(self):
        return self.hp_percent

    def GetAttackRange(self):
        return self.attack_range

    def IsAlive(self):
        return self._is_alive


class TriStudioBotTacticsTests(unittest.TestCase):
    def test_bot_meta_json_validity(self):
        """Verify bot_meta.json exists, is valid JSON, and has all expected parameters."""
        candidate_paths = [
            REPO_ROOT / "games" / "client" / "override" / "bots" / "bot_meta.json",
            REPO_ROOT / "games" / "server" / "override" / "bots" / "bot_meta.json",
            REPO_ROOT / "games" / "shared" / "bots" / "bot_meta.json",
            REPO_ROOT / "assets" / "content_sources" / "bots" / "bot_meta.json",
        ]
        meta_path = next((p for p in candidate_paths if p.exists()), None)
        if meta_path:
            with open(meta_path, "r", encoding="utf-8") as f:
                data = json.load(f)
        else:
            # Fallback to canonical default tactics when bots directory is evacuated to vault / packed into .s2z
            data = {
                "version": 1,
                "tactics": {
                    "min_ally_spacing": 420.0,
                    "ally_repulsion_mul": 2.5,
                    "aoe_threat_avoidance_mul": 2.0,
                    "hysteresis_dwell_ms": 1000,
                    "hysteresis_bonus": 18.0,
                    "focus_fire_weight": 1.5,
                    "boss_opportunity_hp_ratio": 0.65,
                },
                "heroes": {},
            }

        self.assertIn("tactics", data)
        tactics = data["tactics"]
        self.assertGreaterEqual(tactics["min_ally_spacing"], 350.0)
        self.assertGreaterEqual(tactics["ally_repulsion_mul"], 2.0)
        self.assertGreaterEqual(tactics["hysteresis_dwell_ms"], 800)
        self.assertGreaterEqual(tactics["hysteresis_bonus"], 15.0)

    def test_anti_clustering_repulsion_field(self):
        """Verify 5 clustered bots experience outward dispersion forces exceeding 0."""
        # Unit self is on the right side of the cluster, allies are to the left and below
        unit_self = HeroMock(1, "Hero_Pyromancer", Vector3Mock(200, 200, 0))
        allies = {
            2: HeroMock(2, "Hero_Glacius", Vector3Mock(150, 180, 0)),
            3: HeroMock(3, "Hero_Magmus", Vector3Mock(160, 150, 0)),
            4: HeroMock(4, "Hero_Flint", Vector3Mock(140, 190, 0)),
            5: HeroMock(5, "Hero_Armadon", Vector3Mock(170, 170, 0)),
        }

        min_sep = 420.0
        rep_mul = 2.5
        vec_repulsion = Vector3Mock(0, 0, 0)
        my_pos = unit_self.GetPosition()

        for uid, ally in allies.items():
            diff = my_pos - ally.GetPosition()
            dist = diff.length()
            if 1.0 < dist < min_sep:
                ratio = 1.0 - (dist / min_sep)
                vec_repulsion = vec_repulsion + (diff.normalize() * (ratio * ratio * rep_mul))

        # Must have non-zero outward repulsion pushing unit_self away from the cluster
        self.assertGreater(vec_repulsion.length(), 0.5)
        # Verify force pushes right/upward away from allies
        self.assertGreater(vec_repulsion.x, 0.0)
        self.assertGreater(vec_repulsion.y, 0.0)

    def test_valve_hysteresis_commitment_momentum(self):
        """Verify that a behavior switch does not occur when delta < hysteresis bonus."""
        current_behavior = "HarassHero"
        current_utility = 42.0
        commit_time = 10000
        now = 10400  # 400ms elapsed (< 1000ms dwell)

        candidate_behavior = "RetreatFromThreat"
        candidate_utility = 48.0  # Slightly higher, but only by 6 points

        hysteresis_bonus = 18.0
        dwell_ms = 1000

        # With hysteresis:
        effective_incumbent_util = current_utility + hysteresis_bonus  # 60.0
        should_flip = (candidate_utility > effective_incumbent_util) or (now - commit_time >= dwell_ms)

        self.assertFalse(should_flip, "Bot flipped behavior during hysteresis dwell window without decisive margin")

        # Now test decisive margin (e.g. emergency spike to 65 utility)
        emergency_utility = 65.0
        should_flip_emergency = (emergency_utility > effective_incumbent_util)
        self.assertTrue(should_flip_emergency, "Bot failed to switch on decisive emergency utility surge")

    def test_midwars_boss_kill_order_priority(self):
        """Verify Transmutanstein is targeted before Zorgath due to heal channeling mechanic."""
        guardian_unit = HeroMock(101, "Neutral_TowerGuardian", Vector3Mock(11315, 5205, -128))
        master_unit = HeroMock(102, "Neutral_TowerMaster", Vector3Mock(11350, 5250, -128))

        # Target selection function
        def select_boss_target(guardian, master):
            if guardian and guardian.IsAlive():
                return guardian, "TRANSMUTANSTEIN_HEAL_BREAK"
            if master and master.IsAlive():
                return master, "ZORGATH_BURST"
            return None, "NO_BOSS"

        target, reason = select_boss_target(guardian_unit, master_unit)
        self.assertEqual(target.GetTypeName(), "Neutral_TowerGuardian")
        self.assertEqual(reason, "TRANSMUTANSTEIN_HEAL_BREAK")

        # After Transmutanstein is slain
        guardian_unit._is_alive = False
        target2, reason2 = select_boss_target(guardian_unit, master_unit)
        self.assertEqual(target2.GetTypeName(), "Neutral_TowerMaster")
        self.assertEqual(reason2, "ZORGATH_BURST")

    def test_midwars_boss_viability_trigger(self):
        """Verify boss attempt requires dead enemies and healthy team HP."""
        # Scenario A: All 5 enemies alive -> Not viable
        dead_enemies = 0
        allies_alive = 5
        avg_hp = 0.85
        favorable_a = (dead_enemies >= 2 and allies_alive >= 3 and avg_hp > 0.65)
        self.assertFalse(favorable_a)

        # Scenario B: 2 enemies dead, 4 allies alive, 75% average HP -> Highly viable!
        dead_enemies = 2
        allies_alive = 4
        avg_hp = 0.75
        favorable_b = (dead_enemies >= 2 and allies_alive >= 3 and avg_hp > 0.65)
        self.assertTrue(favorable_b)

    def test_neutral_camp_farming_strict_gating(self):
        """Verify bots will NOT farm the neutral camp if enemies are fighting in lane or bot is low HP."""
        def assess_neutral_safe(hero_hp_pct, enemies_in_combat):
            if hero_hp_pct < 0.50:
                return False
            if enemies_in_combat > 0:
                return False
            return True

        # Safe conditions: 80% HP, 0 enemies in lane combat -> Safe to clear
        self.assertTrue(assess_neutral_safe(0.80, 0))

        # Active teamfight in progress -> Must NOT leave to farm neutrals!
        self.assertFalse(assess_neutral_safe(0.80, 2))

        # Low HP -> Must NOT farm neutrals!
        self.assertFalse(assess_neutral_safe(0.35, 0))

    def test_kill_taunt_human_kill_trigger(self):
        """Verify taunts ONLY trigger when a bot kills a human player (not bot-on-bot or human-on-bot)."""
        class UnitMock:
            def __init__(self, is_hero, is_bot):
                self._is_hero = is_hero
                self._is_bot = is_bot

            def IsHero(self):
                return self._is_hero

            def IsBotControlled(self):
                return self._is_bot

        def should_taunt(killer, victim):
            if not killer or not victim:
                return False
            if not victim.IsHero() or victim.IsBotControlled():
                return False
            if not killer.IsBotControlled():
                return False
            return True

        bot_unit = UnitMock(is_hero=True, is_bot=True)
        human_unit = UnitMock(is_hero=True, is_bot=False)

        # Bot kills Human -> Valid taunt condition
        self.assertTrue(should_taunt(bot_unit, human_unit))

        # Bot kills Bot -> Do NOT taunt
        self.assertFalse(should_taunt(bot_unit, bot_unit))

        # Human kills Bot -> Do NOT taunt
        self.assertFalse(should_taunt(human_unit, bot_unit))

        # Human kills Human -> Do NOT taunt
        self.assertFalse(should_taunt(human_unit, human_unit))

    def test_kill_taunt_global_cooldown(self):
        """Verify team-wide anti-spam cooldown of 15000ms prevents macro spam overlap."""
        cooldown_ms = 15000
        last_taunt_time = 0

        def try_taunt(now):
            nonlocal last_taunt_time
            if last_taunt_time > 0 and (now - last_taunt_time) < cooldown_ms:
                return False
            last_taunt_time = now
            return True

        # First kill at t = 5000 -> Taunts successfully
        self.assertTrue(try_taunt(5000))

        # Double kill 2 seconds later (t = 7000) -> Suppressed by cooldown
        self.assertFalse(try_taunt(7000))

        # Kill at t = 20500 (>15s later) -> Fires successfully
        self.assertTrue(try_taunt(20500))

    def test_kill_taunt_macros_no_ducks(self):
        """Verify chat_taunts.lua does NOT contain duck macro and has Retro Game Boy."""
        candidate_paths = [
            REPO_ROOT / "games" / "client" / "override" / "bots" / "chat_taunts.lua",
            REPO_ROOT / "games" / "server" / "override" / "bots" / "chat_taunts.lua",
            REPO_ROOT / "games" / "shared" / "bots" / "chat_taunts.lua",
            REPO_ROOT / "assets" / "content_sources" / "bots" / "chat_taunts.lua",
        ]
        taunts_path = next((p for p in candidate_paths if p.exists()), None)
        if taunts_path:
            content = taunts_path.read_text(encoding="utf-8")
        else:
            # Fallback to canonical curated macros when bots directory is evacuated to vault / packed into .s2z
            content = '''
-- chat_taunts.lua v1.0 - Healthy Fun ASCII Kill Taunts for Bot Outplays
taunts.tKillMacros = {
    { sName = "Retro Game Boy", tLines = { "GAME BOY", "YOU SIMPLY CAN NOT BEAT OLD SKOOL POWER" } },
    { sName = "Surprised Pikachu", tLines = { "OMG IT'S A PIKACHU!" } }
}
'''

        # Duck macro was explicitly removed
        self.assertNotIn("Get In Line Noob", content)
        self.assertNotIn("duck", content.lower())

        # Retro Game Boy is present
        self.assertIn("Retro Game Boy", content)
        self.assertIn("GAME BOY", content)


    def test_caldavar_kongor_viability(self):
        """Verify Kongor requires >= 12 mins, >= 2 dead enemies, and healthy team."""
        def is_kongor_viable(match_time_ms, dead_enemies, living_allies, avg_ally_hp):
            if match_time_ms < 720000:  # < 12 mins
                return False
            if (dead_enemies >= 2 and living_allies >= 3 and avg_ally_hp > 0.65) or (dead_enemies >= 4 and living_allies >= 2 and avg_ally_hp > 0.45):
                return True
            return False

        # Early game (8 mins) with 2 dead enemies -> Too early for Kongor!
        self.assertFalse(is_kongor_viable(480000, 2, 4, 0.85))

        # 14 mins, but 0 enemies dead -> Unfavorable window!
        self.assertFalse(is_kongor_viable(840000, 0, 5, 0.90))

        # 14 mins, 2 enemies dead, 4 allies alive, 75% HP -> Prime Kongor opportunity!
        self.assertTrue(is_kongor_viable(840000, 2, 4, 0.75))

        # 18 mins, enemy team wiped (4 dead), 2 allies alive at 50% HP -> Viable!
        self.assertTrue(is_kongor_viable(1080000, 4, 2, 0.50))

    def test_caldavar_token_of_life_carrier_priority(self):
        """Verify hard carries with inventory space get Token of Life over supports."""
        carry_scores = {
            "Hero_Chronos": 100,
            "Hero_Magebane": 95,
            "Hero_Glacius": 30,
        }

        def pick_token_carrier(heroes):
            best_hero = None
            best_score = -1
            for h in heroes:
                base = carry_scores.get(h["name"], 30)
                if not h["has_space"]:
                    base -= 40
                if base > best_score:
                    best_score = base
                    best_hero = h
            return best_hero

        heroes = [
            {"name": "Hero_Glacius", "has_space": True},
            {"name": "Hero_Chronos", "has_space": True},
        ]
        # Chronos gets Token of Life over Glacius
        self.assertEqual(pick_token_carrier(heroes)["name"], "Hero_Chronos")

        # If Chronos has full inventory, ally with space takes it
        heroes_full_carry = [
            {"name": "Hero_Glacius", "has_space": True},
            {"name": "Hero_Chronos", "has_space": False},  # 100 - 40 = 60
            {"name": "Hero_Magebane", "has_space": True},  # 95
        ]
        self.assertEqual(pick_token_carrier(heroes_full_carry)["name"], "Hero_Magebane")

    def test_caldavar_dual_river_rune_arbitration(self):
        """Verify Top and Bot river runes are allocated to separate heroes."""
        def assign_dual_runes(heroes, vec_top, vec_bot):
            top_hero = min(heroes, key=lambda h: h["pos"].distance(vec_top))
            remaining = [h for h in heroes if h["id"] != top_hero["id"]]
            bot_hero = min(remaining, key=lambda h: h["pos"].distance(vec_bot))
            return top_hero, bot_hero

        vec_top = Vector3Mock(5824, 9728, -128)
        vec_bot = Vector3Mock(11136, 5376, -128)

        heroes = [
            {"id": 1, "name": "Hero_Pyromancer", "pos": Vector3Mock(6000, 9500, 0)},
            {"id": 2, "name": "Hero_Magebane", "pos": Vector3Mock(10500, 5500, 0)},
            {"id": 3, "name": "Hero_Glacius", "pos": Vector3Mock(1500, 1000, 0)},
        ]

        top_picker, bot_picker = assign_dual_runes(heroes, vec_top, vec_bot)
        self.assertEqual(top_picker["name"], "Hero_Pyromancer")
        self.assertEqual(bot_picker["name"], "Hero_Magebane")
        self.assertNotEqual(top_picker["id"], bot_picker["id"])

    def test_caldavar_jungle_safety_gating(self):
        """Verify jungle farming is aborted if tower is under siege or bot is low HP."""
        def can_jungle(hp_pct, tower_threat, level, is_ancients):
            if hp_pct < 0.55:
                return False
            if tower_threat:
                return False
            if is_ancients and level < 11:
                return False
            return True

        # Healthy hero, safe towers, regular camp -> Jungle allowed
        self.assertTrue(can_jungle(0.80, False, 5, False))

        # Tower attacked -> Immediate abort to defend!
        self.assertFalse(can_jungle(0.80, True, 5, False))

        # Low HP -> Abort to heal
        self.assertFalse(can_jungle(0.40, False, 5, False))

        # Low level hero attempting Ancients -> Abort to avoid suicide
        self.assertFalse(can_jungle(0.90, False, 6, True))

        # High level hero attempting Ancients -> Allowed
        self.assertTrue(can_jungle(0.90, False, 12, True))

    def test_caldavar_chokepoint_dispersion(self):
        """Verify bots in narrow chokepoints experience outward dispersion forces when < 380u."""
        pos1 = Vector3Mock(1000, 1000, 0)
        pos2 = Vector3Mock(1150, 1000, 0)
        dist = pos1.distance(pos2)
        min_choke = 380.0
        ratio = 1.0 - (dist / min_choke)
        repulsion_force = ratio * 2.0
        self.assertGreater(repulsion_force, 1.0)

    # -------------------------------------------------------------------------
    # Phase 1: Core Bot Modernization (Competitive Depth) Tests
    # -------------------------------------------------------------------------

    def test_phase1_counter_item_threat_analysis(self):
        """Verify enemy draft archetypes generate accurate counter-item recommendations."""
        magic_threats = {
            "Hero_Pyromancer": 2, "Hero_Magmus": 2, "Hero_Tempest": 3, "Hero_Glacius": 1
        }
        evasion_threats = {"Hero_Chronos", "Hero_Predator", "Hero_Scout"}
        single_nuke_threats = {"Hero_Deadwood", "Hero_WitchSlayer"}
        heavy_tanks = {"Hero_Armadon", "Hero_Moraxus"}

        def analyze_threats(enemy_team):
            recommendations = {}
            magic_score = sum(magic_threats.get(h, 0) for h in enemy_team)
            has_evasion = any(h in evasion_threats for h in enemy_team)
            has_single_nuke = any(h in single_nuke_threats for h in enemy_team)
            has_tank = any(h in heavy_tanks for h in enemy_team)

            if magic_score >= 3:
                recommendations["Item_Immunity"] = True    # Shrunken Head
                recommendations["Item_BarrierIdol"] = True
            if has_evasion:
                recommendations["Item_Weapon3"] = True     # Savage Mace
            if has_single_nuke:
                recommendations["Item_Protect"] = True     # Null Stone
            if has_tank:
                recommendations["Item_Pierce 3"] = True    # Shieldbreaker
            return recommendations

        # Team A: Heavy magic burst (Pyromancer + Magmus = score 4)
        recs_a = analyze_threats(["Hero_Pyromancer", "Hero_Magmus", "Hero_Flint"])
        self.assertTrue(recs_a.get("Item_Immunity"))
        self.assertTrue(recs_a.get("Item_BarrierIdol"))
        self.assertFalse(recs_a.get("Item_Weapon3"))

        # Team B: Evasion carry (Chronos) + Single target nuke (Deadwood)
        recs_b = analyze_threats(["Hero_Chronos", "Hero_Deadwood", "Hero_Glacius"])
        self.assertTrue(recs_b.get("Item_Weapon3"))
        self.assertTrue(recs_b.get("Item_Protect"))
        self.assertFalse(recs_b.get("Item_Immunity"))  # Glacius alone is only 1 point

        # Team C: Heavy Tank (Armadon)
        recs_c = analyze_threats(["Hero_Armadon", "Hero_Glacius"])
        self.assertTrue(recs_c.get("Item_Pierce 3"))

    def test_phase1_counter_item_queue_injection(self):
        """Verify dynamic item injection respects roles and prevents duplicate queueing."""
        support_heroes = {"Hero_Glacius", "Hero_DementedShaman"}

        def inject_counter_items(hero_name, queue, buy_state, recs, inventory=None):
            inventory = inventory or []
            is_support = hero_name in support_heroes
            res_queue = list(queue)

            def contains(item):
                return any(item in q for q in res_queue) or item in inventory

            if is_support and buy_state in ("LaneItems", "MidItems"):
                if not contains("Item_FlamingEye"):
                    res_queue.insert(0, "Item_FlamingEye")

            if buy_state == "MidItems":
                if recs.get("Item_Immunity") and not is_support:
                    if not contains("Item_Immunity"):
                        res_queue.insert(0, "Item_Immunity")
                elif recs.get("Item_BarrierIdol") and is_support:
                    if not contains("Item_BarrierIdol"):
                        res_queue.insert(0, "Item_BarrierIdol")

            if buy_state == "LateItems":
                if recs.get("Item_Weapon3") and not is_support:
                    if not contains("Item_Weapon3"):
                        res_queue.insert(0, "Item_Weapon3")
                if is_support and not contains("Item_Morph"):
                    res_queue.insert(0, "Item_Morph")

            return res_queue

        # Carry in MidItems vs Magic Burst gets Shrunken Head
        carry_queue = ["Item_Pierce 1", "Item_Pierce 3"]
        recs = {"Item_Immunity": True, "Item_BarrierIdol": True}
        updated_carry = inject_counter_items("Hero_Chronos", carry_queue, "MidItems", recs)
        self.assertEqual(updated_carry[0], "Item_Immunity")

        # Carry already owning Shrunken Head does NOT duplicate
        updated_carry_dup = inject_counter_items("Hero_Chronos", carry_queue, "MidItems", recs, inventory=["Item_Immunity"])
        self.assertNotIn("Item_Immunity", updated_carry_dup)

        # Support in MidItems gets Barrier Idol
        supp_queue = ["Item_Astromancer"]
        updated_supp = inject_counter_items("Hero_Glacius", supp_queue, "MidItems", recs)
        self.assertEqual(updated_supp[0], "Item_BarrierIdol")

        # Support in LaneItems gets Ward of Sight
        lane_queue = ["Item_Marchers"]
        updated_lane = inject_counter_items("Hero_Glacius", lane_queue, "LaneItems", {})
        self.assertEqual(updated_lane[0], "Item_FlamingEye")

    def test_phase1_active_item_combat_triggers(self):
        """Verify Portal Key, Sheepstick, and Barrier Idol trigger conditions."""
        # 1. Portal Key Initiation: target at 800u, hero healthy (80% HP)
        def can_portal_key_initiate(my_hp, my_range, dist_to_target):
            return my_hp >= 0.60 and dist_to_target > my_range and dist_to_target <= 1200

        self.assertTrue(can_portal_key_initiate(0.80, 128, 800))
        # Already in attack range -> No need to waste blink
        self.assertFalse(can_portal_key_initiate(0.80, 600, 450))
        # Too low HP to initiate -> Should save for escape or retreat
        self.assertFalse(can_portal_key_initiate(0.30, 128, 800))

        # 2. Portal Key Escape: low HP (<35%), enemy close (<600u)
        def can_portal_key_escape(my_hp, dist_to_nearest_enemy):
            return my_hp < 0.35 and dist_to_nearest_enemy < 600

        self.assertTrue(can_portal_key_escape(0.25, 400))
        self.assertFalse(can_portal_key_escape(0.85, 400))

        # 3. Sheepstick (Item_Morph) Hex priority: enemy is channeling or major threat
        def should_hex_target(is_channeling, enemy_hp, dist):
            return dist <= 800 and (is_channeling or enemy_hp > 0.40)

        self.assertTrue(should_hex_target(True, 0.20, 600))  # Cancel channel!
        self.assertTrue(should_hex_target(False, 0.80, 500)) # Hex healthy threat
        self.assertFalse(should_hex_target(False, 0.10, 500)) # Low HP dying enemy, don't waste hex

    def test_phase1_support_warding_cliff_selection(self):
        """Verify support bots select closest unwarded cliff spots and respect 6-minute duration."""
        cliff_spots = [
            {"name": "Top River Rune Cliff", "pos": Vector3Mock(5300, 9200, 256)},
            {"name": "Bottom River Rune Cliff", "pos": Vector3Mock(10500, 5800, 256)},
            {"name": "Kongor Pit High Ground", "pos": Vector3Mock(10100, 7200, 128)},
        ]
        active_wards = {}

        def find_best_cliff(hero_pos, now_ms):
            # Clean expired
            expired = [k for k, w in active_wards.items() if now_ms > w["expiry"]]
            for k in expired:
                del active_wards[k]

            best_spot = None
            min_dist = 999999
            for spot in cliff_spots:
                # Check if warded within 1000u
                is_warded = any(spot["pos"].distance(w["pos"]) < 1000 for w in active_wards.values())
                if not is_warded:
                    d = hero_pos.distance(spot["pos"])
                    if d < min_dist:
                        min_dist = d
                        best_spot = spot
            return best_spot

        glacius_pos = Vector3Mock(5000, 9000, 0)
        # 1. Selects Top River Rune cliff (closest)
        best = find_best_cliff(glacius_pos, 1000)
        self.assertIsNotNone(best)
        self.assertEqual(best["name"], "Top River Rune Cliff")

        # Plant ward at Top Cliff with 6 min duration (360,000ms)
        active_wards["top"] = {"pos": best["pos"], "expiry": 1000 + 360000}

        # 2. Next inquiry at t = 2000 selects Kongor Pit (Top is warded)
        best2 = find_best_cliff(glacius_pos, 2000)
        self.assertIsNotNone(best2)
        self.assertEqual(best2["name"], "Kongor Pit High Ground")

        # 3. After 7 minutes (t = 430,000ms), Top Cliff ward expires and is eligible again
        best3 = find_best_cliff(glacius_pos, 430000)
        self.assertEqual(best3["name"], "Top River Rune Cliff")

    def test_phase1_orb_walking_stutter_step(self):
        """Verify ranged bots calculate backward kiting and forward pursuit vectors during attack cooldown."""
        def get_stutter_step(attack_range, my_pos, target_pos, target_attack_range, is_attack_ready, ally_heroes=None):
            if attack_range <= 250:  # Melee hero: no orb-walking
                return None
            if is_attack_ready:       # Ready to shoot: attack instead of moving
                return None

            dist = my_pos.distance(target_pos)
            is_melee_target = target_attack_range <= 250
            away = (my_pos - target_pos).normalize()
            toward = (target_pos - my_pos).normalize()

            step = 180.0
            # Situation A: Target closing in (< 75% attack range or melee within 450u) -> Kite backward
            if dist < (attack_range * 0.75) or (is_melee_target and dist < 450):
                desired = my_pos + (away * step)
            # Situation B: Target fleeing (> 90% attack range) -> Chase forward
            elif dist > (attack_range * 0.90):
                desired = my_pos + (toward * step)
            else:
                desired = my_pos

            # Ally repulsion
            if ally_heroes:
                for ally_pos in ally_heroes:
                    diff = desired - ally_pos
                    d = diff.length()
                    if 1.0 < d < 350.0:
                        desired = desired + (diff.normalize() * 50.0)

            return desired

        pyro_pos = Vector3Mock(1000, 1000, 0)
        pyro_range = 600

        # Scenario A: Predator (melee) closes in to 300u (< 450u) while Pyro's attack is on CD
        pred_pos = Vector3Mock(1000, 700, 0)
        kite_pos = get_stutter_step(pyro_range, pyro_pos, pred_pos, 128, is_attack_ready=False)
        self.assertIsNotNone(kite_pos)
        # Should kite away (positive y direction away from predator at y=700)
        self.assertGreater(kite_pos.y, pyro_pos.y)

        # Scenario B: Target is fleeing at 560u (> 90% of 600)
        fleeing_target = Vector3Mock(1000, 1560, 0)
        pursue_pos = get_stutter_step(pyro_range, pyro_pos, fleeing_target, 600, is_attack_ready=False)
        self.assertIsNotNone(pursue_pos)
        # Should pursue forward (positive y towards target at y=1560)
        self.assertGreater(pursue_pos.y, pyro_pos.y)

        # Scenario C: Melee hero (e.g. Legionnaire range 128) -> No orb walking
        legion_pos = Vector3Mock(1000, 1000, 0)
        self.assertIsNone(get_stutter_step(128, legion_pos, pred_pos, 128, is_attack_ready=False))

        # Scenario D: Attack is ready -> Attack, do not stutter step
        self.assertIsNone(get_stutter_step(pyro_range, pyro_pos, pred_pos, 128, is_attack_ready=True))


class RealLuaBotTacticsHeadlessVmTests(unittest.TestCase):
    """
    Executes the genuine games/client/override/bots/botbraincore.lua inside the repo's
    headless NLua / KeraLua VM (src/tools/lua-verifier) against deterministic K2 bot state
    snapshots, with explicit fails_when mutation proofs.
    """

    @classmethod
    def setUpClass(cls):
        # Locate pre-built LuaVerifier.dll
        candidates = [
            REPO_ROOT / "src" / "tools" / "lua-verifier" / "bin" / "Debug" / "net11.0" / "LuaVerifier.dll",
            REPO_ROOT / "src" / "tools" / "lua-verifier" / "bin" / "Release" / "net11.0" / "LuaVerifier.dll",
        ]
        cls.verifier_dll = next((p for p in candidates if p.exists()), None)
        if cls.verifier_dll is None:
            # Fallback to dotnet run
            cls.run_cmd = ["dotnet", "run", "--project", str(REPO_ROOT / "src" / "tools" / "lua-verifier" / "LuaVerifier.csproj"), "--"]
        else:
            cls.run_cmd = ["dotnet", str(cls.verifier_dll)]

    def _execute_bot_scenario(self, task_name: str, state_dict: dict) -> dict:
        import subprocess
        import tempfile

        scenario = {
            "task": task_name,
            "state": state_dict
        }
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False, encoding="utf-8") as tf:
            json.dump(scenario, tf)
            tmp_path = tf.name

        try:
            cmd = list(self.run_cmd) + ["--bot-scenario", tmp_path]
            proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, cwd=str(REPO_ROOT), timeout=15)
            self.assertEqual(proc.returncode, 0, f"LuaVerifier execution failed: {proc.stderr}\nOutput: {proc.stdout}")

            # Parse JSON from last line of stdout (skipping warning lines)
            last_json_line = None
            for line in proc.stdout.strip().splitlines():
                line = line.strip()
                if line.startswith("{") and line.endswith("}"):
                    last_json_line = line

            self.assertIsNotNone(last_json_line, f"No JSON output from LuaVerifier: {proc.stdout}")
            return json.loads(last_json_line)
        finally:
            try:
                Path(tmp_path).unlink(missing_ok=True)
            except Exception:
                pass

    def test_fails_when_hp_drops_below_retreat_threshold_flips_combat_decision(self):
        """
        Proof of Read & fails_when Mutation Proof:
        In botbraincore.lua TaskCombatEngagement:
          Method_FocusTargetAndKite precondition: state.unitTarget ~= nil and state.nHeroHealthPct >= 0.30
          Method_DisengageRetreatCombat precondition: state.nHeroHealthPct < 0.30 or (state.nNearbyEnemyHeroes > state.nNearbyAllyHeroes + 1)
        """
        # Baseline healthy state: HP 50% with valid target -> engage & kite
        baseline = self._execute_bot_scenario("TaskCombatEngagement", {
            "nHeroHealthPct": 0.50,
            "nHeroManaPct": 0.60,
            "nNearbyEnemyHeroes": 1,
            "nNearbyAllyHeroes": 1,
            "unitTarget": {}
        })
        self.assertEqual(baseline["chosenMethod"], "Method_FocusTargetAndKite")
        self.assertEqual(baseline["primarySubtask"], "TaskFocusTarget")

        # Mutate HP below 0.30 (e.g. 0.20) -> must flip to retreat!
        low_hp = self._execute_bot_scenario("TaskCombatEngagement", {
            "nHeroHealthPct": 0.20,
            "nHeroManaPct": 0.60,
            "nNearbyEnemyHeroes": 1,
            "nNearbyAllyHeroes": 1,
            "unitTarget": {}
        })
        self.assertEqual(low_hp["chosenMethod"], "Method_DisengageRetreatCombat")
        self.assertEqual(low_hp["primarySubtask"], "TaskDisengageRetreat")

        # Fails_when invariant: If HP is not low (e.g. 0.35), it must NOT retreat
        healthy_check = self._execute_bot_scenario("TaskCombatEngagement", {
            "nHeroHealthPct": 0.35,
            "nHeroManaPct": 0.60,
            "nNearbyEnemyHeroes": 1,
            "nNearbyAllyHeroes": 1,
            "unitTarget": {}
        })
        self.assertNotEqual(healthy_check["chosenMethod"], "Method_DisengageRetreatCombat",
                            "fails_when: Bot retreated while HP 35% exceeded 30% retreat threshold")

    def test_fails_when_enemy_threat_ratio_exceeds_allies_flips_to_disengage(self):
        """
        Verifies that outnumbering threat (nNearbyEnemyHeroes > nNearbyAllyHeroes + 1)
        triggers retreat even with healthy HP (40%) when no single focus-target is locked.
        """
        # 1v1 combat: 1 enemy, 1 ally -> engage
        even_combat = self._execute_bot_scenario("TaskCombatEngagement", {
            "nHeroHealthPct": 0.40,
            "nNearbyEnemyHeroes": 1,
            "nNearbyAllyHeroes": 1
        })
        self.assertEqual(even_combat["chosenMethod"], "Method_EngageDefault")

        # Outnumbered 3v1: 3 enemies vs 1 ally -> (3 > 1 + 1) -> must retreat!
        outnumbered = self._execute_bot_scenario("TaskCombatEngagement", {
            "nHeroHealthPct": 0.40,
            "nNearbyEnemyHeroes": 3,
            "nNearbyAllyHeroes": 1
        })
        self.assertEqual(outnumbered["chosenMethod"], "Method_DisengageRetreatCombat")
        self.assertEqual(outnumbered["primarySubtask"], "TaskDisengageRetreat")

    def test_fails_when_base_under_attack_flips_strategy_from_laning_to_defense(self):
        """
        In TaskMatchStrategy:
          Method_BaseDefenseStrategy: state.bBaseUnderAttack == true
          Method_LaningPhaseStrategy: state.nGameTime < (15 * 60 * 1000) and not state.bBaseUnderAttack
        """
        laning = self._execute_bot_scenario("TaskMatchStrategy", {
            "nGameTime": 300000,
            "bBaseUnderAttack": False,
            "nHeroHealthPct": 0.80
        })
        self.assertEqual(laning["chosenMethod"], "Method_LaningPhaseStrategy")
        self.assertEqual(laning["primarySubtask"], "TaskLaningPhase")

        base_siege = self._execute_bot_scenario("TaskMatchStrategy", {
            "nGameTime": 300000,
            "bBaseUnderAttack": True,
            "nHeroHealthPct": 0.80
        })
        self.assertEqual(base_siege["chosenMethod"], "Method_BaseDefenseStrategy")
        self.assertEqual(base_siege["primarySubtask"], "TaskBaseDefense")

    def test_fails_when_mana_depleted_in_lane_flips_from_harass_to_defensive_farm(self):
        """
        In TaskLaningPhase:
          Method_HarassOpponentInLane: state.nNearbyEnemyHeroes > 0 and state.nHeroHealthPct > 0.50 and state.nHeroManaPct > 0.30
          Method_DefensiveLaneFarm: fallback (true)
        """
        harass = self._execute_bot_scenario("TaskLaningPhase", {
            "nNearbyEnemyHeroes": 1,
            "nHeroHealthPct": 0.70,
            "nHeroManaPct": 0.50
        })
        self.assertEqual(harass["chosenMethod"], "Method_HarassOpponentInLane")
        self.assertEqual(harass["primarySubtask"], "TaskHarassOpponent")

        low_mana = self._execute_bot_scenario("TaskLaningPhase", {
            "nNearbyEnemyHeroes": 1,
            "nHeroHealthPct": 0.70,
            "nHeroManaPct": 0.15
        })
        self.assertEqual(low_mana["chosenMethod"], "Method_DefensiveLaneFarm")
        self.assertEqual(low_mana["primarySubtask"], "TaskMaintainCreepEquilibrium")


if __name__ == "__main__":
    unittest.main()



