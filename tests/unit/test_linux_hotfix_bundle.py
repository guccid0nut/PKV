# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Regression & ELF64 ABI contract tests for Linux x86_64 bundled runtime libraries and wrapper scripts.

Value-Weighted Gate:
- Protects: ELF64 x86_64 class/machine, exact DT_SONAME, ICU/LZMA-free libxml2.so.2 DT_NEEDED closure,
  ABI-critical STT_OBJECT type on xmlFree/xmlMalloc, 0-dep tcmalloc-free libawesomium stubs, LF line endings
  and LC_NUMERIC=C locale guards in hon.sh & hon-x86_64-server-wrapper.sh, and 0755 POSIX tar permissions.
- Fails when: A distro ICU/LZMA-dependent libxml2 is copied in, xmlFree is exported as STT_FUNC instead of
  STT_OBJECT, legacy tcmalloc libawesomium is restored, or CRLF corrupts Linux shell scripts.
- Seam: Real binary ELF64 program/section header + .dynamic/.dynsym/.dynstr table parsing and tarfile headers.
"""

from __future__ import annotations

import os
import struct
import tarfile
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
CLIENT_LINUX = REPO_ROOT / "games" / "client" / "linux-x64"
SERVER_LINUX = REPO_ROOT / "games" / "server" / "linux-x64"
STAGED_LINUX_TAR = Path(
    r"D:\KongorPlaytestStaging\playtest-20261029-183354-79c8b0e9e\Hotfix-Day2\ProjectKongor-Day2-Hotfix-Linux.tar.gz"
)

DT_NULL = 0
DT_NEEDED = 1
DT_SONAME = 14
SHT_DYNAMIC = 6
SHT_DYNSYM = 11
STT_OBJECT = 1


def parse_elf64_metadata(path: Path) -> dict:
    """Parse ELF64 header, DT_SONAME, DT_NEEDED list, and exported .dynsym symbols without external tools."""
    data = path.read_bytes()
    assert len(data) >= 64, f"{path} is too small to be an ELF64 binary. Fix: Rebuild Linux .so artifacts."
    assert data[:4] == b"\x7fELF", f"{path} missing ELF magic. Fix: Rebuild Linux .so artifacts."
    ei_class = data[4]
    ei_data = data[5]
    assert ei_class == 2, f"{path} is not ELF64 (ei_class={ei_class}). Fix: Compile for x86_64 (-m64)."
    assert ei_data == 1, f"{path} is not little-endian."

    e_type, e_machine, _, _, _, e_shoff, _, _, _, _, e_shentsize, e_shnum, e_shstrndx = struct.unpack_from(
        "<HHIQQQIHHHHHH", data, 16
    )
    assert e_type == 3, f"{path} is not ET_DYN shared object (e_type={e_type})."
    assert e_machine == 62, f"{path} is not EM_X86_64 (e_machine={e_machine})."
    assert e_shoff > 0 and e_shnum > 0, f"{path} has no section headers."

    sections = []
    for idx in range(e_shnum):
        off = e_shoff + idx * e_shentsize
        sh_name, sh_type, sh_flags, sh_addr, sh_offset, sh_size, sh_link, sh_info, sh_addralign, sh_entsize = (
            struct.unpack_from("<IIQQQQIIQQ", data, off)
        )
        sections.append(
            {
                "type": sh_type,
                "offset": sh_offset,
                "size": sh_size,
                "link": sh_link,
                "entsize": sh_entsize,
            }
        )

    def read_cstr(strtab_bytes: bytes, idx: int) -> str:
        if idx < 0 or idx >= len(strtab_bytes):
            return ""
        end = strtab_bytes.find(b"\x00", idx)
        if end == -1:
            end = len(strtab_bytes)
        return strtab_bytes[idx:end].decode("utf-8", errors="replace")

    soname = None
    needed = []
    dynsyms: dict[str, tuple[int, int]] = {}

    for sec in sections:
        if sec["type"] == SHT_DYNAMIC:
            str_sec = sections[sec["link"]]
            dynstr = data[str_sec["offset"] : str_sec["offset"] + str_sec["size"]]
            ent_sz = sec["entsize"] or 16
            for pos in range(sec["offset"], sec["offset"] + sec["size"], ent_sz):
                d_tag, d_val = struct.unpack_from("<qQ", data, pos)
                if d_tag == DT_NULL:
                    break
                if d_tag == DT_NEEDED:
                    needed.append(read_cstr(dynstr, d_val))
                elif d_tag == DT_SONAME:
                    soname = read_cstr(dynstr, d_val)
        elif sec["type"] == SHT_DYNSYM:
            str_sec = sections[sec["link"]]
            dynstr = data[str_sec["offset"] : str_sec["offset"] + str_sec["size"]]
            ent_sz = sec["entsize"] or 24
            for pos in range(sec["offset"], sec["offset"] + sec["size"], ent_sz):
                st_name, st_info, st_other, st_shndx, st_value, st_size = struct.unpack_from(
                    "<IBBHQQ", data, pos
                )
                if st_name == 0 or st_shndx == 0:
                    continue
                sym_name = read_cstr(dynstr, st_name)
                st_type = st_info & 0x0F
                dynsyms[sym_name] = (st_type, st_size)

    return {"soname": soname, "needed": needed, "dynsyms": dynsyms}


def test_01_libxml2_is_self_contained_and_exports_stt_object_xmlfree():
    for xml_path in [
        CLIENT_LINUX / "libs-x86_64" / "libxml2.so.2",
        SERVER_LINUX / "libs-x86_64" / "libxml2.so.2",
    ]:
        assert xml_path.is_file(), f"Missing {xml_path}. Fix: Stage self-contained libxml2.so.2."
        meta = parse_elf64_metadata(xml_path)
        assert meta["soname"] == "libxml2.so.2"
        forbidden = [lib for lib in meta["needed"] if "icu" in lib.lower() or "lzma" in lib.lower()]
        assert not forbidden, (
            f"{xml_path} links against non-portable distro libraries {forbidden}. "
            f"Fix: Compile libxml2 with --without-icu --without-lzma --without-python."
        )
        allowed = {"libz.so.1", "libm.so.6", "libc.so.6", "libdl.so.2", "libpthread.so.0"}
        unexpected = set(meta["needed"]) - allowed
        assert not unexpected, f"{xml_path} has unexpected DT_NEEDED entries: {unexpected}"
        assert "xmlFree" in meta["dynsyms"], f"{xml_path} does not export xmlFree!"
        st_type, st_size = meta["dynsyms"]["xmlFree"]
        assert st_type == STT_OBJECT and st_size == 8, (
            f"{xml_path} exports xmlFree with st_type={st_type}, st_size={st_size}; "
            f"libk2-x86_64.so requires STT_OBJECT (1) 8-byte function pointer!"
        )


def test_02_client_glu_and_server_ncurses_sonames_and_minimal_deps():
    glu_meta = parse_elf64_metadata(CLIENT_LINUX / "libs-x86_64" / "libGLU.so.1")
    assert glu_meta["soname"] == "libGLU.so.1"

    ncurses_meta = parse_elf64_metadata(SERVER_LINUX / "libs-x86_64" / "libncurses.so.5")
    assert ncurses_meta["soname"] == "libncurses.so.5"

    tinfo_meta = parse_elf64_metadata(SERVER_LINUX / "libs-x86_64" / "libtinfo.so.5")
    assert tinfo_meta["soname"] == "libtinfo.so.5"


def test_03_awesomium_stubs_have_zero_deps_and_zero_tcmalloc_hijacks():
    for stub_name in ("libawesomium-1-7.so.5", "libawesomium-1.6.5.so.0"):
        stub_path = CLIENT_LINUX / "libs-stub" / stub_name
        assert stub_path.is_file(), f"Missing clean Awesomium stub {stub_path}"
        meta = parse_elf64_metadata(stub_path)
        assert meta["soname"] == stub_name
        forbidden_deps = [d for d in meta["needed"] if "gconf" in d or "gtk" in d or "X11" in d]
        assert not forbidden_deps, f"{stub_path} still carries legacy GUI dependencies: {forbidden_deps}"
        hijack_syms = {"malloc", "free", "realloc", "calloc", "tc_malloc", "tc_free", "_Znwm", "_ZdlPv"}
        leaked = hijack_syms.intersection(meta["dynsyms"].keys())
        assert not leaked, f"{stub_path} exports allocator symbols {leaked} that hijack glibc/Mesa!"


def test_04_linux_wrappers_enforce_lf_and_c_numeric_locale():
    for script_path in (
        CLIENT_LINUX / "hon.sh",
        SERVER_LINUX / "hon-x86_64-server-wrapper.sh",
    ):
        raw = script_path.read_bytes()
        assert b"\r\n" not in raw, (
            f"{script_path} contains CRLF line endings which fail on Linux with '/bin/sh^M: bad interpreter'. "
            f"Fix: Normalize {script_path} to LF."
        )
        text = raw.decode("utf-8")
        assert "export LC_NUMERIC=C" in text, f"{script_path} missing LC_NUMERIC=C float-parsing guard."
        assert "export LC_MESSAGES=C" in text, f"{script_path} missing LC_MESSAGES=C locale guard."


@pytest.mark.skipif(not STAGED_LINUX_TAR.exists(), reason="Staged Day-2 Linux hotfix tarball not present on this machine")
def test_05_staged_linux_hotfix_tarball_permissions_and_lf_integrity():
    size_bytes = STAGED_LINUX_TAR.stat().st_size
    assert 500_000 < size_bytes < 5 * 1024 * 1024, f"Unexpected Linux hotfix tarball size: {size_bytes} bytes"

    with tarfile.open(STAGED_LINUX_TAR, "r:gz") as tf:
        members = {m.name: m for m in tf.getmembers()}
        for required_exec in (
            "apply-hotfix.sh",
            "revert-hotfix.sh",
            ".hotfix_payload_day2-hotfix-1/run-launcher.sh",
            ".hotfix_payload_day2-hotfix-1/run-client.sh",
            ".hotfix_payload_day2-hotfix-1/games/client/linux-x64/hon.sh",
            ".hotfix_payload_day2-hotfix-1/games/client/linux-x64/libs-x86_64/libxml2.so.2",
            ".hotfix_payload_day2-hotfix-1/games/client/linux-x64/libs-x86_64/libGLU.so.1",
            ".hotfix_payload_day2-hotfix-1/_server_linux/libncurses.so.5",
        ):
            assert required_exec in members, f"Missing required tarball member: {required_exec}"
            mode = members[required_exec].mode & 0o777
            assert mode == 0o755, f"{required_exec} has POSIX mode {oct(mode)} instead of 0o755!"

            if required_exec.endswith(".sh"):
                extracted = tf.extractfile(members[required_exec]).read()
                assert b"\r\n" not in extracted, f"CRLF found inside tarball shell script {required_exec}!"
