# Heroes of Newerth 4.10.1 - Vulkan 1.3 (DXVK) Installer Pack

This folder contains standalone, non-invasive scripts to enable Vulkan 1.3 rendering via DXVK for vanilla Heroes of Newerth 4.10.1 clients launched via WILLOWMAKER.

## Core Principles
- **100% Non-Invasive**: Zero original game binaries or DLLs (`k2_x64.dll`, `vid_d3d9_x64.dll`, `hon_x64.exe`, etc.) are modified.
- **Zero System Changes**: Does NOT modify `hosts`, the Windows Registry, or `System32`.
- **Standard User Context**: No Administrator/UAC elevation required.
- **Instant Rollback**: Deleting `d3d9.dll`, `hon_x64.exe.local`, and `dxvk.conf` completely restores stock Direct3D 9.

---

## Files in this Folder

| File | Platform | Usage |
| :--- | :--- | :--- |
| **`Install-Vulkan.bat`** | Windows 10/11 | Single-file hybrid Batch + PowerShell installer. Share this single file with Windows players. |
| **`install-vulkan.sh`** | Linux / Steam Deck / macOS | POSIX Bash script for Wine, Proton, Lutris, and CrossOver players. |
| **`install_vulkan.py`** | Universal (Cross-Platform) | Pure Python 3 (standard library only, zero external pip dependencies). |
| **`enable-vulkan-4.10.1.ps1`** | Windows (PowerShell) | Standalone pure PowerShell implementation. |

---

## Instructions for Testers

1. Download or copy the appropriate script file into your **Heroes of Newerth** game folder (where `hon_x64.exe` and `WILLOWMAKER.exe` reside).
2. Execute the script:
   - **Windows**: Double-click `Install-Vulkan.bat`.
   - **Linux / Steam Deck**: Run `bash install-vulkan.sh`.
   - **Python**: Run `python install_vulkan.py`.
3. Launch the game using `WILLOWMAKER.exe`.
4. A Vulkan HUD displaying live FPS will appear in the top-left corner confirming Vulkan 1.3 is active.

### Diagnostics & Support
If an error occurs, the installer keeps the console open and writes full diagnostics to `vulkan_install.log` in the game directory. Testers can share this log file in Discord for support.
