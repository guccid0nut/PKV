# 🚀 Bare-Metal K3s + Agones Provisioning (`infra/k3s`)

> **Automated Ubuntu 24.04 / Debian bare-metal installer and fleet health verifier for `k3s` + Agones dedicated server nodes.**

---

## 📂 Scripts

- **`setup-k3s-agones.sh`:** Root bootstrap script that configures kernel UDP socket buffers (`/etc/sysctl.d/99-kongor-udp.conf`), installs `k3s` (with `--disable traefik --disable servicelb` so game servers bind host UDP ports directly), installs Helm 3, and deploys Agones.
- **`verify-fleet.sh`:** Inspects `kubectl get nodes`, `agones-system` pods, `fleets`, `fleetautoscalers`, allocated `gameservers`, and recent `compel-worker` container logs.

---

## ⚡ Quick Start (Linux Bare-Metal)

```bash
# 1. Bootstrap k3s + Agones on Ubuntu/Debian host
sudo bash infra/k3s/setup-k3s-agones.sh

# 2. Verify cluster, Agones controllers, and warm game server fleet
bash infra/k3s/verify-fleet.sh
```

---

## 📖 Related Documentation

- [Kubernetes & Agones Fleet Manifests (`infra/k8s`)](../k8s/README.md)
- [Container & Kubernetes Hosting Guide](../../docs/infrastructure/container-hosting-guide.md)
