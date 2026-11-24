#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ==============================================================================
# Project Kongor / HoN - Smart Cross-Platform Vulkan & DXVK 3.x Installer
# Supports:
#   1) Wine / Proton / GE-Proton / Lutris / Steam Deck / CrossOver (hon_x64.exe)
#      - Vulkan >= 1.4 GPUs (Mesa >= 25.0 / NVIDIA >= 575.51): DXVK 3.x (v3.0.2 / v3.1.1)
#      - Vulkan 1.3-only GPUs (e.g. Mesa 24.x): Automatic safe fallback to DXVK v2.7.1
#      - Provisions/verifies native d3dx9_43.dll & d3dcompiler_43.dll so D3DXGetShaderConstantTableEx
#        populates sampler indices cleanly (preventing untextured $white 2D UI quads under Wine)
#      - Generates hon-wine-vulkan.sh with WINEDLLOVERRIDES="version,d3d9,d3dx9_43,d3dcompiler_43=n,b"
#   2) Native Linux ELF Client (linux-x64/hon-x86_64)
#      - Configures Mesa Zink OpenGL-on-Vulkan (hon-vulkan-zink.sh) + NativeGuard
# ==============================================================================

set -euo pipefail

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR=""
INSTALL_MODE="auto"       # auto | wine | native
FORCE_UPGRADE=0
EXPLICIT_DXVK_VER=""
DRY_RUN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir)
            GAME_DIR="$(cd "$2" && pwd)"
            shift 2
            ;;
        --mode)
            INSTALL_MODE="$2"
            shift 2
            ;;
        --dxvk-version)
            EXPLICIT_DXVK_VER="$2"
            shift 2
            ;;
        --upgrade)
            FORCE_UPGRADE=1
            shift
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        -h|--help)
            cat <<'EOF'
Usage: ./install-vulkan.sh [options]

Options:
  --game-dir <path>      Path to Heroes of Newerth client root (auto-detected if omitted)
  --mode <auto|wine|native>
                         Target Wine/Proton DXVK (hon_x64.exe), Native ELF Zink (hon-x86_64), or both (auto)
  --dxvk-version <ver>   Override target DXVK release version (e.g. 3.1.1 or 2.7.1)
  --upgrade              Download latest compatible DXVK release even if d3d9.dll is already present
  --dry-run              Probe Vulkan support and print planned actions without writing files
EOF
            exit 0
            ;;
        *)
            echo -e "${RED}[ERROR] Unknown option: $1 (run with --help for usage)${NC}" >&2
            exit 1
            ;;
    esac
done

# Auto-locate client directory whether placed inside games/client/, scripts/, or parent folder
if [ -z "${GAME_DIR}" ]; then
    for cand in \
        "${SCRIPT_DIR}" \
        "${SCRIPT_DIR}/../games/client" \
        "$(pwd)" \
        "$(pwd)/games/client"; do
        if [ -d "${cand}" ]; then
            abs_cand="$(cd "${cand}" && pwd)"
            if [ -f "${abs_cand}/hon_x64.exe" ] || \
               [ -f "${abs_cand}/windows-x64/hon_x64.exe" ] || \
               [ -f "${abs_cand}/hon-x86_64" ] || \
               [ -f "${abs_cand}/linux-x64/hon-x86_64" ]; then
                GAME_DIR="${abs_cand}"
                break
            fi
        fi
    done
fi

if [ -z "${GAME_DIR}" ]; then
    echo -e "${RED}[ERROR] Could not locate hon_x64.exe or hon-x86_64 near '${SCRIPT_DIR}'.${NC}" >&2
    echo -e "${YELLOW}Actionable Fix: Place install-vulkan.sh inside your HoN folder or pass: ./install-vulkan.sh --game-dir /path/to/HoN${NC}" >&2
    exit 1
fi

# Normalize if pointed directly at windows-x64/ or linux-x64/ subfolder
if [ "$(basename "${GAME_DIR}")" = "windows-x64" ] && [ -d "$(dirname "${GAME_DIR}")" ]; then
    GAME_DIR="$(dirname "${GAME_DIR}")"
fi

LOG_FILE="${GAME_DIR}/vulkan_install.log"
if [ "${DRY_RUN}" -eq 0 ]; then
    {
        echo "================================================================================"
        echo "Project Kongor - Smart Vulkan & DXVK 3.x Setup Log"
        echo "Timestamp: $(date)"
        echo "Kernel: $(uname -a 2>/dev/null || echo 'Unknown')"
        echo "Game Directory: ${GAME_DIR}"
        echo "Mode: ${INSTALL_MODE} | ForceUpgrade: ${FORCE_UPGRADE}"
        echo "================================================================================"
    } > "${LOG_FILE}"
fi

log_line() {
    local level="$1"
    local msg="$2"
    local ts
    ts="$(date +'%H:%M:%S')"
    if [ "${DRY_RUN}" -eq 0 ]; then
        echo "[${ts}] [${level}] ${msg}" >> "${LOG_FILE}"
    fi
}

