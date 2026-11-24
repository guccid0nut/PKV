// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * VTable Sniffer for K2 Engine C++ Objects (Frida DBI)
 * 
 * Inspects virtual method tables from C++ 'this' pointers passed in RCX.
 * Maps function pointers to module names and RVAs to populate Evidence Dossiers.
 */

function inspectVTable(thisPtr, slotCount = 30) {
    if (!thisPtr || thisPtr.isNull()) {
        console.error("[-] Null this pointer passed to inspectVTable");
        return;
    }

    try {
        const vtablePtr = thisPtr.readPointer();
        console.log(`\n[+] Object at ${thisPtr} -> VTable at ${vtablePtr}`);

        for (let slot = 0; slot < slotCount; slot++) {
            const funcPtr = vtablePtr.add(slot * 8).readPointer();
            if (funcPtr.isNull()) break;

            const mod = Process.findModuleByAddress(funcPtr);
            const modInfo = mod ? `${mod.name}+${funcPtr.sub(mod.base)}` : "UNKNOWN_MEMORY";

            console.log(`    [Slot ${slot.toString().padStart(2, ' ')} | offset 0x${(slot * 8).toString(16).padStart(3, '0')}] -> ${funcPtr} (${modInfo})`);
        }
    } catch (e) {
        console.error(`[-] Failed to inspect VTable: ${e.message}`);
    }
}
