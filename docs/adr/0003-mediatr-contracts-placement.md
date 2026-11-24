# ADR-0003: MediatR Contracts Placement in NEXUS.Contracts

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

The Project Kongor backend services (`KONGOR`, `ZORGATH`, `TRANSMUTANSTEIN`) utilize CQRS (Command Query Responsibility Segregation) patterns powered by MediatR. Historically, request contracts and their execution handlers were frequently co-located within service-specific folders, creating tight coupling and making it difficult for external consumers (such as client CLIs, Avalonia desktop apps, and cross-service RPC callers) to reuse command and query types without referencing entire service assemblies.

## Decision

1. **Contracts in `NEXUS.Contracts`:** All `IRequest<T>`, `ICommand<T>`, `IQuery<T>`, and domain event records are declared under `src/libs/contracts/NEXUS.Contracts/CQRS/{Domain}/`.
2. **Handlers in Microservices:** All `IRequestHandler<TRequest, TResponse>` implementations reside exclusively inside the consuming microservice under `Features/{Domain}/`.
3. **Native AOT & Trimming Friendly:** `NEXUS.Contracts` is marked `<IsAotCompatible>true</IsAotCompatible>` and contains only lightweight data records and Protobuf/gRPC message types with zero runtime business dependencies.

## Alternatives Considered

1. **Co-locate Handlers and Contracts:** Rejected. Forced client applications to reference database entities, EF Core contexts, and server middleware just to dispatch a query.
2. **Per-Service Contract Libraries:** Rejected. Introducing 10+ miniature contract projects added unacceptable NuGet and MSBuild compilation overhead across the 34-project solution.

## Consequences

- **Positive:** Clean contract reuse across desktop GUI (`NEXUS`), CLI (`TAO`), and backend services; fast compile times; decoupled business logic.
- **Negative:** Adding a new CQRS command requires creating the record in `NEXUS.Contracts` and implementing the handler in the service project.
