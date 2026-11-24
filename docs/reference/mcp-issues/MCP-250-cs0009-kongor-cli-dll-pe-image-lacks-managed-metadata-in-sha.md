---
id: MCP-250
server: kongor
tool: kongor_app_eval
category: runtime-error
title: "CS0009 Kongor.Cli.dll PE image lacks managed metadata in shadow runtime"
status: open
verified_by: ""
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-27
last_seen: 2026-10-27
tags: [kongor, kongor_app_eval, runtime-error]
prevention_rule: "Filter native apphost/bundle PE assemblies without managed metadata before adding MetadataReference in AppEvalHandler, and query Postgres directly via Npgsql/psql when kongor_app_eval fails with CS0009."
---

# MCP-250: CS0009 Kongor.Cli.dll PE image lacks managed metadata in shadow runtime

## Summary
Evaluating an ad-hoc C# expression via `kongor` / `kongor_app_eval` failed during Roslyn script compilation with `error CS0009: Metadata file '.../Kongor.Cli.dll' could not be opened -- PE image doesn't contain managed metadata` when running from a shadow-copied MCP host directory containing native `.dll` / apphost PE binaries alongside managed assemblies. Tracked as `open` with a `PEReader.HasMetadata` pre-filter remediation for `AppEvalHandler`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_app_eval` |
| **Expected** | Compile and evaluate the C# diagnostic snippet against loaded managed assemblies and return the serialized result JSON. |
| **Actual (Symptom)** | Fails during compilation with `CS0009: Metadata file '.../Kongor.Cli.dll' could not be opened -- PE image doesn't contain managed metadata`. |
| **Root Cause** | `AppEvalHandler` (`src/tools/kongor-cli/Mcp/Handlers/AppEvalHandler.cs`) enumerates `.dll` files in `AppContext.BaseDirectory` / loaded `Assembly` locations and passes them to `MetadataReference.CreateFromFile(...)` without first inspecting the PE header via `System.Reflection.PortableExecutable.PEReader.HasMetadata`, causing Roslyn to abort compilation whenever a native or single-file bundle stub `.dll` is encountered in the shadow runtime folder. |
| **Fix / Remediation** | 1. Guard every candidate reference path in `AppEvalHandler` with `using var pe = new PEReader(File.OpenRead(path)); if (!pe.HasMetadata) continue;` before calling `MetadataReference.CreateFromFile(path)`.<br>2. When querying database state while `CS0009` is present, use `kongor_db_fixture` or `psql` directly instead of `kongor_app_eval`. |

## Invocation Parameters & Raw Logs
```json
{
  "code": "return Environment.ProcessId;"
}
```
```text
CompilationErrorException: (1,1): error CS0009: Metadata file 'bin/mcp-shadow/Kongor.Cli.dll' could not be opened -- PE image doesn't contain managed metadata.
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_app_eval
```

## Prevention Invariant
Filter native apphost/bundle PE assemblies without managed metadata (`PEReader.HasMetadata`) before adding `MetadataReference` in `AppEvalHandler`, and query Postgres directly via `kongor_db_fixture` / `psql` when `kongor_app_eval` fails with `CS0009`.
