using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Players.Interface.Model;
using Players.Interface.Services;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using StellarFunctions.Game;
using UI.Inventory;
using UnityEngine;
using UnityEngine.UI;

namespace StellarFunctions.Content;

// Resolves the named regions of a native storage-style menu so the config
// panel can borrow its panel, grid and title styling.
public sealed class MenuLayout
{
    public GameObject Menu;
    public Transform Wrapper;
    public Transform Content;
    public Transform Panel;
    public Transform Grid;
    public Component Title;

    public bool Valid => Menu != null && Panel != null && Grid != null && Title != null;
    public bool CanAppend => Menu != null && Content != null && Title != null;

    public static MenuLayout Resolve(GameObject menu)
    {
        var layout = new MenuLayout { Menu = menu };
        if (menu == null)
            return layout;
        var grids = menu.GetComponentsInChildren<InventoryGrid>(true);
        layout.Wrapper = grids.Length != 0 ? grids[0].transform : menu.transform;
        layout.Content = layout.Wrapper.childCount > 0 ? layout.Wrapper.GetChild(0) : layout.Wrapper;
        Transform panel = layout.Wrapper;
        if (panel != null && panel.GetComponent<Image>() == null)
        {
            var image = panel.GetComponentInChildren<Image>(true);
            panel = image != null ? image.transform : null;
        }
        layout.Panel = panel ?? layout.Content;
        var gridGroup = layout.Wrapper.GetComponentInChildren<GridLayoutGroup>(true);
        if (gridGroup != null)
        {
            layout.Grid = gridGroup.transform;
            layout.Panel = gridGroup.transform.parent;
        }
        Component[] texts = layout
            .Wrapper.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && IsText(c))
            .ToArray();
        layout.Title =
            texts.FirstOrDefault(t => layout.Panel != null && t.transform.parent == layout.Panel)
            ?? texts.FirstOrDefault(t => t.GetComponentInParent<GridLayoutGroup>(true) == null)
            ?? texts.FirstOrDefault();
        return layout;
    }

    public bool SetTitle(string text) => Write(Title, text);

    public static bool Write(Component target, string text)
    {
        if (target == null)
            return false;
        foreach (Component component in target.GetComponents<Component>())
            if (component != null && component.GetType().Name == "LocalizeStringEvent")
                DestroyImmediate(component);
        var property = target.GetType().GetProperty("text", typeof(string));
        if (property == null || !property.CanWrite)
            return false;
        property.SetValue(target, text, null);
        return true;
    }

    public static bool IsText(Component component)
    {
        for (Type type = component?.GetType(); type != null; type = type.BaseType)
            if (type.Name == "TMP_Text" || type.Name == "TextMeshProUGUI" || type.Name == "Text")
                return true;
        return false;
    }

    private static void DestroyImmediate(UnityEngine.Object obj) => UnityEngine.Object.DestroyImmediate(obj);
}

// Side-panel configuration UI appended to the native storage menu when a
// custom part is interacted with. FunctionEditor patches A() to replace row
// rendering for function parts, so the private names below are a contract.
public static class ContentConfigMenu
{
    private static GameObject _open;
    private static ContentPart _part;
    private static MenuLayout _layout;
    private static GameObject _side;

    public static GameObject Root => _open;
    public static bool IsOpen => _open != null;
    public static ContentPart Current => _part;

    public static bool TryAppend(GameObject menu, ContentPart part, PartBehaviour behaviour, bool replace = false) =>
        Append(menu, part, behaviour, replace);

    public static bool TryAppendClient(
        GameObject menu,
        ContentPart part,
        PartBehaviour behaviour,
        bool replace = false
    ) => Append(menu, part, behaviour, replace);

    private static bool Append(GameObject menu, ContentPart part, PartBehaviour behaviour, bool replace)
    {
        if (menu == null || part.Def == null || behaviour == null)
            return false;
        try
        {
            MenuLayout layout = MenuLayout.Resolve(menu);
            if (!layout.CanAppend || layout.Panel == null)
            {
                ContentLog.Once(
                    "config menu",
                    "noappend",
                    ContentLogLevel.Warn,
                    "menu has no panel to sit a port panel beside"
                );
                return false;
            }
            GameObject side = BuildSide(layout, replace, behaviour.PanelScale, out Transform grid, out Component title);
            if (side == null || grid == null)
            {
                ContentLog.Error("config menu", "could not build the port panel: side panel or row grid missing");
                return false;
            }
            _open = menu;
            _part = part;
            _layout = layout;
            _layout.Grid = grid;
            _side = side;
            string titleText = behaviour.ConfigTitle(part) ?? part.Def.Label;
            if (title != null)
                MenuLayout.Write(title, titleText);
            Rebuild();
            ContentLog.Info("config menu", "port panel opened beside " + part.Label + "'s menu");
            return true;
        }
        catch (Exception error)
        {
            ContentLog.Error("config menu", "could not build the port panel", error);
            return false;
        }
    }

