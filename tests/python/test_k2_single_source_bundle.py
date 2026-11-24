#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Wave 3A verification tests for single-source K2 bundling (scripts/build_k2_bundle.ps1),
verify_lua retirement, and kongor_logger bounded rotation.
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import zipfile

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
BUNDLE_SCRIPT = REPO_ROOT / "scripts" / "build_k2_bundle.ps1"


def test_verify_lua_retired_and_lib_json_in_canonical_override():
    assert not (REPO_ROOT / "games" / "client" / "verify_lua").exists(), (
        "Legacy mirror games/client/verify_lua/ must remain retired in favor of games/client/override/"
    )
    assert (REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "lib_json.lua").is_file()
    log_file = REPO_ROOT / "games" / "client" / "override" / "logs" / "client_lua_debug.log"
    if log_file.is_file():
        assert log_file.stat().st_size < 2 * 1024 * 1024, (
            f"client_lua_debug.log exceeded 2 MiB rolling cap ({log_file.stat().st_size} bytes)"
        )


def test_kongor_logger_has_bounded_rotation_cap():
    logger_lua = (REPO_ROOT / "games" / "client" / "override" / "ui" / "scripts" / "newui" / "kongor_logger.lua").read_text(encoding="utf-8")
    assert "_MAX_DISK_LOG_BYTES" in logger_lua
    assert "rotateDiskLogIfOversized" in logger_lua


