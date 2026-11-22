using System;
using System.Linq;
using Core.Services;
using Players;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Services;
using StellarFunctions.Content;

namespace StellarFunctions;

public sealed class CallPartBehaviour : PartBehaviour
{
    public override void OnAction(ContentPart part, TrackedPlayerServer player, string action)
    {
        if (part.Part?.State is not FunctionCallState state)
        {
            return;
        }

        if (!FunctionEdits.Authorize(part, player, action))
        {
            return;
        }

        if (FunctionDirectory.Handle(part, player, action))
        {
            return;
        }

        NativeFunctionRuntime.Refresh();
        var liveShips = ServiceLocator.GetService<ShipsServerTracker>();
        if (
            liveShips == null
            || !liveShips.TryGetTrackedShip(part.ShipId, out var liveShip)
            || !liveShip.TryGetStatefulPart(part.PartId, out var livePart)
            || livePart.State is not FunctionCallState current
            || current.InstanceId != state.InstanceId
        )
        {
            return;
        }

        state = current;

        if (CallSizing.Handle(liveShip, livePart, state, player.Id, action))
        {
            return;
        }

        if (action == "reset-input-bindings")
        {
            if (
                NativeFunctionRuntime.TryDefinition(state.SelectedFunctionId, part.ShipId, out var target)
                && target.Graph != null
                && target.Error == null
            )
            {
                var bindingShips = ServiceLocator.GetService<ShipsServerTracker>();
                if (bindingShips != null && bindingShips.TryGetTrackedShip(part.ShipId, out var owner))
                {
                    owner.UpdatePartState(
                        part.PartId,
                        NativeFunctionRuntime.Bind(
                            state with
                            {
                                RuntimeJson = "{}",
                                StructureKey = "",
                            },
                            target.Graph,
                            true
                        )
                    );
                }
            }
            return;
        }

        if (action == "cancel-rebind")
        {
            var tracker = ServiceLocator.GetService<ShipsServerTracker>();
            if (tracker != null && tracker.TryGetTrackedShip(part.ShipId, out var owner))
            {
                owner.UpdatePartState(
                    part.PartId,
                    state with
                    {
                        PendingFunctionId = "",
                        PendingFunctionName = "",
                        Status = "OK",
                    }
                );
            }

            return;
        }
        bool confirmed =
            action == "confirm-rebind:" + state.PendingFunctionId && !string.IsNullOrEmpty(state.PendingFunctionId);
        if (
            !confirmed
            && (action == null || !action.StartsWith(ClientCallInteraction.SelectAction, StringComparison.Ordinal))
        )
        {
            return;
        }

        string requested = confirmed
            ? state.PendingFunctionId
            : action.Substring(ClientCallInteraction.SelectAction.Length);
        if (!Guid.TryParseExact(requested, "N", out var requestedId) || requestedId == Guid.Empty)
        {
            return;
        }
        // Resolve only valid definitions in this player's authoritative assembly.
        FunctionCatalog.Entry next = FunctionCatalog
            .Visible(part.ShipId)
            .FirstOrDefault(entry => entry.Id == requested);
        if (next == null)
        {
            return;
        }

        if (!confirmed && next.Id == state.SelectedFunctionId)
        {
            var sameShips = ServiceLocator.GetService<ShipsServerTracker>();
            if (sameShips != null && sameShips.TryGetTrackedShip(part.ShipId, out var owner))
            {
                owner.UpdatePartState(
                    part.PartId,
                    state with
                    {
                        PendingFunctionId = "",
                        PendingFunctionName = "",
                        Status = "OK",
                    }
                );
            }

            return;
        }
        if (
            NativeFunctionRuntime.HasWiring(new PartContext(part.ShipId, part.PartId))
            && !confirmed
            && next.Id != state.SelectedFunctionId
            && !string.IsNullOrEmpty(state.SelectedFunctionId)
            && new[] { state.Slot0, state.Slot1, state.Slot2 }.Any(s => !string.IsNullOrEmpty(s))
        )
        {
            ShipsServerTracker tracker = ServiceLocator.GetService<ShipsServerTracker>();
            if (tracker != null && tracker.TryGetTrackedShip(part.ShipId, out TrackedShipServer owner))
            {
                owner.UpdatePartState(
                    part.PartId,
                    state with
                    {
                        Status = "CONFIRM REBIND IN CONFIG",
                        PendingFunctionId = next.Id,
                        PendingFunctionName = next.Name,
                    }
                );
            }

            return;
        }

        if (
            NativeFunctionRuntime.TryDefinition(next.Id, part.ShipId, out var definition)
            && definition.Graph != null
            && definition.Error == null
        )
        {
            ApplySelection(liveShip, livePart, state, definition.Graph);
        }
    }

    private static void ApplySelection(
        TrackedShipServer ship,
        StatefulPart part,
        FunctionCallState state,
        FunctionGraph graph
    )
    {
        FunctionCallState selected = state with
        {
            SelectedFunctionId = graph.Id.ToString("N"),
            PendingFunctionId = "",
            PendingFunctionName = "",
            RuntimeJson = "{}",
            StructureKey = "",
            ResizeFeedback = "",
        };
        // The smallest native housing exposes one input even for a constant function.
        int count = Math.Max(1, graph.ParameterCount);
        if (count != state.InputCount)
        {
            selected = CallSizing.PrepareResize(ship, part, selected, count);
        }

        // Bind using the final socket count, then replicate the size and bindings together.
        selected = NativeFunctionRuntime.Bind(selected, graph, true);
        ship.UpdatePartState(part.Id, selected);
        if (selected.InputCount != state.InputCount)
        {
            CallSizing.InvalidateAfterResize();
        }
    }

    public override void OnWorldExit()
    {
        CallResizeWiring.Reset();
        NativeFunctionRuntime.Reset();
        FunctionEdits.Reset();
        FunctionEditor.Reset();
    }
}
