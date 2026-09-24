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
                && TsnVoiceParameters.SupportedExpressions.Contains(descriptor.abbr);
        }

        public RenderResult Layout(RenderPhrase phrase) {
            return new RenderResult() {
                leadingMs = phrase.leadingMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = phrase.durationMs + phrase.leadingMs,
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
                    phrase.AddCacheFile(wavPath);
                    phrase.AddCacheFile(pitchPath);
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
                                progressInfo, cancellation, pitchPath,
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
            string pitchPath, out int progressReported) {
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
                    progressInfo, cancellation, pitchPath, out progressReported);
            }
        }

        float[] InvokeTsnVoiceLocked(TsnVoiceInference.TsnVoiceVoiceHandle handle,
            TsnVoiceSinger singer, RenderPhrase phrase, Progress progress,
            string progressInfo, CancellationTokenSource cancellation,
            string pitchPath, out int progressReported) {
            TsnVoicePackage package = handle.Package;
            string primaryLanguage = singer.PrimaryLanguage();
            HashSet<string> supportedLanguages =
                new HashSet<string>(singer.Record.Languages.Split(
                    new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);
            double lastMs = phrase.notes[phrase.notes.Length - 1].endMs;
            Dictionary<int, List<RenderPhone>> phonesByNote =                new Dictionary<int, List<RenderPhone>>();
            foreach (RenderPhone phone in phrase.phones) {
                if (!phonesByNote.TryGetValue(phone.noteIndex, out List<RenderPhone> group)) {
                    group = new List<RenderPhone>();
                    phonesByNote[phone.noteIndex] = group;
                }
                group.Add(phone);
            }
            double originMs = ComputeOriginMs(phrase, phonesByNote);
            // Per-note language for cross-lingual singing; continuations inherit.            // The native engine requires one language per phrase, so mixed
            // languages are split into runs, synthesized separately, then joined.
            List<string> noteLanguages = new List<string>(phrase.notes.Length);
            for (int i = 0; i < phrase.notes.Length; i++) {
                string lyric = phrase.notes[i].lyric ?? string.Empty;
                if (lyric == TsnVoiceParameters.ContinuationLyric && i > 0) {
                    noteLanguages.Add(noteLanguages[i - 1]);
                } else {
                    noteLanguages.Add(TsnVoiceParameters.DetectNoteLanguage(
                        supportedLanguages, primaryLanguage, lyric));
                }
            }
            List<Tuple<int, int>> runs = new List<Tuple<int, int>>();
            {
                int runStart = 0;
                for (int i = 1; i <= phrase.notes.Length; i++) {
                    if (i == phrase.notes.Length || noteLanguages[i] != noteLanguages[i - 1]) {
                        runs.Add(Tuple.Create(runStart, i));
                        runStart = i;
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
            List<float> allSamples = new List<float>();
            List<TsnVoiceOutputPitch> allPitches = new List<TsnVoiceOutputPitch>();
            for (int runIndex = 0; runIndex < runs.Count; runIndex++) {
                Tuple<int, int> run = runs[runIndex];
                if (cancellation.IsCancellationRequested) {
                    break;
                }
                double runFirstMs = phrase.notes[run.Item1].positionMs;
                double runEndMs = phrase.notes[run.Item2 - 1].endMs;
                List<TsnVoiceInputNote> notes = BuildRunInputs(phrase, phonesByNote,
                    noteLanguages, run.Item1, run.Item2, originMs);
                List<TsnVoicePitchPoint> pitch = SamplePitch(
                    phrase, originMs, runFirstMs, runEndMs);
                List<TsnVoiceControlPoint> controls = SampleControls(
                    phrase, originMs, runFirstMs, runEndMs);
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
                    null);
                allSamples.AddRange(output.Samples);
                allPitches.AddRange(output.Pitch);
            }
            progressReported = lastProgress;
            if (allSamples.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "TsnVoice synthesis returned no audio");
            }
            TsnVoiceSynthesisOutput merged = new TsnVoiceSynthesisOutput();
            merged.SampleRate = TsnVoiceParameters.NativeSampleRate;
            merged.StartTime = 0;
            merged.Samples = allSamples.ToArray();
            merged.Pitch = allPitches;
            SavePitchCache(phrase, pitchPath, merged, originMs);
            float[] samples = TsnVoiceDsp.ResampleLinear(merged.Samples,
                TsnVoiceParameters.NativeSampleRate, TsnVoiceParameters.MixSampleRate);
            return samples;
        }

        /// <summary>
        /// 合成原点：首音符首个音素的实际起始（位置减去前置）。
        /// 输出首采样对准该时刻，与混音槽位（positionMs - leadingMs）一致。
        /// </summary>
        static double ComputeOriginMs(RenderPhrase phrase,
            Dictionary<int, List<RenderPhone>> phonesByNote) {
            double originMs = phrase.notes[0].positionMs;
            if (phonesByNote.TryGetValue(0, out List<RenderPhone> group)) {
                foreach (RenderPhone phone in group) {
                    double start = phone.positionMs - phone.leadingMs;
                    if (start < originMs) {
                        originMs = start;
                    }
                }
            }
            return originMs;
        }

        List<TsnVoiceInputNote> BuildRunInputs(RenderPhrase phrase,
            Dictionary<int, List<RenderPhone>> phonesByNote, List<string> noteLanguages,
            int runStart, int runEndExclusive, double originMs) {
            List<TsnVoiceInputNote> notes = new List<TsnVoiceInputNote>();
            int prevTone = phrase.notes[runStart].tone;
            for (int i = runStart; i < runEndExclusive; i++) {
                RenderNote note = phrase.notes[i];
                string language = noteLanguages[i];
                TsnVoiceInputNote input = new TsnVoiceInputNote();
                input.Id = "n" + i;
                input.StartSeconds = Math.Max(0, (note.positionMs - originMs) / 1000.0);
                input.EndSeconds = Math.Max(input.StartSeconds + 0.001,
                    (note.endMs - originMs) / 1000.0);
                input.MidiPitch = note.tone;
                input.Lyric = string.IsNullOrEmpty(note.lyric)
                    ? TsnVoiceParameters.DefaultLyric(language)
                    : note.lyric;
                input.Language = language;
                input.IsContinuation =
                    input.Lyric == TsnVoiceParameters.ContinuationLyric && i > runStart;
                if (input.IsContinuation) {
                    // 延续音沿用前一实质音符的基音，保持短语内音高上下文连续。
                    input.MidiPitch = prevTone;
                } else {
                    prevTone = note.tone;
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
                    && phonesByNote.TryGetValue(i, out List<RenderPhone> group)) {
                    bool pinned = true;
                    foreach (RenderPhone phone in group) {
                        if (!TsnVoiceParameters.IsPhonemeSymbol(phone.phoneme)) {
                            pinned = false;
                            break;
                        }
                    }
                    if (!pinned) {
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
        static RenderNote OwnerNoteAt(RenderPhrase phrase, double ms) {
            RenderNote owner = phrase.notes[phrase.notes.Length - 1];
            foreach (RenderNote note in phrase.notes) {
                if (note.positionMs <= ms) {
                    owner = note;
                } else {
                    break;
                }
            }
            // 延续音符归属前一实质音符，与其共享发音与音高模式。
            int index = Array.IndexOf(phrase.notes, owner);
            while (index > 0
                && phrase.notes[index].lyric == TsnVoiceParameters.ContinuationLyric) {
                index--;
            }
            return phrase.notes[index];
        }

        static List<TsnVoicePitchPoint> SamplePitch(RenderPhrase phrase,
            double baseMs, double startMs, double endMs) {
            const int pitchInterval = 5;
            List<TsnVoicePitchPoint> result = new List<TsnVoicePitchPoint>();
            for (double ms = startMs; ms <= endMs + 0.001;
                ms += TsnVoiceParameters.PitchSampleStepSeconds * 1000.0) {
                int ticks = phrase.timeAxis.MsPosToTickPos(ms)
                    - (phrase.position - phrase.leading);
                int index = Math.Clamp(ticks / pitchInterval, 0, phrase.pitches.Length - 1);
                RenderNote owner = OwnerNoteAt(phrase, ms);
                double midi;
                if (owner.hasManualPitch) {
                    // 手绘音高：整体按绝对音高约束。
                    midi = phrase.pitches[index] * 0.01;
                } else {
                    // 自动音高：基音 + PITD 偏移，颤音与手绘由模型生成。
                    midi = owner.tone + (phrase.pitches[index]
                        - phrase.pitchesBeforeDeviation[index]) * 0.01;
                }
                TsnVoicePitchPoint point = new TsnVoicePitchPoint();
                point.TimeSeconds = Math.Max(0, (ms - baseMs) / 1000.0);
                point.MidiPitch = Math.Clamp(midi, 0.0, 127.0);
                point.IsAbsolute = owner.hasManualPitch;
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
            return TsnVoiceParameters.BuildSuggestedExpressions();
        }

        public override string ToString() => Renderers.TSNVOICE;
    }
}
