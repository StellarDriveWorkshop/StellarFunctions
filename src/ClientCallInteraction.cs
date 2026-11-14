using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Services;
using HarmonyLib;
using Players.Interface.Model;
using Players;
using Players.Interface.Services;
using Managers.Server.Services;
using StellarFunctions.Content;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using UnityEngine;
using UnityEngine.Localization;
using UI.Inventory;
using Ships.Interface.Model.Parts.StateTypes;
using UnityEngine.InputSystem;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Players.LocalClient.Controls;

namespace StellarFunctions;

internal static class ClientCallInteraction
{
    internal const ushort CallPartId = 62005;
    internal const string SelectAction = "select-function:";

    private static HarmonyLib.Harmony _harmony;
    private static bool _functionMenuWasOpen;

    internal static void Install(string harmonyId)
    {
        if (_harmony != null)
            return;

        _harmony = new HarmonyLib.Harmony(harmonyId);

        // Functions product finishing runs through the support factory's
        // explicit post-configuration hook: once per part, after generic
        // configuration and before socket seating.
        Support.PartFactory.AfterConfigure = AfterCallPrefabConfigured;
        MethodInfo serverInteract = AccessTools.Method(typeof(ShipInteractionService), "PlayerStartInteracting",
            new[] { typeof(TrackedPlayerServer), typeof(TrackedShipServer), typeof(StatefulPart) });
        if (serverInteract == null)
            throw new MissingMethodException(typeof(ShipInteractionService).FullName, "PlayerStartInteracting");
        _harmony.Patch(serverInteract, prefix: new HarmonyMethod(typeof(ClientCallInteraction), nameof(StartFunctionInteraction)));
        MethodInfo menu=AccessTools.Method(typeof(StorageMenuActivator),"TryGetRelevantMenuPrefab",
            new[]{typeof(IStatefulPartState),typeof(GameObject).MakeByRefType()});
        if(menu==null)throw new MissingMethodException("StorageMenuActivator.TryGetRelevantMenuPrefab");
        _harmony.Patch(menu,prefix:new HarmonyMethod(typeof(ClientCallInteraction),nameof(FunctionMenuPrefab)));
        var leave=AccessTools.Method(typeof(PlayerLeavableObjectControl),"OnLeave",new[]{typeof(InputAction.CallbackContext)});
        if(leave==null)throw new MissingMethodException("PlayerLeavableObjectControl.OnLeave");
        _harmony.Patch(leave,prefix:new HarmonyMethod(typeof(ClientCallInteraction),nameof(FunctionLeaveInput)));
        var inventoryOpen=AccessTools.Method(typeof(PlayerInventoryActivator),"SetOpen",new[]{typeof(bool)});
        if(inventoryOpen==null)throw new MissingMethodException("PlayerInventoryActivator.SetOpen");
        _harmony.Patch(inventoryOpen,prefix:new HarmonyMethod(typeof(ClientCallInteraction),nameof(InventoryOpen)));
        FunctionEditor.Install(_harmony);
        // The support layer's generic 6 cm socket stand-off changes cable anchors after
        // JSON layout. These native-shaped housings already seat their sockets.
        _harmony.Patch(AccessTools.Method(typeof(Support.SocketSeating),"Seat"),prefix:new HarmonyMethod(typeof(ClientCallInteraction),nameof(KeepNativeSocketSeats)));
        // The kit installs before the dedicated NetworkManager exists; action
        // and directory networking reconcile through their own EnsureNetworking.
    }

    private static bool KeepNativeSocketSeats(GameObject prefab)=>prefab==null||prefab.GetComponentInChildren<FunctionVisualContext>(true)==null;

    private static bool FunctionMenuPrefab(StorageMenuActivator __instance,IStatefulPartState partState,ref GameObject menuPrefab,ref bool __result)
    {
        if(partState is not FunctionPartState)return true;
        menuPrefab=AccessTools.Field(typeof(StorageMenuActivator),"smallStorageMenu").GetValue(__instance) as GameObject;
        __result=menuPrefab!=null;return false;
    }
    private static bool FunctionLeaveInput(InputAction.CallbackContext context)
    {
        if(!context.started||!ContentConfigMenu.IsOpen||ContentConfigMenu.Current.Part?.State is not FunctionPartState state)return true;
        // A typed F in a Param/Return text field is text, never a leave gesture.
        if(IsTyping())return false;
        return true;
    }
    private static bool IsTyping()
    {
        var current=EventSystem.current?.currentSelectedGameObject;
        if(current==null)return false;
        var tmp=current.GetComponent<TMP_InputField>();
        if(tmp!=null&&tmp.isFocused)return true;
        var input=current.GetComponent<InputField>();
        return input!=null&&input.isFocused;
    }

    private static bool StartFunctionInteraction(TrackedPlayerServer player, TrackedShipServer ship, StatefulPart part)
    {
        if (part.State is not FunctionPartState) return true;
        if (!FunctionEdits.CanReach(player,ship,part)) return false;
        // The native RPC already resolved the authenticated connection and requires
        // Walking. Its stock dispatcher has no case for a custom interactive state.
        // Preserve its native exclusive-use transition; assign only our part's UI kind.
        if (ship.StartInteracting(part.Id))
            player.SetState(new PlayerState
            {
                // The content action channel and configuration menu use the native
                // storage interaction state (9), not GenericPart (8).
                Type = PlayerStateType.InteractingWithChest,
                EntityId = ship.Id,
                PartId = part.Id,
                SubPartId = 0
            });
        return false;
    }

