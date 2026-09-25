using System;
using System.Collections.Generic;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 合成首尾边距：Layout 槽位与音频裁剪共用同一数值，保证对齐严格一致。
    /// 边距取自语音帧率换算；语音不可用时回退零边距（旧行为），两边始终一致。
    /// </summary>
    public static class TsnVoiceLayoutMargins {
        public class Entry {
            public double HeadMs;
            public double TailMs;
            public int HeadFrames = TsnVoiceParameters.KeepHeadFrames;
            public int TailFrames = TsnVoiceParameters.KeepTailFrames;
        }

        public static Entry Get(string voicePath) {
            Entry entry = new Entry();
            try {
                if (string.IsNullOrEmpty(voicePath)) {
                    return entry;
                }
                TsnVoiceInference.TsnVoiceVoiceHandle handle =
                    TsnVoiceInference.GetVoiceHandle(voicePath);
                Dictionary<string, string> config = handle.Package.Config;
                double sampleRate = ParseNumber(config, "SAMPLING_FREQUENCY");
                double framePeriod = ParseNumber(config, "FRAME_PERIOD");
                if (sampleRate > 0 && framePeriod > 0) {
                    double frameSeconds = framePeriod / sampleRate;
                    entry.HeadMs = entry.HeadFrames * frameSeconds * 1000.0;
                    entry.TailMs = entry.TailFrames * frameSeconds * 1000.0;
                }
            } catch {
            }
            return entry;
        }

        static double ParseNumber(Dictionary<string, string> config, string key) {
            if (!config.TryGetValue(key, out string text) || text.Length == 0) {
                return 0;
            }
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value) || value < 0) {
                return 0;
            }
            return value;
        }
    }
}
