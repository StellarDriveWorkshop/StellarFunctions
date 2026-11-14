using StellarFunctions.Support;

// Pure checks for the opt-in screen profiler aggregation: per-bucket call
// and tick accounting, tick-to-millisecond conversion, normalized metrics,
// early-exit counters, capture isolation and report formatting.
internal static class ScreenProfileChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var stats = new ScreenProfileStats();
        stats.Record(ScreenProfileBucket.Update, 1500);
        stats.Record(ScreenProfileBucket.Update, 500);
        stats.Record(ScreenProfileBucket.MeasureLine, 250);
        check(stats.Calls(ScreenProfileBucket.Update) == 2, "update calls accumulate per bucket");
        check(stats.Ticks(ScreenProfileBucket.Update) == 2000, "update ticks accumulate per bucket");
        check(stats.Calls(ScreenProfileBucket.MeasureLine) == 1
            && stats.Ticks(ScreenProfileBucket.MeasureLine) == 250,
            "buckets record independently");

        // frequency 1000 means one tick is exactly one millisecond.
        check(Math.Abs(stats.Milliseconds(ScreenProfileBucket.Update, 1000) - 2000) < .001,
            "ticks convert to milliseconds through the supplied frequency");
        check(Math.Abs(stats.MillisecondsPerCall(ScreenProfileBucket.Update, 1000) - 1000) < .001,
            "milliseconds per call divides by recorded calls");
        stats.Frame();
        stats.Frame();
        check(Math.Abs(stats.MillisecondsPerFrame(ScreenProfileBucket.Update, 1000) - 1000) < .001,
            "milliseconds per frame divides by counted frames");

        var empty = new ScreenProfileStats();
        check(empty.MillisecondsPerCall(ScreenProfileBucket.Update, 1000) == 0
            && empty.MillisecondsPerFrame(ScreenProfileBucket.Update, 1000) == 0
            && empty.Milliseconds(ScreenProfileBucket.Update, 0) == 0,
            "zero calls and zero frequency report zero instead of NaN");

        stats.Exit(ScreenProfileExit.HolderSkip);
        stats.Exit(ScreenProfileExit.Unchanged);
        stats.Exit(ScreenProfileExit.Unchanged);
        check(stats.Exits(ScreenProfileExit.HolderSkip) == 1 && stats.Exits(ScreenProfileExit.Unchanged) == 2,
            "early-exit counters accumulate independently");

        var other = new ScreenProfileStats();
        check(other.Calls(ScreenProfileBucket.Update) == 0 && other.Frames == 0,
            "independent captures never share counters");

        stats.SetScreens(14, 12);
        check(stats.ScreenTotal == 14 && stats.MeasuredScreenTotal == 12,
            "screen counts are recorded for the report");

        const string started = "2026-11-14T00:00:00.0000000Z";
        string json = stats.StructuredJson("abcdef0123", "6000.3.24f1", false, started, 20.0, 1000);
        check(json.Contains("\"dllSha256\": \"abcdef0123\""), "structured report carries the build hash");
        check(json.Contains("\"name\": \"update\"") && json.Contains("\"msPerCall\"") && json.Contains("\"msPerFrame\""),
            "structured report lists every bucket with normalized metrics");
        check(json.Contains("\"frames\": 2") && json.Contains("\"screens\": 14") && json.Contains("\"measuredScreens\": 12"),
            "structured report carries frames and screen counts");
        check(json.Contains("\"holderSkip\": 1") && json.Contains("\"unchanged\": 2"),
            "structured report carries early-exit counts");
        check(json.Contains("\"seconds\": 20"), "structured report carries the capture duration");

        string text = stats.TextReport("abcdef0123", "6000.3.24f1", false, started, 20.0, 1000);
        check(text.Contains("ms/call") && text.Contains("ms/frame") && text.Contains("update"),
            "readable report has the metric columns and bucket names");
        check(text.Contains("inclusive"), "readable report warns that nested buckets are inclusive");
        check(text.Contains("abcdef0123"), "readable report identifies the build");

        string emptyJson = empty.StructuredJson("x", "u", true, started, 0.0, 1000);
        check(emptyJson.Contains("\"calls\": 0") && !emptyJson.Contains("NaN") && !emptyJson.Contains("Infinity"),
            "empty captures still produce valid structured output");
    }
}
