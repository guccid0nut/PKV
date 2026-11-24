# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

import subprocess
import pefile

pe = pefile.PE("games/server/k2_x64.dll")
fmod_imports = []
for entry in pe.DIRECTORY_ENTRY_IMPORT:
    if "fmod" in entry.dll.decode().lower():
        fmod_imports = [imp.name.decode() for imp in entry.imports if imp.name]

undname_path = r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\undname.exe"

symbols = sorted(fmod_imports)
with open("native/proxies/kongor_audio_stub/symbols.txt", "w") as f:
    for sym in symbols:
        p = subprocess.run([undname_path, sym], capture_output=True, text=True)
        undec = sym
        for line in p.stdout.splitlines():
            if line.startswith("is :- "):
                undec = line[6:].strip().strip('"')
                break
        f.write(f"{sym}\t{undec}\n")
print(f"Wrote {len(symbols)} symbols to native/proxies/kongor_audio_stub/symbols.txt")
