# Announcer Forge: Technical Specification & Process Manual

**Subsystem**: Content Forge Pipelines & Audio Synthesis  
**Module**: `scripts/announcer_forge.py`  
**Reference Implementations**: Gordon Ramsay (`ramsay`), Seductive ASMR (`asmr`)  
**Status**: Production / Active  
**Last Updated**: 2026-09-15  

---

## 1. Executive Summary & Architectural Overview

The **Announcer Forge** (`scripts/announcer_forge.py`) is the autonomous, high-fidelity voice pack pipeline for Project Kongor. It provides an end-to-end workflow from voice acquisition (scraped samples, actor recordings, or ElevenLabs generative AI) to in-engine playback and Store 2.0 discovery.

Announcers are global audio customization assets in Heroes of Newerth. Unlike 3D hero entities which exist in world space and require 1-channel Mono spatial audio, Announcer Voice Packs are heard exclusively through the player's 2-channel Stereo UI bus. They replace standard in-game announcements for multikills, killstreaks, objective alerts, and match milestones.

```
[Voice Acquisition]                     [Audio Engineering & Mastering]              [K2 VFS & Store 2.0 Integration]
┌───────────────────────────┐           ┌────────────────────────────────────────┐   ┌────────────────────────────────────────┐
│ • ElevenLabs Generative   │           │ • Per-Cue Silence Valley Trimming      │   │ • 47 Master Stereo WAVs (28 + 19)      │
│   Voice Model (TTS)       │──(MP3)───▶│ • 10 ms Micro-Fade-In (Anti-Pop)       │──▶│ • Dual Soundtables (.soundtable)       │
│ • Real Actor Audio Stems  │           │ • Presence EQ & Dynamics Companding    │   │ • Store 2.0 Config (Type 4, 128x128)   │
│ • Procedural DSP Chords   │           │ • Two-Pass EBU R128 Normalization      │   │ • Store Lua, Cache, Stringtables       │
└───────────────────────────┘           └────────────────────────────────────────┘   └────────────────────────────────────────┘
```

---

## 2. The Definitive K2 Announcer Event Matrix & Engine Aliases

A compliant Announcer Voice Pack requires **28 distinct gameplay event recordings** plus **19 hardcoded legacy engine alias files** for a total of **47 WAVE files**.

### Why 19 Engine Aliases Are Mandatory
The K2 C++ engine (`cgame_x64.dll` and `game_x64.dll`) utilizes dual routing:
1. **Dynamic Soundtable Lookup**: Dispatches named sound events (`multikill0`, `killstreak3`, `denied`) through the active soundtable XML.
2. **Direct Disk Fallback**: For legacy netcode and spectator replay events, engine routines query static filenames on disk (`multikill0.wav`, `killstreak0.wav`, etc.) directly from the announcer's VFS folder (`/shared/sounds/announcer/<id>/`). If these files are absent, the engine falls back to the default announcer or plays silence.

### Master Event Cue Table (28 Primary Cues + 19 Legacy Aliases)

