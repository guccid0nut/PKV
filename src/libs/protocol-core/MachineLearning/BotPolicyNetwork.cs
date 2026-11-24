// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Globalization;
using System.Text;

namespace ASPIRE.Common.MachineLearning;

public sealed class BotPolicyForwardPass
{
    public required double[] Hidden1Pre { get; init; }
    public required double[] Hidden1 { get; init; }
    public required double[] Hidden2Pre { get; init; }
    public required double[] Hidden2 { get; init; }
    public required double[] Output { get; init; }
    public required double[] ComboProbs { get; init; }
    public required double ConfidenceProb { get; init; }
    public required double JukeProb { get; init; }
    public required double ValueEstimate { get; init; }
}

public static class BotPolicyNetwork
{
    public const double MaxAllowedWeightMagnitude = 50.0;

    public static BotPolicyCheckpoint CreateSeedBaseline(int seed = 1337)
    {
        uint rngState = unchecked((uint)seed);

        double NextPseudoRandom()
        {
            rngState = unchecked((1103515245u * rngState + 12345u) % 2147483648u);
            return (rngState / 1073741824.0) - 1.0;
        }

        double scale1 = Math.Sqrt(2.0 / BotPolicyArchitecture.StateDim) * 0.5;
        double scale2 = Math.Sqrt(2.0 / BotPolicyArchitecture.Hidden1) * 0.5;
        double scaleOut = Math.Sqrt(2.0 / BotPolicyArchitecture.Hidden2) * 0.5;

        double[][] w1 = new double[BotPolicyArchitecture.Hidden1][];
        double[] b1 = new double[BotPolicyArchitecture.Hidden1];
        for (int i = 0; i < BotPolicyArchitecture.Hidden1; i++)
        {
            b1[i] = 0.01;
            w1[i] = new double[BotPolicyArchitecture.StateDim];
            for (int j = 0; j < BotPolicyArchitecture.StateDim; j++)
            {
                w1[i][j] = NextPseudoRandom() * scale1;
            }
        }

        double[][] w2 = new double[BotPolicyArchitecture.Hidden2][];
        double[] b2 = new double[BotPolicyArchitecture.Hidden2];
        for (int i = 0; i < BotPolicyArchitecture.Hidden2; i++)
        {
            b2[i] = 0.01;
            w2[i] = new double[BotPolicyArchitecture.Hidden1];
            for (int j = 0; j < BotPolicyArchitecture.Hidden1; j++)
            {
                w2[i][j] = NextPseudoRandom() * scale2;
            }
        }

        double[][] wOut = new double[BotPolicyArchitecture.ActionDim][];
        double[] bOut = new double[BotPolicyArchitecture.ActionDim];
        for (int i = 0; i < BotPolicyArchitecture.ActionDim; i++)
        {
            bOut[i] = i == BotPolicyArchitecture.ConfidenceIndex ? 0.85 : 0.05;
            wOut[i] = new double[BotPolicyArchitecture.Hidden2];
            for (int j = 0; j < BotPolicyArchitecture.Hidden2; j++)
            {
                wOut[i][j] = NextPseudoRandom() * scaleOut;
            }
        }

        return new BotPolicyCheckpoint
        {
            SchemaVersion = 1,
            Generation = 0,
            ParentGeneration = 0,
            Role = "baseline",
            SourceFramework = "seed-1337",
            CreatedAtUtc = TimeProvider.System.GetUtcNow(),
            W1 = w1,
            B1 = b1,
            W2 = w2,
            B2 = b2,
            WOut = wOut,
            BOut = bOut
        };
    }

    public static BotPolicyCheckpoint CloneCheckpoint(
        BotPolicyCheckpoint source,
        string? newRole = null,
        int? newGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new BotPolicyCheckpoint
        {
            SchemaVersion = source.SchemaVersion,
            Generation = newGeneration ?? source.Generation,
            ParentGeneration = source.Generation,
            Role = newRole ?? source.Role,
            SourceFramework = source.SourceFramework,
            CreatedAtUtc = TimeProvider.System.GetUtcNow(),
            MeanTrainingLoss = source.MeanTrainingLoss,
            MeanEvalReturn = source.MeanEvalReturn,
            EvalWinRate = source.EvalWinRate,
            EvaluatedMatches = source.EvaluatedMatches,
            W1 = CloneMatrix(source.W1),
            B1 = [.. source.B1],
            W2 = CloneMatrix(source.W2),
            B2 = [.. source.B2],
            WOut = CloneMatrix(source.WOut),
            BOut = [.. source.BOut]
        };
    }

