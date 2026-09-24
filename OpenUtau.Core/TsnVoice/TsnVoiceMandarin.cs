using System;
using System.Collections.Generic;
using System.Text;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 普通话前端，移植自原生 frontend.cpp（MandarinDictionary）。
    /// 支持注音、拼音（含声调标记与数字）与汉字字形三种输入。
    /// </summary>
    public class TsnVoiceMandarinDictionary {
        Dictionary<string, string> bopomofoToPinyin =
            new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> graphemeToPinyin =
            new Dictionary<string, string>(StringComparer.Ordinal);
        List<KeyValuePair<string, string>> graphemeEntries =
            new List<KeyValuePair<string, string>>();
        Dictionary<string, List<string>> pinyinToPhonemes =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, int>> toneMarks =
            new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);
        Dictionary<string, int> bopomofoTones =
            new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> vowels = new List<string>();

        public static TsnVoiceMandarinDictionary Load(string language) {
            byte[] payload = TsnVoiceContainer.DecodeLegacyDictionaryBytes(
                TsnVoiceDictionaries.GetDictionaryBytes(language, "dict.bin"));
            TsnVoiceBinaryReader reader = new TsnVoiceBinaryReader(payload);
            byte[] version = reader.ReadBytes(4, "普通话词典版本");
            int major = version[0] | (version[1] << 8);
            int minor = version[2] | (version[3] << 8);
            if (major != 1 || minor != 0) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported, "不支持的普通话词典版本");
            }
            Dictionary<string, string> config =
                TsnVoiceBinaryReader.ReadConfiguration(reader, "普通话词典配置");
            byte[] bopomofo = reader.ReadSizedU64("普通话注音表");
            byte[] graphemes = reader.ReadSizedU64("普通话字形表");
            byte[] phonemes = reader.ReadSizedU64("普通话音素表");
            if (!reader.AtEnd) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "普通话词典尾部存在多余数据");
            }
            TsnVoiceMandarinDictionary result = new TsnVoiceMandarinDictionary();
            result.bopomofoToPinyin = TsnVoiceDictionaries.ReadTwoColumns(
                Encoding.UTF8.GetString(bopomofo), "普通话注音表");
            result.graphemeToPinyin = TsnVoiceDictionaries.ReadTwoColumns(
                Encoding.UTF8.GetString(graphemes), "普通话字形表");
            result.graphemeEntries.AddRange(result.graphemeToPinyin);
            result.graphemeEntries.Sort(
                (left, right) => TsnVoiceDictionaries.Utf8ByteLength(
                    right.Key).CompareTo(TsnVoiceDictionaries.Utf8ByteLength(left.Key)));
            result.pinyinToPhonemes = ReadPhonemeTable(
                Encoding.UTF8.GetString(phonemes));
            if (!config.TryGetValue("VOWELS", out string vowelsValue)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "普通话词典缺少 VOWELS");
            }
            result.vowels = TsnVoiceDictionaries.SplitComma(vowelsValue);
            for (int tone = 1; tone <= 4; tone++) {
                string key = "PINYIN_TONE_MARKS_" + tone;
                if (!config.TryGetValue(key, out string mappings)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "普通话词典缺少 " + key);
                }
                foreach (string mapping in TsnVoiceDictionaries.SplitComma(mappings)) {
                    int colon = mapping.IndexOf(':');
                    if (colon <= 0 || colon + 1 >= mapping.Length
                        || result.toneMarks.ContainsKey(mapping.Substring(0, colon))) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的普通话拼音声调映射");
                    }
                    result.toneMarks[mapping.Substring(0, colon)] =
                        new KeyValuePair<string, int>(mapping.Substring(colon + 1), tone);
                }
            }
            for (int tone = 0; tone <= 4; tone++) {
                string key = "BOPOMOFO_TONE_SYMBOLS_" + tone;
                if (!config.TryGetValue(key, out string symbols)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "普通话词典缺少 " + key);
                }
                foreach (string symbol in TsnVoiceDictionaries.SplitComma(symbols)) {
                    if (result.bopomofoTones.TryGetValue(symbol, out int existing)
                        && existing != tone) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "普通话注音声调符号冲突");
                    }
                    result.bopomofoTones[symbol] = tone;
                }
            }
            return result;
        }

        static Dictionary<string, List<string>> ReadPhonemeTable(string text) {
            Dictionary<string, List<string>> result =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string rawLine in text.Split('\n')) {
                string line = rawLine.Trim();
                if (line.Length == 0) {
                    continue;
                }
                string[] columns = line.Split(
                    new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2 || result.ContainsKey(columns[0])) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                        "无效或重复的普通话音素条目");
                }
                List<string> values = new List<string>();
                for (int i = 1; i < columns.Length; i++) {
                    values.Add(columns[i]);
                }
                result[columns[0]] = values;
            }
            return result;
        }

        public TsnVoicePronunciation Lookup(string lyric) {
            string text = lyric.Trim();
            if (text.Length == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "普通话歌词为空");
            }
            TsnVoicePronunciation result = new TsnVoicePronunciation();
            result.Vowels = new List<string>(vowels);
            // 注音输入优先。
            string bopomofo = text;
            int? bopomofoTone = null;
            foreach (KeyValuePair<string, int> toneSymbol in bopomofoTones) {
                int position = 0;
                while (true) {
                    int found = bopomofo.IndexOf(toneSymbol.Key, position,
                        StringComparison.Ordinal);
                    if (found < 0) {
                        break;
                    }
                    if (bopomofoTone.HasValue && bopomofoTone.Value != toneSymbol.Value) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "普通话注音声调标记冲突");
                    }
                    bopomofoTone = toneSymbol.Value;
                    bopomofo = bopomofo.Remove(found, toneSymbol.Key.Length);
                    position = found;
                }
            }
            if (bopomofoToPinyin.TryGetValue(bopomofo, out string bopomofoPinyin)) {
                AppendPinyin(result, bopomofoPinyin, bopomofoTone ?? 1);
                return result;
            }
            // 字形最长匹配。
            int position2 = 0;
            while (position2 < text.Length) {
                KeyValuePair<string, string>? found = null;
                foreach (KeyValuePair<string, string> entry in graphemeEntries) {
                    if (TsnVoiceDictionaries.StartsAt(text, position2, entry.Key)) {
                        found = entry;
                        break;
                    }
                }
                if (found == null) {
                    break;
                }
                AppendPinyin(result, found.Value.Value, 1);
                position2 += found.Value.Key.Length;
            }
            if (position2 == text.Length) {
                return result;
            }
            if (position2 != 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                    "普通话词典无法在 UTF-8 字节 " + position2 + " 处转换歌词");
            }
            // 空格分隔的拼音音节。
            foreach (string syllable in text.Split(
                new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
                AppendPinyin(result, syllable, 1);
            }
            if (result.Phonemes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "普通话歌词没有发音");
            }
            return result;
        }

        void AppendPinyin(TsnVoicePronunciation result, string spelling, int defaultTone) {
            spelling = spelling.Trim();
            int tone = defaultTone;
            bool toneSet = false;
            foreach (KeyValuePair<string, KeyValuePair<string, int>> marked in toneMarks) {
                int position = spelling.IndexOf(marked.Key, StringComparison.Ordinal);
                if (position < 0) {
                    continue;
                }
                if (toneSet || spelling.IndexOf(marked.Key, position + marked.Key.Length,
                    StringComparison.Ordinal) >= 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "普通话拼音存在多个声调标记");
                }
                spelling = spelling.Substring(0, position) + marked.Value.Key
                    + spelling.Substring(position + marked.Key.Length);
                tone = marked.Value.Value;
                toneSet = true;
            }
            KeyValuePair<string, int>[] digits = new KeyValuePair<string, int>[] {
                new KeyValuePair<string, int>("0", 0),
                new KeyValuePair<string, int>("1", 1),
                new KeyValuePair<string, int>("2", 2),
                new KeyValuePair<string, int>("3", 3),
                new KeyValuePair<string, int>("4", 4),
                new KeyValuePair<string, int>("０", 0),
                new KeyValuePair<string, int>("１", 1),
                new KeyValuePair<string, int>("２", 2),
                new KeyValuePair<string, int>("３", 3),
                new KeyValuePair<string, int>("４", 4),
            };
            foreach (KeyValuePair<string, int> digit in digits) {
                if (!spelling.EndsWith(digit.Key, StringComparison.Ordinal)) {
                    continue;
                }
                if (toneSet) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "普通话拼音存在多个声调标记");
                }
                spelling = spelling.Substring(0, spelling.Length - digit.Key.Length);
                tone = digit.Value;
                toneSet = true;
                break;
            }
            spelling = spelling.Replace("ü", "v").Replace("Ü", "v").ToLowerInvariant();
            if (spelling.Length == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "无效的普通话拼音拼写");
            }
            foreach (char c in spelling) {
                if (c < 'a' || c > 'z') {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "无效的普通话拼音拼写");
                }
            }
            if (!pinyinToPhonemes.TryGetValue(spelling, out List<string> phones)) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported,
                    "普通话拼音需要不可用的 CRF 回退：'" + spelling + "'");
            }
            string flag = tone.ToString();
            if (result.LanguageFlag.Length == 0) {
                result.LanguageFlag = flag;
            }
            result.Phonemes.AddRange(phones);
            for (int i = 0; i < phones.Count; i++) {
                result.PhonemeLanguageFlags.Add(flag);
            }
        }
    }
}
