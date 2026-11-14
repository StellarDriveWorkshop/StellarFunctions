using System;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Support;

// Constructs PartSettings/prefab pairs for registered specs by cloning native
// donor assets, then runs the independent build pipeline in PartBuild +
// SocketSeating. All generated Unity objects live under a hidden holder far
// below the world and are owned by this component: a failed build destroys
// both objects before the exception propagates.
public static class PartFactory
{
    private const float HolderDepth = -100000f;
    private static Transform _holder;

    // Name and depth are a contract: FunctionSocketPositionFix recognizes
    // Function prefabs by this holder's name and position.
    public static Transform Holder
    {
        get
        {
            if (_holder == null)
            {
                var go = new GameObject("StellarFunctionsPrefabHolder");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.transform.position = new Vector3(0f, HolderDepth, 0f);
                UnityEngine.Object.DontDestroyOnLoad(go);
                _holder = go.transform;
            }
            return _holder;
        }
    }

    public static PartSettings Build(PartSpec spec, PartSettings donor)
    {
        if (spec == null)
            throw new ArgumentNullException(nameof(spec));
        if (donor == null || donor.part == null)
            throw new InvalidOperationException("donor part settings or prefab is missing");

        GameObject prefab = null;
        PartSettings settings = null;
        try
        {
            prefab = UnityEngine.Object.Instantiate(donor.part, Holder, false);
            prefab.name = spec.Asset;
            prefab.hideFlags = HideFlags.HideAndDontSave;
            settings = UnityEngine.Object.Instantiate(donor);
            settings.name = spec.Asset;
            settings.hideFlags = HideFlags.HideInHierarchy;
            settings.id = spec.Id;
            settings.part = prefab;
            PartBuild.Configure(spec, settings, prefab);
            AfterConfigure?.Invoke(spec, settings, prefab);
            SocketSeating.Apply(spec.Asset, prefab);
            return settings;
        }
        catch
        {
            if (settings != null)
                UnityEngine.Object.DestroyImmediate(settings);
            if (prefab != null)
                UnityEngine.Object.DestroyImmediate(prefab);
            throw;
        }
    }

    // Post-configuration hook: invoked once per part after generic
    // configuration and before socket seating. Functions installs its
    // product finishing callback here; the generic factory knows nothing
    // about it. Exceptions propagate so a failed finish fails the build.
    public static Action<PartSpec, PartSettings, GameObject> AfterConfigure;

    // World-enter cache reset, replacing the legacy ContentBuilder.Invalidate.
    public static void Invalidate()
    {
        PartSockets.Invalidate();
        NativeAssets.Invalidate();
    }
}