| # | In-Engine Event Name | Canonical Audio File | Legacy Engine Alias (`.wav`) | Gameplay Trigger Context | Recommended SFX / Vocal Style |
|---|:---|:---|:---|:---|:---|
| 1 | `first_blood` | `first_blood.wav` | *None* | First kill of the match | Tactile intro (mic tap/gong) + energetic line |
| 2 | `multikill0` | `double_kill.wav` | `multikill0.wav` | 2 kills within 18 seconds | Immediate vocal onset; no heavy background SFX |
| 3 | `multikill1` | `triple_kill.wav` | `multikill1.wav` | 3 kills within 18 seconds | Rising excitement, light shimmer |
| 4 | `multikill2` | `quad_kill.wav` | `multikill2.wav` | 4 kills within 18 seconds | Pure punchy whisper or dramatic shout; immediate |
| 5 | `multikill3` | `annihilation.wav` | `multikill3.wav` | 5 kills within 18 seconds (Ace) | Maximum intensity, long sustain, celebratory |
| 6 | `killstreak0` | `3_kills.wav` | `killstreak0.wav` | 3 consecutive kills without dying (Hat Trick / Bloodlust) | Iconic catchphrase ("Hat trick... delicious") |
| 7 | `killstreak1` | `4_kills.wav` | `killstreak1.wav` | 4 consecutive kills without dying | Pure vocal, direct delivery |
| 8 | `killstreak2` | `5_kills.wav` | `killstreak2.wav` | 5 consecutive kills without dying (Serial Killer) | Rising tension |
| 9 | `killstreak3` | `6_kills.wav` | `killstreak3.wav` | 6 consecutive kills without dying (Ultimate Warrior) | Shimmer tingles or aggression |
| 10 | `killstreak4` | `7_kills.wav` | `killstreak4.wav` | 7 consecutive kills without dying (Legendary) | Resonant reverb |
| 11 | `killstreak5` | `8_kills.wav` | `killstreak5.wav` | 8 consecutive kills without dying (Onslaught) | Crisp snap/tap trigger |
| 12 | `killstreak6` | `9_kills.wav` | `killstreak6.wav` | 9 consecutive kills without dying (Savage Sick) | High drama |
| 13 | `killstreak7` | `10_kills.wav` | `killstreak7.wav` | 10 consecutive kills without dying (Dominating) | Intense presence |
| 14 | `killstreak8` | `immortal.wav` | `killstreak8.wav` | 15+ consecutive kills without dying (Immortal / Godlike) | Majestic, high energy, sustained reverb |
| 15 | `team_killstreak`| `massacre.wav` | `team_killstreak.wav` | Team scores 5 rapid kills | Pure voice; sudden impact |
| 16 | `team_wipe` | `genocide.wav` | `team_wipe.wav` | Entire enemy team dead simultaneously | Eerie shimmer or explosive roar |
| 17 | `denied` | `denied.wav` | *None* | Player denies friendly creep, tower, or hero | Snide, dismissive, sharp tap |
| 18 | `humiliation` | `humiliation.wav` | *None* | Killed by neutral creeps, Kongor, or fountain | Pure vocal mock; zero muddy breath |
| 19 | `smackdown` | `smackdown.wav` | *None* | Target executed while taunted (Taunt smackdown) | Heavy impact, taunting line |
| 20 | `taunt_after` | `smackdown.wav` | `taunt_after.wav` | Post-kill taunt follow-up | Shared audio with `smackdown.wav` |
| 21 | `rage_quit` | `rage_quit.wav` | *None* | Enemy player disconnects/abandons match | Sarcastic mockery; immediate onset |
| 22 | `rival` | `rival.wav` | *None* | Repeatedly killed by the same enemy hero | Personal banter, crisp mic tap |
| 23 | `payback` | `payback.wav` | *None* | Killing your designated rival | Sweet revenge; pure vocal |
| 24 | `start_game` | `prepare_for_battle.wav` | `start_game.wav` | Match starts (0:00 timer / hero spawn) | Triple mic taps / battle horn + greeting |
| 25 | `get_it_on` | `startgame.wav` | `get_it_on.wav` | Match begins / creep spawn ("Get it on!") | Short punchy kickoff phrase |
| 26 | `victory` | `victory.wav` | *None* | Enemy base destroyed (Match Won) | Triumphant fanfare or intimate praise |
| 27 | `defeat` | `defeat.wav` | *None* | Friendly base destroyed (Match Lost) | Pure vocal consoling or furious rant |
| 28 | `kongor_slain` | `kongor_slain.wav` | *None* | Kongor boss defeated | Heavy sub-bass tingles or celebratory line |
| 29 | `base_under_attack` | `structure_under_attack.wav` | `base_under_attack.wav` | Main structure taking damage | Triple tap alarm alert |

---

## 3. Audio Engineering & Mastering Standards

### 3.1. Technical Specifications
Every WAV file generated by the Announcer Forge must strictly conform to:
- **Container**: Microsoft RIFF WAVE (`fmt ` chunk tag `0x0001` PCM).
- **Sampling Rate**: `48,000 Hz` (48 kHz).
- **Bit Depth**: `16-bit` signed integer PCM (`pcm_s16le`).
- **Channel Allocation**: Exactly `2 Channels (Stereo)`.
  > [!CAUTION]
  > Never export 1-channel Mono files for announcers. The K2 engine routes Mono files into OpenAL Soft 3D positional panning. Because announcer events lack 3D world coordinates, Mono files will fail to spatialize or pan violently to `(0, 0, 0)` in the center of the map.

### 3.2. Loudness Normalization (EBU R128)
All cues must undergo two-pass EBU R128 loudness normalization using FFmpeg's `loudnorm` filter:

```powershell
# Pass 1: Measure integrated loudness and True Peak
ffmpeg -i premix.wav -af "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json" -f null -

# Pass 2: Apply linear normalization with measured parameters
ffmpeg -y -i premix.wav -af "loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={input_i}:measured_TP={input_tp}:measured_LRA={input_lra}:measured_thresh={input_thresh}:offset={target_offset}:linear=true" -ar 48000 -ac 2 -c:a pcm_s16le master.wav
```

