# Archived HoN Modification Forum Index (Wayback Reference)

Curated preservation index of high-signal developer and community threads from the archived official Heroes of Newerth Modification Subforum (`forums.heroesofnewerth.com/index.php?/forum/21-modifications/`), cross-checked against Project Kongor's retail x64 binaries (`k2_x64.dll`, `cgame_x64.dll`, `game_shared_x64.dll`).

> [!IMPORTANT]
> **Binary Ground-Truth Rule:** Forum posts span 2010–2021 (32-bit K2 through late Frostburn 4.10.x). Always cross-check commands, CVars, and XML tags against [`k2_entity_xml_schema.json`](./k2_entity_xml_schema.json) and [`scripts/vfs/sync_k2_engine_catalogs.ps1`](../../scripts/vfs/sync_k2_engine_catalogs.ps1) before relying on them in code.

---

## 1. Official Developer Guides & Engine Internals

| Topic ID | Title & Author | Wayback Snapshot | Key Verified Takeaways & Binary Cross-Check |
| :--- | :--- | :--- | :--- |
| **`topic/1546`** | *XML Scripting - A Primer* (`ElementUser`, Frostburn Lead Balance Dev) | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/1546-xml-scripting-a-primer/) | Primary reference for `.entity` types (`statenetaccum`), `<aura>` override-by-name in `<modifier>`, `propagatetoillusions="true"` default on `<on*>` events, leaf (`END_XML_PROCESSOR_NO_CHILDREN`) vs. branch combat actions, `<printdebuginfo />` / `<printvalue />` / `<drawdebugline />`, and `*_baseintelligence` vs. `*_intelligence` dynamic properties (`25` late-patch tags absent in our build are cataloged in [`k2-entity-xml-schema.md`](./k2-entity-xml-schema.md)). |
| **`topic/1644`** | *HoN 64-Bit Known Issues* (`HyperXewl` & `ElementUser`) | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/1644-hon-64-bit-known-issues/) | Documents x64-specific client behaviors: non-power-of-two (`pw^2`, e.g. `128x135`) item/UI icons render blurry on x64; embedded Chromium spawns child `hon_x64.exe` subprocesses that can hold file locks if orphaned; `d3d_exclusive` DPI scaling quirk on 4K displays (`Documents/Heroes of Newerth x64/game/startup.cfg`). |
| **`topic/19`** | *All HoN Assets Unzipped (`resources0.s2z`)* (`ElementUser`) | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/19-all-hon-assets-unzipped-resource0s2z/) | Documents loose-file VFS extraction hygiene (never edit `resources0.s2z` directly; mirror archive paths into override workspace). |
| **`topic/2322`** | *Fortuna's Dice (4.10.1 Community Mod Event)* (`ElementUser`) | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/2322-fortunas-dice/) | Showcases advanced pure-XML/Lua hero reworks and community ability design patterns integrated into official patches. |

---

## 2. Mod Manager, VFS Patching & UI Introspection Threads

| Topic ID | Title & Author | Wayback Snapshot | Key Verified Takeaways & Binary Cross-Check |
| :--- | :--- | :--- | :--- |
| **`topic/112`** | *HoN Mod Manager - Download (x64 & Lua Update)* (`Anakonda`, `Notausgang`) | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/112-hon-mod-manager-download/) | Documents `.honmod` `mod.xml` operations (`<editfile>`, `<find>`, `<findup>`, `<insert>`, `<replace>`, `<copyfile>`, `<stringtable>`), Lua `<editfile>` pitfalls (fixed in `Kongor.Modding` `FuzzyStringPatcher`), and console introspection commands (`uitriggerlist`, `uicmdlist`, `luafunctions`, `ui_debugScript`, `ui_debugHoverWidget`). |
| **`topic/1347`** | *Guide: How to Make / Edit / Update a HoN Mod* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/1347-guide-how-to-makeeditupdate-a-hon-mod/) | Documents `.honmod` manifest schema (`appversion`, `<incompatibility>`, `<requirement>`), `GameBind Impulse PGUP Ruler` distance tool, 3D debug overlays (`d_drawAreaAffectors`, `d_drawProjectileRadiuses`, `cg_drawNavGrid`; note `d_drawUnitBounds` from 32-bit is replaced by `vid_drawModelBounds` / `cg_drawNavGridUnits` in x64), and diagnosing silent hero disappearance from the selection grid. |
| **`topic/2091`** | *Custom Sound / Alt Avatar Voice Testing* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/2091-custom-sound-pack-question/) | Documents `ClientSpawnUnit <HeroEntity.AltX>` (`cgame_x64.dll`) for spawning Alt Avatars client-side in Practice Mode to test voice/SFX packs. |

---

## 3. UI Modernization Benchmarks (Community Reference Mods)

| Topic ID | Title | Wayback Snapshot | Relevance to Project Kongor UI (`override/ui/`) |
| :--- | :--- | :--- | :--- |
| **`topic/1674`** | *In-Game UI Updates (`game_v4.interface`)* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/1674-in-game-ui-updates/) | Community HUD layout and readability enhancements on top of `game_v4.interface`. |
| **`topic/1679`** | *Spectator UI Remastered* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/1679-spectator-ui-remastered/) | Replay and caster HUD layout patterns (`game_spectator.interface`). |
| **`topic/1759`** | *x64 Compatible Mod Pack Collection* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/1759-mods-for-64bit-client/) | Curated `.honmod` compatibility matrix for the 64-bit K2 client (`Kongor.Modding` regression corpus). |
| **`topic/2262`** | *HD Minimap Icons & Hero Indicators* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/2262-hd-minimap/) | High-DPI minimap icon sizing and `textures.s2z` precedence considerations. |
| **`topic/2274`** | *Spell / Cast Range Indicators* | [2021-12-07 Snapshot](https://web.archive.org/web/20211207092852/https://forums.heroesofnewerth.com/index.php?/topic/2274-range-indicators/) | Effect-based range ring `.effect` / `.entity` patterns. |
