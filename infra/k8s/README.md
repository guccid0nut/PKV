# ☸️ Kubernetes & Agones Fleet Manifests (`infra/k8s`)

> **Kubernetes Deployments, Agones `Fleet` / `FleetAutoscaler` specs, and local `k3d` / `k3s` deployment scripts.**

`infra/k8s` defines the cloud-native game server fleet orchestration manifests for running `COMPEL` + `hon_x64` dedicated match workers under Kubernetes and the **Agones** game server operator.

---

## 📂 Directory Contents

| File / Directory | Purpose |
| :--- | :--- |
| `agones/fleet.yaml` | Agones `Fleet` (`kongor-dedicated-fleet`) with `Dynamic` UDP port allocation (`game-udp`, `voice-udp`), `SYS_NICE` capability, and warm standby replicas |
| `agones/fleet-autoscaler.yaml` | Agones `FleetAutoscaler` buffer policy scaling warm standby capacity automatically |
| `agones/values-k3s.yaml` | Helm values for deploying Agones on lightweight `k3s` nodes |
| `agones/base/`, `agones/overlays/` | Kustomize base & environment overlays |
| `compel-k3s.yaml` | Kubernetes `Secret` + `Deployment` manifest for `compel-k3s-server` (`secretKeyRef` parameterized credentials) |
| `local-test-pod.yaml` | Single-pod verification manifest for local `k3d`/`k3s` testing |
| `setup_local_k3d_agones.ps1` | Local Windows `k3d` + Agones cluster bootstrapper |
| `deploy_k3s_servers.ps1` | Fleet deployment helper targeting `k3s` + Agones |

---

## 📖 Related Documentation

- [Bare-Metal k3s + Agones Setup (`infra/k3s`)](../k3s/README.md)
- [ADR-0006: Agones for Game Servers](../../docs/adr/0006-agones-for-game-servers.md)
- [Container & Kubernetes Hosting Guide](../../docs/infrastructure/container-hosting-guide.md)
