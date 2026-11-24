# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
test_learnatorium_items_vfs_parity.py

Protects: Permanent 1:1 parity between HeroDefinitionsCache (ShopCategories, Items,
Recipes, CanonicalItemNames) and the mounted client VFS (resources0..99.s2z,
override/, entities_en.str) across all 10 Learnatorium item tabs (163 unique items).
Fails when: Any Learnatorium shop tab drifts from shop_*.entity, introduces a duplicate
item or hallucinated entity key, displays a 0g/scroll-only price instead of recursive
assembled gold cost, or drifts from entities_en.str menu-mode display names.
Seam: Real .s2z archives + LuaVerifier execution of hero_definitions_cache.lua.
"""
import glob
import json
import os
import re
import subprocess
import unittest
import xml.etree.ElementTree as ET
import zipfile

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LEARNATORIUM_TABS = [
    "Shop_Supplies",
    "Shop_Accessories",
    "Shop_Weapons",
    "Shop_Relics",
    "Shop_Secret",
    "Shop_Recipes1",
    "Shop_Recipes2",
    "Shop_Recipes3",
    "Shop_Recipes4",
    "Shop_Recipes5",
]


class LearnatoriumItemsVfsParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.vfs_entities = {}
        cls.str_entries = {}

        def _parse_str_text(text: str) -> None:
            for raw_line in text.splitlines():
                line = raw_line.strip()
                if not line or line.startswith("//"):
                    continue
                parts = raw_line.split("\t")
                if len(parts) >= 2:
                    key = parts[0].strip()
                    val = parts[-1].strip()
                    if key and val:
                        cls.str_entries[key] = val

        def _natural_s2z_key(p: str):
            return [int(x) if x.isdigit() else x.lower() for x in re.split(r"(\d+)", os.path.basename(p))]

        s2z_paths = sorted(glob.glob(os.path.join(REPO_ROOT, "games", "client", "game", "resources*.s2z")), key=_natural_s2z_key)
        for zp in s2z_paths:
            with zipfile.ZipFile(zp, "r") as zf:
                for entry in zf.namelist():
                    low = entry.lower()
                    if low.startswith("items/") and low.endswith(".entity"):
                        cls.vfs_entities[low] = zf.read(entry).decode("utf-8", errors="replace")
                    elif low.endswith("entities_en.str"):
                        _parse_str_text(zf.read(entry).decode("utf-8", errors="replace"))

        # Overlay mounted loose directories in K2 VFS precedence order (game/ -> ClientPackageFiles/ -> override/)
        for loose_root in (
            os.path.join(REPO_ROOT, "games", "client", "game"),
            os.path.join(REPO_ROOT, "games", "client", "ClientPackageFiles"),
            os.path.join(REPO_ROOT, "games", "client", "override"),
        ):
            items_dir = os.path.join(loose_root, "items")
            if os.path.isdir(items_dir):
                for droot, _, files in os.walk(items_dir):
                    for fn in files:
                        if fn.lower().endswith(".entity"):
                            full_p = os.path.join(droot, fn)
                            rel_low = os.path.relpath(full_p, loose_root).replace("\\", "/").lower()
                            with open(full_p, "r", encoding="utf-8", errors="replace") as ef:
                                cls.vfs_entities[rel_low] = ef.read()
            str_file = os.path.join(loose_root, "stringtables", "entities_en.str")
            if os.path.isfile(str_file):
                with open(str_file, "r", encoding="utf-8", errors="replace") as sf:
                    _parse_str_text(sf.read())

        cls.vfs_shops = {}
        cls.vfs_items = {}
        for path, xml_text in sorted(cls.vfs_entities.items()):
            try:
                root = ET.fromstring(xml_text)
            except ET.ParseError:
                continue
            tag = root.tag.lower()
            name = root.attrib.get("name", "")
            if tag == "shop" and name:
                cls.vfs_shops[name] = [
                    el.attrib["name"] for el in root.findall("item") if el.attrib.get("name")
                ]
            elif tag == "item" and name:
                comps_attr = (root.attrib.get("components", "") or "").strip()
                first_recipe = comps_attr.split(",")[0].strip() if comps_attr else ""
                comps = [c for c in first_recipe.split() if c]
                scroll_cost = int(float(root.attrib.get("cost", "0") or "0"))
                cls.vfs_items[name] = {
                    "scroll_cost": scroll_cost,
                    "components": comps,
                    "path": path,
                }

    def _vfs_total_cost(self, item_name, visited=None):
        visited = visited or set()
        if item_name in visited:
            return 0
        info = self.vfs_items.get(item_name)
        if not info:
            return 0
        total = info["scroll_cost"]
        next_visited = set(visited)
        next_visited.add(item_name)
        for comp in info["components"]:
            total += self._vfs_total_cost(comp, next_visited)
        return total

    def test_lua_shop_items_match_vfs_shops_prices_and_names_one_to_one(self):
        lua_verifier = os.path.join(
            REPO_ROOT, "src", "tools", "lua-verifier", "bin", "Release", "net11.0", "LuaVerifier.exe"
        )
        self.assertTrue(
            os.path.exists(lua_verifier),
            f"Missing LuaVerifier.exe at {lua_verifier}. Build with: dotnet build src/tools/lua-verifier -c Release",
        )

        probe_dir = os.path.join(REPO_ROOT, "artifacts", "temp_probes")
        os.makedirs(probe_dir, exist_ok=True)
        probe_lua = os.path.join(probe_dir, "_learnatorium_vfs_parity_probe.lua")
        probe_json = os.path.join(probe_dir, "_learnatorium_vfs_parity_out.json")
        try:
            with open(probe_lua, "w", encoding="utf-8") as f:
                f.write(
                    f"""
