# 🚀 PROJECT KONGOR V (5.0, 5.0.1 & 5.0.2 Update Notes)
### *Modernizing Newerth: Grounded Engineering, Classic Maps & Community Preservation*

> *"Went a bit crazy obsessing on PROJECT KONGOR over this summer. Spent many nights up till 5 AM just playing around trying to add a lot of things that I felt were missing during retail, updating older legacy tech to modern standards, and seeing how far we could push the engine.*
>
> *The result is **Project Kongor V**."*
>
> — **gucci** 🍩

---

## ✨ Version 5.0.2 — War Effort Quests, Matchmaking & Clan Fixes, Queue Arcade Slot Swaps & Custom Map Updates
* **War Effort Quest & 14-Bag Reward Track *(Initial Rollout)***: Replaced the unused `HON LIVE` homepage midbar button with **War Effort** (Replays stay in the top System Bar). Track `2` rotating daily objectives with incremental progress counts (`0/1`, `10/10`) across classic HoN modes, faction-neutral melee/ranged roles, and custom Kongor modes (including **Strikers** goals/saves, **10v10**, wards, assists, and building damage), reroll quests daily at `00:00 UTC` (up to `2` banked), work through `14` Merrick bags/chests over `28`-day cycles for Silver, Gold, Plinko Tickets, and War Effort items (`Dragon's Treasure Ward` & `Gnome Courier`), and view the **War Effort Leaderboard** *(quest tuning and UI polish are ongoing; includes a `720p` layout fix, Linux path casing fix, and a server-side `WarEffort:Enabled` toggle)*.
* **Matchmaking Queue Fix & Unique Clan Tags/Names**: Fixed a Valkey/Garnet script and cache serialization bug in `ChatServer` that could stall matchmaking queue pairing, and added case-insensitive unique checks for **Clan Tags** and **Clan Names** so duplicate clan names/tags are rejected.
* **Queue Arcade Bot & Player Slot Swaps**: Improved mid-skirmish human ↔ bot slot handoffs on Windows and Linux Queue Arcade servers. When a player joins, disconnects, or gets pulled into a matchmaking game, a bot can step back into the slot (or yield to an incoming player) with updated scoreboard names, mid-match bot brain rebinding, and `[Queue Arcade]` chat notices.
* **Custom Map Stats, Match History & Scoreboards**: Added custom gameplay stat tracking, match history entries, base hero portraits/names for `HoN Arena` units, and mode-specific scoreboards across **Strikers** (Goals/Own-Goals/Assists/Saves), **20-Player (`10v10`)** compact rows, **Devo Wars**, **HoN Arena**, **Prophets**, **Team Deathmatch**, duel-life maps, and co-op modes.
* **Custom Map Lobby Checks, Shops & Mode Fixes**: Added lobby team-count warnings on life- and round-based custom maps (**Pebbles Duel**, **Colosseum Duel**, **Empath Maze**, **True Devo Wars**, and **HoN Arena**) so hosts are warned before starting with an empty opposing team (plus optional bot/`FakePlayer` seeding in Practice); pointed `B` (`ToggleShop`) to `Shop_Outpost` while hiding unused Caldavar tabs in **True Devo Wars**; added a starting shared-lives tip, solo-start, 5 Hellbourne slots, 8-hero roster, and 60s pick timer in **Nightmarish Co-Op**; fixed bronze medallions, 9-slot Shared Spellbringer bar, and 8-pedestal inspector in **Enfo's Team Survival**; fixed duplicate post-goal `3-2-1` kickoff countdowns in **Strikers (`HoN Soccer` 5v5)**; and expanded `10v10` hero-kill packets (`0x44` / `0x6B`) to support full multi-assists and `5x`–`10x` multi-kill strings.
* **Avatar Fixes, D3D9 UI Texture Cleanup & Bot Script Fixes**: Grounded **Devourer** (`Base`, `POG Devourer`, `Alt12`, `Alt15`) blood pool decals in the Store and in-game while hiding floor streaks on Hero Pick; fixed **Trophy Bubbles (`Basic` & `Elite`)** showing duplicate Store models; cleaned up `Eucliwood Hellscythe` (`Hero_Xalynx`) effect handles on Hero Pick exit; added front-end UI texture cleanup on D3D9 to help reduce texture slot pressure in longer sessions; and fixed missing ability/item bindings plus basic arena/maze patrol points in **Colosseum Duel** & **Empath Maze** *(custom-map bot AI remains early / semi-working)*.
* **High-Hz DirectFlip Smoothness, Vulkan Layer Shield & WILLOWMAKER Polish**: Gated the Vulkan replay-capture layer behind explicit capture/Modern HUD modes plus an opt-in `HON_RENDERDOC_ENABLE=1` guard so third-party hooks (`RenderDoc`, `OBS`, `SteamOverlay`, `SteamFossilize`, `MediaSDK`) are neutralized automatically via each Khronos `ImplicitLayers` manifest's `disable_environment` key; tuned DXVK low-latency pacing (`maxFrameLatency=2`, `latencySleep=False`); staggered local 10v10 bot assessment ticks and bounded `AssetPrewarmer` strictly to pre-combat so mid-fight disk hitches are eliminated on `Strikers` and custom maps; promoted **Graphics Quality** and **Shadow Fidelity** (`High (2048)` default) into the main launcher card with a clean HUD default (`Off (Clean Screen)`), live active-preset diff transparency, vendor-neutral EDID + OS **VRR Readiness** detection (`VRR-Ready` badge + 1-click sub-refresh FPS cap helper), scoped live freeze minidump capture, sanitized launch receipts, and eliminated false-positive `SHUTDOWN_HANG_TIMEOUT` banners after clean quits (and hid the redundant standalone **Mod Manager** button, now integrated directly under in-game **Options ➔ Mods**).
* **Offline & Online Shop Guides (Mid Wars & All Maps)**: Fixed offline and online in-game shop guide lookup and featured-guide purchasing across **Mid Wars** and all classic/custom maps so players can buy directly from featured guides without having to exit back to the raw item tree.
* **Offline Mode & DirectPlay P2P Clarity (Select a Mode & Store)**: When launching offline or in DirectPlay P2P without a Master Server, online-only matchmaking banners (**Ranked**, **Quickmatch**, **Mid Wars**) and **Regions** grey out with an **`OFFLINE`** badge while keeping **Bot Match** and **Public** open, and the main-menu **Store** button greys out with a tooltip noting that all hero skins remain selectable during Hero Pick—automatically restoring full color and online access on login.
* **CLI, Control Panel & Experimental Voice/P2P Updates**: Updated the `kongor` CLI so commands work outside the project folder (`kongor where`, `kongor workspace`) with shell tab-completions; added Control Panel endpoint sync to an already-open `WILLOWMAKER` window plus a dedicated server chat/command console; split `textures*.s2z` into two `< 1.50 GiB` archives for patching; expanded `WILLOWMAKER` Safe Mode & crash report scrubbing; and added **experimental** `RNNoise` voice noise suppression, **experimental** P2P STUN/relay NAT helpers, and early Linux **Zink / Wayland** fixes.