log_info()    { echo -e "${CYAN}[$(date +'%H:%M:%S')] [INFO] $1${NC}";    log_line "INFO" "$1"; }
log_success() { echo -e "${GREEN}[$(date +'%H:%M:%S')] [SUCCESS] $1${NC}"; log_line "SUCCESS" "$1"; }
log_warn()    { echo -e "${YELLOW}[$(date +'%H:%M:%S')] [WARN] $1${NC}";   log_line "WARN" "$1"; }
log_error()   { echo -e "${RED}[$(date +'%H:%M:%S')] [ERROR] $1${NC}";     log_line "ERROR" "$1"; }

echo -e "${CYAN}==================================================================${NC}"
echo -e "${GREEN}   Project Kongor - Vulkan & DXVK 3.x Setup (Linux / Steam Deck)  ${NC}"
echo -e "${CYAN}==================================================================${NC}"
log_info "Resolved game directory: ${GAME_DIR}"

# ------------------------------------------------------------------------------
# 1. Probe Host Vulkan API Version (Distinguishes Vulkan 1.4+ vs Vulkan 1.3)
#    DXVK 3.0+ strictly requires Vulkan 1.4 (Mesa >= 25.0 RADV / >= 25.1 ANV/NVK/lvp,
#    or NVIDIA >= 575.51.02). Vulkan 1.3 systems automatically receive DXVK v2.7.1.
# ------------------------------------------------------------------------------
VK_MAJOR=0
VK_MINOR=0
VK_DEVICE_NAME="Unknown Vulkan Adapter"

probe_vulkan_api() {
    if [ -n "${KONGOR_MOCK_VULKAN_API:-}" ]; then
        VK_MAJOR="${KONGOR_MOCK_VULKAN_API%%.*}"
        local rest="${KONGOR_MOCK_VULKAN_API#*.}"
        VK_MINOR="${rest%%.*}"
        VK_DEVICE_NAME="Mocked Vulkan Adapter (${KONGOR_MOCK_VULKAN_API})"
        log_info "[Probe] Using KONGOR_MOCK_VULKAN_API=${KONGOR_MOCK_VULKAN_API} -> Vulkan ${VK_MAJOR}.${VK_MINOR}"
        return
    fi

    if ! command -v vulkaninfo >/dev/null 2>&1; then
        log_warn "vulkaninfo not found in PATH (install 'vulkan-tools' for exact GPU Vulkan API detection)."
        return
    fi

    local summary
    summary="$(vulkaninfo --summary 2>/dev/null || true)"
    if [ -z "${summary}" ]; then
        log_warn "vulkaninfo --summary returned empty output; check Vulkan ICD drivers (/usr/share/vulkan/icd.d)."
        return
    fi

    local dev_line
    dev_line="$(printf '%s\n' "${summary}" | grep -m1 'deviceName' | sed -E 's/.*=[[:space:]]*//' || true)"
    if [ -n "${dev_line}" ]; then
        VK_DEVICE_NAME="${dev_line}"
    fi

    # Extract highest device-level apiVersion across physical GPUs (preferring non-llvmpipe if multiple exist)
    local ver_line
    ver_line="$(printf '%s\n' "${summary}" | grep 'apiVersion' | tail -n 1 || true)"
    if [ -z "${ver_line}" ]; then
        return
    fi

    if printf '%s' "${ver_line}" | grep -qE '[0-9]+\.[0-9]+\.[0-9]+'; then
        local semver
        semver="$(printf '%s' "${ver_line}" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -n 1)"
        VK_MAJOR="${semver%%.*}"
        local rem="${semver#*.}"
        VK_MINOR="${rem%%.*}"
    elif printf '%s' "${ver_line}" | grep -qE '=[[:space:]]*[0-9]+'; then
        local raw_num
        raw_num="$(printf '%s' "${ver_line}" | grep -oE '=[[:space:]]*[0-9]+' | grep -oE '[0-9]+' | head -n 1)"
        if [ -n "${raw_num}" ] && [ "${raw_num}" -gt 0 ]; then
            VK_MAJOR=$(( raw_num >> 22 ))
            VK_MINOR=$(( (raw_num >> 12) & 1023 ))
        fi
    fi
}

probe_vulkan_api
log_info "Detected GPU: ${VK_DEVICE_NAME} (Vulkan API: ${VK_MAJOR}.${VK_MINOR})"

# Inspect existing d3d9.dll DXVK major version if present
detect_existing_dxvk_version() {
    local dll_path="$1"
    if [ ! -f "${dll_path}" ]; then
        echo ""
        return
    fi
    if command -v strings >/dev/null 2>&1; then
        local v
        v="$(strings "${dll_path}" 2>/dev/null | grep -oE 'DXVK:? v[0-9]+\.[0-9]+(\.[0-9]+)?|^v[23]\.[0-9]+\.[0-9]+$' | head -n 1 | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' || true)"
        if [ -n "${v}" ]; then
            echo "${v}"
            return
        fi
    fi
    if command -v python3 >/dev/null 2>&1; then
        python3 - "${dll_path}" <<'PY' 2>/dev/null || true
import re, sys
data = open(sys.argv[1], "rb").read()
m = re.search(rb"v([23]\.\d+\.\d+)", data)
if m:
    print(m.group(1).decode("ascii"))
PY
        return
    fi
    echo ""
}

HON_EXE=""
if [ -f "${GAME_DIR}/hon_x64.exe" ]; then
    HON_EXE="${GAME_DIR}/hon_x64.exe"
