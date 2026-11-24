# Rule: Testing, Hot-Reload, Visual Proofs & Quality Gates

- **Override & Hot-Reload First:** Author client changes in `games/client/override/` (tracked in git; never auto-injected/evicted by commit/push). Verify VFS precedence (`kongor_vfs_resolve`), lint (`scripts/verify_client_lua.ps1`, `scripts/verify_xml_syntax.ps1`), hot-reload (`ReloadInterfaces`), and observe a 30-60s canary log window (`console.log`, `client_lua_debug.log`).
- **Live Triangulation:** Combine **Frida** (`hon_x64.exe`), **Ghidra** (`ProjectKongor.rep`), **Everything** (`ext:log|ext:dmp`), and **WinDbg** (`!analyze -v`, `.exr -1`, `kn`, `r`, `lm vm`). Never guess native vtables/offsets statically.
- **Real Proof or No Proof:** Assert active engine phase & target widget visibility before `kongor_capture_window`; reject login/blank captures; validate via `scripts/verify_proof_screenshot.py`; copy proofs to the conversation artifact directory.
- **Frame Budget & Quality Gates:** Keep Lua UI handlers `<= 1.0 ms`. Verify `scripts/gate_check.ps1` (6/6 PASS `<= 5.0s`), `scripts/re/test_metagen_parity.ps1`, `scripts/verify_evidence_obligations.ps1 -All -Strict`, and `scripts/qa/verify_north_star_slos.ps1`.
- **Value-Weighted Tests:** Every test must guard a real contract (`protects`), fail on a real defect (`fails_when`), and exercise a real boundary (`seam`).

Deep reference: `docs/agent-playbooks/testing_and_runtime_verification_full.md`
