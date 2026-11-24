#!/bin/bash
# SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ============================================================================
# File: load_xdp.sh
# System: Heroes of Newerth - Dedicated Game Server Cluster
# Subsystem: eBPF / XDP Loader & Dynamic Port Map Synchronizer
# Milestone 4.1: Dynamic Linux Kernel UDP Filtering & COMPEL Port Map Sync
# ============================================================================

set -e

ACTION="${1:-status}"
IFACE="${2:-eth0}"
XDP_PROG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BPF_SRC="$XDP_PROG_DIR/xdp_kongor_filter.c"
BPF_OBJ="$XDP_PROG_DIR/xdp_kongor_filter.o"
BPF_FS_DIR="/sys/fs/bpf"
BPF_PINNED_PORTS="$BPF_FS_DIR/kongor_active_ports"
BPF_PINNED_STATS="$BPF_FS_DIR/kongor_stats"

print_usage() {
    echo "Usage: $0 [build|load|unload|status] [interface (default: eth0)]"
    echo ""
    echo "Commands:"
    echo "  build   - Compile eBPF source to object file (requires clang & llvm)"
    echo "  load    - Mount bpffs, attach XDP filter to interface, and pin active ports map"
    echo "  unload  - Detach XDP filter and unpin active maps"
    echo "  status  - Show attached XDP programs, registered active ports, and telemetry drops"
    exit 1
}

build_bpf() {
    echo "[XDP] Compiling $BPF_SRC -> $BPF_OBJ..."
    clang -O2 -g -Wall -target bpf -c "$BPF_SRC" -o "$BPF_OBJ"
    echo "[XDP] Build complete."
}

ensure_bpffs() {
    if [ ! -d "$BPF_FS_DIR" ]; then
        mkdir -p "$BPF_FS_DIR" 2>/dev/null || true
    fi
    if ! mountpoint -q "$BPF_FS_DIR" 2>/dev/null; then
        echo "[XDP] Mounting bpffs at $BPF_FS_DIR..."
        mount -t bpf bpf "$BPF_FS_DIR" 2>/dev/null || true
    fi
}

load_bpf() {
    if [ ! -f "$BPF_OBJ" ]; then
        build_bpf
    fi

    ensure_bpffs

    echo "[XDP] Attaching $BPF_OBJ to interface $IFACE..."
    # Try native driver mode first; fallback to generic/skb mode if unsupported by NIC
    if ip link set dev "$IFACE" xdpdrv obj "$BPF_OBJ" sec xdp 2>/dev/null; then
        echo "[XDP] Successfully attached in Native Driver (XDP_DRV) mode on $IFACE."
    elif ip link set dev "$IFACE" xdp obj "$BPF_OBJ" sec xdp 2>/dev/null; then
        echo "[XDP] Successfully attached in standard XDP mode on $IFACE."
    else
        echo "[XDP] Driver mode unavailable; attaching in Generic (XDP_SKB) mode on $IFACE..."
        ip link set dev "$IFACE" xdpgeneric obj "$BPF_OBJ" sec xdp
        echo "[XDP] Successfully attached in Generic mode on $IFACE."
    fi

    # Pin kongor_active_ports map to /sys/fs/bpf/kongor_active_ports if bpftool is available
    if command -v bpftool >/dev/null 2>&1; then
        if [ ! -e "$BPF_PINNED_PORTS" ]; then
            echo "[XDP] Pinning kongor_active_ports to $BPF_PINNED_PORTS..."
            bpftool map pin name kongor_active_ports "$BPF_PINNED_PORTS" 2>/dev/null || true
        fi
        if [ ! -e "$BPF_PINNED_STATS" ]; then
            bpftool map pin name kongor_stats "$BPF_PINNED_STATS" 2>/dev/null || true
        fi
    fi
}

unload_bpf() {
    echo "[XDP] Detaching XDP filter from interface $IFACE..."
    ip link set dev "$IFACE" xdp off 2>/dev/null || true
    ip link set dev "$IFACE" xdpgeneric off 2>/dev/null || true
    rm -f "$BPF_PINNED_PORTS" "$BPF_PINNED_STATS" 2>/dev/null || true
    echo "[XDP] Filter detached and pinned maps removed."
}

show_status() {
    echo "======================================================================"
    echo "KONGOR DEDICATED GAME SERVER - eBPF / XDP STATUS"
    echo "======================================================================"
    echo "Interface: $IFACE"
    ip link show dev "$IFACE" | grep -i xdp || echo "No active XDP program attached to $IFACE."
    echo ""
    if command -v bpftool >/dev/null 2>&1; then
        echo "Active BPF Maps:"
        bpftool map show name kongor_active_ports 2>/dev/null || bpftool map show pinned "$BPF_PINNED_PORTS" 2>/dev/null || echo "Map 'kongor_active_ports' not loaded."
        echo ""
        echo "Active Dedicated Server Ports Registered in Kernel:"
        bpftool map dump name kongor_active_ports 2>/dev/null || bpftool map dump pinned "$BPF_PINNED_PORTS" 2>/dev/null || true
        echo ""
        echo "Telemetry Counters:"
        bpftool map dump name kongor_stats 2>/dev/null || bpftool map dump pinned "$BPF_PINNED_STATS" 2>/dev/null || true
    fi
    echo "======================================================================"
}

case "$ACTION" in
    build)
        build_bpf
        ;;
    load)
        load_bpf
        ;;
    unload)
        unload_bpf
        ;;
    status)
        show_status
        ;;
    *)
        print_usage
        ;;
esac
