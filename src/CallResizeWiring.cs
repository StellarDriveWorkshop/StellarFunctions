using System;
using System.Collections.Generic;
using System.Linq;
using Core.Services;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Parts.Common.Model;
using Ships.Services;
using StellarFunctions.Content;
using UnityEngine;

namespace StellarFunctions;

internal static class CallResizeWiring
{
    internal const string ConfirmAction = "confirm-call-resize:";
    internal const string CancelAction = "cancel-call-resize:";
    private const string Stale = "Call size or wiring changed. Choose the size again.";
    private const float ConfirmationSeconds = 30;

    internal sealed class Confirmation
    {
        public string Token;
        public int InputCount;
        public byte Inputs;
    }

    private sealed record Plan(SocketConnection[] Connections, CableData[] Removed, byte Inputs);

    private sealed record Pending(
        uint ShipId,
        ushort PartId,
        Guid InstanceId,
        string FunctionId,
        int OldCount,
        string Session,
        Confirmation Confirmation,
        Plan Plan,
        float Expires
    );

    private static readonly Dictionary<uint, Pending> Offers = new();

    internal static void Reset() => Offers.Clear();

    internal static bool IsValidConfirmation(Confirmation confirmation) =>
        confirmation == null
        || (
            Guid.TryParseExact(confirmation.Token, "N", out var token)
            && token != Guid.Empty
            && confirmation.InputCount is 1 or 2
            && confirmation.Inputs != 0
            && (confirmation.Inputs & ~(7 & ~((1 << confirmation.InputCount) - 1))) == 0
        );

    private static bool Matches(Pending pending, uint shipId, ushort partId, FunctionCallState state) =>
        pending.Expires > Time.realtimeSinceStartup
        && pending.ShipId == shipId
        && pending.PartId == partId
        && pending.InstanceId == state.InstanceId
        && pending.FunctionId == state.SelectedFunctionId
        && pending.OldCount == state.InputCount
        && string.IsNullOrEmpty(state.PendingFunctionId);

    internal static Confirmation GetConfirmation(uint playerId, ContentPart part, string session) =>
        Offers.TryGetValue(playerId, out var pending)
        && pending.Session == session
        && part.Part?.State is FunctionCallState state
        && Matches(pending, part.ShipId, part.PartId, state)
            ? pending.Confirmation
            : null;

