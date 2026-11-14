using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using Ships.Services;

namespace StellarFunctions.Support;

// Support-owned part lifecycle host. Drives per-part behaviour callbacks
// from the native server-side TrackedShipServer.UpdateParts postfix:
// added-edge detection, server ticks with a mute-after-N failure budget,
// removal notification to every bound behaviour, and world enter/exit
// fan-out. Server-authoritative by construction: it only observes
// TrackedShipServer state and never touches players, UI or cameras, so it
// is safe on a dedicated server with zero players.
//
// The host knows nothing about pack definitions: the caller injects
// ContentProbe (settings.id -> is a content part) and binds behaviours by
// the same id space. Action handling and configuration stay with the
// caller's behaviour implementation.
public static class BehaviourHost
{
    private const string Scope = "runtime";
    private const int MuteAfter = 5;

    private static readonly Dictionary<ushort, IPartBehaviour> Behaviours = new Dictionary<ushort, IPartBehaviour>();
    private static readonly PartTracking Tracking = new PartTracking();
    private static readonly FailureBudget Budget = new FailureBudget(MuteAfter);
    private static HarmonyLib.Harmony _harmony;
    private static bool _patched;

    // Caller-supplied content-part probe over PartSettings.id. Must be set
    // before the first UpdateParts tick; null means "no content parts".
    public static Func<ushort, bool> ContentProbe { get; set; }

    public static int BehaviourCount => Behaviours.Count;
    public static int TrackedShips => Tracking.ShipCount;

    public static void Bind(ushort partId, IPartBehaviour behaviour)
    {
        if (behaviour == null) return;
        Behaviours[partId] = behaviour;
        EnsurePatch();
        Safe(behaviour, "behaviour registration", behaviour.OnRegistered);
    }

    public static void Unbind(ushort partId) => Behaviours.Remove(partId);

    public static IPartBehaviour Of(ushort partId)
        => Behaviours.TryGetValue(partId, out IPartBehaviour behaviour) ? behaviour : null;

    public static void WorldEnter()
    {
        Tracking.Clear();
        Budget.Reset();
        foreach (IPartBehaviour behaviour in Behaviours.Values) Safe(behaviour, "world enter", behaviour.OnWorldEnter);
    }

    public static void WorldExit()
    {
        Tracking.Clear();
        Budget.Reset();
        foreach (IPartBehaviour behaviour in Behaviours.Values) Safe(behaviour, "world exit", behaviour.OnWorldExit);
    }

    public static bool PartStillExists(uint shipId, ushort partId)
    {
        var tracker = NativeServices.Service<ShipsServerTracker>();
        TrackedShipServer ship = tracker == null ? null : tracker.GetShip(shipId);
        StatefulPart part = default;
        return ship != null && ship.TryGetStatefulPart(partId, out part);
    }

    private static void EnsurePatch()
    {
        if (_patched) return;
        var method = AccessTools.Method(typeof(TrackedShipServer), "UpdateParts", new Type[] { typeof(float) }, null);
        if (method == null)
        {
            NativeServices.Error(Scope, "TrackedShipServer.UpdateParts not found, behaviours will never tick");
            return;
        }
        try
        {
            // A previous attempt may have installed the postfix before failing
            // later; never stack a duplicate.
            var info = HarmonyLib.Harmony.GetPatchInfo(method);
            if (info != null && info.Postfixes.Any(p => p.owner == "stellarfunctions.support.runtime"))
            {
                // Adopt the existing postfix but keep an owner instance so
                // Uninstall can still remove it by harmony id.
                _harmony = new HarmonyLib.Harmony("stellarfunctions.support.runtime");
                _patched = true;
                return;
            }
            _harmony = new HarmonyLib.Harmony("stellarfunctions.support.runtime");
            _harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(BehaviourHost), nameof(AfterUpdateParts)));
        }
        catch (Exception error)
        {
            NativeServices.Error(Scope, "server tick hook install failed", error);
            return;
        }
        _patched = true;
        NativeServices.Trace(Scope, "server tick hook installed");
    }

    // Removes only this host's own postfix (UnpatchSelf is scoped to the
    // "stellarfunctions.support.runtime" owner id). Safe to call when never
    // patched; a later Bind re-installs through EnsurePatch.
    public static void Uninstall()
    {
        try { _harmony?.UnpatchSelf(); }
        catch (Exception error) { NativeServices.Error(Scope, "server tick hook removal failed", error); }
        _harmony = null;
        _patched = false;
    }

    // Harmony postfix on TrackedShipServer.UpdateParts(float). Ships without
    // content parts and no pending removals need no work; this keeps the
    // per-ship update cheap on large servers.
    public static void AfterUpdateParts(TrackedShipServer __instance, float deltaTime)
    {
        if (__instance == null) return;
        uint shipId = ((IShipStateRead)__instance).Id;
        var probe = ContentProbe;
        if (probe == null) return;
        bool hasContent = false;
        foreach (StatefulPart candidate in __instance.StatefulParts)
            if (candidate?.Settings != null && probe(candidate.Settings.id)) { hasContent = true; break; }
        if (!hasContent && !Tracking.HasPending(shipId)) return;

        var present = new HashSet<ushort>();
        // Snapshot like the original PartsOn: a callback that mutates the
        // live StatefulParts collection must not corrupt this enumeration.
        foreach (StatefulPart part in __instance.StatefulParts.ToList())
        {
            if (part?.Settings == null || !probe(part.Settings.id)) continue;
            ushort contentId = part.Settings.id;
            present.Add(part.Id);
            IPartBehaviour behaviour = Of(contentId);
            if (behaviour == null) continue;
            LivePart live = new LivePart((IShipStateWrite)(object)__instance, shipId, part.Id, part);
            if (Tracking.Mark(shipId, part.Id))
            {
                LivePart added = live;
                Safe(behaviour, "part added", () => behaviour.OnPartAdded(added));
                NativeServices.Trace(behaviour.Scope, "placed on ship " + shipId + " as part " + part.Id);
            }
            if (Budget.Muted(contentId)) continue;
            try { behaviour.ServerTick(live, deltaTime); }
            catch (Exception error) { TickFailed(contentId, behaviour.Scope, error); }
        }
        foreach (ushort gone in Tracking.CollectRemoved(shipId, present))
        {
            foreach (IPartBehaviour behaviour in Behaviours.Values)
            {
                uint sid = shipId;
                Safe(behaviour, "part removed", () => behaviour.OnPartRemoved(sid, gone));
            }
            NativeServices.Trace(Scope, "part " + gone + " removed from ship " + shipId);
        }
    }

    private static void TickFailed(ushort partId, string scope, Exception error)
    {
        switch (Budget.Failed(partId))
        {
            case FailureBudget.Outcome.First:
                NativeServices.Error(scope, "server tick failed", error);
                break;
            case FailureBudget.Outcome.Muted:
                NativeServices.Error(scope, "server tick muted after " + MuteAfter + " failures :: " + error.Message);
                break;
        }
    }

    private static void Safe(IPartBehaviour behaviour, string what, Action call)
    {
        try { call(); }
        catch (Exception error) { NativeServices.Error(behaviour.Scope, what + " failed", error); }
    }
}
