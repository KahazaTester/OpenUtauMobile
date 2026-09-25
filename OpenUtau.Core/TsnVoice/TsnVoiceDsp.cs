using System;
using System.Collections.Generic;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 数字信号处理，托管移植自原生 dsp.cpp。
    /// 包含 MLSA 滤波器、混合激励、MLSA 声码及 48kHz→44.1kHz 重采样。
    /// </summary>
    public static class TsnVoiceDsp {
        public const double UnvoicedLf0 = -1.0e10;

        static readonly double[] Pade4 = new double[] {
            1.0, 0.49992730000000002, 0.1067005, 0.011702209999999999, 0.00056562789999999995,
        };

        static readonly double[] Pade5 = new double[] {
            1.0, 0.49993910000000003, 0.1107098, 0.01369984,
            0.00095648529999999997, 3.0417210000000001e-05,
        };

        static readonly double[] Pade6 = new double[] {
            1.0, 0.49996289243801401, 0.11330188501344, 0.014990477313604001,
            0.001229199693052, 5.9608811847000002e-05, 1.343163774e-06,
        };

        static readonly double[] Pade7 = new double[] {
            1.0, 0.49996908707263699, 0.11507703309046, 0.015876603489178,
            0.001424479579072, 8.3492347364999997e-05, 2.9724569789999998e-06,
            4.9755937000000002e-08,
        };

        static double[] Pade(int order) {
            switch (order) {
                case 4:
                    return Pade4;
                case 5:
                    return Pade5;
                case 6:
                    return Pade6;
                default:
                    return Pade7;
            }
        }

        public static double MidiToFreq(double midi) {
            return 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);
        }

        public static double FreqToMidi(double freq) {
            return 69.0 + 12.0 * Math.Log(freq / 440.0, 2.0);
        }

        /// <summary>
        /// 乐谱音高转模型 LF0：该引擎 key 60 为 C5，比常规 MIDI 显示高八度，
        /// 仅在模型输入边界换算。
        /// </summary>
        public static double ScoreLf0(double pitch) {
            if (double.IsNaN(pitch) || double.IsInfinity(pitch)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                    "音高包含 NaN/Inf");
            }
            return Math.Clamp(Math.Log(16.351597831287414) + (pitch - 12.0) * Math.Log(2.0) / 12.0,
                Math.Log(20.0), Math.Log(20000.0));
        }

        public static void RequireFinite(double[] values, string what) {
            foreach (double value in values) {
                if (double.IsNaN(value) || double.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        what + " 包含 NaN/Inf");
                }
            }
        }

        /// <summary>
        /// 确定性高斯随机数（LCG + Box-Muller），与原生实现同种子同序列。
        /// </summary>
        public class GaussianRandom {
            ulong seed;
            readonly ulong initialSeed;
            bool hasSpare;
            double spare;

            public GaussianRandom(ulong seed = 1) {
                this.seed = seed;
                this.initialSeed = seed;
            }

            public void Reset() {
                seed = initialSeed;
                hasSpare = false;
                spare = 0;
            }

            double Uniform() {
                seed = 1103515245UL * seed + 12345UL;
                uint integer = (uint)((seed >> 16) & 0x7fffU);
                return 2.0 * integer / 32767.0 - 1.0;
            }

            public double Next() {
                if (hasSpare) {
                    hasSpare = false;
                    return spare;
                }
                double first = 0;
                double second = 0;
                double radius = 0;
                do {
                    first = Uniform();
                    second = Uniform();
                    radius = first * first + second * second;
                } while (radius == 0 || radius > 1);
                double factor = Math.Sqrt(-2.0 * Math.Log(radius) / radius);
                spare = second * factor;
                hasSpare = true;
                return first * factor;
            }
        }

        /// <summary>
        /// MLSA 滤波器。
        /// </summary>
        public class MlsaFilter {
            readonly int coefficientDimension;
            double alpha;
            double alphaComplement;
            readonly int padeOrder;
            double[] current;
            double[] target;
            double[] increment;
            double[] state;
            bool initialized;

            public MlsaFilter(int coefficientDimension, double alpha = 0.55,
                int padeOrder = 4) {
                if (coefficientDimension < 2 || double.IsNaN(alpha)
                    || double.IsInfinity(alpha)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的 MLSA 配置");
                }
                this.coefficientDimension = coefficientDimension;
                this.alpha = Math.Clamp(alpha, -1.0, 0.6);
                this.alphaComplement = 1.0 - this.alpha * this.alpha;
                this.padeOrder = Math.Clamp(padeOrder, 4, 7);
                this.current = new double[coefficientDimension];
                this.target = new double[coefficientDimension];
                this.increment = new double[coefficientDimension];
                this.state = new double[StateSize()];
            }

            int StateSize() {
                return padeOrder * coefficientDimension + 4 * padeOrder + 3;
            }

            public void Reset() {
                Array.Clear(current, 0, current.Length);
                Array.Clear(target, 0, target.Length);
                Array.Clear(increment, 0, increment.Length);
                state = new double[StateSize()];
                initialized = false;
            }

            public void SetAlpha(double value) {
                if (double.IsNaN(value) || double.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的 MLSA alpha");
                }
                alpha = Math.Clamp(value, -1.0, 0.6);
                alphaComplement = 1.0 - alpha * alpha;
            }

            static double[] MelCepstrumToFilter(double[] source, double alpha) {
                if (alpha == 0) {
                    return (double[])source.Clone();
                }
                double[] result = new double[source.Length];
                double previous = source[source.Length - 1];
                result[result.Length - 1] = previous;
                for (int index = source.Length - 1; index-- > 1;) {
                    previous = source[index] - alpha * previous;
                    result[index] = previous;
                }
                double c0 = Math.Clamp(source[0], -5.0, 10.0);
                result[0] = c0 - alpha * result[1];
                return result;
            }

            public void StartFrame(double[] melCepstrum, int framePeriod,
                int sampleOffset = 0) {
                if (framePeriod == 0 || sampleOffset > framePeriod
                    || melCepstrum.Length != coefficientDimension) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的 MLSA 帧");
                }
                RequireFinite(melCepstrum, "mel cepstrum");
                double[] converted = MelCepstrumToFilter(melCepstrum, alpha);
                if (!initialized) {
                    current = converted;
                    initialized = true;
                }
                target = converted;
                for (int index = 0; index < coefficientDimension; index++) {
                    increment[index] = (target[index] - current[index]) / framePeriod;
                    current[index] += increment[index] * sampleOffset;
                }
            }

            double ApplyScalar(double value) {
                int order = padeOrder;
                double[] coefficients = current;
                double[] padeValues = Pade(padeOrder);
                double accumulated = 0;
                for (int index = order; index > 0; index--) {
                    state[index] = alphaComplement * state[order + index]
                        + alpha * state[index];
                    state[order + index + 1] = state[index] * coefficients[1];
                    double contribution = state[order + index + 1] * padeValues[index];
                    value += index % 2 == 1 ? contribution : -contribution;
                    accumulated += contribution;
                }
                state[order + 1] = value;
                value += accumulated;
                int blockBase = 2 * (order + 1);
                int blockWidth = coefficientDimension + 1;
                int tailBase = blockBase + order * blockWidth;
                accumulated = 0;
                for (int index = order; index > 0; index--) {
                    int start = blockBase + (index - 1) * blockWidth;
                    state[start] = state[tailBase + index - 1];
                    state[start + 1] = alphaComplement * state[start]
                        + alpha * state[start + 1];
                    double filtered = 0;
                    for (int coefficient = 2; coefficient < coefficientDimension;
                        coefficient++) {
                        state[start + coefficient] += alpha
                            * (state[start + coefficient + 1]
                                - state[start + coefficient - 1]);
                        filtered += state[start + coefficient] * coefficients[coefficient];
                    }
                    for (int coefficient = coefficientDimension; coefficient > 1;
                        coefficient--) {
                        state[start + coefficient] = state[start + coefficient - 1];
                    }
                    state[tailBase + index] = filtered;
                    double contribution = filtered * padeValues[index];
                    value += index % 2 == 1 ? contribution : -contribution;
                    accumulated += contribution;
                }
                state[tailBase] = value;
                return value + accumulated;
            }

            public double ApplySample(double sample) {
                if (!initialized || double.IsNaN(sample) || double.IsInfinity(sample)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的 MLSA 采样或帧状态");
                }
                if (sample != 0) {
                    sample *= Math.Exp(current[0]);
                }
                double result = ApplyScalar(sample);
                for (int index = 0; index < coefficientDimension; index++) {
                    current[index] += increment[index];
                }
                return result;
            }

            public void EndFrame() {
                if (initialized) {
                    current = target;
                }
            }
        }

        const int FftLength = 256;

        // 固定 256 点网格的余弦表：与逐个调用 Math.Cos(2πfs/256)
        // 按位一致，仅消除重复计算，不改变任何数值。
        static readonly double[,] CosTable = BuildCosTable();

        static double[,] BuildCosTable() {
            double[,] table = new double[FftLength / 2 + 1, FftLength];
            for (int frequency = 0; frequency <= FftLength / 2; frequency++) {
                for (int sample = 0; sample < FftLength; sample++) {
                    table[frequency, sample] = Math.Cos(2.0 * Math.PI
                        * frequency * sample / FftLength);
                }
            }
            return table;
        }

        /// <summary>
        /// BAP 倒谱滤波暂存：逐帧复用，消除每帧 6 次数组分配；
        /// 运算顺序与逐次分配一致，数值不变。
        /// </summary>
        public class BapScratch {
            readonly double[] padded = new double[FftLength];
            readonly double[] spectrum = new double[FftLength / 2 + 1];
            readonly double[] noisePower = new double[FftLength / 2 + 1];
            readonly double[] pulsePower = new double[FftLength / 2 + 1];
            readonly double[] noise;
            readonly double[] pulse;

            public BapScratch(int bapDimensions) {
                noise = new double[bapDimensions];
                pulse = new double[bapDimensions];
            }

            public void Compute(double[] bap, out double[] noiseOut, out double[] pulseOut) {
                Array.Clear(padded, 0, padded.Length);
                Array.Copy(bap, padded, Math.Min(bap.Length, FftLength));
                for (int frequency = 0; frequency < spectrum.Length; frequency++) {
                    double real = 0;
                    for (int sample = 0; sample < FftLength; sample++) {
                        real += padded[sample] * CosTable[frequency, sample];
                    }
                    spectrum[frequency] = real;
                }
                for (int index = 0; index < spectrum.Length; index++) {
                    double noiseGain = 1.0 / (Math.Exp(spectrum[index]) + 1.0);
                    double pulseGain = 1.0 - noiseGain;
                    noisePower[index] = Math.Log(noiseGain * noiseGain);
                    pulsePower[index] = Math.Log(pulseGain * pulseGain);
                }
                InverseCepstrumInto(noisePower, noise);
                InverseCepstrumInto(pulsePower, pulse);
                noiseOut = noise;
                pulseOut = pulse;
            }
        }

        static double[] InverseCepstrum(double[] values, int size) {
            double[] result = new double[size];
            InverseCepstrumInto(values, result);
            return result;
        }

        static void InverseCepstrumInto(double[] values, double[] result) {
            int size = result.Length;
            for (int sample = 0; sample < size; sample++) {
                double value = values[0] + values[values.Length - 1]
                    * (sample % 2 == 0 ? 1.0 : -1.0);
                for (int frequency = 1; frequency < FftLength / 2; frequency++) {
                    value += 2.0 * values[frequency] * CosTable[frequency, sample];
                }
                result[sample] = value / FftLength;
            }
            result[0] *= 0.5;
        }

        /// <summary>
        /// 混合激励（脉冲 + 噪声经 BAP 倒谱滤波）。
        /// </summary>
        public class MixedExcitation {
            readonly int bapDimensions;
            readonly MlsaFilter noiseFilter;
            readonly MlsaFilter pulseFilter;
            readonly BapScratch bapScratch;
            readonly GaussianRandom random = new GaussianRandom(1);
            double pitchDelta;
            double currentPitch;
            double pulsePhase;

            public MixedExcitation(int bapDimensions, int sampleRate = 48000,
                double alpha = 0.55, ulong seed = 1) {
                if (bapDimensions < 2 || bapDimensions > FftLength / 2 + 1
                    || sampleRate <= 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的混合激励配置");
                }
                this.bapDimensions = bapDimensions;
                this.noiseFilter = new MlsaFilter(bapDimensions, alpha, 4);
                this.pulseFilter = new MlsaFilter(bapDimensions, alpha, 4);
                this.bapScratch = new BapScratch(bapDimensions);
                this.random = new GaussianRandom(seed);
            }

            public void Reset() {
                pitchDelta = 0;
                currentPitch = 0;
                pulsePhase = 0;
                noiseFilter.Reset();
                pulseFilter.Reset();
                random.Reset();
            }

            public void StartFrame(double pitchPeriod, double[] bap, int framePeriod) {
                if (double.IsNaN(pitchPeriod) || double.IsInfinity(pitchPeriod)
                    || pitchPeriod < 0 || framePeriod == 0
                    || bap.Length != bapDimensions) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的混合激励帧");
                }
                RequireFinite(bap, "BAP");
                if (pitchPeriod == 0 || currentPitch == 0) {
                    pitchDelta = 0;
                    currentPitch = pitchPeriod;
                    pulsePhase = pitchPeriod;
                } else {
                    pitchDelta = (pitchPeriod - currentPitch) / framePeriod;
                }
                bapScratch.Compute(bap, out double[] noise, out double[] pulse);
                noiseFilter.StartFrame(noise, framePeriod);
                pulseFilter.StartFrame(pulse, framePeriod);
            }

            public double Next() {
                double gaussian = random.Next();
                double pulse = 0;
                double pitch = currentPitch;
                if (pitch != 0) {
                    pulsePhase += 1;
                    if (pitch <= pulsePhase) {
                        pulse = Math.Sqrt(pitch);
                        pulsePhase -= pitch;
                    }
                    currentPitch = pitch + pitchDelta;
                }
                return noiseFilter.ApplySample(gaussian)
                    + pulseFilter.ApplySample(pulse);
            }

            public void EndFrame(double pitchPeriod) {
                if (double.IsNaN(pitchPeriod) || double.IsInfinity(pitchPeriod)
                    || pitchPeriod < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "无效的基音周期");
                }
                currentPitch = pitchPeriod;
                noiseFilter.EndFrame();
                pulseFilter.EndFrame();
            }
        }

        public static double[] Lf0ToPitchPeriod(double[] lf0, int sampleRate) {
            if (sampleRate <= 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError, "采样率必须为正");
            }
            RequireFinite(lf0, "LF0");
            double minimum = Math.Log(20.0);
            double maximum = Math.Log(20000.0);
            double[] result = new double[lf0.Length];
            for (int i = 0; i < lf0.Length; i++) {
                if (lf0[i] == UnvoicedLf0) {
                    result[i] = 0;
                } else if (lf0[i] <= minimum) {
                    result[i] = sampleRate / 20.0;
                } else if (lf0[i] >= maximum) {
                    result[i] = sampleRate / 20000.0;
                } else {
                    result[i] = sampleRate / Math.Exp(lf0[i]);
                }
            }
            return result;
        }

        public static double[] GenerateRawExcitation(double[] lf0, double[,] bap,
            int sampleRate, int framePeriod, double alpha = 0.55) {
            if (lf0.Length != bap.GetLength(0) || framePeriod == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "LF0/BAP 帧数不一致");
            }
            double[] periods = Lf0ToPitchPeriod(lf0, sampleRate);
            MixedExcitation generator = new MixedExcitation(bap.GetLength(1), sampleRate, alpha);
            generator.Reset();
            double[] result = new double[lf0.Length * framePeriod];
            double[] frameBap = new double[bap.GetLength(1)];
            int write = 0;
            for (int frame = 0; frame < lf0.Length; frame++) {
                for (int i = 0; i < frameBap.Length; i++) {
                    frameBap[i] = bap[frame, i];
                }
                generator.StartFrame(periods[frame], frameBap, framePeriod);
                for (int sample = 0; sample < framePeriod; sample++) {
                    result[write++] = generator.Next();
                }
                generator.EndFrame(periods[frame]);
            }
            return result;
        }

        public static double[] FilterMgcExcitation(double[] excitation, double[,] mgc,
            int framePeriod, double alpha, int padeOrder) {
            double[] alphas = new double[mgc.GetLength(0)];
            for (int i = 0; i < alphas.Length; i++) {
                alphas[i] = alpha;
            }
            return FilterMgcExcitation(excitation, mgc, framePeriod, alphas, padeOrder);
        }

        public static double[] FilterMgcExcitation(double[] excitation, double[,] mgc,
            int framePeriod, double[] alpha, int padeOrder) {
            if (alpha.Length != mgc.GetLength(0)) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "MLSA alpha 帧数与 MGC 不一致");
            }
            RequireFinite(alpha, "MLSA alpha");
            double initial = alpha.Length == 0 ? 0.55 : alpha[0];
            if (framePeriod == 0 || mgc.GetLength(1) < 2
                || excitation.Length != mgc.GetLength(0) * framePeriod) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "激励/MGC 维度不一致");
            }
            RequireFinite(excitation, "excitation");
            RequireFiniteFlat(mgc, "MGC");
            MlsaFilter filter = new MlsaFilter(mgc.GetLength(1), initial, padeOrder);
            double[] result = new double[excitation.Length];
            double[] frameMgc = new double[mgc.GetLength(1)];
            for (int frame = 0; frame < mgc.GetLength(0); frame++) {
                filter.SetAlpha(alpha[frame]);
                for (int i = 0; i < frameMgc.Length; i++) {
                    frameMgc[i] = mgc[frame, i];
                }
                filter.StartFrame(frameMgc, framePeriod);
                int begin = frame * framePeriod;
                for (int sample = 0; sample < framePeriod; sample++) {
                    result[begin + sample] = filter.ApplySample(excitation[begin + sample]);
                }
                filter.EndFrame();
            }
            return result;
        }

        static void RequireFiniteFlat(double[,] values, string what) {
            foreach (double value in values) {
                if (double.IsNaN(value) || double.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        what + " 包含 NaN/Inf");
                }
            }
        }

        /// <summary>
        /// 线性插值重采样（48kHz→44.1kHz），避免引入额外 native 依赖。
        /// </summary>
        public static float[] ResampleLinear(
            float[] samples, int sourceRate, int targetRate) {
            if (sourceRate == targetRate) {
                return (float[])samples.Clone();
            }
            if (samples.Length == 0) {
                return Array.Empty<float>();
            }
            int targetLength = Math.Max(1,
                (int)Math.Round((double)samples.Length * targetRate / sourceRate));
            float[] result = new float[targetLength];
            for (int i = 0; i < targetLength; i++) {
                double position = (double)i * samples.Length / targetLength;
                int left = Math.Clamp((int)Math.Floor(position), 0, samples.Length - 1);
                int right = Math.Min(left + 1, samples.Length - 1);
                double fraction = Math.Clamp(position - left, 0.0, 1.0);
                result[i] = (float)(samples[left] * (1.0 - fraction)
                    + samples[right] * fraction);
            }
            return result;
        }
    }
}
