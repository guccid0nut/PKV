---
id: MCP-284
server: kongor
tool: kongor_assert_visual
category: verification
title: "assert_visual passes unrelated images and visual_diff uses synthetic inputs"
status: resolved
verified_by: "f3c7add47 + McpToolContractTests.Tranche2 + live NetGuard stdio audit"
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-11-17
last_seen: 2026-11-17
tags: [kongor, kongor_assert_visual, verification]
prevention_rule: "Visual assertions must fail when diff percentage exceeds the threshold and must never substitute synthetic baselines or candidates."
---

# MCP-284: assert_visual passes unrelated images and visual_diff uses synthetic inputs

## Summary
`kongor_assert_visual` passed a comparison between the live main menu and an unrelated image, and `kongor_ui_visual_diff` ignored a caller baseline path, substituted a synthetic candidate, and wrote artifacts into the build output folder. Found in the 2026-11-17 live audit. Open; no fix applied.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `kongor` / `kongor_assert_visual` |
| **Expected** | A visual assertion fails when the images differ; golden capture writes the live client frame to the requested path; a diff without a candidate is an error. |
| **Actual (Symptom)** | `kongor_assert_visual` against an Avalonia dashboard render returned `match: true`, `ssim: 0.9647`, `diffPercentage: 99.84`, threshold `tolerant_3d_0.92` (schema default 0.95). `kongor_ui_visual_diff capture_golden` with `baselinePath` returned five viewer goldens under `tests/golden/viewers` and wrote nothing to the path. `diff` without `candidatePath` used `synthetic://k2_hud_candidate` and returned success. `assert` wrote its diff image under `src/tools/kongor-cli/bin/Debug/net11.0/artifacts/visual_diffs/`. The same two images compared with `kongor_ui_visual_diff assert` correctly failed with `ssim: 0.0429`. |
| **Root Cause** | Not traced in source. The two tools compute different SSIM values for the same image pair, and `kongor_assert_visual` does not gate on `diffPercentage`. |
| **Fix / Remediation** | Not fixed. Until fixed, use `kongor_ui_visual_diff` with explicit `baselinePath` and `candidatePath`, and inspect the images. |

## Invocation Parameters & Raw Logs
```text
Evidence (local, git-ignored): artifacts/audits/tao-mcp-live-audit-2026-11-17/run2_results.jsonl
row 241 kongor_assert_visual {fixture: dashboard.png}            -> match true, ssim 0.9647, diffPercentage 99.84
row 237 kongor_ui_visual_diff {assert, same pair}                -> fail, ssim 0.0429
row 234 kongor_ui_visual_diff {capture_golden, baselinePath}     -> tests/golden/viewers list, nothing written
row 238 kongor_ui_visual_diff {diff, no candidatePath}           -> candidate synthetic://k2_hud_candidate
```

## Verification / Pre-Flight Oracle
Exact command or pre-flight check that prevents or verifies this issue:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_assert_visual
```

## Prevention Invariant
Visual assertions must fail when diff percentage exceeds the threshold and must never substitute synthetic baselines or candidates.
