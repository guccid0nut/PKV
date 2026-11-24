# 📚 Project KONGOR V API Catalog

Welcome to the subsystem API catalog for **Project KONGOR V (`PKV`)** applications, microservices, shared libraries, extensions, and developer tools.

> [!NOTE]
> Generate full C# XML doc API metadata locally with `docfx metadata docfx.json` followed by `docfx build docfx.json` (or `.\kongor.ps1 docs`).

---

## 🏛️ Monorepo Subsystems & Canonical Module Guides

### Applications (`src/apps/`)
- **WILLOWMAKER (`src/apps/willowmaker`):** Native client launcher, DXVK Vulkan 1.3 injection, CDN sync, and 1000 FPS unlocker — [`README`](../../src/apps/willowmaker/README.md)
- **COMPEL (`src/apps/compel`):** Dedicated server fleet supervisor, 1.0 ms timer pacer, UDP proxy, and Win32 Job Object sandbox — [`README`](../../src/apps/compel/README.md)
- **COMPEL Chat (`src/apps/compel-gui`):** Avalonia desktop host chat & fleet command console — [`README`](../../src/apps/compel-gui/README.md)
- **NEXUS Control Panel (`src/apps/control-panel`):** Cross-platform Avalonia `11.2.3` management GUI, Docker Stack launcher, secrets vault, and 3D viewer — [`README`](../../src/apps/control-panel/README.md)
- **NEXUS Replay Studio (`src/apps/replay-studio`):** Experimental camera spline director and 60 FPS MP4 highlight exporter — [`README`](../../src/apps/replay-studio/README.md)

### Microservices (`src/services/`)
- **Aspire AppHost (`src/services/aspire-host`):** `.NET Aspire 13.6.5` orchestration and OpenTelemetry distributed dashboard — [`README`](../../src/services/aspire-host/README.md)
- **KONGOR Master Server (`src/services/master-server`):** Central coordinator, SRP-6a authentication, Goblin store, and 4-stage match pipeline — [`README`](../../src/services/master-server/README.md)
- **TRANSMUTANSTEIN Chat Server (`src/services/chat-server`):** Zero-copy `System.IO.Pipelines` binary TCP chat server (Ports `11031–11033`) — [`README`](../../src/services/chat-server/README.md)
- **ZORGATH Web Portal API (`src/services/web-api`):** REST API with JWT, Discord OAuth2, and Scalar OpenAPI (`/scalar/v1`) — [`README`](../../src/services/web-api/README.md)
- **DAWNBRINGER Web Portal UI (`src/services/web-ui`):** ASP.NET Core MVC Razor player portal for match tracking and leaderboards — [`README`](../../src/services/web-ui/README.md)

### Core Libraries (`src/libs/`)
- **Contracts (`src/libs/contracts`):** Shared MediatR CQRS records, DTOs, and wire contracts — [`README`](../../src/libs/contracts/README.md)
- **Protocol Core (`src/libs/protocol-core`):** Binary packet framing, RFC 5054 SRP-6a, PHP serializer, and S2 cryptography — [`README`](../../src/libs/protocol-core/README.md)
- **MERRICK Database Context (`src/libs/database-context`):** EF Core multi-database persistence (PostgreSQL, Supabase, SQL Server, SQLite) — [`README`](../../src/libs/database-context/README.md)
- **Service Defaults (`src/libs/service-defaults`):** OpenTelemetry, health checks, resilience, and forwarded-headers middleware — [`README`](../../src/libs/service-defaults/README.md)
- **Source Generators (`src/libs/source-generators`):** Compile-time Roslyn source generators — [`README`](../../src/libs/source-generators/README.md)

### Extensions (`src/extensions/`)
- **CDN Broker (`src/extensions/cdn-broker`):** Differential CDN patch synchronizer — [`README`](../../src/extensions/cdn-broker/README.md)
- **Modding (`src/extensions/modding`):** `.honmod` / `.s2z` archive engine, XML entity merger, and VFS precedence — [`README`](../../src/extensions/modding/README.md)
- **Replay Renderer (`src/extensions/replay-renderer`):** Headless Vulkan swapchain capture & NVENC MP4 pipeline — [`README`](../../src/extensions/replay-renderer/README.md)
- **Spatial Audio (`src/extensions/spatial-audio`):** OpenAL / HRTF 3D spatial audio engine — [`README`](../../src/extensions/spatial-audio/README.md)
- **Vulkan Interop (`src/extensions/vulkan-interop`):** Shared-memory Vulkan receiver & glTF 2.0 loader — [`README`](../../src/extensions/vulkan-interop/README.md)

### Developer CLI, Pipelines & Native Layer (`src/tools/`, `tools/`, `native/`)
- **Kongor CLI & TAO MCP (`src/tools/kongor-cli`):** Unified `Spectre.Console` CLI and native JSON-RPC 2.0 TAO MCP server — [`README`](../../src/tools/kongor-cli/README.md) • [`TAO Overview`](../tao-mcp/overview.md)
- **Additional CLIs & Tools:** [`EndpointAuditor`](../../src/tools/endpoint-auditor/README.md) • [`HeadlessRenderer`](../../src/tools/headless-renderer/README.md) • [`LuaVerifier`](../../src/tools/lua-verifier/README.md) • [`ModCli`](../../src/tools/mod-cli/README.md) • [`PrebakeTool`](../../src/tools/prebake-tool/README.md) • [`AssetDB`](../../tools/assetdb/README.md) • [`Export`](../../tools/export/README.md) • [`Spectator Console`](../../tools/spectator-console/README.md) • [`Ghidra MCP`](../tools/ghidra.md)
- **Native Interop (`native/`):** Unified CMake proxies (`MPL-2.0`) — [`Native Layer`](../../native/README.md) • [`Proxies`](../../native/proxies/README.md)
