using System.Globalization;
using System.Text;

namespace StellarFunctions.Support;

// Instrumentation buckets for the opt-in screen profiler. Buckets nest:
// Update contains PullFrame/Present; PresentLine contains LineBuild/
// LinePresent; those contain MeasureLine; TmpUpdate sits inside Present or
// PresentLine. Totals are inclusive and must never be summed together.
internal enum ScreenProfileBucket
{
    Update = 0,
    PullFrame,
    Present,
    PresentLine,
    LineBuild,
    LinePresent,
    MeasureLine,
    TmpUpdate,
    BucketCount,
}

// Early exits counted separately so silent skips stay visible.
internal enum ScreenProfileExit
{
    HolderSkip = 0,
    NoFrame,
    InactiveLabel,
    EmptyRect,
    Unchanged,
    ExitCount,
}

// Aggregate counters for one profiling capture. Pure timing math: callers
// supply elapsed Stopwatch ticks and a frequency for conversion, so the
// aggregation and report formatting stay testable without Unity.
internal sealed class ScreenProfileStats
{
    private readonly long[] _calls = new long[(int)ScreenProfileBucket.BucketCount];
    private readonly long[] _ticks = new long[(int)ScreenProfileBucket.BucketCount];
    private readonly long[] _exits = new long[(int)ScreenProfileExit.ExitCount];
    private long _frames;
    private int _screens;
    private int _measuredScreens;

    internal void Record(ScreenProfileBucket bucket, long ticks)
    {
        int index = (int)bucket;
        _calls[index]++;
        _ticks[index] += ticks < 0 ? 0 : ticks;
    }

    internal void Exit(ScreenProfileExit kind) => _exits[(int)kind]++;
    internal void Frame() => _frames++;
    internal void SetScreens(int total, int measured)
    {
        _screens = total;
        _measuredScreens = measured;
    }

    internal long Calls(ScreenProfileBucket bucket) => _calls[(int)bucket];
    internal long Ticks(ScreenProfileBucket bucket) => _ticks[(int)bucket];
    internal long Exits(ScreenProfileExit kind) => _exits[(int)kind];
    internal long Frames => _frames;
    internal int ScreenTotal => _screens;
    internal int MeasuredScreenTotal => _measuredScreens;

    internal double Milliseconds(ScreenProfileBucket bucket, long frequency)
        => frequency > 0 ? _ticks[(int)bucket] * 1000.0 / frequency : 0.0;

    internal double MillisecondsPerCall(ScreenProfileBucket bucket, long frequency)
    {
        long calls = _calls[(int)bucket];
        return calls > 0 ? Milliseconds(bucket, frequency) / calls : 0.0;
    }

    internal double MillisecondsPerFrame(ScreenProfileBucket bucket, long frequency)
        => _frames > 0 ? Milliseconds(bucket, frequency) / _frames : 0.0;

