using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using SerializableBeaconState = Saving.Serialization.V5.Ships.SerializableBeaconState;
using SerializableShip = Saving.Serialization.V15.Ships.SerializableShip;

namespace StellarFunctions;

// Optional, pinned adapters belong to this mod. The schematic bridge keeps its
// own transaction/material checks; no compatibility-policy or source changes.
internal static class SchematicFunctionIntegration
{
    private const string ExpectedSha = "11b6409966980cd2af603def9a880634300dbde21347f41b96429692dd68133d";
    private const string AdapterOwner = "stellar.functions.schematics";
    private const string GuardOwner = "stellar.functions.schematics.guard";
    private static HarmonyLib.Harmony _harmony;
    private static HarmonyLib.Harmony _guards;
    private static bool _checked;
    private static float _nextAttempt;

    [ThreadStatic]
    private static Dictionary<Guid, Guid> _copyIds;

    internal static void InstallIfPresent()
    {
        if (_checked || Time.unscaledTime < _nextAttempt)
            return;
        _nextAttempt = Time.unscaledTime + 1f;
        Assembly assembly = AppDomain
            .CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "ShipSchematics");
        if (assembly == null)
            return;
        _checked = true;
        try
        {
            bool supported;
            using (SHA256 sha = SHA256.Create())
            using (var file = File.OpenRead(assembly.Location))
                supported =
                    BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant() == ExpectedSha;
            if (!supported)
                throw new InvalidOperationException("Unsupported ShipSchematics binary");

            InstallAdapter(assembly);
        }
        catch (Exception error)
        {
            // Roll back the incomplete adapter before guarding schematic operations.
            string failure = error.GetBaseException().Message;
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception rollbackError)
            {
                failure += ". Adapter rollback failed: " + rollbackError.GetBaseException().Message;
            }

