# ADR-0002: Docker.DotNet vs. CLI Execution Boundaries

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

Project Kongor orchestrates dedicated game servers both locally on developer workstations (via Docker Desktop and Windows Job Objects) and in production container fleets. Management of containerized instances requires starting, stopping, inspecting, attaching log streams, and building images. We needed clear architectural boundaries between the C# SDK (`Docker.DotNet`) and external CLI tooling (`docker` / `docker compose`).

## Decision

1. **Runtime Container Lifecycle via Docker.DotNet:** Container creation, start, stop, kill, health-checking, port bindings, and stdout/stderr stream inspection are handled via `Docker.DotNet` communicating directly over the Docker daemon socket / named pipe (`//./pipe/docker_engine` on Windows, `/var/run/docker.sock` on Linux).
2. **Image Building via CLI / BuildKit:** Container image generation (`docker build`) is executed through CLI tooling or shell scripts rather than programmatic tar-stream generation via `Docker.DotNet`. This ensures optimal BuildKit layer caching, multi-stage builds, and multi-platform compilation (`--platform linux/amd64`).

## Alternatives Considered

1. **Shell Out Everything to Docker CLI:** Rejected. Parsing raw text output from CLI commands for status monitoring and stream processing is error-prone and brittle.
2. **Programmatic Tar-Stream Builds via Docker.DotNet:** Rejected. Maintaining in-memory tar-archive streams in C# for large game asset layers proved significantly slower and lacked BuildKit caching capabilities.

## Consequences

- **Positive:** Type-safe, fast container management and real-time streaming without shell-escaping overhead; fast image builds utilizing native BuildKit engine features.
- **Negative:** Requires both `Docker.DotNet` NuGet package and local `docker` CLI in development environments.
- **Neutral:** Host supervisors (`COMPEL`) cleanly isolate container runtime operations from packaging scripts.
