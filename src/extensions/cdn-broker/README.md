<div align="center">

# 🌐 CDN Content Broker & Discord Presence (`NEXUS.CdnBroker`)
### *Differential Asset Synchronization & Discord Rich Presence Engine*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Discord RPC](https://img.shields.io/badge/Discord-Rich%20Presence-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.com/)
[![SHA-256 / CRC32](https://img.shields.io/badge/Integrity-SHA256%20%26%20CRC32-green?style=for-the-badge)](#)

**CDN Content Broker handles dynamic client asset synchronization, configurable S3/community differential delta patching, and real-time Discord Rich Presence integration.**

[🌟 Key Features](#-key-features) •
[🎮 Discord Integration](#-discord-rich-presence)

---

</div>

## 🌟 Key Features

* **📦 Differential Manifest Engine**: Computes CRC32 and SHA-256 hashes of client assets, creating differential patch manifests so players only download changed files.
* **🎮 Discord Rich Presence Service**: Communicates with the Discord desktop client via Named Pipes (`DiscordPresenceService.cs`) using App ID `1003760104199422052`.
* **🦸 Hero Identity Resolver**: Resolves internal entity slugs (`Hero_Devourer`, `Hero_Rocky`) to human-readable names, roles, and CDN portrait assets.
