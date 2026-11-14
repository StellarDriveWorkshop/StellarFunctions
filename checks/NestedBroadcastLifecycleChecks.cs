// Supplied native asset fixture. Exercises real Refresh/compiler and native state events,
// but does not establish network scheduling or dedicated-server behavior.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Core.Services;
using HarmonyLib;
using Newtonsoft.Json.Linq;
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

internal static class NestedBroadcastLifecycleChecks
{
    private const uint CallerShip = 481101,
        PublisherShip = 481102;
    private const ushort CallerReturn = 11,
        CallerCall = 12,
        RootCall = 13,
        PublishedReturn = 21,
        PublishedCall = 22,
        HelperReturn = 23,
        ConstantPart = 24,
        TransmitterPart = 25;

    internal static void Run(Action<bool, string> check)
    {
        Assembly assembly = typeof(FunctionCallState).Assembly;
        Type runtime = assembly.GetType("StellarFunctions.NativeFunctionRuntime", true);
        if (Field(runtime, "_ships").GetValue(null) != null)
        {
            check(
                false,
                "native lifecycle fixture requires a fresh asset runtime so it cannot detach another runtime's ship subscriptions"
            );
            return;
        }
        string[] staticNames =
        {
            "_ships",
            "_connections",
            "_readEmitter",
            "_dirty",
            "_internalChange",
            "WatchedStates",
            "TransmitterAssemblies",
            "Parents",
            "Definitions",
            "Owners",
            "RemovedRelays",
            "PendingMemory",
            "CallErrors",
            "ExternalSources",
        };
        var saved = staticNames.ToDictionary(n => n, n => new SavedField(Field(runtime, n)));
        FieldInfo catalogEntries = Field(assembly.GetType("StellarFunctions.FunctionCatalog", true), "Entries");
        var oldCatalog = new SavedField(catalogEntries);
        var services = new GameObject("Private nested broadcast lifecycle native services") { tag = "ServiceObject" };
        var ships = services.AddComponent<ShipsServerTracker>();
        var connections = services.AddComponent<ConnectionsServerTracker>();
        var tracked = new Dictionary<uint, TrackedShipServer>();
        var states = new Dictionary<uint, ShipState>();
        var edges = new List<SocketConnection>();
        try
        {
            check(
                ReferenceEquals(ServiceLocator.GetService<ShipsServerTracker>(), ships)
                    && ReferenceEquals(ServiceLocator.GetService<ConnectionsServerTracker>(), connections),
                "native Refresh fixture resolves its supplied ship and connection services"
            );

            AddShip(ships, CallerShip, tracked, states);
            AddShip(ships, PublisherShip, tracked, states);
            var caller = states[CallerShip];
            var publisher = states[PublisherShip];
            PartSettings constantSettings = Resources
                .FindObjectsOfTypeAll<PartSettings>()
                .Single(s => s != null && s.id == 46 && s.name == "Constant");
            Guid callerId = Guid.NewGuid(),
                publishedId = Guid.NewGuid(),
                helperId = Guid.NewGuid();
            Guid callerCallInstance = Guid.NewGuid(),
                publishedCallInstance = Guid.NewGuid();
            Add(
                caller,
                CallerReturn,
                Support.Registration.Get(62007),
                new FunctionReturnState
                {
                    DefinitionId = callerId,
                    Name = "External caller",
                    Sources = "{}",
                    NodeIdentities = "{}",
                }
            );
            Add(caller, CallerCall, Support.Registration.Get(62005), Call(publishedId, callerCallInstance));
            Add(caller, RootCall, Support.Registration.Get(62005), Call(callerId, Guid.NewGuid()));
            Add(
                publisher,
                PublishedReturn,
                Support.Registration.Get(62007),
                new FunctionReturnState
                {
                    DefinitionId = publishedId,
                    Name = "Published wrapper",
                    Broadcast = true,
                    Sources = "{}",
                    NodeIdentities = "{}",
                }
            );
            Add(publisher, PublishedCall, Support.Registration.Get(62005), Call(helperId, publishedCallInstance));
            var declared = new JObject
            {
                ["parts"] = new JObject
                {
                    [PublisherShip + ":" + ConstantPart] = new JObject
                    {
                        ["ship"] = PublisherShip,
                        ["part"] = ConstantPart,
                        ["socket"] = 0,
                    },
                },
            };
            Add(
                publisher,
                HelperReturn,
                Support.Registration.Get(62007),
                new FunctionReturnState
                {
                    DefinitionId = helperId,
                    Name = "Publisher local helper",
                    Sources = declared.ToString(),
                    NodeIdentities = "{}",
                }
            );
            const double constantValue = 12.5;
            Add(publisher, ConstantPart, constantSettings, new ConstantState { ConstantValue = constantValue });
            Add(publisher, TransmitterPart, Support.Registration.Get(62009), new FunctionTransmitterState());
            Connect(connections, edges, CallerShip, CallerReturn, 0, CallerCall, 3);
            Connect(connections, edges, PublisherShip, PublishedReturn, 0, PublishedCall, 3);
            Connect(connections, edges, PublisherShip, HelperReturn, 0, ConstantPart, 0);

            // Refresh scans actual tracked native parts and native connection groups. No Definitions are seeded.
            Method(runtime, "Refresh").Invoke(null, null);
            IDictionary defs = (IDictionary)Field(runtime, "Definitions").GetValue(null);
            check(
                defs.Contains(publishedId) && defs.Contains(helperId) && defs.Contains(callerId),
                "native Refresh compiles caller, published target, and private publisher helper from real parts and wires"
            );
            object pubDef = defs[publishedId],
                helperDef = defs[helperId],
                callerDef = defs[callerId];
            check(
                (string)Field(pubDef.GetType(), "Error").GetValue(pubDef) == null
                    && (string)Field(helperDef.GetType(), "Error").GetValue(helperDef) == null
                    && (string)Field(callerDef.GetType(), "Error").GetValue(callerDef) == null,
                "native compilation accepts a nested private helper inside a broadcast target"
            );
            check(
                (int)Field(pubDef.GetType(), "ExpandedCost").GetValue(pubDef) >= 2,
                "native expansion accounts for the published wrapper and its private nested helper"
            );

            var address = new PartContext(CallerShip, RootCall);
            var state = (FunctionCallState)tracked[CallerShip].StatefulParts.Single(p => p.Id == RootCall).State;
            MethodInfo evaluate = Method(runtime, "Evaluate");
            var result = (FunctionCallState)
                evaluate.Invoke(null, new object[] { address, state, new[] { 0d, 0d, 0d }, .02d, 71L });
            double expected = constantValue;
            check(
                result.Status == "OK" && Math.Abs(result.Output - expected) < 1e-9,
                "outer Call evaluates through published target into publisher-local helper using native-compiled graphs"
            );
            var source = new EntityPartSocket(PublisherShip, ConstantPart, 0);
            var parts = ((IEnumerable)Method(runtime, "ExternalParts").Invoke(null, new object[] { address }))
                .Cast<PartContext>()
                .ToArray();
            check(
                parts.Length == 1 && parts[0].Equals(source.PartContext),
                "compiled nested dependency traversal keeps the external emitter on the publisher"
            );
            connections.TryGetSignalConnectionGroupWithSocket(source, out var sourceGroup);
            var groups = ((IEnumerable)Method(runtime, "ExternalGroups").Invoke(null, new object[] { address }))
                .Cast<uint>()
                .ToArray();
            check(
                sourceGroup != null && groups.Length == 1 && groups[0] == sourceGroup.Id,
                "compiled nested dependency traversal exposes the publisher's native signal group to scheduling"
            );
            tracked[PublisherShip].UpdatePartState(ConstantPart, new ConstantState { ConstantValue = 31.25 });
            expected = 31.25;
            result = (FunctionCallState)
                evaluate.Invoke(null, new object[] { address, result, new[] { 0d, 0d, 0d }, .02d, 71L });
            check(
                result.Status == "OK" && Math.Abs(result.Output - expected) < 1e-9,
                "nested external read follows the live publisher emitter value rather than a copied constant"
            );

            StatefulPart transmitter = publisher.GetStatefulParts().Single(p => p.Id == TransmitterPart);
            publisher.RemoveStatefulPart(TransmitterPart);
            Method(runtime, "Refresh").Invoke(null, null);
            result = (FunctionCallState)
                evaluate.Invoke(null, new object[] { address, result, new[] { 0d, 0d, 0d }, .02d, 72L });
            check(
                result.Status == "MISSING FUNCTION" && result.Output == 0,
                "native transmitter removal event refreshes publication and makes cross-assembly nested target unavailable"
            );

            publisher.AddStatefulPart(transmitter);
            Method(runtime, "Refresh").Invoke(null, null);
            result = (FunctionCallState)
                evaluate.Invoke(null, new object[] { address, result, new[] { 0d, 0d, 0d }, .02d, 73L });
            check(
                result.Status == "OK" && Math.Abs(result.Output - expected) < 1e-9,
                "native transmitter restoration event recompiles and restores the same nested published function"
            );
        }
        finally
        {
            Method(runtime, "Reset").Invoke(null, null);
            foreach (SocketConnection edge in edges)
            {
                connections.SignalConnectionGroups.OnConnectionDelete(edge);
            }

            foreach (var pair in saved)
            {
                pair.Value.Restore();
            }

            oldCatalog.Restore();
            UnityEngine.Object.DestroyImmediate(services);
        }
    }

