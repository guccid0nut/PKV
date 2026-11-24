# 🖼️ Kongor Headless UI Renderer (`Kongor.HeadlessRenderer`)

> **Isolated offscreen Avalonia UI renderer for automated visual verification and TAO MCP inspection (`net11.0`).**

`Kongor.HeadlessRenderer` renders Avalonia views offscreen at arbitrary resolutions and themes (`dark` / `light`) with deterministic mock data, keeping heavy UI rendering assemblies decoupled from the core `kongor` CLI binary.

---

## ⚡ Quick Start

```powershell
dotnet run --project src/tools/headless-renderer/Kongor.HeadlessRenderer.csproj -- --help
dotnet run --project src/tools/headless-renderer/Kongor.HeadlessRenderer.csproj -- --view Dashboard --width 1280 --height 720 --theme dark --output artifacts/captures/dashboard.png
```

### CLI Options
- `--view <ViewName>` *(required)* — Target view to render offscreen
- `--width <px>` / `--height <px>` — Output canvas dimensions (default: `1280x720`)
- `--theme <dark|light>` — Avalonia theme variant (default: `dark`)
- `--output <path>` — Optional PNG destination path (prints structured JSON result to stdout)
