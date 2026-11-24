# ADR-0005: .NET Aspire for Local Ecosystem Orchestration

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

Running the complete Project Kongor server topology requires orchestrating PostgreSQL 17, Redis, the KONGOR Master Server, TRANSMUTANSTEIN Chat Server, ZORGATH Web API, and DAWNBRINGER Web UI, alongside distributed OpenTelemetry collectors and health checks. Requiring developers to manage full Kubernetes clusters locally adds significant friction and resource overhead.

## Decision

1. **.NET Aspire as Developer Orchestrator:** The `ASPIRE.ApplicationHost` (`src/services/aspire-host`) serves as the single-command entry point (`./kongor.ps1 run aspire`) for local multi-service orchestration.
2. **Built-in Service Discovery & Dashboard:** Aspire provides local port allocation, service discovery, structured logging, distributed OpenTelemetry tracing, and resource health status on `localhost:5550`.
3. **Production Separation:** In production environments, services deploy directly to K3s/Kubernetes with Agones, bypassing Aspire AppHost while continuing to consume `ASPIRE.ServiceDefaults` for telemetry and health probes.

## Alternatives Considered

1. **Docker Compose Only:** Rejected. Lacks deep .NET runtime integration, dynamic launch profiles, OpenTelemetry trace aggregation, and direct IDE debugging hooks.
2. **Local Kubernetes (Minikube/Kind):** Rejected. Too heavy for casual developer machines and complicates local debugger attachment.

## Consequences

- **Positive:** Single-command local startup (`./kongor.ps1 run aspire`); unified web dashboard for logs and distributed traces; zero Kubernetes overhead on dev workstations.
- **Negative:** Dual deployment definitions (Aspire AppHost for dev vs. Helm/K3s manifests for prod).
- **Neutral:** All services share identical configuration schemas via `ServiceDefaults`.
