using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Core.Services;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Parts.Common.Model;
using Ships.Parts.Common.Model.Reactions;
using Ships.Parts.Common.Tips;
using Ships.Physics;
using Ships.Physics.Collision;
using Ships.Services;
using Ships.Visuals;
using StellarFunctions.Content;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StellarFunctions;

// Shared immutable geometry variants, selected per native part. Never mutate
// global content settings to resize an instance, or rebuild it on value updates.
internal static class CallSizing
{
    internal const string ActionPrefix = "resize-call:";
    private static readonly PartSettings[] Variants = new PartSettings[4];
    private static readonly Collider[] Overlaps = new Collider[64];
    private static readonly Collider[] SupportOverlaps = new Collider[64];
    private const float MaximumSupportDepth = .012f;
    private const float CoplanarTolerance = .001f;
    private static readonly FieldInfo Settings = AccessTools.Field(typeof(StatefulPart), "<Settings>k__BackingField");
    private static readonly FieldInfo ColliderParts = AccessTools.Field(
        typeof(ShipStatefulColliders),
        "_statefulParts"
    );
    private static readonly FieldInfo InteractionParts = AccessTools.Field(
        typeof(ShipInteractionColliders),
        "_statefulParts"
    );
    private static readonly FieldInfo TrackerShips = AccessTools.Field(typeof(ConnectionsTracker), "Ships");
    private static readonly MethodInfo Touching = AccessTools.Method(
        typeof(ConnectionsTracker),
        "OnShipStatefulPartAdded"
    );
    private static readonly FieldInfo SignalMap = AccessTools.Field(typeof(ConnectionsTracker), "SignalGroupMap");
    private static readonly MethodInfo Inventory = typeof(Resources)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
        .MakeGenericMethod(typeof(PartSettings));
    private static HarmonyLib.Harmony _harmony;
    internal static bool Ready { get; private set; }

    internal static string Payload(FunctionCallState state, int count, string session) =>
        ActionPrefix
        + state.InstanceId.ToString("N")
        + ":"
        + state.InputCount.ToString(CultureInfo.InvariantCulture)
        + ":"
        + count.ToString(CultureInfo.InvariantCulture)
        + ":"
        + session;

    internal static bool Handle(
        TrackedShipServer ship,
        StatefulPart part,
        FunctionCallState state,
        uint playerId,
        string action
    )
    {
        if (!action.StartsWith(ActionPrefix, StringComparison.Ordinal))
        {
            return CallResizeWiring.HandleConfirmation(ship, part, state, playerId, action);
        }

        string[] fields = action.Substring(ActionPrefix.Length).Split(':');
        if (
            fields.Length != 4
            || fields[0] != state.InstanceId.ToString("N")
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int expected)
            || expected != state.InputCount
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || count < 1
            || count > 3
            || !Guid.TryParseExact(fields[3], "N", out var session)
            || session == Guid.Empty
        )
        {
            Reply(ship, part, "Size changed or invalid request. Reopen the menu.");
            return true;
        }