---

## ✨ Version 5.0.1 — Queue Arcade, Custom Maps, Friendslist Broadcasting & Bot Expansion

### 🕹️ 1. Queue Arcade ("While You Wait" Skirmish Matches)
* **Play While Waiting in Queue *(Optional)***: Instead of staring at a timer during off-peak hours, queued solo or party players can opt into **Queue Arcade**—automatically dropping into a temporary, zero-stakes skirmish match populated with bots while matchmaking continues in the background.
* **Clean Handoff & Instant Queue Entry**: Entering queue is instant (`0 ms`, modal-free) and features a live top-left Queue Arcade timer banner, `/arcade` toggle commands, a short-ETA check that skips loading if your queue is already about to pop, completely isolated stats/MMR (skirmishes never affect real match history), and a 3-second heads-up before pulling everyone into the real lobby.

### 🤝 2. Spectating, Mentor Requests & Friendslist Broadcasting
* **Live Spectating & Mentoring**: Two-way **Spectate** and **Mentor** request flows wired end-to-end across client, chat server, and match server—complete with mutual-friendship checks for mentoring, slot capacity checks, and automatic LAN/WAN address routing.
* **Friendslist Status & Match Broadcasting**: Real-time friendslist status broadcasting across **Online**, **AFK / DND** chat modes, **In Lobby or Queue**, and **In Match** as games join and start.
* **Monitor Game, Invites & Live Peer Sync**: Inspect friends' active games directly from your friendslist (**Monitor Game** / live match info), send and decline right-click lobby **Game Invites**, sync friend additions/removals live without relogging, whisper cleanly regardless of clan tags, and view non-overlapping social toast notifications.

