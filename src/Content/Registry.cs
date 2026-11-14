using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Players;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using StellarFunctions.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StellarFunctions.Content;

// Base class for per-part behaviours bound to a content definition. Server
// ticks and actions run on the authority side; config/status calls may run on
// either side.
public abstract class PartBehaviour
{
    public ContentPartDef Def { get; internal set; }

    public string Scope => Def != null ? Def.Asset : GetType().Name;

    public virtual Vector2 PanelScale => Vector2.one;

    public virtual void OnRegistered() { }

    public virtual void OnWorldEnter() { }

    public virtual void OnWorldExit() { }

    public virtual void OnPartAdded(ContentPart part) { }

    public virtual void OnPartRemoved(uint shipId, ushort partId) { }

    public virtual void ServerTick(ContentPart part, float deltaTime) { }

    public virtual void OnAction(ContentPart part, TrackedPlayerServer player, string action) { }

    public virtual string ConfigTitle(ContentPart part) => null;

    protected void Trace(string message) => ContentLog.Trace(Scope, message);

    protected void Warn(string message) => ContentLog.Warn(Scope, message);

    protected void Error(string message) => ContentLog.Error(Scope, message);
}

// Functions facade over the support-owned lifecycle host. PartBehaviour
// keeps its Functions API (ContentPart with the pack definition); a thin
// adapter translates the host's LivePart view back into ContentPart.
public static class ContentRuntime
{
    private sealed class Adapter : Support.IPartBehaviour
    {
        private readonly PartBehaviour _inner;

        public Adapter(PartBehaviour inner) => _inner = inner;

        public PartBehaviour Inner => _inner;
        public string Scope => _inner.Scope;

        public void OnRegistered() => _inner.OnRegistered();

        public void OnWorldEnter() => _inner.OnWorldEnter();

        public void OnWorldExit() => _inner.OnWorldExit();

        public void OnPartAdded(Support.LivePart part) => _inner.OnPartAdded(Content(part));

        public void OnPartRemoved(uint shipId, ushort partId) => _inner.OnPartRemoved(shipId, partId);

        public void ServerTick(Support.LivePart part, float deltaTime) => _inner.ServerTick(Content(part), deltaTime);

        private static ContentPart Content(Support.LivePart part) =>
            new ContentPart
            {
                Ship = part.Ship,
                ShipId = part.ShipId,
                PartId = part.PartId,
                Part = part.Part,
                Def = ContentRegistry.PartDef(part.Part.Settings.id),
            };
    }

    public static void Bind(ContentPartDef def, PartBehaviour behaviour)
    {
        if (def == null || behaviour == null)
        {
            return;
        }

        behaviour.Def = def;
        Support.BehaviourHost.Bind(def.Id, new Adapter(behaviour));
    }

    public static void Unbind(ushort partId) => Support.BehaviourHost.Unbind(partId);

    public static PartBehaviour Of(ushort partId) => (Support.BehaviourHost.Of(partId) as Adapter)?.Inner;

    public static bool TryResolve(IShipStateWrite ship, ushort partId, out ContentPart resolved)
    {
        resolved = default;
        StatefulPart part = default;
        if (ship == null || !((IShipStateRead)ship).TryGetStatefulPart(partId, out part) || part.Settings == null)
        {
            return false;
        }

        ContentPartDef def = ContentRegistry.PartDef(part.Settings.id);
        if (def == null)
        {
            return false;
        }

        uint shipId = ((IShipStateRead)ship).Id;
        resolved = new ContentPart
        {
            Ship = ship,
            ShipId = shipId,
            PartId = partId,
            Part = part,
            Def = def,
        };
        return true;
    }

    public static void WorldEnter() => Support.BehaviourHost.WorldEnter();

    public static void WorldExit() => Support.BehaviourHost.WorldExit();
}

