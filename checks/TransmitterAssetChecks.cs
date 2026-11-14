// Narrow asset-only inventory for a custom Signal Transmitter donor review.
// The probe runs after MainGame asset loading with scene activation disabled.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

internal static class TransmitterAssetChecks
{
    public static void Run(string output)
    {
        var lines = new List<string>();
        lines.Add("MainGame activated=" + UnityEngine.SceneManagement.SceneManager.GetSceneByName("MainGame").isLoaded);
        foreach (var asset in Resources.FindObjectsOfTypeAll<ScriptableObject>())
        {
            string typeName = asset.GetType().Name;
            if (!typeName.Contains("PartSettings") && !typeName.Contains("ItemSettings") && !typeName.Contains("Settings")) continue;
            object id = Field(asset, "id");
            string label = asset.name + " " + Convert.ToString(Field(asset, "fullLabel")) + " " + Convert.ToString(Field(asset, "techName"));
            if (typeName.Contains("PartSettings") && (Has(label, "target") || Has(label, "transmit")))
            {
                lines.Add("PART type=" + typeName + " id=" + id + " asset=" + asset.name + " label=" + label);
                var prefab = Field(asset, "part") as GameObject;
                if (prefab == null) continue;
                Transform root = prefab.transform;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    string path = PathOf(t, root);
                    lines.Add("  NODE " + path + " p=" + t.localPosition.ToString("F5") + " r=" + t.localEulerAngles.ToString("F2") + " s=" + t.localScale.ToString("F3"));
                    foreach (Component component in t.GetComponents<Component>())
                    {
                        if (component == null) continue;
                        lines.Add("    COMP " + component.GetType().FullName);
                        if (component is MeshFilter mesh && mesh.sharedMesh != null)
                            DumpMesh(lines, mesh, root, path);
                        if (component is Canvas canvas)
                        {
                            lines.Add("      CANVAS enabled=" + canvas.enabled + " active=" + canvas.isActiveAndEnabled + " mode=" + canvas.renderMode + " overrideSorting=" + canvas.overrideSorting + " order=" + canvas.sortingOrder + " planeDistance=" + canvas.planeDistance + " pixelPerfect=" + canvas.pixelPerfect + " forwardRoot=" + root.InverseTransformDirection(canvas.transform.forward).ToString("F6"));
                            var canvasRect = canvas.transform as RectTransform;
                            if (canvasRect != null)
                            {
                                var corners = new Vector3[4]; canvasRect.GetWorldCorners(corners);
                                lines.Add("      CANVAS_CORNERS_ROOT=" + string.Join(";", corners.Select(p => root.InverseTransformPoint(p).ToString("F6"))));
                            }
                        }
                        if (component is RectTransform rect)
                            lines.Add("      RECT rect=" + rect.rect.ToString("F4") + " anchorMin=" + rect.anchorMin.ToString("F4") + " anchorMax=" + rect.anchorMax.ToString("F4") + " pivot=" + rect.pivot.ToString("F4") + " sizeDelta=" + rect.sizeDelta.ToString("F4"));
                        if (component is TMP_Text text)
                        {
                            var material = text.fontSharedMaterial;
                            lines.Add("      TMP enabled=" + text.enabled + " active=" + text.isActiveAndEnabled + " text=" + text.text + " fontSize=" + text.fontSize + " min=" + text.fontSizeMin + " max=" + text.fontSizeMax + " autoSize=" + text.enableAutoSizing + " color=" + text.color + " alignment=" + text.alignment + " overflow=" + text.overflowMode + " font=" + text.font?.name + " material=" + material?.name + " shader=" + material?.shader?.name + " queue=" + material?.renderQueue);
                            DumpMaterial(lines, material);
                        }
                        if (component is CanvasRenderer canvasRenderer)
                            lines.Add("      CANVAS_RENDERER cull=" + canvasRenderer.cull + " materialCount=" + canvasRenderer.materialCount);
                        if (component is Renderer renderer)
                        {
                            lines.Add("      RENDERER enabled=" + renderer.enabled + " active=" + renderer.isVisible + " sortingLayer=" + renderer.sortingLayerName + " order=" + renderer.sortingOrder);
                            foreach (var material in renderer.sharedMaterials)
                                if (material != null)
                                {
                                    lines.Add("      MATERIAL name=" + material.name + " shader=" + material.shader?.name + " queue=" + material.renderQueue + " renderType=" + material.GetTag("RenderType", false, ""));
                                    DumpMaterial(lines, material);
                                }
                        }
                        foreach (FieldInfo f in component.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (!(f.Name.Equals("id", StringComparison.OrdinalIgnoreCase) || Has(f.Name, "socket") || Has(f.Name, "screen") || Has(f.Name, "channel"))) continue;
                            object value = f.GetValue(component);
                            if (value != null) lines.Add("      FIELD " + f.Name + "=" + value);
                        }
                    }
                }
            }
            if (typeName.Contains("ItemSettings") && (Has(label, "iron") || Has(label, "copper") || Has(label, "shard")))
                lines.Add("ITEM type=" + typeName + " id=" + id + " asset=" + asset.name + " label=" + label);
        }
        File.WriteAllLines(Path.Combine(output, "transmitter-asset-inventory.txt"), lines);
    }

    private static object Field(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null) return field.GetValue(target);
        }
        return null;
    }

    private static bool Has(string value, string needle) => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void DumpMesh(List<string> lines, MeshFilter filter, Transform root, string path)
    {
        Mesh mesh = filter.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (Vector3 vertex in vertices)
        {
            Vector3 point = root.InverseTransformPoint(filter.transform.TransformPoint(vertex));
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        lines.Add("      MESH " + mesh.name + " boundsLocal=" + mesh.bounds.ToString("F5") + " rootMin=" + min.ToString("F5") + " rootMax=" + max.ToString("F5") + " vertices=" + vertices.Length + " triangles=" + mesh.triangles.Length / 3);
        if (path == "Visual/Model/Screen" || path == "Visual/Model/ScreenGlass")
        {
            for (int i = 0; i < vertices.Length; i++)
                lines.Add("        VERTEX " + i + " local=" + vertices[i].ToString("F6") + " root=" + root.InverseTransformPoint(filter.transform.TransformPoint(vertices[i])).ToString("F6"));
            int[] triangles = mesh.triangles;
            lines.Add("        TRIANGLES=" + string.Join(",", triangles));
        }
    }

    private static void DumpMaterial(List<string> lines, Material material)
    {
        if (material == null) return;
        foreach (string property in new[] { "_ZTest", "_ZTestMode", "_ZWrite", "_Cull", "_Surface", "_SrcBlend", "_DstBlend", "_AlphaClip", "_UseUIAlphaClip", "_StencilComp", "_ColorMask", "_FaceColor", "_OutlineColor", "_Color", "_BaseColor" })
        {
            if (!material.HasProperty(property)) continue;
            string value;
            if (property.EndsWith("Color", StringComparison.Ordinal) || property == "_Color" || property == "_BaseColor") value = material.GetColor(property).ToString();
            else value = material.GetFloat(property).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            lines.Add("      MATPROP " + property + "=" + value);
        }
    }

    private static string PathOf(Transform transform, Transform root)
    {
        if (transform == root) return ".";
        var names = new Stack<string>();
        for (Transform t = transform; t != null && t != root; t = t.parent) names.Push(t.name);
        return string.Join("/", names.ToArray());
    }
}
