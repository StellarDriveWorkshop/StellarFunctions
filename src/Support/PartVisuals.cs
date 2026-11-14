using System;
using System.Collections.Generic;
using System.Linq;
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

// Visual construction for a part prefab: donor visual root/clearing, the
// identity-compensated mesh root, mesh/screen/text children, material
// resolution, renderer-bounds measurement, interaction-box fitting and the
// hover-outline rename.
internal static class PartVisuals
{
    private static readonly HashSet<string> WarnedMaterials = new HashSet<string>(StringComparer.Ordinal);

    // The transform new meshes are parented to: the donor's part-visual object,
    // or a created "Visual" node with default visuals when the donor has none.
    public static Transform VisualRoot(PartSpec spec, GameObject prefab)
    {
        var visual = prefab.GetComponentInChildren<IShipPartVisual>();
        if (visual != null && ((IShipPartStateReactor)visual).GameObject != null)
            return ((IShipPartStateReactor)visual).GameObject.transform;
        Transform existing = prefab.transform.Find("Visual");
        GameObject host;
        if (existing != null) host = existing.gameObject;
        else
        {
            host = new GameObject("Visual");
            host.transform.SetParent(prefab.transform, false);
            host.layer = prefab.layer;
        }
        if (host.GetComponent<IShipPartVisual>() == null) host.AddComponent<DefaultShipPartVisuals>();
        NativeServices.Trace(spec.Asset, "donor had no part visual, added a default one on " + host.name);
        return host.transform;
    }

    // Strip the donor's visual children, renderers, mesh filters and custom
    // IShipPartVisual implementations, keeping a default visuals component.
    public static void ClearDonorVisual(PartSpec spec, Transform visual)
    {
        foreach (Transform child in visual.Cast<Transform>().ToList())
            UnityEngine.Object.DestroyImmediate(child.gameObject);
        foreach (Renderer renderer in visual.GetComponents<Renderer>().ToList())
            UnityEngine.Object.DestroyImmediate(renderer);
        foreach (MeshFilter filter in visual.GetComponents<MeshFilter>().ToList())
            UnityEngine.Object.DestroyImmediate(filter);
        bool removed = false;
        foreach (Component component in visual.GetComponents<Component>().ToList())
        {
            if (component is IShipPartVisual && !(component is DefaultShipPartVisuals))
            {
                UnityEngine.Object.DestroyImmediate(component);
                removed = true;
            }
        }
        if (removed && visual.GetComponent<IShipPartVisual>() == null)
        {
            visual.gameObject.AddComponent<DefaultShipPartVisuals>();
            NativeServices.Trace("partbuild", "donor visual component swapped for the default one");
        }
    }

    // Identity-compensated child so mesh local transforms match part space.
    public static Transform MeshRoot(PartSpec spec, Transform visual)
    {
        Transform existing = visual.Find("StellarFunctionsMeshes");
        if (existing != null) return existing;
        var root = new GameObject("StellarFunctionsMeshes") { layer = visual.gameObject.layer };
        root.transform.SetParent(visual, false);
        Vector3 scale = visual.lossyScale;
        root.transform.localScale = new Vector3(
            Mathf.Approximately(scale.x, 0f) ? 1f : 1f / scale.x,
            Mathf.Approximately(scale.y, 0f) ? 1f : 1f / scale.y,
            Mathf.Approximately(scale.z, 0f) ? 1f : 1f / scale.z);
        root.transform.localRotation = Quaternion.Inverse(visual.localRotation);
        root.transform.localPosition = -visual.localPosition;
        return root.transform;
    }