elif [ -f "${GAME_DIR}/windows-x64/hon_x64.exe" ]; then
    HON_EXE="${GAME_DIR}/windows-x64/hon_x64.exe"
fi

NATIVE_LINUX_DIR=""
if [ -f "${GAME_DIR}/hon-x86_64" ]; then
    NATIVE_LINUX_DIR="${GAME_DIR}"
elif [ -f "${GAME_DIR}/linux-x64/hon-x86_64" ]; then
    NATIVE_LINUX_DIR="${GAME_DIR}/linux-x64"
fi

# ------------------------------------------------------------------------------
# 2. Configure Wine / Proton / Lutris / Steam Deck DXVK 3.x Path (hon_x64.exe)
# ------------------------------------------------------------------------------
if { [ "${INSTALL_MODE}" = "auto" ] || [ "${INSTALL_MODE}" = "wine" ]; } && [ -n "${HON_EXE}" ]; then
    log_info "Configuring Wine/Proton DXVK Vulkan pipeline for: ${HON_EXE}"

    DXVK_DEFAULT_3X="3.1.1"
    DXVK_FALLBACK_13="2.7.1"
    SUPPORTS_VK_14=0
    if [ "${VK_MAJOR}" -gt 1 ] || { [ "${VK_MAJOR}" -eq 1 ] && [ "${VK_MINOR}" -ge 4 ]; }; then
        SUPPORTS_VK_14=1
    elif [ "${VK_MAJOR}" -eq 0 ]; then
        # vulkaninfo not installed; default to DXVK 3.x unless overridden
        SUPPORTS_VK_14=1
        log_warn "Could not verify Vulkan 1.4 via vulkaninfo; assuming Vulkan 1.4+ (pass --dxvk-version 2.7.1 if using an older Vulkan 1.3 driver)."
    fi

    # Check Wine version: stock Ubuntu 24.04 Wine 9.0's winevulkan.dll predates Vulkan 1.4
    # and strips VK_KHR_load_store_op_none, causing DXVK 3.x to skip the GPU unless the user
    # runs via Proton / GE-Proton / WineHQ >= 10.0.
    ACTIVE_WINE_CANDIDATE="${WINE:-}"
    if [ -z "${ACTIVE_WINE_CANDIDATE}" ]; then
        for cand in /opt/wine-stable/bin/wine64 /opt/wine-stable/bin/wine /opt/wine-staging/bin/wine64 /opt/wine-staging/bin/wine /opt/wine-devel/bin/wine64 /opt/wine-devel/bin/wine wine64 /usr/lib/wine/wine64 wine; do
            if command -v "${cand}" >/dev/null 2>&1 || [ -x "${cand}" ]; then
                ACTIVE_WINE_CANDIDATE="${cand}"
                break
            fi
        done
    fi
    if [ "${SUPPORTS_VK_14}" -eq 1 ] && [ -n "${ACTIVE_WINE_CANDIDATE}" ] && [ -z "${STEAM_COMPAT_DATA_PATH:-}" ]; then
        WINE_VER_STR="$("${ACTIVE_WINE_CANDIDATE}" --version 2>/dev/null | head -n 1 || true)"
        if [[ "${WINE_VER_STR}" =~ wine-([0-9]+)\.([0-9]+) ]]; then
            W_MAJ="${BASH_REMATCH[1]}"
            W_MIN="${BASH_REMATCH[2]}"
            if [ "${W_MAJ}" -lt 9 ] || { [ "${W_MAJ}" -eq 9 ] && [ "${W_MIN}" -lt 7 ]; }; then
                SUPPORTS_VK_14=0
                log_warn "Detected older ${WINE_VER_STR} (${ACTIVE_WINE_CANDIDATE}): Wine < 9.7's winevulkan.dll strips VK_KHR_load_store_op_none required by DXVK 3.x!"
                log_info "Install WineHQ >= 10.0 / Proton 9+ for DXVK 3.x, or pass --dxvk-version 3.1.1 when launching via Steam Proton/Lutris."
            fi
        fi
    fi

    if [ -n "${EXPLICIT_DXVK_VER}" ]; then
        TARGET_DXVK_VER="${EXPLICIT_DXVK_VER}"
    elif [ "${SUPPORTS_VK_14}" -eq 1 ]; then
        TARGET_DXVK_VER="${DXVK_DEFAULT_3X}"
        log_success "Host GPU (${VK_MAJOR}.${VK_MINOR}) & Wine runtime support Vulkan 1.4 -> DXVK 3.x enabled!"
    else
        TARGET_DXVK_VER="${DXVK_FALLBACK_13}"
        log_warn "Selecting Vulkan 1.3-compatible DXVK v${TARGET_DXVK_VER} so HoN launches cleanly without adapter rejection!"
    fi

    BIN_DIRS=("${GAME_DIR}")
    if [ -d "${GAME_DIR}/windows-x64" ]; then
        BIN_DIRS+=("${GAME_DIR}/windows-x64")
    fi

    TARGET_D3D9="${GAME_DIR}/d3d9.dll"
    EXISTING_VER="$(detect_existing_dxvk_version "${TARGET_D3D9}")"
    EXISTING_MAJOR="${EXISTING_VER%%.*}"
    TARGET_MAJOR="${TARGET_DXVK_VER%%.*}"

    NEEDS_DOWNLOAD=1
    if [ -f "${TARGET_D3D9}" ] && [ "${FORCE_UPGRADE}" -eq 0 ] && [ -z "${EXPLICIT_DXVK_VER}" ]; then
        FILE_SIZE="$(wc -c < "${TARGET_D3D9}" 2>/dev/null || stat -c %s "${TARGET_D3D9}" 2>/dev/null || echo 0)"
        if [ "${FILE_SIZE}" -gt 2000000 ]; then
            if [ "${SUPPORTS_VK_14}" -eq 1 ] && [ "${EXISTING_MAJOR}" = "3" ]; then
                log_success "Verified bundled 64-bit DXVK v${EXISTING_VER} (${FILE_SIZE} bytes) in ${TARGET_D3D9}."
                NEEDS_DOWNLOAD=0
            elif [ "${SUPPORTS_VK_14}" -eq 0 ] && [ "${EXISTING_MAJOR}" = "2" ]; then
                log_success "Verified Vulkan 1.3-compatible DXVK v${EXISTING_VER} (${FILE_SIZE} bytes) in ${TARGET_D3D9}."
                NEEDS_DOWNLOAD=0
            elif [ "${SUPPORTS_VK_14}" -eq 0 ] && [ "${EXISTING_MAJOR}" = "3" ]; then
                log_warn "Existing d3d9.dll is DXVK v${EXISTING_VER} (requires Vulkan 1.4), but host GPU is Vulkan ${VK_MAJOR}.${VK_MINOR}; replacing with DXVK v${TARGET_DXVK_VER} (backing up d3d9.dll.dxvk3.bak)."
                if [ "${DRY_RUN}" -eq 0 ]; then
                    cp -f "${TARGET_D3D9}" "${TARGET_D3D9}.dxvk3.bak"
                fi
            fi
        fi
    fi

    if [ "${DRY_RUN}" -eq 1 ]; then
        log_info "[DRY-RUN] Target DXVK=${TARGET_DXVK_VER} (existing='${EXISTING_VER:-none}', needsDownload=${NEEDS_DOWNLOAD})"
    else
        for dir in "${BIN_DIRS[@]}"; do
            touch "${dir}/hon_x64.exe.local"
            log_info "Ensured DotLocal marker: ${dir}/hon_x64.exe.local"
        done

        if [ "${NEEDS_DOWNLOAD}" -eq 1 ]; then
            log_info "Downloading official 64-bit DXVK v${TARGET_DXVK_VER}..."
            TEMP_DIR="$(mktemp -d 2>/dev/null || mktemp -d -t 'dxvk')"
            TARBALL="${TEMP_DIR}/dxvk-${TARGET_DXVK_VER}.tar.gz"
            URL="https://github.com/doitsujin/dxvk/releases/download/v${TARGET_DXVK_VER}/dxvk-${TARGET_DXVK_VER}.tar.gz"

            if command -v curl >/dev/null 2>&1; then
                curl -fsSL -o "${TARBALL}" "${URL}"
            elif command -v wget >/dev/null 2>&1; then
                wget -q -O "${TARBALL}" "${URL}"
            else
                rm -rf "${TEMP_DIR}"
                log_error "Neither curl nor wget is installed. Install curl ('sudo apt install curl') and re-run."
                exit 1
            fi

            tar -xzf "${TARBALL}" -C "${TEMP_DIR}"
            SOURCE_DLL="$(find "${TEMP_DIR}" -type f -path "*/x64/d3d9.dll" | head -n 1)"
            if [ -z "${SOURCE_DLL}" ]; then
                rm -rf "${TEMP_DIR}"
                log_error "Could not find x64/d3d9.dll inside dxvk-${TARGET_DXVK_VER}.tar.gz."
                exit 1
            fi

            for dir in "${BIN_DIRS[@]}"; do
                cp -f "${SOURCE_DLL}" "${dir}/d3d9.dll"
                log_success "Deployed DXVK v${TARGET_DXVK_VER} x64/d3d9.dll -> ${dir}/d3d9.dll"
            done
            rm -rf "${TEMP_DIR}"
        fi

        # Generate Wayland- & X11-safe DXVK 3.x configuration (no forced dxvk.wsi=win32, no removed enableAsync)
        mkdir -p "${GAME_DIR}/userdataKONGOR/dxvk_cache"
        DXVK_CONF_CONTENT="# Project Kongor Optimized DXVK 3.x / 2.7.1 Configuration (Linux Wine/Proton & Steam Deck)
