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

        /// <summary>是否为引擎支持的语言。</summary>
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
            return new UExpressionDescriptor[] {
                new UExpressionDescriptor(
                    "ALP (alpha)",
                    "alp",
                    AlphaMin,
                    AlphaMax,
                    AlphaDefault),
                new UExpressionDescriptor(
                    "HUS (huskiness)",
                    "hus",
                    HuskinessMin,
                    HuskinessMax,
                    HuskinessDefault),
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
    }
}
