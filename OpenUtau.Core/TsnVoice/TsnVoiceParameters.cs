using System;
using System.Collections.Generic;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 引擎参数在本仓库的对照实现。
    /// 取值来源：参考实现的引擎桥接层（ALP/HUS 自动控制）、
    /// 会话层（采样步长、延续音符、默认歌词）与原生 API（ABI 版本、合成请求结构）。
    /// </summary>
    public static class TsnVoiceParameters {
        /// <summary>原生 ABI 版本，创建引擎前必须校验一致。</summary>
        public const uint AbiVersion = 8;

        /// <summary>原生合成输出采样率（Hz），单声道。</summary>
        public const int NativeSampleRate = 48000;

        /// <summary>混音采样率（Hz），渲染结果需重采样到此频率。</summary>
        public const int MixSampleRate = 44100;

        /// <summary>音高与控制采样步长（秒），对应参考实现 5ms 采样。</summary>
        public const double PitchSampleStepSeconds = 0.005;

        /// <summary>延续音符歌词，沿用参考实现约定：不跑 G2P，直接延长前一发音。</summary>
        public const string ContinuationLyric = "-";

        /// <summary>乐句合并允许的最大间隙（秒），相邻音符必须真正相连才同属一个乐句。</summary>
        public const double MaxPhraseGapSeconds = 0.001;

        /// <summary>支持的 voice format，仅接受解析器可处理的版本。</summary>
        public static readonly int[] SupportedVoiceFormats = new int[] { 0, 1, 2, 5 };

        // ALP：声道 alpha 偏置，公开范围 -100..100，传入原生层前除以 100。
        public const string AlphaId = "alpha";
        public const string AlphaDisplay = "ALP";
        public const float AlphaMin = -100f;
        public const float AlphaMax = 100f;
        public const float AlphaDefault = 0f;

        // HUS：气声控制，公开范围 -1..1，原生层直接使用。
        public const string HuskinessId = "huskiness";
        public const string HuskinessDisplay = "HUS";
        public const float HuskinessMin = -1f;
        public const float HuskinessMax = 1f;
        public const float HuskinessDefault = 0f;

        /// <summary>引擎支持的语言标识。</summary>
        public static readonly string[] Languages = new string[] {
            "ja_JP", "zh_CN", "zh_TW", "en_US", "ko_KR",
        };

        public const string DefaultLanguage = "ja_JP";

        /// <summary>各语言无歌词时的默认音节，与参考实现保持一致。</summary>
        public static string DefaultLyric(string language) {
            switch (language) {
                case "ja_JP":
                    return "a";
                case "zh_CN":
                case "zh_TW":
                    return "la";
                case "en_US":
                    return "love";
                case "ko_KR":
                    return "가";
                default:
                    return "la";
            }
        }

        /// <summary>
        /// 休止与换气歌词对应的静音音素；非此类歌词返回空。
        /// </summary>
        public static string RestPhoneme(string lyric) {
            if (lyric == "R" || lyric == "r") {
                return "sil";
            }
            if (string.Equals(lyric, "br", StringComparison.OrdinalIgnoreCase)) {
                return "pau";
            }
            return null;
        }

        /// <summary>是否为歌手支持的语言。</summary>
        public static bool IsSupportedLanguage(string language) {
            if (string.IsNullOrEmpty(language)) {
                return false;
            }
            for (int i = 0; i < Languages.Length; i++) {
                if (string.Equals(Languages[i], language, StringComparison.Ordinal)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 按歌词文字推断音符语言（跨语言演唱用）：
        /// 谚文→韩语，假名→日语，汉字→中文或日文（两者共享汉字）。
        /// 参考实现中语言是逐音符属性（缺省为语音主语言），从不按文字猜测；
        /// 此处仅在语音支持时采纳文字信号，否则一律回退主语言，
        /// 绝不抛错中断整句：转写失败走逐音符静音加记错路径。
        /// </summary>
        public static string DetectNoteLanguage(
            HashSet<string> supportedLanguages, string primaryLanguage, string lyric) {
            string signal = null;
            for (int i = 0; i < lyric.Length; i++) {
                int code = lyric[i];
                if (char.IsHighSurrogate(lyric[i]) && i + 1 < lyric.Length
                    && char.IsLowSurrogate(lyric[i + 1])) {
                    code = char.ConvertToUtf32(lyric, i);
                    i++;
                }
                if ((code >= 0xAC00 && code <= 0xD7A3)
                    || (code >= 0x1100 && code <= 0x11FF)
                    || (code >= 0x3130 && code <= 0x318F)) {
                    signal = "ko_KR";
                    break;
                }
                if ((code >= 0x3040 && code <= 0x309F)
                    || (code >= 0x30A0 && code <= 0x30FF)
                    || (code >= 0x31F0 && code <= 0x31FF)) {
                    signal = "ja_JP";
                    break;
                }
                if ((code >= 0x3400 && code <= 0x4DBF)
                    || (code >= 0x4E00 && code <= 0x9FFF)
                    || (code >= 0x20000 && code <= 0x2EBEF)) {
                    signal = "zh";
                    break;
                }
            }
            if (signal == null) {
                return primaryLanguage;
            }
            if (signal == "zh") {
                // 汉字为中日共享：主语言读汉字时优先主语言（如日语 voice 的漢字歌词），
                // 否则选支持的中文变体，都不支持则回退主语言由 G2P 逐音符报错。
                if (primaryLanguage == "ja_JP" || primaryLanguage == "zh_CN"
                    || primaryLanguage == "zh_TW") {
                    return primaryLanguage;
                }
                if (supportedLanguages.Contains("zh_CN")) {
                    return "zh_CN";
                }
                if (supportedLanguages.Contains("zh_TW")) {
                    return "zh_TW";
                }
                return primaryLanguage;
            }
            if (supportedLanguages.Contains(signal)) {
                return signal;
            }
            return primaryLanguage;
        }

        /// <summary>
        /// 是否为合法音素符号（SINGER2 音素均为 ASCII）。
        /// 含非 ASCII 的多为转写失败回落的原文歌词，应交回推理内 G2P。
        /// </summary>
        public static bool IsPhonemeSymbol(string symbol) {
            if (string.IsNullOrEmpty(symbol)) {
                return false;
            }
            foreach (char c in symbol) {
                if (c > 127) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>将文件名中的语言标记解析为引擎语言，失败时回退默认语言。</summary>
        public static string InferLanguage(string voiceId) {
            if (!string.IsNullOrEmpty(voiceId)) {
                List<string> found = new List<string>();
                for (int i = 0; i < Languages.Length; i++) {
                    if (voiceId.IndexOf("_" + Languages[i] + "_", StringComparison.OrdinalIgnoreCase) >= 0) {
                        found.Add(Languages[i]);
                    }
                }
                if (found.Count > 0) {
                    return found[0];
                }
            }
            return DefaultLanguage;
        }

        /// <summary>
        /// 渲染器支持的表情缩写：力度、音高偏移与 ALP/HUS 自定义曲线。
        /// 原生声学条件仅接受 alpha/huskiness，其余标准曲线无对应输入，
        /// 为避免误导不予支持。
        /// </summary>
        public static readonly HashSet<string> SupportedExpressions = new HashSet<string>(
            StringComparer.Ordinal) {
            Format.Ustx.DYN,
            Format.Ustx.PITD,
            "alp",
            "hus",
        };

        /// <summary>ALP/HUS 自定义曲线描述，供渲染器 GetSuggestedExpressions 返回。</summary>
        public static UExpressionDescriptor[] BuildSuggestedExpressions() {
            // 注意：此构造不设 type，须显式标为 Curve，否则乐句构建按
            // Numerical 过滤掉，曲线永远到不了渲染器。
            return new UExpressionDescriptor[] {
                new UExpressionDescriptor(
                    "ALP (alpha)",
                    "alp",
                    AlphaMin,
                    AlphaMax,
                    AlphaDefault) {
                    type = Ustx.UExpressionType.Curve,
                },
                new UExpressionDescriptor(
                    "HUS (huskiness)",
                    "hus",
                    HuskinessMin,
                    HuskinessMax,
                    HuskinessDefault) {
                    type = Ustx.UExpressionType.Curve,
                },
            };
        }

        /// <summary>用户绘制的 ALP 值换算为原生 alpha（-1..1）。</summary>
        public static double ToNativeAlpha(double alp) {
            return Math.Clamp(alp / 100.0, -1.0, 1.0);
        }

        /// <summary>用户绘制的 HUS 值钳制到原生范围（-1..1）。</summary>
        public static double ToNativeHuskiness(double huskiness) {
            return Math.Clamp(huskiness, -1.0, 1.0);
        }

        /// <summary>自动音高总开关（默认开启，异常时保持开启）。</summary>
        public static bool IsAutoPitchEnabled() {
            try {
                return Util.Preferences.Default.TsnVoiceAutoPitch;
            } catch {
                return true;
            }
        }
    }
}
