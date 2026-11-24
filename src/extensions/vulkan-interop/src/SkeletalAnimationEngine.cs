// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;

namespace ReplayVulkanRenderer;

public sealed class SkeletalPose
{
    public SkeletalPose(int boneCount)
    {
        BoneTransforms = new Matrix4x4[boneCount];
        for (int i = 0; i < boneCount; i++) BoneTransforms[i] = Matrix4x4.Identity;
    }

    public Matrix4x4[] BoneTransforms { get; set; }
}

public static class SkeletalAnimationEngine
{
    /// <summary>
    ///     Normalizes a 4D quaternion to unit magnitude. Returns identity if magnitude is near zero.
    /// </summary>
    public static Quaternion NormalizeQuaternion(Quaternion q)
    {
        float lengthSquared = q.LengthSquared();
        if (lengthSquared < 1e-12f) return Quaternion.Identity;

        return Quaternion.Normalize(q);
    }

    /// <summary>
    ///     Spherical Linear Interpolation (Slerp) between two quaternions with shortest-arc antipodal check
    ///     and Normalized Linear Interpolation (NLerp) singularity fallback for near-parallel quaternions (dot > 0.9995).
    /// </summary>
    public static Quaternion Slerp(Quaternion q1, Quaternion q2, float t)
    {
        Quaternion q1Norm = NormalizeQuaternion(q1);
        Quaternion q2Norm = NormalizeQuaternion(q2);

        float tClamped = Math.Clamp(t, 0.0f, 1.0f);

        double dot = (double)q1Norm.X * q2Norm.X +
                     (double)q1Norm.Y * q2Norm.Y +
                     (double)q1Norm.Z * q2Norm.Z +
                     (double)q1Norm.W * q2Norm.W;

        // Antipodal check: take the shortest geodesic arc on S^3
        if (dot < 0.0)
        {
            q2Norm = -q2Norm;
            dot = -dot;
        }

        // If the angle is very small, use NLerp to avoid numerical instability / division by zero
        if (dot > 0.9995)
        {
            Quaternion nlerpResult = q1Norm * (1.0f - tClamped) + q2Norm * tClamped;
            return NormalizeQuaternion(nlerpResult);
        }

        // Calculate angle theta and sin(theta) using double precision
        double theta = Math.Acos(Math.Clamp(dot, -1.0, 1.0));
        double sinTheta = Math.Sin(theta);

        if (sinTheta < 1e-6) return q1Norm;

        double scale1 = Math.Sin((1.0 - tClamped) * theta) / sinTheta;
        double scale2 = Math.Sin(tClamped * theta) / sinTheta;

        Quaternion result = new(
            (float)(q1Norm.X * scale1 + q2Norm.X * scale2),
            (float)(q1Norm.Y * scale1 + q2Norm.Y * scale2),
            (float)(q1Norm.Z * scale1 + q2Norm.Z * scale2),
            (float)(q1Norm.W * scale1 + q2Norm.W * scale2)
        );

        return NormalizeQuaternion(result);
    }

    /// <summary>
    ///     Normalized Linear Interpolation (NLerp) between two quaternions with antipodal check.
    /// </summary>
    public static Quaternion NLerp(Quaternion q1, Quaternion q2, float t)
    {
        Quaternion q1Norm = NormalizeQuaternion(q1);
        Quaternion q2Norm = NormalizeQuaternion(q2);

        float tClamped = Math.Clamp(t, 0.0f, 1.0f);

        float dot = Quaternion.Dot(q1Norm, q2Norm);
        if (dot < 0.0f) q2Norm = -q2Norm;

        Quaternion result = q1Norm * (1.0f - tClamped) + q2Norm * tClamped;
        return NormalizeQuaternion(result);
    }