    public static bool ValidateCheckpoint(BotPolicyCheckpoint? checkpoint, out string? error)
    {
        if (checkpoint is null)
        {
            error = "Checkpoint is null.";
            return false;
        }

        if (!ValidateMatrix(checkpoint.W1, BotPolicyArchitecture.Hidden1, BotPolicyArchitecture.StateDim, "W1", out error) ||
            !ValidateVector(checkpoint.B1, BotPolicyArchitecture.Hidden1, "B1", out error) ||
            !ValidateMatrix(checkpoint.W2, BotPolicyArchitecture.Hidden2, BotPolicyArchitecture.Hidden1, "W2", out error) ||
            !ValidateVector(checkpoint.B2, BotPolicyArchitecture.Hidden2, "B2", out error) ||
            !ValidateMatrix(checkpoint.WOut, BotPolicyArchitecture.ActionDim, BotPolicyArchitecture.Hidden2, "WOut", out error) ||
            !ValidateVector(checkpoint.BOut, BotPolicyArchitecture.ActionDim, "BOut", out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    public static BotPolicyForwardPass Forward(BotPolicyCheckpoint model, ReadOnlySpan<double> state)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (state.Length != BotPolicyArchitecture.StateDim)
        {
            throw new ArgumentException($"Expected {BotPolicyArchitecture.StateDim} input features, got {state.Length}.", nameof(state));
        }

        double[] z1 = new double[BotPolicyArchitecture.Hidden1];
        double[] h1 = new double[BotPolicyArchitecture.Hidden1];
        for (int i = 0; i < BotPolicyArchitecture.Hidden1; i++)
        {
            double sum = model.B1[i];
            double[] wRow = model.W1[i];
            for (int j = 0; j < BotPolicyArchitecture.StateDim; j++)
            {
                sum += wRow[j] * state[j];
            }
            z1[i] = sum;
            h1[i] = sum > 0.0 ? sum : 0.0;
        }

        double[] z2 = new double[BotPolicyArchitecture.Hidden2];
        double[] h2 = new double[BotPolicyArchitecture.Hidden2];
        for (int i = 0; i < BotPolicyArchitecture.Hidden2; i++)
        {
            double sum = model.B2[i];
            double[] wRow = model.W2[i];
            for (int j = 0; j < BotPolicyArchitecture.Hidden1; j++)
            {
                sum += wRow[j] * h1[j];
            }
            z2[i] = sum;
            h2[i] = sum > 0.0 ? sum : 0.0;
        }

        double[] output = new double[BotPolicyArchitecture.ActionDim];
        for (int i = 0; i < BotPolicyArchitecture.ActionDim; i++)
        {
            double sum = model.BOut[i];
            double[] wRow = model.WOut[i];
            for (int j = 0; j < BotPolicyArchitecture.Hidden2; j++)
            {
                sum += wRow[j] * h2[j];
            }
            output[i] = sum;
        }

        double[] comboProbs = SoftmaxSlice(output, BotPolicyArchitecture.ComboLogitOffset, BotPolicyArchitecture.ComboCount);
        double confidenceProb = Sigmoid(output[BotPolicyArchitecture.ConfidenceIndex]);
        double jukeProb = Sigmoid(output[BotPolicyArchitecture.JukeIndex]);
        double valueEstimate = output[BotPolicyArchitecture.ValueHeadIndex];

        return new BotPolicyForwardPass
        {
            Hidden1Pre = z1,
            Hidden1 = h1,
            Hidden2Pre = z2,
            Hidden2 = h2,
            Output = output,
            ComboProbs = comboProbs,
            ConfidenceProb = confidenceProb,
            JukeProb = jukeProb,
            ValueEstimate = valueEstimate
        };
    }

    public static string RenderLuaWeightsModule(BotPolicyCheckpoint model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!ValidateCheckpoint(model, out string? error))
        {
            throw new InvalidOperationException($"Cannot export invalid RL checkpoint to Lua: {error}");
        }

        StringBuilder sb = new(128 * 1024);
        sb.AppendLine("-- Auto-generated Project Kongor Bot RL Policy Weights");
        sb.AppendLine("-- Architecture: 128 -> 32 (ReLU) -> 16 (ReLU) -> 16 (4,928 params)");
        sb.AppendLine("return {");
        sb.Append("  schema_version = ").Append(model.SchemaVersion.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        sb.Append("  generation = ").Append(model.Generation.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        sb.Append("  role = ").Append(FormatLuaString(model.Role)).AppendLine(",");
        sb.Append("  source = ").Append(FormatLuaString(model.SourceFramework)).AppendLine(",");
        sb.Append("  created_at_utc = ").Append(FormatLuaString(model.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture))).AppendLine(",");

        AppendLuaMatrix(sb, "W1", model.W1);
        AppendLuaVector(sb, "B1", model.B1);
        AppendLuaMatrix(sb, "W2", model.W2);
        AppendLuaVector(sb, "B2", model.B2);
        AppendLuaMatrix(sb, "W_OUT", model.WOut);
        AppendLuaVector(sb, "B_OUT", model.BOut, trailingComma: false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static double Sigmoid(double x)
    {
        if (x >= 35.0)
        {
            return 1.0 - 1e-15;
        }
        if (x <= -35.0)
        {
            return 1e-15;
        }
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    public static double[] SoftmaxSlice(double[] logits, int offset, int count)
    {
        double max = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            double v = logits[offset + i];
            if (v > max)
            {
                max = v;
            }
        }

        double sum = 0.0;
        double[] probs = new double[count];
        for (int i = 0; i < count; i++)
        {
            double e = Math.Exp(logits[offset + i] - max);
            probs[i] = e;
            sum += e;
        }

        double inv = sum > 0.0 ? 1.0 / sum : 1.0 / count;
        for (int i = 0; i < count; i++)
        {
            probs[i] *= inv;
        }
        return probs;
    }

    private static double[][] CloneMatrix(double[][] src)
    {
        double[][] copy = new double[src.Length][];
        for (int i = 0; i < src.Length; i++)
        {
            copy[i] = [.. src[i]];
        }
        return copy;
    }

    private static bool ValidateMatrix(double[][]? matrix, int rows, int cols, string name, out string? error)
    {
        if (matrix is null || matrix.Length != rows)
        {
            error = $"Matrix '{name}' row count mismatch: expected {rows}, got {matrix?.Length ?? 0}.";
            return false;
        }

        for (int i = 0; i < rows; i++)
        {
            double[]? row = matrix[i];
            if (row is null || row.Length != cols)
            {
                error = $"Matrix '{name}' row {i} column count mismatch: expected {cols}, got {row?.Length ?? 0}.";
                return false;
            }

            for (int j = 0; j < cols; j++)
            {
                double val = row[j];
                if (!double.IsFinite(val) || Math.Abs(val) > MaxAllowedWeightMagnitude)
                {
                    error = $"Matrix '{name}[{i}][{j}]' weight out of bounds or non-finite: {val}.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateVector(double[]? vec, int len, string name, out string? error)
    {
        if (vec is null || vec.Length != len)
        {
            error = $"Vector '{name}' length mismatch: expected {len}, got {vec?.Length ?? 0}.";
            return false;
        }

        for (int i = 0; i < len; i++)
        {
            double val = vec[i];
            if (!double.IsFinite(val) || Math.Abs(val) > MaxAllowedWeightMagnitude)
            {
                error = $"Vector '{name}[{i}]' bias out of bounds or non-finite: {val}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static void AppendLuaVector(StringBuilder sb, string name, double[] vec, bool trailingComma = true)
    {
        sb.Append("  ").Append(name).Append(" = {");
        for (int i = 0; i < vec.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(vec[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append('}');
        if (trailingComma)
        {
            sb.Append(',');
        }
        sb.AppendLine();
    }

    private static void AppendLuaMatrix(StringBuilder sb, string name, double[][] matrix)
    {
        sb.Append("  ").Append(name).AppendLine(" = {");
        for (int i = 0; i < matrix.Length; i++)
        {
            sb.Append("    {");
            double[] row = matrix[i];
            for (int j = 0; j < row.Length; j++)
            {
                if (j > 0)
                {
                    sb.Append(',');
                }
                sb.Append(row[j].ToString("G9", CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            if (i < matrix.Length - 1)
            {
                sb.Append(',');
            }
            sb.AppendLine();
        }
        sb.AppendLine("  },");
    }

    private static string FormatLuaString(string value)
    {
        string sanitized = (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        return $"\"{sanitized}\"";
    }
}