    public static void Forget()
    {
        _side = null;
        _open = null;
        _part = default;
        _layout = null;
    }

    // Find the panel ancestor of MenuLayout.Panel that contains an Image and
    // text, i.e. the card the side panel should sit beside.
    private static Transform PanelCard(MenuLayout layout)
    {
        Transform menu = layout.Menu != null ? layout.Menu.transform : null;
        for (Transform node = layout.Panel; node != null && node != menu; node = node.parent)
        {
            if (
                node.GetComponent<Image>() != null
                && node.Cast<Transform>().Any(child => child.GetComponents<Component>().Any(MenuLayout.IsText))
            )
                return node;
        }
        return layout.Panel;
    }

    private static string PathOf(Transform t)
    {
        string p = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }

    // Clone the donor panel card into a side panel with a fresh row grid.
    private static GameObject BuildSide(
        MenuLayout layout,
        bool replace,
        Vector2 scale,
        out Transform grid,
        out Component title
    )
    {
        grid = null;
        title = null;
        Transform card = PanelCard(layout);
        var cardRect = card as RectTransform;
        GameObject side = UnityEngine.Object.Instantiate(card.gameObject, card.parent);
        side.name = "StellarFunctionsPortPanel";
        side.SetActive(true);
        CleanClones(side);
        var sideRect = side.GetComponent<RectTransform>();
        if (sideRect == null)
        {
            ContentLog.Error(
                "config menu",
                "could not build the port panel: cloned card has no RectTransform (card="
                    + card.name
                    + ", path="
                    + PathOf(card)
                    + ")"
            );
            UnityEngine.Object.Destroy(side);
            return null;
        }
        float width = cardRect != null ? cardRect.rect.width : 520f;
        sideRect.anchoredPosition =
            (cardRect != null ? cardRect.anchoredPosition : Vector2.zero)
            + (replace ? Vector2.zero : new Vector2(width + 40f, 0f));
        if (replace)
        {
            card.gameObject.SetActive(false);
            sideRect.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Horizontal,
                sideRect.rect.width * Mathf.Max(0.5f, scale.x)
            );
            sideRect.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Vertical,
                sideRect.rect.height * Mathf.Max(0.5f, scale.y)
            );
        }
        Component[] texts = side.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && MenuLayout.IsText(c))
            .ToArray();
        title = texts.FirstOrDefault(t => t.transform.parent == side.transform) ?? texts.FirstOrDefault();
        var existing = side.GetComponentInChildren<GridLayoutGroup>(true);
        if (existing != null && existing.transform.parent != side.transform)
        {
            Destroy(existing.gameObject);
            existing = null;
        }
        if (existing != null)
        {
            grid = existing.transform;
            foreach (Transform child in grid.Cast<Transform>().ToList())
                DestroyImmediate(child.gameObject);
            DestroyImmediate(existing);
        }
        else
        {
            var rows = new GameObject("Rows", typeof(RectTransform)) { layer = side.layer };
            rows.transform.SetParent(side.transform, false);
            grid = rows.transform;
        }
        if (grid is RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(34f, 34f);
            rect.offsetMax = new Vector2(-34f, -92f);
        }
        foreach (Component text in texts)
            if (text != title && text != null && !text.transform.IsChildOf(grid))
                text.gameObject.SetActive(false);
        var layoutGroup = grid.gameObject.AddComponent<VerticalLayoutGroup>();
        layoutGroup.spacing = 10f;
        layoutGroup.padding = new RectOffset(6, 6, 6, 6);
        layoutGroup.childAlignment = TextAnchor.UpperLeft;
        layoutGroup.childForceExpandHeight = false;
        layoutGroup.childForceExpandWidth = false;
        layoutGroup.childControlHeight = true;
        layoutGroup.childControlWidth = true;
        return side;
    }

    private static readonly string[] KeptComponents =
    {
        "RectTransform",
        "CanvasRenderer",
        "Image",
        "RawImage",
        "Canvas",
        "CanvasGroup",
        "Mask",
        "RectMask2D",
        "Shadow",
        "Outline",
        "LayoutElement",
        "ContentSizeFitter",
        "VerticalLayoutGroup",
        "HorizontalLayoutGroup",
        "GridLayoutGroup",
        "AspectRatioFitter",
    };

    private static readonly string[] ContentComponents =
    {
        "InventorySlotUI",
        "DestroySlotUI",
        "ItemTooltip",
        "CursorFollow",
        "HoldItemCursorActivator",
        "InventorySlotProvider",
        "ItemSlotLocationProvider",
    };

    // Remove leftover interactive components from a freshly cloned panel:
    // first the inventory-slot GameObjects, then any component that is not a
    // transform, text or kept UI primitive (three passes for dependents).
    private static void CleanClones(GameObject clone)
    {
        var slotHosts = clone
            .GetComponentsInChildren<Component>(true)
            .Where(c => c != null && ContentComponents.Contains(c.GetType().Name))
            .Select(c => c.gameObject)
            .Where(go => go != clone)
            .Distinct()
            .ToList();
        foreach (GameObject host in slotHosts)
            if (host != null)
                DestroyImmediate(host);
        var strippable = clone
            .GetComponentsInChildren<Component>(true)
            .Where(c =>
                c != null && !(c is Transform) && !MenuLayout.IsText(c) && !KeptComponents.Contains(c.GetType().Name)
            )
            .ToList();
        for (int pass = 0; pass < 3 && strippable.Count > 0; pass++)
        {
            var retry = new List<Component>();
            foreach (Component component in strippable)
            {
                if (component == null)
                    continue;
                try
                {
                    DestroyImmediate(component);
                }
                catch
                {
                    retry.Add(component);
                }
            }
            strippable = retry;
        }
        foreach (Component component in strippable)
            if (component != null)
                ContentLog.Once(
                    "config menu",
                    "keep" + component.GetType().Name,
                    ContentLogLevel.Warn,
                    "could not strip " + component.GetType().Name + " from the port panel clone"
                );
    }

    private static void DestroyImmediate(UnityEngine.Object obj)
    {
        if (obj != null)
            UnityEngine.Object.DestroyImmediate(obj);
    }

    // Harmony patch target kept for FunctionEditor, which owns Functions row
    // rendering. The generic ConfigRow pipeline was removed with the
    // Functions-only kit; this private parameterless signature is a contract.
    private static void A() { }

    private static void Rebuild() => A();

    private static void Destroy(UnityEngine.Object obj)
    {
        if (obj != null)
            UnityEngine.Object.Destroy(obj);
    }
}