    // Builds one mesh child: screen/text via the Support screen builders,
    // else donor mesh or primitive shape, in that priority order.
    public static GameObject MeshChild(PartSpec spec, Transform parent, MeshSpec mesh)
    {
        if (mesh == null) return null;
        switch (MeshPlan.Route(mesh.Shape, mesh.Screen))
        {
            case MeshRoute.Text:
                return Screens.BuildText(spec.Asset, parent, mesh);
            case MeshRoute.Screen:
                return Screens.BuildScreen(spec.Asset, parent, mesh, MeshMaterial(spec, mesh));
        }

        Mesh geometry = null;
        string name = "Mesh";
        if (!string.IsNullOrEmpty(mesh.DonorPath))
        {
            geometry = DonorMesh(spec, mesh.DonorPath);
            name = mesh.DonorPath.Replace('/', '_');
        }
        else if (!string.IsNullOrEmpty(mesh.Shape))
        {
            geometry = PrimitiveMesh(mesh.Shape);
            name = mesh.Shape;
        }
        // A declared mesh that resolves to nothing is an essential-element
        // failure: the part must not report as fully constructed without it.
        if (geometry == null)
            throw new InvalidOperationException(spec.Asset + ": mesh could not be resolved from shape/donorPath");
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = mesh.Position;
        go.transform.localRotation = Quaternion.Euler(mesh.Euler);
        go.transform.localScale = mesh.Scale;
        go.AddComponent<MeshFilter>().sharedMesh = geometry;
        go.AddComponent<MeshRenderer>().sharedMaterial = MeshMaterial(spec, mesh);
        return go;
    }

    private static Material MeshMaterial(PartSpec spec, MeshSpec mesh)
    {
        Material material = NativeAssets.Material(mesh.Material) ?? NativeAssets.Material("PartMetal");
        if (material == null)
        {
            if (WarnedMaterials.Add(spec.Asset + "/" + mesh.Material))
                NativeServices.Warn(spec.Asset, "material " + mesh.Material + " not found and PartMetal missing, mesh will be untextured");
            return null;
        }
        bool needsCopy = mesh.Tint.HasValue;
        if (!needsCopy) return material;
        var copy = new Material(material) { name = material.name + "_" + spec.Asset, hideFlags = HideFlags.HideAndDontSave };
        if (copy.HasProperty("_BaseColor")) copy.SetColor("_BaseColor", mesh.Tint.Value);
        if (copy.HasProperty("_Color")) copy.SetColor("_Color", mesh.Tint.Value);
        return copy;
    }

    // donorPath syntax: "PartAssetName|child/path" — falls back to the spec
    // donor asset when no part name is given.
    private static Mesh DonorMesh(PartSpec spec, string donorPath)
    {
        int bar = donorPath.IndexOf('|');
        string partName = bar < 0 ? spec.Donor : donorPath.Substring(0, bar);
        string path = bar < 0 ? donorPath : donorPath.Substring(bar + 1);
        PartSettings settings = NativeAssets.PartByName(partName);
        if (settings == null || settings.part == null)
        {
            NativeServices.Warn(spec.Asset, "donorPath part " + partName + " not found");
            return null;
        }
        Transform node = settings.part.transform.Find(path);
        var filter = node != null ? node.GetComponent<MeshFilter>() : null;
        if (filter == null)
        {
            NativeServices.Warn(spec.Asset, "donorPath " + donorPath + " has no mesh");
            return null;
        }
        return filter.sharedMesh;
    }

    private static Mesh PrimitiveMesh(string shape)
    {
        PrimitiveType type;
        switch ((shape ?? string.Empty).ToLowerInvariant())
        {
            case "sphere": type = PrimitiveType.Sphere; break;
            case "capsule": type = PrimitiveType.Capsule; break;
            case "cylinder": type = PrimitiveType.Cylinder; break;
            case "quad": type = PrimitiveType.Quad; break;
            case "plane": type = PrimitiveType.Plane; break;
            default: type = PrimitiveType.Cube; break;
        }
        var primitive = GameObject.CreatePrimitive(type);
        Mesh mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.DestroyImmediate(primitive);
        return mesh;
    }

