using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenUtau.Core;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 合成输入音符，对应原生 SynthesisNote。
    /// </summary>
    public class TsnVoiceInputNote {
        public string Id = string.Empty;
        public double StartSeconds;
        public double EndSeconds;
        public int MidiPitch = 60;
        public double BaseMidiPitch = 60;
        public string Lyric = string.Empty;
        public string Language = "ja_JP";
        public List<TsnVoiceInputPhoneme> Phonemes = new List<TsnVoiceInputPhoneme>();
        public int LeadingPhonemeCount;
        public double BodyOffsetSeconds;
        public bool IsContinuation;
    }

    public class TsnVoiceInputPhoneme {
        public string Symbol = string.Empty;
        public double DurationSeconds;
        public double StretchWeight = 1.0;
    }

    public class TsnVoicePitchPoint {
        public double TimeSeconds;
        public double MidiPitch;
        public bool IsAbsolute;
    }

    public class TsnVoiceControlPoint {
        public double TimeSeconds;
        public double Alpha;
        public double Huskiness;
    }

    public class TsnVoiceOutputPhoneme {
        public string NoteId = string.Empty;
        public string Symbol = string.Empty;
        public double StartSeconds;
        public double DurationSeconds;
        public double StretchWeight;
        public double BodyOffsetSeconds;
        public bool IsLeading;
    }

    public class TsnVoiceOutputPitch {
        public double TimeSeconds;
        public double MidiPitch;
    }

    public class TsnVoiceSynthesisOutput {
        public int SampleRate = 48000;
        public double StartTime;
        public float[] Samples = Array.Empty<float>();
        public List<TsnVoiceOutputPhoneme> Phonemes = new List<TsnVoiceOutputPhoneme>();
        public List<TsnVoiceOutputPitch> Pitch = new List<TsnVoiceOutputPitch>();
    }

    /// <summary>
    /// 前端调度：语言 G2P 与元音表查询（带缓存）。
    /// </summary>
    static class TsnVoiceFrontend {
        static readonly object dictLock = new object();
        static TsnVoiceJapaneseDictionary japanese;
        static readonly Dictionary<string, TsnVoiceMandarinDictionary> mandarin =
            new Dictionary<string, TsnVoiceMandarinDictionary>(StringComparer.Ordinal);
        static TsnVoiceEnglishDictionary english;
        static TsnVoiceKoreanDictionary korean;

        public static TsnVoicePronunciation Pronounce(string language, string lyric) {
            lock (dictLock) {
                if (language == "ja_JP") {
                    if (japanese == null) {
                        japanese = TsnVoiceJapaneseDictionary.Load();
                    }
                    return japanese.Lookup(lyric);
                }
                if (language == "zh_CN" || language == "zh_TW") {
                    if (!mandarin.TryGetValue(language, out TsnVoiceMandarinDictionary dict)) {
                        dict = TsnVoiceMandarinDictionary.Load(language);
                        mandarin[language] = dict;
                    }
                    return dict.Lookup(lyric);
                }
                if (language == "en_US") {
                    if (english == null) {
                        english = TsnVoiceEnglishDictionary.Load();
                    }
                    return english.Lookup(lyric);
                }
                if (language == "ko_KR") {
                    if (korean == null) {
                        korean = TsnVoiceKoreanDictionary.Load();
                    }
                    return korean.Lookup(lyric);
                }
            }
            throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                "不支持的语言前端 '" + language + "'");
        }

        public static string DefaultLyric(string language) {
            return TsnVoiceParameters.DefaultLyric(language);
        }
    }

    /// <summary>
    /// 托管推理管线，对应原生 inference_pipeline.cpp + ort_runtime.cpp。
    /// 会话经应用统一后端选择器创建（默认 CPU，与原生一致），
    /// 按语音（路径+大小+修改时间）缓存解析包与会话，最多保留 4 个。
    /// 模型字节在会话创建后释放，仅保留配置、问题集与 HMM 数据，
    /// 避免移动端重复解析大语音包与常驻双份模型内存。
    /// </summary>
    public static partial class TsnVoiceInference {
        public class SessionEntry {
            public InferenceSession Session;
            public readonly object RunLock = new object();
        }

        /// <summary>
        /// 单个语音的缓存句柄：解析包、会话与渲染锁。
        /// 解析后的问题集随句柄缓存（纯解析结果复用，数值与逐次解析一致）。
        /// </summary>
        public class TsnVoiceVoiceHandle {
            public TsnVoicePackage Package;
            public Dictionary<string, SessionEntry> Sessions =
                new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
            public Dictionary<string, object> QuestionCache =
                new Dictionary<string, object>(StringComparer.Ordinal);
            public readonly object SyncRoot = new object();
        }

        class CachedVoice {
            public string Key = string.Empty;
            public TsnVoiceVoiceHandle Handle = new TsnVoiceVoiceHandle();
        }

        static readonly object cacheLock = new object();
        static readonly LinkedList<CachedVoice> cache = new LinkedList<CachedVoice>();

        /// <summary>
        /// 清空会话缓存并回收内存，供内存不足时恢复。
        /// </summary>
        public static void DropCache() {
            lock (cacheLock) {
                foreach (CachedVoice cached in cache) {
                    foreach (KeyValuePair<string, SessionEntry> session in
                        cached.Handle.Sessions) {
                        try {
                            session.Value.Session.Dispose();
                        } catch {
                        }
                    }
                }
                cache.Clear();
            }
        }

        static string CacheKey(string path) {
            FileInfo info = new FileInfo(path);
            return path + "\n" + info.Length + "\n" + info.LastWriteTimeUtc.Ticks;
        }

        /// <summary>
        /// 获取语音缓存句柄：解析包只解析一次，会话只创建一次。
        /// </summary>
        public static TsnVoiceVoiceHandle GetVoiceHandle(string voicePath) {
            string key = CacheKey(voicePath);
            lock (cacheLock) {
                LinkedListNode<CachedVoice> hit = null;
                for (LinkedListNode<CachedVoice> node = cache.First;
                    node != null;
                    node = node.Next) {
                    if (node.Value.Key == key) {
                        hit = node;
                        break;
                    }
                }
                if (hit != null) {
                    cache.Remove(hit);
                    cache.AddFirst(hit);
                    return hit.Value.Handle;
                }
                TsnVoiceVoiceHandle handle = new TsnVoiceVoiceHandle();
                handle.Package = TsnVoicePackage.Load(voicePath);
                LoadSessions(handle);
                ReleaseModelBytes(handle.Package);
                CachedVoice entry = new CachedVoice();
                entry.Key = key;
                entry.Handle = handle;
                cache.AddFirst(entry);
                while (cache.Count > 4) {
                    LinkedListNode<CachedVoice> last = cache.Last;
                    foreach (KeyValuePair<string, SessionEntry> session in
                        last.Value.Handle.Sessions) {
                        try {
                            session.Value.Session.Dispose();
                        } catch {
                        }
                    }
                    cache.RemoveLast();
                }
                return handle;
            }
        }

        static Dictionary<string, SessionEntry> GetSessions(TsnVoicePackage voice) {
            return GetVoiceHandle(voice.SourcePath).Sessions;
        }

        /// <summary>
        /// 会话创建后释放模型字节：会话持有原生侧拷贝，
        /// 配置、问题集与 HMM 数据保留供后续渲染使用。
        /// </summary>
        static void ReleaseModelBytes(TsnVoicePackage voice) {
            foreach (TsnVoiceModelSet modelSet in EnumerateModelSets(voice)) {
                foreach (TsnVoiceModelBlob blob in modelSet.Models) {
                    blob.Data = Array.Empty<byte>();
                }
            }
        }

        static List<TsnVoiceModelSet> EnumerateModelSets(TsnVoicePackage voice) {
            List<TsnVoiceModelSet> result = new List<TsnVoiceModelSet>();
            foreach (TsnVoiceModelSet modelSet in voice.UniqueContextModels.Values) {
                result.Add(modelSet);
            }
            result.Add(voice.CommonContextModel);
            result.Add(voice.LinguisticModel);
            result.Add(voice.AcousticStage1Model);
            result.Add(voice.AcousticStage2Model);
            result.Add(voice.AuxiliaryModel);
            result.Add(voice.VocoderModel);
            return result;
        }

        static int ConfiguredThreads(TsnVoiceModelSet modelSet) {
            foreach (string key in new string[] {
                "NUM_THREADS", "NUMBER_OF_THREADS", "THREADS",
            }) {
                if (!modelSet.Config.TryGetValue(key, out string text)) {
                    continue;
                }
                if (!int.TryParse(text,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        modelSet.Role + " 线程数无效");
                }
                if (value < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        modelSet.Role + " 线程数无效");
                }
                return value;
            }
            return 0;
        }

        static void AddSession(Dictionary<string, SessionEntry> sessions,
            TsnVoiceModelSet modelSet) {
            if (modelSet.Models.Count == 0) {
                return;
            }
            if (modelSet.Models.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    modelSet.Role + " 包含多个内嵌模型");
            }
            if (sessions.ContainsKey(modelSet.Role)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "重复的内嵌模型角色 " + modelSet.Role);
            }
            InferenceSession session;
            int intraThreads = ConfiguredThreads(modelSet);
            try {
                // 与原生 ort_runtime 逐项一致，仅作用于 TSNVOICE 会话：
                // 全图优化 + 语音指定的线程数（仅当指定），无执行提供方，
                // 纯 CPU 推理；其它引擎的后端选择不受影响。
                // 仍是托管 1.29 同一引擎，不新增原生库；
                // 1.18 ConvInteger 补丁在 1.29 无需移植。
                // 任何失败回退应用统一选择器，保证总能出声。
                try {
                    SessionOptions options = new SessionOptions();
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    if (intraThreads > 0) {
                        options.IntraOpNumThreads = intraThreads;
                    }
                    session = new InferenceSession(modelSet.Models[0].Data, options);
                } catch (Exception ex) {
                    Serilog.Log.Warning(ex,
                        "TsnVoice {Role} 自定义会话失败，回退默认配置", modelSet.Role);
                    session = Onnx.getInferenceSession(
                        modelSet.Models[0].Data, OnnxRunnerChoice.Default);
                }
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    modelSet.Role + " 无法被 ONNX Runtime 加载：" + e.Message, e);
            }
            SessionEntry entry = new SessionEntry();
            entry.Session = session;
            sessions[modelSet.Role] = entry;
        }

        static void LoadSessions(TsnVoiceVoiceHandle handle) {
            TsnVoicePackage voice = handle.Package;
            Dictionary<string, SessionEntry> sessions = handle.Sessions;
            if (!voice.LegacyContextLayout) {
                foreach (KeyValuePair<string, TsnVoiceModelSet> pair in voice.UniqueContextModels) {
                    AddSession(sessions, pair.Value);
                }
                AddSession(sessions, voice.CommonContextModel);
            }
            AddSession(sessions, voice.LinguisticModel);
            AddSession(sessions, voice.AcousticStage1Model);
            AddSession(sessions, voice.AcousticStage2Model);
            AddSession(sessions, voice.VocoderModel);
        }

        static SessionEntry GetModel(Dictionary<string, SessionEntry> sessions, string role) {
            if (!sessions.TryGetValue(role, out SessionEntry entry)) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "语音缺少模型角色 " + role);
            }
            return entry;
        }

        // 配置读取辅助，对应 configuration_number/first/size/code。
        static double ConfigNumber(Dictionary<string, string> config, string key,
            double fallback, bool required = false) {
            if (!config.TryGetValue(key, out string text) || text.Length == 0) {
                if (required) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "语音缺少 " + key);
                }
                return fallback;
            }
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音配置无效：" + key);
            }
            return value;
        }

        static double ConfigFirstNumber(Dictionary<string, string> config, string key,
            double fallback, bool required = false) {
            if (!config.TryGetValue(key, out string text) || text.Length == 0) {
                if (required) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "语音缺少 " + key);
                }
                return fallback;
            }
            int end = text.IndexOfAny(new char[] { ',', ';' });
            string first = end < 0 ? text : text.Substring(0, end);
            if (!double.TryParse(first, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音配置无效：" + key);
            }
            return value;
        }

        static int ConfigSize(Dictionary<string, string> config, string key,
            int fallback = 0, bool required = false) {
            double value = ConfigNumber(config, key, fallback, required);
            if (value < 0 || value != Math.Truncate(value)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音整数配置无效：" + key);
            }
            return (int)value;
        }

        static float[] ConfigCode(Dictionary<string, string> config,
            string dimensionsKey, string codeKey) {
            int dimensions = ConfigSize(config, dimensionsKey, 0, true);
            if (dimensions == 0) {
                return Array.Empty<float>();
            }
            if (!config.TryGetValue(codeKey, out string text)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音缺少 " + codeKey);
            }
            int semicolon = text.IndexOf(';');
            string first = semicolon < 0 ? text : text.Substring(0, semicolon);
            List<float> result = new List<float>();
            foreach (string item in first.Split(',')) {
                if (!float.TryParse(item, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value)
                    || float.IsNaN(value) || float.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        codeKey + " 存在非数字");
                }
                result.Add(value);
            }
            if (result.Count != dimensions) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    codeKey + " 维度与配置不一致");
            }
            return result.ToArray();
        }

        /// <summary>
        /// 风格码宽松读取：缺失维度键或维度为零时返回空（该语音不用此条件）；
        /// 维度非零但缺失编码时以零填充并记警告（参考实现直接报错中断，
        /// 此处为可渲染降级），组装后的 CNN 输入维度校验仍会拦截真正不兼容的语音。
        /// </summary>
        static float[] ConfigCodeOrEmpty(Dictionary<string, string> config,
            string dimensionsKey, string codeKey) {
            if (!config.TryGetValue(dimensionsKey, out string dimensionsText)
                || dimensionsText.Length == 0) {
                return Array.Empty<float>();
            }
            int dimensions = ConfigSize(config, dimensionsKey, 0, true);
            if (dimensions == 0) {
                return Array.Empty<float>();
            }
            if (!config.TryGetValue(codeKey, out string codeText)
                || codeText.Length == 0) {
                Serilog.Log.Warning(
                    "语音声明 {Dimensions} 维 {Code} 但未提供编码，以零填充继续渲染",
                    dimensions, codeKey);
                return new float[dimensions];
            }
            return ConfigCode(config, dimensionsKey, codeKey);
        }

        // 张量分段运行，对应 run_fixed_segments / run_overlapped_segments。
        static float[,] RunFixedSegments(SessionEntry model, float[,] input,
            Action<double> fraction = null) {
            int rows = input.GetLength(0);
            int columns = input.GetLength(1);
            if (rows == 0 || columns == 0 || model.Session.InputMetadata.Count != 1
                || model.Session.OutputMetadata.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型不是单输入定长矩阵模型");
            }
            string inputName = model.Session.InputMetadata.Keys.First();
            int[] inputShape = model.Session.InputMetadata[inputName].Dimensions;
            string outputName = model.Session.OutputMetadata.Keys.First();
            int[] outputShape = model.Session.OutputMetadata[outputName].Dimensions;
            if (inputShape.Length != 3 || outputShape.Length != 3
                || inputShape[0] != 1 || outputShape[0] != 1
                || inputShape[1] != outputShape[1] || inputShape[2] != columns) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型定长张量形状不兼容");
            }
            int segment = inputShape[1];
            int outputDimensions = outputShape[2];
            float[,] result = new float[rows, outputDimensions];
            float[] feedValues = new float[segment * columns];
            for (int start = 0; start < rows; start += segment) {
                int actual = Math.Min(segment, rows - start);
                for (int row = 0; row < segment; row++) {
                    int source = start + Math.Min(row, actual - 1);
                    for (int column = 0; column < columns; column++) {
                        feedValues[row * columns + column] = input[source, column];
                    }
                }
                float[] output = RunModel(model, inputName,
                    feedValues, new int[] { 1, segment, columns });
                if (output.Length != segment * outputDimensions) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "模型返回了不符合预期的输出形状");
                }
                for (int row = 0; row < actual; row++) {
                    for (int column = 0; column < outputDimensions; column++) {
                        result[start + row, column] = output[row * outputDimensions + column];
                    }
                }
                fraction?.Invoke((start + actual) / (double)rows);
            }
            return result;
        }

        static float[,] RunOverlappedSegments(SessionEntry model, float[,] input,
            int overlapFrames, Action<double> fraction = null) {
            int rows = input.GetLength(0);
            int columns = input.GetLength(1);
            if (rows == 0 || columns == 0 || model.Session.InputMetadata.Count != 1
                || model.Session.OutputMetadata.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型不是单输入定长矩阵模型");
            }
            string inputName = model.Session.InputMetadata.Keys.First();
            int[] inputShape = model.Session.InputMetadata[inputName].Dimensions;
            string outputName = model.Session.OutputMetadata.Keys.First();
            int[] outputShape = model.Session.OutputMetadata[outputName].Dimensions;
            if (inputShape.Length != 3 || outputShape.Length != 3
                || inputShape[0] != 1 || outputShape[0] != 1
                || inputShape[1] != outputShape[1] || inputShape[2] != columns) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型定长张量形状不兼容");
            }
            int window = inputShape[1];
            if (overlapFrames == 0 || overlapFrames > (window - 1) / 2) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError, "模型交叠长度无效");
            }
            int stride = window - 2 * overlapFrames;
            int outputDimensions = outputShape[2];
            int segmentCount = (rows + stride - 1) / stride;
            // 流式混合：数值与原生实现完全一致（相同窗口、相同权重），
            // 但任意时刻最多保留 3 个窗口，长乐句不再一次性持有全部分段。
            Dictionary<int, float[,]> segmentCache = new Dictionary<int, float[,]>();
            float[] feedValues = new float[window * columns];
            float[,] GetSegment(int segmentIndex) {
                if (!segmentCache.TryGetValue(segmentIndex, out float[,] cached)) {
                    int coreStart = segmentIndex * stride;
                    for (int row = 0; row < window; row++) {
                        int source = Math.Clamp(coreStart + row - overlapFrames, 0, rows - 1);
                        for (int column = 0; column < columns; column++) {
                            feedValues[row * columns + column] = input[source, column];
                        }
                    }
                    float[] output = RunModel(model, inputName,
                        feedValues, new int[] { 1, window, columns });
                    if (output.Length != window * outputDimensions) {
                        throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                            "模型返回了不符合预期的输出形状");
                    }
                    cached = new float[window, outputDimensions];
                    for (int row = 0; row < window; row++) {
                        for (int column = 0; column < outputDimensions; column++) {
                            cached[row, column] = output[row * outputDimensions + column];
                        }
                    }
                    segmentCache[segmentIndex] = cached;
                    fraction?.Invoke((segmentIndex + 1.0) / segmentCount);
                }
                return cached;
            }
            float[,] result = new float[rows, outputDimensions];
            int lastSegment = -1;
            for (int frame = 0; frame < rows; frame++) {
                int segmentIndex = frame / stride;
                if (segmentIndex != lastSegment) {
                    // 滑窗前移：丢弃已不可能再被引用的旧窗口。
                    List<int> stale = new List<int>();
                    foreach (int key in segmentCache.Keys) {
                        if (key < segmentIndex - 1) {
                            stale.Add(key);
                        }
                    }
                    foreach (int key in stale) {
                        segmentCache.Remove(key);
                    }
                    lastSegment = segmentIndex;
                }
                int coreStart = segmentIndex * stride;
                int withinCore = frame - coreStart;
                int currentRow = withinCore + overlapFrames;
                float[,] current = GetSegment(segmentIndex);
                for (int column = 0; column < outputDimensions; column++) {
                    result[frame, column] = current[currentRow, column];
                }
                if (segmentIndex > 0 && withinCore < overlapFrames) {
                    double weight = 0.5 + (double)withinCore / (2 * overlapFrames);
                    BlendRow(result, frame, GetSegment(segmentIndex - 1),
                        currentRow + stride, weight, outputDimensions);
                } else {
                    int coreLength = Math.Min(stride, rows - coreStart);
                    int remaining = coreLength - withinCore;
                    if (segmentIndex + 1 < segmentCount && remaining < overlapFrames) {
                        double weight = 0.5 + (double)remaining / (2 * overlapFrames);
                        BlendRow(result, frame, GetSegment(segmentIndex + 1),
                            currentRow - stride, weight, outputDimensions);
                    }
                }
            }
            return result;
        }

        static void BlendRow(float[,] destination, int frame, float[,] other,
            int otherRow, double currentWeight, int columns) {
            for (int column = 0; column < columns; column++) {
                destination[frame, column] = (float)(other[otherRow, column]
                    + (destination[frame, column] - other[otherRow, column]) * currentWeight);
            }
        }

        static float[] RunModel(SessionEntry model, string inputName,
            float[] values, int[] shape) {
            foreach (float value in values) {
                if (float.IsNaN(value) || float.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "模型输入存在 NaN/Inf");
                }
            }
            DenseTensor<float> tensor = new DenseTensor<float>(values, shape);
            List<NamedOnnxValue> inputs = new List<NamedOnnxValue>();
            inputs.Add(NamedOnnxValue.CreateFromTensor(inputName, tensor));
            try {
                lock (model.RunLock) {
                    using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                        model.Session.Run(inputs)) {
                        DisposableNamedOnnxValue first = results.First();
                        float[] output = first.AsTensor<float>().ToArray();
                        foreach (float value in output) {
                            if (float.IsNaN(value) || float.IsInfinity(value)) {
                                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                                    "模型返回 NaN/Inf");
                            }
                        }
                        return output;
                    }
                }
            } catch (TsnVoiceException) {
                throw;
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型推理失败：" + e.Message, e);
            }
        }
    }
}
