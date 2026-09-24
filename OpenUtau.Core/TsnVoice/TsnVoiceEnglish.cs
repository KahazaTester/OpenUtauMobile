using System;
using System.Collections.Generic;
using System.Text;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 英语前端，移植自原生 english_frontend.cpp。
    /// 先查编译词典；词典未收录的词需要 CMU Flite LTS 回退，
    /// 其模型数据移植尚未完成，此时报明确错误而非静默失败。
    /// </summary>
    public class TsnVoiceEnglishDictionary {
        const ulong MaxDictionaryItems = 4000000;
        const ulong MaxDictionaryBytes = 512UL * 1024 * 1024;

        class IdTable {
            public List<ushort> Counts = new List<ushort>();
            public List<uint> Starts = new List<uint>();
            public List<uint> Left = new List<uint>();
            public List<uint> Right = new List<uint>();

            public void Range(int item, out int begin, out int end) {
                if (item < 0 || item >= Counts.Count) {
                    begin = 0;
                    end = 0;
                    return;
                }
                begin = (int)Starts[item];
                end = begin + Counts[item];
            }
        }

        Dictionary<string, string> config =
            new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> words = new List<string>();
        List<string> pronunciations = new List<string>();
        IdTable wordTable = new IdTable();
        IdTable optionTable = new IdTable();
        List<string> optionStrings = new List<string>();
        Dictionary<string, int> wordIds =
            new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> vowels = new List<string>();

        static ushort ReadU16(TsnVoiceBinaryReader reader, string what) {
            byte[] bytes = reader.ReadBytes(2, what);
            return (ushort)(bytes[0] | (bytes[1] << 8));
        }

        static ushort LittleU16(byte[] bytes, int index) {
            int offset = index * 2;
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        static uint LittleU32(byte[] bytes, int index) {
            int offset = index * 4;
            return (uint)(bytes[offset] | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
        }

        static ulong LittleU64(byte[] bytes, int index) {
            int offset = index * 8;
            ulong result = 0;
            for (int i = 0; i < 8; i++) {
                result |= (ulong)bytes[offset + i] << (i * 8);
            }
            return result;
        }

        static int CheckedBytes(ulong count, int width, string what) {
            if (count > MaxDictionaryItems || count > MaxDictionaryBytes / (ulong)width) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 过大");
            }
            return (int)(count * (ulong)width);
        }

        static List<string> ReadStringList(TsnVoiceBinaryReader reader, string what) {
            ulong count = reader.ReadU64(what + " 字符串数");
            ulong dataSize = reader.ReadU64(what + " 字节数");
            if (dataSize > 256UL * 1024 * 1024) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 字符串数据过大");
            }
            byte[] offsets = reader.ReadBytes(CheckedBytes(count, 8, what + " 偏移"), what + " 偏移");
            byte[] data = reader.ReadBytes((int)dataSize, what);
            if (count == 0) {
                if (data.Length > 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 有数据但无字符串");
                }
                return new List<string>();
            }
            if (data.Length == 0 || LittleU64(offsets, 0) != 0 || data[data.Length - 1] != 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 字符串数据无效");
            }
            List<string> result = new List<string>();
            ulong previous = 0;
            for (int index = 0; index < (int)count; index++) {
                ulong start = LittleU64(offsets, index);
                ulong end = index + 1 < (int)count
                    ? LittleU64(offsets, index + 1)
                    : dataSize;
                if ((index != 0 && start <= previous) || end <= start || end > dataSize
                    || data[end - 1] != 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 字符串偏移无效");
                }
                for (ulong i = start; i < end - 1; i++) {
                    if (data[i] == 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 存在内嵌 NUL");
                    }
                }
                result.Add(System.Text.Encoding.UTF8.GetString(
                    data, (int)start, (int)(end - start - 1)));
                previous = start;
            }
            return result;
        }

        static List<string> ReadStringIdTree(TsnVoiceBinaryReader reader, string what) {
            List<string> strings = ReadStringList(reader, what);
            uint nodes = reader.ReadU32(what + " 节点数");
            reader.ReadBytes(CheckedBytes(nodes, 4, what + " 节点"), what + " 节点");
            return strings;
        }

        static IdTable ReadIdTable(TsnVoiceBinaryReader reader, string what) {
            ulong itemCount = reader.ReadU64(what + " 条目数");
            ulong resultCount = reader.ReadU64(what + " 结果数");
            byte[] countsBytes = reader.ReadBytes(
                CheckedBytes(itemCount, 2, what), what + " 计数");
            byte[] startsBytes = reader.ReadBytes(
                CheckedBytes(itemCount, 4, what), what + " 起始");
            byte[] leftBytes = reader.ReadBytes(
                CheckedBytes(resultCount, 4, what), what + " 左结果");
            byte[] rightBytes = reader.ReadBytes(
                CheckedBytes(resultCount, 4, what), what + " 右结果");
            IdTable result = new IdTable();
            for (int index = 0; index < (int)itemCount; index++) {
                ushort count = LittleU16(countsBytes, index);
                uint start = LittleU32(startsBytes, index);
                if ((ulong)start + count > resultCount) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 结果区间越界");
                }
                result.Counts.Add(count);
                result.Starts.Add(start);
            }
            for (int index = 0; index < (int)resultCount; index++) {
                result.Left.Add(LittleU32(leftBytes, index));
                result.Right.Add(LittleU32(rightBytes, index));
            }
            return result;
        }

        static void SkipWfst(TsnVoiceBinaryReader reader, string what) {
            int states = CheckedBytes(reader.ReadU64("WFST 状态数"), 12, what);
            int arcs = CheckedBytes(reader.ReadU64("WFST 弧数"), 32, what);
            int finals = CheckedBytes(reader.ReadU64("WFST 终点数"), 16, what);
            if ((long)states > (long)MaxDictionaryBytes - arcs
                || (long)states + arcs > (long)MaxDictionaryBytes - finals) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 过大");
            }
            reader.ReadBytes(states + arcs + finals, what);
        }

        public static TsnVoiceEnglishDictionary Load() {
            foreach (string name in new string[] { "dict.bin", "ssep.bin", "tobi.bin" }) {
                if (!TsnVoiceDictionaries.DictionaryFileExists("en_US", name)) {
                    throw new TsnVoiceException(TsnVoiceStatus.IoError,
                        "缺少英语词典文件 " + name);
                }
            }
            byte[] payload = TsnVoiceDictionaries.GetDictionaryBytes("en_US", "dict.bin");
            if ((ulong)payload.Length > MaxDictionaryBytes) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语词典过大");
            }
            TsnVoiceBinaryReader reader = new TsnVoiceBinaryReader(payload);
            if (ReadU16(reader, "英语词典主版本") != 1
                || ReadU16(reader, "英语词典次版本") != 0) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported, "不支持的英语词典版本");
            }
            TsnVoiceEnglishDictionary result = new TsnVoiceEnglishDictionary();
            result.config = TsnVoiceBinaryReader.ReadConfiguration(reader, "英语词典配置");
            result.words = ReadStringIdTree(reader, "英语全局字符串树");
            for (int index = 0; index < 4; index++) {
                ReadStringList(reader, "英语字符串映射 " + index);
            }
            ReadStringIdTree(reader, "英语归一化输入树");
            ReadStringIdTree(reader, "英语归一化输出树");
            SkipWfst(reader, "英语归一化 WFST");
            reader.ReadSizedTextU32("英语词典构建标识");
            result.pronunciations = ReadStringList(reader, "英语发音表");
            result.wordTable = ReadIdTable(reader, "英语词候选");
            result.optionTable = ReadIdTable(reader, "英语候选选项");
            result.optionStrings = ReadStringIdTree(reader, "英语选项字符串树");
            if (result.words.Count != result.wordTable.Counts.Count) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语词 ID 与候选表不一致");
            }
            foreach (uint value in result.wordTable.Left) {
                if (value >= result.pronunciations.Count) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语候选发音越界");
                }
            }
            foreach (uint value in result.wordTable.Right) {
                if (value >= result.optionTable.Counts.Count) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语候选选项越界");
                }
            }
            foreach (uint value in result.optionTable.Left) {
                if (value >= result.optionStrings.Count) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语选项键越界");
                }
            }
            foreach (uint value in result.optionTable.Right) {
                if (value >= result.optionStrings.Count) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语选项值越界");
                }
            }
            if (!result.config.TryGetValue("VOWELS", out string vowelsValue)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语词典缺少 VOWELS");
            }
            result.vowels = SplitNonEmpty(vowelsValue, ',', "英语元音");
            for (int index = 0; index < result.words.Count; index++) {
                if (result.wordIds.ContainsKey(result.words[index])) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语词表存在重复");
                }
                result.wordIds[result.words[index]] = index;
            }
            return result;
        }

        static List<string> SplitNonEmpty(string value, char delimiter, string what) {
            List<string> result = new List<string>();
            int start = 0;
            while (start <= value.Length) {
                int end = value.IndexOf(delimiter, start);
                if (end < 0) {
                    end = value.Length;
                }
                string item = value.Substring(start, end - start);
                if (item.Length == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, what + " 存在空条目");
                }
                result.Add(item);
                start = end + 1;
            }
            return result;
        }

        static string NormalizeWord(string lyric) {
            string value = lyric.Trim();
            if (value.Length == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "英语歌词为空");
            }
            value = value.Replace("’", "'");
            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char c in value) {
                if (char.IsWhiteSpace(c)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "英语音符歌词必须恰好是一个词");
                }
                builder.Append(c < 0x80 ? char.ToLowerInvariant(c) : c);
            }
            return builder.ToString();
        }

        bool HasPos(int candidate) {
            int optionSet = (int)wordTable.Right[candidate];
            optionTable.Range(optionSet, out int begin, out int end);
            for (int index = begin; index < end; index++) {
                if (optionStrings[(int)optionTable.Left[index]] == "POS") {
                    return true;
                }
            }
            return false;
        }

        public TsnVoicePronunciation Lookup(string lyric) {
            string word = NormalizeWord(lyric);
            if (!wordIds.TryGetValue(word, out int wordId)) {
                // CMU Flite LTS 回退的模型数据移植尚未完成：报明确错误，
                // 不伪造发音。后续移植 third_party/flite 模型数据后补齐。
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    "英语词 '" + word + "' 不在编译词典中，LTS 回退暂未移植；"
                    + "请使用方括号注音指定发音，或等待 LTS 数据移植完成。");
            }
            wordTable.Range(wordId, out int candidateBegin, out int candidateEnd);
            if (candidateBegin == candidateEnd) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语词没有发音");
            }
            int selected = candidateBegin;
            if (candidateEnd - candidateBegin > 1) {
                List<int> generic = new List<int>();
                for (int index = candidateBegin; index < candidateEnd; index++) {
                    if (!HasPos(index)) {
                        generic.Add(index);
                    }
                }
                if (generic.Count == 1) {
                    selected = generic[0];
                } else {
                    // 无通用读音（或全部带词性标注）时回退首个候选并记录，
                    // 保证渲染能出声；音素器侧已对该音符标错。
                    selected = candidateBegin;
                    Serilog.Log.Warning(
                        "英语词 '{Word}' 存在语境相关读音，选用首个候选", word);
                }
            }
            string encoded = pronunciations[(int)wordTable.Left[selected]];
            List<string> syllables = SplitNonEmpty(encoded, '|', "英语发音");
            TsnVoicePronunciation result = new TsnVoicePronunciation();
            result.Vowels = new List<string>(vowels);
            for (int syllableIndex = 0; syllableIndex < syllables.Count; syllableIndex++) {
                List<string> tokens = SplitNonEmpty(syllables[syllableIndex], ',', "英语音节");
                int stress = 0;
                bool sawStress = false;
                for (int i = 0; i < tokens.Count; i++) {
                    string token = tokens[i];
                    if (token.Length > 0 && token[token.Length - 1] >= '0'
                        && token[token.Length - 1] <= '2') {
                        if (sawStress) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "英语音节存在多个重音标记");
                        }
                        stress = token[token.Length - 1] - '0';
                        token = token.Substring(0, token.Length - 1);
                        tokens[i] = token;
                        sawStress = true;
                    }
                    if (token.Length == 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "英语发音存在无效音素");
                    }
                    foreach (char c in token) {
                        if (!((c >= 'a' && c <= 'z') || c == '#')) {
                            throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                                "英语发音存在无效音素");
                        }
                    }
                }
                int boundary = (syllableIndex == 0 ? 1 : 0)
                    | (syllableIndex + 1 == syllables.Count ? 2 : 0);
                int accent = stress == 1 ? 3 : stress == 2 ? 1 : 0;
                string flag = boundary.ToString() + accent.ToString();
                foreach (string token in tokens) {
                    result.Phonemes.Add(token);
                    result.PhonemeLanguageFlags.Add(flag);
                }
            }
            if (result.Phonemes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "英语发音为空");
            }
            result.LanguageFlag = result.PhonemeLanguageFlags[0];
            return result;
        }
    }
}