_G.Echo = _G.Echo or function() end
_G.Translate = nil
dofile('games/client/override/ui/scripts/newui/hero_definitions_data.lua')
dofile('games/client/override/ui/scripts/newui/hero_definitions_cache.lua')
local HDC = _G.HeroDefinitionsCache
local tabs = {{ 'Shop_Supplies','Shop_Accessories','Shop_Weapons','Shop_Relics','Shop_Secret','Shop_Recipes1','Shop_Recipes2','Shop_Recipes3','Shop_Recipes4','Shop_Recipes5' }}
local out = io.open({json.dumps(probe_json.replace(os.sep, '/'))}, 'w')
for _, tab in ipairs(tabs) do
    local list = HDC.GetShopItems(tab)
    for idx, it in ipairs(list) do
        out:write(string.format('%s\\t%d\\t%s\\t%d\\t%d\\t%s\\t%s\\n',
            tab, idx, tostring(it.itemName), tonumber(it.cost) or 0, tonumber(it.costOnly) or 0,
            tostring(it.displayName), table.concat(it.components or {{}}, ',')))
    end
end
out:close()
"""
                )
            proc = subprocess.run(
                [lua_verifier, probe_lua],
                cwd=REPO_ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                check=False,
            )
            self.assertEqual(proc.returncode, 0, f"LuaVerifier failed:\n{proc.stdout}\n{proc.stderr}")

            with open(probe_json, "r", encoding="utf-8") as f:
                rows = [line.rstrip("\r\n").split("\t") for line in f if line.strip()]
        finally:
            for p in (probe_lua, probe_json):
                if os.path.exists(p):
                    os.remove(p)

        lua_by_tab = {t: [] for t in LEARNATORIUM_TABS}
        seen_entities = set()
        seen_names = set()
        for tab, _idx, ent_name, total_str, scroll_str, disp_name, comps_csv in rows:
            total_cost = int(total_str)
            scroll_cost = int(scroll_str)
            comps = [c for c in comps_csv.split(",") if c]
            lua_by_tab[tab].append(ent_name)

            self.assertNotIn(ent_name, seen_entities, f"Duplicate entity across Learnatorium tabs: {ent_name}")
            self.assertNotIn(disp_name, seen_names, f"Duplicate displayName across Learnatorium tabs: {disp_name}")
            seen_entities.add(ent_name)
            seen_names.add(disp_name)

            self.assertIn(ent_name, self.vfs_items, f"Item {ent_name} missing from VFS /items/")
            expected_total = self._vfs_total_cost(ent_name)
            self.assertEqual(
                total_cost,
                expected_total,
                f"Total assembled gold mismatch for {ent_name}: Lua={total_cost}, VFS={expected_total}",
            )
            self.assertGreater(total_cost, 0, f"Zero-cost item in Learnatorium: {ent_name}")
            self.assertEqual(
                comps,
                self.vfs_items[ent_name]["components"],
                f"Primary recipe components mismatch for {ent_name}",
            )
            if comps:
                self.assertEqual(
                    scroll_cost,
                    self.vfs_items[ent_name]["scroll_cost"],
                    f"Recipe scroll cost mismatch for {ent_name}",
                )

            expected_disp = self.str_entries.get(f"{ent_name}_name")
            if expected_disp:
                self.assertEqual(
                    disp_name,
                    expected_disp,
                    f"Menu-mode displayName drift vs entities_en.str for {ent_name}: Lua='{disp_name}' != VFS='{expected_disp}'",
                )

        for tab in LEARNATORIUM_TABS:
            self.assertEqual(
                lua_by_tab[tab],
                self.vfs_shops[tab],
                f"Shop tab {tab} item list mismatch vs VFS shop_*.entity",
            )


if __name__ == "__main__":
    unittest.main()
