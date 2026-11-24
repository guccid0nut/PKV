# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""Standalone network guard: kills game processes that connect to any non-local address. Logs to guard.log."""
import datetime
import os
from pathlib import Path
import sys
import time

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

from tao_session import NetGuard  # noqa: E402

LOG_PATH = os.environ.get(
    "KONGOR_NETGUARD_LOG",
    str(REPO_ROOT / "artifacts" / "audits" / "guard.log"),
)


def log(msg: str) -> None:
    os.makedirs(os.path.dirname(LOG_PATH), exist_ok=True)
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(f"{datetime.datetime.now().isoformat(timespec='seconds')} {msg}\n")


def main() -> None:
    g = NetGuard()
    log("guard started")
    known = set()
    while g.violation is None:
        new = g.seen_local - known
        if new:
            known |= new
            log(f"local peers: {sorted(new)}")
        time.sleep(0.5)
    log(f"VIOLATION {g.violation} -> process killed")
    print(f"VIOLATION {g.violation}")


if __name__ == "__main__":
    main()
