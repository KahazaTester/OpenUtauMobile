using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 小端二进制读取器，对应原生 BinaryReader 与 read_configuration。
    /// </summary>
    public class TsnVoiceBinaryReader {
        readonly byte[] data;
        int position;

        public TsnVoiceBinaryReader(byte[] data) {
            this.data = data;
        }

        TsnVoiceBinaryReader(byte[] data, int position) {
            this.data = data;
            this.position = position;
        }

        /// <summary>
        /// 复制读取器（共享数据，独立位置），用于模型集前缀窥视。
        /// </summary>
        public TsnVoiceBinaryReader Clone() {
            return new TsnVoiceBinaryReader(data, position);
        }

        public int Position => position;

        public int Remaining => data.Length - position;

        public bool AtEnd => position == data.Length;

        void Require(int size, string what) {
            if (size > Remaining) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "截断的" + what + "（逻辑偏移 0x" + position.ToString("x") + "）：需要 "
                    + size + " 字节，剩余 " + Remaining + " 字节");
            }
        }

        public byte[] ReadBytes(int size, string what) {
            Require(size, what);
            byte[] result = new byte[size];
            Array.Copy(data, position, result, 0, size);
            position += size;
            return result;
        }

        public byte ReadU8(string what) {
            return ReadBytes(1, what)[0];
        }

        public uint ReadU32(string what) {
            byte[] bytes = ReadBytes(4, what);
            return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        }

        public ulong ReadU64(string what) {
            byte[] bytes = ReadBytes(8, what);
            ulong result = 0;
            for (int i = 0; i < 8; i++) {
                result |= (ulong)bytes[i] << (i * 8);
            }
            return result;
        }

        public byte[] ReadSizedU64(string what, ulong maxSize = 2UL * 1024 * 1024 * 1024) {
            ulong size = ReadU64(what + " 长度");
            if (size > maxSize) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 过大");
            }
            return ReadBytes((int)size, what);
        }

        public string ReadSizedTextU32(string what, uint maxSize = 1024 * 1024) {
            uint size = ReadU32(what + " 长度");
            if (size > maxSize) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 过大");
            }
            byte[] bytes = ReadBytes((int)size, what);
            try {
                return Encoding.UTF8.GetString(bytes);
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 不是 UTF-8", e);
            }
        }

        public static Dictionary<string, string> ReadConfiguration(
            TsnVoiceBinaryReader reader, string what, ulong maxEntries = 4096) {
            ulong count = reader.ReadU64(what + " 条目数");
            if (count > maxEntries) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 条目过多");
            }
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);
            for (ulong i = 0; i < count; i++) {
                string prefix = what + " 条目 " + i;
                string key = reader.ReadSizedTextU32(prefix + " 键");
                string value = reader.ReadSizedTextU32(prefix + " 值");
                if (result.ContainsKey(key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 存在重复键");
                }
                result[key] = value;
            }
            return result;
        }
    }

    /// <summary>
    /// 单个 ONNX 模型块。
    /// </summary>
    public class TsnVoiceModelBlob {
        public byte[] Data = Array.Empty<byte>();
        public long LogicalOffset;
    }

    /// <summary>
    /// 同一角色的模型集合。
    /// </summary>
    public class TsnVoiceModelSet {
        public string Role = string.Empty;
        public Dictionary<string, string> Config =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public List<TsnVoiceModelBlob> Models = new List<TsnVoiceModelBlob>();
        public long LogicalOffset;
    }

    /// <summary>
    /// 单语言 HMM 语音集合。
    /// </summary>
    public class TsnVoiceHmmSet {
        public string Language = string.Empty;
        public List<byte[]> Blobs = new List<byte[]>();
        public long LogicalOffset;
    }

    /// <summary>
    /// 语音包解析，托管移植自原生 voice_package.cpp。
    /// 覆盖新旧两种载荷布局；无 LANGUAGE 字段时沿用紧凑单语言布局，
    /// 并从文件名推断唯一语言（与官方加载器行为一致）。
    /// </summary>
    public class TsnVoicePackage {
        public string SourcePath = string.Empty;
        public TsnVoiceHeader Header = new TsnVoiceHeader();
        public Dictionary<string, string> Config =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> Languages = new List<string>();
        public byte[] LabelFeatureRules = Array.Empty<byte>();
        public Dictionary<string, byte[]> LanguageQuestions =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public Dictionary<string, TsnVoiceModelSet> UniqueContextModels =
            new Dictionary<string, TsnVoiceModelSet>(StringComparer.Ordinal);
        public byte[] CommonQuestions = Array.Empty<byte>();
        public TsnVoiceModelSet CommonContextModel = new TsnVoiceModelSet();
        public TsnVoiceModelSet LinguisticModel = new TsnVoiceModelSet();
        public TsnVoiceModelSet AcousticStage1Model = new TsnVoiceModelSet();
        public TsnVoiceModelSet AcousticStage2Model = new TsnVoiceModelSet();
        public TsnVoiceModelSet AuxiliaryModel = new TsnVoiceModelSet();
        public Dictionary<string, TsnVoiceHmmSet> HmmVoiceSets =
            new Dictionary<string, TsnVoiceHmmSet>(StringComparer.Ordinal);
        public Dictionary<string, string> VocoderConfig =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public TsnVoiceModelSet VocoderModel = new TsnVoiceModelSet();
        public bool LegacyContextLayout;

        public static TsnVoicePackage Load(string path) {
            TsnVoiceDecoded decoded = TsnVoiceContainer.DecodeVoiceFile(path);
            TsnVoicePackage package = new TsnVoicePackage();
            package.SourcePath = Path.GetFullPath(path);
            package.Header = decoded.Header;
            package.Parse(decoded.Payload);
            package.ValidateCrossRequirements();
            return package;
        }

        public string LanguagesCsv() {
            return string.Join(",", Languages);
        }

        public bool SupportsSingingScore() {
            return TsnVoiceContainer.SupportsSingingScore(Header.VoiceFormat);
        }

        public byte[] GetLanguageQuestions(string language) {
            if (!LanguageQuestions.TryGetValue(language, out byte[] questions)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                    "语音缺少语言 '" + language + "' 的 unique questions");
            }
            return questions;
        }

        static List<string> SplitLanguages(string text) {
            List<string> result = new List<string>();
            int start = 0;
            while (start <= text.Length) {
                int comma = text.IndexOf(',', start);
                int end = comma < 0 ? text.Length : comma;
                string value = text.Substring(start, end - start).Trim();
                if (value.Length > 0) {
                    result.Add(value);
                }
                if (comma < 0) {
                    break;
                }
                start = comma + 1;
            }
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in result) {
                if (!unique.Add(item)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "无效的 LANGUAGE 配置：'" + text + "'");
                }
            }
            if (result.Count == 0 || result.Count > 16) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "无效的 LANGUAGE 配置：'" + text + "'");
            }
            return result;
        }

        static List<string> InferLanguages(string path) {
            string stem = Path.GetFileNameWithoutExtension(path);
            List<string> result = new List<string>();
            foreach (string language in TsnVoiceParameters.Languages) {
                if (stem.IndexOf("_" + language + "_", StringComparison.OrdinalIgnoreCase) >= 0) {
                    result.Add(language);
                }
            }
            if (result.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "旧版 SINGER 包缺少 LANGUAGE，且文件名无法确定唯一支持语言：" + path);
            }
            return result;
        }

        static TsnVoiceModelSet ReadModelSet(TsnVoiceBinaryReader reader, string role) {
            TsnVoiceModelSet result = new TsnVoiceModelSet();
            result.Role = role;
            result.LogicalOffset = reader.Position;
            // 新记录以 u64 配置条目数开头，旧记录直接以 u8 模型数开头。
            // 在复制体上窥视，原读取器位置不受影响；合法的配置条目数必然有界。
            TsnVoiceBinaryReader probe = reader.Clone();
            ulong configurationEntries = probe.ReadU64(role + " 模型集前缀");
            if (configurationEntries <= 4096) {
                result.Config = TsnVoiceBinaryReader.ReadConfiguration(
                    reader, role + " 模型集配置", 4096);
            }
            byte count = reader.ReadU8(role + " 模型数");
            for (int i = 0; i < count; i++) {
                ulong size = reader.ReadU64(role + " 模型大小");
                long offset = reader.Position;
                if (size > 2UL * 1024 * 1024 * 1024) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, role + " 模型过大");
                }
                byte[] model = reader.ReadBytes((int)size, role + " 模型数据");
                if (model.Length < 8 || model[4] != (byte)'O' || model[5] != (byte)'R'
                    || model[6] != (byte)'T' || model[7] != (byte)'M') {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        role + " 模型 " + i + " 不是 ORT FlatBuffer（逻辑偏移 0x"
                        + offset.ToString("x") + "）");
                }
                TsnVoiceModelBlob blob = new TsnVoiceModelBlob();
                blob.Data = model;
                blob.LogicalOffset = offset;
                result.Models.Add(blob);
            }
            return result;
        }

        static TsnVoiceHmmSet ReadHmmVoiceSet(TsnVoiceBinaryReader reader, string language) {
            TsnVoiceHmmSet result = new TsnVoiceHmmSet();
            result.Language = language;
            result.LogicalOffset = reader.Position;
            byte count = reader.ReadU8(language + " HMM 语音数");
            for (int i = 0; i < count; i++) {
                result.Blobs.Add(reader.ReadSizedU64(language + " HMM 语音"));
            }
            return result;
        }

        void Parse(byte[] payload) {
            TsnVoiceBinaryReader reader = new TsnVoiceBinaryReader(payload);
            Config = TsnVoiceBinaryReader.ReadConfiguration(reader, "DNN 语音配置");
            if (Config.TryGetValue("LANGUAGE", out string languageValue)) {
                Languages = SplitLanguages(languageValue);
                LegacyContextLayout = false;
            } else {
                Languages = InferLanguages(SourcePath);
                LegacyContextLayout = true;
            }
            LabelFeatureRules = reader.ReadSizedU64("标签特征规则");
            if (LegacyContextLayout) {
                LinguisticModel = ReadModelSet(reader, "linguistic");
                AcousticStage1Model = ReadModelSet(reader, "acoustic_stage1");
                AcousticStage2Model = ReadModelSet(reader, "acoustic_stage2");
                AuxiliaryModel = ReadModelSet(reader, "auxiliary");
                HmmVoiceSets[Languages[0]] = ReadHmmVoiceSet(reader, Languages[0]);
                VocoderConfig = TsnVoiceBinaryReader.ReadConfiguration(reader, "神经声码器配置");
                VocoderModel = ReadModelSet(reader, "vocoder");
                if (!reader.AtEnd) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "旧版 SINGER 包尾部存在未解析数据");
                }
                return;
            }
            foreach (string language in Languages) {
                LanguageQuestions[language] = reader.ReadSizedU64(language + " unique-context questions");
                UniqueContextModels[language] = ReadModelSet(reader, "unique_context:" + language);
            }
            CommonQuestions = reader.ReadSizedU64("common-context questions");
            CommonContextModel = ReadModelSet(reader, "common_context");
            LinguisticModel = ReadModelSet(reader, "linguistic");
            AcousticStage1Model = ReadModelSet(reader, "acoustic_stage1");
            AcousticStage2Model = ReadModelSet(reader, "acoustic_stage2");
            AuxiliaryModel = ReadModelSet(reader, "auxiliary");
            foreach (string language in Languages) {
                HmmVoiceSets[language] = ReadHmmVoiceSet(reader, language);
            }
            VocoderConfig = TsnVoiceBinaryReader.ReadConfiguration(reader, "神经声码器配置");
            VocoderModel = ReadModelSet(reader, "vocoder");
            if (!reader.AtEnd) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音载荷尾部存在未解析数据（逻辑偏移 0x" + reader.Position.ToString("x")
                    + "，剩余 " + reader.Remaining + " 字节）");
            }
        }
        static List<int> ParseCsvInts(Dictionary<string, string> config, string key) {
            if (!config.TryGetValue(key, out string text)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "缺少整数列表 '" + key + "'");
            }
            List<int> result = new List<int>();
            int start = 0;
            while (start <= text.Length) {
                int comma = text.IndexOf(',', start);
                int end = comma < 0 ? text.Length : comma;
                string item = text.Substring(start, end - start);
                if (!int.TryParse(item, out int value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的整数列表 '" + key + "'");
                }
                result.Add(value);
                if (comma < 0) {
                    break;
                }
                start = comma + 1;
            }
            return result;
        }

        void ValidateCrossRequirements() {
            string expected = Header.VoiceFormat == 0 ? "SINGER"
                : Header.VoiceFormat == 5 ? "SINGER2" : null;
            if (expected != null) {
                if (!Config.TryGetValue("LABEL_TYPE", out string labelType) || labelType != expected) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音头与配置的标签类型不一致");
                }
            }
            int featureCount = 0;
            if (Config.TryGetValue("ACOUSTIC_FEATURES", out string features) && features.Length > 0) {
                featureCount = 1;
                foreach (char c in features) {
                    if (c == ',') {
                        featureCount++;
                    }
                }
            }
            if (featureCount != ParseCsvInts(Config, "ACOUSTIC_FEATURE_DIMENSIONS").Count) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "声学特征名与维度数量不一致");
            }
            foreach (string key in new string[] { "SAMPLING_FREQUENCY", "FRAME_PERIOD" }) {
                if (!Config.TryGetValue(key, out string voiceValue)
                    || !VocoderConfig.TryGetValue(key, out string vocoderValue)
                    || voiceValue != vocoderValue) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "语音与声码器配置不一致：" + key);
                }
            }
        }
    }
}
