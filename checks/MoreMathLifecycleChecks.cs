// Real compiler + actual patched UpdateSignalValues/MoreMath dispatcher, with
// supplied native state/services and an explicit cached operation list.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Core.Services;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using StellarFunctions.Game;
using UnityEngine;

namespace StellarFunctions;

internal static class MoreMathLifecycleChecks
{
    internal static void Run(Action<bool, string> check)
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MoreMath"))
        {
            return;
        }

        var runtime = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.NativeFunctionRuntime");
        check(
            AccessTools.Field(runtime, "_ships").GetValue(null) == null,
            "MoreMath compiler fixture starts without an active runtime"
        );
        var service = new GameObject("Private MoreMath compile services") { tag = "ServiceObject" };
        var ships = service.AddComponent<ShipsServerTracker>();
        var connections = service.AddComponent<ConnectionsServerTracker>();
        const uint shipId = 483001;
        var state = new ShipState(shipId, shipId, new PartSettingsProvider());
        var ship = (TrackedShipServer)FormatterServices.GetUninitializedObject(typeof(TrackedShipServer));
        AccessTools.Field(typeof(TrackedShip), "<State>k__BackingField").SetValue(ship, state);
        ((IDictionary)AccessTools.Field(typeof(ShipsServerTracker), "_shipsDict").GetValue(ships)).Add(shipId, ship);
        var edges = new List<SocketConnection>();
        GameObject updaterObject = null;
        var assets = Resources
            .FindObjectsOfTypeAll<PartSettings>()
            .GroupBy(p => p.id)
            .ToDictionary(g => g.Key, g => g.First());
        void Add(ushort id, PartSettings asset, IStatefulPartState value) =>
            state.AddStatefulPartWithoutInit(
                new StatefulPart(
                    id,
                    asset,
                    new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
                )
                {
                    State = value,
                }
            );
        void Wire(ushort from, byte output, ushort to, byte input)
        {
            var edge = new SocketConnection(
                new EntityPartSocket(shipId, from, output),
                new EntityPartSocket(shipId, to, input),
                SocketConnectionType.Cable
            );
            connections.SignalConnectionGroups.OnConnectionCreate(edge);
            edges.Add(edge);
        }
        void Unwire(ushort from, byte output, ushort to, byte input)
        {
            var edge = edges.Single(e =>
                e.SocketA.Value.Equals(new EntityPartSocket(shipId, from, output))
                && e.SocketB.Value.Equals(new EntityPartSocket(shipId, to, input))
            );
            connections.SignalConnectionGroups.OnConnectionDelete(edge);
            edges.Remove(edge);
        }
        void Refresh() => AccessTools.Method(runtime, "Refresh").Invoke(null, null);
        Guid definition = Guid.NewGuid(),
            a = Guid.NewGuid(),
            b = Guid.NewGuid();
        try
        {
            Add(
                1,
                Support.Registration.Get(62006),
                new FunctionParamState
                {
                    ParamId = a,
                    Index = 1,
                    Name = "a",
                    DefaultValue = 2,
                }
            );
            Add(
                2,
                Support.Registration.Get(62006),
                new FunctionParamState
                {
                    ParamId = b,
                    Index = 2,
                    Name = "b",
                    DefaultValue = 3,
                }
            );
            Add(3, assets[45], new MathGateState { Operation = (MathGateOperation)10 });
            Add(
                4,
                Support.Registration.Get(62007),
                new FunctionReturnState { DefinitionId = definition, Name = "MoreMath compiler" }
            );
            Add(
                10,
                Support.Registration.Get(62005),
                new FunctionCallState { SelectedFunctionId = definition.ToString("N") }
            );
            Add(
                11,
                Support.Registration.Get(62005),
                new FunctionCallState { SelectedFunctionId = definition.ToString("N") }
            );
            Wire(1, 0, 3, 0);
            Wire(2, 0, 3, 1);
            Wire(3, 2, 4, 0);
            Refresh();
            ship.TryGetStatefulPart(4, out var returned);
            check(
                ((FunctionReturnState)returned.State).Status == "TEMPLATE",
                "MoreMath Power compiles in a native function body (status="
                    + ((FunctionReturnState)returned.State).Status
                    + ")"
            );
            for (int kind = 0; kind < 2; kind++)
            {
                if (kind == 1)
                {
                    Unwire(2, 0, 3, 1);
                    Unwire(3, 2, 4, 0);
                    ship.TryGetStatefulPart(3, out var unaryPart);
                    AccessTools
                        .Field(typeof(StatefulPart), "<Settings>k__BackingField")
                        .SetValue(unaryPart, assets[54]);
                    Wire(3, 1, 4, 0);
                }
                for (int operation = 10; operation <= (kind == 0 ? 30 : 17); operation++)
                {
                    ship.UpdatePartState(
                        3,
                        kind == 0
                            ? (IStatefulPartState)new MathGateState { Operation = (MathGateOperation)operation }
                            : new SignalProcessorState { Operation = (SignalProcessorOperation)operation }
                    );
                    Refresh();
                    check(
                        ((FunctionReturnState)returned.State).Status == "TEMPLATE",
                        "native compiler accepts MoreMath " + (kind == 0 ? "binary " : "unary ") + operation
                    );
                }
            }
            // Supply the correct stock asset/socket layout for each family.
            // Only native model state is tested; no visual/collider is spawned.
            Unwire(3, 1, 4, 0);
            ship.TryGetStatefulPart(3, out var binaryPart);
            AccessTools.Field(typeof(StatefulPart), "<Settings>k__BackingField").SetValue(binaryPart, assets[45]);
            Wire(2, 0, 3, 1);
            Wire(3, 2, 4, 0);
            ship.UpdatePartState(3, new MathGateState { Operation = (MathGateOperation)10, Output = 77 });
            Refresh();
            Add(20, assets[46], new ConstantState { ConstantValue = 1 });
            Add(21, assets[46], new ConstantState());
            Add(22, assets[45], new MathGateState { Operation = (MathGateOperation)30 });
            Wire(20, 0, 22, 0);
            Wire(21, 0, 22, 1);
            Refresh();
            updaterObject = new GameObject("Private native MoreMath updater");
            var updater = updaterObject.AddComponent<SignalConnectionsServerUpdater>();
            var ops = (IList)AccessTools.Field(typeof(SignalConnectionsServerUpdater), "_operations").GetValue(updater);
            Type opType = ops.GetType().GetGenericArguments()[0];
            void Queue(ushort id)
            {
                object item = Activator.CreateInstance(opType);
                AccessTools
                    .Field(opType, "Operation")
                    .SetValue(item, Enum.Parse(AccessTools.Field(opType, "Operation").FieldType, "EvaluatePart"));
                AccessTools.Field(opType, "Part").SetValue(item, new PartContext(shipId, id));
                ops.Add(item);
            }
            void Tick()
            {
                foreach (var group in connections.SignalConnectionGroups.ConnectionGroups.Values.ToArray())
                {
                    connections.UpdateSignalGroupValue(group.Id);
                }

                AccessTools
                    .Field(typeof(SignalConnectionsServerUpdater), "_reloadCacheOnNextTick")
                    .SetValue(updater, false);
                updater.UpdateSignalValues(.1f);
                AccessTools.Method(runtime, "CommitMemory").Invoke(null, null);
            }
            ops.Clear();
            Queue(3);
            Queue(10);
            Queue(11);
            Queue(22);
            Tick();
            ship.TryGetStatefulPart(10, out var first);
            ship.TryGetStatefulPart(11, out var second);
            ship.TryGetStatefulPart(3, out var body);
            ship.TryGetStatefulPart(22, out var ordinary);
            check(
                ((FunctionCallState)first.State).Output == 8 && ((FunctionCallState)second.State).Output == 8,
                "actual MoreMath replacement dispatcher evaluates default-only Calls without a cable-group fallback"
            );
            check(
                ((MathGateState)body.State).Output == 77,
                "actual MoreMath dispatcher leaves owned template body state frozen"
            );
            check(
                ((MathGateState)ordinary.State).Output == 1,
                "ordinary unowned MoreMath counter retains native behavior beside Functions"
            );
            ship.UpdatePartState(
                1,
                new FunctionParamState
                {
                    ParamId = a,
                    Index = 1,
                    Name = "a",
                    DefaultValue = 1,
                }
            );
            ship.UpdatePartState(
                2,
                new FunctionParamState
                {
                    ParamId = b,
                    Index = 2,
                    Name = "b",
                    DefaultValue = 0,
                }
            );
            ship.UpdatePartState(3, new MathGateState { Operation = (MathGateOperation)29, Output = 77 });
            ship.UpdatePartState(10, new FunctionCallState { SelectedFunctionId = definition.ToString("N") });
            ship.UpdatePartState(11, new FunctionCallState { SelectedFunctionId = definition.ToString("N") });
            Refresh();
            ops.Clear();
            Queue(3);
            Queue(10);
            Queue(10);
            Queue(11);
            Queue(22);
            Tick();
            double dt = (double).1f;
            check(
                Math.Abs(((FunctionCallState)first.State).Output - dt) < 1e-10
                    && Math.Abs(((FunctionCallState)second.State).Output - dt) < 1e-10,
                "two native Calls start independent MoreMath timer histories and evaluate once per pass"
            );
            ops.Clear();
            Queue(3);
            Queue(10);
            Queue(10);
            Queue(22);
            Tick();
            check(
                Math.Abs(((FunctionCallState)first.State).Output - 2 * dt) < 1e-10
                    && Math.Abs(((FunctionCallState)second.State).Output - dt) < 1e-10,
                "advancing one native Call does not advance another Call's timer history"
            );
            check(
                ((FunctionCallState)first.State).RuntimeJson != "{}"
                    && ((MathGateState)body.State).Output == 77
                    && ((MathGateState)ordinary.State).Output == 1,
                "timer history commits in the Call while authored body and native counter remain independent"
            );
        }
        finally
        {
            if (updaterObject != null)
            {
                UnityEngine.Object.DestroyImmediate(updaterObject);
            }

            AccessTools.Method(runtime, "Reset").Invoke(null, null);
            foreach (var edge in edges)
            {
                connections.SignalConnectionGroups.OnConnectionDelete(edge);
            }

            UnityEngine.Object.DestroyImmediate(service);
        }
    }
}
