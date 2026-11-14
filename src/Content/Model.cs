using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using UnityEngine;

namespace StellarFunctions.Content;

// Data transfer objects and per-part configuration storage for the
// Functions-owned content kit. Shapes mirror the embedded pack JSON.

public struct ContentPart
{
    public IShipStateWrite Ship;
    public uint ShipId;
    public ushort PartId;
    public StatefulPart Part;
    public ContentPartDef Def;

    public bool Valid => Ship != null && Part != null && Def != null;
    public string Label => (Def == null ? "part" : Def.Label) + " #" + PartId;
    public Vector3 LocalPosition => Part != null ? Part.LocalPosition : Vector3.zero;
    public Quaternion LocalRotation => Part != null ? Part.LocalRotation : Quaternion.identity;
}

public sealed class ContentPartDef
{
    public ushort Id;
    public string Asset;
    public string Label;
    public string Description;
    public string Donor = "SignalSplitter";
    public string State;
    public string Behaviour;
    public string BuildMenuAfter;
    public float Mass = 10f;
    public SnappingStyle Snapping;
    public Vector3 BoundsCenter = new Vector3(0f, 0.25f, 0f);
    public Vector3 BoundsSize = new Vector3(0.9f, 0.5f, 0.9f);
    public bool ClearDonorVisual = true;
    public bool BoundsAuto = true;
    public bool SocketsDeclared;
    public bool SnappingDeclared;
    public bool FitInteraction = true;
    public string Menu = "auto";
    public bool Attached;
    public readonly Dictionary<uint, int> Cost = new Dictionary<uint, int>();
    public readonly List<MeshDef> Meshes = new List<MeshDef>();
    public readonly List<SocketDef> Sockets = new List<SocketDef>();
    public ContentPack Pack;
}

public sealed class SocketDef
{
    public byte Id;
    public string Type = "item";
    public Vector3 Position;
    public Vector3? Facing;
    public bool Relayed;
}

public sealed class MeshDef
{
    public string Shape;
    public string DonorPath;
    public string Material;
    public Vector3 Position;
    public Vector3 Euler;
    public Vector3 Scale = Vector3.one;
    public Color? Tint;
    public string Text;
    public string Screen;
    public string Font;
    public Color? TextColor;
    public int Columns = 16;
    public float Speed = 6f;
}

public sealed class ContentResearchDef
{
    public ushort Id;
    public string Name;
    public Vector2Int Position;
    public bool Hidden;
    public ushort GrantedBy;
    public string GrantedByName;
    public readonly List<ushort> Requires = new List<ushort>();
    public readonly List<string> Parts = new List<string>();
    public readonly List<string> Features = new List<string>();
    public readonly Dictionary<uint, int> Cost = new Dictionary<uint, int>();
}

public sealed class ContentPack
{
    public string Id;
    public string Name;
    public string Version = "0.0.0";
    public string Origin;
    public Assembly Assembly;
    public string Folder;
    public readonly List<ContentPartDef> Parts = new List<ContentPartDef>();
    public readonly List<ContentResearchDef> Research = new List<ContentResearchDef>();
    public readonly List<string> Problems = new List<string>();

    public static ContentPack Parse(string json, string origin, List<string> errors)
    {
        JObject root;
        try { root = JObject.Parse(json); }
        catch (Exception ex) { errors.Add(origin + ": not valid json, " + ex.Message); return null; }

        var pack = new ContentPack
        {
            Origin = origin,
            Id = Str(root, "id"),
            Name = Str(root, "name"),
            Version = Str(root, "version") ?? "0.0.0"
        };
        if (string.IsNullOrEmpty(pack.Id)) { errors.Add(origin + ": pack has no id"); return null; }
        if (string.IsNullOrEmpty(pack.Name)) pack.Name = pack.Id;

        foreach (JObject part in Children(root, "parts"))
        {
            var def = ParsePart(part, pack);
            if (def != null) pack.Parts.Add(def);
        }
        foreach (JObject tech in Children(root, "research"))
        {
            var research = ParseResearch(tech, pack);
            if (research != null) pack.Research.Add(research);
        }
        // "items" and "attach" sections are accepted but unused by this pack.
        return pack;
    }