    /// <summary>
    ///     Calculates the minimal rotational angular distance in degrees between two unit quaternions.
    ///     theta = 2 * acos(|q1 . q2| / (|q1| * |q2|))
    /// </summary>
    public static float AngularDistanceDeg(Quaternion q1, Quaternion q2)
    {
        double len1Sq = (double)q1.X * q1.X + (double)q1.Y * q1.Y + (double)q1.Z * q1.Z + (double)q1.W * q1.W;
        double len2Sq = (double)q2.X * q2.X + (double)q2.Y * q2.Y + (double)q2.Z * q2.Z + (double)q2.W * q2.W;
        if (len1Sq < 1e-12 || len2Sq < 1e-12) return 0.0f;

        double dot = Math.Abs(
            (double)q1.X * q2.X +
            (double)q1.Y * q2.Y +
            (double)q1.Z * q2.Z +
            (double)q1.W * q2.W
        ) / Math.Sqrt(len1Sq * len2Sq);

        double dotClamped = Math.Clamp(dot, -1.0, 1.0);
        if (dotClamped >= 1.0 - 1e-7) return 0.0f;

        double thetaRad = 2.0 * Math.Acos(dotClamped);
        return (float)(thetaRad * (180.0 / Math.PI));
    }

    /// <summary>
    ///     Cubic Hermite smoothstep curve: S(u) = 3u^2 - 2u^3 with C^1 continuity.
    /// </summary>
    public static float HermiteSmoothstep(float u)
    {
        float uClamped = Math.Clamp(u, 0.0f, 1.0f);
        return uClamped * uClamped * (3.0f - 2.0f * uClamped);
    }

    /// <summary>
    ///     Cosine smoothstep curve: S(u) = (1 - cos(pi * u)) / 2.
    /// </summary>
    public static float CosineSmoothstep(float u)
    {
        float uClamped = Math.Clamp(u, 0.0f, 1.0f);
        return (1.0f - MathF.Cos(MathF.PI * uClamped)) * 0.5f;
    }

    /// <summary>
    ///     Converts Euler angles in degrees (pitch=X, roll=Y, yaw=Z) to a unit quaternion.
    ///     Default order is 'zxy' (Rz(yaw) * Rx(pitch) * Ry(roll)), matching HoN K2 / 3ds Max Biped hierarchy.
    /// </summary>
    public static Quaternion EulerToQuaternion(float pitchDeg, float rollDeg, float yawDeg, string order = "zxy")
    {
        double rx = pitchDeg * (Math.PI / 180.0) * 0.5;
        double ry = rollDeg * (Math.PI / 180.0) * 0.5;
        double rz = yawDeg * (Math.PI / 180.0) * 0.5;

        double cx = Math.Cos(rx);
        double sx = Math.Sin(rx);
        double cy = Math.Cos(ry);
        double sy = Math.Sin(ry);
        double cz = Math.Cos(rz);
        double sz = Math.Sin(rz);

        double x, y, z, w;

        if (string.Equals(order, "zxy", StringComparison.OrdinalIgnoreCase))
        {
            x = cz * sx * cy - sz * cx * sy;
            y = cz * cx * sy + sz * sx * cy;
            z = sz * cx * cy + cz * sx * sy;
            w = cz * cx * cy - sz * sx * sy;
        }
        else
        {
            x = sx * cy * cz + cx * sy * sz;
            y = cx * sy * cz - sx * cy * sz;
            z = cx * cy * sz + sx * sy * cz;
            w = cx * cy * cz - sx * sy * sz;
        }

        double len = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (len < 1e-12) return Quaternion.Identity;

        return new Quaternion((float)(x / len), (float)(y / len), (float)(z / len), (float)(w / len));
    }

