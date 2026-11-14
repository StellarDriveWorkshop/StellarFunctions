using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Ships.Interface.Model.Parts.Common;
using StellarFunctions.Content;

namespace StellarFunctions;

// Runs while the native SDK configuration menu is open. No server listener or
// private save is needed: replies are injected through the same bounded parser
// used by the registered client broadcast handler.
internal static class TransmitterDirectoryChecks
{
    private static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    internal static void Run(ContentPart part, Action<bool, string> check)
    {
        Type directory = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionDirectory", true);
        MethodInfo begin = directory.GetMethod("Begin", StaticPrivate);
        MethodInfo accept = directory.GetMethod("Accept", StaticPrivate);
        FieldInfo sequence = directory.GetField("_sequence", StaticPrivate);
        FieldInfo sessionField = directory.GetField("_session", StaticPrivate);
        PropertyInfo current = directory.GetProperty("Current", StaticPrivate);
        Guid target = ((FunctionPartState)part.Part.State).StableId;
        begin.Invoke(null, new object[] { target });
        sequence.SetValue(null, 2L);
        string session = (string)sessionField.GetValue(null);
        string id = Guid.NewGuid().ToString("N");
        string json = Response(
            target.ToString("N"),
            session,
            2,
            new[] { Descriptor(id, "Remote Clamp", "Donor Beacon · structure #77") },
            false
        );

        check(
            (bool)accept.Invoke(null, new object[] { json }),
            "directory accepts a bounded reply for the open target and current session"
        );
        object snapshot = current.GetValue(null);
        check(
            snapshot != null && !(bool)Field(snapshot, "Eligible").GetValue(snapshot),
            "directory retains the host eligibility decision"
        );
        object[] entries = ((IEnumerable)Field(snapshot, "Entries").GetValue(snapshot)).Cast<object>().ToArray();
        check(
            entries.Length == 1 && FieldsPresent(entries[0]),
            "directory accepts complete public descriptors without private Call state"
        );

        check(
            !(bool)
                accept.Invoke(
                    null,
                    new object[]
                    {
                        Response(
                            Guid.NewGuid().ToString("N"),
                            session,
                            2,
                            new[] { Descriptor(id, "Remote Clamp", "Structure #77") },
                            false
                        ),
                    }
                ),
            "directory rejects a reply for a different target GUID"
        );
        check(
            !(bool)
                accept.Invoke(
                    null,
                    new object[]
                    {
                        Response(
                            target.ToString("N"),
                            Guid.NewGuid().ToString("N"),
                            2,
                            new[] { Descriptor(id, "Remote Clamp", "Structure #77") },
                            false
                        ),
                    }
                ),
            "directory rejects a reply from a different session"
        );
        check(
            !(bool)
                accept.Invoke(
                    null,
                    new object[]
                    {
                        Response(
                            target.ToString("N"),
                            session,
                            1,
                            new[] { Descriptor(id, "Remote Clamp", "Structure #77") },
                            false
                        ),
                    }
                ),
            "directory rejects an older sequence after a newer reply"
        );
        check(!(bool)accept.Invoke(null, new object[] { "{" }), "directory rejects malformed JSON");
        check(
            !(bool)accept.Invoke(null, new object[] { new string('x', 196609) }),
            "directory rejects an oversized response before parsing"
        );
        var tooMany = Enumerable
            .Range(0, 257)
            .Select(i => Descriptor(Guid.NewGuid().ToString("N"), "Function " + i, "Structure #77"))
            .ToArray();
        check(
            !(bool)accept.Invoke(null, new object[] { Response(target.ToString("N"), session, 2, tooMany, false) }),
            "directory rejects descriptor counts above its 256-entry limit"
        );

        CheckActualChoices(part, id, check);
        Eligibility(check);
    }

    private static void CheckActualChoices(ContentPart part, string id, Action<bool, string> check)
    {
        Type editor = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionEditor", true);
        MethodInfo choices = editor.GetMethod("Choices", StaticPrivate);
        // UiRedesignChecks temporarily replaces Choices with deterministic
        // display fixtures. Remove just that prefix for this assertion, then
        // restore it so the remainder of the menu fixture stays unchanged.
        HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("stellar.functions.private-ui-fixture");
        harmony.Unpatch(choices, HarmonyPatchType.Prefix, "stellar.functions.private-ui-fixture");
        try
        {
            var actual = ((IEnumerable)choices.Invoke(null, new object[] { part })).Cast<object>().ToArray();
            object match = actual.FirstOrDefault(value =>
                (string)value.GetType().GetField("Item1").GetValue(value) == id
            );
            check(
                match != null
                    && ((string)match.GetType().GetField("Item2").GetValue(match)).Contains(
                        "Donor Beacon · structure #77"
                    ),
                "actual Call choices show the published descriptor origin after directory caching"
            );
        }
        finally
        {
            harmony.Patch(choices, prefix: new HarmonyMethod(typeof(UiRedesignChecks), "FixtureChoices"));
        }
    }

    private static void Eligibility(Action<bool, string> check)
    {
        Type runtime = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.NativeFunctionRuntime", true);
        FieldInfo parentsField = runtime.GetField("Parents", StaticPrivate);
        FieldInfo transmittersField = runtime.GetField("TransmitterAssemblies", StaticPrivate);
        var parents = (IDictionary)parentsField.GetValue(null);
        var transmitters = (IEnumerable)transmittersField.GetValue(null);
        DictionaryEntry[] oldParents = parents.Cast<DictionaryEntry>().ToArray();
        uint[] oldTransmitters = transmitters.Cast<uint>().ToArray();
        try
        {
            parents.Clear();
            parents[7702u] = 7701u;
            var set = transmittersField.GetValue(null);
            set.GetType().GetMethod("Clear").Invoke(set, null);
            MethodInfo hasTransmitter = runtime.GetMethod("HasTransmitter", StaticPrivate);
            check(
                !(bool)hasTransmitter.Invoke(null, new object[] { 7702u }),
                "broadcast eligibility is false without a transmitter in the connected assembly"
            );
            set.GetType().GetMethod("Add").Invoke(set, new object[] { 7701u });
            check(
                (bool)hasTransmitter.Invoke(null, new object[] { 7702u }),
                "broadcast eligibility follows the rotor-connected assembly transmitter registry"
            );
        }
        finally
        {
            parents.Clear();
            foreach (DictionaryEntry entry in oldParents)
                parents[entry.Key] = entry.Value;
            var set = transmittersField.GetValue(null);
            set.GetType().GetMethod("Clear").Invoke(set, null);
            foreach (uint value in oldTransmitters)
                set.GetType().GetMethod("Add").Invoke(set, new object[] { value });
        }
    }

    private static string Response(string target, string session, long sequence, object[] entries, bool eligible) =>
        JsonConvert.SerializeObject(
            new
            {
                Target = target,
                Session = session,
                Sequence = sequence,
                Eligible = eligible,
                Truncated = false,
                Entries = entries,
            },
            Formatting.None
        );

    private static object Descriptor(string id, string name, string origin) =>
        new
        {
            Id = id,
            Name = name,
            Origin = origin,
            Status = "TEMPLATE",
            Revision = new string('a', 64),
            Parameters = "x=0",
            Broadcast = true,
            Published = true,
            Local = false,
        };

    private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, InstancePublic);

    private static bool FieldsPresent(object entry) =>
        new[] { "Id", "Name", "Origin", "Status", "Revision", "Parameters" }.All(name =>
            Field(entry, name)?.GetValue(entry) is string text && text.Length > 0
        );
}
