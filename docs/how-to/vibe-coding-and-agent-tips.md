# 🤖 Tips for Vibe Coders & AI-Assisted Development

A practical, straight-to-the-point field guide for developers pair-programming with AI agents on Project Kongor. *(For full model acknowledgments and an educational case study on how Gemini, Claude, Junie, and Codex were paired on this project, see [`AI Gratitude & Educational Notes`](../ai-gratitude-and-methodology.md).)*

> [!TIP]
> **The 80 / 20 Rule:** Spend **80% of your effort planning** and **20% executing**. Plan, plan, and plan some more—ten minutes of solid upfront alignment saves hours of untangling agent-generated code.

---

## 🧠 1. Planning, Models & Managing Conversations

- **Split the workflow by model strength:**
  1. **Plan** architecture, edge cases, and tricky systems with **larger thinking models**.
  2. **Code, test, and verify** with fast **Flash models** *(for tougher implementations, bring a larger model back in to review)*.
  3. **Control the client** through the [`kongor` (TAO) MCP server](../tao-mcp/overview.md) using **Flash models** to capture screenshot and video proof quickly.
     - *Want to keep using your PC while agents test?* Have TAO run **offscreen**—it can drive the client, inspect UI, and capture visual proof without taking up monitor real estate or stealing focus.
- **Have multiple models work together:** Don't rely on a single model. Every model has its own strengths and blind spots, and pairing them catches subtle issues one model alone would miss.
- **Not sure what an agent is doing? Ask a Flash model to ELI5:** Anytime an agent's plan or diff looks confusing, ask a fast Flash model to explain *what* it is trying to do and *why* in plain English before letting it proceed.
- **Keep your pinned conversations organized:** Only pin conversations that are **actively working or pending**. Stack them in priority order—currently running items at the top, with each next step lined up underneath—and unpin anything finished.
- **Always maintain [`ACTIVE_SESSION_STATE.md`](../ACTIVE_SESSION_STATE.md):** Agents lose context across long sessions and chat resets. Keeping active goals, working hypotheses, and ruled-out dead ends in [`docs/ACTIVE_SESSION_STATE.md`](../ACTIVE_SESSION_STATE.md) lets any agent pick up right where the last one left off.

---

## 📚 2. Shared Project Memory & Agent Locking

- **Use Field Notes (`FN`), MCP-Issues (`MCP`), and Self-Healing Journeys (`SHJ`)—and BUMP them!**
  - **Field Notes (`FN-NNN`):** [`docs/reference/field-notes/`](../reference/field-notes/README.md) via [`scripts/kongor_kb.ps1`](../../scripts/kongor_kb.ps1)
  - **MCP-Issues (`MCP-NNN`):** [`docs/reference/mcp-issues/`](../reference/mcp-issues/README.md) via [`scripts/kongor_mcp_issues.ps1`](../../scripts/kongor_mcp_issues.ps1)
  - **Self-Healing Journeys (`SHJ-NNN`):** [`docs/reference/self-healing-journeys/`](../reference/self-healing-journeys/README.md) via [`scripts/qa/kongor_shj.ps1`](../../scripts/qa/kongor_shj.ps1)
  - Have agents search these first so they don't repeat old mistakes, and **bump** existing entries (or record new ones) whenever an engine quirk or tool issue crops up.
- **Make worker agents LOCK the client and LOCK tests so they never collide:**
  - Lock live game client access via [`scripts/kongor_worker_lock.ps1`](../../scripts/kongor_worker_lock.ps1) so two agents never fight over `hon_x64.exe` or IPC pipes.
  - Lock build and test runs via [`scripts/Invoke-KongorTestLock.ps1`](../../scripts/Invoke-KongorTestLock.ps1) so parallel agents never clobber MSBuild `obj/`/`bin/` folders or shared test state.

---

## 🔬 3. Debugging, Telemetry & Power Tools

Lean on our MCP servers and telemetry logs to handle the heavy lifting of debugging instead of letting agents guess:

- **Core Debugging Stack:**
  - **`everything` MCP:** Instantly locate the newest `.dmp` crash dumps and `.log` files in milliseconds (never wait on slow recursive folder scans).
  - **`windbg` + `ghidra` MCPs:** Inspect crash minidumps and decompile exact engine RVAs *(pair with headless **IDA** when you want a second opinion on tricky decompiler output)*.
  - **`frida` + `kongor` (TAO) MCPs:** Hook native functions, inspect live state, hot-reload UI, and poke the running game client.
  - **Backend Logs:** **OpenTelemetry / Serilog / .NET Aspire** logs and traces for backend microservices (`MasterServer`, `ChatServer`, `COMPEL`, `WILLOWMAKER`).
  - **Game Client Logs:** `console.log`, `client_lua_debug.log`, and `hon_x64_d3d9.log` (DXVK/Vulkan), filtered with [`scripts/triage_client_logs.ps1`](../../scripts/triage_client_logs.ps1).
- **For Advanced Devs:**
  - Also lean on **`idalib`** (headless IDA Pro), **`wireshark`** (packet/netcode inspection), **`texconv`** (DDS/RxGB texture debugging), **`kongor-model-viewer`** (standalone 3D model/animation inspection), **`proximo`** (DLL export/proxy analysis), **`clangd`** (native C++ navigation), and **`LuaVerifier`** (`luac51` offline syntax checks via [`scripts/verify_client_lua.ps1`](../../scripts/verify_client_lua.ps1)).

---

## 🛡️ 4. Safe Shutdown, Tests & Pre-Commit Gates

- **Continuously write tests and lean on experienced devs (your bowling bumpers):** Building test cases as you go—and asking experienced devs on the project for guidance—is the equivalent of putting bumpers on your bowling lane. Without tests and direction, agents are way more likely to throw gutter balls.
- **Stop the client cleanly with [`Stop-KongorGameCleanly.ps1`](../../scripts/lib/Stop-KongorGameCleanly.ps1):** Always use [`scripts/lib/Stop-KongorGameCleanly.ps1`](../../scripts/lib/Stop-KongorGameCleanly.ps1) when closing `hon_x64.exe` during testing rather than force-killing it, avoiding unkillable background zombie processes holding `.s2z` file locks.
- **Finish with [`gate_check.ps1`](../../scripts/gate_check.ps1) before you commit:** Always run [`scripts/gate_check.ps1`](../../scripts/gate_check.ps1) at the end of a task to verify Lua bytecode, XML schemas, KB indexes, and cross-tree asset parity in under 5 seconds before committing.
