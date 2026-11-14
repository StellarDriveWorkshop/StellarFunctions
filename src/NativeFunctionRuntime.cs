using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Services;
using HarmonyLib;
using Newtonsoft.Json;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.State;
using Ships.Services;

namespace StellarFunctions;

internal static class NativeFunctionRuntime
{
    private static ShipsServerTracker _ships;
    private static ConnectionsServerTracker _connections;
    private static Func<EntityPartSocket, double> _readEmitter;
    private static bool _dirty = true,
        _internalChange;
    private static readonly Dictionary<uint, ShipState> WatchedStates = new Dictionary<uint, ShipState>();
    private static readonly HashSet<uint> TransmitterAssemblies = new HashSet<uint>();
    private static readonly Dictionary<uint, uint> Parents = new Dictionary<uint, uint>();
    private static Dictionary<Guid, NativeFunctionDefinition> Definitions =
        new Dictionary<Guid, NativeFunctionDefinition>();
    private static readonly Dictionary<PartContext, HashSet<Guid>> Owners =
        new Dictionary<PartContext, HashSet<Guid>>();
    private static readonly HashSet<SocketConnection> RemovedRelays = new HashSet<SocketConnection>();
    private static readonly Dictionary<PartContext, string> PendingMemory = new Dictionary<PartContext, string>();
    private static readonly Dictionary<PartContext, string> CallErrors = new Dictionary<PartContext, string>();
    private static readonly Dictionary<Guid, EntityPartSocket> ExternalSources =
        new Dictionary<Guid, EntityPartSocket>();

    internal static bool IsOwned(PartContext part) => Owners.ContainsKey(part);

    internal static bool IsOurConnectionTracker(ConnectionsTracker tracker) => ReferenceEquals(tracker, _connections);

    internal static uint AssemblyOf(uint id)
    {
        uint current = id;
        int hops = 0;
        while (Parents.TryGetValue(current, out uint parent) && parent != current)
        {
            current = parent;
            if (++hops > 4096)
            {
                throw new FunctionFault("BAD ROTOR TREE");
            }
        }
        return current;
    }

    internal static void Dirty()
    {
        if (!_internalChange)
        {
            _dirty = true;
        }
    }

    internal static void Reset()
    {
        if (_ships != null)
        {
            _ships.OnShipTracked -= Tracked;
            _ships.OnShipShipStateReadUntracked -= Untracked;
        }
        foreach (var state in WatchedStates.Values)
        {
            Unwatch(state);
        }

        WatchedStates.Clear();
        if (_connections != null)
        {
            _connections.OnSignalGroupUpdated -= GroupChanged;
        }

        _ships = null;
        _connections = null;
        _readEmitter = null;
        Definitions.Clear();
        Owners.Clear();
        Parents.Clear();
        TransmitterAssemblies.Clear();
        RemovedRelays.Clear();
        PendingMemory.Clear();
        ExternalSources.Clear();
        CallErrors.Clear();
        FunctionCatalog.Clear();
        _dirty = true;
    }

    private static void Added(uint ship, StatefulPart part) => Dirty();

    private static void Removed(uint ship, StatefulPart part) => Dirty();

    private static void Tracked(TrackedShipServer ship)
    {
        // ShipsServerTracker copies delegate values into ShipState at load time.
        // A later tracker-field subscription misses already loaded ships. Attach
        // to the actual state and cover subsequent loads with OnShipTracked.
        if (ship == null || ship.State == null)
        {
            return;
        }

        if (WatchedStates.TryGetValue(ship.Id, out var previous))
        {
            if (ReferenceEquals(previous, ship.State))
            {
                return;
            }

            Unwatch(previous);
        }
        WatchedStates[ship.Id] = ship.State;
        ship.State.ShipStatefulPartAdded += Added;
        ship.State.ShipStatefulPartRemoved += Removed;
        ship.State.ShipStatefulPartUpdated += Updated;
        Dirty();
    }

    private static void Unwatch(ShipState state)
    {
        state.ShipStatefulPartAdded -= Added;
        state.ShipStatefulPartRemoved -= Removed;
        state.ShipStatefulPartUpdated -= Updated;
    }

