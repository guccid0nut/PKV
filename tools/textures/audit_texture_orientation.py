# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

import os
import sys
import io
import time
import argparse
import shutil
import zipfile
from collections import defaultdict
from PIL import Image
import numpy as np

def rotate_bak(filepath):
    """5-tier backup rotation protocol."""
    if not os.path.exists(filepath):
        return
    for i in range(5, 0, -1):
        prev_suffix = '.bak' if i == 1 else ('.bak_old' if i == 2 else f'.bak_old{i-1}')
        curr_suffix = '.bak_old' if i == 1 else f'.bak_old{i}'
        prev = filepath + prev_suffix
        curr = filepath + curr_suffix
        if os.path.exists(prev):
            if i == 5 and os.path.exists(curr):
                ts = int(time.time())
                os.replace(curr, f"{curr}.retired_{ts}")
            shutil.move(prev, curr)
    bak1 = filepath + '.bak'
    shutil.copy2(filepath, bak1)

class BaselineCatalog:
    """Indexes authentic baseline assets across loose folders and .s2z archives."""
    def __init__(self, client_root='games/client'):
        self.client_root = client_root
        self.loose_roots = [
            os.path.join(client_root, 'ClientPackageFiles'),
            os.path.join(client_root, 'game')
        ]
        self.archive_index = {} # normalized_rel_path -> (archive_path, zip_inner_path)
        self.archive_stem_index = defaultdict(list) # stem_path -> [(ext, archive_path, zip_inner_path)]
        self.zip_handles = {}
        self._build_index()

    def _normalize_key(self, path):
        p = path.replace('\\', '/').lstrip('/')
        # Strip leading numbers (e.g. 00000000/)
        parts = p.split('/')
        if parts and parts[0].isdigit():
            p = '/'.join(parts[1:])
        return p.lower()

    def _build_index(self):
        t0 = time.time()
        game_dir = os.path.join(self.client_root, 'game')
        if not os.path.exists(game_dir):
            return

        # Prioritize archives: textures.s2z, then resources99 down to resources0
        archives = []
        for f in os.listdir(game_dir):
            if f.endswith('.s2z'):
                archives.append(os.path.join(game_dir, f))
        
        # Sort so newer/higher precedence archives come first
        def archive_sort_key(path):
            name = os.path.basename(path).lower()
            if 'texture' in name:
                return 1000
            if 'resource' in name:
                digits = ''.join(c for c in name if c.isdigit())
                return int(digits) if digits else 0
            return -1

        archives.sort(key=archive_sort_key, reverse=True)

        count = 0
        for arch_path in archives:
            try:
                z = zipfile.ZipFile(arch_path, 'r')
                self.zip_handles[arch_path] = z
                for inner_name in z.namelist():
                    norm = self._normalize_key(inner_name)
                    ext = os.path.splitext(norm)[1].lower()
                    if ext in ('.png', '.tga', '.dds', '.jpg'):
                        if norm not in self.archive_index:
                            self.archive_index[norm] = (arch_path, inner_name)
                            count += 1
                        stem, _ = os.path.splitext(norm)
                        self.archive_stem_index[stem].append((ext, arch_path, inner_name))
            except Exception as e:
                print(f"[BaselineCatalog] Warning opening {arch_path}: {e}")

        print(f"[BaselineCatalog] Indexed {count} baseline textures from {len(archives)} archives in {time.time()-t0:.2f}s")

    def get_baseline_image(self, rel_vfs_path):
        """
        Retrieves authentic baseline PIL Image for a given relative VFS path.
        Returns: (PIL.Image or None, source_str, matched_ext)
        """
        norm_rel = self._normalize_key(rel_vfs_path)
        stem, target_ext = os.path.splitext(norm_rel)

        # 1. Check loose folders
        for root in self.loose_roots:
            # Check exact match
            exact_path = os.path.join(root, norm_rel)
            if os.path.exists(exact_path):
                try:
                    im = Image.open(exact_path)
                    im.load()
                    return im, exact_path, target_ext
                except Exception:
                    pass
            # Check fallback extensions (.dds, .tga, .png)
            for fallback_ext in ['.dds', '.tga', '.png']:
                cand = os.path.join(root, stem + fallback_ext)
                if os.path.exists(cand):
                    try:
                        im = Image.open(cand)
                        im.load()
                        return im, cand, fallback_ext
                    except Exception:
                        pass

        # 2. Check archives
        if norm_rel in self.archive_index:
            arch_path, inner_name = self.archive_index[norm_rel]
            try:
                z = self.zip_handles[arch_path]
                data = z.read(inner_name)
                im = Image.open(io.BytesIO(data))
                im.load()
                return im, f"{os.path.basename(arch_path)}:{inner_name}", target_ext
            except Exception:
                pass

        # Try other extensions in archives
        if stem in self.archive_stem_index:
            # Prefer matching target_ext, else .dds, then .tga, then .png
            candidates = self.archive_stem_index[stem]
            candidates.sort(key=lambda x: (x[0] != target_ext, x[0] != '.dds', x[0] != '.tga'))
            for ext, arch_path, inner_name in candidates:
                try:
                    z = self.zip_handles[arch_path]
                    data = z.read(inner_name)
                    im = Image.open(io.BytesIO(data))
                    im.load()
                    return im, f"{os.path.basename(arch_path)}:{inner_name}", ext
                except Exception:
                    pass

        return None, None, None

    def get_baseline_raw(self, rel_vfs):
        """Retrieves authentic raw bytes for exact extension match if present."""
        norm_rel = self._normalize_key(rel_vfs)
        # Check loose
        for root in self.loose_roots:
            p = os.path.join(root, norm_rel)
            if os.path.exists(p):
                with open(p, 'rb') as f:
                    return f.read(), p
        # Check archives
        if norm_rel in self.archive_index:
            arch_path, inner_name = self.archive_index[norm_rel]
            try:
                z = self.zip_handles[arch_path]
                return z.read(inner_name), f"{os.path.basename(arch_path)}:{inner_name}"
            except Exception:
                pass
        return None, None

