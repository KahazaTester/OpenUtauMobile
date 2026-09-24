using System;
using System.Collections.Generic;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 状态时值分配结果。
    /// </summary>
    public class TsnVoiceStateTiming {
        public int PhonemeIndex;
        public int StateIndex;
        public int StartFrame;
        public int EndFrame;
    }

    /// <summary>
    /// 音素时长规划。
    /// </summary>
    public class TsnVoiceDurationPlan {
        public int StateCount;
        public List<TsnVoiceStateTiming> Timings = new List<TsnVoiceStateTiming>();
        public int FrameCount;
    }

    /// <summary>
    /// HMM 时长模型，托管移植自原生 hts_duration.cpp。
    /// 嵌入式 HTS 语音的头部与数据区同样经过 legacy 块解码。
    /// </summary>
    public static class TsnVoiceDuration {
        class HtsQuestion {
            public List<string> Patterns = new List<string>();
        }

        class HtsNode {
            public string Question = string.Empty;
            public bool NoIsLeaf;
            public int NoIndex;
            public string NoLeaf = string.Empty;
            public bool YesIsLeaf;
            public int YesIndex;
            public string YesLeaf = string.Empty;
        }

        class DecisionTree {
            public Dictionary<string, HtsQuestion> Questions =
                new Dictionary<string, HtsQuestion>(StringComparer.Ordinal);
            public Dictionary<int, HtsNode> Nodes = new Dictionary<int, HtsNode>();

            public int SelectPdf(string label) {
                object current = 0;
                HashSet<int> visited = new HashSet<int>();
                while (current is int) {
                    int index = (int)current;
                    if (!visited.Add(index)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "HTS 时长树存在环");
                    }
                    if (!Nodes.TryGetValue(index, out HtsNode node)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "HTS 时长节点损坏");
                    }
                    if (!Questions.TryGetValue(node.Question, out HtsQuestion question)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "HTS 问题缺失");
                    }
                    bool matches = false;
                    foreach (string pattern in question.Patterns) {
                        if (TsnVoiceLabel.PatternMatch(pattern, label)) {
                            matches = true;
                            break;
                        }
                    }
                    current = matches
                        ? (node.YesIsLeaf ? (object)node.YesLeaf : node.YesIndex)
                        : (node.NoIsLeaf ? (object)node.NoLeaf : node.NoIndex);
                }
                string leaf = (string)current;
                int separator = leaf.LastIndexOf('_');
                if (separator < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "HTS 叶节点缺少 PDF 索引");
                }
                if (!ulong.TryParse(leaf.Substring(separator + 1), out ulong value)
                    || value == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "HTS 叶节点 PDF 索引无效");
                }
                return (int)value;
            }
        }

        class DurationModel {
            public int StateCount;
            public int PdfCount;
            public double[] Means = Array.Empty<double>();
            public double[] Variances = Array.Empty<double>();
            public DecisionTree Tree = new DecisionTree();

            public void Predict(string label, double[] means, double[] variances) {
                int pdf = Tree.SelectPdf(label);
                if (pdf <= 0 || pdf > PdfCount) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "时长树选中的 PDF 无效");
                }
                int offset = (pdf - 1) * StateCount;
                Array.Copy(Means, offset, means, 0, StateCount);
                Array.Copy(Variances, offset, variances, 0, StateCount);
            }
        }

        static uint LittleU32(byte[] bytes, int offset = 0) {
            if (bytes.Length - offset < 4) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "截断的小端整数");
            }
            return (uint)(bytes[offset] | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
        }

        static byte[] HtsSection(byte[] blob, string name, ref int stateCount) {
            if (blob.Length < 4) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "嵌入式 HTS 语音截断");
            }
            uint headerSize = LittleU32(blob);
            if (headerSize > blob.Length - 4) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 头大小无效");
            }
            byte[] headerData = new byte[headerSize];
            Array.Copy(blob, 4, headerData, 0, (int)headerSize);
            byte[] decodedHeader = TsnVoiceContainer.DecodeLegacyBlock(headerData);
            string header = System.Text.Encoding.UTF8.GetString(decodedHeader);
            Dictionary<string, List<KeyValuePair<int, int>>> positions =
                new Dictionary<string, List<KeyValuePair<int, int>>>(StringComparer.Ordinal);
            string section = string.Empty;
            foreach (string rawLine in header.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0) {
                    continue;
                }
                if (line.StartsWith("[") && line.EndsWith("]")) {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 头格式错误");
                }
                string key = line.Substring(0, colon);
                string value = line.Substring(colon + 1);
                if (section == "GLOBAL" && key == "NUM_STATES") {
                    if (!int.TryParse(value, out stateCount)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 状态数无效");
                    }
                } else if (section == "POSITION") {
                    List<KeyValuePair<int, int>> ranges = new List<KeyValuePair<int, int>>();
                    foreach (string item in value.Split(',')) {
                        string trimmed = item.Trim();
                        int dash = trimmed.IndexOf('-');
                        if (dash < 0 || !int.TryParse(trimmed.Substring(0, dash), out int first)
                            || !int.TryParse(trimmed.Substring(dash + 1), out int last)
                            || last < first) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "无效的 HTS 数据区间");
                        }
                        ranges.Add(new KeyValuePair<int, int>(first, last));
                    }
                    positions[key] = ranges;
                }
            }
            if (stateCount == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 语音没有状态");
            }
            if (!positions.TryGetValue(name, out List<KeyValuePair<int, int>> found)
                || found.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "HTS 语音缺少 '" + name + "' 区间");
            }
            int dataStart = 4 + (int)headerSize;
            int rangeStart = found[0].Key;
            int rangeEnd = found[0].Value;
            if (rangeEnd >= blob.Length - dataStart) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 区间超出数据范围");
            }
            byte[] sectionData = new byte[rangeEnd - rangeStart + 1];
            Array.Copy(blob, dataStart + rangeStart, sectionData, 0, sectionData.Length);
            return TsnVoiceContainer.DecodeLegacyBlock(sectionData);
        }

        static string Unquote(string value) {
            value = value.Trim();
            if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\"")) {
                return value.Substring(1, value.Length - 2);
            }
            return value;
        }

        static DecisionTree ParseTree(string source) {
            DecisionTree result = new DecisionTree();
            bool firstTree = false;
            bool inNodes = false;
            foreach (string rawLine in source.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0) {
                    continue;
                }
                if (line.StartsWith("QS ")) {
                    int brace = line.IndexOf('{');
                    int close = line.LastIndexOf('}');
                    if (brace < 0 || close <= brace) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "HTS 问题格式错误");
                    }
                    string name = Unquote(line.Substring(3, brace - 3).Trim());
                    HtsQuestion question = new HtsQuestion();
                    string body = line.Substring(brace + 1, close - brace - 1);
                    int position = 0;
                    while (true) {
                        int begin = body.IndexOf('"', position);
                        if (begin < 0) {
                            break;
                        }
                        int end = body.IndexOf('"', begin + 1);
                        if (end < 0) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "HTS 模式未终结");
                        }
                        question.Patterns.Add(body.Substring(begin + 1, end - begin - 1));
                        position = end + 1;
                    }
                    if (question.Patterns.Count == 0 || result.Questions.ContainsKey(name)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "无效或重复的 HTS 问题");
                    }
                    result.Questions[name] = question;
                    continue;
                }
                if (!firstTree && line.Contains('[') && line.EndsWith("]")) {
                    firstTree = true;
                    continue;
                }
                if (firstTree && line == "{") {
                    inNodes = true;
                    continue;
                }
                if (inNodes && line == "}") {
                    break;
                }
                if (!inNodes) {
                    continue;
                }
                string[] columns = line.Split(
                    new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 4 || !int.TryParse(columns[0], out int index)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 决策节点格式错误");
                }
                string questionName = Unquote(columns[1]);
                HtsNode node = new HtsNode();
                node.Question = questionName;
                ParseChild(columns[2], out bool noIsLeaf, out int noIndex, out string noLeaf);
                ParseChild(columns[3], out bool yesIsLeaf, out int yesIndex, out string yesLeaf);
                node.NoIsLeaf = noIsLeaf;
                node.NoIndex = noIndex;
                node.NoLeaf = noLeaf;
                node.YesIsLeaf = yesIsLeaf;
                node.YesIndex = yesIndex;
                node.YesLeaf = yesLeaf;
                if (result.Nodes.ContainsKey(index)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "重复的 HTS 决策节点");
                }
                result.Nodes[index] = node;
            }
            if (result.Nodes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "HTS 时长树没有节点");
            }
            return result;
        }

        static void ParseChild(
            string value, out bool isLeaf, out int index, out string leaf) {
            string unquoted = Unquote(value);
            if (int.TryParse(unquoted, out index)) {
                isLeaf = false;
                leaf = string.Empty;
                return;
            }
            isLeaf = true;
            index = 0;
            leaf = unquoted;
        }

        static DurationModel LoadDurationModel(byte[] blob) {
            int stateCount = 0;
            byte[] pdf = HtsSection(blob, "DURATION_PDF", ref stateCount);
            int treeStates = stateCount;
            byte[] tree = HtsSection(blob, "DURATION_TREE", ref treeStates);
            if (treeStates != stateCount || pdf.Length < 4) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的 HTS 时长数据");
            }
            uint pdfCount = LittleU32(pdf);
            ulong expected = 4UL + (ulong)pdfCount * (ulong)stateCount * 2UL * 4UL;
            if (pdfCount == 0 || (ulong)pdf.Length != expected) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "时长 PDF 大小不匹配");
            }
            DurationModel result = new DurationModel();
            result.StateCount = stateCount;
            result.PdfCount = (int)pdfCount;
            result.Means = new double[result.PdfCount * stateCount];
            result.Variances = new double[result.Means.Length];
            int position = 4;
            for (int item = 0; item < result.PdfCount; item++) {
                for (int state = 0; state < stateCount; state++, position += 4) {
                    result.Means[item * stateCount + state] =
                        BitConverter.ToSingle(pdf, position);
                }
                for (int state = 0; state < stateCount; state++, position += 4) {
                    result.Variances[item * stateCount + state] =
                        BitConverter.ToSingle(pdf, position);
                }
            }
            result.Tree = ParseTree(System.Text.Encoding.UTF8.GetString(tree));
            return result;
        }

        static double[] ParseWeights(
            TsnVoicePackage voice, string language, int count) {
            string key = language + "_HMM_DUR_CODE";
            if (!voice.Config.TryGetValue(key, out string text)) {
                key = "HMM_DUR_CODE";
                voice.Config.TryGetValue(key, out text);
            }
            if (text == null) {
                if (count == 1) {
                    return new double[] { 1.0 };
                }
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音缺少 " + key);
            }
            int rowEnd = text.IndexOf(';');
            string row = rowEnd < 0 ? text : text.Substring(0, rowEnd);
            List<double> result = new List<double>();
            foreach (string item in row.Split(',')) {
                if (!double.TryParse(item, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        key + " 存在非数字");
                }
                result.Add(value);
            }
            double sum = 0;
            foreach (double value in result) {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        key + " 不是合法的插值向量");
                }
                sum += value;
            }
            if (result.Count != count || Math.Abs(sum - 1.0) > 1e-9) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    key + " 不是合法的插值向量");
            }
            return result.ToArray();
        }

        /// <summary>
        /// 按目标帧数分配各状态时长（rho 插值 + 贪心取整）。
        /// </summary>
        public static int[] AllocateSpecifiedDuration(
            double[] means, double[] variances, int targetFrames, int minimum = 1) {
            if (means.Length == 0 || means.Length != variances.Length
                || targetFrames < minimum * means.Length) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的时长分配");
            }
            double sumMean = 0;
            double sumVariance = 0;
            for (int i = 0; i < means.Length; i++) {
                if (double.IsNaN(means[i]) || double.IsInfinity(means[i])
                    || double.IsNaN(variances[i]) || double.IsInfinity(variances[i])
                    || variances[i] <= 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "时长参数无效");
                }
                sumMean += means[i];
                sumVariance += variances[i];
            }
            double rho = (targetFrames - sumMean) / sumVariance;
            int[] frames = new int[means.Length];
            for (int i = 0; i < means.Length; i++) {
                double raw = Math.Max(means[i] + rho * variances[i] + 0.5, 0.0);
                frames[i] = Math.Clamp((int)Math.Truncate(raw), minimum, targetFrames);
            }
            int total = 0;
            foreach (int value in frames) {
                total += value;
            }
            while (total != targetFrames) {
                bool increase = total < targetFrames;
                int best = frames.Length;
                double bestCost = double.PositiveInfinity;
                for (int i = 0; i < frames.Length; i++) {
                    if ((!increase && frames[i] <= minimum)
                        || (increase && frames[i] >= targetFrames)) {
                        continue;
                    }
                    double candidate = frames[i] + (increase ? 1.0 : -1.0);
                    double cost = Math.Abs(rho - (candidate - means[i]) / variances[i]);
                    if (cost <= bestCost) {
                        best = i;
                        bestCost = cost;
                    }
                }
                if (best == frames.Length) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "时长目标无法满足");
                }
                if (increase) {
                    frames[best]++;
                    total++;
                } else {
                    frames[best]--;
                    total--;
                }
            }
            return frames;
        }

        /// <summary>
        /// 为渲染标签序列构建时长规划。
        /// </summary>
        public static TsnVoiceDurationPlan BuildDurationPlan(
            TsnVoicePackage voice, string language, List<string> renderedLabels,
            int targetFrames) {
            if (!voice.HmmVoiceSets.TryGetValue(language, out TsnVoiceHmmSet set)
                || set.Blobs.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音缺少该语言的 HMM 时长模型");
            }
            List<DurationModel> models = new List<DurationModel>();
            foreach (byte[] blob in set.Blobs) {
                models.Add(LoadDurationModel(blob));
            }
            int stateCount = models[0].StateCount;
            foreach (DurationModel model in models) {
                if (model.StateCount != stateCount) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "插值时长模型的状态数不一致");
                }
            }
            double[] weights = ParseWeights(voice, language, models.Count);
            double[] means = new double[renderedLabels.Count * stateCount];
            double[] variances = new double[means.Length];
            double[] blendedMean = new double[stateCount];
            double[] blendedVariance = new double[stateCount];
            for (int phone = 0; phone < renderedLabels.Count; phone++) {
                Array.Clear(blendedMean, 0, stateCount);
                Array.Clear(blendedVariance, 0, stateCount);
                foreach (DurationModel model in models) {
                    int modelIndex = models.IndexOf(model);
                    double[] predictedMeans = new double[stateCount];
                    double[] predictedVariances = new double[stateCount];
                    model.Predict(renderedLabels[phone], predictedMeans, predictedVariances);
                    for (int state = 0; state < stateCount; state++) {
                        blendedMean[state] += predictedMeans[state] * weights[modelIndex];
                        blendedVariance[state] += predictedVariances[state] * weights[modelIndex];
                    }
                }
                Array.Copy(blendedMean, 0, means, phone * stateCount, stateCount);
                Array.Copy(blendedVariance, 0, variances, phone * stateCount, stateCount);
            }
            int[] allocation = AllocateSpecifiedDuration(means, variances, targetFrames);
            TsnVoiceDurationPlan result = new TsnVoiceDurationPlan();
            result.StateCount = stateCount;
            result.FrameCount = targetFrames;
            int frame = 0;
            for (int phone = 0; phone < renderedLabels.Count; phone++) {
                for (int state = 0; state < stateCount; state++) {
                    int duration = allocation[phone * stateCount + state];
                    TsnVoiceStateTiming timing = new TsnVoiceStateTiming();
                    timing.PhonemeIndex = phone;
                    timing.StateIndex = state;
                    timing.StartFrame = frame;
                    timing.EndFrame = frame + duration;
                    result.Timings.Add(timing);
                    frame += duration;
                }
            }
            if (frame != targetFrames) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError, "时长分配不变式失败");
            }
            return result;
        }

        /// <summary>
        /// 用用户固定的音素时长重定时规划。
        /// </summary>
        public static TsnVoiceDurationPlan RetimeDurationPlan(
            TsnVoiceDurationPlan source, double[] phonemeDurations) {
            if (source.StateCount == 0
                || source.Timings.Count != phonemeDurations.Length * source.StateCount) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                    "固定音素时值与时长规划不匹配");
            }
            double totalDuration = 0;
            foreach (double value in phonemeDurations) {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "固定音素时值无效");
                }
                totalDuration += value;
            }
            int minimum = source.StateCount;
            if (source.FrameCount < phonemeDurations.Length * minimum) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError, "时长规划短于状态下限");
            }
            int[] phoneFrames = new int[phonemeDurations.Length];
            double[] remainder = new double[phonemeDurations.Length];
            for (int i = 0; i < phoneFrames.Length; i++) {
                phoneFrames[i] = minimum;
            }
            int discretionary = source.FrameCount - phonemeDurations.Length * minimum;
            int assigned = 0;
            for (int phone = 0; phone < phoneFrames.Length; phone++) {
                double exact = phonemeDurations[phone] / totalDuration * discretionary;
                int whole = (int)Math.Floor(exact);
                phoneFrames[phone] += whole;
                assigned += whole;
                remainder[phone] = exact - whole;
            }
            while (assigned++ < discretionary) {
                int best = ArgMax(remainder);
                phoneFrames[best]++;
                remainder[best] = -1;
            }
            TsnVoiceDurationPlan result = new TsnVoiceDurationPlan();
            result.StateCount = source.StateCount;
            result.FrameCount = source.FrameCount;
            int frame = 0;
            for (int phone = 0; phone < phoneFrames.Length; phone++) {
                int begin = phone * source.StateCount;
                int[] states = new int[source.StateCount];
                double[] fractions = new double[source.StateCount];
                for (int i = 0; i < states.Length; i++) {
                    states[i] = 1;
                }
                int original = 0;
                for (int state = 0; state < source.StateCount; state++) {
                    TsnVoiceStateTiming timing = source.Timings[begin + state];
                    original += timing.EndFrame - timing.StartFrame;
                }
                int stateAssigned = source.StateCount;
                for (int state = 0; state < source.StateCount; state++) {
                    TsnVoiceStateTiming timing = source.Timings[begin + state];
                    double ratio = (double)(timing.EndFrame - timing.StartFrame) / original;
                    double exact = ratio * (phoneFrames[phone] - source.StateCount);
                    int whole = (int)Math.Floor(exact);
                    states[state] += whole;
                    stateAssigned += whole;
                    fractions[state] = exact - whole;
                }
                while (stateAssigned++ < phoneFrames[phone]) {
                    int best = ArgMax(fractions);
                    states[best]++;
                    fractions[best] = -1;
                }
                for (int state = 0; state < source.StateCount; state++) {
                    TsnVoiceStateTiming timing = new TsnVoiceStateTiming();
                    timing.PhonemeIndex = phone;
                    timing.StateIndex = state;
                    timing.StartFrame = frame;
                    timing.EndFrame = frame + states[state];
                    result.Timings.Add(timing);
                    frame += states[state];
                }
            }
            return result;
        }

        static int ArgMax(double[] values) {
            int best = 0;
            for (int i = 1; i < values.Length; i++) {
                if (values[i] > values[best]) {
                    best = i;
                }
            }
            return best;
        }
    }
}