    private static void Untracked(IShipStateRead ship)
    {
        if (WatchedStates.TryGetValue(ship.Id, out var state))
        {
            Unwatch(state);
            WatchedStates.Remove(ship.Id);
        }
        Dirty();
    }

    private static void GroupChanged(uint group) => Dirty();

    private static void Updated(uint ship, StatefulPartStateChangeEvent change) =>
        NoteStateEdit(ship, change.PartId, change.PreviousState, change.NewState);

    internal static void NoteStateEdit(uint ship, ushort part, IStatefulPartState previous, IStatefulPartState next)
    {
        // Value/history/UI updates are frequent. Compare authored Call fields
        // directly instead of building semantic strings on every native update.
        if (previous is FunctionCallState before && next is FunctionCallState call)
        {
            if (
                before.InstanceId != call.InstanceId
                || before.SelectedFunctionId != call.SelectedFunctionId
                || before.Slot0 != call.Slot0
                || before.Slot1 != call.Slot1
                || before.Slot2 != call.Slot2
                || before.InputCount != call.InputCount
            )
            {
                Dirty();
            }

            return;
        }
        bool relevant =
            next is FunctionPartState
            || next is SignalTransmitterState
            || next is RotorState
            || IsOwned(new PartContext(ship, part));
        if (relevant && !string.Equals(Semantics(previous), Semantics(next), StringComparison.Ordinal))
        {
            Dirty();
        }
    }

    private static string Semantics(IStatefulPartState state)
    {
        switch (state)
        {
            case FunctionReturnState r:
                return "return:"
                    + r.DefinitionId
                    + ":"
                    + r.Name
                    + ":"
                    + r.Sources
                    + ":"
                    + r.Broadcast
                    + ":"
                    + r.IsHeldByPlayer;
            case FunctionParamState p:
                return "param:"
                    + p.ParamId
                    + ":"
                    + p.Index
                    + ":"
                    + p.Name
                    + ":"
                    + p.DefaultValue?.ToString("R", CultureInfo.InvariantCulture);
            case FunctionCallState c:
                return "call:"
                    + c.InstanceId
                    + ":"
                    + c.SelectedFunctionId
                    + ":"
                    + c.Slot0
                    + ":"
                    + c.Slot1
                    + ":"
                    + c.Slot2
                    + ":"
                    + c.InputCount;
            case MathGateState m:
                return "binary:" + m.Operation;
            case SignalProcessorState u:
                return "unary:" + u.Operation;
            case ConstantState c:
                return "constant:" + c.ConstantValue.ToString("R", CultureInfo.InvariantCulture);
            case SignalTransmitterState t:
                return "label:" + t.Channel;
            case FunctionTransmitterState t:
                return "transmitter:" + t.TransmitterId + ":" + t.IsHeldByPlayer;
            case RotorState r:
                return "rotor:" + r.AnchoredShipId;
            default:
                return state?.GetType().FullName;
        }
    }

    internal static void Refresh()
    {
        // Initial nested bindings can change a graph's dependencies. Settle the
        // bounded nesting chain before the native scheduler consumes its cache.
        for (int pass = 0; pass < 9; pass++)
        {
            RefreshCore();
            if (!_dirty || _ships == null || _connections == null)
            {
                return;
            }
        }
        throw new FunctionFault("UNSTABLE SIGNATURES");
    }

