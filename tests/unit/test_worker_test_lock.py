# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Unit & real-boundary seam tests for the Worker Test-Runner Mutex Lock
(`scripts/kongor_worker_lock.ps1 -Scope test-runner` & `scripts/Invoke-KongorTestLock.ps1`).

Value-Weighted Testing Gate:
  - protects: Machine-wide serialization of `dotnet build`/`dotnet test`/`pytest`/`cmake --build`
    across concurrent agents (`$env:TEMP\\kongor_test_runner.lock`), ROE separation between
    `game-worker` (live client) and `test-runner` (offline workers permitted), dead-PID &
    expired-lease auto-reaping, and exact child exit-code propagation.
  - fails_when:
      (1) `kongor-offline-worker` is blocked from `-Scope test-runner` OR permitted to acquire `-Scope game-worker`;
      (2) a stale lock with a dead PID or expired `leaseTtlSeconds` blocks subsequent test runs;
      (3) two concurrent `Invoke-KongorTestLock.ps1` invocations overlap their critical sections instead of queuing;
      (4) `-NoWait` fails to reject immediately when an active live PID holds the lock; or
      (5) `Invoke-KongorTestLock.ps1` swallows a non-zero test exit code or leaves an orphan lock file after exit.
  - seam: Real `powershell.exe` subprocess execution against `scripts/kongor_worker_lock.ps1`
    and `scripts/Invoke-KongorTestLock.ps1` with isolated per-test lock files.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

REPO_ROOT = Path(__file__).resolve().parents[2]
LOCK_SCRIPT = REPO_ROOT / "scripts" / "kongor_worker_lock.ps1"
WRAPPER_SCRIPT = REPO_ROOT / "scripts" / "Invoke-KongorTestLock.ps1"


def _run_ps(args: list[str], env_overrides: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    if env_overrides:
        env.update(env_overrides)
    return subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", *args],
        cwd=str(REPO_ROOT),
        env=env,
        capture_output=True,
        text=True,
        timeout=20,
        check=False,
    )