# Note: dxvk.wsi is intentionally omitted so both X11/XWayland and native Wine-Wayland WSI work cleanly.
dxvk.enableGraphicsPipelineLibrary = Auto
dxvk.gplFastLink = True
dxvk.numCompilerThreads = 0
dxvk.latencySleep = Auto
dxvk.nvLowLatency2 = Auto
dxvk.tearFree = False
dxvk.allowFse = False
d3d9.presentInterval = 0
d3d9.maxFrameRate = 0
d3d9.maxFrameLatency = 1
d3d9.numBackBuffers = 3
d3d9.samplerAnisotropy = 16
d3d9.cachedDynamicBuffers = True
d3d9.evictManagedOnUnlock = False
d3d9.allowDiscard = True
d3d9.shaderModel = 3
d3d9.seamlessCubes = True
d3d9.deferSurfaceCreation = True
d3d9.samplerLodBias = -0.25
d3d9.floatEmulation = True
"
        for dir in "${BIN_DIRS[@]}"; do
            printf '%s' "${DXVK_CONF_CONTENT}" > "${dir}/dxvk.conf"
        done
        printf '%s' "${DXVK_CONF_CONTENT}" > "${GAME_DIR}/userdataKONGOR/dxvk.conf"
        log_success "Wrote Wayland/X11-compatible dxvk.conf to ${GAME_DIR}/dxvk.conf and userdataKONGOR/dxvk.conf"

        # Verify / provision native Microsoft d3dx9_43.dll & d3dcompiler_43.dll for Wine:
        # Wine's builtin d3dx9_43.dll (vkd3d-shader) misbinds SM3 sampler registers (0xFFFFFFFF in
        # D3DXGetShaderConstantTableEx), corrupting hero team-color materials (solid red/black) and water shaders.
        ACTIVE_WINEPREFIX="${WINEPREFIX:-${HOME}/.wine}"
        verify_native_d3dx_pair() {
            local base_dir="$1"
            local f1="${base_dir}/d3dx9_43.dll"
            local f2="${base_dir}/d3dcompiler_43.dll"
            [ -f "${f2}" ] || f2="${base_dir}/D3DCompiler_43.dll"
            if [ -f "${f1}" ] && [ -f "${f2}" ]; then
                local s1 s2
                s1="$(wc -c < "${f1}" 2>/dev/null || stat -c %s "${f1}" 2>/dev/null || echo 0)"
                s2="$(wc -c < "${f2}" 2>/dev/null || stat -c %s "${f2}" 2>/dev/null || echo 0)"
                if [ "${s1}" -gt 1500000 ] && [ "${s2}" -gt 1500000 ]; then
                    return 0
                fi
            fi
            return 1
        }

        HAS_NATIVE_D3DX9=0
        for check_dir in "${GAME_DIR}" "${GAME_DIR}/windows-x64" "${ACTIVE_WINEPREFIX}/drive_c/windows/system32"; do
            if verify_native_d3dx_pair "${check_dir}"; then
                HAS_NATIVE_D3DX9=1
                log_success "Verified native 64-bit d3dx9_43.dll + d3dcompiler_43.dll in ${check_dir}."
                break
            fi
        done

        # Tier 1: Reuse already-installed native DLLs from local Steam Proton / Lutris prefixes if present
        if [ "${HAS_NATIVE_D3DX9}" -eq 0 ]; then
            for pfx_sys32 in \
                "${HOME}/.steam/steam/steamapps/compatdata"/*/pfx/drive_c/windows/system32 \
                "${HOME}/.local/share/Steam/steamapps/compatdata"/*/pfx/drive_c/windows/system32 \
                "${HOME}/Games"/*/drive_c/windows/system32; do
                if [ -d "${pfx_sys32}" ] && verify_native_d3dx_pair "${pfx_sys32}"; then
                    cp -f "${pfx_sys32}/d3dx9_43.dll" "${GAME_DIR}/d3dx9_43.dll"
                    if [ -f "${pfx_sys32}/d3dcompiler_43.dll" ]; then
                        cp -f "${pfx_sys32}/d3dcompiler_43.dll" "${GAME_DIR}/d3dcompiler_43.dll"
                    else
                        cp -f "${pfx_sys32}/D3DCompiler_43.dll" "${GAME_DIR}/d3dcompiler_43.dll"
                    fi
                    HAS_NATIVE_D3DX9=1
                    log_success "Copied native 64-bit d3dx9_43.dll + d3dcompiler_43.dll from existing prefix (${pfx_sys32})."
                    break
                fi
            done
        fi

        # Tier 2: Try winetricks if available
        if [ "${HAS_NATIVE_D3DX9}" -eq 0 ] && [ -z "${STEAM_COMPAT_DATA_PATH:-}" ] && command -v winetricks >/dev/null 2>&1; then
            log_info "Attempting native d3dx9_43 & d3dcompiler_43 install via winetricks into ${ACTIVE_WINEPREFIX}..."
            if WINEPREFIX="${ACTIVE_WINEPREFIX}" winetricks -q d3dx9_43 d3dcompiler_43 >/dev/null 2>&1 && verify_native_d3dx_pair "${ACTIVE_WINEPREFIX}/drive_c/windows/system32"; then
                cp -f "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/d3dx9_43.dll" "${GAME_DIR}/d3dx9_43.dll"
                if [ -f "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/d3dcompiler_43.dll" ]; then
                    cp -f "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/d3dcompiler_43.dll" "${GAME_DIR}/d3dcompiler_43.dll"
                elif [ -f "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/D3DCompiler_43.dll" ]; then
                    cp -f "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/D3DCompiler_43.dll" "${GAME_DIR}/d3dcompiler_43.dll"
                fi
                HAS_NATIVE_D3DX9=1
                log_success "Installed native d3dx9_43.dll & d3dcompiler_43.dll via winetricks."
            fi
        fi

        # Tier 3: Self-contained SHA256-verified Microsoft DirectX Jun2010 double-CAB extraction via cabextract
        # Essential on pure 64-bit Linux (wine64 without 32-bit wine wrapper) where winetricks aborts early.
        if [ "${HAS_NATIVE_D3DX9}" -eq 0 ] && [ -z "${STEAM_COMPAT_DATA_PATH:-}" ] && command -v cabextract >/dev/null 2>&1; then
            log_info "Downloading SHA256-verified Microsoft DirectX Jun2010 redistributable for 64-bit d3dx9_43 + d3dcompiler_43..."
            DX_TMP="$(mktemp -d 2>/dev/null || mktemp -d -t 'kongor_dx')"
            DX_EXE="${DX_TMP}/directx_Jun2010_redist.exe"
            DX_URL="https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe"
            if command -v curl >/dev/null 2>&1; then
                curl -fsSL -o "${DX_EXE}" "${DX_URL}" || true
            elif command -v wget >/dev/null 2>&1; then
                wget -q -O "${DX_EXE}" "${DX_URL}" || true
            fi
            if [ -f "${DX_EXE}" ] && command -v sha256sum >/dev/null 2>&1; then
                DX_SHA="$(sha256sum "${DX_EXE}" | awk '{print $1}')"
                if [ "${DX_SHA}" = "053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b" ] || [ "${DX_SHA}" = "8746ee1a84a083a90e37899d71d50d5c7c015e69688a466aa80447f011780c0d" ]; then
                    mkdir -p "${DX_TMP}/inner"
                    cabextract -L -q -d "${DX_TMP}" -F "*d3dx9_43*x64*" -F "*d3dcompiler_43*x64*" "${DX_EXE}"
                    for inner_cab in "${DX_TMP}"/*.cab; do
                        [ -f "${inner_cab}" ] && cabextract -L -q -d "${DX_TMP}/inner" -F "*.dll" "${inner_cab}"
                    done
                    if verify_native_d3dx_pair "${DX_TMP}/inner"; then
                        for dir in "${BIN_DIRS[@]}"; do
                            cp -f "${DX_TMP}/inner/d3dx9_43.dll" "${dir}/d3dx9_43.dll"
                            cp -f "${DX_TMP}/inner/d3dcompiler_43.dll" "${dir}/d3dcompiler_43.dll"
                        done
                        if [ -d "${ACTIVE_WINEPREFIX}/drive_c/windows/system32" ]; then
                            cp -f "${DX_TMP}/inner/d3dx9_43.dll" "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/d3dx9_43.dll"
                            cp -f "${DX_TMP}/inner/d3dcompiler_43.dll" "${ACTIVE_WINEPREFIX}/drive_c/windows/system32/d3dcompiler_43.dll"
                        fi
                        HAS_NATIVE_D3DX9=1
                        log_success "Extracted & verified native Microsoft 64-bit d3dx9_43.dll + d3dcompiler_43.dll (SHA256=${DX_SHA:0:12}...)."
                    fi
                else
                    log_error "SHA256 mismatch on directx_Jun2010_redist.exe (got ${DX_SHA}); refusing untrusted archive."
                fi
            fi
            rm -rf "${DX_TMP}"
        fi

        if [ "${HAS_NATIVE_D3DX9}" -eq 0 ] && [ -z "${STEAM_COMPAT_DATA_PATH:-}" ]; then
            log_error "Native Microsoft 64-bit d3dx9_43.dll + d3dcompiler_43.dll could not be provisioned!"
            log_info "Actionable Fix: Install cabextract & curl ('sudo apt install cabextract curl' or 'sudo dnf install cabextract curl') and re-run '${0}', or launch via GE-Proton/Lutris with d3dx9_43 enabled."
            exit 1
        fi

        # Stamp-based shader & DXVK cache invalidation: purge stale/poisoned shader caches ONLY when DXVK or D3DX9 changes
        STAMP_FILE="${GAME_DIR}/userdataKONGOR/.kongor_vulkan_stamp"
        CUR_STAMP="dxvk=${TARGET_DXVK_VER};float=True"
        if command -v sha256sum >/dev/null 2>&1; then
            for sf in "${GAME_DIR}/d3d9.dll" "${GAME_DIR}/d3dx9_43.dll" "${GAME_DIR}/d3dcompiler_43.dll"; do
                if [ -f "${sf}" ]; then
                    CUR_STAMP="${CUR_STAMP};$(basename "${sf}")=$(sha256sum "${sf}" | awk '{print substr($1,1,16)}')"
                fi
            done
        fi
        OLD_STAMP=""
        [ -f "${STAMP_FILE}" ] && OLD_STAMP="$(cat "${STAMP_FILE}" 2>/dev/null || true)"
        if [ "${CUR_STAMP}" != "${OLD_STAMP}" ] || [ "${FORCE_UPGRADE}" -eq 1 ]; then
            log_info "Graphics runtime stamp updated -> purging stale shader & DXVK caches to prevent poisoned descriptors..."
            rm -rf "${GAME_DIR}/userdataKONGOR/dxvk_cache"/* 2>/dev/null || true
            for sc_root in "${GAME_DIR}/userdata" "${GAME_DIR}/userdataKONGOR" "${ACTIVE_WINEPREFIX}/drive_c/users"; do
                if [ -d "${sc_root}" ]; then
                    find "${sc_root}" -maxdepth 6 \( -iname "shadercache*" -o -iname "*.dxvk-cache" \) -exec rm -rf {} + 2>/dev/null || true
                fi
            done
            printf '%s\n' "${CUR_STAMP}" > "${STAMP_FILE}"
            log_success "Shader cache stamp recorded (${STAMP_FILE})."
        fi

        # Generate hon-wine-vulkan.sh wrapper (sets mandatory WINEDLLOVERRIDES for NativeGuard + DXVK + D3DX9)
        WINE_WRAPPER="${GAME_DIR}/hon-wine-vulkan.sh"
        cat > "${WINE_WRAPPER}" <<'EOF_WINE'
#!/usr/bin/env bash
# ==============================================================================
# Project Kongor - Wine / Proton DXVK 3.x Launcher Wrapper
# Enforces WINEDLLOVERRIDES="version,d3d9,d3dx9_43,d3dcompiler_43=n,b" so Wine
# loads Project Kongor's NativeGuard (version.dll), DXVK 3.x (d3d9.dll), and
# native D3DX9 shader reflection (d3dx9_43.dll + d3dcompiler_43.dll).
# ==============================================================================
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${DIR}"

EXE="${DIR}/hon_x64.exe"
if [ ! -f "${EXE}" ] && [ -f "${DIR}/windows-x64/hon_x64.exe" ]; then
    EXE="${DIR}/windows-x64/hon_x64.exe"
fi

WINE_BIN="${WINE:-}"
if [ -z "${WINE_BIN}" ]; then
    for cand in /opt/wine-stable/bin/wine64 /opt/wine-stable/bin/wine /opt/wine-staging/bin/wine64 /opt/wine-staging/bin/wine /opt/wine-devel/bin/wine64 /opt/wine-devel/bin/wine wine64 /usr/lib/wine/wine64 wine; do
        if command -v "${cand}" >/dev/null 2>&1 || [ -x "${cand}" ]; then
            WINE_BIN="${cand}"
            break
        fi
    done
    if [ -z "${WINE_BIN}" ]; then
        echo "[hon-wine-vulkan.sh] ERROR: Neither wine64 nor wine was found in PATH." >&2
        echo "[hon-wine-vulkan.sh] Actionable Fix: Install WineHQ >= 10.0 ('sudo apt install winehq-stable') or run via Proton/Lutris with WINEDLLOVERRIDES='version,d3d9,d3dx9_43,d3dcompiler_43=n,b'." >&2
        exit 1
    fi
fi
case "${WINE_BIN}" in
    */*) export PATH="$(dirname "${WINE_BIN}"):${PATH}" ;;
