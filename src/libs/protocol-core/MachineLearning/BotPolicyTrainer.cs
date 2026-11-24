// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

namespace ASPIRE.Common.MachineLearning;

public sealed record AwacTrainingOptions(
    int Epochs = 6,
    double LearningRate = 3e-3,
    double WeightDecay = 1e-4,
    double Gamma = 0.99,
    double GaeLambda = 0.95,
    double AwacTemperature = 0.5,
    double MaxAdvantageWeight = 20.0,
    double ValueLossCoeff = 0.5,
    double ConfidenceLossCoeff = 0.25,
    double MaxGradNorm = 1.0);

internal sealed class AwacSample
{
    public required double[] State { get; init; }
    public required int ComboAction { get; init; }
    public required bool JukeFlag { get; init; }
    public required double MoveX { get; init; }
    public required double MoveY { get; init; }
    public required double TargetReturn { get; init; }
    public required double NormalizedAdvantage { get; init; }
    public required double AwacWeight { get; init; }
}

public static class BotPolicyTrainer
{
    public static (BotPolicyCheckpoint Candidate, RlTrainingSummary Summary) TrainAwac(
        BotPolicyCheckpoint startingModel,
        IReadOnlyList<RlEpisodeRecord> episodes,
        AwacTrainingOptions? options = null,
        int? nextGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(startingModel);
        ArgumentNullException.ThrowIfNull(episodes);

        if (!BotPolicyNetwork.ValidateCheckpoint(startingModel, out string? validationError))
        {
            throw new InvalidOperationException($"Cannot train from invalid starting model: {validationError}");
        }

        AwacTrainingOptions opt = options ?? new AwacTrainingOptions();
        int targetGen = nextGeneration ?? (startingModel.Generation + 1);
        BotPolicyCheckpoint candidate = BotPolicyNetwork.CloneCheckpoint(startingModel, "candidate", targetGen);
        candidate.SourceFramework = "csharp-awac";

        List<AwacSample> samples = BuildSamples(candidate, episodes, opt);
        if (samples.Count == 0)
        {
            return (candidate, new RlTrainingSummary(
                EpisodesUsed: episodes.Count,
                StepsUsed: 0,
                Epochs: 0,
                InitialLoss: 0.0,
                FinalLoss: 0.0,
                PolicyLoss: 0.0,
                ValueLoss: 0.0,
                ConfidenceLoss: 0.0,
                NewCandidateGeneration: targetGen));
        }

        AdamWState adam = new();
        double initialLoss = 0.0;
        double finalLoss = 0.0;
        double lastPolicyLoss = 0.0;
        double lastValueLoss = 0.0;
        double lastConfLoss = 0.0;

        GradientAccumulator grads = new();

        for (int epoch = 0; epoch < Math.Max(1, opt.Epochs); epoch++)
        {
            grads.Clear();
            double totalPolicyLoss = 0.0;
            double totalValueLoss = 0.0;
            double totalConfLoss = 0.0;

            foreach (AwacSample sample in samples)
            {
                BotPolicyForwardPass fwd = BotPolicyNetwork.Forward(candidate, sample.State);
                double[] dOut = new double[BotPolicyArchitecture.ActionDim];

                // 1. Discrete Combo AWAC Policy Loss: -w * log(pi[a])
                int combo = Math.Clamp(sample.ComboAction, 0, BotPolicyArchitecture.ComboCount - 1);
                double pCombo = Math.Max(1e-8, fwd.ComboProbs[combo]);
                totalPolicyLoss += -sample.AwacWeight * Math.Log(pCombo);
                for (int c = 0; c < BotPolicyArchitecture.ComboCount; c++)
                {
                    double target = c == combo ? 1.0 : 0.0;
                    dOut[BotPolicyArchitecture.ComboLogitOffset + c] += sample.AwacWeight * (fwd.ComboProbs[c] - target);
                }

                // 2. Juke BCE weighted by AWAC weight
                double yJuke = sample.JukeFlag ? 1.0 : 0.0;
                double pJuke = Math.Clamp(fwd.JukeProb, 1e-7, 1.0 - 1e-7);
                totalPolicyLoss += -sample.AwacWeight * 0.25 * (yJuke * Math.Log(pJuke) + (1.0 - yJuke) * Math.Log(1.0 - pJuke));
                dOut[BotPolicyArchitecture.JukeIndex] += sample.AwacWeight * 0.25 * (pJuke - yJuke);

                // 3. Continuous Move regression when advantage > 0
                if (sample.NormalizedAdvantage > 0.0)
                {
                    double dx = fwd.Output[BotPolicyArchitecture.MoveXIndex] - sample.MoveX;
                    double dy = fwd.Output[BotPolicyArchitecture.MoveYIndex] - sample.MoveY;
                    double moveScale = 0.2 * sample.AwacWeight;
                    totalPolicyLoss += 0.5 * moveScale * (dx * dx + dy * dy);
                    dOut[BotPolicyArchitecture.MoveXIndex] += moveScale * dx;
                    dOut[BotPolicyArchitecture.MoveYIndex] += moveScale * dy;
                }

                // 4. Critic Value MSE: 0.5 * c_v * (V - G)^2
                double vErr = fwd.ValueEstimate - sample.TargetReturn;
                totalValueLoss += 0.5 * opt.ValueLossCoeff * vErr * vErr;
                dOut[BotPolicyArchitecture.ValueHeadIndex] += opt.ValueLossCoeff * vErr;

                // 5. Confidence BCE predicting positive return probability
                double yConf = sample.TargetReturn > 0.0 ? 1.0 : 0.0;
                double pConf = Math.Clamp(fwd.ConfidenceProb, 1e-7, 1.0 - 1e-7);
                totalConfLoss += -opt.ConfidenceLossCoeff * (yConf * Math.Log(pConf) + (1.0 - yConf) * Math.Log(1.0 - pConf));
                dOut[BotPolicyArchitecture.ConfidenceIndex] += opt.ConfidenceLossCoeff * (pConf - yConf);

                AccumulateBackprop(candidate, sample.State, fwd, dOut, grads);
            }

            double invN = 1.0 / samples.Count;
            grads.Scale(invN);

            lastPolicyLoss = totalPolicyLoss * invN;
            lastValueLoss = totalValueLoss * invN;
            lastConfLoss = totalConfLoss * invN;
            double totalLoss = lastPolicyLoss + lastValueLoss + lastConfLoss;

            if (epoch == 0)
            {
                initialLoss = totalLoss;
            }
            finalLoss = totalLoss;

            grads.ClipGlobalNorm(opt.MaxGradNorm);
            adam.Step(candidate, grads, opt.LearningRate, opt.WeightDecay);
        }

        if (!BotPolicyNetwork.ValidateCheckpoint(candidate, out string? postError))
        {
            throw new InvalidOperationException($"Trained AWAC candidate failed finite/bounds verification: {postError}");
        }

        candidate.MeanTrainingLoss = finalLoss;
        candidate.CreatedAtUtc = TimeProvider.System.GetUtcNow();

        return (candidate, new RlTrainingSummary(
            EpisodesUsed: episodes.Count,
            StepsUsed: samples.Count,
            Epochs: Math.Max(1, opt.Epochs),
            InitialLoss: initialLoss,
            FinalLoss: finalLoss,
            PolicyLoss: lastPolicyLoss,
            ValueLoss: lastValueLoss,
            ConfidenceLoss: lastConfLoss,
            NewCandidateGeneration: targetGen));
    }

