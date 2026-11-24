// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * Network Packet Dispatcher Tracer for game_x64.dll (Frida DBI)
 * 
 * Safely intercepts the UDP packet dispatcher loop, decoding opcodes
 * and byte lengths without blocking the real-time networking thread.
 */

const GAME_MODULE = "game_x64.dll";
const PACKET_PARSER_RVA = "0x35d40"; // FUN_180035d40

const targetMod = Process.findModuleByName(GAME_MODULE);
if (!targetMod) {
    console.error(`[-] ${GAME_MODULE} is not loaded. Wait for game initialization.`);
} else {
    const fnAddr = targetMod.base.add(ptr(PACKET_PARSER_RVA));
    console.log(`[+] Tracing Packet Dispatcher at ${GAME_MODULE}+${PACKET_PARSER_RVA} (${fnAddr})`);

    let packetCount = 0;

    Interceptor.attach(fnAddr, {
        onEnter: function (args) {
            packetCount++;
            const pServer = this.context.rcx;
            const pPacket = this.context.rdx;
            const clientIdx = this.context.r8.toInt32();

            if (pPacket.isNull()) return;

            try {
                // Read 16-bit opcode from packet header
                const opcode = pPacket.readU16();
                
                // Read first 16 bytes of payload
                const payloadDump = pPacket.readByteArray(16);
                const hexStr = Array.from(new Uint8Array(payloadDump))
                    .map(b => b.toString(16).padStart(2, '0'))
                    .join(' ');

                console.log(`[PACKET #${packetCount}] Client: ${clientIdx} | Opcode: 0x${opcode.toString(16).padStart(4, '0')} | Header: ${hexStr}`);
            } catch (err) {
                console.error(`[-] Error reading packet buffer: ${err.message}`);
            }
        },
        onLeave: function (retval) {
            // Preserved for status tracking
        }
    });
}
