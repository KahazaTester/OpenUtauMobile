using System;
using System.Collections.Generic;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 韩语前端，移植自原生 korean_frontend.cpp。
    /// 按配置的音系规则做连音与发音推导。
    /// </summary>
    public class TsnVoiceKoreanDictionary {
        class Syllable {
            public string Source = string.Empty;
            public string Initial = string.Empty;
            public string Medial = string.Empty;
            public List<string> Finals = new List<string>();
            public string Cluster = string.Empty;
        }

        Dictionary<string, string> initial = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> medial = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> final = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, List<string>> clusters =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Dictionary<string, int> clusterKeep =
            new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, string> neutral =
            new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> hAssimilation =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> nasalization =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> liquidization =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> initialAspiration =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> finalAspiration =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> fortification =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        Dictionary<string, KeyValuePair<string, string>> palatalization =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        HashSet<string> sMedials = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> macrons = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> vowels = new HashSet<string>(StringComparer.Ordinal);

        static readonly string[] HangulInitials = new string[] {
            "ㄱ", "ㄲ", "ㄴ", "ㄷ", "ㄸ", "ㄹ", "ㅁ", "ㅂ", "ㅃ", "ㅅ",
            "ㅆ", "ㅇ", "ㅈ", "ㅉ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
        };

        static readonly string[] HangulMedials = new string[] {
            "ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅘ",
            "ㅙ", "ㅚ", "ㅛ", "ㅜ", "ㅝ", "ㅞ", "ㅟ", "ㅠ", "ㅡ", "ㅢ", "ㅣ",
        };

        static readonly string[] HangulFinals = new string[] {
            "", "ㄱ", "ㄲ", "ㄳ", "ㄴ", "ㄵ", "ㄶ", "ㄷ", "ㄹ", "ㄺ",
            "ㄻ", "ㄼ", "ㄽ", "ㄾ", "ㄿ", "ㅀ", "ㅁ", "ㅂ", "ㅄ", "ㅅ",
            "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
        };

        public static TsnVoiceKoreanDictionary Load() {
            byte[] payload = TsnVoiceContainer.DecodeLegacyDictionaryBytes(
                TsnVoiceDictionaries.GetDictionaryBytes("ko_KR", "dict.bin"));
            TsnVoiceBinaryReader reader = new TsnVoiceBinaryReader(payload);
            byte[] version = reader.ReadBytes(4, "韩语词典版本");
            if (version[0] != 1 || version[1] != 0 || version[2] != 0 || version[3] != 0) {
                throw new TsnVoiceException(TsnVoiceStatus.Unsupported, "不支持的韩语词典版本");
            }
            Dictionary<string, string> config =
                TsnVoiceBinaryReader.ReadConfiguration(reader, "韩语词典配置");
            if (!reader.AtEnd) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "韩语词典尾部存在多余数据");
            }
            TsnVoiceKoreanDictionary result = new TsnVoiceKoreanDictionary();
            result.initial = ParseStringMap(Require(config, "INITIAL_PHONEMES"));
            result.medial = ParseStringMap(Require(config, "MEDIAL_PHONEMES"));
            result.final = ParseStringMap(Require(config, "FINAL_PHONEMES"));
            result.clusters = ParseClusterMap(Require(config, "CLUSTER_DECOMPOSITION"));
            foreach (string entry in SplitRule(Require(config, "CLUSTER_SOLO_KEEP"), ';')) {
                int colon = entry.IndexOf(':');
                result.clusterKeep[entry.Substring(0, colon).Trim()] =
                    int.Parse(entry.Substring(colon + 1).Trim());
            }
            result.neutral = ParseStringMap(Require(config, "FINAL_NEUTRALIZATION"));
            result.hAssimilation = ParsePairRules(Require(config, "H_ASSIMILATION_RULES"));
            result.nasalization = ParsePairRules(Require(config, "NASALIZATION_RULES"));
            result.liquidization = ParsePairRules(Require(config, "LIQUIDIZATION_RULES"));
            result.initialAspiration = ParsePairRules(Require(config, "INITIAL_ASPIRATION_RULES"));
            result.finalAspiration = ParsePairRules(Require(config, "FINAL_ASPIRATION_RULES"));
            result.fortification = ParsePairRules(Require(config, "FORTIFICATION_RULES"));
            result.palatalization = ParsePairRules(Require(config, "DT_PALATALIZATION_RULES"));
            foreach (string item in Require(config, "S_PALATALIZATION_MEDIALS").Split(',')) {
                result.sMedials.Add(item.Trim());
            }
            foreach (string item in Require(config, "MACRON").Split(',')) {
                if (item.Trim().Length > 0) {
                    result.macrons.Add(item.Trim());
                }
            }
            foreach (string item in Require(config, "VOWELS").Split(',')) {
                if (item.Trim().Length > 0) {
                    result.vowels.Add(item.Trim());
                }
            }
            return result;
        }

        static string Require(Dictionary<string, string> config, string key) {
            if (!config.TryGetValue(key, out string value)) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "韩语词典缺少 " + key);
            }
            return value;
        }

        static List<string> SplitRule(string value, char delimiter) {
            List<string> result = new List<string>();
            foreach (string item in value.Split(delimiter)) {
                string trimmed = item.Trim();
                if (trimmed.Length > 0) {
                    result.Add(trimmed);
                }
            }
            return result;
        }

        static Dictionary<string, string> ParseStringMap(string value) {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string entry in SplitRule(value, ';')) {
                int colon = entry.IndexOf(':');
                if (colon < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的韩语规则");
                }
                string key = entry.Substring(0, colon).Trim();
                string mapped = entry.Substring(colon + 1).Trim();
                if (key.Length == 0 || mapped.Length == 0 || result.ContainsKey(key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "重复的韩语规则");
                }
                result[key] = mapped;
            }
            return result;
        }

        static Dictionary<string, List<string>> ParseClusterMap(string value) {
            Dictionary<string, List<string>> result =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string entry in SplitRule(value, ';')) {
                int colon = entry.IndexOf(':');
                if (colon < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的韩语簇规则");
                }
                string key = entry.Substring(0, colon).Trim();
                List<string> items = SplitRule(entry.Substring(colon + 1), ',');
                if (items.Count != 2 || result.ContainsKey(key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的韩语簇规则");
                }
                result[key] = items;
            }
            return result;
        }

        static Dictionary<string, KeyValuePair<string, string>> ParsePairRules(string value) {
            Dictionary<string, KeyValuePair<string, string>> result =
                new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            foreach (string entry in SplitRule(value, ';')) {
                int colon = entry.IndexOf(':');
                if (colon < 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的韩语音系规则");
                }
                List<string> left = SplitRule(entry.Substring(0, colon), ',');
                List<string> right = SplitRule(entry.Substring(colon + 1), ',');
                if (left.Count != 2 || right.Count != 2) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "无效的韩语音系规则");
                }
                string key = left[0] + "\n" + left[1];
                KeyValuePair<string, string> mapped = new KeyValuePair<string, string>(
                    right[0] == "-" ? string.Empty : right[0],
                    right[1] == "-" ? string.Empty : right[1]);
                if (result.ContainsKey(key)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "重复的韩语音系规则");
                }
                result[key] = mapped;
            }
            return result;
        }

        bool ApplyRuleList(
            List<Dictionary<string, KeyValuePair<string, string>>> rules,
            Syllable previous, Syllable current) {
            if (previous.Finals.Count == 0 || current.Initial.Length == 0) {
                return false;
            }
            string neutralForm = previous.Finals[previous.Finals.Count - 1];
            if (neutral.TryGetValue(neutralForm, out string neutralized)) {
                neutralForm = neutralized;
            }
            foreach (Dictionary<string, KeyValuePair<string, string>> rule in rules) {
                if (!rule.TryGetValue(neutralForm + "\n" + current.Initial,
                    out KeyValuePair<string, string> mapped)) {
                    continue;
                }
                if (mapped.Key.Length == 0) {
                    previous.Finals.RemoveAt(previous.Finals.Count - 1);
                } else {
                    previous.Finals[previous.Finals.Count - 1] = mapped.Key;
                }
                current.Initial = mapped.Value;
                if (current.Medial == "ㅣ") {
                    if (current.Initial == "ㄷ") {
                        current.Initial = "ㅈ";
                    }
                    if (current.Initial == "ㅌ") {
                        current.Initial = "ㅊ";
                    }
                }
                return true;
            }
            return false;
        }

        bool ReduceCluster(Syllable syllable) {
            if (syllable.Cluster.Length == 0) {
                return false;
            }
            List<string> decomposed = clusters[syllable.Cluster];
            int keep = clusterKeep[syllable.Cluster];
            syllable.Finals.Clear();
            syllable.Finals.Add(decomposed[keep - 1]);
            syllable.Cluster = string.Empty;
            return true;
        }

        bool ApplyNonLiaison(Syllable previous, Syllable current) {
            List<Dictionary<string, KeyValuePair<string, string>>> all =
                new List<Dictionary<string, KeyValuePair<string, string>>> {
                    hAssimilation, nasalization, liquidization,
                    initialAspiration, finalAspiration, fortification,
                };
            List<Dictionary<string, KeyValuePair<string, string>>> firstThree =
                new List<Dictionary<string, KeyValuePair<string, string>>> {
                    hAssimilation, nasalization, liquidization,
                };
            if (previous.Finals.Count == 2) {
                if (ApplyRuleList(all, previous, current)) {
                    if (previous.Finals.Count == 2) {
                        ReduceCluster(previous);
                    }
                    return true;
                }
                ReduceCluster(previous);
                return ApplyRuleList(firstThree, previous, current);
            }
            if (ApplyRuleList(all, previous, current)) {
                return true;
            }
            return ApplyRuleList(firstThree, previous, current);
        }

        static List<string> SplitUtf8(string text) {
            List<string> result = new List<string>();
            int i = 0;
            while (i < text.Length) {
                // 按 UTF-16 代理对切分，覆盖 BMP 内谚文与全部单字。
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length
                    && char.IsLowSurrogate(text[i + 1])) {
                    result.Add(text.Substring(i, 2));
                    i += 2;
                } else {
                    result.Add(text.Substring(i, 1));
                    i += 1;
                }
            }
            return result;
        }

        static int CodePoint(string value) {
            return char.ConvertToUtf32(value, 0);
        }

        public TsnVoicePronunciation Lookup(string lyric) {
            List<Syllable> syllables = new List<Syllable>();
            List<int> boundaries = new List<int>();
            bool pendingBoundary = false;
            foreach (string character in SplitUtf8(lyric)) {
                if (character == "/") {
                    pendingBoundary = syllables.Count > 0;
                    continue;
                }
                if (macrons.Contains(character)) {
                    continue;
                }
                if (pendingBoundary) {
                    boundaries.Add(syllables.Count);
                    pendingBoundary = false;
                }
                int codepoint = CodePoint(character);
                if (codepoint >= 0xAC00 && codepoint <= 0xD7A3) {
                    int offset = codepoint - 0xAC00;
                    Syllable item = new Syllable();
                    item.Source = character;
                    item.Initial = HangulInitials[offset / 588];
                    item.Medial = HangulMedials[(offset % 588) / 28];
                    string finalForm = HangulFinals[offset % 28];
                    if (finalForm.Length > 0) {
                        if (clusters.TryGetValue(finalForm, out List<string> decomposed)) {
                            item.Finals.AddRange(decomposed);
                            item.Cluster = finalForm;
                        } else {
                            item.Finals.Add(finalForm);
                        }
                    }
                    if (item.Medial == "ㅢ" && item.Initial != "ㅇ") {
                        item.Medial = "ㅣ";
                    }
                    syllables.Add(item);
                } else {
                    bool hasInitial = initial.ContainsKey(character);
                    bool hasMedial = medial.ContainsKey(character);
                    if (!hasInitial && !hasMedial) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "韩语前端无法解析歌词");
                    }
                    Syllable item = new Syllable();
                    item.Source = character;
                    item.Initial = hasInitial ? character : string.Empty;
                    item.Medial = hasMedial ? character : string.Empty;
                    syllables.Add(item);
                }
            }
            if (syllables.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "韩语歌词为空");
            }
            boundaries.Add(syllables.Count);
            int begin = 0;
            foreach (int end in boundaries) {
                HashSet<int> liaisonDone = new HashSet<int>();
                for (int pass = 0; pass < 3; pass++) {
                    bool changed = false;
                    for (int index = begin; index + 1 < end; index++) {
                        Syllable previous = syllables[index];
                        Syllable current = syllables[index + 1];
                        if (current.Initial == "ㅇ" && previous.Finals.Count > 0
                            && previous.Finals[previous.Finals.Count - 1] != "ㅎ") {
                            string last = previous.Finals[previous.Finals.Count - 1];
                            previous.Finals.RemoveAt(previous.Finals.Count - 1);
                            if (last != "ㅇ") {
                                string key = last + "\nㅇ\n" + current.Medial;
                                if (!palatalization.TryGetValue(key,
                                    out KeyValuePair<string, string> mapped)) {
                                    current.Initial = last;
                                } else {
                                    if (mapped.Key.Length > 0) {
                                        previous.Finals.Add(mapped.Key);
                                    }
                                    current.Initial = mapped.Value;
                                }
                            }
                            liaisonDone.Add(index);
                            changed = true;
                            continue;
                        }
                        if (!liaisonDone.Contains(index)) {
                            changed |= ApplyNonLiaison(previous, current);
                        }
                    }
                    if (!changed) {
                        break;
                    }
                }
                begin = end;
            }
            TsnVoicePronunciation result = new TsnVoicePronunciation();
            result.Vowels.AddRange(vowels);
            foreach (Syllable item in syllables) {
                string phoneInitial = item.Initial.Length == 0
                    ? string.Empty
                    : initial[item.Initial];
                if (phoneInitial.Contains("/")) {
                    string[] alternatives = phoneInitial.Split('/');
                    phoneInitial = alternatives.Length > 1 && sMedials.Contains(item.Medial)
                        ? alternatives[1]
                        : alternatives[0];
                }
                if (phoneInitial.Length > 0 && phoneInitial != "-") {
                    result.Phonemes.Add(phoneInitial);
                }
                if (item.Medial.Length > 0) {
                    result.Phonemes.Add(medial[item.Medial]);
                }
                if (item.Finals.Count == 2) {
                    ReduceCluster(item);
                }
                foreach (string finalForm in item.Finals) {
                    string phone = final[finalForm];
                    if (phone != "-") {
                        result.Phonemes.Add(phone);
                    }
                }
            }
            if (result.Phonemes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "韩语歌词没有产生音素");
            }
            result.LanguageFlag = "0";
            return result;
        }
    }
}
