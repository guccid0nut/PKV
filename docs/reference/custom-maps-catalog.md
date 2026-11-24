# Project Kongor Seasonal, Custom & Classic Maps Catalog

This reference documents all seasonal Halloween map rotations and HoN 4.10-repaired community/classic `.s2z` map archives verified and shipped across `games/client/game/maps/` and `games/server/game/maps/`.

---

## 1. Automatic Seasonal Halloween Maps (Wave A)

Built deterministically via [`build_seasonal_halloween_maps.py`](../../scripts/assets/build_seasonal_halloween_maps.py) in strict Zip32 (`allowZip64=False`) format and rotated automatically during Halloween (`Oct 18–Nov 4` UTC default window, or whenever `cg_seasonTheme == 'halloween'`) via [`SeasonalMapResolver.cs`](../../src/services/chat-server/Services/SeasonalMapResolver.cs) across all Online TMM & Co-Op Bot queues (`IncludeRankedQueues = true`) as well as offline Bot Match and Practice launchers (`bot_match.lua`, `new_player_experience.lua`, `regions.lua`, `store2.lua`, `main_v2.lua`).

| Archive (`maps/<slug>.s2z`) | Title | Size (Bytes) | SHA-256 | Notes & 4.10 Upgrades |
| --- | --- | ---: | --- | --- |
| `midwars_halloween.s2z` | Mid Wars (Halloween) | `4,252,698` | `1a055eac1733aec6031712d421e3af0cb9ae7e6f082f48f06a00eaf7e93427e1` | Classic S2 Games Halloween Mid Wars terrain + `Neutral_HeadlessBoss` (`HeadlessBoss_controller`) merged with HoN 4.10 `midwars.s2z` river rune boat, boat spawner, `midwarRuneWay_0..5` waypoints, and 4.10 `resources/` balance parity. |
| `caldavar_halloween.s2z` | Forests of Caldavar (Halloween) | `1,701,395` | `6ab1f42481f70408e2d580270d8dd2e6b680c63a9d19dfa2712f5d057a8e74b1` | Official S2 Games / Frostburn Halloween Forests of Caldavar 5v5 map archive. |

> **Winter Overlays Policy:** Community/AI `.honmod` texture overlays (`frozen_caldavar`, `frozen_midwars`) remain **manual-only** in Willowmaker's Mod Manager so players are never subjected to missing-archive disconnects if a `.honmod` is not enabled locally.

---

## 2. Promoted Custom & Classic Maps (`13 / 14` Verified — Wave B)

Built and deployed deterministically via [`build_custom_maps_catalog.py`](../../scripts/assets/build_custom_maps_catalog.py) (`--deploy`, deterministic `ZipInfo` timestamps + mandatory `ElementTree` XML validation gate across all `.entity`, `.interface`, `.package`, `.mdf`, `.material`, `.effect`, and `.gamemechanics` entries). Every promoted archive below was live-tested in `hon_x64.exe` (`Vulkan 1.3 / DXVK`, `143–144 FPS`), visually inspected in active gameplay (`GamePhase == 3`), protected in [`ModManagerViewModel.cs`](../../src/apps/willowmaker/ViewModels/ModManagerViewModel.cs), wired into Hosted Games / Loading UI metadata ([`player_hosted_v2.lua`](../../games/client/override/ui/scripts/newui/player_hosted_v2.lua), [`loading_v2.lua`](../../games/client/override/ui/scripts/newui/loading_v2.lua)), verified for 100% SHA-256 parity across `32 / 32` client and server `maps/*.s2z` archives, and verified against the Cross-Match Mechanics Isolation Canary (`Custom Map -> Caldavar` in a single client process).

