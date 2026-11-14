using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Services;

namespace StellarFunctions;

internal sealed class NativeFunctionDefinition
{
    internal PartContext Address;
    internal uint Assembly;
    internal FunctionReturnState State;
    internal FunctionGraph Graph;
    internal string Error;
    internal string Structure;
    internal int ExpandedCost;
    internal readonly HashSet<PartContext> Claims = new HashSet<PartContext>();
    internal readonly Dictionary<Guid, EntityPartSocket> External = new Dictionary<Guid, EntityPartSocket>();
}

internal sealed class NativeFunctionCompiler
{
    private readonly ShipsServerTracker _ships;
    private readonly ConnectionsServerTracker _connections;
    private readonly Func<uint, uint> _root;
    private readonly NativeFunctionDefinition _definition;
    private readonly List<FunctionNode> _nodes = new List<FunctionNode>();
    private readonly Dictionary<PartContext, int> _compiled = new Dictionary<PartContext, int>();
    private readonly Dictionary<Guid, FunctionParameter> _params = new Dictionary<Guid, FunctionParameter>();
    private readonly JObject _identities;
    private readonly JObject _sources;
    private int _visited;

    internal NativeFunctionCompiler(
        ShipsServerTracker ships,
        ConnectionsServerTracker connections,
        Func<uint, uint> root,
        NativeFunctionDefinition definition
    )
    {
        _ships = ships;
        _connections = connections;
        _root = root;
        _definition = definition;
        _identities = ParseObject(definition.State.NodeIdentities, 65536);
        _sources = ParseObject(definition.State.Sources, 32768);
    }

    internal FunctionGraph Compile()
    {
        int result = Input(new EntityPartSocket(_definition.Address.ShipId, _definition.Address.PartId, 0));
        if (result < 0)
            throw new FunctionFault("UNWIRED RETURN");
        string structure = string.Join(
            ";",
            _nodes.Select(n =>
                n.Id.ToString("N")
                + ":"
                + n.Op
                + ":"
                + n.Operation
                + ":"
                + n.Reference
                + ":"
                + string.Join(",", Enumerable.Range(0, n.InputCount).Select(n.Input))
                + ":"
                + string.Join(",", Enumerable.Range(0, 3).Select(n.Binding))
            )
        );
        string metadata = _identities.ToString(Formatting.None);
        if (metadata.Length > 65536)
            throw new FunctionFault("IDENTITY LIMIT");
        _definition.State = _definition.State with { NodeIdentities = metadata };
        return new FunctionGraph(
            _definition.State.DefinitionId,
            _definition.State.Name,
            _params.Values.ToArray(),
            _nodes.ToArray(),
            result,
            Hash(structure)
        );
    }

    private int Input(EntityPartSocket socket)
    {
        var emitters = new HashSet<EntityPartSocket>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        Gather(socket, emitters, labels);
        var pending = new Queue<string>(labels);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var externals = new List<EntityPartSocket>();
        while (pending.Count > 0)
        {
            string label = pending.Dequeue();
            if (!seen.Add(label))
                continue;
            JToken declared = _sources["channels"]?[label];
            if (declared != null)
            {
                externals.Add(DeclaredSource(declared));
                continue;
            }
            foreach (TrackedShipServer ship in _ships.Ships.Where(s => _root(s.Id) == _definition.Assembly))
            foreach (StatefulPart part in ship.StatefulParts)
                if (
                    part.State is SignalTransmitterState transmitter
                    && string.Equals(transmitter.Channel, label, StringComparison.Ordinal)
                )
                {
                    var localLabels = new HashSet<string>(StringComparer.Ordinal);
                    var localEmitters = new HashSet<EntityPartSocket>();
                    var localClaims = new HashSet<PartContext>();
                    Gather(new EntityPartSocket(ship.Id, part.Id, 0), localEmitters, localLabels, localClaims);
                    // A same-name receiver elsewhere is not part of this body.
                    // Follow writer components (or explicit label aliases), not
                    // every physical receiver in the ship's global channel map.
                    if (
                        localEmitters.Count == 0
                        && !localLabels.Any(other => !string.Equals(other, label, StringComparison.Ordinal))
                    )
                        continue;
                    emitters.UnionWith(localEmitters);
                    _definition.Claims.UnionWith(localClaims);
                    foreach (string other in localLabels)
                        if (!seen.Contains(other))
                            pending.Enqueue(other);
                }
        }
        // Duplicate sockets are removed before judging competing producers.
        if (emitters.Count + externals.Distinct().Count() > 1)
            throw new FunctionFault("MULTIPLE SOURCES");
        if (externals.Count > 0)
            return External(externals[0]);
        return emitters.Count == 0 ? -1 : Node(emitters.Single());
    }

