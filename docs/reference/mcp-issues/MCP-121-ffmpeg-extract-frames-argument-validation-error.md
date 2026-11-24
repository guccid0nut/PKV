---
id: MCP-121
server: ffmpeg
tool: ffmpeg_extract_frames
category: parameter-mismatch
title: "FFMPEG-EXTRACT-FRAMES-ARGUMENT-VALIDATION-ERROR"
status: documented
superseded_by: ""
supersedes: []
occurrences: 1
date: 2026-10-11
last_seen: 2026-10-11
tags: [ffmpeg, ffmpeg_extract_frames, parameter-mismatch]
prevention_rule: "Always check schema file (`mcp/ffmpeg/ffmpeg_extract_frames.json`) and pass `output_dir` and `filename_prefix`."
---

# MCP-121: FFMPEG-EXTRACT-FRAMES-ARGUMENT-VALIDATION-ERROR

## Summary
Tracked MCP issue on `ffmpeg` / `ffmpeg_extract_frames` (category: `parameter-mismatch`, status: `resolved`, occurrences: 1). Frame extraction with custom filename pattern. -> Validation error: `output_dir` Field required.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `ffmpeg` / `ffmpeg_extract_frames` |
| **Expected** | Frame extraction with custom filename pattern. |
| **Actual (Symptom)** | Validation error: `output_dir` Field required. |
| **Root Cause** | `ffmpeg_extract_frames` requires `output_dir` (directory path) and optional `filename_prefix`, not `output_pattern`. |
| **Fix / Remediation** | Always check schema file (`mcp/ffmpeg/ffmpeg_extract_frames.json`) and pass `output_dir` and `filename_prefix`. |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"fps": 30, "input_path": "...", "output_pattern": "...", "start_time": 1, "vframes": 15}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server ffmpeg -Tool ffmpeg_extract_frames
```

## Prevention Invariant
Always check schema file (`mcp/ffmpeg/ffmpeg_extract_frames.json`) and pass `output_dir` and `filename_prefix`.
