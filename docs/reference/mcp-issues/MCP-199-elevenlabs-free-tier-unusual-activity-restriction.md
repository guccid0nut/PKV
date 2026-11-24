---
id: MCP-199
server: elevenlabs
tool: creative_get_flow_run_status
category: auth-config
title: "ELEVENLABS-FREE-TIER-UNUSUAL-ACTIVITY-RESTRICTION"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-14
last_seen: 2026-10-14
tags: [elevenlabs, creative_get_flow_run_status, auth-config]
prevention_rule: "Ensure paid subscription tier is active on the connected ElevenLabs workspace before batch speech synthesis, and verify status via `creative_get_flow_run_status`."
---

# MCP-199: ELEVENLABS-FREE-TIER-UNUSUAL-ACTIVITY-RESTRICTION

## Summary
Tracked MCP issue on `elevenlabs` / `creative_get_flow_run_status` (category: `auth-config`, status: `resolved`, occurrences: 1). Synthesize speech generation `WtQCP7JrD27FqdNGs9t4` using `eleven_turbo_v2_5`. -> Returned `status: "failed"` with `error_message: "Unusual activity has been detected on your account, so Free Tier access has been disabled. Please upgrade to a paid subscription to continue."`

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `elevenlabs` / `creative_get_flow_run_status` |
| **Expected** | Synthesize speech generation `WtQCP7JrD27FqdNGs9t4` using `eleven_turbo_v2_5`. |
| **Actual (Symptom)** | Returned `status: "failed"` with `error_message: "Unusual activity has been detected on your account, so Free Tier access has been disabled. Please upgrade to a paid subscription to continue."` |
| **Root Cause** | ElevenLabs API blocked Free Tier synthesis due to VPN/proxy detection before the account was upgraded to a paid tier. |
| **Fix / Remediation** | Ensure paid subscription tier is active on the connected ElevenLabs workspace before batch speech synthesis, and verify status via `creative_get_flow_run_status`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"flow_id": "KnngqtzjFNVsxxNX5hgp", "session_ids": ["MphyW3cD4MC7HoNfUHxT"]}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server elevenlabs -Tool creative_get_flow_run_status
```

## Prevention Invariant
Ensure paid subscription tier is active on the connected ElevenLabs workspace before batch speech synthesis, and verify status via `creative_get_flow_run_status`.