        CallResizeWiring.Request(ship, part, state, playerId, session.ToString("N"), count);
        return true;
    }

    internal static void Reply(TrackedShipServer ship, StatefulPart part, string message)
    {
        if (part.State is FunctionCallState state && state.ResizeFeedback != message)
        {
            ship.UpdatePartState(part.Id, state with { ResizeFeedback = message });
        }
    }

    internal static string SizeProblem(TrackedShipServer ship, StatefulPart part, FunctionCallState state, int count)
    {
        if (!Ready)
        {
            return "Call resizing is unavailable on this game build.";
        }
        if (!string.IsNullOrEmpty(state.PendingFunctionId))
        {
            return "Finish or cancel the function change first.";
        }
        return count != state.InputCount && !Clearance(ship, part, count, out string error) ? error : null;
    }

    internal static FunctionCallState AtSize(FunctionCallState state, int count) =>
        state with
        {
            InputCount = count,
            ResizeFeedback = "",
            ListeningMask = (byte)(state.ListeningMask & ((1 << count) - 1)),
            LastInput1 = count < 2 ? 0 : state.LastInput1,
            LastInput2 = count < 3 ? 0 : state.LastInput2,
        };

    internal static void InvalidateAfterResize()
    {
        NativeFunctionRuntime.Dirty();
        NativeTemplateHooks.InvalidateSchedule();
    }

    private static bool Clearance(TrackedShipServer ship, StatefulPart part, int count, out string error)
    {
        error = "";
        Bounds current = part.Settings.Bounds;
        Bounds requested = Variant(count).Bounds;
        if (current.Contains(requested.min) && current.Contains(requested.max))
        {
            return true;
        }

        if (!ship.IsInteriorColliderObjectCreated || ship.InteriorColliders == null)
        {
            error = "Wait for ship collision geometry to load before changing size.";
            return false;
        }
        // Donor housings are not perfectly nested: size1 protrudes 22 mm
        // beyond size3's left edge. Test requested-minus-current, never the
        // shared interior (which can contain existing mount/player contact).
        // Peel up to six non-overlapping slabs in part-local space. No heap
        // collections, world discovery, or work on ordinary state updates.
        Vector3 min = requested.min,
            max = requested.max,
            oldMin = current.min,
            oldMax = current.max;
        // Native interaction/socket targeting colliders have no solid-part
        // identifier. Resolve this Call's registered hierarchy once per edit,
        // rather than treating its oversized F/pickup box as foreign geometry.
        Transform ownInteractions = null;
        var interactions = ship.InteriorColliders.GetComponentInChildren<ShipInteractionColliders>(true);
        if (
            interactions != null
            && InteractionParts.GetValue(interactions) is Dictionary<ushort, IShipPartInteractions> parts
            && parts.TryGetValue(part.Id, out var owned)
            && owned.GameObject != null
            && owned.GameObject.transform.IsChildOf(ship.InteriorColliders.transform)
        )
        {
            ownInteractions = owned.GameObject.transform;
        }

        BoxCollider supportBody = null;
        var solids = ship.InteriorColliders.GetComponentInChildren<ShipStatefulColliders>(true);
        if (
            requested.min.y >= current.min.y - .0001f
            && solids != null
            && ColliderParts.GetValue(solids) is Dictionary<ushort, GameObject> solidParts
            && solidParts.TryGetValue(part.Id, out var solid)
            && solid != null
            && solid.transform.IsChildOf(ship.InteriorColliders.transform)
        )
        {
            supportBody = solid.GetComponent<BoxCollider>();
        }

        Physics.SyncTransforms();
        float supportFace = float.NaN;
        for (int axis = 0; axis < 3; axis++)
        {
            if (min[axis] < oldMin[axis])
            {
                Vector3 end = max;
                end[axis] = Mathf.Min(max[axis], oldMin[axis]);
                if (!ClearRegion(ship, part, min, end, ownInteractions, supportBody, ref supportFace, out error))
                {
                    return false;
                }

                min[axis] = end[axis];
            }
            if (max[axis] > oldMax[axis])
            {
                Vector3 start = min;
                start[axis] = Mathf.Max(min[axis], oldMax[axis]);
                if (!ClearRegion(ship, part, start, max, ownInteractions, supportBody, ref supportFace, out error))
                {
                    return false;
                }

                max[axis] = start[axis];
            }
            if (min[axis] >= max[axis])
            {
                return true;
            }
        }
        return true;
    }

    private static bool ClearRegion(
        TrackedShipServer ship,
        StatefulPart part,
        Vector3 min,
        Vector3 max,
        Transform ownInteractions,
        BoxCollider supportBody,
        ref float supportFace,
        out string error
    )
    {
        error = "";
        // Retain the existing 2 mm contact skin. Tiny donor differences below
        // that tolerance do not become an artificially inflated query box.
        const float skin = .002f;
        Vector3 localHalf = (max - min) * .5f;
        if (localHalf.x <= skin || localHalf.y <= skin || localHalf.z <= skin)
        {
            return true;
        }

        var root = ship.InteriorColliders.transform;
        Vector3 center = root.TransformPoint(part.LocalPosition + part.LocalRotation * ((min + max) * .5f));
        Vector3 scale = root.lossyScale;
        scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        Vector3 half = Vector3.Scale(localHalf - Vector3.one * skin, scale);
        PhysicsScene scene = ship.InteriorColliders.scene.GetPhysicsScene();
        int hits = scene.OverlapBox(
            center,
            half,
            Overlaps,
            root.rotation * part.LocalRotation,
            ~0,
            QueryTriggerInteraction.Ignore
        );
        if (hits >= Overlaps.Length)
        {
            error = "Not enough clear space to change this Call's size.";
            Array.Clear(Overlaps, 0, Overlaps.Length);
            return false;
        }
        bool blocked = false;
        for (int i = 0; i < hits; i++)
        {
            var collider = Overlaps[i];
            Overlaps[i] = null;
            if (collider == null)
            {
                continue;
            }

            if (ownInteractions != null && collider.transform.IsChildOf(ownInteractions))
            {
                continue;
            }

            var identifier = collider.GetComponentInParent<ShipPartIdentifier>();
            if (identifier != null && identifier.Id == part.Id && collider.transform.IsChildOf(root))
            {
                continue;
            }
            // Stock 100 mm hull colliders extend past their visible surface.
            // Saved mounts can already overlap that supporting plane by 10 mm.
            // Preserve existing backing contact, including a flush continuation
            // across separately generated tile colliders.
            Vector3 mountNormal = root.rotation * part.LocalRotation * Vector3.up;
            if (supportBody != null && collider is BoxCollider hull && IsBackingHull(hull, root, mountNormal))
            {
                if (HasMountContact(supportBody, hull, mountNormal, out _))
                {
                    continue;
                }

                Vector3 origin = supportBody.transform.position;
                Vector3 backing = root.TransformPoint(
                    part.LocalPosition + part.LocalRotation * new Vector3(0, min.y, 0)
                );
                float face = HullFace(hull, origin, mountNormal);
                if (face - Vector3.Dot(backing - origin, mountNormal) <= MaximumSupportDepth)
                {
                    if (float.IsNaN(supportFace))
                    {
                        supportFace = FindSupportFace(scene, supportBody, root, mountNormal);
                    }

                    if (Mathf.Abs(face - supportFace) <= CoplanarTolerance)
                    {
                        continue;
                    }
                }
            }

            blocked = true;
        }
        if (blocked)
        {
            error = "Not enough clear space to change this Call's size.";
        }

        return !blocked;
    }

    private static bool IsBackingHull(BoxCollider box, Transform root, Vector3 normal) =>
        box.transform.IsChildOf(root)
        && box.GetComponentInParent<ShipHullColliderChunk>() != null
        && Mathf.Abs(Vector3.Dot(box.transform.forward, normal)) > .9999f;

    private static bool HasMountContact(BoxCollider body, BoxCollider hull, Vector3 normal, out float depth) =>
        Physics.ComputePenetration(
            body,
            body.transform.position,
            body.transform.rotation,
            hull,
            hull.transform.position,
            hull.transform.rotation,
            out Vector3 separation,
            out depth
        )
        && Vector3.Dot(separation, normal) > .9999f;

    private static float HullFace(BoxCollider hull, Vector3 origin, Vector3 normal) =>
        Vector3.Dot(hull.transform.TransformPoint(hull.center) - origin, normal)
        + Mathf.Abs(Vector3.Dot(hull.transform.TransformVector(Vector3.forward * (hull.size.z * .5f)), normal));

    private static float FindSupportFace(PhysicsScene scene, BoxCollider body, Transform root, Vector3 normal)
    {
        Transform t = body.transform;
        Vector3 origin = t.position;
        Vector3 half = Vector3.Scale(body.size * .5f, Vector3.Max(t.lossyScale, -t.lossyScale));
        int hits = scene.OverlapBox(
            t.TransformPoint(body.center),
            half,
            SupportOverlaps,
            t.rotation,
            ~0,
            QueryTriggerInteraction.Ignore
        );
        float face = float.PositiveInfinity;
        if (hits < SupportOverlaps.Length)
        {
            for (int i = 0; i < hits; i++)
            {
                if (
                    SupportOverlaps[i] is BoxCollider hull
                    && IsBackingHull(hull, root, normal)
                    && HasMountContact(body, hull, normal, out float depth)
                    && depth <= MaximumSupportDepth
                )
                {
                    face = Mathf.Min(face, HullFace(hull, origin, normal));
                }
            }
        }

        Array.Clear(SupportOverlaps, 0, hits);
        return face;
    }

    internal static void Install()
    {
        if (_harmony != null)
        {
            return;
        }

        if (
            typeof(StatefulPart).Assembly.ManifestModule.ModuleVersionId
            != new Guid("ae9e219b-6548-45c9-b374-2af51262ea2c")
        )
        {
            throw new NotSupportedException("Call sizing requires the inspected Ships.Interface build.");
        }

        if (
            Settings == null
            || Settings.FieldType != typeof(PartSettings)
            || ColliderParts == null
            || InteractionParts == null
            || TrackerShips == null
            || Touching == null
            || SignalMap == null
        )
        {
            throw new MissingMemberException("Unsupported native Call geometry lifecycle.");
        }

        _harmony = new HarmonyLib.Harmony("stellar.functions.call-sizing");
        try
        {
            // Geometry variants are private instance settings, not buildable content.
            // Exclude only our exact objects; preserve native duplicate-ID failures.
            _harmony.Patch(
                AccessTools.Method(typeof(PartSettingsProvider), "EnsureDictIsCached"),
                transpiler: new HarmonyMethod(typeof(CallSizing), nameof(CatalogueInventory))
            );
            foreach (string name in new[] { "AddStatefulPart", "AddStatefulPartWithoutInit" })
            {
                _harmony.Patch(
                    AccessTools.Method(typeof(ShipState), name, new[] { typeof(StatefulPart) }),
                    prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeAdd))
                );
            }

            _harmony.Patch(
                AccessTools.Method(
                    typeof(ShipState),
                    "SetPartState",
                    new[] { typeof(ushort), typeof(IStatefulPartState) }
                ),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeState)),
                postfix: new HarmonyMethod(typeof(CallSizing), nameof(AfterState))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipPartVisuals), "AddStatefulPart"),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeAdd))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipStatefulColliders), "AddStatefulPart"),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeAdd))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipInteractionColliders), "AddStatefulPart"),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeAdd))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipPartVisuals), "OnStatefulPartStateChange"),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeVisualState))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipStatefulColliders), "OnStatefulPartStateChange"),
                postfix: new HarmonyMethod(typeof(CallSizing), nameof(AfterColliderState))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipInteractionColliders), "OnStatefulPartStateChange"),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(BeforeInteractionState))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(ShipPartInstancer), "CreateFloatingPartVisualParent"),
                prefix: new HarmonyMethod(typeof(CallSizing), nameof(SparseSocketVisuals))
            );
            Ready = true;
        }
        catch
        {
            _harmony.UnpatchSelf();
            _harmony = null;
            throw;
        }
    }

    internal static void Uninstall()
    {
        CallResizeWiring.Reset();
        Ready = false;
        _harmony?.UnpatchSelf();
        _harmony = null;
        for (int i = 1; i < 3; i++)
        {
            if (Variants[i] != null)
            {
                UnityEngine.Object.Destroy(Variants[i].part);
                UnityEngine.Object.Destroy(Variants[i]);
                Variants[i] = null;
            }
        }

        Variants[3] = null;
        Array.Clear(Overlaps, 0, Overlaps.Length);
    }

    internal static void Configure(PartSettings original, GameObject prefab)
    {
        if (Variants[3] == original)
        {
            return;
        }

        if (Variants[3] != null)
        {
            throw new NotSupportedException("Call content was rebuilt during use; restart before resizing.");
        }

        var donors = Resources.FindObjectsOfTypeAll<PartSettings>();
        var combinator = donors.First(p => p.id == 45);
        var processor = donors.First(p => p.id == 54);
        var oldMesh = combinator.part.transform.Find("Visuals/MathGate").GetComponent<MeshFilter>().sharedMesh;
        Variants[3] = original;
        foreach (var screen in prefab.GetComponentsInChildren<Support.ScreenSurface>(true))
        {
            FunctionScreens.ConfigureCallFace(screen, 3);
        }

        for (int count = 1; count <= 2; count++)
        {
            var variant = UnityEngine.Object.Instantiate(original);
            variant.name = original.name + "_" + count + "Inputs";
            variant.part = UnityEngine.Object.Instantiate(prefab, prefab.transform.parent);
            variant.part.name = variant.name + "_Prefab";
            var donor = count == 1 ? processor : combinator;
            var source = donor
                .part.transform.Find(count == 1 ? "Visuals/UnaryMathGate" : "Visuals/MathGate")
                .GetComponent<MeshFilter>();
            var mesh = variant.part.GetComponentsInChildren<MeshFilter>(true).Single(m => m.sharedMesh == oldMesh);
            var offset = new Vector3(count == 1 ? -.125f : 0, 0, 0);
            mesh.sharedMesh = source.sharedMesh;
            // Mirror the wide donor's raised shoulder onto the output side of input 1.
            mesh.transform.localScale = count == 1 ? Vector3.one : new Vector3(-1f, 1f, 1f);
            mesh.transform.localPosition = offset;
            mesh.GetComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
            var bounds = variant.part.GetComponent<ShipPartBounds>();
            var sourceBounds = donor.part.GetComponent<ShipPartBounds>();
            bounds.center = sourceBounds.center + offset;
            bounds.bounds = sourceBounds.bounds;
            foreach (var socket in variant.part.GetComponentsInChildren<SocketObject>(true))
            {
                if (socket.Id != 3 && socket.Id >= count)
                {
                    UnityEngine.Object.DestroyImmediate(socket.gameObject);
                }
            }
            foreach (var screen in variant.part.GetComponentsInChildren<Support.ScreenSurface>(true))
            {
                var position = screen.transform.localPosition;
                position.x = offset.x;
                position.z = count == 1 ? -.028f : -.012f;
                screen.transform.localPosition = position;
                FunctionScreens.ConfigureCallFace(screen, count);
            }
            var interaction = variant.part.GetComponentInChildren<IShipPartInteractions>(true);
            var box = Support.SocketSeating.InteractionBox(interaction);
            box.center = box.transform.InverseTransformPoint(variant.part.transform.TransformPoint(bounds.center));
            Vector3 ratio = variant.part.transform.lossyScale;
            Vector3 local = box.transform.lossyScale;
            box.size = Vector3.Scale(
                bounds.bounds + Vector3.one * .04f,
                new Vector3(Mathf.Abs(ratio.x / local.x), Mathf.Abs(ratio.y / local.y), Mathf.Abs(ratio.z / local.z))
            );
            variant.part.AddComponent<CallVariantPrefab>();
            Variants[count] = variant;
        }
    }

    private static IEnumerable<CodeInstruction> CatalogueInventory(IEnumerable<CodeInstruction> instructions)
    {
        int matches = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, Inventory))
            {
                matches++;
                yield return new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(typeof(CallSizing), nameof(FilterCatalogueSettings))
                );
            }
        }
        if (matches != 1)
        {
            throw new NotSupportedException(
                "Native part catalogue inventory changed; expected one inspected settings query."
            );
        }
    }

    private static PartSettings[] FilterCatalogueSettings(PartSettings[] settings)
    {
        if (Variants[1] == null && Variants[2] == null)
        {
            return settings;
        }

        return settings.Where(p => !ReferenceEquals(p, Variants[1]) && !ReferenceEquals(p, Variants[2])).ToArray();
    }

    internal static PartSettings Variant(int count) =>
        count >= 1 && count <= 3 && Variants[count] != null
            ? Variants[count]
            : throw new InvalidOperationException("Call size geometry is unavailable.");

    private static void Apply(StatefulPart part, FunctionCallState state)
    {
        if (part.Settings.id != 62005)
        {
            return;
        }

        var variant = Variant(state.InputCount);
        if (!ReferenceEquals(part.Settings, variant))
        {
            Settings.SetValue(part, variant);
        }
    }

    private static void BeforeAdd(StatefulPart __0)
    {
        if (__0.State is FunctionCallState state)
        {
            Apply(__0, state);
        }
    }

    private static void BeforeState(ShipState __instance, ushort id, IStatefulPartState state, out bool __state)
    {
        __state = false;
        if (
            state is not FunctionCallState next
            || !__instance.TryGetPart(id, out var part)
            || part.Settings.id != 62005
        )
        {
            return;
        }

        __state = part.State is not FunctionCallState old || old.InputCount != next.InputCount;
        if (__state)
        {
            Apply(part, next);
        }
    }

    private static void AfterState(ShipState __instance, ushort id, bool __state)
    {
        if (!__state || !__instance.TryGetPart(id, out var part))
        {
            return;
        }
        // Native touching sockets are discovered on add, not state updates.
        RefreshTouching(ServiceLocator.GetService<ConnectionsServerTracker>(), __instance.Id, part);
        RefreshTouching(ServiceLocator.GetService<ConnectionsClientTracker>(), __instance.Id, part);
    }

    private static void RefreshTouching(ConnectionsTracker tracker, uint ship, StatefulPart part)
    {
        if (tracker == null)
        {
            return;
        }

        if (
            TrackerShips.GetValue(tracker) is IShipsProvider provider
            && provider.TryGetShip(ship, out var owner)
            && owner.TryGetStatefulPart(part.Id, out var live)
            && ReferenceEquals(live, part)
        )
        {
            DropStaleTouching(tracker, ship, part);
            Touching.Invoke(tracker, new object[] { ship, part });
        }
    }

    // Native touching discovery only runs on part add/remove; a resize swaps
    // the socket layout in place, so Touching/RelayedByPart records made under
    // the old geometry would survive as ghost connections (issue #1). Delete
    // them for every socket id any Call variant can expose, then let the
    // native add path recreate the pairs that still coincide. Cable
    // connections are managed by the native cable tracker before the geometry
    // changes.
    private static void DropStaleTouching(ConnectionsTracker tracker, uint ship, StatefulPart part)
    {
        var map = (ConnectionGroupMap)SignalMap.GetValue(tracker);
        if (map == null || part.Settings?.part == null)
        {
            return;
        }

        var ids = new HashSet<byte>();
        foreach (var settings in Variants)
        {
            if (settings?.part != null)
            {
                foreach (var socket in settings.part.GetComponentsInChildren<SocketObject>(true))
                {
                    if (socket != null)
                    {
                        ids.Add(socket.Id);
                    }
                }
            }
        }

        foreach (var socket in part.Settings.part.GetComponentsInChildren<SocketObject>(true))
        {
            if (socket != null)
            {
                ids.Add(socket.Id);
            }
        }

        foreach (byte id in ids)
        {
            var socket = new EntityPartSocket(ship, part.Id, id);
            if (!map.TryGetSocketConnections(socket, out var connections))
            {
                continue;
            }

            foreach (var connection in connections.ToArray())
            {
                if (
                    connection.Type == SocketConnectionType.Touching
                    || connection.Type == SocketConnectionType.RelayedByPart
                )
                {
                    map.OnConnectionDelete(connection);
                }
            }
        }
    }

    private static bool Changed(StatefulPartStateChangeEvent change) =>
        change.NewState is FunctionCallState next
        && change.Part.Settings.id == 62005
        && (change.PreviousState is not FunctionCallState old || next.InputCount != old.InputCount);

    private static bool BeforeVisualState(ShipPartVisuals __instance, StatefulPartStateChangeEvent change)
    {
        if (!Changed(change))
        {
            return true;
        }

        if (__instance.TryGetFloatingPartVisual(change.PartId, out var previous))
        {
            previous.GameObject.SetActive(false);
        }

        __instance.RemoveStatefulPart(change.Part);
        __instance.AddStatefulPart(change.Part);
        __instance.InitializeStatefulPart(change.Part);
        return false;
    }

    private static void AfterColliderState(ShipStatefulColliders __instance, StatefulPartStateChangeEvent change)
    {
        if (!Changed(change))
        {
            return;
        }

        var parts = (Dictionary<ushort, GameObject>)ColliderParts.GetValue(__instance);
        if (parts.TryGetValue(change.PartId, out var item) && item.TryGetComponent<BoxCollider>(out var box))
        {
            var bounds = change.Part.Settings.Bounds;
            box.center = bounds.center;
            box.size = bounds.size;
        }
    }

    private static bool BeforeInteractionState(ShipInteractionColliders __instance, StatefulPartStateChangeEvent change)
    {
        if (!Changed(change))
        {
            return true;
        }

        var parts = (Dictionary<ushort, IShipPartInteractions>)InteractionParts.GetValue(__instance);
        if (parts.TryGetValue(change.PartId, out var previous))
        {
            previous.GameObject.SetActive(false);
        }

        __instance.RemoveStatefulPart(change.Part);
        __instance.AddStatefulPart(change.Part);
        __instance.InitializeStatefulPart(change.Part);
        return false;
    }

    private static bool SparseSocketVisuals(GameObject part, Transform parent, ref GameObject __result)
    {
        if (part.GetComponent<CallVariantPrefab>() == null)
        {
            return true;
        }

        var visual = part.GetComponentInChildren<IShipPartVisual>().GameObject;
        var result = UnityEngine.Object.Instantiate(visual, parent);
        var tracker = result.AddComponent<PartSocketsTracker>();
        tracker.sockets = new Transform[4];
        foreach (var socket in part.GetComponentsInChildren<SocketObject>())
        {
            var socketVisual = UnityEngine.Object.Instantiate(socket.visualObject, result.transform);
            socketVisual.transform.localPosition =
                Quaternion.Inverse(part.transform.rotation) * (socket.transform.position - part.transform.position);
            socketVisual.transform.localRotation =
                Quaternion.Inverse(part.transform.rotation) * socket.transform.rotation;
            tracker.sockets[socket.Id] = socketVisual.transform;
        }
        foreach (var tip in part.GetComponentsInChildren<PartTip>())
        {
            var copy = UnityEngine.Object.Instantiate(tip.visualObject, result.transform);
            copy.name = tip.name;
            copy.transform.localPosition =
                Quaternion.Inverse(part.transform.rotation) * (tip.transform.position - part.transform.position);
            copy.transform.localRotation = Quaternion.Inverse(part.transform.rotation) * tip.transform.rotation;
        }
        __result = result;
        return false;
    }
}

public sealed class CallVariantPrefab : MonoBehaviour { }
