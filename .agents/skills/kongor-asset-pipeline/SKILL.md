---
name: kongor-asset-pipeline
description: >
  K2 Engine asset pipeline and validation workflow for 2D/3D textures, normal maps,
  materials, and models. Audits stem-based textures.s2z shadowing (CTextureArchive),
  validates source RGB .tga vs .agb DXT5 _rxgb.dds normal maps, checks POT dimensions,
  and sanitizes alpha borders via scripts/assets/audit_k2_textures.ps1 and scripts/assets/sanitize_alpha_texture.ps1.
  Use when creating, overriding, or debugging K2 textures, UI icons, normal maps, or materials.
capability_contract:
  allowed_worker_roles: ["kongor-offline-worker", "kongor-game-worker"]
  allowed_tools: ["run_command", "view_file", "replace_file_content", "write_to_file"]
  requires_worker_lock: false
  max_probes: 5
---

# K2 Engine Asset Pipeline & Texture QA (`kongor-asset-pipeline`)

Governs texture authoring, normal-map channel layout, `textures.s2z` stem-shadowing prevention, and border-halo sanitization across Project Kongor.

---

## 1. K2 Texture Formats & POT Invariant

`k2_x64.dll` and `vid_d3d9_x64.dll` (`-> d3d9.dll` DXVK v3.0.2) resolve textures through two distinct paths: pre-baked `CTextureArchive` / `CTextureCache` DDS streams and loose `CBitmap::Load` source files.

| Usage | Loose `override/` Format | Archive (`textures.s2z`) Format | Notes |
|---|---|---|---|
| **UI Icons & Elements** | `.png`, `.tga`, or `DXT5` / `RGB32` `.dds` | `DXT5` (`00000000`, `00000009`) or `RGB32` (`00000002`) | Use 8-bit alpha (`DXT5` or `RGBA8`) for smooth anti-aliased UI edges. |
| **Diffuse / Albedo** | `.tga`, `.png`, or `DXT1` / `DXT5` `.dds` | `DXT1` / `DXT5` | Use `DXT1` when opaque; `DXT5` when alpha test or translucency is active. |
| **Tangent Normal Maps** | **Standard RGB `.tga`** (`R=Nx, G=Ny, B=Nz, A=spec`) | **`*_rxgb.dds` (`DXT5`)** + `*_s.dds` (`DXT1`) | Keep loose normal maps as RGB `.tga` referenced by `.material`. |
| **Specular / Team Masks** | `.tga` or `DXT1` / `DXT5` `.dds` | `DXT1` / `DXT5` | K2 extracts 32-bit normal-map alpha into companion `*_s` (`DXT1`) on load. |

- **Power-of-Two (POT) Dimensions:** Author all mipmapped 3D and UI textures with power-of-two dimensions ($w = 2^n, h = 2^m$, e.g. $128\times 128$, $512\times 256$, $2048\times 2048$) so D3D9 mipmap generation succeeds without stretching or hitches.

---

## 2. K2 Normal Map Pipeline (`vid_d3d9_x64.dll + 0x9da30` & `+0x982d0`)

### Rule A — Keep Loose `override/` Normal Maps as Standard Tangent-Space RGB `.tga`
In `D3D_Register2DTexture` (`vid_d3d9_x64.dll + 0x9da30`), when `vid_shaderRXGBNormalmap` is enabled:
1. K2 first queries **`textures.s2z` (`CTextureArchive`, `+0x96040`)** and **`userdata/` (`CTextureCache`, `+0x956c0`)** for `<stem>_rxgb.dds` (and `<stem>_s.dds`). It does **not** probe loose `override/<stem>_rxgb.dds` or `override/<stem>.dds` siblings when `.material` specifies `texture="..._normal.tga"`.
2. On an archive/cache miss, K2 loads the `.material` path via `CBitmap::Load` (`k2_x64.dll + 0x685fd0` -> `CBitmap::LoadTGA`), **swizzles RGB -> RxGB on the CPU** (`FUN_1800982d0` `case 0xf`), extracts 32-bit alpha specular to `_s` (`case 0x10`, `DXT1`), compresses RxGB as `DXT5`, and writes it to `CTextureCache`.

