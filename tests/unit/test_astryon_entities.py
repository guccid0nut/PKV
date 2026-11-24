# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Unit and validation tests for Astryon Gameplay Entities & Abilities XML (Milestone 4)."""

import os
import unittest
import xml.etree.ElementTree as ET

ASTRYON_DIR = os.path.abspath(
    os.path.join(
        os.path.dirname(__file__),
        "..",
        "..",
        "games",
        "client",
        "game",
        "heroes",
        "astryon",
    )
)


class TestAstryonEntities(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not os.path.isdir(ASTRYON_DIR):
            raise unittest.SkipTest(f"Astryon hero assets not unpacked on disk: {ASTRYON_DIR}")
    def test_all_xml_files_are_well_formed(self):
        """Verify that every .entity file parses without syntax errors."""
        entity_files = []
        for root, _, files in os.walk(ASTRYON_DIR):
            for f in files:
                if f.endswith(".entity"):
                    entity_files.append(os.path.join(root, f))

        self.assertGreaterEqual(
            len(entity_files),
            15,
            f"Expected at least 15 .entity files, found {len(entity_files)}",
        )
        for file_path in entity_files:
            try:
                tree = ET.parse(file_path)
                root = tree.getroot()
                self.assertIsNotNone(root, f"Root element missing in {file_path}")
            except Exception as exc:
                self.fail(f"Failed to parse XML in {file_path}: {exc}")

    def test_hero_entity_attributes(self):
        """Verify base attributes, combat ratings, and inventory bindings in hero.entity."""
        hero_file = os.path.join(ASTRYON_DIR, "hero.entity")
        self.assertTrue(os.path.isfile(hero_file), "hero.entity does not exist")
        tree = ET.parse(hero_file)
        root = tree.getroot()

        self.assertEqual(root.tag, "hero")
        self.assertEqual(root.attrib.get("name"), "Hero_Astryon")
        self.assertEqual(root.attrib.get("primaryattribute"), "Agility")

        # Base attributes and progression
        self.assertEqual(root.attrib.get("strength"), "22")
        self.assertEqual(root.attrib.get("strengthperlevel"), "2.4")
        self.assertEqual(root.attrib.get("agility"), "24")
        self.assertEqual(root.attrib.get("agilityperlevel"), "2.9")
        self.assertEqual(root.attrib.get("intelligence"), "18")
        self.assertEqual(root.attrib.get("intelligenceperlevel"), "1.8")

        # Combat and attack properties
        self.assertEqual(root.attrib.get("attacktype"), "melee")
        self.assertEqual(root.attrib.get("attackrange"), "150")
        self.assertEqual(root.attrib.get("attackcooldown"), "1700")
        self.assertEqual(root.attrib.get("attackdamagemin"), "28")
        self.assertEqual(root.attrib.get("attackdamagemax"), "34")
        self.assertEqual(root.attrib.get("attackactiontime"), "350")
        self.assertEqual(root.attrib.get("attackduration"), "700")

        # Base ratings and mobility
        self.assertEqual(root.attrib.get("movespeed"), "310")
        self.assertEqual(root.attrib.get("turnrate"), "450")
        self.assertEqual(root.attrib.get("armor"), "-0.16")
        self.assertEqual(root.attrib.get("magicarmor"), "5.5")
        self.assertEqual(root.attrib.get("carryrating"), "4")
        self.assertEqual(root.attrib.get("initiatorrating"), "4")

        # Inventory ability slots
        self.assertEqual(root.attrib.get("inventory0"), "Ability_Astryon1")
        self.assertEqual(root.attrib.get("inventory1"), "Ability_Astryon2")
        self.assertEqual(root.attrib.get("inventory2"), "Ability_Astryon3")
        self.assertEqual(root.attrib.get("inventory3"), "Ability_Astryon4")

    def test_ability_1_void_rift(self):
        """Verify Ability 1 (Void Rift) parameters and states."""
        ab1_file = os.path.join(ASTRYON_DIR, "ability_1.entity")
        self.assertTrue(os.path.isfile(ab1_file))
        tree = ET.parse(ab1_file)
        root = tree.getroot()

        self.assertEqual(root.tag, "ability")
        self.assertEqual(root.attrib.get("name"), "Ability_Astryon1")
        self.assertEqual(root.attrib.get("actiontype"), "target_position")
        self.assertEqual(root.attrib.get("range"), "600,700,800,900")
        self.assertEqual(root.attrib.get("targetradius"), "300")
        self.assertEqual(root.attrib.get("manacost"), "75,85,95,105")
        self.assertEqual(root.attrib.get("cooldowntime"), "12000,10500,9000,7500")

        # Check slow state
        slow_state = os.path.join(ASTRYON_DIR, "state_slow.entity")
        self.assertTrue(os.path.isfile(slow_state))
        s_tree = ET.parse(slow_state)
        s_root = s_tree.getroot()
        self.assertEqual(s_root.attrib.get("name"), "State_Astryon_Ability1_Slow")
        self.assertEqual(
            s_root.attrib.get("movespeedmultiplier"), "-0.20,-0.25,-0.30,-0.35"
        )
        self.assertEqual(
            s_root.attrib.get("attackspeed"), "-0.20,-0.25,-0.30,-0.35"
        )

        # Check buff state
        buff_state = os.path.join(ASTRYON_DIR, "state_buff.entity")
        self.assertTrue(os.path.isfile(buff_state))
        b_tree = ET.parse(buff_state)
        b_root = b_tree.getroot()
        self.assertEqual(b_root.attrib.get("name"), "State_Astryon_Ability1_Buff")

    def test_ability_2_chrono_fracture(self):
        """Verify Ability 2 (Chrono Fracture) parameters and stasis state."""
        ab2_file = os.path.join(ASTRYON_DIR, "ability_2.entity")
        self.assertTrue(os.path.isfile(ab2_file))
        tree = ET.parse(ab2_file)
        root = tree.getroot()

        self.assertEqual(root.tag, "ability")
        self.assertEqual(root.attrib.get("name"), "Ability_Astryon2")
        self.assertEqual(root.attrib.get("actiontype"), "target_position")
        self.assertEqual(root.attrib.get("range"), "750")
        self.assertEqual(root.attrib.get("targetradius"), "250")
        self.assertEqual(root.attrib.get("manacost"), "90,100,110,120")
        self.assertEqual(root.attrib.get("cooldowntime"), "16000,14000,12000,10000")

        # Check stasis state
        stasis_state = os.path.join(ASTRYON_DIR, "state_stasis.entity")
        self.assertTrue(os.path.isfile(stasis_state))
        s_tree = ET.parse(stasis_state)
        s_root = s_tree.getroot()
        self.assertEqual(s_root.attrib.get("name"), "State_Astryon_Ability2_Stasis")
        self.assertEqual(s_root.attrib.get("stunned"), "true")
        self.assertEqual(s_root.attrib.get("silenced"), "true")
        self.assertEqual(s_root.attrib.get("disarmed"), "true")
        self.assertEqual(s_root.attrib.get("armor"), "-2,-4,-6,-8")
        self.assertEqual(s_root.attrib.get("magicarmor"), "-2.0,-3.0,-4.0,-5.0")

    def test_ability_3_void_aegis(self):
        """Verify Ability 3 (Void Aegis) passive and shield mechanics."""
        ab3_file = os.path.join(ASTRYON_DIR, "ability_3.entity")
        self.assertTrue(os.path.isfile(ab3_file))
        tree = ET.parse(ab3_file)
        root = tree.getroot()

        self.assertEqual(root.tag, "ability")
        self.assertEqual(root.attrib.get("name"), "Ability_Astryon3")
        self.assertEqual(root.attrib.get("actiontype"), "no_target")
        self.assertEqual(root.attrib.get("manacost"), "50,60,70,80")
        self.assertEqual(root.attrib.get("cooldowntime"), "18000,15000,12000,9000")

        # Check passive momentum state
        passive_state = os.path.join(ASTRYON_DIR, "state_passive.entity")
        self.assertTrue(os.path.isfile(passive_state))
        p_tree = ET.parse(passive_state)
        p_root = p_tree.getroot()
        self.assertEqual(p_root.attrib.get("name"), "State_Astryon_Ability3_Passive")
        self.assertEqual(p_root.attrib.get("maxcharges"), "5")
        self.assertEqual(p_root.attrib.get("movespeedpercharge"), "4,6,8,10")
        self.assertEqual(p_root.attrib.get("damagepercharge"), "3,5,7,9")

        # Check shield state
        shield_state = os.path.join(ASTRYON_DIR, "state_shield.entity")
        self.assertTrue(os.path.isfile(shield_state))
        sh_tree = ET.parse(shield_state)
        sh_root = sh_tree.getroot()
        self.assertEqual(sh_root.attrib.get("name"), "State_Astryon_Ability3_Shield")

    def test_ability_4_singularity_collapse(self):
        """Verify Ability 4 (Singularity Collapse) parameters, affector, and states."""
        ab4_file = os.path.join(ASTRYON_DIR, "ability_4.entity")
        self.assertTrue(os.path.isfile(ab4_file))
        tree = ET.parse(ab4_file)
        root = tree.getroot()

        self.assertEqual(root.tag, "ability")
        self.assertEqual(root.attrib.get("name"), "Ability_Astryon4")
        self.assertEqual(root.attrib.get("actiontype"), "target_position")
        self.assertEqual(root.attrib.get("range"), "900")
        self.assertEqual(root.attrib.get("targetradius"), "450")
        self.assertEqual(root.attrib.get("manacost"), "150,225,300")
        self.assertEqual(root.attrib.get("cooldowntime"), "110000,95000,80000")

        # Check affector
        affector_file = os.path.join(ASTRYON_DIR, "affector_singularity.entity")
        self.assertTrue(os.path.isfile(affector_file))
        af_tree = ET.parse(affector_file)
        af_root = af_tree.getroot()
        self.assertEqual(af_root.attrib.get("name"), "Affector_Astryon_Ability4")
        self.assertEqual(af_root.attrib.get("radius"), "450")
        self.assertEqual(af_root.attrib.get("lifetime"), "4500")

        # Check silence state
        silence_state = os.path.join(ASTRYON_DIR, "state_silence.entity")
        self.assertTrue(os.path.isfile(silence_state))
        sil_tree = ET.parse(silence_state)
        sil_root = sil_tree.getroot()
        self.assertEqual(sil_root.attrib.get("name"), "State_Astryon_Ability4_Silence")
        self.assertEqual(sil_root.attrib.get("silenced"), "true")
        self.assertEqual(sil_root.attrib.get("disarmed"), "true")

        # Check SotM modifier and states
        sotm_state = os.path.join(ASTRYON_DIR, "state_sotm.entity")
        self.assertTrue(os.path.isfile(sotm_state))
        sotm_tree = ET.parse(sotm_state)
        sotm_root = sotm_tree.getroot()
        self.assertEqual(sotm_root.attrib.get("name"), "State_Astryon_Ability4_Sotm_Debuff")
        self.assertEqual(sotm_root.attrib.get("incomingdamagemultiplier"), "1.25")

    def test_modular_ability_subdirectories(self):
        """Verify that ability_01 through ability_04 exist and contain valid ability entities."""
        for i in range(1, 5):
            subdir = os.path.join(ASTRYON_DIR, f"ability_{i:02d}")
            self.assertTrue(
                os.path.isdir(subdir), f"Subdirectory {subdir} does not exist"
            )
            ab_file = os.path.join(subdir, "ability.entity")
            self.assertTrue(
                os.path.isfile(ab_file), f"ability.entity missing in {subdir}"
            )
            tree = ET.parse(ab_file)
            root = tree.getroot()
            self.assertEqual(root.attrib.get("name"), f"Ability_Astryon{i}")


if __name__ == "__main__":
    unittest.main()
