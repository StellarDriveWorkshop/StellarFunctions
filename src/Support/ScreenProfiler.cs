using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StellarFunctions.Support;

// Opt-in CPU profiler for the world-space screen path. Disabled unless the
// STELLARFUNCTIONS_PROFILE environment variable is 1/true/yes or a
// "StellarFunctions.profile" marker file sits beside the DLL. While
// disabled every instrumented call site costs one static-null branch and
// no timestamp is taken.
//
// F7 starts a ~20 s capture (Plugin.OnUpdate calls Tick); F7 again stops
// early. Each finished capture writes a structured .json and a readable
// .txt under <game>/UserData/StellarFunctions/profiler/ with unique
// timestamps, so runs cannot overwrite each other. Captures are independent:
// counters reset on every start.
internal static class ScreenProfiler
{
    internal const float CaptureSeconds = 20f;
    private const string MarkerName = "StellarFunctions.profile";
    private const string EnvName = "STELLARFUNCTIONS_PROFILE";

    private static readonly bool _enabled = DetectEnabled();
    private static ScreenProfileStats _stats;
    private static float _captureStart;
    private static DateTime _captureStartUtc;
    private static int _surfaces;
    private static int _measuredSurfaces;
    private static string _buildHash;

    internal static bool Enabled => _enabled;

    private static bool DetectEnabled()
    {
        try
        {
            string env = Environment.GetEnvironmentVariable(EnvName);
            if (
                !string.IsNullOrEmpty(env)
                && (
                    env == "1"
                    || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase)
                )
            )
                return true;
        }
        catch { }
        try
        {
            string dir = Path.GetDirectoryName(typeof(ScreenProfiler).Assembly.Location);
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MarkerName));
        }
        catch
        {
            return false;
        }
    }

    // Called from ScreenSurface.Awake/OnDestroy. Rare events, so the counter
    // stays accurate even while profiling is disabled.
    internal static void SurfaceCreated(bool measured)
    {
        _surfaces++;
        if (measured)
            _measuredSurfaces++;
    }

    internal static void SurfaceDestroyed(bool measured)
    {
        _surfaces--;
        if (measured)
            _measuredSurfaces--;
    }

    // Returns a timestamp only while a capture is active; callers must pass
    // the result to End, which ignores zero.
    internal static long Begin(ScreenProfileBucket bucket) => _stats == null ? 0L : Stopwatch.GetTimestamp();

    internal static void End(ScreenProfileBucket bucket, long start)
    {
        if (start == 0L || _stats == null)
            return;
        _stats.Record(bucket, Stopwatch.GetTimestamp() - start);
    }

    internal static void Exit(ScreenProfileExit kind) => _stats?.Exit(kind);

    internal static void Frame() => _stats?.Frame();

    // Called once per frame from Plugin.OnUpdate. Polls the F7 trigger and
    // auto-stops expired captures. Returns immediately when disabled.
    internal static void Tick()
    {
        if (!_enabled)
            return;
        if (_stats != null)
        {
            _stats.Frame();
            if (Time.unscaledTime - _captureStart >= CaptureSeconds)
            {
                Finish();
                return;
            }
        }
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f7Key.wasPressedThisFrame)
        {
            if (_stats == null)
                Start();
            else
                Finish();
        }
    }

    private static void Start()
    {
        _stats = new ScreenProfileStats();
        _captureStart = Time.unscaledTime;
        _captureStartUtc = DateTime.UtcNow;
        NativeServices.Warn(
            "screen-profile",
            "capture started — press F7 to stop early or wait " + CaptureSeconds + "s"
        );
    }

    private static void Finish()
    {
        var stats = _stats;
        _stats = null;
        if (stats == null)
            return;
        // Report generation and file I/O must never throw into OnUpdate: a
        // managed fault here would repeat every frame and could mask the real
        // failure. Everything below is guarded; writes go through a temp file
        // so an interrupted capture cannot leave a truncated report behind.
        try
        {
            stats.SetScreens(_surfaces, _measuredSurfaces);
            double seconds = Time.unscaledTime - _captureStart;
            string build = BuildHash();
            string started = _captureStartUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string json = stats.StructuredJson(
                build,
                Application.unityVersion,
                Application.isBatchMode,
                started,
                seconds,
                Stopwatch.Frequency
            );
            string text = stats.TextReport(
                build,
                Application.unityVersion,
                Application.isBatchMode,
                started,
                seconds,
                Stopwatch.Frequency
            );
            string stamp = _captureStartUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string dir = OutputDirectory();
            Directory.CreateDirectory(dir);
            WriteAtomic(Path.Combine(dir, "screen-profile-" + stamp + ".json"), json);
            WriteAtomic(Path.Combine(dir, "screen-profile-" + stamp + ".txt"), text);
            NativeServices.Warn("screen-profile", "capture finished — reports written to " + dir);
        }
        catch (Exception error)
        {
            NativeServices.Warn("screen-profile", "capture finish failed: " + error.Message);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, Encoding.UTF8);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }

    private static string OutputDirectory()
    {
        // The DLL lives in <game>/Mods; UserData sits at the game root.
        string mods = Path.GetDirectoryName(typeof(ScreenProfiler).Assembly.Location);
        string root = mods == null ? null : Directory.GetParent(mods)?.FullName;
        return Path.Combine(root ?? Path.GetTempPath(), "UserData", "StellarFunctions", "profiler");
    }

    private static string BuildHash()
    {
        if (_buildHash != null)
            return _buildHash;
        try
        {
            string path = typeof(ScreenProfiler).Assembly.Location;
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(File.ReadAllBytes(path));
            var hex = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            _buildHash = hex.ToString();
        }
        catch
        {
            _buildHash = "unknown";
        }
        return _buildHash;
    }
}
