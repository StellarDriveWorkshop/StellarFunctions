using System;
using System.IO;
using System.Reflection;
using FishNet.Serializing;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace StellarFunctions;

public static class CallSizingCodecChecks
{
    public static void Run(Action<bool, string> check)
    {
        var native = AccessTools.TypeByName("Managers.Server.Network.Serialization.StatefulPartSerialization");
        var nativeWrite = AccessTools.Method(native, "WriteState");
        var nativeRead = AccessTools.Method(native, "ReadNewState");
        Type codec = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.FunctionStateCodec", true);
        MethodInfo encode = codec.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo decode = codec.GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic);
        Guid instance = Guid.NewGuid();
        var original = new FunctionCallState
        {
            InstanceId = instance,
            SelectedFunctionId = Guid.NewGuid().ToString("N"),
            Slot0 = Guid.NewGuid().ToString("N"),
            Slot1 = Guid.NewGuid().ToString("N"),
            Slot2 = Guid.NewGuid().ToString("N"),
            RuntimeJson = "{\"history\":7}",
            StructureKey = "kept-structure",
        };

        string legacyJson = (string)encode.Invoke(null, new object[] { original, true });
        JObject legacy = JObject.Parse(legacyJson);
        legacy["schema"] = 1;
        ((JObject)legacy["state"]).Property("InputCount").Remove();
        var missingCount = JObject.Parse(legacyJson);
        ((JObject)missingCount["state"]).Property("InputCount").Remove();
        check((int)missingCount["schema"] == 2, "new Calls use schema2 so old builds refuse the new geometry metadata");
        Reject(
            check,
            () => decode.Invoke(null, new object[] { missingCount.ToString(), typeof(FunctionCallState), true }),
            "schema2 Call refuses a missing size rather than silently expanding"
        );
        var legacyCopy = (FunctionCallState)
            decode.Invoke(null, new object[] { legacy.ToString(), typeof(FunctionCallState), true });
        check(
            legacyCopy.InputCount == 3 && legacyCopy.InstanceId == instance && legacyCopy.StableId == instance,
            "legacy Call state without InputCount loads with three inputs and keeps its identity"
        );

        foreach (int count in new[] { 1, 2, 3 })
        {
            var value = original with
            {
                InputCount = count,
                ResizeFeedback = "Resize saved",
                RuntimeJson = "{\"history\":7}",
            };
            foreach (bool privateData in new[] { false, true })
            {
                string json = (string)encode.Invoke(null, new object[] { value, privateData });
                var copy = (FunctionCallState)
                    decode.Invoke(null, new object[] { json, typeof(FunctionCallState), privateData });
                check(
                    copy.InputCount == count
                        && copy.InstanceId == instance
                        && copy.StableId == instance
                        && copy.SelectedFunctionId == value.SelectedFunctionId
                        && copy.Slot0 == value.Slot0
                        && copy.Slot1 == value.Slot1
                        && copy.Slot2 == value.Slot2
                        && copy.ResizeFeedback == value.ResizeFeedback,
                    $"Call sizing {count} round-trips in {(privateData ? "private" : "public")} state with stable authored fields"
                );
                check(
                    privateData
                        ? copy.RuntimeJson == value.RuntimeJson && copy.StructureKey == value.StructureKey
                        : !json.Contains("RuntimeJson") && !json.Contains("StructureKey"),
                    $"Call sizing {count} preserves private runtime state only in private data"
                );
            }
            var writer = new Writer();
            nativeWrite.Invoke(null, new object[] { writer, value });
            var reader = new Reader(writer.GetArraySegment(), null);
            var received = (FunctionCallState)nativeRead.Invoke(null, new object[] { new FunctionCallState(), reader });
            check(
                received.InputCount == count
                    && received.InstanceId == value.InstanceId
                    && received.Slot0 == value.Slot0
                    && received.RuntimeJson == "{}"
                    && reader.Remaining == 0,
                "native FishNet state writer/reader preserve size " + count + " without leaking private Memory"
            );
        }

        foreach (object invalid in new object[] { "2", 2.0f, true, null, 0, 4, -1 })
        {
            JObject malformed = JObject.Parse(legacyJson);
            malformed["state"]["InputCount"] = invalid == null ? JValue.CreateNull() : JToken.FromObject(invalid);
            Reject(
                check,
                () => decode.Invoke(null, new object[] { malformed.ToString(), typeof(FunctionCallState), true }),
                $"malformed Call InputCount ({(invalid == null ? "null" : invalid.ToString())}) is rejected"
            );
        }

        Reject(
            check,
            () => encode.Invoke(null, new object[] { original with { InputCount = 0 }, true }),
            "Call encode rejects input count below one"
        );
        Reject(
            check,
            () => encode.Invoke(null, new object[] { original with { InputCount = 4 }, true }),
            "Call encode rejects input count above three"
        );
        Reject(
            check,
            () => encode.Invoke(null, new object[] { original with { ResizeFeedback = new string('x', 129) }, true }),
            "Call encode rejects resize feedback over 128 characters"
        );
        Reject(
            check,
            () => encode.Invoke(null, new object[] { original with { ResizeFeedback = "bad\nfeedback" }, true }),
            "Call encode rejects control characters in resize feedback"
        );

        var call = new FunctionCallState { InputCount = 2, ListeningMask = 7 };
        check(call.EmissionSockets.Length == 1 && call.EmissionSockets[0] == 3, "Call output socket remains socket 3");
        for (int count = 1; count <= 3; count++)
        {
            for (int mask = 0; mask <= 7; mask++)
            {
                var sized = call with { InputCount = count, ListeningMask = (byte)mask };
                byte effective = (byte)(
                    mask
                    & (
                        count == 1 ? 1
                        : count == 2 ? 3
                        : 7
                    )
                );
                byte[] expected = new byte[(effective & 1) + ((effective >> 1) & 1) + ((effective >> 2) & 1)];
                int at = 0;
                for (byte socket = 0; socket < 3; socket++)
                {
                    if ((effective & (1 << socket)) != 0)
                    {
                        expected[at++] = socket;
                    }
                }

                byte[] sockets = sized.ListeningSockets;
                bool same = ReferenceEquals(sockets, sized.ListeningSockets);
                check(
                    same && sockets.Length == expected.Length,
                    $"Call listening mask {mask} with {count} inputs reuses its precomputed array and hides excess sockets"
                );
                for (int i = 0; i < expected.Length; i++)
                {
                    check(
                        sockets[i] == expected[i],
                        $"Call listening mask {mask} with {count} inputs exposes only active socket {expected[i]}"
                    );
                }
            }
        }
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
