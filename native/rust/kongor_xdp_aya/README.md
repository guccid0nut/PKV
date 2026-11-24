# Kongor Pure-Rust `aya` XDP Ingress Filter (`kongor_xdp_aya`)

Memory-safe Rust counterpart to `infra/ebpf/xdp_kongor_filter.c` for Project Kongor dedicated game servers.

## Architecture

- **`src/filter_logic.rs`** (`#![no_std]`, `#![forbid(unsafe_code)]`, zero external crates):
  - Byte-for-byte behavioral parity with `infra/ebpf/xdp_kongor_filter.c`:
    - Ethernet header check + IEEE 802.1Q (`0x8100`) / 802.1ad (`0x88A8`) VLAN unwrap (`<= 2` tags)
    - IPv4 (variable IHL `20..=60` bounds validation) & IPv6 + UDP destination port extraction
    - Dynamic `kongor_active_ports` map lookup (`KongorPortEntry { pid, flags, registered_at_ns }`) + static HoN server port range fallback (`11235..=11534`, `11735..=12034`, `21235..=21534`, `21735..=22034`)
    - Direct K2 minimum payload enforcement (`>= 3B` game / `>= 2B` voice)
    - COMPEL proxy 40-byte watermark envelope validation (`>= 43B` game / `>= 41B` voice, challenge @ 28, counter @ 32, rejecting all-zero and all-ones flood sentinels)
    - Exact 7-slot telemetry counter indices (`STAT_PASS_GAME` .. `STAT_DROP_UNREGISTERED_PORT`)
  - Compiles and unit-tests natively on Windows and Linux hosts via `cargo test` with no `clang` or `bpf-linker`.

- **`ebpf/src/main.rs`** (`aya-ebpf`, excluded from host workspace):
  - Thin Linux kernel `#[xdp]` program binding pinned `kongor_active_ports` (`HashMap<u16, KongorPortEntry>`) and `kongor_stats` (`PerCpuArray<u64>`) directly to `kongor_xdp_aya::filter_logic::filter_packet`.

## Host Unit Tests (Windows & Linux, Offline)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 -Owner xdp-test -- cargo test --offline --manifest-path native/rust/Cargo.toml -p kongor_xdp_aya
```

## Linux Kernel BPF Compilation (`bpf-linker`)

On a Linux build host with `rustup`, `nightly` toolchain + `rust-src`, and `bpf-linker`:

```bash
rustup toolchain install nightly --component rust-src
cargo install bpf-linker
cd native/rust/kongor_xdp_aya/ebpf
cargo +nightly build \
  --target bpfel-unknown-none \
  -Z build-std=core \
  --release
```

The resulting ELF object (`target/bpfel-unknown-none/release/kongor-xdp-aya`) can be attached with `aya` userspace loaders or `bpftool` using the same `/sys/fs/bpf/kongor_active_ports` pinned map schema consumed by COMPEL.