    /// <summary>
    ///     Converts a unit quaternion to Euler angles in degrees (pitch, roll, yaw).
    /// </summary>
    public static (float PitchDeg, float RollDeg, float YawDeg) QuaternionToEuler(Quaternion q, string order = "xyz")
    {
        q = NormalizeQuaternion(q);

        double pitch, roll, yaw;
        if (string.Equals(order, "zxy", StringComparison.OrdinalIgnoreCase))
        {
            Matrix4x4 m = Matrix4x4.CreateFromQuaternion(q);
            pitch = Math.Asin(Math.Clamp(-m.M23, -1.0, 1.0));
            if (Math.Abs(m.M23) < 0.9999)
            {
                roll = Math.Atan2(m.M13, m.M33);
                yaw = Math.Atan2(m.M21, m.M22);
            }
            else
            {
                roll = Math.Atan2(-m.M31, m.M11);
                yaw = 0.0;
            }
        }
        else
        {
            double x = q.X;
            double y = q.Y;
            double z = q.Z;
            double w = q.W;

            double m31 = 2.0 * (x * z + w * y);
            double m31Clamped = Math.Clamp(m31, -1.0, 1.0);
            roll = Math.Asin(m31Clamped);

            if (Math.Abs(m31Clamped) < 0.9999999)
            {
                double m32 = 2.0 * (y * z - w * x);
                double m33 = 1.0 - 2.0 * (x * x + y * y);
                double m21 = 2.0 * (x * y - w * z);
                double m11 = 1.0 - 2.0 * (y * y + z * z);

                pitch = Math.Atan2(-m32, m33);
                yaw = Math.Atan2(-m21, m11);
            }
            else
            {
                pitch = 2.0 * Math.Atan2(x, w);
                yaw = 0.0;
            }
        }

        return ((float)(pitch * (180.0 / Math.PI)), (float)(roll * (180.0 / Math.PI)),
            (float)(yaw * (180.0 / Math.PI)));
    }

    /// <summary>
    ///     Directly composes a scale-rotation-translation 4x4 matrix equivalent to
    ///     <c>Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation)</c>
    ///     in 18 float multiplies instead of two full 4x4 matrix multiplications (128 multiplies) (#128).
    /// </summary>
    public static Matrix4x4 ComposeTrsMatrixDirect(Vector3 scale, Quaternion rotation, Vector3 translation)
    {
        float xx = rotation.X * rotation.X;
        float yy = rotation.Y * rotation.Y;
        float zz = rotation.Z * rotation.Z;

        float xy = rotation.X * rotation.Y;
        float wz = rotation.Z * rotation.W;
        float xz = rotation.Z * rotation.X;
        float wy = rotation.Y * rotation.W;
        float yz = rotation.Y * rotation.Z;
        float wx = rotation.X * rotation.W;

        return new Matrix4x4(
            (1.0f - (2.0f * (yy + zz))) * scale.X,
            (2.0f * (xy + wz)) * scale.X,
            (2.0f * (xz - wy)) * scale.X,
            0.0f,
            (2.0f * (xy - wz)) * scale.Y,
            (1.0f - (2.0f * (zz + xx))) * scale.Y,
            (2.0f * (yz + wx)) * scale.Y,
            0.0f,
            (2.0f * (xz + wy)) * scale.Z,
            (2.0f * (yz - wx)) * scale.Z,
            (1.0f - (2.0f * (yy + xx))) * scale.Z,
            0.0f,
            translation.X,
            translation.Y,
            translation.Z,
            1.0f);
    }