    private static List<AwacSample> BuildSamples(
        BotPolicyCheckpoint criticModel,
        IReadOnlyList<RlEpisodeRecord> episodes,
        AwacTrainingOptions opt)
    {
        List<AwacSample> samples = [];
        List<double> rawAdvantages = [];

        foreach (RlEpisodeRecord ep in episodes)
        {
            if (ep.Steps is null || ep.Steps.Count == 0)
            {
                continue;
            }

            foreach (IGrouping<(int Team, string Hero), RlTrajectoryStep> trajGroup in
                ep.Steps.GroupBy(s => (s.Team, s.Hero ?? string.Empty)))
            {
                List<RlTrajectoryStep> traj = trajGroup
                    .Where(s => s.Features is { Length: BotPolicyArchitecture.StateDim } &&
                                double.IsFinite(s.Reward) &&
                                s.Features.All(double.IsFinite))
                    .OrderBy(s => s.Tick)
                    .ToList();

                if (traj.Count == 0)
                {
                    continue;
                }

                double[] values = new double[traj.Count];
                for (int i = 0; i < traj.Count; i++)
                {
                    values[i] = BotPolicyNetwork.Forward(criticModel, traj[i].Features).ValueEstimate;
                }

                double[] returns = new double[traj.Count];
                double[] advantages = new double[traj.Count];

                double gae = 0.0;
                for (int t = traj.Count - 1; t >= 0; t--)
                {
                    double nonTerminal = (t == traj.Count - 1 || traj[t].Done) ? 0.0 : 1.0;
                    double nextValue = (t < traj.Count - 1 && !traj[t].Done) ? values[t + 1] : 0.0;
                    double delta = traj[t].Reward + opt.Gamma * nextValue * nonTerminal - values[t];
                    gae = delta + opt.Gamma * opt.GaeLambda * nonTerminal * gae;
                    advantages[t] = gae;
                    returns[t] = gae + values[t];
                }

                for (int t = 0; t < traj.Count; t++)
                {
                    rawAdvantages.Add(advantages[t]);
                    samples.Add(new AwacSample
                    {
                        State = traj[t].Features,
                        ComboAction = traj[t].ComboAction,
                        JukeFlag = traj[t].JukeFlag,
                        MoveX = Math.Clamp(traj[t].MoveX, -1.0, 1.0),
                        MoveY = Math.Clamp(traj[t].MoveY, -1.0, 1.0),
                        TargetReturn = returns[t],
                        NormalizedAdvantage = advantages[t],
                        AwacWeight = 1.0
                    });
                }
            }
        }

        if (samples.Count == 0)
        {
            return samples;
        }

        double mean = 0.0;
        for (int i = 0; i < rawAdvantages.Count; i++)
        {
            mean += rawAdvantages[i];
        }
        mean /= rawAdvantages.Count;

        double variance = 0.0;
        for (int i = 0; i < rawAdvantages.Count; i++)
        {
            double d = rawAdvantages[i] - mean;
            variance += d * d;
        }
        double std = Math.Sqrt(variance / rawAdvantages.Count) + 1e-6;

        List<AwacSample> weighted = new(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            AwacSample s = samples[i];
            double normAdv = (rawAdvantages[i] - mean) / std;
            double scaled = Math.Clamp(normAdv / Math.Max(0.05, opt.AwacTemperature), -5.0, 5.0);
            double w = Math.Min(opt.MaxAdvantageWeight, Math.Exp(scaled));
            weighted.Add(new AwacSample
            {
                State = s.State,
                ComboAction = s.ComboAction,
                JukeFlag = s.JukeFlag,
                MoveX = s.MoveX,
                MoveY = s.MoveY,
                TargetReturn = s.TargetReturn,
                NormalizedAdvantage = normAdv,
                AwacWeight = w
            });
        }

        return weighted;
    }

