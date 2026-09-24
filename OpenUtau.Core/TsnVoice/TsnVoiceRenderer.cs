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
    /// 乐句内的相邻同语言音符连续合成；用户音高曲线按绝对音高约束，
    /// ALP/HUS 自定义曲线参与声学条件；音素编辑经固定音素传入。
    /// </summary>
    public class TsnVoiceRenderer : IRenderer {
        static readonly object lockObj = new object();

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
                lock (lockObj) {
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
                                progressInfo, cancellation, pitchPath);
                        } catch (TsnVoiceException e) when (
                            e.Status == TsnVoiceStatus.Cancelled
                            || cancellation.IsCancellationRequested) {
                            return result;
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
                    progress.Complete(phrase.phones.Length, progressInfo);
                    return result;
                }
            });
            return task;
        }

        float[] InvokeTsnVoice(RenderPhrase phrase, Progress progress,
            string progressInfo, CancellationTokenSource cancellation,
            string pitchPath) {
            TsnVoiceSinger singer = phrase.singer as TsnVoiceSinger;
            if (singer == null) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "TsnVoice 渲染器需要 TsnVoice 歌手");
            }
            TsnVoicePackage package = singer.OpenPackage();
            string language = singer.PrimaryLanguage();
            double firstMs = phrase.notes[0].positionMs;
            List<TsnVoiceInputNote> notes = new List<TsnVoiceInputNote>();
            Dictionary<int, List<RenderPhone>> phonesByNote =
                new Dictionary<int, List<RenderPhone>>();
            foreach (RenderPhone phone in phrase.phones) {
                if (!phonesByNote.TryGetValue(phone.noteIndex, out List<RenderPhone> group)) {
                    group = new List<RenderPhone>();
                    phonesByNote[phone.noteIndex] = group;
                }
                group.Add(phone);
            }
            for (int i = 0; i < phrase.notes.Length; i++) {
                RenderNote note = phrase.notes[i];
                TsnVoiceInputNote input = new TsnVoiceInputNote();
                input.Id = "n" + i;
                input.StartSeconds = Math.Max(0, (note.positionMs - firstMs) / 1000.0);
                input.EndSeconds = Math.Max(input.StartSeconds + 0.001,
                    (note.endMs - firstMs) / 1000.0);
                input.MidiPitch = note.tone;
                input.Lyric = string.IsNullOrEmpty(note.lyric)
                    ? TsnVoiceParameters.DefaultLyric(language)
                    : note.lyric;
                input.Language = language;
                input.IsContinuation =
                    input.Lyric == TsnVoiceParameters.ContinuationLyric;
                if (!input.IsContinuation
                    && phonesByNote.TryGetValue(i, out List<RenderPhone> group)) {
                    foreach (RenderPhone phone in group) {
                        TsnVoiceInputPhoneme pinned = new TsnVoiceInputPhoneme();
                        pinned.Symbol = phone.phoneme;
                        pinned.DurationSeconds = Math.Max(phone.durationMs, 1.0) / 1000.0;
                        pinned.StretchWeight = Math.Pow(2.0, 1.0 - phone.velocity / 100.0);
                        input.Phonemes.Add(pinned);
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
                notes.Add(input);
            }
            List<TsnVoicePitchPoint> pitch = SamplePitch(phrase, firstMs);
            List<TsnVoiceControlPoint> controls = SampleControls(phrase, firstMs);
            TsnVoiceSynthesisOutput output = TsnVoiceInference.Synthesize(
                package, notes, pitch, controls,
                () => cancellation.IsCancellationRequested,
                (value, stage) => progress.Complete(0, progressInfo + " " + stage),
                null);
            if (output.Samples.Length == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "TsnVoice 合成未返回音频");
            }
            SavePitchCache(phrase, pitchPath, output);
            float[] samples = output.SampleRate == TsnVoiceParameters.MixSampleRate
                ? output.Samples
                : TsnVoiceDsp.ResampleLinear(output.Samples, output.SampleRate,
                    TsnVoiceParameters.MixSampleRate);
            return samples;
        }

        static List<TsnVoicePitchPoint> SamplePitch(RenderPhrase phrase,
            double firstMs) {
            const int pitchInterval = 5;
            double lastMs = phrase.notes[phrase.notes.Length - 1].endMs;
            List<TsnVoicePitchPoint> result = new List<TsnVoicePitchPoint>();
            for (double ms = firstMs; ms <= lastMs + 0.001;
                ms += TsnVoiceParameters.PitchSampleStepSeconds * 1000.0) {
                int ticks = phrase.timeAxis.MsPosToTickPos(ms)
                    - (phrase.position - phrase.leading);
                int index = Math.Clamp(ticks / pitchInterval, 0, phrase.pitches.Length - 1);
                TsnVoicePitchPoint point = new TsnVoicePitchPoint();
                point.TimeSeconds = Math.Max(0, (ms - firstMs) / 1000.0);
                point.MidiPitch = Math.Clamp(phrase.pitches[index] * 0.01, 0.0, 127.0);
                // 编辑器音高曲线恒为权威：按绝对音高约束。
                point.IsAbsolute = true;
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
            double firstMs) {
            const int pitchInterval = 5;
            float[] alpCurve = null;
            float[] husCurve = null;
            if (phrase.curves != null) {
                alpCurve = FindCurve(phrase.curves, "alp");
                husCurve = FindCurve(phrase.curves, "hus");
            }
            double lastMs = phrase.notes[phrase.notes.Length - 1].endMs;
            List<TsnVoiceControlPoint> result = new List<TsnVoiceControlPoint>();
            for (double ms = firstMs; ms <= lastMs + 0.001;
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
                point.TimeSeconds = Math.Max(0, (ms - firstMs) / 1000.0);
                point.Alpha = TsnVoiceParameters.ToNativeAlpha(alp);
                point.Huskiness = TsnVoiceParameters.ToNativeHuskiness(hus);
                result.Add(point);
            }
            return result;
        }

        static void SavePitchCache(RenderPhrase phrase, string pitchPath,
            TsnVoiceSynthesisOutput output) {
            try {
                using (FileStream stream = new FileStream(pitchPath, FileMode.Create,
                    FileAccess.Write)) {
                    using (BinaryWriter writer = new BinaryWriter(stream)) {
                        writer.Write(output.Pitch.Count);
                        foreach (TsnVoiceOutputPitch point in output.Pitch) {
                            double ms = phrase.notes[0].positionMs
                                + point.TimeSeconds * 1000.0;
                            float tick = phrase.timeAxis.MsPosToTickPos(ms)
                                - phrase.position;
                            writer.Write(tick);
                            // RenderPitchResult.tones 为 MIDI 半音，调用端自行换算为音分。
                            writer.Write((float)point.MidiPitch);
                        }
                    }
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
