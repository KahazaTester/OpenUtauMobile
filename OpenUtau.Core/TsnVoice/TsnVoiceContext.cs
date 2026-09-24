using System;
using System.Collections.Generic;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 标签时值（状态/帧位置），对应原生 LabelTiming。
    /// </summary>
    public class TsnVoiceLabelTiming {
        public int StateIndex;
        public int StateCount = 5;
        public int FrameIndex;
        public int PhoneFrames = 1;
    }

    /// <summary>
    /// 上下文问题集，托管移植自原生 context_compiler.cpp。
    /// 支持 reserved/binary/numeric/special 四类问题。
    /// </summary>
    public class TsnVoiceContextQuestions {
        enum Kind {
            Reserved,
            Binary,
            Numeric,
            Special,
        }

        class Question {
            public Kind Kind = Kind.Binary;
            public string Name = string.Empty;
            public List<string> Patterns = new List<string>();
            public char Category = 'P';
            public int FieldIndex;
            public double Minimum;
            public double Maximum;
            public double ClipMinimum;
            public double ClipMaximum;
            public List<string> SpecialValues = new List<string>();
        }

        class SpecialSpec {
            public string Base = string.Empty;
            public char Category = 'P';
            public int FieldIndex;
            public List<string> Values = new List<string>();
            public List<string> Thresholds = new List<string>();
        }

        const char IntegerMarker = '\x01';
        const string IntegerSentinel = "314159265358979323846";

        readonly List<Question> questions = new List<Question>();

        static List<SpecialSpec> SpecialSpecs() {
            List<string> pitches = new List<string>();
            string[] pitchNames = new string[] {
                "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B",
            };
            for (int octave = 0; octave < 10; octave++) {
                foreach (string pitch in pitchNames) {
                    pitches.Add(pitch + octave.ToString());
                }
            }
            List<string> deltas = new List<string>();
            List<string> deltaThresholds = new List<string>();
            for (int value = 19; value > 0; value--) {
                deltas.Add("m" + value.ToString());
            }
            deltas.Add("p0");
            for (int value = 1; value < 20; value++) {
                deltas.Add("p" + value.ToString());
            }
            for (int value = -19; value < 20; value++) {
                deltaThresholds.Add(value.ToString());
            }
            List<string> dynamics = new List<string> {
                "p4", "p3", "p2", "p1", "mp", "n", "mf", "f1", "f2", "f3", "f4",
            };
            List<SpecialSpec> result = new List<SpecialSpec>();
            result.Add(new SpecialSpec {
                Base = "L-Note_Abs_Scale", Category = 'D', FieldIndex = 0,
                Values = new List<string>(pitches), Thresholds = new List<string>(pitches),
            });
            result.Add(new SpecialSpec {
                Base = "C-Note_Abs_Scale", Category = 'E', FieldIndex = 0,
                Values = new List<string>(pitches), Thresholds = new List<string>(pitches),
            });
            result.Add(new SpecialSpec {
                Base = "C-Note_Dynamic", Category = 'E', FieldIndex = 27,
                Values = new List<string>(dynamics), Thresholds = new List<string>(dynamics),
            });
            result.Add(new SpecialSpec {
                Base = "C-Note_Prev_Delta_Abs_Scale", Category = 'E', FieldIndex = 56,
                Values = new List<string>(deltas), Thresholds = new List<string>(deltaThresholds),
            });
            result.Add(new SpecialSpec {
                Base = "C-Note_Next_Delta_Abs_Scale", Category = 'E', FieldIndex = 57,
                Values = new List<string>(deltas), Thresholds = new List<string>(deltaThresholds),
            });
            result.Add(new SpecialSpec {
                Base = "R-Note_Abs_Scale", Category = 'F', FieldIndex = 0,
                Values = new List<string>(pitches), Thresholds = new List<string>(pitches),
            });
            return result;
        }

        static void ParseAttributes(string text, bool required,
            out double minimum, out double maximum,
            out double clipMinimum, out double clipMaximum) {
            Dictionary<string, double> values =
                new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string token in text.Split(
                new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
                int equal = token.IndexOf('=');
                if (equal < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "上下文问题属性格式错误");
                }
                string key = token.Substring(0, equal);
                if (key != "MIN" && key != "MAX" && key != "CLIP_MIN" && key != "CLIP_MAX") {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "未知上下文问题属性 '" + key + "'");
                }
                if (!double.TryParse(token.Substring(equal + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number)
                    || double.IsNaN(number) || double.IsInfinity(number)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "上下文问题属性不是数字");
                }
                if (values.ContainsKey(key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "重复上下文问题属性 '" + key + "'");
                }
                values[key] = number;
            }
            if (values.Count == 0 && !required) {
                minimum = 0;
                maximum = 1;
                clipMinimum = 0;
                clipMaximum = 1;
                return;
            }
            if (!values.TryGetValue("MIN", out minimum)
                || !values.TryGetValue("MAX", out maximum)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "上下文问题缺少 MIN/MAX");
            }
            if (!values.TryGetValue("CLIP_MIN", out clipMinimum)) {
                clipMinimum = minimum;
            }
            if (!values.TryGetValue("CLIP_MAX", out clipMaximum)) {
                clipMaximum = maximum;
            }
            if (!(minimum < maximum) || clipMinimum > clipMaximum) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "上下文问题数值区间无效");
            }
        }

        static string DecodePattern(string raw, out int integers) {
            System.Text.StringBuilder result = new System.Text.StringBuilder(raw.Length);
            integers = 0;
            for (int index = 0; index < raw.Length; index++) {
                if (raw[index] != '%') {
                    result.Append(raw[index]);
                    continue;
                }
                index++;
                if (index >= raw.Length) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "上下文模式存在悬空 %");
                }
                if (raw[index] == '%') {
                    result.Append('%');
                } else if (raw[index] == 'd') {
                    result.Append(IntegerMarker);
                    integers++;
                } else {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "不支持的上下文模式指令");
                }
            }
            return result.ToString();
        }

        static KeyValuePair<char, int> LocateIntegerField(string pattern) {
            string concrete = pattern.Replace(
                IntegerMarker.ToString(), IntegerSentinel);
            List<KeyValuePair<char, int>> matches = new List<KeyValuePair<char, int>>();
            foreach (char category in new char[] {
                'P', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
            }) {
                for (int index = 0; index < 118; index++) {
                    TsnVoiceLabel probe = new TsnVoiceLabel();
                    try {
                        probe.Set(category, index, IntegerSentinel);
                    } catch (TsnVoiceException) {
                        continue;
                    }
                    if (TsnVoiceLabel.PatternMatch(concrete, probe.Render())) {
                        matches.Add(new KeyValuePair<char, int>(category, index));
                    }
                }
            }
            if (matches.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "数值上下文模式无法定位唯一标签字段");
            }
            return matches[0];
        }

        static double Normalize(double value, double minimum, double maximum,
            double clipMinimum, double clipMaximum) {
            double clipped = Math.Clamp(value, clipMinimum, clipMaximum);
            if (clipped <= minimum) {
                return 0;
            }
            if (clipped >= maximum) {
                return 1;
            }
            return (clipped - minimum) / (maximum - minimum);
        }

        static double CompileReserved(string name, double minimum, double maximum,
            double clipMinimum, double clipMaximum, TsnVoiceLabelTiming timing) {
            if (timing.StateCount == 0 || timing.StateIndex >= timing.StateCount
                || timing.PhoneFrames == 0 || timing.FrameIndex >= timing.PhoneFrames) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "上下文编译的标签时值无效");
            }
            double duration = timing.PhoneFrames;
            double value;
            if (name == "Pos_to_Half_C-Frame_in_Phone(Fw)") {
                value = Math.Min(Math.Min(timing.FrameIndex + 1, 200.0), duration / 2.0);
            } else if (name == "Pos_to_Half_C-Frame_in_Phone(Bw)") {
                value = Math.Min(Math.Min(timing.PhoneFrames - timing.FrameIndex, 200.0),
                    duration / 2.0);
            } else if (name == "Pos_Percent_C-Frame_in_Phone(Fw)") {
                value = timing.PhoneFrames == 1
                    ? 50.0
                    : 100.0 * timing.FrameIndex / (timing.PhoneFrames - 1);
            } else if (name == "Pos_Percent_C-Frame_in_Phone(Bw)") {
                value = timing.PhoneFrames == 1
                    ? 50.0
                    : 100.0 * (timing.PhoneFrames - 1 - timing.FrameIndex)
                        / (timing.PhoneFrames - 1);
            } else if (name == "Phone_Duration" || name == "Phoneme_Duration") {
                value = duration;
            } else if (name == "Pos_C-State_in_Phone(Fw)") {
                value = timing.StateIndex + 2;
            } else if (name == "Pos_C-State_in_Phone(Bw)") {
                value = timing.StateCount - timing.StateIndex + 1;
            } else {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "不支持的保留上下文问题 '" + name + "'");
            }
            return Normalize(value, minimum, maximum, clipMinimum, clipMaximum);
        }

        static bool IsFrameQuestion(string name) {
            return name == "Pos_to_Half_C-Frame_in_Phone(Fw)"
                || name == "Pos_to_Half_C-Frame_in_Phone(Bw)"
                || name == "Pos_Percent_C-Frame_in_Phone(Fw)"
                || name == "Pos_Percent_C-Frame_in_Phone(Bw)"
                || name == "Phone_Duration" || name == "Phoneme_Duration";
        }

        public static TsnVoiceContextQuestions Parse(byte[] source, int expectedDimensions) {
            string text = System.Text.Encoding.UTF8.GetString(source);
            TsnVoiceContextQuestions result = new TsnVoiceContextQuestions();
            List<SpecialSpec> specials = SpecialSpecs();
            Dictionary<string, int> specialPositions =
                new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string rawLine in text.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) {
                    continue;
                }
                Question question = new Question();
                int open = line.IndexOf('{');
                if (open < 0) {
                    int attribute = line.IndexOfAny(new char[] { ' ', '\t' });
                    if (attribute < 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "保留上下文问题缺少数值区间");
                    }
                    question.Kind = Kind.Reserved;
                    question.Name = line.Substring(0, attribute).Trim();
                    ParseAttributes(line.Substring(attribute), true,
                        out question.Minimum, out question.Maximum,
                        out question.ClipMinimum, out question.ClipMaximum);
                } else {
                    int close = line.IndexOf('}', open + 1);
                    if (close < 0 || line.IndexOf('{', open + 1) >= 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "上下文问题格式错误");
                    }
                    question.Name = line.Substring(0, open).Trim();
                    string body = line.Substring(open + 1, close - open - 1);
                    List<string> rawPatterns = new List<string>();
                    foreach (string item in body.Split(',')) {
                        rawPatterns.Add(item.Trim());
                    }
                    if (rawPatterns.Count == 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "上下文问题模式为空");
                    }
                    int integerCount = 0;
                    foreach (string rawPattern in rawPatterns) {
                        if (rawPattern.Length == 0) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "上下文问题存在空模式");
                        }
                        question.Patterns.Add(DecodePattern(rawPattern, out int count));
                        integerCount += count;
                    }
                    string attributesText = line.Substring(close + 1).Trim();
                    SpecialSpec special = null;
                    foreach (SpecialSpec candidate in specials) {
                        if (question.Name.StartsWith(candidate.Base + "<=",
                            StringComparison.Ordinal)) {
                            special = candidate;
                            break;
                        }
                    }
                    if (special != null) {
                        if (!specialPositions.TryGetValue(special.Base, out int position)) {
                            position = 0;
                        }
                        if (attributesText.Length > 0 || integerCount != 0
                            || position >= special.Thresholds.Count) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "无效的特殊上下文阈值组");
                        }
                        string suffix = question.Name.Substring(special.Base.Length + 2);
                        if (!suffix.Equals(special.Thresholds[position],
                            StringComparison.Ordinal)) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "特殊上下文阈值乱序");
                        }
                        specialPositions[special.Base] = position + 1;
                        if (position != 0) {
                            continue;
                        }
                        question.Kind = Kind.Special;
                        question.Category = special.Category;
                        question.FieldIndex = special.FieldIndex;
                        question.SpecialValues = special.Values;
                    } else if (attributesText.Length == 0) {
                        if (integerCount != 0) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "数值上下文模式缺少区间");
                        }
                        question.Kind = Kind.Binary;
                    } else {
                        if (question.Patterns.Count != 1 || integerCount != 1) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "数值上下文问题需要单个 %d 模式");
                        }
                        ParseAttributes(attributesText, true,
                            out question.Minimum, out question.Maximum,
                            out question.ClipMinimum, out question.ClipMaximum);
                        KeyValuePair<char, int> field =
                            LocateIntegerField(question.Patterns[0]);
                        question.Kind = Kind.Numeric;
                        question.Category = field.Key;
                        question.FieldIndex = field.Value;
                    }
                }
                if (question.Name.Length == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "上下文问题名为空");
                }
                result.questions.Add(question);
            }
            foreach (SpecialSpec spec in specials) {
                if (!specialPositions.TryGetValue(spec.Base, out int count)
                    || count != spec.Thresholds.Count) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "特殊上下文阈值组不完整 '" + spec.Base + "'");
                }
            }
            if (result.questions.Count != expectedDimensions) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "上下文问题编译出 " + result.questions.Count + " 维，期望 "
                    + expectedDimensions + " 维");
            }
            return result;
        }

        public int Dimensions => questions.Count;

        public float[] Compile(TsnVoiceLabel label, TsnVoiceLabelTiming timing) {
            string rendered = label.Render();
            float[] result = new float[questions.Count];
            for (int i = 0; i < questions.Count; i++) {
                Question question = questions[i];
                double value = 0;
                switch (question.Kind) {
                    case Kind.Reserved:
                        value = CompileReserved(question.Name,
                            question.Minimum, question.Maximum,
                            question.ClipMinimum, question.ClipMaximum, timing);
                        break;
                    case Kind.Binary: {
                            bool matched = false;
                            foreach (string pattern in question.Patterns) {
                                if (TsnVoiceLabel.PatternMatch(pattern, rendered)) {
                                    matched = true;
                                    break;
                                }
                            }
                            value = matched ? 1.0 : 0.0;
                            break;
                        }
                    case Kind.Numeric: {
                            string field = label.Get(question.Category, question.FieldIndex);
                            if (field != "x" && field != "xx") {
                                if (!double.TryParse(field,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out double number)) {
                                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                        "标签数值字段不是数字");
                                }
                                value = Normalize(number,
                                    question.Minimum, question.Maximum,
                                    question.ClipMinimum, question.ClipMaximum);
                            }
                            break;
                        }
                    case Kind.Special: {
                            string field = label.Get(question.Category, question.FieldIndex);
                            int found = question.SpecialValues.IndexOf(field);
                            if (found >= 0) {
                                value = (double)found / question.SpecialValues.Count;
                            }
                            break;
                        }
                }
                result[i] = (float)value;
            }
            return result;
        }

        /// <summary>
        /// 分区编译：帧问题单独成组，其余归入状态-音素组。
        /// </summary>
        public void CompilePartitioned(TsnVoiceLabel label, TsnVoiceLabelTiming timing,
            out float[] frame, out float[] stateAndPhoneme) {
            float[] compiled = Compile(label, timing);
            List<float> frameList = new List<float>();
            List<float> restList = new List<float>();
            for (int i = 0; i < questions.Count; i++) {
                if (questions[i].Kind == Kind.Reserved
                    && IsFrameQuestion(questions[i].Name)) {
                    frameList.Add(compiled[i]);
                } else {
                    restList.Add(compiled[i]);
                }
            }
            frame = frameList.ToArray();
            stateAndPhoneme = restList.ToArray();
        }
    }

    /// <summary>
    /// 二值问题集（unique-context），每行 {模式,...}。
    /// </summary>
    public class TsnVoiceBinaryQuestions {
        readonly List<List<string>> patterns = new List<List<string>>();

        public static TsnVoiceBinaryQuestions Parse(byte[] source, int expectedDimensions) {
            string text = System.Text.Encoding.UTF8.GetString(source);
            TsnVoiceBinaryQuestions result = new TsnVoiceBinaryQuestions();
            foreach (string rawLine in text.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) {
                    continue;
                }
                int open = line.IndexOf('{');
                int close = line.IndexOf('}', open < 0 ? 0 : open + 1);
                if (open < 0 || close < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "unique-context 问题格式错误");
                }
                List<string> group = new List<string>();
                foreach (string item in line.Substring(open + 1, close - open - 1).Split(',')) {
                    group.Add(item.Trim());
                }
                if (group.Count == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "unique-context 问题为空");
                }
                result.patterns.Add(group);
            }
            if (result.patterns.Count != expectedDimensions) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "unique-context 问题维度不匹配");
            }
            return result;
        }

        public float[] Compile(TsnVoiceLabel label) {
            string rendered = label.Render();
            float[] result = new float[patterns.Count];
            for (int i = 0; i < patterns.Count; i++) {
                bool matched = false;
                foreach (string pattern in patterns[i]) {
                    if (TsnVoiceLabel.PatternMatch(pattern, rendered)) {
                        matched = true;
                        break;
                    }
                }
                result[i] = matched ? 1.0f : 0.0f;
            }
            return result;
        }
    }
}