esac

# VFS Archive Pre-Flight Sanity Check
for req_s2z in "${DIR}/game/resources0.s2z" "${DIR}/game/textures.s2z"; do
    if [ ! -r "${req_s2z}" ]; then
        echo "[hon-wine-vulkan.sh] ERROR: Required K2 VFS archive missing or unreadable: ${req_s2z}" >&2
        echo "[hon-wine-vulkan.sh] Actionable Fix: Run from a complete Project Kongor client directory containing game/*.s2z and KONGOR/updates/textures.s2z." >&2
        exit 1
    fi
done

mkdir -p "${DIR}/userdataKONGOR/dxvk_cache"
ACTIVE_PFX="${WINEPREFIX:-${HOME}/.wine}"
if [ ! -d "${ACTIVE_PFX}" ]; then
    export WINEARCH="${WINEARCH:-win64}"
fi
DLL_LIST="version,d3d9"
for d3dx_cand in "${DIR}/d3dx9_43.dll" "${DIR}/windows-x64/d3dx9_43.dll" "${ACTIVE_PFX}/drive_c/windows/system32/d3dx9_43.dll"; do
    if [ -f "${d3dx_cand}" ] && [ "$(wc -c < "${d3dx_cand}" 2>/dev/null || echo 0)" -gt 1500000 ]; then
        DLL_LIST="version,d3d9,d3dx9_43,d3dcompiler_43"
        break
    fi
