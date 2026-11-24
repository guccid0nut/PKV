# Hero Forge Engine: Technical Specification & Process Manual

**Subsystem**: Phase 4 ("The Living Nexus") — Autonomous Content Creation Pipeline  
**Module**: `scripts/hero_forge.py`  
**Status**: Production / Active  
**Last Updated**: 2026-09-14  

---

## 1. Executive Summary & Architectural Overview

The **Hero Forge Engine** (`scripts/hero_forge.py`) is the autonomous, AI-accelerated hero and avatar generation pipeline for Project Kongor. It bridges creative intent (lore, archetype, combat profile, aesthetics) and strict K2 engine binary specifications into a unified, transactional, zero-defect content workflow.

### Core Capabilities
1. **Dual Generation Modes**:
   - **`--mode hero`**: Generates a completely new, original hero from five creative prompts, calibrating bounding boxes, attack action ratios, blend times, and locomotion speeds against the empirical monorepo dataset in `src/tools/hon-model-tools/hero_archetype_matrix.json`.
   - **`--mode alt`**: Generates a streamlined Alt Avatar targeting an existing base hero, reusing core gameplay logic while generating custom visual geometry, materials, textures, animations, voice lines, and store catalog bindings.
2. **Transactional Staging Sandbox (`output/staging/<id>/`)**:
   - Isolates all generation work in a sandbox guarded by **4 pre-commit validation gateways**.
   - Zero unvalidated assets touch active game directories.
   - Enforces atomic VFS promotion to `games/shared/` and `games/client/` with clean rollback (`--rollback`) support.
3. **5 Parametric Ability XML Templates**:
   - Pre-validated Abstract Syntax Tree (AST) structures for `SkillshotProjectile`, `TargetedStunNuke`, `PointTargetAoe`, `PassiveProcOrAura`, and `VectorDashOrMobility`.
4. **Store 2.0 & Catalog Synchronization**:
   - Single-step atomic synchronization across `StoreItemsConfiguration.json`, `Upgrades.JSON`, `store_avatars.package` (across 8 mirror locations), `heroes.xml` (3 mirrors), `heroes.txt` (2 mirrors), `entities_en.str` (4 mirrors), and `interface_en.str` (3 mirrors).
5. **Invariant R0 (Zero-Mutation Baseline)**:
   - All 277 existing hero entities and 469 MDF files in `games/shared/heroes/` and `games/client/` are strictly **read-only reference baselines**. The pipeline guarantees 0 byte mutation on existing assets.
6. **Invariant R6 (Archive Size Hard Ceiling — Max 1.5 GB)**:
   - Due to K2's signed 32-bit ZIP offset arithmetic (`int32_t`, overflowing at 2.14 GB in `k2_x64.dll`) and `allowZip64 = False` requirement, all `.s2z` resource archives must strictly remain **under 1.5 GB**. Any archive exceeding 2.14 GB suffers signed 32-bit integer overflow, causing teapots, corrupt geometry, and fatal access violations. Never shadow base game assets into custom archives.

### Related Content Pipelines
- **Announcer Forge**: [`announcer-forge.md`](./announcer-forge.md) — Specification and process manual for 48 kHz stereo announcer voice packs, automated silence-valley trimming, dual soundtables, and Store 2.0 integration.
- **Hero Animation Standards**: [`hero-animation-standards.md`](./hero-animation-standards.md) — Empirical calibration matrix extracted from all 277 heroes and 469 MDFs.

---

## 2. Transactional Staging Sandbox Architecture

To prevent corrupt XML, out-of-spec audio, or broken animations from ever entering the live virtual file system (VFS), `hero_forge.py` executes all asset synthesis within an isolated staging sandbox:

```
output/staging/staging_hero_<clean_id>_<timestamp>/
├── manifest.json                        <-- Metadata, catalog mappings, staged file hashes
├── validation_report.json               <-- Results of 4 pre-commit gateways
├── backup/                              <-- Pre-promotion snapshots of all catalog files
│   ├── StoreItemsConfiguration.json.bak
│   ├── Upgrades.JSON.bak
│   ├── store_avatars.package.bak
│   ├── heroes.xml.bak
│   ├── heroes.txt.bak
│   ├── entities_en.str.bak
│   └── interface_en.str.bak
├── shared/
│   └── heroes/
│       └── <hero_id>/
│           ├── hero.entity              <-- Primary entity definition
│           ├── model.mdf                <-- K2 model definition & frameevents
│           ├── preview.mdf              <-- UI store preview definition
│           ├── high.model / hero.model  <-- Rigged 43-bone SMDL geometry
│           ├── color.tga                <-- 32-bit RGBA diffuse texture
│           ├── normal.tga               <-- Tangent-space RxGB normal map
│           ├── spec_color.tga           <-- Specular & glossiness map
│           ├── glow.tga                 <-- Emissive glow map
│           ├── icon.tga                 <-- 512x512 hero portrait icon
│           ├── materials/
│           │   ├── hero.material
│           │   └── weapon.material
│           ├── clips/                   <-- 13 binary K2 .clip animations
│           │   ├── idle.clip
│           │   ├── walk_1.clip
│           │   ├── attack_1.clip
│           │   ├── attack_2.clip
│           │   ├── ability_1.clip
│           │   ├── ability_2.clip
│           │   ├── ability_3.clip
│           │   ├── ability_4.clip
│           │   ├── death_1.clip
│           │   ├── portrait.clip
│           │   ├── default_1.clip
│           │   ├── bored_1.clip
│           │   └── taunt_1.clip
│           ├── sounds/
│           │   ├── voice/               <-- Standard 20-line voice pack (48 kHz 16-bit PCM)
│           │   │   ├── select.wav       <-- 2-channel Stereo (UI / store greeting)
│           │   │   ├── spawn.wav        <-- 1-channel Mono (3D in-world)
│           │   │   ├── move_1..5.wav    <-- 1-channel Mono (3D in-world)
│           │   │   ├── attack_1..4.wav  <-- 1-channel Mono (3D in-world)
│           │   │   ├── ability_1..4.wav <-- 1-channel Mono (3D in-world)
│           │   │   ├── kill_1..2.wav    <-- 1-channel Mono (3D in-world)
│           │   │   ├── respawn.wav      <-- 1-channel Mono (3D in-world)
│           │   │   ├── taunt.wav        <-- 1-channel Mono (3D in-world)
│           │   │   └── death.wav        <-- 1-channel Mono (3D in-world)
│           │   └── ability SFX (.wav)
│           ├── effects/                 <-- Swipe, aura, impact particle systems
│           ├── ability_01/              <-- Q ability entity, projectile, effect, sounds
│           ├── ability_02/              <-- W ability entity, affector/dash, effect, sounds
│           ├── ability_03/              <-- E ability entity, states, effect, sounds
│           └── ability_04/              <-- R ultimate ability entity, effect, sounds
└── client/
    ├── game/heroes/<hero_id>/           <-- Client game VFS mirror
    └── override/heroes/<hero_id>/       <-- Client override VFS mirror
```

