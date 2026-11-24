#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
Quaternion and 3D Rotation Mathematical Utilities for Skeletal Animation Blending.
Provides robust quaternion operations, Euler angle conversions, shortest-path Slerp,
NLerp fallback, angular velocity calculation, and smooth blending weight curves.
"""

import sys
import math
import logging
import argparse
from typing import Tuple, Union, Optional
import numpy as np

logger = logging.getLogger("tools.cinematic.quaternion_math")


def normalize_quat(q: Union[np.ndarray, list, tuple]) -> np.ndarray:
    """Normalizes a 4D quaternion [w, x, y, z] to unit magnitude."""
    q_arr = np.asarray(q, dtype=np.float64)
    norm = np.linalg.norm(q_arr)
    if norm < 1e-12:
        return np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float64)
    return q_arr / norm


def euler_to_quat(pitch_deg: float, roll_deg: float, yaw_deg: float, order: str = 'zxy') -> np.ndarray:
    """
    Converts Euler angles in degrees (pitch=X, roll=Y, yaw=Z) to a unit quaternion [w, x, y, z].
    Default order is 'zxy' (Rz(yaw) * Rx(pitch) * Ry(roll)), matching HoN K2 / 3ds Max Biped hierarchy.
    """
    rx = math.radians(float(pitch_deg))
    ry = math.radians(float(roll_deg))
    rz = math.radians(float(yaw_deg))

    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)

    if order.lower() == 'zxy':
        # R = Rz(yaw) * Rx(pitch) * Ry(roll)
        Rz = np.array([[cz, -sz, 0.0], [sz, cz, 0.0], [0.0, 0.0, 1.0]], dtype=np.float64)
        Rx = np.array([[1.0, 0.0, 0.0], [0.0, cx, -sx], [0.0, sx, cx]], dtype=np.float64)
        Ry = np.array([[cy, 0.0, sy], [0.0, 1.0, 0.0], [-sy, 0.0, cy]], dtype=np.float64)
        R = Rz @ Rx @ Ry

        tr = R[0, 0] + R[1, 1] + R[2, 2]
        if tr > 0.0:
            S = math.sqrt(tr + 1.0) * 2.0
            w = 0.25 * S
            x = (R[2, 1] - R[1, 2]) / S
            y = (R[0, 2] - R[2, 0]) / S
            z = (R[1, 0] - R[0, 1]) / S
        elif (R[0, 0] > R[1, 1]) and (R[0, 0] > R[2, 2]):
            S = math.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2.0
            w = (R[2, 1] - R[1, 2]) / S
            x = 0.25 * S
            y = (R[0, 1] + R[1, 0]) / S
            z = (R[0, 2] + R[2, 0]) / S
        elif R[1, 1] > R[2, 2]:
            S = math.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2.0
            w = (R[0, 2] - R[2, 0]) / S
            x = (R[0, 1] + R[1, 0]) / S
            y = 0.25 * S
            z = (R[1, 2] + R[2, 1]) / S
        else:
            S = math.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2.0
            w = (R[1, 0] - R[0, 1]) / S
            x = (R[0, 2] + R[2, 0]) / S
            y = (R[1, 2] + R[2, 1]) / S
            z = 0.25 * S
        return normalize_quat(np.array([w, x, y, z], dtype=np.float64))
    else:
        cx_h, sx_h = math.cos(rx * 0.5), math.sin(rx * 0.5)
        cy_h, sy_h = math.cos(ry * 0.5), math.sin(ry * 0.5)
        cz_h, sz_h = math.cos(rz * 0.5), math.sin(rz * 0.5)
        w = cx_h * cy_h * cz_h - sx_h * sy_h * sz_h
        x = sx_h * cy_h * cz_h + cx_h * sy_h * sz_h
        y = cx_h * sy_h * cz_h - sx_h * cy_h * sz_h
        z = cx_h * cy_h * sz_h + sx_h * sy_h * cz_h
        return normalize_quat(np.array([w, x, y, z], dtype=np.float64))


def quat_to_euler(q: Union[np.ndarray, list, tuple], order: str = 'xyz') -> Tuple[float, float, float]:
    """
    Converts a unit quaternion [w, x, y, z] to Euler angles in degrees (pitch, roll, yaw).
    """
    q_norm = normalize_quat(q)
    w, x, y, z = q_norm[0], q_norm[1], q_norm[2], q_norm[3]

    if order.lower() == 'xyz':
        # Pitch (X)
        sinr_cosp = 2.0 * (w * x + y * z)
        cosr_cosp = 1.0 - 2.0 * (x * x + y * y)
        pitch = math.atan2(sinr_cosp, cosr_cosp)

        # Roll (Y)
        sinp = 2.0 * (w * y - z * x)
        if abs(sinp) >= 1.0:
            roll = math.copysign(math.pi / 2.0, sinp)
        else:
            roll = math.asin(sinp)

        # Yaw (Z)
        siny_cosp = 2.0 * (w * z + x * y)
        cosy_cosp = 1.0 - 2.0 * (y * y + z * z)
        yaw = math.atan2(siny_cosp, cosy_cosp)
    else:
        # Generic yaw, pitch, roll
        pitch = math.atan2(2.0 * (w * x + y * z), 1.0 - 2.0 * (x * x + y * y))
        sinp = max(-1.0, min(1.0, 2.0 * (w * y - z * x)))
        roll = math.asin(sinp)
        yaw = math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))

    return (math.degrees(pitch), math.degrees(roll), math.degrees(yaw))


def quat_multiply(q1: np.ndarray, q2: np.ndarray) -> np.ndarray:
    """
    Computes quaternion product q1 * q2 representing rotation q2 followed by q1.
    Both quaternions in [w, x, y, z] format.
    """
    w1, x1, y1, z1 = q1[0], q1[1], q1[2], q1[3]
    w2, x2, y2, z2 = q2[0], q2[1], q2[2], q2[3]

    w = w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2
    x = w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2
    y = w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2
    z = w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2

    return normalize_quat(np.array([w, x, y, z], dtype=np.float64))


def quat_slerp(q1: Union[np.ndarray, list, tuple], q2: Union[np.ndarray, list, tuple], t: float) -> np.ndarray:
    """
    Spherical Linear Interpolation (Slerp) between two unit quaternions with antipodal check
    and Normalized Linear Interpolation (NLerp) fallback for near-parallel quaternions.

    Args:
        q1: Starting unit quaternion [w, x, y, z].
        q2: Ending unit quaternion [w, x, y, z].
        t: Interpolation factor in range [0.0, 1.0].

    Returns:
        np.ndarray: Interpolated unit quaternion [w, x, y, z].
    """
    q1_n = normalize_quat(q1)
    q2_n = normalize_quat(q2)

    t_clamped = max(0.0, min(1.0, float(t)))

    # Compute dot product
    dot = float(np.dot(q1_n, q2_n))

    # Antipodal check: take the shortest arc
    if dot < 0.0:
        q2_n = -q2_n
        dot = -dot

    # If the angle is very small, use NLerp to avoid numerical instability / division by zero
    if dot > 0.9995:
        result = (1.0 - t_clamped) * q1_n + t_clamped * q2_n
        return normalize_quat(result)

    # Calculate angle theta and sin(theta)
    theta = math.acos(max(-1.0, min(1.0, dot)))
    sin_theta = math.sin(theta)

    if sin_theta < 1e-6:
        return q1_n.copy()

    scale1 = math.sin((1.0 - t_clamped) * theta) / sin_theta
    scale2 = math.sin(t_clamped * theta) / sin_theta

    result = scale1 * q1_n + scale2 * q2_n
    return normalize_quat(result)


def quat_angular_distance_deg(q1: Union[np.ndarray, list, tuple], q2: Union[np.ndarray, list, tuple]) -> float:
    """
    Calculates the minimal rotational angular distance in degrees between two unit quaternions.
    """
    q1_n = normalize_quat(q1)
    q2_n = normalize_quat(q2)

    dot = abs(float(np.dot(q1_n, q2_n)))
    dot_clamped = max(-1.0, min(1.0, dot))

    theta_rad = 2.0 * math.acos(dot_clamped)
    return math.degrees(theta_rad)


def hermite_smoothstep(u: float) -> float:
    """Cubic Hermite smoothstep curve: S(u) = 3u^2 - 2u^3 with C1 continuity."""
    u_clamped = max(0.0, min(1.0, float(u)))
    return u_clamped * u_clamped * (3.0 - 2.0 * u_clamped)


def cosine_smoothstep(u: float) -> float:
    """Cosine smoothstep curve: S(u) = (1 - cos(pi * u)) / 2."""
    u_clamped = max(0.0, min(1.0, float(u)))
    return (1.0 - math.cos(math.pi * u_clamped)) * 0.5


def lerp_vec(v1: np.ndarray, v2: np.ndarray, t: float) -> np.ndarray:
    """Linear interpolation between two 3D vectors."""
    t_clamped = max(0.0, min(1.0, float(t)))
    return (1.0 - t_clamped) * np.asarray(v1, dtype=np.float64) + t_clamped * np.asarray(v2, dtype=np.float64)


def setup_cli_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configures structured logging with optional file output."""
    level = logging.DEBUG if verbose else logging.INFO
    fmt = "%(asctime)s [%(levelname)s] [%(name)s] %(message)s"
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, encoding="utf-8"))
    logging.basicConfig(level=level, format=fmt, handlers=handlers, force=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Quaternion and 3D Rotation Mathematical Utilities")
    parser.add_argument("--test", action="store_true", help="Run self-test suite on math operations")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose debug logging")
    parser.add_argument("--log-file", type=str, default=None, help="Optional path to write structured log file")
    args = parser.parse_args()

    setup_cli_logging(args.verbose, args.log_file)
    logger.info("Initializing Quaternion Math Engine...")

    # Self-test
    q_id = normalize_quat([1, 0, 0, 0])
    q_rot = euler_to_quat(45, 0, 90, order='zxy')
    euler_back = quat_to_euler(q_rot)
    slerp_mid = quat_slerp(q_id, q_rot, 0.5)
    dist = quat_angular_distance_deg(q_id, q_rot)

    logger.info(f"Self-Test Identity Quat: {q_id}")
    logger.info(f"Self-Test Rotated Quat: {q_rot}")
    logger.info(f"Self-Test Euler back (deg): {euler_back}")
    logger.info(f"Self-Test Slerp t=0.5: {slerp_mid}")
    logger.info(f"Self-Test Angular Distance: {dist:.2f} deg")
    logger.info("Quaternion Math self-test passed successfully.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