    private static ContentPartDef ParsePart(JObject o, ContentPack pack)
    {
        ushort id = (ushort)Num(o, "id", 0);
        string asset = Str(o, "asset");
        if (id == 0 || string.IsNullOrEmpty(asset))
        {
            pack.Problems.Add("part with no id or asset name skipped");
            return null;
        }
        var def = new ContentPartDef
        {
            Pack = pack,
            Id = id,
            Asset = asset,
            Label = Str(o, "label") ?? asset,
            Description = Str(o, "description") ?? string.Empty,
            Donor = Str(o, "donor") ?? "SignalSplitter",
            State = Str(o, "state") ?? "SignalSplitterState",
            Behaviour = Str(o, "behaviour"),
            BuildMenuAfter = Str(o, "buildMenuAfter"),
            Mass = (float)Num(o, "mass", 10),
            Menu = Str(o, "menu") ?? "auto"
        };
        string snapping = Str(o, "snapping");
        if (!string.IsNullOrEmpty(snapping))
        {
            SnappingStyle style;
            if (Enum.TryParse(snapping, true, out style))
            {
                def.Snapping = style;
                def.SnappingDeclared = true;
            }
            else pack.Problems.Add(asset + ": unknown snapping style " + snapping + ", keeping the donor's");
        }

        var bounds = o["bounds"] as JObject;
        if (bounds != null)
        {
            Vector3? center = Vec3(bounds["center"]);
            Vector3? size = Vec3(bounds["size"]);
            if (center.HasValue) def.BoundsCenter = center.Value;
            if (size.HasValue) def.BoundsSize = size.Value;
            def.BoundsAuto = !center.HasValue && !size.HasValue;
        }

        var visual = o["visual"] as JObject;
        if (visual != null)
        {
            def.ClearDonorVisual = Bool(visual, "clearDonor", true);
            def.FitInteraction = Bool(visual, "fitInteraction", true);
            foreach (JObject mesh in Children(visual, "meshes")) def.Meshes.Add(ParseMesh(mesh));
        }
        def.SocketsDeclared = o["sockets"] != null;
        foreach (JObject socket in Children(o, "sockets"))
        {
            def.Sockets.Add(new SocketDef
            {
                Id = (byte)Num(socket, "id", 0),
                Type = Str(socket, "type") ?? "item",
                Position = Vec3(socket["position"]) ?? Vector3.zero,
                Facing = Vec3(socket["facing"]),
                Relayed = Bool(socket, "relayed", false)
            });
        }
        ParseCost(o["cost"] as JObject, def.Cost);
        return def;
    }

    private static MeshDef ParseMesh(JObject o)
    {
        return new MeshDef
        {
            Shape = Str(o, "shape"),
            DonorPath = Str(o, "donorPath"),
            Material = Str(o, "material") ?? "PartMetal",
            Position = Vec3(o["position"]) ?? Vector3.zero,
            Euler = Vec3(o["rotation"]) ?? Vector3.zero,
            Scale = Vec3(o["scale"]) ?? Vector3.one,
            Tint = ColorOf(o["color"]),
            Text = Str(o, "text"),
            Screen = Str(o, "screen"),
            Font = Str(o, "font"),
            TextColor = ColorOf(o["textColor"]),
            Columns = Mathf.Clamp((int)Num(o, "columns", 16), 4, 64),
            Speed = Mathf.Clamp((float)Num(o, "speed", 6), 0f, 40f)
        };
    }