    private static void AddShip(
        ShipsServerTracker ships,
        uint id,
        IDictionary<uint, TrackedShipServer> tracked,
        IDictionary<uint, ShipState> states
    )
    {
        var state = new ShipState(id, id, new PartSettingsProvider());
        var ship = (TrackedShipServer)FormatterServices.GetUninitializedObject(typeof(TrackedShipServer));
        Field(typeof(TrackedShip), "<State>k__BackingField").SetValue(ship, state);
        ((IDictionary)Field(typeof(ShipsServerTracker), "_shipsDict").GetValue(ships)).Add(id, ship);
        tracked.Add(id, ship);
        states.Add(id, state);
    }

    private static void Add(ShipState state, ushort id, PartSettings settings, IStatefulPartState partState)
    {
        state.AddStatefulPartWithoutInit(
            new StatefulPart(
                id,
                settings,
                new FloatingPartPlacement(new Vector3(id, 0, 0), PartOrientation.Forward, PartOrientation.Up)
            )
            {
                State = partState,
            }
        );
    }

    private static FunctionCallState Call(Guid target, Guid instance) =>
        new FunctionCallState
        {
            InstanceId = instance,
            SelectedFunctionId = target.ToString("N"),
            InputCount = 3,
        };

    private static void Connect(
        ConnectionsServerTracker tracker,
        IList<SocketConnection> edges,
        uint ship,
        ushort from,
        byte fromSocket,
        ushort to,
        byte toSocket
    )
    {
        var edge = new SocketConnection(
            new EntityPartSocket(ship, from, fromSocket),
            new EntityPartSocket(ship, to, toSocket),
            SocketConnectionType.Cable
        );
        tracker.SignalConnectionGroups.OnConnectionCreate(edge);
        edges.Add(edge);
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private sealed class SavedField
    {
        private readonly FieldInfo _field;
        private readonly object _value;
        private readonly DictionaryEntry[] _entries;
        private readonly object[] _items;

        internal SavedField(FieldInfo field)
        {
            _field = field;
            _value = field.GetValue(null);
            if (_value is IDictionary dictionary)
            {
                var entries = new List<DictionaryEntry>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add(entry);
                }

                _entries = entries.ToArray();
            }
            else
            {
                _items =
                    _value is IEnumerable enumerable && _value is not string
                        ? enumerable.Cast<object>().ToArray()
                        : null;
            }
        }

        internal void Restore()
        {
            if (_items == null && _entries == null)
            {
                _field.SetValue(null, _value);
                return;
            }
            Method(_value.GetType(), "Clear").Invoke(_value, null);
            if (_entries != null)
            {
                MethodInfo add = _value
                    .GetType()
                    .GetMethods()
                    .First(m => m.Name == "Add" && m.GetParameters().Length == 2);
                foreach (DictionaryEntry entry in _entries)
                {
                    add.Invoke(_value, new[] { entry.Key, entry.Value });
                }
            }
            else
            {
                MethodInfo add = _value
                    .GetType()
                    .GetMethods()
                    .First(m => m.Name == "Add" && m.GetParameters().Length == 1);
                foreach (object item in _items)
                {
                    add.Invoke(_value, new[] { item });
                }
            }
            if (!_field.IsInitOnly)
            {
                _field.SetValue(null, _value);
            }
        }
    }
}