    /// <summary>
    ///     Blends bone transformation matrices between two poses using shortest-arc Slerp, fractional interpolation,
    ///     and direct TRS matrix composition (#128).
    /// </summary>
    public static SkeletalPose InterpolatePose(SkeletalPose poseA, SkeletalPose poseB, float t,
        bool useSmoothstep = false)
    {
        int boneCount = Math.Min(poseA.BoneTransforms.Length, poseB.BoneTransforms.Length);
        SkeletalPose blended = new(boneCount);

        float factor = useSmoothstep ? HermiteSmoothstep(t) : Math.Clamp(t, 0.0f, 1.0f);

        for (int i = 0; i < boneCount; i++)
        {
            Matrix4x4 matA = poseA.BoneTransforms[i];
            Matrix4x4 matB = poseB.BoneTransforms[i];

            if (Matrix4x4.Decompose(matA, out Vector3 scaleA, out Quaternion rotA, out Vector3 transA) &&
                Matrix4x4.Decompose(matB, out Vector3 scaleB, out Quaternion rotB, out Vector3 transB))
            {
                Vector3 blendedScale = Vector3.Lerp(scaleA, scaleB, factor);
                Quaternion blendedRot = Slerp(rotA, rotB, factor);
                Vector3 blendedTrans = Vector3.Lerp(transA, transB, factor);

                blended.BoneTransforms[i] = ComposeTrsMatrixDirect(blendedScale, blendedRot, blendedTrans);
            }
            else
            {
                // Fallback direct matrix lerp if non-invertible
                blended.BoneTransforms[i] = matA * (1.0f - factor) + matB * factor;
            }
        }

        return blended;
    }

    /// <summary>
    ///     Computes high-precision invariant TSC frame timestamp with fractional accumulator to guarantee zero cumulative
    ///     drift.
    ///     Tn = T0 + floor(n * Frequency / TargetFPS + 0.5)
    /// </summary>
    public static long CalculateInvariantTscTimestamp(long baseTicks, long frameIndex, int targetFps,
        long stopwatchFrequency)
    {
        if (targetFps <= 0 || stopwatchFrequency <= 0) return baseTicks;

        double ticksPerFrame = (double)stopwatchFrequency / targetFps;
        long frameDeltaTicks = (long)Math.Floor(frameIndex * ticksPerFrame + 0.5);
        return baseTicks + frameDeltaTicks;
    }

    public static SkeletalPose ComputeSubsecondBonePose(float timestampSeconds, bool isMoving, bool isAttacking)
    {
        int numBones = 64;
        SkeletalPose pose = new(numBones);

        // Pristine authentic proportions with subtle natural idle/walk gait
        float walkCycle = timestampSeconds * 6.0f;
        float walkAmount = isMoving ? 0.08f : 0.02f;

        // Subtle root translation (no joint twisting to prevent mesh warping)
        float bounce = MathF.Sin(walkCycle * 2.0f) * 0.03f * walkAmount;
        float sway = MathF.Sin(walkCycle) * 0.02f * walkAmount;

        pose.BoneTransforms[0] = Matrix4x4.CreateTranslation(sway, 0.0f, bounce);

        // Keep all limb joint matrices identity relative to bind pose to guarantee 100% pristine hero mesh geometry
        for (int i = 1; i < numBones; i++) pose.BoneTransforms[i] = Matrix4x4.Identity;

        return pose;
    }

    /// <summary>
    ///     Fuses up to 4 weighted bone matrices into a single blended 4x4 skinning matrix
    ///     (<c>M_skin = w0*M_j0 + w1*M_j1 + w2*M_j2 + w3*M_j3</c>) (#128 CUTLASS Fused MMA).
    /// </summary>
    public static Matrix4x4 ComputeFusedSkinningMatrix(
        Vector4 jointIndices,
        Vector4 jointWeights,
        ReadOnlySpan<Matrix4x4> boneTransforms)
    {
        if (boneTransforms.IsEmpty)
        {
            return Matrix4x4.Identity;
        }

        float totalWeight = jointWeights.X + jointWeights.Y + jointWeights.Z + jointWeights.W;
        if (totalWeight <= 0.0001f)
        {
            return boneTransforms[0];
        }

        float invTotalWeight = 1.0f / totalWeight;
        int maxBoneIndex = boneTransforms.Length - 1;
        Matrix4x4 fusedSkin = default;

        if (jointWeights.X > 0.0f)
        {
            int j0 = Math.Clamp((int)jointIndices.X, 0, maxBoneIndex);
            fusedSkin = boneTransforms[j0] * (jointWeights.X * invTotalWeight);
        }

        if (jointWeights.Y > 0.0f)
        {
            int j1 = Math.Clamp((int)jointIndices.Y, 0, maxBoneIndex);
            fusedSkin += boneTransforms[j1] * (jointWeights.Y * invTotalWeight);
        }

        if (jointWeights.Z > 0.0f)
        {
            int j2 = Math.Clamp((int)jointIndices.Z, 0, maxBoneIndex);
            fusedSkin += boneTransforms[j2] * (jointWeights.Z * invTotalWeight);
        }

        if (jointWeights.W > 0.0f)
        {
            int j3 = Math.Clamp((int)jointIndices.W, 0, maxBoneIndex);
            fusedSkin += boneTransforms[j3] * (jointWeights.W * invTotalWeight);
        }

        return fusedSkin;
    }