    private static void AfterCallPrefabConfigured(Support.PartSpec spec, PartSettings settings, GameObject prefab)
    {
        if (spec == null || FunctionStateCodec.StateType(spec.Id)==null || settings == null || prefab == null)
            return;
        // Mark before CallSizing.Configure clones the 1/2-input variant prefabs.
        FunctionSocketPositionFix.MarkSockets(prefab);
        // Native placement clones IShipPartVisual.GameObject, not the outer
        // settings prefab. Context must travel with that cloned visual branch.
        var visuals=prefab.GetComponentInChildren<IShipPartVisual>(true)?.GameObject;
        if(visuals==null)throw new InvalidOperationException("Function prefab has no native visual branch.");
        if(visuals.GetComponent<FunctionVisualContext>()==null)visuals.AddComponent<FunctionVisualContext>();

        // Copy the donor's actual textured material, not the kit's generic
        // metal fallback. Only the housing is stretched; sockets stay unit scale.
        var donor=Resources.FindObjectsOfTypeAll<PartSettings>().FirstOrDefault(p=>p.id==(spec.Id==62009?39:spec.Id==CallPartId?45:82));
        if(donor!=null)settings.size=donor.size; // native quarter-grid phase, independent of generated collider bounds
        if(spec.Id==62009&&donor!=null)
        {
            foreach(var mesh in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var original=donor.part.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m=>m.sharedMesh==mesh.sharedMesh);
                if(original!=null&&original.TryGetComponent<MeshRenderer>(out var source)&&mesh.TryGetComponent<MeshRenderer>(out var target))target.sharedMaterials=source.sharedMaterials;
            }
        }
        var donorMesh=donor?.part?.transform.Find(spec.Id==CallPartId?"Visuals/MathGate":"Visuals/SignalDisplay");
        if(donorMesh!=null)
            foreach(var mesh in prefab.GetComponentsInChildren<MeshFilter>(true))
                if(mesh.sharedMesh==donorMesh.GetComponent<MeshFilter>()?.sharedMesh&&mesh.TryGetComponent<MeshRenderer>(out var renderer))
                    renderer.sharedMaterials=donorMesh.GetComponent<MeshRenderer>().sharedMaterials;
        foreach(var screen in prefab.GetComponentsInChildren<Support.ScreenSurface>(true))
        {
            var panel=screen.transform.Find("Panel");if(panel!=null)panel.gameObject.SetActive(false);
            // The transmitter's 3D text plane clears the native curved opaque
            // screen by the measured 52.4 mm convex depth plus a small margin.
            if(spec.Id is 62006 or 62007)FunctionScreens.ConfigureDefinitionFace(screen);
        }
        PartInteraction interaction = prefab.GetComponentInChildren<PartInteraction>(true);
        if (interaction == null)
            throw new InvalidOperationException("The donor has no PartInteraction component on the function prefab.");

        List<PartInteractionConfig> interactions = AccessTools.Field(typeof(PartInteraction), "interactions")
            ?.GetValue(interaction) as List<PartInteractionConfig>;
        if (interactions == null)
            throw new InvalidOperationException("Call prefab interaction configuration is unavailable.");
        LocalizedString label = new LocalizedString("StringLocalization", "Interact");
        int interactIndex = interactions.FindIndex(item => item.type == InteractionType.Interact);
        if (interactIndex >= 0)
        {
            PartInteractionConfig interact = interactions[interactIndex];
            interact.localizedLabel = label;
            interactions[interactIndex] = interact;
        }
        else
        {
            interactions.Add(new PartInteractionConfig
            {
                type = InteractionType.Interact,
                localizedLabel = label
            });
        }
        if(spec.Id==CallPartId)CallSizing.Configure(settings,prefab);
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        Support.PartFactory.AfterConfigure = null;
    }

    internal static void Tick()
    {
        // Keep state 9 for the content action gate. Presentation
        // mirrors TargetingComputer: one tracked menu, without inventory.
        bool open=ContentConfigMenu.IsOpen && ContentConfigMenu.Current.Part?.State is FunctionPartState;
        if(open&&!_functionMenuWasOpen)
            foreach(var inventory in Resources.FindObjectsOfTypeAll<PlayerInventoryActivator>())
                if(inventory.isActiveAndEnabled && inventory.IsInventoryActive)inventory.Close();
        _functionMenuWasOpen=open;
    }
    private static bool InventoryOpen(bool val)=>!val||!IsFunctionInteraction();
    internal static bool IsFunctionInteraction()
    {
        var players=ServiceLocator.GetService<IPlayersProviderClient>();
        var ships=ServiceLocator.GetService<IShipsClientProvider>();
        return players!=null&&ships!=null&&players.TryGetLocalPlayerState(out var state)&&
            state.Type==PlayerStateType.InteractingWithChest&&
            ships.TryGetPartState<IStatefulPartState>(new PartContext(state.EntityId,state.PartId),out var part)&&part is FunctionPartState;
    }
}