#### Standard Target Values:
- **Energetic / Comedic / Aggressive Packs** (e.g., Ramsay, Samuel L. Jackson, Flamboyant):
  - Integrated Loudness: `I = -14.0 LUFS` ($\pm 1.0\text{ LUFS}$)
  - True Peak: `TP <= -1.0 dBTP`
- **Intimate / ASMR / Seductive Packs** (e.g., Whispers of Newerth):
  - Integrated Loudness: `I = -16.0 LUFS` ($\pm 1.0\text{ LUFS}$)
  - True Peak: `TP <= -1.14 dBTP`
  - Loudness Range: `LRA = 11.0 LU`

### 3.3. AI TTS Prompt Leakage Elimination
Modern AI voice models (ElevenLabs, Bark, Tortoise) often vocalize prompt stage directions (e.g. `[Whispering seductively]`, `[Angry shout]`) during the first 1.2 to 1.8 seconds.

#### The Silence-Valley Trimming Algorithm:
1. **Compute Energy Envelope**: Calculate short-time RMS frame energy over 10 ms windows.
2. **Find Speech Termination of Intro**: Identify where the energy of the prompt direction drops below $-35\text{ dBFS}$.
3. **Locate Inter-Phrase Silence Valley**: Find the minimum energy point ($<-45\text{ dBFS}$) before the true announcer speech onset.
4. **Apply Micro-Fade-In**: Slice at the valley and apply a `10 ms` linear fade-in (`afade=t=in:ss=0:d=0.01`). This eliminates boundary clicks, zero-crossing pops, and DC offset thumps while preserving 100% of initial consonants (e.g., the "H" in *"Hat trick"*).

### 3.4. Sound Effect Layering vs. Pure Vocal Rules
- **Tactile Lead-In Triggers** (`delay_ms = 250..350`):
  Use for match milestone cues (`first_blood`, `3_kills`, `start_game`, `denied`). Play the Foley trigger (mic tap, chime, gong) at $t = 0.0\text{s}$, with the vocal onset entering at $t = 0.25\text{s}$–$0.35\text{s}$.
- **Pure Vocal Delivery** (`sfx = None`, `delay_ms = 0`):
  Mandatory for fast-paced combat cues (`double_kill`, `quad_kill`, `4_kills`, `massacre`, `humiliation`, `payback`, `defeat`). High-tier kill events must have immediate zero-latency vocal attack. Unwanted breathing, sighing, or heavy rumble sound effects must never be layered over these cues.

---

## 4. K2 XML Soundtable Architecture

The announcer directory must provide **dual soundtables** to ensure compatibility across all engine call sites:
1. `<id>.soundtable`
2. `<id>_announcer.soundtable` (or `announcer_<id>.soundtable`)

### Canonical Soundtable XML Schema