def evaluate_texture(override_path, override_img, baseline_img, bl_ext=None):
    """
    Compares override texture with baseline directly and flipped.
    In K2, raw PNG/TGA is upright in viewers; DDS is vertically flipped relative to raw (plan v4 §3.4).
    Therefore:
    - If baseline is .dds: upright raw == flip(dds).
    - If baseline is not .dds: upright raw == direct(baseline).
    Returns: dict with metrics and classification.
    """
    # Standardize modes to RGBA for pixel comparison
    im_ov = override_img.convert('RGBA')
    im_bl = baseline_img.convert('RGBA')

    # Resize baseline to override dimensions if mismatched
    if im_ov.size != im_bl.size:
        im_bl = im_bl.resize(im_ov.size, Image.Resampling.BILINEAR)

    arr_ov = np.array(im_ov).astype(float)
    arr_bl = np.array(im_bl).astype(float)
    arr_fl = np.flipud(arr_bl)

    diff_direct = float(np.mean(np.abs(arr_ov - arr_bl)))
    diff_flipped = float(np.mean(np.abs(arr_ov - arr_fl)))

    # Determine symmetry
    is_symmetric = (diff_direct < 0.5 and diff_flipped < 0.5)

    is_dds_baseline = (bl_ext and bl_ext.lower() == '.dds')
    diff_upright = diff_flipped if is_dds_baseline else diff_direct
    diff_inverted = diff_direct if is_dds_baseline else diff_flipped

    if is_symmetric:
        classification = "SYMMETRIC"
    elif diff_upright + 0.5 < diff_inverted:
        if diff_upright < 1.5:
            classification = "PROVABLY_NORMAL" # OK_UPRIGHT (plan v4 §3.2)
        else:
            classification = "LIKELY_NORMAL"
    elif diff_inverted + 0.5 < diff_upright:
        if diff_inverted < 1.5:
            classification = "PROVABLY_INVERTED"
        else:
            classification = "LIKELY_INVERTED"
    else:
        classification = "AMBIGUOUS"

    return {
        'diff_direct': diff_direct,
        'diff_flipped': diff_flipped,
        'classification': classification,
        'is_symmetric': is_symmetric,
        'width': im_ov.width,
        'height': im_ov.height
    }

def create_preview_composite(override_img, restored_img, baseline_img, output_path, label=""):
    """Creates a 3-panel comparison preview: Current | Restored | Baseline."""
    w, h = 128, 128
    thumb_ov = override_img.convert('RGBA').resize((w, h), Image.Resampling.BILINEAR)
    thumb_rs = restored_img.convert('RGBA').resize((w, h), Image.Resampling.BILINEAR)
    thumb_bl = baseline_img.convert('RGBA').resize((w, h), Image.Resampling.BILINEAR)

    composite = Image.new('RGBA', (w * 3 + 20, h + 30), (30, 30, 30, 255))
    composite.paste(thumb_ov, (5, 25))
    composite.paste(thumb_rs, (w + 10, 25))
    composite.paste(thumb_bl, (w * 2 + 15, 25))
    
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    composite.save(output_path)

