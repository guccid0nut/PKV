# 💬 COMPEL Chat (`COMPEL.GUI`)

> **Desktop host console & live chat/command interface for COMPEL dedicated game server fleets (`net11.0`, Avalonia 11.2.3).**

`COMPEL.GUI` provides a standalone graphical host console for connecting to a running `COMPEL` control plane (`Host`, `ControlPort`, optional bearer `AuthToken`), inspecting active dedicated match worker instances, streaming live server chat/console output, and dispatching console commands.

---

## ⚡ Quick Start

```powershell
# Run directly via dotnet CLI
dotnet run --project src/apps/compel-gui/COMPEL.GUI.csproj
```

---

## 🏗️ Architecture

- **Framework:** Avalonia `11.2.3` + `CommunityToolkit.Mvvm` (`net11.0`, cross-platform X11 & Win32 AngleEgl/Software fallback)
- **Shared Fleet & Secrets Contracts:** Links `ISecretsManagerService`, `IGameServerFleetService`, `CompelChatViewModel`, and fleet DTOs from `src/apps/control-panel/` alongside `Kongor.Core` and `NEXUS.Contracts`
- **Primary Views & ViewModels:**
  - `Views/MainWindow.axaml` — Connection bar (`Host`, `Port`, `Token`), worker instance selector, live chat/log stream, and command input
  - `ViewModels/CompelChatViewModel.cs` — Async control-plane polling, connection state indicator, and command dispatch

---

## 📖 Related Documentation

- [COMPEL Server Manager](../compel/README.md)
- [NEXUS Control Panel GUI](../control-panel/README.md)