```xml
<?xml version="1.0" encoding="UTF-8"?>
<soundtable name="announcer_asmr">
    <sound name="first_blood" sample="/shared/sounds/announcer/asmr/first_blood.wav" volume="1.0" minfalloff="100" maxfalloff="1500" dampening="0.0" />
    <sound name="multikill0" sample="/shared/sounds/announcer/asmr/double_kill.wav" volume="1.0" />
    <sound name="multikill1" sample="/shared/sounds/announcer/asmr/triple_kill.wav" volume="1.0" />
    <sound name="multikill2" sample="/shared/sounds/announcer/asmr/quad_kill.wav" volume="1.0" />
    <sound name="multikill3" sample="/shared/sounds/announcer/asmr/annihilation.wav" volume="1.0" />
    <sound name="killstreak0" sample="/shared/sounds/announcer/asmr/3_kills.wav" volume="1.0" />
    <sound name="killstreak1" sample="/shared/sounds/announcer/asmr/4_kills.wav" volume="1.0" />
    <sound name="killstreak2" sample="/shared/sounds/announcer/asmr/5_kills.wav" volume="1.0" />
    <sound name="killstreak3" sample="/shared/sounds/announcer/asmr/6_kills.wav" volume="1.0" />
    <sound name="killstreak4" sample="/shared/sounds/announcer/asmr/7_kills.wav" volume="1.0" />
    <sound name="killstreak5" sample="/shared/sounds/announcer/asmr/8_kills.wav" volume="1.0" />
    <sound name="killstreak6" sample="/shared/sounds/announcer/asmr/9_kills.wav" volume="1.0" />
    <sound name="killstreak7" sample="/shared/sounds/announcer/asmr/10_kills.wav" volume="1.0" />
    <sound name="killstreak8" sample="/shared/sounds/announcer/asmr/immortal.wav" volume="1.0" />
    <sound name="team_killstreak" sample="/shared/sounds/announcer/asmr/massacre.wav" volume="1.0" />
    <sound name="team_wipe" sample="/shared/sounds/announcer/asmr/genocide.wav" volume="1.0" />
    <sound name="denied" sample="/shared/sounds/announcer/asmr/denied.wav" volume="1.0" />
    <sound name="humiliation" sample="/shared/sounds/announcer/asmr/humiliation.wav" volume="1.0" />
    <sound name="smackdown" sample="/shared/sounds/announcer/asmr/smackdown.wav" volume="1.0" />
    <sound name="taunt_after" sample="/shared/sounds/announcer/asmr/smackdown.wav" volume="1.0" />
    <sound name="rage_quit" sample="/shared/sounds/announcer/asmr/rage_quit.wav" volume="1.0" />
    <sound name="rival" sample="/shared/sounds/announcer/asmr/rival.wav" volume="1.0" />
    <sound name="payback" sample="/shared/sounds/announcer/asmr/payback.wav" volume="1.0" />
    <sound name="start_game" sample="/shared/sounds/announcer/asmr/prepare_for_battle.wav" volume="1.0" />
    <sound name="get_it_on" sample="/shared/sounds/announcer/asmr/startgame.wav" volume="1.0" />
    <sound name="victory" sample="/shared/sounds/announcer/asmr/victory.wav" volume="1.0" />
    <sound name="defeat" sample="/shared/sounds/announcer/asmr/defeat.wav" volume="1.0" />
    <sound name="kongor_slain" sample="/shared/sounds/announcer/asmr/kongor_slain.wav" volume="1.0" />
    <sound name="base_under_attack" sample="/shared/sounds/announcer/asmr/structure_under_attack.wav" volume="1.0" />
</soundtable>
```

---

## 5. Store 2.0 Integration & Master Server Synchronization

For an announcer pack to be purchasable, equipable in options, and visible in Store 2.0, seven catalog and configuration files must be synchronized:

### 5.1. Master Server Catalog (`StoreItemsConfiguration.json`)
Located at: `src/services/master-server/Configuration/Store/StoreItemsConfiguration.json`
- **Schema Type**: Announcers must strictly use `"Type": 4`.
- **Sequential ID**: IDs $\ge 6013$ (e.g. Ramsay = 6013, ASMR = 6014, Next = 6015).

```json
{
  "ID": 6014,
  "Name": "Seductive ASMR Announcer",
  "Code": "ASMR",
  "Type": 4,
  "Resource": "/ui/fe2/store/icons/announcer_asmr.tga",
  "Purchasable": true,
  "GoldCost": 1,
  "SilverCost": 1,
  "IsPremium": false,
  "IsBundle": false,
  "IsEnabled": true,
  "IsDynamic": false,
  "Required": []
}
```

### 5.2. Upgrades Seed Data (`Upgrades.JSON`)
Located at: `src/services/master-server/Data/Seed/Upgrades.JSON`
- Must specify `"UpgradeType": 4` and matching `"UpgradeId"`.

```json
{
  "UpgradeId": "6014",
  "Code": "ASMR",
  "Name": "Seductive ASMR Announcer",
  "Type": "Announcer",
  "Description": "Seductive ASMR Announcer Pack",
  "UpgradeType": 4,
  "Enabled": true
}
```

### 5.3. Master Server Binary Sync (Dynamic Reload)
The master server watches `StoreItemsConfiguration.json`. If running from pre-compiled binaries, changes must be mirrored to:
`src/services/master-server/bin/Debug/net11.0/Configuration/Store/StoreItemsConfiguration.json`

### 5.4. Client Store Lua Script (`store.lua`)
Located at: `games/shared/ui/scripts/store.lua`  
Register the item in `VoiceAnnouncersTable`:

```lua
['6014'] = {
    ['product_id']       = '6014',
    ['cost_gold']        = '1',
    ['cost_silver']      = '1',
    ['voicePack_code']   = 'asmr',
    ['name']             = 'Seductive ASMR Announcer',
    ['search_tags']      = 'asmr whisper seductive tingles sensual',
},
```

### 5.5. Hero Definitions Cache (`hero_definitions_cache.lua`)
Located at: `games/shared/ui/scripts/newui/hero_definitions_cache.lua`

```lua
['ASMR'] = 'Seductive ASMR Announcer',
```