done
export WINEDLLOVERRIDES="${DLL_LIST}=n,b;mscoree=d;mshtml=d${WINEDLLOVERRIDES:+;${WINEDLLOVERRIDES}}"
export DXVK_CONFIG_FILE="${DXVK_CONFIG_FILE:-${DIR}/userdataKONGOR/dxvk.conf}"
export DXVK_STATE_CACHE_PATH="${DXVK_STATE_CACHE_PATH:-${DIR}/userdataKONGOR/dxvk_cache}"
export DXVK_SHADER_CACHE_PATH="${DXVK_SHADER_CACHE_PATH:-${DIR}/userdataKONGOR/dxvk_cache}"
export WINEDEBUG="${WINEDEBUG:--all}"

# If the client tree uses POSIX symlinked directories (e.g. Lutris/Proton split staging),
# bypass NativeGuard's Win32 reparse-point skipping in VFS_LOOSE_CACHE so core/shaders & overrides load cleanly.
if [ -n "$(find "${DIR}" -maxdepth 2 -type l 2>/dev/null | head -n 1)" ]; then
    export KONGOR_DISABLE_VFS_LOOSE_CACHE="${KONGOR_DISABLE_VFS_LOOSE_CACHE:-1}"
fi

# Default to vid_vulkan on hardware GPUs (enables $waterdistortion 0px RT clamp & frame telemetry);
# fall back to vid_d3d9 only if vid_vulkan_x64.dll is absent or running on CPU llvmpipe without KONGOR_ALLOW_SOFTWARE_VULKAN=1.
DEFAULT_VID="vid_d3d9"
if [ -f "${DIR}/vid_vulkan_x64.dll" ] || [ -f "${DIR}/windows-x64/vid_vulkan_x64.dll" ]; then
    if [ "${KONGOR_ALLOW_SOFTWARE_VULKAN:-0}" = "1" ]; then
        DEFAULT_VID="vid_vulkan"
    elif command -v vulkaninfo >/dev/null 2>&1; then
        if vulkaninfo --summary 2>/dev/null | grep -qE "PHYSICAL_DEVICE_TYPE_(DISCRETE|INTEGRATED|VIRTUAL)_GPU"; then
            DEFAULT_VID="vid_vulkan"
        fi
    else
        DEFAULT_VID="vid_vulkan"
    fi