### 🗺️ 3. Expanded Playable Map Roster (10v10 Chaos, Classic, Arenas, Co-Op, WTF & Seasonal)
Every map below is repaired for HoN `4.10` compatibility and shipped on both client and dedicated servers (see the [Custom & Seasonal Maps Catalog](../reference/custom-maps-catalog.md) for technical repair notes):
* **20-Player (`10v10`) Chaos Modes**:
  * **Devo Wars (10v10 Chaos)** (100-kill goal), **Mid Wars (10v10 Chaos)** (with river rune boat), and **True Devo Wars (10v10 Chaos)** *(PvP, 75 team lives + hook upgrade shop)*—backed by 20-player lobbies and custom scoreboards.
* **Classic & Multi-Lane Battlegrounds**:
  * **Three Kingdoms** (5v5) and **Persian Pass** (4v4), alongside **Grimm's Crossing** (3v3), **Darkwood Vale** (4v4), **Watchtower** (5v5), and Classic/PBT variants of **Forests of Caldavar** and **Mid Wars**.
* **Sports, Skillshot Arenas, Duels & Party Modes**:
  * **Strikers (`HoN Soccer` 5v5)** *(playable PvP or with bots)*, plus PvP custom-rule arenas **Valkyrie Wars**, **Archer Wars**, **True Devo Wars** (5v5), **Pebbles Duel**, **HoN Arena**, **Colosseum Duel**, **Prophets**, **Capture the Flag**, and **Rift Wars** *(lobby setup automatically enforces human slots on custom-rule arenas without bot AI)*.
* **Wave Survival, Co-Op & Maze Modes**:
  * **Enfo's Team Survival** *(human co-op two-lane wave defense)*, **Nightmarish Co-Op** *(human co-op monster wave survival)*, **Empath Maze** *(human labyrinth minigame)*, and **The Grimm Hunt**.
* **WTF & Classic Brawl Modes**:
  * **Forests of Caldavar (WTF)** (zero cooldowns and infinite mana sandbox) and **Mid Wars Classic (Watchtower / OMG)**.
* **Seasonal Halloween Maps & Optional Themes**:
  * **Mid Wars (Halloween)** (featuring the Headless Horseman boss and 4.10 river rune boat) and **Forests of Caldavar (Halloween)**, plus optional visual map theme mods in **Options ➔ Mods** and seasonal soundtrack selection in **Options ➔ Sound**.
* **Fast Custom-to-Standard Map Transitions**: Unified custom map mechanics definitions into the global `4.10` base schema so switching from custom modes back to standard maps takes seconds without needing to restart the client.

### 🤖 4. Expanded Bot Support *(Early / Semi-Working — 20 Maps + Difficulty Scaling)*
* **Candid Status *(Work in Progress)***: Custom-map bot support is **semi-working / early AI**. Bots can navigate, brawl, shop, and fill lobbies for casual practice, hosted matches, and Queue Arcade, though custom-mode positioning and objective coordination are still actively being tuned.
* **All 20 Bot-Enabled Maps**:
  * *Custom, Sports & Objective Modes*: **Strikers (`HoN Soccer` 5v5)** (29-hero Strikers roster, Goalie/Sweeper-Keeper saves that yield the net when a human stands in goal, Energy Rune pickups, own-goal safety checks, and team passing via **`"pass"`** chat callouts), **Team Deathmatch** (between-round plateau shopping & sudden-death ring convergence), **Grimm's Crossing** (3v3), **The Grimm Hunt** (objective/boss & shrine shield awareness), and **Devo Wars (5v5 & 10v10)** (single-hero `DevourerBot` hook-brawler AI + hook upgrade items).
  * *Multi-Lane Maps (New Waypoint Graphs)*: **Three Kingdoms** (5v5), **Persian Pass** (4v4), **Darkwood Vale** (4v4), and **Watchtower** (5v5).
  * *Caldavar & Mid Wars Variants*: **Forests of Caldavar** (Standard, Old, PBT/Reborn, Halloween, WTF) and **Mid Wars** (Standard, PBT/Reborn, Halloween, Classic OMG, and 10v10 Chaos).
* **Bot Difficulty Selection**: Added persistent **Easy / Medium / Hard** bot difficulty selection across practice and hosted game menus.

