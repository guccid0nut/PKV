# 🌐 Project Kongor: P2P & LAN Multiplayer Guide
### Host, Join & Play Heroes of Newerth Without a Master Server

Welcome to the definitive guide on hosting and playing peer-to-peer (P2P), LAN, and offline matches in **Project Kongor V** (`hon_x64.exe`).

In modern Project Kongor, players are no longer dependent on a centralized master server or remote matchmaking cluster. Whether you want to set up an offline LAN party with roommates, host a private match over the Internet for your friends, play against tactical AI bots completely offline, or play when the master server is down for maintenance, everything is built directly into the game client.

---

## 📋 Table of Contents
1. [⚡ Quick Start: 30-Second Summary](#1--quick-start-30-second-summary)
2. [🏠 Playing on LAN (Local Network / Same Household)](#2--playing-on-lan-local-network--same-household)
   - [LAN Host Setup](#lan-host-setup)
   - [LAN Joining Setup](#lan-joining-setup)
   - [Finding Your LAN IPv4 Address](#finding-your-lan-ipv4-address)
   - [LAN Firewall Check](#lan-firewall-check)
3. [🌍 Playing Over the Internet (Friends in Other Locations)](#3--playing-over-the-internet-friends-in-other-locations)
   - [Network Ports Reference](#network-ports-reference)
   - [Router Port Forwarding (UDP 11235)](#router-port-forwarding-udp-11235)
   - [Finding Your Public WAN IP](#finding-your-public-wan-ip)
   - [Internet Host & Join Steps](#internet-host--join-steps)
4. [🚀 The Zero-Port-Forwarding Alternative (Virtual LAN: Radmin VPN / Tailscale)](#4--the-zero-port-forwarding-alternative-virtual-lan-radmin-vpn--tailscale)
5. [🤖 Offline Bot Matches (Instant Single-Player / Practice)](#5--offline-bot-matches-instant-single-player--practice)
6. [⚙️ In-Game Lobby & Match Customization](#6--in-game-lobby--match-customization)
7. [💻 Power-User In-Engine Console Commands (Ctrl+F8)](#7--power-user-in-engine-console-commands-ctrlf8)
8. [🛠️ Troubleshooting & Frequently Asked Questions](#8--troubleshooting--frequently-asked-questions)

---

## 1. ⚡ Quick Start: 30-Second Summary

| Mode | Master Server Needed? | Router Port Forwarding Needed? | Host Action | Joiner Action |
| :--- | :---: | :---: | :--- | :--- |
| **LAN (Same Wi-Fi/Switch)** | ❌ No | ❌ No | Click **Play P2P** ➔ **Host Match** ➔ Configure & Start | Click **Play P2P** ➔ **Join Match** ➔ Enter Host's Local IP (`192.168.X.X`) |
| **Internet (Direct Connect)** | ❌ No |  **Yes** (UDP `11235`) | Port forward `11235` UDP ➔ Click **Play P2P** ➔ **Host Match** | Click **Play P2P** ➔ **Join Match** ➔ Enter Host's Public IP |
| **Virtual LAN (Radmin/Tailscale)** | ❌ No | ❌ No | Connect to VPN room ➔ Click **Play P2P** ➔ **Host Match** | Connect to VPN room ➔ Click **Play P2P** ➔ Enter Host's Virtual IP |
| **Offline Bot Match** | ❌ No | ❌ No | Click **Play Bot Match** on login screen ➔ Pick map/difficulty ➔ Play | N/A (Solo offline with bots) |

---

## 2. 🏠 Playing on LAN (Local Network / Same Household)

Playing over a Local Area Network (Ethernet switch or home Wi-Fi) is the simplest and lowest-latency way to play. **No router port forwarding is required.**

### LAN Host Setup
1. Launch **Project Kongor** (`hon_x64.exe`).
2. On the Login Screen:
   - Type your desired nickname into the **Username** field (e.g. `KongorKing`).
   - Click the blue **Play P2P** button (located directly beneath the Login button).
3. In the popup dialog, click **Host Match**.
4. The client will transition directly into the **Hosted Game Lobby**:
   - Choose your **Map** (Forests of Caldavar, Mid Wars, Watchtower, etc.).
   - Choose your **Game Mode** (All Pick, Single Draft, Banning Draft, etc.).
   - Verify the **Server** dropdown is set to **Local** or **Practice**.
   - Click **Create Game**.
5. Once your match lobby loads, share your **Local IPv4 Address** with other players in your house.

---

### LAN Joining Setup
1. Launch **Project Kongor** (`hon_x64.exe`).
2. On the Login Screen:
   - Type your chosen nickname into the **Username** field (e.g. `Pyromancer99`).
   - Click **Play P2P**.
3. In the popup dialog, click **Join Match**.
4. In the address box, enter the Host's Local IP (e.g. `192.168.1.50` or `192.168.1.50:11235`).
5. Click **Connect** (or press Enter).
6. You will immediately connect to the host's lobby!

---

### Finding Your LAN IPv4 Address
The host can find their local IP in 5 seconds on Windows:

#### Method A: Via Windows PowerShell or Command Prompt
1. Press `Win + R`, type `powershell` (or `cmd`), and hit **Enter**.
2. Run:
   ```powershell
   ipconfig
   ```
3. Look for your active connection (**Ethernet adapter** or **Wireless LAN adapter Wi-Fi**).
4. Find the line:
   ```text
   IPv4 Address. . . . . . . . . . . : 192.168.1.50
   ```
   *(Your address will typically start with `192.168.X.X` or `10.0.X.X`)*. Give this address to your LAN friends.

---

### LAN Firewall Check
By far the most common reason a LAN connection fails is the **Windows Defender Firewall** on the Host's PC blocking incoming UDP packets.

#### 1. Network Profile Must Be "Private"
Make sure your home Wi-Fi/Ethernet network is set to **Private Network** (not Public):
- Open Windows **Settings** ➔ **Network & internet** ➔ **Wi-Fi** (or **Ethernet**).
- Click on your active network connection.
- Under **Network profile type**, select **Private network**.

#### 2. Quick One-Line Firewall Rule (PowerShell Admin)
To guarantee Windows Firewall allows game traffic without interruptions, open PowerShell as Administrator and run:
```powershell
New-NetFirewallRule -DisplayName "Project Kongor Game Server" -Direction Inbound -LocalPort 11235 -Protocol UDP -Action Allow
New-NetFirewallRule -DisplayName "Project Kongor Voice Server" -Direction Inbound -LocalPort 11435 -Protocol UDP -Action Allow
```

---

## 3. 🌍 Playing Over the Internet (Friends in Other Locations)

When hosting a game on your home PC for friends connecting over the Internet (outside your house), network packets must traverse your home router.

### Network Ports Reference

| Port | Protocol | Purpose | Requirement |
| :--- | :---: | :--- | :--- |
| **`11235`** | **UDP** | Primary Game Traffic & State Synchronization | **Mandatory** |
| **`11435`** | **UDP** | In-Game Voice Chat (VoIP) | Optional (only if using in-game voice) |

> [!IMPORTANT]
> **Protocols must be UDP**, not just TCP! The K2 game engine communicates high-frequency player state and game ticks exclusively over UDP. Setting TCP-only will cause connection timeouts.

---

### Router Port Forwarding (UDP 11235)
1. Open your web browser and log into your router's administration portal (usually `http://192.168.1.1` or `http://192.168.0.1` — check the label on your router).
2. Navigate to the **Port Forwarding**, **Virtual Server**, or **NAT** settings tab.
3. Add a new Port Forwarding rule with the following values:
   - **Rule Name**: `Kongor-Host`
   - **Protocol**: `UDP` (or `Both / UDP+TCP`)
   - **External / WAN Port**: `11235`
   - **Internal / LAN Port**: `11235`
   - **Internal IP Address**: Your PC's Local IPv4 address (found via `ipconfig`, e.g. `192.168.1.50`).
4. *(Optional Voice Rule)*:
   - **Rule Name**: `Kongor-Voice`
   - **Protocol**: `UDP`
   - **External & Internal Port**: `11435`
   - **Internal IP**: Your PC's Local IPv4 address.
5. Save/Apply settings on the router.

---

### Finding Your Public WAN IP
The host must provide their **Public (External) IP** to remote friends:
- Visit [https://icanhazip.com](https://icanhazip.com) or [https://whatismyipaddress.com](https://whatismyipaddress.com).
- Copy the IPv4 address shown (e.g., `98.142.55.101`).

---

### Internet Host & Join Steps
1. **Host**:
   - Follow the same steps as LAN: Click **Play P2P** ➔ **Host Match** ➔ Configure map and settings ➔ **Create Game**.
   - Stay in the lobby and give your **Public IP** to your friends.
2. **Friends (Joining)**:
   - Enter your nickname on the login screen.
   - Click **Play P2P** ➔ **Join Match**.
   - Enter the Host's Public IP (e.g. `98.142.55.101` or `98.142.55.101:11235`).
   - Click **Connect**.

---

## 4. 🚀 The Zero-Port-Forwarding Alternative (Virtual LAN: Radmin VPN / Tailscale)

If you cannot access your home router, your ISP uses **CGNAT (Carrier-Grade NAT)**, or you simply don't want to configure port forwarding, you can use a Virtual LAN tool.

With a Virtual LAN, your computers connect to an encrypted virtual network that behaves exactly like a local home network:

### Recommended Free Tools:
1. **Radmin VPN** *(Easiest for gaming - no account required)*:
   - Download & install [Radmin VPN](https://www.radmin-vpn.com/).
   - Host clicks **Create Network** (set Network Name & Password).
   - Friends click **Join Network** and enter the name and password.
   - Friends copy the Host's **Radmin IP** (starts with `26.X.X.X`).
   - In Project Kongor: Click **Play P2P** ➔ **Join Match** ➔ Enter the `26.X.X.X` IP ➔ Click **Connect**.
2. **Tailscale** *(Enterprise-grade mesh VPN, zero lag)*:
   - Both install Tailscale and join the same tailnet.
   - Join using the Host's Tailscale `100.X.X.X` IP.

> [!TIP]
> Virtual LANs completely bypass router firewalls, NAT restrictions, and strict ISP filters. If you frequently play custom matches with a regular group of friends, creating a permanent Radmin VPN or Tailscale network is often the fastest and most reliable setup!

---

## 5. 🤖 Offline Bot Matches (Instant Single-Player / Practice)

Want to play Heroes of Newerth solo against bots with zero internet connection, zero network setup, and zero master server?

1. On the client Login Screen, click **Play Bot Match** (located right beneath **Play P2P**).
2. A dedicated **Offline Bot Match Dialog** opens:
   - **Select Map**:
     - `Mid Wars`: Fast-paced 5v5 single-lane all-mid action.
     - `Caldavar (FoC)`: Classic 3-lane Forests of Caldavar.
   - **Select Bot Difficulty**:
     - `Easy`: Relaxed bots with reduced aggression and slower rotations.
     - `Medium`: Balanced bots with standard laning, spell combos, and lane coordination.
     - `Hard`: High-tactical bots with aggressive pushing, team fighting, and ganking.
3. Click **Start Match**.
4. The game will immediately load you in with 9 AI bots filling all remaining player slots!

---

## 6. ⚙️ In-Game Lobby & Match Customization

When hosting via **Play P2P ➔ Host Match**, you have full control over the match parameters:

- **Game Modes**:
  - **All Pick (AP)**: Standard hero selection.
  - **Single Draft (SD)**: Pick from 3 random heroes (1 STR, 1 AGI, 1 INT).
  - **All Random (AR)**: Pure random chaos.
  - **Banning Draft (BD)**: Captains ban and pick heroes.
  - **Draft Mode (DM)** / **Mid Wars**: Optimized lane dynamics.
- **Match Options**:
  - **Casual Mode**: Higher gold gain, no gold loss on death.
  - **Duplicate Heroes**: Multiple players can pick the same hero.
  - **No Powerups (Runes)**: Disables river runes.
  - **No Respawn Timer**: Instant respawn for frantic scrims.
  - **Alternate Picks**: Turn-based sequential drafting.
  - **Hardcore**: Classic brutal mechanics.
  - **Autobalance & Tournament Rules**: Competitive tournament integrity.
- **Player Limits**:
  - Configure team sizes from 1v1 up to 5v5.
  - Allocate dedicated spectator and referee slots.

---

## 7. 💻 Power-User In-Engine Console Commands (`Ctrl+F8`)

For advanced players, tournament organizers, and developers, the K2 engine provides direct console commands accessible by pressing `Ctrl + F8`:

### Direct Connect
```text
Connect 192.168.1.50:11235
```
*(Connects directly to the specified IP and port).*

### Direct Host via Console
```text
StartGame local "MyMatch" map:caldavar allheroes:true
```
*(Instantly initializes a local listen server and loads Forests of Caldavar).*

### Mid Wars Host via Console
```text
StartGame local "MidWarsFun" map:midwars allheroes:true dev:false
```

### Adding Bots to Slots
Once inside a local lobby or match:
```text
AddFakePlayer 1
AddFakePlayer 2
```
*(Adds an AI bot to Team 1 (Legion) or Team 2 (Hellbourne)).*

### Kicking or Restarting
```text
Disconnect
```
*(Safely leaves the current server and returns to the main menu).*

---

## 8. 🛠️ Troubleshooting & Frequently Asked Questions

### Q: "Connection to server timed out" when joining on LAN
- **Cause**: Almost always Windows Defender Firewall on the host's PC.
- **Fix**:
  1. Ensure both computers are connected to the same Wi-Fi network or switch.
  2. Verify your network profile is set to **Private** in Windows Network Settings.
  3. On the Host PC, add the firewall rule via PowerShell Admin:
     ```powershell
     New-NetFirewallRule -DisplayName "Kongor Port 11235" -Direction Inbound -LocalPort 11235 -Protocol UDP -Action Allow
     ```

### Q: Friends over the Internet cannot connect to my host
- **Cause 1**: The port forwarding rule was configured for **TCP instead of UDP**. Make sure the router rule is set to **UDP**.
- **Cause 2**: Your PC's internal IP changed (e.g. from `192.168.1.50` to `192.168.1.52`). Set a static DHCP reservation in your router for your gaming PC.
- **Cause 3**: Carrier-Grade NAT (CGNAT). If your router's WAN IP begins with `100.64.X.X`–`100.127.X.X`, your ISP does not assign you a public IPv4. Use **Radmin VPN** or **Tailscale** instead.

### Q: Does the host need a high-end PC?
- **Answer**: The K2 engine's built-in listen server is extremely lightweight. Hosting a 5v5 match requires very little extra CPU overhead (~2–5% on a modern quad-core processor). A standard broadband connection with at least 5–10 Mbps upload speed is sufficient for 10 players.

### Q: Can players use custom mods or alt avatars in P2P?
- **Answer**: Yes! Local alt avatars and audio modifications installed in your game directory work seamlessly in P2P matches.

### Q: Where do match replays get saved?
- **Answer**: Local replays are saved automatically in:
  ```text
  %USERPROFILE%\Documents\Heroes of Newerth x64\game\replays\
  ```
  or inside `games/client/userdataKONGOR/` depending on your launcher profile.

---

*Project Kongor V is an independent, non-profit community preservation initiative dedicated to keeping Heroes of Newerth alive, accessible, and playable forever.*