### 5.6. Stringtables (`interface_en.str` & `entities_en.str`)
Located at: `games/shared/stringtables/` and `games/client/` mirrors:
```properties
store_pack_info_announcer_asmr_title           Seductive ASMR Announcer
store_pack_info_announcer_asmr_desc            Let gentle whispers, delicate mic taps, and tingling shimmers guide your kills across Newerth.
Announcer_asmr                                 Seductive ASMR Announcer
```

### 5.7. Player Vault Default Ownership Seeding
To ensure test accounts immediately own the announcer pack:
- `src/libs/contracts/Constants/StoreDefaults.cs`:
  ```csharp
  public const string AsmrAnnouncer = "av.ASMR";
  ```
- `src/libs/database-context/Handlers/SeedDataHandlers.cs`:
  ```csharp
  inventory.Add("av.ASMR");
  ```

---

## 6. Visual Assets: Store Icon Standards

Store 2.0 displays 128x128 icons in both the Store catalog carousel and the In-Game Sound Options dropdown.

```
games/shared/00000000/ui/fe2/store/icons/
├── announcer_<id>.tga     <-- 128x128 32-bit uncompressed TrueColor TGA
└── announcer_<id>.dds     <-- 128x128 DirectDraw Surface, BC1 (DXT1), 8 Mipmaps
```

### Specifications:
1. **Dimensions**: Exactly `128 x 128` pixels (power of two).
2. **DDS Format**: `BC1 / DXT1` (Opaque or 1-bit alpha).
3. **Mipmap Count**: Strictly `8 mip levels` ($128 \to 64 \to 32 \to 16 \to 8 \to 4 \to 2 \to 1$).
4. **Mirror Directories**:
   - `games/shared/00000000/ui/fe2/store/icons/`
   - `games/client/override/ui/fe2/store/icons/`
5. **Texture Cache Purge**:
   Whenever updating or introducing an icon, delete any cached surface from:
   `games/client/userdataKONGOR/extensions/texturecache/00000000/ui/fe2/store/icons/announcer_<id>.dds`
   Otherwise, the client will display a blank or black square.

---

## 7. In-Engine Testing & Verification

### 7.1. Direct Console Audio Testing
In the K2 console (`Ctrl+F8` or via `kongor_game_command`):
```c
// Test direct audio file playback
PlaySound /shared/sounds/announcer/asmr/first_blood.wav

// Test multi-kill sequence
PlaySound /shared/sounds/announcer/asmr/quad_kill.wav

// Equip announcer via cvar
sound_announcer asmr
```

### 7.2. Automated Speech Recognition Assertions
To verify that prompt directions (e.g. `[Whispering seductively]`) are 100% eliminated, run automated Windows Speech Recognition checks:
```powershell
Add-Type -AssemblyName System.Speech
$engine = New-Object System.Speech.Recognition.SpeechRecognitionEngine
# Verify sample audio begins immediately with target phrase
```

### 7.3. Unit Test Suite
Every announcer pack must have a dedicated automated test suite in `tests/hon_model_tools/` (e.g., `test_asmr_announcer_pack.py` and `test_ramsay_announcer_pack.py`):
```powershell
python -m unittest tests/hon_model_tools/test_asmr_announcer_pack.py
python -m unittest tests/hon_model_tools/test_ramsay_announcer_pack.py
```
Checks:
- [x] 48,000 Hz, 16-bit PCM, 2-Channel Stereo across all 47 WAVs.
- [x] Valid XML in both soundtables.
- [x] DDS icon has 128x128 resolution, DXT1 FourCC, and 8 mipmaps.
- [x] Zero duplicate SHA-256 hashes across the 28 primary events.
- [x] Consistent EBU R128 loudness ($-16.0 \pm 1.0\text{ LUFS}$ or $-14.0 \pm 1.0\text{ LUFS}$) and True Peak $\le -1.0\text{ dBTP}$.

---

## 8. Living Troubleshooting Log

