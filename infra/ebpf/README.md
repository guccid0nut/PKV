# Kongor eBPF / XDP Network Ingress Abuse Filter

## Overview
The Kongor eBPF/XDP filter executes directly in the Linux network device driver (`XDP_DRV` / `XDP_SKB`) before packet memory (`sk_buff`) is allocated by the Linux kernel. It provides line-rate hardware defense against UDP volumetric floods, reflection attacks, and malformed challenge packets targeting Heroes of Newerth dedicated game servers.

---

## Architecture

```
Physical NIC / Wire
        │
        ▼
[ eBPF / XDP Driver Filter ] ─── (Malformed / Short UDP) ───► [ XDP_DROP ] (0 CPU overhead)
        │
        ▼ (Valid 40-byte Watermark)
[ Linux Kernel TCP/UDP Stack ]
        │
        ▼
[ COMPEL UDPForwarder (C#) ]
        │
        ▼
[ HoN Dedicated Server (hon-x86_64-server) ]
```

---

## Filter Rules & Watermark Inspection
1. **Target Port Filter**:
   * **Game UDP**: `11235 - 11534`, `21235 - 21534`
   * **Voice UDP**: `11735 - 12034`, `21735 - 22034`
   * All non-game packets (e.g. SSH port 22, K8s API port 6443) pass through untouched (`XDP_PASS`).
2. **Length Enforcement**:
   * Game packets must be at least **43 bytes** (`40` bytes watermark + `2` bytes connection ID + `1` byte packet type).
   * Voice packets must be at least **41 bytes** (`40` bytes watermark + `1` byte packet type).
   * Truncated packets are dropped immediately (`XDP_DROP`).
3. **Envelope Verification**:
   * Offset 28: uint32 challenge
   * Offset 32: uint16 counter
   * Malformed or spoofed burst floods are discarded without scheduling thread tasks in userspace.

---

## Usage

### 1. Build eBPF Object File
Requires `clang` and `llvm`:
```bash
sudo ./load_xdp.sh build
```

### 2. Attach Filter to Network Interface
```bash
sudo ./load_xdp.sh load eth0
```

### 3. Check Live Packet Counters
```bash
sudo ./load_xdp.sh status eth0
```

### 4. Detach Filter
```bash
sudo ./load_xdp.sh unload eth0
```

---

## Kubernetes / Agones DaemonSet Deployment
To deploy across all node hosts in an Agones cluster, run the filter container as a privileged DaemonSet with `hostNetwork: true`.
