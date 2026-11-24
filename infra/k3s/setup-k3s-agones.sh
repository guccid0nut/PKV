#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)
# ============================================================================
# Script: setup-k3s-agones.sh
# System: Project Kongor - Cloud-Native Game Server Orchestration
# Purpose: Zero-touch automated installation of lightweight k3s + Agones
#          on Ubuntu 24.04 / Debian bare-metal servers.
# ============================================================================

set -euo pipefail

echo "===================================================================="
echo "    PROJECT KONGOR: K3s + Agones Bare-Metal Fleet Installer        "
echo "===================================================================="

if [ "$(id -u)" -ne 0 ]; then
    echo "[ERROR] This script must be run as root (or via sudo)." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# ----------------------------------------------------------------------------
# 1. System Dependencies & Kernel UDP Tuning
# ----------------------------------------------------------------------------
echo "[1/6] Applying high-throughput UDP kernel tuning..."
cat <<'EOF' > /etc/sysctl.d/99-kongor-udp.conf
# High-density dedicated game server UDP buffer tuning
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
net.core.rmem_default = 2097152
net.core.wmem_default = 2097152
net.ipv4.udp_rmem_min = 16384
net.ipv4.udp_wmem_min = 16384
net.core.netdev_max_backlog = 10000
EOF
sysctl --system > /dev/null

echo "[1/6] Installing prerequisite packages..."
apt-get update -qq
apt-get install -y -qq curl ca-certificates iptables socat > /dev/null

# ----------------------------------------------------------------------------
# 2. Install k3s (Lightweight Kubernetes, < 500 MB footprint)
# ----------------------------------------------------------------------------
# Traefik (HTTP ingress) and ServiceLB (Cloud LB simulator) are disabled to save
# memory and CPU cycles; HoN game servers bind directly to host UDP ports.
echo "[2/6] Installing k3s (disabling Traefik and ServiceLB)..."
if ! command -v k3s >/dev/null 2>&1; then
    curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC="--disable traefik --disable servicelb" sh -
else
    echo "k3s is already installed."
fi

# Set up kubeconfig permissions
mkdir -p "$HOME/.kube"
cp /etc/rancher/k3s/k3s.yaml "$HOME/.kube/config"
chmod 600 "$HOME/.kube/config"
export KUBECONFIG="$HOME/.kube/config"

# Wait for node Ready
echo "Waiting for k3s node to be Ready..."
kubectl wait --for=condition=Ready node --all --timeout=60s

# ----------------------------------------------------------------------------
# 3. Install Helm 3
# ----------------------------------------------------------------------------
echo "[3/6] Ensuring Helm 3 is installed..."
if ! command -v helm >/dev/null 2>&1; then
    curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
else
    echo "Helm is already installed ($(helm version --short))."
fi

# ----------------------------------------------------------------------------
# 4. Install Agones via Helm
# ----------------------------------------------------------------------------
echo "[4/6] Installing Agones Gaming CRDs and Controllers..."
helm repo add agones https://agones.dev/chart/helm 2>/dev/null || true
helm repo update agones

VALUES_FILE="${REPO_ROOT}/infra/k8s/agones/values-k3s.yaml"
if [ ! -f "${VALUES_FILE}" ]; then
    echo "[ERROR] Values file not found at ${VALUES_FILE}" >&2
    exit 1
fi

helm upgrade --install agones agones/agones \
    --namespace agones-system \
    --create-namespace \
    -f "${VALUES_FILE}"

echo "Waiting for Agones controller pods to become ready..."
kubectl wait --namespace agones-system \
    --for=condition=ready pod \
    --selector=app=agones \
    --timeout=120s

# ----------------------------------------------------------------------------
# 5. Configure Secrets & Host Directories
# ----------------------------------------------------------------------------
echo "[5/6] Ensuring Kongor directories and credentials exist..."
mkdir -p /opt/kongor/games/server

# If kongor-host-credentials secret does not exist, create from required env vars
if ! kubectl get secret kongor-host-credentials >/dev/null 2>&1; then
    : "${KONGOR_HOST_USER:=kongor_host}"
    if [ -z "${KONGOR_HOST_PASSWORD:-}" ] || [ -z "${COMPEL_AUTHENTICATION_TOKEN:-}" ]; then
        echo "[ERROR] kongor-host-credentials secret is missing. Export KONGOR_HOST_PASSWORD and COMPEL_AUTHENTICATION_TOKEN (or pre-create the secret via kubectl) before running." >&2
        exit 1
    fi
    echo "Creating secret kongor-host-credentials for host user '${KONGOR_HOST_USER}'..."
    kubectl create secret generic kongor-host-credentials \
        --from-literal=username="${KONGOR_HOST_USER}" \
        --from-literal=password="${KONGOR_HOST_PASSWORD}" \
        --from-literal=token="${COMPEL_AUTHENTICATION_TOKEN}"
fi

# ----------------------------------------------------------------------------
# 6. Deploy Kongor Fleet & Autoscaler
# ----------------------------------------------------------------------------
echo "[6/6] Deploying Kongor Dedicated Server Fleet & Autoscaler..."
FLEET_MANIFEST="${REPO_ROOT}/infra/k8s/agones/fleet.yaml"
AUTOSCALER_MANIFEST="${REPO_ROOT}/infra/k8s/agones/fleet-autoscaler.yaml"

kubectl apply -f "${FLEET_MANIFEST}"
kubectl apply -f "${AUTOSCALER_MANIFEST}"

echo ""
echo "===================================================================="
echo "    K3s + Agones Installation Complete!                            "
echo "===================================================================="
echo "Verify status with:"
echo "  kubectl get gameservers"
echo "  kubectl get fleets"
echo "  kubectl get fleetautoscalers"
echo ""
echo "Run verification script:"
echo "  ${SCRIPT_DIR}/verify-fleet.sh"
echo "===================================================================="