    private static void AccumulateBackprop(
        BotPolicyCheckpoint model,
        double[] state,
        BotPolicyForwardPass fwd,
        double[] dOut,
        GradientAccumulator grads)
    {
        double[] dH2 = new double[BotPolicyArchitecture.Hidden2];
        for (int i = 0; i < BotPolicyArchitecture.ActionDim; i++)
        {
            double gOut = dOut[i];
            if (gOut == 0.0)
            {
                continue;
            }
            grads.BOut[i] += gOut;
            double[] gwRow = grads.WOut[i];
            double[] wRow = model.WOut[i];
            for (int j = 0; j < BotPolicyArchitecture.Hidden2; j++)
            {
                gwRow[j] += gOut * fwd.Hidden2[j];
                dH2[j] += gOut * wRow[j];
            }
        }

        double[] dH1 = new double[BotPolicyArchitecture.Hidden1];
        for (int i = 0; i < BotPolicyArchitecture.Hidden2; i++)
        {
            if (fwd.Hidden2Pre[i] <= 0.0)
            {
                continue;
            }
            double gZ2 = dH2[i];
            if (gZ2 == 0.0)
            {
                continue;
            }
            grads.B2[i] += gZ2;
            double[] gwRow = grads.W2[i];
            double[] wRow = model.W2[i];
            for (int j = 0; j < BotPolicyArchitecture.Hidden1; j++)
            {
                gwRow[j] += gZ2 * fwd.Hidden1[j];
                dH1[j] += gZ2 * wRow[j];
            }
        }

        for (int i = 0; i < BotPolicyArchitecture.Hidden1; i++)
        {
            if (fwd.Hidden1Pre[i] <= 0.0)
            {
                continue;
            }
            double gZ1 = dH1[i];
            if (gZ1 == 0.0)
            {
                continue;
            }
            grads.B1[i] += gZ1;
            double[] gwRow = grads.W1[i];
            for (int j = 0; j < BotPolicyArchitecture.StateDim; j++)
            {
                gwRow[j] += gZ1 * state[j];
            }
        }
    }