---

## 3. The 4 Pre-Commit Validation Gateways

Before promoting staged assets to `games/shared/` and `games/client/`, the staging sandbox must pass **all four pre-commit validation gateways with 0 errors**. If any gateway fails, atomic promotion is aborted and the staging directory is cleaned up.

### Gateway 1: XML Schema Validation (`validate_gateway_1_xml`)
- **Parser**: Full DOM syntax scan via `xml.etree.ElementTree`.
- **Target Extensions**: `.entity`, `.mdf`, `.material`, `.xml`, `.package`, `.effect`.
- **Invariants**:
  1. **Well-Formed XML**: Zero parsing syntax errors, properly nested and closed tags, valid entity escapes (`&amp;`, `&lt;`, `&gt;`).
  2. **Valid Root Tags**: Root element must belong to the K2 engine vocabulary: `hero`, `ability`, `projectile`, `affector`, `state`, `model`, `material`, `heroes`, `template`, `package`, `effect`.
  3. **Mandatory Attributes**:
     - `<hero>`: `name`, `primaryattribute`, `attacktype`, `movespeed`, `attackduration`, `attackactiontime`, `boundsheight`, `boundsradius`, `inventory0`.
     - `<ability>`: `name`, `actiontype`, `targetscheme`, `icon`, `casttime`, `castactiontime`, `manacost`, `cooldowntime`, `maxlevel`.
     - `<model>`: `name`, `file`, `type="K2"`.
  4. **Frameevent Syntax**: All `<frameevent frame="..." cmd="...">` tags in `.mdf` must contain integer frame indexes and valid engine command strings (`PlaySoundLinear`, `StartEffect`, etc.).
  5. **Clean UTF-8 Encoding**: Every file must decode cleanly without BOM or non-UTF-8 bytes.

### Gateway 2: 48 kHz Audio Validation (`validate_gateway_2_audio`)
- **Parser**: Direct binary RIFF header parsing and sample verification via `scripts/dsp_audio_fallback.py verify_wav_compliance`.
- **Invariants**:
  1. **Header Integrity**: Bytes 0..3 must be `RIFF`, bytes 8..11 must be `WAVE`.
  2. **Format Tag**: Must be `1` (WAVE_FORMAT_PCM, uncompressed PCM).
  3. **Sample Rate**: Must be exactly **`48,000 Hz`**.
  4. **Bit Depth**: Must be exactly **`16-bit signed integer`**.
  5. **Strict Spatial Channel Discipline**:
     - **In-World 3D Spatial Sounds** (`spawn.wav`, `move_*.wav`, `attack_*.wav`, `ability_*.wav`, `death.wav`, `taunt.wav`, ability SFX): **Strictly Mono (1 channel)** to enable OpenAL Soft HRTF 3D positional panning.
     - **UI & Store Preview Sounds** (`select.wav`, announcer cues): **Stereo (2 channels)** or Mono.
  6. **Signal Quality**: Non-empty signal (samples > 2,400, duration > 0.05s), zero NaN/Inf values, no full-scale digital clipping over 10 consecutive samples.

### Gateway 3: Weapon Posture & Cyclic Loop Closure (`validate_gateway_3_weapon_and_clips`)
- **Parser**: Binary K2 `.clip` reader (`io_scene_hon.clip_parser.read_clip`) cross-referenced with `model.mdf`.
- **Invariants**:
  1. **Continuous $C^1$ Cyclic Boundary Closure**:
     - On looping animation tracks (`idle.clip`, `walk_1.clip`, `portrait.clip`, `default_1.clip`):
       $$\Delta R = \max_{b \in \text{bones}} \left( \|\mathbf{rot}_{\text{end}}(b) - \mathbf{rot}_{0}(b)\| \right) < 0.01^\circ$$
     - Continuous velocity smoothing over the final 6 frames into frame 0 eliminates loop seam "hitches".
  2. **Euler Rotation Angle Clamping**:
     - All Euler angles across all bones must stay strictly within $[-180.0^\circ, +180.0^\circ]$, eliminating 360° gimbal drift.
  3. **Weapon Socket Anchor Grip**:
     - Attachment bone `_bone_weapon` must be present.
     - On `idle.clip`, the weapon bone's local translation variance must be $< 0.001$ units (firmly locked grip, zero weapon jitter).
  4. **Attack Action Point Ratio**:
     - On action clips (`attack_1`, `attack_2`):
       $$\text{Ratio} = \frac{\text{ActionTime}}{\text{Duration}} \approx 0.42 \quad (0.38 \le \text{Ratio} \le 0.46)$$

