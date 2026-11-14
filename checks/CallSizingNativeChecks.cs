using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Services;
using Ships;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Ships.Parts.Common.Model;
using Ships.Parts.Common.Model.Reactions;
using Ships.Physics;
using Ships.Physics.Collision;
using Ships.Visuals;
using StellarFunctions.Content;
using StellarFunctions.Game;
using UnityEngine;

namespace StellarFunctions;

internal static class CallSizingNativeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var settings = Support.Registration.Get(62005);
        var state = new ShipState(490001, 490002, null);
        var ship = new TrackedShipClient(null, state);
        const ushort partId = 71;
        Vector3 mount = new Vector3(2.25f, .5f, -1.75f);
        var part = new StatefulPart(
            partId,
            settings,
            new FloatingPartPlacement(mount, PartOrientation.Right, PartOrientation.Up)
        );
        Quaternion originalRotation = part.LocalRotation;
        var call = new FunctionCallState
        {
            InstanceId = Guid.NewGuid(),
            SelectedFunctionId = Guid.NewGuid().ToString("N"),
            SelectedName = "Lifecycle",
            Slot0 = Guid.NewGuid().ToString("N"),
            Slot1 = Guid.NewGuid().ToString("N"),
            Slot2 = Guid.NewGuid().ToString("N"),
            RuntimeJson = "{\"history\":7}",
            StructureKey = "native-sizing-fixture",
            InputCount = 3,
            ListeningMask = 7,
        };
        part.State = call;
        state.AddStatefulPartWithoutInit(part);

        var instancerObject = new GameObject("Native Call sizing instancer");
        var instancer = instancerObject.AddComponent<ShipPartInstancer>();
        var visualsObject = new GameObject("Native Call sizing visuals");
        var visuals = visualsObject.AddComponent<ShipPartVisuals>();
        SetField(typeof(ShipPartVisuals), visuals, "_partInstancer", instancer);
        visuals.Setup(ship);

        var colliderObject = new GameObject("Native Call sizing solid colliders");
        var colliderParent = new GameObject("Native Call sizing solid collider parent");
        colliderParent.transform.SetParent(colliderObject.transform, false);
        var colliders = colliderObject.AddComponent<ShipStatefulColliders>();
        SetField(typeof(ShipStatefulColliders), colliders, "collidersParent", colliderParent.transform);
        SetField(typeof(ShipStatefulColliders), colliders, "_shipPartInstancer", instancer);

        var interactionObject = new GameObject("Native Call sizing interactions");
        var interactions = interactionObject.AddComponent<ShipInteractionColliders>();
        interactions.Setup(ship);

        state.HookPartListeners(visualsObject);
        state.HookPartListeners(colliderObject);
        state.HookPartListeners(interactionObject);

        try
        {
            CheckVariant(1, settings, check);
            CheckVariant(2, settings, check);
            CheckVariant(3, settings, check);
            check(
                part.Settings.id == 62005 && settings.id == 62005,
                "all Call geometry variants keep native part ID 62005"
            );

            var solidMap =
                (Dictionary<ushort, GameObject>)GetField(typeof(ShipStatefulColliders), colliders, "_statefulParts");
            CheckVisual(3, visuals, partId, check);
            CheckSolid(3, solidMap, partId, check);
            CheckInteraction(3, interactionObject.transform, ship.Id, partId, check);

            SetCount(1, state, part, call, visuals, solidMap, interactionObject.transform, ship.Id, check);
            SetCount(2, state, part, call, visuals, solidMap, interactionObject.transform, ship.Id, check);
            SetCount(3, state, part, call, visuals, solidMap, interactionObject.transform, ship.Id, check);

            var beforeSameCountVisual = Visual(visuals, partId).GameObject;
            var beforeSameCountCollider = solidMap[partId];
            var current = (FunctionCallState)part.State;
            var sameCount = current with { Output = 17, Status = "same count", ResizeFeedback = "x" };
            state.SetPartState(partId, sameCount);
            check(
                ReferenceEquals(beforeSameCountVisual, Visual(visuals, partId).GameObject),
                "same-count Call state update preserves native visual instance"
            );
            check(
                ReferenceEquals(beforeSameCountCollider, solidMap[partId]),
                "same-count Call state update preserves solid collider object"
            );
            check(
                ((FunctionCallState)part.State).ResizeFeedback.Length == 1,
                "native Call state callback retains one-character resize feedback"
            );
            state.SetPartState(partId, ((FunctionCallState)part.State) with { ResizeFeedback = new string('x', 128) });
            check(
                ReferenceEquals(beforeSameCountVisual, Visual(visuals, partId).GameObject)
                    && ReferenceEquals(beforeSameCountCollider, solidMap[partId]),
                "repeated same-count feedback update keeps native visual and collider instances"
            );
            check(
                ((FunctionCallState)part.State).ResizeFeedback.Length == 128,
                "native Call state callback retains 128-character resize feedback"
            );

            var live = (FunctionCallState)part.State;
            check(
                live.InstanceId == call.InstanceId
                    && live.SelectedFunctionId == call.SelectedFunctionId
                    && live.Slot0 == call.Slot0
                    && live.Slot1 == call.Slot1
                    && live.Slot2 == call.Slot2
                    && live.RuntimeJson == call.RuntimeJson
                    && live.StructureKey == call.StructureKey,
                "native resize lifecycle preserves Call identity, selected function, slots, and private runtime history"
            );
            check(
                part.LocalPosition == mount && Quaternion.Angle(part.LocalRotation, originalRotation) < .01f,
                "native resize lifecycle preserves mounted position and rotation"
            );
        }
        finally
        {
            state.UnhookPartListeners(visualsObject);
            state.UnhookPartListeners(colliderObject);
            state.UnhookPartListeners(interactionObject);
            UnityEngine.Object.DestroyImmediate(interactionObject);
            UnityEngine.Object.DestroyImmediate(colliderObject);
            UnityEngine.Object.DestroyImmediate(visualsObject);
            UnityEngine.Object.DestroyImmediate(instancerObject);
        }
    }

    private static void SetCount(
        int count,
        ShipState state,
        StatefulPart part,
        FunctionCallState initial,
        ShipPartVisuals visuals,
        Dictionary<ushort, GameObject> solidMap,
        Transform interactions,
        uint shipId,
        Action<bool, string> check
    )
    {
        var previousVisual = Visual(visuals, part.Id).GameObject;
        var previousCollider = solidMap[part.Id];
        var prior = (FunctionCallState)part.State;
        var next = prior with
        {
            InputCount = count,
            ResizeFeedback = "",
            ListeningMask = 7,
            LastInput0 = 11,
            LastInput1 = 12,
            LastInput2 = 13,
        };
        state.SetPartState(part.Id, next);
        var applied = (FunctionCallState)part.State;
        check(applied.InputCount == count, $"native ShipState.SetPartState applies Call size {count}");
        check(
            applied.InstanceId == initial.InstanceId
                && applied.Slot0 == initial.Slot0
                && applied.Slot1 == initial.Slot1
                && applied.Slot2 == initial.Slot2
                && applied.RuntimeJson == initial.RuntimeJson,
            $"native size {count} state update preserves identity, saved slots, and private history"
        );
        check(
            ReferenceEquals(part.Settings, Variant(count)),
            $"native size {count} selects its cached per-instance PartSettings variant"
        );
        check(
            !ReferenceEquals(previousVisual, Visual(visuals, part.Id).GameObject),
            $"native size {count} rebuilds placed visual through state listener"
        );
        check(
            ReferenceEquals(previousCollider, solidMap[part.Id]),
            $"native size {count} updates existing solid collider without replacing it"
        );
        CheckVisual(count, visuals, part.Id, check);
        CheckSolid(count, solidMap, part.Id, check);
        CheckInteraction(count, interactions, shipId, part.Id, check);
    }

    private static void CheckVariant(int count, PartSettings original, Action<bool, string> check)
    {
        PartSettings variant = Variant(count);
        check(
            variant.id == 62005 && ReferenceEquals(Variant(count), variant),
            $"Call size {count} uses a stable cached variant with ID 62005"
        );
        var sockets = variant.GetSockets().OrderBy(s => s.Id).ToArray();
        byte[] expected =
            count == 1 ? new byte[] { 0, 3 }
            : count == 2 ? new byte[] { 0, 1, 3 }
            : new byte[] { 0, 1, 2, 3 };
        check(
            sockets.Select(s => s.Id).SequenceEqual(expected),
            $"Call size {count} PartSettings.GetSockets returns [{string.Join(",", expected)}]"
        );
        check(
            Mathf.Abs(sockets.Single(s => s.Id == 0).transform.localPosition.x - -.125f) < .001f,
            $"Call size {count} keeps first input socket at x=-0.125"
        );
        var donor = Resources.FindObjectsOfTypeAll<PartSettings>().Single(s => s.id == (count == 1 ? 54 : 45));
        string path = count == 1 ? "Visuals/UnaryMathGate" : "Visuals/MathGate";
        var donorFilter = donor.part.transform.Find(path).GetComponent<MeshFilter>();
        var variantFilter = variant
            .part.GetComponentsInChildren<MeshFilter>(true)
            .Single(m => m.sharedMesh == donorFilter.sharedMesh);
        check(
            ReferenceEquals(variantFilter.sharedMesh, donorFilter.sharedMesh),
            $"Call size {count} uses native donor {donor.id} mesh"
        );
        var donorRenderer = donorFilter.GetComponent<MeshRenderer>();
        var variantRenderer = variantFilter.GetComponent<MeshRenderer>();
        check(
            donorRenderer.sharedMaterials.SequenceEqual(variantRenderer.sharedMaterials),
            $"Call size {count} uses native donor {donor.id} materials"
        );
        check(
            donorRenderer
                .sharedMaterials.Select(m => m.shader)
                .SequenceEqual(variantRenderer.sharedMaterials.Select(m => m.shader)),
            $"Call size {count} uses native donor {donor.id} shaders"
        );
        var donorBounds = donor.part.GetComponent<ShipPartBounds>();
        var variantBounds = variant.part.GetComponent<ShipPartBounds>();
        Bounds expectedBounds =
            count == 3
                ? original.Bounds
                : new Bounds(donorBounds.center + new Vector3(count == 1 ? -.125f : 0, 0, 0), donorBounds.bounds);
        check(
            (variantBounds.center - expectedBounds.center).sqrMagnitude < .000001f
                && variantBounds.bounds == expectedBounds.size,
            $"Call size {count} uses native donor {donor.id} solid bounds with expected center offset"
        );
        check(ReferenceEquals(original, Variant(3)), "three-input Call keeps the original shared PartSettings asset");
    }

    private static void CheckVisual(int count, ShipPartVisuals visuals, ushort partId, Action<bool, string> check)
    {
        var visual = Visual(visuals, partId);
        var tracker = visual.GameObject.GetComponentInChildren<PartSocketsTracker>(true);
        byte[] expected =
            count == 1 ? new byte[] { 0, 3 }
            : count == 2 ? new byte[] { 0, 1, 3 }
            : new byte[] { 0, 1, 2, 3 };
        check(
            tracker != null
                && tracker.sockets.Length == 4
                && expected.All(id => tracker.sockets[id] != null)
                && Enumerable
                    .Range(0, 4)
                    .Where(id => tracker.sockets[id] != null)
                    .Select(id => (byte)id)
                    .SequenceEqual(expected),
            $"native placed visual at size {count} has sparse socket tracker [{string.Join(",", expected)}]"
        );
        check(tracker.sockets[3] != null, $"native placed visual at size {count} keeps output socket 3");
        for (int id = 0; id < 3; id++)
            check(
                (tracker.sockets[id] != null) == (id < count),
                $"native visual at size {count} hides input socket {id} when unused"
            );
    }

    private static void CheckSolid(
        int count,
        Dictionary<ushort, GameObject> solidMap,
        ushort partId,
        Action<bool, string> check
    )
    {
        check(
            solidMap.TryGetValue(partId, out var colliderObject),
            $"native solid collider exists for Call size {count}"
        );
        if (!colliderObject)
            return;
        var box = colliderObject.GetComponent<BoxCollider>();
        var identifier = colliderObject.GetComponent<ShipPartIdentifier>();
        Bounds expected = Variant(count).Bounds;
        check(
            box != null
                && (box.center - expected.center).sqrMagnitude < .000001f
                && (box.size - expected.size).sqrMagnitude < .000001f,
            $"native solid collider bounds match Call size {count}"
        );
        check(
            identifier != null && identifier.Id == partId,
            $"native solid collider at size {count} retains part identifier {partId}"
        );
    }

    private static void CheckInteraction(
        int count,
        Transform root,
        uint shipId,
        ushort partId,
        Action<bool, string> check
    )
    {
        var sockets = root.GetComponentsInChildren<SocketInteractionObject>()
            .Select(s => s.EntityPartSocket)
            .Where(e => e.EntityId == shipId && e.Socket.PartId == partId)
            .Select(e => e.Socket.SocketId)
            .OrderBy(id => id)
            .ToArray();
        byte[] expected =
            count == 1 ? new byte[] { 0, 3 }
            : count == 2 ? new byte[] { 0, 1, 3 }
            : new byte[] { 0, 1, 2, 3 };
        check(
            sockets.SequenceEqual(expected),
            $"native interaction socket count and identifiers follow Call size {count}: [{string.Join(",", sockets)}]"
        );
    }

    private static IShipPartVisual Visual(ShipPartVisuals visuals, ushort partId)
    {
        if (!visuals.TryGetFloatingPartVisual(partId, out IShipPartVisual visual))
            throw new InvalidOperationException("Native Call visual was not registered.");
        return visual;
    }

    private static PartSettings Variant(int count)
    {
        Type sizing = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.CallSizing", true);
        MethodInfo variant = sizing.GetMethod("Variant", BindingFlags.Static | BindingFlags.NonPublic);
        return (PartSettings)variant.Invoke(null, new object[] { count });
    }

    private static object GetField(Type type, object instance, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(instance);

    private static void SetField(Type type, object instance, string name, object value) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SetValue(instance, value);
}
