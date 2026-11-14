using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Ships.Cables;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using UnityEngine;

namespace StellarFunctions.Support;

// Socket construction: replaces the donor's SocketObject set with the
// spec-declared sockets, resolves per-type templates from native donors,
// applies ids/types/relay links and tints item sockets.
internal static class PartSockets
{
    private static readonly string[] SocketDonors =
    {
        "SignalSplitter",
        "FluidSplitter",
        "PowerSplitter",
        "FluidTank",
        "MathGate",
        "Battery",
    };
    private static readonly Dictionary<CableType, SocketObject> Templates = new Dictionary<CableType, SocketObject>();
    private static readonly Color ItemSocketTint = new Color(0.94f, 0.66f, 0.18f);

    // Item sockets have no named CableType member; the native value is 3.
    public static CableType CableTypeOf(string name)
    {
        if (string.Equals(name, "power", StringComparison.OrdinalIgnoreCase))
            return CableType.Power;
        if (string.Equals(name, "signal", StringComparison.OrdinalIgnoreCase))
            return CableType.Signal;
        if (string.Equals(name, "fluid", StringComparison.OrdinalIgnoreCase))
            return CableType.Fluid;
        return (CableType)3;
    }

    public static void Build(PartSpec spec, GameObject prefab)
    {
        foreach (SocketObject socket in prefab.GetComponentsInChildren<SocketObject>(true).ToList())
            UnityEngine.Object.DestroyImmediate(socket.gameObject);
        var idField = AccessTools.Field(typeof(SocketObject), "id");
        var typeField = AccessTools.Field(typeof(SocketObject), "type");
        var relayedField = AccessTools.Field(typeof(SocketObject), "relayedSockets");
        var made = new List<SocketObject>();
        var relayed = new List<SocketObject>();
        foreach (SocketSpec socketDef in (spec.Sockets ?? Enumerable.Empty<SocketSpec>()).OrderBy(s => s.Id))
        {
            CableType type = CableTypeOf(socketDef.Type);
            SocketObject template = SocketTemplate(spec, type);
            // A declared socket without a resolvable template is an
            // essential-element failure, not a skippable extra.
            if (template == null)
                throw new InvalidOperationException(
                    spec.Asset + ": no socket template for cable type " + socketDef.Type
                );
            GameObject go = UnityEngine.Object.Instantiate(template.gameObject, prefab.transform, false);
            go.name = "Socket" + socketDef.Id;
            go.transform.localPosition = socketDef.Position;
            go.transform.localRotation = SocketRotation(spec, socketDef);
            var socket = go.GetComponent<SocketObject>();
            idField.SetValue(socket, socketDef.Id);
            typeField.SetValue(socket, type);
            relayedField.SetValue(socket, new SocketObject[0]);
            SetSocketCableType(spec, socket, type);
            if ((int)type == 3)
                TintSockets(go, ItemSocketTint);
            made.Add(socket);
            if (socketDef.Relayed)
                relayed.Add(socket);
        }
        foreach (SocketObject socket in relayed)
            relayedField.SetValue(socket, relayed.Where(other => other != socket).ToArray());
        for (int i = 0; i < made.Count; i++)
            if (made[i].Id != i)
                NativeServices.Warn(
                    spec.Asset,
                    "socket ids must run 0..n-1 for socket visuals to line up, got " + made[i].Id + " at index " + i
                );
    }

    private static Quaternion SocketRotation(PartSpec spec, SocketSpec socket)
    {
        if (socket.Facing.HasValue)
            return Quaternion.Euler(socket.Facing.Value);
        Vector3 outward = socket.Position - spec.BoundsCenter;
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f)
            outward = Vector3.forward;
        return Quaternion.LookRotation(-outward.normalized, Vector3.up);
    }

    private static SocketObject SocketTemplate(PartSpec spec, CableType type)
    {
        if (Templates.TryGetValue(type, out var cached) && cached != null)
            return cached;
        CableType lookFor = (CableType)((int)type == 3 ? 1 : (int)type);
        foreach (string donor in SocketDonors)
        {
            PartSettings settings = NativeAssets.PartByName(donor);
            if (settings == null || settings.part == null)
                continue;
            var socket = settings
                .part.GetComponentsInChildren<SocketObject>(true)
                .FirstOrDefault(s => s != null && s.Type == lookFor);
            if (socket != null)
            {
                Templates[type] = socket;
                return socket;
            }
        }
        var fallback = Resources
            .FindObjectsOfTypeAll<SocketObject>()
            .FirstOrDefault(s => s != null && s.Type == lookFor);
        if (fallback != null)
            Templates[type] = fallback;
        return fallback;
    }

    private static void SetSocketCableType(PartSpec spec, SocketObject socket, CableType type)
    {
        if (socket.interactive == null)
        {
            NativeServices.Warn(
                spec.Asset,
                "socket " + socket.Id + " has no interactive object, it cannot be plugged into"
            );
            return;
        }
        var interactions = socket.interactive.GetComponentsInChildren<SocketInteractionObject>(true);
        if (interactions.Length == 0)
        {
            NativeServices.Warn(
                spec.Asset,
                "socket " + socket.Id + " interactive object has no SocketInteractionObject"
            );
            return;
        }
        foreach (var interaction in interactions)
            interaction.cableType = type;
    }

    private static void TintSockets(GameObject socket, Color tint)
    {
        foreach (Renderer renderer in socket.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null)
                    continue;
                var copy = new Material(materials[i])
                {
                    name = materials[i].name + "_ItemSocket",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (copy.HasProperty("_BaseColor"))
                    copy.SetColor("_BaseColor", tint);
                if (copy.HasProperty("_Color"))
                    copy.SetColor("_Color", tint);
                materials[i] = copy;
            }
            renderer.sharedMaterials = materials;
        }
    }

    public static void Invalidate() => Templates.Clear();
}
