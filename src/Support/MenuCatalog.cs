using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Ships.Interface.Settings;
using UI.BuildMenu;

namespace StellarFunctions.Support;

// Inserts materialized parts into the native build menu's tabsConfig after
// their declared anchors. Insertions are tracked per row and removed
// individually on restore, so entries other mods added to the same rows
// survive world replacement.
public static class MenuCatalog
{
    private const string Scope = "menu";
    private const int RowCapacity = 8;
    private static readonly Dictionary<BuildMenuTabConfig.BuildMenuRow, List<PartSettings>> Inserted =
        new Dictionary<BuildMenuTabConfig.BuildMenuRow, List<PartSettings>>();
    private static FieldInfo _tabsConfig;
    private static bool _installed;

    // Returns true only when the patch is verified installed (or was already
    // installed); false on any failure so callers can retry instead of
    // treating a handled failure as success.
    public static bool Install()
    {
        if (_installed)
        {
            return true;
        }

        _tabsConfig = AccessTools.Field(typeof(BuildMenu), "tabsConfig");
        var method = AccessTools.Method(typeof(BuildMenu), "Start");
        if (_tabsConfig == null || method == null)
        {
            NativeServices.Error(Scope, "BuildMenu.tabsConfig/Start not found, build-menu integration disabled");
            return false;
        }
        try
        {
            var harmony = new HarmonyLib.Harmony("stellarfunctions.support.menu");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(MenuCatalog), nameof(BeforeStart)));
            var info = HarmonyLib.Harmony.GetPatchInfo(method);
            if (info == null || !info.Prefixes.Any(p => p.owner == harmony.Id))
            {
                NativeServices.Error(Scope, "build-menu patch did not apply to BuildMenu.Start");
                return false;
            }
        }
        catch (Exception ex)
        {
            NativeServices.Error(Scope, "build-menu patch failed", ex);
            return false;
        }
        _installed = true;
        NativeServices.Trace(Scope, "build-menu patch installed");
        return true;
    }

    // Harmony prefix on BuildMenu.Start (__instance is required for Harmony
    // injection on a parameterless method). Materializes pending parts first:
    // menu entries only ever reference successfully built PartSettings.
    public static void BeforeStart(BuildMenu __instance)
    {
        Registration.EnsureMaterialized();
        if (_tabsConfig == null || __instance == null)
        {
            return;
        }

        if (!(_tabsConfig.GetValue(__instance) is BuildMenuTabConfig[] tabs))
        {
            return;
        }

        foreach (var entry in Registration.MenuEntries())
        {
            string anchorName = entry.Key;
            var settings = entry.Value;
            if (
                tabs.Any(tab =>
                    tab != null
                    && tab.rows != null
                    && tab.rows.Any(row => row != null && row.parts != null && row.parts.Contains(settings))
                )
            )
            {
                continue;
            }

            if (
                !MenuPlan.TryPlace(
                    tabs.Select(tab => tab?.rows ?? Array.Empty<BuildMenuTabConfig.BuildMenuRow>()),
                    row => row?.parts,
                    part => Matches(part, anchorName),
                    RowCapacity,
                    out int tabIndex,
                    out int rowIndex,
                    out int insertIndex
                )
            )
            {
                continue;
            }

            var target = tabs[tabIndex].rows[rowIndex];
            var list = target.parts.ToList();
            list.Insert(insertIndex, settings);
            target.parts = list.ToArray();
            TrackInsertion(target, settings);
        }
        _tabsConfig.SetValue(__instance, tabs);
    }

    // Removes only the entries this component inserted; parts other mods or
    // native code added to the same rows are preserved.
    public static void Restore()
    {
        foreach (var pair in Inserted)
        {
            var row = pair.Key;
            if (row?.parts == null)
            {
                continue;
            }

            var kept = row.parts.Where(p => !pair.Value.Contains(p)).ToArray();
            if (kept.Length != row.parts.Length)
            {
                row.parts = kept;
            }
        }
        Inserted.Clear();
    }

    private static void TrackInsertion(BuildMenuTabConfig.BuildMenuRow row, PartSettings settings)
    {
        if (row == null)
        {
            return;
        }

        if (!Inserted.TryGetValue(row, out var list))
        {
            Inserted[row] = list = new List<PartSettings>();
        }

        if (!list.Contains(settings))
        {
            list.Add(settings);
        }
    }

    private static bool Matches(PartSettings settings, string name)
    {
        if (settings == null)
        {
            return false;
        }

        return settings.name == name || settings.fullLabel == name;
    }
}
