<div align="center">

# 📦 NEXUS Contracts & DTOs (`NEXUS.Contracts`)
### *Strongly-Typed Domain Contracts, RPC Interfaces & DTO Records*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![AOT Ready](https://img.shields.io/badge/Native%20AOT-Compatible-2ECC71?style=for-the-badge)](#)
[![Records](https://img.shields.io/badge/C%23%20Records-Immutable-blue?style=for-the-badge)](#)

**NEXUS Contracts defines the strongly-typed DTO records, RPC interfaces, domain events, and shared contracts used across the entire ProjectKongor ecosystem.**

[🌟 Key Components](#-key-components)

---

</div>

## 🌟 Key Components

* **📜 DTO Records**: Immutable, zero-overhead C# records for account profiles, match submissions, leaderboard rankings, and store items.
* **🔌 Domain Interfaces**: Canonical service abstractions (`IEconomyService`, `IDiscordPresenceService`, `IStorageService`, `IAuthService`).
* **📢 Events & Messages**: Strongly-typed payload envelopes for Redis Pub/Sub events and Discord webhook notifications.
* **🛡️ Native AOT Verified**: Strict serialization attributes ensuring 100% compatibility with Native AOT trimming and compilation.