| Slug (`maps/<slug>.s2z`) | Display Title | Category | Community / Archive Credit | Size (Bytes) | SHA-256 | HoN 4.10 Repairs Applied |
| --- | --- | --- | --- | ---: | --- | --- |
| `three_kingdoms` | Three Kingdoms (5v5) | Classic 3-Lane | HoN Custom Map Contest Community (`ThreeKingdoms.s2z`) | `1,137,756` | `ec12755cb6ec6fa192b8d3557998551a1562594003c5c308f80339375283f776` | Stripped 2 legacy `Prop_Water` entities; remapped 34 pre-4.0 tower/model paths in `entitylist`. |
| `persian_pass` | Persian Pass (4v4) | Classic 2-Lane | HoN Custom Map Contest Community (`PersianPass.s2z`) | `455,841` | `f2a63836fcbc672ae65892ca3f676bee0b1e68c10c4c12044890544626074e10` | Stripped 2 legacy `Prop_Water` entities; remapped 6 pre-4.0 tower/model paths in `entitylist`. |
| `archers` | Archer Wars | Skillshot Arena | HoN Custom Maps Community (`Archers104.s2z`) | `587,505` | `91bfccf8675e9ba58fa7ebb0afe776d347f0c0f3eaa75cf2eb8effce6307e4f6` | Hoisted additive mechanics tags to global 4.10 `base.gamemechanics`; fixed `Gadget_Water400` root name typo; namespaced `cm_archers_State_Hero_Spawner`; mapped `Hero_Archer` HUD icon. |
| `valkwars` | Valkyrie Wars | Skillshot Arena | HoN Custom Maps Community (`valkwars13.s2z`) | `1,611,370` | `8b47f3ad2d65e13322660699903e75c1a0d6e1706cc50882c05dd27960a1d5cd` | Hoisted additive mechanics tags (`DefuseKit` + `DefuseKitImmunity`); added `Gadget_Loc_1` marker; mapped `Javelin`/`SuperJavelin` to `BindingProjectile`/`SuperiorMagic` within K2's 32-bit `uint` `effecttype` bitmask; namespaced 2 spawner entities; relocated 1 colliding `.entity` path. |
| `pebbles_duel` | Pebbles Duel | 1v1 / Arena | HoN Custom Maps Community (`Pebbles_Duel_v1.3.s2z`) | `312,075` | `a40597892e2a3df928940d95416e7312c0710d24344d640e2ee7cd349c3b0627` | Hoisted additive mechanics tags; mapped `Hero_Rocky` HUD icon/portrait (`hero.tga` -> `icon.tga`); namespaced 2 colliding spawner entities. |
| `truedevo` | True Devo Wars | Skillshot Arena | HoN Custom Maps Community (`truedevo_a19_2.s2z`) | `912,130` | `404b0f893eef633e9e766160ff1a0bd08cbcead6673e9ade874588b02a1c353c` | Stripped 2 `Prop_Water` entities; hoisted additive mechanics tags; wrapped bare `<expirestate>` inside `<oninflict>` across 6 `cooldownred/state_red*.entity` files; relocated invalid `<setpos0>`/`<playeffect>` tags out of `impact_kill.effect` into `Projectile_Devourer_Ability1` headshot kill branch; namespaced 18 colliding map entities; `B`-key Outpost shop default & crisp upgrade icons. |
| `arena` | HoN Arena | Hero Brawler | HoN Custom Maps Community (`arena006.s2z`) | `166,781` | `38f9a5244297640d04d5199bb3fee2bb6a632d904a49d3b4532ec4e172fc0f8e` | Hoisted additive mechanics tags; removed 2 obsolete HoN 2.x `resources/ui/game*.interface` overrides; added explicit `<onframe />` inside `arena_State_Main` `interim` modifier; relocated 141 custom `arena_*` `.entity` files to `resources/cm_arena/`. |
| `colosseum_duel` | Colosseum Duel | Gladiator Duel | HoN Custom Maps Community (`colosseum_duel_001.s2z`) | `9,170,952` | `29ec4d59b1353d4b721ccc517ffbe76d58a2ba338d0b2040c8f6d9c5b025a8a4` | Relocated 2 colliding `.entity` paths to `resources/cm_colosseum_duel/`; 150-HP custom Nomad 1v1 reflex duel & combat-pit bot waypoints. |
| `empath_maze` | Empath Maze | Minigame / Maze | HoN Custom Maps Community (`EmpathMaze001.s2z`) | `280,800` | `c596471dbb0f73f7ec196ca56ee83f5f40fc4dacb0b6f80fbb8c746f83d32497` | Relocated 3 colliding `.entity` paths to `resources/cm_empath_maze/`; labyrinth bot patrol waypoints & team-life setup guard. |
| `enfos` | Enfo's Team Survival | Co-Op Wave Defense | HoN Custom Maps Community — WC3 Enfo's Tribute (`enfos_007.s2z`) | `2,361,633` | `b012f028c41c6336de6407b6a227644267424ceca3133e41303518f6f6e8e7e2` | Hoisted additive mechanics tags; removed 1 obsolete HoN 2.x `resources/ui/game.interface` override; namespaced 2 colliding spawner entities; repaired bronze medallions, 9-slot Shared Spellbringer bar & 8-pedestal inspector. |
| `nightmarish_coop` | Nightmarish Co-Op | Co-Op Campaign | HoN Custom Maps Community (`NightmarishCOOP_002.s2z`) | `4,177,288` | `ced08f85783bcd2964df7ce433d460fddf42c3747efe6f052a53560cd6f817ba` | Hoisted additive mechanics tags; removed 1 obsolete HoN 2.x UI override; remapped 5 pre-4.0 tower/courier/`hydralisk` paths; repaired malformed `<modifier key="hardcore">` XML in 2 `n_defiler*.entity` files; namespaced 6 colliding entities; relocated 13 `.entity` paths; solo-start, 8-hero curated roster, 60s pick timer & welcome briefing. |
| `mids_omg` | Mid Wars Classic (Watchtower) | Classic Brawl | S2 Games / HoN Classic Archive (`x64_MIDS_OMG.s2z`) | `1,183,322` | `f312bcfa3c587715aa5cade1c768cd6d0be3c09cc102772cf0c9f32310d6ce0d` | Remapped 1 legacy 3.x model path in `entitylist`. |
| `caldavar_wtf` | Forests of Caldavar (WTF) | Zero-CD Sandbox | HoN Custom Maps Community (`newwtf_a14.s2z`) | `1,862,118` | `4d3e7fe504997f4a43b7fd98d072aa627162d261cce17a5485f5da7870de6668` | Closed missing `</ontimer>` XML tag in `Ability_EmeraldWardenpet1`; namespaced 3 colliding entities (`Gadget_FluxDummyReveal`, `State_BloodChalice`, `State_BloodChalice_Aura`); relocated 14 colliding `.entity` paths. |

