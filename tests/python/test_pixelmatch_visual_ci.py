# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Wave 3A verification tests for tools/visual/pixelmatch_ci.py and
scripts/qa/verify_visual_regression.ps1.
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys

import numpy as np
from PIL import Image

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "tools" / "visual"))
import pixelmatch_ci as pm  # noqa: E402


def _make_textured_frame(width: int = 160, height: int = 120) -> np.ndarray:
    """Builds a synthetic non-blank UI-like frame with sharp panels, bars, and gradient."""
    img = np.zeros((height, width, 4), dtype=np.uint8)
    img[..., 3] = 255
    # Background gradient
    xs = np.linspace(20, 70, width, dtype=np.uint8)
    ys = np.linspace(15, 55, height, dtype=np.uint8)
    img[..., 0] = xs[None, :]
    img[..., 1] = ys[:, None]
    img[..., 2] = 60
    # Sharp gold HUD panel
    img[20:60, 30:130, :3] = (200, 165, 65)
    # Dark chat pane
    img[70:110, 10:90, :3] = (25, 30, 38)
    return img


def test_identical_frames_zero_mismatch(tmp_path: pathlib.Path):
    arr = _make_textured_frame()
    base = tmp_path / "base.png"
    cand = tmp_path / "cand.png"
    diff = tmp_path / "diff.png"
    Image.fromarray(arr).save(base)
    Image.fromarray(arr).save(cand)

    report = pm.compare_images(base, cand, diff_output_path=diff, threshold=0.1, max_diff_ratio=0.001)
    assert report.passed is True
    assert report.mismatched_pixels == 0
    assert report.mismatch_ratio == 0.0
    assert report.candidate_is_blank is False
    assert diff.is_file()


def test_real_ui_regression_detected_and_diff_png_written(tmp_path: pathlib.Path):
    base_arr = _make_textured_frame()
    cand_arr = base_arr.copy()
    # Corrupt a 30x25 block (750 px / 19200 px = 3.9% > 1% cap)
    cand_arr[25:50, 40:70, :3] = (220, 20, 220)

    base = tmp_path / "base.png"
    cand = tmp_path / "cand.png"
    diff = tmp_path / "diff.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    report = pm.compare_images(base, cand, diff_output_path=diff, threshold=0.1, max_diff_ratio=0.01)
    assert report.passed is False
    assert report.mismatched_pixels >= 650
    assert report.failure_reason is not None
    assert "Visual regression mismatch" in report.failure_reason
    assert diff.is_file()


def test_ignore_region_masks_dynamic_hud_clock(tmp_path: pathlib.Path):
    base_arr = _make_textured_frame()
    cand_arr = base_arr.copy()
    # Dynamic clock/FPS badge in top-right corner
    cand_arr[2:18, 120:158, :3] = (255, 255, 255)

    base = tmp_path / "base.png"
    cand = tmp_path / "cand.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    unmasked = pm.compare_images(base, cand, max_diff_ratio=0.005)
    assert unmasked.passed is False

    masked = pm.compare_images(
        base,
        cand,
        max_diff_ratio=0.005,
        ignore_regions=[pm.IgnoreRegion.parse("120,2,38,16")],
    )
    assert masked.passed is True
    assert masked.mismatched_pixels == 0
    assert masked.ignored_pixels == 38 * 16


def test_antialiased_1px_edge_skipped_when_include_aa_false(tmp_path: pathlib.Path):
    base_arr = np.full((64, 64, 4), 255, dtype=np.uint8)
    base_arr[:, 32:, :3] = (20, 20, 20)
    cand_arr = base_arr.copy()
    # 1px anti-aliased gray column directly on the vertical contrast edge
    cand_arr[10:54, 32, :3] = (140, 140, 140)

    base = tmp_path / "aa_base.png"
    cand = tmp_path / "aa_cand.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    rep_skip_aa = pm.compare_images(base, cand, include_aa=False, max_diff_ratio=0.001)
    rep_with_aa = pm.compare_images(base, cand, include_aa=True, max_diff_ratio=0.001)
    assert rep_skip_aa.antialiased_pixels > 0
    assert rep_skip_aa.mismatched_pixels < rep_with_aa.mismatched_pixels


