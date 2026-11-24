#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# Project Kongor: Attach/Detach XDP Scrubber to Network Interface
set -euo pipefail

INTERFACE=${1:-eth0}
ACTION=${2:-attach}
BPF_OBJ="kongor_xdp_filter.bpf.o"

if [ "$ACTION" == "attach" ]; then
    echo "[ProjectKongor] Attaching XDP scrubber to ${INTERFACE}..."
    if ip link set dev "${INTERFACE}" xdpdrv obj "${BPF_OBJ}" sec xdp 2>/dev/null; then
        echo "[ProjectKongor] Attached using native driver mode (xdpdrv) at line-rate."
    else
        echo "[ProjectKongor] Native driver mode not supported on ${INTERFACE}, falling back to generic mode (xdpgeneric)..."
        ip link set dev "${INTERFACE}" xdpgeneric obj "${BPF_OBJ}" sec xdp
        echo "[ProjectKongor] Attached in generic mode."
    fi
elif [ "$ACTION" == "detach" ]; then
    echo "[ProjectKongor] Detaching XDP scrubber from ${INTERFACE}..."
    ip link set dev "${INTERFACE}" xdp off 2>/dev/null || ip link set dev "${INTERFACE}" xdpgeneric off
    echo "[ProjectKongor] Detached successfully."
else
    echo "Usage: $0 <interface> [attach|detach]"
    exit 1
fi
