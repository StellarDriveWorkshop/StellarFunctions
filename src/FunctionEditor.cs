using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Core.Services;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using StellarFunctions.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StellarFunctions;

// Small native TMP fields embedded in the content kit's configuration panel.
// Drafts are client-local. Only Apply sends a bounded, revision-checked edit.
internal static class FunctionEditor
{
    private static readonly FieldInfo Layout = AccessTools.Field(typeof(ContentConfigMenu), "_layout");
    private static readonly FieldInfo Side = AccessTools.Field(typeof(ContentConfigMenu), "_side");
    private static string _callRevision = "",
        _directoryRevision = "",
        _inputCountFeedbackBefore = "";
    private static int _requestedInputCount;
    private static float _inputCountRequestedAt;
    private static FunctionDirectory.Snapshot _seenDirectory;
    private static TMP_Dropdown _dropdown,
        _sourceDropdown;
    private static Transform _body;
    private static ScrollRect _bodyScroll;
    private static bool _advanced;

    // Developer-only escape hatch for the experimental external-signals
    // editor, hidden by default. STELLARFUNCTIONS_ADVANCED_OPTIONS=1 or a
    // StellarFunctions.advanced marker beside the DLL re-enables it. Existing
    // Sources data still loads and round-trips through Apply while hidden.
    internal static readonly bool AdvancedOptions = AdvancedOptionsEnabled();

    private static bool AdvancedOptionsEnabled()
    {
        string flag = Environment.GetEnvironmentVariable("STELLARFUNCTIONS_ADVANCED_OPTIONS");
        if (
            flag == "1"
            || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(flag, "yes", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        try
        {
            string path = typeof(FunctionEditor).Assembly.Location;
            return !string.IsNullOrEmpty(path)
                && File.Exists(Path.Combine(Path.GetDirectoryName(path), "StellarFunctions.advanced"));
        }
        catch
        {
            return false;
        }
    }

    private static string _sourceSearch = "",
        _selectionRequested = "";
    private static float _selectionAt,
        _scrollPosition = 1;

    private sealed class BroadcastEdit
    {
        internal bool Value,
            PreviousValue;
        internal float At;
        internal long Sequence;
    }

    private static readonly Dictionary<string, BroadcastEdit> BroadcastEdits = new Dictionary<string, BroadcastEdit>();
    private static readonly List<(Toggle Toggle, string Id, TMP_Text Status)> BroadcastControls =
        new List<(Toggle, string, TMP_Text)>();
    private static TMP_FontAsset _font;
    private static float _nextChoices;
    private static (string Id, string Name)[] _choices = Array.Empty<(string, string)>();
    private static GameObject _root;
    private static Guid _target;
    private static FunctionPartState _original,
        _expected;
    private static string _name,
        _default,
        _channel = "",
        _notice = "";
    private static int _index = 1,
        _sourceIndex;
    private static JObject _sources;
    private static TMP_Text _feedback,
        _statusText,
        _syncText;
    private static float _sentAt;
    private static bool _pending;
    private static List<(EntityPartSocket Socket, string Name)> _options = new List<(EntityPartSocket, string)>();

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        var rows = AccessTools.Method(typeof(ContentConfigMenu), "A", Type.EmptyTypes);
        if (rows == null)
        {
            throw new MissingMethodException("Pinned configuration row builder");
        }

        harmony.Patch(rows, prefix: new HarmonyMethod(typeof(FunctionEditor), nameof(UseCustomRows)));
        // Construct/hide the donor presentation in the same call that opens it,
        // before a native frame can expose its inventory while config sync waits.
        foreach (string name in new[] { "TryAppend", "TryAppendClient" })
        {
            harmony.Patch(
                AccessTools.Method(typeof(ContentConfigMenu), name),
                postfix: new HarmonyMethod(typeof(FunctionEditor), nameof(MenuAppended))
            );
        }
    }

    private static void MenuAppended(bool __result)
    {
        if (__result && ContentConfigMenu.Current.Part?.State is FunctionPartState)
        {
            Tick();
        }
    }

    private static bool UseCustomRows() => ContentConfigMenu.Current.Part?.State is not FunctionPartState;

    internal static void Tick()
    {
        FunctionSourcePresentation.Tick();
        if (!ContentConfigMenu.IsOpen)
        {
            if (_target != Guid.Empty)
            {
                Reset();
            }

            return;
        }
        ContentPart part = ContentConfigMenu.Current;
        if (part.Part?.State is not FunctionPartState state)
        {
            if (_target != Guid.Empty)
            {
                Reset();
            }

            return;
        }
        if (state.StableId != _target)
        {
            Reset();
            _target = state.StableId;
            _original = state;
            _name =
                state is FunctionParamState p ? p.Name
                : state is FunctionReturnState ret ? ret.Name
                : "";
            _index = state is FunctionParamState arg ? arg.Index : 1;
            _default = state is FunctionParamState value
                ? value.DefaultValue?.ToString("R", CultureInfo.InvariantCulture) ?? ""
                : "";
            _sources = state is FunctionReturnState returned ? JObject.Parse(returned.Sources) : new JObject();
            _options = SourceOptions(part);
        }
        // Broadcast is an immediate independent edit. Its replicated state can
        // arrive after the directory reply; keep the draft revision current
        // without silently adopting another player's name/source edits.
        if (
            _original is FunctionReturnState oldReturn
            && state is FunctionReturnState liveReturn
            && oldReturn.Name == liveReturn.Name
            && oldReturn.Sources == liveReturn.Sources
        )
        {
            _original = oldReturn with { Broadcast = liveReturn.Broadcast };
        }

        FunctionDirectory.Poll(part);
        if (!ReferenceEquals(_seenDirectory, FunctionDirectory.Current) && CanRebuild())
        {
            _seenDirectory = FunctionDirectory.Current;
            string revision =
                _seenDirectory == null
                    ? ""
                    : JsonConvert.SerializeObject(
                        new
                        {
                            _seenDirectory.Eligible,
                            _seenDirectory.Truncated,
                            _seenDirectory.Entries,
                        }
                    );
            if (revision != _directoryRevision)
            {
                _directoryRevision = revision;
                Rebuild();
            }
        }
        if (state is FunctionCallState call)
        {
            _original = call;
            if (!string.IsNullOrEmpty(_selectionRequested))
            {
                if (call.SelectedFunctionId == _selectionRequested || call.PendingFunctionId == _selectionRequested)
                {
                    _selectionRequested = "";
                    SetNotice(
                        string.IsNullOrEmpty(call.PendingFunctionId)
                            ? "Function selected."
                            : "Review the function change below."
                    );
                }
                else if (Time.unscaledTime - _selectionAt > 5)
                {
                    _selectionRequested = "";
                    SetNotice("Selection not accepted; check connection, reach and template availability.");
                    _callRevision = "";
                }
            }
            if (_requestedInputCount != 0)
            {
                if (call.InputCount == _requestedInputCount)
                {
                    _requestedInputCount = 0;
                    _callRevision = "";
                    SetNotice("Input count updated.");
                }
                else if (!string.IsNullOrEmpty(call.ResizeFeedback) && call.ResizeFeedback != _inputCountFeedbackBefore)
                {
                    _requestedInputCount = 0;
                    _callRevision = "";
                    SetNotice(call.ResizeFeedback);
                }
                else if (Time.unscaledTime - _inputCountRequestedAt > 5f)
                {
                    _requestedInputCount = 0;
                    _callRevision = "";
                    SetNotice("Input count change was not confirmed by the server.");
                }
            }
            if (Time.unscaledTime >= _nextChoices)
            {
                _nextChoices = Time.unscaledTime + .25f;
                _choices = Choices(part);
            }
            string revision =
                call.SelectedFunctionId
                + "|"
                + call.SelectedName
                + "|"
                + call.PendingFunctionId
                + "|"
                + call.Status
                + "|"
                + call.Input0Label
                + "|"
                + call.Input1Label
                + "|"
                + call.Input2Label
                + "|"
                + call.InputCount
                + "|"
                + call.ResizeFeedback
                + "|"
                + (FunctionDirectory.Current?.CallWired ?? false)
                + "|"
                + string.Join("|", _choices.Select(e => e.Id + e.Name));
            if (revision != _callRevision && CanRebuild())
            {
                Rebuild();
                _callRevision = revision;
            }
        }
        if (_pending)
        {
            if (EditMatches(state, _expected))
            {
                _original = state;
                _pending = false;
                _notice = "Applied by server.";
                FunctionDirectory.RequestRefresh();
            }
            else if (Time.realtimeSinceStartup - _sentAt > 3f)
            {
                _pending = false;
                _notice = "No edit accepted. Check fields/reach or reopen after another edit.";
            }
        }
        if (_root == null && Layout.GetValue(null) is MenuLayout layout && layout.Grid != null)
        {
            Build(part, layout);
        }

        ResizePanel();
        FitDropdown(_dropdown);
        FitDropdown(_sourceDropdown);
        UpdateBroadcastControls();
        if (_feedback != null)
        {
            _feedback.text = _notice;
        }

        if (_syncText != null)
        {
            _syncText.text = FunctionDirectory.Status == "Ready" ? "Synced with server" : FunctionDirectory.Status;
        }

        if (_statusText != null)
        {
            _statusText.text = state is FunctionReturnState ret ? "Status: " + ret.Status : "Template argument";
            _statusText.color =
                state is FunctionReturnState returned && returned.Status != "TEMPLATE"
                    ? new Color(1, .72f, .4f)
                    : new Color(.5f, .95f, 1);
        }
    }

    internal static void Reset()
    {
        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root);
        }