def test_build_k2_bundle_deterministic_sharding_and_32bit_compliance(tmp_path: pathlib.Path):
    src = tmp_path / "override"
    (src / "ui" / "scripts").mkdir(parents=True)
    (src / "textures").mkdir(parents=True)
    (src / "logs").mkdir(parents=True)

    # Create deterministic assets that force at least 2 shards under a small MaxShardBytes cap (70 KiB)
    (src / "ui" / "scripts" / "a_main.lua").write_text("local x = 1\nreturn x\n", encoding="utf-8")
    (src / "ui" / "scripts" / "b_sub.lua").write_text("local y = 2\nreturn y\n", encoding="utf-8")
    # Uncompressible-ish binary blobs stored as .dds (STORED mode) to force shard split
    (src / "textures" / "tex1.dds").write_bytes(bytes((i * 31 + 7) % 256 for i in range(45_000)))
    (src / "textures" / "tex2.dds").write_bytes(bytes((i * 17 + 13) % 256 for i in range(45_000)))
    # Excluded files that must never enter the bundle
    (src / "logs" / "client_lua_debug.log").write_text("should be ignored\n", encoding="utf-8")
    (src / "ui" / "scripts" / "stale.lua.bak").write_text("backup\n", encoding="utf-8")

    out1 = tmp_path / "out1"
    out2 = tmp_path / "out2"

    for out_dir in (out1, out2):
        res = subprocess.run(
            [
                "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", str(BUNDLE_SCRIPT),
                "-SourceDir", str(src),
                "-OutputDir", str(out_dir),
                "-MaxShardBytes", "70000",
                "-SkipValidation",
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        assert res.returncode == 0, f"build_k2_bundle.ps1 failed:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}"

    m1 = json.loads((out1 / "bundle_manifest.json").read_text(encoding="utf-8"))
    m2 = json.loads((out2 / "bundle_manifest.json").read_text(encoding="utf-8"))

    assert m1["totalFiles"] == 4
    assert m1["excludedFiles"] == 2
    assert m1["shardCount"] >= 2
    assert [s["sha256"] for s in m1["shards"]] == [s["sha256"] for s in m2["shards"]], (
        "Deterministic builds over identical inputs must produce identical SHA-256 shard digests"
    )

    all_entries = []
    for shard in m1["shards"]:
        assert shard["strict32BitVerified"] is True
        assert shard["actualBytes"] <= 70_000
        shard_path = out1 / shard["fileName"]
        raw = shard_path.read_bytes()
        assert b"PK\x06\x06" not in raw and b"PK\x06\x07" not in raw
        with zipfile.ZipFile(shard_path, "r") as zf:
            all_entries.extend(zf.namelist())

    assert sorted(all_entries) == [
        "textures/tex1.dds",
        "textures/tex2.dds",
        "ui/scripts/a_main.lua",
        "ui/scripts/b_sub.lua",
    ]


def test_main_v2_modularization_extracted_controllers():
    override_ui = REPO_ROOT / "games" / "client" / "override" / "ui"
    main_v2 = override_ui / "scripts" / "main_v2.lua"
    lines = main_v2.read_text(encoding="utf-8", errors="replace").splitlines()
    assert len(lines) < 7300, f"Expected main_v2.lua modularized below 7300 lines, got {len(lines)}"

    for mod in ("main_v2_p2p.lua", "main_v2_botmatch.lua", "main_v2_walkthrough.lua"):
        p = override_ui / "scripts" / mod
        assert p.is_file(), f"Extracted module missing: {p}"
        for iface_name in ("main_v2.interface", "main.interface"):
            iface_text = (override_ui / "fe2" / iface_name).read_text(encoding="utf-8", errors="replace")
            assert f"/ui/scripts/{mod}" in iface_text, f"{mod} must be loaded in {iface_name}"


def test_codemod_lua_getwidget_safety_and_edge_cases():
    import sys

    sys.path.insert(0, str(REPO_ROOT / "scripts" / "qa"))
    import codemod_lua_getwidget as cm

    sample = (
        "local function Demo(idx, show)\n"
        "\tGetWidget('panel_' .. idx):SetVisible(show) -- inline comment\n"
        "\tinterface:GetWidget(\"foo\", 'main'):FadeIn(250)\n"
        "\tlocal w = GetWidget('assign_keep'):GetValue()\n"
        "\tif idx == 0 then\n"
        "\t\treturn GetWidget('ret_keep'):IsVisible()\n"
        "\tend\n"
        "\tGetWidget('chain_skip'):SetVisible(true):FadeIn(10)\n"
        "\t-- GetWidget('comment_skip'):SetVisible(false)\n"
        "\t--[[\n"
        "\tGetWidget('long_comment_skip'):SetVisible(false)\n"
        "\t]]\n"
        "\tlocal s = [[\n"
        "\tGetWidget('long_string_skip'):SetVisible(false)\n"
        "\t]]\n"
        "\tlocal x = true and\n"
        "\t\tGetWidget('multiline_skip'):IsVisible()\n"
        "end\n"
    )
    out, candidates, rewritten, skipped, _ = cm.transform_lua_source(sample)
    assert rewritten == 2, f"Expected exactly 2 safe rewrites, got {rewritten}:\n{out}"
    assert skipped == 2, f"Expected 2 skipped (chain + multiline), got {skipped}"
    assert candidates == 4
    assert "do local _kw = GetWidget('panel_' .. idx); if _kw then _kw:SetVisible(show) end end -- inline comment" in out
    assert "do local _kw = interface:GetWidget(\"foo\", 'main'); if _kw then _kw:FadeIn(250) end end" in out
    assert "local w = GetWidget('assign_keep'):GetValue()" in out
    assert "return GetWidget('ret_keep'):IsVisible()" in out
    assert "GetWidget('chain_skip'):SetVisible(true):FadeIn(10)" in out
    assert "GetWidget('long_comment_skip'):SetVisible(false)" in out
    assert "GetWidget('long_string_skip'):SetVisible(false)" in out
    assert "\t\tGetWidget('multiline_skip'):IsVisible()" in out

    luac51 = cm.find_luac51(REPO_ROOT)
    assert luac51 is not None
    ok, err = cm.verify_luac51(out, luac51)
    assert ok, f"luac51 rejected transformed Lua: {err}"


def test_build_k2_bundle_rejects_broken_lua_with_luac51_stderr(tmp_path: pathlib.Path):
    """Must-Fix #6: luac51 must be discovered at real repo paths and reject syntax errors with stderr."""
    src = tmp_path / "src_bad_lua"
    (src / "ui" / "scripts").mkdir(parents=True)
    (src / "ui" / "scripts" / "broken.lua").write_text(
        "local function broken(\n    local x = ===\nend\n",
        encoding="utf-8",
    )
    out = tmp_path / "out_bad_lua"
    # Pre-seed an existing shard to verify Should-Fix #20 (existing output is never deleted on failure)
    out.mkdir(parents=True)
    existing_shard = out / "resources90.s2z"
    existing_shard.write_bytes(b"PRESERVED_PREVIOUS_SHARD")

    res = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(BUNDLE_SCRIPT),
            "-SourceDir", str(src),
            "-OutputDir", str(out),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert res.returncode != 0, f"Expected broken Lua to fail bundling!\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}"
    combined = res.stdout + "\n" + res.stderr
    assert "Lua 5.1 syntax check failed" in combined
    assert "broken.lua" in combined
    # luac51 stderr details (e.g. unexpected symbol / expected) must be captured
    assert "unexpected symbol" in combined or "expected" in combined or "syntax" in combined.lower(), (
        f"Expected captured luac51 stderr diagnostic in error output, got:\n{combined}"
    )
    assert existing_shard.read_bytes() == b"PRESERVED_PREVIOUS_SHARD", (
        "Existing shard must not be deleted when a subsequent build fails validation"
    )


def test_build_k2_bundle_deep_path_accurate_budget_rollover_and_single_asset_guard(tmp_path: pathlib.Path):
    """Must-Fix #7: Deep UTF-8 paths + header/DEFLATE/EOCD budget must roll over cleanly without mid-pack crash."""
    src = tmp_path / "src_deep"
    deep_dir = src / "ui" / ("nested_segment_" * 4) / ("subfolder_deep_" * 4)
    deep_dir.mkdir(parents=True)

    # RelPath is ~145+ bytes -> 2 * UTF8ByteCount ~= 300+ bytes per entry (well over old 128-byte fixed estimate).
    # Create 250 small stored .bin files totaling ~60 KiB payload so headers alone add ~95 KiB and force rollover!
    for i in range(250):
        (deep_dir / f"asset_{i:04d}_deep_payload.bin").write_bytes(bytes([(i + j) % 256 for j in range(180)]))

    out = tmp_path / "out_deep"
    res = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(BUNDLE_SCRIPT),
            "-SourceDir", str(src),
            "-OutputDir", str(out),
            "-MaxShardBytes", "65536",
            "-SkipValidation",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert res.returncode == 0, f"Deep-path shard rollover failed:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}"
    manifest = json.loads((out / "bundle_manifest.json").read_text(encoding="utf-8"))
    assert manifest["totalFiles"] == 250
    assert manifest["shardCount"] >= 2, f"Expected deep paths to trigger rollover, got {manifest['shardCount']} shards"
    for shard in manifest["shards"]:
        assert shard["actualBytes"] <= 65536, f"Shard {shard['fileName']} overflowed cap: {shard['actualBytes']}"
        assert shard["strict32BitVerified"] is True

    # Near-limit single asset whose raw length <= MaxShardBytes but raw + headers + EOCD > MaxShardBytes
    # must fail fast at scan time rather than crashing mid-pack.
    near_src = tmp_path / "src_near_limit"
    near_dir = near_src / ("a" * 80)
    near_dir.mkdir(parents=True)
    (near_dir / "exact_limit.bin").write_bytes(b"\xAB" * 65500)
    near_out = tmp_path / "out_near_limit"
    res_near = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(BUNDLE_SCRIPT),
            "-SourceDir", str(near_src),
            "-OutputDir", str(near_out),
            "-MaxShardBytes", "65536",
            "-SkipValidation",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert res_near.returncode != 0, "Expected near-limit single asset + headers + EOCD to fail at scan time"
    assert "exceeds MaxShardBytes" in (res_near.stdout + res_near.stderr)


def test_build_k2_bundle_accepts_stored_binary_with_pk0607_and_validates_guards(tmp_path: pathlib.Path):
    """
    Must-Fix #8 & Should-Fix #20:
    - Stored binary asset containing raw PK\\x06\\x06 and PK\\x06\\x07 bytes must NOT false-positive as ZIP64.
    - Nested .s2z inside SourceDir must be excluded with warning telemetry.
    - Relaxed XML parse error (.entity) must be counted & logged in bundle_manifest.json.
    - OutputDir inside SourceDir must be rejected.
    """
    src = tmp_path / "src_pk_guard"
    (src / "textures").mkdir(parents=True)
    (src / "ui" / "scripts").mkdir(parents=True)
    (src / "entities").mkdir(parents=True)

    # Stored .bin containing literal ZIP64 magic bytes inside payload
    payload = b"HEADER" + b"PK\x06\x06" + b"\x00" * 64 + b"PK\x06\x07" + b"\xFF" * 64 + b"TAIL"
    (src / "textures" / "fake_zip64_payload.bin").write_bytes(payload)
    # Valid Lua file to prove real luac51 validation runs when -SkipValidation is omitted
    (src / "ui" / "scripts" / "ok.lua").write_text("local ok = 42\nreturn ok\n", encoding="utf-8")
    # Nested .s2z that must be excluded with warning
    (src / "nested.s2z").write_bytes(b"PK\x05\x06" + b"\x00" * 18)
    # Relaxed K2 .entity with malformed XML fragment (should warn and record, not hard-fail)
    (src / "entities" / "relaxed.entity").write_text("<hero name='unclosed'>\n", encoding="utf-8")

    # Guard 1: OutputDir inside SourceDir must fail immediately
    nested_out = src / "sub_out"
    res_nested = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(BUNDLE_SCRIPT),
            "-SourceDir", str(src),
            "-OutputDir", str(nested_out),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert res_nested.returncode != 0, "Expected OutputDir inside SourceDir to be rejected"
    assert "cannot be equal to or inside SourceDir" in (res_nested.stdout + res_nested.stderr)

    # Guard 2: Real bundle build without -SkipValidation must accept PK\x06\x07 stored payload,
    # run real luac51 on ok.lua, exclude nested.s2z, and record relaxed XML warning.
    out = tmp_path / "out_pk_guard"
    res = subprocess.run(
        [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(BUNDLE_SCRIPT),
            "-SourceDir", str(src),
            "-OutputDir", str(out),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert res.returncode == 0, f"Bundle with stored PK\\x06\\x07 payload failed:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}"
    manifest = json.loads((out / "bundle_manifest.json").read_text(encoding="utf-8"))
    assert manifest["validatedLua"] == 1
    assert manifest["luac51Path"] != ""
    assert manifest["excludedNestedS2zFiles"] == 1
    assert manifest["relaxedXmlWarningCount"] == 1
    assert manifest["relaxedXmlWarnings"][0]["relPath"] == "entities/relaxed.entity"
    assert manifest["shardCount"] == 1
    assert manifest["shards"][0]["strict32BitVerified"] is True

    shard_path = out / manifest["shards"][0]["fileName"]
    with zipfile.ZipFile(shard_path, "r") as zf:
        names = zf.namelist()
        assert "textures/fake_zip64_payload.bin" in names
        assert "nested.s2z" not in names
        assert zf.read("textures/fake_zip64_payload.bin") == payload


def test_codemod_lua_getwidget_latin1_multiline_comparison_semicolon_and_apply_luac51(tmp_path: pathlib.Path):
    """Should-Fix #18: Latin-1 bytes in verify_luac51, multiline </>/; safety, and default --apply luac51."""
    import sys

    sys.path.insert(0, str(REPO_ROOT / "scripts" / "qa"))
    import codemod_lua_getwidget as cm

    # Raw Lua script with Latin-1 (0xE9, 0xA9) comment bytes + multiline </>/; edge cases + valid rewrite
    raw_bytes = (
        b"-- Caf\xe9 copyright \xa9 comment\n"
        b"local function EdgeCases(a)\n"
        b"\tGetWidget('safe_stmt'):SetVisible(true)\n"
        b"\tif a <\n"
        b"\t\tGetWidget('cmp_prev_lt'):GetValue() then\n"
        b"\t\treturn\n"
        b"\tend\n"
        b"\tif\n"
        b"\t\tGetWidget('cmp_next_gt'):GetValue()\n"
        b"\t\t> 10 then\n"
        b"\t\treturn\n"
        b"\tend\n"
        b"\tlocal tbl = {\n"
        b"\t\tfirst = 1;\n"
        b"\t\tGetWidget('semi_table_item'):GetValue()\n"
        b"\t}\n"
        b"\tlocal tbl2 = {\n"
        b"\t\tGetWidget('comma_table_item'):GetValue()\n"
        b"\t\t, 2\n"
        b"\t}\n"
        b"\tfoo(\n"
        b"\t\tGetWidget('paren_call_arg'):GetValue()\n"
        b"\t)\n"
        b"end\n"
    )
    lua_file = tmp_path / "latin1_edges.lua"
    lua_file.write_bytes(raw_bytes)

    source = raw_bytes.decode("utf-8", errors="surrogateescape")
    out, candidates, rewritten, skipped, _ = cm.transform_lua_source(source)
    assert rewritten == 1, f"Expected ONLY safe_stmt rewritten, got {rewritten}:\n{out}"
    assert skipped == 5, f"Expected all 5 multiline/table/arg expressions skipped, got {skipped}"
    assert "do local _kw = GetWidget('safe_stmt'); if _kw then _kw:SetVisible(true) end end" in out
    assert "\t\tGetWidget('cmp_prev_lt'):GetValue() then" in out
    assert "\t\tGetWidget('cmp_next_gt'):GetValue()" in out
    assert "\t\tGetWidget('semi_table_item'):GetValue()" in out
    assert "\t\tGetWidget('comma_table_item'):GetValue()" in out
    assert "\t\tGetWidget('paren_call_arg'):GetValue()" in out

    # Direct verify_luac51 call with surrogateescape Latin-1 bytes must NOT raise UnicodeEncodeError
    luac51 = cm.find_luac51(REPO_ROOT)
    assert luac51 is not None
    ok, err = cm.verify_luac51(out, luac51)
    assert ok, f"verify_luac51 failed on Latin-1 Lua: {err}"

    # CLI --apply must run verify_luac51 by default (without --verify-luac51 flag) and preserve Latin-1 bytes
    proc = subprocess.run(
        [sys.executable, str(REPO_ROOT / "scripts" / "qa" / "codemod_lua_getwidget.py"), str(lua_file), "--apply", "--json"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert proc.returncode == 0, f"codemod --apply failed:\nSTDOUT:{proc.stdout}\nSTDERR:{proc.stderr}"
    payload = json.loads(proc.stdout)
    assert payload["mode"] == "apply"
    assert payload["total_rewritten"] == 1
    assert payload["files"][0]["luac51_ok"] is True, "Expected --apply to enable luac51 verification by default"
    final_bytes = lua_file.read_bytes()
    assert b"Caf\xe9 copyright \xa9" in final_bytes, "Latin-1 comment bytes must round-trip intact"


def test_zip64_verifier_rejects_cd_extra_0x0001_and_locator_while_accepting_stored_payload(tmp_path: pathlib.Path):
    """Must-Fix #8: Directly verify Test-NoZip64Signatures parses EOCD + CD entries accurately."""
    import struct

    def _build_minimal_zip(payload: bytes, cd_extra: bytes = b"", inject_locator: bool = False) -> bytes:
        name = b"dir/blob.bin"
        crc = zipfile.zlib.crc32(payload) & 0xFFFFFFFF
        lfh = struct.pack(
            "<4sHHHHHIIIHH",
            b"PK\x03\x04", 20, 0, 0, 0, 0, crc, len(payload), len(payload), len(name), 0,
        ) + name + payload
        cd_offset = len(lfh)
        cdfh = struct.pack(
            "<4sHHHHHHIIIHHHHHII",
            b"PK\x01\x02", 20, 20, 0, 0, 0, 0, crc, len(payload), len(payload),
            len(name), len(cd_extra), 0, 0, 0, 0, 0,
        ) + name + cd_extra
        locator = (b"PK\x06\x07" + b"\x00" * 16) if inject_locator else b""
        eocd = struct.pack(
            "<4sHHHHIIH",
            b"PK\x05\x06", 0, 0, 1, 1, len(cdfh), cd_offset, 0,
        )
        return lfh + cdfh + locator + eocd

    clean_with_magic = tmp_path / "clean_magic.s2z"
    clean_with_magic.write_bytes(_build_minimal_zip(b"DATA" + b"PK\x06\x06" + b"PK\x06\x07" + b"END"))

    bad_extra = tmp_path / "bad_extra_0001.s2z"
    bad_extra.write_bytes(_build_minimal_zip(b"hello", cd_extra=struct.pack("<HHQ", 0x0001, 8, 5)))

    bad_locator = tmp_path / "bad_locator.s2z"
    bad_locator.write_bytes(_build_minimal_zip(b"hello", inject_locator=True))

    ps_harness = (
        f"$tokens = $null; $errs = $null; "
        f"$ast = [System.Management.Automation.Language.Parser]::ParseFile('{BUNDLE_SCRIPT}', [ref]$tokens, [ref]$errs); "
        f"$fn = $ast.Find({{ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-NoZip64Signatures' }}, $true); "
        f"Invoke-Expression $fn.Extent.Text; "
        f"$r1 = Test-NoZip64Signatures -ArchivePath '{clean_with_magic}'; "
        f"$r2 = Test-NoZip64Signatures -ArchivePath '{bad_extra}'; "
        f"$r3 = Test-NoZip64Signatures -ArchivePath '{bad_locator}'; "
        f"@{{ clean=$r1; extra=$r2; locator=$r3 }} | ConvertTo-Json -Compress"
    )
    res = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_harness],
        capture_output=True,
        text=True,
        check=False,
    )
    assert res.returncode == 0, f"PS harness failed:\nSTDOUT:{res.stdout}\nSTDERR:{res.stderr}"
    # Parse the last non-empty JSON line (ignoring any Write-Warning lines)
    json_line = [ln for ln in res.stdout.splitlines() if ln.strip().startswith("{")][-1]
    parsed = json.loads(json_line)
    assert parsed["clean"] is True, "Stored payload containing PK\\x06\\x07 must be accepted"
    assert parsed["extra"] is False, "CD entry with ZIP64 0x0001 extra field must be rejected"
    assert parsed["locator"] is False, "Archive with ZIP64 EOCD Locator PK\\x06\\x07 before EOCD must be rejected"