### Gateway 4: Tangent-Space RxGB Normal Map Check (`validate_gateway_4_normal_map`)
- **Parser**: Raw pixel array analysis via PIL and NumPy.
- **Invariants**:
  1. **Power-of-Two Dimensions**: Width and height must be powers of two (512x512, 1024x1024, 2048x2048).
  2. **Tangent-Space RxGB Packing**:
     - Alpha Channel ($A$) contains Tangent $N_x$: $N_x = \frac{A}{255.0} \times 2.0 - 1.0$.
     - Green Channel ($G$) contains Tangent $N_y$: $N_y = \frac{G}{255.0} \times 2.0 - 1.0$ (Direct3D $Y^-$ inverted).
     - Blue Channel ($B$) is fixed at 255.
     - Red Channel ($R$) stores reconstructed $N_z$ or 255.
  3. **Unit Vector Magnitude Constraint**:
     $$N_x^2 + N_y^2 \le 1.05$$
     Ensures that reconstructed $N_z = \sqrt{\max(0, 1 - N_x^2 - N_y^2)}$ is strictly real-valued.
  4. **Perceptual Anomaly Audit**:
     - Zero pure magenta missing-texture pixels (`#FF00FF` / $R=255, G=0, B=255$).
     - Zero completely untextured pure white geometry fallback (`#FFFFFF` across all pixels).

---

## 4. Parametric Ability XML Templates (5 Archetypes)

The Hero Forge pipeline codifies 5 fundamental gameplay archetypes into parametric Abstract Syntax Trees (ASTs):

| Archetype | Action Type | Target Scheme | Key Mechanics | Entity Files |
|---|---|---|---|---|
| **`SkillshotProjectile`** | `target_position` | `enemy_units` | Linear moving skillshot projectile, touch collision affector, magical damage, stun | `ability.entity`, `projectile.entity`, `cast.effect`, `trail.effect`, `impact.effect` |
| **`TargetedStunNuke`** | `target_entity` | `enemy_units` | Point-and-click single-target burst nuke, Superior Magic damage, execute stun | `ability.entity`, `cast.effect`, `impact.effect` |
| **`PointTargetAoe`** | `target_position` | `enemy_units` | Ground-targeted reticle, delayed affector burst, area damage, area stun | `ability.entity`, `affector.entity`, `aoe.effect`, `impact.effect` |
| **`PassiveProcOrAura`** | `passive` | `enemy_units` | Passive attribute amplification, basic attack on-hit armor shred debuff, self move/attack speed surge | `ability.entity`, `state_debuff.entity`, `state_buff.entity`, `impact.effect` |
| **`VectorDashOrMobility`** | `target_position` | `enemy_units` | High-speed directional dash, unbind, disjoint projectiles, collision damage and slow | `ability.entity`, `projectile_dash.entity`, `state_dash.entity`, `trail.effect`, `impact.effect` |

---

## 5. Store 2.0 Catalog & Stringtable Synchronization

When a hero or alt avatar passes all 4 gateways and promotes, `hero_forge.py` atomically updates all master-server and client catalogs:

### Catalog Continuation Sequence
- **Next Store Product ID**: **`6012`** (Gwynplaine was 6010, Crimson Grin was 6011).
- **Next Upgrades UpgradeId**: **`"6012"`** (synchronized with Store Product ID).
- **Next store_avatars heroEntryID**: **`1308`** (Gwynplaine was 1306, Crimson Grin was 1307).

### Synchronized Files
1. `src/services/master-server/Configuration/Store/StoreItemsConfiguration.json`
2. `src/services/master-server/Data/Seed/Upgrades.JSON`
3. `games/shared/content/store_avatars.package` (+ 7 mirror locations across client and override)
4. `games/shared/heroes.xml` (+ 2 mirror locations)
5. `games/shared/heroes.txt` (+ mirror location)
6. `games/shared/stringtables/entities_en.str` (+ 3 mirror locations)
7. `games/shared/stringtables/interface_en.str` (+ 2 mirror locations)

---

## 6. CLI Command Reference & Modes

### 6.1 Headless Original Hero Generation (`--mode hero`)
```powershell
python scripts/hero_forge.py --mode hero `
    --hero-id vespera `
    --display-name "Vespera, the Dusk Warden" `
    --primary-stat agility `
    --combat-type melee `
    --archetype carry `
    --weapon-archetype 1h_blades_axes `
    --abilities SkillshotProjectile,VectorDashOrMobility,PassiveProcOrAura,TargetedStunNuke `
    --lore "A nightblade assassin wielding shadowy daggers." `
    --offline
```

### 6.2 Streamlined Alt Avatar Generation (`--mode alt`)
```powershell
python scripts/hero_forge.py --mode alt `
    --base-hero pyromancer `
    --alt-id alt10 `
    --display-name "Cryomancer" `
    --weapon-archetype staves_polearms `
    --lore "A frostbitten incarnation of Pyromancer." `
    --offline