def test_blank_candidate_frame_rejected_even_against_blank_baseline(tmp_path: pathlib.Path):
    blank = np.zeros((64, 64, 4), dtype=np.uint8)
    blank[..., 3] = 255
    base = tmp_path / "blank1.png"
    cand = tmp_path / "blank2.png"
    Image.fromarray(blank).save(base)
    Image.fromarray(blank).save(cand)

    report = pm.compare_images(base, cand, reject_blank=True)
    assert report.passed is False
    assert report.candidate_is_blank is True
    assert "blank/solid frame" in (report.failure_reason or "")


def test_powershell_wrapper_emits_valid_json(tmp_path: pathlib.Path):
    arr = _make_textured_frame()
    base = tmp_path / "ps_base.png"
    cand = tmp_path / "ps_cand.png"
    Image.fromarray(arr).save(base)
    Image.fromarray(arr).save(cand)

    ps_script = REPO_ROOT / "scripts" / "qa" / "verify_visual_regression.ps1"
    proc = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(ps_script),
            "-BaselinePath", str(base),
            "-CandidatePath", str(cand),
            "-Json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc.returncode == 0, f"Wrapper failed:\nSTDOUT:{proc.stdout}\nSTDERR:{proc.stderr}"
    payload = json.loads(proc.stdout)
    assert payload["passed"] is True
    assert payload["mismatched_pixels"] == 0


def test_white_vs_transparent_alpha_diff_detected(tmp_path: pathlib.Path):
    """Must-Fix #9: Opaque white (255,255,255,255) vs transparent (0,0,0,0) must produce full perceptual delta."""
    base_arr = _make_textured_frame(120, 80)
    cand_arr = base_arr.copy()
    # Set a 40x40 region opaque white in baseline, fully transparent in candidate
    base_arr[20:60, 40:80] = (255, 255, 255, 255)
    cand_arr[20:60, 40:80] = (0, 0, 0, 0)

    base = tmp_path / "alpha_base.png"
    cand = tmp_path / "alpha_cand.png"
    diff = tmp_path / "alpha_diff.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    report = pm.compare_images(base, cand, diff_output_path=diff, threshold=0.1, max_diff_ratio=0.01)
    assert report.passed is False
    assert report.mismatched_pixels >= 1500, f"Expected all 1600 white-vs-transparent pixels flagged, got {report.mismatched_pixels}"
    assert diff.is_file()


def test_full_image_ignore_mask_fails_explicitly(tmp_path: pathlib.Path):
    """Must-Fix #11: evaluated_pixels == 0 must fail explicitly instead of silently passing."""
    arr = _make_textured_frame(80, 60)
    base = tmp_path / "ign_base.png"
    cand = tmp_path / "ign_cand.png"
    Image.fromarray(arr).save(base)
    Image.fromarray(arr).save(cand)

    report = pm.compare_images(
        base,
        cand,
        ignore_regions=[pm.IgnoreRegion.parse("0,0,80,60")],
    )
    assert report.evaluated_pixels == 0
    assert report.ignored_pixels == 80 * 60
    assert report.passed is False
    assert report.failure_reason == "All pixels excluded by ignore_regions (evaluated_pixels=0)"


def test_1080p_fullscreen_failing_diff_under_3_seconds(tmp_path: pathlib.Path):
    """Must-Fix #12: Vectorized AA + diffing on a 1920x1080 full-screen failing diff must finish in < 3.0s."""
    rng = np.random.default_rng(42)
    base_arr = np.zeros((1080, 1920, 4), dtype=np.uint8)
    base_arr[..., :3] = rng.integers(10, 120, size=(1080, 1920, 3), dtype=np.uint8)
    base_arr[..., 3] = 255
    cand_arr = np.zeros((1080, 1920, 4), dtype=np.uint8)
    cand_arr[..., :3] = rng.integers(140, 250, size=(1080, 1920, 3), dtype=np.uint8)
    cand_arr[..., 3] = 255

    base = tmp_path / "hd_base.png"
    cand = tmp_path / "hd_cand.png"
    diff = tmp_path / "hd_diff.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    report = pm.compare_images(base, cand, diff_output_path=diff, threshold=0.1, max_diff_ratio=0.01, include_aa=False)
    assert report.passed is False
    assert report.width == 1920 and report.height == 1080
    assert report.mismatched_pixels > 1_500_000
    assert report.elapsed_ms < 3000.0, f"1080p full-screen diff exceeded 3s budget: {report.elapsed_ms:.1f}ms"
    assert diff.is_file()


