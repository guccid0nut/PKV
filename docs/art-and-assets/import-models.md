# Heroes of Newerth (Project Kongor) — 3D Model & Character Import Pipeline
**Comprehensive Technical Specification & Asset Authoring Guide**

---

## Table of Contents
1. [Executive Summary & Pipeline Architecture](#1-executive-summary--pipeline-architecture)
2. [Complete Asset Checklist & Directory Layout](#2-complete-asset-checklist--directory-layout)
3. [AI Model Generation (Meshy AI & ComfyUI)](#3-ai-model-generation-meshy-ai--comfyui)
4. [Blender Workspace & 43-Bone K2 Armature Standard](#4-blender-workspace--43-bone-k2-armature-standard)
5. [Mesh Geometry & SMDL Export Rules](#5-mesh-geometry--smdl-export-rules)
6. [Texture Pipeline & Direct3D Scanline Inversion](#6-texture-pipeline--direct3d-scanline-inversion)
7. [CLIP Animation Authoring, Retargeting & Physics](#7-clip-animation-authoring-retargeting--physics)
8. [Multi-Sensory Impact System (Audio & Particles)](#8-multi-sensory-impact-system-audio--particles)
9. [Model Definition (`model.mdf`) & Hero Entity XML](#9-model-definition-modelmdf--hero-entity-xml)
10. [Verification, In-Engine Reloading & Troubleshooting](#10-verification-in-engine-reloading--troubleshooting)

---

## 1. Executive Summary & Pipeline Architecture

The **K2 Engine** powering *Heroes of Newerth* uses a proprietary binary asset architecture designed for high-throughput, low-latency deterministic rendering. 

```
[ AI Model Generator ]          [ Blender 5.2+ LTS / 4.2+ ]          [ K2 Engine / Project Kongor ]
 Meshy AI / ComfyUI    ───────►   io_scene_hon Addon        ───────►  SMDL v3 Binary (.model)
 (FBX / GLB / PBR)               - 43-Bone K2 Armature                CLIP v2 Binary (.clip)
                                 - Tangent Orthonormalization         K2 Material XML (.material)
                                 - Direct3D Scanline Inversion        Model Definition (.mdf)
                                 - Chain Physics & Keyframing         Hero Entity XML (.entity)
```

### Core Engine Principles
1. **Coordinate System**: K2 is **Right-Handed Z-Up** ($+X$ = Right, $+Y$ = Forward, $+Z$ = Up). 1 Blender unit = 1 K2 unit ($\approx 1\text{ cm}$). Standard humanoid hero height is $100\text{--}120\text{ units}$.
2. **Binary Formats**:
   - `.model` / `.smdl`: Proprietary chunk-based binary mesh containing vertex streams, normals, orthonormal tangents, UVs, and bone weight influences.
   - `.clip`: Proprietary chunk-based binary skeletal animation containing sampled bone matrix transforms per frame.
3. **Materials**: Declarative XML (`.material`) referencing Direct3D HLSL shaders (`mesh_color_unit_team_lightmap.psh` or `hero_diffuse_normal_mrao_team_emissive.shader`).

---

## 2. Complete Asset Checklist & Directory Layout

Every character or hero avatar requires a structured directory tree under `heroes/<hero_name>/`. The canonical structure must be mirrored across:
- `games/client/override/heroes/<hero_name>/` (development hot-reload)
- `games/client/game/heroes/<hero_name>/` (client distribution)
- `games/shared/heroes/<hero_name>/` (server-client replicated state)

### Master Directory Layout
```
heroes/<hero_name>/
├── hero.entity                      # Hero stats, attack timings, voice bindings, ability slots
├── model.mdf                        # Model definition, anim clips, frameevent audio/fx triggers
├── high.mdf                         # High-detail model definition (usually mirrors model.mdf)
├── preview.mdf                      # Store & hero pick screen preview definition
├── high.model                       # Binary K2 SMDL mesh with 43-bone skeletal skinning
├── hero.material                    # PBR shader definition, samplers, specular/gloss parameters
├── weapon.material                  # Material for attached weapon mesh (if distinct)
├── icon.tga                         # 128x128 or 256x256 uncompressed 32-bit RGBA hero icon
├── color.tga                        # 2048x2048 Diffuse / Albedo map (Direct3D flipped)
├── normal.tga                       # 2048x2048 Tangent-space Normal + Alpha Specular
├── spec_color.tga                   # 2048x2048 Specular Tint / Metalness map
├── glow.tga                         # 2048x2048 Emissive / Lightmap texture
├── team.tga                         # 256x256 Team coloration mask (Red/Green = team colors)
│
├── clips/                           # Binary K2 CLIP animation files
│   ├── default_1.clip               # Primary calm idle animation (breathing loop)
│   ├── idle.clip                    # Mirror of default_1.clip
│   ├── bored_1.clip                 # Extended idle animation (twitching, fidgeting, laughing)
│   ├── walk_1.clip                  # Locomotion cycle (limp, stride, run)
│   ├── attack_1.clip                # Primary melee/ranged auto-attack
│   ├── attack_2.clip                # Secondary alternating attack swing
│   ├── ability_1.clip               # Q ability cast gesture (e.g. chain throw)
│   ├── ability_2.clip               # W ability cast gesture (e.g. stalker crouch)
│   ├── ability_4.clip               # R ultimate cast gesture (e.g. leaping execute)
│   ├── death_1.clip                 # Death collapse animation (loopbackframe="-1")
│   ├── taunt_1.clip                 # Taunt / emote animation (theatrical bow)
│   └── portrait.clip                # Focused head/chest animation for circular HUD portrait
│
├── sounds/                          # Audio assets
│   ├── voice/                       # Voicelines (WAV/MP3 format, 44.1kHz, 16-bit mono/stereo)
│   │   ├── select_1.wav .. 4.wav    # Character selection responses
│   │   ├── move_1.wav .. 4.wav      # Movement order confirmations
│   │   ├── attack_1.wav .. 4.wav    # Attack order confirmations
│   │   ├── hero_select.wav          # Match start announcer introduction line
│   │   ├── taunt_prior.wav          # Pre-kill insult line
│   │   ├── taunt_after.wav          # Post-kill mockery line
│   │   ├── death.wav                # Death vocalization
│   │   ├── no_mana.wav              # "Not enough mana" vocal reminder
│   │   └── cooldown.wav             # "Ability not ready" vocal reminder
│   └── attack_impact.wav            # Heavy metallic weapon strike sound
│
├── effects/                         # K2 visual particle definitions
│   └── attack_impact.effect         # Sparks, crimson blood arc, and hit audio on target
│
└── ability_01/ .. ability_04/       # Hero ability directories
    ├── ability.entity               # Spell mechanics, range, cooldown, mana cost
    ├── affector.entity              # Area-of-effect or projectile payload logic
    ├── icon.tga                     # 128x128 ability icon
    ├── effects/
    │   ├── cast.effect              # Muzzle flash or cast aura on hero
    │   └── impact.effect            # Target collision burst (sparks, chain burst, decal)
    └── sounds/
        ├── cast.wav                 # Ability activation sound
        └── impact.wav               # Ability hit / explosion sound
```

---

## 3. AI Model Generation (Meshy AI & ComfyUI)

When sourcing characters and weapons from generative AI platforms (e.g. **Meshy AI**, **Tripo3D**, or **ComfyUI 3D workflows**), apply these exact settings to guarantee asset compatibility:

### Generation Guidelines
1. **Pose**:
   - Character geometry must be generated in a **standard T-Pose** (arms horizontal, legs straight, facing $+Y$).
   - Never generate in an action pose or A-pose with curled fingers; K2 skinning binds cleanest to orthogonal T-poses.
2. **Mesh Topology & Decimation**:
   - Target vertex count: **15,000 to 35,000 vertices** for hero meshes; **5,000 to 15,000 vertices** for weapon meshes.
   - Decimate or retopologize high-poly raw sculpts ($> 100\text{k}$) before binding to prevent performance drops during 5v5 teamfights.
3. **Texture Atlas Generation**:
   - Request **2048x2048** resolution maps.
   - PBR channels required: Base Color (Diffuse), Tangent Normal, Roughness, Metallic, and Ambient Occlusion.
4. **Animation Exports**:
   - Export individual animation clips at **60 FPS** or **30 FPS** in **FBX 7.4 (2014/2016)** format with embedded skeletal hierarchy.
   - Download separate FBXs for each action (`idle`, `bored`, `walk`, `attack_1`, `attack_2`, `ability_1`, `ability_2`, `ability_4`, `death`, `taunt`).

---

## 4. Blender Workspace & 43-Bone K2 Armature Standard

HoN characters utilize a standardized 43-bone hierarchy derived from 3ds Max Character Studio Biped (`Bip01 ...`). Adhering to these exact names and parent-child linkages is required for camera tracking, weapon binding, health bar rendering, and particle attachment.

### The 43-Bone K2 Hierarchy
```
Scene Root (None)
├── Bip01 (Scene Root)
│   └── Bip01 Pelvis (Bip01)
│       ├── Bip01 Spine (Bip01 Pelvis)
│       │   └── Bip01 Spine1 (Bip01 Spine)
│       │       └── Bip01 Spine2 (Bip01 Spine1)
│       │           ├── Bip01 Neck (Bip01 Spine2)
│       │           │   └── Bip01 Head (Bip01 Neck)
│       │           │       ├── _bone_head (Bip01 Head)
│       │           │       └── _bone_fx_head (Bip01 Head)
│       │           ├── Bip01 L Clavicle (Bip01 Spine2)
│       │           │   └── Bip01 L UpperArm (Bip01 L Clavicle)
│       │           │       └── Bip01 L Forearm (Bip01 L UpperArm)
│       │           │           └── Bip01 L Hand (Bip01 L Forearm)
│       │           │               ├── Bip01 L Finger0 (Bip01 L Hand)
│       │           │               ├── Bip01 L Finger1 (Bip01 L Hand)
│       │           │               ├── Bip01 L Finger2 (Bip01 L Hand)
│       │           │               └── _bone_fx_l_hand (Bip01 L Hand)
│       │           ├── Bip01 R Clavicle (Bip01 Spine2)
│       │           │   └── Bip01 R UpperArm (Bip01 R Clavicle)
│       │           │       └── Bip01 R Forearm (Bip01 R UpperArm)
│       │           │           └── Bip01 R Hand (Bip01 R Forearm)
│       │           │               ├── Bip01 R Finger0 (Bip01 R Hand)
│       │           │               ├── Bip01 R Finger1 (Bip01 R Hand)
│       │           │               ├── Bip01 R Finger2 (Bip01 R Hand)
│       │           │               ├── _bone_fx_r_hand (Bip01 R Hand)
│       │           │               ├── _bone_weapon (Bip01 R Hand)
│       │           │               └── _bone_weaponattach (Bip01 R Hand)
│       │           │                   ├── _bone_weapon1 (_bone_weaponattach)
│       │           │                   ├── _bone_weapon2 (_bone_weaponattach)
│       │           │                   └── _bone_weapon3 (_bone_weaponattach)
│       │           └── _bone_fx_chest (Bip01 Spine2)
│       ├── Bip01 L Thigh (Bip01 Pelvis)
│       │   └── Bip01 L Calf (Bip01 L Thigh)
│       │       └── Bip01 L Foot (Bip01 L Calf)
│       │           └── Bip01 L Toe0 (Bip01 L Foot)
│       ├── Bip01 R Thigh (Bip01 Pelvis)
│       │   └── Bip01 R Calf (Bip01 R Thigh)
│       │       └── Bip01 R Foot (Bip01 R Calf)
│       │           └── Bip01 R Toe0 (Bip01 R Foot)
│       └── _bone_center (Bip01 Pelvis)
├── _bone_origin (Scene Root)
└── _bone_overhead (Scene Root)
```

### Purpose of Key Locator Bones
- **`_bone_overhead`**: Positioned at $Z \approx 130\text{--}150$ directly above the head. Anchors the player health bar, hero name tag, debuff icons, and stun status effects.
- **`_bone_center`**: Positioned at the hero's center of mass ($Z \approx 50\text{--}60$). Target for homing projectiles and aura ground projections.
- **`_bone_origin`**: Ground level $(0, 0, 0)$. Anchors selection rings, shadow projectors, and circular telegraph decals.
- **`_bone_weaponattach`**: Placed at the palm/grip of the dominant hand. Anchors weapon geometry, muzzle effects, and chain links.
- **`_bone_weapon1`, `2`, `3`**: Segmental chain bones extending down the chain or blade to simulate dynamic whip physics.

---

## 5. Mesh Geometry & SMDL Export Rules

The K2 binary SMDL exporter (`io_scene_hon`) requires strict geometric validation before writing vertex streams. Neglecting these rules will result in black artifacts, crushed faces, or texture tearing.

### 1. UV Seam Un-Welding
In Blender, adjacent polygon faces can share a single 3D vertex position while referencing different UV coordinates across texture seams. If exported naively, the vertex will be assigned a single arbitrary UV, creating severe texture distortion across seams.
- **Rule**: Duplicate/un-weld vertices along all UV split seams so every polygon face loop has an isolated vertex reference.

### 2. Tangent-Bitangent-Normal (TBN) Orthonormalization
K2 normal map shaders compute pixel illumination using the TBN transformation matrix:
$$[\vec{T}, \vec{B}, \vec{N}]$$
If the vertex tangent vector $\vec{T}$ is not strictly perpendicular to the vertex normal vector $\vec{N}$ ($\vec{T} \cdot \vec{N} \neq 0$), normal map reflections become distorted, causing the metallic/camouflage discoloration bug (`[ISSUE-113]`).
- **Implementation**: Apply Gram-Schmidt orthogonalization on every vertex loop prior to writing the `tang` chunk:
  $$\vec{T}_{\text{ortho}} = \text{normalize}\left(\vec{T} - (\vec{T} \cdot \vec{N})\vec{N}\right)$$
  Enforce $\vec{T}_{\text{ortho}} \cdot \vec{N} \equiv 0.0$.

### 3. Winding Order & Culling
K2 uses counter-clockwise (CCW) front-face winding and back-face culling by default. In Blender:
- Select all mesh objects $\rightarrow$ Edit Mode $\rightarrow$ Mesh $\rightarrow$ Normals $\rightarrow$ **Recalculate Outside** (`Shift+N`).
- Ensure no negative scaling exists on objects (Apply Transformations: `Ctrl+A` $\rightarrow$ **All Transforms**).

---

## 6. Texture Pipeline & Direct3D Scanline Inversion

Heroes of Newerth executes on Direct3D 9/11 (and modern Vulkan translation layers). Direct3D defines texture coordinates from top-left $(0, 0)$ to bottom-right $(1, 1)$, whereas OpenGL and Blender define $(0, 0)$ at bottom-left.

### The Direct3D Scanline Inversion Equation
When exporting SMDL UV coordinates or baking TGA texture maps, you **must** apply vertical scanline inversion:

$$V_{\text{smdl}} = 1.0 - V_{\text{blender}}$$

```
Blender UV Space (OpenGL)           Direct3D / K2 Texture Memory
(0, 1) +----------------+ (1, 1)    (0, 0) +----------------+ (1, 0)
       |                |                  |                |
       |     HEAD       |                  |     HEAD       |
       |                |     ======>      |                |
       |     FEET       |                  |     FEET       |
       |                |                  |                |
(0, 0) +----------------+ (1, 0)    (0, 1) +----------------+ (1, 1)
```

If $V$ is not inverted, normal maps, diffuse maps, and specular masks will sample upside-down, displaying boots on the face and inverted normal shading.

### Texture File Specifications (Uncompressed 32-bit TGA)
All textures must be uncompressed Truevision TGA (`.tga`) with 8 bits per channel:

| Texture Name | Format | Channels | Channel Packing Specification |
| :--- | :--- | :--- | :--- |
| `color.tga` | 24-bit RGB / 32-bit RGBA | RGB (A=Alpha Test) | Diffuse / Albedo color. Alpha channel holds alpha cutout if used. |
| `normal.tga` | 32-bit RGBA | RGBA | **R**: Tangent $X$, **G**: Tangent $Y$, **B**: Tangent $Z$.<br>**A (CRITICAL)**: Specular intensity multiplier ($0\text{--}255$). Cloth=20, Leather=70, Metal=200. |
| `spec_color.tga` | 24-bit RGB | RGB | Specular highlight tint and metallic reflectivity. |
| `glow.tga` | 24-bit RGB | RGB | Emissive self-illumination mask (pure black where not glowing). |
| `team.tga` | 24-bit RGB | RGB | Team color mask: Red=Primary Team Color, Green=Secondary. |

> [!CAUTION]
> **Never save `normal.tga` without an Alpha channel!** 
> If `normal.tga` lacks an Alpha channel (24-bit RGB), K2 defaults `normalmap.a` to $1.0$ ($255$), causing maximum mirror-like specular blowout across cloth, hair, and skin.

---

## 7. CLIP Animation Authoring, Retargeting & Physics

The K2 engine uses sampled skeletal clips (`.clip`, version 2). Each clip stores rotation, translation, and scale for every bone across $N$ discrete frames.

### 1. Blender Pose Bone Rotation Mode
- **Rule**: All 43 pose bones in `Gwynplaine_Armature` must be explicitly set to `rotation_mode = 'XYZ'` (Euler) or evaluated via pose matrices.
- If bones are set to `QUATERNION` mode while scripts or curves insert into `rotation_euler`, Blender ignores all Euler keyframes, causing the armature to freeze in a static horizontal T-Pose across all frames.

### 2. Retargeting AI FBX Animations
AI platforms (e.g. Meshy AI) generate armatures using `Bip001 ` prefixes instead of standard `Bip01 `. When retargeting:
```python
BONE_REMAP = {
    "Bip001 Pelvis": "Bip01 Pelvis",
    "Bip001 Spine": "Bip01 Spine",
    "Bip001 Spine1": "Bip01 Spine1",
    "Bip001 Spine2": "Bip01 Spine2",
    "Bip001 Neck": "Bip01 Neck",
    "Bip001 Head": "Bip01 Head",
    "Bip001 L Clavicle": "Bip01 L Clavicle",
    "Bip001 L UpperArm": "Bip01 L UpperArm",
    "Bip001 L Forearm": "Bip01 L Forearm",
    "Bip001 L Hand": "Bip01 L Hand",
    "Bip001 R Clavicle": "Bip01 R Clavicle",
    "Bip001 R UpperArm": "Bip01 R UpperArm",
    "Bip001 R Forearm": "Bip01 R Forearm",
    "Bip001 R Hand": "Bip01 R Hand",
    "Bip001 L Thigh": "Bip01 L Thigh",
    "Bip001 L Calf": "Bip01 L Calf",
    "Bip001 L Foot": "Bip01 L Foot",
    "Bip001 L Toe0": "Bip01 L Toe0",
    "Bip001 R Thigh": "Bip01 R Thigh",
    "Bip001 R Calf": "Bip01 R Calf",
    "Bip001 R Foot": "Bip01 R Foot",
    "Bip001 R Toe0": "Bip01 R Toe0",
}
```

### 3. Idle vs Bored Architecture
- **`default_1.clip` (Primary Idle)**:
  - Looped continuously during normal gameplay.
  - Posture: Menacing, relaxed gothic stance. Arms hang naturally down at sides ($Z_{\text{hand}} \approx 65\text{--}70$, holding chain scythe). Subtle rhythmic spine breathing wave ($\pm 2^\circ$).
- **`bored_1.clip` (Boredom Fidget)**:
  - Triggered by K2's internal boredom timer after staying motionless for $10\text{--}15\text{ seconds}$.
  - Posture: Violent rictus grin head-spasms, maniacal laughter body shakes, rapid scythe chain spins.

### 4. Chain Weapon Dynamics
To prevent static rigidity on attached weapon chains:
- Child bones `_bone_weapon1`, `_bone_weapon2`, `_bone_weapon3` inherit transform velocity from `_bone_weaponattach`.
- On auto-attacks and ability casts, whip/chain curves are applied with dynamic drag:
  - **Windup ($t_0 \dots t_{\text{strike}}$)**: Chain lags behind hand movement in an arc.
  - **Impact ($t_{\text{strike}}$)**: Chain snaps straight forward with maximum velocity.
  - **Recovery ($t_{\text{strike}} \dots t_{\text{end}}$)**: Chain overshoots, swings back under gravity, and coils naturally beside the hip.

---

## 8. Multi-Sensory Impact System (Audio & Particles)

In competitive MOBAs, instant audio-visual confirmation of attack and ability impacts is vital. K2 coordinates multi-sensory feedback across three synchronized layers:

```
                  ┌────────────────────────────────────────┐
                  │           Player Casts Attack          │
                  └───────────────────┬────────────────────┘
                                      │
               ┌──────────────────────┴──────────────────────┐
               ▼                                             ▼
     [ Animation Begins ]                          [ Timer Counts ]
      attack_1.clip plays                           attackactiontime="350ms"
               │                                             │
               ▼                                             ▼
   Frame 11 Reached (~350ms) ────────────┬───────────► Strike Time Reached!
                                         │                   │
               ┌─────────────────────────┼───────────────────┘
               ▼                         ▼
      [ Auditory Impact ]       [ Visual Impact ]           [ Mechanics ]
       PlaySoundLinear           attack_impact.effect        Damage Registered
       attack_impact.wav         Sparks + Blood Arc          Enemy Health Drops
```

### 1. Auditory Feedback Configuration
- **Melee Swing / Whoosh**: Triggered via `<frameevent>` in `model.mdf` during the windup frame:
  ```xml
  <frameevent frame="4" cmd="PlaySoundLinear /shared/sounds/attacks/attack_heavy_%.wav 400 1800 0.6"/>
  ```
- **Weapon Impact**: Triggered when the weapon connects with target geometry. Configured via `attackimpacteffect` in `hero.entity` or `<onimpact>` in ability entities.
- **Spell Cast & Impact**:
  - `cast.wav`: Triggered at cast start.
  - `impact.wav`: Triggered at `<onimpact>` on `target_entity`.

### 2. Particle Visuals (`attack_impact.effect`)
An authentic chain scythe impact effect combines:
1. **Directional Slash Ribbon**: A brief (150ms) curved crimson planar mesh or ribbon tracing the scythe blade.
2. **Metallic Sparks**: 15–25 particle billboards with gravity, bounce, and high initial velocity scattering from the contact point.
3. **Flesh/Blood Splatter**: Directional particle burst tinted deep crimson oriented along the strike normal.

---

## 9. Model Definition (`model.mdf`) & Hero Entity XML

### 1. `model.mdf`
The MDF file binds the binary mesh (`high.model`) to animation clips and associates audio/effect events with specific frames:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<model name="rig" file="high.model" type="K2" high="high.model" med="high.model" low="high.model">

	<!-- Idle & Boredom -->
	<anim name="idle" clip="clips/default_1.clip" loop="true" fps="30" blendtime="200"/>
	<anim name="portrait" clip="clips/portrait.clip" loop="true" fps="30"/>
	<anim name="bored_1" clip="clips/bored_1.clip" fps="30"/>
	
	<!-- Taunt -->
	<anim name="taunt_1" clip="clips/taunt_1.clip" fps="30">
		<frameevent frame="1" cmd="PlaySoundLinear sounds/voice/taunt_prior.wav 1000 2500 0.85"/>
	</anim>
	
	<!-- Locomotion -->
	<anim name="walk_1" clip="clips/walk_1.clip" loop="true" fps="30" blendtime="200">
		<frameevent frame="10" cmd="StartEffect /shared/effects/footstep_l_#GetTerrainType()#.effect"/>
		<frameevent frame="22" cmd="StartEffect /shared/effects/footstep_r_#GetTerrainType()#.effect"/>
	</anim>
	
	<!-- Auto-Attacks (Impacts calibrated to frame 11 ~ 350ms) -->
	<anim name="attack_1" clip="clips/attack_1.clip" fps="30">
		<frameevent frame="4" cmd="PlaySoundLinear /shared/sounds/attacks/attack_medium_%.wav 400 1800 0.6"/>
	</anim>
	<anim name="attack_2" clip="clips/attack_2.clip" fps="30">
		<frameevent frame="5" cmd="PlaySoundLinear /shared/sounds/attacks/attack_heavy_%.wav 400 1800 0.6"/>
	</anim>
	
	<!-- Abilities -->
	<anim name="ability_1" clip="clips/ability_1.clip" fps="30">
		<frameevent frame="8" cmd="PlaySoundLinear ability_01/sounds/cast.wav 1200 2500 0.85"/>
	</anim>
	<anim name="ability_2" clip="clips/ability_2.clip" fps="30">
		<frameevent frame="4" cmd="PlaySoundLinear ability_02/sounds/cast.wav 1200 2500 0.85"/>
	</anim>
	<anim name="ability_4" clip="clips/ability_4.clip" fps="30">
		<frameevent frame="6" cmd="PlaySoundLinear ability_04/sounds/cast.wav 1500 2800 0.95"/>
	</anim>
	
	<!-- Death -->
	<anim name="death_1" clip="clips/death_1.clip" loop="true" loopbackframe="-1" fps="30">
		<frameevent frame="1" cmd="PlaySoundLinear sounds/voice/death.wav 1200 2500 0.85"/>
	</anim>

</model>
```

### 2. `hero.entity` Key Timing Parameters
```xml
<!-- Melee Auto-Attack Synchronization -->
attackduration="800"
attackactiontime="350"
attackcooldown="1700"
attackrange="150"
attacktype="melee"
attackimpacteffect="effects/attack_impact.effect"
```
- `attackactiontime="350"`: The exact duration in milliseconds from animation start until damage registers and `attackimpacteffect` spawns. At 30 FPS, $350\text{ms} \approx \text{Frame } 11$.
- `attackduration="800"`: Total length of the attack swing before the hero can perform another queued command without animation canceling.

---

## 10. Verification, In-Engine Reloading & Troubleshooting

### In-Engine Hot-Reload Commands
While running the HoN client (`hon_x64.exe`), you can instantly reload assets from the in-game console (`Ctrl+F8` or via `kongor_game_command`):
- `reloadmodel <model_path>`: Hot-reloads the SMDL geometry and MDF file (e.g. `reloadmodel /heroes/gwynplaine/model.mdf`).
- `reloadmaterial <material_path>`: Recompiles HLSL shaders and re-reads TGA textures without restarting the game.
- `reloadentity <entity_name>`: Reloads entity stats, voice sound paths, and attack timings.

### Common Issues & Diagnostic Matrix

| Issue / Symptom | Root Cause Diagnosis | Remediation Protocol |
| :--- | :--- | :--- |
| **Faceted camouflage/purple geometric patterns on mesh** | Non-orthogonal vertex tangents ($\vec{T} \cdot \vec{N} \neq 0$) or inverted Direct3D UV coordinates. | 1. Re-export model using Gram-Schmidt tangent orthonormalization.<br>2. Invert texture scanlines ($V_{\text{smdl}} = 1.0 - V_{\text{blender}}$).<br>3. Purge texture cache in `userdataKONGOR/extensions/texturecache`. |
| **Character frozen in horizontal T-Pose** | Blender pose bones configured with `rotation_mode = 'QUATERNION'` while Euler keyframes were exported, causing static bind pose evaluation. | Enforce `rotation_mode = 'XYZ'` across all 43 pose bones before animation evaluation, or bake action curves directly to pose matrices. |
| **Shiny plastic / mirror-like specular glare** | Missing Alpha channel in `normal.tga`. K2 defaults missing alpha to 255 (maximum reflectivity). | Save `normal.tga` as 32-bit RGBA with specular luminance packed in Alpha (cloth=20, leather=70, metal=200). |
| **No sound or sparks when hitting enemies** | `attackimpacteffect` is blank (`""`) in `hero.entity`, or impact WAV files are missing from disk. | Assign `attackimpacteffect="effects/attack_impact.effect"` and ensure 16-bit uncompressed PCM WAV files are deployed to `sounds/`. |
| **Health bar floating in the wrong place** | `_bone_overhead` is missing or improperly positioned in the armature. | Ensure `_bone_overhead` exists as a child of `Scene Root` positioned at $Z \approx 140\text{ units}$. |
| **Hero facing backwards or sideways** | Armature forward axis mismatch ($+Y$ vs $+X$ or $-Y$). | Ensure character mesh faces $+Y$ with eyes looking forward and feet aligned along $X$-axis in Blender rest pose. |
| **Fallback Teapot / Pink Geometry / Access Violation Crash** | Archive (`.s2z`) exceeds signed 32-bit limit (2.14 GB) or uses Zip64 headers, causing signed `int32_t` offset overflow in `k2_x64.dll`. | **Enforce 1.5 GB safe ceiling (`allowZip64 = False`):** Keep all `.s2z` archives under 1.5 GB. Never shadow base assets; only pack modified/forge delta files. Partition into multiple archives if necessary. |
| **White squares / missing hero & ability icons** | Byte `0x11` of 32-bit `icon.tga` is `0x00` instead of `0x08`, causing K2's strict TGA parser to reject the file as invalid. | Patch byte `0x11` of the TGA header to `0x08` (8 attribute bits for alpha channel). |

---

## 11. Archive Packaging & The 1.5 GB Safe Ceiling (`.s2z`)

> [!CAUTION]
> **CRITICAL ARCHITECTURAL CONSTRAINT: SIGNED 32-BIT ZIP LIMIT (1.5 GB SAFE CEILING)**  
> The K2 Engine's archive subsystem (`CArchive` / `CFileManager` in `k2_x64.dll`) uses signed 32-bit file offsets (`int32_t`, overflowing at $2\,\text{GiB} = 2,147,483,647\,\text{bytes}$) and does not support Zip64 (`allowZip64 = False`).  
> **Project Kongor enforces a strict $1.5\,\text{GB}$ maximum size per `.s2z` archive.**

### 11.1 The Signed 32-Bit Overflow Failure Mode
When an archive exceeds 2.14 GB or writes Zip64 central directory records:
1. Any file positioned past $2.14\,\text{GB}$ triggers signed 32-bit integer overflow in `k2_x64.dll`.
2. When the engine attempts to read that asset, it seeks to an invalid offset and reads arbitrary bytes.
3. **MDF Files:** Fail to parse $\to$ K2 falls back to rendering the default debug **teapot**.
4. **SMDL / MOX Geometry:** Corrupted vertex/index headers $\to$ **`EXCEPTION_ACCESS_VIOLATION (0xC0000005)`** crash.
5. **Entity Registry:** Entity lookups hang during gameplay (`game_shared_x64!CEntityRegistry::LookupID`), freezing the main thread.
6. **Shadowing Hazard:** Packaging unmodified base game assets into `resources99.s2z` bloats the archive past the 32-bit threshold.

### 11.2 Mandatory Asset Packaging Standards
All archive injection scripts (`scripts/vfs/inject_override_to_s2z.ps1`, mod tools, and CI pipelines) strictly enforce:

1. **Delta-Only Packing (Zero Base Shadowing):**  
   Never pack unmodified base game assets into override/custom archives. Only inject files that are uniquely created or modified (Forge heroes, custom textures, new sounds, modified Lua/XML). Let base archives (`resources0.s2z` .. `resources5.s2z`, `ClientPackageFiles.s2z`) serve base assets.
2. **Safe Size Ceiling ($\le 1.5\,\text{GB}$, `allowZip64 = False`):**  
   Keep every `.s2z` archive strictly under **1.5 GB** with `allowZip64 = False`.
3. **Multi-Archive Partitioning:**  
   If total custom assets approach 1.5 GB, split them into sequentially numbered archives (e.g. `resources98.s2z`, `resources99.s2z`).
4. **CI/Script Validation Gate:**  
   Scripts assert archive size and reject archives exceeding `1.5GB`:
   ```powershell
   if ((Get-Item $archPath).Length -ge 1.5GB) {
       throw "FATAL: $archPath exceeds 1.5 GB safe limit! K2 signed 32-bit zip reader will overflow at 2.14 GB. Aborting."
   }
   ```