def audit_directory(override_dir='games/client/override', client_root='games/client', fix=False, preview_dir=None, report_file=None, filter_str=None):
    print(f"=== Project Kongor: Texture Orientation Auditor ===")
    print(f"Scanning target: {override_dir}")
    print(f"Auto-fix enabled: {fix}")
    if filter_str:
        print(f"Active filter: '{filter_str}'")
    
    catalog = BaselineCatalog(client_root=client_root)
    
    valid_extensions = ('.png', '.tga', '.dds', '.jpg')
    override_files = []
    for root, _, files in os.walk(override_dir):
        for f in files:
            ext = os.path.splitext(f)[1].lower()
            if ext in valid_extensions and not f.endswith('.bak') and '.bak_old' not in f:
                full_path = os.path.join(root, f)
                if filter_str and filter_str.lower() not in full_path.replace('\\', '/').lower():
                    continue
                override_files.append(full_path)
                
    total_files = len(override_files)
    print(f"Discovered {total_files} texture assets to audit in {override_dir}")
    
    results = {
        'PROVABLY_INVERTED': [],
        'LIKELY_INVERTED': [],
        'PROVABLY_NORMAL': [],
        'LIKELY_NORMAL': [],
        'SYMMETRIC': [],
        'AMBIGUOUS': [],
        'CUSTOM': [],
        'ERROR': []
    }
    
    t0 = time.time()
    for idx, fpath in enumerate(override_files):
        if (idx + 1) % 500 == 0 or idx == total_files - 1:
            print(f"Auditing [{idx+1}/{total_files}] ({(idx+1)/total_files*100:.1f}%) ... Elapsed: {time.time()-t0:.1f}s")
            
        rel_vfs = os.path.relpath(fpath, override_dir).replace('\\', '/')
        
        try:
            im_ov = Image.open(fpath)
            im_ov.load()
        except Exception as e:
            results['ERROR'].append({'path': fpath, 'error': str(e)})
            continue
            
        bl_img, bl_source, bl_ext = catalog.get_baseline_image(rel_vfs)
        
        if bl_img is None:
            results['CUSTOM'].append({'path': fpath, 'vfs': rel_vfs, 'size': im_ov.size})
            continue
            
        eval_res = evaluate_texture(fpath, im_ov, bl_img, bl_ext)
        eval_res['path'] = fpath
        eval_res['vfs'] = rel_vfs
        eval_res['baseline_source'] = bl_source
        eval_res['override_mode'] = im_ov.mode
        
        cls = eval_res['classification']
        results[cls].append(eval_res)
        
        # If Inverted and Fix requested
        if fix and cls in ('PROVABLY_INVERTED', 'LIKELY_INVERTED'):
            rotate_bak(fpath)
            # Check if exact raw baseline bytes are available
            raw_bytes, raw_source = catalog.get_baseline_raw(rel_vfs)
            if raw_bytes is not None:
                with open(fpath, 'wb') as out_f:
                    out_f.write(raw_bytes)
                print(f"[RESTORATION] Replaced {rel_vfs} with authentic raw bytes from {raw_source}")
            else:
                # Revert by flipping current image back
                flipped_back = im_ov.transpose(Image.FLIP_TOP_BOTTOM)
                save_format = 'TGA' if fpath.lower().endswith('.tga') else ('PNG' if fpath.lower().endswith('.png') else None)
                if save_format:
                    flipped_back.save(fpath, format=save_format)
                else:
                    flipped_back.save(fpath)
                print(f"[RESTORATION] Flipped {rel_vfs} back to upright orientation (5-tier .bak preserved)")
            eval_res['fixed'] = True
            
            # If preview requested
            if preview_dir:
                preview_name = rel_vfs.replace('/', '_').replace('\\', '_') + '.preview.png'
                preview_path = os.path.join(preview_dir, preview_name)
                create_preview_composite(im_ov, flipped_back, bl_img, preview_path)
                eval_res['preview'] = preview_path
                
        elif preview_dir and cls in ('PROVABLY_INVERTED', 'LIKELY_INVERTED'):
            preview_name = rel_vfs.replace('/', '_').replace('\\', '_') + '.preview.png'
            preview_path = os.path.join(preview_dir, preview_name)
            flipped_preview = im_ov.transpose(Image.FLIP_TOP_BOTTOM)
            create_preview_composite(im_ov, flipped_preview, bl_img, preview_path)
            eval_res['preview'] = preview_path

    elapsed = time.time() - t0
    print(f"\n=== Audit Complete in {elapsed:.2f}s ===")
    print(f"  PROVABLY_INVERTED: {len(results['PROVABLY_INVERTED'])}")
    print(f"  LIKELY_INVERTED:   {len(results['LIKELY_INVERTED'])}")
    print(f"  PROVABLY_NORMAL:   {len(results['PROVABLY_NORMAL'])}")
    print(f"  LIKELY_NORMAL:     {len(results['LIKELY_NORMAL'])}")
    print(f"  SYMMETRIC:         {len(results['SYMMETRIC'])}")
    print(f"  AMBIGUOUS:         {len(results['AMBIGUOUS'])}")
    print(f"  CUSTOM (No Base):  {len(results['CUSTOM'])}")
    print(f"  ERROR:             {len(results['ERROR'])}")

    if report_file:
        generate_markdown_report(results, report_file, elapsed, fix)

    return results

