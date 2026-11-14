// Asset-only native Unity fixture. No world, network listener or private save.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Core.Events;
using Core.Services;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Parts.Common.Model.Reactions;
using Ships.Visuals;
using StellarFunctions.Content;
using StellarFunctions.Game;
using TMPro;
using UI.Common;
using UI.Inventory;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StellarFunctions;

internal static class UiRedesignChecks
{
    private static readonly Type Editor = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.FunctionEditor");
    private static Action<bool, string> Check;
    private static string Output;
    private static string Sent;
    private static readonly string ChoiceId = "11111111111111111111111111111111";
    private static readonly string OtherId = "22222222222222222222222222222222";
    private static readonly HarmonyLib.Harmony UiHarmony = new HarmonyLib.Harmony(
        "stellar.functions.private-ui-fixture"
    );

    private static bool FixtureChoices(ref (string Id, string Name)[] __result)
    {
        __result = new[] { (ChoiceId, "Clamp"), (OtherId, new string('W', 64)) };
        return false;
    }

    private static bool CaptureAction(ContentPart part, string action, ref bool __result)
    {
        Sent = action;
        __result = true;
        return false;
    }

    private static bool FunctionInteraction;

    private static bool FixtureInteraction(ref bool __result)
    {
        __result = FunctionInteraction;
        return false;
    }

    internal static void Run(string output, Action<bool, string> check)
    {
        Output = output;
        Check = check;
        UiHarmony.Patch(
            AccessTools.Method(Editor, "Choices"),
            prefix: new HarmonyMethod(typeof(UiRedesignChecks), nameof(FixtureChoices))
        );
        UiHarmony.Patch(
            AccessTools.Method(typeof(ContentActions), "Send", new[] { typeof(ContentPart), typeof(string) }),
            prefix: new HarmonyMethod(typeof(UiRedesignChecks), nameof(CaptureAction))
        );

        InventoryLifecycle();
        Persistence();
        foreach (ushort id in new ushort[] { 62005, 62006, 62007 })
        {
            var settings = Support.Registration.Get(id);
            var sockets = settings.part.GetComponentsInChildren<SocketObject>(true).OrderBy(s => s.Id).ToArray();
            Check(sockets.Length == (id == 62005 ? 4 : 1), "stable socket counts for " + id);
            Check(
                sockets.All(s => s.transform.localScale == Vector3.one),
                "socket meshes retain native unit scale for " + id
            );
            Check(
                sockets.All(s => Math.Abs(s.transform.localPosition.y - .07f) < .0001f),
                "native socket height for " + id
            );
            Check(
                sockets.All(s => s.visualObject.transform.Find("SDModKitSocketPad") == null),
                "generic kit stand-offs do not change native socket seats for " + id
            );
            if (id == 62005)
            {
                Check(
                    sockets.Select(s => s.Id).SequenceEqual(new byte[] { 0, 1, 2, 3 }),
                    "Call inputs 0 1 2 and output 3 unchanged"
                );
                Check(
                    Math.Abs(sockets[0].transform.localPosition.x + .125f) < .0001f
                        && Math.Abs(sockets[1].transform.localPosition.x - .125f) < .0001f
                        && Math.Abs(sockets[2].transform.localPosition.x - .375f) < .0001f,
                    "all three inputs use native quarter-metre grid phase"
                );
                Check(
                    sockets.Take(3).All(s => Math.Abs(s.transform.localPosition.z + .125f) < .0001f)
                        && Math.Abs(sockets[3].transform.localPosition.z - .125f) < .0001f,
                    "native front and back anchor planes unchanged by finishing"
                );
            }
            var donor = Resources
                .FindObjectsOfTypeAll<Ships.Interface.Settings.PartSettings>()
                .First(p => p.id == (id == 62005 ? 45 : 82));
            Check(settings.size == donor.size, "native snapping phase and orientation dimensions for " + id);
            var original = donor
                .part.transform.Find(id == 62005 ? "Visuals/MathGate" : "Visuals/SignalDisplay")
                .GetComponent<MeshFilter>();
            var body = settings
                .part.GetComponentsInChildren<MeshFilter>(true)
                .Single(m => m.sharedMesh == original.sharedMesh);
            Check(
                body.GetComponent<MeshRenderer>()
                    .sharedMaterials.SequenceEqual(original.GetComponent<MeshRenderer>().sharedMaterials),
                "native textured housing materials for " + id
            );
            Check(
                body.transform.localScale == new Vector3(id == 62005 ? 1.6f : 1, 1, 1),
                "only housing is lengthened for " + id
            );
            RenderPart(settings.part, id);
        }
        var transmitter = Support.Registration.Get(62009);
        Check(
            PartStateFactory.BuildDefaultState(transmitter) is FunctionTransmitterState,
            "native state factory constructs transmitter"
        );
        Check(
            transmitter.part.GetComponentsInChildren<SocketObject>(true).Length == 0,
            "transmitter has zero signal sockets"
        );
        Check(
            !transmitter
                .part.GetComponentsInChildren<MeshFilter>(true)
                .Any(m => m.sharedMesh != null && m.sharedMesh.name.Contains("SignalSocket")),
            "transmitter has no visible signal connector meshes"
        );
        var recipe = ContentRegistry.PartDef(62009).Cost;
        Check(
            recipe.Count == 3 && recipe[101] == 1 && recipe[112] == 1 && recipe[103] == 1,
            "transmitter recipe is one Iron, Copper and Stellar Shard"
        );
        Check(
            (
                (System.Collections.ICollection)
                    AccessTools
                        .Field(typeof(Ships.Interface.Settings.PartSettings), "buildingCost")
                        .GetValue(transmitter)
            ).Count == 3,
            "native recipe retains all three resource entries"
        );
        RenderPart(transmitter.part, 62009);
        RenderCallFaces();
        // Locate the real serialized native inventory frame while scene activation
        // stays disabled. SDK clones this same frame in the running client.
        var grids = Resources.FindObjectsOfTypeAll<InventoryGrid>();
        File.WriteAllLines(
            Path.Combine(Output, "inventory-donors.txt"),
            grids.Select(g => g.name + " / " + g.transform.root.name)
        );
        var grid = grids.FirstOrDefault(g => MenuLayout.Resolve(g.gameObject).Valid);
        if (grid == null)
        {
            throw new Exception("Native inventory frame was not loaded; cannot qualify UI using a mock frame.");
        }

        var eventObject = new GameObject("Private events", typeof(EventSystem));
        foreach (ushort id in new ushort[] { 62005, 62006, 62007, 62009 })
        {
            Menu(grid.transform.root.gameObject, id);
        }

        UnityEngine.Object.DestroyImmediate(eventObject);
        UiHarmony.UnpatchSelf();
    }