    private static void RefreshCore()
    {
        var ships = ServiceLocator.GetService<ShipsServerTracker>();
        var connections = ServiceLocator.GetService<ConnectionsServerTracker>();
        if (ships == null || connections == null)
        {
            return;
        }

        if (!ReferenceEquals(_ships, ships) || !ReferenceEquals(_connections, connections))
        {
            Reset();
            _ships = ships;
            _connections = connections;
            var values = AccessTools
                .Field(typeof(ConnectionsTracker), "SignalConnectionGroupValues")
                .GetValue(connections);
            var read = AccessTools.Method(
                typeof(SignalConnectionGroupValues),
                "GetSocketSignal",
                new[] { typeof(EntityPartSocket) }
            );
            if (values == null || read?.ReturnType != typeof(double))
            {
                throw new MissingMethodException("Native emitter read contract");
            }

            _readEmitter =
                (Func<EntityPartSocket, double>)
                    Delegate.CreateDelegate(typeof(Func<EntityPartSocket, double>), values, read);
            ships.OnShipTracked += Tracked;
            ships.OnShipShipStateReadUntracked += Untracked;
            foreach (var ship in ships.Ships)
            {
                Tracked(ship);
            }

            connections.OnSignalGroupUpdated += GroupChanged;
        }
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _internalChange = true;
        try
        {
            Parents.Clear();
            TrackedShipServer[] all = ships.Ships.OrderBy(s => s.Id).ToArray();
            var present = new HashSet<uint>(all.Select(s => s.Id));
            foreach (TrackedShipServer ship in all)
            {
                foreach (StatefulPart part in ship.StatefulParts)
                {
                    if (
                        part.State is RotorState rotor
                        && rotor.AnchoredShipId != ship.Id
                        && present.Contains(rotor.AnchoredShipId)
                    )
                    {
                        uint a = AssemblyOf(ship.Id),
                            b = AssemblyOf(rotor.AnchoredShipId);
                        if (a != b)
                        {
                            Parents[Math.Max(a, b)] = Math.Min(a, b);
                        }
                    }
                }
            }

            TransmitterAssemblies.Clear();
            foreach (var ship in all)
            {
                if (ship.StatefulParts.Any(p => p.State is FunctionTransmitterState t && !t.IsHeldByPlayer))
                {
                    TransmitterAssemblies.Add(AssemblyOf(ship.Id));
                }
            }

            var returns = all.SelectMany(s =>
                    s.StatefulParts.Where(p => p.State is FunctionReturnState)
                        .Select(p => new NativeFunctionDefinition
                        {
                            Address = new PartContext(s.Id, p.Id),
                            Assembly = AssemblyOf(s.Id),
                            State = (FunctionReturnState)p.State,
                        })
                )
                .ToArray();
            var previous = Definitions;
            var next = new Dictionary<Guid, NativeFunctionDefinition>();
            var duplicateIds = new HashSet<Guid>(
                returns.GroupBy(d => d.State.DefinitionId).Where(g => g.Count() > 1).Select(g => g.Key)
            );
            var duplicateNames = new HashSet<string>(
                returns
                    .GroupBy(d => d.Assembly + ":" + d.State.Name, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key),
                StringComparer.Ordinal
            );
            foreach (NativeFunctionDefinition def in returns)
            {
                try
                {
                    if (duplicateIds.Contains(def.State.DefinitionId))
                    {
                        throw new FunctionFault("DUPLICATE ID");
                    }

                    if (duplicateNames.Contains(def.Assembly + ":" + def.State.Name))
                    {
                        throw new FunctionFault("DUPLICATE NAME");
                    }

                    def.Graph = new NativeFunctionCompiler(ships, connections, AssemblyOf, def).Compile();
                }
                catch (FunctionFault error)
                {
                    def.Error = error.Message;
                }
                catch (Exception)
                {
                    def.Error = "COMPILE ERROR";
                }
                if (def.Error != null && previous.TryGetValue(def.State.DefinitionId, out var old))
                {
                    def.Claims.UnionWith(old.Claims);
                }

                if (!next.ContainsKey(def.State.DefinitionId))
                {
                    next.Add(def.State.DefinitionId, def);
                }
            }
            Definitions = next;
            foreach (NativeFunctionDefinition def in returns)
            {
                if (def.Error == null)
                {
                    try
                    {
                        def.ExpandedCost = Expanded(def, new HashSet<Guid>(), 0);
                        def.Structure = ExpandedStructure(def, new HashSet<Guid>());
                    }
                    catch (FunctionFault error)
                    {
                        def.Error = error.Message;
                    }
                }

                if (def.Error != null && previous.TryGetValue(def.State.DefinitionId, out var old))
                {
                    def.Claims.UnionWith(old.Claims);
                }
            }
            Owners.Clear();
            ExternalSources.Clear();
            foreach (NativeFunctionDefinition def in returns)
            {
                foreach (PartContext node in def.Claims)
                {
                    if (!Owners.TryGetValue(node, out var owners))
                    {
                        Owners[node] = owners = new HashSet<Guid>();
                    }

                    owners.Add(def.State.DefinitionId);
                }
                foreach (var source in def.External)
                {
                    ExternalSources[source.Key] = source.Value;
                }

                FunctionReturnState state = def.State with { Status = def.Error ?? "TEMPLATE" };
                def.State = state;
                if (
                    ships.TryGetTrackedShip(def.Address.ShipId, out TrackedShipServer owner)
                    && owner.TryGetStatefulPart(def.Address.PartId, out var p)
                    && !Equals(p.State, state)
                )
                {
                    owner.UpdatePartState(p.Id, state);
                }
            }
            CallErrors.Clear();
            int reserved = 0;
            foreach (TrackedShipServer ship in all)
            {
                foreach (
                    StatefulPart part in ship.StatefulParts.Where(p => p.State is FunctionCallState).OrderBy(p => p.Id)
                )
                {
                    var address = new PartContext(ship.Id, part.Id);
                    var call = (FunctionCallState)part.State;
                    if (TryDefinition(call.SelectedFunctionId, ship.Id, out var def) && def.Error == null)
                    {
                        FunctionCallState bound = Bind(call, def.Graph, false);
                        if (!Equals(call, bound))
                        {
                            ship.UpdatePartState(part.Id, bound);
                            _dirty = true;
                        }
                        if (IsOwned(address))
                        {
                            continue;
                        }

                        int cost = def.ExpandedCost;
                        if (reserved + cost > 32768)
                        {
                            CallErrors[address] = "WORLD OPERATION LIMIT";
                            continue;
                        }
                        reserved += cost;
                        try
                        {
                            ValidateNativeDependencies(address);
                        }
                        catch (FunctionFault error)
                        {
                            CallErrors[address] = error.Message;
                        }
                    }
                }
            }

            ReconcileRelays();
            FunctionCatalog.Replace(returns.Where(d => d.Graph != null && d.Error == null));
            foreach (var ship in all)
            {
                foreach (var part in ship.StatefulParts.Where(p => p.State is FunctionTransmitterState).ToArray())
                {
                    var state = (FunctionTransmitterState)part.State;
                    var local = returns.Where(d => d.Assembly == AssemblyOf(ship.Id)).ToArray();
                    string summary =
                        local.Count(IsPublished)
                        + " broadcasting · "
                        + local.Count(d => d.Error == null && !IsPublished(d))
                        + " private"
                        + (
                            local.Any(d => d.Error != null)
                                ? " · " + local.Count(d => d.Error != null) + " invalid"
                                : ""
                        );
                    if (state.Summary != summary)
                    {
                        ship.UpdatePartState(part.Id, state with { Summary = summary });
                    }
                }
            }

            NativeTemplateHooks.InvalidateSchedule();
        }
        finally
        {
            _internalChange = false;
        }
    }