// Pack registry: parses embedded JSON, owns part/behaviour lookup and the
// world-enter/exit lifecycle for registered content.
public static class ContentRegistry
{
    private static readonly List<ContentPack> Loaded = new List<ContentPack>();
    private static readonly List<string> Errors = new List<string>();
    private static readonly Dictionary<ushort, ContentPartDef> PartsById = new Dictionary<ushort, ContentPartDef>();
    private static readonly Dictionary<string, ContentPartDef> PartsByAsset = new Dictionary<string, ContentPartDef>(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly Dictionary<string, Func<PartBehaviour>> Makers = new Dictionary<
        string,
        Func<PartBehaviour>
    >(StringComparer.Ordinal);
    private static bool _installed;
    private static bool _appliedThisWorld;

    public static List<ContentPack> Packs => Loaded;
    public static IReadOnlyList<string> Problems => Errors;

    public static ContentPartDef PartDef(ushort partId) => PartsById.TryGetValue(partId, out var def) ? def : null;

    public static ContentPartDef PartDefByAsset(string asset) =>
        asset != null && PartsByAsset.TryGetValue(asset, out var def) ? def : null;

    public static PartSettings PartByAsset(string asset)
    {
        var def = PartDefByAsset(asset);
        return def != null ? Support.Registration.Get(def.Id) : null;
    }

    public static ContentPack LoadEmbedded(Assembly assembly, string resource)
    {
        EnsureInstalled();
        string text = EmbeddedText(assembly, resource);
        if (text == null)
        {
            Fail(assembly.GetName().Name + ": embedded pack " + resource + " not found");
            return null;
        }
        var pack = ContentPack.Parse(text, assembly.GetName().Name + "/" + resource, Errors);
        if (pack == null)
        {
            return null;
        }

        pack.Assembly = assembly;
        return Register(pack);
    }

    public static void Behaviour(string key, Func<PartBehaviour> make)
    {
        if (!string.IsNullOrEmpty(key) && make != null)
        {
            Makers[key] = make;
        }
    }

    private static ContentPack Register(ContentPack pack)
    {
        var existing = Loaded.FirstOrDefault(p => p.Id == pack.Id);
        if (existing != null)
        {
            Fail("pack id " + pack.Id + " loaded twice, keeping " + existing.Origin + " and ignoring " + pack.Origin);
            return existing;
        }
        Loaded.Add(pack);
        foreach (ContentPartDef def in pack.Parts)
        {
            if (PartsById.ContainsKey(def.Id))
            {
                Fail(
                    pack.Id
                        + ": part id "
                        + def.Id
                        + " already used by "
                        + PartsById[def.Id].Pack.Id
                        + "/"
                        + PartsById[def.Id].Asset
                );
                pack.Problems.Add("part id " + def.Id + " clashes with " + PartsById[def.Id].Asset);
                continue;
            }
            PartsById[def.Id] = def;
            PartsByAsset[def.Asset] = def;
            Game.SupportLink.RegisterDefinition(def);
            BindBehaviour(pack, def);
        }
        foreach (string problem in pack.Problems)
        {
            ContentLog.Warn(pack.Id, problem);
        }

        ContentLog.Info(
            "registry",
            pack.Name
                + " "
                + pack.Version
                + " loaded: "
                + pack.Parts.Count
                + " parts, "
                + pack.Research.Count
                + " techs"
        );
        return pack;
    }

    private static void BindBehaviour(ContentPack pack, ContentPartDef def)
    {
        if (string.IsNullOrEmpty(def.Behaviour))
        {
            return;
        }

        if (Makers.TryGetValue(def.Behaviour, out var make))
        {
            try
            {
                ContentRuntime.Bind(def, make());
                ContentLog.Trace(pack.Id, def.Asset + " bound to registered behaviour " + def.Behaviour);
                return;
            }
            catch (Exception error)
            {
                Fail(pack.Id + ": behaviour " + def.Behaviour + " could not be constructed :: " + error.Message);
                return;
            }
        }
        Type type = ResolveType(def.Behaviour, pack.Assembly);
        if (type == null || !typeof(PartBehaviour).IsAssignableFrom(type))
        {
            Fail(pack.Id + ": behaviour " + def.Behaviour + " is neither a registered key nor a PartBehaviour type");
            pack.Problems.Add(def.Asset + " behaviour " + def.Behaviour + " missing");
            return;
        }
        try
        {
            ContentRuntime.Bind(def, (PartBehaviour)Activator.CreateInstance(type));
            ContentLog.Trace(pack.Id, def.Asset + " bound to " + type.FullName);
        }
        catch (Exception error)
        {
            Fail(pack.Id + ": behaviour " + def.Behaviour + " could not be constructed :: " + error.Message);
        }
    }

    private static Type ResolveType(string name, Assembly preferred)
    {
        Type type = preferred?.GetType(name, false, true);
        if (type != null)
        {
            return type;
        }

        type = Type.GetType(name, false, true);
        if (type != null)
        {
            return type;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = assembly.GetType(name, false, true);
                if (type != null)
                {
                    return type;
                }
            }
            catch { }
        }
        return null;
    }

