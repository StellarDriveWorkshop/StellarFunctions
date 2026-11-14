using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using StellarFunctions.Support;

namespace StellarFunctions.Support;

// Owns registration of materialized parts into the native part catalogue.
// Patches PartSettingsProvider.EnsureDictIsCached so both cold providers
// (dict built after our parts exist) and already-cached providers (dict
// built before registration) resolve our parts. Merge inserts only missing
// ids: foreign same-id entries are never overwritten and are reported once.
public static class Catalogue
{
    private const string Scope = "catalogue";
    private static readonly Dictionary<ushort, PartSettings> Registered = new Dictionary<ushort, PartSettings>();
    private static readonly HashSet<string> Conflicts = new HashSet<string>();
    private static FieldInfo _partsDict;
    private static bool _installed;

    // Returns true only when the patch is verified installed (or was already
    // installed); false on any failure so callers can retry instead of
    // treating a handled failure as success.
    public static bool Install()
    {
        if (_installed)
            return true;
        _partsDict = AccessTools.Field(typeof(PartSettingsProvider), "_partsDict");
        var method = AccessTools.Method(typeof(PartSettingsProvider), "EnsureDictIsCached");
        if (_partsDict == null || method == null)
        {
            NativeServices.Error(
                Scope,
                "PartSettingsProvider._partsDict/EnsureDictIsCached not found, catalogue injection disabled"
            );
            return false;
        }
        try
        {
            var harmony = new HarmonyLib.Harmony("stellarfunctions.support.catalogue");
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(Catalogue), nameof(BeforeEnsureDict)),
                postfix: new HarmonyMethod(typeof(Catalogue), nameof(AfterEnsureDict))
            );
            var info = HarmonyLib.Harmony.GetPatchInfo(method);
            bool active =
                info != null
                && info.Prefixes.Any(p => p.owner == harmony.Id)
                && info.Postfixes.Any(p => p.owner == harmony.Id);
            if (!active)
            {
                NativeServices.Error(Scope, "catalogue patch did not apply to EnsureDictIsCached");
                return false;
            }
        }
        catch (Exception ex)
        {
            NativeServices.Error(Scope, "catalogue patch failed", ex);
            return false;
        }
        _installed = true;
        NativeServices.Trace(Scope, "catalogue patch installed");
        return true;
    }

    public static void Register(PartSettings settings)
    {
        if (settings != null)
            Registered[settings.id] = settings;
    }

    // Harmony prefix: make sure every spec is materialized, then cover
    // providers that cached their dictionary before registration ran.
    public static void BeforeEnsureDict()
    {
        Registration.EnsureMaterialized();
        InjectIntoProvider();
    }

    // Harmony postfix on PartSettingsProvider.EnsureDictIsCached.
    public static void AfterEnsureDict(PartSettingsProvider __instance)
    {
        if (Registered.Count == 0 || _partsDict == null || __instance == null)
            return;
        if (!(_partsDict.GetValue(__instance) is Dictionary<ushort, PartSettings> dict))
            return;
        var plan = CatalogueMerge.Plan(dict, Registered);
        if (plan.Conflicts != null)
            foreach (ushort id in plan.Conflicts)
                ReportForeignConflict(__instance, id, dict[id]);
        if (plan.Merged != null)
            _partsDict.SetValue(__instance, plan.Merged);
    }

    private static void ReportForeignConflict(PartSettingsProvider provider, ushort id, PartSettings foreign)
    {
        string key = (provider != null ? provider.GetHashCode() : 0) + ":" + id;
        if (!Conflicts.Add(key))
            return;
        NativeServices.Error(
            Scope,
            "part id "
                + id
                + " resolves to foreign settings '"
                + (foreign != null ? foreign.name : "null")
                + "'; keeping foreign entry, ours not injected"
        );
    }

    private static void InjectIntoProvider()
    {
        var provider = NativeServices.Service<PartSettingsProvider>();
        if (provider != null)
            AfterEnsureDict(provider);
    }
}
