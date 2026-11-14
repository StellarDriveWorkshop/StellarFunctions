using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StellarFunctions.Support;

// Reflection bridge into the Items assembly, which is intentionally not a
// compile-time reference. Resolves ItemSettings assets by id and constructs
// ItemInstance values for building-cost recipes.
public static class ItemCatalog
{
    private static Type _itemSettingsType;
    private static Type _itemInstanceType;
    private static Dictionary<uint, object> _byId;

    public static Type ItemInstanceType => _itemInstanceType ??= ResolveType("Items.Model.ItemInstance");

    private static Type ResolveType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = null;
            try
            {
                type = assembly.GetType(fullName, false);
            }
            catch { }
            if (type != null)
            {
                return type;
            }
        }
        return Type.GetType(fullName);
    }

    private static Type ItemSettingsType => _itemSettingsType ??= ResolveType("Items.Model.ItemSettings");

    public static object Find(uint id)
    {
        Type settingsType = ItemSettingsType;
        if (settingsType == null || !typeof(UnityEngine.Object).IsAssignableFrom(settingsType))
        {
            return null;
        }

        if (_byId == null)
        {
            _byId = new Dictionary<uint, object>();
            var idMember = settingsType.GetProperty("id") ?? (MemberInfo)settingsType.GetField("id");
            foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(settingsType))
            {
                object value =
                    idMember is PropertyInfo prop ? prop.GetValue(candidate)
                    : idMember is FieldInfo field ? field.GetValue(candidate)
                    : null;
                if (value is uint uid && candidate != null && !_byId.ContainsKey(uid))
                {
                    _byId[uid] = candidate;
                }
            }
        }
        return _byId.TryGetValue(id, out var found) ? found : null;
    }
}
