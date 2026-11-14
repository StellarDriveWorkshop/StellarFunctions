using System;
using System.Linq;
using Core.Services;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Players;
using Ships.Services;
using StellarFunctions.Content;

namespace StellarFunctions;

internal static class FunctionBroadcast
{
    internal static string Payload(string id, string revision, bool enabled) =>
        "broadcast:"
        + new JObject
        {
            ["id"] = id,
            ["revision"] = revision,
            ["enabled"] = enabled,
        }.ToString(Formatting.None);

    internal static bool Handle(ContentPart part, string action)
    {
        if (!action.StartsWith("broadcast:", StringComparison.Ordinal))
        {
            return false;
        }

        var edit = NativeFunctionCompiler.ParseObject(action.Substring(10), 1024);
        if (
            edit["enabled"]?.Type != JTokenType.Boolean
            || !Guid.TryParseExact((string)edit["id"], "N", out var id)
            || id == Guid.Empty
        )
        {
            throw new FunctionFault("INVALID BROADCAST EDIT");
        }

        NativeFunctionRuntime.Refresh();
        var definition = NativeFunctionRuntime
            .LocalDefinitions(part.ShipId)
            .FirstOrDefault(d => d.State.DefinitionId == id);
        if (
            definition == null
            || part.Part.State is FunctionReturnState r && r.DefinitionId != id
            || part.Part.State is not FunctionReturnState && part.Part.State is not FunctionTransmitterState
        )
        {
            throw new FunctionFault("BROADCAST TARGET NOT LOCAL");
        }
        // Compiled definitions intentionally ignore transient interaction changes.
        // Writing their cached state would clear an open Return's IsInUse flag
        // and make every subsequent authenticated menu request fail.
        var ships = ServiceLocator.GetService<ShipsServerTracker>();
        if (
            ships == null
            || !ships.TryGetTrackedShip(definition.Address.ShipId, out var ship)
            || !ship.TryGetStatefulPart(definition.Address.PartId, out var livePart)
            || livePart.State is not FunctionReturnState live
            || live.DefinitionId != id
            || live.IsHeldByPlayer
        )
        {
            throw new FunctionFault("BROADCAST TARGET UNAVAILABLE");
        }

        if ((string)edit["revision"] != FunctionEdits.Revision(live))
        {
            throw new FunctionFault("EDIT CONFLICT: REOPEN");
        }

        bool enabled = (bool)edit["enabled"];
        if (enabled && (!NativeFunctionRuntime.HasTransmitter(part.ShipId) || definition.Error != null))
        {
            throw new FunctionFault("VALID TEMPLATE AND TRANSMITTER REQUIRED");
        }

        ship.UpdatePartState(definition.Address.PartId, live with { Broadcast = enabled });
        return true;
    }
}

public sealed class TransmitterPartBehaviour : PartBehaviour
{
    public override void OnAction(ContentPart part, TrackedPlayerServer player, string action)
    {
        if (!FunctionEdits.Authorize(part, player, action))
        {
            return;
        }

        try
        {
            if (!FunctionDirectory.Handle(part, player, action))
            {
                FunctionBroadcast.Handle(part, action);
            }
        }
        catch (Exception error)
            when (error is FunctionFault
                || error is JsonException
                || error is FormatException
                || error is OverflowException
            )
        {
            MelonLogger.Warning("[functions] Transmitter edit refused: " + error.Message);
        }
    }

    public override void OnWorldExit()
    {
        NativeFunctionRuntime.Reset();
        FunctionEdits.Reset();
        FunctionEditor.Reset();
    }
}
