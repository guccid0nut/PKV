#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)
# Project Kongor Bot Deep RL Offline Trainer (PyTorch AWAC + Critic Value + Confidence)
# Shares the exact 128 -> 32 -> 16 -> 16 (4,928-param) schema with C# BotPolicyNetwork/BotPolicyTrainer.

from __future__ import annotations

import argparse
import glob
import json
import math
import os
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Tuple

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

STATE_DIM = 128
HIDDEN1 = 32
HIDDEN2 = 16
ACTION_DIM = 16
COMBO_OFFSET = 8
COMBO_COUNT = 5
WEIGHT_BOUND = 50.0


class BotPolicyMlp(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.fc1 = nn.Linear(STATE_DIM, HIDDEN1)
        self.fc2 = nn.Linear(HIDDEN1, HIDDEN2)
        self.out = nn.Linear(HIDDEN2, ACTION_DIM)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        h1 = F.relu(self.fc1(x))
        h2 = F.relu(self.fc2(h1))
        return self.out(h2)

    def load_checkpoint_dict(self, data: Dict[str, Any]) -> None:
        with torch.no_grad():
            self.fc1.weight.copy_(torch.tensor(data["w1"], dtype=torch.float32))
            self.fc1.bias.copy_(torch.tensor(data["b1"], dtype=torch.float32))
            self.fc2.weight.copy_(torch.tensor(data["w2"], dtype=torch.float32))
            self.fc2.bias.copy_(torch.tensor(data["b2"], dtype=torch.float32))
            self.out.weight.copy_(torch.tensor(data["wOut"], dtype=torch.float32))
            self.out.bias.copy_(torch.tensor(data["bOut"], dtype=torch.float32))

    def to_checkpoint_dict(
        self,
        generation: int,
        parent_generation: int,
        role: str,
        mean_loss: float,
    ) -> Dict[str, Any]:
        with torch.no_grad():
            w1 = torch.clamp(self.fc1.weight, -WEIGHT_BOUND, WEIGHT_BOUND).cpu().tolist()
            b1 = torch.clamp(self.fc1.bias, -WEIGHT_BOUND, WEIGHT_BOUND).cpu().tolist()
            w2 = torch.clamp(self.fc2.weight, -WEIGHT_BOUND, WEIGHT_BOUND).cpu().tolist()
            b2 = torch.clamp(self.fc2.bias, -WEIGHT_BOUND, WEIGHT_BOUND).cpu().tolist()
            w_out = torch.clamp(self.out.weight, -WEIGHT_BOUND, WEIGHT_BOUND).cpu().tolist()
            b_out = torch.clamp(self.out.bias, -WEIGHT_BOUND, WEIGHT_BOUND).cpu().tolist()

        return {
            "schemaVersion": 1,
            "generation": generation,
            "parentGeneration": parent_generation,
            "role": role,
            "sourceFramework": "pytorch-awac",
            "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "meanTrainingLoss": float(mean_loss),
            "meanEvalReturn": 0.0,
            "evalWinRate": 0.0,
            "evaluatedMatches": 0,
            "w1": w1,
            "b1": b1,
            "w2": w2,
            "b2": b2,
            "wOut": w_out,
            "bOut": b_out,
        }


def validate_checkpoint_dict(cp: Dict[str, Any]) -> None:
    def _chk_mat(m: List[List[float]], rows: int, cols: int, name: str) -> None:
        if len(m) != rows:
            raise ValueError(f"{name}: expected {rows} rows, got {len(m)}")
        for r in m:
            if len(r) != cols:
                raise ValueError(f"{name}: expected {cols} cols, got {len(r)}")
            for v in r:
                if not math.isfinite(v) or abs(v) > WEIGHT_BOUND:
                    raise ValueError(f"{name}: non-finite or out-of-bounds value {v}")

    def _chk_vec(v: List[float], length: int, name: str) -> None:
        if len(v) != length:
            raise ValueError(f"{name}: expected {length}, got {len(v)}")
        for x in v:
            if not math.isfinite(x) or abs(x) > WEIGHT_BOUND:
                raise ValueError(f"{name}: non-finite or out-of-bounds value {x}")

    _chk_mat(cp["w1"], HIDDEN1, STATE_DIM, "w1")
    _chk_vec(cp["b1"], HIDDEN1, "b1")
    _chk_mat(cp["w2"], HIDDEN2, HIDDEN1, "w2")
    _chk_vec(cp["b2"], HIDDEN2, "b2")
    _chk_mat(cp["wOut"], ACTION_DIM, HIDDEN2, "wOut")
    _chk_vec(cp["bOut"], ACTION_DIM, "bOut")


def render_lua_module(cp: Dict[str, Any]) -> str:
    validate_checkpoint_dict(cp)

    def fmt_vec(vec: List[float]) -> str:
        return "{" + ", ".join(f"{x:.9g}" for x in vec) + "}"

    def fmt_mat(mat: List[List[float]]) -> str:
        rows = ["    {" + ",".join(f"{x:.9g}" for x in r) + "}" for r in mat]
        return "{\n" + ",\n".join(rows) + "\n  }"

    lines = [
        "-- Auto-generated Project Kongor Bot RL Policy Weights (PyTorch AWAC)",
        "-- Architecture: 128 -> 32 (ReLU) -> 16 (ReLU) -> 16 (4,928 params)",
        "return {",
        f"  schema_version = {int(cp['schemaVersion'])},",
        f"  generation = {int(cp['generation'])},",
        f"  role = \"{cp['role']}\",",
        f"  source = \"{cp['sourceFramework']}\",",
        f"  W1 = {fmt_mat(cp['w1'])},",
        f"  B1 = {fmt_vec(cp['b1'])},",
        f"  W2 = {fmt_mat(cp['w2'])},",
        f"  B2 = {fmt_vec(cp['b2'])},",
        f"  W_OUT = {fmt_mat(cp['wOut'])},",
        f"  B_OUT = {fmt_vec(cp['bOut'])}",
        "}",
    ]
    return "\n".join(lines) + "\n"


def write_text_atomic(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(content, encoding="utf-8")
    os.replace(tmp, path)


def load_episodes(trajectory_dir: Path, max_episodes: int = 200) -> List[Dict[str, Any]]:
    files = sorted(glob.glob(str(trajectory_dir / "ep_*.jsonl")))
    if len(files) > max_episodes:
        files = files[-max_episodes:]
    episodes: List[Dict[str, Any]] = []
    for fpath in files:
        with open(fpath, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    ep = json.loads(line)
                    if ep.get("steps"):
                        episodes.append(ep)
                except json.JSONDecodeError:
                    continue
    return episodes


def build_gae_tensors(
    model: BotPolicyMlp,
    episodes: List[Dict[str, Any]],
    gamma: float = 0.99,
    gae_lambda: float = 0.95,
    beta: float = 0.5,
    max_weight: float = 20.0,
) -> Tuple[torch.Tensor, ...]:
    states: List[List[float]] = []
    combos: List[int] = []
    jukes: List[float] = []
    moves: List[List[float]] = []
    returns_all: List[float] = []
    adv_all: List[float] = []

    model.eval()
    for ep in episodes:
        groups: Dict[Tuple[int, str], List[Dict[str, Any]]] = {}
        for step in ep.get("steps", []):
            feats = step.get("features") or []
            if len(feats) != STATE_DIM or not all(math.isfinite(float(x)) for x in feats):
                continue
            r = float(step.get("reward", 0.0))
            if not math.isfinite(r):
                continue
            key = (int(step.get("team", 1)), str(step.get("hero", "")))
            groups.setdefault(key, []).append(step)

        for traj in groups.values():
            traj.sort(key=lambda s: int(s.get("tick", 0)))
            x_t = torch.tensor([s["features"] for s in traj], dtype=torch.float32)
            with torch.no_grad():
                vals = model(x_t)[:, 7].numpy()

            gae = 0.0
            n = len(traj)
            ep_adv = np.zeros(n, dtype=np.float32)
            ep_ret = np.zeros(n, dtype=np.float32)
            for t in range(n - 1, -1, -1):
                done = bool(traj[t].get("done", False)) or t == n - 1
                non_term = 0.0 if done else 1.0
                next_val = float(vals[t + 1]) if not done else 0.0
                delta = float(traj[t].get("reward", 0.0)) + gamma * next_val * non_term - float(vals[t])
                gae = delta + gamma * gae_lambda * non_term * gae
                ep_adv[t] = gae
                ep_ret[t] = gae + float(vals[t])

            for t, step in enumerate(traj):
                states.append([float(v) for v in step["features"]])
                combos.append(max(0, min(COMBO_COUNT - 1, int(step.get("comboAction", 0)))))
                jukes.append(1.0 if step.get("jukeFlag") else 0.0)
                moves.append([
                    max(-1.0, min(1.0, float(step.get("moveX", 0.0)))),
                    max(-1.0, min(1.0, float(step.get("moveY", 0.0)))),
                ])
                returns_all.append(float(ep_ret[t]))
                adv_all.append(float(ep_adv[t]))

    if not states:
        raise RuntimeError("No valid RL steps found in trajectory corpus.")

    adv_np = np.array(adv_all, dtype=np.float32)
    norm_adv = (adv_np - adv_np.mean()) / (adv_np.std() + 1e-6)
    awac_weights = np.minimum(max_weight, np.exp(np.clip(norm_adv / max(0.05, beta), -5.0, 5.0)))

    return (
        torch.tensor(states, dtype=torch.float32),
        torch.tensor(combos, dtype=torch.long),
        torch.tensor(jukes, dtype=torch.float32),
        torch.tensor(moves, dtype=torch.float32),
        torch.tensor(returns_all, dtype=torch.float32),
        torch.tensor(norm_adv, dtype=torch.float32),
        torch.tensor(awac_weights, dtype=torch.float32),
    )


def train_awac(
    base_checkpoint: Dict[str, Any],
    episodes: List[Dict[str, Any]],
    epochs: int = 8,
    lr: float = 3e-3,
    next_generation: int | None = None,
) -> Tuple[Dict[str, Any], float, float]:
    model = BotPolicyMlp()
    model.load_checkpoint_dict(base_checkpoint)

    states, combos, jukes, moves, returns, norm_adv, awac_w = build_gae_tensors(model, episodes)

    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    model.train()
    initial_loss = 0.0
    final_loss = 0.0

    for epoch in range(max(1, epochs)):
        optimizer.zero_grad()
        out = model(states)

        combo_logits = out[:, COMBO_OFFSET : COMBO_OFFSET + COMBO_COUNT]
        ce = F.cross_entropy(combo_logits, combos, reduction="none")
        loss_combo = (awac_w * ce).mean()

        juke_bce = F.binary_cross_entropy_with_logits(out[:, 6], jukes, reduction="none")
        loss_juke = (awac_w * 0.25 * juke_bce).mean()

        pos_mask = (norm_adv > 0.0).float()
        move_mse = ((out[:, 0:2] - moves) ** 2).sum(dim=1) * 0.5
        loss_move = (0.2 * awac_w * pos_mask * move_mse).mean()

        value_pred = out[:, 7]
        loss_value = 0.5 * 0.5 * ((value_pred - returns) ** 2).mean()

        conf_target = (returns > 0.0).float()
        loss_conf = 0.25 * F.binary_cross_entropy_with_logits(out[:, 4], conf_target)

        total_loss = loss_combo + loss_juke + loss_move + loss_value + loss_conf
        total_loss.backward()
        nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
        optimizer.step()

        loss_val = float(total_loss.item())
        if epoch == 0:
            initial_loss = loss_val
        final_loss = loss_val

    gen = next_generation if next_generation is not None else int(base_checkpoint.get("generation", 0)) + 1
    out_cp = model.to_checkpoint_dict(
        generation=gen,
        parent_generation=int(base_checkpoint.get("generation", 0)),
        role="candidate",
        mean_loss=final_loss,
    )
    validate_checkpoint_dict(out_cp)
    return out_cp, initial_loss, final_loss


def create_seed_baseline() -> Dict[str, Any]:
    rng_state = 1337

    def next_prng() -> float:
        nonlocal rng_state
        rng_state = (1103515245 * rng_state + 12345) % 2147483648
        return (rng_state / 1073741824.0) - 1.0

    scale1 = math.sqrt(2.0 / STATE_DIM) * 0.5
    scale2 = math.sqrt(2.0 / HIDDEN1) * 0.5
    scale_out = math.sqrt(2.0 / HIDDEN2) * 0.5

    return {
        "schemaVersion": 1,
        "generation": 0,
        "parentGeneration": 0,
        "role": "champion",
        "sourceFramework": "seed-1337",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "w1": [[next_prng() * scale1 for _ in range(STATE_DIM)] for _ in range(HIDDEN1)],
        "b1": [0.01 for _ in range(HIDDEN1)],
        "w2": [[next_prng() * scale2 for _ in range(HIDDEN1)] for _ in range(HIDDEN2)],
        "b2": [0.01 for _ in range(HIDDEN2)],
        "wOut": [[next_prng() * scale_out for _ in range(HIDDEN2)] for _ in range(ACTION_DIM)],
        "bOut": [0.85 if i == 4 else 0.05 for i in range(ACTION_DIM)],
    }


def run_self_test() -> int:
    torch.manual_seed(42)
    with tempfile.TemporaryDirectory() as tmpdir:
        base = create_seed_baseline()
        ep = {
            "matchId": 99001,
            "mapName": "caldavar",
            "winnerTeam": 1,
            "candidateTeam": 1,
            "steps": [],
        }
        for tick in range(1, 31):
            is_good = tick % 2 == 0
            feats = [1.0 if (is_good and i < 16) else (-1.0 if (not is_good and i < 16) else 0.0) for i in range(STATE_DIM)]
            ep["steps"].append({
                "matchId": 99001,
                "hero": "Hero_Armadon",
                "team": 1,
                "modelRole": "candidate",
                "tick": tick * 1000,
                "features": feats,
                "comboAction": 2 if is_good else 0,
                "jukeFlag": is_good,
                "moveX": 0.5 if is_good else -0.5,
                "moveY": 0.25 if is_good else -0.25,
                "confidence": 0.8,
                "reward": 1.0 if is_good else -0.8,
                "done": tick == 30,
            })

        cand, l0, lf = train_awac(base, [ep], epochs=20, lr=1e-2, next_generation=1)
        if not (lf < l0):
            raise AssertionError(f"Expected AWAC loss decrease, got initial={l0:.4f}, final={lf:.4f}")

        out_json = Path(tmpdir) / "candidate.json"
        out_lua = Path(tmpdir) / "candidate.lua"
        write_text_atomic(out_json, json.dumps(cand))
        write_text_atomic(out_lua, render_lua_module(cand))
        loaded = json.loads(out_json.read_text(encoding="utf-8"))
        validate_checkpoint_dict(loaded)
        lua_text = out_lua.read_text(encoding="utf-8")
        if "W_OUT" not in lua_text or "generation = 1" not in lua_text:
            raise AssertionError("Exported Lua weights missing expected keys.")

        print(f"[PASS] PyTorch AWAC self-test: loss {l0:.4f} -> {lf:.4f}, params=4928, lua_bytes={len(lua_text)}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Project Kongor PyTorch AWAC Offline Bot Trainer")
    parser.add_argument("--runtime-dir", default="games/server/override/bots/rl_runtime", help="Runtime RL checkpoint directory")
    parser.add_argument("--trajectories", default="logs/docker/compel-queue-arcade/rl_trajectories", help="Trajectory JSONL ring-buffer directory")
    parser.add_argument("--epochs", type=int, default=12, help="AWAC training epochs")
    parser.add_argument("--lr", type=float, default=3e-3, help="AdamW learning rate")
    parser.add_argument("--self-test", action="store_true", help="Run synthetic AWAC verification suite and exit")
    args = parser.parse_args()

    if args.self_test:
        return run_self_test()

    runtime_dir = Path(args.runtime_dir)
    traj_dir = Path(args.trajectories)
    champ_path = runtime_dir / "champion.json"
    base = json.loads(champ_path.read_text(encoding="utf-8")) if champ_path.exists() else create_seed_baseline()
    validate_checkpoint_dict(base)

    episodes = load_episodes(traj_dir)
    if not episodes:
        print(f"[WARN] No trajectory episodes found under {traj_dir}. Nothing to train.")
        return 1

    cand, l0, lf = train_awac(base, episodes, epochs=args.epochs, lr=args.lr)
    write_text_atomic(runtime_dir / "candidate.json", json.dumps(cand))
    write_text_atomic(runtime_dir / "candidate.lua", render_lua_module(cand))
    print(
        f"[RL_PYTORCH] Trained Candidate gen={cand['generation']} from {len(episodes)} episode(s): "
        f"loss {l0:.5f} -> {lf:.5f}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
