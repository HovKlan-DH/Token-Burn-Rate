using System;
using System.Collections.Generic;

namespace TokenBurnRate.Models;

/// <summary>
/// Marks runs inside a bar caption that should be drawn emphasised.
///
/// The captions are built as plain strings by the view models and drawn by
/// <c>UsageBar</c> as a single shaped run, so there is nowhere to hang a style. Rather
/// than give the bar a second "which words" property that every caller would have to keep
/// in step with the text, the emphasis travels in the string itself between two control
/// characters that cannot occur in real caption text, and the bar splits them back out
/// just before it shapes the run.
/// </summary>
public static class UsageText
{
    private const char Open = '\u0002';
    private const char Close = '\u0003';

    /// <summary>Wraps <paramref name="text"/> so the bar draws it emphasised.</summary>
    public static string Highlight(string text) => Open + text + Close;

    /// <summary>
    /// Strips the markers, for anywhere the caption is shown as ordinary text - a tooltip,
    /// a clipboard copy, an accessibility name.
    ///
    /// Tests for either marker, not just the opener: the two are stripped together, so a
    /// string carrying only a stray Close - a caption sliced mid-run, say - would otherwise
    /// take the untouched path and leave a U+0003 to render as a control character or a
    /// tofu box in whatever showed it.
    /// </summary>
    public static string Plain(string text)
        => text.AsSpan().IndexOfAny(Open, Close) < 0
            ? text
            : text.Replace(Open.ToString(), "").Replace(Close.ToString(), "");

    /// <summary>
    /// Splits a marked caption into the bare string plus the ranges to emphasise, with the
    /// offsets already rebased onto the stripped text.
    /// </summary>
    public static (string Text, IReadOnlyList<(int Start, int Length)> Spans) Split(string text)
    {
        // Either marker, for the reason Plain tests both: a stray Close still has to be
        // stripped, or the bar shapes a control character into the run.
        if (text.AsSpan().IndexOfAny(Open, Close) < 0)
            return (text, Array.Empty<(int, int)>());

        var sb = new System.Text.StringBuilder(text.Length);
        var spans = new List<(int, int)>();
        var start = -1;

        foreach (var c in text)
        {
            if (c == Open) { start = sb.Length; continue; }
            if (c == Close)
            {
                if (start >= 0 && sb.Length > start) spans.Add((start, sb.Length - start));
                start = -1;
                continue;
            }
            sb.Append(c);
        }

        return (sb.ToString(), spans);
    }
}
