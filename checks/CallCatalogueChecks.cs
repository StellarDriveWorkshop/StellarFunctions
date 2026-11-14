using System;
using System.Linq;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using StellarFunctions.Game;
using UnityEngine;

namespace StellarFunctions;

internal static class CallCatalogueChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var canonical = Support.Registration.Get(62005);
        var inventory = Resources.FindObjectsOfTypeAll<PartSettings>();
        check(
            inventory.Count(p => p.id == 62005) == 3,
            "catalogue regression contains canonical Call plus two runtime geometry clones"
        );
        // 0.4.0 throws the production duplicate-62005 exception at this real call.
        var provider = new PartSettingsProvider();
        var all = provider.GetAllPartSettings();
        check(all.Count(p => p.id == 62005) == 1, "cold native catalogue has exactly one buildable Call");
        check(
            ReferenceEquals(provider.GetPartSettingById(62005), canonical),
            "cold native catalogue resolves Call to canonical shared content"
        );
        check(
            all.Count == all.Select(p => p.id).Distinct().Count(),
            "cold catalogue retains native globally unique ID contract"
        );
        foreach (ushort id in new ushort[] { 45, 54, 62006, 62007, 62009 })
        {
            check(
                provider.GetPartSettingById(id) != null,
                "cold catalogue retains unrelated stock/shared content " + id
            );
        }

        foreach (ushort id in new ushort[] { 54, 62005 })
        {
            var duplicate = UnityEngine.Object.Instantiate(provider.GetPartSettingById(id));
            try
            {
                bool rejected = false;
                try
                {
                    new PartSettingsProvider().GetAllPartSettings();
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }
                check(rejected, "unowned duplicate ID " + id + " still fails native catalogue construction");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(duplicate);
            }
        }
        check(
            ReferenceEquals(new PartSettingsProvider().GetPartSettingById(62005), canonical),
            "fresh catalogue recovers after fixture duplicates are destroyed"
        );
        check(
            Resources.FindObjectsOfTypeAll<PartSettings>().Count(p => p.id == 62005) == 3,
            "filter leaves sized instances' geometry assets intact"
        );
    }
}