    private static int Expanded(NativeFunctionDefinition def, HashSet<Guid> stack, int depth)
    {
        if (def.Error != null || def.Graph == null)
        {
            throw new FunctionFault(def.Error ?? "MISSING FUNCTION");
        }

        if (depth >= 8)
        {
            throw new FunctionFault("NESTING LIMIT");
        }

        if (!stack.Add(def.Graph.Id))
        {
            throw new FunctionFault("RECURSION");
        }

        try
        {
            int count = def.Graph.Count;
            for (int i = 0; i < def.Graph.Count; i++)
            {
                if (def.Graph.Node(i).Op == FunctionOp.Nested)
                {
                    if (
                        !Definitions.TryGetValue(def.Graph.Node(i).Reference, out var target)
                        || (target.Assembly != def.Assembly && !IsPublished(target))
                    )
                    {
                        throw new FunctionFault("MISSING FUNCTION");
                    }

                    count += Expanded(target, stack, depth + 1);
                    if (count > 4096)
                    {
                        throw new FunctionFault("OPERATION LIMIT");
                    }
                }
            }

            return count;
        }
        finally
        {
            stack.Remove(def.Graph.Id);
        }
    }

    internal static bool HasTransmitter(uint ship) => TransmitterAssemblies.Contains(AssemblyOf(ship));