### 🛡️ 5. Launcher Safety, Crash Privacy & Self-Hosting
* **Signed Update Guard (`PKV Guard`)**: `WILLOWMAKER` verifies cryptographic manifest signatures and blocks file deletions or unverified updates when connected to non-PKV third-party servers.
* **Safe Mode Recovery & Crash Privacy Scrubbing**: Added a 1-click *"Relaunch in Safe Mode"* recovery option after unexpected client crashes, paired with deeper minidump (`MDMP`) and Discord diagnostic snippet scrubbing so local OS paths, handles, memory slack, and unapproved local mod names are stripped before sharing reports.
* **1-Command Self-Hosted Docker Stack**: Added [`scripts/docker/launch_docker_stack.ps1`](../../scripts/docker/launch_docker_stack.ps1) (`kongor docker-stack`) so community hosts can launch the full backend (`MasterServer`, `ChatServer`, `WebAPI`, `WebUI`, `Garnet`, and `COMPEL` Match + Queue Arcade fleets) in Docker with a single command.
* **Hosted Lobby Cleanup**: Idle `0/10` player-hosted lobbies are now automatically reclaimed so the server browser stays clean.

---

## 🏗️ Version 5.0.0 — Core Platform & Engine Modernization Baseline

### 🕺 1. Hero Emotes Unlocked for Everyone
* **Dance Emotes Unlocked**: Hero dance animations have always existed in the game files, but were previously locked away. Now they are unlocked for all heroes and all players (`/dance` or hotkey) to celebrate plays with your team.

---

### 🛠️ 2. In-Game Mod Toggles & Quick Settings (SysBar)
* **No External Mod Managers Needed**: Built-in mod management avoids external third-party patchers that break across updates.
* **In-Game Mod Toggles**: Community quality-of-life options—such as the low-health **Heartbeat** warning pulse and optional map themes—can be toggled directly in **Options ➔ Mods**.
* **Quick Settings in the System Taskbar**: System taskbar featuring quick settings, master volume controls, live FPS & Ping counters, and match controls without digging through nested menus.

---

### 🌐 3. Flexible Multiplayer (Online, LAN, or Direct Connect)
* **Multiple Ways to Play**: Connect to a community master server, host an offline LAN match on your local network, or direct-connect to a friend's hosted server.
* **More Reliable Reconnects**: Addressed common pre-game lobby disconnects and the `"Reason 56"` reconnect error via dedicated server slot recycling and packet watermark alignment.

---

### ⚡ 4. Vulkan Graphics & Input Responsiveness
* **Vulkan Translation**: Added DXVK / Vulkan 1.3 rendering support to help keep frame pacing consistent during busy teamfights.
* **Reflex & Input Polling**: Added NVIDIA Reflex hooks and finer mouse/keyboard input sampling to help reduce click-to-cast latency.
* **Higher Refresh Support**: Unlocked internal frame rate caps for high-refresh-rate monitors (`144Hz`, `240Hz`, and above).

---

### 📦 5. Faster Archive Decompression & Asset Caching
* **Accelerated Decompression**: Upgraded `.s2z` archive decompression (`zlibwapi-fast` / `libdeflate`) to reduce match and skin loading times compared to legacy retail builds.
* **Background Pre-Caching**: Frequently used hero assets warm in the background to help reduce first-cast spell hitches.

---

### 🎨 6. 100+ Community Avatars Included Free
* **Available Out of the Box**: Over a hundred classic community avatars, visual touch-ups, and skins are merged into the base packages for everyone to equip for free.

---

### 🤖 7. Offline Practice & Solo Bot Matches
* **Play Without an Internet Connection**: Launch solo practice matches against bots offline anytime to test heroes, item builds, or mechanics.
* **Automated 5v5 Bot Scrimmages**: Single-command 5v5 bot scrimmages for warm-ups, server testing, or offline spectating.
* **Quick-Practice Drawer**: Flyout menu on the home screen to quickly pick heroes, test builds, and preview skins.

---

### 🖥️ 8. Dedicated Server Hosting (COMPEL)
* **Community Server Hosting**: COMPEL supervisor tools allow community members to host dedicated match servers with per-port crash recovery and isolated restarts.
* **1.0ms Precision Timer**: Dedicated servers request `1.0ms` Windows timer resolution (`timeBeginPeriod(1)`) to help keep 20 Hz server ticks steady.

---

### 🎧 9. Directional Audio & Voice Chat
* **3D Positional Audio**: OpenAL spatial audio routing to help clarify the direction of nearby spell casts, footsteps, and jungle camps.
* **Opus Voice Comms**: Low-latency Opus voice chat with configurable noise suppression and microphone gain controls.

---

