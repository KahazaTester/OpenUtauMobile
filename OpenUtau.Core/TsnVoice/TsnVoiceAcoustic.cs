using System;
using System.Collections.Generic;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 单帧控制：音高、基准音高、ALP、HUS。
    /// </summary>
    public class TsnVoiceFrameControls {
        public double MidiPitch = 60;
        public double BaseMidiPitch = 60;
        public bool PitchIsAbsolute;
        public double Alpha;
        public double Huskiness;
    }

    /// <summary>
    /// 声学参数：LF0、MGC、BAP 与声码器隐变量。
    /// </summary>
    public class TsnVoiceAcousticParameters {
        public double[] Lf0 = Array.Empty<double>();
        public double[,] Mgc = new double[0, 0];
        public double[,] Bap = new double[0, 0];
        public float[,] Latent = new float[0, 0];
    }

    /// <summary>
    /// 声学后处理，托管移植自原生 acoustic_postprocess.cpp。
    /// stage1 七维输出解析 V/UV、LF0、颤音；stage2 按配置切分 MGC/BAP/LAT。
    /// </summary>
    public static class TsnVoiceAcoustic {
        const double VuvThreshold = 0.5;
        const int GateFadeFrames = 20;
        const double GateFloor = -3.0;

        static double ConfigNumber(
            Dictionary<string, string> config, string key, double fallback) {
            if (!config.TryGetValue(key, out string text) || text.Length == 0) {
                return fallback;
            }
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result)
                || double.IsNaN(result) || double.IsInfinity(result)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音数值配置无效：" + key);
            }
            return result;
        }

        static int ConfigSize(
            Dictionary<string, string> config, string key, int fallback) {
            double value = ConfigNumber(config, key, fallback);
            if (value < 0 || value != Math.Truncate(value)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音整数配置无效：" + key);
            }
            return (int)value;
        }

        static int[] FeatureDimensions(TsnVoicePackage voice) {
            if (!voice.Config.TryGetValue("ACOUSTIC_FEATURE_DIMENSIONS", out string dimensionsEntry)
                || !voice.Config.TryGetValue("ACOUSTIC_FEATURES", out string namesEntry)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音缺少声学特征配置");
            }
            List<int> dimensions = new List<int>();
            foreach (string item in dimensionsEntry.Split(',')) {
                if (!int.TryParse(item, out int dimension) || dimension <= 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "语音声学维度无效");
                }
                dimensions.Add(dimension);
            }
            string[] names = namesEntry.Split(',');
            if (names.Length != dimensions.Count) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "声学特征名与维度数量不一致");
            }
            Dictionary<string, int> byName =
                new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++) {
                byName[names[i]] = dimensions[i];
            }
            // 分流拼接与二进制一致（CreateMgcStructure/CreateBapStructure，
            // MGC0/MGC1、BAP0/BAP1 优先于整体 MGC/BAP）：
            // 声学 DNN 输出按拼接后的 MGC/BAP/LAT 排布，其余流（LF0/VIB 等）
            // 由各自管线消费，此处不参与。
            int mgc;
            if (byName.ContainsKey("MGC0") && byName.ContainsKey("MGC1")) {
                mgc = byName["MGC0"] + byName["MGC1"];
            } else if (byName.ContainsKey("MGC")) {
                mgc = byName["MGC"];
            } else {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    "推理需要 MGC（或 MGC0+MGC1）声学特征；该语音声明为 " + namesEntry);
            }
            int bap;
            if (byName.ContainsKey("BAP0") && byName.ContainsKey("BAP1")) {
                bap = byName["BAP0"] + byName["BAP1"];
            } else if (byName.ContainsKey("BAP")) {
                bap = byName["BAP"];
            } else {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    "推理需要 BAP（或 BAP0+BAP1）声学特征；该语音声明为 " + namesEntry);
            }
            if (!byName.ContainsKey("LAT")) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    "推理需要 MGC,BAP,LAT 声学特征；该语音声明为 " + namesEntry);
            }
            return new int[] { mgc, bap, byName["LAT"] };
        }

        static double[] MajorityFilter(double[] source, int filterSize) {
            if (filterSize == 0) {
                return (double[])source.Clone();
            }
            int right = (filterSize - 1) / 2;
            int left = filterSize - 1 - right;
            int[] prefix = new int[source.Length + 1];
            for (int i = 0; i < source.Length; i++) {
                prefix[i + 1] = prefix[i] + (source[i] >= VuvThreshold ? 1 : 0);
            }
            double[] result = new double[source.Length];
            for (int frame = 0; frame < source.Length; frame++) {
                int begin = frame > left ? frame - left : 0;
                int end = Math.Min(source.Length, frame + right + 1);
                int voiced = prefix[end] - prefix[begin];
                int window = end - begin;
                result[frame] = 2 * voiced == window
                    ? (source[frame] >= VuvThreshold ? 1.0 : 0.0)
                    : (2 * voiced > window ? 1.0 : 0.0);
            }
            return result;
        }

        static double[] GateEnvelope(double[] c0, string[] phonemes, double threshold) {
            double[] result = new double[c0.Length];
            for (int i = 0; i < result.Length; i++) {
                result[i] = 1.0;
            }
            for (int frame = 0; frame < c0.Length; frame++) {
                if (phonemes[frame] == "sil"
                    || (phonemes[frame] == "pau" && !(c0[frame] >= threshold))) {
                    result[frame] = 0;
                }
            }
            int remaining = 0;
            for (int i = 0; i < result.Length; i++) {
                if (result[i] == 1) {
                    remaining = GateFadeFrames;
                } else if (remaining != 0) {
                    result[i] = (double)(--remaining) / GateFadeFrames;
                }
            }
            remaining = 0;
            for (int i = result.Length; i-- > 0;) {
                if (result[i] == 1) {
                    remaining = GateFadeFrames;
                } else if (remaining != 0) {
                    double fade = (double)(--remaining) / GateFadeFrames;
                    result[i] = Math.Max(result[i], fade);
                }
            }
            return result;
        }

        public static TsnVoiceAcousticParameters Postprocess(
            TsnVoicePackage voice, float[,] stage1, float[,] stage2,
            string[] framePhonemes, TsnVoiceFrameControls[] controls) {
            int frames = stage1.GetLength(0);
            if (frames == 0 || stage1.GetLength(1) != 7
                || stage2.GetLength(0) != frames
                || framePhonemes.Length != frames || controls.Length != frames) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "声学输出帧维度不一致");
            }
            int[] dimensions = FeatureDimensions(voice);
            int mgcDimensions = dimensions[0];
            int bapTailDimensions = dimensions[1];
            int bapDimensions = bapTailDimensions + 1;
            int latentDimensions = dimensions[2];
            if (mgcDimensions < 1 || stage2.GetLength(1)
                != (mgcDimensions - 1) + bapTailDimensions + latentDimensions) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "CNN2 输出与声学特征维度不匹配：输出=" + stage2.GetLength(1)
                    + "，MGC=" + mgcDimensions + "，BAP 尾=" + bapTailDimensions
                    + "，LAT=" + latentDimensions);
            }
            double[] lf0Weight = new double[frames];
            double[] vibratoWeight = new double[frames];
            for (int frame = 0; frame < frames; frame++) {
                lf0Weight[frame] = stage1[frame, 1];
                vibratoWeight[frame] = stage1[frame, 4];
            }
            lf0Weight = MajorityFilter(lf0Weight,
                ConfigSize(voice.Config, "LF0_VUV_MAJORITY_FILTER_SIZE", 0));
            vibratoWeight = MajorityFilter(vibratoWeight,
                ConfigSize(voice.Config, "VIB_VUV_MAJORITY_FILTER_SIZE", 0));
            double sampleRate = ConfigNumber(voice.Config, "SAMPLING_FREQUENCY", 0);
            double framePeriod = ConfigNumber(voice.Config, "FRAME_PERIOD", 0);
            if (sampleRate <= 0 || framePeriod <= 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音采样率或帧长无效");
            }
            double frameSeconds = framePeriod / sampleRate;
            double[] lf0 = new double[frames];
            double[] utilization = new double[frames];
            double[] phase = new double[frames];
            bool[] vibratoValid = new bool[frames];
            for (int i = 0; i < frames; i++) {
                lf0[i] = TsnVoiceDsp.UnvoicedLf0;
            }
            for (int frame = 0; frame < frames; frame++) {
                if (lf0Weight[frame] >= VuvThreshold) {
                    lf0[frame] = stage1[frame, 0];
                }
                vibratoValid[frame] = lf0[frame] != TsnVoiceDsp.UnvoicedLf0
                    && vibratoWeight[frame] >= VuvThreshold;
                if (frame == 0 || !vibratoValid[frame]) {
                    continue;
                }
                double frequency = Math.Clamp(stage1[frame, 3], 2.0, 10.0);
                utilization[frame] = Math.Min(
                    utilization[frame - 1] + 2 * frameSeconds * frequency, 1.0);
                phase[frame] = phase[frame - 1] + frequency;
            }
            double remaining = 0;
            for (int frame = frames; frame-- > 1;) {
                if (!vibratoValid[frame]) {
                    remaining = 0;
                    continue;
                }
                if (remaining < 1) {
                    double frequency = Math.Clamp(stage1[frame, 3], 2.0, 10.0);
                    remaining += 2 * frameSeconds * frequency;
                    utilization[frame] *= remaining;
                }
            }
            double vibratoToLf0 = Math.Log(2.0) / 1200.0;
            for (int frame = 0; frame < frames; frame++) {
                if (!vibratoValid[frame]) {
                    continue;
                }
                double amplitude = Math.Clamp(stage1[frame, 2], 0.0, 400.0);
                lf0[frame] += vibratoToLf0 * amplitude * utilization[frame]
                    * Math.Sin(2 * Math.PI * frameSeconds * phase[frame]);
            }
            double[,] mgc = new double[frames, mgcDimensions];
            double[,] bap = new double[frames, bapDimensions];
            float[,] latent = new float[frames, latentDimensions];
            double[] c0 = new double[frames];
            for (int frame = 0; frame < frames; frame++) {
                c0[frame] = stage1[frame, 6];
                bap[frame, 0] = stage1[frame, 5] - controls[frame].Huskiness;
                int read = 0;
                for (int dimension = 1; dimension < mgcDimensions; dimension++) {
                    mgc[frame, dimension] = stage2[frame, read++];
                }
                for (int dimension = 1; dimension < bapDimensions; dimension++) {
                    bap[frame, dimension] = stage2[frame, read++];
                }
                for (int dimension = 0; dimension < latentDimensions; dimension++) {
                    latent[frame, dimension] = stage2[frame, read++];
                }
            }
            double compressorShift = ConfigNumber(voice.Config, "COMPRESSOR_SHIFT", 0);
            double compressorRate = ConfigNumber(voice.Config, "COMPRESSOR_RATE", 1);
            double compressorThreshold = ConfigNumber(voice.Config, "COMPRESSOR_THRESHOLD", 0);
            for (int i = 0; i < c0.Length; i++) {
                c0[i] += compressorShift;
                if (compressorRate != 1 && c0[i] > compressorThreshold) {
                    c0[i] = compressorThreshold
                        + (c0[i] - compressorThreshold) * compressorRate;
                }
            }
            string gateType = "none";
            if (voice.Config.TryGetValue("NOISE_GATE_TYPE", out string configuredGate)) {
                gateType = configuredGate;
            }
            if (gateType == "c0_threshold") {
                double[] envelope = GateEnvelope(c0, framePhonemes,
                    ConfigNumber(voice.Config, "NOISE_GATE_C0_THRESHOLD", 3));
                for (int frame = 0; frame < frames; frame++) {
                    c0[frame] = (1 - envelope[frame]) * GateFloor
                        + envelope[frame] * c0[frame];
                }
            } else if (gateType != "none" && gateType.Length > 0) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    "语音使用了不支持的噪声门");
            }
            double c0Shift = ConfigNumber(voice.Config, "C0_SHIFT", 0);
            for (int frame = 0; frame < frames; frame++) {
                mgc[frame, 0] = c0[frame] + c0Shift;
            }
            TsnVoiceDsp.RequireFinite(lf0, "LF0");
            foreach (double value in mgc) {
                if (double.IsNaN(value) || double.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声学后处理产生 NaN/Inf");
                }
            }
            foreach (double value in bap) {
                if (double.IsNaN(value) || double.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声学后处理产生 NaN/Inf");
                }
            }
            foreach (float value in latent) {
                if (float.IsNaN(value) || float.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声学后处理产生 NaN/Inf");
                }
            }
            TsnVoiceAcousticParameters result = new TsnVoiceAcousticParameters();
            result.Lf0 = lf0;
            result.Mgc = mgc;
            result.Bap = bap;
            result.Latent = latent;
            return result;
        }
    }
}