Therefore:
- Export loose normal maps in `games/client/override/` as **standard tangent-space RGB `.tga`** (`Red = Nx ~128`, `Green = Ny ~128`, `Blue = Nz ~245`, `Alpha = specular / 255`) and reference `.tga` in `.material`.
- Pre-swizzling a loose `.tga` (or referencing a pre-swizzled loose `.dds` via `CBitmap::Load`) causes **runtime double-swizzling** (`R=255 -> Alpha=255`, flattening $N_x$ to $+1.0$).

### Rule B — Pre-Baked `*_rxgb.dds` Archive Channel Layout (`.agb`)
K2 HLSL normal shaders (`mesh_color_unit_team_lightmap_spec.psh:168`) sample `tex2D(normalmap_0, In.Texcoord0).agb * 2.0f - 1.0f`. Pre-baked `*_rxgb.dds` entries inside `textures.s2z` use **DXT5** with exact `.agb` packing:

```text
[Source K2 Tangent RGB (.tga)]          [Archive *_rxgb.dds (DXT5 .agb)]
Red   = Nx (~128)                       Red   = 255 (0xFF constant)
Green = +Ny (~128)            ----->    Green = +Ny (6-bit, NOT inverted from K2 .tga)
Blue  = Nz (~245)                       Blue  = Nz  (5-bit, ~245)
Alpha = Specular / 255                  Alpha = Nx  (8-bit interpolated, ~128)
```

- DirectXTex `texconv.exe` swizzle mask for K2 `.agb`: `--swizzle 1gbr -f DXT5` (pass `--invert-y` / `invert_green=True` **only** if your source normal map uses the opposite Y/Green convention from K2's shipped `.tga` files; never pass `--invert-y` when converting an existing K2 `.tga`).

---

## 3. `textures.s2z` Stem-Shadowing (`vid_d3d9_x64.dll + 0x96040` / `+0x4bb80`)

`textures.s2z` contains a `descriptor` mapping 12 CVar-driven quality/mipmap tiers (`00000000`–`00000011`, where `00000000`, `00000001`, `00000002`, and `00000009` are full-resolution `DOWNSIZE='0'` tiers).

`CTextureArchive` (`+0x4bb80`) calls `Filename_StripExtension` on the requested texture path and opens `<tier>/<stem>.dds` (or `<tier>/<stem>_rxgb.dds` for normal maps) **before** `CBitmap::Load` checks `games/client/override/`:
- **Same-extension overrides** (`override/maps/devowars/minimap.dds` vs `00000000/maps/devowars/minimap.dds`) are shadowed whenever their bytes diverge from `textures.s2z`.
- **Cross-extension overrides** (`override/ui/fe2/newui/res/homepage/border_gold.png` vs `00000000/ui/fe2/newui/res/homepage/border_gold.dds`) are **also shadowed** because `CTextureArchive` strips `.png`/`.tga` before probing `textures.s2z`!
- **Byte-identical vanilla copies** in `override/` (matching CRC32 and byte size of `textures.s2z` or `resources*.s2z`) render identically and require no rename.

### Remediation Steps
1. Audit candidate paths before shipping:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/assets/audit_k2_textures.ps1 -Detailed
   ```
2. For any modified file flagged under `Shadowing Hazards`, rename the file to a **versioned basename** (e.g. `border_gold_v2.png` or `tab38_icon.dds`).
3. Update every referencing `.package`, `.interface`, `.material`, or `.entity` attribute to the new basename.

---

## 4. Premultiplied-Alpha & Border Halo Sanitization

Standard bilinear downsampling bleeds transparent black $(0, 0, 0, 0)$ border pixels into semi-transparent icon edges ($16 \le A \le 240$), producing dark halos in-game. Sanitize transparent UI icons before DDS conversion:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/assets/sanitize_alpha_texture.ps1 -CutoutSolidBorder
```

---

## 5. Completion Criteria

Every texture or material change is complete only when all three checks pass:

1. **Texture & Shadowing Audit (`0` new hazards, `0` corrupt headers, `0` pre-swizzled `.tga` normals):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/assets/audit_k2_textures.ps1 -TargetDir games/client/override -Filter <asset_subpath> -Strict
   ```
2. **XML / Material Syntax Lint (`0` broken XML files):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify_xml_syntax.ps1
   ```
3. **In-Engine Visual Proof (Active Match or Authenticated Front-End, never Login Screen):**
   Verify rendered shading and alpha edges live via `scripts/launch_screen_practice.ps1` and `kongor_capture_window`.
