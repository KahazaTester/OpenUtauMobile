using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// 乐谱准备与合成流程，托管移植自原生 inference_pipeline.cpp。
    /// 相邻同语言音符合成为连续乐句，首尾各拼接模型上下文后裁剪。
    /// </summary>
    public static partial class TsnVoiceInference {
        class PreparedScore {
            public TsnVoicePronunciation Pronunciation;
            public List<string> LabelPhonemes = new List<string>();
            public List<TsnVoiceLabel> Labels = new List<TsnVoiceLabel>();
            public List<string> RenderedLabels = new List<string>();
            public TsnVoiceDurationPlan Duration = new TsnVoiceDurationPlan();
            public List<int> PhoneStarts = new List<int>();
            public List<int> PhoneEnds = new List<int>();
            public List<string> FramePhonemes = new List<string>();
            public List<TsnVoiceFrameControls> Controls = new List<TsnVoiceFrameControls>();
            public List<string> PhoneNoteIds = new List<string>();
            public List<double> PhoneStretchWeights = new List<double>();
            public List<double> PhoneBodyOffsets = new List<double>();
            public List<bool> PhoneIsLeading = new List<bool>();
            public int ContentStartFrame;
            public int ContentFrameCount;
        }

        static double InterpolatePitch(List<TsnVoicePitchPoint> points, double time,
            double fallback) {
            if (points.Count == 0) {
                return fallback;
            }
            int lo = 0;
            int hi = points.Count - 1;
            if (time <= points[0].TimeSeconds) {
                return points[0].MidiPitch;
            }
            if (time >= points[hi].TimeSeconds) {
                return points[hi].MidiPitch;
            }
            while (hi - lo > 1) {
                int mid = (lo + hi) / 2;
                if (points[mid].TimeSeconds <= time) {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            double width = points[hi].TimeSeconds - points[lo].TimeSeconds;
            if (width <= 0) {
                return points[hi].MidiPitch;
            }
            double ratio = Math.Clamp((time - points[lo].TimeSeconds) / width, 0.0, 1.0);
            return points[lo].MidiPitch + (points[hi].MidiPitch - points[lo].MidiPitch) * ratio;
        }

        static double InterpolateControl(List<TsnVoiceControlPoint> points, double time,
            Func<TsnVoiceControlPoint, double> projection, double fallback) {
            if (points.Count == 0) {
                return fallback;
            }
            int lo = 0;
            int hi = points.Count - 1;
            if (time <= points[0].TimeSeconds) {
                return projection(points[0]);
            }
            if (time >= points[hi].TimeSeconds) {
                return projection(points[hi]);
            }
            while (hi - lo > 1) {
                int mid = (lo + hi) / 2;
                if (points[mid].TimeSeconds <= time) {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            double width = points[hi].TimeSeconds - points[lo].TimeSeconds;
            if (width <= 0) {
                return projection(points[hi]);
            }
            double ratio = Math.Clamp((time - points[lo].TimeSeconds) / width, 0.0, 1.0);
            return projection(points[lo]) + (projection(points[hi]) - projection(points[lo])) * ratio;
        }

        static bool InterpolateAbsolute(List<TsnVoicePitchPoint> points, double time) {
            if (points.Count == 0) {
                return false;
            }
            if (time <= points[0].TimeSeconds) {
                return points[0].IsAbsolute;
            }
            if (time >= points[points.Count - 1].TimeSeconds) {
                return points[points.Count - 1].IsAbsolute;
            }
            int lo = 0;
            int hi = points.Count - 1;
            while (hi - lo > 1) {
                int mid = (lo + hi) / 2;
                if (points[mid].TimeSeconds <= time) {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            // 沿用原生逻辑：用插值后的绝对标记四舍五入判定。
            double width = points[hi].TimeSeconds - points[lo].TimeSeconds;
            double ratio = width <= 0 ? 1.0
                : (time - points[lo].TimeSeconds) / width;
            double value = (points[lo].IsAbsolute ? 1.0 : 0.0) * (1 - ratio)
                + (points[hi].IsAbsolute ? 1.0 : 0.0) * ratio;
            return value >= 0.5;
        }

        static List<TsnVoiceFrameControls> SampleNoteControls(
            TsnVoiceInputNote note, List<TsnVoicePitchPoint> pitchPoints,
            List<TsnVoiceControlPoint> controlPoints, int frameCount, double frameSeconds) {
            List<TsnVoiceFrameControls> controls = new List<TsnVoiceFrameControls>(frameCount);
            for (int frame = 0; frame < frameCount; frame++) {
                double time = note.StartSeconds + frame * frameSeconds;
                TsnVoiceFrameControls control = new TsnVoiceFrameControls();
                control.MidiPitch = InterpolatePitch(pitchPoints, time, note.MidiPitch);
                control.BaseMidiPitch = note.MidiPitch;
                control.PitchIsAbsolute = InterpolateAbsolute(pitchPoints, time);
                control.Alpha = InterpolateControl(controlPoints, time, p => p.Alpha, 0.0);
                control.Huskiness = InterpolateControl(controlPoints, time, p => p.Huskiness, 0.0);
                controls.Add(control);
            }
            return controls;
        }

        static void ValidateInput(TsnVoicePackage voice, List<TsnVoiceInputNote> notes,
            List<TsnVoicePitchPoint> pitchPoints, List<TsnVoiceControlPoint> controlPoints) {
            if (notes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument, "合成请求没有音符");
            }
            // 注意：词典以内嵌资源提供，DataPath 覆盖目录无需存在；
            // 各语言词典在首次使用时加载，缺失会报明确错误。
            HashSet<string> languages = new HashSet<string>(voice.Languages,
                StringComparer.Ordinal);
            string phraseLanguage = notes[0].Language;
            for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++) {
                TsnVoiceInputNote note = notes[noteIndex];
                if (double.IsNaN(note.StartSeconds) || double.IsInfinity(note.StartSeconds)
                    || double.IsNaN(note.EndSeconds) || double.IsInfinity(note.EndSeconds)
                    || note.StartSeconds < 0 || note.EndSeconds <= note.StartSeconds) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "音符时间边界无效");
                }
                if (note.MidiPitch < 0 || note.MidiPitch > 127) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "音符 MIDI 音高越界");
                }
                if (note.Id.Length == 0 || note.Lyric.Length == 0 || note.Language.Length == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "音符 id、歌词、语言均不能为空");
                }
                if (!languages.Contains(note.Language)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "所选语音不支持音符语言 '" + note.Language + "'");
                }
                if (note.Language != phraseLanguage) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "一个合成乐句只能使用一种语言");
                }
                if (note.LeadingPhonemeCount > note.Phonemes.Count) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "前置音素数越界");
                }
                if (double.IsNaN(note.BodyOffsetSeconds)
                    || double.IsInfinity(note.BodyOffsetSeconds)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "音符主体偏移必须有限");
                }
                if (note.IsContinuation) {
                    if (noteIndex == 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "合成乐句不能以延续音符开头");
                    }
                    if (note.Phonemes.Count > 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "延续音符不能携带固定音素");
                    }
                    TsnVoiceInputNote previous = notes[noteIndex - 1];
                    if (previous.EndSeconds + 0.001 < note.StartSeconds) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "延续音符必须与前一音符相连");
                    }
                }
                foreach (TsnVoiceInputPhoneme phoneme in note.Phonemes) {
                    if (phoneme.Symbol.Length == 0
                        || double.IsNaN(phoneme.DurationSeconds)
                        || double.IsInfinity(phoneme.DurationSeconds)
                        || double.IsNaN(phoneme.StretchWeight)
                        || double.IsInfinity(phoneme.StretchWeight)
                        || phoneme.DurationSeconds < 0 || phoneme.StretchWeight < 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "音符存在无效的固定音素");
                    }
                }
            }
            for (int i = 1; i < pitchPoints.Count; i++) {
                if (pitchPoints[i].TimeSeconds < pitchPoints[i - 1].TimeSeconds) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "音高点必须按时间排序");
                }
            }
            foreach (TsnVoicePitchPoint point in pitchPoints) {
                if (double.IsNaN(point.TimeSeconds) || double.IsInfinity(point.TimeSeconds)
                    || double.IsNaN(point.MidiPitch) || double.IsInfinity(point.MidiPitch)
                    || point.TimeSeconds < 0 || point.MidiPitch < 0 || point.MidiPitch > 127) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "音高点超出有效范围");
                }
            }
            for (int i = 1; i < controlPoints.Count; i++) {
                if (controlPoints[i].TimeSeconds < controlPoints[i - 1].TimeSeconds) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "控制点必须按时间排序");
                }
            }
            foreach (TsnVoiceControlPoint point in controlPoints) {
                if (double.IsNaN(point.TimeSeconds) || double.IsInfinity(point.TimeSeconds)
                    || double.IsNaN(point.Alpha) || double.IsInfinity(point.Alpha)
                    || double.IsNaN(point.Huskiness) || double.IsInfinity(point.Huskiness)
                    || point.TimeSeconds < 0 || point.Alpha < -1 || point.Alpha > 1
                    || point.Huskiness < -1 || point.Huskiness > 1) {
                    throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                        "控制点超出有效范围");
                }
            }
        }

        static PreparedScore PrepareNote(TsnVoicePackage voice, TsnVoiceInputNote note,
            List<TsnVoicePitchPoint> pitchPoints, List<TsnVoiceControlPoint> controlPoints,
            int? targetFramesOverride = null) {
            string lyric = note.Lyric;
            if (note.Phonemes.Count > 0) {
                lyric = TsnVoiceFrontend.DefaultLyric(note.Language);
            }
            TsnVoicePronunciation pronunciation;
            try {
                pronunciation = TsnVoiceFrontend.Pronounce(note.Language, lyric);
            } catch (TsnVoiceException e) when (
                e.Status == TsnVoiceStatus.InvalidArgument
                || e.Status == TsnVoiceStatus.Unsupported) {
                // 单个歌词无法转写时不中断整句：该音符以静音占据此时值并记错。
                // 基础设施错误（词典缺失等）继续上抛。
                Serilog.Log.Error(e, "TsnVoice 无法转写歌词 {Lyric}（{Language}），该音符静音",
                    note.Lyric, note.Language);
                pronunciation = new TsnVoicePronunciation();
                pronunciation.Phonemes.Add("sil");
            }
            if (note.Phonemes.Count > 0) {
                pronunciation.Phonemes.Clear();
                foreach (TsnVoiceInputPhoneme phoneme in note.Phonemes) {
                    pronunciation.Phonemes.Add(phoneme.Symbol);
                }
                if (pronunciation.PhonemeLanguageFlags.Count != note.Phonemes.Count) {
                    pronunciation.PhonemeLanguageFlags.Clear();
                }
            }
            TsnVoiceLabel.BuildIsolatedLabels(pronunciation.Phonemes, pronunciation.Vowels,
                note.Language, pronunciation.LanguageFlag, note.MidiPitch,
                note.EndSeconds - note.StartSeconds, pronunciation.PhonemeLanguageFlags,
                out List<string> outPhonemes, out List<TsnVoiceLabel> labels);
            List<string> rendered = new List<string>(labels.Count);
            foreach (TsnVoiceLabel label in labels) {
                rendered.Add(label.Render());
            }
            double sampleRate = ConfigNumber(voice.Config, "SAMPLING_FREQUENCY", 0, true);
            double framePeriod = ConfigNumber(voice.Config, "FRAME_PERIOD", 0, true);
            if (sampleRate <= 0 || framePeriod <= 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice, "语音帧时序无效");
            }
            double frameSeconds = framePeriod / sampleRate;
            int estimatedFrames = Math.Max(labels.Count * 5,
                (int)Math.Round((note.EndSeconds - note.StartSeconds) / frameSeconds,
                    MidpointRounding.AwayFromZero));
            int targetFrames = Math.Max(labels.Count * 5,
                targetFramesOverride ?? estimatedFrames);
            TsnVoiceDurationPlan duration = TsnVoiceDuration.BuildDurationPlan(
                voice, note.Language, rendered, targetFrames);
            if (note.Phonemes.Count > 0 && note.Phonemes.Count == labels.Count) {
                double[] pinned = new double[note.Phonemes.Count];
                for (int i = 0; i < pinned.Length; i++) {
                    pinned[i] = note.Phonemes[i].DurationSeconds;
                }
                duration = TsnVoiceDuration.RetimeDurationPlan(duration, pinned);
            }
            int leadingCount = note.LeadingPhonemeCount;
            if (note.Phonemes.Count == 0) {
                HashSet<string> vowelSet = new HashSet<string>(pronunciation.Vowels,
                    StringComparer.Ordinal);
                leadingCount = 0;
                for (int i = 0; i < pronunciation.Phonemes.Count; i++) {
                    if (vowelSet.Contains(pronunciation.Phonemes[i])) {
                        break;
                    }
                    leadingCount++;
                }
            }
            List<int> phoneStarts = new List<int>();
            List<int> phoneEnds = new List<int>();
            for (int i = 0; i < labels.Count; i++) {
                phoneStarts.Add(targetFrames);
                phoneEnds.Add(0);
            }
            List<string> framePhonemes = new List<string>();
            for (int i = 0; i < targetFrames; i++) {
                framePhonemes.Add(string.Empty);
            }
            foreach (TsnVoiceStateTiming timing in duration.Timings) {
                phoneStarts[timing.PhonemeIndex] = Math.Min(
                    phoneStarts[timing.PhonemeIndex], timing.StartFrame);
                phoneEnds[timing.PhonemeIndex] = Math.Max(
                    phoneEnds[timing.PhonemeIndex], timing.EndFrame);
                for (int frame = timing.StartFrame; frame < timing.EndFrame; frame++) {
                    framePhonemes[frame] = outPhonemes[timing.PhonemeIndex];
                }
            }
            foreach (string value in framePhonemes) {
                if (value.Length == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "时长规划未覆盖每一帧");
                }
            }
            PreparedScore result = new PreparedScore();
            result.Pronunciation = pronunciation;
            result.LabelPhonemes = outPhonemes;
            result.Labels = labels;
            result.RenderedLabels = rendered;
            result.Duration = duration;
            result.PhoneStarts = phoneStarts;
            result.PhoneEnds = phoneEnds;
            result.FramePhonemes = framePhonemes;
            result.Controls = SampleNoteControls(note, pitchPoints, controlPoints,
                targetFrames, frameSeconds);
            for (int i = 0; i < outPhonemes.Count; i++) {
                result.PhoneNoteIds.Add(note.Id);
            }
            double bodyOffset = note.Phonemes.Count == 0 && leadingCount != 0
                ? result.PhoneEnds[leadingCount - 1] * frameSeconds
                : note.BodyOffsetSeconds;
            for (int i = 0; i < outPhonemes.Count; i++) {
                result.PhoneBodyOffsets.Add(bodyOffset);
                result.PhoneStretchWeights.Add(i < note.Phonemes.Count
                    ? note.Phonemes[i].StretchWeight
                    : 1.0);
                result.PhoneIsLeading.Add(i < leadingCount);
            }
            return result;
        }

        static void MergePart(PreparedScore result, PreparedScore part) {
            if (result.Duration.StateCount != 0
                && result.Duration.StateCount != part.Duration.StateCount) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "乐句内时长状态数发生变化");
            }
            int phoneOffset = result.LabelPhonemes.Count;
            int frameOffset = result.Duration.FrameCount;
            result.Duration.StateCount = part.Duration.StateCount;
            foreach (TsnVoiceStateTiming timing in part.Duration.Timings) {
                TsnVoiceStateTiming shifted = new TsnVoiceStateTiming();
                shifted.PhonemeIndex = timing.PhonemeIndex + phoneOffset;
                shifted.StateIndex = timing.StateIndex;
                shifted.StartFrame = timing.StartFrame + frameOffset;
                shifted.EndFrame = timing.EndFrame + frameOffset;
                result.Duration.Timings.Add(shifted);
            }
            result.Duration.FrameCount += part.Duration.FrameCount;
            foreach (int value in part.PhoneStarts) {
                result.PhoneStarts.Add(value + frameOffset);
            }
            foreach (int value in part.PhoneEnds) {
                result.PhoneEnds.Add(value + frameOffset);
            }
            result.LabelPhonemes.AddRange(part.LabelPhonemes);
            result.Labels.AddRange(part.Labels);
            result.RenderedLabels.AddRange(part.RenderedLabels);
            result.FramePhonemes.AddRange(part.FramePhonemes);
            result.Controls.AddRange(part.Controls);
            result.PhoneNoteIds.AddRange(part.PhoneNoteIds);
            result.PhoneStretchWeights.AddRange(part.PhoneStretchWeights);
            result.PhoneBodyOffsets.AddRange(part.PhoneBodyOffsets);
            result.PhoneIsLeading.AddRange(part.PhoneIsLeading);
        }

        static PreparedScore PrepareScore(TsnVoicePackage voice, List<TsnVoiceInputNote> notes,
            List<TsnVoicePitchPoint> pitchPoints, List<TsnVoiceControlPoint> controlPoints) {
            double sampleRate = ConfigNumber(voice.Config, "SAMPLING_FREQUENCY", 0, true);
            double framePeriod = ConfigNumber(voice.Config, "FRAME_PERIOD", 0, true);
            double frameSeconds = framePeriod / sampleRate;
            double scoreStart = notes[0].StartSeconds;
            PreparedScore result = new PreparedScore();
            int accumulatedFrames = 0;
            foreach (TsnVoiceInputNote note in notes) {
                int absoluteEnd = Math.Max(0, (int)Math.Round(
                    (note.EndSeconds - scoreStart) / frameSeconds,
                    MidpointRounding.AwayFromZero));
                int requestedFrames = absoluteEnd >= accumulatedFrames
                    ? absoluteEnd - accumulatedFrames
                    : 0;
                if (note.IsContinuation) {
                    if (result.Duration.Timings.Count == 0
                        || result.LabelPhonemes.Count == 0
                        || result.PhoneEnds.Count == 0
                        || result.FramePhonemes.Count == 0) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                            "延续音符缺少前置发音");
                    }
                    if (requestedFrames != 0) {
                        TsnVoiceStateTiming last =
                            result.Duration.Timings[result.Duration.Timings.Count - 1];
                        last.EndFrame += requestedFrames;
                        result.Duration.FrameCount += requestedFrames;
                        result.PhoneEnds[result.PhoneEnds.Count - 1] += requestedFrames;
                        string tail = result.LabelPhonemes[result.LabelPhonemes.Count - 1];
                        for (int i = 0; i < requestedFrames; i++) {
                            result.FramePhonemes.Add(tail);
                        }
                        result.Controls.AddRange(SampleNoteControls(note, pitchPoints,
                            controlPoints, requestedFrames, frameSeconds));
                    }
                    accumulatedFrames = result.Duration.FrameCount;
                    continue;
                }
                PreparedScore part = PrepareNote(voice, note, pitchPoints,
                    controlPoints, requestedFrames);
                MergePart(result, part);
                accumulatedFrames = result.Duration.FrameCount;
            }
            Dictionary<string, int> noteIndices =
                new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < notes.Count; i++) {
                noteIndices[notes[i].Id] = i;
            }
            // 跨音符边界 stitch 上下文：前后各两音素、元音距离、相邻音高。
            for (int phone = 0; phone < result.Labels.Count; phone++) {
                TsnVoiceLabel label = result.Labels[phone];
                for (int relative = -2; relative <= 2; relative++) {
                    int index = phone + relative;
                    if (index < 0) {
                        if (phone == 0 && relative == -1) {
                            label.Set('P', relative + 3, "sil");
                        }
                        continue;
                    }
                    if (index < result.LabelPhonemes.Count) {
                        label.Set('P', relative + 3,
                            result.LabelPhonemes[index]);
                    } else if (relative > 0) {
                        label.Set('P', relative + 3, "pau");
                    }
                }
                if (label.Get('P', 0) != "v") {
                    for (int previous = phone; previous-- > 0;) {
                        if (result.Labels[previous].Get('P', 0) == "v") {
                            label.Set('P', 13, (phone - previous).ToString());
                            break;
                        }
                    }
                    for (int next = phone + 1; next < result.Labels.Count; next++) {
                        if (result.Labels[next].Get('P', 0) == "v") {
                            label.Set('P', 14, (next - phone).ToString());
                            break;
                        }
                    }
                }
                if (!noteIndices.TryGetValue(result.PhoneNoteIds[phone], out int owner)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "音素标签缺少所属音符");
                }
                int? previousNote = null;
                for (int i = owner; i-- > 0;) {
                    if (!notes[i].IsContinuation) {
                        previousNote = i;
                        break;
                    }
                }
                int? nextNote = null;
                for (int i = owner + 1; i < notes.Count; i++) {
                    if (!notes[i].IsContinuation) {
                        nextNote = i;
                        break;
                    }
                }
                if (previousNote.HasValue) {
                    label.Set('D', 0, TsnVoiceLabel.PitchName(
                        notes[previousNote.Value].MidiPitch));
                    label.Set('E', 56, TsnVoiceLabel.PitchDelta(
                        notes[owner].MidiPitch - notes[previousNote.Value].MidiPitch));
                }
                if (nextNote.HasValue) {
                    label.Set('F', 0, TsnVoiceLabel.PitchName(
                        notes[nextNote.Value].MidiPitch));
                    label.Set('E', 57, TsnVoiceLabel.PitchDelta(
                        notes[nextNote.Value].MidiPitch - notes[owner].MidiPitch));
                }
                result.RenderedLabels[phone] = label.Render();
            }
            RebuildPhraseDurations(voice, notes, frameSeconds, result);
            result.ContentFrameCount = result.Duration.FrameCount;
            PrependPhraseHead(voice, notes, result);
            AppendPhraseTail(voice, notes, result);
            return result;
        }

        static void RebuildPhraseDurations(TsnVoicePackage voice,
            List<TsnVoiceInputNote> notes, double frameSeconds, PreparedScore score) {
            Dictionary<string, TsnVoiceInputNote> noteById =
                new Dictionary<string, TsnVoiceInputNote>(StringComparer.Ordinal);
            foreach (TsnVoiceInputNote note in notes) {
                if (!note.IsContinuation) {
                    noteById[note.Id] = note;
                }
            }
            TsnVoiceDurationPlan rebuilt = new TsnVoiceDurationPlan();
            rebuilt.FrameCount = score.Duration.FrameCount;
            List<int> phoneStarts = new List<int>();
            List<int> phoneEnds = new List<int>();
            for (int i = 0; i < score.LabelPhonemes.Count; i++) {
                phoneStarts.Add(rebuilt.FrameCount);
                phoneEnds.Add(0);
            }
            List<string> framePhonemes = new List<string>();
            for (int i = 0; i < rebuilt.FrameCount; i++) {
                framePhonemes.Add(string.Empty);
            }
            int phone = 0;
            while (phone < score.PhoneNoteIds.Count) {
                string noteId = score.PhoneNoteIds[phone];
                int phoneEnd = phone + 1;
                while (phoneEnd < score.PhoneNoteIds.Count
                    && score.PhoneNoteIds[phoneEnd] == noteId) {
                    phoneEnd++;
                }
                if (!noteById.TryGetValue(noteId, out TsnVoiceInputNote note)) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "音素时值所属音符缺失");
                }
                int groupStart = score.PhoneStarts[phone];
                int groupEnd = score.PhoneEnds[phoneEnd - 1];
                if (groupEnd <= groupStart) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "音素时值分组为空");
                }
                List<string> labels = score.RenderedLabels.GetRange(phone, phoneEnd - phone);
                TsnVoiceDurationPlan plan = TsnVoiceDuration.BuildDurationPlan(
                    voice, note.Language, labels, groupEnd - groupStart);
                if (note.Phonemes.Count > 0) {
                    double[] pinned = new double[note.Phonemes.Count];
                    for (int i = 0; i < pinned.Length; i++) {
                        pinned[i] = note.Phonemes[i].DurationSeconds;
                    }
                    plan = TsnVoiceDuration.RetimeDurationPlan(plan, pinned);
                }
                if (rebuilt.StateCount != 0 && rebuilt.StateCount != plan.StateCount) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "乐句内时长状态数发生变化");
                }
                rebuilt.StateCount = plan.StateCount;
                foreach (TsnVoiceStateTiming timing in plan.Timings) {
                    TsnVoiceStateTiming shifted = new TsnVoiceStateTiming();
                    shifted.PhonemeIndex = timing.PhonemeIndex + phone;
                    shifted.StateIndex = timing.StateIndex;
                    shifted.StartFrame = timing.StartFrame + groupStart;
                    shifted.EndFrame = timing.EndFrame + groupStart;
                    phoneStarts[shifted.PhonemeIndex] = Math.Min(
                        phoneStarts[shifted.PhonemeIndex], shifted.StartFrame);
                    phoneEnds[shifted.PhonemeIndex] = Math.Max(
                        phoneEnds[shifted.PhonemeIndex], shifted.EndFrame);
                    for (int frame = shifted.StartFrame; frame < shifted.EndFrame; frame++) {
                        framePhonemes[frame] = score.LabelPhonemes[shifted.PhonemeIndex];
                    }
                    rebuilt.Timings.Add(shifted);
                }
                if (note.Phonemes.Count == 0) {
                    int lastLeading = phone;
                    while (lastLeading < phoneEnd && score.PhoneIsLeading[lastLeading]) {
                        lastLeading++;
                    }
                    double bodyOffset = lastLeading == phone
                        ? 0.0
                        : (phoneEnds[lastLeading - 1] - groupStart) * frameSeconds;
                    for (int i = phone; i < phoneEnd; i++) {
                        score.PhoneBodyOffsets[i] = bodyOffset;
                    }
                }
                phone = phoneEnd;
            }
            foreach (string value in framePhonemes) {
                if (value.Length == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "重建的时长规划未覆盖每一帧");
                }
            }
            score.Duration = rebuilt;
            score.PhoneStarts = phoneStarts;
            score.PhoneEnds = phoneEnds;
            score.FramePhonemes = framePhonemes;
        }

        static void PrependPhraseHead(TsnVoicePackage voice,
            List<TsnVoiceInputNote> notes, PreparedScore score) {
            const int headSilenceFrames = 20;
            double sampleRate = ConfigNumber(voice.Config, "SAMPLING_FREQUENCY", 0, true);
            double framePeriod = ConfigNumber(voice.Config, "FRAME_PERIOD", 0, true);
            double frameSeconds = framePeriod / sampleRate;
            TsnVoiceInputNote content = null;
            foreach (TsnVoiceInputNote note in notes) {
                if (!note.IsContinuation) {
                    content = note;
                    break;
                }
            }
            if (content == null || score.LabelPhonemes.Count == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "乐句头部缺少内容音符");
            }
            TsnVoiceInputNote silence = new TsnVoiceInputNote();
            silence.StartSeconds = notes[0].StartSeconds - headSilenceFrames * frameSeconds;
            silence.EndSeconds = notes[0].StartSeconds;
            silence.MidiPitch = content.MidiPitch;
            silence.Lyric = "-";
            silence.Language = content.Language;
            TsnVoiceInputPhoneme sil = new TsnVoiceInputPhoneme();
            sil.Symbol = "sil";
            sil.DurationSeconds = headSilenceFrames * frameSeconds;
            sil.StretchWeight = 1.0;
            silence.Phonemes.Add(sil);
            PreparedScore head = PrepareNote(voice, silence,
                new List<TsnVoicePitchPoint>(), new List<TsnVoiceControlPoint>(),
                headSilenceFrames);
            if (head.Labels.Count > 0) {
                TsnVoiceLabel label = head.Labels[0];
                label.Set('P', 4, score.LabelPhonemes[0]);
                if (score.LabelPhonemes.Count > 1) {
                    label.Set('P', 5, score.LabelPhonemes[1]);
                }
                head.RenderedLabels[0] = label.Render();
            }
            if (score.Duration.StateCount != 0
                && score.Duration.StateCount != head.Duration.StateCount) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "乐句头部时长状态数发生变化");
            }
            int phoneOffset = head.LabelPhonemes.Count;
            int frameOffset = head.Duration.FrameCount;
            foreach (TsnVoiceStateTiming timing in score.Duration.Timings) {
                timing.PhonemeIndex += phoneOffset;
                timing.StartFrame += frameOffset;
                timing.EndFrame += frameOffset;
            }
            score.Duration.Timings.InsertRange(0, head.Duration.Timings);
            score.Duration.StateCount = head.Duration.StateCount;
            score.Duration.FrameCount += frameOffset;
            for (int i = 0; i < score.PhoneStarts.Count; i++) {
                score.PhoneStarts[i] += frameOffset;
                score.PhoneEnds[i] += frameOffset;
            }
            score.PhoneStarts.InsertRange(0, head.PhoneStarts);
            score.PhoneEnds.InsertRange(0, head.PhoneEnds);
            score.LabelPhonemes.InsertRange(0, head.LabelPhonemes);
            score.Labels.InsertRange(0, head.Labels);
            score.RenderedLabels.InsertRange(0, head.RenderedLabels);
            score.FramePhonemes.InsertRange(0, head.FramePhonemes);
            score.Controls.InsertRange(0, head.Controls);
            for (int i = 0; i < head.PhoneNoteIds.Count; i++) {
                score.PhoneNoteIds.Insert(0, string.Empty);
            }
            score.PhoneStretchWeights.InsertRange(0, head.PhoneStretchWeights);
            score.PhoneBodyOffsets.InsertRange(0, head.PhoneBodyOffsets);
            score.PhoneIsLeading.InsertRange(0, head.PhoneIsLeading);
            score.ContentStartFrame += frameOffset;
        }

        static void AppendPhraseTail(TsnVoicePackage voice,
            List<TsnVoiceInputNote> notes, PreparedScore score) {
            const int pauseFrames = 300;
            double sampleRate = ConfigNumber(voice.Config, "SAMPLING_FREQUENCY", 0, true);
            double framePeriod = ConfigNumber(voice.Config, "FRAME_PERIOD", 0, true);
            double frameSeconds = framePeriod / sampleRate;
            TsnVoiceInputNote content = null;
            for (int i = notes.Count; i-- > 0;) {
                if (!notes[i].IsContinuation) {
                    content = notes[i];
                    break;
                }
            }
            if (content == null) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "乐句尾部缺少内容音符");
            }
            double phraseEnd = notes[notes.Count - 1].EndSeconds;
            TsnVoiceInputNote tailNote = new TsnVoiceInputNote();
            tailNote.StartSeconds = phraseEnd;
            tailNote.EndSeconds = phraseEnd + pauseFrames * frameSeconds;
            tailNote.MidiPitch = content.MidiPitch;
            tailNote.Lyric = "-";
            tailNote.Language = content.Language;
            TsnVoiceInputPhoneme pau = new TsnVoiceInputPhoneme();
            pau.Symbol = "pau";
            pau.DurationSeconds = pauseFrames * frameSeconds;
            pau.StretchWeight = 1.0;
            tailNote.Phonemes.Add(pau);
            PreparedScore tail = PrepareNote(voice, tailNote,
                new List<TsnVoicePitchPoint>(), new List<TsnVoiceControlPoint>(),
                pauseFrames);
            if (tail.Labels.Count > 0 && score.LabelPhonemes.Count > 0) {
                TsnVoiceLabel label = tail.Labels[0];
                label.Set('P', 2, score.LabelPhonemes[score.LabelPhonemes.Count - 1]);
                if (score.LabelPhonemes.Count > 1) {
                    label.Set('P', 1,
                        score.LabelPhonemes[score.LabelPhonemes.Count - 2]);
                }
                tail.RenderedLabels[0] = label.Render();
            }
            if (score.Duration.StateCount != 0
                && score.Duration.StateCount != tail.Duration.StateCount) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "乐句尾部时长状态数发生变化");
            }
            int phoneOffset = score.LabelPhonemes.Count;
            int frameOffset = score.Duration.FrameCount;
            score.Duration.StateCount = tail.Duration.StateCount;
            foreach (TsnVoiceStateTiming timing in tail.Duration.Timings) {
                TsnVoiceStateTiming shifted = new TsnVoiceStateTiming();
                shifted.PhonemeIndex = timing.PhonemeIndex + phoneOffset;
                shifted.StateIndex = timing.StateIndex;
                shifted.StartFrame = timing.StartFrame + frameOffset;
                shifted.EndFrame = timing.EndFrame + frameOffset;
                score.Duration.Timings.Add(shifted);
            }
            score.Duration.FrameCount += tail.Duration.FrameCount;
            foreach (int value in tail.PhoneStarts) {
                score.PhoneStarts.Add(value + frameOffset);
            }
            foreach (int value in tail.PhoneEnds) {
                score.PhoneEnds.Add(value + frameOffset);
            }
            score.LabelPhonemes.AddRange(tail.LabelPhonemes);
            score.Labels.AddRange(tail.Labels);
            score.RenderedLabels.AddRange(tail.RenderedLabels);
            score.FramePhonemes.AddRange(tail.FramePhonemes);
            score.Controls.AddRange(tail.Controls);
            for (int i = 0; i < tail.PhoneNoteIds.Count; i++) {
                score.PhoneNoteIds.Add(string.Empty);
            }
            score.PhoneStretchWeights.AddRange(tail.PhoneStretchWeights);
            score.PhoneBodyOffsets.AddRange(tail.PhoneBodyOffsets);
            score.PhoneIsLeading.AddRange(tail.PhoneIsLeading);
        }

        static float[,] CompileFrameContext(PreparedScore score) {
            int frames = score.Duration.FrameCount;
            float[,] result = new float[frames, 5];
            for (int phone = 0; phone < score.Labels.Count; phone++) {
                int duration = score.PhoneEnds[phone] - score.PhoneStarts[phone];
                if (duration == 0) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "音素没有帧");
                }
                for (int offset = 0; offset < duration; offset++) {
                    double forward = Math.Min(Math.Min(offset + 1, 200.0), duration / 2.0);
                    double backward = Math.Min(Math.Min(duration - offset, 200.0),
                        duration / 2.0);
                    int frame = score.PhoneStarts[phone] + offset;
                    result[frame, 0] = NormalizeContext(forward, 1, 200);
                    result[frame, 1] = NormalizeContext(backward, 1, 200);
                    result[frame, 2] = duration == 1
                        ? 0.5f
                        : (float)offset / (duration - 1);
                    result[frame, 3] = duration == 1
                        ? 0.5f
                        : (float)(duration - 1 - offset) / (duration - 1);
                    result[frame, 4] = NormalizeContext(duration, 1, 400);
                }
            }
            return result;
        }

        static float NormalizeContext(double value, double minimum, double maximum) {
            return (float)Math.Clamp((value - minimum) / (maximum - minimum), 0.0, 1.0);
        }

        static int ModelInputDimensions(SessionEntry model) {
            if (model.Session.InputMetadata.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型不是定长矩阵模型");
            }
            string name = model.Session.InputMetadata.Keys.First();
            int[] shape = model.Session.InputMetadata[name].Dimensions;
            if (shape.Length != 3) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "模型不是定长矩阵模型");
            }
            return shape[2];
        }

        static float[,] ConcatenateColumns(List<float[,]> matrices) {
            if (matrices.Count == 0 || matrices[0].GetLength(0) == 0) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "无法拼接空矩阵");
            }
            int rows = matrices[0].GetLength(0);
            int columns = 0;
            foreach (float[,] matrix in matrices) {
                if (matrix.GetLength(0) != rows) {
                    throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                        "矩阵行数不一致");
                }
                columns += matrix.GetLength(1);
            }
            float[,] result = new float[rows, columns];
            for (int row = 0; row < rows; row++) {
                int write = 0;
                foreach (float[,] matrix in matrices) {
                    for (int column = 0; column < matrix.GetLength(1); column++) {
                        result[row, write++] = matrix[row, column];
                    }
                }
            }
            return result;
        }

        static float[,] RepeatedCode(int frames, float[] code) {
            float[,] result = new float[frames, code.Length];
            for (int frame = 0; frame < frames; frame++) {
                for (int i = 0; i < code.Length; i++) {
                    result[frame, i] = code[i];
                }
            }
            return result;
        }

        static float[,] LegacyLinguisticFeatures(TsnVoicePackage voice,
            Dictionary<string, SessionEntry> sessions, PreparedScore score,
            out float[,] frameContext, Action<double> fraction = null) {
            SessionEntry model = GetModel(sessions, "linguistic");
            int statePhonemeDimensions = ModelInputDimensions(model);
            int totalDimensions = statePhonemeDimensions + 5;
            TsnVoiceContextQuestions questions = TsnVoiceContextQuestions.Parse(
                voice.LabelFeatureRules, totalDimensions);
            int frames = score.Duration.FrameCount;
            float[,] contexts = new float[frames, statePhonemeDimensions];
            frameContext = new float[frames, 5];
            foreach (TsnVoiceStateTiming timing in score.Duration.Timings) {
                TsnVoiceLabelTiming labelTiming = new TsnVoiceLabelTiming();
                labelTiming.StateIndex = timing.StateIndex;
                labelTiming.StateCount = score.Duration.StateCount;
                labelTiming.PhoneFrames = score.PhoneEnds[timing.PhonemeIndex]
                    - score.PhoneStarts[timing.PhonemeIndex];
                // 与原生一致逐帧编译：帧问题依赖帧在音素内的位置。
                for (int frame = timing.StartFrame; frame < timing.EndFrame; frame++) {
                    labelTiming.FrameIndex = frame - score.PhoneStarts[timing.PhonemeIndex];
                    questions.CompilePartitioned(score.Labels[timing.PhonemeIndex],
                        labelTiming, out float[] framePart, out float[] restPart);
                    if (framePart.Length != 5
                        || restPart.Length != statePhonemeDimensions) {
                        throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                            "旧版上下文分区维度无效");
                    }
                    for (int i = 0; i < 5; i++) {
                        frameContext[frame, i] = framePart[i];
                    }
                    for (int i = 0; i < statePhonemeDimensions; i++) {
                        contexts[frame, i] = restPart[i];
                    }
                }
            }
            return RunFixedSegments(model, contexts, fraction);
        }

        static float[,] ModernLinguisticFeatures(TsnVoicePackage voice,
            Dictionary<string, SessionEntry> sessions, PreparedScore score,
            string language, out float[,] frameContext,
            Action<double> fraction = null) {
            SessionEntry uniqueModel = GetModel(sessions, "unique_context:" + language);
            int uniqueDimensions = ModelInputDimensions(uniqueModel);
            TsnVoiceBinaryQuestions uniqueQuestions = TsnVoiceBinaryQuestions.Parse(
                voice.GetLanguageQuestions(language), uniqueDimensions);
            float[,] uniqueContext =
                new float[score.Labels.Count, uniqueDimensions];
            for (int phone = 0; phone < score.Labels.Count; phone++) {
                float[] compiled = uniqueQuestions.Compile(score.Labels[phone]);
                for (int i = 0; i < uniqueDimensions; i++) {
                    uniqueContext[phone, i] = compiled[i];
                }
            }
            float[,] uniqueEmbedding = RunFixedSegments(uniqueModel, uniqueContext,
                f => fraction?.Invoke(f * 0.2));
            SessionEntry commonModel = GetModel(sessions, "common_context");
            int commonDimensions = ModelInputDimensions(commonModel);
            TsnVoiceContextQuestions commonQuestions = TsnVoiceContextQuestions.Parse(
                voice.CommonQuestions, commonDimensions);
            int frames = score.Duration.FrameCount;
            float[,] commonContext = new float[frames, commonDimensions];
            float[,] repeatedUnique =
                new float[frames, uniqueEmbedding.GetLength(1)];
            frameContext = CompileFrameContext(score);
            foreach (TsnVoiceStateTiming timing in score.Duration.Timings) {
                TsnVoiceLabelTiming labelTiming = new TsnVoiceLabelTiming();
                labelTiming.StateIndex = timing.StateIndex;
                labelTiming.StateCount = score.Duration.StateCount;
                for (int frame = timing.StartFrame; frame < timing.EndFrame; frame++) {
                    labelTiming.FrameIndex =
                        frame - score.PhoneStarts[timing.PhonemeIndex];
                    labelTiming.PhoneFrames = score.PhoneEnds[timing.PhonemeIndex]
                        - score.PhoneStarts[timing.PhonemeIndex];
                    float[] compiled = commonQuestions.Compile(
                        score.Labels[timing.PhonemeIndex], labelTiming);
                    for (int i = 0; i < commonDimensions; i++) {
                        commonContext[frame, i] = compiled[i];
                    }
                    for (int i = 0; i < uniqueEmbedding.GetLength(1); i++) {
                        repeatedUnique[frame, i] =
                            uniqueEmbedding[timing.PhonemeIndex, i];
                    }
                }
            }
            float[,] commonEmbedding = RunFixedSegments(commonModel, commonContext,
                f => fraction?.Invoke(0.2 + f * 0.4));
            float[,] linguisticInput = ConcatenateColumns(new List<float[,]> {
                repeatedUnique, commonEmbedding,
            });
            return RunFixedSegments(GetModel(sessions, "linguistic"), linguisticInput,
                f => fraction?.Invoke(0.6 + f * 0.4));
        }

        static void RunAcousticModels(TsnVoicePackage voice,
            Dictionary<string, SessionEntry> sessions, PreparedScore score,
            float[,] linguistic, float[,] frameContext,
            Action<double> stage1Fraction, Action<double> stage2Fraction,
            out float[,] stage1, out float[,] stage2) {
            int frames = score.Duration.FrameCount;
            float[,] lf0Context = new float[frames, 1];
            for (int frame = 0; frame < frames; frame++) {
                lf0Context[frame, 0] =
                    (float)TsnVoiceDsp.ScoreLf0(score.Controls[frame].MidiPitch);
            }
            float[,] emotion = RepeatedCode(frames, ConfigCodeOrEmpty(voice.Config,
                "EMOTION_CONTEXT_DIMENSIONS", "EMOTION_CODE"));
            float[,] speaker = RepeatedCode(frames, ConfigCodeOrEmpty(voice.Config,
                "SPEAKER_CONTEXT_DIMENSIONS", "SPEAKER_CODE"));
            float[,] stage1Input = ConcatenateColumns(new List<float[,]> {
                lf0Context, emotion, speaker, frameContext, linguistic,
            });
            int expectedStage1 = ConfigSize(voice.Config,
                "CNN1_NUM_INPUT_DIMENSIONS", 0, true);
            if (stage1Input.GetLength(1) != expectedStage1) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "组装的 CNN1 输入维度错误");
            }
            stage1 = RunOverlappedSegments(GetModel(sessions, "acoustic_stage1"),
                stage1Input, 100, stage1Fraction);
            float[,] stage2Input = ConcatenateColumns(new List<float[,]> {
                stage1, stage1Input,
            });
            int expectedStage2 = ConfigSize(voice.Config,
                "CNN2_NUM_INPUT_DIMENSIONS", 0, true);
            if (stage2Input.GetLength(1) != expectedStage2) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "组装的 CNN2 输入维度错误");
            }
            stage2 = RunOverlappedSegments(GetModel(sessions, "acoustic_stage2"),
                stage2Input, 100, stage2Fraction);
        }

        static void ApplyPitchConstraints(PreparedScore score,
            TsnVoiceAcousticParameters acoustic) {
            if (acoustic.Lf0.Length != score.Controls.Count) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "声学 F0 与乐谱帧数不一致");
            }
            double ln2div12 = Math.Log(2.0) / 12.0;
            for (int frame = 0; frame < acoustic.Lf0.Length; frame++) {
                if (acoustic.Lf0[frame] == TsnVoiceDsp.UnvoicedLf0) {
                    continue;
                }
                TsnVoiceFrameControls control = score.Controls[frame];
                if (double.IsNaN(control.MidiPitch) || double.IsInfinity(control.MidiPitch)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "音高约束包含 NaN/Inf");
                }
                if (control.PitchIsAbsolute) {
                    acoustic.Lf0[frame] = Math.Log(440.0)
                        + (control.MidiPitch - 69.0) * ln2div12;
                    continue;
                }
                double semitones = control.MidiPitch - control.BaseMidiPitch;
                acoustic.Lf0[frame] += semitones * ln2div12;
            }
        }

        static void SmoothFreePitchBoundaries(PreparedScore score,
            TsnVoiceAcousticParameters acoustic) {
            const int radiusFrames = 12;
            double minimumJump = Math.Log(2.0) / 12.0;
            for (int boundary = 1; boundary < score.Controls.Count; boundary++) {
                if (score.Controls[boundary - 1].BaseMidiPitch
                    == score.Controls[boundary].BaseMidiPitch) {
                    continue;
                }
                int begin = boundary > radiusFrames ? boundary - radiusFrames : 0;
                int end = Math.Min(acoustic.Lf0.Length, boundary + radiusFrames + 1);
                bool hasAbsolute = false;
                for (int i = begin; i < end; i++) {
                    if (score.Controls[i].PitchIsAbsolute) {
                        hasAbsolute = true;
                        break;
                    }
                }
                if (hasAbsolute) {
                    continue;
                }
                int nearLeft = boundary;
                while (nearLeft > begin) {
                    nearLeft--;
                    if (acoustic.Lf0[nearLeft] != TsnVoiceDsp.UnvoicedLf0) {
                        break;
                    }
                }
                int nearRight = boundary;
                while (nearRight < end
                    && acoustic.Lf0[nearRight] == TsnVoiceDsp.UnvoicedLf0) {
                    nearRight++;
                }
                if (acoustic.Lf0[nearLeft] == TsnVoiceDsp.UnvoicedLf0
                    || nearRight >= end
                    || acoustic.Lf0[nearRight] == TsnVoiceDsp.UnvoicedLf0
                    || Math.Abs(acoustic.Lf0[nearRight] - acoustic.Lf0[nearLeft])
                        < minimumJump) {
                    continue;
                }
                int left = begin;
                while (left < boundary
                    && acoustic.Lf0[left] == TsnVoiceDsp.UnvoicedLf0) {
                    left++;
                }
                if (left >= boundary) {
                    continue;
                }
                int right = end - 1;
                while (right > boundary
                    && acoustic.Lf0[right] == TsnVoiceDsp.UnvoicedLf0) {
                    right--;
                }
                if (right <= boundary
                    || acoustic.Lf0[right] == TsnVoiceDsp.UnvoicedLf0) {
                    continue;
                }
                double leftLf0 = acoustic.Lf0[left];
                double rightLf0 = acoustic.Lf0[right];
                for (int frame = left + 1; frame < right; frame++) {
                    if (acoustic.Lf0[frame] == TsnVoiceDsp.UnvoicedLf0) {
                        continue;
                    }
                    double phase = (double)(frame - left) / (right - left);
                    double ratio = 0.5 * (1.0 - Math.Cos(Math.PI * phase));
                    acoustic.Lf0[frame] = leftLf0 + (rightLf0 - leftLf0) * ratio;
                }
            }
        }

        static List<TsnVoiceOutputPitch> AcousticPitchPoints(PreparedScore score,
            List<TsnVoiceInputNote> notes, TsnVoiceAcousticParameters acoustic,
            int sampleRate, int framePeriod) {
            if (acoustic.Lf0.Length != score.Controls.Count) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "声学 F0 与乐谱帧数不一致");
            }
            double frameSeconds = (double)framePeriod / sampleRate;
            double start = notes[0].StartSeconds;
            double[] editorLf0 = (double[])acoustic.Lf0.Clone();
            int maxBridge = Math.Max(1, (int)Math.Round(0.06 / frameSeconds,
                MidpointRounding.AwayFromZero));
            int maxBoundaryBridge = Math.Max(maxBridge,
                (int)Math.Round(0.18 / frameSeconds, MidpointRounding.AwayFromZero));
            int begin = 0;
            while (begin < editorLf0.Length) {
                if (editorLf0[begin] != TsnVoiceDsp.UnvoicedLf0) {
                    begin++;
                    continue;
                }
                int end = begin;
                while (end < editorLf0.Length
                    && editorLf0[end] == TsnVoiceDsp.UnvoicedLf0) {
                    end++;
                }
                int count = end - begin;
                bool crossesBoundary = false;
                for (int frame = Math.Max(begin, 1); frame < end; frame++) {
                    if (score.Controls[frame - 1].BaseMidiPitch
                        != score.Controls[frame].BaseMidiPitch) {
                        crossesBoundary = true;
                        break;
                    }
                }
                int bridgeLimit = crossesBoundary ? maxBoundaryBridge : maxBridge;
                if (begin != 0 && end < editorLf0.Length && count <= bridgeLimit) {
                    double left = editorLf0[begin - 1];
                    double right = editorLf0[end];
                    for (int offset = 0; offset < count; offset++) {
                        double ratio = (double)(offset + 1) / (count + 1);
                        editorLf0[begin + offset] = left + (right - left) * ratio;
                    }
                }
                begin = end;
            }
            List<TsnVoiceOutputPitch> result = new List<TsnVoiceOutputPitch>();
            int contentEnd = score.ContentStartFrame + score.ContentFrameCount;
            for (int frame = score.ContentStartFrame; frame < contentEnd; frame++) {
                double time = start + (frame - score.ContentStartFrame) * frameSeconds;
                double lf0 = editorLf0[frame];
                if (lf0 == TsnVoiceDsp.UnvoicedLf0 || double.IsNaN(lf0)
                    || double.IsInfinity(lf0)) {
                    continue;
                }
                double frequency = Math.Exp(lf0);
                if (double.IsNaN(frequency) || double.IsInfinity(frequency)
                    || frequency <= 0) {
                    continue;
                }
                double midi = TsnVoiceDsp.FreqToMidi(frequency);
                if (double.IsNaN(midi) || double.IsInfinity(midi)) {
                    continue;
                }
                TsnVoiceOutputPitch point = new TsnVoiceOutputPitch();
                point.TimeSeconds = time;
                point.MidiPitch = Math.Clamp(midi, 0.0, 127.0);
                result.Add(point);
            }
            return result;
        }

        static double[] RenderPrenet(TsnVoicePackage voice,
            Dictionary<string, SessionEntry> sessions,
            TsnVoiceAcousticParameters acoustic, double[] rawExcitation,
            Action<double> fraction = null) {
            Dictionary<string, string> config = voice.VocoderConfig;
            int segment = ConfigSize(config, "SEGMENT_LENGTH", 0, true);
            int left = ConfigSize(config, "LEFT_MARGIN_LENGTH", 0, true);
            int right = ConfigSize(config, "RIGHT_MARGIN_LENGTH", 0, true);
            int framePeriod = ConfigSize(config, "FRAME_PERIOD", 0, true);
            if (segment <= left + right
                || rawExcitation.Length != acoustic.Latent.GetLength(0) * framePeriod) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "声码器窗口配置无效");
            }
            int stride = segment - left - right;
            int latentRows = acoustic.Latent.GetLength(0);
            int latentColumns = acoustic.Latent.GetLength(1);
            int paddedFrames = ((latentRows + stride - 1) / stride) * stride;
            float[,] latentPadded = new float[left + paddedFrames + right, latentColumns];
            for (int frame = 0; frame < left + paddedFrames + right; frame++) {
                int source = Math.Clamp(frame - left, 0, latentRows - 1);
                for (int i = 0; i < latentColumns; i++) {
                    latentPadded[frame, i] = acoustic.Latent[source, i];
                }
            }
            float[] excitationPadded = new float[(left + paddedFrames + right) * framePeriod];
            for (int sample = 0; sample < excitationPadded.Length; sample++) {
                int source = Math.Clamp(sample - left * framePeriod, 0,
                    rawExcitation.Length - 1);
                excitationPadded[sample] = (float)rawExcitation[source];
            }
            SessionEntry model = GetModel(sessions, "vocoder");
            if (model.Session.InputMetadata.Count != 2
                || model.Session.OutputMetadata.Count != 1) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "声码器模型必须有两个输入、一个输出");
            }
            string sampleInputName = null;
            string frameInputName = null;
            foreach (KeyValuePair<string, NodeMetadata> input in model.Session.InputMetadata) {
                int[] shape = input.Value.Dimensions;
                if (shape.Length != 3 || shape[0] != 1) {
                    continue;
                }
                if (shape[1] == segment * framePeriod && shape[2] == 1) {
                    sampleInputName = input.Key;
                }
                if (shape[1] == segment && shape[2] == latentColumns) {
                    frameInputName = input.Key;
                }
            }
            if (sampleInputName == null || frameInputName == null) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "声码器输入张量与配置不匹配");
            }
            List<double> result = new List<double>(paddedFrames * framePeriod);
            float[] sampleFeed = new float[segment * framePeriod];
            float[] frameFeed = new float[segment * latentColumns];
            for (int center = 0; center < paddedFrames; center += stride) {
                Array.Copy(excitationPadded, center * framePeriod, sampleFeed, 0,
                    sampleFeed.Length);
                for (int frame = 0; frame < segment; frame++) {
                    for (int i = 0; i < latentColumns; i++) {
                        frameFeed[frame * latentColumns + i] =
                            latentPadded[center + frame, i];
                    }
                }
                float[] output = RunVocoder(model, sampleInputName, sampleFeed,
                    segment * framePeriod, frameInputName, frameFeed, segment, latentColumns);
                int expected = stride * framePeriod;
                if (output.Length != expected) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声码器返回的采样数不符合预期");
                }
                foreach (float value in output) {
                    result.Add(value);
                }
                fraction?.Invoke((center + stride) / (double)paddedFrames);
            }
            // 对照原生 result.resize：裁去填充窗多余采样；RemoveRange 一次完成，
            // 逐个 RemoveAt 为 O(n²)，长乐句会浪费数分钟。
            if (result.Count > rawExcitation.Length) {
                result.RemoveRange(rawExcitation.Length,
                    result.Count - rawExcitation.Length);
            }
            return result.ToArray();
        }

        static float[] RunVocoder(SessionEntry model, string sampleName,
            float[] sampleValues, int sampleCount, string frameName,
            float[] frameValues, int segment, int latentColumns) {
            foreach (float value in sampleValues) {
                if (float.IsNaN(value) || float.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声码器输入存在 NaN/Inf");
                }
            }
            foreach (float value in frameValues) {
                if (float.IsNaN(value) || float.IsInfinity(value)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声码器输入存在 NaN/Inf");
                }
            }
            List<NamedOnnxValue> inputs = new List<NamedOnnxValue>();
            inputs.Add(NamedOnnxValue.CreateFromTensor(sampleName,
                new DenseTensor<float>(sampleValues, new int[] { 1, sampleCount, 1 })));
            inputs.Add(NamedOnnxValue.CreateFromTensor(frameName,
                new DenseTensor<float>(frameValues, new int[] { 1, segment, latentColumns })));
            try {
                lock (model.RunLock) {
                    using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                        model.Session.Run(inputs)) {
                        float[] output = results.First().AsTensor<float>().ToArray();
                        foreach (float value in output) {
                            if (float.IsNaN(value) || float.IsInfinity(value)) {
                                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                                    "声码器返回 NaN/Inf");
                            }
                        }
                        return output;
                    }
                }
            } catch (TsnVoiceException) {
                throw;
            } catch (Exception e) {
                throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                    "声码器推理失败：" + e.Message, e);
            }
        }

        static TsnVoiceSynthesisOutput BuildMetadata(PreparedScore score,
            List<TsnVoiceInputNote> notes, int sampleRate, int framePeriod) {
            TsnVoiceSynthesisOutput result = new TsnVoiceSynthesisOutput();
            result.SampleRate = sampleRate;
            result.StartTime = notes[0].StartSeconds;
            double frameSeconds = (double)framePeriod / sampleRate;
            int contentEnd = score.ContentStartFrame + score.ContentFrameCount;
            for (int frame = score.ContentStartFrame; frame < contentEnd; frame++) {
                double time = result.StartTime
                    + (frame - score.ContentStartFrame) * frameSeconds;
                TsnVoiceOutputPitch point = new TsnVoiceOutputPitch();
                point.TimeSeconds = time;
                point.MidiPitch = score.Controls[frame].MidiPitch;
                result.Pitch.Add(point);
            }
            for (int phone = 0; phone < score.LabelPhonemes.Count; phone++) {
                if (score.PhoneNoteIds[phone].Length == 0) {
                    continue;
                }
                TsnVoiceOutputPhoneme phoneme = new TsnVoiceOutputPhoneme();
                phoneme.NoteId = score.PhoneNoteIds[phone];
                phoneme.Symbol = score.LabelPhonemes[phone];
                phoneme.DurationSeconds =
                    (score.PhoneEnds[phone] - score.PhoneStarts[phone]) * frameSeconds;
                phoneme.StretchWeight = score.PhoneStretchWeights[phone];
                phoneme.BodyOffsetSeconds = score.PhoneBodyOffsets[phone];
                phoneme.IsLeading = score.PhoneIsLeading[phone];
                result.Phonemes.Add(phoneme);
            }
            return result;
        }

        /// <summary>
        /// 完整合成：时序→语言→声学→激励→声码→裁剪。
        /// </summary>
        public static TsnVoiceSynthesisOutput Synthesize(
            TsnVoicePackage voice, List<TsnVoiceInputNote> notes,
            List<TsnVoicePitchPoint> pitchPoints, List<TsnVoiceControlPoint> controlPoints,
            Func<bool> isCancelled, Action<float, string> reportProgress,
            Action<TsnVoiceSynthesisOutput> reportMetadata) {
            if (notes.Count > 100000 || pitchPoints.Count > 10000000
                || controlPoints.Count > 10000000) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidArgument,
                    "合成请求过大");
            }
            ValidateInput(voice, notes, pitchPoints, controlPoints);
            if (isCancelled()) {
                throw new TsnVoiceException(TsnVoiceStatus.Cancelled, "合成已取消");
            }
            reportProgress(0.02f, "validating");
            PreparedScore score = PrepareScore(voice, notes, pitchPoints, controlPoints);
            reportProgress(0.12f, "timing");
            if (isCancelled()) {
                throw new TsnVoiceException(TsnVoiceStatus.Cancelled, "合成已取消");
            }
            int sampleRate = ConfigSize(voice.Config, "SAMPLING_FREQUENCY", 0, true);
            int framePeriod = ConfigSize(voice.Config, "FRAME_PERIOD", 0, true);
            if (reportMetadata != null) {
                reportMetadata(BuildMetadata(score, notes, sampleRate, framePeriod));
            }
            Dictionary<string, SessionEntry> sessions = GetSessions(voice);
            float[,] frameContext;
            float[,] linguistic;
            if (voice.LegacyContextLayout) {
                linguistic = LegacyLinguisticFeatures(voice, sessions, score,
                    out frameContext,
                    f => reportProgress(0.12f + (float)(0.22 * f), "linguistic"));
            } else {
                linguistic = ModernLinguisticFeatures(voice, sessions, score,
                    notes[0].Language, out frameContext,
                    f => reportProgress(0.12f + (float)(0.22 * f), "linguistic"));
            }
            reportProgress(0.34f, "linguistic");
            if (isCancelled()) {
                throw new TsnVoiceException(TsnVoiceStatus.Cancelled, "合成已取消");
            }
            RunAcousticModels(voice, sessions, score, linguistic, frameContext,
                f => reportProgress(0.34f + (float)(0.15 * f), "acoustic"),
                f => reportProgress(0.49f + (float)(0.15 * f), "acoustic"),
                out float[,] stage1, out float[,] stage2);
            TsnVoiceAcousticParameters acoustic = TsnVoiceAcoustic.Postprocess(
                voice, stage1, stage2,
                score.FramePhonemes.ToArray(), score.Controls.ToArray());
            ApplyPitchConstraints(score, acoustic);
            SmoothFreePitchBoundaries(score, acoustic);
            reportProgress(0.64f, "acoustic");
            if (isCancelled()) {
                throw new TsnVoiceException(TsnVoiceStatus.Cancelled, "合成已取消");
            }
            double defaultAlpha = ConfigFirstNumber(voice.Config, "ALPHA", 0, true);
            double minAlpha = ConfigNumber(voice.Config, "MIN_ALPHA", defaultAlpha - 0.05);
            double maxAlpha = ConfigNumber(voice.Config, "MAX_ALPHA", defaultAlpha + 0.05);
            if (minAlpha > maxAlpha || defaultAlpha < minAlpha || defaultAlpha > maxAlpha) {
                throw new TsnVoiceException(TsnVoiceStatus.InvalidVoice,
                    "语音 alpha 范围不一致");
            }
            double[] alpha = new double[score.Controls.Count];
            for (int frame = 0; frame < alpha.Length; frame++) {
                // ALP 为用户偏移，非 MLSA 配置替换：±1.0 映射默认 0.55 附近 ±0.05。
                alpha[frame] = Math.Clamp(
                    defaultAlpha + score.Controls[frame].Alpha * 0.05,
                    minAlpha, maxAlpha);
            }
            double[] raw = TsnVoiceDsp.GenerateRawExcitation(acoustic.Lf0,
                acoustic.Bap, sampleRate, framePeriod, defaultAlpha);
            double[] enhanced = RenderPrenet(voice, sessions, acoustic, raw,
                f => reportProgress(0.64f + (float)(0.30 * f), "vocoder"));
            int pade = ConfigSize(voice.Config, "PADE_ORDER", 0, true);
            double[] pcm = TsnVoiceDsp.FilterMgcExcitation(enhanced, acoustic.Mgc,
                framePeriod, alpha, pade);
            TsnVoiceSynthesisOutput result = BuildMetadata(score, notes,
                sampleRate, framePeriod);
            result.Samples = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++) {
                double normalized = pcm[i] / 32768.0;
                if (double.IsNaN(normalized) || double.IsInfinity(normalized)) {
                    throw new TsnVoiceException(TsnVoiceStatus.ModelError,
                        "声码器产生 NaN/Inf");
                }
                result.Samples[i] = (float)normalized;
            }
            int contentSampleBegin = score.ContentStartFrame * framePeriod;
            int contentSamples = score.ContentFrameCount * framePeriod;
            int contentSampleEnd = contentSampleBegin + contentSamples;
            if (contentSamples == 0 || contentSampleEnd > result.Samples.Length) {
                throw new TsnVoiceException(TsnVoiceStatus.InternalError,
                    "渲染内容区间超出上下文音频");
            }
            const double attackFadeSeconds = 0.015;
            int fadeSamples = Math.Min(contentSamples / 2,
                (int)Math.Round(sampleRate * attackFadeSeconds,
                    MidpointRounding.AwayFromZero));
            for (int sample = 0; sample < fadeSamples; sample++) {
                double phase = (double)(sample + 1) / fadeSamples;
                float gain = (float)(0.5 * (1.0 - Math.Cos(Math.PI * phase)));
                result.Samples[contentSampleBegin + sample] *= gain;
            }
            int fadeOutFrames = ConfigSize(voice.Config,
                "SYNTHESIZER_FADE_OUT_FRAMES", 20);
            int releaseSamples = Math.Min(contentSamples, fadeOutFrames * framePeriod);
            for (int sample = 0; sample < releaseSamples; sample++) {
                double phase = releaseSamples <= 1
                    ? 1.0
                    : (double)sample / (releaseSamples - 1);
                float gain = (float)(0.5 * (1.0 + Math.Cos(Math.PI * phase)));
                result.Samples[contentSampleEnd - releaseSamples + sample] *= gain;
            }
            float[] contentAudio = new float[contentSamples];
            Array.Copy(result.Samples, contentSampleBegin, contentAudio, 0,
                contentSamples);
            result.Samples = contentAudio;
            result.Pitch = AcousticPitchPoints(score, notes, acoustic,
                sampleRate, framePeriod);
            reportProgress(1.0f, "complete");
            Log.Information("TsnVoice 合成完成：{Frames} 帧，{Samples} 采样",
                score.ContentFrameCount, contentSamples);
            return result;
        }
    }
}