```

### 6.3 Pre-Commit Validation Sweep (`--dry-run`)
```powershell
python scripts/hero_forge.py --mode hero --hero-id vespera --dry-run
```
*Executes all 4 gateways without modifying active VFS or master catalogs.*

### 6.4 Atomic Rollback (`--rollback`)
```powershell
python scripts/hero_forge.py --rollback staging_hero_vespera_20260914_055207
# or by hero name:
python scripts/hero_forge.py --rollback vespera
```
*Restores pre-promotion catalog snapshots from `backup/` and removes all deployed VFS assets.*

### 6.5 Interactive Terminal Questionnaire (`--interactive`)
```powershell
python scripts/hero_forge.py --interactive
```
*Interactive prompts for the 5 creative prompts with verified defaults.*

---

## 7. Living Troubleshooting Log ([HF-001] - [HF-019])

The following living issue log catalogs known failure modes, engine quirks, and preventive remediations:

| Issue ID | Subsystem | Failure Symptom | Root Cause | Preventive Remediation |
|---|---|---|---|---|
| **[HF-001]** | Gateway 1 / XML | K2 map load crash `XML parse error in hero.entity` or `Missing mandatory attribute`. | Missing required attributes (`boundsheight`, `inventory0`) or unescaped XML ampersand in lore string. | Gateway 1 asserts well-formed XML and validates mandatory schema attributes prior to promotion. |
| **[HF-002]** | Gateway 2 / Audio | In-game sound effect plays at center-screen regardless of hero 3D world position; HRTF fails. | Sound file encoded as 2-channel Stereo instead of 1-channel Mono. OpenAL Soft collapses multi-channel audio to ambient stereo. | Gateway 2 enforces strict Mono (1 channel) on all in-world sounds; Stereo is permitted exclusively for `select.wav` and UI. |
| **[HF-003]** | Gateway 3 / Animation | Character model hitches or jerks once per second during `idle` or `walk_1` locomotion loops. | Discontinuous boundary seam between frame $N-1$ and frame 0 ($\Delta R \ge 0.01^\circ$ or velocity step discontinuity). | Gateway 3 enforces continuous $C^1$ cubic Hermite smoothing (`enforce_cyclic_boundary`) over the final 6 frames into frame 0. |
| **[HF-004]** | Gateway 4 / Normal Map | Model renders with black lighting artifacts or lighting seams under sunlight. | Tangent-space RxGB normal vector magnitude blowout ($N_x^2 + N_y^2 > 1.05$), causing imaginary $N_z$ square root in K2 pixel shader. | Gateway 4 audits all pixels ensuring $N_x^2 + N_y^2 \le 1.05$ and validates Direct3D $Y^-$ inversion. |
| **[HF-005]** | Catalogs / Store | Newly added hero fails to appear in in-game Store or Master Server reports foreign key upgrade error. | UpgradeId in `Upgrades.JSON` desynchronized from Store Product ID in `StoreItemsConfiguration.json`. | Atomic catalog synchronization locks ID calculation: Store Product ID and UpgradeId share the exact same integer ID ($\ge 6012$). |
| **[HF-006]** | Invariant R0 | Base hero default appearance or default animations mutated after deploying an Alt Avatar. | Script modified `games/shared/heroes/<base>/hero.entity` directly instead of confining alt assets to `alt/<skin>/`. | Strict directory isolation enforced: in `--mode alt`, `hero.entity` is strictly read-only; new `<altavatar>` tags are inserted only into `heroes.xml`. |
| **[HF-007]** | Gateway 3 / Gameplay | Auto-attack animation deals damage too early or too late relative to swing visual contact. | Attack action point ratio $\text{ActionTime} / \text{Duration}$ deviated from historical 0.42 standard. | Gateway 3 asserts action point ratio stays strictly within $0.38 \le \text{Ratio} \le 0.46$. |
| **[HF-008]** | Gateway 4 / Textures | Character renders as glowing bright magenta in game client or UI preview. | Missing texture asset replaced by engine fallback `#FF00FF` ($R=255, G=0, B=255$). | Gateway 4 scans all texture pixel arrays ensuring zero pure magenta `#FF00FF` pixels exist. |
| **[HF-009]** | Staging / Rollback | Interrupted atomic promotion leaves orphaned files in `games/shared/heroes/`. | Process killed during VFS file copy step before catalog synchronization finished. | Transactional staging captures backup snapshots in `output/staging/<id>/backup/`; `execute_rollback` restores all original files and prunes empty dirs. |
| **[HF-010]** | Armatures / Non-Humanoid | Alt avatar mesh collapses into spastic polygon soup or T-poses when animated on non-humanoid creatures (e.g. Bubbles' 85-bone turtle shell rig). | Pipeline defaulted to humanoid 43-bone biped armature instead of binding to creature's native bone hierarchy. | Inherited Armature Binding extracts native bone hierarchy from base hero's `high.model`; K2 maps animation channels by string name, guaranteeing 100% mechanical fidelity across all native animations. |
| **[HF-011]** | Audio / ElevenLabs MCP | Voice line generation fails with `You need to be on the creator tier or above to use this voice.` | Library voice was selected that requires ElevenLabs Creator tier. | Pipeline maps hero archetypes to workspace-accessible premade voices (e.g., `Callum` for monsters/tricksters, `Harry` for warriors, `George` for casters) with zero tier lockouts. |
| **[HF-012]** | Visuals / Meshy MCP | Meshy normal maps render inverted lighting or flat surfaces in K2. | Meshy outputs OpenGL $Y^+$ tangent-space normals; K2 pixel shaders expect Direct3D $Y^-$ tangent-space RxGB. | Pipeline automatically applies Direct3D $Y^-$ inversion and packages components into RxGB ($R=\text{reconstructed } N_z, G=N_y, B=255, A=N_x$). |
| **[HF-013]** | 3D Mesh / Texture Mapping | Alt avatar model renders with distorted white patches and incorrect silhouette matching default hero. | Pasted generative 2D textures onto base hero's default model (`high.model`) with mismatched topology and non-square 1:2 UV unwrapping. | Enforce full 3D asset generation via Meshy (`meshy_text_to_3d` + `meshy_text_to_3d_refine`) for unique avatar geometry and matching UV coordinates. Never paste arbitrary textures onto mismatched base meshes. |
| **[HF-014]** | Visual QA / Blender Staging | Broken mesh hierarchy or distorted animations discovered only after complex in-game deployment. | Lack of visual inspection gateway before VFS promotion. | Mandate Blender staging: import Meshy asset, rig, and animation actions into Blender (`blender` or `$env:BLENDER_EXE`), render visual confirmation stills (`blender_render_frame`), and save the master project to `output/blender/<id>.blend` for permanent access. Then export via `io_scene_hon` to native K2 formats (`.model`, `.clip`, `.mdf`). |
| **[HF-015]** | Client / Cache Coherence | In-game client continues to display old or corrupted textures/icons even after new files are written to VFS. | K2 engine aggressively caches compiled textures in VRAM and `userdataKONGOR/extensions/texturecache/`. | Always clear client texture cache directories and restart or reload the client before running visual verification sweeps. |
| **[HF-016]** | Blender Rig / Bone Starburst | Blender project opens as a massive starburst ball of giant spiky bones obscuring the character model. | Synthetic GLTF/FBX bone imports generate huge bone tail lengths (1,100–2,600+ units) at 0.01 scale in `glTF_not_exported`. In Blender's default octahedral display mode, bone diamonds expand to colossal size over the 135-unit mesh. | Enforce `armature.data.display_type = 'STICK'` or `'WIRE'`, and set realistic bone lengths ($10 \le L \le 30$ units) scaled proportionally to parent-child offsets during pipeline import. |
| **[HF-017]** | In-Game Mesh / Explosion & Root Joint | In-game hero pick screen or Caldavar fountain displays model exploded into a spidery polygon web. | Threefold coordinate mismatch: (1) Unapplied object transforms (`Armature.scale = 0.01`, +90° X rotation from FBX); (2) Missing `Scene Root` identity joint at Bone 0 (Bone 0 was `Hips` with parent -1), causing coordinate desync with K2 engine; (3) Action fcurves with object-level `scale` tracks animating the armature back to 0.01 on frame updates. | Enforce strict `(1.0, 1.0, 1.0)` transform application in Blender (`bpy.ops.object.transform_apply()`), insert `Scene Root` at origin $(0, 0, 0)$ as Bone 0 with parent -1, parent `Hips` to `Scene Root`, and strip non-pose object-level transform fcurves (`not fc.data_path.startswith('pose.bones')`) across all imported animation actions. |
| **[HF-018]** | Model Exporter / Pitch Inversion | Primary child bones of `Scene Root` (`Hips`) have pitch/roll inverted or Y/Z swapped during K2 model/clip export. | `scene_exporter.py` executed `pbone.parent.matrix.inverted() @ matrix` on direct children of `Scene Root`. Because Blender's edit bone `Scene Root` basis has an internal roll offset relative to HoN world coordinates, inverting it rotated child coordinate frames. | In `scene_exporter.py`, exempt direct children of `Scene Root` (`if pbone.parent and pbone.parent.name != "Scene Root":`), preserving exact HoN world translations and Euler rotations for the primary root bone. |
| **[HF-019]** | Model Exporter / 16-Bit Index Limit | In-game hero model explodes into giant spiderweb radiating from origin (0, 0, 0) on pick screen or fountain. | K2 engine Direct3D 9 model loader in `k2_x64.dll` strictly expects 16-bit index buffers (`D3DFMT_INDEX16`, `face_type = 2`). Meshes with > 65,535 loop vertices force 32-bit indices (`face_type = 4`), which K2 truncates to 16-bit words. Upper words collapse to `0x0000` (Vertex 0 / Scene Root), collapsing faces across the world. | Pipeline (`smdl_writer.py`, `scene_exporter.py`) logs error `[HF-019]` if vertices exceed 65,535. The alt avatar builder (`build_abyssal_bubbles.py`, `hero_forge.py`) automatically applies a Blender Decimate modifier (ratio 0.15 for high-poly FBX/GLTF > 10,000 faces) to guarantee vertex count $\le 65,535$ before SMDL export. |
| **[HF-020]** | Visual FX / Glow & Bone Coordinate Inversion | Staff crystal or weapon particle billboard floats disconnected in air or shifts into hero torso/neck when adjusted. | For `<particlesystem space="entity">`, billboards attached to a bone (`bone="RightHand"`) add `position="X Y Z"` in bone local space. In non-neutral poses, bone rotation matrices invert screen projection ($\partial X_{screen}/\partial X_{bone} \approx -3.5\text{ px/unit}$). Moving negative in X pulled glow inwards toward torso instead of outwards to weapon. | Mandate multi-angle differential triangulation (front, 180° back, 90° side) with 20px grid overlays to solve bone-to-screen gradients. Apply multi-tier billboard setup: small deep core glow (`size="32-36"`, high color intensity, `depthbias="-15"` to render through cage prongs without occlusion clipping), soft outer bloom (`size="65-75"`), dynamic ambient light (`<light falloffend="115-125">`), and synchronized motion ribbon emitters. |
| **[HF-021]** | Shaders / Team Tint Mask Calibration | Character in-game and store preview renders covered in a solid turquoise/cyan or team-colored wash, hiding textures. | K2 hero shader multiplies team color against `team.tga`. If `team.tga` contains non-zero luminance or white noise across the mesh, the engine paints the entire body with team color. | Ensure `team.tga` is solid `#000000` (black) for all non-tinted surfaces. Non-zero values are permitted exclusively on designated team accent geometry. |
| **[HF-022]** | Textures / Direct3D Scanline & Tangent RxGB Normal Inversion | Meshy/Blender textures render upside down, with inverted lighting or inverted normal seams under K2 shaders. | OpenGL and generative PBR pipelines (Meshy) place UV origin at bottom-left; K2 Direct3D shaders expect top-left origin with inverted Green channel tangent normals. | Pipeline applies Direct3D $Y^-$ scanline inversion on normal maps during baking, encoding tangent components into K2 RxGB ($R=\text{reconstructed } N_z, G=-N_y, B=255, A=N_x$) with unit magnitude constraint $N_x^2 + N_y^2 \le 1.05$. |
| **[HF-023]** | Weapon & Equipment / Questionnaire & Meshy Generation | New heroes lack weapons or have weapons incorrectly positioned, unrigged, or missing swing trail effects. | Pipeline did not explicitly query weapon intent during hero generation or provide automated generative 3D asset workflows for weapons. | Incorporate Step 3 weapon questionnaire (`has_weapon`, `weapon_archetype`). Support 6 weapon archetypes. Generate weapon 3D mesh via Meshy MCP (`meshy_text_to_3d` + `refine`). Socket-parent weapon to `_bone_weapon` / `_bone_weaponattach` in Blender, apply Hermite cyclic smoothing to eliminate loop seam jitter, and hook `<trailemitter>` ribbons in `.mdf`. |
| **[HF-024]** | Audio / Voice Line Repetition & Full Interaction Matrix | Player only hears 2 alternating voice lines ("The pressure rises" and "Swimming through the deep") during hero movement. | Pipeline used `STANDARD_FALLBACK_REUSE` where `move_3..5` duplicated `move_1..2` and `attack_3..4` duplicated `attack_1..2`. K2 randomly picks among `move_%.wav`, resulting in repetitive dialogue. | Enforce a complete 24+ unique line script matrix across movement (`move_1..5.wav`), attack (`attack_1..4.wav`), selection (`select_1..4.wav`), flavor (`select_flavour_1..2.wav`), status (`no_mana.wav`, `cooldown.wav`), and taunts (`taunt_prior.wav`, `taunt_after.wav`). Synthesize via ElevenLabs MCP (`creative_generate_speech`) with archetype-matched voices and transcode to 48 kHz 16-bit PCM (Mono 3D / Stereo UI). |

---

## 8. Verification & Compliance Checklist

To verify the Hero Forge pipeline in any CI or local developer environment:
1. **Zero-Mutation Check (R0)**:
   ```powershell
   git status --porcelain games/
   ```
   *Must report 0 modified files.*
2. **Dry-Run Validation Sweep**:
   ```powershell
   python scripts/hero_forge.py --mode hero --hero-id vespera --dry-run
   python scripts/hero_forge.py --mode alt --base-hero pyromancer --alt-id alt10 --dry-run
   ```
   *Must report: All 4 Pre-Commit Gateways PASSED (100% OK).*
3. **Automated Unit Tests**:
   ```powershell
   python -m unittest tests/hon_model_tools/test_hero_forge.py
   ```
   *Must pass 100% of test cases.*

---

## 9. Weapon & Equipment Architecture: Questionnaire, Meshy MCP & Socket Binding

Every hero or Alt Avatar created through the Hero Forge pipeline must pass through the **Equipment Determination Gateway**:

### 9.1 Interactive & Headless Questionnaire
During Step 3 of avatar generation, the pipeline prompts:
1. **Weapon Presence (`has_weapon: bool`)**: "Does this hero or avatar wield a weapon? (y/n)"
2. **Archetype Selection (`weapon_archetype`)**:
   - `staves_polearms`: Two-handed or single-handed staves, polearms, spears, tridents (e.g. Abyssal Bubbles' trident staff).
   - `1h_blades_axes`: One-handed swords, daggers, axes, warhammers.
   - `dual_wielding`: Paired weapons bound across both hands.
   - `bows_launchers`: Ranged bows, crossbows, cannons.
   - `chains_whips`: Multi-joint segmented physics weapons.
   - `unarmed`: Gauntlets, claws, hand-to-hand combatants.
3. **Generative Mesh Synthesis**: "Generate custom 3D weapon model via Meshy MCP? (y/n)"

### 9.2 Meshy MCP Generative 3D Asset Protocol
When a custom weapon is requested, the pipeline drives Meshy MCP:
1. **Text-to-3D Synthesis (`meshy_text_to_3d`)**:
   - Generates high-fidelity 3D mesh candidates from descriptive prompt (e.g. `"Deep-sea trident staff with bioluminescent cyan orb cage, coral shaft, barnacles, ornate obsidian prongs, fantasy game asset, low poly game ready"`).
2. **Text-to-3D Refinement (`meshy_text_to_3d_refine`)**:
   - Enhances geometric detail and generates high-resolution PBR texture maps (Base Color, Roughness, Metallic, Normal).
3. **Asset Ingestion & Staging**:
   - Downloads `.glb` / `.fbx` and stages into `output/staging/<id>/shared/heroes/<hero>/weapon/`.

### 9.3 Blender Socket Binding & Cyclic Boundary Smoothing
1. **Socket Anchoring**:
   - The weapon mesh is imported into the master Blender project (`output/blender/<id>.blend`).
   - Sockets `_bone_weapon` (primary grip in `RightHand`) and `_bone_weaponattach` (secondary grip in `LeftHand`) are positioned at the exact hand contact center.
   - Transforms are frozen (`scale = (1.0, 1.0, 1.0)`, rotation applied).
2. **Continuous $C^1$ Hermite Smoothing**:
   - Weapon physics solver (`weapon_physics_solver.py`) applies cubic Hermite interpolation over the final 6 frames into frame 0 on looping animation tracks (`idle.clip`, `walk_1.clip`, `run_1.clip`).
   - Guarantees translation variance $< 0.001$ units on `idle.clip`, eliminating weapon jitter at loop seams.
3. **MDF Frameevent & Trail Ribbon Hooks**:
   - `model.mdf` declares weapon swing effects on action frames:
     ```xml
     <anim name="attack_1" clip="clips/attack_1.clip" fps="30">
         <frameevent frame="9" cmd="StartEffect effects/attack_swipe.effect"/>
     </anim>
     ```
   - Effects anchor `<trailemitter>` ribbons (`arrow_trail.material`) directly to `_bone_weapon` or `RightHand`.

---

## 10. Visual FX & Glow Calibration Protocol: Local Bone Space Triangulation

Attaching bioluminescent crystal glows, flaming blade enchantments, or magical orbs to skeletal joints requires strict adherence to K2 bone coordinate behavior:

### 10.1 The Bone Coordinate Space Inversion Trap
- In K2 particle systems (`space="entity"`), a `<billboard>` or `<light>` attached to a bone (`bone="RightHand"`) computes its `position="X Y Z"` in that bone's **local rotated frame**, NOT in world space.
- In resting or animation poses (such as `idle.clip` frame 0), hand/arm bones are frequently rotated relative to the camera.
- On character limbs (e.g. `RightHand`), the bone's local $X$ axis often maps negatively to screen horizontal coordinates:
  $$\frac{\partial X_{\text{screen}}}{\partial X_{\text{bone}}} \approx -3.5 \text{ px/unit}$$
- **Trap**: Decreasing $X$ (making it negative) moves the particle effect *inward* toward the torso/neck rather than *outward* toward the weapon head!

### 10.2 Multi-Angle Differential Triangulation Protocol
To deterministically locate particle billboards without guess-and-check:
1. **Multi-Angle Visual Captures**:
   - **Front View**: Calibrates screen horizontal offset ($\Delta X$) and vertical elevation ($\Delta Z$).
   - **Back View (180° Rotation)**: Inverts horizontal screen displacement ($\Delta X_{\text{back}} \approx -\Delta X_{\text{front}}$). If the sign inverts symmetrically, the displacement is purely along the primary bone lateral axis; any residual asymmetry indicates an orthogonal depth offset ($\Delta Y$).
   - **Side View (90° Rotation)**: Directly exposes the depth plane ($\Delta Y$) perpendicular to the camera.
2. **Pixel Coordinate Grid Calibration**:
   - Overlay a 20-pixel coordinate grid over the target attachment (e.g. staff trident cage orb).
   - Measure target center $(x_{\text{target}}, y_{\text{target}})$ and current particle core center $(x_{\text{glow}}, y_{\text{glow}})$.
   - Measure the displacement gradient $\frac{\Delta \text{screen}}{\Delta \text{bone}}$ across two distinct test coordinates.
   - Solve for the exact bone delta:
     $$\Delta X_{\text{bone}} = \frac{x_{\text{target}} - x_{\text{glow}}}{\partial X_{\text{screen}} / \partial X_{\text{bone}}}$$

### 10.3 Multi-Tier Billboard & Dynamic Illumination Architecture
Every weapon crystal or magical core effect must implement the 4-tier illumination stack:
```xml
<!-- Tier 1: Deep Core Glow (Pierces geometry, high luminance) -->
<billboard
    bone="RightHand"
    material="/shared/effects/materials/whitedot.material"
    startcolor="0.0 0.40 0.60"
    midcolor="0.0 0.65 0.95"
    endcolor="0.0 0.40 0.60"
    life="2000"
    loop="true"
    size="36"
    depthbias="-15"
    position="15.5 1.0 44.5"
/>

<!-- Tier 2: Soft Outer Bloom (Atmospheric halo) -->
<billboard
    bone="RightHand"
    material="/shared/effects/materials/whitedot.material"
    startcolor="0.0 0.18 0.30"
    midcolor="0.0 0.32 0.50"
    endcolor="0.0 0.18 0.30"
    life="2400"
    loop="true"
    size="72"
    depthbias="-15"
    position="15.5 1.0 44.5"
/>

<!-- Tier 3: Bioluminescent Ambient Light (In-world surface illumination) -->
<light
    bone="RightHand"
    color="0.0 0.40 0.60"
    falloffend="125"
    position="15.5 1.0 44.5"
/>

<!-- Tier 4: Locomotion Motion Ribbon Trails & Sparkle Motes (World Space) -->
<particlesystem name="system1" space="world" scale="1.0">
    <trailemitter
        bone="RightHand"
        position="15.5 1.0 44.5"
        spawnrate="50"
        particlelife="350"
        material="/shared/effects/materials/arrow_trail.material"
    />
</particlesystem>
```
- **`depthbias="-15"` Rule**: Pulls the billboard forward in depth-buffer testing, allowing the crystal glow to shine through cage prongs, weapon housings, or sockets without ugly geometry clipping or occlusion pop-in.

---

## 11. Texture & Shader Standards: Direct3D Inversion, RxGB Normals & Team Tint Masks

### 11.1 Direct3D $Y^-$ Scanline Inversion
- **Convention Clash**: OpenGL and Blender textures place UV origin $(0, 0)$ at the bottom-left corner. Direct3D 9/11 expects origin $(0, 0)$ at the top-left corner.
- **Symptom**: Exporting standard OpenGL normal maps causes character surfaces to render with inverted lighting (sunlight from above illuminates lower surfaces; concave creases appear convex).
- **Remediation**: The Hero Forge texture pipeline automatically applies vertical flip / $Y^-$ scanline inversion to all normal maps prior to K2 engine packaging.

### 11.2 Tangent-Space RxGB Normal Map Architecture
K2 shaders do not read standard RGB normal maps. They require compressed **RxGB format**:
- **Alpha Channel ($A$)**: Contains Tangent $N_x$ (Red channel of standard normal).
- **Green Channel ($G$)**: Contains Tangent $N_y$ (Green channel of standard normal, with Direct3D $Y^-$ inversion: $-N_y$).
- **Blue Channel ($B$)**: Fixed at `255`.
- **Red Channel ($R$)**: Stores reconstructed $N_z$ or `255`.
- **Unit Magnitude Assertion**:
  $$N_x^2 + N_y^2 \le 1.05$$
  Reconstructed normal component $N_z = \sqrt{\max(0, 1 - N_x^2 - N_y^2)}$ must evaluate to a real number; exceeding 1.05 results in black pixel artifacts under sunlight.

### 11.3 Team Tint Mask Calibration (`team.tga`)
- **Shader Behavior**: The K2 hero shader multiplies player team color (Legion blue/cyan, Hellbourne red/orange) across pixels where `team.tga` has non-zero luminance.
- **The Solid Color Wash Bug**: If `team.tga` contains any diffuse texture bake, ambient occlusion, or non-zero grayscale noise, the engine paints the entire character mesh in a solid uniform turquoise/cyan wash, completely obscuring custom skin, scales, clothing, or carapace.
- **Golden Rule**: `team.tga` must be **solid `#000000` (black)** across all non-tinted areas. Only designated team-color accents (flags, ribbons, sash trims) may contain non-zero mask values.

---

## 12. Character Voice Pack Architecture: ElevenLabs MCP & 24-Line Script Matrix

### 12.1 The Repetition Trap & Root Cause Diagnosis
In early pipeline iterations, `STANDARD_FALLBACK_REUSE` mapped multiple movement and attack slots to only two lines:
```python
# Outdated Fallback Matrix (DO NOT USE)
STANDARD_FALLBACK_REUSE = {
    "move_3": "move_1",   # Swimming through the deep
    "move_4": "move_2",   # The pressure rises
    "move_5": "move_1",   # Swimming through the deep
    "attack_3": "attack_1",
    "attack_4": "attack_2",
}
```
Because the K2 engine randomly samples from `move_%.wav` on every right-click move order, having only 2 unique recordings meant the player was subjected to relentless repetition of only two spoken phrases.

### 12.2 The Complete 24-Line Character Voice Matrix
Every Hero Forge voice pack must author **24 distinct, unique audio recordings**:

| Audio File | Channel | Trigger Context | Example Theme (Deep-Sea / Abyssal) |
|---|---|---|---|
| **`spawn.wav`** | Mono | Match start spawn | *"The dark trenches call, and I answer."* |
| **`respawn.wav`** | Mono | Fountain resurgence | *"You cannot drown what belongs to the deep!"* |
| **`move_1.wav`** | Mono | Move order 1 | *"Swimming through the deep."* |
| **`move_2.wav`** | Mono | Move order 2 | *"The pressure rises."* |
| **`move_3.wav`** | Mono | Move order 3 | *"Drifting with the undercurrent."* |
| **`move_4.wav`** | Mono | Move order 4 | *"No wave can stay my path."* |
| **`move_5.wav`** | Mono | Move order 5 | *"Gliding through black waters."* |
| **`attack_1.wav`** | Mono | Attack order 1 | *"Crush beneath the trench!"* |
| **`attack_2.wav`** | Mono | Attack order 2 | *"Feel the oceanic pressure!"* |
| **`attack_3.wav`** | Mono | Attack order 3 | *"Drown in the deep!"* |
| **`attack_4.wav`** | Mono | Attack order 4 | *"Broken against the reef!"* |
| **`select_1.wav`** | Mono | Hero selection 1 | *"From the deep abyss, I arise!"* |
| **`select_2.wav`** | Mono | Hero selection 2 | *"The depths have eyes everywhere."* |
| **`select_3.wav`** | Mono | Hero selection 3 | *"My shell withstands all pressure."* |
| **`select_4.wav`** | Mono | Hero selection 4 | *"Speak, surface dweller."* |
| **`select_flavour_1.wav`** | Mono | Repeated clicking 1 | *"Keep prodding me and you'll end up at the bottom of the Mariana Trench!"* |
| **`select_flavour_2.wav`** | Mono | Repeated clicking 2 | *"The crushing dark is my sanctuary... not yours."* |
| **`ability_1.wav`** | Mono | Q Cast | *"Into the depths!"* |
| **`ability_2.wav`** | Mono | W Cast | *"The undertow takes you!"* |
| **`ability_3.wav`** | Mono | E Cast | *"Retreat into the shell!"* |
| **`ability_4.wav`** | Mono | R Climax | *"Welcome to the crushing abyss!"* |
| **`no_mana.wav`** | Mono | Out of Mana | *"My arcane tides have run dry."* |
| **`cooldown.wav`** | Mono | On Cooldown | *"The currents must realign."* |
| **`kill_1.wav`** | Mono | Hero Kill 1 | *"Dragged down to the benthic zone."* |
| **`kill_2.wav`** | Mono | Hero Kill 2 | *"Consumed by the leviathans."* |
| **`death.wav`** | Mono | Hero Death | *"Returning... to the darkness..."* |
| **`taunt_prior.wav`** | Mono | Taunt initiation | *"You cannot survive the crushing pressure down here!"* |
| **`taunt_after.wav`** | Mono | Taunt execution | *"Nothing but flotsam on the ocean floor!"* |
| **`select.wav`** | Stereo | Store / Pick greeting | Master greeting formatted as 2-channel Stereo. |

### 12.3 ElevenLabs MCP Integration & Audio Standards
1. **Archetype Voice Selection (`creative_list_voices`)**:
   - Queries ElevenLabs MCP for character voices matching hero lore:
     - `ancient_turtle` / `elder_creature`: Ancient Monster (`wXvR48IpOq9HACltTmt7`) — deep, gravelly, slow, guttural 57.5 Hz F0 resonance.
     - `creature` / `monster`: Ancient Monster (`wXvR48IpOq9HACltTmt7`) or Callum (`N2lVS1w4EtoT3dr4eOWO`).
     - `warrior` / `bruiser`: Harry (`SOYHLrjzK2X1ezoPC6cr`).
     - `tank`: Roger (`CwhRBWXzGAHq8TQ4Fs17`).
     - `carry`: Charlie (`IKne3meq5aSn9XLyUdCD`).
     - `mage` / `caster`: George (`JBFqnCBsd6RMkjVDRZzb`).
     - `support`: River (`SAz9YHcvj6GT2YYXdXww`).
2. **Speech Generation (`creative_generate_speech`) & Concurrency Guardrails**:
   - Synthesizes each unique line from the 28-30 line script matrix using `model_id: "eleven_multilingual_v2"` or `eleven_turbo_v2_5`.
   - **Subscription Tier Limit (`[HF-011]`)**: Unlocked premade workspace voices avoid HTTP 401/403 Creator Tier restrictions.
   - **Rate Limiting**: ElevenLabs limits parallel synthesis to **maximum 2 concurrent requests**. Exceeding 2 triggers `429 Too many concurrent requests`. Batch generation must enforce semaphore or serial throttling.
3. **Strict Audio Engineering Standards**:
   - **Sampling Rate**: Exactly **`48,000 Hz`**.
   - **Format**: **`16-bit PCM RIFF WAVE`** (`WAVE_FORMAT_PCM`).
   - **Channel Discipline**:
     - **Mono (1 Channel)** for all in-world 3D spatial sounds (enables OpenAL Soft HRTF positional audio).
     - **Stereo (2 Channels)** for `select.wav` and UI announcer cues.
   - **Headroom**: Normalized to $-1\text{ dBFS}$ peak amplitude ($0.89$ scaling) to eliminate digital clipping.

### 12.4 K2 Entity Sound Hooks & Wildcard Matching
In `hero.entity`, configure standard attributes with the `%` wildcard:
```xml
<altavatar key="Hero_Bubbles.Alt16"
    selectedsound="alt16/sounds/voice/select_%.wav"
    selectedflavorsound="alt16/sounds/voice/select_flavour_%.wav"
    confirmmovesound="alt16/sounds/voice/move_%.wav"
    confirmattacksound="alt16/sounds/voice/attack_%.wav"
    nomanasound="alt16/sounds/voice/no_mana.wav"
    cooldownsound="alt16/sounds/voice/cooldown.wav"
    tauntedsound="alt16/sounds/voice/taunt_prior.wav"
    tauntkillsound="alt16/sounds/voice/taunt_after.wav"
/>
```
The `%` wildcard allows the K2 engine to deterministically enumerate all indexed files on disk (`move_1.wav`, `move_2.wav`, `move_3.wav`, `move_4.wav`, `move_5.wav`) and randomly sample across them with equal probability on every unit order, guaranteeing varied, immersive gameplay dialogue.
