// A controlled Handle regression: real ServiceLocator lookup and native ShipState
// mutation, with the runtime cache seeded to model interaction-only IsInUse drift.
// This does not claim to exercise the full game/server interaction lifecycle.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using StellarFunctions.Content;
using UnityEngine;

namespace StellarFunctions;

public static class BroadcastStateChecks
{
    private const uint ShipId = 7701;
    private const ushort ReturnPartId = 1;
    private const ushort TransmitterPartId = 2;

    public static void Run(Action<bool, string> check)
    {
        Type runtime = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.NativeFunctionRuntime", true);
        Type definitionType = runtime.Assembly.GetType("StellarFunctions.NativeFunctionDefinition", true);
        FieldInfo definitionsField = Field(runtime, "Definitions");
        FieldInfo parentsField = Field(runtime, "Parents");
        FieldInfo transmittersField = Field(runtime, "TransmitterAssemblies");
        FieldInfo shipsField = Field(runtime, "_ships");
        FieldInfo connectionsField = Field(runtime, "_connections");
        FieldInfo dirtyField = Field(runtime, "_dirty");
        FieldInfo internalChangeField = Field(runtime, "_internalChange");
        FieldInfo watchedStatesField = Field(runtime, "WatchedStates");
        // The immutable baseline predates per-ship watches. Keep that fixture
        // compatible so it reaches the actual IsInUse overwrite assertion first.
        IDictionary watchedStates =
            watchedStatesField == null ? new Hashtable() : (IDictionary)watchedStatesField.GetValue(null);
        List<DictionaryEntry> oldWatchedStates = Snapshot(watchedStates);
        Type catalogType = runtime.Assembly.GetType("StellarFunctions.FunctionCatalog", true);
        IDictionary catalogEntries = (IDictionary)
            catalogType.GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        List<DictionaryEntry> oldCatalogEntries = Snapshot(catalogEntries);

        object oldDefinitions = definitionsField.GetValue(null);
        object oldParents = parentsField.GetValue(null);
        object oldTransmitters = transmittersField.GetValue(null);
        object oldShips = shipsField.GetValue(null);
        object oldConnections = connectionsField.GetValue(null);
        object oldDirty = dirtyField.GetValue(null);
        object oldInternalChange = internalChangeField.GetValue(null);

        GameObject serviceObject = null;
        HarmonyLib.Harmony refreshPatch = null;
        try
        {
            // Refresh must not rebuild the intentionally seeded cache from a
            // surrounding test world. Only this one method is suppressed here.
            refreshPatch = new HarmonyLib.Harmony("StellarFunctions.BroadcastStateChecks.Refresh");
            refreshPatch.Patch(
                runtime.GetMethod("Refresh", BindingFlags.Static | BindingFlags.NonPublic),
                prefix: new HarmonyMethod(
                    typeof(BroadcastStateChecks).GetMethod(
                        nameof(SkipRefresh),
                        BindingFlags.Static | BindingFlags.NonPublic
                    )
                )
            );

            object definitions = Activator.CreateInstance(definitionsField.FieldType);
            object parents = Activator.CreateInstance(parentsField.FieldType);
            object transmitters = Activator.CreateInstance(transmittersField.FieldType);
            definitionsField.SetValue(null, definitions);
            parentsField.SetValue(null, parents);
            transmittersField.SetValue(null, transmitters);
            shipsField.SetValue(null, null);
            connectionsField.SetValue(null, null);
            dirtyField.SetValue(null, false);
            internalChangeField.SetValue(null, false);
            ((IDictionary)parents).Add(ShipId, ShipId);
            ((ISet<uint>)transmitters).Add(ShipId);

            serviceObject = new GameObject("Private broadcast state check service");
            serviceObject.tag = "ServiceObject";
            ShipsServerTracker tracker = serviceObject.AddComponent<ShipsServerTracker>();
            check(
                ReferenceEquals(Core.Services.ServiceLocator.GetService<ShipsServerTracker>(), tracker),
                "controlled ServiceLocator resolves the synthetic native ship tracker"
            );
            ShipState state = new ShipState(ShipId, ShipId, new PartSettingsProvider());
            TrackedShipServer tracked = (TrackedShipServer)
                FormatterServices.GetUninitializedObject(typeof(TrackedShipServer));
            typeof(TrackedShip)
                .GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(tracked, state);
            Dictionary<uint, TrackedShipServer> trackedShips =
                (Dictionary<uint, TrackedShipServer>)
                    typeof(ShipsServerTracker)
                        .GetField("_shipsDict", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(tracker);
            trackedShips.Add(ShipId, tracked);

            Guid id = Guid.NewGuid();
            string sources = new JObject
            {
                ["channels"] = new JObject
                {
                    ["input"] = new JObject
                    {
                        ["ship"] = ShipId,
                        ["part"] = 9,
                        ["socket"] = 0,
                    },
                },
            }.ToString(Newtonsoft.Json.Formatting.None);
            FunctionReturnState live = ReturnState(id, broadcast: false, inUse: false, sources: sources);
            StatefulPart returnPart = MakePart(ReturnPartId, 62007, live);
            state.AddStatefulPartWithoutInit(returnPart);

            object graph = MemoryGraph(id);
            object definition = MakeDefinition(definitionType, id, ReturnPartId, live, graph);
            ((IDictionary)definitions).Add(id, definition);
            ContentPart returnContent = new ContentPart
            {
                ShipId = ShipId,
                PartId = ReturnPartId,
                Part = returnPart,
            };
            MethodInfo handle = runtime
                .Assembly.GetType("StellarFunctions.FunctionBroadcast", true)
                .GetMethod("Handle", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo revision = runtime
                .Assembly.GetType("StellarFunctions.FunctionEdits", true)
                .GetMethod("Revision", BindingFlags.Static | BindingFlags.NonPublic);

            // Native interaction updates IsInUse after the cache is captured.
            // Semantics intentionally ignore that field, so the cache remains stale.
            // ShipsServerTracker.LoadShip snapshots its current delegate values
            // into each ShipState. A later += to the tracker property does not
            // update a state that was wired while the property was null.
            state.ShipStatefulPartUpdated = tracker.ShipStatefulPartUpdated;
            int lateTrackerUpdates = 0;
            tracker.ShipStatefulPartUpdated += (_, __) => lateTrackerUpdates++;
            FunctionReturnState interacting = live with { IsInUse = true, HoldingPlayerId = 314 };
            state.SetPartState(ReturnPartId, interacting);
            check(
                (bool)typeof(FunctionReturnState).GetProperty("IsInUse").GetValue(returnPart.State),
                "native ShipState fixture models a Return currently in use"
            );
            check(
                lateTrackerUpdates == 0,
                "late tracker state-update subscription does not reach a ShipState wired before the subscription"
            );

            string revisionText = (string)revision.Invoke(null, new object[] { live });
            string enable = Payload(id, revisionText, true);
            check(
                (bool)handle.Invoke(null, new object[] { returnContent, enable }),
                "broadcast Handle accepts a valid Return edit"
            );
            FunctionReturnState afterReturn = (FunctionReturnState)returnPart.State;
            check(afterReturn.Broadcast, "Return broadcast edit writes the requested value");
            check(afterReturn.IsInUse, "Return broadcast edit preserves the live IsInUse state");
            check(
                afterReturn.DefinitionId == id && afterReturn.Sources == sources,
                "Return broadcast edit preserves function identity and source bindings"
            );

            // The transmitter path targets the same Return, even though the
            // action arrived from a transmitter ContentPart.
            Field(definitionType, "State").SetValue(definition, afterReturn with { IsInUse = false });
            FunctionReturnState transmitterLive = afterReturn with { IsInUse = true };
            state.SetPartState(ReturnPartId, transmitterLive);
            StatefulPart transmitterPart = MakePart(TransmitterPartId, 62009, new FunctionTransmitterState());
            state.AddStatefulPartWithoutInit(transmitterPart);
            ContentPart transmitterContent = new ContentPart
            {
                ShipId = ShipId,
                PartId = TransmitterPartId,
                Part = transmitterPart,
            };
            string disable = Payload(id, (string)revision.Invoke(null, new object[] { transmitterLive }), false);
            check(
                (bool)handle.Invoke(null, new object[] { transmitterContent, disable }),
                "transmitter Handle accepts a valid broadcast edit"
            );
            FunctionReturnState afterTransmitter = (FunctionReturnState)returnPart.State;
            check(
                !afterTransmitter.Broadcast && afterTransmitter.IsInUse,
                "transmitter broadcast edit changes only Broadcast on the live Return state"
            );
            check(
                afterTransmitter.DefinitionId == id && afterTransmitter.Sources == sources,
                "transmitter broadcast edit preserves function identity and source bindings"
            );

            Reject(
                check,
                handle,
                returnContent,
                Payload(id, "stale-revision", true),
                "EDIT CONFLICT: REOPEN",
                "broadcast Handle rejects a stale Return revision"
            );

            FunctionReturnState held = afterTransmitter with { IsHeldByPlayer = true, HoldingPlayerId = 314 };
            state.SetPartState(ReturnPartId, held);
            Field(definitionType, "State").SetValue(definition, afterTransmitter);
            Reject(
                check,
                handle,
                returnContent,
                Payload(id, (string)revision.Invoke(null, new object[] { held }), true),
                "BROADCAST TARGET UNAVAILABLE",
                "broadcast Handle rejects a Return held in live native state"
            );
            check(ReferenceEquals(returnPart.State, held), "held Return rejection does not mutate its native state");

            FunctionReturnState unheld = held with { IsHeldByPlayer = false, HoldingPlayerId = 0 };
            state.SetPartState(ReturnPartId, unheld);
            Field(definitionType, "State").SetValue(definition, unheld with { IsInUse = false });
            ((ISet<uint>)transmitters).Remove(ShipId);
            Reject(
                check,
                handle,
                returnContent,
                Payload(id, (string)revision.Invoke(null, new object[] { unheld }), true),
                "VALID TEMPLATE AND TRANSMITTER REQUIRED",
                "broadcast Handle refuses publication without a transmitter"
            );
            check(
                ReferenceEquals(returnPart.State, unheld),
                "missing-transmitter rejection leaves the Return unchanged"
            );
            ((ISet<uint>)transmitters).Add(ShipId);

            trackedShips.Remove(ShipId);
            Reject(
                check,
                handle,
                returnContent,
                Payload(id, (string)revision.Invoke(null, new object[] { unheld }), false),
                "BROADCAST TARGET UNAVAILABLE",
                "broadcast Handle refuses an unavailable tracked Return"
            );
            trackedShips.Add(ShipId, tracked);

            Guid missing = Guid.NewGuid();
            Reject(
                check,
                handle,
                returnContent,
                Payload(missing, "missing", true),
                "BROADCAST TARGET NOT LOCAL",
                "broadcast Handle rejects an unknown Return identity"
            );

            WatchLifecycleChecks(check, runtime, tracked, state, dirtyField, watchedStates);
        }
        finally
        {
            MethodInfo unwatch = runtime.GetMethod("Unwatch", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (DictionaryEntry entry in Snapshot(watchedStates))
            {
                if (!oldWatchedStates.Any(old => Equals(old.Key, entry.Key) && ReferenceEquals(old.Value, entry.Value)))
                {
                    unwatch?.Invoke(null, new[] { entry.Value });
                }
            }

            watchedStates.Clear();
            foreach (DictionaryEntry entry in oldWatchedStates)
            {
                watchedStates.Add(entry.Key, entry.Value);
            }

            refreshPatch?.UnpatchSelf();
            if (serviceObject != null)
            {
                UnityEngine.Object.DestroyImmediate(serviceObject);
            }

            catalogEntries.Clear();
            foreach (DictionaryEntry entry in oldCatalogEntries)
            {
                catalogEntries.Add(entry.Key, entry.Value);
            }

            definitionsField.SetValue(null, oldDefinitions);
            parentsField.SetValue(null, oldParents);
            transmittersField.SetValue(null, oldTransmitters);
            shipsField.SetValue(null, oldShips);
            connectionsField.SetValue(null, oldConnections);
            dirtyField.SetValue(null, oldDirty);
            internalChangeField.SetValue(null, oldInternalChange);
        }
    }

    private static bool SkipRefresh() => false;

    private static void WatchLifecycleChecks(
        Action<bool, string> check,
        Type runtime,
        TrackedShipServer tracked,
        ShipState originalState,
        FieldInfo dirtyField,
        IDictionary watchedStates
    )
    {
        if (watchedStates.Count != 0)
        {
            check(false, "controlled asset-only watcher fixture starts without previously watched server ships");
            return;
        }

        MethodInfo track = runtime.GetMethod("Tracked", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo untrack = runtime.GetMethod("Untracked", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo reset = runtime.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo added = runtime.GetMethod("Added", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo updated = runtime.GetMethod("Updated", BindingFlags.Static | BindingFlags.NonPublic);
        check(
            track != null && untrack != null && reset != null && added != null && updated != null,
            "native runtime exposes the per-ship watch lifecycle hooks"
        );
        if (track == null || untrack == null || reset == null || added == null || updated == null)
        {
            return;
        }

        track.Invoke(null, new object[] { tracked });
        check(
            ReferenceEquals(watchedStates[ShipId], originalState),
            "production Tracked hook watches the ship's live ShipState"
        );
        CheckDelegateCount(
            check,
            originalState,
            "ShipStatefulPartAdded",
            added,
            1,
            "production Tracked hook attaches one add handler to a late-loaded ship state"
        );
        CheckDelegateCount(
            check,
            originalState,
            "ShipStatefulPartUpdated",
            updated,
            1,
            "production Tracked hook attaches one update handler to a late-loaded ship state"
        );

        dirtyField.SetValue(null, false);
        originalState.AddStatefulPart(MakePart(3, 62009, new FunctionTransmitterState()));
        check((bool)dirtyField.GetValue(null), "native late-added transmitter invokes the production dirty handler");

        track.Invoke(null, new object[] { tracked });
        CheckDelegateCount(
            check,
            originalState,
            "ShipStatefulPartAdded",
            added,
            1,
            "repeated Tracked notification does not duplicate the production add handler"
        );
        dirtyField.SetValue(null, false);
        originalState.AddStatefulPart(MakePart(4, 62009, new FunctionTransmitterState()));
        check((bool)dirtyField.GetValue(null), "idempotent watch still dirties once for a subsequent native addition");

        untrack.Invoke(null, new object[] { tracked });
        check(!watchedStates.Contains(ShipId), "production Untracked hook removes the ship-state watch");
        CheckDelegateCount(
            check,
            originalState,
            "ShipStatefulPartAdded",
            added,
            0,
            "production Untracked hook detaches its add handler"
        );
        dirtyField.SetValue(null, false);
        originalState.AddStatefulPart(MakePart(5, 62009, new FunctionTransmitterState()));
        check(!(bool)dirtyField.GetValue(null), "an untracked ShipState no longer dirties the runtime on additions");

        track.Invoke(null, new object[] { tracked });
        ShipState replacement = new ShipState(ShipId, ShipId, new PartSettingsProvider());
        typeof(TrackedShip)
            .GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(tracked, replacement);
        track.Invoke(null, new object[] { tracked });
        check(
            ReferenceEquals(watchedStates[ShipId], replacement),
            "production Tracked hook rebinds when a ship's ShipState instance changes"
        );
        CheckDelegateCount(
            check,
            originalState,
            "ShipStatefulPartAdded",
            added,
            0,
            "state replacement detaches the previous ShipState add handler"
        );
        CheckDelegateCount(
            check,
            replacement,
            "ShipStatefulPartAdded",
            added,
            1,
            "state replacement attaches one add handler to the new ShipState"
        );
        dirtyField.SetValue(null, false);
        originalState.AddStatefulPart(MakePart(6, 62009, new FunctionTransmitterState()));
        check(!(bool)dirtyField.GetValue(null), "replaced ShipState no longer dirties the runtime");
        replacement.AddStatefulPart(MakePart(1, 62009, new FunctionTransmitterState()));
        check((bool)dirtyField.GetValue(null), "replacement ShipState additions dirty the runtime");

        // Reset must unhook the production callbacks before the controlled
        // registries are restored by Run's finally block.
        track.Invoke(null, new object[] { tracked });
        reset.Invoke(null, null);
        check(watchedStates.Count == 0, "runtime Reset clears the watched-ship registry");
        CheckDelegateCount(
            check,
            replacement,
            "ShipStatefulPartAdded",
            added,
            0,
            "runtime Reset detaches its add handler from the watched ShipState"
        );
        CheckDelegateCount(
            check,
            replacement,
            "ShipStatefulPartUpdated",
            updated,
            0,
            "runtime Reset detaches its update handler from the watched ShipState"
        );
    }

    private static void CheckDelegateCount(
        Action<bool, string> check,
        ShipState state,
        string eventField,
        MethodInfo target,
        int expected,
        string message
    )
    {
        Delegate handler = (Delegate)
            typeof(ShipState).GetField(eventField, BindingFlags.Instance | BindingFlags.Public).GetValue(state);
        int count = handler?.GetInvocationList().Count(d => d.Method == target) ?? 0;
        check(count == expected, message + " (found " + count + ")");
    }

    private static StatefulPart MakePart(ushort id, ushort settingsId, IStatefulPartState state)
    {
        PartSettings settings = Resources
            .FindObjectsOfTypeAll<PartSettings>()
            .First(p => p != null && p.id == settingsId);
        return new StatefulPart(
            id,
            settings,
            new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
        )
        {
            State = state,
        };
    }

    private static FunctionReturnState ReturnState(Guid id, bool broadcast, bool inUse, string sources) =>
        new FunctionReturnState
        {
            DefinitionId = id,
            Name = "Native broadcast state",
            Broadcast = broadcast,
            IsInUse = inUse,
            Sources = sources,
        };

    private static object MakeDefinition(Type type, Guid id, ushort partId, FunctionReturnState state, object graph)
    {
        object value = Activator.CreateInstance(type, true);
        Field(type, "Address").SetValue(value, new PartContext(ShipId, partId));
        Field(type, "Assembly").SetValue(value, ShipId);
        Field(type, "State").SetValue(value, state);
        Field(type, "Graph").SetValue(value, graph);
        Field(type, "Structure").SetValue(value, "broadcast-state-check");
        Field(type, "ExpandedCost").SetValue(value, 1);
        return value;
    }

    private static object MemoryGraph(Guid id)
    {
        Assembly assembly = typeof(FunctionReturnState).Assembly;
        Type graphType = assembly.GetType("StellarFunctions.FunctionGraph", true);
        Type nodeType = assembly.GetType("StellarFunctions.FunctionNode", true);
        Array nodes = Array.CreateInstance(nodeType, 1);
        object op = Enum.Parse(assembly.GetType("StellarFunctions.FunctionOp", true), "Constant");
        object node = Activator.CreateInstance(
            nodeType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { Guid.NewGuid(), op, null, null, 1d, Guid.Empty, Array.Empty<Guid>(), (byte)0 },
            null
        );
        nodes.SetValue(node, 0);
        Array parameters = Array.CreateInstance(assembly.GetType("StellarFunctions.FunctionParameter", true), 0);
        return Activator.CreateInstance(
            graphType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { id, "Native broadcast state", parameters, nodes, 0, "broadcast-state-check" },
            null
        );
    }

    private static string Payload(Guid id, string revision, bool enabled) =>
        "broadcast:"
        + new JObject
        {
            ["id"] = id.ToString("N"),
            ["revision"] = revision,
            ["enabled"] = enabled,
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static void Reject(
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

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);

    private static List<DictionaryEntry> Snapshot(IDictionary dictionary)
    {
        var copy = new List<DictionaryEntry>();
        IDictionaryEnumerator entries = dictionary.GetEnumerator();
        while (entries.MoveNext())
        {
            copy.Add(new DictionaryEntry(entries.Key, entries.Value));
        }

        return copy;
    }
}
