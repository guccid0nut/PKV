// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * Windows x64 __fastcall Hook Template for Project Kongor (Frida DBI)
 * 
 * Windows x64 Calling Convention:
 *   - RCX: First integer / pointer argument (or 'this' pointer for member methods)
 *   - RDX: Second argument
 *   - R8:  Third argument
 *   - R9:  Fourth argument
 *   - [RSP + 0x28]: Fifth argument (after 32-byte shadow space + return address)
 */

function hookFastcall(moduleName, rvaOrExport, argCount = 4, name = "Function") {
    const targetModule = Process.findModuleByName(moduleName);
    if (!targetModule) {
        console.error(`[-] Module ${moduleName} not found!`);
        return;
    }

    const targetAddress = typeof rvaOrExport === "string" && rvaOrExport.startsWith("0x")
        ? targetModule.base.add(ptr(rvaOrExport))
        : targetModule.findExportByName(rvaOrExport) || targetModule.base.add(ptr(rvaOrExport));

    if (!targetAddress) {
        console.error(`[-] Failed to resolve ${rvaOrExport} in ${moduleName}`);
        return;
    }

    console.log(`[+] Attaching hook to ${name} at ${moduleName}+${targetAddress.sub(targetModule.base)} (${targetAddress})`);

    let callCount = 0;
    const MAX_BURST_LOGS = 100; // Guard against tight-loop spam crashes

    Interceptor.attach(targetAddress, {
        onEnter: function (args) {
            callCount++;
            if (callCount > MAX_BURST_LOGS) return;

            const rcx = this.context.rcx;
            const rdx = this.context.rdx;
            const r8 = this.context.r8;
            const r9 = this.context.r9;

            console.log(`\n[>>> ${name} #${callCount}]`);
            console.log(`    RCX (this/arg0): ${rcx}`);
            if (argCount >= 2) console.log(`    RDX (arg1):      ${rdx}`);
            if (argCount >= 3) console.log(`    R8  (arg2):      ${r8}`);
            if (argCount >= 4) console.log(`    R9  (arg3):      ${r9}`);

            // Inspect potential string pointers safely
            safeInspectString("RCX", rcx);
            safeInspectString("RDX", rdx);
        },
        onLeave: function (retval) {
            if (callCount <= MAX_BURST_LOGS) {
                console.log(`[<<< ${name} Return]: ${retval}`);
            }
        }
    });
}

function safeInspectString(label, pointerVal) {
    if (pointerVal.isNull()) return;
    try {
        // Only inspect if in a valid committed memory range
        const memRange = Process.findRangeByAddress(pointerVal);
        if (memRange && memRange.protection.includes("r")) {
            const str = pointerVal.readUtf8String(64);
            if (str && str.length > 2 && /^[\x20-\x7E]+$/.test(str)) {
                console.log(`        ↳ String (${label}): "${str}"`);
            }
        }
    } catch (e) {
        // Non-string pointer or inaccessible memory; silently ignore
    }
}

// Example usage:
// hookFastcall("game_x64.dll", "0x35d40", 3, "FUN_180035d40_PacketParser");
