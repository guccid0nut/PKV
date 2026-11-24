<div align="center">

# 🗄️ MERRICK Database Context (`NEXUS.DatabaseContext`)
### *Multi-Provider Entity Framework Core Data Layer for Heroes of Newerth*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![EF Core 10](https://img.shields.io/badge/EF%20Core-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://learn.microsoft.com/en-us/ef/core/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-17.0-336791?style=for-the-badge&logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![Supabase](https://img.shields.io/badge/Supabase-Cloud%20Database-3ECF8E?style=for-the-badge&logo=supabase&logoColor=white)](https://supabase.com/)

**MERRICK Database Context provides the multi-provider Entity Framework Core data access layer for player accounts, inventories, match statistics, and store transactions.**

[🌟 Key Features](#-key-features) •
[🚀 DI Setup](#-usage-in-services)

---

</div>

## 🌟 Key Features

* **🔌 Multi-Provider Support**: Seamless support for PostgreSQL 17 (Npgsql), Supabase, and Microsoft SQL Server 2022.
* **📦 100% Decoupled Repository Pattern**: Repositories (`IAccountRepository`, `IMatchRepository`, `IHeroStatsRepository`, `IEconomyRepository`) are fully abstracted from database contexts.
* **⚡ Compiled Queries & Optimized Indices**: High-throughput LINQ compiled queries for sub-millisecond account lookups and rating queries.
* **🛡️ Resilient Migrations**: Automatic migration execution and Testcontainers integration for integration testing.

---

## 🚀 Usage in Services

```csharp
// Program.cs registration
builder.Services.AddMerrickDbContext(builder.Configuration);
builder.Services.AddMerrickRepositories();
```