| ID | Symptom / Log Snippet | Root Cause Diagnosis | Permanent Prevention / Remediation |
|:---|:---|:---|:---|
| **[AF-001]** | Announcer speaks prompt stage directions (*"Whispering seductively"*) aloud at the start of cues. | Modern AI TTS models interpret square bracket directions as spoken script when punctuation is ambiguous. | Run automated silence-valley RMS detection and slice at the quietest inter-phrase point ($<-45\text{ dBFS}$) with a 10 ms micro-fade-in. |
| **[AF-002]** | Multi-kill cues play default announcer or dead silence during spectator/replay modes. | Engine legacy C++ routines query hardcoded filenames (`multikill0.wav`, `killstreak0.wav`) instead of soundtable XML keys. | Generate 19 legacy engine alias WAV copies alongside the 28 primary files (total 47 WAVs). |
| **[AF-003]** | Announcer card does not appear on Store Page 3 despite valid `StoreItemsConfiguration.json`. | Master server running from `bin/Debug/` did not receive the updated catalog because file copy occurred only in the repo root. | Sync catalog files directly to `src/services/master-server/bin/Debug/net11.0/` or rely on the master server file watcher. |
| **[AF-004]** | Announcer breath or rumble SFX drowns out combat on high-tier streaks (`quad_kill`, `humiliation`). | Heavy acoustic layers forced indiscriminately onto rapid-fire combat cues create audio fatigue. | Enforce per-cue SFX rules: high-tempo cues must be pure vocal lines (`sfx = None`) with 0 ms latency. |
| **[AF-005]** | Store icon displays as an untextured black square in Store 2.0. | DDS texture missing mipmaps or stale mip surface cached in `userdataKONGOR/extensions/texturecache/`. | Export exactly 8 mip levels with BC1/DXT1 compression and purge the texture cache folder upon deployment. |
| **[AF-006]** | Announcer voice pans erratically to center of map or fails to play. | Audio file was authored as 1-channel Mono instead of 2-channel Stereo. | Enforce Gateway 2: Stereo 2-channel PCM only. Never export mono files for UI announcers. |
| **[AF-007]** | Announcer works when selected in game options, but fails during bot scrimmage triggers. | Soundtable was named `{id}.soundtable` while scrimmage choreography queried `{id}_announcer.soundtable`. | Always deploy both `{id}.soundtable` and `{id}_announcer.soundtable` with identical mappings. |
| **[AF-008]** | Model exports with 0 bones or meshes omitted in SMDL v3 binary. | Blender `io_scene_hon` uses `selected_objects` if non-empty; if only one object was selected, the armature and sibling meshes are omitted. | Deselect all objects prior to joining, then explicitly select all scene objects (`arm_obj`, `text_obj`, `prop_obj`, `invis_obj`) before calling `export_hon_model`. |
| **[AF-009]** | Text mesh gets absorbed into thematic prop mesh in Blender. | `bpy.ops.object.join()` joins all currently selected meshes into the active object; if `text_obj` was selected, it is merged into the prop. | Call `deselect_all()` immediately prior to selecting prop elements for joining. |
| **[AF-010]** | Arcade text visual pops out of existence when animating or scaling large on screen. | K2 engine frustum culls UI models whose bounding box is smaller than the animated scale bounds. | Include an `_invis_box` mesh (scale 800x50x250) bound to the `_invis_box` bone to enforce camera frustum persistence. |
| **[AF-011]** | Store 2.0 announcer preview buttons play default arcade text instead of custom pack 3D animations. | `HoN_Store.VoiceAnnouncersTable[productID]['voicePack_models']` in `store.lua` was hardcoded or defaulted to `'arcade_text'`. | Map `['voicePack_models'] = '<pack_id>'` in `HoN_Store.VoiceAnnouncersTable` across `games/shared/ui/scripts/store.lua` and all client mirrors. |
| **[AF-012]** | Blender 4.2 headless process crashes silently or buffers log output during curve/mesh conversion. | Headless Blender stdout on Windows is block-buffered; unhandled exceptions do not flush before process exit. Furthermore, creating font curves leaves orphaned datablocks. | Force unbuffered logging via `functools.partial(print, flush=True)` and cleanly convert/unlink font curve datablocks before GLTF node evaluations. |
| **[AF-013]** | Arcade text appears flat/monochromatic with poor legibility in high-tempo combat. | Single-material geometry lacks contrast and screen-space separation from noisy 3D background battle scenes. | Author dual-tone beveled geometry: `TextMesh_Front` (`material.material` chrome specular) + `TextMesh_Border` (`border.material` with `vDiffuseColor="3.0000 0.3500 0.0500"` for K2 HDR bloom aura). |
| **[AF-014]** | Meshy-generated GLB props exceed K2 vertex limits causing SMDL export crash or vertex distortion. | High-poly Meshy assets often contain 80,000–120,000 vertices, exceeding the K2 16-bit vertex index limit of 65,535 per mesh (`[HF-019]`). | Apply Blender `decimate` modifier (ratio 0.25 to 0.45) to restrict every individual mesh under 50,000 vertices, and assert `num_vertices <= 65535` in pre-flight unit tests. |