### 🎬 10. Replay Protection & Creator Tools *(Early WIP)*
* **Replay Crash Shield**: Match replays (`.honreplay`) use background write-ahead logging (`KWAL`) to help protect recordings if a match server closes unexpectedly.
* **Upright Video Theater**: Fixed in-game video orientation and audio sync for hero guides and clips.
* **Creator Tools *(Experimental / WIP)***: Early camera spline controls, highlight clipping experiments, and a Blender `4.2+` `.model`/`.mdf` add-on for community 3D modders (actively in development).

---

### 🌐 11. Kongor Web Portal, Browser Mini-Games & Discord Sync
* **Web Stats & Match History**: Browse match scorecards, lifetime stats by mode, and hero guides in the DAWNBRINGER web portal.
* **Browser Mini-Games**: Casual web portal mini-games—including **Devourer Wars (DVO)**, **Monkey King Checkers**, **KONGOR Chess**, and **Plinko**.
* **Discord Integration**: Optional Discord linking to sync your Discord avatar in-game and display current match status via Discord Rich Presence.

---

### 💻 12. Cross-Platform Services & Client Compatibility
* **Native Multi-Platform Services & Tools**: Backend `.NET 11` microservices, the `kongor` CLI, and the Avalonia desktop Control Panel run natively across **Windows**, **Linux**, and **macOS** on both `x64` and `ARM64` hardware (including Apple Silicon and Snapdragon X Elite).
* **Game Client Compatibility**: The `x64` K2 game client (`hon_x64.exe`) runs natively on Windows (`x64`, or Windows on ARM via Prism translation) and on **Linux / Steam Deck / macOS** through Wine, Proton, or CrossOver translation layers.

---

### 🙏 13. Acknowledgments, Credits & Respect for Newerth's Builders
* **Original *Heroes of Newerth* & K2 Engine Teams (`S2 Games`, `Frostburn Studios` & `Garena`, 2009–2022)**: None of this exists without the engineers, designers, artists, animators, composers, QA staff, and community leads who poured over a decade into *Heroes of Newerth*—with a special shout-out to **`ElementUser` (`EU`)** for his years of tireless work maintaining scripts, fixing engine/state/tooltip bugs, guiding Public Beta Test (`PBT`) patches, updating the UI, and talking directly with players across S2 Games and Frostburn Studios, alongside **`Fielding`**, **`Idejder`**, **`Nome`**, **`Wza`**, **`HyperXewl` (`xewl`)**, **`Kros`**, **`Buro`**, **`MacroHard`**, **`Diva`**, **`AceJR`**, **`CJK`**, **`SoundWizard`**, **`MsPudding`**, **`Nigma`**, **`BreakCPK`**, and everyone who worked on HoN *(historical gratitude only; Project Kongor V is an independent community project and is not affiliated with or endorsed by Garena, S2 Games, Frostburn Studios, Kongor Studios, or any individuals named above)*.
* **Open-Source Pioneers, Bot Authors & Custom Map Creators**: Grateful credit to **`[K]ONGOR`** (`Project-KONGOR-Open-Source` contributors), **`korDen`**, **`KRAT0Sz` (`why2k`)**, **`RedBear`**, **`Theli`**, **`shawwn`**, **`manu311`**, **`mrhappyasthma`**, **Craig Mason-Jones** (`JSON4Lua`), the **HoNBots** pioneers (`tkt-hon`, `kairus101`, `St0l3n_ID`, `Anakonda`, `Sparks1992`, `AsiaPingPong`), and HoN's UI modders & custom map makers (`Bang!`, `Notausgang`, `xewl`, `Valky`, `Cralle`, and the HoN Custom Maps community). Full license and library credits live in [`NOTICE`](../../NOTICE) and [`THIRD_PARTY_NOTICES.md`](../../THIRD_PARTY_NOTICES.md).
* **AI Models & Educational Notes**: Thank you as well to the AI models and developer platforms that helped accelerate planning, cross-review, and debugging—especially **Google / DeepMind (`Gemini` & `Antigravity`)**, **Anthropic (`Claude`, `Claude Code`, & `MCP`)**, and **JetBrains (`Junie`, `PyCharm`, & `Rider`)**, alongside **OpenAI (`GPT` & `Codex`)** and optional creative/audio ML tools. See [**AI Gratitude & Educational Notes**](../ai-gratitude-and-methodology.md) and [**Credits & Gratitude**](../credits-and-gratitude.md).

---

*See you in Newerth!* 🛡️🔥