    internal static string BucketName(ScreenProfileBucket bucket) => bucket switch
    {
        ScreenProfileBucket.Update => "update",
        ScreenProfileBucket.PullFrame => "pullFrame",
        ScreenProfileBucket.Present => "present",
        ScreenProfileBucket.PresentLine => "presentLine",
        ScreenProfileBucket.LineBuild => "lineBuild",
        ScreenProfileBucket.LinePresent => "linePresent",
        ScreenProfileBucket.MeasureLine => "measureLine",
        ScreenProfileBucket.TmpUpdate => "tmpUpdate",
        _ => bucket.ToString(),
    };

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    internal string StructuredJson(string buildId, string unityVersion, bool batchMode,
        string startedUtc, double seconds, long frequency)
    {
        var text = new StringBuilder(1200);
        text.Append("{\n");
        text.Append("  \"tool\": \"stellarfunctions-screen-profile\",\n");
        text.Append("  \"format\": 1,\n");
        text.Append("  \"dllSha256\": \"").Append(buildId).Append("\",\n");
        text.Append("  \"unity\": \"").Append(unityVersion).Append("\",\n");
        text.Append("  \"batchMode\": ").Append(batchMode ? "true" : "false").Append(",\n");
        text.Append("  \"startedUtc\": \"").Append(startedUtc).Append("\",\n");
        text.Append("  \"seconds\": ").Append(F(seconds)).Append(",\n");
        text.Append("  \"stopwatchFrequency\": ").Append(frequency).Append(",\n");
        text.Append("  \"frames\": ").Append(_frames).Append(",\n");
        text.Append("  \"screens\": ").Append(_screens).Append(",\n");
        text.Append("  \"measuredScreens\": ").Append(_measuredScreens).Append(",\n");
        text.Append("  \"buckets\": [");
        for (int i = 0; i < (int)ScreenProfileBucket.BucketCount; i++)
        {
            var bucket = (ScreenProfileBucket)i;
            if (i > 0) text.Append(',');
            text.Append("\n    {\"name\": \"").Append(BucketName(bucket)).Append("\", \"calls\": ")
                .Append(_calls[i]).Append(", \"ticks\": ").Append(_ticks[i])
                .Append(", \"ms\": ").Append(F(Milliseconds(bucket, frequency)))
                .Append(", \"msPerCall\": ").Append(F(MillisecondsPerCall(bucket, frequency)))
                .Append(", \"msPerFrame\": ").Append(F(MillisecondsPerFrame(bucket, frequency)))
                .Append('}');
        }
        text.Append("\n  ],\n  \"exits\": {");
        for (int i = 0; i < (int)ScreenProfileExit.ExitCount; i++)
        {
            if (i > 0) text.Append(", ");
            string name = ((ScreenProfileExit)i).ToString();
            text.Append('"').Append(char.ToLowerInvariant(name[0])).Append(name[1..])
                .Append("\": ").Append(_exits[i]);
        }
        text.Append("}\n}\n");
        return text.ToString();
    }

    internal string TextReport(string buildId, string unityVersion, bool batchMode,
        string startedUtc, double seconds, long frequency)
    {
        var text = new StringBuilder(1400);
        text.Append("StellarFunctions screen profile — ").Append(startedUtc).Append('\n');
        text.Append("Build: StellarFunctions.dll sha256=").Append(buildId)
            .Append(" (MelonInfo/Unity ").Append(unityVersion)
            .Append(batchMode ? ", batchMode" : "").Append(")\n");
        text.Append("Capture: ").Append(F(seconds)).Append(" s, frames (OnUpdate ticks): ").Append(_frames)
            .Append(", screens alive at stop: ").Append(_screens)
            .Append(" (").Append(_measuredScreens).Append(" measured-line)\n\n");
        text.Append("bucket         calls      ms total   ms/call   ms/frame\n");
        for (int i = 0; i < (int)ScreenProfileBucket.BucketCount; i++)
        {
            var bucket = (ScreenProfileBucket)i;
            text.Append(BucketName(bucket).PadRight(14))
                .Append(_calls[i].ToString().PadLeft(8))
                .Append(F(Milliseconds(bucket, frequency)).PadLeft(12))
                .Append(F(MillisecondsPerCall(bucket, frequency)).PadLeft(10))
                .Append(F(MillisecondsPerFrame(bucket, frequency)).PadLeft(10))
                .Append('\n');
        }
        text.Append("\nEarly exits: holderSkip=").Append(_exits[(int)ScreenProfileExit.HolderSkip])
            .Append(" noFrame=").Append(_exits[(int)ScreenProfileExit.NoFrame])
            .Append(" inactiveLabel=").Append(_exits[(int)ScreenProfileExit.InactiveLabel])
            .Append(" emptyRect=").Append(_exits[(int)ScreenProfileExit.EmptyRect])
            .Append(" unchanged=").Append(_exits[(int)ScreenProfileExit.Unchanged]).Append('\n');
        text.Append("Note: bucket timings are inclusive and nested (update contains present/pullFrame; "
            + "presentLine contains lineBuild/linePresent/measureLine; tmpUpdate sits inside present paths). "
            + "Do not sum bucket totals. Timings are wall-clock inside instrumented methods, "
            + "not process-wide CPU; frames are Plugin.OnUpdate ticks, not a measured FPS.\n");
        return text.ToString();
    }
}
