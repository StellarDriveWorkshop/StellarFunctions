// Broadcast checkbox lifecycle regressions against an open native SDK menu.
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using StellarFunctions.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StellarFunctions;

internal static class BroadcastMenuLifecycleChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Editor = typeof(FunctionCallState).Assembly.GetType(
        "StellarFunctions.FunctionEditor",
        true
    );
    private static readonly Type Directory = typeof(FunctionCallState).Assembly.GetType(
        "StellarFunctions.FunctionDirectory",
        true
    );

    internal static void Run(ContentPart part, Action<bool, string> check)
    {
        bool isReturn = part.Part.State is FunctionReturnState;
        if (!isReturn && part.Part.State is not FunctionTransmitterState)
            throw new ArgumentException(
                "Broadcast menu checks require an open Return or Transmitter part.",
                nameof(part)
            );
        if (isReturn)
            part.Part.State = ((FunctionReturnState)part.Part.State) with { Broadcast = false };

        var edits = (IDictionary)AccessTools.Field(Editor, "BroadcastEdits").GetValue(null);
        edits.Clear();
        var begin = AccessTools.Method(Directory, "Begin");
        var accept = AccessTools.Method(Directory, "Accept");
        var sequenceField = AccessTools.Field(Directory, "_sequence");
        string returnId = isReturn
            ? ((FunctionReturnState)part.Part.State).DefinitionId.ToString("N")
            : Guid.NewGuid().ToString("N");

        // The initial view is eligible and private. A successful edit causes a
        // newer descriptor to arrive while the native panel remains open.
        begin.Invoke(null, new object[] { ((FunctionPartState)part.Part.State).StableId });
        string session = (string)AccessTools.Field(Directory, "_session").GetValue(null);
        var initial = Response(part, session, 1, true, returnId, false);
        sequenceField.SetValue(null, 1L);
        accept.Invoke(null, new object[] { initial });
        AccessTools.Method(Editor, "Rebuild").Invoke(null, null);
        Tick();
        Canvas.ForceUpdateCanvases();

        GameObject root = CurrentRoot();
        var toggle = root.GetComponentsInChildren<Toggle>(true).Single(t => t.name == "Broadcast checkbox");
        Check(
            check,
            toggle.interactable && !toggle.isOn,
            "broadcast checkbox starts private and enabled in eligible menu " + part.Def.Id
        );

        AccessTools.Field(typeof(UiRedesignChecks), "Sent").SetValue(null, null);
        toggle.isOn = true;
        var pending = edits[returnId];
        long editSequence = (long)AccessTools.Field(pending.GetType(), "Sequence").GetValue(pending);
        Check(
            check,
            Sent().StartsWith("broadcast:") && !toggle.interactable,
            "first checkbox click sends an action and locks duplicates " + part.Def.Id
        );
        if (isReturn)
            Check(
                check,
                !((FunctionReturnState)part.Part.State).Broadcast,
                "optimistic Return checkbox click leaves part state host-owned"
            );

        long acknowledgedSequence = editSequence + 1;
        sequenceField.SetValue(null, acknowledgedSequence);
        string acknowledgement = Response(part, session, acknowledgedSequence, true, returnId, true);
        Check(
            check,
            (bool)accept.Invoke(null, new object[] { acknowledgement }),
            "menu accepts the fresh broadcast acknowledgement " + part.Def.Id
        );
        GameObject oldRoot = root;
        Tick();
        Canvas.ForceUpdateCanvases();

        // Changed directory data rebuilds the panel while the optimistic edit
        // is pending. The rebuilt label starts disabled; acknowledgement must
        // restore both Toggle.interactable and its label tint in the same tick.
        root = CurrentRoot();
        toggle = root.GetComponentsInChildren<Toggle>(true).Single(t => t.name == "Broadcast checkbox");
        var caption = root.GetComponentsInChildren<TMP_Text>(true).Single(t => t.text == "Broadcast to all ships");
        var status = root.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(t => t.text == "Each Call keeps its own inputs and Memory.");
        var feedback = (TMP_Text)AccessTools.Field(Editor, "_feedback").GetValue(null);
        Check(
            check,
            !ReferenceEquals(oldRoot, root),
            "fresh descriptor rebuilds the open panel while its optimistic edit is pending " + part.Def.Id
        );
        Check(
            check,
            !edits.Contains(returnId) && toggle.isOn && toggle.interactable,
            "fresh acknowledgment clears pending state and re-enables broadcast after rebuild " + part.Def.Id
        );
        Check(
            check,
            caption.color != Color.gray && status != null,
            "rebuilt broadcast label returns to enabled tint with eligible status " + part.Def.Id
        );
        Check(
            check,
            feedback != null && feedback.text.Contains("saved"),
            "successful rebuilt broadcast edit reports saved " + part.Def.Id
        );

        AccessTools.Field(typeof(UiRedesignChecks), "Sent").SetValue(null, null);
        toggle.isOn = false;
        Check(
            check,
            Sent().StartsWith("broadcast:") && !toggle.interactable,
            "enabled broadcast control accepts a second click after acknowledgment " + part.Def.Id
        );
        edits.Clear();

        // Eligibility is independently authoritative and can change while this
        // menu remains open; no Return pickup or reopen should be needed.
        long disabledSequence = acknowledgedSequence + 1;
        sequenceField.SetValue(null, disabledSequence);
        Check(
            check,
            (bool)
                accept.Invoke(null, new object[] { Response(part, session, disabledSequence, false, returnId, true) }),
            "directory accepts an ineligible snapshot while the menu stays open " + part.Def.Id
        );
        Tick();
        Canvas.ForceUpdateCanvases();
        root = CurrentRoot();
        toggle = root.GetComponentsInChildren<Toggle>(true).Single(t => t.name == "Broadcast checkbox");
        string disabledReason = isReturn ? "Add a Function Transmitter to this ship/base to broadcast." : null;
        if (isReturn)
            Check(
                check,
                !toggle.interactable && root.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == disabledReason),
                "ineligible Return snapshot disables the checkbox with an explicit reason"
            );
        else
            Check(
                check,
                !toggle.interactable
                    && root.GetComponentsInChildren<TMP_Text>(true)
                        .Any(t => t.text.Contains("Add a Function Transmitter")),
                "ineligible Transmitter snapshot disables the checkbox with an explicit reason"
            );

        long enabledSequence = disabledSequence + 1;
        sequenceField.SetValue(null, enabledSequence);
        Check(
            check,
            (bool)accept.Invoke(null, new object[] { Response(part, session, enabledSequence, true, returnId, true) }),
            "directory accepts restored eligibility while the menu stays open " + part.Def.Id
        );
        Tick();
        Canvas.ForceUpdateCanvases();
        root = CurrentRoot();
        toggle = root.GetComponentsInChildren<Toggle>(true).Single(t => t.name == "Broadcast checkbox");
        caption = root.GetComponentsInChildren<TMP_Text>(true).Single(t => t.text == "Broadcast to all ships");
        status = root.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(t => t.text == "Each Call keeps its own inputs and Memory.");
        Check(
            check,
            toggle.interactable && caption.color != Color.gray && status != null,
            "restored eligibility re-enables checkbox and label without reopening " + part.Def.Id
        );
        if (isReturn)
            Check(
                check,
                !((FunctionReturnState)part.Part.State).Broadcast,
                "directory eligibility updates do not pick up or mutate Return state"
            );
    }

    private static string Response(
        ContentPart part,
        string session,
        long sequence,
        bool eligible,
        string id,
        bool broadcast
    ) =>
        JsonConvert.SerializeObject(
            new
            {
                Target = ((FunctionPartState)part.Part.State).StableId.ToString("N"),
                Session = session,
                Sequence = sequence,
                Eligible = eligible,
                Truncated = false,
                Entries = new[]
                {
                    new
                    {
                        Id = id,
                        Name = "Lifecycle template",
                        Origin = "Test structure",
                        Status = "TEMPLATE",
                        Revision = new string('c', 64),
                        Parameters = "x=0",
                        Broadcast = broadcast,
                        Published = broadcast,
                        Local = true,
                    },
                },
            }
        );

    private static string Sent() => (string)AccessTools.Field(typeof(UiRedesignChecks), "Sent").GetValue(null) ?? "";

    private static GameObject CurrentRoot() => (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);

    private static void Tick() => AccessTools.Method(Editor, "Tick").Invoke(null, null);

    private static void Check(Action<bool, string> check, bool condition, string message) => check(condition, message);
}
