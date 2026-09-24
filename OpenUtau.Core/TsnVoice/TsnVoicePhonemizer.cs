using System;
using System.Collections.Generic;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 音素器基类：歌词经 TsnVoice 前端转写为 SINGER2 音素。
    /// 方括号注音（phoneticHint）直接作为指定音素，延续音符“-”原样透传，
    /// 由渲染器按延续规则处理（不跑 G2P、不新增辅音）。
    /// </summary>
    public abstract class TsnVoiceBasePhonemizer : Phonemizer {
        USinger singer;

        static readonly object dictLock = new object();
        static TsnVoiceJapaneseDictionary japanese;
        static TsnVoiceMandarinDictionary mandarinCn;
        static TsnVoiceMandarinDictionary mandarinTw;
        static TsnVoiceEnglishDictionary english;
        static TsnVoiceKoreanDictionary korean;

        /// <summary>音素器负责的引擎语言。</summary>
        protected abstract string EngineLanguage { get; }

        public override void SetSinger(USinger singer) {
            this.singer = singer;
        }

        protected virtual TsnVoicePronunciation LookupLyric(string lyric) {
            string language = EngineLanguage;
            lock (dictLock) {
                if (language == "ja_JP") {
                    if (japanese == null) {
                        japanese = TsnVoiceJapaneseDictionary.Load();
                    }
                    return japanese.Lookup(lyric);
                }
                if (language == "zh_CN") {
                    if (mandarinCn == null) {
                        mandarinCn = TsnVoiceMandarinDictionary.Load("zh_CN");
                    }
                    return mandarinCn.Lookup(lyric);
                }
                if (language == "zh_TW") {
                    if (mandarinTw == null) {
                        mandarinTw = TsnVoiceMandarinDictionary.Load("zh_TW");
                    }
                    return mandarinTw.Lookup(lyric);
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
                "音素器不支持语言 '" + language + "'");
        }

        public override Result Process(
            Note[] notes, Note? prev, Note? next,
            Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
            Note note = notes[0];
            List<Phoneme> phonemes = new List<Phoneme>();
            if (!string.IsNullOrEmpty(note.phoneticHint)) {
                // 方括号注音：空格分隔的指定音素序列。
                string[] symbols = note.phoneticHint.Split(
                    new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (symbols.Length == 0) {
                    symbols = new string[] { note.lyric };
                }
                phonemes.AddRange(Distribute(note, new List<string>(symbols)));
            } else if (note.lyric == TsnVoiceParameters.ContinuationLyric) {
                phonemes.Add(new Phoneme {
                    phoneme = TsnVoiceParameters.ContinuationLyric,
                    position = 0,
                });
            } else {
                try {
                    TsnVoicePronunciation pronunciation = LookupLyric(note.lyric);
                    phonemes.AddRange(Distribute(note, pronunciation.Phonemes));
                } catch (Exception e) {
                    Log.Error(e, "TsnVoice 转写失败：{Lyric}", note.lyric);
                    phonemes.Add(new Phoneme {
                        phoneme = note.lyric,
                        position = 0,
                        error = e,
                    });
                }
            }
            return new Result {
                phonemes = phonemes.ToArray(),
            };
        }

        /// <summary>
        /// 音素在音符内均匀分布；精确时值由渲染时的 HMM 时长模型决定。
        /// </summary>
        static List<Phoneme> Distribute(Note note, List<string> symbols) {
            List<Phoneme> result = new List<Phoneme>();
            for (int i = 0; i < symbols.Count; i++) {
                int position = symbols.Count <= 1
                    ? 0
                    : note.duration * i / symbols.Count;
                result.Add(new Phoneme {
                    phoneme = symbols[i],
                    position = position,
                });
            }
            return result;
        }
    }

    [Phonemizer("TsnVoice Japanese Phonemizer", "TSNVOICE JA", language: "JA")]
    public class TsnVoiceJapanesePhonemizer : TsnVoiceBasePhonemizer {
        protected override string EngineLanguage => "ja_JP";
    }

    [Phonemizer("TsnVoice Mandarin Phonemizer", "TSNVOICE ZH", language: "ZH")]
    public class TsnVoiceMandarinPhonemizer : TsnVoiceBasePhonemizer {
        protected override string EngineLanguage => "zh_CN";
    }

    [Phonemizer("TsnVoice Taiwanese Mandarin Phonemizer", "TSNVOICE ZH-TW", language: "ZH")]
    public class TsnVoiceTaiwanesePhonemizer : TsnVoiceBasePhonemizer {
        protected override string EngineLanguage => "zh_TW";
    }

    [Phonemizer("TsnVoice English Phonemizer", "TSNVOICE EN", language: "EN")]
    public class TsnVoiceEnglishPhonemizer : TsnVoiceBasePhonemizer {
        protected override string EngineLanguage => "en_US";
    }

    [Phonemizer("TsnVoice Korean Phonemizer", "TSNVOICE KO", language: "KO")]
    public class TsnVoiceKoreanPhonemizer : TsnVoiceBasePhonemizer {
        protected override string EngineLanguage => "ko_KR";
    }
}
