# ⚡ Prebake Tool (`prebake-tool`)

> **Entity XML harvester and MemoryPack + Zstandard binary cache generator (`net11.0`).**

`prebake-tool` parses HoN entity definitions and Lua caches from client `.s2z` archives or loose files, compiling deterministic `entity_cache.bin` (MemoryPack + Zstd) and `hero_definitions_cache.lua` bundles for fast client UI and server startup.

---

## ⚡ Quick Start

```powershell
# Via unified Kongor CLI
.\kongor.ps1 prebake all --dry-run

# Or run standalone
dotnet run --project src/tools/prebake-tool/prebake-tool.csproj -- all -g games/client -o artifacts/prebake_scratch --dry-run
```

### Key Options & Subcommands
- Subcommands: `all`, `entities`, `lua`, `bundle`
- `-g|--game-dir <DIR>` — Source client directory (default: `games/client`)
- `-s|--shared-dir <DIR>` — Optional shared entities directory
- `-o|--output-dir <DIR>` — Output directory (use an isolated scratch directory during testing!)
- `-c|--compression-level <1-22>` — Zstandard compression level (default: `3`)
- `-m|--target-mode <vfs|s2z|both>` — Deployment mode (default: `both`)
- `--dry-run` — Simulate execution without writing files
