using System.Globalization;
using StellarFunctions;
using StellarFunctions.Support;

internal static class CallFaceTextChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(CallFaceText.Cardinal(1f, 0f) == 0, "dominant +Z selects forward");
        check(CallFaceText.Cardinal(0f, 1f) == 1, "dominant +X selects right");
        check(CallFaceText.Cardinal(-1f, 0f) == 2, "dominant -Z selects back");
        check(CallFaceText.Cardinal(0f, -1f) == 3, "dominant -X selects left");
        check(CallFaceText.Cardinal(0f, 0f) == 3, "zero view direction picks left like vanilla");

        float Width(string text, float size) => Weight(text) * size / 8f;
        ScreenLineFrame Step(MeasuredScreenLine line, float steps = 0f) => line.Present(steps, Width);
        MeasuredScreenLine Layout(string name) => new(name, 2f, 1f, Width);

        var shortName = Layout("AB");
        check(Step(shortName, 100).Text == "AB" && Step(shortName).FontSize == 2f,
            "short names stay stationary at the preferred size");
        var medium = Step(Layout("ABCDEF"));
        check(medium.Text == "ABCDEF" && Math.Abs(medium.FontSize - 4f / 3f) < .00001f,
            "medium names shrink just enough to fit");
        var threshold = Step(Layout("ABCDEFGH"));
        check(threshold.Text == "ABCDEFGH" && threshold.FontSize == 1f,
            "a name that fits at the minimum size stays whole");

        const string wide = "WWWWWWWiZ";
        var line = Layout(wide);
        var windows = new List<string>();
        for (int i = 0; i < wide.Length + 3; i++)
        {
            var frame = Step(line, 1f);
            windows.Add(frame.Text);
            check(frame.FontSize == 1f && Width(frame.Text, frame.FontSize) <= 1f,
                "every long-name window fits at its readable floor");
        }
        check(windows.Distinct().Count() > 4 && windows.Any(text => text.Contains("Z")),
            "the wide name progresses through different windows and shows the tail");
        check(windows.Select(window => window[0]).SequenceEqual(wide + "   "),
            "every position in the full name appears in order");
        check(Step(line).Text == windows[0], "scroll wraps through the gap to the start");

        line = Layout(wide);
        string initial = Step(line, .5f).Text;
        check(Step(line, .5f).Text == initial && Step(line).Text == windows[1],
            "sub-step time accumulates without skipping a window");
        line = Layout(wide);
        Step(line, 6f);
        check(Step(line).Text == windows[1], "a slow frame does not skip any text position");
        check(Step(Layout("iiiiiiiii")).Text == "iiiiiiiii",
            "the same number of narrow glyphs fit without scrolling");
        float WideDisplay(string text, float size) => Width(text, size) / 4f;
        var largeDisplay = new MeasuredScreenLine(wide, 2f, 1f, WideDisplay);
        check(largeDisplay.Present(0f, WideDisplay).Text == wide,
            "the same name can stay whole on a larger display");

        var renamed = Layout("NEWWWWWWWZ");
        check(Step(renamed).Text.StartsWith("NEW"), "rename starts at the new name's first glyph");
        check(Step(Layout("")).FontSize == 2f && Step(Layout("X")).FontSize == 2f,
            "empty or short text restores the preferred size after scrolling");
        check(Step(Layout(wide)).Text == initial, "returning to a long name starts at the beginning");

        const string literal = "<b>\\u003C</b>";
        var raw = new MeasuredScreenLine(literal, 2f, 1f, WideDisplay);
        check(raw.Present(0f, WideDisplay).Text == literal, "layout preserves literal markup-like names");
        check(MeasuredScreenLine.SingleLine("A\rB\nC\tD\u2028E\u2029F") == "A B C D E F",
            "control characters and Unicode line separators stay on one line");

        const string unicode = "𝄞e\u0301Wide";
        float NarrowDisplay(string text, float size) => Weight(text) * size / 2f;
        var unicodeLine = new MeasuredScreenLine(unicode, 2f, 1f, NarrowDisplay);
        check(unicodeLine.Present(1f, NarrowDisplay).Text == "𝄞e\u0301" &&
            unicodeLine.Present(1f, NarrowDisplay).Text.StartsWith("e\u0301"),
            "scrolling advances past a whole supplementary glyph and preserves the next combining cluster");

        int measured = 0;
        float Count(string text, float size)
        {
            measured++;
            return Width(text, size);
        }
        var cached = new MeasuredScreenLine(wide, 2f, 1f, Count);
        for (int i = 0; i < wide.Length + 3; i++) cached.Present(1f, Count);
        int before = measured;
        for (int i = 0; i < wide.Length + 3; i++) cached.Present(1f, Count);
        check(measured == before, "previously displayed offsets reuse measured windows on wrap");
    }

    private static float Weight(string text)
    {
        float width = 0f;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            width += elements.GetTextElement()[0] switch { 'W' => 2f, 'i' => .25f, _ => 1f };
        return width;
    }
}