def test_powershell_wrapper_json_pure_on_failure_and_error(tmp_path: pathlib.Path):
    """Must-Fix #10: verify_visual_regression.ps1 -Json must emit pure valid JSON on stdout even when diff fails."""
    base_arr = _make_textured_frame()
    cand_arr = base_arr.copy()
    cand_arr[10:90, 20:140, :3] = (240, 20, 200)

    base = tmp_path / "fail_base.png"
    cand = tmp_path / "fail_cand.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    ps_script = REPO_ROOT / "scripts" / "qa" / "verify_visual_regression.ps1"
    proc = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(ps_script),
            "-BaselinePath", str(base),
            "-CandidatePath", str(cand),
            "-MaxDiffRatio", "0.005",
            "-Json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc.returncode == 1, f"Expected exit code 1 on regression, got {proc.returncode}"
    payload = json.loads(proc.stdout)
    assert payload["passed"] is False
    assert payload["mismatched_pixels"] > 0
    assert "Visual regression mismatch" in payload["failure_reason"]

    # Missing file error under -Json must exit 2 and still emit pure valid JSON on stdout
    proc_err = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(ps_script),
            "-BaselinePath", str(tmp_path / "does_not_exist.png"),
            "-CandidatePath", str(cand),
            "-Json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc_err.returncode == 2, f"Expected exit code 2 on missing input, got {proc_err.returncode}"
    err_payload = json.loads(proc_err.stdout)
    assert err_payload["passed"] is False
    assert err_payload["error_type"] == "FileNotFoundError"