---

## 9. Announcer Forge CLI Reference

```powershell
# Ingest raw voice recordings and master complete announcer pack:
python scripts/announcer_forge.py --announcer-id valkyrie --name "Valkyrie Announcer Pack" --raw-dir "scratch/valkyrie_mp3" --target-lufs -14.0

# Procedural chord synthesis for automated CI validation:
python scripts/announcer_forge.py --announcer-id synth_test --name "Procedural Synth Pack" --mode synth --dry-run

# Full automated test sweep:
python -m unittest tests/hon_model_tools/test_content_forge_phase3.py
```

---

## 10. 3D Arcade Text Animation Forge Pipeline

The Arcade Text Forge generates custom, thematic 3D animated visual announcements displayed front-and-center when iconic combat events occur (First Blood, Smackdown, Multi-kills, Streaks).

### 10.1. Engine Architecture & VFS Locations
K2 queries visual announcement models based on the equipped announcer's `arcadetext` attribute in `base.upgrades`:
```xml
<announcervoice name="Gordon Ramsay" voiceset="ramsay" arcadetext="ramsay" />
<announcervoice name="Seductive ASMR" voiceset="asmr" arcadetext="seductive" />
```
Path: `/ui/common/models/<arcadetext>/<event>.mdf`

```
games/shared/ui/common/models/<arcadetext>/
├── <event>.model          <-- Binary SMDL v3 3D geometry (TextMesh, Prop, _invis_box)
├── <event>.clip           <-- Binary CLIP v2 animation tracks (60 frames @ 30 fps)
├── <event>.mdf            <-- K2 model definition linking clip and start effects
├── <event>.effect         <-- Visual particle bursts (sparks, white flash, smoke)
├── material.material      <-- Environment reflection shader with deadlock sky cubemap
└── color.tga              <-- 512x512 diffuse gradient texture
```

### 10.2. Screen-Space Coordinate System & Skeletal Hierarchy
Arcade text models exist in K2 UI screen space:
- **X-axis**: Horizontal span ($-1000$ to $+1000$).
- **Z-axis**: Vertical span ($-250$ to $+250$). Text faces front in the $X$-$Z$ plane.
- **Y-axis**: Depth towards/away from camera ($-120$ background to $0$ foreground).

#### Armature Bones:
1. **`Scene Root` (Bone 0)**: Root joint at $(0, 0, 0)$, parent $-1$, identity transform.
2. **`_bone_center` (Bone 1)**: Child of `Scene Root`. Animates entrance slam ($0.02 \to 1.32$), elastic settle ($0.94 \to 1.00$), and floating hover.
3. **`_bone_prop` (Bone 2)**: Child of `_bone_center`. Animates primary thematic prop (e.g., donut wobble, raw steak wobble, spinning Michelin stars).
4. **`_bone_prop2` (Bone 3)**: Child of `_bone_center`. Animates secondary thematic prop (e.g., bouncing chef hat, cleaver chop impact, hovering silver cloche reveal).
5. **`_invis_box` (Bone 4)**: Child of `Scene Root`. Bounds mesh for frustum preservation.

### 10.3. Headless Blender 4.2 Automation Workflow
The pipeline uses headless Blender 4.2 with `io_scene_hon`:
```powershell
# Generate 3D models and animation clips for all pilot cues:
blender -b --python scripts/blender_arcade_text_builder.py -- --out-dir output/arcade_text/ramsay

# Assemble materials, effects, fallbacks, and deploy to VFS:
python scripts/arcade_text_forge.py --pack ramsay

# Execute automated test suite:
python -m unittest tests/hon_model_tools/test_arcade_text_forge.py
```

### 10.4. Meshy 3D Prop Pipeline & Thematic Asset Catalog
The pipeline leverages the Meshy API (`text-to-3d` / `glb` download) to generate high-fidelity PBR textured 3D assets, decimated and scaled for K2 screen-space arcade text cues.

#### Prop Ingestion Standards:
- **Geometry Decimation**: Raw Meshy meshes (80,000–120,000 vertices) are decimated with Blender's `decimate` modifier (ratio 0.25–0.45) to ensure each mesh remains strictly $\le 65,535$ deduplicated vertices (`[HF-019]`).
- **Pivot Normalization**: Mesh origins are automatically centered to their geometric bounds center (`geometry_to_origin`).
- **Screen-Space Scaling**: Assets are scaled to an average bounding radius of 50–90 units to harmoniously frame the 180–300 unit text labels.
- **Texture Conversion**: 2K PNG PBR diffuse maps are converted to uncompressed or DXT1 1024x1024 TGAs.