---

## 2B. 20-Player (`10v10`) Chaos Custom Maps

Built deterministically via [`build_10v10_chaos_maps.py`](../../scripts/assets/build_10v10_chaos_maps.py) in strict Zip32 (`allowZip64=False`) format with XML validation, full `DevourerBot` & `TeamBotBrain` 20-player bot support (`metadata.lua`, `teambotbrain.lua`, `devourer_main.lua`), and `10v10` (`teamsize:10`) Hosted Games UI & overflow lobby team-join headers (`player_hosted_v2.package`, `lobby_v2.package`, `player_hosted_v2.lua`).

| Slug (`maps/<slug>.s2z`) | Display Title | Category | Base Archive | Size (Bytes) | SHA-256 | 20-Player (`10v10`) Engineering Notes |
| --- | --- | --- | --- | ---: | --- | --- |
| `midwars_10v10` | Mid Wars (10v10 Chaos) | 10v10 Brawl | HoN 4.10 `midwars.s2z` | `4,481,674` | `23a527b80cdd06988a4cc07862a0fccf06ffbdb9b891b1fc644f419817d8c596` | `teamsize="10"` (`maxplayersperteam="10"`), 4.10 river rune boat & boss buffs preserved, full 20-hero Mid Wars bot & player support. |
| `devowars_10v10` | Devo Wars (10v10 Chaos) | 10v10 Skillshot Arena | HoN 4.10 `devowars.s2z` | `308,020` | `9f206c2574ff3b8e2b12e186dc7b8afd69feca54c106ce4f44ab2f159eec178c` | Added `LegionSpawn6..10` & `HellbourneSpawn6..10` river bank spawn points; `<modifier key="devowars_10v10" modpriority="120">` overrides `game_logic` to 10 spawns/team + 100-kill score goal while preserving `devowars` `forcepickhero="Hero_Devourer"`; `botmatch` enabled in `sv_worldMapGameModes`. |
| `truedevo_10v10` | True Devo Wars (10v10 Chaos) | 10v10 Skillshot Arena | HoN 4.10-repaired `truedevo.s2z` | `912,959` | `cbb5861a2ea642c542383a094020010d60eb5d0f630d3bd1643c9e9754d59e57` | Added `TrueDevoLegionSpawn1..10` & `TrueDevoHellbourneSpawn1..10` + 20 spread `Hero_Devourer` spawns; scaled team lives to `75` (`maxcharges="150"`); namespaced map-private entities/paths under `cm_truedevo_10v10_`. |

