# 🚀 Project KONGOR Deployment & Operations Runbook

> **Scope:** Web services, master server, chat server, and dedicated game server cluster (COMPEL).  
> **Target Audience:** DevOps engineers, platform administrators, and community host operators.

---

## 🏛️ Architecture & Port Reference

| Service | Component Code | Default Port(s) | Health Check Endpoint | Primary Protocol |
| :--- | :--- | :---: | :---: | :--- |
| **Master Server** | `KONGOR` | `55555` | `http://127.0.0.1:55555/health` | HTTP / REST / SRP-6a |
| **Web API** | `ZORGATH` | `5200` / `5201` | `http://127.0.0.1:5200/health` | REST / OpenAPI (Scalar) |
| **Web UI** | `DAWNBRINGER` | `5266` / `5557` | `http://127.0.0.1:5266/health` | ASP.NET Core Razor / Tailwind |
| **Chat Server** | `TRANSMUTANSTEIN` | `5554` (HTTPS), `11031-11033` (TCP) | `https://127.0.0.1:5554/health` | System.IO.Pipelines TCP Framing |
| **Server Manager (Host)**| `COMPEL` | `11234` (HTTP), `11235+` (UDP) | `http://127.0.0.1:11234/health` | UDP Game / Challenge-Response |
| **Server Manager (Docker Tier D)**| `COMPEL` | `11334` (HTTP), `11335-11342` (UDP) | `http://127.0.0.1:11334/health` | Containerized Ranked/Public Fleet |
| **Queue Arcade (Docker Tier Q)**| `COMPEL` | `11484` (HTTP), `11485-11492` (UDP) | `http://127.0.0.1:11484/health` | Containerized "While You Wait" Fleet |
| **Distributed Cache**| Redis / Garnet | `28298` (Local / Aspire / Docker), `6379` | `redis-cli -p 28298 ping` | RESP3 |
| **Database** | PostgreSQL (`MERRICK`) | `5432` | `pg_isready` | PostgreSQL Wire |

---

## 📋 Pre-Deployment Verification Checklist

Before deploying updates to staging or production:

1. **Verify Environment Prerequisites:**
   ```powershell
   ./kongor.ps1 doctor
   ```
2. **Compile Native C++ Shims & Proxies:**
   ```powershell
   ./kongor.ps1 build native
   ```
3. **Execute Full Test Suites:**
   ```powershell
   ./kongor.ps1 test unit
   ./kongor.ps1 test integration
   ./kongor.ps1 test soak
   ```
4. **Scan Dependencies for Known CVEs (SARIF 2.1.0):**
   ```powershell
   ./kongor.ps1 scan
   ```
5. **Verify Service Health Status:**
   ```powershell
   ./kongor.ps1 health
   ```
6. **Generate CycloneDX SBOM (Software Bill of Materials):**
   ```powershell
   ./kongor.ps1 sbom
   ```
7. **Compile Production Tailwind CSS Asset Bundle:**
   ```powershell
   ./kongor.ps1 tailwind build
   ```

---

## 📦 Deployment Workflows

### 1. Local & Staging Orchestration via .NET Aspire or Turnkey Docker Stack

#### A. Turnkey Full-Stack Docker Compose (`kongor-stack`)
Launches `garnet`, `master-server`, `chat-server`, `web-api`, `web-ui`, Dedicated Match Fleet (`:11334`), and Queue Arcade Fleet (`:11484`) inside Linux containers with fail-closed secret validation:

```powershell
# 1. Rotate placeholder/dev-default secrets safely (.bak backup created automatically)
./kongor.ps1 docker init-secrets
./kongor.ps1 docker check

# 2. Launch complete stack (Eco | Balanced | High) + optional native Windows HoN client
./kongor.ps1 docker up --perf Balanced --client

# 3. Verify container & COMPEL worker fleet health
./kongor.ps1 status
```

#### B. Native Host Orchestration via .NET Aspire
Recommended when step-debugging C# microservices directly in your IDE:

```powershell
# 1. Compile all solution projects
./kongor.ps1 build

# 2. Start full local cluster with OpenTelemetry dashboard
./kongor.ps1 aspire

# 3. Access Aspire Telemetry Dashboard
# Open https://localhost:5550 in browser
```

---

### 2. Standalone Microservice Deployments (Systemd / Docker)

For production host machines running core services:

```powershell
# Publish Release binaries for standalone hosting
dotnet publish src/services/master-server/KONGOR.MasterServer.csproj -c Release -o /opt/kongor/master
dotnet publish src/services/web-api/ZORGATH.WebPortal.API.csproj -c Release -o /opt/kongor/api
dotnet publish src/services/web-ui/DAWNBRINGER.WebPortal.UI.csproj -c Release -o /opt/kongor/ui
dotnet publish src/services/chat-server/TRANSMUTANSTEIN.ChatServer.csproj -c Release -o /opt/kongor/chat

# Restart systemd units
sudo systemctl restart kongor-master
sudo systemctl restart kongor-api
sudo systemctl restart kongor-ui
sudo systemctl restart kongor-chat

# Verify operational status
./kongor.ps1 health
```

---

### 3. Dedicated Game Server Fleet (COMPEL via Agones Kubernetes)

COMPEL instances run inside isolated Win32 / Linux container sandboxes managed by Agones:

#### A. Build and Push Container Images
```powershell
docker build -f infra/docker/Dockerfile.dedicated -t projectkongor/compel-dedicated:latest .
```

#### B. Deploy or Update Agones Fleet
```powershell
# Apply fleet configuration (infra/k8s/agones/fleet.yaml)
kubectl apply -f infra/k8s/agones/fleet.yaml

# Perform a rolling update on the game server fleet
kubectl patch fleet kongor-dedicated-fleet -n default --type=json `
  -p='[{"op":"replace","path":"/spec/template/spec/template/spec/containers/0/image","value":"projectkongor/compel-dedicated:latest"}]'

# Monitor fleet allocation and readiness
kubectl get fleet kongor-dedicated-fleet -n default -w
kubectl get gameservers -n default
```

#### C. Rollback Agones Fleet
```powershell
# Revert image to previous stable tag
kubectl patch fleet kongor-dedicated-fleet -n default --type=json `
  -p='[{"op":"replace","path":"/spec/template/spec/template/spec/containers/0/image","value":"projectkongor/compel-dedicated:v5.0-stable"}]'
```

---

### 4. Database Migrations (`MERRICK` EF Core)

All schema changes are tracked via EF Core migrations in `src/libs/database-context`:

```powershell
# 1. Create a safe point-in-time database snapshot
pg_dump -h <host> -p 5432 -U kongor -d merrick > merrick_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss').sql

# 2. Check pending migrations
dotnet ef migrations list `
  --project src/libs/database-context `
  --startup-project src/services/web-api

# 3. Apply migrations to target database
dotnet ef database update `
  --project src/libs/database-context `
  --startup-project src/services/web-api

# 4. Rollback to a specific prior migration if needed
dotnet ef database update <TargetMigrationName> `
  --project src/libs/database-context `
  --startup-project src/services/web-api
```
