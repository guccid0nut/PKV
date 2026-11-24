# Interoperability, Cleanroom Scope & Good-Faith Takedown Policy

> **Informational Notice (Not Legal Advice):** This document summarizes the technical boundaries of the **Project KONGOR V (`PKV`)** source repository, what is and is not hosted here, and how rightsholders or contributors can contact the maintainer for prompt, good-faith resolution. Nothing in this document constitutes legal advice.

---

## 1. Independent, Non-Commercial Preservation Scope

**Project KONGOR V (`PKV`)** (<https://github.com/guccid0nut/PKV>) is an independent, non-commercial open-source software interoperability and preservation project maintained by `guccidonut`.

- Official online authentication and matchmaking services for *Heroes of Newerth* permanently ceased operations on **June 20, 2022**.
- Project KONGOR V accepts **zero donations, sponsorships, subscriptions, or payments of any kind** (see [`docs/charter.md`](docs/charter.md)).
- **Non-Affiliation:** Project KONGOR V is **not** affiliated with, endorsed by, or sponsored by **Garena Online Private Limited**, **S2 Games**, **Frostburn Studios**, **Kongor Studios (`HoN Reborn`)**, or the upstream **`[K]ONGOR`** team (see [`NAMING_POLICY.md`](NAMING_POLICY.md)).

---

## 2. What This Repository Hosts (and What It Never Hosts)

Every public source snapshot is produced through an automated 8-gate cleanroom export pipeline ([`scripts/Invoke-PublicGithubExport.ps1`](scripts/Invoke-PublicGithubExport.ps1) and [`config/public_export_manifest.json`](config/public_export_manifest.json)):

### Hosted in This Repository
- Cleanroom C#, C/C++, Python, Lua, and PowerShell source code for server protocol interoperability (`KONGOR.MasterServer`, `TRANSMUTANSTEIN.ChatServer`, `ZORGATH.WebPortal.API`, `DAWNBRINGER.WebPortal.UI`), launcher/server supervision (`WILLOWMAKER`, `COMPEL`, `NEXUS.ControlPanel.GUI`), native Vulkan/audio/stability shims (`native/`), and the **TAO (Together As One) MCP** developer server (`src/tools/kongor-cli/`).
- Publicly documented protocol framing constants required for unmodified client session compatibility (including the chat handshake salt previously published in the public domain by Denis Koroskin in [`korDen/honserver`](https://github.com/korDen/honserver), attributed in [`SRPAuthenticationHandlers.cs`](src/services/master-server/Handlers/SRP/SRPAuthenticationHandlers.cs) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)).
- Engineering documentation, schemas, and troubleshooting field notes (`docs/`).

### Never Hosted in This Repository
- **Zero** retail *Heroes of Newerth* executables (`.exe`, `.dll`, `.so`, `.dylib`) or `.s2z` game archives.
- **Zero** retail 3D models, animations, textures, icons, audio files, or character biography/flavor prose.
- **Zero** download links to retail game client or dedicated server binary distributions.
- Users who wish to run the game client or dedicated match server locally must supply their own lawfully obtained *Heroes of Newerth* installation (see [`scripts/vfs/Hydrate-LocalGameAssets.ps1`](scripts/vfs/Hydrate-LocalGameAssets.ps1)).

---

## 3. Open-Source Attribution & Multi-Licensing

- **Platform, Services, Launchers & TAO MCP (`src/`, `scripts/`, `tests/`, `infra/`, `tools/`):** Licensed under **`AGPL-3.0-or-later`** ([`LICENSE`](LICENSE)). Upstream `[K]ONGOR` `AGPL-3.0` work and community contributors are credited in [`NOTICE`](NOTICE) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
- **Native Engine Interop Layer (`native/`, excluding `native/deps/`):** Licensed under **`MPL-2.0`** ([`native/LICENSE`](native/LICENSE)).
- **Documentation (`docs/`):** Licensed under **`CC BY-NC-SA 4.0`** ([`docs/LICENSE.md`](docs/LICENSE.md)).

---

## 4. Good-Faith Rightsholder, Trademark & Attribution Contact

We deeply respect intellectual property rights, open-source licenses, and accurate contributor credit. If you are a rightsholder, authorized representative, or open-source author and believe any file, notice, or reference in this repository raises a copyright, trademark, or attribution concern:

1. **Private Channel (Preferred for Sensitive Legal/IP Notices):** Open a private report via **GitHub Private Vulnerability Reporting** (`Security` tab → **Report a vulnerability** on <https://github.com/guccid0nut/PKV/security/advisories/new>). Reports go privately to the maintainer without exposing personal contact data publicly.
2. **Public Attribution / Credit Channel:** Open an issue using the **Attribution, License, or IP Inquiry** template (<https://github.com/guccid0nut/PKV/issues/new/choose>).

**Good-Faith Commitment:** Valid attribution fixes or removal requests are acknowledged promptly (targeted within **one week**) so concerns can be resolved cooperatively and transparently.