def test_nan_and_out_of_range_thresholds_rejected_and_json_cli_error(tmp_path: pathlib.Path):
    """Should-Fix #17: NaN/Inf/out-of-range threshold and max_diff_ratio must raise ValueError and exit 2 in CLI."""
    import pytest

    arr = _make_textured_frame(40, 40)
    base = tmp_path / "t_base.png"
    cand = tmp_path / "t_cand.png"
    Image.fromarray(arr).save(base)
    Image.fromarray(arr).save(cand)

    for bad_t in (float("nan"), float("inf"), -0.1, 1.5):
        with pytest.raises(ValueError, match="threshold"):
            pm.compare_images(base, cand, threshold=bad_t)

    for bad_r in (float("nan"), float("-inf"), -0.01, 1.01):
        with pytest.raises(ValueError, match="max_diff_ratio"):
            pm.compare_images(base, cand, max_diff_ratio=bad_r)

    # CLI with --threshold nan --json must exit 2 and emit valid JSON error payload
    proc = subprocess.run(
        [
            sys.executable, str(REPO_ROOT / "tools" / "visual" / "pixelmatch_ci.py"),
            str(base), str(cand), "--threshold", "nan", "--json",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc.returncode == 2
    err_payload = json.loads(proc.stdout)
    assert err_payload["passed"] is False
    assert err_payload["error_type"] == "ValueError"
    assert "threshold" in err_payload["error"]


def test_dimension_mismatch_sets_diff_output_path_none_and_summary_comparator(tmp_path: pathlib.Path):
    """Should-Fix #17 & Nice-to-Have #22: dimension mismatch leaves diff_output_path=None; text CLI uses '>' on fail."""
    base_arr = _make_textured_frame(80, 60)
    cand_arr = _make_textured_frame(100, 60)
    base = tmp_path / "dim_base.png"
    cand = tmp_path / "dim_cand.png"
    diff = tmp_path / "dim_diff_should_not_exist.png"
    Image.fromarray(base_arr).save(base)
    Image.fromarray(cand_arr).save(cand)

    report = pm.compare_images(base, cand, diff_output_path=diff)
    assert report.passed is False
    assert report.diff_output_path is None
    assert not diff.exists()
    assert "dimension mismatch" in (report.failure_reason or "").lower()

    # Non-JSON CLI summary must print '>' (not '<=') when mismatch_ratio > max_diff_ratio
    proc = subprocess.run(
        [
            sys.executable, str(REPO_ROOT / "tools" / "visual" / "pixelmatch_ci.py"),
            str(base), str(cand), "--max-diff-ratio", "0.01",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc.returncode == 1
    assert "100.0000% > 1.0000%" in proc.stdout, f"Expected '>' comparator on fail, got: {proc.stdout}"


def test_vectorized_aa_matches_scalar_mapbox_reference_and_transparent_blank_rejected(tmp_path: pathlib.Path):
    """Must-Fix #9 & #12: Vectorized AA matches exact scalar Mapbox semantics; all-transparent blank still rejected."""
    def _scalar_has_many_siblings(rgb: np.ndarray, x: int, y: int) -> bool:
        h, w, _ = rgb.shape
        x0, y0 = max(x - 1, 0), max(y - 1, 0)
        x1, y1 = min(x + 1, w - 1), min(y + 1, h - 1)
        center = rgb[y, x]
        zeroes = 1 if (x == x0 or x == x1 or y == y0 or y == y1) else 0
        eq = int(np.count_nonzero(np.all(rgb[y0 : y1 + 1, x0 : x1 + 1] == center, axis=-1))) - 1
        return (eq + zeroes) > 2

    def _scalar_is_aa(rgb1: np.ndarray, rgb2: np.ndarray, x: int, y: int) -> bool:
        h, w, _ = rgb1.shape
        x0, y0 = max(x - 1, 0), max(y - 1, 0)
        x1, y1 = min(x + 1, w - 1), min(y + 1, h - 1)
        zeroes = 1 if (x == x0 or x == x1 or y == y0 or y == y1) else 0
        min_d, max_d = 0.0, 0.0
        min_x = max_x = x
        min_y = max_y = y
        c = rgb1[y, x]
        for ny in range(y0, y1 + 1):
            for nx in range(x0, x1 + 1):
                if nx == x and ny == y:
                    continue
                nb = rgb1[ny, nx]
                if np.all(c == nb):
                    zeroes += 1
                    if zeroes > 2:
                        return False
                    continue
                dy = float(
                    (c[0] - nb[0]) * 0.29889531
                    + (c[1] - nb[1]) * 0.58662247
                    + (c[2] - nb[2]) * 0.11448223
                )
                if dy < min_d:
                    min_d, min_x, min_y = dy, nx, ny
                elif dy > max_d:
                    max_d, max_x, max_y = dy, nx, ny
        if min_d == 0.0 or max_d == 0.0:
            return False
        return (
            (_scalar_has_many_siblings(rgb1, min_x, min_y) and _scalar_has_many_siblings(rgb2, min_x, min_y))
            or (_scalar_has_many_siblings(rgb1, max_x, max_y) and _scalar_has_many_siblings(rgb2, max_x, max_y))
        )

    # Construct 32x32 frame with flat blocks, diagonal 1px AA transitions, borders, and corners
    rgb1 = np.full((32, 32, 3), 240.0, dtype=np.float32)
    rgb1[:, 16:] = (20.0, 20.0, 20.0)
    rgb1[16:, :16] = (60.0, 120.0, 200.0)
    rgb2 = rgb1.copy()
    rgb2[4:28, 16] = (130.0, 130.0, 130.0)
    for k in range(32):
        rgb2[k, k] = (100.0, 140.0, 160.0)

    sib1 = pm._has_many_siblings_map(rgb1)
    sib2 = pm._has_many_siblings_map(rgb2)
    vec_aa = pm._is_antialiased_map(pm._luma_u8(rgb2), sib2, sib1 & sib2)

    for y in range(32):
        for x in range(32):
            assert bool(sib1[y, x]) == _scalar_has_many_siblings(rgb1, x, y), f"sib1 mismatch at ({x},{y})"
            assert bool(sib2[y, x]) == _scalar_has_many_siblings(rgb2, x, y), f"sib2 mismatch at ({x},{y})"
            assert bool(vec_aa[y, x]) == _scalar_is_aa(rgb2, rgb1, x, y), f"aa mismatch at ({x},{y})"

    # Fully transparent candidate frame must STILL register as blank despite checkerboard diff compositing
    trans = np.zeros((48, 48, 4), dtype=np.uint8)
    base = tmp_path / "trans_base.png"
    cand = tmp_path / "trans_cand.png"
    Image.fromarray(trans).save(base)
    Image.fromarray(trans).save(cand)
    rep = pm.compare_images(base, cand, reject_blank=True)
    assert rep.passed is False
    assert rep.candidate_is_blank is True
    assert rep.candidate_luma_stddev == 0.0