    /// <summary>
    ///     Deforms a single vertex using Linear Blend Skinning (LBS) by fusing the weighted bone matrices first
    ///     and executing a single <see cref="Vector3.Transform(Vector3, Matrix4x4)" /> call (#128).
    /// </summary>
    public static Vector3 DeformVertexSkeletalLbs(Vector3 restPos, Vector4 jointIndices, Vector4 jointWeights,
        SkeletalPose pose)
    {
        Matrix4x4 fusedSkin = ComputeFusedSkinningMatrix(jointIndices, jointWeights, pose.BoneTransforms);
        return Vector3.Transform(restPos, fusedSkin);
    }

    /// <summary>
    ///     Batch Linear Blend Skinning (LBS) across a span of vertices with a 1-slot register cache
    ///     for consecutive vertices sharing identical <c>(jointIndices, jointWeights)</c> (#128).
    /// </summary>
    public static void DeformVertexBatchSkeletalLbs(
        ReadOnlySpan<Vector3> restPositions,
        ReadOnlySpan<Vector4> jointIndices,
        ReadOnlySpan<Vector4> jointWeights,
        SkeletalPose pose,
        Span<Vector3> outAnimatedPositions)
    {
        int count = restPositions.Length;
        if (jointIndices.Length < count || jointWeights.Length < count || outAnimatedPositions.Length < count)
        {
            throw new ArgumentException(
                $"Batch LBS span lengths must be at least restPositions.Length ({count}).");
        }

        ReadOnlySpan<Matrix4x4> bones = pose.BoneTransforms;
        bool hasCachedMatrix = false;
        Vector4 cachedIndices = default;
        Vector4 cachedWeights = default;
        Matrix4x4 cachedSkinMatrix = default;

        for (int i = 0; i < count; i++)
        {
            Vector4 indices = jointIndices[i];
            Vector4 weights = jointWeights[i];

            if (!hasCachedMatrix || indices != cachedIndices || weights != cachedWeights)
            {
                cachedIndices = indices;
                cachedWeights = weights;
                cachedSkinMatrix = ComputeFusedSkinningMatrix(indices, weights, bones);
                hasCachedMatrix = true;
            }

            outAnimatedPositions[i] = Vector3.Transform(restPositions[i], cachedSkinMatrix);
        }
    }

