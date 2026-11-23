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
        public byte DirectInputs;
    }

    private sealed record Plan(SocketConnection[] Connections, CableData[] Removed, byte Inputs, byte DirectInputs);

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

    internal static void ClearOffer(uint playerId) => Offers.Remove(playerId);

    internal static bool IsValidConfirmation(Confirmation confirmation) =>
        confirmation == null
        || (
            Guid.TryParseExact(confirmation.Token, "N", out var token)
            && token != Guid.Empty
            && confirmation.InputCount is 1 or 2
            && confirmation.Inputs != 0
            && (confirmation.DirectInputs & ~confirmation.Inputs) == 0
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
        FunctionCallState next = Prepare(ship, part, state, count, playerId, session);
        if (next != state)
        {
            ship.UpdatePartState(part.Id, next);
            if (next.InputCount != state.InputCount)
            {
                CallSizing.InvalidateAfterResize();
            }
        }
    }

    internal static FunctionCallState Prepare(
        TrackedShipServer ship,
        StatefulPart part,
        FunctionCallState state,
        int count,
        uint playerId,
        string session
    )
    {
        Offers.Remove(playerId);
        if (count == state.InputCount)
        {
            return state with { ResizeFeedback = "" };
        }

        string problem = Inspect(ship, part, state, count, out var plan);
        if (problem != null)
        {
            return state with { ResizeFeedback = problem };
        }

        if (plan.Inputs == 0)
        {
            return CallSizing.AtSize(state, count);
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
                DirectInputs = plan.DirectInputs,
            },
            plan,
            now + ConfirmationSeconds
        );
        return state with { ResizeFeedback = "" };
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
        if (
            plan.Inputs != pending.Plan.Inputs
            || plan.DirectInputs != pending.Plan.DirectInputs
            || !plan.Connections.SequenceEqual(pending.Plan.Connections)
        )
        {
            CallSizing.Reply(ship, part, Stale);
            return true;
        }

        if (plan.Removed.Length == 0)
        {
            // The geometry refresh drops the confirmed direct contacts itself.
            Resize(ship, part, state, count);
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
            || !Matches(pending, ship.Id, part.Id, live)
            || Inspect(ship, part, live, count, out var remaining) != null
            || remaining.Inputs != plan.DirectInputs
            || remaining.DirectInputs != plan.DirectInputs
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
        out Plan plan
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
        byte directInputs = 0;
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

            string label = id == 3 ? "Output" : "Input " + (id + 1);
            if (edges.Count != 1)
            {
                return label + " has unsupported or ambiguous wiring. Disconnect it before resizing.";
            }
            var wire = edges.First();
            if (wire.Type == SocketConnectionType.Cable && (!wire.SocketA.HasValue || !wire.SocketB.HasValue))
            {
                return label + " needs a fully plugged signal cable before resizing.";
            }
            if (
                !wire.SocketA.HasValue
                || !wire.SocketB.HasValue
                || (wire.SocketA.Value != socket && wire.SocketB.Value != socket)
                || (id != 3 && id >= state.InputCount)
            )
            {
                return label + " has stale or ambiguous wiring. Disconnect it before resizing.";
            }
            EntityPartSocket peer = wire.SocketA.Value == socket ? wire.SocketB.Value : wire.SocketA.Value;
            if (
                peer == socket
                || (
                    wire.Type == SocketConnectionType.Touching
                    && peer.EntityId == ship.Id
                    && peer.Socket.PartId == part.Id
                )
                || !HasLivePeer(connections, peer, wire)
            )
            {
                return label + " has stale or ambiguous wiring. Disconnect it before resizing.";
            }

            if (wire.Type == SocketConnectionType.Cable)
            {
                if (
                    cables == null
                    || wire.CableId == 0
                    || !cables.TryGetCableState(wire.CableId, out var cable)
                    || cable.Type != CableType.Signal
                )
                {
                    return label + " needs a fully plugged signal cable before resizing.";
                }
                var data = new CableData { Id = wire.CableId, State = cable };
                if (!data.ToSocketConnection().Equals(wire))
                {
                    return label + " has stale or ambiguous wiring. Disconnect it before resizing.";
                }
                if (id != 3 && id >= count)
                {
                    inputs |= (byte)(1 << id);
                    if (!removed.Any(item => item.Id == wire.CableId))
                    {
                        removed.Add(data);
                    }
                }
            }
            else if (wire.Type == SocketConnectionType.Touching)
            {
                if (id != 3 && id >= count)
                {
                    inputs |= (byte)(1 << id);
                    directInputs |= (byte)(1 << id);
                }
            }
            else
            {
                return label + " has unsupported wiring. Disconnect it before resizing.";
            }
            topology[id] = wire;
        }

        var preview = connections.FindTouchingSocketPairs(
            ship.Id,
            CallSizing.Variant(count),
            part.Placement,
            new PartContext(ship.Id, part.Id)
        );
        var seen = new bool[4];
        foreach (var pair in preview)
        {
            byte id = pair.Current.Socket.Socket.SocketId;
            if (pair.Current.Type != CableType.Signal || id > 3 || seen[id])
            {
                return "That size would make a new direct connection. Move the nearby part before resizing.";
            }
            var edge = topology[id];
            var local = new EntityPartSocket(ship.Id, part.Id, id);
            if (
                edge.Type != SocketConnectionType.Touching
                || !edge.SocketA.HasValue
                || !edge.SocketB.HasValue
                || !(
                    (edge.SocketA.Value == local && edge.SocketB.Value == pair.Other.Socket)
                    || (edge.SocketB.Value == local && edge.SocketA.Value == pair.Other.Socket)
                )
            )
            {
                return "That size would make a new direct connection. Move the nearby part before resizing.";
            }
            seen[id] = true;
        }
        for (byte id = 0; id <= 3; id++)
        {
            if (
                topology[id].SocketA.HasValue
                && topology[id].Type == SocketConnectionType.Touching
                && (id == 3 || id < count)
                && !seen[id]
            )
            {
                return "A retained direct connection could not be verified. Check the wiring and try again.";
            }
        }

        plan = new Plan(topology, removed.ToArray(), inputs, directInputs);
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
            && edges.Contains(wire)
            && edges.Count(edge => edge.Type != SocketConnectionType.RelayedByPart) == 1;
    }
}
