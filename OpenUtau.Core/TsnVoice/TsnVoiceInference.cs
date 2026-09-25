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
        static readonly Dictionary<string, TsnVoiceEnglishDictionary> english =
            new Dictionary<string, TsnVoiceEnglishDictionary>(StringComparer.Ordinal);
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
                if (language == "en_US" || language == "en_AU") {
                    if (!english.TryGetValue(language, out TsnVoiceEnglishDictionary dict)) {
                        dict = TsnVoiceEnglishDictionary.Load(language);
                        english[language] = dict;
                    }
                    return dict.Lookup(lyric);
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
                try {
                    List<string> keys = new List<string>();
                    foreach (KeyValuePair<string, string> pair in handle.Package.Config) {
                        keys.Add(pair.Key + "(" + (pair.Value?.Length ?? 0) + ")");
                    }
                    keys.Sort(StringComparer.Ordinal);
                    Serilog.Log.Information("TsnVoice 语音配置键：{Keys}",
                        string.Join(",", keys));
                } catch {
                }
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
                // 初始化器设备分配子与二进制对应内存调优一致（质量无关），
                // 设置失败时逐级回退，保证总能出声。
                // use_ort_model_bytes_directly 刻意不用：
                // 它要求模型字节常驻，与 ReleaseModelBytes 的移动端内存
                // 策略冲突（≥128MB 模型 ×4 缓存有 OOM 风险），属纯内存优化。
                // 任何失败回退应用统一选择器，保证总能出声。
                try {
                    SessionOptions options = new SessionOptions();
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    if (intraThreads > 0) {
                        options.IntraOpNumThreads = intraThreads;
                    }
                    try {
                        options.AddSessionConfigEntry(
                            "session.use_device_allocator_for_initializers", "1");
                    } catch {
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
        /// 风格码宽松读取（保留给单行旧调用方）：缺失维度键或维度为零返回空；
        /// 维度非零但缺失编码时以零填充并记警告。
        /// 表情/说话人请用 ConfigEmotionRows/ConfigSpeakerRows（含回退链）。
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

        /// <summary>
        /// 风格码矩阵全行解析（裸键），对应 DnnVoice::Load 的严格读取：
        /// 各分号行维度必须一致，否则报无效语音；缺失返回空。
        /// </summary>
        public static List<float[]> ConfigCodeRows(Dictionary<string, string> config,
            string dimensionsKey, string codeKey) {
            List<float[]> result = new List<float[]>();
            if (!config.TryGetValue(dimensionsKey, out string dimensionsText)
                || dimensionsText.Length == 0) {
                return result;
            }
            int dimensions = ConfigSize(config, dimensionsKey, 0, true);
            if (dimensions == 0) {
                return result;
            }
            if (!config.TryGetValue(codeKey, out string codeText)
                || codeText.Length == 0) {
                return result;
            }
            return ParseCodeRows(codeKey, codeText, dimensions);
        }

        static List<float[]> ParseCodeRows(string codeKey, string codeText,
            int dimensions) {
            List<float[]> result = new List<float[]>();
            // 空行跳过（对应原生分词器忽略空记号），全空按缺失处理。
            bool anyRow = false;
            foreach (string row in codeText.Split(';')) {
                string trimmedRow = row.Trim();
                if (trimmedRow.Length == 0) {
                    continue;
                }
                anyRow = true;
                List<float> values = new List<float>();
                foreach (string item in trimmedRow.Split(',')) {
                    if (!float.TryParse(item, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value)
                        || float.IsNaN(value) || float.IsInfinity(value)) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            codeKey + " 存在非数字");
                    }
                    values.Add(value);
                }
                if (values.Count != dimensions) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        codeKey + " 维度与配置不一致");
                }
                result.Add(values.ToArray());
            }
            if (!anyRow) {
                return result;
            }
            if (result.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    codeKey + " 为空");
            }
            return result;
        }

        /// <summary>
        /// 表情矩阵全行解析（含缺省回退链），对应二进制两条读取路径：
        /// DnnVoice::Load 的裸 EMOTION_CODE，以及 DnnVoice::Settings 经
        /// AVAILABLE_EMOTION_INDICES 构造的独热行（缺编码时）。
        /// 顺序：裸编码 → 语言编码 → 独热回退 → 零填充（记警告）。
        /// 独热回退保证缺编码语音仍有非零条件，避免模型输入全零劣化。
        /// </summary>
        public static List<float[]> ConfigEmotionRows(
            Dictionary<string, string> config, string language) {
            if (!config.TryGetValue("EMOTION_CONTEXT_DIMENSIONS",
                out string dimensionsText) || dimensionsText.Length == 0) {
                return new List<float[]>();
            }
            int dimensions = ConfigSize(config,
                "EMOTION_CONTEXT_DIMENSIONS", 0, true);
            if (dimensions == 0) {
                return new List<float[]>();
            }
            if (config.TryGetValue("EMOTION_CODE", out string bare)
                && bare.Length > 0) {
                return ParseCodeRows("EMOTION_CODE", bare, dimensions);
            }
            string languageKey = (language ?? string.Empty).Trim() + "_EMOTION_CODE";
            if (config.TryGetValue(languageKey, out string localized)
                && localized.Length > 0) {
                Serilog.Log.Information(
                    "语音使用语言表情编码 {Key}（{Dimensions} 维）", languageKey, dimensions);
                return ParseCodeRows(languageKey, localized, dimensions);
            }
            List<float[]> oneHot = OneHotRows(config, language,
                "AVAILABLE_EMOTION_INDICES", dimensions);
            if (oneHot.Count > 0) {
                Serilog.Log.Information(
                    "语音缺 EMOTION_CODE，按可用表情索引构造 {Rows} 个独热行", oneHot.Count);
                return oneHot;
            }
            Serilog.Log.Warning(
                "语音声明 {Dimensions} 维 EMOTION_CODE 但未提供编码与可用索引，以零填充继续渲染",
                dimensions);
            List<float[]> result = new List<float[]>();
            result.Add(new float[dimensions]);
            return result;
        }

        /// <summary>
        /// 说话人矩阵全行解析：裸编码 → 语言编码 → 零填充（记警告）。
        /// </summary>
        public static List<float[]> ConfigSpeakerRows(
            Dictionary<string, string> config, string language) {
            if (!config.TryGetValue("SPEAKER_CONTEXT_DIMENSIONS",
                out string dimensionsText) || dimensionsText.Length == 0) {
                return new List<float[]>();
            }
            int dimensions = ConfigSize(config,
                "SPEAKER_CONTEXT_DIMENSIONS", 0, true);
            if (dimensions == 0) {
                return new List<float[]>();
            }
            if (config.TryGetValue("SPEAKER_CODE", out string bare)
                && bare.Length > 0) {
                return ParseCodeRows("SPEAKER_CODE", bare, dimensions);
            }
            string languageKey = (language ?? string.Empty).Trim() + "_SPEAKER_CODE";
            if (config.TryGetValue(languageKey, out string localized)
                && localized.Length > 0) {
                Serilog.Log.Information(
                    "语音使用语言说话人编码 {Key}（{Dimensions} 维）", languageKey, dimensions);
                return ParseCodeRows(languageKey, localized, dimensions);
            }
            Serilog.Log.Warning(
                "语音声明 {Dimensions} 维 SPEAKER_CODE 但未提供编码，以零填充继续渲染",
                dimensions);
            List<float[]> result = new List<float[]>();
            result.Add(new float[dimensions]);
            return result;
        }

        /// <summary>
        /// 由可用索引构造独热矩阵行，对应 Settings::Load 的回退路径：
        /// 每个索引对应 dimensions 维单位向量；越界索引报无效语音。
        /// 键按语言优先、裸键兜底（与 GetCodeList 一致）。
        /// </summary>
        static List<float[]> OneHotRows(Dictionary<string, string> config,
            string language, string key, int dimensions) {
            List<float[]> result = new List<float[]>();
            string text = null;
            string languageKey = (language ?? string.Empty).Trim() + "_" + key;
            if (!config.TryGetValue(languageKey, out text) || text.Length == 0) {
                if (!config.TryGetValue(key, out text) || text.Length == 0) {
                    return result;
                }
            }
            foreach (string item in text.Split(',')) {
                string trimmed = item.Trim();
                if (trimmed.Length == 0) {
                    continue;
                }
                if (!int.TryParse(trimmed, out int index) || index < 0
                    || index >= dimensions) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        key + " 存在越界索引");
                }
                float[] row = new float[dimensions];
                row[index] = 1.0f;
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// 缺省表情混合权重，对应 DnnVoice::Load 的 DEFAULT_INTERPOLATION_RATIO：
        /// 缺失时取首行；存在时按 [0,1] 钳制后原样使用（不归一）；
        /// 长度与行数不一致记警告后取首行。
        /// </summary>
        public static double[] ConfigDefaultEmotionWeights(
            Dictionary<string, string> config, int rows) {
            double[] first = new double[Math.Max(0, rows)];
            if (first.Length > 0) {
                first[0] = 1.0;
            }
            if (rows <= 0) {
                return first;
            }
            if (!config.TryGetValue("DEFAULT_INTERPOLATION_RATIO", out string text)
                || text.Trim().Length == 0) {
                return first;
            }
            List<double> values = new List<double>();
            foreach (string item in text.Split(',')) {
                string trimmed = item.Trim();
                if (trimmed.Length == 0) {
                    continue;
                }
                if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value)
                    || double.IsNaN(value) || double.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "DEFAULT_INTERPOLATION_RATIO 存在非数字");
                }
                values.Add(Math.Clamp(value, 0.0, 1.0));
            }
            if (values.Count != rows) {
                Serilog.Log.Warning(
                    "语音 DEFAULT_INTERPOLATION_RATIO 长度 {Actual} 与表情行数 {Rows} 不一致，取首行",
                    values.Count, rows);
                return first;
            }
            return values.ToArray();
        }

        /// <summary>语音表情矩阵行数（0 表示该语音不用表情条件）。</summary>
        public static int EmotionRowCount(Dictionary<string, string> config,
            string language = null) {
            return ConfigEmotionRows(config, language ?? string.Empty).Count;
        }

        /// <summary>
        /// 按混合权重合成表情条件向量：单行时直接返回该行（与旧行为一致，
        /// 权重无关）；多行时按二进制全局混合语义加权。
        /// </summary>
        public static float[] BlendEmotionCode(List<float[]> rows, double[] weights) {
            if (rows.Count == 0) {
                return Array.Empty<float>();
            }
            if (rows.Count == 1) {
                return rows[0];
            }
            double[] normalized = TsnVoiceParameters.NormalizeEmotionWeights(
                weights ?? Array.Empty<double>(), rows.Count);
            float[] result = new float[rows[0].Length];
            for (int i = 0; i < rows.Count; i++) {
                float[] row = rows[i];
                double weight = normalized[i];
                for (int j = 0; j < result.Length; j++) {
                    result[j] += (float)(row[j] * weight);
                }
            }
            return result;
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