    private static ContentResearchDef ParseResearch(JObject o, ContentPack pack)
    {
        ushort id = (ushort)Num(o, "id", 0);
        if (id == 0) { pack.Problems.Add("research with no id skipped"); return null; }
        var def = new ContentResearchDef
        {
            Id = id,
            Name = Str(o, "name") ?? ("Tech " + id),
            Hidden = Bool(o, "hidden", false),
            GrantedBy = (ushort)Num(o, "grantedBy", 0),
            GrantedByName = Str(o, "grantedByName"),
            Position = Vec2(o["position"]) ?? new Vector2Int(0, 0)
        };
        if (!def.Hidden && o["position"] == null)
            pack.Problems.Add(def.Name + ": position must be [x, y], defaulting to 0,0");
        var requires = o["requires"] as JArray;
        if (requires != null) def.Requires.AddRange(requires.Select(t => t.ToObject<ushort>()));
        var parts = o["parts"] as JArray;
        if (parts != null) def.Parts.AddRange(parts.Select(t => (string)t));
        var features = o["features"] as JArray;
        if (features != null) def.Features.AddRange(features.Select(t => (string)t));
        ParseCost(o["cost"] as JObject, def.Cost);
        return def;
    }

    private static void ParseCost(JObject o, Dictionary<uint, int> cost)
    {
        if (o == null) return;
        foreach (var pair in o)
            if (uint.TryParse(pair.Key, out uint item)) cost[item] = pair.Value.ToObject<int>();
    }

    private static IEnumerable<JObject> Children(JObject o, string name)
    {
        if (o == null) yield break;
        var array = o[name] as JArray;
        if (array == null) yield break;
        foreach (var token in array)
        {
            var child = token as JObject;
            if (child != null) yield return child;
        }
    }

    private static string Str(JObject o, string name)
    {
        var token = o[name];
        return token != null && token.Type != JTokenType.Null ? (string)token : null;
    }

    private static double Num(JObject o, string name, double fallback)
    {
        var token = o[name];
        return token != null && token.Type != JTokenType.Null ? (double)token : fallback;
    }

    private static bool Bool(JObject o, string name, bool fallback)
    {
        var token = o[name];
        return token != null && token.Type != JTokenType.Null ? (bool)token : fallback;
    }

    private static Vector3? Vec3(JToken token)
    {
        var array = token as JArray;
        if (array == null || array.Count < 3) return null;
        return new Vector3((float)array[0], (float)array[1], (float)array[2]);
    }

    private static Vector2Int? Vec2(JToken token)
    {
        var array = token as JArray;
        if (array == null || array.Count < 2) return null;
        return new Vector2Int((int)array[0], (int)array[1]);
    }

    private static Color? ColorOf(JToken token)
    {
        var array = token as JArray;
        if (array == null || array.Count < 3) return null;
        return new Color((float)array[0], (float)array[1], (float)array[2], array.Count > 3 ? (float)array[3] : 1f);
    }
}

public enum ContentLogLevel { Trace, Info, Warn, Error }

// Thin wrapper over MelonLogger.
public static class ContentLog
{
    private static readonly HashSet<string> OnceKeys = new HashSet<string>(StringComparer.Ordinal);

    public static ContentLogLevel ConsoleFloor = ContentLogLevel.Warn;

    public static void Trace(string scope, string message) => Write(ContentLogLevel.Trace, scope, message);
    public static void Info(string scope, string message) => Write(ContentLogLevel.Info, scope, message);
    public static void Warn(string scope, string message) => Write(ContentLogLevel.Warn, scope, message);
    public static void Error(string scope, string message) => Write(ContentLogLevel.Error, scope, message);
    public static void Error(string scope, string message, Exception error) => Write(ContentLogLevel.Error, scope, message + " :: " + error);

    public static bool Once(string scope, string key, ContentLogLevel level, string message)
    {
        string display = scope + "/" + key;
        if (!OnceKeys.Add(level + "|" + display)) return false;
        Write(level, display, message);
        return true;
    }

    private static void Write(ContentLogLevel level, string scope, string message)
    {
        if (level < ConsoleFloor) return;
        string text = scope + ": " + message;
        switch (level)
        {
            case ContentLogLevel.Error: MelonLoader.MelonLogger.Error(text); break;
            case ContentLogLevel.Warn: MelonLoader.MelonLogger.Warning(text); break;
            default: MelonLoader.MelonLogger.Msg(text); break;
        }
    }
}
