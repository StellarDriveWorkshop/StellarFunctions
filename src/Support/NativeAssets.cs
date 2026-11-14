using System;
using System.Collections.Generic;
using System.Linq;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Support;

// Read-only lookups into loaded native assets: materials by name and
// PartSettings by asset name. Both caches are built lazily from
// Resources.FindObjectsOfTypeAll; Invalidate drops the part-name cache on
// world enter, matching the legacy builder's lifetime.
public static class NativeAssets
{
    private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>(
        StringComparer.OrdinalIgnoreCase
    );
    private static Dictionary<string, PartSettings> _partsByName;

    public static Material Material(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (Materials.TryGetValue(name, out var cached))
        {
            return cached;
        }

        Material found = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m != null && m.name == name);
        Materials[name] = found;
        return found;
    }

    public static PartSettings PartByName(string name)
    {
        if (_partsByName == null)
        {
            _partsByName = new Dictionary<string, PartSettings>();
            foreach (var settings in Resources.FindObjectsOfTypeAll<PartSettings>())
            {
                if (settings != null && !_partsByName.ContainsKey(settings.name))
                {
                    _partsByName[settings.name] = settings;
                }
            }
        }
        return name != null && _partsByName.TryGetValue(name, out var found) && found != null ? found : null;
    }

    public static void Invalidate() => _partsByName = null;
}