    internal static bool IsPublished(NativeFunctionDefinition definition) =>
        definition != null
        && definition.Graph != null
        && definition.Error == null
        && definition.State.Broadcast
        && !definition.State.IsHeldByPlayer
        && TransmitterAssemblies.Contains(definition.Assembly);

    internal static IEnumerable<NativeFunctionDefinition> LocalDefinitions(uint ship) =>
        Definitions.Values.Where(d => d.Assembly == AssemblyOf(ship)).ToArray();

    internal static string Origin(uint ship)
    {
        if (_ships != null && _ships.TryGetTrackedShip(ship, out var owner))
        {
            string name = owner
                .StatefulParts.Select(p => p.State)
                .OfType<BeaconState>()
                .Select(b => b.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (name != null)
            {
                return new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray())
                    + " · structure #"
                    + AssemblyOf(ship);
            }
        }
        return "Structure #" + AssemblyOf(ship);
    }

    internal static bool TryDefinition(string id, uint ship, out NativeFunctionDefinition definition)
    {
        definition = null;
        return Guid.TryParse(id, out Guid guid)
            && Definitions.TryGetValue(guid, out definition)
            && (definition.Assembly == AssemblyOf(ship) || IsPublished(definition));
    }

    internal static FunctionCallState Bind(FunctionCallState state, FunctionGraph graph, bool confirmed)
    {
        string[] slots = { state.Slot0, state.Slot1, state.Slot2 };
        if (confirmed || slots.All(string.IsNullOrEmpty))
        {
            slots = new string[3];
            for (int i = 0; i < graph.ParameterCount; i++)
            {
                slots[i] = graph.Parameter(i).Id.ToString("N");
            }
        }
        byte mask = 0;
        for (int i = 0; i < state.InputCount; i++)
        {
            if (graph.HasParameter(NativeFunctionCompiler.ParseId(slots[i])))
            {
                mask |= (byte)(1 << i);
            }
        }

        string Label(int slot)
        {
            Guid id = NativeFunctionCompiler.ParseId(slots[slot]);
            if (id == Guid.Empty)
            {
                return "Unused";
            }

            FunctionParameter p = graph.FindParameter(id);
            return p == null
                ? "Param removed: reset mapping"
                : "["
                    + p.Index
                    + "] "
                    + p.Name
                    + " · default "
                    + (p.Default ?? 0).ToString("G6", CultureInfo.InvariantCulture);
        }
        return state with
        {
            Slot0 = slots[0] ?? "",
            Slot1 = slots[1] ?? "",
            Slot2 = slots[2] ?? "",
            ListeningMask = mask,
            SelectedName = graph.Name,
            Input0Label = Label(0),
            Input1Label = Label(1),
            Input2Label = Label(2),
        };
    }

    internal static bool HasWiring(PartContext part) =>
        Enumerable.Range(0, 3).Any(i => Wired(new EntityPartSocket(part.ShipId, part.PartId, (byte)i)));

    internal static bool Wired(EntityPartSocket socket)
    {
        return _connections != null
            && _connections.TryGetSignalConnectionGroupWithSocket(socket, out var group)
            && group.AllConnections.Any(e =>
                e.Type != SocketConnectionType.RelayedByTransmitter
                && (
                    (e.SocketA.HasValue && e.SocketA.Value.Equals(socket))
                    || (e.SocketB.HasValue && e.SocketB.Value.Equals(socket))
                )
            );
    }

