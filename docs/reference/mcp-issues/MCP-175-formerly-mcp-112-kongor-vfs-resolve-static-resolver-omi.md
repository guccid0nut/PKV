---
id: MCP-175
server: kongor
tool: kongor_vfs_resolve
category: runtime-error
title: "(formerly MCP-112): KONGOR-VFS-RESOLVE-STATIC-RESOLVER-OMITS-CLIENTPACKAGEFILES"
status: patched
verified_by: "McpHighValueToolsTests.VfsResolve_MatchesCanonicalMountHierarchy (commit 5e7ff0adb)"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-10
last_seen: 2026-10-10
tags: [kongor, kongor_vfs_resolve, runtime-error]
prevention_rule: "Resolved in Step 8: `VfsResolveHandler` and `VfsLocateHandler` now evaluate K2's exact 8-layer `-mod` hierarchy (`override` -> `KONGOR/*` -> `../shared` -> `ClientPackageFiles` -> `game` -> `base`) with `.s2z` Central Directory indexing and..."
---

# MCP-175: (formerly MCP-112): KONGOR-VFS-RESOLVE-STATIC-RESOLVER-OMITS-CLIENTPACKAGEFILES

## Summary
Tracked MCP issue on `kongor` / `kongor_vfs_resolve` (category: `runtime-error`, status: `patched`, occurrences: 1). Reports accurate runtime VFS resolution precedence including ClientPackageFiles. -> Omits ClientPackageFiles from the resolution stack entirely and produces static inverted priorities.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_vfs_resolve` |
| **Expected** | Reports accurate runtime VFS resolution precedence including ClientPackageFiles. |
| **Actual (Symptom)** | Omits ClientPackageFiles from the resolution stack entirely and produces static inverted priorities. |
| **Root Cause** | VfsResolveHandler.cs:17-26 is a hard-coded static resolver that does not model the live K2 engine FileManager mod stack or ClientPackageFiles. |
| **Fix / Remediation** | Resolved in Step 8: `VfsResolveHandler` and `VfsLocateHandler` now evaluate K2's exact 8-layer `-mod` hierarchy (`override` -> `KONGOR/*` -> `../shared` -> `ClientPackageFiles` -> `game` -> `base`) with `.s2z` Central Directory indexing and `action: "audit_shadows"`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
{"virtual_path": "/ui/scripts/store2.lua"}
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_vfs_resolve
```

## Prevention Invariant
Resolved in Step 8: `VfsResolveHandler` and `VfsLocateHandler` now evaluate K2's exact 8-layer `-mod` hierarchy (`override` -> `KONGOR/*` -> `../shared` -> `ClientPackageFiles` -> `game` -> `base`) with `.s2z` Central Directory indexing and...
