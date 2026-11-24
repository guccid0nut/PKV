# ADR-0007: Autonomous Agent Token-Bucket Rate Limiting

- **Status:** Accepted
- **Date:** 2026-09-28
- **Deciders:** Core Engineering Team

## Context

The Together As One (TAO) MCP Server exposes 106+ ecosystem tools to LLM coding agents and autonomous supervisors. Without strict, deterministic rate-limiting and budget controls, runaway LLM loops can overwhelm master server databases, spam dedicated server commands, or trigger rapid high-frequency tool calls that exhaust host system resources.

## Decision

1. **Per-Agent Token Buckets:** Implement in-memory, thread-safe token-bucket rate limiting partitioned by calling agent conversation ID and tool classification category.
2. **Category-Based Budgets:** Tools declare rate limit tiers (`ReadSafe`, `StandardAction`, `DestructiveMutation`, `InfrastructureAdmin`) upon registration rather than relying on a global static map, preventing newly added tools from silently defaulting to permissive limits.
3. **Graceful Exhaustion Handling:** When a tool call exceeds its token budget, the MCP server returns structured error metadata indicating retry-after duration, remaining tokens, and budget capacity, allowing agents to back off rather than fail abruptly.

## Alternatives Considered

1. **Global Unpartitioned Rate Limiter:** Rejected. A single rogue agent could starve all other agents or developer sessions.
2. **Fixed Time Windows:** Rejected. Bursty traffic at the edge of fixed windows can still create spikes; token-bucket algorithms smooth out bursts while enforcing strict sustained limits.

## Consequences

- **Positive:** Complete protection against LLM infinite execution loops; reliable quality of service across concurrent agents; transparent error telemetry.
- **Negative:** Adds slight tracking overhead and memory tracking for active agent sessions.
- **Neutral:** Local developer CLI operations bypass agent limits via direct invocations.
