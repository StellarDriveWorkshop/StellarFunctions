using System;
using Core.Services;
using FishNet;
using FishNet.Managing;
using MelonLoader;
using UnityEngine;

namespace StellarFunctions.Support;

// Minimal access to native game services and logging for the support layer.
// No local-player or client-UI dependency: every member is safe on a
// dedicated server with zero players.
public static class NativeServices
{
    private static readonly System.Collections.Generic.Dictionary<System.Type, object> ServiceCache =
        new System.Collections.Generic.Dictionary<System.Type, object>();
    private static bool _sceneHooked;

    // Fresh lookup every call: no caching, correct for callers that must
    // observe service replacement immediately.
    public static T Service<T>()
        where T : class
    {
        try
        {
            return ServiceLocator.GetService<T>();
        }
        catch
        {
            return null;
        }
    }

    // Cached lookup preserving the legacy GameRefs semantics: per-type cache,
    // Unity-object liveness check, scene-load invalidation, and no permanent
    // null caching. Use where repeated lookups would otherwise be wasteful.
    public static T CachedService<T>()
        where T : class
    {
        EnsureSceneHook();
        System.Type type = typeof(T);
        if (ServiceCache.TryGetValue(type, out object cached))
        {
            bool alive = cached is UnityEngine.Object unity ? unity != null : cached != null;
            if (alive)
            {
                return (T)cached;
            }

            ServiceCache.Remove(type);
        }
        T service = Service<T>();
        if (service != null)
        {
            ServiceCache[type] = service;
        }
        else
        {
            ServiceCache.Remove(type);
        }

        return service;
    }

    public static void InvalidateServices() => ServiceCache.Clear();

    private static void EnsureSceneHook()
    {
        if (_sceneHooked)
        {
            return;
        }

        _sceneHooked = true;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => InvalidateServices();
        UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene => InvalidateServices();
    }

    public static bool IsServer
    {
        get
        {
            try
            {
                return NetworkManager.Instances.Count > 0 && InstanceFinder.IsServerStarted;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsClient
    {
        get
        {
            try
            {
                return NetworkManager.Instances.Count > 0 && InstanceFinder.IsClientStarted;
            }
            catch
            {
                return false;
            }
        }
    }

    public static int NetworkManagerCount
    {
        get
        {
            try
            {
                return NetworkManager.Instances.Count;
            }
            catch
            {
                return 0;
            }
        }
    }

    public static void Trace(string scope, string message) => Write("TRACE", scope, message);

    public static void Info(string scope, string message) => Write("INFO", scope, message);

    public static void Warn(string scope, string message) => Write("WARN", scope, message);

    public static void Error(string scope, string message) => Write("ERROR", scope, message);

    public static void Error(string scope, string message, Exception error) =>
        Write("ERROR", scope, message + " :: " + error);

    // Release severity floor (LogGate levels: Trace=0..Error=3). Routine
    // Trace/Info diagnostics are hidden by default so ordinary players see
    // warnings and errors only. Enable detailed diagnostics by setting the
    // STELLARFUNCTIONS_DIAGNOSTICS environment variable to 1/true, placing a
    // "StellarFunctions.diagnostics" marker file beside the DLL, or setting
    // DiagnosticsEnabled/Floor programmatically for troubleshooting.
    public static int Floor = LogGate.Warn;

    private static bool? _diagnostics;

    public static bool DiagnosticsEnabled
    {
        get
        {
            if (!_diagnostics.HasValue)
            {
                _diagnostics = DetectDiagnostics();
            }

            return _diagnostics.Value;
        }
        set => _diagnostics = value;
    }

    private static bool DetectDiagnostics()
    {
        try
        {
            string env = System.Environment.GetEnvironmentVariable("STELLARFUNCTIONS_DIAGNOSTICS");
            if (
                !string.IsNullOrEmpty(env)
                && (
                    env == "1"
                    || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return true;
            }
        }
        catch { }
        try
        {
            string dir = System.IO.Path.GetDirectoryName(typeof(NativeServices).Assembly.Location);
            return !string.IsNullOrEmpty(dir)
                && System.IO.File.Exists(System.IO.Path.Combine(dir, "StellarFunctions.diagnostics"));
        }
        catch
        {
            return false;
        }
    }

    private static void Write(string level, string scope, string message)
    {
        if (!LogGate.Visible(LogGate.Level(level), Floor, DiagnosticsEnabled))
        {
            return;
        }

        string text = "[support:" + scope + "] " + message;
        switch (level)
        {
            case "ERROR":
                MelonLogger.Error(text);
                break;
            case "WARN":
                MelonLogger.Warning(text);
                break;
            default:
                MelonLogger.Msg(text);
                break;
        }
    }
}