    internal static FunctionCallState Evaluate(
        PartContext address,
        FunctionCallState call,
        double[] inputs,
        double dt,
        long pass
    )
    {
        PendingMemory.Remove(address);
        if (CallErrors.TryGetValue(address, out string denied))
        {
            return call with { Output = 0, Status = denied, LastSignalPass = pass };
        }

        if (!TryDefinition(call.SelectedFunctionId, address.ShipId, out var def))
        {
            return call with { Output = 0, Status = "MISSING FUNCTION", LastSignalPass = pass };
        }

        if (def.Error != null)
        {
            return call with { Output = 0, Status = def.Error, LastSignalPass = pass };
        }

        try
        {
            call = Bind(call, def.Graph, false);
            string[] slots = { call.Slot0, call.Slot1, call.Slot2 };
            var args = new Dictionary<Guid, double>();
            for (int i = 0; i < call.InputCount; i++)
            {
                Guid id = NativeFunctionCompiler.ParseId(slots[i]);
                if (id == Guid.Empty)
                {
                    continue;
                }

                if (!def.Graph.HasParameter(id))
                {
                    throw new FunctionFault("PARAM REMOVED");
                }

                if (Wired(new EntityPartSocket(address.ShipId, address.PartId, (byte)i)))
                {
                    args.Add(id, inputs[i]);
                }
            }
            string structure = def.Structure;
            var old = string.Equals(call.StructureKey, structure, StringComparison.Ordinal)
                ? ReadMemory(call.RuntimeJson)
                : new Dictionary<string, double>();
            FunctionEvaluation result = FunctionEvaluator.Evaluate(
                def.Graph,
                (caller, id) =>
                {
                    if (!Definitions.TryGetValue(id, out var nested) || nested.Error != null)
                    {
                        return null;
                    }

                    uint callerAssembly =
                        caller != null && Definitions.TryGetValue(caller.Id, out var callerDef)
                            ? callerDef.Assembly
                            : def.Assembly;
                    return (nested.Assembly == callerAssembly || IsPublished(nested)) ? nested.Graph : null;
                },
                args,
                old,
                id => ReadExternal(id),
                dt
            );
            if (result.Error != null)
            {
                return call with { Output = 0, Status = result.Error, LastSignalPass = pass };
            }

            string next = JsonConvert.SerializeObject(result.ProposedState, Formatting.None);
            if (next.Length > 262144)
            {
                throw new FunctionFault("STATE SIZE LIMIT");
            }

            PendingMemory[address] = next;
            return call with
            {
                Output = result.Output,
                Status =
                    !string.IsNullOrEmpty(call.PendingFunctionId) ? "CONFIRM REBIND IN CONFIG"
                    : call.StructureKey.Length > 0 && call.StructureKey != structure ? "STATE RESET: BODY EDIT"
                    : "OK",
                StructureKey = structure,
                LastInput0 = inputs[0],
                LastInput1 = inputs[1],
                LastInput2 = inputs[2],
                LastSignalPass = pass,
            };
        }
        catch (FunctionFault error)
        {
            return call with { Output = 0, Status = error.Message, LastSignalPass = pass };
        }
        catch (Exception)
        {
            return call with { Output = 0, Status = "EVALUATION ERROR", LastSignalPass = pass };
        }
    }

    private static string ExpandedStructure(NativeFunctionDefinition def, HashSet<Guid> seen)
    {
        if (!seen.Add(def.Graph.Id))
        {
            throw new FunctionFault("RECURSION");
        }

        try
        {
            return NativeFunctionCompiler.Hash(
                def.Graph.Structure
                    + string.Join(
                        ";",
                        Enumerable
                            .Range(0, def.Graph.Count)
                            .Where(i => def.Graph.Node(i).Op == FunctionOp.Nested)
                            .Select(i =>
                            {
                                if (
                                    !Definitions.TryGetValue(def.Graph.Node(i).Reference, out var target)
                                    || (target.Assembly != def.Assembly && !IsPublished(target))
                                )
                                {
                                    throw new FunctionFault("MISSING FUNCTION");
                                }

                                return ExpandedStructure(target, seen);
                            })
                    )
            );
        }
        finally
        {
            seen.Remove(def.Graph.Id);
        }
    }

