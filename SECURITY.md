# Security Policy

## Supported Versions

**Project KONGOR V (`PKV`)** tracks security fixes on the active `main` branch.

| Version / Branch | Supported |
| :--- | :---: |
| `main` (`v5.x`) | ✅ Yes |
| Legacy snapshots (`< v5.0`) | ❌ No |

---

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for unpatched security vulnerabilities affecting authentication (`SRP-6a` / OAuth / session tokens), remote packet parsing (`KONGOR.MasterServer`, `TRANSMUTANSTEIN.ChatServer`, `COMPEL` UDP proxy), native memory safety (`native/`), or container/cluster isolation.

### Preferred Disclosure Channel
1. Use **GitHub Private Vulnerability Reporting** (`Security` tab → **Report a vulnerability**) on this repository so maintainers can triage and patch the issue privately before public disclosure.
2. Include:
   - Affected component/file path (e.g., `src/services/chat-server/...` or `native/proxies/native_guard/...`).
   - Minimal reproduction steps or unit-test proof-of-concept.
   - Potential impact (crash/DoS, auth bypass, privilege escalation, SSRF, or memory corruption).

### Response Targets
- **Initial acknowledgment:** Targeted within **one week**
- **Triage & remediation plan:** **Best-effort**, prioritized by severity
- **Coordinated advisory & credit:** Published alongside the regression test and patch commit once verified.

---

## Built-In Repository Security Gates

Every export and CI run enforces automated security invariants:
- **Zero Committed Secrets:** Enforced via [`.gitleaks.toml`](.gitleaks.toml) and [`scripts/lib/Test-KongorSecretScrub.ps1`](scripts/lib/Test-KongorSecretScrub.ps1) (scanning connection strings, bearer/API tokens, private keys, and workstation PII).
- **Dependency Vulnerability Gate (SCA):** `./kongor.ps1 scan` emits SARIF 2.1.0 and blocks unmitigated High/Critical NuGet advisories in CI.
- **Static Analysis (SAST):** GitHub CodeQL (`security-extended`) runs across C# projects alongside Roslyn banned-symbol checks ([`BannedSymbols.txt`](BannedSymbols.txt)).
