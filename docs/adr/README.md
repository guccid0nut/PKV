# 📜 Architecture Decision Records (ADRs)

Architecture Decision Records document the key architectural and technical choices made throughout the Project Kongor ecosystem, preserving the rationale, trade-offs, and consequences of design decisions.

## Index of Accepted Records

| ADR | Title | Status | Date | Area |
|-----|-------|:------:|:----:|------|
| [ADR-0001](0001-srp-auth-permanence.md) | SRP Authentication Permanence | Accepted | 2026-09-28 | Cryptography / Master Server |
| [ADR-0002](0002-docker-dotnet-boundaries.md) | Docker.DotNet vs. CLI Execution Boundaries | Accepted | 2026-09-28 | Infrastructure / Server Supervisor |
| [ADR-0003](0003-mediatr-contracts-placement.md) | MediatR Contracts Placement in NEXUS.Contracts | Accepted | 2026-09-28 | Architecture / CQRS |
| [ADR-0004](0004-tailwind-standalone-cli.md) | Tailwind CSS v4 Standalone CLI Build Pipeline | Accepted | 2026-09-28 | Web UI / Front-end |
| [ADR-0005](0005-aspire-for-orchestration.md) | .NET Aspire for Local Ecosystem Orchestration | Accepted | 2026-09-28 | Orchestration / Developer Experience |
| [ADR-0006](0006-agones-for-game-servers.md) | Agones Kubernetes Operator for Game Server Fleets | Accepted | 2026-09-28 | Fleet Management / COMPEL |
| [ADR-0007](0007-mcp-rate-limiting-strategy.md) | Autonomous Agent Token-Bucket Rate Limiting | Accepted | 2026-09-28 | MCP Tooling / Governance |
| [ADR-0008](0008-argon2id-shadow-upgrade.md) | Argon2id Shadow Password Hash Upgrades | Accepted | 2026-09-28 | Security / Identity |
| [ADR-0009](0009-native-build-unification.md) | Unified Cross-Platform CMake Native Build Matrix | Accepted | 2026-09-29 | Native C++ / CI/CD |
| [ADR-0010](0010-goose-systems-language-evaluation.md) | Evaluating `aardappel/goose` (`v2026.4`) for Standalone Native CLI Tools | Proposed | 2026-11-10 | Native Tooling / Systems Languages |

---

## Contributing New ADRs

To propose an architectural change or document a consensus decision:
1. Copy [0000-template.md](0000-template.md) to `NNNN-short-descriptive-title.md`.
2. Populate Context, Decision, Alternatives Considered, and Consequences.
3. Link the new ADR in this index and register it in [docs/toc.yml](../toc.yml).