    private void Gather(
        EntityPartSocket start,
        HashSet<EntityPartSocket> emitters,
        HashSet<string> labels,
        HashSet<PartContext> labelClaims = null
    )
    {
        var pending = new Queue<EntityPartSocket>();
        var seen = new HashSet<EntityPartSocket>();
        pending.Enqueue(start);
        if (!_connections.TryGetSignalConnectionGroupWithSocket(start, out var group))
            return;
        SocketConnection[] edges = group
            .AllConnections.Where(e => e.Type != SocketConnectionType.RelayedByTransmitter)
            .ToArray();
        while (pending.Count > 0)
        {
            EntityPartSocket current = pending.Dequeue();
            if (!seen.Add(current))
                continue;
            if (++_visited > 4096)
                throw new FunctionFault("DISCOVERY LIMIT");
            if (_root(current.EntityId) != _definition.Assembly)
                throw new FunctionFault("OUTSIDE ASSEMBLY");
            if (
                !_ships.TryGetTrackedShip(current.EntityId, out TrackedShipServer ship)
                || !ship.TryGetStatefulPart(current.Socket.PartId, out StatefulPart part)
            )
                throw new FunctionFault("MISSING PART");
            if (part.State is SignalTransmitterState transmitter && transmitter.Channel != null)
            {
                labels.Add(transmitter.Channel);
                if (_sources["channels"]?[transmitter.Channel] == null)
                    (labelClaims ?? _definition.Claims).Add(current.PartContext);
            }
            byte id = current.Socket.SocketId;
            if (
                (part.State is ISignalEmitterState emitter && emitter.EmissionSockets.Contains(id))
                || (part.State is ISignalProcessorState processor && processor.EmissionSockets.Contains(id))
            )
                emitters.Add(current);
            foreach (SocketConnection edge in edges)
            {
                if (edge.SocketA.HasValue && edge.SocketA.Value.Equals(current) && edge.SocketB.HasValue)
                    pending.Enqueue(edge.SocketB.Value);
                if (edge.SocketB.HasValue && edge.SocketB.Value.Equals(current) && edge.SocketA.HasValue)
                    pending.Enqueue(edge.SocketA.Value);
            }
        }
    }

    private int Node(EntityPartSocket output)
    {
        PartContext context = output.PartContext;
        if (_compiled.TryGetValue(context, out int existing))
            return existing;
        TrackedShipServer ship = _ships.GetShip(context.ShipId);
        if (ship == null || !ship.TryGetStatefulPart(context.PartId, out StatefulPart part))
            throw new FunctionFault("MISSING PART");
        JToken sensor = _sources["parts"]?[Address(context)];
        if (sensor != null)
            return External(DeclaredSource(sensor));
        // IDs below our allocation are not evidence of stock semantics. Other
        // mods reuse native state records while evaluating different operations.
        // These exact stock asset identities were inspected in the pinned game.
        if (part.State is not FunctionPartState && !IsStockOperation(part))
            throw new FunctionFault("DECLARE EXTERNAL SOURCE");
        if (
            part.State is VelocitySensorState
            || part.State is AngularVelSensorState
            || part.State is GasSensorState
            || part.State is FluidTankState
        )
            throw new FunctionFault("DECLARE EXTERNAL SOURCE");
        if (_nodes.Count >= 256)
            throw new FunctionFault("BODY LIMIT");
        int index = _nodes.Count;
        _nodes.Add(null);
        _compiled.Add(context, index);
        _definition.Claims.Add(context);
        Guid nodeId = Identity(context, part.State);
        int Read(byte socket) => Input(new EntityPartSocket(context.ShipId, context.PartId, socket));
        FunctionNode node;
        if (part.State is FunctionParamState param)
        {
            var p = new FunctionParameter(param.ParamId, param.Name, param.Index, param.DefaultValue);
            _params[p.Id] = p;
            node = new FunctionNode(nodeId, FunctionOp.Parameter, reference: p.Id);
        }
        else if (part.State is ConstantState constant)
            node = new FunctionNode(nodeId, FunctionOp.Constant, initial: constant.ConstantValue);
        else if (part.State is MathGateState binary)
        {
            int operation = (byte)binary.Operation;
            if (operation >= 10 && operation <= 30 && MoreMathCompatibility.Ready)
                node = new FunctionNode(
                    nodeId,
                    FunctionOp.MoreMathBinary,
                    new[] { Read(0), Read(1) },
                    MoreMathOperations.BinaryName(operation),
                    initial: operation
                );
            else
            {
                string op = binary.Operation.ToString();
                FunctionEvaluator.Binary(op, 1, 1);
                node = new FunctionNode(nodeId, FunctionOp.Binary, new[] { Read(0), Read(1) }, op);
            }
        }
        else if (part.State is SignalProcessorState unary)
        {
            int operation = (byte)unary.Operation;
            if (operation >= 10 && operation <= 17 && MoreMathCompatibility.Ready)
                node = new FunctionNode(
                    nodeId,
                    FunctionOp.MoreMathUnary,
                    new[] { Read(0) },
                    MoreMathOperations.UnaryName(operation),
                    initial: operation
                );
            else
            {
                string op = unary.Operation.ToString();
                if (op != "Delta")
                    FunctionEvaluator.Unary(op, 1);
                node = new FunctionNode(nodeId, FunctionOp.Unary, new[] { Read(0) }, op);
            }
        }
        else if (part.State is MemoryRegisterState memory)
            node = new FunctionNode(nodeId, FunctionOp.Memory, new[] { Read(0), Read(1) }, initial: memory.Value);
        else if (part.State is DeltaState)
            node = new FunctionNode(nodeId, FunctionOp.RawDelta, new[] { Read(0) });
        else if (part.State is FunctionCallState call)
        {
            if (!Guid.TryParse(call.SelectedFunctionId, out Guid target))
                throw new FunctionFault("MISSING FUNCTION");
            var bindings = new[] { ParseId(call.Slot0), ParseId(call.Slot1), ParseId(call.Slot2) };
            byte wired = 0;
            int[] inputs = { -1, -1, -1 };
            for (byte i = 0; i < call.InputCount; i++)
                if (bindings[i] != Guid.Empty)
                {
                    inputs[i] = Read(i);
                    if (inputs[i] >= 0)
                        wired |= (byte)(1 << i);
                }
            node = new FunctionNode(
                nodeId,
                FunctionOp.Nested,
                inputs,
                reference: target,
                bindings: bindings,
                wiredMask: wired
            );
        }
        else
            throw new FunctionFault("UNSUPPORTED NODE");
        _nodes[index] = node;
        return index;
    }