// Storage-menu hook: retitles menus for custom parts and opens the config
// side panel. Functions edits authenticate through ContentActions instead.
public static class ContentMenus
{
    private static bool _patched;
    private static FieldInfo _currentMenu;

    public static void Install()
    {
        if (_patched)
            return;
        _patched = true;
        var method = AccessTools.Method(typeof(StorageMenuActivator), "ShowMenu", Type.EmptyTypes, null);
        if (method == null)
        {
            ContentLog.Warn(
                "menus",
                "StorageMenuActivator.ShowMenu not found, custom part menus keep the donor's title"
            );
            return;
        }
        _currentMenu = AccessTools.Field(typeof(StorageMenuActivator), "_currentMenu");
        new HarmonyLib.Harmony("stellar.functions.kit.menus").Patch(
            method,
            postfix: new HarmonyMethod(typeof(ContentMenus), nameof(AfterShowMenu))
        );
        ContentLog.Trace("menus", "storage menu title hook installed");
    }

    // Harmony postfix on StorageMenuActivator.ShowMenu().
    public static void AfterShowMenu(StorageMenuActivator __instance)
    {
        if (_currentMenu == null)
            return;
        var menu = _currentMenu.GetValue(__instance) as GameObject;
        if (menu == null)
        {
            ContentLog.Once("menus", "nomenu", ContentLogLevel.Warn, "storage menu opened but _currentMenu was null");
            return;
        }
        PartSettings settings = OpenPart();
        if (settings == null)
        {
            ContentLog.Once(
                "menus",
                "nopart",
                ContentLogLevel.Warn,
                "storage menu opened but the interacted part could not be resolved"
            );
            return;
        }
        ContentPartDef def = ContentRegistry.PartDef(settings.id);
        if (def == null)
        {
            ContentLog.Trace("menus", "opened vanilla part " + settings.name + " id " + settings.id);
            return;
        }
        ContentLog.Info("menus", def.Label + " opened, menu mode " + def.Menu);
        if (!def.Attached)
            Retitle(menu, def.Label);
        bool panel = string.Equals(def.Menu, "panel", StringComparison.OrdinalIgnoreCase);
        if (panel || string.Equals(def.Menu, "config", StringComparison.OrdinalIgnoreCase))
            OpenPanel(menu, def, panel);
    }

