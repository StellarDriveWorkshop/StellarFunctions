using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions;

// Functions-owned compatibility repair for Demo 0.8.3. The native
// PartSettingsProvider snapshots every loaded PartSettings asset into a
// private dictionary on first use and never invalidates it. Thumbnail
// rendering for the research table, build menu and part info resolves part
// ids through GetPartSettingById, which throws for ids missing from that
// snapshot — parts registered after the snapshot (i.e. after the first
// world enter) would preview with blank thumbnails or error spam.
//
// This prefix is purely additive: when the snapshot exists but lacks the
// requested id, it resolves the part and returns it without touching the
// native dictionary. Registered Functions parts resolve immediately through
// the support registry on every call, so several missing ids in the same
// frame each succeed. Only the foreign-part fallback scan is throttled. A
// null snapshot still takes the native lazy-build path, and genuinely
// unknown ids still throw natively.
internal static class PartSettingsLookupFix
{
    private const float ScanInterval = 2f;
    private static readonly Dictionary<ushort, PartSettings> Late = new Dictionary<ushort, PartSettings>();
    private static HarmonyLib.Harmony _harmony;
    private static float _nextScan;

    internal static void Install(string harmonyId)
    {
        if (_harmony != null)
        {
            return;
        }

        HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(harmonyId);
        try
        {
            MethodInfo lookup = AccessTools.Method(
                typeof(PartSettingsProvider),
                nameof(PartSettingsProvider.GetPartSettingById)
            );
            if (lookup == null)
            {
                throw new MissingMethodException("PartSettingsProvider.GetPartSettingById");
            }

            harmony.Patch(lookup, prefix: new HarmonyMethod(typeof(PartSettingsLookupFix), nameof(ResolveLatePart)));
        }
        catch
        {
            harmony.UnpatchSelf();
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        Late.Clear();
    }

    private static bool ResolveLatePart(PartSettingsProvider __instance, ushort id, ref PartSettings __result)
    {
        var dict =
            AccessTools.Field(typeof(PartSettingsProvider), "_partsDict")?.GetValue(__instance)
            as Dictionary<ushort, PartSettings>;
        if (dict == null || dict.ContainsKey(id))
        {
            return true;
        }

        if (Late.TryGetValue(id, out PartSettings cached) && cached != null)
        {
            __result = cached;
            return false;
        }
        // Registered parts resolve immediately and independently; the
        // expensive asset scan is reserved for foreign late registrations.
        PartSettings ours = Support.Registration.Get(id);
        if (ours != null)
        {
            Late[id] = ours;
            __result = ours;
            return false;
        }
        if (Time.unscaledTime < _nextScan)
        {
            return true;
        }

        _nextScan = Time.unscaledTime + ScanInterval;
        foreach (PartSettings settings in Resources.FindObjectsOfTypeAll<PartSettings>())
        {
            if (settings != null && settings.id == id)
            {
                Late[id] = settings;
                __result = settings;
                return false;
            }
        }

        return true;
    }
}
