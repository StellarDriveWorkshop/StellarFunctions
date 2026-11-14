using System;
using System.Reflection;
using HarmonyLib;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions;

// Functions-owned socket-position repair for Demo 0.8.3. The content kit builds
// custom prefabs under a hidden holder 100 km below the world, so the native
// socket query reads world-space socket positions as if they were prefab-local.
// This marks only Function part prefabs and corrects only their sockets; vanilla
// and other mods' sockets keep native behavior. When StellarSocketPositionFix is
// also installed, both postfixes compute the same corrected value, so the result
// is idempotent.
internal static class FunctionSocketPositionFix
{
    private static HarmonyLib.Harmony _harmony;
    private static bool _active;

    internal static void Install(string harmonyId)
    {
        if (_harmony != null)
        {
            return;
        }

        HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(harmonyId);
        try
        {
            Type tracker = AccessTools.TypeByName("Ships.Cables.ConnectionsTracker");
            if (tracker == null)
            {
                throw new MissingMethodException("Ships.Cables.ConnectionsTracker");
            }

            MethodInfo socketPosition = FindSocketPosition(tracker);
            if (socketPosition == null)
            {
                throw new MissingMethodException("ConnectionsTracker.GetSocketPosition");
            }

            // Required for correct Function socket placement; a failure here must
            // stop the mod rather than leave parts with broken socket positions.
            harmony.Patch(
                socketPosition,
                postfix: new HarmonyMethod(typeof(FunctionSocketPositionFix), nameof(AfterGetSocketPosition))
            );
        }
        catch
        {
            harmony.UnpatchSelf();
            throw;
        }

        _harmony = harmony;
        _active = true;
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        _active = false;
    }

    private static MethodInfo FindSocketPosition(Type tracker)
    {
        foreach (
            MethodInfo method in tracker.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        )
        {
            if (method.Name != "GetSocketPosition" || !method.IsStatic || method.ReturnType != typeof(Vector3))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (
                parameters.Length == 2
                && parameters[0].ParameterType == typeof(IPartPlacement)
                && parameters[1].ParameterType == typeof(SocketObject)
            )
            {
                return method;
            }
        }
        return null;
    }

    internal static void MarkSockets(GameObject prefab)
    {
        if (prefab == null)
        {
            return;
        }

        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        for (int i = 0; i < sockets.Length; i++)
        {
            SocketObject socket = sockets[i];
            if (socket == null)
            {
                continue;
            }

            FunctionSocketMarker marker = socket.GetComponent<FunctionSocketMarker>();
            if (marker == null)
            {
                marker = socket.gameObject.AddComponent<FunctionSocketMarker>();
            }

            if (marker.Root == null)
            {
                marker.Root = prefab.transform;
            }
        }
    }

    public static void AfterGetSocketPosition(IPartPlacement placement, SocketObject socketObject, ref Vector3 __result)
    {
        if (!_active || placement == null || socketObject == null)
        {
            return;
        }

        FunctionSocketMarker marker = socketObject.GetComponent<FunctionSocketMarker>();
        if (marker == null)
        {
            marker = MarkFunctionsHolderSocket(socketObject);
        }

        if (marker == null || marker.Root == null)
        {
            return;
        }

        Vector3 local = marker.Root.InverseTransformPoint(socketObject.transform.position);
        __result = placement.LocalPosition + placement.Rotation * local;
    }

    internal static FunctionSocketMarker MarkFunctionsHolderSocket(SocketObject socketObject)
    {
        Transform root = socketObject.transform.root;
        if (root == null || root.name != "StellarFunctionsPrefabHolder" || root.position.y > -50000f)
        {
            return null;
        }

        Transform prefab = socketObject.transform;
        while (prefab.parent != null && prefab.parent != root)
        {
            prefab = prefab.parent;
        }

        if (prefab == root || !IsFunctionsAsset(prefab.name))
        {
            return null;
        }

        MarkSockets(prefab.gameObject);
        return socketObject.GetComponent<FunctionSocketMarker>();
    }

    private static bool IsFunctionsAsset(string name)
    {
        return name == "StellarFunctionParam"
            || name == "StellarFunctionReturn"
            || name == "StellarFunctionCall"
            || name == "StellarFunctionTransmitter"
            || name.StartsWith("StellarFunctionCall_", StringComparison.Ordinal);
    }
}

public sealed class FunctionSocketMarker : MonoBehaviour
{
    public Transform Root;
}