    private static void Persistence()
    {
        var original = new FunctionCallState
        {
            InstanceId = new Guid("33333333333333333333333333333333"),
            SelectedFunctionId = ChoiceId,
            SelectedName = "Clamp",
            Slot0 = OtherId,
            Slot1 = "",
            Slot2 = "",
            ListeningMask = 1,
            RuntimeJson = "{\"memory-test\":2.5}",
            StructureKey = "example-structure",
            Output = 2.5,
        };
        var codec = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.FunctionStateCodec");
        string encoded = (string)AccessTools.Method(codec, "Encode").Invoke(null, new object[] { original, true });
        var decoded = (FunctionCallState)
            AccessTools.Method(codec, "Decode").Invoke(null, new object[] { encoded, typeof(FunctionCallState), true });
        Check(
            decoded == original,
            "existing Call save envelope retains identity, mapping and private Memory unchanged"
        );
    }

    private static void InventoryLifecycle()
    {
        // Control only the predicate because this asset-only process has no
        // authenticated player/world. All tracking and inventory methods are native.
        var interaction = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.ClientCallInteraction");
        UiHarmony.Patch(
            AccessTools.Method(interaction, "IsFunctionInteraction"),
            prefix: new HarmonyMethod(typeof(UiRedesignChecks), nameof(FixtureInteraction))
        );
        var tracker = ServiceLocator.GetService<MenuTracker>();
        GameObject services = null;
        if (tracker == null)
        {
            services = new GameObject("Private menu service");
            services.tag = "ServiceObject";
            tracker = services.AddComponent<MenuTracker>();
            AccessTools
                .Field(typeof(MenuTracker), "menuOpenedEvent")
                .SetValue(tracker, ScriptableObject.CreateInstance<GameEvent>());
            AccessTools
                .Field(typeof(MenuTracker), "menuClosedEvent")
                .SetValue(tracker, ScriptableObject.CreateInstance<GameEvent>());
        }
        var owner = new GameObject("Private inventory activator");
        owner.SetActive(false);
        var inventory = owner.AddComponent<PlayerInventoryActivator>();
        var frame = new GameObject("Private inventory lifecycle frame", typeof(RectTransform));
        frame.SetActive(false);
        AccessTools.Field(typeof(PlayerInventoryActivator), "inventoryCanvasPrefab").SetValue(inventory, frame);
        FunctionInteraction = false;
        inventory.ToggleInventory();
        Check(
            inventory.IsInventoryActive && tracker.HasClosableMenuOpen(),
            "normal native inventory opens and is tracked"
        );
        FunctionInteraction = true;
        inventory.Close();
        Check(!inventory.IsInventoryActive, "native inventory can close while Function interaction is active");
        inventory.ToggleInventory();
        Check(!inventory.IsInventoryActive, "Function interaction blocks the native inventory open transition");
        FunctionInteraction = false;
        inventory.ToggleInventory();
        Check(inventory.IsInventoryActive, "normal inventory opens again after leaving Function interaction");
        inventory.Close();
        Check(!tracker.HasClosableMenuOpen(), "normal inventory close leaves no tracked menu behind");
        UnityEngine.Object.DestroyImmediate(owner);
        UnityEngine.Object.DestroyImmediate(frame);
        if (services != null)
        {
            UnityEngine.Object.DestroyImmediate(services);
        }
    }

