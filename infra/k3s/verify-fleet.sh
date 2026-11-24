#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ============================================================================
# Script: verify-fleet.sh
# System: Project Kongor - Cloud-Native Game Server Orchestration
# Purpose: Inspects and verifies health of k3s cluster, Agones controllers,
#          game server pods, warm standby buffer, and allocated UDP ports.
# ============================================================================

set -euo pipefail

echo "===================================================================="
echo "    PROJECT KONGOR: K3s + Agones Fleet Health Verification         "
echo "===================================================================="

# 1. Cluster Node Status
echo ""
echo "--- [1] Kubernetes Nodes ---"
kubectl get nodes -o wide

# 2. Agones System Pods
echo ""
echo "--- [2] Agones System Pods ---"
kubectl get pods -n agones-system

# 3. Fleet & Autoscaler Status
echo ""
echo "--- [3] Kongor Fleets & Autoscalers ---"
kubectl get fleets
echo ""
kubectl get fleetautoscalers

# 4. GameServer Custom Resources & States
echo ""
echo "--- [4] Agones GameServers (State & Allocated Ports) ---"
kubectl get gameservers -o custom-columns=NAME:.metadata.name,STATE:.status.state,NODE:.status.nodeName,ADDRESS:.status.address,PORTS:.status.ports

# 5. Pod Summary & Resource Consumption
echo ""
echo "--- [5] Fleet Pods ---"
kubectl get pods -l app=kongor-dedicated -o wide

# 6. Sample COMPEL Worker Logs
echo ""
echo "--- [6] Recent COMPEL Supervisor Logs ---"
FIRST_POD=$(kubectl get pods -l app=kongor-dedicated -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)
if [ -n "${FIRST_POD}" ]; then
    echo "Logs from pod: ${FIRST_POD} (container: compel-worker)"
    kubectl logs "${FIRST_POD}" -c compel-worker --tail=25 || true
else
    echo "No fleet pods currently running."
fi

echo ""
echo "===================================================================="
echo "Verification complete."
echo "===================================================================="