    /// <summary>
    ///     Transforms a local axis-aligned bounding box <c>[localMin, localMax]</c> into world space
    ///     using the Xash3D-FWGS <c>Matrix3x4_TransformAABB</c> center + absolute-basis-extents identity
    ///     (18 multiplies, 0 branches) instead of transforming all 8 box corners (#143).
    /// </summary>
    public static void TransformAabbFast(
        Vector3 localMin,
        Vector3 localMax,
        in Matrix4x4 transform,
        out Vector3 worldMin,
        out Vector3 worldMax)
    {
        Vector3 localCenter = (localMin + localMax) * 0.5f;
        Vector3 localExtents = (localMax - localMin) * 0.5f;

        // Transform center via row-vector convention (v * M) matching Vector3.Transform(v, Matrix4x4)
        float worldCenterX = (localCenter.X * transform.M11) + (localCenter.Y * transform.M21) + (localCenter.Z * transform.M31) + transform.M41;
        float worldCenterY = (localCenter.X * transform.M12) + (localCenter.Y * transform.M22) + (localCenter.Z * transform.M32) + transform.M42;
        float worldCenterZ = (localCenter.X * transform.M13) + (localCenter.Y * transform.M23) + (localCenter.Z * transform.M33) + transform.M43;

        // Transform extents via absolute upper-3x3 basis elements (9 multiplies, 0 branches)
        float worldExtentX = (localExtents.X * MathF.Abs(transform.M11))
                           + (localExtents.Y * MathF.Abs(transform.M21))
                           + (localExtents.Z * MathF.Abs(transform.M31));
        float worldExtentY = (localExtents.X * MathF.Abs(transform.M12))
                           + (localExtents.Y * MathF.Abs(transform.M22))
                           + (localExtents.Z * MathF.Abs(transform.M32));
        float worldExtentZ = (localExtents.X * MathF.Abs(transform.M13))
                           + (localExtents.Y * MathF.Abs(transform.M23))
                           + (localExtents.Z * MathF.Abs(transform.M33));

        Vector3 worldCenter = new Vector3(worldCenterX, worldCenterY, worldCenterZ);
        Vector3 worldExtents = new Vector3(worldExtentX, worldExtentY, worldExtentZ);

        worldMin = worldCenter - worldExtents;
        worldMax = worldCenter + worldExtents;
    }

    /// <summary>
    ///     4-tier fast-path entity world matrix constructor modeled after Xash3D-FWGS
    ///     <c>Matrix4x4_CreateFromEntity</c>:
    ///     Tier 0 (Zero angles = pure translation, 0 trig calls),
    ///     Tier 1 (Yaw-only = 1 SinCos call),
    ///     Tier 2 (Pitch+Yaw = 2 SinCos calls),
    ///     Tier 3 (Full Pitch+Yaw+Roll = 3 SinCos calls) (#143).
    /// </summary>
    public static Matrix4x4 CreateEntityMatrixFast(
        Vector3 origin,
        float pitchDegrees,
        float yawDegrees,
        float rollDegrees,
        out int trigTierUsed)
    {
        const float degToRad = MathF.PI / 180.0f;

        if (pitchDegrees == 0.0f && yawDegrees == 0.0f && rollDegrees == 0.0f)
        {
            trigTierUsed = 0;
            return new Matrix4x4(
                1.0f, 0.0f, 0.0f, 0.0f,
                0.0f, 1.0f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                origin.X, origin.Y, origin.Z, 1.0f);
        }

        if (pitchDegrees == 0.0f && rollDegrees == 0.0f)
        {
            trigTierUsed = 1;
            (float sy, float cy) = MathF.SinCos(yawDegrees * degToRad);
            return new Matrix4x4(
                cy,   0.0f, -sy,  0.0f,
                0.0f, 1.0f, 0.0f, 0.0f,
                sy,   0.0f, cy,   0.0f,
                origin.X, origin.Y, origin.Z, 1.0f);
        }

        if (rollDegrees == 0.0f)
        {
            trigTierUsed = 2;
            (float sy, float cy) = MathF.SinCos(yawDegrees * degToRad);
            (float sp, float cp) = MathF.SinCos(pitchDegrees * degToRad);
            return new Matrix4x4(
                cy,      0.0f, -sy,     0.0f,
                sy * sp, cp,   cy * sp, 0.0f,
                sy * cp, -sp,  cy * cp, 0.0f,
                origin.X, origin.Y, origin.Z, 1.0f);
        }

        trigTierUsed = 3;
        Matrix4x4 rot = Matrix4x4.CreateFromYawPitchRoll(
            yawDegrees * degToRad,
            pitchDegrees * degToRad,
            rollDegrees * degToRad);
        rot.M41 = origin.X;
        rot.M42 = origin.Y;
        rot.M43 = origin.Z;
        return rot;
    }
}