    private static void Menu(GameObject donor, ushort id)
    {
        var canvasObject = new GameObject("Private function fixture root", typeof(RectTransform));
        var clone = UnityEngine.Object.Instantiate(donor, canvasObject.transform);
        clone.SetActive(true);
        var canvas = clone.GetComponent<Canvas>() ?? clone.AddComponent<Canvas>();
        canvas.enabled = true;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scale = clone.GetComponent<CanvasScaler>() ?? clone.AddComponent<CanvasScaler>();
        scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scale.referenceResolution = new Vector2(1920, 1080);
        scale.matchWidthOrHeight = .5f;
        if (clone.GetComponent<GraphicRaycaster>() == null)
        {
            clone.AddComponent<GraphicRaycaster>();
        }
        // Asset scripts normally need live services. The menu host consumes only the
        // frame here, so remove InventoryGrid before enabling its native Start.
        foreach (var component in clone.GetComponentsInChildren<InventoryGrid>(true))
        {
            UnityEngine.Object.DestroyImmediate(component);
        }

        var settings = Support.Registration.Get(id);
        var part = new StatefulPart(
            1,
            settings,
            new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
        );
        part.State =
            id == 62005
                ? new FunctionCallState
                {
                    SelectedFunctionId = ChoiceId,
                    SelectedName = "Clamp",
                    Input0Label = "[1] x · default 0",
                    Input1Label = "[2] hi · default 1",
                    Input2Label = "[3] lo · default -1",
                    Status = "OK",
                }
            : id == 62006
                ? new FunctionParamState
                {
                    Name = "Target velocity",
                    Index = 1,
                    DefaultValue = 1,
                }
            : id == 62009 ? new FunctionTransmitterState()
            : new FunctionReturnState { Name = "Clamp", Status = "TEMPLATE" };
        var content = new ContentPart
        {
            ShipId = 1,
            PartId = 1,
            Part = part,
            Def = ContentRegistry.PartDef(id),
        };
        PartBehaviour behaviour =
            id == 62005 ? new CallPartBehaviour()
            : id == 62009 ? new TransmitterPartBehaviour()
            : new DefinitionPartBehaviour();
        bool opened = TryAppendClient(clone, content, behaviour);
        Check(opened, "real client frame opens for " + id);
        if (id == 62007 || id == 62009)
        {
            var immediateRoot = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
            var side = (GameObject)AccessTools.Field(typeof(ContentConfigMenu), "_side").GetValue(null);
            var menuRoot = ContentConfigMenu.Root.transform;
            bool donorsHidden = menuRoot
                .Cast<Transform>()
                .Where(t => t.gameObject != side)
                .All(t => !t.gameObject.activeSelf);
            bool controlsLive =
                immediateRoot != null
                && (
                    id == 62007
                        ? immediateRoot
                            .GetComponentsInChildren<TMP_InputField>(true)
                            .Any(i => i.name == "Name" && i.gameObject.activeInHierarchy)
                        : immediateRoot
                            .GetComponentsInChildren<TMP_Text>(true)
                            .Any(t => t.text == "Function Transmitter" && t.gameObject.activeInHierarchy)
                );
            Check(
                immediateRoot != null && donorsHidden && controlsLive,
                "client append builds the live Return/Transmitter controls on first open and hides donor UI " + id
            );
        }
        AccessTools.Method(Editor, "Tick").Invoke(null, null);
        Canvas.ForceUpdateCanvases();
        GameObject root = null;
        if (id == 62007 || id == 62009)
        {
            var directory = typeof(FunctionReturnState).Assembly.GetType("StellarFunctions.FunctionDirectory");
            string session = (string)AccessTools.Field(directory, "_session").GetValue(null);
            var returnId = id == 62007 ? ((FunctionReturnState)part.State).DefinitionId : Guid.NewGuid();
            string snapshot = Newtonsoft.Json.JsonConvert.SerializeObject(
                new
                {
                    Target = ((FunctionPartState)part.State).StableId.ToString("N"),
                    Session = session,
                    Sequence = 1,
                    Eligible = false,
                    Truncated = false,
                    Entries = new[]
                    {
                        new
                        {
                            Id = returnId.ToString("N"),
                            Name = "Clamp",
                            Origin = "Zeus · structure #1",
                            Status = "TEMPLATE",
                            Revision = new string('a', 64),
                            Parameters = "x=0, hi=1, lo=-1",
                            Broadcast = false,
                            Published = false,
                            Local = true,
                        },
                    },
                }
            );
            AccessTools.Field(directory, "_sequence").SetValue(null, 1L);
            Check(
                (bool)AccessTools.Method(directory, "Accept").Invoke(null, new object[] { snapshot }),
                "menu accepts host public descriptor for " + id
            );
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Canvas.ForceUpdateCanvases();
            var disabledRoot = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
            Check(
                !disabledRoot.GetComponentInChildren<Toggle>().interactable,
                "broadcast checkbox disabled without transmitter for " + id
            );
            Check(
                disabledRoot
                    .GetComponentsInChildren<TMP_Text>()
                    .Any(t => t.text.Contains("Add a Function Transmitter")),
                "disabled broadcast explanation is visible for " + id
            );
            RenderCanvas(canvas, id + "-broadcast-disabled", 1920, 1080);
            snapshot = snapshot.Replace("\"Eligible\":false", "\"Eligible\":true");
            Check(
                (bool)AccessTools.Method(directory, "Accept").Invoke(null, new object[] { snapshot }),
                "menu accepts eligible update for " + id
            );
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Canvas.ForceUpdateCanvases();
            var eligibleRoot = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
            root = eligibleRoot;
            var toggle = eligibleRoot.GetComponentInChildren<Toggle>();
            Check(
                toggle.interactable && !toggle.isOn,
                "broadcast checkbox starts off and enables with a transmitter for " + id
            );
            Sent = null;
            toggle.isOn = true;
            Check(
                Sent != null && Sent.StartsWith("broadcast:") && Sent.Contains(returnId.ToString("N")),
                "broadcast checkbox sends Return identity from " + id
            );
            bool authoritativeBroadcast =
                id == 62007 ? ((FunctionReturnState)part.State).Broadcast : CachedBroadcast(returnId.ToString("N"));
            Check(
                toggle.isOn && !authoritativeBroadcast,
                "broadcast checkbox responds immediately while authoritative state stays host-owned for " + id
            );
            Check(
                !toggle.interactable,
                "broadcast toggle disables duplicate edits while the server decision is pending for " + id
            );
            var edits = (System.Collections.IDictionary)AccessTools.Field(Editor, "BroadcastEdits").GetValue(null);
            object pendingEdit = edits[returnId.ToString("N")];
            AccessTools.Field(pendingEdit.GetType(), "At").SetValue(pendingEdit, Time.unscaledTime - 6f);
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Canvas.ForceUpdateCanvases();
            authoritativeBroadcast =
                id == 62007 ? ((FunctionReturnState)part.State).Broadcast : CachedBroadcast(returnId.ToString("N"));
            Check(
                !toggle.isOn && toggle.interactable && !authoritativeBroadcast,
                "rejected broadcast edit rolls back to the last authoritative value after timeout for " + id
            );
            var notice = (TMP_Text)AccessTools.Field(Editor, "_feedback").GetValue(null);
            Check(
                notice != null && notice.text.Contains("not accepted"),
                "broadcast rollback explains that the server rejected the edit for " + id
            );

            // A newer matching snapshot acknowledges the optimistic value before
            // timeout. This proves the pending UI is reconciled from authority.
            Sent = null;
            toggle.isOn = true;
            pendingEdit = edits[returnId.ToString("N")];
            Check(
                pendingEdit != null && toggle.isOn && !toggle.interactable,
                "a second broadcast edit stays optimistic while awaiting confirmation for " + id
            );
            if (id == 62007)
            {
                long editSequence = (long)AccessTools.Field(pendingEdit.GetType(), "Sequence").GetValue(pendingEdit);
                long acknowledgedSequence = editSequence + 1;
                AccessTools.Field(directory, "_sequence").SetValue(null, acknowledgedSequence);
                var acknowledged = new
                {
                    Target = ((FunctionPartState)part.State).StableId.ToString("N"),
                    Session = session,
                    Sequence = acknowledgedSequence,
                    Eligible = true,
                    Truncated = false,
                    Entries = new[]
                    {
                        new
                        {
                            Id = returnId.ToString("N"),
                            Name = "Clamp",
                            Origin = "Zeus · structure #1",
                            Status = "TEMPLATE",
                            Revision = new string('a', 64),
                            Parameters = "x=0, hi=1, lo=-1",
                            Broadcast = true,
                            Published = true,
                            Local = true,
                        },
                    },
                };
                Check(
                    (bool)
                        AccessTools
                            .Method(directory, "Accept")
                            .Invoke(null, new object[] { Newtonsoft.Json.JsonConvert.SerializeObject(acknowledged) }),
                    "directory accepts a fresh authoritative broadcast acknowledgement"
                );
                part.State = ((FunctionReturnState)part.State) with { Broadcast = true };
                AccessTools.Method(Editor, "Tick").Invoke(null, null);
                Canvas.ForceUpdateCanvases();
                root = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
                toggle = root.GetComponentInChildren<Toggle>();
                Check(
                    !edits.Contains(returnId.ToString("N")) && toggle.isOn && toggle.interactable,
                    "fresh matching snapshot clears pending broadcast edit before timeout"
                );
                notice = (TMP_Text)AccessTools.Field(Editor, "_feedback").GetValue(null);
                Check(
                    notice != null && notice.text.Contains("saved"),
                    "fresh broadcast acknowledgement gives immediate saved feedback"
                );

                // Now make another edit, drop the cache as a real session reset
                // can, and exercise the timeout rollback to the saved prior value.
                Sent = null;
                toggle.isOn = false;
                pendingEdit = edits[returnId.ToString("N")];
                Check(
                    toggle.isOn == false && !toggle.interactable && ((FunctionReturnState)part.State).Broadcast,
                    "broadcast edit optimistically turns off without mutating authoritative Return state"
                );
                var currentProperty = directory.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic);
                object savedSnapshot = currentProperty.GetValue(null);
                var draftField = root.GetComponentInChildren<TMP_InputField>();
                if (draftField != null && EventSystem.current != null)
                {
                    EventSystem.current.SetSelectedGameObject(draftField.gameObject);
                }

                currentProperty.GetSetMethod(true).Invoke(null, new object[] { null });
                AccessTools.Field(pendingEdit.GetType(), "At").SetValue(pendingEdit, Time.unscaledTime - 6f);
                AccessTools.Method(Editor, "Tick").Invoke(null, null);
                Canvas.ForceUpdateCanvases();
                root = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
                toggle = root.GetComponentInChildren<Toggle>();
                notice = (TMP_Text)AccessTools.Field(Editor, "_feedback").GetValue(null);
                var waiting = root.GetComponentsInChildren<TMP_Text>(true)
                    .Any(t => t.text.Contains("waiting for a fresh directory"));
                bool restoredMissingCache =
                    !edits.Contains(returnId.ToString("N"))
                    && toggle.isOn
                    && !toggle.interactable
                    && ((FunctionReturnState)part.State).Broadcast;
                Check(
                    restoredMissingCache,
                    "missing-cache broadcast timeout restores the previous value and clears pending edit (pending="
                        + edits.Contains(returnId.ToString("N"))
                        + ", toggle="
                        + toggle.isOn
                        + ", enabled="
                        + toggle.interactable
                        + ", authoritative="
                        + ((FunctionReturnState)part.State).Broadcast
                        + ")"
                );
                Check(
                    waiting && notice != null && notice.text.Contains("not accepted"),
                    "missing-cache rollback explains the rejection and waits for a fresh directory"
                );
                currentProperty.GetSetMethod(true).Invoke(null, new[] { savedSnapshot });
                AccessTools.Method(Editor, "Tick").Invoke(null, null);
                Canvas.ForceUpdateCanvases();
                root = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
                toggle = root.GetComponentInChildren<Toggle>();
                Check(
                    toggle.isOn && toggle.interactable,
                    "restoring an authoritative directory re-enables the broadcast control"
                );
                if (EventSystem.current != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            }
        }
        root = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
        Check(root != null, "custom editor builds in native frame for " + id);
        var rect = (RectTransform)root.transform;
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        Canvas.ForceUpdateCanvases();
        Check(
            root.GetComponentsInChildren<TMP_Text>(true).All(t => !t.text.Contains("Press F while looking")),
            "obsolete F-cycle instructions absent for " + id
        );
        Check(
            root.GetComponentsInChildren<TMP_InputField>(true).Length
                == (
                    id == 62005 || id == 62009 ? 0
                    : id == 62006 ? 2
                    : 1
                ),
            "expected Call/Param/Return editable fields for " + id
        );
        var children = root
            .transform.Cast<Transform>()
            .Where(t => t.gameObject.activeSelf)
            .OfType<RectTransform>()
            .ToArray();
        for (int i = 1; i < children.Length; i++)
        {
            var a = new Vector3[4];
            var b = new Vector3[4];
            children[i - 1].GetWorldCorners(a);
            children[i].GetWorldCorners(b);
            Check(b[1].y <= a[0].y + .1f, "nonoverlapping native editor rows " + id + " / " + i);
        }
        foreach (
            var size in new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440) }
        )
        {
            RenderCanvas(canvas, id + "-" + size.x, size.x, size.y);
        }

        if (id == 62005)
        {
            TransmitterDirectoryChecks.Run(content, Check);
            var dropdown = root.GetComponentInChildren<TMP_Dropdown>();
            Check(
                dropdown != null && root.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Socket diagram"),
                "Call has actual dropdown and three-input diagram"
            );
            Sent = null;
            AccessTools.Method(typeof(TMP_Dropdown), "Start").Invoke(dropdown, null);
            dropdown.RefreshShownValue();
            dropdown.Show();
            Canvas.ForceUpdateCanvases();
            foreach (var fade in canvas.GetComponentsInChildren<CanvasGroup>())
            {
                fade.alpha = 1; // capture the completed open animation in this same-frame fixture
            }

            Check(dropdown.IsExpanded, "native dropdown expands into selectable options");
            File.WriteAllLines(
                Path.Combine(Output, "dropdown-tree.txt"),
                canvas
                    .GetComponentsInChildren<RectTransform>(true)
                    .Where(r =>
                        r.name == "Dropdown List"
                        || r.name == "Template"
                        || r.GetComponent<Toggle>() != null
                        || r.GetComponent<Canvas>() != null
                        || r.GetComponent<CanvasGroup>() != null
                    )
                    .Select(r =>
                        r.name
                        + " active="
                        + r.gameObject.activeInHierarchy
                        + " rect="
                        + r.rect
                        + " pos="
                        + r.position
                        + " parent="
                        + r.parent?.name
                        + " alpha="
                        + r.GetComponent<CanvasGroup>()?.alpha
                        + " canvas="
                        + r.GetComponent<Canvas>()?.renderMode
                        + " sort="
                        + r.GetComponent<Canvas>()?.sortingOrder
                        + " layer="
                        + r.GetComponent<Canvas>()?.sortingLayerID
                    )
            );
            RenderCanvas(canvas, "call-dropdown", 1920, 1080);
            dropdown.Hide();
            AccessTools.Method(typeof(TMP_Dropdown), "ImmediateDestroyDropdownList").Invoke(dropdown, null);
            Check(
                ((FunctionCallState)part.State).SelectedFunctionId == ChoiceId && Sent == null,
                "merely opening menus/dropdown sends no action and leaves selection unchanged"
            );
            dropdown.value = 2;
            Check(Sent == "select-function:" + OtherId, "actual dropdown callback sends the chosen stable identity");
            Check(
                ((FunctionCallState)part.State).SelectedFunctionId == ChoiceId,
                "client callback never mutates authoritative selection locally"
            );
            part.State = ((FunctionCallState)part.State) with
            {
                PendingFunctionId = OtherId,
                PendingFunctionName = new string('W', 64),
                Status = "CONFIRM REBIND IN CONFIG",
            };
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Canvas.ForceUpdateCanvases();
            root = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
            var buttons = root.GetComponentsInChildren<Button>();
            Check(
                buttons.Any(b => b.name == "Confirm function change")
                    && buttons.Any(b => b.name == "Cancel function change"),
                "wired change presents explicit confirm and cancel controls"
            );
            Sent = null;
            buttons.Single(b => b.name == "Cancel function change").onClick.Invoke();
            Check(Sent == "cancel-rebind", "actual cancel callback sends cancellation only");
            buttons.Single(b => b.name == "Confirm function change").onClick.Invoke();
            Check(Sent == "confirm-rebind:" + OtherId, "actual confirmation callback names the exact pending identity");
            RenderCanvas(canvas, "call-confirmation-long-name", 1280, 720);
            part.State = ((FunctionCallState)part.State) with
            {
                PendingFunctionId = "",
                PendingFunctionName = "",
                ListeningMask = 1,
                Input1Label = "Unused",
                Input2Label = "Unused",
                Status = "OK",
            };
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Canvas.ForceUpdateCanvases();
            RenderCanvas(canvas, "call-inactive-inputs", 1920, 1080);
            ResponsiveEditorChecks.Run(content, Check);
            CallSizingUiChecks(content);
        }
        if (id == 62007)
        {
            part.State = ((FunctionReturnState)part.State) with { Broadcast = true };
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Check(
                ((FunctionReturnState)AccessTools.Field(Editor, "_original").GetValue(null)).Broadcast,
                "late replicated broadcast update keeps an existing Return draft revision current"
            );
            part.State = ((FunctionReturnState)part.State) with { Status = "DUPLICATE NAME" };
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Check(
                root.GetComponentsInChildren<TMP_Text>().Any(t => t.text == "Status: DUPLICATE NAME"),
                "Return compiler errors stay visible and update without reopening"
            );
            ResponsiveEditorChecks.Run(content, Check);
        }
        if (id == 62007 || id == 62009)
        {
            BroadcastMenuLifecycleChecks.Run(content, Check);
        }

        AccessTools.Method(Editor, "Reset").Invoke(null, null);
        ContentConfigMenu.Forget();
        UnityEngine.Object.DestroyImmediate(canvasObject);
    }

    private static void CallSizingUiChecks(ContentPart content)
    {
        var directory = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.FunctionDirectory", true);
        var current = directory.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic);
        var setter = current.GetSetMethod(true);
        void Refresh(bool rebuild = false)
        {
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            var existing = (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
            foreach (
                var dropdown in existing == null
                    ? Array.Empty<TMP_Dropdown>()
                    : existing.GetComponentsInChildren<TMP_Dropdown>(true)
            )
            {
                dropdown.Hide();
            }

            if (rebuild)
            {
                AccessTools.Field(Editor, "_callRevision").SetValue(null, "");
                AccessTools.Method(Editor, "Rebuild").Invoke(null, null);
            }
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            Canvas.ForceUpdateCanvases();
        }
        GameObject CallRoot() => (GameObject)AccessTools.Field(Editor, "_root").GetValue(null);
        Button CountButton(int count) =>
            CallRoot().GetComponentsInChildren<Button>(true).Single(b => b.gameObject.name == "Inputs " + count);
        FunctionCallState State() => (FunctionCallState)content.Part.State;
        void SetCount(int count) => content.Part.State = State() with { InputCount = count, ResizeFeedback = "" };
        void SetDirectory(bool? wired)
        {
            if (wired == null)
            {
                setter.Invoke(null, new object[] { null });
                return;
            }
            Type snapshotType = directory.GetNestedType("Snapshot", BindingFlags.Public | BindingFlags.NonPublic);
            object snapshot = Activator.CreateInstance(snapshotType, true);
            snapshotType.GetField("Target").SetValue(snapshot, State().StableId.ToString("N"));
            snapshotType.GetField("Session").SetValue(snapshot, "private-ui-fixture");
            snapshotType.GetField("Sequence").SetValue(snapshot, 1L);
            snapshotType.GetField("Eligible").SetValue(snapshot, true);
            snapshotType.GetField("CallWired").SetValue(snapshot, wired.Value);
            Type entryType = snapshotType.GetField("Entries").FieldType.GetElementType();
            snapshotType.GetField("Entries").SetValue(snapshot, Array.CreateInstance(entryType, 0));
            setter.Invoke(null, new[] { snapshot });
        }
        void CheckPreview(int count)
        {
            var root = CallRoot();
            int terminals = root.GetComponentsInChildren<Transform>(true)
                .Count(t => t.name.StartsWith("Input ") && t.name.EndsWith(" socket"));
            Check(terminals == count, "Call preview shows exactly " + count + " active input terminals");
            var diagram = root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Socket diagram");
            var texts = diagram.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).ToArray();
            Check(
                texts.Count(t => t.StartsWith("Input ")) == count && texts.Contains("Output"),
                "Call preview labels " + count + " inputs and the unchanged output"
            );
            RenderCanvas((Canvas)root.GetComponentInParent<Canvas>(), "call-size-" + count, 1920, 1080);
        }

        Sent = null;
        AccessTools.Method(Editor, "Reset").Invoke(null, null);
        SetCount(3);
        SetDirectory(null);
        Refresh();
        Check(
            Sent == null || Sent.StartsWith("directory:"),
            "opening and rebuilding the Call sizing menu sends no mutation action"
        );
        Check(
            Enumerable.Range(1, 3).All(n => !CountButton(n).interactable),
            "input count controls remain disabled while the function directory is absent"
        );
        SetDirectory(false);
        Refresh(true);
        Check(
            Enumerable.Range(1, 3).All(n => CountButton(n).interactable),
            "directory snapshot unlocks the 1/2/3 input selector when the Call is unwired"
        );
        Check(
            Enumerable
                .Range(1, 3)
                .All(n => CallRoot().GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == n.ToString())),
            "input selector exposes all three count choices"
        );
        Check(
            CallRoot()
                .GetComponentsInChildren<TMP_Text>(true)
                .Any(t => t.text.Contains("Hidden inputs use parameter defaults")),
            "Call sizing explains how hidden arguments are supplied"
        );
        CountButton(1).onClick.Invoke();
        Check(
            Sent == "resize-call:" + State().InstanceId.ToString("N") + ":3:1",
            "explicit count selection sends only the instance-scoped resize action"
        );
        Check(State().InputCount == 3, "client resize request leaves authoritative input count unchanged");
        Refresh();
        Check(
            Enumerable.Range(1, 3).All(n => !CountButton(n).interactable),
            "input controls disable while the resize awaits authoritative state"
        );
        SetCount(1);
        Refresh();
        CheckPreview(1);
        SetCount(2);
        Refresh();
        CheckPreview(2);
        SetCount(3);
        Refresh();
        CheckPreview(3);

        Sent = null;
        CountButton(1).onClick.Invoke();
        Refresh();
        content.Part.State = State() with { ResizeFeedback = "Disconnect all terminals before resizing." };
        Refresh();
        Check(
            State().InputCount == 3
                && CallRoot()
                    .GetComponentsInChildren<TMP_Text>(true)
                    .Any(t => t.text == "Disconnect all terminals before resizing."),
            "resize rejection keeps the prior count and displays server feedback"
        );
        Check(CountButton(3).interactable, "rejected resize clears the pending lock");
        CountButton(1).onClick.Invoke();
        Refresh();
        Check(!CountButton(1).interactable, "retry of a rejected count remains pending until confirmed or timed out");
        AccessTools.Field(Editor, "_inputCountRequestedAt").SetValue(null, Time.unscaledTime - 6f);
        Refresh();
        Check(
            CountButton(1).interactable && AccessTools.Field(Editor, "_requestedInputCount").GetValue(null).Equals(0),
            "unchanged repeated server feedback cannot leave resize controls stuck beyond the five-second timeout"
        );
        Check(State().InputCount == 3, "resize timeout keeps the server-confirmed three-input preview");
        SetDirectory(true);
        Refresh(true);
        Check(
            Enumerable.Range(1, 3).All(n => !CountButton(n).interactable),
            "any wired terminal including output keeps resizing disabled"
        );
        Check(
            CallRoot()
                .GetComponentsInChildren<TMP_Text>(true)
                .Any(t => t.text == "Disconnect all terminals to change input count."),
            "wired Call shows the disconnect-all-terminals reason"
        );
        SetDirectory(false);
        Refresh(true);
    }

    private static bool TryAppendClient(GameObject donor, ContentPart content, PartBehaviour behaviour)
    {
        var method = AccessTools.Method(typeof(ContentConfigMenu), "TryAppendClient");
        if (method == null)
        {
            throw new MissingMethodException(typeof(ContentConfigMenu).FullName, "TryAppendClient");
        }

        var arguments = method
            .GetParameters()
            .Select(parameter =>
            {
                Type type = parameter.ParameterType;
                if (type == typeof(GameObject))
                {
                    return (object)donor;
                }

                if (type == typeof(Transform))
                {
                    return donor.transform;
                }

                if (type == typeof(ContentPart))
                {
                    return content;
                }

                if (typeof(PartBehaviour).IsAssignableFrom(type))
                {
                    return behaviour;
                }

                if (type == typeof(bool))
                {
                    return true;
                }

                if (parameter.HasDefaultValue)
                {
                    return parameter.DefaultValue;
                }

                return type.IsValueType ? Activator.CreateInstance(type) : null;
            })
            .ToArray();
        return (bool)method.Invoke(null, arguments);
    }

    private static bool CachedBroadcast(string id)
    {
        var directory = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.FunctionDirectory", true);
        var snapshot = directory.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var entries = (System.Collections.IEnumerable)snapshot.GetType().GetField("Entries").GetValue(snapshot);
        object entry = entries.Cast<object>().Single(e => (string)e.GetType().GetField("Id").GetValue(e) == id);
        return (bool)entry.GetType().GetField("Broadcast").GetValue(entry);
    }

    private static void RenderCanvas(Canvas canvas, string name, int width, int height)
    {
        var canvasStates = canvas
            .GetComponentsInChildren<Canvas>(true)
            .Select(c =>
                (
                    Canvas: c,
                    Mode: c.renderMode,
                    Camera: c.worldCamera,
                    Distance: c.planeDistance,
                    Sorting: c.overrideSorting
                )
            )
            .ToArray();
        // Camera.Render does not composite independent overlay sorting canvases.
        // Flatten only the native dropdown's overlay ordering for this offscreen
        // snapshot, retaining its transforms/widgets. Runtime code is untouched.
        var overlays = canvasStates
            .Where(s => s.Canvas != canvas && s.Canvas.gameObject.activeInHierarchy && s.Sorting)
            .OrderBy(s => s.Canvas.sortingOrder)
            .Select(s =>
                (
                    Rect: (RectTransform)s.Canvas.transform,
                    Parent: s.Canvas.transform.parent,
                    Index: s.Canvas.transform.GetSiblingIndex()
                )
            )
            .ToArray();
        foreach (var overlay in overlays)
        {
            overlay.Rect.SetParent(canvas.transform, true);
        }

        var cameraObject = new GameObject("Private UI camera");
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(width, height, 24);
        var previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.025f, .045f, .06f);
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 20;
            camera.cullingMask = 1 << canvas.gameObject.layer;
            camera.orthographic = true;
            camera.orthographicSize = 540;
            foreach (var state in canvasStates)
            {
                state.Canvas.renderMode = RenderMode.ScreenSpaceCamera;
                state.Canvas.worldCamera = camera;
                state.Canvas.planeDistance = 1;
                state.Canvas.overrideSorting = false;
            }
            Canvas.ForceUpdateCanvases();
            camera.Render();
            Save(target, previous, name, width, height);
        }
        finally
        {
            RenderTexture.active = previous;
            foreach (var state in canvasStates)
            {
                state.Canvas.renderMode = state.Mode;
                state.Canvas.worldCamera = state.Camera;
                state.Canvas.planeDistance = state.Distance;
                state.Canvas.overrideSorting = state.Sorting;
            }
            foreach (var overlay in overlays)
            {
                overlay.Rect.SetParent(overlay.Parent, true);
                overlay.Rect.SetSiblingIndex(overlay.Index);
            }
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(cameraObject);
        }
    }

    private static void RenderPart(GameObject prefab, ushort id)
    {
        var settings = Support.Registration.Get(id);
        var shipState = new ShipState(4000001, 4000002, null);
        var ship = new TrackedShipClient(null, shipState);
        var part = new StatefulPart(
            1,
            settings,
            new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
        );
        part.State =
            id == 62005 ? new FunctionCallState { SelectedName = "Clamp" }
            : id == 62006 ? new FunctionParamState { Name = "Target" }
            : id == 62009 ? new FunctionTransmitterState { Summary = "3 broadcasting · 2 private" }
            : new FunctionReturnState { Name = "Clamp" };
        shipState.AddStatefulPartWithoutInit(part);
        var instanceOwner = new GameObject("Private native visual instancer");
        var instancer = instanceOwner.AddComponent<ShipPartInstancer>();
        var visualOwner = new GameObject("Private native ship visuals");
        var nativeVisuals = visualOwner.AddComponent<ShipPartVisuals>();
        AccessTools.Field(typeof(ShipPartVisuals), "_partInstancer").SetValue(nativeVisuals, instancer);
        nativeVisuals.Setup(ship);
        nativeVisuals.AddStatefulPart(part);
        nativeVisuals.InitializeStatefulPart(part);
        Check(
            nativeVisuals.TryGetFloatingPartVisual(part.Id, out IShipPartVisual native),
            "native placed visual is registered for " + id
        );
        var visual = native.GameObject;
        visual.transform.position = new Vector3(10000, 10000, 10000);
        visual.SetActive(true);
        var cameraObject = new GameObject("Private model camera");
        var camera = cameraObject.AddComponent<Camera>();
        var lightObject = new GameObject("Private light");
        var light = lightObject.AddComponent<Light>();
        var target = new RenderTexture(800, 600, 24);
        var previous = RenderTexture.active;
        var ambient = RenderSettings.ambientLight;
        try
        {
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.025f, .045f, .06f);
            camera.orthographic = true;
            camera.orthographicSize = id == 62009 ? .85f : .37f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 5;
            var center =
                visual.transform.position + new Vector3(id == 62005 ? .125f : 0, id == 62009 ? .65f : .075f, 0);
            camera.transform.position =
                center + (id == 62009 ? new Vector3(1.5f, .8f, -2) : new Vector3(.12f, .65f, -.42f));
            camera.transform.LookAt(center);
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.transform.rotation = Quaternion.Euler(45, -30, 0);
            RenderSettings.ambientLight = new Color(.6f, .6f, .6f);
            foreach (var surface in visual.GetComponentsInChildren<Support.ScreenSurface>(true))
            {
                AccessTools
                    .Field(typeof(StellarFunctions.Support.ScreenSurface), "_last")
                    .SetValue(surface, Time.unscaledTime - .2f);
                AccessTools
                    .Method(typeof(StellarFunctions.Support.ScreenSurface), "Update", Type.EmptyTypes)
                    .Invoke(surface, null);
            }
            Canvas.ForceUpdateCanvases();
            string[] shown = visual.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).ToArray();
            File.AppendAllLines(
                Path.Combine(Output, "native-placed-labels.txt"),
                new[] { id + " initial: " + string.Join(" | ", shown) }
            );
            Check(
                shown.Contains(
                    id == 62009 ? "Function Transmitter\n3 broadcasting\n2 private"
                    : id == 62006 ? "Target"
                    : "Clamp"
                ),
                "native placed face shows its Param/function name for "
                    + id
                    + " (shown: "
                    + string.Join(" | ", shown)
                    + ")"
            );
            if (id == 62006 || id == 62007)
            {
                var surface = visual.GetComponentInChildren<Support.ScreenSurface>(true);
                var label = surface.GetComponentInChildren<TextMeshPro>(true);
                Check(
                    !label.enableAutoSizing
                        && !label.richText
                        && label.textWrappingMode == TextWrappingModes.NoWrap
                        && label.overflowMode == TextOverflowModes.Truncate,
                    "Param/Return face uses literal single-line measured text for " + id
                );
                float preferred = label.fontSize;
                string longName = new string('W', 48) + "Z";
                part.State =
                    id == 62006
                        ? ((FunctionParamState)part.State) with
                        {
                            Name = longName,
                        }
                        : ((FunctionReturnState)part.State) with
                        {
                            Name = longName,
                        };
                RefreshFace(surface, label);
                CheckScroll(surface, label, "face " + id);
                part.State =
                    id == 62006
                        ? ((FunctionParamState)part.State) with
                        {
                            Name = "Target",
                        }
                        : ((FunctionReturnState)part.State) with
                        {
                            Name = "Clamp",
                        };
                RefreshFace(surface, label);
                Check(
                    label.text == (id == 62006 ? "Target" : "Clamp") && Mathf.Abs(label.fontSize - preferred) < .0001f,
                    "short Param/Return name restores its readable size for " + id
                );
            }
            camera.Render();
            Save(target, previous, "part-" + id, 800, 600);
            if (id == 62009)
            {
                RenderTexture.active = target;
                var pixels = new Texture2D(800, 600, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 800, 600), 0, 0);
                pixels.Apply();
                Check(
                    pixels.GetPixels().Count(c => c.g > .75f && c.b > .75f && c.r > .25f && c.g > c.r * 1.2f) > 70,
                    "transmitter text produces visible cyan glyph pixels over its opaque curved screen"
                );
                UnityEngine.Object.DestroyImmediate(pixels);
                RenderTexture.active = previous;
                camera.transform.position = center + new Vector3(-1.5f, .8f, 2);
                camera.transform.LookAt(center);
                camera.Render();
                Save(target, previous, "transmitter-rear", 800, 600);
                camera.transform.position = center + new Vector3(1.5f, .8f, -2);
                camera.transform.LookAt(center);
            }
            part.State =
                part.State is FunctionParamState argument ? argument with { Name = "New target" }
                : part.State is FunctionReturnState returned ? returned with { Name = "New clamp" }
                : part.State is FunctionTransmitterState transmitterState
                    ? transmitterState with
                    {
                        Summary = "1 broadcasting · 4 private",
                    }
                : ((FunctionCallState)part.State) with { SelectedName = "New clamp" };
            foreach (var surface in visual.GetComponentsInChildren<Support.ScreenSurface>(true))
            {
                AccessTools
                    .Field(typeof(StellarFunctions.Support.ScreenSurface), "_last")
                    .SetValue(surface, Time.unscaledTime - .2f);
                AccessTools
                    .Method(typeof(StellarFunctions.Support.ScreenSurface), "Update", Type.EmptyTypes)
                    .Invoke(surface, null);
            }
            Canvas.ForceUpdateCanvases();
            shown = visual.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).ToArray();
            File.AppendAllLines(
                Path.Combine(Output, "native-placed-labels.txt"),
                new[] { id + " renamed: " + string.Join(" | ", shown) }
            );
            Check(
                shown.Contains(
                    id == 62009 ? "Function Transmitter\n1 broadcasting\n4 private"
                    : id == 62006 ? "New target"
                    : "New clamp"
                ),
                "placed visual follows its own renamed live state for " + id
            );
        }
        finally
        {
            RenderTexture.active = previous;
            RenderSettings.ambientLight = ambient;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(visualOwner);
            UnityEngine.Object.DestroyImmediate(instanceOwner);
        }
    }

    private static void RenderCallFaces()
    {
        foreach (int count in new[] { 1, 2, 3 })
        {
            var state = new ShipState((uint)(4000100 + count), (uint)(4000200 + count), null);
            var ship = new TrackedShipClient(null, state);
            var part = new StatefulPart(
                1,
                Support.Registration.Get(62005),
                new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up)
            )
            {
                State = new FunctionCallState { InputCount = count, SelectedName = "Toggle" },
            };
            state.AddStatefulPartWithoutInit(part);
            var instancerOwner = new GameObject("Call label native instancer");
            var instancer = instancerOwner.AddComponent<ShipPartInstancer>();
            var visualOwner = new GameObject("Call label native visuals");
            var visuals = visualOwner.AddComponent<ShipPartVisuals>();
            AccessTools.Field(typeof(ShipPartVisuals), "_partInstancer").SetValue(visuals, instancer);
            visuals.Setup(ship);
            visuals.AddStatefulPart(part);
            visuals.InitializeStatefulPart(part);
            Check(
                visuals.TryGetFloatingPartVisual(1, out var native),
                "label fixture uses native placed visual for count " + count
            );
            var visual = native.GameObject;
            visual.transform.position = new Vector3(10000, 10000, 10000);
            visual.SetActive(true);
            var surface = visual.GetComponentInChildren<Support.ScreenSurface>(true);
            var label = surface.GetComponentInChildren<TextMeshPro>(true);
            var cameraOwner = new GameObject("Call face camera");
            var camera = cameraOwner.AddComponent<Camera>();
            var lightOwner = new GameObject("Call face light");
            var light = lightOwner.AddComponent<Light>();
            var target = new RenderTexture(800, 600, 24);
            var previous = RenderTexture.active;
            var ambient = RenderSettings.ambientLight;
            void Refresh() => RefreshFace(surface, label);
            try
            {
                Check(
                    (surface.transform.localScale - Vector3.one).sqrMagnitude < .000001f
                        && Mathf.Abs(label.transform.lossyScale.x - label.transform.lossyScale.y) < .0001f,
                    "Call " + count + " face retains natural glyph aspect rather than scaled X"
                );
                Check(
                    !label.enableAutoSizing
                        && label.textWrappingMode == TextWrappingModes.NoWrap
                        && label.overflowMode == TextOverflowModes.Truncate
                        && !label.richText,
                    "Call " + count + " uses literal single-line measured text"
                );
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.025f, .045f, .06f);
                camera.orthographic = true;
                camera.orthographicSize =
                    count == 1 ? .18f
                    : count == 2 ? .235f
                    : .38f;
                camera.nearClipPlane = .01f;
                camera.farClipPlane = 5;
                camera.transform.position = surface.transform.position + Vector3.up * .8f;
                camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                light.transform.rotation = Quaternion.Euler(45, -30, 0);
                RenderSettings.ambientLight = new Color(.6f, .6f, .6f);
                Refresh();
                Check(
                    label.text == "Toggle"
                        && !label.isTextTruncated
                        && label.textBounds.size.y > (count == 1 ? .014f : .025f)
                        && label.textBounds.size.x < label.rectTransform.rect.width,
                    "short Toggle label fits at readable physical height on Call "
                        + count
                        + " bounds="
                        + label.textBounds.size
                        + " rect="
                        + label.rectTransform.rect.size
                );
                camera.Render();
                Save(target, previous, "call-face-" + count + "-short", 800, 600);
                float preferred = label.fontSize;
                if (count == 3)
                {
                    part.State = ((FunctionCallState)part.State) with { SelectedName = "Clamp Tes" };
                    Refresh();
                    var present = AccessTools.Method(
                        typeof(StellarFunctions.Support.ScreenSurface),
                        "Present",
                        new[] { typeof(float) }
                    );
                    for (int i = 0; i < 20; i++)
                    {
                        present.Invoke(surface, new object[] { .25f });
                    }

                    Check(
                        label.text == "Clamp Tes" && label.firstOverflowCharacterIndex < 0,
                        "three-input Call keeps Clamp Tes stationary and within its text rectangle"
                    );
                }
                part.State = ((FunctionCallState)part.State) with { SelectedName = new string('W', 48) + "Z" };
                Refresh();
                CheckScroll(surface, label, "Call " + count);
                camera.Render();
                Save(target, previous, "call-face-" + count + "-long", 800, 600);
                part.State = ((FunctionCallState)part.State) with { SelectedName = "<b>\nLiteral" };
                Refresh();
                Check(
                    !label.text.Contains("\n") && label.text.StartsWith("<b>"),
                    "Call " + count + " face normalizes line breaks and treats markup as literal text"
                );
                part.State = ((FunctionCallState)part.State) with { SelectedName = "Toggle" };
                Refresh();
                Check(
                    label.text == "Toggle" && Mathf.Abs(label.fontSize - preferred) < .00001f,
                    "Call " + count + " restores the preferred font size for short names"
                );
            }
            finally
            {
                RenderTexture.active = previous;
                RenderSettings.ambientLight = ambient;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraOwner);
                UnityEngine.Object.DestroyImmediate(lightOwner);
                UnityEngine.Object.DestroyImmediate(visualOwner);
                UnityEngine.Object.DestroyImmediate(instancerOwner);
            }
        }
    }

    private static void RefreshFace(Support.ScreenSurface surface, TextMeshPro label)
    {
        AccessTools
            .Field(typeof(StellarFunctions.Support.ScreenSurface), "_last")
            .SetValue(surface, Time.unscaledTime - .2f);
        AccessTools
            .Method(typeof(StellarFunctions.Support.ScreenSurface), "Update", Type.EmptyTypes)
            .Invoke(surface, null);
        label.ForceMeshUpdate(true, true);
    }

    private static void CheckScroll(Support.ScreenSurface surface, TextMeshPro label, string description)
    {
        var present = AccessTools.Method(
            typeof(StellarFunctions.Support.ScreenSurface),
            "Present",
            new[] { typeof(float) }
        );
        string initial = label.text;
        bool changed = false,
            sawTail = initial.Contains("Z"),
            fits = true;
        for (int i = 0; i < 120; i++)
        {
            present.Invoke(surface, new object[] { .25f });
            label.ForceMeshUpdate(true, true);
            changed |= label.text != initial;
            sawTail |= label.text.Contains("Z");
            fits &=
                label.textBounds.size.x <= label.rectTransform.rect.width + .003f
                && label.firstOverflowCharacterIndex < 0;
        }
        Check(changed && sawTail, description + " visibly scrolls through the distinctive name tail");
        Check(fits, description + " scrolling glyphs fit the actual face width");
    }

    private static void Save(RenderTexture target, RenderTexture previous, string name, int width, int height)
    {
        RenderTexture.active = target;
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        texture.Apply();
        Check(
            texture.GetPixels().Count(pixel => pixel.r > .1f || pixel.g > .12f || pixel.b > .15f)
                > width * height / 100,
            "actual visible pixels in " + name
        );
        File.WriteAllBytes(Path.Combine(Output, name + ".png"), texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        RenderTexture.active = previous;
    }
}
