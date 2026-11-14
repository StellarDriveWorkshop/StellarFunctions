using System;
using System.Linq;
using Ships.Cables;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using UnityEngine;

namespace StellarFunctions.Support;

// Socket seating and interaction-box finishing applied after sockets are
// grafted: pushes sockets out of the part bounds, adds mounting pads for
// floating sockets, and grows a roughly-centred donor interaction box to
// cover the declared bounds. Seat() is Harmony-patched by name; keep exactly
// one overload.
public static class SocketSeating
{
    private static readonly Color PadTint = new Color(0.62f, 0.64f, 0.67f);
    private static Material _pad;
    private static Mesh _cube;

    public static void Apply(string scope, GameObject prefab)
    {
        var bounds = prefab == null ? null : prefab.GetComponent<ShipPartBounds>();
        if (bounds == null) return;
        FitInteractionBox(scope, prefab, bounds);
        foreach (SocketObject socket in prefab.GetComponentsInChildren<SocketObject>(true))
            Seat(scope, prefab, bounds, socket);
    }

    // Push a socket out of the part bounds and add a mounting pad when the
    // socket floats away from the hull. The scalar decision lives in
    // SeatMath so checks/core exercises the same code the game runs.
    public static void Seat(string scope, GameObject prefab, ShipPartBounds bounds, SocketObject socket)
    {
        if (prefab == null || bounds == null || socket == null) return;
        Vector3 dir = -prefab.transform.InverseTransformDirection(socket.transform.forward);
        Vector3 local = prefab.transform.InverseTransformPoint(socket.transform.position);
        var plan = SeatMath.Evaluate(local.x, local.y, local.z,
            bounds.center.x, bounds.center.y, bounds.center.z,
            bounds.bounds.x, bounds.bounds.y, bounds.bounds.z,
            dir.x, dir.y, dir.z);
        if (!plan.HasAxis) return;
        if (plan.AxisShift != 0f)
        {
            if (plan.Axis == 0) local.x += plan.AxisShift;
            else if (plan.Axis == 1) local.y += plan.AxisShift;
            else local.z += plan.AxisShift;
            socket.transform.position = prefab.transform.TransformPoint(local);
            NativeServices.Trace(scope, "socket " + socket.Id + " pushed out to " + SeatMath.SocketStandoff + " clear of the bounds");
        }
        if (plan.PadDistance > 0f) AddPad(scope, socket, plan.PadDistance);
    }

    private static void AddPad(string scope, SocketObject socket, float distance)
    {
        if (socket.visualObject == null) return;
        Transform visual = socket.visualObject.transform;
        Transform pad = visual.Find("StellarFunctionsSocketPad");
        float face = FaceDepth(visual);
        if (distance - face < 0.01f) return;
        if (pad != null)
        {
            ShapePad(pad, face, distance);
            return;
        }
        Material material = PadMaterial();
        if (material == null)
        {
            NativeServices.Warn(scope, "PartMetal not found, sockets get no mounting pad");
            return;
        }
        var go = new GameObject("StellarFunctionsSocketPad") { layer = socket.visualObject.layer };
        go.transform.SetParent(visual, false);
        go.AddComponent<MeshFilter>().sharedMesh = Cube();
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        ShapePad(go.transform, face, distance);
    }

    // Deepest mesh extent along the socket's local +z (the plug direction).
    private static float FaceDepth(Transform visual)
    {
        float depth = 0f;
        foreach (MeshFilter filter in visual.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter == null || filter.sharedMesh == null || filter.transform.name == "StellarFunctionsSocketPad") continue;
            Bounds bounds = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = visual.InverseTransformPoint(filter.transform.TransformPoint(bounds.center
                    + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));
                depth = Mathf.Max(depth, corner.z);
            }
        }
        return depth;
    }

    private static void ShapePad(Transform pad, float face, float distance)
    {
        float length = distance - face;
        pad.localRotation = Quaternion.identity;
        pad.localPosition = new Vector3(0f, 0f, face + length * 0.5f);
        pad.localScale = new Vector3(0.16f, 0.16f, length);
    }

    private static Material PadMaterial()
    {
        if (_pad != null) return _pad;
        Material source = NativeAssets.Material("PartMetal");
        if (source == null) return null;
        _pad = new Material(source) { name = "PartMetal_SocketPad", hideFlags = HideFlags.HideAndDontSave };
        if (_pad.HasProperty("_BaseColor")) _pad.SetColor("_BaseColor", PadTint);
        if (_pad.HasProperty("_Color")) _pad.SetColor("_Color", PadTint);
        return _pad;
    }

    private static Mesh Cube()
    {
        if (_cube == null)
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
        }
        return _cube;
    }

    // Grow the donor interaction box to cover the declared bounds when it is
    // roughly centred on the part already.
    private static void FitInteractionBox(string scope, GameObject prefab, ShipPartBounds bounds)
    {
        var interactions = prefab.GetComponentInChildren<IShipPartInteractions>();
        if (interactions == null || ((IShipPartStateReactor)interactions).GameObject == null) return;
        var box = InteractionBox(interactions);
        if (box == null) return;
        Transform boxTransform = box.transform;
        Transform prefabTransform = prefab.transform;
        Vector3 centre = prefabTransform.InverseTransformPoint(boxTransform.TransformPoint(box.center));
        Vector3 size = ScaledSize(box.size, boxTransform.lossyScale, prefabTransform.lossyScale);
        const float tolerance = 0.018f;
        Vector3 offset = centre - bounds.center;
        if (offset.sqrMagnitude > tolerance * tolerance
            || Mathf.Abs(size.x - bounds.bounds.x) > tolerance
            || Mathf.Abs(size.y - bounds.bounds.y) > tolerance
            || Mathf.Abs(size.z - bounds.bounds.z) > tolerance) return;
        Vector3 wanted = bounds.bounds + Vector3.one * 0.04f;
        box.size = ScaledSize(wanted, prefabTransform.lossyScale, boxTransform.lossyScale);
    }

    // The box collider on the interaction object, preferring the one sharing a
    // GameObject with the "PartInteraction" component, else the largest.
    public static BoxCollider InteractionBox(IShipPartInteractions interactions)
    {
        Collider[] colliders = ((IShipPartStateReactor)interactions).GameObject.GetComponentsInChildren<Collider>(true);
        Component hover = ((IShipPartStateReactor)interactions).GameObject.GetComponentsInChildren<Component>(true)
            .FirstOrDefault(c => c != null && c.GetType().Name == "PartInteraction");
        return colliders.OfType<BoxCollider>()
            .FirstOrDefault(box => hover != null && box.gameObject == hover.gameObject)
            ?? colliders.OfType<BoxCollider>().OrderByDescending(box => Volume(box.size)).FirstOrDefault();
    }

    public static float Volume(Vector3 size) => FitMath.Volume(size.x, size.y, size.z);

    private static Vector3 ScaledSize(Vector3 size, Vector3 from, Vector3 to)
        => new Vector3(
            size.x * FitMath.Ratio(from.x, to.x),
            size.y * FitMath.Ratio(from.y, to.y),
            size.z * FitMath.Ratio(from.z, to.z));
}
