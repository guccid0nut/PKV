# 🔧 Project KONGOR Troubleshooting & Diagnostics Guide

> **Purpose:** Step-by-step triage procedures for operational incidents, authentication issues, matchmaking hangs, and performance bottlenecks.

---

## 🚨 Incident Triage Decision Matrix

```
[Issue Reported]
   │
   ├─► "Players cannot log into game client or web"
   │     └─► See Section 1: Authentication & Login Failures
   │
   ├─► "Queue is popping but match never launches"
   │     └─► See Section 2: Matchmaking & Game Server Allocation
   │
   ├─► "Web Portal is unresponsive or unstyled"
   │     └─► See Section 3: Web Portal (DAWNBRINGER) Issues
   │
   └─► "High latency, micro-stutters, or tickrate lag"
         └─► See Section 4: Performance & System Telemetry
```

---

## 1. Authentication & Login Failures

### Symptom: Client displays "Login Failed", "Invalid Protocol", or timeouts

#### Step 1: Query Master Server Health Endpoint
```powershell
./kongor.ps1 health
# Or directly via curl:
curl.exe -i http://127.0.0.1:55555/health
```
- **If `postgresql: Unhealthy`:** The MERRICK database connection is failing. Check PostgreSQL service status (`pg_isready`) or network connectivity.
- **If `redis: Degraded`:** Session cache is down or timing out. Authentications will fail or fall back to cold database lookups.

#### Step 2: Check Master Server Logs for SRP-6a Errors
```powershell
./kongor.ps1 logs master | Select-String "SRP|Auth|Handshake|Error"
```

#### Step 3: Inspect Player Account State in PostgreSQL
Connect to the MERRICK database and inspect the user record:
```sql
SELECT "AccountId", "Nickname", "AccountType", "IsBanned", "SuspensionExpiresUtc"
FROM "Accounts"
WHERE LOWER("Nickname") = LOWER('<username>');
```
- If `SuspensionExpiresUtc > NOW()`, inform the player of their active suspension.
- If `IsBanned = true`, account was terminated by staff.

---

## 2. Matchmaking & Game Server Allocation

### Symptom: Players join queue, timer ticks indefinitely, or allocation fails

#### Step 1: Check Dedicated Game Server Fleet Capacity
```powershell
# Check Agones game server status
kubectl get gameservers -n default

# Count servers in Ready state:
(kubectl get gameservers -n default -o json | ConvertFrom-Json).items |
  Where-Object { $_.status.state -eq "Ready" } | Measure-Object
```
- If **0 Ready servers** exist, new matches cannot start.
- Check if pods are crashlooping:
  ```powershell
  kubectl describe fleet kongor-dedicated-fleet -n default
  ```

#### Step 2: Inspect Redis Matchmaking Queues
Inspect active matchmaking tickets and queues on port 28298:
```powershell
# In PowerShell:
redis-cli -p 28298 KEYS "MATCHMAKING:*"
redis-cli -p 28298 KEYS "LOBBY:*"

# Clear corrupted or orphaned match locks:
redis-cli -p 28298 DEL "MATCHMAKING:LOCK:GLOBAL"
```

#### Step 3: Check TRANSMUTANSTEIN Chat Server
Match coordination and lobby broadcasts rely on the chat server:
```powershell
curl.exe -k -i https://127.0.0.1:5554/health
```

---

## 3. Web Portal (DAWNBRINGER) Issues

### Symptom: Web UI throws 502 / 503 or styles fail to render

#### Step 1: Check Web UI & API Health
```powershell
# Check Web UI
curl.exe -i http://127.0.0.1:5266/health

# Check Web API dependency
curl.exe -i http://127.0.0.1:5200/health
```
- If Web API is down, DAWNBRINGER will degrade to cached or empty data views.

#### Step 2: Rebuild Tailwind CSS Bundle
If the portal renders without styles or icons:
```powershell
./kongor.ps1 tailwind build
```

---

## 4. Performance, Latency & Tickrate Spikes

### Symptom: Server tickrate drops below 50 TPS or high input latency

#### Step 1: Inspect Continuous CPU Profiles (Pyroscope)
Open the Pyroscope profiling dashboard:
```
http://localhost:4040
```
- Select application `compel` or `kongor-master`
- Check the Flamegraph for hot methods in garbage collection, serialization, or physics calculations.

#### Step 2: Check Redis Command Latency
```powershell
redis-cli -p 28298 --latency-history
```
- Latency should consistently remain `< 1.0 ms`. If latency exceeds `10 ms`, inspect background key eviction or disk fsync bottlenecks.

#### Step 3: Check Slow Database Queries
Run query profiling against PostgreSQL:
```sql
SELECT query, calls, mean_exec_time, max_exec_time
FROM pg_stat_statements
ORDER BY mean_exec_time DESC
LIMIT 10;
```

---

## 5. Turnkey Docker Stack (`kongor-stack`) Triage

### Symptom A: `kongor docker up` aborts at pre-flight (`exit 2`)
- **Cause**: Missing or development-placeholder values in `.NET User Secrets` (`00005072-6f6a-6563-7420-4b4f4e474f52`), `.env`, or host ports already occupied by local Aspire/COMPEL processes.
- **Fix**:
  ```powershell
  # Safely generate/rotate missing or placeholder cryptographic secrets (.bak backup created first)
  ./kongor.ps1 docker init-secrets

  # Re-run fail-closed pre-flight validation
  ./kongor.ps1 docker check

  # Automatically stop conflicting local host .NET/COMPEL processes before starting containers
  ./kongor.ps1 docker up --perf Balanced --stop-host-conflict
  ```

### Symptom B: Host `hon_x64.exe` logs in, sees lobby, but times out connecting to containerized UDP match
- **Cause**: Split-horizon UDP routing mismatch if your workstation switched Wi-Fi/VPN interfaces after `userdata/docker/.env.stack` was generated.
- **Fix**: Re-run `./kongor.ps1 docker up --perf Balanced` so [`scripts/docker/launch_docker_stack.ps1`](../../scripts/docker/launch_docker_stack.ps1) refreshes `CHAT_SERVER_LAN_HOST` to your active routed LAN IPv4 address, and verify published UDP ports (`11335-11342` Tier D, `11485-11492` Tier Q) via `./kongor.ps1 status`.

---

## 🛠️ Quick Diagnostic Commands Cheat Sheet

| Task | Command |
| :--- | :--- |
| Environment, Port & Docker Stack Diagnostic | `./kongor.ps1 doctor` |
| Unified Ecosystem & Container Fleet Status | `./kongor.ps1 status` |
| Fail-Closed Docker Pre-Flight & Secret Check | `./kongor.ps1 docker check` |
| Stream Live Container or Host Service Logs | `./kongor.ps1 docker logs -s master-server -f` |
| Recompile Tailwind Assets | `./kongor.ps1 tailwind build` |
| Run Complete Automated Test Suite | `./kongor.ps1 test unit` |
| Stop Orphaned Host & Docker Stack Processes | `./kongor.ps1 stop all` |
