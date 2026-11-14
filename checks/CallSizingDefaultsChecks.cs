// Synthetic evaluator/compiler regression checks for hidden Call input defaults.
// Native service and connection caches are supplied by this fixture; this does
// not qualify world discovery, transport, normal scheduling, or dedicated use.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using StellarFunctions.Content;
using StellarFunctions.Game;
using UnityEngine;

namespace StellarFunctions;

internal static class CallSizingDefaultsChecks
{
    private const uint EvalShip = 492001;
    private const ushort EvalCallPart = 51;
    private const uint CompileShip = 492101;
    private const ushort CompileReturnPart = 61;
    private const ushort CompileCallPart = 62;
    private const ushort CompileConstantPart = 63;

    internal static void Run(Action<bool, string> check)
    {
        EvaluateHiddenInputs(check);
        CompileNestedHiddenInputs(check);
    }

    private static void EvaluateHiddenInputs(Action<bool, string> check)
    {
        Type runtime = AssemblyType("NativeFunctionRuntime");
        FieldInfo definitionsField = Field(runtime, "Definitions");
        FieldInfo parentsField = Field(runtime, "Parents");
        FieldInfo pendingField = Field(runtime, "PendingMemory");
        FieldInfo errorsField = Field(runtime, "CallErrors");
        FieldInfo connectionsField = Field(runtime, "_connections");
        object oldDefinitions = definitionsField.GetValue(null),
            oldParents = parentsField.GetValue(null);
        object oldPending = pendingField.GetValue(null),
            oldErrors = errorsField.GetValue(null);
        object oldConnections = connectionsField.GetValue(null);
        GameObject host = new GameObject("Private Call default evaluator connections");
        ConnectionsServerTracker connections = host.AddComponent<ConnectionsServerTracker>();
        var edges = new List<SocketConnection>();
        try
        {
            definitionsField.SetValue(null, Activator.CreateInstance(definitionsField.FieldType));
            parentsField.SetValue(null, Activator.CreateInstance(parentsField.FieldType));
            pendingField.SetValue(null, Activator.CreateInstance(pendingField.FieldType));
            errorsField.SetValue(null, Activator.CreateInstance(errorsField.FieldType));
            connectionsField.SetValue(null, connections);

            Guid definitionId = Guid.NewGuid();
            Guid[] parameters = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            object graph = SumGraph(definitionId, parameters, new double?[] { 10, 20, 30 });
            object definition = Definition(definitionId, EvalShip, graph, "defaults-structure");
            ((IDictionary)definitionsField.GetValue(null)).Add(definitionId, definition);

            var call = new FunctionCallState
            {
                InstanceId = Guid.NewGuid(),
                SelectedFunctionId = definitionId.ToString("N"),
                Slot0 = parameters[0].ToString("N"),
                Slot1 = parameters[1].ToString("N"),
                Slot2 = parameters[2].ToString("N"),
                InputCount = 1,
                StructureKey = "defaults-structure",
                RuntimeJson = "{\"owned-history\":4}",
            };
            var evaluate = runtime.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic);
            PartContext address = new PartContext(EvalShip, EvalCallPart);

            // Old native input groups deliberately remain on hidden sockets 1 and 2.
            AddWire(connections, edges, EvalShip, EvalCallPart, 1, 7001);
            AddWire(connections, edges, EvalShip, EvalCallPart, 2, 7002);
            FunctionCallState oneUnwired = Evaluate(evaluate, address, call, new[] { 0d, 999d, 777d }, 1);
            Near(
                check,
                oneUnwired.Output,
                60,
                "one-input Call evaluates hidden unwired parameters from defaults 20 and 30 despite stale native wires"
            );
            check(oneUnwired.Status == "OK", "one-input hidden-default evaluation completes successfully");

            AddWire(connections, edges, EvalShip, EvalCallPart, 0, 7000);
            FunctionCallState oneZero = Evaluate(evaluate, address, call, new[] { 0d, 999d, 777d }, 2);
            Near(
                check,
                oneZero.Output,
                50,
                "visible wired zero stays zero while hidden one-input parameters use defaults"
            );

            call = call with { InputCount = 2 };
            FunctionCallState twoInputs = Evaluate(evaluate, address, call, new[] { 0d, 2d, 777d }, 3);
            Near(
                check,
                twoInputs.Output,
                32,
                "two-input Call uses wired zero and two plus the hidden third parameter default"
            );

            call = call with { InputCount = 3 };
            FunctionCallState threeInputs = Evaluate(evaluate, address, call, new[] { 0d, 2d, 3d }, 4);
            Near(check, threeInputs.Output, 5, "three-input Call uses all three wired values including wired zero");

            foreach (SocketConnection edge in edges)
            {
                connections.SignalConnectionGroups.OnConnectionDelete(edge);
            }

            edges.Clear();
            Guid noDefaultId = Guid.NewGuid(),
                noDefaultParameter = Guid.NewGuid();
            object noDefaultGraph = SingleParameterGraph(noDefaultId, noDefaultParameter, null);
            ((IDictionary)definitionsField.GetValue(null)).Add(
                noDefaultId,
                Definition(noDefaultId, EvalShip, noDefaultGraph, "no-default-structure")
            );
            FunctionCallState noDefault = Evaluate(
                evaluate,
                address,
                new FunctionCallState
                {
                    SelectedFunctionId = noDefaultId.ToString("N"),
                    Slot0 = noDefaultParameter.ToString("N"),
                    InputCount = 1,
                    StructureKey = "no-default-structure",
                },
                new[] { 0d, 0d, 0d },
                5
            );
            Near(check, noDefault.Output, 0, "unwired visible parameter without a default falls back to zero");

            check(
                oneUnwired.InstanceId == call.InstanceId
                    && oneUnwired.SelectedFunctionId == call.SelectedFunctionId
                    && oneUnwired.Slot0 == call.Slot0
                    && oneUnwired.Slot1 == call.Slot1
                    && oneUnwired.Slot2 == call.Slot2
                    && oneUnwired.RuntimeJson == call.RuntimeJson,
                "evaluation preserves Call selection, binding identities, and private RuntimeJson ownership"
            );
        }
        finally
        {
            foreach (SocketConnection edge in edges)
            {
                connections.SignalConnectionGroups.OnConnectionDelete(edge);
            }

            definitionsField.SetValue(null, oldDefinitions);
            parentsField.SetValue(null, oldParents);
            pendingField.SetValue(null, oldPending);
            errorsField.SetValue(null, oldErrors);
            connectionsField.SetValue(null, oldConnections);
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static void CompileNestedHiddenInputs(Action<bool, string> check)
    {
        var host = new GameObject("Private Call default compiler native services");
        host.tag = "ServiceObject";
        var ships = host.AddComponent<ShipsServerTracker>();
        var connections = host.AddComponent<ConnectionsServerTracker>();
        var shipState = new ShipState(CompileShip, CompileShip, new PartSettingsProvider());
        var ship = (TrackedShipServer)FormatterServices.GetUninitializedObject(typeof(TrackedShipServer));
        typeof(TrackedShip)
            .GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(ship, shipState);
        ((IDictionary)Field(typeof(ShipsServerTracker), "_shipsDict").GetValue(ships)).Add(CompileShip, ship);
        var edges = new List<SocketConnection>();
        try
        {
            Guid definitionId = Guid.NewGuid();
            Guid[] bindings = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var callState = new FunctionCallState
            {
                InstanceId = Guid.NewGuid(),
                SelectedFunctionId = definitionId.ToString("N"),
                Slot0 = bindings[0].ToString("N"),
                Slot1 = bindings[1].ToString("N"),
                Slot2 = bindings[2].ToString("N"),
                InputCount = 1,
            };
            var returnState = new FunctionReturnState
            {
                DefinitionId = Guid.NewGuid(),
                Name = "Nested caller",
                Sources = "{}",
                NodeIdentities = "{}",
            };
            PartSettings callSettings = Support.Registration.Get(62005);
            PartSettings returnSettings = Support.Registration.Get(62007);
            PartSettings constantSettings = Resources
                .FindObjectsOfTypeAll<PartSettings>()
                .Single(s => s.id == 46 && s.name == "Constant");
            AddPart(shipState, CompileReturnPart, returnSettings, returnState);
            AddPart(shipState, CompileCallPart, callSettings, callState);
            AddPart(
                shipState,
                CompileConstantPart,
                constantSettings,
                PartStateFactory.BuildDefaultState(constantSettings)
            );
            AddWire(connections, edges, CompileShip, CompileReturnPart, 0, CompileCallPart, 3);
            AddWire(connections, edges, CompileShip, CompileCallPart, 2, CompileConstantPart, 0);

            Type definitionType = AssemblyType("NativeFunctionDefinition");
            object definition = Activator.CreateInstance(definitionType, true);
            Set(definitionType, definition, "Address", new PartContext(CompileShip, CompileReturnPart));
            Set(definitionType, definition, "Assembly", CompileShip);
            Set(definitionType, definition, "State", returnState);
            Type compilerType = AssemblyType("NativeFunctionCompiler");
            object compiler = Activator.CreateInstance(
                compilerType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new object[] { ships, connections, new Func<uint, uint>(id => id), definition },
                null
            );
            object graph = compilerType
                .GetMethod("Compile", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(compiler, null);
            Type graphType = graph.GetType();
            int nodeCount = (int)
                graphType.GetProperty("Count", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(graph, null);
            int result = (int)
                graphType.GetField("Result", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(graph);
            object nested = graphType
                .GetMethod("Node", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(graph, new object[] { result });
            Type nodeType = nested.GetType();
            check(
                nodeType.GetField("Op", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(nested).ToString()
                    == "Nested",
                "actual NativeFunctionCompiler emits a nested Call node from Return-to-Call native wiring"
            );
            check(nodeCount == 1, "actual nested compiler omits the disconnected stock Constant 46 on a hidden input");
            MethodInfo input = nodeType.GetMethod("Input", BindingFlags.Instance | BindingFlags.NonPublic);
            check(
                (int)input.Invoke(nested, new object[] { 0 }) == -1
                    && (int)input.Invoke(nested, new object[] { 1 }) == -1
                    && (int)input.Invoke(nested, new object[] { 2 }) == -1,
                "compiled one-input Call does not traverse the stale hidden input-2 wire to stock Constant 46"
            );
            byte wiredMask = (byte)
                nodeType.GetField("WiredMask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(nested);
            check((wiredMask & 6) == 0, "compiled nested wired mask excludes hidden socket bits 2 and 4");
            MethodInfo binding = nodeType.GetMethod("Binding", BindingFlags.Instance | BindingFlags.NonPublic);
            check(
                (Guid)binding.Invoke(nested, new object[] { 0 }) == Guid.Parse(callState.Slot0)
                    && (Guid)binding.Invoke(nested, new object[] { 1 }) == Guid.Parse(callState.Slot1)
                    && (Guid)binding.Invoke(nested, new object[] { 2 }) == Guid.Parse(callState.Slot2),
                "compiled nested Call preserves all three binding identities, including hidden slots"
            );
        }
        finally
        {
            foreach (SocketConnection edge in edges)
            {
                connections.SignalConnectionGroups.OnConnectionDelete(edge);
            }

            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static object SumGraph(Guid id, Guid[] parameters, double?[] defaults)
    {
        object[] args = new object[3];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = Parameter(parameters[i], "p" + (i + 1), i + 1, defaults[i]);
        }

        Array nodes = Array.CreateInstance(AssemblyType("FunctionNode"), 5);
        nodes.SetValue(Node("Parameter", reference: parameters[0]), 0);
        nodes.SetValue(Node("Parameter", reference: parameters[1]), 1);
        nodes.SetValue(Node("Parameter", reference: parameters[2]), 2);
        nodes.SetValue(Node("Binary", new[] { 0, 1 }, "Add"), 3);
        nodes.SetValue(Node("Binary", new[] { 3, 2 }, "Add"), 4);
        return Graph(id, "Default sum", args, nodes, 4, "defaults-sum");
    }

    private static object SingleParameterGraph(Guid id, Guid parameter, double? fallback)
    {
        Array parameters = Array.CreateInstance(AssemblyType("FunctionParameter"), 1);
        parameters.SetValue(Parameter(parameter, "missing", 1, fallback), 0);
        Array nodes = Array.CreateInstance(AssemblyType("FunctionNode"), 1);
        nodes.SetValue(Node("Parameter", reference: parameter), 0);
        return Graph(id, "No default", parameters, nodes, 0, "no-default");
    }

    private static object Parameter(Guid id, string name, int index, double? fallback)
    {
        Type type = AssemblyType("FunctionParameter");
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { id, name, index, fallback },
            null
        );
    }

    private static object Graph(Guid id, string name, object[] parameters, Array nodes, int result, string structure)
    {
        Array parameterArray = Array.CreateInstance(AssemblyType("FunctionParameter"), parameters.Length);
        for (int i = 0; i < parameters.Length; i++)
        {
            parameterArray.SetValue(parameters[i], i);
        }

        return Graph(id, name, parameterArray, nodes, result, structure);
    }

    private static object Graph(Guid id, string name, Array parameters, Array nodes, int result, string structure)
    {
        Type type = AssemblyType("FunctionGraph");
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { id, name, parameters, nodes, result, structure },
            null
        );
    }

    private static object Node(string op, int[] inputs = null, string operation = null, Guid reference = default)
    {
        Type type = AssemblyType("FunctionNode");
        object functionOp = Enum.Parse(AssemblyType("FunctionOp"), op);
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { Guid.NewGuid(), functionOp, inputs, operation, 0d, reference, Array.Empty<Guid>(), (byte)0 },
            null
        );
    }

    private static object Definition(Guid id, uint ship, object graph, string structure)
    {
        Type type = AssemblyType("NativeFunctionDefinition");
        object value = Activator.CreateInstance(type, true);
        Set(type, value, "Address", new PartContext(ship, 1));
        Set(type, value, "Assembly", ship);
        Set(
            type,
            value,
            "State",
            new FunctionReturnState
            {
                DefinitionId = id,
                Name = "Synthetic",
                Sources = "{}",
                NodeIdentities = "{}",
            }
        );
        Set(type, value, "Graph", graph);
        Set(type, value, "Structure", structure);
        Set(type, value, "ExpandedCost", 5);
        return value;
    }

    private static FunctionCallState Evaluate(
        MethodInfo evaluate,
        PartContext address,
        FunctionCallState call,
        double[] inputs,
        long pass
    ) => (FunctionCallState)evaluate.Invoke(null, new object[] { address, call, inputs, .02d, pass });

    private static void AddPart(ShipState state, ushort id, PartSettings settings, IStatefulPartState partState)
    {
        var part = new StatefulPart(
            id,
            settings,
            new FloatingPartPlacement(new Vector3(id, 0, 0), PartOrientation.Forward, PartOrientation.Up)
        )
        {
            State = partState,
        };
        state.AddStatefulPartWithoutInit(part);
    }

    private static void AddWire(
        ConnectionsServerTracker tracker,
        List<SocketConnection> edges,
        uint ship,
        ushort part,
        byte socket,
        ushort otherPart,
        byte otherSocket = 0
    )
    {
        var edge = new SocketConnection(
            new EntityPartSocket(ship, part, socket),
            new EntityPartSocket(ship, otherPart, otherSocket),
            SocketConnectionType.Cable
        );
        tracker.SignalConnectionGroups.OnConnectionCreate(edge);
        edges.Add(edge);
    }

    private static Type AssemblyType(string shortName) =>
        typeof(FunctionCallState).Assembly.GetType("StellarFunctions." + shortName, true);

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static void Set(Type type, object instance, string name, object value) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);

    private static void Near(Action<bool, string> check, double actual, double expected, string message) =>
        check(Math.Abs(actual - expected) < 1e-9, message + $" (expected {expected}, got {actual})");
}
