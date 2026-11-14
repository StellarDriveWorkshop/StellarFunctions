using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Ships.Interface.Model.Parts.Common;
using StellarFunctions.Content;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StellarFunctions;

// Focused responsive-layout and draft-lifecycle checks against the live native
// SDK frame already opened by UiRedesignChecks. No mock editor hierarchy.
internal static class ResponsiveEditorChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Editor = typeof(FunctionCallState).Assembly.GetType(
        "StellarFunctions.FunctionEditor",
        true
    );

    internal static void Run(ContentPart part, Action<bool, string> check)
    {
        var root = (GameObject)Editor.GetField("_root", PrivateStatic).GetValue(null);
        check(root != null, "responsive editor fixture uses the open native SDK menu");
        if (root == null)
        {
            return;
        }

        var canvas = ContentConfigMenu.Root.GetComponentInParent<Canvas>();
        check(canvas != null, "responsive editor has a native menu canvas");
        if (canvas == null)
        {
            return;
        }

        var scaler = canvas.GetComponent<CanvasScaler>();
        Vector2 oldReference = scaler != null ? scaler.referenceResolution : Vector2.zero;
        float oldMatch = scaler != null ? scaler.matchWidthOrHeight : 0;
        try
        {
            if (scaler != null)
            {
                // ScaleWithScreenSize turns this into a short logical viewport
                // while preserving the real canvas, SDK frame and UI objects.
                scaler.referenceResolution = new Vector2(1920, 400);
                scaler.matchWidthOrHeight = 1;
                Canvas.ForceUpdateCanvases();
                AccessTools.Method(Editor, "Tick").Invoke(null, null);
                Canvas.ForceUpdateCanvases();
                CheckPanelAndFooter(root, canvas, check, "short canvas");
            }
            else
            {
                check(false, "native menu CanvasScaler is available for short-window regression");
            }

            CheckScroll(root, check);
            if (scaler != null)
            {
                scaler.referenceResolution = oldReference;
                scaler.matchWidthOrHeight = oldMatch;
                Canvas.ForceUpdateCanvases();
                AccessTools.Method(Editor, "Tick").Invoke(null, null);
                Canvas.ForceUpdateCanvases();
            }
            if (part.Part.State is FunctionReturnState)
            {
                CheckAdvancedAndDraft(root, part, check);
            }
            else if (part.Part.State is FunctionCallState)
            {
                CheckCallCaptionAndDraft(root, check);
            }
        }
        finally
        {
            if (scaler != null)
            {
                scaler.referenceResolution = oldReference;
                scaler.matchWidthOrHeight = oldMatch;
                Canvas.ForceUpdateCanvases();
                AccessTools.Method(Editor, "Tick").Invoke(null, null);
                Canvas.ForceUpdateCanvases();
            }
            if (part.Part.State is FunctionCallState)
            {
                root = (GameObject)Editor.GetField("_root", PrivateStatic).GetValue(null);
                var scroll = root
                    ?.GetComponentsInChildren<ScrollRect>(true)
                    .FirstOrDefault(s => s.name == "Sources (scroll)");
                if (scroll?.content != null && scroll.viewport != null && scroll.verticalScrollbar != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
                    Canvas.ForceUpdateCanvases();
                    scroll.Rebuild(CanvasUpdate.PostLayout);
                    AccessTools.Method(typeof(ScrollRect), "LateUpdate")?.Invoke(scroll, null);
                    Canvas.ForceUpdateCanvases();
                    bool fits = scroll.content.rect.height <= scroll.viewport.rect.height + 2f;
                    check(
                        fits,
                        "restored Call content fits the viewport before scrollbar visibility is checked (content="
                            + scroll.content.rect.height
                            + ", viewport="
                            + scroll.viewport.rect.height
                            + ")"
                    );
                    check(
                        !scroll.verticalScrollbar.gameObject.activeInHierarchy,
                        "scrollbar hides again when restored Call content fits the viewport"
                    );
                }
            }
            if (part.Part.State is FunctionReturnState)
            {
                typeof(UiRedesignChecks)
                    .GetMethod("RenderCanvas", PrivateStatic)
                    .Invoke(null, new object[] { canvas, "return-advanced-sources", 1280, 720 });
            }
        }
    }

    private static void CheckPanelAndFooter(GameObject root, Canvas canvas, Action<bool, string> check, string label)
    {
        var panel = (RectTransform)root.transform.parent.parent;
        var canvasRect = (RectTransform)canvas.transform;
        var panelCorners = Corners(panel);
        var canvasCorners = Corners(canvasRect);
        check(Contains(canvasCorners, panelCorners, 2f), label + " editor panel remains inside the canvas");
        var apply = root.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "Apply");
        if (apply != null)
        {
            check(
                Contains(panelCorners, Corners((RectTransform)apply.transform), 2f),
                label + " fixed Apply button remains inside the panel"
            );
            check(
                apply.transform.parent == root.transform,
                label + " Apply stays in the fixed footer outside the scroll content"
            );
        }
        var scroll = root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.name == "Sources (scroll)");
        if (scroll != null)
        {
            check(
                Contains(panelCorners, Corners((RectTransform)scroll.transform), 2f),
                label + " flexible content viewport remains inside the panel"
            );
            check(
                scroll.transform.parent == root.transform,
                label + " Sources viewport is the flexible sibling between header and footer"
            );
        }
        var feedback = (TMP_Text)Editor.GetField("_feedback", PrivateStatic).GetValue(null);
        if (feedback != null)
        {
            check(feedback.transform.parent == root.transform, label + " feedback stays visible in the fixed footer");
        }
    }

    private static void CheckScroll(GameObject root, Action<bool, string> check)
    {
        var scroll = root.GetComponentsInChildren<ScrollRect>(true).FirstOrDefault(s => s.name == "Sources (scroll)");
        check(scroll != null, "editor content is hosted in the flexible Sources scroll viewport");
        if (scroll == null)
        {
            return;
        }

        check(
            scroll.vertical && scroll.content != null && scroll.viewport != null,
            "Sources scroll has a vertical content viewport"
        );
        check(
            scroll.verticalScrollbar != null
                && scroll.verticalScrollbarVisibility == ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport,
            "Sources scroll uses a real auto-hiding vertical scrollbar"
        );
        if (scroll.content == null || scroll.viewport == null || scroll.verticalScrollbar == null)
        {
            return;
        }

        var added = new System.Collections.Generic.List<GameObject>();
        try
        {
            for (int i = 0; i < 24; i++)
            {
                var row = new GameObject(
                    "Responsive overflow fixture row " + i,
                    typeof(RectTransform),
                    typeof(LayoutElement)
                );
                row.layer = scroll.content.gameObject.layer;
                row.transform.SetParent(scroll.content, false);
                row.GetComponent<LayoutElement>().preferredHeight = 48;
                added.Add(row);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.viewport);
            Canvas.ForceUpdateCanvases();
            scroll.Rebuild(CanvasUpdate.PostLayout);
            Canvas.ForceUpdateCanvases();
            check(
                scroll.content.rect.height > scroll.viewport.rect.height + 100,
                "overflow content exceeds the viewport and remains scrollable"
            );
            check(
                scroll.verticalScrollbar.gameObject.activeInHierarchy && scroll.verticalScrollbar.size < .99f,
                "visible scrollbar thumb reflects actual overflow"
            );
        }
        finally
        {
            foreach (var row in added)
            {
                UnityEngine.Object.DestroyImmediate(row);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            Canvas.ForceUpdateCanvases();
        }
    }

    private static void CheckAdvancedAndDraft(GameObject root, ContentPart part, Action<bool, string> check)
    {
        var advanced = root.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.name == "Advanced · External signals");
        check(advanced != null, "Return editor exposes a collapsed Advanced · External signals section");
        if (advanced == null)
        {
            return;
        }

        check(
            !root.GetComponentsInChildren<TMP_InputField>(true).Any(i => i.name == "Search sources"),
            "external source search stays collapsed until requested"
        );

        var nameInput = root.GetComponentsInChildren<TMP_InputField>(true).FirstOrDefault(i => i.name == "Name");
        check(nameInput != null, "Return name remains in the always-visible editor controls");
        if (nameInput != null)
        {
            nameInput.text = "Draft survives directory refresh";
            if (EventSystem.current != null)
            {
                nameInput.OnSelect(new BaseEventData(EventSystem.current));
                EventSystem.current.SetSelectedGameObject(nameInput.gameObject);
            }
            nameInput.ActivateInputField();
            check(
                EventSystem.current != null && EventSystem.current.currentSelectedGameObject == nameInput.gameObject,
                "native EventSystem selection is on the name field before the directory refresh fixture"
            );
            check(
                !(bool)AccessTools.Method(Editor, "CanRebuild").Invoke(null, null),
                "focused name field blocks editor rebuilds"
            );
            var before = nameInput;
            RefreshDirectory(part, "Refreshed candidate");
            AccessTools.Method(Editor, "Tick").Invoke(null, null);
            root = (GameObject)Editor.GetField("_root", PrivateStatic).GetValue(null);
            check(root != null, "focused directory refresh retains a live editor root");
            if (root == null)
            {
                return;
            }

            var current = root.GetComponentsInChildren<TMP_InputField>(true).FirstOrDefault(i => i.name == "Name");
            check(ReferenceEquals(before, current), "directory refresh defers rebuild while the name field is focused");
            check(current?.text == "Draft survives directory refresh", "Return name draft survives directory refresh");
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            nameInput.DeactivateInputField();
        }

        InjectSourceOptions("Alpha fuel sensor", "Beta velocity sensor");
        advanced.onClick.Invoke();
        AccessTools.Method(Editor, "Tick").Invoke(null, null);
        AccessTools.Method(Editor, "Tick").Invoke(null, null);
        root = (GameObject)Editor.GetField("_root", PrivateStatic).GetValue(null);
        check(root != null, "expanded source editor rebuilds after the focused draft is released");
        if (root == null)
        {
            return;
        }

        var search = root.GetComponentsInChildren<TMP_InputField>(true).FirstOrDefault(i => i.name == "Search sources");
        var sourceDropdown = root.GetComponentsInChildren<TMP_Dropdown>(true)
            .FirstOrDefault(d => d.name == "Source dropdown");
        var identify = root.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "Identify source");
        check(
            search != null && sourceDropdown != null && identify != null,
            "expanded source controls provide search, selection and explicit Identify source action"
        );
        if (search == null || sourceDropdown == null || identify == null)
        {
            return;
        }

        search.text = "beta";
        check(
            sourceDropdown.options.Count == 2
                && sourceDropdown.options[0].text == "Select a source…"
                && sourceDropdown.options[1].text.Contains("Beta velocity sensor"),
            "source search filters actual dropdown options by display name"
        );
        var options = (IEnumerable)Editor.GetField("_options", PrivateStatic).GetValue(null);
        int sourceIndex = (int)Editor.GetField("_sourceIndex", PrivateStatic).GetValue(null);
        object selected = options.Cast<object>().ElementAt(sourceIndex);
        var details = root.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(t => t.name == "Selected source details");
        check(
            (string)selected.GetType().GetField("Item2").GetValue(selected) == "Alpha fuel sensor"
                && sourceDropdown.value == 0
                && details?.text.Contains("Alpha fuel sensor") == true,
            "filtering away the cached selected socket keeps its stable identity and shows a placeholder"
        );
        search.text = "";
        check(
            sourceDropdown.options.Count == 3
                && sourceDropdown.options.Skip(1).Count(o => o.text.Contains("sensor")) == 2,
            "clearing source search restores all available choices after the placeholder"
        );
        sourceDropdown.value = sourceDropdown.options.FindIndex(o => o.text.Contains("Beta velocity sensor"));
        sourceIndex = (int)Editor.GetField("_sourceIndex", PrivateStatic).GetValue(null);
        selected = options.Cast<object>().ElementAt(sourceIndex);
        check(
            (string)selected.GetType().GetField("Item2").GetValue(selected) == "Beta velocity sensor",
            "source dropdown callback selects the matching stable socket identity"
        );
        search.text = "alpha";
        sourceIndex = (int)Editor.GetField("_sourceIndex", PrivateStatic).GetValue(null);
        selected = options.Cast<object>().ElementAt(sourceIndex);
        check(
            (string)selected.GetType().GetField("Item2").GetValue(selected) == "Beta velocity sensor"
                && sourceDropdown.value == 0,
            "filtering away the newly selected socket preserves that cached identity"
        );
        search.text = "";
        identify.onClick.Invoke();
        string notice = (string)Editor.GetField("_notice", PrivateStatic).GetValue(null);
        check(notice.Contains("Beta velocity sensor"), "Identify source callback reports the selected source identity");
    }

    private static void CheckCallCaptionAndDraft(GameObject root, Action<bool, string> check)
    {
        var dropdown = root.GetComponentsInChildren<TMP_Dropdown>(true)
            .FirstOrDefault(d => d.name == "Function dropdown");
        if (dropdown == null)
        {
            dropdown = root.GetComponentsInChildren<TMP_Dropdown>(true).FirstOrDefault();
        }

        check(dropdown != null, "Call editor retains its explicit function dropdown");
        if (dropdown == null)
        {
            return;
        }

        var originalOptions = dropdown.options.ToList();
        dropdown.options = Enumerable
            .Range(0, 40)
            .Select(i => new TMP_Dropdown.OptionData("Tall dropdown item " + i))
            .ToList();
        AccessTools.Method(Editor, "FitDropdown").Invoke(null, new object[] { dropdown });
        check(dropdown.template.rect.height > 250, "long dropdown uses available screen height before scrolling");
        var canvasRect = (RectTransform)dropdown.GetComponentInParent<Canvas>().rootCanvas.transform;
        check(
            dropdown.template.rect.height <= canvasRect.rect.height - 24,
            "adaptive dropdown height stays bounded by the native canvas"
        );
        dropdown.options = originalOptions;
        AccessTools.Method(Editor, "FitDropdown").Invoke(null, new object[] { dropdown });
        string before = dropdown.captionText?.text;
        var sentField = typeof(UiRedesignChecks).GetField("Sent", BindingFlags.Static | BindingFlags.NonPublic);
        sentField.SetValue(null, null);
        AccessTools.Method(typeof(TMP_Dropdown), "Start")?.Invoke(dropdown, null);
        dropdown.Show();
        dropdown.Hide();
        check(
            dropdown.captionText?.text == before
                && ((FunctionCallState)ContentConfigMenu.Current.Part.State).SelectedFunctionId
                    == "11111111111111111111111111111111"
                && sentField.GetValue(null) == null,
            "opening and closing Call choices preserves the authoritative caption and sends no selection action"
        );
    }

    private static void RefreshDirectory(ContentPart part, string name)
    {
        Type directory = typeof(FunctionCallState).Assembly.GetType("StellarFunctions.FunctionDirectory", true);
        var target = ((FunctionPartState)part.Part.State).StableId;
        var begin = directory.GetMethod("Begin", PrivateStatic);
        var accept = directory.GetMethod("Accept", PrivateStatic);
        var sequence = directory.GetField("_sequence", PrivateStatic);
        begin.Invoke(null, new object[] { target });
        var session = (string)directory.GetField("_session", PrivateStatic).GetValue(null);
        long next = (long)sequence.GetValue(null) + 1;
        sequence.SetValue(null, next);
        var entry = new
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Origin = "Test Beacon · structure #77",
            Status = "TEMPLATE",
            Revision = new string('b', 64),
            Parameters = "x=0",
            Broadcast = false,
            Published = false,
            Local = true,
        };
        string json = JsonConvert.SerializeObject(
            new
            {
                Target = target.ToString("N"),
                Session = session,
                Sequence = next,
                Eligible = false,
                Truncated = false,
                Entries = new[] { entry },
            }
        );
        accept.Invoke(null, new object[] { json });
    }

    private static void InjectSourceOptions(params string[] names)
    {
        var field = Editor.GetField("_options", PrivateStatic);
        var old = field.GetValue(null);
        var elementType = old.GetType().GetGenericArguments()[0];
        var list = (IList)
            Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(elementType));
        for (int i = 0; i < names.Length; i++)
        {
            var socket = new EntityPartSocket(7001, (ushort)(7002 + i), 0);
            list.Add(Activator.CreateInstance(elementType, new object[] { socket, names[i] }));
        }
        field.SetValue(null, list);
    }

    private static Vector3[] Corners(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return corners;
    }

    private static bool Contains(Vector3[] outer, Vector3[] inner, float margin)
    {
        float left = outer.Min(p => p.x) + margin,
            right = outer.Max(p => p.x) - margin;
        float bottom = outer.Min(p => p.y) + margin,
            top = outer.Max(p => p.y) - margin;
        return inner.All(p => p.x >= left && p.x <= right && p.y >= bottom && p.y <= top);
    }
}
