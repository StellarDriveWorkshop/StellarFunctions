using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Saving.Services.Converters;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using SerializableBeaconState = Saving.Serialization.V5.Ships.SerializableBeaconState;
using SerializableShip = Saving.Serialization.V15.Ships.SerializableShip;

namespace StellarFunctions;

// Store only our parts' versioned envelopes inside the native ship DTO, so the
// world/checkpoint and schematic are one snapshot. No independently saved sidecar.
// Beacon records provide a native string carrier; stock Beacon parts are untouched.
internal static class NativeFunctionPersistence
{
    internal const string Prefix = "@StellarFunctions/1:";
    private static HarmonyLib.Harmony _harmony;

    internal static void Install()
    {
        if (_harmony != null)
            return;
        _harmony = new HarmonyLib.Harmony("stellar.functions.persistence");
        var save = AccessTools.Method(
            typeof(ShipsConverter),
            "ConvertShipStateToSerializable",
            new[] { typeof(IShipState) }
        );
        var load = AccessTools.Method(
            typeof(ShipPartConverter),
            "ConvertToFloatingPartState",
            new[] { typeof(SerializableShip) }
        );
        if (save == null || load == null)
            throw new MissingMethodException("Native ship state persistence contract");
        _harmony.Patch(save, postfix: new HarmonyMethod(typeof(NativeFunctionPersistence), nameof(AfterSerialize)));
        _harmony.Patch(load, postfix: new HarmonyMethod(typeof(NativeFunctionPersistence), nameof(AfterDeserialize)));
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private static void AfterSerialize(IShipState state, SerializableShip __result)
    {
        var metadata = state
            .StatefulParts.Where(p => p.State is FunctionPartState)
            .Select(p => new SerializableBeaconState
            {
                partId = p.Id,
                name = Prefix + FunctionStateCodec.Encode((FunctionPartState)p.State, true),
            })
            .ToArray();
        if (metadata.Length > 0)
            __result.beaconStates = (__result.beaconStates ?? Array.Empty<SerializableBeaconState>())
                .Concat(metadata)
                .ToArray();
    }

    private static void AfterDeserialize(SerializableShip ship, StatefulPart[] __result)
    {
        foreach (StatefulPart part in __result)
        {
            Type expected = FunctionStateCodec.StateType(part.Settings.id);
            if (expected == null)
                continue;
            var records = (ship.beaconStates ?? Array.Empty<SerializableBeaconState>())
                .Where(b =>
                    b.partId == part.Id && b.name != null && b.name.StartsWith(Prefix, StringComparison.Ordinal)
                )
                .ToArray();
            if (records.Length != 1)
                throw new InvalidDataException(
                    "Function part metadata is missing or duplicated; restore the intact snapshot rather than saving defaults."
                );
            FunctionPartState state = FunctionStateCodec.Decode(
                records[0].name.Substring(Prefix.Length),
                expected,
                true
            );
            state = SchematicFunctionIntegration.RemapLoaded(state);
            part.State = state with { IsHeldByPlayer = false, HoldingPlayerId = 0, IsInUse = false };
        }
    }
}
