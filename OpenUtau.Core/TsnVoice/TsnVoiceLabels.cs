using System;
using System.Collections.Generic;
using System.Text;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// SINGER2 上下文标签，托管移植自原生 labels.cpp。
    /// 共 118 个字段，分属 P/A/B/C/D/E/F/G/H/I/J 十一类。
    /// </summary>
    public class TsnVoiceLabel {
        public const int FieldCount = 118;

        static readonly string Template =
            "*@*^*-*+*=*_*%*^*_*~*-*!*[*$*]*"
            + "/A:*-*-*@*~*"
            + "/B:*_*_*@*|*"
            + "/C:*+*+*@*&*"
            + "/D:*!*#*$*%*|*&*;*-*"
            + "/E:*]*^*=*~*!*@*#*+*]*$*|*[*&*]*=*^*~*#*_*;*$*&*%*[*|*]*-*^*+*~*=*@*$*!*%*#*|*|*-*&*&*+*[*;*]*;*~*~*^*^*@*[*#*=*!*~*+*!*^*"
            + "/F:*#*#*-*$*$*+*%*;*"
            + "/G:*_*"
            + "/H:*_*"
            + "/I:*_*"
            + "/J:*~*@*";

        struct Category {
            public char Name;
            public int Size;
        }

        static readonly Category[] Categories = new Category[] {
            new Category { Name = 'P', Size = 16 },
            new Category { Name = 'A', Size = 5 },
            new Category { Name = 'B', Size = 5 },
            new Category { Name = 'C', Size = 5 },
            new Category { Name = 'D', Size = 9 },
            new Category { Name = 'E', Size = 60 },
            new Category { Name = 'F', Size = 9 },
            new Category { Name = 'G', Size = 2 },
            new Category { Name = 'H', Size = 2 },
            new Category { Name = 'I', Size = 2 },
            new Category { Name = 'J', Size = 3 },
        };

        static readonly string[] PitchNames = new string[] {
            "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B",
        };

        readonly string[] values = new string[FieldCount];

        public TsnVoiceLabel() {
            for (int i = 0; i < values.Length; i++) {
                values[i] = "xx";
            }
        }

        static int CategoryOffset(char category, int index) {
            int offset = 0;
            foreach (Category entry in Categories) {
                if (entry.Name == category) {
                    if (index < 0 || index >= entry.Size) {
                        throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                            "SINGER2 字段越界");
                    }
                    return offset + index;
                }
                offset += entry.Size;
            }
            throw new TsnVoiceException(TsnVoiceStatus.InternalError, "未知 SINGER2 分类");
        }

        public string Get(char category, int index) {
            return values[CategoryOffset(category, index)];
        }

        public void Set(char category, int index, string value) {
            if (value.Length == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError, "SINGER2 字段不能为空");
            }
            values[CategoryOffset(category, index)] = value;
        }

        public string Render() {
            StringBuilder result = new StringBuilder(Template.Length + FieldCount * 3);
            int field = 0;
            foreach (char c in Template) {
                if (c == '*') {
                    if (field >= values.Length) {
                        throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                            "SINGER2 模板无效");
                    }
                    result.Append(values[field++]);
                } else {
                    result.Append(c);
                }
            }
            if (field != values.Length) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError, "SINGER2 字段数无效");
            }
            return result.ToString();
        }

        /// <summary>
        /// HTS 通配匹配（* 与 ?），对应原生 hts_pattern_match。
        /// </summary>
        public static bool PatternMatch(string pattern, string value) {
            int p = 0;
            int v = 0;
            int star = -1;
            int restart = 0;
            while (v < value.Length) {
                if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v])) {
                    p++;
                    v++;
                } else if (p < pattern.Length && pattern[p] == '*') {
                    star = p++;
                    restart = v;
                } else if (star >= 0) {
                    p = star + 1;
                    v = ++restart;
                } else {
                    return false;
                }
            }
            while (p < pattern.Length && pattern[p] == '*') {
                p++;
            }
            return p == pattern.Length;
        }

        static string Number(double value) {
            if (value == Math.Truncate(value)) {
                return ((long)value).ToString();
            }
            return value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture);
        }

        static long Rounded(double value) {
            return (long)Math.Round(value, MidpointRounding.ToEven);
        }

        public static string PitchName(int midiPitch) {
            int pitch = Math.Clamp(midiPitch, 0, 127);
            return PitchNames[pitch % 12] + (pitch / 12).ToString();
        }

        public static string PitchDelta(int semitones) {
            int value = Math.Clamp(semitones, -19, 19);
            return value < 0 ? "m" + (-value).ToString() : "p" + value.ToString();
        }

        /// <summary>
        /// 构建孤立音符的 SINGER2 标签序列，对应原生 build_isolated_singer2_labels。
        /// </summary>
        public static void BuildIsolatedLabels(
            List<string> phonemes, List<string> vowels, string language,
            string languageFlag, int midiPitch, double durationSeconds,
            List<string> phonemeLanguageFlags,
            out List<string> outPhonemes, out List<TsnVoiceLabel> outLabels) {
            if (phonemes.Count == 0 || midiPitch < 0 || midiPitch > 127
                || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds)
                || durationSeconds <= 0
                || (phonemeLanguageFlags.Count > 0
                    && phonemeLanguageFlags.Count != phonemes.Count)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "孤立音符标签输入无效");
            }
            string languageName;
            string flag = languageFlag;
            if (language == "zh_CN" || language == "zh_TW") {
                languageName = "CHI";
                if (flag.Length == 0) {
                    flag = "1";
                }
            } else if (language == "ja_JP") {
                languageName = "JPN";
                if (flag.Length == 0) {
                    flag = "0";
                }
            } else if (language == "ko_KR") {
                languageName = "KOR";
                if (flag.Length == 0) {
                    flag = "0";
                }
            } else if (language == "en_US" || language == "en_AU") {
                languageName = "ENG";
                if (flag.Length == 0) {
                    flag = "00";
                }
            } else {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                    "孤立标签器不支持该语言");
            }
            HashSet<string> vowelSet = new HashSet<string>(vowels, StringComparer.Ordinal);
            double bpm = 120.0;
            double durationTicks = durationSeconds * bpm * 960.0 / 60.0;
            string[] e = new string[60];
            for (int i = 0; i < e.Length; i++) {
                e[i] = "xx";
            }
            e[0] = PitchNames[midiPitch % 12] + (midiPitch / 12).ToString();
            e[1] = (midiPitch % 12).ToString();
            e[2] = "0";
            e[3] = "4/4";
            e[4] = Number(bpm);
            e[5] = "1";
            e[6] = Rounded(durationSeconds * 100.0).ToString();
            e[7] = Rounded(durationTicks / 40.0).ToString();
            e[9] = "1";
            e[10] = "2";
            e[11] = "0";
            e[12] = Rounded(2400.0 / bpm).ToString();
            e[13] = "0";
            e[14] = "96";
            e[15] = "0";
            e[16] = "100";
            e[17] = "1";
            e[18] = "1";
            e[19] = "0";
            e[20] = Rounded(durationSeconds * 10.0).ToString();
            e[21] = "0";
            e[22] = Rounded(durationTicks / 40.0).ToString();
            e[23] = "0";
            e[24] = "100";
            e[25] = "0";
            e[26] = "0";
            e[27] = "n";
            e[58] = "0";
            e[59] = "0";
            List<string> context = new List<string>(phonemes);
            context.Add("pau");
            outPhonemes = new List<string>(phonemes);
            outLabels = new List<TsnVoiceLabel>();
            for (int phoneIndex = 0; phoneIndex < phonemes.Count; phoneIndex++) {
                TsnVoiceLabel label = new TsnVoiceLabel();
                label.Set('P', 0, vowelSet.Contains(phonemes[phoneIndex]) ? "v" : "c");
                for (int relative = -2; relative <= 2; relative++) {
                    int index = phoneIndex + relative;
                    if (index >= 0 && index < context.Count) {
                        label.Set('P', relative + 3, context[index]);
                        label.Set('P', relative + 8, "00");
                    }
                }
                label.Set('P', 11, (phoneIndex + 1).ToString());
                label.Set('P', 12, (phonemes.Count - phoneIndex).ToString());
                if (!vowelSet.Contains(phonemes[phoneIndex])) {
                    for (int previous = phoneIndex; previous-- > 0;) {
                        if (vowelSet.Contains(phonemes[previous])) {
                            label.Set('P', 13, (phoneIndex - previous).ToString());
                            break;
                        }
                    }
                    for (int next = phoneIndex + 1; next < phonemes.Count; next++) {
                        if (vowelSet.Contains(phonemes[next])) {
                            label.Set('P', 14, (next - phoneIndex).ToString());
                            break;
                        }
                    }
                }
                label.Set('B', 0, phonemes.Count.ToString());
                label.Set('B', 1, "1");
                label.Set('B', 2, "1");
                label.Set('B', 3, languageName);
                label.Set('B', 4, phonemeLanguageFlags.Count == 0
                    ? flag
                    : phonemeLanguageFlags[phoneIndex]);
                for (int index = 0; index < e.Length; index++) {
                    label.Set('E', index, e[index]);
                }
                label.Set('H', 0, "1");
                label.Set('H', 1, "1");
                label.Set('J', 0, "2");
                label.Set('J', 1, "2");
                label.Set('J', 2, "1");
                outLabels.Add(label);
            }
        }
    }
}
