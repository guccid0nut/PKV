<div align="center">

# ⚙️ ASPIRE Application Host
### *Cloud-Native Distributed Microservice Orchestrator*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![.NET Aspire 13.5](https://img.shields.io/badge/Aspire-13.5-0078D4?style=for-the-badge&logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-Tracing%20%26%20Metrics-F5A800?style=for-the-badge&logo=opentelemetry&logoColor=white)](https://opentelemetry.io/)
[![Pyroscope](https://img.shields.io/badge/Pyroscope-Continuous%20Profiling-FF5722?style=for-the-badge&logo=grafana&logoColor=white)](https://pyroscope.io/)

**ASPIRE ApplicationHost is the local orchestration layer that coordinates ProjectKongor's 4 core backend services (`MasterServer`, `ChatServer`, `WebAPI`, `WebUI`), Garnet/Redis cache, database, and optional sidecars into a unified development environment.**

[⚡ Quickstart](#-quickstart) •
[📊 Orchestrated Services](#-orchestrated-services-matrix) •
[🗺️ Architecture Topology](#-orchestration-architecture-topology)

---

### ⚡ Quickstart: Launch Cluster
Launch the local service cluster with interactive telemetry in **Windows PowerShell**:

```powershell
kongor start
```
*(Or launch standalone: `dotnet run --project src/services/aspire-host/ASPIRE.ApplicationHost.csproj`)*  
**Live Interactive Dashboard**: [`https://localhost:5550`](https://localhost:5550)

---

</div>

## 📊 Orchestrated Services Matrix

| Service | Target Port | Protocol | Technical Role |
|---|---|---|---|
| **Master Server** | `55555` | HTTP/REST | Player authentication, SRP-6a, Goblin store, stats ingestion |
| **Chat Server** | `11031-11033` | TCP Binary | Zero-copy binary chat pipelines, channels, whispers, lobbies |
| **Web API (Zorgath)** | `5100 / 5101` | HTTPS/REST | Public REST API for web portal, leaderboards, match history |
| **Web UI (Dawnbringer)** | `5200 / 5201` | HTTPS/MVC | Responsive player portal, leaderboards, account registration |
| **Replay Renderer *(Experimental)*** | `5300` | HTTP/gRPC | Optional sidecar for replay-to-MP4 highlight video rendering |
| **PostgreSQL / SQLite** | `5432` | TCP/SQL | Primary relational database (MERRICK multi-DB schema) |
| **Garnet / Redis Cache** | `6379` | TCP/RESP | Distributed caching, session tokens, and Pub/Sub backplane |
| **Pyroscope *(Optional)*** | `4040` | HTTP | Continuous CPU and memory flamegraph profiling sidecar |
| **Aspire Dashboard** | `5550` | HTTPS | Interactive web UI with OpenTelemetry traces, metrics & logs |

---

## 🗺️ Orchestration Architecture Topology

<div align="center">
  <img src="../../../docs/images/aspire_orchestration.svg" alt="Aspire Orchestration Architecture" width="100%" />
</div>

---

> [!TIP]
> ### 💡 Live Telemetry & Distributed Traces
> Access `https://localhost:5550` while testing. You can inspect exact execution millisecond timings for any database query, Redis cache hit, or HTTP request across the entire system.