    internal static void Request(
        TrackedShipServer ship,
        StatefulPart part,
        FunctionCallState state,
        uint playerId,
        string session,
        int count
    )
    {
        Offers.Remove(playerId);
        if (count == state.InputCount)
        {
            CallSizing.Reply(ship, part, "");
            return;
        }

        string problem = Inspect(ship, part, state, count, out var plan);
        if (problem != null)
        {
            CallSizing.Reply(ship, part, problem);
            return;
        }

        if (plan.Inputs == 0)
        {
            Resize(ship, part, state, count);
            return;
        }

        float now = Time.realtimeSinceStartup;
        foreach (uint expired in Offers.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
        {
            Offers.Remove(expired);
        }

        Offers[playerId] = new Pending(
            ship.Id,
            part.Id,
            state.InstanceId,
            state.SelectedFunctionId,
            state.InputCount,
            session,
            new Confirmation
            {
                Token = Guid.NewGuid().ToString("N"),
                InputCount = count,
                Inputs = plan.Inputs,
            },
            plan,
            now + ConfirmationSeconds
        );
        CallSizing.Reply(ship, part, "");
    }

    internal static bool HandleConfirmation(
        TrackedShipServer ship,
        StatefulPart part,
        FunctionCallState state,
        uint playerId,
        string action
    )
    {
        bool cancel = action.StartsWith(CancelAction, StringComparison.Ordinal);
        if (!cancel && !action.StartsWith(ConfirmAction, StringComparison.Ordinal))
        {
            return false;
        }

        string token = action.Substring(cancel ? CancelAction.Length : ConfirmAction.Length);
        if (
            !Offers.TryGetValue(playerId, out var pending)
            || token != pending.Confirmation.Token
            || !Matches(pending, ship.Id, part.Id, state)
        )
        {
            if (!cancel)
            {
                CallSizing.Reply(ship, part, Stale);
            }
            return true;
        }

        Offers.Remove(playerId);
        if (cancel)
        {
            return true;
        }

        int count = pending.Confirmation.InputCount;
        string problem = Inspect(ship, part, state, count, out var plan);
        if (problem != null)
        {
            CallSizing.Reply(ship, part, problem);
            return true;
        }
        if (plan.Inputs != pending.Plan.Inputs || !plan.Connections.SequenceEqual(pending.Plan.Connections))
        {
            CallSizing.Reply(ship, part, Stale);
            return true;
        }

        // Use the same registry removal that the game uses when discarding a
        // picked-up cable. It removes the saved record and notifies clients.
        // Resize only after every confirmed cable is absent from both stores.
        var cables = ServiceLocator.GetService<CablesServerTracker>();
        var connections = ServiceLocator.GetService<ConnectionsServerTracker>();
        try
        {
            foreach (var cable in plan.Removed)
            {
                cables.DeleteCable(cable.Id);
                var edge = cable.ToSocketConnection();
                if (
                    cables.TryGetCableState(cable.Id, out _)
                    || HasEdge(connections, edge.SocketA.Value, edge)
                    || HasEdge(connections, edge.SocketB.Value, edge)
                )
                {
                    CallSizing.Reply(ship, part, "Cable removal did not complete. Check the wiring and try again.");
                    return true;
                }
            }
        }
        catch (Exception error)
        {
            ContentLog.Error("resize Call", "Native cable removal failed", error);
            CallSizing.Reply(ship, part, "Cable removal was interrupted. Check the wiring before trying again.");
            return true;
        }

        if (
            part.State is not FunctionCallState live
            || live.InstanceId != state.InstanceId
            || live.InputCount != state.InputCount
            || Inspect(ship, part, live, count, out var remaining, checkContacts: true) != null
            || remaining.Inputs != 0
            || !remaining.Connections.SequenceEqual(
                plan.Connections.Select(edge => plan.Removed.Any(cable => cable.Id == edge.CableId) ? default : edge)
            )
        )
        {
            CallSizing.Reply(ship, part, "Cables were removed. The Call could not resize. Choose the size again.");
            return true;
        }

        Resize(ship, part, live, count);
        return true;
    }

    private static bool HasEdge(ConnectionsServerTracker tracker, EntityPartSocket socket, SocketConnection edge) =>
        tracker.SignalConnectionGroups.TryGetSocketConnections(socket, out var edges) && edges.Contains(edge);

    private static void Resize(TrackedShipServer ship, StatefulPart part, FunctionCallState state, int count)
    {
        ship.UpdatePartState(part.Id, CallSizing.AtSize(state, count));
        CallSizing.InvalidateAfterResize();
    }

    private static string Inspect(
        TrackedShipServer ship,
        StatefulPart part,
        FunctionCallState state,
        int count,
        out Plan plan,
        bool checkContacts = false
    )
    {
        plan = null;
        string problem = CallSizing.SizeProblem(ship, part, state, count);
        if (problem != null)
        {
            return problem;
        }

        var connections = ServiceLocator.GetService<ConnectionsServerTracker>();
        var cables = ServiceLocator.GetService<CablesServerTracker>();
        if (connections == null || connections.SignalConnectionGroups == null)
        {
            return "Call wiring is still loading. Try again.";
        }

        var topology = new SocketConnection[4];
        var removed = new List<CableData>(2);
        byte inputs = 0;
        bool wired = false;
        for (byte id = 0; id <= 3; id++)
        {
            var socket = new EntityPartSocket(ship.Id, part.Id, id);
            if (!connections.SignalConnectionGroups.TryGetSocketConnections(socket, out var edges))
            {
                if (connections.IsSocketPhysicallyOccupied(socket))
                {
                    return "Disconnect unsupported wiring before resizing.";
                }
                continue;
            }

            var wire = edges.FirstOrDefault();
            string label = id == 3 ? "Output" : "Input " + (id + 1);
            if (edges.Count != 1 || wire.Type != SocketConnectionType.Cable)
            {
                return label + " has a direct or unsupported connection. Disconnect it before resizing.";
            }
            if (
                cables == null
                || wire.CableId == 0
                || !wire.SocketA.HasValue
                || !wire.SocketB.HasValue
                || !cables.TryGetCableState(wire.CableId, out var cable)
                || cable.Type != CableType.Signal
            )
            {
                return label + " needs a fully plugged signal cable before resizing.";
            }

            var data = new CableData { Id = wire.CableId, State = cable };
            EntityPartSocket peer = wire.SocketA.Value == socket ? wire.SocketB.Value : wire.SocketA.Value;
            if (
                !data.ToSocketConnection().Equals(wire)
                || (wire.SocketA.Value != socket && wire.SocketB.Value != socket)
                || (id != 3 && id >= state.InputCount)
                || !HasLivePeer(connections, peer, wire)
            )
            {
                return label + " has stale or ambiguous wiring. Disconnect it before resizing.";
            }

            wired = true;
            topology[id] = wire;
            if (id != 3 && id >= count)
            {
                inputs |= (byte)(1 << id);
                if (!removed.Any(item => item.Id == wire.CableId))
                {
                    removed.Add(data);
                }
            }
        }

        if (
            (wired || checkContacts)
            && connections
                .FindTouchingSocketPairs(
                    ship.Id,
                    CallSizing.Variant(count),
                    part.Placement,
                    new PartContext(ship.Id, part.Id)
                )
                .Count != 0
        )
        {
            return "That size would make a new direct connection. Move the nearby part before resizing.";
        }

        plan = new Plan(topology, removed.ToArray(), inputs);
        return null;
    }

    private static bool HasLivePeer(
        ConnectionsServerTracker connections,
        EntityPartSocket socket,
        SocketConnection wire
    )
    {
        var ships = ServiceLocator.GetService<ShipsServerTracker>();
        return ships != null
            && ships.TryGetTrackedShip(socket.EntityId, out var owner)
            && owner.TryGetStatefulPart(socket.Socket.PartId, out var part)
            && part.Settings?.part != null
            && part.Settings.part.GetComponentsInChildren<SocketObject>()
                .Any(other => other.Type == CableType.Signal && other.Id == socket.Socket.SocketId)
            && connections.SignalConnectionGroups.TryGetSocketConnections(socket, out var edges)
            && edges.Count == 1
            && edges.Contains(wire);
    }
}
