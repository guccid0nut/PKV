#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
verify_proof_screenshot.py - Mandatory Visual Proof Semantic Truth Verifier

Validates that captured proof screenshots are genuine, non-blank, non-stalled,
and not trapped on the unauthenticated pre-login screen or "Login Failed" modal.
Optionally verifies paired engine state JSON telemetry (tick alignment, gamePhase,
and target widget visibility).

Usage:
    python scripts/verify_proof_screenshot.py --image <path> [--state-json <path>] [--expected <type>] [--json]
"""

import os
import sys

# Prevent sibling scripts/re/ directory from shadowing Python's stdlib `re` module
# when invoked without `python -P`.
_SCRIPT_DIR = os.path.normcase(os.path.abspath(os.path.dirname(__file__)))
sys.path = [p for p in sys.path if os.path.normcase(os.path.abspath(p or ".")) != _SCRIPT_DIR]

import argparse
import json
import cv2
import numpy as np


def verify_options_graphics_preview(img: np.ndarray, stats: dict, reference_path: str | None = None) -> tuple[bool, str]:
    """
    Verifies the 'Options -> Display -> Set Your Graphics Level' 3D sample scene
    (Scout, Flint Beastwood, Legion Well, and Fountain Water), optionally comparing
    per-region 3D BGR color histograms against a Windows DXVK reference capture.
    """
    h, w, _ = img.shape
    # Preview <modelpanel> spans roughly x=[31.4%..64.8%], y=[10.2%..37.8%] on 16:9 (1280x720 / 1920x1080);
    # start well_roi at y=13.0%, x=34.0% to exclude optional top-left DXVK_HUD devinfo text and 700p/720p border shift.
    well_roi = img[int(h * 0.130) : int(h * 0.220), int(w * 0.340) : int(w * 0.640)]
    heroes_roi = img[int(h * 0.185) : int(h * 0.355), int(w * 0.340) : int(w * 0.635)]
    water_roi = img[int(h * 0.315) : int(h * 0.375), int(w * 0.318) : int(w * 0.480)]

    # 1. Intrinsic sanity checks (detect $white + TeamColor blowouts and missing water)
    wb, wg, wr = well_roi[:, :, 0].astype(np.int16), well_roi[:, :, 1].astype(np.int16), well_roi[:, :, 2].astype(np.int16)
    well_blown_white = float(((wr >= 245) & (wg >= 245) & (wb >= 245)).mean())
    well_pitch_black = float(((wr <= 8) & (wg <= 8) & (wb <= 8)).mean())

    hb, hg, hr = heroes_roi[:, :, 0].astype(np.int16), heroes_roi[:, :, 1].astype(np.int16), heroes_roi[:, :, 2].astype(np.int16)
    hero_pure_red = float(((hr >= 200) & (hg <= 35) & (hb <= 25)).mean())

    tb, tg, tr = water_roi[:, :, 0].astype(np.int16), water_roi[:, :, 1].astype(np.int16), water_roi[:, :, 2].astype(np.int16)
    water_teal_ratio = float(((tb >= 65) & (tg >= 55) & ((tb - tr) >= 20) & ((tg - tr) >= 15)).mean())

    stats["preview_well_white_ratio"] = round(well_blown_white, 4)
    stats["preview_well_black_ratio"] = round(well_pitch_black, 4)
    stats["preview_hero_purered_ratio"] = round(hero_pure_red, 4)
    stats["preview_water_teal_ratio"] = round(water_teal_ratio, 4)

    if well_blown_white >= 0.12 and well_pitch_black >= 0.10:
        return False, (
            f"REJECTED: Options graphics preview Legion Well is blown-out white/black ($white fallback: "
            f"white={well_blown_white:.1%}, black={well_pitch_black:.1%})! "
            f"Action: Mount full VFS archives (resources98/99.s2z, KONGOR/updates/textures.s2z) and verify Microsoft d3dx9_43.dll."
        )

    if hero_pure_red >= 0.03:
        return False, (
            f"REJECTED: Options graphics preview Flint/Scout models show solid TeamColor red blowout "
            f"(pure_red={hero_pure_red:.1%} >= 3.0%)! "
            f"Action: Ensure native Microsoft d3dx9_43.dll + d3dcompiler_43.dll are loaded instead of Wine builtin vkd3d-shader, and purge shadercache."
        )

    if water_teal_ratio < 0.15:
        return False, (
            f"REJECTED: Options graphics preview is missing visible teal/blue fountain water "
            f"(water_teal_ratio={water_teal_ratio:.1%} < 15.0%)! "
            f"Action: Verify mesh_color_water5 compiles cleanly, slider is Medium/High/Ultra, and vid_vulkan $waterdistortion clamp is active."
        )

    # 2. Optional Windows Golden Reference 3D BGR Histogram Correlation Check
    if reference_path:
        if not os.path.exists(reference_path):
            return False, f"Reference image not found: {reference_path}"
        ref = cv2.imread(reference_path)
        if ref is None:
            return False, f"Failed to decode reference image: {reference_path}"
        if (ref.shape[0], ref.shape[1]) != (h, w):
            ref = cv2.resize(ref, (w, h), interpolation=cv2.INTER_AREA)

        rois = {
            "well": (well_roi, ref[int(h * 0.130) : int(h * 0.220), int(w * 0.340) : int(w * 0.640)], 0.55),
            "heroes": (heroes_roi, ref[int(h * 0.185) : int(h * 0.355), int(w * 0.340) : int(w * 0.635)], 0.55),
            "water": (water_roi, ref[int(h * 0.315) : int(h * 0.375), int(w * 0.318) : int(w * 0.480)], 0.50),
        }
        corrs = {}
        for name, (cand_patch, ref_patch, min_corr) in rois.items():
            h_cand = cv2.calcHist([cand_patch], [0, 1, 2], None, [4, 4, 4], [0, 256, 0, 256, 0, 256])
            h_ref = cv2.calcHist([ref_patch], [0, 1, 2], None, [4, 4, 4], [0, 256, 0, 256, 0, 256])
            cv2.normalize(h_cand, h_cand)
            cv2.normalize(h_ref, h_ref)
            corr = float(cv2.compareHist(h_cand, h_ref, cv2.HISTCMP_CORREL))
            corrs[name] = round(corr, 4)
        stats["reference_correlations"] = corrs
        for name, (_, _, min_corr) in rois.items():
            if corrs[name] < min_corr:
                return False, (
                    f"REJECTED: Options graphics preview '{name}' ROI diverges from Windows reference "
                    f"(3D color correlation={corrs[name]:.3f} < {min_corr:.2f}; all={corrs})! "
                    f"Action: Inspect shader compilation, D3DX9 sampler bindings, and VFS texture archives."
                )

    return True, "Options graphics preview verified (textured heroes, stone well, and visible fountain water)."


def verify_image(
    image_path: str,
    expected_type: str = "general",
    allow_login: bool = False,
    reference_path: str | None = None,
) -> tuple[bool, str, dict, int]:
    """
    Validates the screenshot visually using OpenCV.
    Returns: (is_valid, message, stats_dict, exit_code)
    Exit codes: 0 = valid, 1 = semantic failure, 2 = missing file / load error
    """
    if not os.path.exists(image_path):
        return False, f"Image file not found: {image_path}", {}, 2

    img = cv2.imread(image_path)
    if img is None:
        return False, f"Failed to load image via OpenCV: {image_path}", {}, 2

    h, w, c = img.shape
    stats = {
        "width": w,
        "height": h,
        "channels": c,
    }

    if w < 640 or h < 360:
        return False, f"Image resolution too low ({w}x{h}). Minimum required: 640x360.", stats, 1

    # 1. Blank / Flat image check
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    mean_val = float(np.mean(gray))
    std_val = float(np.std(gray))
    laplacian_var = float(cv2.Laplacian(gray, cv2.CV_64F).var())

    stats["brightness"] = round(mean_val, 2)
    stats["std_dev"] = round(std_val, 2)
    stats["laplacian_var"] = round(laplacian_var, 2)

    if mean_val < 5.0:
        return False, (
            f"Image is completely black/blank (mean brightness: {mean_val:.2f} < 5.0). "
            f"Action: Verify client renderer is active and not minimized or hung."
        ), stats, 1

    if mean_val > 250.0:
        return False, (
            f"Image is completely white/blown out (mean brightness: {mean_val:.2f} > 250.0). "
            f"Action: Verify client renderer viewport."
        ), stats, 1

    if laplacian_var < 15.0 or std_val < 10.0:
        return False, (
            f"Image has insufficient visual texture/variance (Laplacian: {laplacian_var:.1f}, std: {std_val:.1f}). "
            f"Likely a flat loading screen or solid color frame."
        ), stats, 1

    # 2. Login Screen & Login Failed Detection
    script_dir = os.path.dirname(os.path.abspath(__file__))
    fixtures_dir = os.path.join(script_dir, "fixtures")

    modal_ref_path = os.path.join(fixtures_dir, "login_failed_modal_ref.png")
    logo_ref_path = os.path.join(fixtures_dir, "login_hon_logo_ref.png")

    if not allow_login:
        if os.path.exists(modal_ref_path):
            modal_ref = cv2.imread(modal_ref_path)
            if modal_ref is not None:
                # Resize template if image is not 1080p
                if (h, w) != (1080, 1920):
                    scale_y = h / 1080.0
                    scale_x = w / 1920.0
                    scaled_modal = cv2.resize(modal_ref, (int(modal_ref.shape[1] * scale_x), int(modal_ref.shape[0] * scale_y)))
                else:
                    scaled_modal = modal_ref

                if scaled_modal.shape[0] <= h and scaled_modal.shape[1] <= w:
                    res_modal = cv2.matchTemplate(img, scaled_modal, cv2.TM_CCOEFF_NORMED)
                    _, max_modal_conf, _, _ = cv2.minMaxLoc(res_modal)
                    stats["modal_confidence"] = round(float(max_modal_conf), 3)
                    if max_modal_conf >= 0.80:
                        return False, (
                            f"REJECTED: Screenshot contains 'Login Failed: Connection failed' dialog (confidence: {max_modal_conf:.3f})! "
                            f"The Aspire master-server was offline or the client failed authentication. "
                            f"Action: Start cluster via 'kongor_server_manage action: start target: cluster' and authenticate."
                        ), stats, 1

        if os.path.exists(logo_ref_path):
            logo_ref = cv2.imread(logo_ref_path)
            if logo_ref is not None:
                if (h, w) != (1080, 1920):
                    scale_y = h / 1080.0
                    scale_x = w / 1920.0
                    scaled_logo = cv2.resize(logo_ref, (int(logo_ref.shape[1] * scale_x), int(logo_ref.shape[0] * scale_y)))
                else:
                    scaled_logo = logo_ref

                if scaled_logo.shape[0] <= h and scaled_logo.shape[1] <= w:
                    res_logo = cv2.matchTemplate(img, scaled_logo, cv2.TM_CCOEFF_NORMED)
                    _, max_logo_conf, _, _ = cv2.minMaxLoc(res_logo)
                    stats["logo_confidence"] = round(float(max_logo_conf), 3)
                    if max_logo_conf >= 0.85:
                        return False, (
                            f"REJECTED: Screenshot is trapped on the unauthenticated pre-login screen (confidence: {max_logo_conf:.3f})! "
                            f"This is NOT proof of the requested feature ('{expected_type}'). "
                            f"Action: Launch practice mode ('scripts/launch_screen_practice.ps1') or log into main menu."
                        ), stats, 1

    # 3. $checker Neon-Magenta (#FF00FF) Missing-Texture Cluster Detection
    # Separates unlit (#FF00FF) and scene-lit/shadowed $checker terrain/prop patches
    # (symmetric R≈B, e.g. lit RGB=195,35,195 or dim RGB=165,15,165 -> r-b >= -5)
    # from K2's cool-violet UI chrome (Difficulty diamonds & Level 8 GoldenRed Mastery
    # XP bar RGB=170,66,179 / bevel RGB=161,12,171 where B exceeds R by ~9..10).
    world_slice = img[int(h * 0.06) : int(h * 0.76), :, :]
    b_ch = world_slice[:, :, 0].astype(np.int16)
    g_ch = world_slice[:, :, 1].astype(np.int16)
    r_ch = world_slice[:, :, 2].astype(np.int16)
    magenta_mask = (
        ((r_ch > 220) & (g_ch < 35) & (b_ch > 220) & (np.abs(r_ch - b_ch) <= 8))
        | (
            (r_ch > 160)
            & (b_ch > 160)
            & (g_ch <= 45)
            & ((r_ch - b_ch) >= -5)
            & ((r_ch - b_ch) <= 15)
            & (np.minimum(r_ch, b_ch) - g_ch >= 115)
        )
    ).astype(np.uint8)
    magenta_px = int(magenta_mask.sum())
    num_labels, _, comp_stats, _ = cv2.connectedComponentsWithStats(magenta_mask, connectivity=8)
    max_magenta_cluster = int(comp_stats[1:, cv2.CC_STAT_AREA].max()) if num_labels > 1 else 0

    stats["magenta_px"] = magenta_px
    stats["max_magenta_cluster"] = max_magenta_cluster

    if max_magenta_cluster >= 100 or (magenta_px >= 200 and max_magenta_cluster >= 50):
        # Universal $checker check: verify sharp step boundary or dark-checker adjacency
        # Real K2 $checker textures exhibit high boundary step gradients (>= 40.0) or dark-block adjacency
        kernel = np.ones((3, 3), np.uint8)
        dilated = cv2.dilate(magenta_mask, kernel, iterations=1)
        boundary = (dilated - magenta_mask) > 0

        dark_adj = bool((boundary & (r_ch < 50) & (g_ch < 50) & (b_ch < 50)).any())

        gray_slice = cv2.cvtColor(world_slice, cv2.COLOR_BGR2GRAY)
        gx = cv2.Sobel(gray_slice, cv2.CV_32F, 1, 0, ksize=3)
        gy = cv2.Sobel(gray_slice, cv2.CV_32F, 0, 1, ksize=3)
        grad_mag = np.sqrt(gx**2 + gy**2)
        mean_boundary_grad = float(grad_mag[boundary].mean()) if boundary.any() else 0.0

        if dark_adj or mean_boundary_grad >= 40.0:
            return False, (
                f"REJECTED: Screenshot contains neon-magenta ($checker / #FF00FF) missing-texture terrain/prop artifacts "
                f"(magenta_px={magenta_px}, max_cluster={max_magenta_cluster} >= 100, boundary_grad={mean_boundary_grad:.1f})! "
                f"Action: Inspect console.log for 'CTexture::Load' warnings, purge shadercache.s2z, and verify 00000000/00000003/00000005 mip tiers."
            ), stats, 1

    # 3b. Linux OpenGL (vid_gl2) Black-and-White $checker Missing-Texture Detection
    # On Linux OpenGL, missing 3D world textures render as uncolored black (#000000) + bright white (#E0E0E0..#FFFFFF)
    # geometric checker facets with massive pure-black world area and sharp black-adjacent-to-white edges.
    expected_lower = expected_type.lower()
    if expected_lower in ("hud", "in_game", "ingame", "heartbeat"):
        sat_max = np.maximum(np.maximum(r_ch, g_ch), b_ch) - np.minimum(np.minimum(r_ch, g_ch), b_ch)
        pure_black = ((r_ch <= 10) & (g_ch <= 10) & (b_ch <= 10)).astype(np.uint8)
        bright_achromatic = ((r_ch >= 215) & (g_ch >= 215) & (b_ch >= 215) & (sat_max <= 15)).astype(np.uint8)
        world_px = float(world_slice.shape[0] * world_slice.shape[1])
        black_ratio = float(pure_black.sum()) / max(1.0, world_px)
        white_ratio = float(bright_achromatic.sum()) / max(1.0, world_px)
        bw_adj_px = int((cv2.dilate(bright_achromatic, np.ones((5, 5), np.uint8), iterations=1) & pure_black).sum())
        stats["bw_black_ratio"] = round(black_ratio, 4)
        stats["bw_white_ratio"] = round(white_ratio, 4)
        stats["bw_adj_px"] = bw_adj_px
        if black_ratio >= 0.12 and white_ratio >= 0.015 and bw_adj_px >= 800:
            return False, (
                f"REJECTED: Screenshot contains Linux OpenGL black-and-white ($checker) missing-texture world geometry "
                f"(black_ratio={black_ratio:.1%}, white_ratio={white_ratio:.1%}, bw_adj_px={bw_adj_px})! "
                f"Action: Launch with KONGOR_FORCE_FULLRES=1 (vid_textureDownsize 0, vid_sceneBuffer true) or hardware D3D12/OpenGL GPU."
            ), stats, 1

    # 4. Expected Feature Semantics
    expected_lower = expected_type.lower()
    if expected_lower in ("hud", "in_game", "heartbeat"):
        if laplacian_var < 50.0:
            return False, (
                f"REJECTED: Expected active in-game match/practice mode, but texture complexity is too low "
                f"(Laplacian: {laplacian_var:.1f} < 50.0). Verify practice mode is fully loaded."
            ), stats, 1

    if expected_lower == "options_graphics_preview" or reference_path is not None:
        prev_ok, prev_msg = verify_options_graphics_preview(img, stats, reference_path)
        if not prev_ok:
            return False, prev_msg, stats, 1

    return True, f"VERIFIED: Screenshot passed visual validity checks (brightness: {mean_val:.1f}, variance: {laplacian_var:.1f}, expected: '{expected_type}').", stats, 0


def verify_state(
    state_path: str,
    expected_type: str = "general",
    expected_tick: int | None = None,
    tick_tolerance: int = 5,
    allow_login: bool = False
) -> tuple[bool, str, dict, int]:
    """
    Validates paired engine state telemetry JSON.
    Returns: (is_valid, message, parsed_state, exit_code)
    Exit codes: 0 = valid, 1 = semantic failure, 2 = missing file / parse error
    """
    if not os.path.exists(state_path):
        return False, f"State JSON file not found: {state_path}", {}, 2

    try:
        with open(state_path, "r", encoding="utf-8") as f:
            state = json.load(f)
    except Exception as e:
        return False, f"Failed to parse state JSON ({state_path}): {e}", {}, 2

    if not isinstance(state, dict):
        return False, f"State JSON must be an object/dict, got {type(state).__name__}", {}, 2

    # 1. Tick alignment check
    tick = None
    for k in ("tick", "engine_tick", "current_tick", "frame", "engineTick"):
        if k in state and isinstance(state[k], (int, float)):
            tick = int(state[k])
            break

    if expected_tick is not None:
        if tick is None:
            return False, f"REJECTED: Expected tick {expected_tick} specified, but no tick field found in state JSON.", state, 1
        tick_delta = abs(tick - expected_tick)
        if tick_delta > tick_tolerance:
            return False, (
                f"REJECTED: Engine tick mismatch! State tick is {tick}, but expected {expected_tick} "
                f"(tolerance: +/-{tick_tolerance}, delta: {tick_delta}). Action: Capture state and screenshot on the exact same frame."
            ), state, 1

    # 2. GamePhase check
    phase = None
    for k in ("gamePhase", "game_phase", "phase", "phase_id"):
        if k in state and isinstance(state[k], (int, float)):
            phase = int(state[k])
            break

    is_active_match = bool(state.get("isActiveMatch", state.get("is_active_match", False)))
    expected_lower = expected_type.lower()

    if phase == 0 and not allow_login:
        return False, (
            "REJECTED: State JSON reports gamePhase=0 (unauthenticated pre-login screen)! "
            "Action: Authenticate or launch practice mode before capturing proof."
        ), state, 1

    if expected_lower in ("hud", "in_game", "heartbeat"):
        if phase is not None and phase != 5 and not is_active_match:
            return False, (
                f"REJECTED: Expected active in-game match/practice mode, but state JSON reports gamePhase={phase} "
                f"(isActiveMatch={is_active_match}). Must be gamePhase=5."
            ), state, 1

    if expected_lower in ("store", "theater", "vault", "codex", "ready_check", "onboarding_presets"):
        if phase is not None and phase == 0:
            return False, (
                f"REJECTED: Expected {expected_type} interface, but state JSON reports gamePhase=0 (pre-login)."
            ), state, 1

    if expected_lower in ("ready_check", "onboarding_presets"):
        if phase is not None and phase != 1:
            return False, (
                f"REJECTED: Expected authenticated lobby (gamePhase=1) for {expected_type}, "
                f"but state JSON reports gamePhase={phase}."
            ), state, 1

    # 3. Widget visibility check
    visible_widgets = state.get("visibleWidgets") or state.get("visible_widgets") or state.get("widgets")
    if visible_widgets is None and expected_lower in ("ready_check", "onboarding_presets"):
        return False, (
            f"REJECTED: Expected visibleWidgets telemetry in state JSON for '{expected_type}'."
        ), state, 1

    if visible_widgets is not None:
        widget_set = set()
        if isinstance(visible_widgets, list):
            widget_set = {str(w).lower() for w in visible_widgets}
        elif isinstance(visible_widgets, dict):
            widget_set = {str(k).lower() for k, v in visible_widgets.items() if v}

        if expected_lower == "store":
            store_candidates = {"store_container2", "store_container", "store_root", "store_overlay"}
            if not widget_set.intersection(store_candidates):
                return False, (
                    "REJECTED: Expected Store catalog, but none of the store widgets (store_container2, store_container) "
                    "are visible in state JSON. Action: Open and focus the Store interface."
                ), state, 1

        if expected_lower == "theater":
            theater_candidates = {"cinematic_theater_modal", "theater_modal", "cinematic_theater"}
            if not widget_set.intersection(theater_candidates):
                return False, (
                    "REJECTED: Expected Cinematic Theater, but cinematic_theater_modal is not visible in state JSON. "
                    "Action: Open the Cinematic Theater modal."
                ), state, 1

        if expected_lower == "ready_check":
            ready_check_candidates = {"matchmaker_accept_game_dialog", "ready_check_root"}
            if not widget_set.intersection(ready_check_candidates):
                return False, (
                    "REJECTED: Expected 10-player Ready-Check modal, but neither matchmaker_accept_game_dialog nor ready_check_root is visible in state JSON. "
                    "Action: Trigger TMMMatchPendingAccept in authenticated lobby (gamePhase=1)."
                ), state, 1

        if expected_lower == "onboarding_presets":
            onboarding_candidates = {"onboarding_presets_root"}
            if not widget_set.intersection(onboarding_candidates):
                return False, (
                    "REJECTED: Expected Onboarding Presets modal, but onboarding_presets_root is not visible in state JSON. "
                    "Action: Trigger KongorOnboarding.Open() in authenticated lobby (gamePhase=1)."
                ), state, 1

    # 4. Heartbeat low HP check
    if expected_lower == "heartbeat":
        hp_pct = None
        for k in ("hero_hp_pct", "hero_health_pct", "hp_pct", "health_percent"):
            if k in state and isinstance(state[k], (int, float)):
                hp_pct = float(state[k])
                break
        if hp_pct is not None and hp_pct > 0.35:
            return False, (
                f"REJECTED: Expected heartbeat visual proof (low HP <= 35%), but hero health is {hp_pct * 100:.1f}%. "
                f"Action: Lower hero health below 35% to trigger heartbeat pulse."
            ), state, 1

    return True, f"VERIFIED: State telemetry passed checks (phase: {phase}, tick: {tick}, expected: '{expected_type}').", state, 0


def main():
    parser = argparse.ArgumentParser(description="Verify proof screenshot semantic validity.")
    parser.add_argument("--image", required=True, help="Path to screenshot image")
    parser.add_argument("--state-json", default=None, help="Optional path to paired engine state JSON telemetry")
    parser.add_argument("--expected-tick", type=int, default=None, help="Expected engine tick to assert against state JSON")
    parser.add_argument("--tick-tolerance", type=int, default=5, help="Allowed tick drift tolerance between image and state (default: 5)")
    parser.add_argument("--expected", default="general", help="Expected feature type (general, in_game, hud, store, theater, heartbeat, vault, codex, ready_check, onboarding_presets, options_graphics_preview)")
    parser.add_argument("--reference", default=None, help="Optional Windows golden reference image for ROI histogram similarity verification")
    parser.add_argument("--allow-login", action="store_true", help="Allow login screen if intentionally capturing it")
    parser.add_argument("--strict", action="store_true", help="Strict mode: require --state-json for feature verification")
    parser.add_argument("--json", action="store_true", help="Output machine-readable JSON object")
    args = parser.parse_args()

    errors = []
    warnings = []
    details = {
        "imagePath": args.image,
        "expected": args.expected,
        "stateJsonPath": args.state_json,
    }
    worst_exit_code = 0

    # 1. Image verification
    img_ok, img_msg, img_stats, img_code = verify_image(args.image, args.expected, args.allow_login, args.reference)
    details["imageStats"] = img_stats
    if not img_ok:
        errors.append(img_msg)
        worst_exit_code = max(worst_exit_code, img_code)

    # 2. State verification
    if args.state_json:
        state_ok, state_msg, state_data, state_code = verify_state(
            args.state_json,
            expected_type=args.expected,
            expected_tick=args.expected_tick,
            tick_tolerance=args.tick_tolerance,
            allow_login=args.allow_login,
        )
        details["stateSummary"] = {
            "tick": state_data.get("tick") or state_data.get("engine_tick"),
            "gamePhase": state_data.get("gamePhase") or state_data.get("game_phase"),
            "isActiveMatch": state_data.get("isActiveMatch", False),
        }
        if not state_ok:
            errors.append(state_msg)
            worst_exit_code = max(worst_exit_code, state_code)
    elif args.strict and args.expected.lower() not in ("general",):
        errors.append(f"REJECTED: Strict mode requires --state-json pairing for feature '{args.expected}'.")
        worst_exit_code = max(worst_exit_code, 1)

    overall_valid = (len(errors) == 0)

    if args.json:
        payload = {
            "valid": overall_valid,
            "status": "PASS" if overall_valid else ("ERROR" if worst_exit_code == 2 else "FAIL"),
            "exitCode": worst_exit_code,
            "errors": errors,
            "warnings": warnings,
            "details": details,
        }
        print(json.dumps(payload, indent=2))
        sys.exit(worst_exit_code)
    else:
        if overall_valid:
            print(f"[PASS] {img_msg}")
            if args.state_json:
                print(f"[PASS] State telemetry verified and paired.")
            sys.exit(0)
        else:
            for err in errors:
                print(f"[FAIL] {err}", file=sys.stderr)
            sys.exit(worst_exit_code if worst_exit_code != 0 else 1)


if __name__ == "__main__":
    main()