    private static Dictionary<string, double> ReadMemory(string json)
    {
        if (json == null || json.Length > 262144)
        {
            throw new FunctionFault("STATE SIZE LIMIT");
        }

        return JsonConvert.DeserializeObject<Dictionary<string, double>>(
                json,
                new JsonSerializerSettings { MaxDepth = 8 }
            ) ?? new Dictionary<string, double>();
    }

    private static double ReadExternal(Guid id)
    {
        if (!ExternalSources.TryGetValue(id, out var socket) || IsOwned(socket.PartContext))
        {
            throw new FunctionFault("MISSING SOURCE");
        }
        // A reference names one real emitter, not the aggregate of every output
        // sharing its cable group. Native scheduling refreshes it beforehand.
        return _readEmitter(socket);
    }

    private static IEnumerable<EntityPartSocket> SourcesFor(NativeFunctionDefinition definition)
    {
        var seen = new HashSet<Guid>();
        var sources = new HashSet<EntityPartSocket>();
        void Walk(NativeFunctionDefinition def)
        {
            if (def.Graph == null || !seen.Add(def.Graph.Id))
            {
                return;
            }

            sources.UnionWith(def.External.Values);
            for (int i = 0; i < def.Graph.Count; i++)
            {
                if (
                    def.Graph.Node(i).Op == FunctionOp.Nested
                    && Definitions.TryGetValue(def.Graph.Node(i).Reference, out var child)
                    && (child.Assembly == def.Assembly || IsPublished(child))
                )
                {
                    Walk(child);
                }
            }
        }
        Walk(definition);
        return sources;
    }

    internal static IEnumerable<PartContext> ExternalParts(PartContext call)
    {
        if (
            _ships == null
            || !_ships.TryGetTrackedShip(call.ShipId, out var ship)
            || !ship.TryGetStatefulPart(call.PartId, out var part)
            || part.State is not FunctionCallState state
            || !TryDefinition(state.SelectedFunctionId, call.ShipId, out var def)
            || def.Error != null
        )
        {
            return Array.Empty<PartContext>();
        }

        return SourcesFor(def).Select(s => s.PartContext).Distinct().ToArray();
    }

    private static void ValidateNativeDependencies(PartContext root)
    {
        var active = new HashSet<PartContext>();
        var finished = new HashSet<PartContext>();
        int visited = 0;
        void Visit(PartContext address, int depth = 0)
        {
            if (depth > 256)
            {
                throw new FunctionFault("DEPENDENCY DEPTH LIMIT");
            }

            if (finished.Contains(address) || IsOwned(address))
            {
                return;
            }

            if (!active.Add(address))
            {
                throw new FunctionFault("EXTERNAL DEPENDENCY CYCLE");
            }

            if (++visited > 4096)
            {
                throw new FunctionFault("DEPENDENCY LIMIT");
            }

            if (
                !_ships.TryGetTrackedShip(address.ShipId, out var ship)
                || !ship.TryGetStatefulPart(address.PartId, out var part)
            )
            {
                throw new FunctionFault("MISSING SOURCE");
            }
            // Native Memory reads the previous committed value; its write path
            // is the deliberate cycle boundary, matching the template executor.
            if (part.State is not MemoryRegisterState && part.State is ISignalProcessorState processor)
            {
                foreach (byte socket in processor.ListeningSockets)
                {
                    if (
                        _connections.TryGetSignalConnectionGroupWithSocket(
                            new EntityPartSocket(address.ShipId, address.PartId, socket),
                            out var group
                        )
                    )
                    {
                        _connections.GetSignalGroupInputsAndOutputs(group.Id, out var inputs, out _);
                        foreach (var source in inputs.Distinct())
                        {
                            Visit(source.PartContext, depth + 1);
                        }
                    }
                }

                if (part.State is FunctionCallState)
                {
                    foreach (var source in ExternalParts(address))
                    {
                        Visit(source, depth + 1);
                    }
                }
            }
            active.Remove(address);
            finished.Add(address);
        }
        Visit(root);
    }