    private static void OpenPanel(GameObject menu, ContentPartDef def, bool replace)
    {
        PartBehaviour behaviour = ContentRuntime.Of(def.Id);
        if (behaviour == null)
            return;
        var players = Support.NativeServices.CachedService<IPlayersProviderClient>();
        PlayerState state = default;
        if (players == null || !players.TryGetLocalPlayerState(out state))
            return;
        var serverShips = Support.NativeServices.CachedService<ShipsServerTracker>();
        TrackedShipServer serverShip = serverShips == null ? null : serverShips.GetShip(state.EntityId);
        if (
            serverShip != null
            && ContentRuntime.TryResolve((IShipStateWrite)(object)serverShip, state.PartId, out var resolved)
        )
        {
            ContentConfigMenu.TryAppend(menu, resolved, behaviour, replace);
            return;
        }
        var clientShips = Support.NativeServices.CachedService<IShipsClientProvider>();
        IShipStateReadClient ship = default;
        StatefulPart part = default;
        if (
            clientShips == null
            || !clientShips.TryGetShip(state.EntityId, out ship)
            || !((IShipStateRead)ship).TryGetStatefulPart(state.PartId, out part)
        )
        {
            ContentLog.Once(
                "menus",
                "noclientconfig",
                ContentLogLevel.Warn,
                "client port settings could not resolve the selected part"
            );
            return;
        }
        resolved = new ContentPart
        {
            ShipId = state.EntityId,
            PartId = state.PartId,
            Part = part,
            Def = def,
        };
        ContentConfigMenu.TryAppendClient(menu, resolved, behaviour, replace);
    }

    // PartSettings of the part the local player is interacting with, resolved
    // through the client ship provider.
    public static PartSettings OpenPart()
    {
        var players = Support.NativeServices.CachedService<IPlayersProviderClient>();
        var ships = Support.NativeServices.CachedService<IShipsClientProvider>();
        if (players == null || ships == null)
        {
            ContentLog.Once(
                "menus",
                "noservices",
                ContentLogLevel.Warn,
                "player or ship provider missing, menu titles left alone"
            );
            return null;
        }
        PlayerState state = default;
        if (!players.TryGetLocalPlayerState(out state))
        {
            ContentLog.Once(
                "menus",
                "nostate",
                ContentLogLevel.Warn,
                "local player state unavailable while a menu was open"
            );
            return null;
        }
        IShipStateReadClient ship = default;
        if (!ships.TryGetShip(state.EntityId, out ship))
        {
            ContentLog.Once(
                "menus",
                "noship",
                ContentLogLevel.Warn,
                "ship " + state.EntityId + " not found on the client while a menu was open"
            );
            return null;
        }
        StatefulPart part = default;
        if (!((IShipStateRead)ship).TryGetStatefulPart(state.PartId, out part))
        {
            ContentLog.Once(
                "menus",
                "nostateful",
                ContentLogLevel.Warn,
                "part " + state.PartId + " not found on ship " + state.EntityId
            );
            return null;
        }
        return part.Settings;
    }

    public static bool Retitle(GameObject menu, string title)
    {
        if (menu == null || string.IsNullOrEmpty(title))
            return false;
        MenuLayout layout = MenuLayout.Resolve(menu);
        if (layout.Title != null && layout.SetTitle(title))
        {
            ContentLog.Trace("menus", "menu titled " + title);
            return true;
        }
        ContentLog.Once("menus", "notitle", ContentLogLevel.Warn, "menu had no title text, left alone");
        return false;
    }
}
