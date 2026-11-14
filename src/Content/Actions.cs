using System;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Serializing;
using FishNet.Transporting;
using Players;
using Players.Interface.Model;
using Players.Services;
using Ships;
using Ships.Interface.Model.State;
using Ships.Services;
using StellarFunctions.Game;
using UnityEngine;

namespace StellarFunctions.Content;

// Client-to-server part action channel. Carries { ShipId, PartId, Action } and
// is validated on the server against the interacting player's native chest
// state before reaching the part behaviour.
public static class ContentActions
{
    private struct ActionBroadcast : IBroadcast
    {
        public uint ShipId;
        public ushort PartId;
        public string Action;
    }

    private static bool _serialised;
    private static readonly Support.BroadcastLink<ActionBroadcast> _link =
        Support.BroadcastLink<ActionBroadcast>.ForServer(OnAction, true);

    public static void Install()
    {
        if (!_serialised)
        {
            _serialised = true;
            GenericWriter<ActionBroadcast>.SetWrite(
                (Writer writer, ActionBroadcast value) =>
                {
                    writer.WriteUInt32(value.ShipId);
                    writer.WriteUInt16(value.PartId);
                    writer.WriteString(value.Action);
                }
            );
            GenericReader<ActionBroadcast>.SetRead(reader => new ActionBroadcast
            {
                ShipId = reader.ReadUInt32(),
                PartId = reader.ReadUInt16(),
                Action = reader.ReadStringAllocated(),
            });
        }
        EnsureNetworking();
    }

    // Unregisters the action broadcast handler; the serializer delegates
    // stay installed so a later Install() does not replace them.
    public static void Uninstall() => _link.Uninstall();

    // Called every frame by the plugin; the support-owned link re-registers
    // the handler when the NetworkManager instance changes (scene reloads,
    // dedicated server start). Research ticking lives in Plugin.OnUpdate.
    public static void EnsureNetworking() => _link.Tick();

    public static bool Send(ContentPart part, string action)
    {
        if (part.Def == null || string.IsNullOrEmpty(action))
            return false;
        EnsureNetworking();
        NetworkManager manager = InstanceFinder.NetworkManager;
        if (manager == null)
            return false;
        if (manager.IsServerStarted)
            return Dispatch(PlayerAccess.LocalServerPlayer, part.ShipId, part.PartId, action);
        if (!manager.IsClientStarted)
            return false;
        manager.ClientManager.Broadcast(
            new ActionBroadcast
            {
                ShipId = part.ShipId,
                PartId = part.PartId,
                Action = action,
            },
            Channel.Reliable
        );
        return true;
    }

    private static void OnAction(NetworkConnection connection, ActionBroadcast broadcast, Channel channel) =>
        Dispatch(PlayerFor(connection), broadcast.ShipId, broadcast.PartId, broadcast.Action);

    private static TrackedPlayerServer PlayerFor(NetworkConnection connection)
    {
        var players = Support.NativeServices.CachedService<PlayersServerTracker>();
        if (players?.ActivePlayers == null || connection == null)
            return null;
        foreach (TrackedPlayerServer player in players.ActivePlayers)
            if (player != null && ((TrackedPlayer)player).ConnectionId == connection.ClientId)
                return player;
        return null;
    }

    // Server-side gate: the player must be in the native chest-interaction
    // state for exactly this ship and part.
    private static bool Dispatch(TrackedPlayerServer player, uint shipId, ushort partId, string action)
    {
        if (player == null)
            return false;
        PlayerState state = ((TrackedPlayer)player).State;
        if (state.Type != PlayerStateType.InteractingWithChest || state.EntityId != shipId || state.PartId != partId)
            return false;
        var tracker = Support.NativeServices.CachedService<ShipsServerTracker>();
        TrackedShipServer ship = tracker == null ? null : tracker.GetShip(shipId);
        if (ship == null || !ContentRuntime.TryResolve((IShipStateWrite)(object)ship, partId, out var resolved))
            return false;
        PartBehaviour behaviour = ContentRuntime.Of(resolved.Def.Id);
        if (behaviour == null)
            return false;
        try
        {
            behaviour.OnAction(resolved, player, action);
            return true;
        }
        catch (Exception error)
        {
            string op = action ?? string.Empty;
            int colon = op.IndexOf(':');
            if (colon > 0)
                op = op.Substring(0, colon);
            if (op.Length > 48)
                op = op.Substring(0, 48);
            ContentLog.Error(
                resolved.Def.Asset,
                "action failed on ship " + shipId + " part " + partId + " op " + op,
                error
            );
            return false;
        }
    }
}
