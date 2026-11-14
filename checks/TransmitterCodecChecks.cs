// Focused codec/copy checks for the Function Transmitter addition.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace StellarFunctions;

public static class TransmitterCodecChecks
{
    public static void Run(Action<bool, string> check)
    {
        Assembly assembly = typeof(FunctionReturnState).Assembly;
        Type codec = assembly.GetType("StellarFunctions.FunctionStateCodec", true);
        MethodInfo encode = codec.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo decode = codec.GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo stateType = codec.GetMethod("StateType", BindingFlags.Static | BindingFlags.NonPublic);
        check(
            (Type)stateType.Invoke(null, new object[] { (ushort)62009 }) == typeof(FunctionTransmitterState),
            "62009 resolves to FunctionTransmitterState"
        );

        var original = new FunctionTransmitterState { TransmitterId = Guid.NewGuid(), Summary = new string('S', 128) };
        string encoded = (string)encode.Invoke(null, new object[] { original, true });
        var envelope = JObject.Parse(encoded);
        check(
            (string)envelope["kind"] == "transmitter" && (int?)envelope["schema"] == 1,
            "transmitter uses additive schema 1 kind"
        );
        var restored = (FunctionTransmitterState)
            decode.Invoke(null, new object[] { encoded, typeof(FunctionTransmitterState), true });
        check(
            restored.TransmitterId == original.TransmitterId
                && restored.StableId == original.TransmitterId
                && restored.Summary == original.Summary,
            "transmitter identity and 128-character summary round-trip"
        );
        Reject(
            check,
            () => encode.Invoke(null, new object[] { original with { Summary = new string('S', 129) }, true }),
            "transmitter summary over 128 characters is rejected"
        );

        Guid returnId = Guid.NewGuid();
        string returnJson = (string)
            encode.Invoke(
                null,
                new object[]
                {
                    new FunctionReturnState
                    {
                        DefinitionId = returnId,
                        Name = "Broadcast",
                        Broadcast = true,
                    },
                    true,
                }
            );
        var returned = (FunctionReturnState)
            decode.Invoke(null, new object[] { returnJson, typeof(FunctionReturnState), true });
        check(returned.Broadcast, "Return Broadcast survives current codec round-trip");
        JObject legacy = JObject.Parse(returnJson);
        ((JObject)legacy["state"]).Property("Broadcast").Remove();
        var legacyReturn = (FunctionReturnState)
            decode.Invoke(null, new object[] { legacy.ToString(), typeof(FunctionReturnState), true });
        check(!legacyReturn.Broadcast, "legacy Return without Broadcast decodes as false");
        JObject malformed = JObject.Parse(returnJson);
        malformed["state"]["Broadcast"] = "true";
        Reject(
            check,
            () => decode.Invoke(null, new object[] { malformed.ToString(), typeof(FunctionReturnState), true }),
            "non-boolean Return Broadcast is rejected"
        );

        Type schematic = assembly.GetType("StellarFunctions.SchematicFunctionIntegration", true);
        FieldInfo copyIds = schematic.GetField("_copyIds", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo remap = schematic.GetMethod("RemapLoaded", BindingFlags.Static | BindingFlags.NonPublic);
        Guid oldReturn = Guid.NewGuid(),
            newReturn = Guid.NewGuid();
        Guid oldTransmitter = Guid.NewGuid(),
            newTransmitter = Guid.NewGuid();
        try
        {
            copyIds.SetValue(
                null,
                new Dictionary<Guid, Guid> { [oldReturn] = newReturn, [oldTransmitter] = newTransmitter }
            );
            var copiedReturn = (FunctionReturnState)
                remap.Invoke(
                    null,
                    new object[]
                    {
                        new FunctionReturnState
                        {
                            DefinitionId = oldReturn,
                            Name = "kept",
                            Sources = "{}",
                            NodeIdentities = "{}",
                            Broadcast = true,
                        },
                    }
                );
            check(
                copiedReturn.DefinitionId == newReturn
                    && !copiedReturn.Broadcast
                    && copiedReturn.Name == "kept"
                    && copiedReturn.Sources == "{}",
                "schematic copy remaps Return identity, resets Broadcast, preserves authored fields"
            );
            var copiedTransmitter = (FunctionTransmitterState)
                remap.Invoke(
                    null,
                    new object[]
                    {
                        new FunctionTransmitterState { TransmitterId = oldTransmitter, Summary = "keep summary" },
                    }
                );
            check(
                copiedTransmitter.TransmitterId == newTransmitter
                    && copiedTransmitter.StableId == newTransmitter
                    && copiedTransmitter.Summary == "keep summary",
                "schematic copy remaps TransmitterId and preserves Summary"
            );
        }
        finally
        {
            copyIds.SetValue(null, null);
        }

        foreach (
            FunctionPartState state in new FunctionPartState[]
            {
                original,
                new FunctionReturnState { Broadcast = true },
                new FunctionParamState { Name = "Argument" },
                new FunctionCallState { RuntimeJson = "{\"private\":7}" },
            }
        )
        {
            string wireJson = (string)encode.Invoke(null, new object[] { state, false });
            var wireCopy = (FunctionPartState)decode.Invoke(null, new object[] { wireJson, state.GetType(), false });
            check(
                wireCopy.StableId == state.StableId
                    && (!(state is FunctionReturnState) || ((FunctionReturnState)wireCopy).Broadcast)
                    && !wireJson.Contains("RuntimeJson"),
                "public network envelope round-trip preserves identity/broadcast and omits history for "
                    + state.GetType().Name
            );
        }
        FieldInfo wireSchema = assembly
            .GetType("StellarFunctions.NativeStateSerialization", true)
            .GetField("SchemaVersion", BindingFlags.Static | BindingFlags.NonPublic);
        check((byte)wireSchema.GetRawConstantValue() == 4, "native shared part-state wire schema is version 4");
    }

    private static void Reject(Action<bool, string> check, Action action, string message)
    {
        try
        {
            action();
            check(false, message);
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
        {
            check(true, message);
        }
        catch (InvalidDataException)
        {
            check(true, message);
        }
    }
}