    // Resize the donor interaction box to the declared bounds and disable
    // overlapping colliders so clicks land on the part, not stale geometry.
    public static void FitInteraction(PartSpec spec, GameObject prefab, Vector3 boundsCenter, Vector3 boundsSize)
    {
        var interactions = prefab.GetComponentInChildren<IShipPartInteractions>();
        if (interactions == null || ((IShipPartStateReactor)interactions).GameObject == null)
        {
            NativeServices.Trace(spec.Asset, "donor has no interactions object, nothing to interact with");
            return;
        }
        var box = SocketSeating.InteractionBox(interactions);
        if (box == null)
        {
            NativeServices.Trace(spec.Asset, "donor interactions have no box collider to resize");
            return;
        }
        int disabled = 0;
        foreach (Collider collider in ((IShipPartStateReactor)interactions).GameObject.GetComponentsInChildren<Collider>(true))
            if (collider != box && collider.enabled)
            {
                collider.enabled = false;
                disabled++;
            }
        Transform boxTransform = box.transform;
        Transform prefabTransform = prefab.transform;
        box.center = boxTransform.InverseTransformPoint(prefabTransform.TransformPoint(boundsCenter));
        Vector3 boxScale = boxTransform.lossyScale;
        Vector3 prefabScale = prefabTransform.lossyScale;
        Vector3 size = boundsSize + Vector3.one * 0.04f;
        box.size = new Vector3(
            FitMath.ScaleRatio(size.x, prefabScale.x, boxScale.x),
            FitMath.ScaleRatio(size.y, prefabScale.y, boxScale.y),
            FitMath.ScaleRatio(size.z, prefabScale.z, boxScale.z));
        NativeServices.Trace(spec.Asset, "interaction collider " + boxTransform.name + " set to centre " + box.center + " size " + box.size
            + (disabled > 0 ? ", disabled " + disabled + " overlapping colliders" : string.Empty));
    }

    // Rename the mesh root to the interaction object's name so the vanilla
    // hover outline finds the custom visuals.
    public static void NameForHover(PartSpec spec, GameObject prefab)
    {
        var interactions = prefab.GetComponentInChildren<IShipPartInteractions>();
        var visual = prefab.GetComponentInChildren<IShipPartVisual>();
        if (interactions == null || visual == null || ((IShipPartStateReactor)interactions).GameObject == null
            || ((IShipPartStateReactor)visual).GameObject == null) return;
        var interaction = ((IShipPartStateReactor)interactions).GameObject.GetComponentsInChildren<Component>(true)
            .FirstOrDefault(c => c != null && c.GetType().Name == "PartInteraction");
        if (interaction == null) return;
        string name = interaction.name;
        if (FindNamed(((IShipPartStateReactor)visual).GameObject.transform, name) != null) return;
        Transform meshes = ((IShipPartStateReactor)visual).GameObject.transform.Find("StellarFunctionsMeshes");
        if (meshes == null)
        {
            NativeServices.Trace(spec.Asset, "no mod meshes to name for the hover outline");
            return;
        }
        meshes.name = name;
        NativeServices.Trace(spec.Asset, "visual named " + name + " so the vanilla hover outline finds it");
    }

    private static Transform FindNamed(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            Transform found = FindNamed(child, name);
            if (found != null) return found;
        }
        return null;
    }

    // Measure renderer bounds in prefab-local space when the spec did not
    // declare explicit bounds. Socket renderers are excluded, matching legacy.
    public static bool MeasureBounds(PartSpec spec, GameObject prefab, out Vector3 center, out Vector3 size)
    {
        center = spec.BoundsCenter;
        size = spec.BoundsSize;
        Matrix4x4 worldToLocal = prefab.transform.worldToLocalMatrix;
        bool any = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.GetComponentInParent<SocketObject>() != null) continue;
            var filter = renderer.GetComponent<MeshFilter>();
            Bounds bounds = filter != null && filter.sharedMesh != null
                ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4 toLocal = worldToLocal * renderer.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = toLocal.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)));
                if (!any) { min = max = corner; any = true; }
                else { min = Vector3.Min(min, corner); max = Vector3.Max(max, corner); }
            }
        }
        if (!any)
        {
            NativeServices.Warn(spec.Asset, "no meshes to measure, keeping the declared bounds");
            return false;
        }
        center = (min + max) * 0.5f;
        size = Vector3.Max(max - min, Vector3.one * 0.1f);
        NativeServices.Trace(spec.Asset, "bounds measured from the model: centre " + center + " size " + size);
        return true;
    }
}
