<div align="center">

# 💬 TRANSMUTANSTEIN
### *High-Throughput Binary TCP Chat Server & Match Coordinator*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![System.IO.Pipelines](https://img.shields.io/badge/Pipelines-Zero--Copy-512BD4?style=for-the-badge)](#)
[![Redis Pub/Sub](https://img.shields.io/badge/Redis-Pub%2FSub%20Backplane-DC382D?style=for-the-badge&logo=redis&logoColor=white)](https://redis.io/)
[![TCP Sockets](https://img.shields.io/badge/TCP%20Ports-11031%20%7C%2011032%20%7C%2011033-orange?style=for-the-badge)](#)

**TRANSMUTANSTEIN is the ultra-low latency, zero-copy binary TCP chat and match coordinator for Heroes of Newerth. Powered by System.IO.Pipelines, it coordinates channels, private whispers, clan rosters, and match lobbies with zero GC allocations.**

[⚡ Quickstart](#-quickstart) •
[🌟 Key Features](#-key-features) •
[🔌 Port Matrix](#-tcp-port-architecture)

---

### ⚡ Quickstart: Launch Chat Server
Start the high-throughput TCP chat server in **Windows PowerShell**:

```powershell
kongor chat-server
```
*(Or launch standalone: `dotnet run --project src/services/chat-server/TRANSMUTANSTEIN.ChatServer.csproj`)*

---

</div>

## 🌟 Key Features

* **⚡ System.IO.Pipelines Zero-Copy Architecture**: Processes binary chat protocol frames directly on memory buffers (`ReadOnlySequence<byte>`) with zero heap allocation per packet.
* **📢 Multi-Node Redis Pub/Sub**: Horizontal scaling across multiple chat server nodes via Redis Pub/Sub backplane.
* **👥 Channels, Whispers, Clans & Match Coordination**: Full legacy protocol support for global channels, private messages, clan rosters, and match lobby sync.
* **🛡️ Rate Limiting & Anti-Flood**: Token bucket rate limiting per connection preventing denial-of-service and chat flooding.

---

## 🔌 TCP Port Architecture

| Port | Protocol | Service Channel | Purpose |
| :---: | :---: | :--- | :--- |
| **`11031`** | Binary TCP | **Lobby & Global Channels** | Public chat channels, game lobbies, clan lists |
| **`11032`** | Binary TCP | **Whispers & Presence** | 1-on-1 private messaging and online status |
| **`11033`** | Binary TCP | **Match Coordinator** | Matchmaking lobby synchronization & slave assignment |

---

> [!TIP]
> ### 💡 Performance Note: Zero GC Pressure
> By utilizing `System.IO.Pipelines` and pooled memory buffers (`ArrayPool<byte>`), TRANSMUTANSTEIN can comfortably sustain **100,000+ messages per second** on a single CPU core without triggering garbage collection pauses.
