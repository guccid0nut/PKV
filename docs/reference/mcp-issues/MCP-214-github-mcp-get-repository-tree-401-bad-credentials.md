---
id: MCP-214
server: github
tool: get_repository_tree
category: auth-config
title: "GITHUB-MCP-GET-REPOSITORY-TREE-401-BAD-CREDENTIALS"
status: codified
codified_in: ".agents/rules/mcp_issues.md"
verified_by: "scripts/kongor_mcp_issues.ps1"
superseded_by: ""
supersedes: []
occurrences: 4
date: 2026-10-14
last_seen: 2026-10-20
tags: [github, get_repository_tree, auth-config]
prevention_rule: "For public GitHub repositories, bypass the authenticated `github` MCP server when token credentials return 401 and query unauthenticated endpoints directly via `read_url_content` (`https://api.github.com/repos/<owner>/<repo>/git/trees/main?..."
---

# MCP-214: GITHUB-MCP-GET-REPOSITORY-TREE-401-BAD-CREDENTIALS

## Summary
Tracked MCP issue on `github` / `get_repository_tree` (category: `auth-config`, status: `documented`, occurrences: 4). Return the repository file tree for `rehan-remade/universal-modder`. -> Returned `failed to get repository info: GET https://api.github.com/repos/rehan-remade/universal-modder: 401 Bad credentials []`.

## Expected -> Actual -> Root Cause -> Fix

| Stage | Details |
| :--- | :--- |
| **MCP Server & Tool** | `github` / `get_repository_tree` |
| **Expected** | Return the repository file tree for `rehan-remade/universal-modder`. |
| **Actual (Symptom)** | Returned `failed to get repository info: GET https://api.github.com/repos/rehan-remade/universal-modder: 401 Bad credentials []`. |
| **Root Cause** | The configured GitHub Personal Access Token in the `github` MCP server environment is expired or invalid, causing authenticated REST API calls via the `github` MCP server to fail with HTTP 401 even on public repositories. |
| **Fix / Remediation** | For public GitHub repositories, bypass the authenticated `github` MCP server when token credentials return 401 and query unauthenticated endpoints directly via `read_url_content` (`https://api.github.com/repos/<owner>/<repo>/git/trees/main?recursive=1` and `https://raw.githubusercontent.com/<owner>/<repo>/main/<path>`). |

## Invocation Parameters & Raw Logs
```text
Parameters:
`{"owner": "rehan-remade", "recursive": true, "repo": "universal-modder"}`
```

## Verification / Pre-Flight Oracle
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/kongor_mcp_issues.ps1 -Action search -Server github -Tool get_repository_tree
```

## Prevention Invariant
For public GitHub repositories, bypass the authenticated `github` MCP server when token credentials return 401 and query unauthenticated endpoints directly via `read_url_content` (`https://api.github.com/repos/<owner>/<repo>/git/trees/main?...
