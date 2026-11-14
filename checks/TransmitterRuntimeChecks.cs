// Focused production-runtime policy/evaluation checks. These seed the native
// runtime's private registries because the asset-only harness has no gameplay
// world or server services; they do not claim compilation or signal scheduling.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using StellarFunctions.Content;
using UnityEngine;

namespace StellarFunctions;

public static class TransmitterRuntimeChecks
{
    public static void Run(Action<bool, string> check)
    {
        Type runtime = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.NativeFunctionRuntime", true);
        Type definitionType = runtime.Assembly.GetType("StellarFunctions.NativeFunctionDefinition", true);
        FieldInfo definitionsField = Field(runtime, "Definitions");
        FieldInfo parentsField = Field(runtime, "Parents");
        FieldInfo transmittersField = Field(runtime, "TransmitterAssemblies");
        FieldInfo pendingField = Field(runtime, "PendingMemory");
        Type catalogType = runtime.Assembly.GetType("StellarFunctions.FunctionCatalog", true);
        IDictionary catalogEntries = (IDictionary)
            catalogType.GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var oldCatalogEntries = Snapshot(catalogEntries);
        object oldDefinitions = definitionsField.GetValue(null);
        object oldParents = parentsField.GetValue(null);
        object oldTransmitters = transmittersField.GetValue(null);
        object oldPending = pendingField.GetValue(null);
        MethodInfo tryDefinition = runtime.GetMethod("TryDefinition", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo evaluate = runtime.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic);
        Type definitionsType = definitionsField.FieldType;
        Type parentsType = parentsField.FieldType;
        Type transmitterType = transmittersField.FieldType;
        Type pendingType = pendingField.FieldType;
        object definitions = Activator.CreateInstance(definitionsType);
        object parents = Activator.CreateInstance(parentsType);
        object transmitters = Activator.CreateInstance(transmitterType);
        object pending = Activator.CreateInstance(pendingType);
        definitionsField.SetValue(null, definitions);
        parentsField.SetValue(null, parents);
        transmittersField.SetValue(null, transmitters);
        pendingField.SetValue(null, pending);

        try
        {
            uint assembly = 4100,
                remote = 4200,
                rotorChild = 4201;
            Guid id = Guid.NewGuid();
            object graph = MemoryGraph(id);
            object definition = Definition(
                definitionType,
                id,
                assembly,
                graph,
                ReturnState(id, "Public limiter", true)
            );
            ((IDictionary)definitions).Add(id, definition);
            MethodInfo isPublished = runtime.GetMethod("IsPublished", BindingFlags.Static | BindingFlags.NonPublic);

            CheckLookup(
                check,
                tryDefinition,
                id,
                assembly,
                true,
                "definition remains locally callable without a transmitter"
            );
            CheckLookup(
                check,
                tryDefinition,
                id,
                remote,
                false,
                "broadcast definition is private until its assembly has a transmitter"
            );
            CheckValue(
                check,
                !(bool)isPublished.Invoke(null, new[] { definition }),
                "Return Broadcast alone does not publish without a transmitter"
            );
            Add(transmitters, assembly);
            CheckLookup(
                check,
                tryDefinition,
                id,
                remote,
                true,
                "broadcast definition becomes callable remotely while its transmitter is present"
            );
            CheckValue(
                check,
                (bool)isPublished.Invoke(null, new[] { definition }),
                "valid broadcast graph is published while its transmitter is present"
            );
            definitionType
                .GetField("Error", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, "BAD TEMPLATE");
            CheckValue(
                check,
                !(bool)isPublished.Invoke(null, new[] { definition }),
                "invalid function graph is never published"
            );
            definitionType.GetField("Error", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(definition, null);

            // A private definition becomes visible only while the rotor tree
            // resolves the child to the owning assembly.
            Guid privateId = Guid.NewGuid();
            object privateGraph = MemoryGraph(privateId);
            object privateDefinition = Definition(
                definitionType,
                privateId,
                assembly,
                privateGraph,
                new FunctionReturnState { DefinitionId = privateId, Name = "Private local" }
            );
            ((IDictionary)definitions).Add(privateId, privateDefinition);
            Put(parents, rotorChild, assembly);
            CheckLookup(
                check,
                tryDefinition,
                privateId,
                rotorChild,
                true,
                "rotor child shares its connected assembly's private definition"
            );
            ((IDictionary)parents).Remove(rotorChild);
            CheckLookup(
                check,
                tryDefinition,
                privateId,
                rotorChild,
                false,
                "detaching rotor child closes private definition scope"
            );
            CheckLookup(
                check,
                tryDefinition,
                id,
                rotorChild,
                true,
                "detached child still reaches a separately published definition"
            );

            var shared = new FunctionCallState
            {
                SelectedFunctionId = id.ToString("N"),
                StructureKey = "broadcast-memory-structure",
                RuntimeJson = State(graph, 10),
            };
            var other = new FunctionCallState
            {
                SelectedFunctionId = id.ToString("N"),
                StructureKey = "broadcast-memory-structure",
                RuntimeJson = State(graph, 100),
            };
            PartContext firstAddress = new PartContext(remote, 10),
                secondAddress = new PartContext(rotorChild, 11);
            FunctionCallState first = Call(evaluate, firstAddress, shared, 1);
            FunctionCallState second = Call(evaluate, secondAddress, other, 1);
            check(first.Status == "OK", "remote Call A evaluates successfully");
            check(second.Status == "OK", "remote Call B evaluates successfully");
            Near(check, first.Output, 10, "remote Call A reads its own broadcast Memory history");
            Near(check, second.Output, 100, "remote Call B reads a separate broadcast Memory history");
            string firstPending = (string)((IDictionary)pending)[firstAddress];
            string secondPending = (string)((IDictionary)pending)[secondAddress];
            Near(check, MemoryValue(firstPending, graph), 11, "remote Call A proposes its own next Memory value");
            Near(check, MemoryValue(secondPending, graph), 101, "remote Call B proposes its own next Memory value");
            check(firstPending != secondPending, "two remote Calls retain distinct private Memory snapshots");

            FunctionCallState persisted = first with { RuntimeJson = firstPending };
            Remove(transmitters, assembly);
            FunctionCallState withdrawn = Call(evaluate, firstAddress, persisted, 2);
            check(
                withdrawn.Status == "MISSING FUNCTION" && withdrawn.Output == 0,
                "transmitter loss makes the remote Call fail closed"
            );
            check(
                withdrawn.RuntimeJson == persisted.RuntimeJson && withdrawn.StructureKey == persisted.StructureKey,
                "transmitter loss retains the remote Call's saved Memory and structure identity"
            );
            Add(transmitters, assembly);
            definitionType
                .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, ReturnState(id, "Public limiter", false));
            FunctionCallState hidden = Call(evaluate, firstAddress, persisted, 3);
            check(
                hidden.Status == "MISSING FUNCTION"
                    && hidden.RuntimeJson == persisted.RuntimeJson
                    && hidden.StructureKey == persisted.StructureKey,
                "clearing Return Broadcast hides the Call while preserving its private state"
            );
            definitionType
                .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, ReturnState(id, "Public limiter", true));
            FunctionCallState resumed = Call(evaluate, firstAddress, persisted, 4);
            check(resumed.Status == "OK", "restoring publication resumes the remote Call");
            Near(check, resumed.Output, 11, "resumed remote Call continues from its retained Memory history");

            // Withdrawing either required publication condition immediately
            // removes distant lookup and catalog visibility.
            Remove(transmitters, assembly);
            CheckLookup(
                check,
                tryDefinition,
                id,
                remote,
                false,
                "removing the transmitter closes remote definition lookup"
            );
            CheckValue(
                check,
                !(bool)isPublished.Invoke(null, new[] { definition }),
                "removing the transmitter withdraws publication"
            );
            Add(transmitters, assembly);
            definitionType
                .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, ReturnState(id, "Public limiter", false));
            CheckLookup(
                check,
                tryDefinition,
                id,
                remote,
                false,
                "clearing Return Broadcast closes remote definition lookup"
            );
            definitionType
                .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, ReturnState(id, "Public limiter", true));

            Type catalog = runtime.Assembly.GetType("StellarFunctions.FunctionCatalog", true);
            MethodInfo replace = catalog.GetMethod("Replace", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo visible = catalog.GetMethod(
                "Visible",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(uint) },
                null
            );
            object array = Array.CreateInstance(definitionType, 3);
            ((Array)array).SetValue(definition, 0);
            ((Array)array).SetValue(definition, 1);
            ((Array)array).SetValue(privateDefinition, 2);
            replace.Invoke(null, new[] { array });
            var remoteEntries = ((IEnumerable)visible.Invoke(null, new object[] { remote })).Cast<object>().ToArray();
            check(remoteEntries.Length == 1, "catalog deduplicates one published identity for a distant structure");
            object entry = remoteEntries[0];
            check(
                (bool)
                    entry
                        .GetType()
                        .GetField("Broadcast", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(entry),
                "distant catalog marks the visible entry as broadcast"
            );
            Remove(transmitters, assembly);
            replace.Invoke(null, new[] { array });
            check(
                !((IEnumerable)visible.Invoke(null, new object[] { remote })).Cast<object>().Any(),
                "transmitter removal withdraws the definition from a rebuilt distant catalog"
            );
            Add(transmitters, assembly);
            definitionType
                .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, ReturnState(id, "Public limiter", false));
            replace.Invoke(null, new[] { array });
            check(
                !((IEnumerable)visible.Invoke(null, new object[] { remote })).Cast<object>().Any(),
                "private definitions stay out of a distant catalog"
            );
            definitionType
                .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(definition, ReturnState(id, "Public limiter", true));
            replace.Invoke(null, new[] { array });
            DirectorySnapshot(check, runtime.Assembly, remote, id, privateId);
            BroadcastHandleChecks(check, runtime.Assembly, definitionType, definition, id, assembly, transmitters);
            BroadcastNestedRuntimeChecks(
                check,
                runtime.Assembly,
                runtime,
                definitionType,
                definitions,
                transmitters,
                assembly,
                remote
            );
        }
        finally
        {
            catalogEntries.Clear();
            foreach (DictionaryEntry entry in oldCatalogEntries)
                catalogEntries.Add(entry.Key, entry.Value);
            definitionsField.SetValue(null, oldDefinitions);
            parentsField.SetValue(null, oldParents);
            transmittersField.SetValue(null, oldTransmitters);
            pendingField.SetValue(null, oldPending);
        }
    }

    private static object Definition(Type type, Guid id, uint assembly, object graph, FunctionReturnState state)
    {
        object value = Activator.CreateInstance(type, true);
        type.GetField("Address", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(value, new PartContext(assembly, 1));
        type.GetField("Assembly", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, assembly);
        type.GetField("State", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, state);
        type.GetField("Graph", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, graph);
        type.GetField("Structure", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(value, "broadcast-memory-structure");
        type.GetField("ExpandedCost", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, 5);
        return value;
    }

    private static FunctionReturnState ReturnState(Guid id, string name, bool broadcast)
    {
        var state = new FunctionReturnState { DefinitionId = id, Name = name };
        PropertyInfo property = typeof(FunctionReturnState).GetProperty(
            "Broadcast",
            BindingFlags.Instance | BindingFlags.Public
        );
        if (property == null)
            throw new MissingMemberException(typeof(FunctionReturnState).FullName, "Broadcast");
        property.SetValue(state, broadcast, null);
        return state;
    }

    private static object MemoryGraph(Guid id)
    {
        Assembly assembly = typeof(FunctionReturnState).Assembly;
        Type graphType = assembly.GetType("StellarFunctions.FunctionGraph", true);
        Type nodeType = assembly.GetType("StellarFunctions.FunctionNode", true);
        Array nodes = Array.CreateInstance(nodeType, 4);
        nodes.SetValue(Node(FunctionOp("Memory"), new[] { 1, 2 }, initial: 0), 0);
        nodes.SetValue(Node(FunctionOp("Binary"), new[] { 0, 3 }, operation: "Add"), 1);
        nodes.SetValue(Node(FunctionOp("Constant"), initial: 1), 2);
        nodes.SetValue(Node(FunctionOp("Constant"), initial: 1), 3);
        Array parameters = Array.CreateInstance(assembly.GetType("StellarFunctions.FunctionParameter", true), 0);
        return Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { id, "Broadcast memory", parameters, nodes, 0, "broadcast-memory-structure" },
            null
        );
    }

    private static object Node(
        object op,
        int[] inputs = null,
        string operation = null,
        double initial = 0,
        Guid reference = default,
        Guid[] bindings = null,
        byte wiredMask = 0
    )
    {
        Type type = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionNode", true);
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[]
            {
                Guid.NewGuid(),
                op,
                inputs,
                operation,
                initial,
                reference,
                bindings ?? Array.Empty<Guid>(),
                wiredMask,
            },
            null
        );
    }

    private static object FunctionOp(string name) =>
        Enum.Parse(typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionOp", true), name);

    private static string State(object graph, double memory)
    {
        FieldInfo nodes = graph.GetType().GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic);
        Guid nodeId = (Guid)
            nodes
                .GetValue(graph)
                .GetType()
                .GetElementType()
                .GetField("Id", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(((Array)nodes.GetValue(graph)).GetValue(0));
        return new JObject { [nodeId.ToString("N")] = memory }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static double MemoryValue(string json, object graph)
    {
        string key = JObject.Parse(State(graph, 0)).Properties().Single().Name;
        return (double)JObject.Parse(json)[key];
    }

    private static FunctionCallState Call(
        MethodInfo evaluate,
        PartContext address,
        FunctionCallState call,
        long pass
    ) => (FunctionCallState)evaluate.Invoke(null, new object[] { address, call, new[] { 0d, 0d, 0d }, .02d, pass });

    private static void BroadcastNestedRuntimeChecks(
        Action<bool, string> check,
        Assembly assembly,
        Type runtime,
        Type definitionType,
        object definitions,
        object transmitters,
        uint localAssembly,
        uint remoteAssembly
    )
    {
        MethodInfo evaluate = runtime.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo expanded = runtime.GetMethod("Expanded", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo expandedStructure = runtime.GetMethod(
            "ExpandedStructure",
            BindingFlags.Static | BindingFlags.NonPublic
        );

        Type paramType = assembly.GetType("StellarFunctions.FunctionParameter", true);
        Type graphType = assembly.GetType("StellarFunctions.FunctionGraph", true);
        Type nodeType = assembly.GetType("StellarFunctions.FunctionNode", true);

        Guid helperParamId = Guid.NewGuid();
        object helperParam = Activator.CreateInstance(
            paramType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { helperParamId, "y", 1, (double?)0 },
            null
        );
        Array helperParams = Array.CreateInstance(paramType, 1);
        helperParams.SetValue(helperParam, 0);

        Array helperNodes = Array.CreateInstance(nodeType, 3);
        helperNodes.SetValue(Node(FunctionOp("Parameter"), reference: helperParamId), 0);
        helperNodes.SetValue(Node(FunctionOp("Constant"), initial: 100), 1);
        helperNodes.SetValue(Node(FunctionOp("Binary"), new[] { 0, 1 }, operation: "Add"), 2);

        Guid helperId = Guid.NewGuid();
        object helperGraph = Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { helperId, "Inner helper", helperParams, helperNodes, 2, "helper-structure" },
            null
        );
        object helperDef = Definition(
            definitionType,
            helperId,
            localAssembly,
            helperGraph,
            ReturnState(helperId, "Inner helper", false)
        );
        ((IDictionary)definitions).Add(helperId, helperDef);

        Guid innerParamId = Guid.NewGuid();
        object innerParam = Activator.CreateInstance(
            paramType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { innerParamId, "x", 1, (double?)0 },
            null
        );
        Array innerParams = Array.CreateInstance(paramType, 1);
        innerParams.SetValue(innerParam, 0);

        Array innerNodes = Array.CreateInstance(nodeType, 4);
        innerNodes.SetValue(Node(FunctionOp("Parameter"), reference: innerParamId), 0);
        innerNodes.SetValue(
            Node(
                FunctionOp("Nested"),
                new[] { 0, -1, -1 },
                reference: helperId,
                bindings: new[] { helperParamId },
                wiredMask: 1
            ),
            1
        );
        innerNodes.SetValue(Node(FunctionOp("Constant"), initial: 10), 2);
        innerNodes.SetValue(Node(FunctionOp("Binary"), new[] { 1, 2 }, operation: "Add"), 3);

        Guid innerId = Guid.NewGuid();
        object innerGraph = Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { innerId, "Inner broadcast", innerParams, innerNodes, 3, "inner-structure" },
            null
        );
        object innerDef = Definition(
            definitionType,
            innerId,
            localAssembly,
            innerGraph,
            ReturnState(innerId, "Inner broadcast", true)
        );
        ((IDictionary)definitions).Add(innerId, innerDef);

        Array outerParams = Array.CreateInstance(paramType, 0);
        Array outerNodes = Array.CreateInstance(nodeType, 2);
        outerNodes.SetValue(Node(FunctionOp("Constant"), initial: 5), 0);
        outerNodes.SetValue(
            Node(
                FunctionOp("Nested"),
                new[] { 0, -1, -1 },
                reference: innerId,
                bindings: new[] { innerParamId },
                wiredMask: 1
            ),
            1
        );

        Guid outerId = Guid.NewGuid();
        object outerGraph = Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { outerId, "Outer", outerParams, outerNodes, 1, "outer-structure" },
            null
        );
        object outerDef = Definition(
            definitionType,
            outerId,
            remoteAssembly,
            outerGraph,
            ReturnState(outerId, "Outer", false)
        );
        ((IDictionary)definitions).Add(outerId, outerDef);

        Add(transmitters, localAssembly);

        int cost = (int)expanded.Invoke(null, new object[] { outerDef, new HashSet<Guid>(), 0 });
        check(cost == 9, "expanded cost includes nested broadcast graph and its local helper");
        string outerStructure = (string)expandedStructure.Invoke(null, new object[] { outerDef, new HashSet<Guid>() });
        definitionType
            .GetField("Structure", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(outerDef, outerStructure);
        definitionType
            .GetField("ExpandedCost", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(outerDef, cost);

        PartContext outerCallAddress = new PartContext(remoteAssembly, 50);
        var outerCall = new FunctionCallState
        {
            SelectedFunctionId = outerId.ToString("N"),
            StructureKey = outerStructure,
        };

        FunctionCallState evaluated = Call(evaluate, outerCallAddress, outerCall, 10);
        check(evaluated.Status == "OK", "Call invoking function with nested broadcast function succeeds");
        Near(check, evaluated.Output, 115, "nested broadcast function produces correct computed output");

        Remove(transmitters, localAssembly);
        FunctionCallState closed = Call(evaluate, outerCallAddress, outerCall, 11);
        check(
            closed.Status == "MISSING FUNCTION" && closed.Output == 0,
            "withdrawing publisher transmitter makes nested broadcast call fail closed"
        );

        Add(transmitters, localAssembly);
        FunctionCallState restored = Call(evaluate, outerCallAddress, outerCall, 12);
        check(restored.Status == "OK", "restoring transmitter restores nested broadcast call");
        Near(check, restored.Output, 115, "restored nested call outputs correct value");

        definitionType
            .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(innerDef, ReturnState(innerId, "Inner broadcast", false));
        FunctionCallState unbroadcast = Call(evaluate, outerCallAddress, outerCall, 13);
        check(
            unbroadcast.Status == "MISSING FUNCTION" && unbroadcast.Output == 0,
            "unbroadcasted remote function called inside function is rejected"
        );
        definitionType
            .GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(innerDef, ReturnState(innerId, "Inner broadcast", true));

        Guid localHelperId = Guid.NewGuid();
        Guid localHelperParamId = Guid.NewGuid();
        object localHelperParam = Activator.CreateInstance(
            paramType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { localHelperParamId, "z", 1, (double?)0 },
            null
        );
        Array localHelperParams = Array.CreateInstance(paramType, 1);
        localHelperParams.SetValue(localHelperParam, 0);

        Array localHelperNodes = Array.CreateInstance(nodeType, 3);
        localHelperNodes.SetValue(Node(FunctionOp("Parameter"), reference: localHelperParamId), 0);
        localHelperNodes.SetValue(Node(FunctionOp("Constant"), initial: 20), 1);
        localHelperNodes.SetValue(Node(FunctionOp("Binary"), new[] { 0, 1 }, operation: "Add"), 2);

        object localHelperGraph = Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[]
            {
                localHelperId,
                "Local helper",
                localHelperParams,
                localHelperNodes,
                2,
                "local-helper-struct",
            },
            null
        );
        object localHelperDef = Definition(
            definitionType,
            localHelperId,
            remoteAssembly,
            localHelperGraph,
            ReturnState(localHelperId, "Local helper", false)
        );
        ((IDictionary)definitions).Add(localHelperId, localHelperDef);

        Guid localOuterId = Guid.NewGuid();
        Array localOuterNodes = Array.CreateInstance(nodeType, 2);
        localOuterNodes.SetValue(Node(FunctionOp("Constant"), initial: 7), 0);
        localOuterNodes.SetValue(
            Node(
                FunctionOp("Nested"),
                new[] { 0, -1, -1 },
                reference: localHelperId,
                bindings: new[] { localHelperParamId },
                wiredMask: 1
            ),
            1
        );

        object localOuterGraph = Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { localOuterId, "Local outer", outerParams, localOuterNodes, 1, "local-outer-struct" },
            null
        );
        object localOuterDef = Definition(
            definitionType,
            localOuterId,
            remoteAssembly,
            localOuterGraph,
            ReturnState(localOuterId, "Local outer", false)
        );
        ((IDictionary)definitions).Add(localOuterId, localOuterDef);

        int localCost = (int)expanded.Invoke(null, new object[] { localOuterDef, new HashSet<Guid>(), 0 });
        string localStructure = (string)
            expandedStructure.Invoke(null, new object[] { localOuterDef, new HashSet<Guid>() });
        definitionType
            .GetField("Structure", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(localOuterDef, localStructure);
        definitionType
            .GetField("ExpandedCost", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(localOuterDef, localCost);

        PartContext localCallAddress = new PartContext(remoteAssembly, 51);
        var localCall = new FunctionCallState
        {
            SelectedFunctionId = localOuterId.ToString("N"),
            StructureKey = localStructure,
        };
        FunctionCallState localEvaluated = Call(evaluate, localCallAddress, localCall, 14);
        check(
            localEvaluated.Status == "OK",
            "functions inside functions on the same base continue to work without broadcast"
        );
        Near(check, localEvaluated.Output, 27, "same-base nested function produces correct computed output");
    }

    private static void DirectorySnapshot(
        Action<bool, string> check,
        Assembly assembly,
        uint remote,
        Guid published,
        Guid privateId
    )
    {
        var settings = Resources
            .FindObjectsOfTypeAll(typeof(PartSettings))
            .Cast<PartSettings>()
            .First(s => s.id == 62005);
        var call = new FunctionCallState { SelectedFunctionId = published.ToString("N") };
        var stateful = new StatefulPart(
            12,
            settings,
            new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
        )
        {
            State = call,
        };
        var part = new ContentPart
        {
            ShipId = remote,
            PartId = 12,
            Part = stateful,
        };
        Type directory = assembly.GetType("StellarFunctions.FunctionDirectory", true);
        MethodInfo build = directory.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic);
        object snapshot = build.Invoke(null, new object[] { part, "runtime-session" });
        Type snapshotType = snapshot.GetType();
        check(
            (string)snapshotType.GetField("Target").GetValue(snapshot) == call.InstanceId.ToString("N"),
            "remote directory snapshot is bound to the requested Call"
        );
        check(
            (string)snapshotType.GetField("Session").GetValue(snapshot) == "runtime-session",
            "remote directory snapshot preserves its session nonce"
        );
        check(
            !(bool)snapshotType.GetField("Eligible").GetValue(snapshot),
            "remote Call cannot author broadcast state without a local transmitter"
        );
        Array entries = (Array)snapshotType.GetField("Entries").GetValue(snapshot);
        check(
            entries.Length == 1
                && (string)entries.GetValue(0).GetType().GetField("Id").GetValue(entries.GetValue(0))
                    == published.ToString("N"),
            "remote directory lists the published definition and excludes the private local definition"
        );
        check(
            !entries.Cast<object>().Any(e => (string)e.GetType().GetField("Id").GetValue(e) == privateId.ToString("N")),
            "remote directory does not disclose a private definition"
        );
    }

    private static void BroadcastHandleChecks(
        Action<bool, string> check,
        Assembly assembly,
        Type definitionType,
        object definition,
        Guid id,
        uint assemblyId,
        object transmitters
    )
    {
        Type broadcastType = assembly.GetType("StellarFunctions.FunctionBroadcast", true);
        MethodInfo handle = broadcastType.GetMethod("Handle", BindingFlags.Static | BindingFlags.NonPublic);
        FunctionReturnState returned = ReturnState(id, "Public limiter", false);
        FieldInfo stateField = definitionType.GetField("State", BindingFlags.Instance | BindingFlags.NonPublic);
        stateField.SetValue(definition, returned);
        ContentPart local = ReturnPart(assemblyId, returned);

        string invalidBoolean =
            "broadcast:"
            + new JObject
            {
                ["id"] = id.ToString("N"),
                ["revision"] = Revision(assembly, returned),
                ["enabled"] = "true",
            }.ToString(Newtonsoft.Json.Formatting.None);
        RejectHandle(
            check,
            handle,
            local,
            invalidBoolean,
            "INVALID BROADCAST EDIT",
            "broadcast handler rejects a non-boolean enabled token"
        );

        // This cache-only fixture has no tracked live target. Revision/held/
        // transmitter checks now run against native ShipState in BroadcastStateChecks.
        RejectHandle(
            check,
            handle,
            local,
            Payload(assembly, id, "stale-revision", false),
            "BROADCAST TARGET UNAVAILABLE",
            "cached definitions cannot substitute for a missing live broadcast target"
        );

        ContentPart distant = ReturnPart(assemblyId + 1, returned);
        RejectHandle(
            check,
            handle,
            distant,
            Payload(assembly, id, Revision(assembly, returned), false),
            "BROADCAST TARGET NOT LOCAL",
            "broadcast handler rejects a Return outside the target assembly"
        );

        FunctionReturnState current = (FunctionReturnState)stateField.GetValue(definition);
        check(!ReturnBroadcast(current), "rejected broadcast edits leave the Return unpublished");
    }

    private static ContentPart ReturnPart(uint ship, FunctionReturnState state)
    {
        var settings = Resources
            .FindObjectsOfTypeAll(typeof(PartSettings))
            .Cast<PartSettings>()
            .First(s => s.id == 62007);
        var stateful = new StatefulPart(
            1,
            settings,
            new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
        )
        {
            State = state,
        };
        return new ContentPart
        {
            ShipId = ship,
            PartId = 1,
            Part = stateful,
        };
    }

    private static string Revision(Assembly assembly, FunctionPartState state) =>
        (string)
            assembly
                .GetType("StellarFunctions.FunctionEdits", true)
                .GetMethod("Revision", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { state });

    private static string Payload(Assembly assembly, Guid id, string revision, bool enabled) =>
        (string)
            assembly
                .GetType("StellarFunctions.FunctionBroadcast", true)
                .GetMethod("Payload", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { id.ToString("N"), revision, enabled });

    private static bool ReturnBroadcast(FunctionReturnState state) =>
        (bool)state.GetType().GetProperty("Broadcast").GetValue(state, null);

    private static void RejectHandle(
        Action<bool, string> check,
        MethodInfo handle,
        ContentPart part,
        string action,
        string expected,
        string message
    )
    {
        try
        {
            handle.Invoke(null, new object[] { part, action });
            check(false, message + "; action was accepted");
        }
        catch (TargetInvocationException error)
            when (error.InnerException != null
                && error.InnerException.GetType().Name == "FunctionFault"
                && error.InnerException.Message == expected
            )
        {
            check(true, message);
        }
    }

    private static List<DictionaryEntry> Snapshot(IDictionary dictionary)
    {
        var copy = new List<DictionaryEntry>();
        IDictionaryEnumerator entries = dictionary.GetEnumerator();
        while (entries.MoveNext())
            copy.Add(new DictionaryEntry(entries.Key, entries.Value));
        return copy;
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);

    private static void Add(object set, uint value) =>
        set.GetType().GetMethod("Add").Invoke(set, new object[] { value });

    private static void Remove(object set, uint value) =>
        set.GetType().GetMethod("Remove").Invoke(set, new object[] { value });

    private static void Put(object dictionary, uint key, uint value) => ((IDictionary)dictionary)[key] = value;

    private static void CheckLookup(
        Action<bool, string> check,
        MethodInfo method,
        Guid id,
        uint ship,
        bool expected,
        string message
    )
    {
        object[] args = { id.ToString("N"), ship, null };
        bool actual = (bool)method.Invoke(null, args);
        if (actual != expected)
            throw new InvalidOperationException(message + "; actual=" + actual + ", expected=" + expected);
        check(true, message);
    }

    private static void CheckValue(Action<bool, string> check, bool value, string message) => check(value, message);

    private static void Near(Action<bool, string> check, double actual, double expected, string message) =>
        check(Math.Abs(actual - expected) < 1e-8, message + "; actual=" + actual + ", expected=" + expected);
}