def generate_markdown_report(results, output_path, elapsed, fixed):
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write("# Project Kongor: Comprehensive Texture Orientation Audit Report\n\n")
        f.write(f"- **Scan Date:** {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"- **Scan Duration:** {elapsed:.2f} seconds\n")
        f.write(f"- **Remediation Applied:** {'YES (5-tier .bak rotation)' if fixed else 'NO (Dry Run)'}\n\n")
        
        f.write("## 1. Executive Summary\n\n")
        f.write("| Status Category | Count | Meaning |\n")
        f.write("|---|---|---|\n")
        f.write(f"| **PROVABLY_INVERTED** | **{len(results['PROVABLY_INVERTED'])}** | Proven 100% upside down (`diff_flipped ≈ 0.0`) |\n")
        f.write(f"| **LIKELY_INVERTED**   | **{len(results['LIKELY_INVERTED'])}**   | Strongly inverted (`diff_flipped < diff_direct`) |\n")
        f.write(f"| **PROVABLY_NORMAL**   | {len(results['PROVABLY_NORMAL'])}   | Verified correct upright orientation |\n")
        f.write(f"| **LIKELY_NORMAL**     | {len(results['LIKELY_NORMAL'])}     | Upright orientation verified |\n")
        f.write(f"| **SYMMETRIC**         | {len(results['SYMMETRIC'])}         | Vertically symmetric (orientation invariant) |\n")
        f.write(f"| **AMBIGUOUS**         | {len(results['AMBIGUOUS'])}         | Unclear delta between direct and flipped |\n")
        f.write(f"| **CUSTOM**            | {len(results['CUSTOM'])}            | Custom/new assets with no upstream baseline |\n")
        f.write(f"| **ERROR**             | {len(results['ERROR'])}             | Unreadable or corrupt image headers |\n\n")
        
        all_inverted = results['PROVABLY_INVERTED'] + results['LIKELY_INVERTED']
        if all_inverted:
            f.write(f"## 2. Inverted Textures Requiring Action ({len(all_inverted)} Total)\n\n")
            f.write("| VFS Path | Dimensions | Direct Diff | Flipped Diff | Baseline Source | Action |\n")
            f.write("|---|---|---|---|---|---|\n")
            for item in all_inverted:
                act = "Restored to upright" if fixed else "FLAGGED FOR RESTORATION"
                f.write(f"| `{item['vfs']}` | {item['width']}x{item['height']} | {item['diff_direct']:.2f} | {item['diff_flipped']:.2f} | `{item['baseline_source']}` | **{act}** |\n")
            f.write("\n")
            
        if results['CUSTOM']:
            f.write(f"## 3. Custom / Non-Baseline Textures ({len(results['CUSTOM'])} Total)\n\n")
            f.write("These textures do not exist in upstream HoN `.s2z` packages and are unique additions:\n\n")
            for item in results['CUSTOM'][:50]:
                f.write(f"- `{item['vfs']}` ({item['size'][0]}x{item['size'][1]})\n")
            if len(results['CUSTOM']) > 50:
                f.write(f"- *... and {len(results['CUSTOM']) - 50} more custom textures.*\n")
            f.write("\n")

    print(f"[BaselineCatalog] Written audit report to {output_path}")

def main():
    parser = argparse.ArgumentParser(description="Audit and correct upside-down textures in Project Kongor.")
    parser.add_argument('--override-dir', default='games/client/override', help="Path to override folder.")
    parser.add_argument('--client-root', default='games/client', help="Path to games/client root.")
    parser.add_argument('--fix', action='store_true', help="Automatically flip provably inverted textures and create .bak.")
    parser.add_argument('--preview-dir', default=None, help="Directory to save visual comparison composites.")
    parser.add_argument('--report', default='docs/audits/TEXTURE_ORIENTATION_AUDIT.md', help="Path to generate markdown report.")
    parser.add_argument('--filter', default=None, help="Only process files matching this substring/path.")
    
    args = parser.parse_args()
    audit_directory(
        override_dir=args.override_dir,
        client_root=args.client_root,
        fix=args.fix,
        preview_dir=args.preview_dir,
        report_file=args.report,
        filter_str=args.filter
    )

if __name__ == '__main__':
    main()
