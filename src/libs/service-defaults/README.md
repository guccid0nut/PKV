<div align="center">

# 📊 Aspire Service Defaults (`Kongor.ServiceDefaults`)
### *Cross-Cutting Observability, Health Checks & Polly v8 Resilience*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Aspire 13.5](https://img.shields.io/badge/Aspire-13.5-0078D4?style=for-the-badge&logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-1.10-F5A800?style=for-the-badge&logo=opentelemetry&logoColor=white)](https://opentelemetry.io/)
[![Polly v8](https://img.shields.io/badge/Polly-v8%20Resilience-purple?style=for-the-badge)](#)

**Aspire Service Defaults provides shared microservice resilience, standardized health checks, and OpenTelemetry tracing across all ProjectKongor services.**

[🌟 Key Features](#-key-features)

---

</div>

## 🌟 Key Features

* **🏥 Standardized Health Checks**: Health endpoints (`/health`, `/alive`) checking database connectivity, Redis ping, and circuit breaker degradation.
* **📈 OpenTelemetry Tracing & Metrics**: Automatic tracing of incoming HTTP requests, EF Core database queries, and Redis operations.
* **🛡️ Polly v8 Resilience**: Unified HTTP resilience handlers with automatic retries, exponential backoffs, and circuit breaking.
