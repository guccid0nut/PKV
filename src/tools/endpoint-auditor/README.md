# 🔍 Endpoint Auditor (`EndpointAuditor`)

> **Static client-to-master-server route reconciler verifying K2 Lua/XML `SubmitForm` targets against `KONGOR.MasterServer` (`net11.0`).**

`EndpointAuditor` scans HoN client UI scripts/XML (`FormDefinition`, `SubmitForm`, direct PHP/API calls) and reconciles every route against `KONGOR.MasterServer` handlers and controllers to surface missing, partial, or deprecated endpoints.

---

## ⚡ Quick Start

```powershell
# Via unified Kongor CLI
.\kongor.ps1 audit-endpoints

# Or run directly (args: [clientDir] [nexusDir] [outputDir])
dotnet run --project src/tools/endpoint-auditor/EndpointAuditor.csproj -- <clientDir> src/services/master-server
```

---

## 🏗️ Architecture

- **Client Scanner (`Program.cs`):** Inspects loose files and `.s2z` archives for XML `<form>` definitions, Lua `SubmitForm(...)` calls, and direct HTTP/PHP endpoints.
- **Master Server Scanner:** Indexes `[Http*]` controller routes and `f=<action>` `ClientRequester` / `ServerRequester` handlers under `src/services/master-server/`.
- **Output:** Emits structured JSON comparison reports classifying each endpoint as `Implemented`, `Partially Implemented`, `Missing`, or `External/Deprecated`.
