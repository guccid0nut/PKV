# 💉 Frida Dynamic Binary Instrumentation (DBI) & MCP Integration

> **Category:** `Developer Tool` | **Location:** `src/tools/frida` | **Pairing:** `Dynamic Tracing + Ghidra Evidence`

---

## 📋 Overview

While the **Ghidra MCP Server** provides static analysis, decompilation, and xref discovery, **Frida DBI** provides live, zero-recompile runtime tracing into `hon_x64.exe` and K2 engine DLLs.

By injecting JavaScript hooks into active processes, developers and AI agents can:
1. Intercept `__fastcall` parameters in `RCX`, `RDX`, `R8`, `R9` on live game instances.
2. Read heap structures and C++ Virtual Method Tables (`vtable_sniffer.js`).
3. Audit real-time UDP packet frames (`trace_packets.js`) without compiling C++ proxy DLLs.
4. Populate `signature_mask`, `vtable_index`, and parameter types in **Evidence Dossiers**.

---

## 🛡️ Thread Safety & Windows x64 Guardrails

Heroes of Newerth (`hon_x64.exe`) runs multiple concurrent high-frequency threads:
- **DirectX 9 / Vulkan Swapchain Render Thread** (~60–240 Hz)
- **Physics Simulation & Game Tick Thread** (~20–60 Hz)
- **UDP Socket Network Thread** (Event-driven)

### Critical Stability Rules:
> [!WARNING]
> **Never attach unthrottled console logging to tight render or tick loops.**
> Excessive JavaScript-to-native IPC latency inside the swapchain or tick loops will stall the Windows multimedia timer, desync UDP heartbeats, or trigger `STATUS_ACCESS_VIOLATION` (`0xC0000005`).

1. **Burst Caps**: All hook scripts must define `MAX_BURST_LOGS` (e.g. 100 entries) or a time throttle.
2. **Safe Pointer Inspection**: Always verify memory range readability (`Process.findRangeByAddress()`) before calling `.readUtf8String()` or reading struct offsets.
3. **Target Event Handlers**: Prefer hooking discrete event dispatchers (e.g. `FUN_180035d40` packet parser, `IEntityAbility::LevelUp`) rather than per-frame matrix transforms.

---

## 🛠️ MCP Integration (`frida-mcp`)

To enable AI agents to dynamically instrument the game engine, register `frida-mcp` in your local MCP configuration:

```json
{
  "mcpServers": {
    "frida": {
      "command": "npx",
      "args": ["-y", "frida-mcp"]
    }
  }
}
```

### Pre-Baked Script Library:
- [`scripts/trace_fastcall.js`](scripts/trace_fastcall.js): General `__fastcall` x64 parameter and string pointer tracer.
- [`scripts/trace_packets.js`](scripts/trace_packets.js): Live game packet opcode and buffer sniffer for `game_x64.dll`.
- [`scripts/vtable_sniffer.js`](scripts/vtable_sniffer.js): Traverses object vtables and resolves slot function pointers to module names and RVAs.
