// SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
//
// Linux eBPF/XDP kernel entry point using `aya-ebpf`.
// Delegates all packet classification to the shared `#![no_std]` + `#![forbid(unsafe_code)]`
// state machine in `kongor_xdp_aya::filter_logic`.

#![no_std]
#![no_main]

use aya_ebpf::{
    bindings::xdp_action,
    macros::{map, xdp},
    maps::{HashMap, PerCpuArray},
    programs::XdpContext,
};
use kongor_xdp_aya::filter_logic::{
    filter_packet, KongorPortEntry, StatCounter, XdpAction, STAT_MAX_ENTRIES,
};

#[map(name = "kongor_active_ports")]
static KONGOR_ACTIVE_PORTS: HashMap<u16, KongorPortEntry> =
    HashMap::pinned(1024, 0);

#[map(name = "kongor_stats")]
static KONGOR_STATS: PerCpuArray<u64> =
    PerCpuArray::with_max_entries(STAT_MAX_ENTRIES as u32, 0);

#[inline(always)]
fn increment_stat(stat: StatCounter) {
    if let Some(ptr) = KONGOR_STATS.get_ptr_mut(stat as u32) {
        // SAFETY: `PerCpuArray::get_ptr_mut` yields a valid per-CPU mutable slot for the
        // current CPU executing the XDP program (non-preemptible in BPF execution context).
        unsafe {
            *ptr += 1;
        }
    }
}

#[inline(always)]
fn map_xdp_action(action: XdpAction) -> u32 {
    match action {
        XdpAction::Aborted => xdp_action::XDP_ABORTED,
        XdpAction::Drop => xdp_action::XDP_DROP,
        XdpAction::Pass => xdp_action::XDP_PASS,
        XdpAction::Tx => xdp_action::XDP_TX,
        XdpAction::Redirect => xdp_action::XDP_REDIRECT,
    }
}

#[xdp]
pub fn xdp_kongor_ingress_filter(ctx: XdpContext) -> u32 {
    let start = ctx.data();
    let end = ctx.data_end();
    if end < start {
        return xdp_action::XDP_PASS;
    }

    // SAFETY: `ctx.data()..ctx.data_end()` is the kernel-verified linear XDP packet slice.
    let packet = unsafe { core::slice::from_raw_parts(start as *const u8, end - start) };

    let decision = filter_packet(packet, |dst_port| {
        // SAFETY: Read-only BPF hash map lookup by u16 host-byte-order port key.
        unsafe { KONGOR_ACTIVE_PORTS.get(&dst_port).copied() }
    });

    if let Some(stat) = decision.stat {
        increment_stat(stat);
    }

    map_xdp_action(decision.action)
}

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    loop {}
}