fi
VID_DRIVER="${KONGOR_VID_DRIVER:-${DEFAULT_VID}}"

has_mod_arg=0
has_vid_arg=0
for arg in "$@"; do
    case "${arg}" in
        -mod) has_mod_arg=1 ;;
        -vid) has_vid_arg=1 ;;
    esac
done

MOD_ARGS=()
if [ "${has_mod_arg}" -eq 0 ] && { [ -d "${DIR}/override" ] || [ -d "${DIR}/ClientPackageFiles" ]; }; then
    MOD_ARGS=(-mod "base;game;ClientPackageFiles;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions;override")
fi

VID_ARGS=()
if [ "${has_vid_arg}" -eq 0 ]; then
    VID_ARGS=(-vid "${VID_DRIVER}")
fi

echo "[hon-wine-vulkan.sh] Launching ${EXE} via ${WINE_BIN} (driver=${VID_DRIVER}, WINEDLLOVERRIDES=${WINEDLLOVERRIDES})"
exec "${WINE_BIN}" "${EXE}" "${VID_ARGS[@]}" "${MOD_ARGS[@]}" "$@"
EOF_WINE
        chmod +x "${WINE_WRAPPER}"
        log_success "Generated Wine/Proton DXVK launcher: ${WINE_WRAPPER}"
    fi
