# Third-Party Notices, Upstream Lineage & Cleanroom Algorithmic Attributions

This document records foundational open-source upstream code, git submodules, vendored libraries, optional runtime compatibility libraries, and open-source repositories whose published algorithms, data structures, and specifications informed cleanroom implementations in **Project KONGOR V (`PKV`)** (<https://github.com/guccid0nut/PKV>).

---

## 1. Foundational Open-Source Upstream Lineage & Preservation Pioneers

### Project KONGOR Open Source (`[K]ONGOR` & Contributors)
- **Upstream Organization & Authors:** [Project-KONGOR-Open-Source](https://github.com/Project-KONGOR-Open-Source) / [[K]ONGOR](https://github.com/K-O-N-G-O-R) and Project KONGOR Contributors
- **Upstream Repositories:** `NEXUS`, `WILLOWMAKER`, `COMPEL`, `CODEX`, `ARCANA`
- **License:** GNU Affero General Public License v3.0 (`AGPL-3.0`)
- **Role in Project KONGOR V:** Foundational protocol research, database schema, and service architecture in `MERRICK.DatabaseContext`, `KONGOR.MasterServer`, `TRANSMUTANSTEIN.ChatServer`, `COMPEL`, `WILLOWMAKER`, `ZORGATH.WebPortal.API`, `NEXUS.CdnBroker`, `Kongor.Core`, and `NEXUS.Contracts`, extended alongside newly authored PKV platform, TAO MCP (`AGPL-3.0-or-later`), and `native/` (`MPL-2.0`) subsystems. All derived and interconnected platform/service modules are licensed under `AGPL-3.0-or-later` (see root [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE)).

### Denis Koroskin (`korDen/honserver`)
- **Upstream Repository:** [korDen/honserver](https://github.com/korDen/honserver)
- **Role in Project KONGOR V:** Public protocol documentation and chat server handshake salt constant referenced in `src/services/master-server/Handlers/SRP/SRPAuthenticationHandlers.cs`.

### Anton Romanov (`KRAT0Sz` / `why2k`), `RedBear`, & `Theli` (`theli-ua/pyHoNBot`)
- **Upstream Repositories:** [KRAT0Sz/K2-Blender-master](https://github.com/KRAT0Sz/K2-Blender-master), [KRAT0Sz/KONGOR-TOOL](https://github.com/KRAT0Sz/KONGOR-TOOL), and [theli-ua/pyHoNBot](https://github.com/theli-ua/pyHoNBot)
- **Licenses:** MIT License (`Copyright (c) 2024-2026 Anton Romanov, K2-Blender Contributors`) and Apache-2.0 (`KRAT0Sz/KONGOR-TOOL`)
- **Role in Project KONGOR V:** K2 SMDL/CLIP/material shader helper foundations by Anton Romanov (`KRAT0Sz` / `why2k`) in `src/tools/blender/io_scene_hon/` (distributed under `GPL-3.0-or-later` for Blender `bpy` compatibility with the verbatim MIT notice preserved in `src/tools/blender/io_scene_hon/LICENSE`), modern Blender 4.2+/5.0 K2 architectural patterns informed by `KRAT0Sz/KONGOR-TOOL` alongside foundational K2 format & engine research from `RedBear`, and early SRP authentication research by `Theli` (`theli-ua/pyHoNBot`) referenced in `SRPAuthenticationHandlers.cs`.

### Shawn Presser (`shawwn/hon`), `manu311`, & `mrhappyasthma`
- **Upstream References:** [shawwn/hon](https://github.com/shawwn/hon) (`chatserver_protocol.h` packet command identifier constants in `ChatProtocol.cs`), `manu311` (Linux Flatpak packaging inspiration in `infra/flatpak/`), and `mrhappyasthma` (community preservation utilities).

### Tactical Bot Brains & HoNBots Community Pioneers (`tkt-hon`, `kairus101`, `St0l3n_ID`, `Anakonda`, `Sparks1992`, `AsiaPingPong`)
- **Role in Project KONGOR V:** Autonomous tactical bot decision tree models and Mid Wars bot heuristics (`games/client/override/bots/`), building upon foundational game engine bot scripts by S2 Games and Frostburn Studios.

### Craig Mason-Jones (`JSON4Lua`)
- **License:** MIT License (`Copyright (c) 2009 Craig Mason-Jones`)
- **Role in Project KONGOR V:** Lua JSON encoder/decoder utility (`games/client/verify_lua/ui/scripts/lib_json.lua`).

### Classic HoN Modding, Custom Map & Retail Development Acknowledgments
- **Original *Heroes of Newerth* & K2 Engine Teams (`S2 Games`, `Frostburn Studios` & `Garena`, 2009–2022):** Historical acknowledgment and gratitude to the engineers, designers, artists, audio creators, QA testers, and community leads who created and maintained *Heroes of Newerth*—including **`ElementUser` (`EU`)** for his long-standing work on gameplay scripts, engine/state fixes, tooltips, UI updates, PBT patches, and community communication, alongside **`Fielding`**, **`Idejder`**, **`Nome`**, **`Wza`**, **`HyperXewl` (`xewl`)**, **`Kros`**, **`Buro`**, **`MacroHard`**, **`Diva`**, **`AceJR`**, **`CJK`**, **`SoundWizard`**, **`MsPudding`**, **`Nigma`**, **`BreakCPK`**, and the broader S2 Games, Frostburn Studios, and Garena teams *(historical tribute only; non-affiliated)*.
- **Classic HoN Modders & Custom Map Authors:** Recognition to **`Bang!`**, **`Notausgang`**, **`HyperXewl` (`xewl`)**, **`Valky`**, **`Cralle`**, and the HoN Custom Map Contest & Custom Maps community creators behind *Three Kingdoms*, *Persian Pass*, *True Devo Wars*, *Valkyrie Wars*, *Archer Wars*, *Pebbles Duel*, *HoN Arena*, *Colosseum Duel*, *Empath Maze*, *Enfo's Team Survival*, *Nightmarish Co-Op*, and *Forests of Caldavar (WTF)*.
- **AI Engineering Collaborators & Educational Case Study:** Gratitude to **Google / DeepMind (`Gemini` & `Antigravity`)**, **Anthropic (`Claude`, `Claude Code`, & the open `Model Context Protocol`)**, **JetBrains (`Junie`, `PyCharm`, & `Rider`)**, and **OpenAI (`GPT` & `Codex`)**—documented for educational transparency in [`docs/ai-gratitude-and-methodology.md`](docs/ai-gratitude-and-methodology.md) *(non-affiliated)*.

---

## 2. Submodules, Vendored Native Dependencies & Web/App Bundles

### Native Submodules & Vendored C/C++ Headers (`native/`)
- **RmlUi (`native/deps/rmlui`):** [mikke89/RmlUi](https://github.com/mikke89/RmlUi) — `MIT License` (`Copyright (c) 2008-2014 CodePoint Ltd, Shift Technology Ltd; Copyright (c) 2019-2026 The RmlUi Team`).
- **libdeflate (`native/deps/libdeflate`):** [ebiggers/libdeflate](https://github.com/ebiggers/libdeflate) — `MIT License` (`Copyright 2016 Eric Biggers`).
- **mimalloc (`native/deps/mimalloc`):** [microsoft/mimalloc](https://github.com/microsoft/mimalloc) — `MIT License` (`Copyright (c) 2018-2024 Microsoft Corporation, Daan Leijen`; see `native/deps/mimalloc/LICENSE`).
- **miniz (`native/deps/miniz`):** [richgel999/miniz](https://github.com/richgel999/miniz) — `MIT License` (`Copyright 2013-2014 RAD Game Tools and Valve Software; Copyright 2010-2014 Rich Geldreich and Tenacious Software LLC`).
- **stb_truetype (`native/deps/stb_truetype.h`):** [nothings/stb](https://github.com/nothings/stb) — `MIT OR Unlicense` (`Sean Barrett`).
- **QR-Code-generator (`native/vulkan-layer/deps/qrcodegen.*`):** [nayuki/QR-Code-generator](https://github.com/nayuki/QR-Code-generator) — `MIT License` (`Copyright (c) Project Nayuki`).
- **Khronos Vulkan-Headers (`native/vulkan-layer/include/vulkan/`):** [KhronosGroup/Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers) — `Apache-2.0 OR MIT` (`Copyright 2015-2026 The Khronos Group Inc.`).
- **Lua 5.1.5 (`native/tools/luac51/lua-5.1.5`):** [lua.org](https://www.lua.org/license.html) — `MIT License` (`Copyright (C) 1994-2012 Lua.org, PUC-Rio`).
- **Raylib 5.5 (CMake `FetchContent`):** [raysan5/raylib](https://github.com/raysan5/raylib) — `Zlib License` (`Copyright (c) 2013-2024 Ramon Santamaria`) in `native/tools/kongor-model-viewer/`.
- **The Goose Programming Language (`v2026.4` Opt-In Native CLI Toolchain):** [aardappel/goose](https://github.com/aardappel/goose) — `Apache License 2.0` (`Copyright 2026 Wouter van Oortmerssen`). Optional compiler toolchain (`-DKONGOR_ENABLE_GOOSE=ON`) transpiling standalone CLI spikes under `native/` to portable C.

### Web UI, Desktop Assets, Fonts & NuGet Open-Source Libraries
- **Magick.NET (`Magick.NET-Q16-AnyCPU 14.17.2`):** [dlemstra/Magick.NET](https://github.com/dlemstra/Magick.NET) — `Apache License 2.0` (`Copyright Dirk Lemstra`).
- **Three.js (`r168`):** [mrdoob/three.js](https://github.com/mrdoob/three.js) — `MIT License` (`Copyright 2010-2024 Three.js authors`).
- **Google Draco 3D Compression Decoder:** [google/draco](https://github.com/google/draco) — `Apache License 2.0` (`Copyright 2016 The Draco Authors`, licensed under <https://www.apache.org/licenses/LICENSE-2.0>).
- **meshoptimizer (`0.18` JS/WASM Decoder & Cleanroom C# Inspiration):** [zeux/meshoptimizer](https://github.com/zeux/meshoptimizer) — `MIT License` (`Copyright (c) 2016-2024 Arseny Kapoulkine`).
- **Tailwind CSS, Simple.SwaggerThemeToggler & CDN Libraries:** `Tailwind CSS` (`MIT`), `Simple.SwaggerThemeToggler` (`MIT`), `Font Awesome Free 6.5.1` (`CC BY 4.0` icons / `MIT` CSS), `Chart.js` (`MIT`), `chess.js` (`BSD-2-Clause`), `@microsoft/signalr` (`MIT`), and `jQuery` (`MIT`).
- **Bundled Fonts:** `Inter` (`SIL Open Font License 1.1`, Rasmus Andersson / The Inter Project Authors) and `Roboto Mono` (`Apache License 2.0`, Christian Robertson / Google).
- **Core .NET & Rust Ecosystem Libraries:** `Avalonia` & `Avalonia.Svg.Skia` (`MIT`), `SkiaSharp` & `HarfBuzzSharp` (`MIT`), `Spectre.Console` & `Spectre.Console.Cli` (`MIT`), `MemoryPack` & `MessagePack-CSharp` (`MIT`), `StackExchange.Redis` (`MIT`), `Npgsql` (`PostgreSQL`), `Dapper` (`Apache-2.0`), `Polly` (`BSD-3-Clause`), `Serilog` (`Apache-2.0`), `OpenTelemetry .NET` (`Apache-2.0`), `Pyroscope .NET` (`Apache-2.0`), `Google.Protobuf` (`BSD-3-Clause`), `Grpc.Net` (`Apache-2.0`), `Scalar.AspNetCore` (`MIT`), `FluentValidation` (`Apache-2.0`), `MediatR 12.5.0` (`Apache-2.0`), `OneOf` (`MIT`), `Concentus` (`BSD-3-Clause`), `srp` (`MIT`), `Konscious.Security.Cryptography.Argon2` (`MIT`), `AspNet.Security.OAuth.Discord` (`Apache-2.0`), `DiscordRichPresence` (`MIT`), `NLua` (`MIT`), `Silk.NET` (`MIT`), `SharpGLTF` (`MIT`), `DuckDB.NET` (`MIT`), `Parquet.Net` (`MIT`), `AWSSDK.S3` (`Apache-2.0`), `MailKit` & `MimeKit` (`MIT`), `SendGrid` (`MIT`), `Docker.DotNet` (`Apache-2.0`), `SSH.NET` (`MIT`), `Tmds.DBus.Protocol` (`MIT`), `ModelContextProtocol` (`MIT`), `ZiggyCreatures.FusionCache` (`MIT`), `aya` / `aya-ebpf` (`MIT OR Apache-2.0`), `xunit` (`Apache-2.0`), `Moq` (`BSD-3-Clause`), `FluentAssertions 7.2.0` (`Apache-2.0`), `Testcontainers` (`MIT`), `Verify` (`MIT`), `CsCheck` (`Apache-2.0`), `NetArchTest.eNhancedEdition` (`MIT`), and `coverlet` (`MIT`).

---

## 3. Optional Local Game Runtime Libraries (Not Distributed in Public Source Exports)

> **Note:** Cleanroom GitHub source exports contain **zero** `.dll`, `.exe`, `.so`, or `.s2z` binaries. The notices below document third-party compatibility libraries used when developers mount a local `games/` runtime workspace.

### DXVK (`games/client/d3d9.dll` — Version `v3.0.2`)
- **Upstream Repository:** [doitsujin/dxvk](https://github.com/doitsujin/dxvk)
- **License:** zlib License (`Copyright (c) 2017-2026 Philip Rebohle; Copyright (c) 2019-2026 Joshua Ashton`)

### Linux x86_64 Compatibility Shared Libraries (`libxml2.so.2`, `libGLU.so.1`, `libncurses.so.5` / `libtinfo.so.5`) & Retail Engine Audio
- **GNOME `libxml2`:** `MIT License` (`Copyright (C) 1998-2012 Daniel Veillard`).
- **Mesa `libGLU`:** `SGI Free Software License B Version 2.0`.
- **GNU `ncurses`:** `X11 / MIT Permissive License` (`Copyright (c) 1998-2018 Free Software Foundation, Inc.`).
- **FMOD Ex Sound System:** `Copyright (c) Firelight Technologies Pty, Ltd.` (audio middleware linked by retail *Heroes of Newerth* K2 engine installations).
- **OpenAL Soft (`openal32.dll` / `libopenal.so` — Optional Spatial Audio HRTF Runtime):** [kcat/openal-soft](https://github.com/kcat/openal-soft) — `LGPL-2.0-or-later` (`Copyright (C) 1999-2024 Chris Robinson and OpenAL Soft Contributors`; dynamically loaded shared library).

---

## 4. Cleanroom Algorithmic & Architectural Attributions

Where specific algorithms, mathematical models, or diagnostic workflows in Project KONGOR V were inspired by published open-source reference specifications (cleanroom implementations only; zero copyleft/FSL source code copied across incompatible boundaries), we gratefully credit:

| Upstream Project | License | Cleanroom Subsystem / Algorithm in Project KONGOR V |
| :--- | :--- | :--- |
| **[geode-sdk/geode](https://github.com/geode-sdk/geode)** | BSL-1.0 | SEH/VEH MSVC `0xE06D7363` RTTI decoder, `.pdata` primary entry resolver, patch interval guard, and main-thread swap queue in `native/proxies/native_guard/`. |
| **[morluto/rea](https://github.com/morluto/rea)** | MIT | RFC 8785 canonical closure digest verifier, obligation ledger gate, and Win32 Job Object commit-limit telemetry in `scripts/verify_evidence_obligations.ps1` and `src/tools/kongor-cli/Mcp/`. |
| **[obsproject/obs-studio](https://github.com/obsproject/obs-studio)** | GPL-2.0-or-later (cleanroom spec only) | AVX-aligned shared-memory frame capture ring, Vulkan Loader Dispatch Table keying, QPC frame-pacing throttle, and WASAPI process-loopback blob builder in `native/vulkan-layer/` and `src/tools/kongor-cli/Mcp/`. |
| **[ran-j/PS2Recomp](https://github.com/ran-j/PS2Recomp)** | MIT | Relocation-masked trie symbol scanner, Tarjan SCC cycle detector, jump-table recovery, and 8 KB hot-page texture lookup cache. |
| **[shadps4-emu/shadPS4](https://github.com/shadps4-emu/shadPS4)** | GPL-2.0-or-later (cleanroom spec only) | VEH access-violation direction decoder, code-cave detour synthesizer, RenderDoc API bridge, and 4 KB page dirty bitmask tracker. |
| **[tigerbeetle/tigerbeetle](https://github.com/tigerbeetle/tigerbeetle)** | Apache-2.0 | `Exhaustigen` decision-tree enumerator, `DirectedMatrixPacketSimulator`, `TigerBeetleTimeSim`, Lemire bounded PRNG, and storage fault simulator. |
| **[Genymobile/scrcpy](https://github.com/Genymobile/scrcpy)** | Apache-2.0 | Stream clock estimator, single-slot atomic frame swap, `CancelIoEx` pipe interrupter, and lock-free SPSC audio buffer. |
| **[repowise-dev/repowise](https://github.com/repowise-dev/repowise)** | FSL-1.1-ALv2 (cleanroom spec only) | Doc-drift fingerprinter, layer conformance checker, two-pass git secret scanner, and blast-radius test ranker in `scripts/gate_check.ps1`. |
| **[karpathy/autoresearch](https://github.com/karpathy/autoresearch)** | MIT | Fixed-budget keep/discard metric ratchet (`scripts/qa/Invoke-KongorAutoRatchet.ps1`). |
| **[rust-minidump/rust-minidump](https://github.com/rust-minidump/rust-minidump)** | MIT / Apache-2.0 | AMD64 non-canonical `#GPF` register inspector, poison-fill classifier, and bounded stack-scan `CALL` validator. |
| **[zeux/meshoptimizer](https://github.com/zeux/meshoptimizer)** | MIT | K2 `.model` orphan-vertex, ACMR vertex-cache, and LOD triangle monotonicity auditor (`K2ModelMeshIntegrityAuditor.cs`). |
| **[KRAT0Sz/KONGOR-TOOL](https://github.com/KRAT0Sz/KONGOR-TOOL)** (`KRAT0Sz` / `why2k`, with K2 format assistance from `RedBear`) | Apache-2.0 | Blender 4.2+ `schema_version` extension manifest & clean zip packager, Blender 5.0+ Slotted Actions F-Curve adapter, bulk `foreach_set` keyframing, dynamic child-bone length sizing, viewport bone toggles, constant-key `1e-6` epsilon compression, strict stream chunk bounds validation, and `surf` collision convex hull generation in `src/tools/blender/io_scene_hon/` and `src/tools/blender/package_extension.py`. |
| **Additional Cleanroom Reference Inspirations** | Permissive / Mixed (cleanroom spec only) | `WerWolv/ImHex`, `EpicGamesExt/raddebugger`, `EpicGames/OpenRigLogic`, `FWGS/xash3d-fwgs`, `TsudaKageyu/minhook`, `cameron314/concurrentqueue` & Dmitry Vyukov MPMC ring, `BitterSecurity/Decepticon`, `f3d-app/f3d`, `academa-labs/manimgx`, `rizinorg/rizin`, `rehan-remade/universal-modder`, `OpenSkill` (Weng-Lin), `tehtmi/unluac`, `gamedev-skills/awesome-gamedev-agent-skills`, `mattpocock/skills`, `addyosmani/agent-skills`, and `garrytan/gstack`. |
| **YARP (Yet Another Reverse Proxy)** (`Yarp.ReverseProxy`) | MIT License | Microsoft Corporation — opt-in HTTP edge reverse proxy (`KONGOR.EdgeGateway`). |
| **Dapper** (`Dapper`) | Apache-2.0 License | Stack Exchange / Marc Gravell & Contributors — parameterized relational read projections (`ZORGATH.WebPortal.API`). |
| **coturn** ([coturn/coturn](https://github.com/coturn/coturn)) | BSD-3-Clause | Optional self-hosted STUN/TURN relay container (`infra/docker/docker-compose.p2p-relay.yml`, `infra/p2p/coturn.conf`). |
| **rathole** ([rapiz1/rathole](https://github.com/rapiz1/rathole)) | Apache-2.0 | Optional self-hosted NAT-traversal UDP tunnel container (`infra/docker/docker-compose.p2p-relay.yml`, `infra/p2p/rathole-*.toml`). |
| **RNNoise** ([xiph/rnnoise](https://github.com/xiph/rnnoise)) | BSD-3-Clause | Optional native GRU voice denoiser ABI binding (`RnNoiseNativeBridge.cs`; falls back honestly to C# spectral gating when native library is absent). |
| **Garage** ([deuxfleurs/garage](https://git.deuxfleurs.fr/Deuxfleurs/garage)) | AGPL-3.0-or-later | Optional self-hosted S3-compatible content-addressed storage (`infra/docker/docker-compose.garage-cas.yml`, `infra/garage/garage.toml`). |
| **Grafana LGTM+P & GlitchTip** (`grafana`, `loki`, `tempo`, `mimir`, `pyroscope`, `glitchtip`, `pgvector`) | AGPL-3.0 / Apache-2.0 / MIT / PostgreSQL | Optional self-hosted observability, error tracking, and vector-dedup containers (`infra/docker/docker-compose.observability.yml`, `infra/postgres/pgvector_mcp_issues.sql`). |
