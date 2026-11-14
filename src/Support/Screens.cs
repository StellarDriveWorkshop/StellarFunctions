using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace StellarFunctions.Support;

// Presentation frame a screen-channel provider returns. Pure data; the
// Support ScreenSurface consumes it directly.
public struct ScreenFrame
{
    public string Text;
    public bool Scroll;
    public Color? TextColor;
}

// Support-owned screen infrastructure: channel provider registry, TMP font
// discovery, and world-space screen/text construction. Contains only
// reusable machinery — channel content and display policy stay with the
// owning mod (Functions registers "stellar-functions.status" itself).
//
// Channel ownership: registration is last-writer-wins per channel name and
// providers are expected to use namespaced channel names. Unprovide removes
// a provider only when the expected delegate equals the registered one, so
// a stale provider cannot unregister its replacement; there is deliberately
// no ownership-token framework yet.
public static class Screens
{
    private static readonly ChannelProviders<GameObject, ScreenFrame> Channels = new ChannelProviders<GameObject, ScreenFrame>();
    private static TMP_FontAsset _font;
    private static Mesh _quad;

    public static void Provide(string channel, Func<GameObject, ScreenFrame> source)
        => Channels.Provide(channel, source);

    public static bool Unprovide(string channel, Func<GameObject, ScreenFrame> expected)
        => Channels.Unprovide(channel, expected);

    internal static bool TryGet(string channel, GameObject screen, out ScreenFrame frame)
    {
        frame = default;
        if (!Channels.TryGetProvider(channel, out var source)) return false;
        try
        {
            frame = source(screen);
            return true;
        }
        catch (Exception error)
        {
            // Once per channel: a throwing provider must not log at the
            // 10 Hz polling rate.
            if (Channels.MarkProviderError(channel))
                NativeServices.Error("screens", "screen " + channel + " failed :: " + error.Message);
            return false;
        }
    }

    public static TMP_FontAsset Font(string name)
    {
        TMP_FontAsset[] all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        if (!string.IsNullOrEmpty(name))
        {
            var named = all.FirstOrDefault(f => f != null && string.Equals(f.name, name, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
        }
        if (_font != null) return _font;
        _font = Resources.FindObjectsOfTypeAll<TMP_Text>()
            .Where(t => t != null && t.font != null)
            .GroupBy(t => t.font)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault(f => f.name != null && !f.name.StartsWith("Liberation", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(f => f != null && !f.name.StartsWith("Liberation", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(f => f != null);
        if (_font != null)
            NativeServices.Trace("screens", "game font " + _font.name);
        return _font;
    }

    // Builds a world-space text mesh from a MeshSpec.
    public static GameObject BuildText(string name, Transform parent, MeshSpec mesh)
    {
        var go = new GameObject("Text", typeof(RectTransform)) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = mesh.Position;
        go.transform.localRotation = Quaternion.Euler(mesh.Euler);
        BuildLabel(go, mesh.Font, mesh.Text ?? string.Empty,
            mesh.TextColor ?? mesh.Tint ?? Color.white, new Vector2(mesh.Scale.x, mesh.Scale.y));
        return go;
    }

    // Builds a scrolling display (panel quad + TextMeshPro) with the
    // Support ScreenSurface component.
    public static GameObject BuildScreen(string name, Transform parent, MeshSpec mesh, Material material)
    {
        var go = new GameObject(ScreenText.FormatName(mesh.Screen, mesh.Columns, mesh.Speed));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = mesh.Position;
        go.transform.localRotation = Quaternion.Euler(mesh.Euler);
        var panel = new GameObject("Panel") { layer = go.layer };
        panel.transform.SetParent(go.transform, false);
        panel.transform.localScale = new Vector3(mesh.Scale.x, mesh.Scale.y, 1f);
        panel.AddComponent<MeshFilter>().sharedMesh = Quad();
        panel.AddComponent<MeshRenderer>().sharedMaterial = material;
        var text = new GameObject("Text", typeof(RectTransform)) { layer = go.layer };
        text.transform.SetParent(go.transform, false);
        text.transform.localPosition = new Vector3(0f, 0f, -0.004f);
        BuildLabel(text, mesh.Font, string.Empty,
            mesh.TextColor ?? new Color(0.45f, 0.95f, 1f), new Vector2(mesh.Scale.x * 0.92f, mesh.Scale.y * 0.8f));
        go.AddComponent<ScreenSurface>();
        return go;
    }

    private static void BuildLabel(GameObject host, string fontName, string content, Color color, Vector2 size)
    {
        var text = host.AddComponent<TextMeshPro>();
        TMP_FontAsset font = Font(fontName);
        if (font != null) text.font = font;
        text.rectTransform.sizeDelta = size;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.enableAutoSizing = true;
        text.fontSizeMin = 0.05f;
        text.fontSizeMax = 40f;
        text.color = color;
        text.text = content;
        text.ForceMeshUpdate(false, false);
    }

    private static Mesh Quad()
    {
        if (_quad == null)
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
        }
        return _quad;
    }
}