    // Installs shared hooks once: config menu patches, action broadcasts,
    // scene lifecycle and periodic content research retry.
    public static void Install() => EnsureInstalled();

    private static void EnsureInstalled()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        ContentMenus.Install();
        ContentActions.Install();
        Support.Registration.Bootstrap = Game.SupportLink.EnsureInstalled;
        Support.ResearchService.PartResolver = ResearchSpecs.ResolvePart;
        Support.BehaviourHost.ContentProbe = id => PartDef(id) != null;
        SceneManager.sceneLoaded += (scene, mode) => WorldEnter();
        SceneManager.sceneUnloaded += scene => WorldExit();
    }

    public static void WorldEnter()
    {
        _appliedThisWorld = true;
        Support.PartFactory.Invalidate();
        Game.PartLookup.Invalidate();
        // Same invalidation set GameRefs.Invalidate performed, now invoked
        // directly so the lazy GameRefs scene hook is no longer required.
        Support.NativeServices.InvalidateServices();
        Game.PlayerAccess.Invalidate();
        ContentActions.EnsureNetworking();
        foreach (ContentPartDef def in PartsById.Values)
        {
            if (!def.Attached && Support.Registration.Get(def.Id) == null)
            {
                ContentLog.Error(def.Pack.Id, def.Asset + " failed to build, donor " + def.Donor + " was not found");
            }
        }
        Support.ResearchService.Apply(ResearchSpecs.FromPacks(Loaded));
        ContentRuntime.WorldEnter();
        ContentLog.Info("registry", "world ready with " + Loaded.Count + " packs and " + PartsById.Count + " parts");
    }

    public static void WorldExit()
    {
        // Preserve the unload-side invalidation the lazy GameRefs scene hook
        // used to provide, independent of world-applied state.
        Game.PlayerAccess.Invalidate();
        Game.PartLookup.Invalidate();
        Support.NativeServices.InvalidateServices();
        if (!_appliedThisWorld)
        {
            return;
        }

        _appliedThisWorld = false;
        Support.MenuCatalog.Restore();
        ContentRuntime.WorldExit();
    }

    private static string EmbeddedText(Assembly assembly, string resource)
    {
        string leaf = resource.Replace('/', '.');
        string name = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n =>
                n == leaf
                || n.EndsWith("." + leaf, StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("." + resource.Replace('/', '.'), StringComparison.OrdinalIgnoreCase)
            );
        if (name == null)
        {
            return null;
        }

        using (var stream = assembly.GetManifestResourceStream(name))
        using (var reader = new StreamReader(stream))
        {
            return reader.ReadToEnd();
        }
    }

    private static void Fail(string message)
    {
        Errors.Add(message);
        ContentLog.Error("registry", message);
    }
}
