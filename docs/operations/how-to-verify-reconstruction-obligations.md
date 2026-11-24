# How to Verify Reverse-Engineered Functions with the Reconstruction Obligation Ledger

This guide explains how to reverse engineer native K2 engine functions or network packets without introducing regressions or edge-case bugs, using Project Kongor's double-entry Evidence Dossier protocol (adapted from the REA architecture).

---

## 1. When to Use This Workflow
Follow this protocol whenever:
* You are reverse-engineering a native K2 function in `k2_x64.dll`, `cgame_x64.dll`, or `game_x64.dll`.
* You are implementing a new network packet dispatcher or parser.
* You are writing a proxy DLL hook (via Proximo) that intercepts engine vtables.
* You are diagnosing a native crash dump (`.dmp`) and patching client logic.

---

## 2. Step 1: Decompile & Extract Evidence (Ghidra MCP)

Query the Ghidra MCP server to decompile the target function by RVA or symbol:
```json
{
  "ServerName": "ghidra",
  "ToolName": "decompile_function",
  "Arguments": {
    "binary_name": "game_x64.dll",
    "name_or_address": "0x35d40"
  }
}
```

Inspect the decompiled C for:
* Parameters, calling conventions, and vtable index.
* Preconditions (null pointer checks, buffer size checks).
* Boundary conditions (index clamps, max clients).
* Bitmask checks and state flags.
* Indirect calls (`call rax`) and opaque struct offsets (`*(rcx + 0x88)`).

---

## 3. Step 2: Create Evidence Dossier v2

Copy the template from [`artifacts/evidence/_template.json`](../../artifacts/evidence/_template.json) into `artifacts/evidence/<category>/<dossier_id>.json`:

1. Fill out `target`, `interface`, and `decompiled_c`.
2. Populate the **Obligation Ledger (`obligations`)**:
   ```json
   "obligations": [
     {
       "id": "OBL-01",
       "description": "Reject packet buffer if size < 4 bytes (return false)",
       "condition_type": "null_check",
       "evidence_rva": "0x35d40",
       "status": "pending",
       "satisfied_in": "src/services/master-server/PacketDispatcher.cs",
       "verified_by_test": "tests/unit/services/test_packet_dispatcher.py",
       "test_method": "test_buffer_too_small"
     }
   ]
   ```
3. Populate **Limitations & Unknowns (`limitations_and_unknowns`)**:
   * Any struct offset not fully understood must be listed.
   * If an offset is not needed for your fix, set `"status": "waived"` with a required `"rationale"`.
   * Never blindly guess the meaning of an offset.

---

## 4. Step 3: Dynamic Triangulation (Frida / WinDbg)

If the function contains unknown offsets, virtual dispatch, or unconfirmed branches, verify dynamically on a running `hon_x64.exe`:

1. Open [`scripts/re/triangulate_rva.js`](../../scripts/re/triangulate_rva.js).
2. Configure `targetModule`, `rva`, and `inspectOffsets`.
3. Attach via Frida CLI or Frida MCP:
   ```powershell
   frida -n hon_x64.exe -l scripts/re/triangulate_rva.js
   ```
4. Trigger the in-game action (cast ability, kill hero, send packet).
5. Compare observed register values with Ghidra decompilation. If runtime behavior contradicts static code, update the dossier with the live facts.

Once confirmed, set `"triangulation_status": "dynamically_confirmed"`.

---

## 5. Step 4: Implement & Tag Cleanroom Code

When authoring your cleanroom implementation, patch, or Lua script, explicitly tag each satisfied obligation with a comment matching the ID:

```csharp
// satisfies: OBL-01
if (packetBuffer.Length < 4) {
    return false;
}
```

In your unit or integration test:
```python
def test_buffer_too_small():
    # satisfies: OBL-01
    assert parse_packet(b"\x00") is False
```

Update the obligation in the dossier to `"status": "satisfied"`.

---

## 6. Step 5: Automated Audit Gate

Run the verification script to audit all obligations:

```powershell
# Audit a specific dossier
powershell -ExecutionPolicy Bypass -File scripts\verify_evidence_obligations.ps1 -Dossier artifacts/evidence/packets/game_x64_0x35d40_packet_parser.json

# Audit all modified files
powershell -ExecutionPolicy Bypass -File scripts\verify_evidence_obligations.ps1 -Files "src/services/master-server/PacketDispatcher.cs"

# Audit everything in artifacts/evidence/
powershell -ExecutionPolicy Bypass -File scripts\verify_evidence_obligations.ps1 -All
```

### Pre-Injection Integration
When running `scripts/vfs/inject_override_to_s2z.ps1`, the obligation check runs automatically for any modified files that map to an active dossier. In an emergency hotfix, bypass with:
```powershell
.\scripts\vfs\inject_override_to_s2z.ps1 -RelativePath "ui/fe2/main_v2.interface" -SkipObligationCheck
```