fi

# ------------------------------------------------------------------------------
# 3. Configure Native Linux ELF Client (hon-x86_64) via Mesa Zink (OpenGL->Vulkan)
# ------------------------------------------------------------------------------
if { [ "${INSTALL_MODE}" = "auto" ] || [ "${INSTALL_MODE}" = "native" ]; } && [ -n "${NATIVE_LINUX_DIR}" ]; then
    log_info "Configuring Native Linux ELF Vulkan (Mesa Zink) wrapper in: ${NATIVE_LINUX_DIR}"
    ZINK_WRAPPER="${NATIVE_LINUX_DIR}/hon-vulkan-zink.sh"
    if [ "${DRY_RUN}" -eq 1 ]; then
        log_info "[DRY-RUN] Would generate ${ZINK_WRAPPER} delegating to ${NATIVE_LINUX_DIR}/hon.sh with KONGOR_USE_ZINK=1"
    else
        cat > "${ZINK_WRAPPER}" <<'EOF_ZINK'
#!/usr/bin/env bash
# ==============================================================================
# Project Kongor - Native Linux ELF (hon-x86_64) Vulkan Wrapper via Mesa Zink
# Routes vid_gl2-x86_64.so OpenGL calls over Vulkan 1.3/1.4 (MESA_LOADER_DRIVER_OVERRIDE=zink)
# while preserving libkongor_native_guard.so multi-monitor & frame pacing hooks.
# ==============================================================================
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "${DIR}/userdata/shadercache"

export KONGOR_USE_ZINK=1
export MESA_LOADER_DRIVER_OVERRIDE=zink
export mesa_glthread=true
export MESA_SHADER_CACHE_DIR="${MESA_SHADER_CACHE_DIR:-${DIR}/userdata/shadercache}"
export MESA_SHADER_CACHE_MAX_SIZE="${MESA_SHADER_CACHE_MAX_SIZE:-2G}"
export MESA_DISK_CACHE_SINGLE_FILE=1

echo "[hon-vulkan-zink.sh] Launching native hon-x86_64 with Mesa Zink (OpenGL -> Vulkan)..."
exec "${DIR}/hon.sh" "$@"
EOF_ZINK
        chmod +x "${ZINK_WRAPPER}"
        log_success "Generated Native Linux Zink Vulkan launcher: ${ZINK_WRAPPER}"
    fi
fi

echo ""
log_success "Vulkan setup complete!"
if [ -n "${HON_EXE}" ]; then
    echo -e "${GREEN}  -> Wine/Proton DXVK 3.x: run '${GAME_DIR}/hon-wine-vulkan.sh' (or set Lutris/Steam launch option: WINEDLLOVERRIDES=\"version,d3d9,d3dx9_43,d3dcompiler_43=n,b\" %command%)${NC}"
fi
if [ -n "${NATIVE_LINUX_DIR}" ]; then
    echo -e "${GREEN}  -> Native Linux ELF:     run '${NATIVE_LINUX_DIR}/hon.sh' (OpenGL) or '${NATIVE_LINUX_DIR}/hon-vulkan-zink.sh' (Mesa Zink Vulkan)${NC}"
fi
echo ""