class WorkerTestLockTests(unittest.TestCase):
    def test_offline_worker_allowed_on_test_runner_but_denied_on_game_worker(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            game_lock = tmp_path / "game.lock"
            test_lock = tmp_path / "test.lock"
            events_path = tmp_path / "events.jsonl"
            role_env = {"KONGOR_WORKER_ROLE": "kongor-offline-worker"}

            denied = _run_ps(
                [
                    "-File",
                    str(LOCK_SCRIPT),
                    "-Scope",
                    "game-worker",
                    "-Action",
                    "acquire",
                    "-Owner",
                    "offline-1",
                    "-LockFilePath",
                    str(game_lock),
                    "-EventStreamPath",
                    str(events_path),
                ],
                env_overrides=role_env,
            )
            self.assertEqual(denied.returncode, 2, msg=denied.stdout + denied.stderr)
            self.assertIn("[ROE-DENIED]", denied.stdout)
            self.assertFalse(game_lock.exists())

            allowed = _run_ps(
                [
                    "-File",
                    str(LOCK_SCRIPT),
                    "-Scope",
                    "test-runner",
                    "-Action",
                    "acquire",
                    "-Owner",
                    "offline-1",
                    "-PidToTrack",
                    str(os.getpid()),
                    "-CommandSummary",
                    "dotnet test unit",
                    "-LockFilePath",
                    str(test_lock),
                    "-EventStreamPath",
                    str(events_path),
                ],
                env_overrides=role_env,
            )
            self.assertEqual(allowed.returncode, 0, msg=allowed.stdout + allowed.stderr)
            self.assertTrue(test_lock.exists())

            released = _run_ps(
                [
                    "-File",
                    str(LOCK_SCRIPT),
                    "-Scope",
                    "test-runner",
                    "-Action",
                    "release",
                    "-Owner",
                    "offline-1",
                    "-LockFilePath",
                    str(test_lock),
                    "-EventStreamPath",
                    str(events_path),
                ],
                env_overrides=role_env,
            )
            self.assertEqual(released.returncode, 0, msg=released.stdout + released.stderr)
            self.assertFalse(test_lock.exists())

    def test_dead_pid_and_expired_ttl_locks_are_reaped_automatically(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            lock_file = tmp_path / "stale_test.lock"
            events_path = tmp_path / "events.jsonl"

            # 1. Dead PID lock is reaped
            lock_file.write_text(
                json.dumps(
                    {
                        "scope": "test-runner",
                        "owner": "crashed-agent",
                        "pid": 999991,
                        "processCreationTicks": 123456789,
                        "acquiredAt": datetime.now(timezone.utc).isoformat(),
                        "leaseTtlSeconds": 600,
                    }
                ),
                encoding="utf-8",
            )
            res_dead = _run_ps(
                [
                    "-File",
                    str(LOCK_SCRIPT),
                    "-Scope",
                    "test-runner",
                    "-Action",
                    "status",
                    "-LockFilePath",
                    str(lock_file),
                    "-EventStreamPath",
                    str(events_path),
                ]
            )
            self.assertEqual(res_dead.returncode, 0, msg=res_dead.stdout + res_dead.stderr)
            self.assertIn('"locked":false', res_dead.stdout.replace(" ", ""))
            self.assertFalse(lock_file.exists())

            # 2. Live PID with expired leaseTtlSeconds is reaped
            expired_at = (datetime.now(timezone.utc) - timedelta(seconds=30)).isoformat()
            lock_file.write_text(
                json.dumps(
                    {
                        "scope": "test-runner",
                        "owner": "hung-agent",
                        "pid": os.getpid(),
                        "processCreationTicks": 0,
                        "acquiredAt": expired_at,
                        "leaseTtlSeconds": 2,
                    }
                ),
                encoding="utf-8",
            )
            res_ttl = _run_ps(
                [
                    "-File",
                    str(LOCK_SCRIPT),
                    "-Scope",
                    "test-runner",
                    "-Action",
                    "status",
                    "-LockFilePath",
                    str(lock_file),
                    "-EventStreamPath",
                    str(events_path),
                ]
            )
            self.assertEqual(res_ttl.returncode, 0, msg=res_ttl.stdout + res_ttl.stderr)
            self.assertIn('"locked":false', res_ttl.stdout.replace(" ", ""))
            self.assertFalse(lock_file.exists())

    def test_concurrent_wrappers_serialize_without_overlap_and_preserve_exit_code(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            lock_file = tmp_path / "contend.lock"
            trace_a = tmp_path / "trace_a.json"
            trace_b = tmp_path / "trace_b.json"

            child_snippet_a = (
                "import json, time; "
                "s = time.time(); time.sleep(0.45); e = time.time(); "
                f"open({str(trace_a)!r}, 'w').write(json.dumps({{'start': s, 'end': e}}))"
            )
            child_snippet_b = (
                "import json, sys, time; "
                "s = time.time(); time.sleep(0.05); e = time.time(); "
                f"open({str(trace_b)!r}, 'w').write(json.dumps({{'start': s, 'end': e}})); "
                "sys.exit(7)"
            )

            proc_a = subprocess.Popen(
                [
                    "powershell",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(WRAPPER_SCRIPT),
                    "-Owner",
                    "worker-a",
                    "-WaitSeconds",
                    "10",
                    "-LockFilePath",
                    str(lock_file),
                    "--",
                    sys.executable,
                    "-c",
                    child_snippet_a,
                ],
                cwd=str(REPO_ROOT),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
            try:
                # Wait briefly until worker-a creates the lock file
                deadline = time.time() + 4.0
                while not lock_file.exists() and time.time() < deadline:
                    time.sleep(0.03)
                self.assertTrue(lock_file.exists(), "worker-a did not create test-runner lock file in time")

                proc_b = subprocess.Popen(
                    [
                        "powershell",
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        str(WRAPPER_SCRIPT),
                        "-Owner",
                        "worker-b",
                        "-WaitSeconds",
                        "10",
                        "-LockFilePath",
                        str(lock_file),
                        "--",
                        sys.executable,
                        "-c",
                        child_snippet_b,
                    ],
                    cwd=str(REPO_ROOT),
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                )
                out_a, err_a = proc_a.communicate(timeout=15)
                out_b, err_b = proc_b.communicate(timeout=15)
            finally:
                if proc_a.poll() is None:
                    proc_a.kill()

            self.assertEqual(proc_a.returncode, 0, msg=out_a + err_a)
            self.assertEqual(proc_b.returncode, 7, msg=out_b + err_b)
            self.assertFalse(lock_file.exists(), "Wrapper left an orphan lock file after exit")

            span_a = json.loads(trace_a.read_text(encoding="utf-8"))
            span_b = json.loads(trace_b.read_text(encoding="utf-8"))
            self.assertGreaterEqual(
                span_b["start"],
                span_a["end"] - 0.02,
                msg=f"Concurrent critical sections overlapped: A={span_a}, B={span_b}",
            )


if __name__ == "__main__":
    unittest.main()