---

## 3. Skipped & Superseded Archives (Not Shipped in `maps/`)

Cataloged locally under git-ignored `games/custom_maps_catalog/skipped/` and `games/custom_maps_catalog/raw/superseded/` for historical preservation:

### Skipped Unfinished Map (`1`) & Non-Map UI Archive (`1`)
- **`SurvivalMode.s2z` (`survival_mode.s2z`)**: Skipped (`games/custom_maps_catalog/skipped/survival_mode.s2z`). Unfinished HoN 2.0 single-player prototype (`<world name="untitled" image="">`, missing `tiledefinitionmap` causing black terrain, truncated UTF-16 `resources/game_settings.cfg`, malformed UI trigger text pasted inside `resources/game_info.entity`, `nolobby="true"` hardcoded to a single `player_hero` entity, missing spawner targets `W1Zombie2`/`W10Skeleton2`, and removed pre-3.0 Master of Arms clips/icons/effects).
- **`resourcesCustomUI.s2z`**: Skipped (`games/custom_maps_catalog/skipped/resourcesCustomUI.s2z`). Legacy HoN 2.x direct-IP UI mod (`ui/fe2/ipconnect.package`) authored by `xewl`; contains no `worldconfig` or map data.

### Superseded Duplicate / Alpha Prototypes (`8`)
- **`d3vowars.s2z`** -> Superseded by official shipped `devowars.s2z` (`42/45` files identical; `devowars.s2z` adds minimap & icon).
- **`prophet003.s2z`** -> Superseded by official shipped `prophets.s2z` (early Warlock Brawl prototype with `24` `wl_*` collisions).
- **`Nightmarish_004.s2z`** -> Superseded by newer `NightmarishCOOP_002.s2z` (`nightmarish_coop.s2z`).
- **`PEBBLES_DUEL_007.s2z`** -> Superseded by newer `Pebbles_Duel_v1.3.s2z` (`pebbles_duel.s2z`).
- **`truedevo_a18.s2z`** -> Superseded by newer `truedevo_a19_2.s2z` (`truedevo.s2z`).
- **`WTF001.s2z`**, **`realwtf.s2z`**, **`unrealwtf.s2z`** -> Superseded by newer `newwtf_a14.s2z` (`caldavar_wtf.s2z`).

---

## 4. K2 Engine Architecture Notes (`CGameMechanics` & `CEntityRegistry` Isolation)

1. **32-Bit `uint` `<effecttype>` Bitmask (`game_shared_x64.dll` `CGameMechanics::LookupEffectType` @ `0x1804c7b90`):**
   - K2 stores `<effecttype>` flags in a 32-bit `uint` (`1u << index`, max `32` entries). Vanilla HoN 4.10 defines `31` `<effecttype>` entries (`bits 0..30`). Global `base.gamemechanics` adds `<effecttype name="DefuseKit" />` in the final legal slot (`bit 31 = 0x80000000`) alongside `54` purely additive `<targetscheme>`, `<immunitytype>`, `<ping>`, and `<popup>` definitions, while `valkwars`'s `Javelin` and `SuperJavelin` map to `BindingProjectile` and `SuperiorMagic`.
2. **Zero-Stall Map Transitions & Cross-Match Entity Isolation:**
   - When a custom map bundles its own `resources/base.gamemechanics`, unmounting the map's `server:curgame_world` / `client:curgame_world` resource context forces `CResourceManager` to re-parse all `~6,000+` dependent `.entity` definitions (`180+ s` main-thread stall) and crashes in `CGameMechanics::PostLoad` if HoN 4.10 combat types are missing. Hoisting additive tags into global `base.gamemechanics` and stripping per-map `resources/base.gamemechanics` reduces map-to-map transitions to `~5–8 seconds`.
   - Relocating custom-named entities off vanilla `.entity` VFS paths (`resources/cm_<slug>/<mounted>`) ensures `CResourceManager` cleanly unregisters map-private entities and reloads vanilla hero definitions from `resources0.s2z` on map exit, while [`MatchServerManagerSupervisor.cs`](../../src/apps/compel/Services/Supervision/MatchServerManagerSupervisor.cs) recycles dedicated COMPEL `hon_server` workers (`forceCustomMapRecycle`) after every custom map match.