    private sealed class GradientAccumulator
    {
        public readonly double[][] W1 = AllocMatrix(BotPolicyArchitecture.Hidden1, BotPolicyArchitecture.StateDim);
        public readonly double[] B1 = new double[BotPolicyArchitecture.Hidden1];
        public readonly double[][] W2 = AllocMatrix(BotPolicyArchitecture.Hidden2, BotPolicyArchitecture.Hidden1);
        public readonly double[] B2 = new double[BotPolicyArchitecture.Hidden2];
        public readonly double[][] WOut = AllocMatrix(BotPolicyArchitecture.ActionDim, BotPolicyArchitecture.Hidden2);
        public readonly double[] BOut = new double[BotPolicyArchitecture.ActionDim];

        public void Clear()
        {
            ClearMatrix(W1);
            Array.Clear(B1);
            ClearMatrix(W2);
            Array.Clear(B2);
            ClearMatrix(WOut);
            Array.Clear(BOut);
        }

        public void Scale(double factor)
        {
            ScaleMatrix(W1, factor);
            ScaleVector(B1, factor);
            ScaleMatrix(W2, factor);
            ScaleVector(B2, factor);
            ScaleMatrix(WOut, factor);
            ScaleVector(BOut, factor);
        }

        public void ClipGlobalNorm(double maxNorm)
        {
            if (maxNorm <= 0.0)
            {
                return;
            }

            double sqSum =
                SumSquaresMatrix(W1) + SumSquaresVector(B1) +
                SumSquaresMatrix(W2) + SumSquaresVector(B2) +
                SumSquaresMatrix(WOut) + SumSquaresVector(BOut);

            double norm = Math.Sqrt(sqSum);
            if (norm > maxNorm)
            {
                Scale(maxNorm / norm);
            }
        }
    }

    private sealed class AdamWState
    {
        private const double Beta1 = 0.9;
        private const double Beta2 = 0.999;
        private const double Eps = 1e-8;
        private int _t;

        private readonly double[][] _mW1 = AllocMatrix(BotPolicyArchitecture.Hidden1, BotPolicyArchitecture.StateDim);
        private readonly double[][] _vW1 = AllocMatrix(BotPolicyArchitecture.Hidden1, BotPolicyArchitecture.StateDim);
        private readonly double[] _mB1 = new double[BotPolicyArchitecture.Hidden1];
        private readonly double[] _vB1 = new double[BotPolicyArchitecture.Hidden1];

        private readonly double[][] _mW2 = AllocMatrix(BotPolicyArchitecture.Hidden2, BotPolicyArchitecture.Hidden1);
        private readonly double[][] _vW2 = AllocMatrix(BotPolicyArchitecture.Hidden2, BotPolicyArchitecture.Hidden1);
        private readonly double[] _mB2 = new double[BotPolicyArchitecture.Hidden2];
        private readonly double[] _vB2 = new double[BotPolicyArchitecture.Hidden2];

