using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.TsnVoice {
    /// <summary>
    /// TSNVOICE 自动音高服务：仅对本节新建的音符生效。
    /// 新音符落子即标记，渲染完成后把模型音高烘焙为 PITD（可撤销、可保存、
    /// 可在参数面板继续手调）；移动/变调/拉伸/改词保持标记，改动后重烘；
    /// 手调音高锚点、颤音或该段 PITD 后永久取消标记；已存在工程的音符
    /// 永不触碰（打开与渲染 .ustx 不会产生自动音高）。
    /// </summary>
    public sealed class TsnVoiceAutoPitchService : ICmdSubscriber {
        public static TsnVoiceAutoPitchService Inst { get; } = new TsnVoiceAutoPitchService();

        const int BakeDebounceMs = 2000;

        readonly object gate = new object();
        readonly Dictionary<UVoicePart, HashSet<UNote>> marked =
            new Dictionary<UVoicePart, HashSet<UNote>>();
        readonly Dictionary<UVoicePart, CancellationTokenSource> debouncers =
            new Dictionary<UVoicePart, CancellationTokenSource>();
        readonly Dictionary<UVoicePart, HashSet<ulong>> bakedHashes =
            new Dictionary<UVoicePart, HashSet<ulong>>();
        readonly HashSet<UVoicePart> pitdDirty = new HashSet<UVoicePart>();
        readonly HashSet<UVoicePart> suppressPitd = new HashSet<UVoicePart>();
        readonly Dictionary<UVoicePart, Tuple<List<int>, List<int>>> pitdBaseline =
            new Dictionary<UVoicePart, Tuple<List<int>, List<int>>>();
        readonly LoadRenderedPitch pitchLoader = new LoadRenderedPitch();

        TsnVoiceAutoPitchService() { }

        public void Initialize() {
            DocManager.Inst.AddSubscriber(this);
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (!TsnVoiceParameters.IsAutoPitchEnabled()) {
                return;
            }
            switch (cmd) {
                case AddNoteCommand add:
                    MarkNotes(add.Part, add.Notes);
                    break;
                case RemoveNoteCommand:
                case MoveNoteCommand:
                case ResizeNoteCommand:
                case ChangeNoteLyricCommand:
                case ChangeNoteTuningCommand:
                    if (cmd is NoteCommand touched) {
                        ScheduleIfMarked(touched.Part, touched.Notes);
                    }
                    break;
                case ResetPitchPointsCommand reset:
                    if (reset.Note != null && reset.Part != null) {
                        MarkNotes(reset.Part, new UNote[] { reset.Note });
                    }
                    break;
                case PitchExpCommand pitched:
                    // 手调音高锚点后永久取消标记（含撤销恢复的重新标记）。
                    if (pitched.Note != null && pitched.Part != null) {
                        if (isUndo) {
                            MarkNotes(pitched.Part, new UNote[] { pitched.Note });
                        } else {
                            UnmarkNotes(pitched.Part, new UNote[] { pitched.Note });
                        }
                    }
                    break;
                case VibratoCommand vibrato:
                    if (vibrato.Notes != null && vibrato.Part != null) {
                        if (isUndo) {
                            MarkNotes(vibrato.Part, vibrato.Notes);
                        } else {
                            UnmarkNotes(vibrato.Part, vibrato.Notes);
                        }
                    }
                    break;
                case SetCurveCommand curve:
                    if (curve.Key == Format.Ustx.PITD && curve.Part != null) {
                        MarkPitdDirty(curve.Part);
                    }
                    break;
                case MergedSetCurveCommand merged:
                    if (merged.Key == Format.Ustx.PITD && merged.Part != null) {
                        MarkPitdDirty(merged.Part);
                    }
                    break;
                case PasteCurveCommand pasted:
                    if (pasted.Key == Format.Ustx.PITD && pasted.Part != null) {
                        MarkPitdDirty(pasted.Part);
                    }
                    break;
                case ClearCurveCommand cleared:
                    if (cleared.Key == Format.Ustx.PITD && cleared.Part != null) {
                        MarkPitdDirty(cleared.Part);
                    }
                    break;
                case PartRenderedNotification rendered:
                    if (rendered.part is UVoicePart voicePart) {
                        ScheduleBake(voicePart);
                    }
                    break;
            }
        }

        static bool IsTsnVoicePart(UVoicePart part) {
            try {
                UProject project = DocManager.Inst.Project;
                if (part == null || project == null
                    || part.trackNo < 0 || part.trackNo >= project.tracks.Count) {
                    return false;
                }
                return project.tracks[part.trackNo].Singer is TsnVoiceSinger;
            } catch {
                return false;
            }
        }

        void MarkNotes(UVoicePart part, IEnumerable<UNote> notes) {
            if (part == null || notes == null || !IsTsnVoicePart(part)) {
                return;
            }
            lock (gate) {
                if (!marked.TryGetValue(part, out HashSet<UNote> set)) {
                    set = new HashSet<UNote>();
                    marked[part] = set;
                }
                foreach (UNote note in notes) {
                    if (note != null) {
                        set.Add(note);
                    }
                }
            }
            ScheduleBake(part);
        }

        void UnmarkNotes(UVoicePart part, IEnumerable<UNote> notes) {
            if (part == null || notes == null) {
                return;
            }
            lock (gate) {
                if (marked.TryGetValue(part, out HashSet<UNote> set)) {
                    foreach (UNote note in notes) {
                        set.Remove(note);
                    }
                    if (set.Count == 0) {
                        marked.Remove(part);
                    }
                }
            }
        }

        void MarkPitdDirty(UVoicePart part) {
            lock (gate) {
                // 自身烘焙写入不计入用户改动。
                if (!suppressPitd.Contains(part)) {
                    pitdDirty.Add(part);
                }
            }
        }

        void ScheduleIfMarked(UVoicePart part, IEnumerable<UNote> notes) {
            if (part == null || notes == null) {
                return;
            }
            bool any = false;
            lock (gate) {
                if (marked.TryGetValue(part, out HashSet<UNote> set)) {
                    foreach (UNote note in notes) {
                        if (note != null && set.Contains(note)) {
                            any = true;
                            break;
                        }
                    }
                }
            }
            if (any) {
                ScheduleBake(part);
            }
        }

        void ScheduleBake(UVoicePart part) {
            if (part == null) {
                return;
            }
            CancellationTokenSource cts;
            lock (gate) {
                if (debouncers.TryGetValue(part, out CancellationTokenSource existing)) {
                    try {
                        existing.Cancel();
                        existing.Dispose();
                    } catch {
                    }
                }
                cts = new CancellationTokenSource();
                debouncers[part] = cts;
            }
            CancellationToken token = cts.Token;
            Task.Run(async () => {
                try {
                    await Task.Delay(BakeDebounceMs, token).ConfigureAwait(false);
                } catch (TaskCanceledException) {
                    return;
                }
                if (token.IsCancellationRequested) {
                    return;
                }
                lock (gate) {
                    if (debouncers.TryGetValue(part, out CancellationTokenSource current)
                        && current == cts) {
                        debouncers.Remove(part);
                    }
                }
                BakeForPart(part);
            });
        }

        void BakeForPart(UVoicePart part) {
            UProject project;
            try {
                project = DocManager.Inst.Project;
            } catch {
                return;
            }
            if (project == null || !project.parts.Contains(part) || !IsTsnVoicePart(part)) {
                lock (gate) {
                    marked.Remove(part);
                    bakedHashes.Remove(part);
                    pitdBaseline.Remove(part);
                    pitdDirty.Remove(part);
                }
                return;
            }
            if (!TsnVoiceParameters.IsAutoPitchEnabled()) {
                return;
            }
            List<UNote> targets;
            lock (gate) {
                if (!marked.TryGetValue(part, out HashSet<UNote> set) || set.Count == 0) {
                    return;
                }
                targets = set.Where(n => n != null && part.notes.Contains(n)).ToList();
                set.IntersectWith(targets);
                if (set.Count == 0) {
                    marked.Remove(part);
                    return;
                }
            }
            ApplyPitdDiff(part, targets);
            lock (gate) {
                if (!marked.TryGetValue(part, out HashSet<UNote> set) || set.Count == 0) {
                    return;
                }
                targets = set.Where(n => n != null && part.notes.Contains(n)).ToList();
            }
            if (targets.Count == 0) {
                return;
            }
            var renderer = project.tracks[part.trackNo].RendererSettings.Renderer;
            if (renderer == null || !renderer.SupportsRenderPitch) {
                return;
            }
            // 仅对已有渲染缓存的乐句烘焙，避免无缓存时的误导提示。
            bool anyCached = false;
            foreach (Render.RenderPhrase phrase in part.renderPhrases) {
                try {
                    if (renderer.LoadRenderedPitch(phrase) != null) {
                        anyCached = true;
                        break;
                    }
                } catch {
                }
            }
            if (!anyCached) {
                return;
            }
            // 哈希去重：内容未变的乐句不重复写 PITD，避免撤销历史膨胀。
            HashSet<ulong> baked;
            lock (gate) {
                if (!bakedHashes.TryGetValue(part, out baked)) {
                    baked = new HashSet<ulong>();
                    bakedHashes[part] = baked;
                }
            }
            List<UNote> fresh = new List<UNote>();
            foreach (UNote note in targets) {
                bool covered = false;
                foreach (Render.RenderPhrase phrase in part.renderPhrases) {
                    foreach (Render.RenderNote renderNote in phrase.notes) {
                        if (phrase.position + renderNote.position
                            == part.position + note.position
                            && renderNote.duration == note.duration) {
                            if (baked.Contains(phrase.hash)) {
                                covered = true;
                            }
                            break;
                        }
                    }
                    if (covered) {
                        break;
                    }
                }
                if (!covered) {
                    fresh.Add(note);
                }
            }
            if (fresh.Count == 0) {
                return;
            }
            // 自身写入期间忽略 PITD 脏标记，基线在命令落盘后刷新。
            lock (gate) {
                suppressPitd.Add(part);
            }
            try {
                pitchLoader.RunAsync(project, part, fresh, DocManager.Inst,
                    (_, __) => { }, CancellationToken.None, false);
            } catch (Exception e) {
                Log.Warning(e, "TSNVOICE 自动音高烘焙失败");
                lock (gate) {
                    suppressPitd.Remove(part);
                }
                return;
            }
            // 仅记录实际处理的乐句哈希，未覆盖的不计入，避免漏烘。
            HashSet<ulong> positions = new HashSet<ulong>();
            lock (gate) {
                if (!bakedHashes.TryGetValue(part, out HashSet<ulong> done)) {
                    done = new HashSet<ulong>();
                    bakedHashes[part] = done;
                }
                if (done.Count > 400) {
                    done.Clear();
                }
                HashSet<int> freshPositions = new HashSet<int>();
                foreach (UNote note in fresh) {
                    freshPositions.Add(part.position + note.position);
                }
                foreach (Render.RenderPhrase phrase in part.renderPhrases) {
                    foreach (Render.RenderNote renderNote in phrase.notes) {
                        if (freshPositions.Contains(phrase.position + renderNote.position)) {
                            positions.Add(phrase.hash);
                            break;
                        }
                    }
                }
                foreach (ulong hash in positions) {
                    done.Add(hash);
                }
            }
            DocManager.Inst.PostOnUIThread(() => {
                lock (gate) {
                    suppressPitd.Remove(part);
                }
                RefreshPitdBaseline(part);
            });
        }

        void ApplyPitdDiff(UVoicePart part, List<UNote> targets) {
            List<int> curXs;
            List<int> curYs;
            var curve = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD);
            if (curve == null) {
                curXs = new List<int>();
                curYs = new List<int>();
            } else {
                curXs = new List<int>(curve.xs);
                curYs = new List<int>(curve.ys);
            }
            Tuple<List<int>, List<int>> baseline;
            lock (gate) {
                if (!pitdDirty.Contains(part)) {
                    return;
                }
                pitdDirty.Remove(part);
                if (!pitdBaseline.TryGetValue(part, out baseline)) {
                    pitdBaseline[part] = Tuple.Create(curXs, curYs);
                    return;
                }
            }
            // 曲线插值影响相邻点区间：任一差异点的前后区间都视为改动。
            SortedSet<int> anchors = new SortedSet<int>(baseline.Item1);
            foreach (int x in curXs) {
                anchors.Add(x);
            }
            List<int> ordered = anchors.ToList();
            HashSet<int> changed = new HashSet<int>();
            for (int i = 0; i < ordered.Count; i++) {
                int x = ordered[i];
                if (SamplePoints(baseline.Item1, baseline.Item2, x)
                    != SamplePoints(curXs, curYs, x)) {
                    changed.Add(x);
                    if (i > 0) {
                        changed.Add(ordered[i - 1]);
                    }
                    if (i + 1 < ordered.Count) {
                        changed.Add(ordered[i + 1]);
                    }
                }
            }
            if (changed.Count == 0) {
                return;
            }
            int start = changed.Min();
            int end = changed.Max();
            List<UNote> overlapped = new List<UNote>();
            foreach (UNote note in targets) {
                if (note.End > start && note.position < end) {
                    overlapped.Add(note);
                }
            }
            if (overlapped.Count > 0) {
                Log.Information("TSNVOICE 自动音高：{Count} 个音符被手调 PITD 覆盖，转为手动",
                    overlapped.Count);
                UnmarkNotes(part, overlapped);
            }
        }

        static int SamplePoints(List<int> xs, List<int> ys, int x) {
            if (xs.Count == 0 || ys.Count == 0) {
                return 0;
            }
            int best = 0;
            int bestDist = Math.Abs(xs[0] - x);
            for (int i = 1; i < xs.Count && i < ys.Count; i++) {
                int dist = Math.Abs(xs[i] - x);
                if (dist < bestDist) {
                    bestDist = dist;
                    best = i;
                }
            }
            return best < ys.Count ? ys[best] : 0;
        }

        void RefreshPitdBaseline(UVoicePart part) {
            try {
                var curve = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD);
                List<int> xs = curve != null ? new List<int>(curve.xs) : new List<int>();
                List<int> ys = curve != null ? new List<int>(curve.ys) : new List<int>();
                lock (gate) {
                    pitdBaseline[part] = Tuple.Create(xs, ys);
                    pitdDirty.Remove(part);
                }
            } catch {
            }
        }
    }
}
