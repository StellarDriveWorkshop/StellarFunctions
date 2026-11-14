using System;
using System.Globalization;

namespace StellarFunctions.Support;

// State belongs to a single laid-out label. Measurement reports the fraction
// of its usable rectangle needed by an uncut line at a given size.
internal sealed class MeasuredScreenLine
{
    private readonly string _ticker;
    private readonly int[] _starts;
    private readonly ScreenLineFrame?[] _windows;
    private readonly float _fontSize;
    private readonly ScreenLineFrame _stationary;
    private double _offset;

    internal MeasuredScreenLine(string content, float preferred, float minimum,
        Func<string, float, float> measure)
    {
        float demand = measure(content, preferred);
        _stationary = new ScreenLineFrame(content, preferred);
        if (demand <= 1f) return;

        if (measure(content, minimum) <= 1f)
        {
            float size = Math.Max(minimum, preferred / demand);
            _stationary = new ScreenLineFrame(content, measure(content, size) <= 1f ? size : minimum);
            return;
        }

        _fontSize = minimum;
        _ticker = content + "   ";
        _starts = StringInfo.ParseCombiningCharacters(_ticker);
        _windows = new ScreenLineFrame?[_starts.Length];
    }

    internal ScreenLineFrame Present(float steps, Func<string, float, float> measure)
    {
        if (_windows == null) return _stationary;

        // Limit catch-up so even a slow frame cannot skip a text element.
        int start = (int)_offset;
        _offset = (_offset + Math.Min(steps, 1f)) % _starts.Length;
        if (_windows[start] is ScreenLineFrame cached) return cached;

        // Find the longest fitting prefix at this offset. The gap is part
        // of the ticker so the end of a name stays distinct from its start.
        int low = 1;
        int high = _starts.Length;
        var first = Window(start, 1);
        float demand = measure(first, _fontSize);
        var result = new ScreenLineFrame(first, _fontSize);
        if (demand > 1f)
        {
            // An unusually large single text element still needs a turn.
            float size = _fontSize / demand;
            while (size > .001f && measure(first, size) > 1f)
                size *= .5f;
            result = new ScreenLineFrame(first, size);
        }
        else
        {
            while (low < high)
            {
                int count = low + (high - low + 1) / 2;
                string candidate = Window(start, count);
                if (measure(candidate, _fontSize) <= 1f)
                {
                    result = new ScreenLineFrame(candidate, _fontSize);
                    low = count;
                }
                else high = count - 1;
            }
        }

        _windows[start] = result;
        return result;
    }

    private string Window(int start, int count)
    {
        int end = start + count;
        int first = _starts[start];
        if (end < _starts.Length) return _ticker.Substring(first, _starts[end] - first);
        if (end == _starts.Length) return _ticker.Substring(first);
        return _ticker.Substring(first) + _ticker.Substring(0, _starts[end - _starts.Length]);
    }

    internal static string SingleLine(string text)
    {
        char[] normalized = null;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (char.IsControl(value) || value is '\u2028' or '\u2029')
            {
                normalized ??= text.ToCharArray();
                normalized[i] = ' ';
            }
        }
        return normalized == null ? text : new string(normalized);
    }
}

internal readonly struct ScreenLineFrame
{
    internal readonly string Text;
    internal readonly float FontSize;

    internal ScreenLineFrame(string text, float fontSize)
    {
        Text = text;
        FontSize = fontSize;
    }
}