        _requestedInputCount = 0;
        _inputCountRequestedAt = 0;
        _root = null;
        _body = null;
        _bodyScroll = null;
        _sourceDropdown = null;
        _advanced = false;
        _sourceSearch = "";
        _selectionRequested = "";
        _scrollPosition = 1;
        BroadcastEdits.Clear();
        BroadcastControls.Clear();
        FunctionSourcePresentation.Reset();
        _directoryRevision = "";
        _seenDirectory = null;
        FunctionDirectory.Reset();
        _feedback = null;
        _statusText = null;
        _dropdown = null;
        _callRevision = "";
        _choices = Array.Empty<(string, string)>();
        _nextChoices = 0;
        _target = Guid.Empty;
        _original = null;
        _pending = false;
        _expected = null;
        _notice = "";
        _sourceIndex = 0;
        _channel = "";
        _options.Clear();
    }

    private static void Build(ContentPart part, MenuLayout layout)
    {
        // Use the native framed panel and menu tracker, but own every row.
        // The old config labels and custom fields must not both occupy this space.
        var side = Side.GetValue(null) as GameObject;
        if (side == null)
        {
            return;
        }
        // Keep the frame inside this native menu's lifetime, center relative to
        // its full canvas, and remove the donor's trash/inventory decoration.
        var menuRoot = ContentConfigMenu.Root.transform;
        side.transform.SetParent(menuRoot, false);
        if (menuRoot is RectTransform menuRect && menuRoot.GetComponent<Canvas>() == null)
        {
            menuRect.anchorMin = Vector2.zero;
            menuRect.anchorMax = Vector2.one;
            menuRect.offsetMin = menuRect.offsetMax = Vector2.zero;
            menuRect.localScale = Vector3.one;
        }
        foreach (Transform child in menuRoot.Cast<Transform>().ToArray())
        {
            if (child != side.transform)
            {
                child.gameObject.SetActive(false);
            }
        }

        _font = (layout.Title as TMP_Text)?.font ?? Support.Screens.Font(null);
        var panel = (RectTransform)side.transform;
        foreach (var controller in panel.GetComponents<LayoutGroup>())
        {
            UnityEngine.Object.DestroyImmediate(controller);
        }

        var fitter = panel.GetComponent<ContentSizeFitter>();
        if (fitter != null)
        {
            UnityEngine.Object.DestroyImmediate(fitter);
        }

        panel.anchorMin = panel.anchorMax = new Vector2(.5f, .5f);
        panel.pivot = new Vector2(.5f, .5f);
        panel.anchoredPosition = Vector2.zero;
        ResizePanel();
        foreach (var text in side.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!text.transform.IsChildOf(layout.Grid))
            {
                text.gameObject.SetActive(false);
            }
        }

        foreach (Transform child in layout.Grid.Cast<Transform>().ToArray())
        {
            UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        var grid = (RectTransform)layout.Grid;
        grid.anchorMin = Vector2.zero;
        grid.anchorMax = Vector2.one;
        grid.offsetMin = new Vector2(40, 34);
        grid.offsetMax = new Vector2(-40, -34);
        foreach (var controller in grid.GetComponents<LayoutGroup>())
        {
            UnityEngine.Object.DestroyImmediate(controller);
        }

        _root = Node(grid, "Function fields", typeof(VerticalLayoutGroup));
        var rootRect = (RectTransform)_root.transform;
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
        var group = _root.GetComponent<VerticalLayoutGroup>();
        group.spacing = 8;
        group.childControlWidth = true;
        group.childControlHeight = true;
        group.childForceExpandWidth = true;
        group.childForceExpandHeight = false;
        var title = Label(
            _root.transform,
            _original is FunctionCallState ? "Call"
                : _original is FunctionParamState ? "Param"
                : _original is FunctionTransmitterState ? "Function Transmitter"
                : "Return"
        );
        title.fontSize = 36;
        title.alignment = TextAlignmentOptions.Center;
        title.GetComponent<LayoutElement>().preferredHeight = 52;
        _body = SourceScroll(_root.transform);
        _bodyScroll = _body.parent.parent.GetComponent<ScrollRect>();
        if (_original is FunctionCallState current)
        {
            BuildCall(part, current);
        }
        else if (_original is FunctionTransmitterState)
        {
            BuildTransmitter(part);
        }
        else
        {
            _statusText = Label(_body, "");
            Label(_body, "Edit fields, then Apply. F or Esc closes without applying.");
            Input(_body, "Name", _name, 64, s => _name = s);
            if (_original is FunctionParamState)
            {
                Button(
                    _body,
                    "Index: " + _index + " (click to cycle 1–3)",
                    () =>
                    {
                        _index = _index % 3 + 1;
                        Rebuild();
                    }
                );
                Input(_body, "Default (empty = 0)", _default, 64, s => _default = s);
            }
            else
            {
                var data = DirectoryView(part);
                BroadcastControl(
                    _body,
                    part,
                    data.Entries.FirstOrDefault(e => e.Id == _original.StableId.ToString("N"))
                );
                if (AdvancedOptions)
                {
                    Button(
                        _body,
                        "Advanced · External signals",
                        () =>
                        {
                            _advanced = !_advanced;
                            Rebuild();
                        }
                    );
                    if (_advanced)
                    {
                        BuildExternalSources(part);
                    }
                }
            }
            Button(
                _root.transform,
                "Apply",
                () =>
                {
                    try
                    {
                        if (_pending)
                        {
                            SetNotice("Saving this edit…");
                            return;
                        }
                        string payload = FunctionEdits.Payload(_original, _name, _index, _default, _sources);
                        _expected = FunctionEdits.Apply(_original, payload);
                        if (!ContentActions.Send(part, payload))
                        {
                            SetNotice("Not connected to server.");
                            return;
                        }
                        _pending = true;
                        _sentAt = Time.realtimeSinceStartup;
                        SetNotice("Saving…");
                        FunctionDirectory.RequestRefresh();
                    }
                    catch (Exception error)
                    {
                        SetNotice(error.Message);
                    }
                }
            );
        }
        _syncText = Label(_root.transform, FunctionDirectory.Status);
        _syncText.fontSize = 19;
        _syncText.GetComponent<LayoutElement>().preferredHeight = 24;
        _feedback = Label(_root.transform, _notice);
        _feedback.fontSize = 21;
        _feedback.textWrappingMode = TextWrappingModes.Normal;
        _feedback.GetComponent<LayoutElement>().preferredHeight = 58;
        Canvas.ForceUpdateCanvases();
        _bodyScroll.verticalNormalizedPosition = _scrollPosition;
    }

    private static bool EditMatches(FunctionPartState live, FunctionPartState expected) =>
        live is FunctionParamState p && expected is FunctionParamState q
            ? p.Name == q.Name && p.Index == q.Index && p.DefaultValue == q.DefaultValue
            : live is FunctionReturnState r
                && expected is FunctionReturnState s
                && r.Name == s.Name
                && r.Sources == s.Sources;

    private static bool CanRebuild() =>
        (_dropdown == null || !_dropdown.IsExpanded)
        && (_sourceDropdown == null || !_sourceDropdown.IsExpanded)
        && (
            _root == null
            || !_root
                .GetComponentsInChildren<TMP_InputField>()
                .Any(i =>
                    i.isFocused
                    || UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject == i.gameObject
                )
        );

    private static void SetNotice(string message)
    {
        _notice = message;
        if (_feedback != null)
        {
            _feedback.text = message;
        }
    }

    private static void ResizePanel()
    {
        if (Side.GetValue(null) is not GameObject side || ContentConfigMenu.Root == null)
        {
            return;
        }

        var canvas = side.GetComponentInParent<Canvas>();
        var rect = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
        float width = rect != null && rect.rect.width > 0 ? rect.rect.width : 1920;
        float height = rect != null && rect.rect.height > 0 ? rect.rect.height : 1080;
        ((RectTransform)side.transform).sizeDelta = new Vector2(
            Mathf.Min(820, Mathf.Max(280, width - 48)),
            Mathf.Min(1000, Mathf.Max(240, height - 48))
        );
    }

    // Loaded native state is a useful preview while the public directory arrives.
    // It never grants authority, invents remote templates or replaces server checks.
    private static FunctionDirectory.Snapshot DirectoryView(ContentPart part)
    {
        if (FunctionDirectory.Current != null)
        {
            return FunctionDirectory.Current;
        }

        var ships = AssemblyShips(part).ToArray();
        var entries = ships
            .SelectMany(s => s.StatefulParts)
            .Where(p => p.State is FunctionReturnState r && !r.IsHeldByPlayer)
            .Select(p => (FunctionReturnState)p.State)
            .Select(r => new FunctionDirectory.Entry
            {
                Id = r.DefinitionId.ToString("N"),
                Name = r.Name,
                Origin = "Local structure",
                Status = r.Status,
                Revision = FunctionEdits.Revision(r),
                Parameters = "Parameter details syncing…",
                Broadcast = r.Broadcast,
                Published = false,
                Local = true,
            })
            .ToArray();
        return new FunctionDirectory.Snapshot
        {
            Eligible = ships.Any(s =>
                s.StatefulParts.Any(p => p.State is FunctionTransmitterState t && !t.IsHeldByPlayer)
            ),
            Entries = entries,
        };
    }

    private static (string Id, string Name)[] Choices(ContentPart part) =>
        DirectoryView(part)
            .Entries.Where(e => e.Status == "TEMPLATE")
            .Select(e => (e.Id, e.Name + (e.Local ? " · local" : " · " + e.Origin)))
            .ToArray();

    private static void BroadcastControl(Transform parent, ContentPart part, FunctionDirectory.Entry entry)
    {
        bool eligible = DirectoryView(part).Eligible;
        bool valid = entry?.Status == "TEMPLATE";
        bool pending = entry != null && BroadcastEdits.TryGetValue(entry.Id, out _);
        bool value = entry != null && (pending ? BroadcastEdits[entry.Id].Value : entry.Broadcast);
        var toggle = Checkbox(
            parent,
            "Broadcast to all ships",
            value,
            eligible && valid && entry != null && !pending,
            requested =>
            {
                if (entry == null)
                {
                    return;
                }
                // A focused draft can defer panel reconstruction. Resolve the newest
                // revision on click rather than closing over the old descriptor.
                var latest = FunctionDirectory.Current?.Entries.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
                if (ContentActions.Send(part, FunctionBroadcast.Payload(latest.Id, latest.Revision, requested)))
                {
                    BroadcastEdits[entry.Id] = new BroadcastEdit
                    {
                        Value = requested,
                        PreviousValue = latest.Broadcast,
                        At = Time.unscaledTime,
                        Sequence = FunctionDirectory.LastRequestedSequence,
                    };
                    SetNotice("Saving broadcast setting…");
                    FunctionDirectory.RequestRefresh();
                }
                else
                {
                    SetNotice("Not connected to server.");
                    Rebuild();
                }
            }
        );
        string reason =
            !eligible ? "Add a Function Transmitter to this ship/base to broadcast."
            : !valid ? "Fix this template before broadcasting."
            : pending ? "Saving…"
            : "Each Call keeps its own inputs and Memory.";
        var status = Label(parent, reason);
        status.fontSize = 21;
        status.textWrappingMode = TextWrappingModes.Normal;
        status.GetComponent<LayoutElement>().preferredHeight = 52;
        if (entry != null)
        {
            BroadcastControls.Add((toggle, entry.Id, status));
        }
    }

    private static void UpdateBroadcastControls()
    {
        foreach (var binding in BroadcastControls)
        {
            if (binding.Toggle == null)
            {
                continue;
            }

            var current = FunctionDirectory.Current?.Entries.FirstOrDefault(e => e.Id == binding.Id);
            if (BroadcastEdits.TryGetValue(binding.Id, out var edit))
            {
                binding.Toggle.SetIsOnWithoutNotify(edit.Value);
                SetBroadcastAvailability(binding.Toggle, false);
                if (
                    current != null
                    && FunctionDirectory.Current.Sequence > edit.Sequence
                    && current.Broadcast == edit.Value
                )
                {
                    BroadcastEdits.Remove(binding.Id);
                    SetNotice("Broadcast setting saved.");
                }
                else if (Time.unscaledTime - edit.At > 5)
                {
                    BroadcastEdits.Remove(binding.Id);
                    SetNotice("Broadcast setting was not accepted; showing the last known value.");
                    if (current == null)
                    {
                        binding.Toggle.SetIsOnWithoutNotify(edit.PreviousValue);
                        SetBroadcastAvailability(binding.Toggle, false);
                        binding.Status.text = "Server confirmation unavailable; waiting for a fresh directory.";
                        continue;
                    }
                }
                else
                {
                    binding.Status.text = "Saving…";
                    continue;
                }
            }
            if (current == null)
            {
                continue;
            }

            binding.Toggle.SetIsOnWithoutNotify(current.Broadcast);
            bool eligible = FunctionDirectory.Current.Eligible && current.Status == "TEMPLATE";
            SetBroadcastAvailability(binding.Toggle, eligible);
            binding.Status.text =
                !FunctionDirectory.Current.Eligible ? "Add a Function Transmitter to this ship/base to broadcast."
                : current.Status != "TEMPLATE" ? "Fix this template before broadcasting."
                : "Each Call keeps its own inputs and Memory.";
        }
    }

    private static void BuildTransmitter(ContentPart part)
    {
        Label(_body, "Functions on this connected ship/base");
        var data = DirectoryView(part);
        Label(
            _body,
            data.Entries.Count(e => e.Published)
                + " broadcasting · "
                + data.Entries.Count(e => e.Status == "TEMPLATE" && !e.Published)
                + " private · "
                + data.Entries.Count(e => e.Status != "TEMPLATE")
                + " invalid"
        );
        if (FunctionDirectory.Current == null)
        {
            Label(_body, "Local preview · syncing with server…");
        }

        foreach (var entry in data.Entries)
        {
            var functionName = Label(_body, entry.Name);
            functionName.fontSize = 28;
            functionName.GetComponent<LayoutElement>().preferredHeight = 44;
            Label(_body, string.IsNullOrEmpty(entry.Parameters) ? "No parameters" : entry.Parameters).fontSize = 21;
            Label(
                _body,
                entry.Status != "TEMPLATE" ? "Invalid: " + entry.Status
                    : entry.Published ? "Broadcasting"
                    : "Private"
            );
            BroadcastControl(_body, part, entry);
        }
        if (data.Entries.Length == 0)
        {
            Label(_body, "No Return blocks loaded on this structure yet.");
        }

        if (data.Truncated)
        {
            Label(_body, "Showing the first 256 definitions.");
        }

        Label(_body, "F / Esc: close");
    }

    private static void BuildExternalSources(ContentPart part)
    {
        var explanation = Label(_body, "Allow live reads from outside the template. Other labels stay private.");
        explanation.textWrappingMode = TextWrappingModes.Normal;
        explanation.GetComponent<LayoutElement>().preferredHeight = 60;
        foreach (string section in new[] { "channels", "parts" })
        {
            if (_sources[section] is JObject entries)
            {
                foreach (JProperty entry in entries.Properties().ToArray())
                {
                    string key = entry.Name,
                        kind = section;
                    Button(
                        _body,
                        "Remove " + kind + " · " + key,
                        () =>
                        {
                            ((JObject)_sources[kind]).Remove(key);
                            Rebuild();
                        }
                    );
                }
            }
        }

        if (_options.Count == 0)
        {
            Label(_body, "No supported source loaded in this rotor assembly.");
            return;
        }
        Input(
            _body,
            "Search sources",
            _sourceSearch,
            128,
            s =>
            {
                _sourceSearch = s;
                UpdateSourceChoices();
            }
        );
        _sourceDropdown = Dropdown(
            _body,
            new List<string>(),
            0,
            i =>
            {
                var filtered = FilteredSources();
                if (i < 1 || i > filtered.Length)
                {
                    return;
                }

                _sourceIndex = filtered[i - 1];
                UpdateSourceDetails();
            }
        );
        _sourceDropdown.name = "Source dropdown";
        var details = Label(_body, "");
        details.name = "Selected source details";
        details.textWrappingMode = TextWrappingModes.Normal;
        details.overflowMode = TextOverflowModes.Overflow;
        details.fontSize = 21;
        details.GetComponent<LayoutElement>().preferredHeight = 100;
        UpdateSourceChoices();
        Button(
            _body,
            "Identify source",
            () =>
            {
                if (!SourceVisible())
                {
                    SetNotice("Choose a matching source first.");
                    return;
                }
                FunctionSourcePresentation.Identify(part, _options[_sourceIndex].Socket, out string message);
                SetNotice(_options[_sourceIndex].Name + ": " + message);
            }
        );
        Button(
            _body,
            "Allow selected part as an external sensor",
            () =>
            {
                if (!SourceVisible())
                {
                    SetNotice("Choose a matching source first.");
                    return;
                }
                PutSource("parts", null);
                SetNotice("External source added to draft. Apply to save.");
                Rebuild();
            }
        );
        Input(_body, "External label name", _channel, 64, s => _channel = s);
        Button(
            _body,
            "Bind label to selected external source",
            () =>
            {
                if (!SourceVisible())
                {
                    SetNotice("Choose a matching source first.");
                    return;
                }
                if (!string.IsNullOrWhiteSpace(_channel))
                {
                    PutSource("channels", _channel.Trim());
                    SetNotice("Label binding added to draft. Apply to save.");
                    Rebuild();
                }
            }
        );
    }

    private static int[] FilteredSources() =>
        Enumerable
            .Range(0, _options.Count)
            .Where(i =>
                string.IsNullOrWhiteSpace(_sourceSearch)
                || _options[i].Name.IndexOf(_sourceSearch, StringComparison.OrdinalIgnoreCase) >= 0
            )
            .ToArray();

    private static bool SourceVisible() =>
        _options.Count > 0
        && _sourceIndex >= 0
        && _sourceIndex < _options.Count
        && FilteredSources().Contains(_sourceIndex);

    private static void UpdateSourceChoices()
    {
        if (_sourceDropdown == null)
        {
            return;
        }

        _sourceDropdown.Hide();
        var filtered = FilteredSources();
        _sourceDropdown.ClearOptions();
        _sourceDropdown.AddOptions(
            new[] { filtered.Length == 0 ? "No matching sources" : "Select a source…" }
                .Concat(filtered.Select(i => _options[i].Name))
                .ToList()
        );
        // Filtering never silently changes the selected physical source.
        int selected = Array.IndexOf(filtered, _sourceIndex);
        _sourceDropdown.SetValueWithoutNotify(selected + 1);
        _sourceDropdown.RefreshShownValue();
        if (selected < 0 && filtered.Length > 0)
        {
            _sourceDropdown.captionText.text = "Choose a matching source…";
        }

        _sourceDropdown.interactable = filtered.Length > 0;
        UpdateSourceDetails();
    }

    private static void UpdateSourceDetails()
    {
        var label = _root.GetComponentsInChildren<TMP_Text>().FirstOrDefault(t => t.name == "Selected source details");
        if (label == null)
        {
            return;
        }

        if (_options.Count == 0 || _sourceIndex < 0 || _sourceIndex >= _options.Count)
        {
            label.text = "Selected: (none)";
            return;
        }
        label.text = "Selected: " + _options[_sourceIndex].Name + (SourceVisible() ? "" : " (not in current filter)");
    }

    private static void SetBroadcastAvailability(Toggle toggle, bool enabled)
    {
        toggle.interactable = enabled;
        // A pending snapshot can rebuild the checkbox while it is disabled.
        // Native Selectable re-enables its image, but does not recolor our label.
        foreach (var label in toggle.GetComponentsInChildren<TMP_Text>())
        {
            label.color = enabled ? new Color(.75f, .9f, .95f) : Color.gray;
        }
    }

    private static Toggle Checkbox(Transform parent, string caption, bool value, bool enabled, Action<bool> change)
    {
        var root = Node(parent, "Broadcast checkbox", typeof(Image), typeof(Toggle), typeof(LayoutElement));
        root.GetComponent<LayoutElement>().preferredHeight = 44;
        root.GetComponent<Image>().color = new Color(.10f, .18f, .23f);
        var box = Node(root.transform, "Checkbox", typeof(Image));
        Place(box.transform, new Vector2(0, .5f), new Vector2(28, 28));
        ((RectTransform)box.transform).anchoredPosition = new Vector2(18, 0);
        box.GetComponent<Image>().color = new Color(.25f, .4f, .45f);
        var mark = Node(box.transform, "Checkmark", typeof(Image));
        Place(mark.transform, new Vector2(.5f, .5f), new Vector2(17, 17));
        mark.GetComponent<Image>().color = new Color(.3f, .95f, 1);
        var label = Label(root.transform, caption);
        var rect = (RectTransform)label.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(44, 0);
        rect.offsetMax = new Vector2(-5, 0);
        label.color = enabled ? new Color(.75f, .9f, .95f) : Color.gray;
        var toggle = root.GetComponent<Toggle>();
        toggle.targetGraphic = box.GetComponent<Image>();
        toggle.graphic = mark.GetComponent<Image>();
        toggle.SetIsOnWithoutNotify(value);
        mark.GetComponent<Image>().enabled = value;
        toggle.interactable = enabled;
        toggle.onValueChanged.AddListener(v =>
        {
            change(v);
            if (toggle != null)
            {
                mark.GetComponent<Image>().enabled = v;
                SetBroadcastAvailability(toggle, false);
            }
        });
        return toggle;
    }

    private static void BuildCall(ContentPart part, FunctionCallState call)
    {
        Label(_body, "Function");
        var options = _choices;
        int selected =
            Array.FindIndex(
                options,
                e => e.Id == (string.IsNullOrEmpty(_selectionRequested) ? call.SelectedFunctionId : _selectionRequested)
            ) + 1;
        var choices = new List<string>
        {
            selected == 0 && !string.IsNullOrEmpty(call.SelectedFunctionId)
                ? "Unavailable: " + call.SelectedName
                : "Choose a function…",
        };
        choices.AddRange(options.Select(e => e.Name));
        _dropdown = Dropdown(
            _body,
            choices,
            selected,
            index =>
            {
                if (index > 0 && index <= options.Length)
                {
                    var chosen = options[index - 1];
                    if (ContentActions.Send(part, ClientCallInteraction.SelectAction + chosen.Id))
                    {
                        _selectionRequested = chosen.Id;
                        _selectionAt = Time.unscaledTime;
                        SetNotice("Selecting " + chosen.Name + "…");
                        FunctionDirectory.RequestRefresh();
                    }
                    else
                    {
                        SetNotice("Not connected to server.");
                        _dropdown.SetValueWithoutNotify(selected);
                        _dropdown.RefreshShownValue();
                    }
                }
                else
                {
                    _dropdown.SetValueWithoutNotify(selected);
                    _dropdown.RefreshShownValue();
                }
            }
        );
        var selectionHint = Label(
            _body,
            options.Length == 0
                ? "No local or broadcast templates available."
                : "Select explicitly. Opening or closing this menu changes nothing."
        );
        selectionHint.fontSize = 21;
        selectionHint.textWrappingMode = TextWrappingModes.Normal;
        selectionHint.GetComponent<LayoutElement>().preferredHeight = 46;
        BuildCallSizing(part, call);
        Diagram(_body, call);
        var status = Label(_body, "Status: " + call.Status);
        status.color = call.Status == "OK" ? new Color(.5f, .95f, 1) : new Color(1, .72f, .4f);
        if (!string.IsNullOrEmpty(call.PendingFunctionId))
        {
            Label(_body, "Change to " + call.PendingFunctionName + "?");
            Label(_body, "Keeps cables; resets input mapping and private Memory.");
            Button(
                _body,
                "Confirm function change",
                () => SendAction(part, "confirm-rebind:" + call.PendingFunctionId, "Applying function change…")
            );
            Button(
                _body,
                "Cancel function change",
                () => SendAction(part, "cancel-rebind", "Cancelling function change…")
            );
        }
        else
        {
            Button(
                _body,
                "Reset input mapping and Memory…",
                () =>
                {
                    // Explicit second click; no accidental reset from dropdown/F.
                    if (_notice == "Confirm reset: cables stay; input meanings and Memory reset.")
                    {
                        ContentActions.Send(part, "reset-input-bindings");
                        _notice = "Reset requested.";
                    }
                    else
                    {
                        _notice = "Confirm reset: cables stay; input meanings and Memory reset.";
                    }
                }
            );
        }

        Label(_body, "F / Esc: close");
    }

    private static void SendAction(ContentPart part, string action, string message)
    {
        SetNotice(ContentActions.Send(part, action) ? message : "Not connected to server.");
        FunctionDirectory.RequestRefresh();
    }

    private static void BuildCallSizing(ContentPart part, FunctionCallState call)
    {
        var row = Node(_body, "Input count", typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.GetComponent<LayoutElement>().preferredHeight = 44;
        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 8;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        var caption = Label(row.transform, "Inputs");
        caption.GetComponent<LayoutElement>().preferredWidth = 110;
        caption.GetComponent<LayoutElement>().flexibleWidth = 0;
        bool wired = FunctionDirectory.Current?.CallWired ?? false;
        bool pending = _requestedInputCount != 0;
        bool enabled =
            FunctionDirectory.Current != null && !wired && !pending && string.IsNullOrEmpty(call.PendingFunctionId);
        for (int count = 1; count <= 3; count++)
        {
            int chosen = count;
            var item = Node(
                row.transform,
                "Inputs " + count,
                typeof(Image),
                typeof(UnityEngine.UI.Button),
                typeof(LayoutElement)
            );
            item.GetComponent<LayoutElement>().preferredWidth = 58;
            var image = item.GetComponent<Image>();
            image.color = count == call.InputCount ? new Color(.17f, .48f, .58f) : new Color(.12f, .23f, .29f);
            var label = Label(item.transform, count.ToString());
            label.alignment = TextAlignmentOptions.Center;
            var rect = (RectTransform)label.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var button = item.GetComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.interactable = enabled;
            button.onClick.AddListener(() =>
            {
                if (chosen == call.InputCount || _requestedInputCount != 0)
                {
                    return;
                }

                string payload = CallSizing.Payload(call, chosen);
                if (ContentActions.Send(part, payload))
                {
                    _requestedInputCount = chosen;
                    _inputCountFeedbackBefore = call.ResizeFeedback;
                    _inputCountRequestedAt = Time.unscaledTime;
                    _callRevision = "";
                    SetNotice("Updating input count…");
                    FunctionDirectory.RequestRefresh();
                }
                else
                {
                    SetNotice("Not connected to server.");
                }
            });
        }
        string reason =
            wired ? "Disconnect all terminals to change input count."
            : pending ? "Waiting for server confirmation…"
            : !string.IsNullOrEmpty(call.ResizeFeedback) ? call.ResizeFeedback
            : "Hidden inputs use parameter defaults (or 0 without a default).";
        var help = Label(_body, reason);
        help.fontSize = 19;
        help.textWrappingMode = TextWrappingModes.Normal;
        help.GetComponent<LayoutElement>().preferredHeight = 46;
    }

    private static void Diagram(Transform parent, FunctionCallState call)
    {
        int count = Mathf.Clamp(call.InputCount, 1, 3);
        float modelHeight =
            count == 1 ? 100
            : count == 2 ? 166
            : 218;
        var root = Node(parent, "Socket diagram", typeof(LayoutElement));
        root.GetComponent<LayoutElement>().preferredHeight = 230;
        // The diagram rotates the case: the output sits opposite input 1.
        var housing = Node(root.transform, "Call housing", typeof(Image));
        housing.GetComponent<Image>().color = new Color(.72f, .77f, .8f);
        Place(housing.transform, new Vector2(.56f, .5f), new Vector2(count == 1 ? 100 : 116, modelHeight));
        var lcd = Node(housing.transform, "Display", typeof(Image));
        lcd.GetComponent<Image>().color = new Color(.025f, .05f, .06f);
        Place(
            lcd.transform,
            new Vector2(.5f, .5f),
            new Vector2(
                count == 1 ? 84 : 100,
                count == 1 ? 54
                    : count == 2 ? 70
                    : 90
            )
        );
        var name = Label(lcd.transform, string.IsNullOrEmpty(call.SelectedName) ? "Call" : call.SelectedName);
        name.alignment = TextAlignmentOptions.Center;
        name.textWrappingMode = TextWrappingModes.Normal;
        name.enableAutoSizing = true;
        name.fontSizeMin = 14;
        name.fontSizeMax = 24;
        var nr = (RectTransform)name.transform;
        nr.anchorMin = Vector2.zero;
        nr.anchorMax = Vector2.one;
        nr.offsetMin = new Vector2(8, 8);
        nr.offsetMax = new Vector2(-8, -8);
        var labels = new[] { call.Input0Label, call.Input1Label, call.Input2Label };
        float firstSocketY = .5f + (count - 1) * .145f;
        for (int i = 0; i < count; i++)
        {
            float y = firstSocketY - i * .29f;
            var socket = Node(root.transform, "Input " + (i + 1) + " socket", typeof(Image));
            socket.GetComponent<Image>().color =
                (call.ListeningMask & (1 << i)) != 0 ? new Color(.3f, .9f, 1) : new Color(.25f, .3f, .32f);
            Place(socket.transform, new Vector2(.465f, y), new Vector2(22, 30));
            var label = Label(root.transform, "Input " + (i + 1) + "   " + labels[i]);
            label.fontSize = 20;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Truncate;
            Place(label.transform, new Vector2(.22f, y), new Vector2(290, 60));
        }
        var output = Node(root.transform, "Output socket", typeof(Image));
        output.GetComponent<Image>().color = new Color(.3f, .9f, 1);
        Place(output.transform, new Vector2(.655f, firstSocketY), new Vector2(22, 30));
        var outLabel = Label(root.transform, "Output");
        Place(outLabel.transform, new Vector2(.83f, firstSocketY), new Vector2(150, 40));
    }

    private static void Place(Transform transform, Vector2 anchor, Vector2 size)
    {
        var rect = (RectTransform)transform;
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = new Vector2(.5f, .5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = size;
    }

    private static TMP_Dropdown Dropdown(Transform parent, List<string> choices, int selected, Action<int> change)
    {
        var root = Node(parent, "Function dropdown", typeof(Image), typeof(TMP_Dropdown), typeof(LayoutElement));
        root.GetComponent<LayoutElement>().preferredHeight = 48;
        root.GetComponent<Image>().color = new Color(.12f, .23f, .29f);
        var caption = Label(root.transform, "");
        var cr = (RectTransform)caption.transform;
        cr.anchorMin = Vector2.zero;
        cr.anchorMax = Vector2.one;
        cr.offsetMin = new Vector2(12, 3);
        cr.offsetMax = new Vector2(-42, -3);
        var arrow = Label(root.transform, "▼");
        Place(arrow.transform, new Vector2(1, .5f), new Vector2(36, 42));
        ((RectTransform)arrow.transform).anchoredPosition = new Vector2(-22, 0);
        var template = Node(root.transform, "Template", typeof(Image), typeof(ScrollRect), typeof(CanvasGroup));
        template.GetComponent<Image>().color = new Color(.04f, .1f, .14f);
        var tr = (RectTransform)template.transform;
        tr.anchorMin = new Vector2(0, 0);
        tr.anchorMax = new Vector2(1, 0);
        tr.pivot = new Vector2(.5f, 1);
        tr.offsetMin = new Vector2(0, -250);
        tr.offsetMax = Vector2.zero;
        var view = Node(template.transform, "Viewport", typeof(Image), typeof(RectMask2D));
        view.GetComponent<Image>().color = Color.clear;
        var vr = (RectTransform)view.transform;
        vr.anchorMin = Vector2.zero;
        vr.anchorMax = Vector2.one;
        vr.offsetMin = vr.offsetMax = Vector2.zero;
        var content = Node(view.transform, "Content");
        var con = (RectTransform)content.transform;
        con.anchorMin = new Vector2(0, 1);
        con.anchorMax = Vector2.one;
        con.pivot = new Vector2(.5f, 1);
        con.sizeDelta = new Vector2(0, 44);
        var item = Node(content.transform, "Item", typeof(Image), typeof(Toggle));
        var ir = (RectTransform)item.transform;
        ir.anchorMin = Vector2.zero;
        ir.anchorMax = Vector2.one;
        ir.sizeDelta = Vector2.zero;
        var image = item.GetComponent<Image>();
        image.color = new Color(.14f, .24f, .3f);
        var toggle = item.GetComponent<Toggle>();
        toggle.targetGraphic = image;
        var check = Node(item.transform, "Selected", typeof(Image));
        check.GetComponent<Image>().color = new Color(.3f, .9f, 1);
        Place(check.transform, new Vector2(0, .5f), new Vector2(4, 30));
        ((RectTransform)check.transform).anchoredPosition = new Vector2(5, 0);
        toggle.graphic = check.GetComponent<Image>();
        var text = Label(item.transform, "");
        var tx = (RectTransform)text.transform;
        tx.anchorMin = Vector2.zero;
        tx.anchorMax = Vector2.one;
        tx.offsetMin = new Vector2(14, 0);
        tx.offsetMax = new Vector2(-8, 0);
        var scroll = template.GetComponent<ScrollRect>();
        scroll.viewport = vr;
        scroll.content = con;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.scrollSensitivity = 30;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        AddScrollbar(scroll);
        var dropdown = root.GetComponent<TMP_Dropdown>();
        dropdown.targetGraphic = root.GetComponent<Image>();
        dropdown.template = tr;
        dropdown.captionText = caption;
        dropdown.itemText = text;
        dropdown.options = choices.Select(s => new TMP_Dropdown.OptionData(s)).ToList();
        dropdown.SetValueWithoutNotify(selected);
        dropdown.RefreshShownValue();
        template.SetActive(false);
        dropdown.onValueChanged.AddListener(index => change(index));
        return dropdown;
    }

    private static void FitDropdown(TMP_Dropdown dropdown)
    {
        if (dropdown == null || dropdown.template == null || dropdown.IsExpanded)
        {
            return;
        }

        var canvas = dropdown.GetComponentInParent<Canvas>()?.rootCanvas;
        if (canvas == null)
        {
            return;
        }

        var canvasRect = (RectTransform)canvas.transform;
        var corners = new Vector3[4];
        ((RectTransform)dropdown.transform).GetWorldCorners(corners);
        float bottom = canvasRect.InverseTransformPoint(corners[0]).y;
        float top = canvasRect.InverseTransformPoint(corners[1]).y;
        float available = Mathf.Max(bottom - canvasRect.rect.yMin, canvasRect.rect.yMax - top) - 24;
        var size = dropdown.template.sizeDelta;
        size.y = Mathf.Max(44, Mathf.Min(available, dropdown.options.Count * 44 + 8));
        dropdown.template.sizeDelta = size;
    }

    private static void PutSource(string section, string label)
    {
        var source = _options[_sourceIndex % _options.Count].Socket;
        if (_sources[section] is not JObject entries)
        {
            _sources[section] = entries = new JObject();
        }

        entries[label ?? source.EntityId + ":" + source.Socket.PartId] = new JObject
        {
            ["ship"] = source.EntityId,
            ["part"] = source.Socket.PartId,
            ["socket"] = source.Socket.SocketId,
        };
    }

    private static void Rebuild()
    {
        if (_bodyScroll != null)
        {
            _scrollPosition = _bodyScroll.verticalNormalizedPosition;
        }

        if (_root != null)
        {
            UnityEngine.Object.DestroyImmediate(_root);
        }

        _root = null;
        _feedback = null;
        _statusText = null;
        _syncText = null;
        _dropdown = null;
        _sourceDropdown = null;
        _body = null;
        _bodyScroll = null;
        BroadcastControls.Clear();
    }

    private static GameObject Node(Transform parent, string name, params Type[] components)
    {
        var obj = new GameObject(name, new[] { typeof(RectTransform) }.Concat(components).ToArray());
        obj.layer = parent.gameObject.layer;
        obj.transform.SetParent(parent, false);
        return obj;
    }

    private static Transform SourceScroll(Transform parent)
    {
        var root = Node(parent, "Sources (scroll)", typeof(ScrollRect), typeof(LayoutElement));
        var sizing = root.GetComponent<LayoutElement>();
        sizing.minHeight = 0;
        sizing.preferredHeight = 0;
        sizing.flexibleHeight = 1;
        var view = Node(root.transform, "Viewport", typeof(Image), typeof(RectMask2D));
        view.GetComponent<Image>().color = new Color(.04f, .09f, .12f, .5f);
        var viewport = (RectTransform)view.transform;
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = Vector2.zero;
        var content = Node(viewport, "Sources", typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        var rect = (RectTransform)content.transform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(.5f, 1);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var layout = content.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.spacing = 7;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandHeight = false;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = root.GetComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = rect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.scrollSensitivity = 32;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        AddScrollbar(scroll);
        return content.transform;
    }

    private static void AddScrollbar(ScrollRect scroll)
    {
        var track = Node(scroll.transform, "Scrollbar", typeof(Image), typeof(Scrollbar));
        track.GetComponent<Image>().color = new Color(.07f, .15f, .19f);
        var rect = (RectTransform)track.transform;
        rect.anchorMin = new Vector2(1, 0);
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(1, .5f);
        rect.sizeDelta = new Vector2(16, 0);
        rect.anchoredPosition = Vector2.zero;
        var handle = Node(track.transform, "Handle", typeof(Image));
        handle.GetComponent<Image>().color = new Color(.24f, .64f, .77f);
        var hr = (RectTransform)handle.transform;
        hr.anchorMin = Vector2.zero;
        hr.anchorMax = Vector2.one;
        hr.offsetMin = hr.offsetMax = Vector2.zero;
        var bar = track.GetComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.handleRect = hr;
        bar.targetGraphic = handle.GetComponent<Image>();
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarSpacing = 6;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
    }

    private static TMP_Text Label(Transform parent, string text)
    {
        var obj = Node(parent, "Label", typeof(TextMeshProUGUI), typeof(LayoutElement));
        var label = obj.GetComponent<TextMeshProUGUI>();
        label.font = _font ?? Support.Screens.Font(null);
        label.fontSize = 24;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.richText = false;
        label.text = text;
        label.color = new Color(.75f, .9f, .95f);
        label.raycastTarget = false;
        obj.GetComponent<LayoutElement>().minHeight = 34;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return label;
    }

    private static void Input(Transform parent, string title, string value, int limit, Action<string> change)
    {
        Label(parent, title);
        var obj = Node(parent, title, typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
        obj.GetComponent<Image>().color = new Color(.07f, .12f, .15f, .95f);
        obj.GetComponent<LayoutElement>().preferredHeight = 48;
        var view = Node(obj.transform, "Viewport", typeof(RectMask2D));
        var rect = (RectTransform)view.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(8, 3);
        rect.offsetMax = new Vector2(-8, -3);
        TMP_Text label = Label(view.transform, value);
        var textRect = (RectTransform)label.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var input = obj.GetComponent<TMP_InputField>();
        input.textViewport = rect;
        input.textComponent = label;
        input.targetGraphic = obj.GetComponent<Image>();
        input.characterLimit = limit;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.text = value;
        input.onValueChanged.AddListener(s => change(s));
    }

    private static void Button(Transform parent, string text, Action action)
    {
        var obj = Node(parent, text, typeof(Image), typeof(UnityEngine.UI.Button), typeof(LayoutElement));
        obj.GetComponent<Image>().color = new Color(.14f, .22f, .26f);
        obj.GetComponent<LayoutElement>().preferredHeight = 44;
        var label = Label(obj.transform, text);
        label.alignment = TextAlignmentOptions.Center;
        var rect = (RectTransform)label.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var button = obj.GetComponent<UnityEngine.UI.Button>();
        button.targetGraphic = obj.GetComponent<Image>();
        button.onClick.AddListener(() => action());
    }

    private static IEnumerable<IShipStateRead> AssemblyShips(ContentPart part)
    {
        IShipsProvider ships = ServiceLocator.GetService<IShipsClientProvider>();
        if (ships == null || !ships.TryGetShip(part.ShipId, out IShipStateRead current))
        {
            return Array.Empty<IShipStateRead>();
        }

        var candidates = new Dictionary<uint, IShipStateRead> { { current.Id, current } };
        if (ships.TryGetRootShip(current.Id, out var root))
        {
            candidates[root.Id] = root;
            foreach (var linked in ships.GetRootConnectedShips(root.Id))
            {
                // Use native data only for discovery. A rotor-only closure below
                // excludes docked neighbors from the actual picker.
                if (linked.Ship != null)
                {
                    candidates[linked.Ship.Id] = linked.Ship;
                }
            }
        }
        foreach (var ship in ships.GetAllShipsInSpace(current.SpaceId))
        {
            candidates[ship.Id] = ship;
        }

        var connected = new HashSet<uint> { current.Id };
        bool changed;
        do
        {
            changed = false;
            foreach (var ship in candidates.Values)
            {
                foreach (var piece in ship.StatefulParts)
                {
                    if (
                        piece.State is RotorState r
                        && candidates.ContainsKey(r.AnchoredShipId)
                        && (connected.Contains(ship.Id) || connected.Contains(r.AnchoredShipId))
                    )
                    {
                        changed |= connected.Add(ship.Id);
                        changed |= connected.Add(r.AnchoredShipId);
                    }
                }
            }
        } while (changed && connected.Count <= 128);
        return candidates.Values.Where(s => connected.Contains(s.Id)).OrderBy(s => s.Id).ToArray();
    }

    private static List<(EntityPartSocket, string)> SourceOptions(ContentPart part)
    {
        var result = new List<(EntityPartSocket, string)>();
        foreach (var ship in AssemblyShips(part))
        {
            foreach (var piece in ship.StatefulParts.OrderBy(p => p.Id))
            {
                bool supported =
                    piece.State is ConstantState
                    || piece.State is LeverState
                    || piece.State is MathGateState
                    || piece.State is SignalProcessorState
                    || piece.State is MemoryRegisterState
                    || piece.State is VelocitySensorState
                    || piece.State is AngularVelSensorState
                    || piece.State is GasSensorState
                    || piece.State is FluidTankState;
                if (!supported)
                {
                    continue;
                }

                byte[] sockets =
                    piece.State is ISignalEmitterState e ? e.EmissionSockets
                    : piece.State is ISignalProcessorState p ? p.EmissionSockets
                    : Array.Empty<byte>();
                foreach (byte socket in sockets)
                {
                    result.Add(
                        (
                            new EntityPartSocket(ship.Id, piece.Id, socket),
                            FunctionSourcePresentation.Label(part, new EntityPartSocket(ship.Id, piece.Id, socket))
                        )
                    );
                    if (result.Count >= 512)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }
}