    private EntityPartSocket DeclaredSource(JToken declaration)
    {
        uint ship = (uint?)declaration["ship"] ?? 0;
        ushort part = (ushort?)declaration["part"] ?? 0;
        byte socket = (byte?)declaration["socket"] ?? 0;
        if (
            _root(ship) != _definition.Assembly
            || !_ships.TryGetTrackedShip(ship, out TrackedShipServer target)
            || !target.TryGetStatefulPart(part, out StatefulPart source)
        )
            throw new FunctionFault("MISSING SOURCE");
        // Emitter reads may observe a real mod sensor using a stock Constant state;
        // this is not permission to clone an unknown mod's internal operations.
        bool supported =
            source.State is ConstantState
            || source.State is LeverState
            || source.State is MathGateState
            || source.State is SignalProcessorState
            || source.State is MemoryRegisterState
            || source.State is VelocitySensorState
            || source.State is AngularVelSensorState
            || source.State is GasSensorState
            || source.State is FluidTankState;
        byte[] sockets =
            source.State is ISignalEmitterState emitter ? emitter.EmissionSockets
            : source.State is ISignalProcessorState processor ? processor.EmissionSockets
            : Array.Empty<byte>();
        if (!supported || !sockets.Contains(socket))
            throw new FunctionFault("UNSUPPORTED SOURCE");
        return new EntityPartSocket(ship, part, socket);
    }

    private static bool IsStockOperation(StatefulPart part)
    {
        ushort id = part.Settings.id;
        string name = part.Settings.name;
        return id == 46 && name == "Constant" && part.State is ConstantState
            || id == 45 && name == "MathGate" && part.State is MathGateState
            || id == 48 && name == "MemoryRegister" && part.State is MemoryRegisterState
            || id == 54
                && name == "SignalProcessor"
                && (part.State is SignalProcessorState || part.State is DeltaState);
    }

    private int External(EntityPartSocket socket)
    {
        Guid id = StableHashId(
            _definition.State.DefinitionId.ToString("N")
                + ":"
                + socket.EntityId
                + ":"
                + socket.Socket.PartId
                + ":"
                + socket.Socket.SocketId
        );
        int found = _nodes.FindIndex(n => n != null && n.Op == FunctionOp.External && n.Reference == id);
        if (found >= 0)
            return found;
        if (_nodes.Count >= 256)
            throw new FunctionFault("BODY LIMIT");
        _definition.External[id] = socket;
        _nodes.Add(new FunctionNode(id, FunctionOp.External, reference: id));
        return _nodes.Count - 1;
    }

    private Guid Identity(PartContext address, IStatefulPartState state)
    {
        if (state is FunctionPartState custom)
            return custom.StableId;
        string key = Address(address);
        string value = (string)_identities[key];
        if (value != null)
        {
            if (!Guid.TryParse(value, out Guid known) || known == Guid.Empty)
                throw new FunctionFault("BAD NODE ID");
            return known;
        }
        Guid id = Guid.NewGuid();
        _identities[key] = id.ToString("N");
        return id;
    }

    internal static Guid ParseId(string id) => Guid.TryParse(id, out Guid value) ? value : Guid.Empty;

    internal static string Address(PartContext address) =>
        address.ShipId.ToString(CultureInfo.InvariantCulture)
        + ":"
        + address.PartId.ToString(CultureInfo.InvariantCulture);

    internal static JObject ParseObject(string json, int max)
    {
        if (json == null || json.Length > max)
            throw new FunctionFault("METADATA LIMIT");
        try
        {
            using var reader = new JsonTextReader(new System.IO.StringReader(json)) { MaxDepth = 16 };
            return JObject.Load(reader);
        }
        catch (JsonException)
        {
            throw new FunctionFault("BAD METADATA");
        }
    }

    internal static string Hash(string text)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    private static Guid StableHashId(string text)
    {
        using SHA256 sha = SHA256.Create();
        return new Guid(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(16).ToArray());
    }
}
