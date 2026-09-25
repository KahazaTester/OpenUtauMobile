using System;
using System.Collections.Generic;
using OpenUtau.Core.Render;
using OpenUtau.Core.TsnVoice;
using OpenUtau.Core.Ustx;

namespace OpenUtauMobile.Controls;

/// <summary>
/// TsnVoice 合成实际时值：对照参考实现的已发布合成音节，
/// 面板直接显示模型音素（起止、符号），不再显示管线均分；
/// 无模型数据的音符（错误、未渲染）仍显示管线卡片以保留编辑与报错。
/// </summary>
public class TsnVoiceModelSpan {
    public double AbsStartTick;
    public double AbsEndTick;
    public string Symbol = string.Empty;
    public bool IsLeading;
    public double AbsBodyTick;
}

public static class TsnVoiceTimingOverlay {
    /// <summary>
    /// 收集分片内所有乐句的模型音素（绝对 Tick，有序）。
    /// </summary>
    public static List<TsnVoiceModelSpan> GetPhones(UVoicePart? part) {
        List<TsnVoiceModelSpan> result = new List<TsnVoiceModelSpan>();
        if (part == null || part.renderPhrases == null) {
            return result;
        }
        foreach (RenderPhrase phrase in part.renderPhrases) {
            if (!(phrase.singer is TsnVoiceSinger)) {
                continue;
            }
            List<TsnVoiceRenderer.TsnVoiceRenderedPhone> phones;
            try {
                phones = TsnVoiceRenderer.LoadRenderedPhonemes(phrase);
            } catch {
                continue;
            }
            foreach (TsnVoiceRenderer.TsnVoiceRenderedPhone phone in phones) {
                TsnVoiceModelSpan span = new TsnVoiceModelSpan();
                span.AbsStartTick = phrase.position + phone.StartTick;
                span.AbsEndTick = phrase.position + phone.EndTick;
                span.Symbol = phone.Symbol;
                span.IsLeading = phone.IsLeading;
                span.AbsBodyTick = phrase.position + phone.BodyTick;
                if (span.AbsEndTick > span.AbsStartTick) {
                    result.Add(span);
                }
            }
        }
        result.Sort((left, right) => left.AbsStartTick.CompareTo(right.AbsStartTick));
        return result;
    }

    public static bool IsTsnVoicePart(UVoicePart? part) {
        if (part == null) {
            return false;
        }
        try {
            var project = OpenUtau.Core.DocManager.Inst.Project;
            if (project == null || part.trackNo < 0 || part.trackNo >= project.tracks.Count) {
                return false;
            }
            return project.tracks[part.trackNo].Singer is TsnVoiceSinger;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// 管线音素是否被模型音素覆盖（有交叠即视为覆盖）。
    /// </summary>
    public static bool IsCovered(List<TsnVoiceModelSpan> spans, double absStart, double absEnd) {
        int lo = 0;
        int hi = spans.Count;
        while (lo < hi) {
            int mid = (lo + hi) / 2;
            if (spans[mid].AbsEndTick <= absStart) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }
        return lo < spans.Count && spans[lo].AbsStartTick < absEnd;
    }
}
