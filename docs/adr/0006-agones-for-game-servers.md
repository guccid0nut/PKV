# ADR-0006: Agones Kubernetes Operator for Game Server Fleets

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

Dedicated game servers for Heroes of Newerth (`hon_x64.exe`) are stateful, high-throughput UDP workloads. Standard Kubernetes `Deployments` and `ReplicaSets` treat pods as stateless, ephemeral instances, and will terminate or reschedule pods mid-game during scaling events or node drains. Furthermore, game sessions require deterministic public UDP port allocation and lifecycle state management (`Ready`, `Allocated`, `Reserved`, `Shutdown`).

## Decision

1. **Agones as Fleet Controller:** Production game server fleets are managed via Google/Linux Foundation's Agones operator running on K3s/Kubernetes.
2. **Dedicated GameServer CRDs:** Each game instance is deployed as an Agones `GameServer` custom resource, enabling the matchmaker to allocate and lock specific instances without risk of termination during autoscaling.
3. **COMPEL Fleet Supervisor Integration:** The `COMPEL` supervisor handles local process sandboxing (via Win32 Job Objects on bare-metal nodes) and communicates with the Agones SDK sidecar when executing inside Kubernetes.

## Alternatives Considered

1. **Vanilla Kubernetes StatefulSets:** Rejected. StatefulSets do not support dynamic allocation locks, session-aware drain protections, or per-pod dynamic public UDP port ranges.
2. **Static Bare-Metal Hosting Only:** Rejected. Lacks automated fleet elasticity, rolling upgrades, and cloud multi-region scale.

## Consequences

- **Positive:** Zero player disconnects during fleet autoscaling; native UDP port mappings; automated health checks and zombie process termination.
- **Negative:** Requires running the Agones CRDs and operator controller in the cluster.
- **Neutral:** Local development continues to use COMPEL standalone or Docker without requiring a local Agones installation.
