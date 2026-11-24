# 🧪 Lua Verifier (`LuaVerifier`)

> **Headless NLua sandbox mocking K2 UI widgets, Cvars, `UIManager`, and PHP/Base64 serialization (`net11.0`).**

`LuaVerifier` validates client Lua UI scripts offline without launching `hon_x64.exe`, catching undefined globals (`Windows` vs `UIManager`), `DoEvent()` misuse, PHP serialization issues, and Lua syntax/runtime errors during CI and `scripts/gate_check.ps1`.

---

## ⚡ Quick Start

```powershell
# Via unified Kongor CLI
.\kongor.ps1 lua-verify

# Or build & invoke directly
dotnet build src/tools/lua-verifier/LuaVerifier.csproj -c Release
dotnet run --project src/tools/lua-verifier/LuaVerifier.csproj -- <script-or-args>
```

---

## 🏗️ Architecture

- **`K2Environment.cs`:** Provides cleanroom K2 engine globals (`UIManager`, `Host`, `Cvar`, `UITrigger`, `GetWidget`) and Base64/PHP serialization helpers.
- **`Program.cs` (`MockWidget`):** Emulates K2 widget properties and methods (`SetGlow`, `SetFont`, `SetColor`, `DoEvent`, `DoEventN`) inside an NLua VM.
- **Gate Integration:** Built and exercised by `.github/workflows/build-and-test.yml` and `scripts/test_gate_mutants.ps1`.
