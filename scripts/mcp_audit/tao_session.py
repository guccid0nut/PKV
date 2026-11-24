# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""TAO MCP batch session runner and loopback network guard (`NetGuard`).

Usage:
  python scripts/mcp_audit/tao_session.py list <out.json>
  python scripts/mcp_audit/tao_session.py run <calls.json> <out.json> [per_call_timeout_s]

calls.json: [{"tool": "...", "args": {...}, "timeout": 60}, ...]
"""
import ipaddress
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

import psutil

REPO = str(Path(__file__).resolve().parents[2])
GUARDED = ("hon_x64.exe", "hon_server_x64.exe", "hon_server.exe", "k2_x64.exe")


def resolve_kongor_dll() -> str:
    env_override = os.environ.get("KONGOR_MCP_DLL_PATH", "").strip()
    if env_override and os.path.isfile(env_override):
        return env_override

    candidates = [
        os.path.join(REPO, "src", "tools", "kongor-cli", "bin", "Release", "net11.0", "kongor.dll"),
        os.path.join(REPO, "artifacts", "prepush-gate", "bin", "Kongor.Cli", "release", "kongor.dll"),
        os.path.join(REPO, "src", "tools", "kongor-cli", "bin", "Debug", "net11.0", "kongor.dll"),
    ]
    existing = [p for p in candidates if os.path.isfile(p)]
    if existing:
        return max(existing, key=os.path.getmtime)
    return candidates[0]


class NetGuard:
    """Kills guarded game processes the moment one holds a TCP or UDP connection to a non-local address."""

    def __init__(self):
        self.violation = None
        self.local = set()
        for addrs in psutil.net_if_addrs().values():
            for a in addrs:
                self.local.add(a.address.split("%")[0])
        self.seen_local = set()
        self.stop = False
        threading.Thread(target=self._loop, daemon=True).start()

    def _is_local(self, ip: str) -> bool:
        if ip in self.local:
            return True
        try:
            addr = ipaddress.ip_address(ip)
            return addr.is_loopback or addr.is_unspecified
        except ValueError:
            return False

    def _loop(self):
        while not self.stop and self.violation is None:
            for p in psutil.process_iter(["name", "pid"]):
                if (p.info["name"] or "").lower() not in GUARDED:
                    continue
                try:
                    conns = p.net_connections(kind="inet")
                except (psutil.NoSuchProcess, psutil.AccessDenied) as exc:
                    print(f"[NetGuard] cannot read connections of pid {p.info['pid']}: {exc}", file=sys.stderr)
                    continue
                for c in conns:
                    if not c.raddr:
                        continue
                    if self._is_local(c.raddr.ip):
                        self.seen_local.add(f"{p.info['name']}->{c.raddr.ip}:{c.raddr.port}")
                        continue
                    self.violation = f"{p.info['name']} pid={p.info['pid']} -> {c.raddr.ip}:{c.raddr.port} ({c.status})"
                    print(f"[NetGuard] EXTERNAL CONNECTION {self.violation}; killing process", file=sys.stderr)
                    try:
                        p.kill()
                    except psutil.Error as exc:
                        print(
                            f"[NetGuard] KILL FAILED pid={p.info['pid']}: {exc}. "
                            f"Run: Stop-Process -Id {p.info['pid']} -Force",
                            file=sys.stderr,
                        )
                    break
            time.sleep(0.25)


def resolve_dotnet_executable() -> str:
    local_dotnet = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "dotnet", "dotnet.exe")
    if os.name == "nt" and os.path.isfile(local_dotnet):
        return local_dotnet
    return "dotnet"


class Session:
    def __init__(
        self,
        protocol_version: str = "2024-11-05",
        extra_env: dict[str, str] | None = None,
        extra_args: list[str] | None = None,
    ):
        env = os.environ.copy()
        env["KONGOR_DISABLE_JOB_OBJECT"] = "1"
        if extra_env:
            for k, v in extra_env.items():
                if v is None:
                    env.pop(k, None)
                else:
                    env[k] = v
        dll_path = resolve_kongor_dll()
        if not os.path.isfile(dll_path):
            raise FileNotFoundError(
                f"Compiled kongor.dll not found at '{dll_path}'. "
                "Run: ./scripts/Invoke-KongorTestLock.ps1 -Owner 'build' -- dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release"
            )
        dotnet_exe = resolve_dotnet_executable()
        if dotnet_exe != "dotnet":
            env["DOTNET_ROOT"] = os.path.dirname(dotnet_exe)
        cmd = [dotnet_exe, dll_path, "mcp"] + (extra_args or [])
        self.proc = subprocess.Popen(
            cmd,
            cwd=REPO,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
            text=True,
            bufsize=1,
            encoding="utf-8",
            errors="replace",
        )
        self.out_q = queue.Queue()
        self.stderr_lines = []
        threading.Thread(target=self._pump_out, daemon=True).start()
        threading.Thread(target=self._pump_err, daemon=True).start()
        self.next_id = 1
        self.init_response = self.request(
            "initialize",
            {
                "protocolVersion": protocol_version,
                "capabilities": {},
                "clientInfo": {"name": "KongorMcpAudit", "version": "1.0"},
            },
            60,
        )
        self.notify("notifications/initialized")

    def _pump_out(self):
        for line in self.proc.stdout:
            self.out_q.put(line)
        self.out_q.put(None)

    def _pump_err(self):
        for line in self.proc.stderr:
            self.stderr_lines.append(line.rstrip("\n"))

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params, timeout):
        rid = self.next_id
        self.next_id += 1
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}) + "\n")
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while True:
            remaining = deadline - time.time()
            if remaining <= 0:
                return {"__timeout__": True, "after_s": timeout}
            try:
                line = self.out_q.get(timeout=remaining)
            except queue.Empty:
                return {"__timeout__": True, "after_s": timeout}
            if line is None:
                return {"__eof__": True}
            try:
                msg = json.loads(line)
            except Exception:
                continue
            if msg.get("id") == rid:
                return msg

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception as exc:
            print(f"stdin close failed: {exc}", file=sys.stderr)
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()


def summarize(msg, limit):
    if "__timeout__" in msg or "__eof__" in msg:
        return {"transport": msg}
    if "error" in msg:
        return {"jsonrpc_error": msg["error"]}
    res = msg.get("result", {})
    texts = []
    images = 0
    for c in res.get("content", []):
        if c.get("type") == "text":
            texts.append(c.get("text", ""))
        elif c.get("type") == "image":
            images += 1
    text = "\n".join(texts)
    out = {"isError": res.get("isError", False), "images": images, "text_len": len(text)}
    try:
        parsed = json.loads(text)
        out["json"] = True
        if isinstance(parsed, dict):
            out["top_keys"] = list(parsed.keys())[:40]
            for k in (
                "status",
                "success",
                "ok",
                "error",
                "errorCode",
                "message",
                "verdict",
                "channel",
                "executionFidelity",
                "executionMode",
                "fallbackReason",
                "mcpIssueId",
                "remediation_call",
                "hint",
            ):
                if k in parsed:
                    v = parsed[k]
                    s = json.dumps(v) if not isinstance(v, str) else v
                    out[k] = s[:600]
    except Exception:
        out["json"] = False
    out["text"] = text[:limit]
    return out


def main():
    mode = sys.argv[1]
    if mode == "list":
        s = Session()
        msg = s.request("tools/list", {}, 60)
        s.close()
        tools = msg.get("result", {}).get("tools", [])
        with open(sys.argv[2], "w", encoding="utf-8") as f:
            json.dump(tools, f, indent=1)
        print(f"tools: {len(tools)}")
        for t in tools:
            schema = t.get("inputSchema", {})
            props = list((schema.get("properties") or {}).keys())
            req = schema.get("required") or []
            print(f"{t['name']} | req={req} | props={props[:14]}")
        return
    with open(sys.argv[2], encoding="utf-8") as f_in:
        calls = json.load(f_in)
    limit = int(sys.argv[4]) if len(sys.argv) > 4 else 80000
    guard = NetGuard()
    s = Session()
    for c in calls:
        if guard.violation:
            print(f"ABORTED BATCH before {c['tool']}: {guard.violation}")
            break
        t0 = time.time()
        mark = len(s.stderr_lines)
        msg = s.request("tools/call", {"name": c["tool"], "arguments": c.get("args", {})}, c.get("timeout", 60))
        dt = round((time.time() - t0) * 1000)
        summ = summarize(msg, limit)
        summ["tool"] = c["tool"]
        summ["args"] = c.get("args", {})
        summ["elapsed_ms"] = dt
        warn = [l for l in s.stderr_lines[mark:] if any(w in l.lower() for w in ("warn", "error", "fail", "exception"))]
        summ["stderr_flags"] = warn[:6]
        print(
            f"{c['tool']} ms={dt} isError={summ.get('isError')} "
            f"status={str(summ.get('status'))[:40]} transport={summ.get('transport')}"
        )
        with open(sys.argv[3], "a", encoding="utf-8") as f:
            f.write(json.dumps(summ) + "\n")
        sys.stdout.flush()
        if c.get("settle"):
            time.sleep(c["settle"])
        if "transport" in summ:
            print("!! transport failure, restarting session", file=sys.stderr)
            s.close()
            s = Session()
    s.close()
    guard.stop = True
    print(f"NETGUARD violation={guard.violation} local_peers={sorted(guard.seen_local)[:12]}")


if __name__ == "__main__":
    main()