#### Catalog of 13 Available Culinary Props:

| # | Prop Name | Meshy Task ID | GLB Asset File | Material Name | Assigned Texture | Thematic Role / Choreography |
|---|:---|:---|:---|:---|:---|:---|
| 1 | **Donut** | `01a0a767-708b-70d6-9e29-94e85d30046a` | `donut.glb` | `donut.material` | `donut.tga` | Pilot: `smackdown` primary base prop; elastic squash and wobble. |
| 2 | **Butcher Cleaver** | `01a0a767-7bac-76ee-8870-1688d0effdbb` | `cleaver.glb` | `cleaver.material` | `cleaver.tga` | Pilot: `bloodlust` secondary prop; high chop on frame 8. |
| 3 | **Michelin Star** | `01a0a767-8b2a-7128-9b26-2eed76baa80b` | `michelin_star.glb` | `star.material` | `star.tga` | Pilot: `immortal` primary prop; twin golden spinning stars. |
| 4 | **Chef Hat** | `01a0a768-1108-730b-98f4-66e54b10a87b` | `chef_hat.glb` | `hat.material` | `hat.tga` | Pilot: `smackdown` secondary prop; bounces and lands atop donut. |
| 5 | **Cast Iron Pan** | `01a0a768-1c7b-76ae-b36e-67184559e720` | `frying_pan.glb` | `pan.material` | `pan.tga` | Ready: `humiliation` / `denied` frying pan smack. |
| 6 | **Raw Ribeye Steak** | `01a0a768-277e-714b-9fd0-0d94d62caaf0` | `raw_steak.glb` | `steak.material` | `steak.tga` | Pilot: `bloodlust` primary prop; wobbles on meat block before cleaver strike. |
| 7 | **Glass Salt Shaker** | `01a0a768-35db-705e-9fb3-674fbfac2f74` | `salt_shaker.glb` | `salt.material` | `salt.tga` | Ready: `first_blood` seasoning sprinkle. |
| 8 | **Pepper Grinder** | `01a0a768-4587-7378-81ba-98507cfbbeea` | `pepper_grinder.glb` | `pepper.material` | `pepper.tga` | Ready: `killstreak` twist and grind action. |
| 9 | **Rolling Pin** | `01a0a768-55de-77d5-82e7-39cf57a864e5` | `rolling_pin.glb` | `rolling_pin.material` | `rolling_pin.tga` | Ready: `flawless_victory` rolling flatten motion. |
| 10 | **Silver Cloche** | `01a0a768-6270-7710-af40-e90897132fca` | `silver_cloche.glb` | `cloche.material` | `cloche.tga` | Pilot: `immortal` secondary prop; lifts upward to reveal the star. |
| 11 | **Ripe Tomato** | `01a0a768-6c83-734e-a050-0a67654fc446` | `tomato.glb` | `tomato.material` | `tomato.tga` | Ready: `humiliation` tomato splat impact. |
| 12 | **Culinary Torch** | `01a0a768-765c-74ef-bc98-b27296dd23fa` | `creme_brulee_torch.glb` | `torch.material` | `torch.tga` | Ready: `annihilation` flame burst emitter. |
| 13 | **Chef Santoku Knife** | `01a0a768-8150-751e-8b70-ad8e4cdb0b65` | `chef_knife.glb` | `knife.material` | `knife.tga` | Ready: `hattrick` rapid triple-dice slicing. |

### 10.5. Dual-Tone Beveled Typography & HDR Bloom Shader Architecture
To solve visual blandness and ensure crisp readability across bright and dark map terrains, arcade text models utilize a dual-mesh, dual-material construction:

1. **`TextMesh_Front` (`material.material`)**:
   - Primary extruded text with metallic gold/chrome diffuse gradient (`color.tga`).
   - Reflective cubemap sampling (`deadlock_sky`) for glinting specular sheen during rotational entrances.
   - Positioned slightly in front ($Y = +1.5$ units) with a tighter bevel radius ($0.035$).

2. **`TextMesh_Border` (`border.material`)**:
   - Secondary extruded outline offset slightly behind ($Y = -1.0$ unit) with an enlarged outer bevel ($0.075$).
   - Mapped to `border_color.tga` (dark obsidian core transitioning to molten fiery rim).
   - Configured with overdrive diffuse emission (`vDiffuseColor="3.0000 0.3500 0.0500"`). When values exceed $1.0$, the K2 engine post-processing bloom pass generates an intense volcanic HDR aura encircling the letters.