            _guards = new HarmonyLib.Harmony(GuardOwner);
            try
            {
                InstallUnsupportedGuards(assembly);
            }
            catch (Exception guardError)
            {
                // Continue installing Functions itself even when an unknown
                // schematic API cannot be fully guarded.
                MelonLoader.MelonLogger.Error(
                    "StellarFunctions: schematic integration failed ("
                        + failure
                        + "). Guard coverage is incomplete ("
                        + guardError.GetBaseException().Message
                        + "). Remove ShipSchematics before using schematics."
                );
                return;
            }
            MelonLoader.MelonLogger.Error(
                "StellarFunctions: schematic integration failed ("
                    + failure
                    + "). Discovered capture and placement entry points are blocked. Use the reviewed Ship Schematics 1.0.2 build."
            );
        }
    }

    private static void InstallAdapter(Assembly assembly)
    {
        Type library = assembly.GetType("ShipSchematics.SchematicLibrary", true);
        Type subships = assembly.GetType("ShipSchematics.Subships", true);
        Type fileType = assembly.GetType("ShipSchematics.SchematicFile", true);
        MethodInfo strip = AccessTools.Method(library, "StripContents", new[] { typeof(SerializableShip) });
        MethodInfo retarget = AccessTools.Method(
            library,
            "RetargetLinks",
            new[] { typeof(SerializableShip), typeof(Func<uint, uint>) }
        );
        MethodInfo place = AccessTools.Method(
            subships,
            "Place",
            new[] { fileType, typeof(Vector3), typeof(Quaternion), typeof(string).MakeByRefType() }
        );
        if (strip == null || retarget == null || place == null)
            throw new MissingMethodException("Pinned schematic metadata/copy contract");

        _harmony = new HarmonyLib.Harmony(AdapterOwner);
        _harmony.Patch(strip, prefix: Hook(nameof(BeforeStrip)), postfix: Hook(nameof(AfterStrip)));
        RequirePatch(strip, nameof(BeforeStrip), HarmonyPatchType.Prefix, AdapterOwner);
        RequirePatch(strip, nameof(AfterStrip), HarmonyPatchType.Postfix, AdapterOwner);
        _harmony.Patch(retarget, postfix: Hook(nameof(AfterRetarget)));
        RequirePatch(retarget, nameof(AfterRetarget), HarmonyPatchType.Postfix, AdapterOwner);
        _harmony.Patch(place, prefix: Hook(nameof(BeforePlace)), finalizer: Hook(nameof(AfterPlace)));
        RequirePatch(place, nameof(BeforePlace), HarmonyPatchType.Prefix, AdapterOwner);
        RequirePatch(place, nameof(AfterPlace), HarmonyPatchType.Finalizer, AdapterOwner);
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(SchematicFunctionIntegration), name);

    private static void RequirePatch(MethodInfo target, string name, HarmonyPatchType kind, string owner)
    {
        var info = HarmonyLib.Harmony.GetPatchInfo(target);
        MethodInfo expected = AccessTools.Method(typeof(SchematicFunctionIntegration), name);
        var patches =
            kind == HarmonyPatchType.Prefix ? info?.Prefixes
            : kind == HarmonyPatchType.Postfix ? info?.Postfixes
            : info?.Finalizers;
        if (patches == null || !patches.Any(p => p.owner == owner && p.PatchMethod == expected))
            throw new InvalidOperationException("Missing schematic " + kind + " hook: " + name);
    }

    private static void InstallUnsupportedGuards(Assembly assembly)
    {
        int captures = 0,
            places = 0;
        var errors = new List<string>();
        foreach (string name in new[] { "ShipSchematics.SchematicLibrary", "ShipSchematics.Subships" })
        {
            Type type = assembly.GetType(name);
            if (type == null)
                continue;
            foreach (
                MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "Capture" || m.Name == "Place")
            )
            {
                try
                {
                    _guards.Patch(method, prefix: Hook(nameof(RejectUnsupported)));
                    RequirePatch(method, nameof(RejectUnsupported), HarmonyPatchType.Prefix, GuardOwner);
                    if (method.Name == "Capture")
                        captures++;
                    else
                        places++;
                }
                catch (Exception error)
                {
                    errors.Add(
                        method.DeclaringType.FullName + "." + method.Name + ": " + error.GetBaseException().Message
                    );
                }
            }
        }
        if (captures == 0 || places == 0 || errors.Count > 0)
            throw new InvalidOperationException(
                "Capture guards="
                    + captures
                    + ", Place guards="
                    + places
                    + (errors.Count > 0 ? "; " + string.Join("; ", errors) : string.Empty)
            );
    }

    private static void RejectUnsupported() =>
        throw new InvalidOperationException(
            "Schematic operation blocked: StellarFunctions requires the reviewed ShipSchematics 1.0.2 binary to preserve function data."
        );

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _guards?.UnpatchSelf();
        _harmony = null;
        _guards = null;
        _checked = false;
        _copyIds = null;
    }

    private static bool IsOurs(SerializableBeaconState state) =>
        state.name != null && state.name.StartsWith(NativeFunctionPersistence.Prefix, StringComparison.Ordinal);

    private static void BeforeStrip(SerializableShip ship, out SerializableBeaconState[] __state) =>
        __state = ship?.beaconStates?.Where(IsOurs).ToArray() ?? Array.Empty<SerializableBeaconState>();

    private static void AfterStrip(SerializableShip ship, SerializableBeaconState[] __state)
    {
        if (ship != null && __state.Length > 0)
            ship.beaconStates = (ship.beaconStates ?? Array.Empty<SerializableBeaconState>())
                .Where(s => !IsOurs(s))
                .Concat(__state)
                .ToArray();
    }

    private static void AfterRetarget(SerializableShip ship, Func<uint, uint> resolve)
    {
        if (ship?.beaconStates == null)
            return;
        for (int i = 0; i < ship.beaconStates.Length; i++)
        {
            var record = ship.beaconStates[i];
            if (!IsOurs(record))
                continue;
            var state = Decode(ship, record);
            if (state is FunctionReturnState returned)
            {
                JObject identities = NativeFunctionCompiler.ParseObject(returned.NodeIdentities, 65536),
                    remapped = new JObject();
                foreach (JProperty p in identities.Properties())
                {
                    string[] key = p.Name.Split(':');
                    if (key.Length != 2 || !uint.TryParse(key[0], out uint old))
                        throw new InvalidDataException("Invalid function body address.");
                    string target = resolve(old) + ":" + key[1];
                    if (remapped[target] != null)
                        throw new InvalidDataException(
                            "Function body addresses collided while remapping a partial schematic."
                        );
                    remapped[target] = p.Value.DeepClone();
                }
                JObject sources = NativeFunctionCompiler.ParseObject(returned.Sources, 32768);
                foreach (string section in new[] { "channels", "parts" })
                    if (sources[section] is JObject entries)
                    {
                        var next = new JObject();
                        foreach (JProperty p in entries.Properties())
                        {
                            if (p.Value is not JObject source)
                                throw new InvalidDataException("Invalid external function source.");
                            JObject copy = (JObject)source.DeepClone();
                            copy["ship"] = resolve((uint?)source["ship"] ?? 0);
                            string key = p.Name;
                            if (section == "parts")
                            {
                                string[] address = key.Split(':');
                                if (address.Length != 2 || !uint.TryParse(address[0], out uint old))
                                    throw new InvalidDataException("Invalid function source address.");
                                key = resolve(old) + ":" + address[1];
                            }
                            if (next[key] != null)
                                throw new InvalidDataException("Function source addresses collided during remapping.");
                            next[key] = copy;
                        }
                        sources[section] = next;
                    }
                state = returned with
                {
                    NodeIdentities = remapped.ToString(Formatting.None),
                    Sources = sources.ToString(Formatting.None),
                };
            }
            record.name = NativeFunctionPersistence.Prefix + FunctionStateCodec.Encode(state, true);
            ship.beaconStates[i] = record;
        }
    }

    private static FunctionPartState Decode(SerializableShip ship, SerializableBeaconState record)
    {
        ushort settings = 0;
        if (ship.statefulFloatingParts != null)
            foreach (var part in ship.statefulFloatingParts)
                if (part.id == record.partId)
                    settings = part.settingsId;
        if (ship.statefulHullParts != null)
            foreach (var part in ship.statefulHullParts)
                if (part.id == record.partId)
                    settings = part.settingsId;
        Type type = FunctionStateCodec.StateType(settings);
        if (type == null)
            throw new InvalidDataException("Function metadata does not belong to a function content part.");
        return FunctionStateCodec.Decode(record.name.Substring(NativeFunctionPersistence.Prefix.Length), type, true);
    }

    private static void BeforePlace(object file, out Dictionary<Guid, Guid> __state)
    {
        __state = _copyIds;
        if (file == null)
            return;
        Type type = file.GetType();
        string root = (string)type.GetField("shipJson").GetValue(file);
        string[] children = (string[])type.GetField("childShipJson").GetValue(file) ?? Array.Empty<string>();
        if (children.Length > 127)
            throw new InvalidDataException("Function schematic ship limit exceeded.");
        var map = new Dictionary<Guid, Guid>();
        int count = 0;
        foreach (string json in new[] { root }.Concat(children))
        {
            if (json == null || json.Length > 8388608)
                throw new InvalidDataException("Function schematic payload limit exceeded.");
            SerializableShip ship = JsonUtility.FromJson<SerializableShip>(json);
            foreach (var record in ship.beaconStates ?? Array.Empty<SerializableBeaconState>())
            {
                if (!IsOurs(record))
                    continue;
                if (++count > 4096)
                    throw new InvalidDataException("Function schematic part limit exceeded.");
                FunctionPartState state = Decode(ship, record);
                if (!map.ContainsKey(state.StableId))
                    map[state.StableId] = Guid.NewGuid();
                if (state is FunctionReturnState returned)
                    foreach (
                        JProperty p in NativeFunctionCompiler.ParseObject(returned.NodeIdentities, 65536).Properties()
                    )
                    {
                        if (!Guid.TryParse((string)p.Value, out Guid id) || id == Guid.Empty)
                            throw new InvalidDataException("Invalid schematic node identity.");
                        if (!map.ContainsKey(id))
                            map[id] = Guid.NewGuid();
                    }
            }
        }
        _copyIds = map;
    }

    private static Exception AfterPlace(Exception __exception, Dictionary<Guid, Guid> __state)
    {
        _copyIds = __state;
        return __exception;
    }

    internal static FunctionPartState RemapLoaded(FunctionPartState state)
    {
        if (_copyIds == null)
            return state;
        Guid Map(Guid id)
        {
            if (id == Guid.Empty)
                return Guid.Empty;
            if (!_copyIds.TryGetValue(id, out Guid next))
                _copyIds[id] = next = Guid.NewGuid();
            return next;
        }
        string Reference(string text) => Guid.TryParse(text, out Guid id) ? Map(id).ToString("N") : string.Empty;
        if (state is FunctionParamState p)
            return p with { ParamId = Map(p.ParamId) };
        if (state is FunctionReturnState r)
        {
            JObject nodes = NativeFunctionCompiler.ParseObject(r.NodeIdentities, 65536);
            foreach (JProperty node in nodes.Properties())
                node.Value = Reference((string)node.Value);
            return r with
            {
                DefinitionId = Map(r.DefinitionId),
                NodeIdentities = nodes.ToString(Formatting.None),
                Status = "TEMPLATE PENDING",
                Broadcast = false,
            };
        }
        if (state is FunctionTransmitterState transmitter)
            return transmitter with { TransmitterId = Map(transmitter.TransmitterId) };
        var c = (FunctionCallState)state;
        return c with
        {
            InstanceId = Map(c.InstanceId),
            SelectedFunctionId = Reference(c.SelectedFunctionId),
            Slot0 = Reference(c.Slot0),
            Slot1 = Reference(c.Slot1),
            Slot2 = Reference(c.Slot2),
            PendingFunctionId = "",
            PendingFunctionName = "",
            ResizeFeedback = "",
            RuntimeJson = "{}",
            StructureKey = "",
            Output = 0,
            LastInput0 = 0,
            LastInput1 = 0,
            LastInput2 = 0,
            LastSignalPass = 0,
            Status = "FRESH SCHEMATIC COPY",
        };
    }
}
