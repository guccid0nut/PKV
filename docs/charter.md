# 📜 Project KONGOR V Charter: Video Game Preservation & The Forever-Free Social Contract

<div align="center">

*“Project KONGOR V always was and forever will be completely free.”*

</div>

---

## 🏛️ 1. The Preservation Mission

On **June 20, 2022**, after more than 12 years of active service, the official servers for *Heroes of Newerth* were permanently taken offline. Because the game engine relied on central master and chat server handshakes for authentication, matchmaking, and dedicated server allocation, the shutdown rendered online and bot-match services inaccessible in their original form.

**Project KONGOR V (`PKV`)** (<https://github.com/guccidonut/PKV>) is an independent, non-commercial, open-source video game preservation and interoperability engineering project maintained by `guccidonut`. Its purpose is to research, document, and maintain cleanroom networking services, runtime stability shims, and developer tooling for historical, archival, and non-commercial community study.

---

## ⚠️ 2. Cleanroom Platform Code & Intellectual Property Boundaries

> [!IMPORTANT]
> **OPEN-SOURCE PROVENANCE, MULTI-LICENSING & INTELLECTUAL PROPERTY BOUNDARIES**
>
> 1. **Open-Source Provenance & Multi-Licensing**:
>    - Platform services, desktop tools, TAO MCP, scripts, and tests at the repository root are licensed under **`AGPL-3.0-or-later`** ([`LICENSE`](../LICENSE)), honoring foundational `[K]ONGOR` (`Project-KONGOR-Open-Source`) open-source lineage alongside newly authored Project KONGOR V (`PKV`) architecture.
>    - The standalone C++ engine modernization layer in `native/` (excluding permissive dependencies under `native/deps/`) is licensed under **`MPL-2.0`** ([`native/LICENSE`](../native/LICENSE)), and documentation under `docs/` is licensed under **`CC BY-NC-SA 4.0`** ([`docs/LICENSE.md`](LICENSE.md)). See [`NOTICE`](../NOTICE) and [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md) for complete attribution.
> 2. **Zero Retail Game Assets in Public Releases**: Public source distributions contain **zero** retail *Heroes of Newerth* executables, `.s2z` archives, 3D models, textures, icons, audio files, or character lore prose. Every service, desktop launcher, native C++ shim, and unit test suite compiles and runs cleanly from pure source code without game files present.
> 3. **Ownership of Trademarks and Copyrights**: *Heroes of Newerth*™ and *Newerth*™, together with all official characters, retail lore, game audio, visual assets, and trademarks, are the intellectual property of **Garena Online Private Limited** (and historically **S2 Games** and **Frostburn Studios**).
> 4. **No Affiliation**: Project KONGOR V (`PKV`) is **not** affiliated with, sponsored by, or endorsed by Garena, S2 Games, Frostburn Studios, Kongor Studios (`HoN Reborn`), or the original `[K]ONGOR` team. See [`NAMING_POLICY.md`](../NAMING_POLICY.md) and [`INTEROPERABILITY_AND_DMCA.md`](../INTEROPERABILITY_AND_DMCA.md).

Players and server operators who wish to connect a game client or dedicated server must supply their own lawfully obtained *Heroes of Newerth (`v4.10.1`)* installation.

---

## 💎 3. The "Always and Forever Free" Project Governance Pledge

> [!IMPORTANT]
> **PROJECT GOVERNANCE PLEDGE & LICENSE CLARITY:**
>
> 1. **Our Project Pledge (Zero Monetization by PKV):** Project KONGOR V will **always and forever remain 100% free**—zero donations, zero subscriptions, zero paywalls, zero paid cosmetics, and zero financial solicitation of any kind.
> 2. **Pure Open-Source Code Licensing (`AGPL-3.0` §10 Compliance):** Software source code in this repository is licensed under unmodified OSI-approved copyleft terms (`AGPL-3.0-or-later` and `MPL-2.0`) with **no** non-commercial "further restrictions" added to the software license itself (preserving full `AGPL-3.0` Section 10 compatibility with upstream open-source code). Anyone distributing modified or network-hosted versions of `AGPL-3.0-or-later` code must provide complete Corresponding Source and preserve attribution notices under `AGPL-3.0` Sections 5, 7, and 13.
> 3. **Documentation & Third-Party Rightsholder Boundaries:** Written guides and artwork in `docs/` carry binding NonCommercial protection under **`CC BY-NC-SA 4.0`** ([`docs/LICENSE.md`](LICENSE.md)), and open-source licenses in this repository never grant any license to commercialize Garena / S2 Games / Frostburn Studios trademarks or retail game assets.

---

## 🧪 4. Supporting the Project (Zero Donations Policy)

Project KONGOR V (`PKV`) has **zero corporate backing, zero revenue, and accepts zero donations, sponsorships, or financial contributions of any kind**.

The best way to support video game preservation and interoperability research is through cleanroom engineering contributions, bug reports, protocol documentation, and community testing. See [`INTEROPERABILITY_AND_DMCA.md`](../INTEROPERABILITY_AND_DMCA.md) for our good-faith rightsholder and attribution contact policy *(informational only; not legal advice)*.

---

## 🙏 5. Acknowledgments & Tribute to the Builders of Newerth

Preserving a game means honoring everyone whose craft, care, and late nights built it in the first place:

- **Original *Heroes of Newerth* & K2 Engine Creators (`S2 Games`, `Frostburn Studios` & `Garena`, 2009–2022):** Deep appreciation to every engineer, designer, artist, animator, composer, QA tester, and community manager who shaped *Heroes of Newerth* over more than 12 years—with a special shout-out to **`ElementUser` (`EU`)** for his years of tireless work on scripts, bug fixes, tooltips, UI updates, Public Beta Test (`PBT`) patches, and direct communication with players across both S2 Games and Frostburn Studios, alongside **`Fielding`**, **`Idejder`**, **`Nome`**, **`Wza`**, **`HyperXewl` (`xewl`)**, **`Kros`**, **`Buro`**, **`MacroHard`**, **`Diva`**, **`AceJR`**, **`CJK`**, **`SoundWizard`**, **`MsPudding`**, **`Nigma`**, **`BreakCPK`**, and the entire original development teams *(tribute only; Project KONGOR V is independent and unaffiliated)*.
- **Open-Source Preservation & Tooling Pioneers:** Grateful credit to **`[K]ONGOR`** ([Project-KONGOR-Open-Source](https://github.com/Project-KONGOR-Open-Source) contributors), **Denis Koroskin** (`korDen`), **Anton Romanov** (`KRAT0Sz` / `why2k`), **`RedBear`**, **`Theli`**, **Shawn Presser** (`shawwn`), **`manu311`**, **`mrhappyasthma`**, and **Craig Mason-Jones** (`JSON4Lua`).
- **Bot Authors, UI Modders & Custom Map Creators:** Thank you to the HoNBots community pioneers (`tkt-hon`, `kairus101`, `St0l3n_ID`, `Anakonda`, `Sparks1992`, `AsiaPingPong`) and classic HoN UI modders and custom map makers (`Bang!`, `Notausgang`, `xewl`, `Valky`, `Cralle`, and the HoN Custom Maps & Contest community).
- **AI Collaborators & Educational Transparency:** Gratitude to **Google / DeepMind (`Gemini` & `Antigravity`)**, **Anthropic (`Claude`, `Claude Code`, & the open `Model Context Protocol`)**, **JetBrains (`Junie`, `PyCharm`, & `Rider`)**, and **OpenAI (`GPT` & `Codex`)** for pair-programming, review, and debugging assistance—documented openly for educational study in [`AI Gratitude & Educational Notes`](ai-gratitude-and-methodology.md) alongside [`Community Credits`](credits-and-gratitude.md).

---

<div align="center">

*“Legends never die. Kongor lives.”*

</div>
