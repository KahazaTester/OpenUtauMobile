using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TsnVoice 渲染器：一等引擎，与 Vogen/DiffSinger 并列注册于 Renderers。
    /// 乐句内的相邻同语言音符连续合成；未手绘音高的音符使用模型自动 F0，
    /// 手绘音高的音符按绝对音高约束；ALP/HUS 自定义曲线参与声学条件；
    /// 音素编辑经固定音素传入。
    /// </summary>
    public class TsnVoiceRenderer : IRenderer {
        public USingerType SingerType => USingerType.TsnVoice;

        public bool SupportsRenderPitch => true;

        public bool SupportsExpression(UExpressionDescriptor descriptor) {
            return descriptor != null
                && (TsnVoiceParameters.SupportedExpressions.Contains(descriptor.abbr)
                    || TsnVoiceParameters.IsEmotionAbbr(descriptor.abbr));
        }

        public RenderResult Layout(RenderPhrase phrase) {
            // 音频内容始于首个有音素音符之前（保留模型静默边距，避免起音
            // 被淡入吃掉），槽位与其对齐，与管线 preutter/偏移无关，保证混音位置精确。
            double slotStartMs = phrase.positionMs;
            double tailMs = 0;
            if (phrase.phones.Length > 0) {
                int firstSung = phrase.phones[0].noteIndex;
                if (firstSung >= 0 && firstSung < phrase.notes.Length) {
                    slotStartMs = phrase.notes[firstSung].positionMs;
                }
            }
            if (phrase.singer is TsnVoiceSinger singer && !string.IsNullOrEmpty(singer.Location)) {
                try {
                    TsnVoiceLayoutMargins.Entry margins =
                        TsnVoiceLayoutMargins.Get(singer.Location);
                    slotStartMs -= margins.HeadMs;
                    tailMs = margins.TailMs;
                } catch {
                }
            }
            return new RenderResult() {
                leadingMs = phrase.positionMs - slotStartMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = phrase.durationMs + phrase.positionMs - slotStartMs + tailMs,
            };
        }

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress,
            int trackNo, CancellationTokenSource cancellation, bool isPreRender = false,
            RenderPhraseEvents? renderEvents = null) {
            Task<RenderResult> task = Task.Run(() => {
                RenderResult result = Layout(phrase);
                if (cancellation.IsCancellationRequested) {
                    return result;
                }
                    string wavPath = Path.Join(PathManager.Inst.CachePath,
                        $"tsn-{phrase.hash:x16}.wav");
                    string pitchPath = Path.Join(PathManager.Inst.CachePath,
                        $"tsn-{phrase.hash:x16}.pitch");
                    string phnPath = Path.Join(PathManager.Inst.CachePath,
                        $"tsn-{phrase.hash:x16}.phn");
                    phrase.AddCacheFile(wavPath);
                    phrase.AddCacheFile(pitchPath);
                    phrase.AddCacheFile(phnPath);
                    string progressInfo = $"Track {trackNo + 1}: {this} "
                        + $"\"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";
                    progress.Complete(0, progressInfo);
                    if (File.Exists(wavPath)) {
                        try {
                            using (WaveStream waveStream = Wave.OpenFile(wavPath)) {
                                result.samples = Wave.GetSamples(
                                    WaveExtensionMethods.ToSampleProvider(waveStream).ToMono(1, 0));
                            }
                        } catch (Exception e) {
                            Log.Warning(e, "读取 TsnVoice 缓存失败，重新渲染");
                            result.samples = null;
                        }
                    }
                    if (result.samples == null) {
                        try {
                            result.samples = InvokeTsnVoice(phrase, progress,
                                progressInfo, cancellation, pitchPath, phnPath,
                                out int reported);
                            progress.Complete(
                                Math.Max(0, phrase.phones.Length - reported),
                                progressInfo);
                        } catch (TsnVoiceException e) when (
                            e.Status == TsnVoiceStatus.Cancelled
                            || cancellation.IsCancellationRequested) {
                            return result;
                        } catch (OutOfMemoryException e) {
                            Log.Error(e, "TsnVoice 内存不足，已清理缓存");
                            TsnVoiceInference.DropCache();
                            GC.Collect();
                            throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                                "内存不足，语音过长或同时渲染过多，已清理缓存，请重试", e);
                        }
                        if (result.samples != null) {
                            try {
                                Wave.WriteMono16Wav(wavPath, result.samples);
                            } catch (Exception e) {
                                Log.Warning(e, "写入 TsnVoice 缓存失败");
                            }
                        }
                    }
                    if (result.samples != null) {
                        Renderers.ApplyDynamics(phrase, result);
                    }
                    return result;
            });
            return task;
        }

        float[] InvokeTsnVoice(RenderPhrase phrase, Progress progress,
            string progressInfo, CancellationTokenSource cancellation,
            string pitchPath, string phnPath, out int progressReported) {
            TsnVoiceSinger singer = phrase.singer as TsnVoiceSinger;
            if (singer == null) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "TsnVoice 渲染器需要 TsnVoice 歌手");
            }
            // 解析包与会话按语音缓存，同一语音串行，不同语音并行。
            TsnVoiceInference.TsnVoiceVoiceHandle handle =
                TsnVoiceInference.GetVoiceHandle(singer.Location);
            lock (handle.SyncRoot) {
                return InvokeTsnVoiceLocked(handle, singer, phrase, progress,
                    progressInfo, cancellation, pitchPath, phnPath, out progressReported);
            }
        }

        float[] InvokeTsnVoiceLocked(TsnVoiceInference.TsnVoiceVoiceHandle handle,
            TsnVoiceSinger singer, RenderPhrase phrase, Progress progress,
            string progressInfo, CancellationTokenSource cancellation,
            string pitchPath, string phnPath, out int progressReported) {
            TsnVoicePackage package = handle.Package;
            string primaryLanguage = singer.PrimaryLanguage();
            HashSet<string> supportedLanguages =
                new HashSet<string>(singer.Record.Languages.Split(
                    new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);
            double lastMs = phrase.notes[phrase.notes.Length - 1].endMs;
            Dictionary<int, List<RenderPhone>> phonesByNote = new Dictionary<int, List<RenderPhone>>();
            foreach (RenderPhone phone in phrase.phones) {
                if (!phonesByNote.TryGetValue(phone.noteIndex, out List<RenderPhone> group)) {
                    group = new List<RenderPhone>();
                    phonesByNote[phone.noteIndex] = group;
                }
                group.Add(phone);
            }
            // 仅有音素的音符才发声：乐句首尾吸入的相邻上下文音符
            // （其音素在相邻乐句）在此跳过，否则同一音符被两个乐句重复演唱，
            // 造成提前/交叠的随机感偏移。其它渲染器同样只按 phones 发声。
            List<int> sungIdx = new List<int>(phrase.notes.Length);
            for (int i = 0; i < phrase.notes.Length; i++) {
                if (phonesByNote.ContainsKey(i)) {
                    sungIdx.Add(i);
                }
            }
            double originMs = ComputeOriginMs(phrase, phonesByNote);
            // Per-note language for cross-lingual singing; continuations inherit.
            // 参考实现语言是逐音符属性（缺省主语言），此处以文字信号为首选、
            // 逐语言实际转写验证为准：罗马音等拉丁歌词在主语言转写失败时
            // 自动落到其它支持语言（如日语），全失败则回退主语言走逐音符记错。
            // The native engine requires one language per phrase, so mixed
            // languages are split into runs, synthesized separately, then joined.
            List<string> recordLanguages = new List<string>();
            foreach (string item in singer.Record.Languages.Split(
                new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                string trimmed = item.Trim();
                if (trimmed.Length > 0 && !recordLanguages.Contains(trimmed)) {
                    recordLanguages.Add(trimmed);
                }
            }
            List<string> noteLanguages = new List<string>(sungIdx.Count);
            bool[] autoNote = ComputeAutoEligibility(phrase);
            for (int s = 0; s < sungIdx.Count; s++) {
                string lyric = phrase.notes[sungIdx[s]].lyric ?? string.Empty;
                if (lyric == TsnVoiceParameters.ContinuationLyric && s > 0) {
                    noteLanguages.Add(noteLanguages[s - 1]);
                } else {
                    noteLanguages.Add(ResolveNoteLanguage(recordLanguages,
                        supportedLanguages, primaryLanguage, lyric));
                }
            }
            List<Tuple<int, int>> runs = new List<Tuple<int, int>>();
            {
                int runStart = 0;
                for (int s = 1; s <= sungIdx.Count; s++) {
                    if (s == sungIdx.Count || noteLanguages[s] != noteLanguages[s - 1]) {
                        runs.Add(Tuple.Create(runStart, s));
                        runStart = s;
                    }
                }
            }
            Log.Information(
                "TsnVoice render: {Voice} {Notes} notes {Phones} phones {Runs} runs {Langs}",
                singer.Record.Id, phrase.notes.Length, phrase.phones.Length,
                runs.Count, string.Join(",", noteLanguages.Distinct()));
            int lastProgress = 0;
            progressReported = 0;
            double totalMs = Math.Max(1.0, lastMs - originMs);
            TsnVoiceLayoutMargins.Entry margins = TsnVoiceLayoutMargins.Get(singer.Location);
            // 各 run 音频按绝对位置摆入整句 48k 缓冲（间隙补零），再整体重采样；
            // 直接拼接仅在 run 首尾相接时成立，跳过上下文音符后不再假设。
            // 内容含首尾静默边距（短音符不被淡入淡出吃掉），槽位已同步前移。
            double contentStartMs = phrase.notes[sungIdx[0]].positionMs - margins.HeadMs;
            double contentEndMs =
                phrase.notes[sungIdx[sungIdx.Count - 1]].extendedEndMs + margins.TailMs;
            int contentSamples48 = Math.Max(1, (int)Math.Round(
                (contentEndMs - contentStartMs) / 1000.0
                * TsnVoiceParameters.NativeSampleRate));
            float[] mixed48 = new float[contentSamples48];
            List<TsnVoiceOutputPitch> allPitches = new List<TsnVoiceOutputPitch>();
            List<TsnVoiceOutputPhoneme> allPhonemes = new List<TsnVoiceOutputPhoneme>();
            int completedRuns = 0;
            for (int runIndex = 0; runIndex < runs.Count; runIndex++) {
                Tuple<int, int> run = runs[runIndex];
                if (cancellation.IsCancellationRequested) {
                    break;
                }
                double runFirstMs = phrase.notes[sungIdx[run.Item1]].positionMs;
                double runEndMs = phrase.notes[sungIdx[run.Item2 - 1]].extendedEndMs;
                List<TsnVoiceInputNote> notes = BuildRunInputs(phrase, phonesByNote,
                    noteLanguages, sungIdx, run.Item1, run.Item2, originMs);
                List<TsnVoicePitchPoint> pitch = SamplePitch(
                    phrase, originMs, runFirstMs, runEndMs,
                    TsnVoiceParameters.IsAutoPitchEnabled(), autoNote);
                List<TsnVoiceControlPoint> controls = SampleControls(
                    phrase, originMs, runFirstMs, runEndMs);
                List<double[]> emotionWeights = SampleEmotionWeights(
                    package, phrase, notes, originMs);
                double runBase = (runFirstMs - originMs) / totalMs;
                double runSpan = Math.Max(0.0, runEndMs - runFirstMs) / totalMs;
                int capturedIndex = runIndex;
                int capturedCount = runs.Count;
                TsnVoiceSynthesisOutput output = TsnVoiceInference.Synthesize(
                    package, notes, pitch, controls,
                    () => cancellation.IsCancellationRequested,
                    (value, stage) => {
                        int current = Math.Clamp(
                            (int)((runBase + value * runSpan) * phrase.phones.Length),
                            0, phrase.phones.Length);
                        int delta = current - lastProgress;
                        lastProgress = current;
                        progress.Complete(delta, progressInfo + " " + stage
                            + (capturedCount > 1 ? " (" + (capturedIndex + 1) + "/"
                                + capturedCount + ")" : string.Empty));
                    },
                    null,
                    emotionWeights);
                allPitches.AddRange(output.Pitch);
                allPhonemes.AddRange(output.Phonemes);
                completedRuns++;
                int runOffset48 = (int)Math.Round((runFirstMs - contentStartMs)
                    / 1000.0 * TsnVoiceParameters.NativeSampleRate);
                // 超出乐谱跨度的 run 尾部不再截断：与参考实现一致，
                // 整段音频参与混音（短音符 padding 导致 overrun 时尤其重要，
                // 否则尾部被吃掉）；重叠区做加法混音，无重叠时与直接拷贝一致。
                int runSkip = 0;
                if (runOffset48 < 0) {
                    runSkip = Math.Min(-runOffset48, output.Samples.Length);
                    runOffset48 = 0;
                }
                int runCopy = output.Samples.Length - runSkip;
                if (runCopy <= 0) {
                    Log.Warning("TsnVoice run {Index}/{Total} 音频完全落在槽位之前，已跳过",
                        runIndex + 1, runs.Count);
                } else {
                    int need = runOffset48 + runCopy;
                    if (need > mixed48.Length) {
                        Log.Warning(
                            "TsnVoice run {Index}/{Total} 音频超出乐谱跨度 {Over:F1}ms，扩展混音缓冲",
                            runIndex + 1, runs.Count,
                            (need - mixed48.Length) * 1000.0
                                / TsnVoiceParameters.NativeSampleRate);
                        Array.Resize(ref mixed48, need);
                    }
                    for (int i = 0; i < runCopy; i++) {
                        mixed48[runOffset48 + i] += output.Samples[runSkip + i];
                    }
                }
                double runSpanMs = runEndMs - runFirstMs;
                double runAudioMs = output.Samples.Length * 1000.0
                    / TsnVoiceParameters.NativeSampleRate;
                Log.Information(
                    "TsnVoice run {Index}/{Total}: 首音符 {First}ms 尾音符 {End}ms 跨度 {Span:F1}ms 音频 {Audio:F1}ms 音高点 {Pitches}",
                    runIndex + 1, runs.Count, runFirstMs, runEndMs,
                    runSpanMs, runAudioMs, output.Pitch.Count);
            }
            progressReported = lastProgress;
            if (completedRuns == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "TsnVoice synthesis returned no audio");
            }
            TsnVoiceSynthesisOutput merged = new TsnVoiceSynthesisOutput();
            merged.SampleRate = TsnVoiceParameters.NativeSampleRate;
            merged.StartTime = 0;
            merged.Samples = mixed48;
            merged.Pitch = allPitches;
            merged.Phonemes = allPhonemes;
            SavePitchCache(phrase, pitchPath, merged, originMs);
            SavePhonemeCache(phrase, phnPath, merged, originMs);
            float[] samples = TsnVoiceDsp.ResampleLinear(merged.Samples,
                TsnVoiceParameters.NativeSampleRate, TsnVoiceParameters.MixSampleRate);
            int expectedSamples = Math.Max(1, (int)Math.Round(
                (contentEndMs - contentStartMs) / 1000.0 * TsnVoiceParameters.MixSampleRate));
            Log.Information(
                "TsnVoice 摆放自检：槽位 {Slot:F1}ms 内容 {Content:F1}ms 采样 {Samples}/{Expected} 音素 {Phones}",
                contentStartMs, contentEndMs - contentStartMs,
                samples.Length, expectedSamples, merged.Phonemes.Count);
            return samples;
        }

        /// <summary>
        /// 合成原点：乐句内所有音素实际起始的最小值，仅用作 run 间公共时间基；
        /// 混音槽位由 Layout 按首个发声音符对齐，与此处取值无关。
        /// </summary>
        static double ComputeOriginMs(RenderPhrase phrase,
            Dictionary<int, List<RenderPhone>> phonesByNote) {
            double originMs = double.PositiveInfinity;
            foreach (List<RenderPhone> group in phonesByNote.Values) {
                foreach (RenderPhone phone in group) {
                    double start = phone.positionMs - phone.leadingMs;
                    if (start < originMs) {
                        originMs = start;
                    }
                }
            }
            if (double.IsPositiveInfinity(originMs)) {
                originMs = phrase.notes[0].positionMs;
            }
            return originMs;
        }

        List<TsnVoiceInputNote> BuildRunInputs(RenderPhrase phrase,
            Dictionary<int, List<RenderPhone>> phonesByNote, List<string> noteLanguages,
            List<int> sungIdx, int runStart, int runEndExclusive, double originMs) {
            List<TsnVoiceInputNote> notes = new List<TsnVoiceInputNote>();
            for (int s = runStart; s < runEndExclusive; s++) {
                int i = sungIdx[s];
                RenderNote note = phrase.notes[i];
                string language = noteLanguages[s];
                TsnVoiceInputNote input = new TsnVoiceInputNote();
                input.Id = "n" + i;
                input.StartSeconds = Math.Max(0, (note.positionMs - originMs) / 1000.0);
                input.EndSeconds = Math.Max(input.StartSeconds + 0.001,
                    (note.extendedEndMs - originMs) / 1000.0);
                input.MidiPitch = note.tone;
                // 基音取含 tuning 的有效音高（与乐句音高一致），标签与校验仍用整数 tone。
                input.BaseMidiPitch = Math.Clamp((double)note.adjustedTone, 0.0, 127.0);
                input.Lyric = string.IsNullOrEmpty(note.lyric)
                    ? TsnVoiceParameters.DefaultLyric(language)
                    : note.lyric;
                input.Language = language;
                phonesByNote.TryGetValue(i, out List<RenderPhone> group);
                bool isDash = input.Lyric == TsnVoiceParameters.ContinuationLyric;
                // 用户在延续符上写方括号注音视为固定音素覆盖，不再按延续处理。
                bool dashPinnedOverride = isDash && group != null
                    && !(group.Count == 1 && group[0].phoneme == "-");
                bool adjacent = s == runStart
                    || phrase.notes[sungIdx[s - 1]].extendedEndMs + 1.0
                        >= note.positionMs;
                input.IsContinuation = isDash && !dashPinnedOverride
                    && s > runStart && adjacent;
                if (input.IsContinuation) {
                    // 延续音跟随自身调号：同调时与旧行为一致，
                    // 跨调 - /+ 滑向新调；发音与上下文仍归属前一实质音符。
                    input.MidiPitch = note.tone;
                    input.BaseMidiPitch = Math.Clamp((double)note.adjustedTone, 0.0, 127.0);
                }
                if (isDash && !input.IsContinuation && !dashPinnedOverride) {
                    // 句首或断开的延续符无法归属前一发音：记错并以静音占据，
                    // 整句其余音符照常渲染。
                    Log.Error("TsnVoice 延续音符 {Id} 缺少前置发音，该音符静音",
                        input.Id);
                    TsnVoiceInputPhoneme lone = new TsnVoiceInputPhoneme();
                    lone.Symbol = "sil";
                    lone.DurationSeconds = Math.Max(
                        input.EndSeconds - input.StartSeconds, 0.001);
                    lone.StretchWeight = 1.0;
                    input.Phonemes.Add(lone);
                    notes.Add(input);
                    continue;
                }
                string restPhoneme = input.IsContinuation
                    ? null
                    : TsnVoiceParameters.RestPhoneme(input.Lyric);
                if (restPhoneme != null) {
                    // 休止与换气不跑 G2P，直接以静音占据此时值。
                    TsnVoiceInputPhoneme rest = new TsnVoiceInputPhoneme();
                    rest.Symbol = restPhoneme;
                    rest.DurationSeconds = Math.Max(
                        input.EndSeconds - input.StartSeconds, 0.001);
                    rest.StretchWeight = 1.0;
                    input.Phonemes.Add(rest);
                } else if (!input.IsContinuation
                    && group != null) {
                    bool pinned = true;
                    foreach (RenderPhone phone in group) {
                        if (!TsnVoiceParameters.IsPhonemeSymbol(phone.phoneme)) {
                            pinned = false;
                            break;
                        }
                    }
                    bool useFrontend = false;
                    if (pinned && !dashPinnedOverride && !isDash) {
                        // 对照原生 prepare_note：仅用户固定才重定时。
                        // 管道音素与前端转写一致视为未固定，走 HTS 时长模型；
                        // 方括号改写等才固定时长；转写失败记错并以静音占据，
                        // 整句其余音符照常渲染。
                        try {
                            TsnVoicePronunciation fresh =
                                TsnVoiceFrontend.Pronounce(language, input.Lyric);
                            if (PhonemeListsEqual(fresh.Phonemes, group)) {
                                useFrontend = true;
                                pinned = false;
                            } else if (group.Count == 1
                                && group[0].phoneme == input.Lyric) {
                                // 单音素原文回传是他语言音素器失败的残留，并非用户注音；
                                // 本语言转写已成功，同样走前端非固定路径。
                                useFrontend = true;
                                pinned = false;
                            }
                        } catch (TsnVoiceException e) when (
                            e.Status == TsnVoiceStatus.InvalidArgument
                            || e.Status == TsnVoiceStatus.Unsupported) {
                            Log.Error(e, "TsnVoice 音符 {Id} 歌词 {Lyric} 无法转写，该音符静音",
                                input.Id, input.Lyric);
                            pinned = false;
                        }
                    }
                    if (useFrontend) {
                        // 留空交回推理内 G2P：HTS 决定辅音时值，元音推导前置。
                    } else if (!pinned) {
                        // Symbols with non-ASCII text are usually failed phonemizer
                        // fallbacks holding the raw lyric; let the inference
                        // frontend for this language transcribe them instead.
                        Log.Warning("TsnVoice note {Lyric} has invalid pins, using {Language} frontend",
                            input.Lyric, language);
                    } else {
                        foreach (RenderPhone phone in group) {
                            TsnVoiceInputPhoneme pinnedPhone = new TsnVoiceInputPhoneme();
                            pinnedPhone.Symbol = phone.phoneme;
                            // 固定时长取音素完整跨度（含前置），与起始原点对齐；
                            // 保持比例，由时长规划缩放到绝对帧位。
                            pinnedPhone.DurationSeconds = Math.Max(
                                phone.leadingMs + phone.durationMs, 1.0) / 1000.0;
                            pinnedPhone.StretchWeight = Math.Pow(2.0, 1.0 - phone.velocity / 100.0);
                            input.Phonemes.Add(pinnedPhone);
                        }
                        int leading = 0;
                        foreach (RenderPhone phone in group) {
                            if (phone.leading > 0) {
                                leading++;
                            } else {
                                break;
                            }
                        }
                        input.LeadingPhonemeCount = Math.Min(leading, input.Phonemes.Count);
                    }
                }
                notes.Add(input);
            }
            return notes;
        }
        static string ResolveNoteLanguage(List<string> recordLanguages,
            HashSet<string> supportedLanguages, string primaryLanguage, string lyric) {
            string hint = TsnVoiceParameters.DetectNoteLanguage(
                supportedLanguages, primaryLanguage, lyric);
            List<string> candidates = new List<string>();
            candidates.Add(hint);
            candidates.Add(primaryLanguage);
            candidates.AddRange(recordLanguages);
            HashSet<string> tried = new HashSet<string>(StringComparer.Ordinal);
            foreach (string language in candidates) {
                if (!supportedLanguages.Contains(language) || !tried.Add(language)) {
                    continue;
                }
                try {
                    TsnVoiceFrontend.Pronounce(language, lyric);
                    return language;
                } catch (TsnVoiceException e) when (
                    e.Status == TsnVoiceStatus.InvalidArgument
                    || e.Status == TsnVoiceStatus.Unsupported) {
                    continue;
                }
            }
            return primaryLanguage;
        }

        static bool PhonemeListsEqual(List<string> fresh, List<RenderPhone> group) {
            if (fresh.Count != group.Count) {
                return false;
            }
            for (int i = 0; i < fresh.Count; i++) {
                if (!string.Equals(fresh[i], group[i].phoneme, StringComparison.Ordinal)) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 自动音高资格（逐音符预计算）：本节新建/重置标记，且无手调音高点、
        /// 颤音，且跨度内无 PITD 数据。烘焙与手调 PITD 均使该跨度转手动，
        /// 模型 F0 永不覆盖既有调音；重置时由命令同步清空跨度 PITD。
        /// </summary>
        static bool[] ComputeAutoEligibility(RenderPhrase phrase) {
            bool[] result = new bool[phrase.notes.Length];
            float[] pitd = FindCurve(phrase.curves, "pitd");
            for (int i = 0; i < result.Length; i++) {
                RenderNote note = phrase.notes[i];
                result[i] = note.tsnAutoPitch && !note.hasManualPitch
                    && !SpanHasPitd(phrase, pitd, note.positionMs, note.extendedEndMs);
            }
            return result;
        }

        /// <summary>跨度内是否存在 PITD 数据（采样数组精确整数比对）。</summary>
        public static bool SpanHasPitd(RenderPhrase phrase, float[] pitdCurve,
            double startMs, double endMs) {
            if (pitdCurve == null || pitdCurve.Length == 0) {
                return false;
            }
            const int pitchInterval = 5;
            int baseTick = phrase.position - phrase.leading;
            int startIdx = (phrase.timeAxis.MsPosToTickPos(startMs) - baseTick)
                / pitchInterval + 1;
            int endIdx = (phrase.timeAxis.MsPosToTickPos(endMs) - baseTick)
                / pitchInterval;
            if (endIdx < 0 || startIdx >= pitdCurve.Length) {
                return false;
            }
            startIdx = Math.Max(0, startIdx);
            endIdx = Math.Min(pitdCurve.Length, endIdx + 1);
            for (int i = startIdx; i < endIdx; i++) {
                if (pitdCurve[i] != 0f) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 帧归属音符（按时间，不回溯）：延续/ sustain 帧归属自身音符，
        /// 各自调号决定音高，跨调 - /+ 滑向新调而非钉在旧调。
        /// </summary>
        static int NoteIndexAt(RenderPhrase phrase, double ms) {
            int owner = phrase.notes.Length - 1;
            for (int i = 0; i < phrase.notes.Length; i++) {
                if (phrase.notes[i].positionMs <= ms) {
                    owner = i;
                } else {
                    break;
                }
            }
            return owner;
        }

        /// <summary>
        /// 自动资格回溯：延续音符与 sustain 延长沿用前一实质音符的
        ///新建/手调标记（自身不带标记时），仅决定是否自动，不决定音高。
        /// </summary>
        static int AutoRefIndexAt(RenderPhrase phrase, int owner) {
            while (owner > 0
                && (phrase.notes[owner].lyric == TsnVoiceParameters.ContinuationLyric
                    || phrase.notes[owner].lyric.StartsWith("+"))) {
                owner--;
            }
            return owner;
        }

        static List<TsnVoicePitchPoint> SamplePitch(RenderPhrase phrase,
            double baseMs, double startMs, double endMs,
            bool autoEnabled, bool[] autoNote) {
            const int pitchInterval = 5;
            List<TsnVoicePitchPoint> result = new List<TsnVoicePitchPoint>();
            for (double ms = startMs; ms <= endMs + 0.001;
                ms += TsnVoiceParameters.PitchSampleStepSeconds * 1000.0) {
                int ticks = phrase.timeAxis.MsPosToTickPos(ms)
                    - (phrase.position - phrase.leading);
                int index = Math.Clamp(ticks / pitchInterval, 0, phrase.pitches.Length - 1);
                int ownerIndex = NoteIndexAt(phrase, ms);
                RenderNote owner = phrase.notes[ownerIndex];
                // 资格向上继承，音高取当前音符自身调号。
                bool auto = autoEnabled && autoNote[AutoRefIndexAt(phrase, ownerIndex)];
                double midi;
                if (!auto) {
                    // 手绘/既有音高：整体按绝对音高约束，原样保留调音。
                    midi = phrase.pitches[index] * 0.01;
                } else {
                    // 自动音高：含 tuning 的基音，颤音与表情由模型生成；
                    // 不叠加任何既有 PITD，避免在已建立音高上重复应用。
                    midi = owner.adjustedTone;
                }
                TsnVoicePitchPoint point = new TsnVoicePitchPoint();
                point.TimeSeconds = Math.Max(0, (ms - baseMs) / 1000.0);
                point.MidiPitch = Math.Clamp(midi, 0.0, 127.0);
                point.IsAbsolute = !auto;
                result.Add(point);
            }
            return result;
        }

        static float[] FindCurve(Tuple<string, float[]>[] curves, string abbr) {
            foreach (Tuple<string, float[]> curve in curves) {
                if (curve.Item1 == abbr) {
                    return curve.Item2;
                }
            }
            return null;
        }

        /// <summary>
        /// 表情混合权重采样：每音符在其起始处取值（XSY 式逐音符表情），
        /// 合成时按音素归属逐帧混合；无 EMO 曲线时返回首行权重。
        /// </summary>
        static List<double[]> SampleEmotionWeights(TsnVoicePackage package,
            RenderPhrase phrase, List<TsnVoiceInputNote> notes, double originMs) {
            int count;
            try {
                count = TsnVoiceInference.EmotionRowCount(package.Config);
            } catch (Exception e) {
                Log.Warning(e, "读取 TsnVoice 表情行数失败，使用缺省权重");
                return null;
            }
            if (count <= 1) {
                return null;
            }
            List<double[]> result = new List<double[]>(notes.Count);
            const int pitchInterval = 5;
            foreach (TsnVoiceInputNote note in notes) {
                double[] raw = new double[count];
                raw[0] = 1.0;
                if (phrase.curves != null) {
                    double ms = originMs + note.StartSeconds * 1000.0;
                    int ticks = phrase.timeAxis.MsPosToTickPos(ms)
                        - (phrase.position - phrase.leading);
                    int index = ticks / pitchInterval;
                    for (int i = 0; i < count; i++) {
                        float[] curve = FindCurve(phrase.curves, "emo" + (i + 1));
                        if (curve != null && curve.Length > 0) {
                            raw[i] = curve[Math.Clamp(index, 0, curve.Length - 1)];
                        } else if (i > 0) {
                            raw[i] = 0.0;
                        }
                    }
                }
                result.Add(raw);
            }
            return result;
        }

        static List<TsnVoiceControlPoint> SampleControls(RenderPhrase phrase,
            double baseMs, double startMs, double endMs) {
            const int pitchInterval = 5;
            float[] alpCurve = null;
            float[] husCurve = null;
            if (phrase.curves != null) {
                alpCurve = FindCurve(phrase.curves, "alp");
                husCurve = FindCurve(phrase.curves, "hus");
            }
            List<TsnVoiceControlPoint> result = new List<TsnVoiceControlPoint>();
            for (double ms = startMs; ms <= endMs + 0.001;
                ms += TsnVoiceParameters.PitchSampleStepSeconds * 1000.0) {
                int ticks = phrase.timeAxis.MsPosToTickPos(ms)
                    - (phrase.position - phrase.leading);
                int index = ticks / pitchInterval;
                double alp = 0;
                double hus = 0;
                if (alpCurve != null && alpCurve.Length > 0) {
                    alp = alpCurve[Math.Clamp(index, 0, alpCurve.Length - 1)];
                }
                if (husCurve != null && husCurve.Length > 0) {
                    hus = husCurve[Math.Clamp(index, 0, husCurve.Length - 1)];
                }
                TsnVoiceControlPoint point = new TsnVoiceControlPoint();
                point.TimeSeconds = Math.Max(0, (ms - baseMs) / 1000.0);
                point.Alpha = TsnVoiceParameters.ToNativeAlpha(alp);
                point.Huskiness = TsnVoiceParameters.ToNativeHuskiness(hus);
                result.Add(point);
            }
            return result;
        }

        /// <summary>
        /// 合成音素定时（对照原生 PublishBatch 的 SynthesizedSyllable）：
        /// 供音素面板叠加显示模型实际时值，缓存键与音频/音高一致。
        /// </summary>
        public class TsnVoiceRenderedPhone {
            public int NoteIndex;
            public string Symbol = string.Empty;
            public float StartTick;
            public float EndTick;
            public bool IsLeading;
            public float BodyTick;
        }

        static readonly object phoneCacheLock = new object();
        static readonly Dictionary<ulong, Tuple<long, List<TsnVoiceRenderedPhone>>> phoneCache =
            new Dictionary<ulong, Tuple<long, List<TsnVoiceRenderedPhone>>>();

        static void SavePhonemeCache(RenderPhrase phrase, string phnPath,
            TsnVoiceSynthesisOutput output, double originMs) {
            try {
                using (FileStream stream = new FileStream(phnPath, FileMode.Create,
                    FileAccess.Write)) {
                    using (BinaryWriter writer = new BinaryWriter(stream)) {
                        List<TsnVoiceOutputPhoneme> phones = new List<TsnVoiceOutputPhoneme>();
                        foreach (TsnVoiceOutputPhoneme phone in output.Phonemes) {
                            if (phone.NoteId.Length > 1 && phone.NoteId[0] == 'n'
                                && int.TryParse(phone.NoteId.Substring(1),
                                    out int noteIndex)
                                && noteIndex >= 0
                                && noteIndex < phrase.notes.Length) {
                                phones.Add(phone);
                            }
                        }
                        writer.Write(phones.Count);
                        foreach (TsnVoiceOutputPhoneme phone in phones) {
                            int noteIndex = int.Parse(phone.NoteId.Substring(1));
                            double startMs = originMs + phone.StartSeconds * 1000.0;
                            double endMs = startMs + phone.DurationSeconds * 1000.0;
                            double bodyMs = phrase.notes[noteIndex].positionMs
                                + phone.BodyOffsetSeconds * 1000.0;
                            writer.Write(noteIndex);
                            writer.Write(phone.Symbol ?? string.Empty);
                            writer.Write(startMs);
                            writer.Write(endMs);
                            writer.Write(phone.IsLeading);
                            writer.Write(bodyMs);
                        }
                    }
                }
            } catch (Exception e) {
                Log.Warning(e, "写入 TsnVoice 音素缓存失败");
            }
        }

        /// <summary>
        /// 读取合成音素定时（乐句相对 Tick），无缓存返回空表。
        /// </summary>
        public static List<TsnVoiceRenderedPhone> LoadRenderedPhonemes(RenderPhrase phrase) {
            string phnPath = Path.Join(PathManager.Inst.CachePath,
                $"tsn-{phrase.hash:x16}.phn");
            if (!File.Exists(phnPath)) {
                return new List<TsnVoiceRenderedPhone>();
            }
            try {
                long mtime = new FileInfo(phnPath).LastWriteTimeUtc.Ticks;
                lock (phoneCacheLock) {
                    if (phoneCache.TryGetValue(phrase.hash, out var cached)
                        && cached.Item1 == mtime) {
                        return cached.Item2;
                    }
                }
                List<TsnVoiceRenderedPhone> result = new List<TsnVoiceRenderedPhone>();
                using (FileStream stream = new FileStream(phnPath, FileMode.Open,
                    FileAccess.Read)) {
                    using (BinaryReader reader = new BinaryReader(stream)) {
                        int count = reader.ReadInt32();
                        if (count < 0 || count > 100000) {
                            return result;
                        }
                        for (int i = 0; i < count; i++) {
                            TsnVoiceRenderedPhone phone = new TsnVoiceRenderedPhone();
                            phone.NoteIndex = reader.ReadInt32();
                            phone.Symbol = reader.ReadString();
                            double startMs = reader.ReadDouble();
                            double endMs = reader.ReadDouble();
                            phone.IsLeading = reader.ReadBoolean();
                            double bodyMs = reader.ReadDouble();
                            phone.StartTick = phrase.timeAxis.MsPosToTickPos(startMs)
                                - phrase.position;
                            phone.EndTick = phrase.timeAxis.MsPosToTickPos(endMs)
                                - phrase.position;
                            phone.BodyTick = phrase.timeAxis.MsPosToTickPos(bodyMs)
                                - phrase.position;
                            result.Add(phone);
                        }
                    }
                }
                lock (phoneCacheLock) {
                    phoneCache[phrase.hash] = Tuple.Create(mtime, result);
                    while (phoneCache.Count > 8) {
                        foreach (ulong key in new List<ulong>(phoneCache.Keys)) {
                            if (key != phrase.hash) {
                                phoneCache.Remove(key);
                                break;
                            }
                        }
                    }
                }
                return result;
            } catch (Exception e) {
                Log.Warning(e, "读取 TsnVoice 音素缓存失败");
                return new List<TsnVoiceRenderedPhone>();
            }
        }

        static void SavePitchCache(RenderPhrase phrase, string pitchPath,
            TsnVoiceSynthesisOutput output, double originMs) {
            try {
                using (FileStream stream = new FileStream(pitchPath, FileMode.Create,
                    FileAccess.Write)) {
                    using (BinaryWriter writer = new BinaryWriter(stream)) {
                        writer.Write(output.Pitch.Count);
                        foreach (TsnVoiceOutputPitch point in output.Pitch) {
                            double ms = originMs + point.TimeSeconds * 1000.0;
                            float tick = phrase.timeAxis.MsPosToTickPos(ms)
                                - phrase.position;
                            writer.Write(tick);
                            // RenderPitchResult.tones 为 MIDI 半音，调用端自行换算为音分。
                            writer.Write((float)point.MidiPitch);
                        }
                    }
                }
                if (output.Pitch.Count == 0) {
                    Log.Warning("TsnVoice 未产生有效音高点，自动音高曲线为空");
                } else {
                    Log.Information("TsnVoice 音高缓存：{Count} 个点", output.Pitch.Count);
                }
            } catch (Exception e) {
                Log.Warning(e, "写入 TsnVoice 音高缓存失败");
            }
        }

        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            string pitchPath = Path.Join(PathManager.Inst.CachePath,
                $"tsn-{phrase.hash:x16}.pitch");
            if (!File.Exists(pitchPath)) {
                return null;
            }
            try {
                using (FileStream stream = new FileStream(pitchPath, FileMode.Open,
                    FileAccess.Read)) {
                    using (BinaryReader reader = new BinaryReader(stream)) {
                        int count = reader.ReadInt32();
                        if (count <= 0 || count > 360000) {
                            return null;
                        }
                        float[] ticks = new float[count];
                        float[] tones = new float[count];
                        for (int i = 0; i < count; i++) {
                            ticks[i] = reader.ReadSingle();
                            tones[i] = reader.ReadSingle();
                        }
                        return new RenderPitchResult {
                            ticks = ticks,
                            tones = tones,
                        };
                    }
                }
            } catch (Exception e) {
                Log.Warning(e, "读取 TsnVoice 音高缓存失败");
                return null;
            }
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer,
            URenderSettings renderSettings) {
            // 表情行数随语音：多表情语音追加 EMO1..N；
            // 解析失败时仅返回 ALP/HUS，不阻塞。
            if (singer is TsnVoiceSinger tsnSinger
                && !string.IsNullOrEmpty(tsnSinger.Location)) {
                try {
                    TsnVoicePackage package =
                        TsnVoicePackage.Load(tsnSinger.Location);
                    int count = TsnVoiceInference.EmotionRowCount(package.Config);
                    if (count > 1) {
                        return TsnVoiceParameters.BuildSuggestedExpressions(
                            count, TsnVoiceEmotions.FindEmotions(tsnSinger.Location));
                    }
                } catch (Exception e) {
                    Log.Warning(e, "读取 TsnVoice 表情行数失败，仅建议 ALP/HUS");
                }
            }
            return TsnVoiceParameters.BuildSuggestedExpressions();
        }

        public override string ToString() => Renderers.TSNVOICE;
    }
}
