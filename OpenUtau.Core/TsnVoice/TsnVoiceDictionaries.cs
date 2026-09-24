using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using OpenUtau.Core;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 发音结果，对应原生 Pronunciation。
    /// </summary>
    public class TsnVoicePronunciation {
        public List<string> Phonemes = new List<string>();
        public List<string> Vowels = new List<string>();
        public string LanguageFlag = string.Empty;
        public List<string> PhonemeLanguageFlags = new List<string>();
    }

    /// <summary>
    /// 词典根目录解析与通用文本读取。
    /// 发音词典与语音目录随程序内嵌，开箱即用；DataPath 下的同名文件可覆盖内嵌版本。
    /// </summary>
    public static class TsnVoiceDictionaries {
        static readonly Assembly assembly = typeof(TsnVoiceDictionaries).Assembly;

        /// <summary>
        /// 词典根目录（用户覆盖用）。
        /// </summary>
        public static string DictionaryRoot {
            get {
                return Path.Combine(PathManager.Inst.DictionariesPath, "TsnVoice");
            }
        }

        static string Dotted(params string[] parts) {
            return string.Join(".", parts);
        }

        static byte[] ReadEmbedded(string name) {
            using (Stream stream = assembly.GetManifestResourceStream(name)) {
                if (stream == null) {
                    throw new TsnVoiceException(TsnVoiceStatus.IoError,
                        "缺少内嵌 TsnVoice 数据文件：" + name);
                }
                using (MemoryStream buffer = new MemoryStream((int)stream.Length)) {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        public static bool EmbeddedDictionaryExists(params string[] parts) {
            return assembly.GetManifestResourceInfo(
                "TsnVoice.Dictionaries." + Dotted(parts)) != null;
        }

        /// <summary>
        /// 读取词典文件字节：DataPath 覆盖优先，否则使用内嵌资源。
        /// </summary>
        public static byte[] GetDictionaryBytes(params string[] parts) {
            List<string> all = new List<string>();
            all.Add(DictionaryRoot);
            all.AddRange(parts);
            string filePath = Path.Combine(all.ToArray());
            if (File.Exists(filePath)) {
                try {
                    return File.ReadAllBytes(filePath);
                } catch (Exception e) {
                    throw new TsnVoiceException(TsnVoiceStatus.IoError,
                        "无法读取 TsnVoice 词典文件：" + filePath, e);
                }
            }
            return ReadEmbedded("TsnVoice.Dictionaries." + Dotted(parts));
        }

        /// <summary>
        /// 读取词典文本（UTF-8，去除 BOM）。
        /// </summary>
        public static string GetDictionaryText(params string[] parts) {
            string text = Encoding.UTF8.GetString(GetDictionaryBytes(parts));
            // 注意：必须使用 Ordinal 比较。U+FEFF 在语言比较中是可忽略字符，
            // 默认的 StartsWith("\uFEFF") 会对任意字符串返回 true 并吃掉首字符。
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) {
                text = text.Substring(1);
            }
            return text;
        }

        public static bool DictionaryFileExists(params string[] parts) {
            List<string> all = new List<string>();
            all.Add(DictionaryRoot);
            all.AddRange(parts);
            if (File.Exists(Path.Combine(all.ToArray()))) {
                return true;
            }
            return EmbeddedDictionaryExists(parts);
        }

        /// <summary>
        /// 读取语音目录文件字节（catalog.json、立绘等）：DataPath 覆盖优先。
        /// </summary>
        public static byte[] GetVoiceBytes(params string[] parts) {
            List<string> all = new List<string>();
            all.Add(PathManager.Inst.DataPath);
            all.Add("TsnVoice");
            all.AddRange(parts);
            string filePath = Path.Combine(all.ToArray());
            if (File.Exists(filePath)) {
                try {
                    return File.ReadAllBytes(filePath);
                } catch (Exception e) {
                    throw new TsnVoiceException(TsnVoiceStatus.IoError,
                        "无法读取 TsnVoice 语音数据文件：" + filePath, e);
                }
            }
            return ReadEmbedded("TsnVoice.Voice." + Dotted(parts));
        }

        /// <summary>
        /// 按行解析“键 值1 值2...”表格文本。
        /// </summary>
        public static List<KeyValuePair<string, List<string>>> ReadKeyTable(
            string text, string what) {
            List<KeyValuePair<string, List<string>>> result =
                new List<KeyValuePair<string, List<string>>>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            int lineNumber = 0;
            foreach (string rawLine in text.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0) {
                    continue;
                }
                lineNumber++;
                string[] columns = line.Split(
                    new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2 || !keys.Add(columns[0])) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "无效或重复的词典条目：" + what + " 第 " + lineNumber
                        + " 行（" + line + "）");
                }
                List<string> values = new List<string>();
                for (int i = 1; i < columns.Length; i++) {
                    values.Add(columns[i]);
                }
                result.Add(new KeyValuePair<string, List<string>>(columns[0], values));
            }
            return result;
        }

        /// <summary>
        /// 按行解析“键 值”两列表格。
        /// </summary>
        public static Dictionary<string, string> ReadTwoColumns(string text, string what) {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rawLine in text.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0) {
                    continue;
                }
                // 与原生一致：取前两列，多余列忽略。
                string[] columns = line.Split(
                    new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2 || result.ContainsKey(columns[0])) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "无效或重复的表格条目：" + what + "（" + line + "）");
                }
                result[columns[0]] = columns[1];
            }
            return result;
        }

        public static List<string> SplitComma(string value) {
            List<string> result = new List<string>();
            foreach (string item in value.Split(',')) {
                if (item.Length > 0) {
                    result.Add(item);
                }
            }
            return result;
        }

        public static bool StartsAt(string text, int position, string value) {
            return position <= text.Length
                && value.Length <= text.Length - position
                && text.Substring(position, value.Length).Equals(value, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 日语前端，移植自原生 frontend.cpp（JapaneseDictionary）。
    /// 纯文本表格，最长匹配。
    /// </summary>
    public class TsnVoiceJapaneseDictionary {
        List<KeyValuePair<string, List<string>>> entries =
            new List<KeyValuePair<string, List<string>>>();
        List<string> vowels = new List<string>();
        List<string> macrons = new List<string>();
        List<string> reductionMarks = new List<string>();
        List<string> scoreMarks = new List<string>();

        public static TsnVoiceJapaneseDictionary Load() {
            TsnVoiceJapaneseDictionary result = new TsnVoiceJapaneseDictionary();
            result.entries = TsnVoiceDictionaries.ReadKeyTable(
                TsnVoiceDictionaries.GetDictionaryText("japanese.utf_8.table"), "日语音素表");
            List<KeyValuePair<string, List<string>>> romaji =
                TsnVoiceDictionaries.ReadKeyTable(
                    TsnVoiceDictionaries.GetDictionaryText("japanese.romaji.table"), "日语罗马字表");
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> entry in result.entries) {
                keys.Add(entry.Key);
            }
            foreach (KeyValuePair<string, List<string>> entry in romaji) {
                if (!keys.Add(entry.Key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "假名表与罗马字表存在重复键");
                }
                result.entries.Add(entry);
            }
            result.entries.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
            Dictionary<string, string> config = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (string rawLine in TsnVoiceDictionaries.GetDictionaryText(
                "japanese.utf_8.conf").Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0) {
                    continue;
                }
                int equal = line.IndexOf('=');
                if (equal < 0 || equal + 2 >= line.Length
                    || line[equal + 1] != '"' || line[line.Length - 1] != '"') {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "日语词典配置格式错误");
                }
                string key = line.Substring(0, equal);
                string value = line.Substring(equal + 2, line.Length - equal - 3);
                if (key.Length == 0 || config.ContainsKey(key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "日语词典配置键重复");
                }
                config[key] = value;
            }
            result.vowels = RequireConfigList(config, "VOWELS");
            result.macrons = RequireConfigList(config, "MACRON");
            result.reductionMarks = RequireConfigList(config, "VOWEL_REDUCTION");
            if (!config.TryGetValue("SCORE_MARKS", out string scoreMarksValue)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "日语词典缺少 SCORE_MARKS");
            }
            foreach (string group in scoreMarksValue.Split(';')) {
                result.scoreMarks.AddRange(TsnVoiceDictionaries.SplitComma(group));
            }
            string macronRules = TsnVoiceDictionaries.GetDictionaryText(
                "japanese.macron");
            foreach (char value in macronRules) {
                if (!char.IsWhiteSpace(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.Unsupported, "不支持非空的日语长音覆盖表");
                }
            }
            if (result.vowels.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "日语元音表为空");
            }
            return result;
        }

        static List<string> RequireConfigList(Dictionary<string, string> config, string key) {
            if (!config.TryGetValue(key, out string value)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "日语词典缺少 " + key);
            }
            return TsnVoiceDictionaries.SplitComma(value);
        }

        static string LongestMatch(List<string> candidates, string text, int position) {
            string best = null;
            foreach (string candidate in candidates) {
                if (TsnVoiceDictionaries.StartsAt(text, position, candidate)
                    && (best == null || candidate.Length > best.Length)) {
                    best = candidate;
                }
            }
            return best;
        }

        public TsnVoicePronunciation Lookup(string lyric) {
            if (lyric.Length == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "日语歌词为空");
            }
            HashSet<string> vowelSet = new HashSet<string>(vowels, StringComparer.Ordinal);
            List<string> phonemes = new List<string>();
            int position = 0;
            while (position < lyric.Length) {
                string mark = LongestMatch(scoreMarks, lyric, position);
                if (mark != null) {
                    position += mark.Length;
                    continue;
                }
                string macron = LongestMatch(macrons, lyric, position);
                if (macron != null) {
                    if (phonemes.Count == 0 || !vowelSet.Contains(phonemes[phonemes.Count - 1])) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "日语长音标记前缺少元音");
                    }
                    phonemes.Add(phonemes[phonemes.Count - 1]);
                    position += macron.Length;
                    continue;
                }
                KeyValuePair<string, List<string>>? found = null;
                foreach (KeyValuePair<string, List<string>> entry in entries) {
                    if (TsnVoiceDictionaries.StartsAt(lyric, position, entry.Key)) {
                        found = entry;
                        break;
                    }
                }
                if (found == null) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "日语词典无法转换 UTF-8 字节 " + position + " 处的歌词");
                }
                phonemes.AddRange(found.Value.Value);
                position += found.Value.Key.Length;
                string reduction = LongestMatch(reductionMarks, lyric, position);
                if (reduction != null) {
                    position += reduction.Length;
                }
            }
            if (phonemes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "日语歌词没有音素");
            }
            TsnVoicePronunciation result = new TsnVoicePronunciation();
            result.Phonemes = phonemes;
            result.Vowels = new List<string>(vowels);
            result.LanguageFlag = "0";
            return result;
        }
    }
}
