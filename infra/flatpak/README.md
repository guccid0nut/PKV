# 📦 Project KONGOR Flatpak (WILLOWMAKER)

This directory provides the official **Flatpak** packaging specification for **Project KONGOR** and the **WILLOWMAKER** client runtime on Linux and **Steam Deck (SteamOS)**.

---

## 🌟 Why Flatpak?

Modern Linux distributions (Ubuntu 24.04+, Fedora 40+, Arch, SteamOS) have deprecated or removed legacy libraries required by the native Heroes of Newerth K2 client (`hon-x86_64`).

This Flatpak provides a deterministic sandbox based on `org.freedesktop.Platform 24.08` with:
- **`libudev.so.0` compatibility symlink** (resolves missing device manager library).
- **`GConf-3.2.6` & `dbus-glib`** (legacy GNOME desktop settings & sound subsystem).
- **`gtk+-2.24.32`** (GTK2 runtime for the native HoN engine).
- **`gtk3`** (GTK3 runtime for the Avalonia .NET WILLOWMAKER launcher).
- **`libGLU` & graphics stack** (`freetype`, `pango`, `harfbuzz`).
- **XWayland & X11 fallback** with automated `gl_modesetting ""` configuration to prevent modern display compositor crashes.

---

## 🚀 Building the Flatpak

### Prerequisites (on Linux)
Ensure `flatpak` and `flatpak-builder` are installed:
```bash
# Ubuntu / Debian
sudo apt install flatpak flatpak-builder

# Fedora
sudo dnf install flatpak flatpak-builder

# Arch Linux
sudo pacman -S flatpak flatpak-builder
```

Install the freedesktop 24.08 SDK and Platform runtimes:
```bash
flatpak install flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08
```

### Build & Local Install
```bash
cd infra/flatpak
make install
```

### Build Single-File Bundle (`.flatpak`)
To distribute a standalone bundle for offline use or transfer to a Steam Deck:
```bash
make bundle
# Produces net.kongor.Willowmaker.flatpak
```

---

## 🎮 Steam Deck Setup (SteamOS)

1. Switch to **Desktop Mode** on your Steam Deck.
2. Copy `net.kongor.Willowmaker.flatpak` to your Steam Deck.
3. Open a terminal or double-click to install via Discover / Flatpak:
   ```bash
   flatpak install --user net.kongor.Willowmaker.flatpak
   ```
4. Open the Steam Desktop client, click **Games -> Add a Non-Steam Game to My Library**, select **Project KONGOR (WILLOWMAKER)**, and return to Gaming Mode!

---

## 📜 Acknowledgments
This Flatpak was inspired by **manu311**.

