# Rule: Pragmatic Minimalism & Heavy Telemetry ("Kongor-Tail")

- **Decision Ladder:** YAGNI -> reuse existing Kongor/K2 code -> native/installed primitives -> smallest complete change -> root-cause fix (no speculative wrappers).
- **Heavy Telemetry > Code Golf:** Log every state transition, packet handler, fallback branch, and tool call verbosely.
- **PowerShell & Everything MCP:** Use `Select-String`, `Copy-Item`, `Get-ChildItem`; locate logs/dumps via `everything_search` (`sort: "date-modified-desc"`).
- **User Sovereignty & IDE-Native Edits:** Ask permission before altering user direction or running batch-edit scripts; use IDE native file edit tools.
- **Actionable Errors & Completeness:** Every failure states what failed and the exact fix command; implement full edge cases, null guards, and tests immediately.
- **Valid Links & Privacy:** Repo-relative links in committed docs (zero PII/secrets); full `file:///c:/...` URIs in chat; embed copied proof images in artifacts.

Deep reference: `docs/agent-playbooks/engineering_philosophy_full.md`