    internal static IEnumerable<uint> ExternalGroups(PartContext call)
    {
        if (
            _ships == null
            || !_ships.TryGetTrackedShip(call.ShipId, out var ship)
            || !ship.TryGetStatefulPart(call.PartId, out var part)
            || part.State is not FunctionCallState state
            || !TryDefinition(state.SelectedFunctionId, call.ShipId, out var def)
            || def.Error != null
        )
        {
            return Array.Empty<uint>();
        }

        var visited = new HashSet<Guid>();
        var groups = new HashSet<uint>();
        void Walk(NativeFunctionDefinition d)
        {
            if (!visited.Add(d.Graph.Id))
            {
                return;
            }

            foreach (var source in d.External.Values)
            {
                if (_connections.TryGetSignalConnectionGroupWithSocket(source, out var group))
                {
                    groups.Add(group.Id);
                }
            }

            for (int i = 0; i < d.Graph.Count; i++)
            {
                if (
                    d.Graph.Node(i).Op == FunctionOp.Nested
                    && Definitions.TryGetValue(d.Graph.Node(i).Reference, out var child)
                    && child.Error == null
                    && (child.Assembly == d.Assembly || IsPublished(child))
                )
                {
                    Walk(child);
                }
            }
        }
        Walk(def);
        return groups.ToArray();
    }

    internal static void CommitMemory()
    {
        foreach (var pair in PendingMemory.ToArray())
        {
            if (
                _ships != null
                && _ships.TryGetTrackedShip(pair.Key.ShipId, out var ship)
                && ship.TryGetStatefulPart(pair.Key.PartId, out var part)
                && part.State is FunctionCallState call
                && call.RuntimeJson != pair.Value
            )
            {
                ship.UpdatePartState(part.Id, call with { RuntimeJson = pair.Value });
            }
        }

        PendingMemory.Clear();
    }

    internal static void NoteFilteredRelay(SocketConnection relay) => RemovedRelays.Add(relay);

    private static void ReconcileRelays()
    {
        var existing = new HashSet<SocketConnection>();
        foreach (var owned in Owners.Keys)
        {
            if (
                _connections.TryGetSignalConnectionGroupWithSocket(
                    new EntityPartSocket(owned.ShipId, owned.PartId, 0),
                    out var group
                )
            )
            {
                existing.UnionWith(
                    group.AllConnections.Where(c => c.Type == SocketConnectionType.RelayedByTransmitter)
                );
            }
        }

        foreach (var edge in existing)
        {
            if (
                (edge.SocketA.HasValue && IsOwned(edge.SocketA.Value.PartContext))
                || (edge.SocketB.HasValue && IsOwned(edge.SocketB.Value.PartContext))
            )
            {
                RemovedRelays.Add(edge);
                _connections.DeleteConnection(edge, CableType.Signal);
            }
        }

        foreach (var edge in RemovedRelays.ToArray())
        {
            if (!edge.SocketA.HasValue || !edge.SocketB.HasValue)
            {
                RemovedRelays.Remove(edge);
                continue;
            }
            var a = edge.SocketA.Value;
            var b = edge.SocketB.Value;
            if (IsOwned(a.PartContext) || IsOwned(b.PartContext))
            {
                continue;
            }

            if (
                _ships.TryGetTrackedShip(a.EntityId, out var sa)
                && sa.TryGetStatefulPart(a.Socket.PartId, out var pa)
                && pa.State is SignalTransmitterState ta
                && _ships.TryGetTrackedShip(b.EntityId, out var sb)
                && sb.TryGetStatefulPart(b.Socket.PartId, out var pb)
                && pb.State is SignalTransmitterState tb
                && ta.Channel != null
                && string.Equals(ta.Channel, tb.Channel, StringComparison.Ordinal)
                && AssemblyOf(a.EntityId) == AssemblyOf(b.EntityId)
            )
            {
                _connections.CreateConnection(edge, CableType.Signal);
            }

            RemovedRelays.Remove(edge);
        }
    }
}