        private readonly double[][] _mWOut = AllocMatrix(BotPolicyArchitecture.ActionDim, BotPolicyArchitecture.Hidden2);
        private readonly double[][] _vWOut = AllocMatrix(BotPolicyArchitecture.ActionDim, BotPolicyArchitecture.Hidden2);
        private readonly double[] _mBOut = new double[BotPolicyArchitecture.ActionDim];
        private readonly double[] _vBOut = new double[BotPolicyArchitecture.ActionDim];

        public void Step(BotPolicyCheckpoint model, GradientAccumulator grads, double lr, double weightDecay)
        {
            _t++;
            double bc1 = 1.0 - Math.Pow(Beta1, _t);
            double bc2 = 1.0 - Math.Pow(Beta2, _t);

            UpdateMatrix(model.W1, grads.W1, _mW1, _vW1, lr, weightDecay, bc1, bc2);
            UpdateVector(model.B1, grads.B1, _mB1, _vB1, lr, 0.0, bc1, bc2);
            UpdateMatrix(model.W2, grads.W2, _mW2, _vW2, lr, weightDecay, bc1, bc2);
            UpdateVector(model.B2, grads.B2, _mB2, _vB2, lr, 0.0, bc1, bc2);
            UpdateMatrix(model.WOut, grads.WOut, _mWOut, _vWOut, lr, weightDecay, bc1, bc2);
            UpdateVector(model.BOut, grads.BOut, _mBOut, _vBOut, lr, 0.0, bc1, bc2);
        }

        private static void UpdateMatrix(
            double[][] weights,
            double[][] grads,
            double[][] m,
            double[][] v,
            double lr,
            double weightDecay,
            double bc1,
            double bc2)
        {
            for (int i = 0; i < weights.Length; i++)
            {
                UpdateVector(weights[i], grads[i], m[i], v[i], lr, weightDecay, bc1, bc2);
            }
        }

        private static void UpdateVector(
            double[] weights,
            double[] grads,
            double[] m,
            double[] v,
            double lr,
            double weightDecay,
            double bc1,
            double bc2)
        {
            for (int i = 0; i < weights.Length; i++)
            {
                double g = grads[i];
                double mi = Beta1 * m[i] + (1.0 - Beta1) * g;
                double vi = Beta2 * v[i] + (1.0 - Beta2) * g * g;
                m[i] = mi;
                v[i] = vi;

                double mHat = mi / bc1;
                double vHat = vi / bc2;

                double w = weights[i];
                if (weightDecay > 0.0)
                {
                    w -= lr * weightDecay * w;
                }
                w -= lr * mHat / (Math.Sqrt(vHat) + Eps);
                weights[i] = Math.Clamp(w, -BotPolicyNetwork.MaxAllowedWeightMagnitude, BotPolicyNetwork.MaxAllowedWeightMagnitude);
            }
        }
    }

    private static double[][] AllocMatrix(int rows, int cols)
    {
        double[][] m = new double[rows][];
        for (int i = 0; i < rows; i++)
        {
            m[i] = new double[cols];
        }
        return m;
    }

    private static void ClearMatrix(double[][] m)
    {
        for (int i = 0; i < m.Length; i++)
        {
            Array.Clear(m[i]);
        }
    }

    private static void ScaleMatrix(double[][] m, double factor)
    {
        for (int i = 0; i < m.Length; i++)
        {
            ScaleVector(m[i], factor);
        }
    }

    private static void ScaleVector(double[] v, double factor)
    {
        for (int i = 0; i < v.Length; i++)
        {
            v[i] *= factor;
        }
    }

    private static double SumSquaresMatrix(double[][] m)
    {
        double sum = 0.0;
        for (int i = 0; i < m.Length; i++)
        {
            sum += SumSquaresVector(m[i]);
        }
        return sum;
    }

    private static double SumSquaresVector(double[] v)
    {
        double sum = 0.0;
        for (int i = 0; i < v.Length; i++)
        {
            sum += v[i] * v[i];
        }
        return sum;
    }
}
