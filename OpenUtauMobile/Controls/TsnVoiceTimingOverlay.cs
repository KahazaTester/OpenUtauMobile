using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using OpenUtau.Core.Render;
using OpenUtau.Core.TsnVoice;
using OpenUtau.Core.Ustx;
using OpenUtauMobile.Themes.OpenUtauMobile.Runtime;

namespace OpenUtauMobile.Controls;

/// <summary>
/// TsnVoice 合成音素定时叠加层：显示渲染产物中的模型实际时值
/// （音素边界、前置辅音区、主体起点），对照参考实现的已发布合成音节。
/// 只读渲染缓存，不改动文档；无缓存时静默跳过。
/// </summary>
public static class TsnVoiceTimingOverlay {
    public static void Draw(DrawingContext context, UVoicePart? part,
        double tickOffset, double tickWidth,
        double top, double height, double viewLeftTick, double viewRightTick) {
        if (part == null || part.renderPhrases == null) {
            return;
        }
        IPen boundaryPen = ThemeResources.GetPen("Sem.Color.Primary", 1.5);
        IPen bodyPen = ThemeResources.GetPen("Sem.Color.Secondary", 2.0);
        IBrush leadingBrush = ThemeResources.GetBrush("Sem.Color.PrimaryContainer");
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
                double absStart = phrase.position + phone.StartTick;
                double absEnd = phrase.position + phone.EndTick;
                if (absEnd < viewLeftTick || absStart > viewRightTick) {
                    continue;
                }
                double x1 = (absStart - tickOffset) * tickWidth;
                double x2 = (absEnd - tickOffset) * tickWidth;
                if (phone.IsLeading && x2 > x1 + 0.5) {
                    using (context.PushOpacity(0.22)) {
                        context.DrawRectangle(leadingBrush, null,
                            new Rect(x1, top, x2 - x1, height));
                    }
                }
                context.DrawLine(boundaryPen,
                    new Point(x1, top), new Point(x1, top + height));
                double absBody = phrase.position + phone.BodyTick;
                if (absBody >= absStart && absBody <= absEnd) {
                    double xb = (absBody - tickOffset) * tickWidth;
                    context.DrawLine(bodyPen,
                        new Point(xb, top), new Point(xb, top + Math.Min(10.0, height)));
                }
            }
        }
    }
}
